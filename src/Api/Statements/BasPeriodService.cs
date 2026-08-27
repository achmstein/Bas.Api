using Bas.Api.Contracts.Bas;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Bas.Api.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Statements;

/// <summary>A refusal the caller can act on, carrying the HTTP status it should produce.</summary>
public sealed record BasError(int StatusCode, string Title, string Detail);

/// <summary>
/// Everything a worker can do to their own activity statements.
///
/// <para>The service takes a worker id rather than reading one from the request, so the ownership
/// question is answered once, by the caller, from the token's subject. There is no code path here
/// that can reach another worker's statement.</para>
/// </summary>
public sealed class BasPeriodService(
    BasDbContext db, TimeProvider timeProvider, WebhookPublisher webhooks)
{
    /// <summary>How many past quarters a worker sees when they have never saved anything.</summary>
    private const int VisibleQuarters = 8;

    /// <summary>
    /// The worker's statements, newest first.
    ///
    /// <para>Quarters they have never touched are included as drafts that exist only in this
    /// response. A worker's first visit would otherwise show an empty list, when what they need is
    /// the quarter they are supposed to be lodging.</para>
    /// </summary>
    public async Task<IReadOnlyList<BasPeriodSummary>> ListAsync(
        Guid workerId, CancellationToken cancellationToken)
    {
        var saved = await db.BasPeriods
            .AsNoTracking()
            .Where(p => p.WorkerId == workerId)
            .ToListAsync(cancellationToken);

        var summaries = saved.Select(ToSummary).ToList();
        var seen = saved.Select(p => (p.FinancialYear, p.Quarter)).ToHashSet();

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        foreach (var quarter in BasCalendar.RecentQuarters(today, VisibleQuarters))
        {
            if (seen.Contains((quarter.FinancialYear, quarter.Quarter)))
                continue;

            summaries.Add(EmptySummary(quarter));
        }

        return summaries
            .OrderByDescending(s => s.FinancialYear)
            .ThenByDescending(s => s.Quarter)
            .ToList();
    }

    /// <summary>
    /// One statement. An untouched quarter comes back as an empty draft rather than a 404 — the
    /// worker is entitled to that quarter whether or not they have opened it yet, and making the
    /// client handle "not found" for a perfectly normal case is needless work on their side.
    /// </summary>
    public async Task<(BasPeriodResponse? Period, BasError? Error)> GetAsync(
        Guid workerId, int financialYear, int quarter, CancellationToken cancellationToken)
    {
        if (!BasCalendar.TryCreate(financialYear, quarter, out var period, out var error))
            return (null, BadPeriod(error!));

        var existing = await FindAsync(workerId, financialYear, quarter, tracked: false, cancellationToken);

        return (existing is null ? EmptyDraft(period) : ToResponse(existing), null);
    }

    /// <summary>
    /// Replaces the statement's figures.
    ///
    /// <para>A full replacement, not a merge. Absent means "this statement has no such label",
    /// which is a real and different thing from zero — so a partner sending only the fields that
    /// changed would silently clear the rest. That is documented on
    /// <see cref="SaveBasRequest"/> and it is the reason this is a PUT.</para>
    /// </summary>
    public async Task<(BasPeriodResponse? Period, BasError? Error)> SaveAsync(
        Guid workerId, int financialYear, int quarter, SaveBasRequest request,
        CancellationToken cancellationToken)
    {
        if (!BasCalendar.TryCreate(financialYear, quarter, out var calendar, out var calendarError))
            return (null, BadPeriod(calendarError!));

        if (Validate(request) is { } invalid)
            return (null, invalid);

        var now = timeProvider.GetUtcNow();
        var period = await FindAsync(workerId, financialYear, quarter, tracked: true, cancellationToken);

        if (period is null)
        {
            period = new BasPeriod
            {
                WorkerId = workerId,
                FinancialYear = calendar.FinancialYear,
                Quarter = calendar.Quarter,
                PeriodStart = calendar.PeriodStart,
                PeriodEnd = calendar.PeriodEnd,
                DueDate = calendar.DueDate,
                Status = BasPeriodStatus.Draft,
                CreatedAt = now
            };

            db.BasPeriods.Add(period);
        }
        else if (!period.IsEditable)
        {
            return (null, new BasError(
                StatusCodes.Status409Conflict,
                "Statement is no longer editable",
                $"This statement is '{ToWireStatus(period.Status)}'. Figures can only be changed while " +
                "it is a draft, or after a failed push."));
        }

        Apply(request, period);
        period.UpdatedAt = now;

        // A retry after a failed push starts again as a draft, so the reconciler does not pick up
        // the old failure alongside the new figures.
        if (period.Status is BasPeriodStatus.Failed)
        {
            period.Status = BasPeriodStatus.Draft;
            period.FailureReason = null;
            period.SubmittedAt = null;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two saves for a brand-new quarter arrived together; the unique index decided it.
            db.ChangeTracker.Clear();

            var winner = await FindAsync(workerId, financialYear, quarter, tracked: false, cancellationToken);
            if (winner is null)
                throw;

            return (ToResponse(winner), null);
        }

        return (ToResponse(period), null);
    }

    /// <summary>
    /// Queues the statement for the practice.
    ///
    /// <para>Never lodges inline. Practice Manager is one browser session behind a queue of one
    /// with ten-minute cold-start logins, and BAS is quarterly — every worker lodges inside the
    /// same 72 hours. A synchronous call over that would be a guaranteed outage on the busiest day
    /// of the quarter.</para>
    /// </summary>
    public async Task<(SubmitBasResponse? Response, BasError? Error)> SubmitAsync(
        Guid workerId, int financialYear, int quarter, CancellationToken cancellationToken)
    {
        if (!BasCalendar.TryCreate(financialYear, quarter, out var calendar, out var calendarError))
            return (null, BadPeriod(calendarError!));

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (!BasCalendar.HasEnded(calendar, today))
        {
            return (null, new BasError(
                StatusCodes.Status409Conflict,
                "Period has not ended",
                $"This quarter ends on {calendar.PeriodEnd:yyyy-MM-dd}. An activity statement cannot be " +
                "lodged before its period is over."));
        }

        var worker = await db.Workers.SingleOrDefaultAsync(w => w.Id == workerId, cancellationToken);
        if (worker is null)
            return (null, new BasError(StatusCodes.Status404NotFound, "Unknown worker", "No such worker."));

        // Checked here rather than at the push, because the reconciler would otherwise retry
        // forever against something that can never succeed - and each attempt that gets as far as
        // creating a client leaves an orphan behind in the live practice.
        if (!worker.IsCompleteForLodgement)
        {
            return (null, new BasError(
                StatusCodes.Status409Conflict,
                "Worker identity is incomplete",
                "A TFN, given name, family name and date of birth are required before an activity " +
                "statement can be lodged. Send them to PUT /api/v1/workers/me."));
        }

        var period = await FindAsync(workerId, financialYear, quarter, tracked: true, cancellationToken);
        if (period is null)
        {
            return (null, new BasError(
                StatusCodes.Status409Conflict,
                "Nothing to submit",
                "This statement has no figures saved. Save it before submitting."));
        }

        if (period.Status is not (BasPeriodStatus.Draft or BasPeriodStatus.Failed))
        {
            // Not an error: a partner retrying a request whose response they lost should get the
            // same answer rather than a conflict.
            return (new SubmitBasResponse
            {
                PeriodId = period.Id,
                Status = ToWireStatus(period.Status),
                SubmittedAt = period.SubmittedAt ?? period.UpdatedAt
            }, null);
        }

        if (period is { TotalSales: null, GstOnSales: null, GstOnPurchases: null })
        {
            return (null, new BasError(
                StatusCodes.Status409Conflict,
                "Nothing to submit",
                "This statement has no GST figures. Save at least G1, 1A and 1B before submitting."));
        }

        var now = timeProvider.GetUtcNow();
        var previousStatus = period.Status;
        period.Status = BasPeriodStatus.Submitted;
        period.SubmittedAt = now;
        period.FailureReason = null;
        period.UpdatedAt = now;

        // Enqueue for the practice. The ledger is what the reconciler sweeps; submitting is the
        // only thing that puts work on it, and a re-submit resets the attempt budget so a
        // corrected statement is not still serving a penalty from the version before it.
        var state = await db.SyncStates.SingleOrDefaultAsync(s => s.BasPeriodId == period.Id, cancellationToken);
        if (state is null)
        {
            db.SyncStates.Add(new SyncState
            {
                BasPeriodId = period.Id,
                Status = SyncStatus.Pending,
                DirtyAt = now,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            state.Status = SyncStatus.Pending;
            state.DirtyAt = now;
            state.NextAttemptAt = now;
            state.AttemptCount = 0;
            state.LastError = null;
            state.UpdatedAt = now;
        }

        // Enqueued into the same unit of work as the status change it describes, so a submit that
        // rolls back cannot leave a webhook promising something that never happened.
        await webhooks.EnqueueStatusChangeAsync(period, previousStatus, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return (new SubmitBasResponse
        {
            PeriodId = period.Id,
            Status = BasStatuses.Submitted,
            SubmittedAt = now
        }, null);
    }

    /// <summary>Status and the net amount Practice Manager computed.</summary>
    public async Task<(BasStatusResponse? Status, BasError? Error)> GetStatusAsync(
        Guid workerId, int financialYear, int quarter, CancellationToken cancellationToken)
    {
        if (!BasCalendar.TryCreate(financialYear, quarter, out var calendar, out var calendarError))
            return (null, BadPeriod(calendarError!));

        var period = await FindAsync(workerId, financialYear, quarter, tracked: false, cancellationToken);

        return (new BasStatusResponse
        {
            Status = period is null ? BasStatuses.Draft : ToWireStatus(period.Status),
            NetAmount = period?.NetAmount,
            DueDate = calendar.DueDate,
            SubmittedAt = period?.SubmittedAt,
            FailureReason = period?.FailureReason
        }, null);
    }

    // ------------------------------------------------------------------------------- internals

    private Task<BasPeriod?> FindAsync(
        Guid workerId, int financialYear, int quarter, bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? db.BasPeriods : db.BasPeriods.AsNoTracking();

        return query.SingleOrDefaultAsync(
            p => p.WorkerId == workerId && p.FinancialYear == financialYear && p.Quarter == quarter,
            cancellationToken);
    }

    private static BasError? Validate(SaveBasRequest request)
    {
        // T4 exists to tell the ATO why an instalment was varied down. A varied amount without one
        // is a statement the ATO will bounce, so it is better refused here than a quarter later.
        if (request.VariedInstalmentAmount is not null && string.IsNullOrWhiteSpace(request.VariationReasonCode))
        {
            return new BasError(
                StatusCodes.Status400BadRequest,
                "Variation reason required",
                "T9 (varied instalment amount) requires T4 (variationReasonCode).");
        }

        return null;
    }

    private static void Apply(SaveBasRequest request, BasPeriod period)
    {
        // StatementType is deliberately not touched here. It is filled in by the reconciler from the
        // statement the ATO actually issued - see BasPeriod.StatementType.
        period.TotalSales = request.TotalSales;
        period.GstOnSales = request.GstOnSales;
        period.GstOnPurchases = request.GstOnPurchases;
        period.TotalPurchases = request.TotalPurchases;
        period.CashAccountingMethod = request.CashAccountingMethod;

        period.InstalmentIncome = request.InstalmentIncome;
        period.AtoInstalmentAmount = request.AtoInstalmentAmount;
        period.VariedInstalmentAmount = request.VariedInstalmentAmount;
        period.VariationReasonCode = request.VariationReasonCode;

        period.TotalSalaryWages = request.TotalSalaryWages;
        period.AmountWithheld = request.AmountWithheld;
    }

    private static BasError BadPeriod(string detail) =>
        new(StatusCodes.Status400BadRequest, "Invalid period", detail);

    internal static string ToWireStatus(BasPeriodStatus status) => status switch
    {
        BasPeriodStatus.Draft => BasStatuses.Draft,
        BasPeriodStatus.Submitted => BasStatuses.Submitted,
        BasPeriodStatus.AwaitingStatement => BasStatuses.AwaitingStatement,
        BasPeriodStatus.Pushed => BasStatuses.Pushed,
        BasPeriodStatus.InReview => BasStatuses.InReview,
        BasPeriodStatus.Lodged => BasStatuses.Lodged,
        BasPeriodStatus.Failed => BasStatuses.Failed,
        _ => BasStatuses.Draft
    };

    private static BasPeriodSummary ToSummary(BasPeriod p) => new()
    {
        Id = p.Id,
        FinancialYear = p.FinancialYear,
        Quarter = p.Quarter,
        PeriodStart = p.PeriodStart,
        PeriodEnd = p.PeriodEnd,
        DueDate = p.DueDate,
        Status = ToWireStatus(p.Status),
        NetAmount = p.NetAmount,
        SubmittedAt = p.SubmittedAt
    };

    private static BasPeriodSummary EmptySummary(BasQuarter q) => new()
    {
        Id = Guid.Empty,
        FinancialYear = q.FinancialYear,
        Quarter = q.Quarter,
        PeriodStart = q.PeriodStart,
        PeriodEnd = q.PeriodEnd,
        DueDate = q.DueDate,
        Status = BasStatuses.Draft,
        NetAmount = null,
        SubmittedAt = null
    };

    /// <summary>
    /// A quarter the worker has never saved. <c>Id</c> is empty because nothing has been created —
    /// it becomes real on the first save.
    /// </summary>
    private static BasPeriodResponse EmptyDraft(BasQuarter q) => new()
    {
        Id = Guid.Empty,
        FinancialYear = q.FinancialYear,
        Quarter = q.Quarter,
        PeriodStart = q.PeriodStart,
        PeriodEnd = q.PeriodEnd,
        DueDate = q.DueDate,
        Status = BasStatuses.Draft,
        UpdatedAt = default
    };

    private static BasPeriodResponse ToResponse(BasPeriod p) => new()
    {
        Id = p.Id,
        FinancialYear = p.FinancialYear,
        Quarter = p.Quarter,
        PeriodStart = p.PeriodStart,
        PeriodEnd = p.PeriodEnd,
        DueDate = p.DueDate,
        Status = ToWireStatus(p.Status),
        StatementType = p.StatementType,
        TotalSales = p.TotalSales,
        GstOnSales = p.GstOnSales,
        GstOnPurchases = p.GstOnPurchases,
        TotalPurchases = p.TotalPurchases,
        CashAccountingMethod = p.CashAccountingMethod,
        InstalmentIncome = p.InstalmentIncome,
        AtoInstalmentAmount = p.AtoInstalmentAmount,
        VariedInstalmentAmount = p.VariedInstalmentAmount,
        VariationReasonCode = p.VariationReasonCode,
        TotalSalaryWages = p.TotalSalaryWages,
        AmountWithheld = p.AmountWithheld,
        NetAmount = p.NetAmount,
        SubmittedAt = p.SubmittedAt,
        FailureReason = p.FailureReason,
        UpdatedAt = p.UpdatedAt
    };
}
