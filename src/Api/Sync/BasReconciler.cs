using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Bas.Api.Statements;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Bas.Api.Infrastructure;
using Bas.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bas.Api.Sync;

/// <summary>How often the reconciler looks for work, and how patiently it retries.</summary>
public sealed class ReconcilerOptions
{
    public const string SectionName = "Reconciler";

    public bool Enabled { get; set; } = true;

    /// <summary>How often to look for due work.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many statements to take per sweep. Pushed one at a time regardless.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// Backoff between failed attempts, by attempt number. The last entry repeats.
    /// </summary>
    public TimeSpan[] RetrySchedule { get; set; } =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6)
    ];

    /// <summary>Attempts before a statement is given up on and marked failed for a human.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 8;

    /// <summary>
    /// How long to wait before re-checking a statement the ATO has not issued yet. Hours, not
    /// minutes: nothing we do makes the ATO issue it sooner, and every check costs a Practice
    /// Manager session slot that a real push could have used.
    /// </summary>
    public TimeSpan AwaitingStatementInterval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>
/// Pushes submitted activity statements into Practice Manager, and owns the retrying.
///
/// <para><b>Strictly one at a time.</b> Practice Manager is a single browser session behind a queue
/// of one — concurrency here would not make anything faster, it would just move the queue upstream
/// and make failures harder to read.</para>
/// </summary>
public sealed class BasReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<ReconcilerOptions> options,
    BasMetrics metrics,
    TimeProvider timeProvider,
    ILogger<BasReconciler> logger) : SweepingBackgroundService(timeProvider, logger)
{
    private readonly ReconcilerOptions _options = options.Value;

    protected override bool Enabled => _options.Enabled;

    protected override TimeSpan PollInterval => _options.PollInterval;

    protected override string DisabledMessage => "no statements will be pushed.";

    public override async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();

        // One aggregate per sweep keeps the queue gauges honest without the observable callbacks
        // ever touching the database.
        var queue = await db.SyncStates
            .Where(s => s.Status == SyncStatus.Pending)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Oldest = g.Min(s => s.NextAttemptAt) })
            .SingleOrDefaultAsync(cancellationToken);

        metrics.RecordSyncQueue(queue?.Count ?? 0, queue is null ? TimeSpan.Zero : now - queue.Oldest);

        var due = await db.SyncStates
            .Where(s => s.NextAttemptAt <= now
                        && (s.Status == SyncStatus.Pending || s.Status == SyncStatus.AwaitingStatement))
            .OrderBy(s => s.NextAttemptAt)
            .Take(_options.BatchSize)
            .Select(s => s.BasPeriodId)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
            return;

        Logger.LogInformation("Reconciler found {Count} statement(s) due.", due.Count);

        foreach (var periodId in due)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            // A scope per subject so one poisonous entity cannot contaminate the change tracker
            // for the rest of the batch.
            await using var itemScope = scopeFactory.CreateAsyncScope();
            await PushOneAsync(itemScope.ServiceProvider, periodId, cancellationToken);
        }
    }

    private async Task PushOneAsync(
        IServiceProvider services, Guid periodId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<BasDbContext>();
        var gateway = services.GetRequiredService<IPracticeManagerGateway>();
        var webhooks = services.GetRequiredService<WebhookPublisher>();

        var state = await db.SyncStates.SingleOrDefaultAsync(s => s.BasPeriodId == periodId, cancellationToken);
        var period = await db.BasPeriods.SingleOrDefaultAsync(p => p.Id == periodId, cancellationToken);

        if (state is null || period is null)
            return;

        var worker = await db.Workers.SingleOrDefaultAsync(w => w.Id == period.WorkerId, cancellationToken);
        if (worker is null)
        {
            await FailAsync(db, webhooks, state, period, "The worker no longer exists.", cancellationToken);
            return;
        }

        var tfn = WorkerIdentityService.RevealTfn(worker);
        if (string.IsNullOrEmpty(tfn))
        {
            // Submit already refuses this, so reaching it means the identity was cleared afterwards.
            await FailAsync(db, webhooks, state, period, "The worker has no TFN on file.", cancellationToken);
            return;
        }

        var hash = ContentHash(period, worker);

        // Nothing has changed since the last successful push. A redeploy, a duplicate submit or a
        // spurious wake-up then costs nothing - which matters when every push consumes a slot on a
        // single browser session with a ten-minute cold start.
        if (state.Status is SyncStatus.Synced && state.ContentHash == hash)
            return;

        var now = TimeProvider.GetUtcNow();
        var previousStatus = period.Status;
        var outcome = await gateway.PushAsync(period, worker, tfn, cancellationToken);

        switch (outcome)
        {
            case PushOutcome.Pushed pushed:
                state.Status = SyncStatus.Synced;
                state.ContentHash = hash;
                state.AttemptCount = 0;
                state.TransientAttemptCount = 0;
                state.LastError = null;
                state.LastSyncedAt = now;
                state.NextAttemptAt = now;
                state.UpdatedAt = now;

                period.Status = BasPeriodStatus.Pushed;
                period.UpdatedAt = now;

                // Practice Manager's own figures, taken as read. The statement type is the one the
                // ATO issued - the push declined to guess it, and this is where we find out.
                if (pushed.Readback is { } readback)
                {
                    period.NetAmount = readback.NetAmount;
                    period.StatementType = readback.StatementType ?? period.StatementType;

                    if (readback.SectionsMissing.Count > 0)
                    {
                        // The worker filled in labels their statement does not have. PM accepted the
                        // write and discarded them, so the figures are gone and nobody would know.
                        // Not a failure - the rest of the statement is fine and the agent will see
                        // it - but it must not be silent.
                        Logger.LogWarning(
                            "Statement {PeriodId} (FY{Year} Q{Quarter}) carried figures for [{Sections}], which " +
                            "the statement the ATO issued does not include. Practice Manager discarded them.",
                            period.Id, period.FinancialYear, period.Quarter,
                            string.Join(", ", readback.SectionsMissing));
                    }
                }

                Logger.LogInformation(
                    "Pushed statement {PeriodId} (FY{Year} Q{Quarter}) to Practice Manager as client {ClientId}, " +
                    "statement {StatementId} (type {Type}); sections [{Sections}]; label 9 {Net}.",
                    period.Id, period.FinancialYear, period.Quarter,
                    pushed.ClientId, pushed.StatementId, pushed.Readback?.StatementType ?? "unknown",
                    string.Join(", ", pushed.SectionsPushed), pushed.Readback?.NetAmount);
                break;

            case PushOutcome.AwaitingStatement:
                // Not a failure, so the attempt budget is untouched. The ATO issues shortly after
                // the period ends; until then there is genuinely nothing to write to.
                state.Status = SyncStatus.AwaitingStatement;
                state.TransientAttemptCount = 0;
                state.LastError = null;
                state.NextAttemptAt = now + _options.AwaitingStatementInterval;
                state.UpdatedAt = now;

                period.Status = BasPeriodStatus.AwaitingStatement;
                period.UpdatedAt = now;

                Logger.LogInformation(
                    "Statement {PeriodId} (FY{Year} Q{Quarter}) has not been issued by the ATO yet; " +
                    "re-checking in {Interval}.",
                    period.Id, period.FinancialYear, period.Quarter, _options.AwaitingStatementInterval);
                break;

            case PushOutcome.Unavailable unavailable:
                // Practice Manager cannot act right now. Back off progressively, but do not spend
                // the attempt budget on an outage that says nothing about this statement.
                metrics.PushUnavailable();
                state.TransientAttemptCount++;
                state.LastError = unavailable.Reason;
                state.NextAttemptAt =
                    now + RetrySchedule.Backoff(_options.RetrySchedule, state.TransientAttemptCount);
                state.UpdatedAt = now;

                Logger.LogWarning(
                    "Practice Manager unavailable for statement {PeriodId}: {Reason}. Retrying at {Next:o}.",
                    period.Id, unavailable.Reason, state.NextAttemptAt);
                break;

            case PushOutcome.Rejected rejected:
                // Practice Manager answered, so any outage is over - only the rejection counts.
                metrics.PushRejected();
                state.AttemptCount++;
                state.TransientAttemptCount = 0;
                state.LastError = rejected.Reason;
                state.UpdatedAt = now;

                if (state.AttemptCount >= _options.MaxAttempts)
                {
                    await FailAsync(db, webhooks, state, period, rejected.Reason, cancellationToken);
                    return;
                }

                state.NextAttemptAt = now + RetrySchedule.Backoff(_options.RetrySchedule, state.AttemptCount);

                Logger.LogWarning(
                    "Push rejected for statement {PeriodId} (attempt {Attempt}/{Max}): {Reason}. Retrying at {Next:o}.",
                    period.Id, state.AttemptCount, _options.MaxAttempts, rejected.Reason, state.NextAttemptAt);
                break;
        }

        await webhooks.EnqueueStatusChangeAsync(period, previousStatus, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(
        BasDbContext db,
        WebhookPublisher webhooks,
        SyncState state,
        BasPeriod period,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();
        var previousStatus = period.Status;

        metrics.PushFailed();

        state.Status = SyncStatus.Failed;
        state.LastError = reason;
        state.UpdatedAt = now;

        period.Status = BasPeriodStatus.Failed;
        period.FailureReason = reason;
        period.UpdatedAt = now;

        Logger.LogError(
            "Giving up on statement {PeriodId} (FY{Year} Q{Quarter}) after {Attempts} attempt(s): {Reason}",
            period.Id, period.FinancialYear, period.Quarter, state.AttemptCount, reason);

        await webhooks.EnqueueStatusChangeAsync(period, previousStatus, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A hash of everything that would change the push. Identity is in it as well as the figures:
    /// correcting a misspelled surname has to reach the practice too.
    /// </summary>
    internal static string ContentHash(BasPeriod p, Worker w)
    {
        var content = string.Join('|',
            p.FinancialYear, p.Quarter, p.PeriodStart, p.PeriodEnd,
            p.TotalSales, p.GstOnSales, p.GstOnPurchases, p.CashAccountingMethod,
            p.InstalmentIncome, p.AtoInstalmentAmount, p.VariedInstalmentAmount, p.VariationReasonCode,
            p.TotalSalaryWages, p.AmountWithheld,
            // The TFN itself is deliberately absent - its last three digits are enough to notice a
            // change, and a hash input is one more place the full number would otherwise live.
            w.TfnLast3, w.FirstName, w.FamilyName, w.DateOfBirth, w.Phone, w.Email);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
