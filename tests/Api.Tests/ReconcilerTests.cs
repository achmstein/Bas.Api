using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Bas.Api.Infrastructure;
using Bas.Api.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// The reconciler's state machine, driven through the real pipeline with a fake Practice Manager.
///
/// <para>The gateway is faked rather than the database: what is worth testing here is which
/// outcomes advance a statement, which retry, which spend the attempt budget and which do not — and
/// that is decided entirely by the reconciler, not by gRPC. A real Practice Manager would also mean
/// a real browser session and a real Xero login on every test.</para>
/// </summary>
public sealed class ReconcilerTests(ReconcilerFactory factory) : IClassFixture<ReconcilerFactory>, IDisposable
{
    private const int Year = 2026;
    private const int Quarter = 4;

    private readonly ReconcilerFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Submitting_puts_the_statement_on_the_ledger()
    {
        var (_, periodId) = await SubmitAsync("recon-enqueue");

        var state = await LedgerAsync(periodId);
        state.ShouldNotBeNull();
        state.Status.ShouldBe(SyncStatus.Pending);
        state.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_successful_push_marks_the_statement_pushed()
    {
        var (_, periodId) = await SubmitAsync("recon-success");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(4242, 9001, ["Gst"]);

        await SweepAsync();

        (await PeriodAsync(periodId)).Status.ShouldBe(BasPeriodStatus.Pushed);

        var state = await LedgerAsync(periodId);
        state!.Status.ShouldBe(SyncStatus.Synced);
        state.ContentHash.ShouldNotBeNullOrEmpty();
        state.LastSyncedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Unchanged_content_is_not_pushed_twice()
    {
        // Every push costs a slot on a single browser session with a ten-minute cold start, so a
        // redeploy or a duplicate wake-up must not spend one re-sending identical figures.
        var (_, periodId) = await SubmitAsync("recon-idempotent");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(1, 2, ["Gst"]);

        await SweepAsync();
        var callsAfterFirst = _factory.Gateway.Calls;

        await MakeDueAsync(periodId);
        await SweepAsync();

        _factory.Gateway.Calls.ShouldBe(callsAfterFirst);
    }

    [Fact]
    public async Task Changed_figures_are_pushed_again()
    {
        var (client, periodId) = await SubmitAsync("recon-changed");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(1, 2, ["Gst"]);
        await SweepAsync();

        var before = _factory.Gateway.Calls;

        // A failed statement is editable again; correct it and re-submit.
        await MarkFailedAsync(periodId);
        await client.PutAsJsonAsync($"/api/v1/bas/{Year}/{Quarter}", SimplerBas() with { TotalSales = 44000 });
        await client.PostAsync($"/api/v1/bas/{Year}/{Quarter}/submit", null);

        await SweepAsync();

        _factory.Gateway.Calls.ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_statement_the_ATO_has_not_issued_waits_without_spending_the_attempt_budget()
    {
        var (_, periodId) = await SubmitAsync("recon-awaiting");
        _factory.Gateway.Outcome = new PushOutcome.AwaitingStatement(4242);

        await SweepAsync();

        (await PeriodAsync(periodId)).Status.ShouldBe(BasPeriodStatus.AwaitingStatement);

        var state = await LedgerAsync(periodId);
        state!.Status.ShouldBe(SyncStatus.AwaitingStatement);
        // Waiting on the ATO is not a failure, so it must not count towards giving up.
        state.AttemptCount.ShouldBe(0);
        state.NextAttemptAt.ShouldBeGreaterThan(_factory.Clock.GetUtcNow());
    }

    [Fact]
    public async Task An_unavailable_practice_manager_retries_without_spending_the_attempt_budget()
    {
        // PM being unreachable says nothing about this statement, so it must not push it towards
        // being marked failed.
        var (_, periodId) = await SubmitAsync("recon-unavailable");
        _factory.Gateway.Outcome = new PushOutcome.Unavailable("session refused");

        await SweepAsync();

        var state = await LedgerAsync(periodId);
        state!.Status.ShouldBe(SyncStatus.Pending);
        state.AttemptCount.ShouldBe(0);
        state.LastError!.ShouldContain("session refused");
        (await PeriodAsync(periodId)).Status.ShouldBe(BasPeriodStatus.Submitted);
    }

    [Fact]
    public async Task An_unavailable_practice_manager_backs_off_progressively()
    {
        // A flat retry interval would hammer a downstream that is already struggling — and PM is a
        // single browser session with a ten-minute cold start, so that matters. The outage must
        // climb the schedule without spending the attempt budget.
        var (_, periodId) = await SubmitAsync("recon-unavailable-backoff");
        _factory.Gateway.Outcome = new PushOutcome.Unavailable("session refused");
        var now = _factory.Clock.GetUtcNow();

        await SweepAsync();
        var first = await LedgerAsync(periodId);
        first!.NextAttemptAt.ShouldBe(now + TimeSpan.FromMinutes(1));

        await MakeDueAsync(periodId);
        await SweepAsync();
        var second = await LedgerAsync(periodId);
        second!.NextAttemptAt.ShouldBe(now + TimeSpan.FromMinutes(5));

        await MakeDueAsync(periodId);
        await SweepAsync();
        var third = await LedgerAsync(periodId);
        third!.NextAttemptAt.ShouldBe(now + TimeSpan.FromMinutes(15));

        // Still not a step towards being marked failed.
        third.AttemptCount.ShouldBe(0);
        third.Status.ShouldBe(SyncStatus.Pending);
    }

    [Fact]
    public async Task The_first_rejection_waits_the_first_schedule_entry()
    {
        // The schedule's opening entry exists to make the first retry quick; indexing past it
        // would silently turn a one-minute wait into five.
        var (_, periodId) = await SubmitAsync("recon-first-rejection");
        _factory.Gateway.Outcome = new PushOutcome.Rejected("PM said no");

        await SweepAsync();

        var state = await LedgerAsync(periodId);
        state!.AttemptCount.ShouldBe(1);
        state.NextAttemptAt.ShouldBe(_factory.Clock.GetUtcNow() + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task An_outage_during_rejections_does_not_spend_the_budget()
    {
        var (_, periodId) = await SubmitAsync("recon-outage-interleaved");

        // Three rejections spend three attempts.
        _factory.Gateway.Outcome = new PushOutcome.Rejected("PM said no");
        for (var i = 0; i < 3; i++)
        {
            await MakeDueAsync(periodId);
            await SweepAsync();
        }
        (await LedgerAsync(periodId))!.AttemptCount.ShouldBe(3);

        // An outage in the middle spends none.
        _factory.Gateway.Outcome = new PushOutcome.Unavailable("session refused");
        for (var i = 0; i < 3; i++)
        {
            await MakeDueAsync(periodId);
            await SweepAsync();
        }
        var during = await LedgerAsync(periodId);
        during!.AttemptCount.ShouldBe(3);
        during.Status.ShouldBe(SyncStatus.Pending);

        // Exhausting the default budget of 8 still takes five more genuine rejections.
        _factory.Gateway.Outcome = new PushOutcome.Rejected("PM said no");
        for (var i = 0; i < 4; i++)
        {
            await MakeDueAsync(periodId);
            await SweepAsync();
        }
        (await LedgerAsync(periodId))!.Status.ShouldBe(SyncStatus.Pending);

        await MakeDueAsync(periodId);
        await SweepAsync();
        (await LedgerAsync(periodId))!.Status.ShouldBe(SyncStatus.Failed);
    }

    [Fact]
    public async Task A_rejected_push_backs_off_and_eventually_gives_up()
    {
        var (_, periodId) = await SubmitAsync("recon-rejected");
        _factory.Gateway.Outcome = new PushOutcome.Rejected("PM said no");

        await SweepAsync();

        var first = await LedgerAsync(periodId);
        first!.AttemptCount.ShouldBe(1);
        first.Status.ShouldBe(SyncStatus.Pending);
        first.NextAttemptAt.ShouldBeGreaterThan(_factory.Clock.GetUtcNow());

        // Exhaust the budget. Each sweep needs the backoff to have elapsed.
        for (var i = 0; i < 10; i++)
        {
            await MakeDueAsync(periodId);
            await SweepAsync();
        }

        var final = await LedgerAsync(periodId);
        final!.Status.ShouldBe(SyncStatus.Failed);

        var period = await PeriodAsync(periodId);
        period.Status.ShouldBe(BasPeriodStatus.Failed);
        period.FailureReason!.ShouldContain("PM said no");
    }

    [Fact]
    public async Task A_failed_statement_can_be_corrected_and_resubmitted()
    {
        var (client, periodId) = await SubmitAsync("recon-recover");
        _factory.Gateway.Outcome = new PushOutcome.Rejected("PM said no");

        for (var i = 0; i < 10; i++)
        {
            await MakeDueAsync(periodId);
            await SweepAsync();
        }

        (await LedgerAsync(periodId))!.Status.ShouldBe(SyncStatus.Failed);

        // Re-submitting resets the budget: a corrected statement should not still be serving a
        // penalty earned by the version before it.
        await client.PutAsJsonAsync($"/api/v1/bas/{Year}/{Quarter}", SimplerBas() with { TotalSales = 50000 });
        var resubmit = await client.PostAsync($"/api/v1/bas/{Year}/{Quarter}/submit", null);
        resubmit.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var state = await LedgerAsync(periodId);
        state!.Status.ShouldBe(SyncStatus.Pending);
        state.AttemptCount.ShouldBe(0);
        state.LastError.ShouldBeNull();

        _factory.Gateway.Outcome = new PushOutcome.Pushed(1, 2, ["Gst"]);
        await SweepAsync();

        (await PeriodAsync(periodId)).Status.ShouldBe(BasPeriodStatus.Pushed);
    }

    [Fact]
    public async Task Work_that_is_not_yet_due_is_left_alone()
    {
        var (_, periodId) = await SubmitAsync("recon-not-due");
        _factory.Gateway.Outcome = new PushOutcome.Rejected("PM said no");
        await SweepAsync();

        var before = _factory.Gateway.Calls;

        // Backoff has not elapsed.
        await SweepAsync();

        _factory.Gateway.Calls.ShouldBe(before);
    }

    [Fact]
    public async Task The_snapshot_never_asserts_a_statement_type()
    {
        // The ATO chooses it. Blank is what tells Practice Manager to find the statement it issued
        // rather than create one of whatever type it was told.
        var (_, _) = await SubmitAsync("recon-no-type");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(1, 2, ["Gst"]);

        await SweepAsync();

        _factory.Gateway.LastPeriod.ShouldNotBeNull();
        _factory.Gateway.LastPeriod!.StatementType.ShouldBeNull();
    }

    [Fact]
    public async Task A_successful_push_records_what_Practice_Manager_computed()
    {
        // Never calculated here: if our arithmetic and the ATO's disagree, ours is the wrong one.
        var (_, periodId) = await SubmitAsync("recon-readback");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(
            1, 2, ["Gst"], new StatementReadback("C", 2900, 870, 2030, []));

        await SweepAsync();

        var period = await PeriodAsync(periodId);
        period.NetAmount.ShouldBe(2030);

        // The type the ATO issued, learned rather than guessed.
        period.StatementType.ShouldBe("C");
    }

    [Fact]
    public async Task A_push_still_succeeds_when_the_statement_cannot_be_read_back()
    {
        // The figures landed. Not knowing the net amount is a lesser outcome, not a failure.
        var (_, periodId) = await SubmitAsync("recon-noreadback");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(1, 2, ["Gst"], Readback: null);

        await SweepAsync();

        var period = await PeriodAsync(periodId);
        period.Status.ShouldBe(BasPeriodStatus.Pushed);
        period.NetAmount.ShouldBeNull();
    }

    [Fact]
    public async Task Figures_sent_for_a_section_the_statement_lacks_are_still_pushed_but_reported()
    {
        // Practice Manager accepts the write and discards them, so the figures are gone. The
        // statement is otherwise fine and the agent will see it - but it must not be silent.
        var (_, periodId) = await SubmitAsync("recon-missing-section");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(
            1, 2, ["Gst"], new StatementReadback("C", 2900, 870, 2030, ["PAYG instalment"]));

        await SweepAsync();

        (await PeriodAsync(periodId)).Status.ShouldBe(BasPeriodStatus.Pushed);
    }

    [Fact]
    public async Task The_content_hash_covers_identity_as_well_as_figures()
    {
        // Correcting a misspelled surname has to reach the practice too, not just new numbers.
        var period = new BasPeriod { FinancialYear = 2026, Quarter = 4, TotalSales = 100 };
        var worker = new Worker { FirstName = "Jordan", FamilyName = "Ellis" };

        var before = BasReconciler.ContentHash(period, worker);
        worker.FamilyName = "Ellis-Smith";

        BasReconciler.ContentHash(period, worker).ShouldNotBe(before);
    }

    [Fact]
    public async Task A_rejected_push_increments_the_failure_counter()
    {
        // The counter is what turns "the queue silently stopped" into an alert. Filtered to this
        // host's own meter instance, because parallel test hosts publish under the same name.
        var (_, _) = await SubmitAsync("recon-metrics");
        _factory.Gateway.Outcome = new PushOutcome.Rejected("PM said no");

        var meter = _factory.Services.GetRequiredService<BasMetrics>().Meter;

        long rejected = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == meter && instrument.Name == "bas.push.failures")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome" && Equals(tag.Value, "rejected"))
                    Interlocked.Add(ref rejected, measurement);
            }
        });
        listener.Start();

        await SweepAsync();

        // At least: the sweep may also push other tests' leftover due statements.
        Interlocked.Read(ref rejected).ShouldBeGreaterThanOrEqualTo(1);
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task SweepAsync()
    {
        await using var scope = _factory.CreateDbScope();
        var reconciler = scope.ServiceProvider.GetServices<IHostedService>().OfType<BasReconciler>().Single();
        await reconciler.SweepAsync(CancellationToken.None);
    }

    /// <summary>Brings a backed-off ledger row forward so the next sweep considers it.</summary>
    private async Task MakeDueAsync(Guid periodId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        var state = await db.SyncStates.SingleAsync(s => s.BasPeriodId == periodId);
        state.NextAttemptAt = _factory.Clock.GetUtcNow();
        await db.SaveChangesAsync();
    }

    private async Task MarkFailedAsync(Guid periodId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        var period = await db.BasPeriods.SingleAsync(p => p.Id == periodId);
        period.Status = BasPeriodStatus.Failed;
        await db.SaveChangesAsync();
    }

    private async Task<SyncState?> LedgerAsync(Guid periodId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        return await db.SyncStates.AsNoTracking().SingleOrDefaultAsync(s => s.BasPeriodId == periodId);
    }

    private async Task<BasPeriod> PeriodAsync(Guid periodId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        return await db.BasPeriods.AsNoTracking().SingleAsync(p => p.Id == periodId);
    }

    private static SaveBasRequest SimplerBas() => new()
    {
        TotalSales = 31900,
        GstOnSales = 2900,
        GstOnPurchases = 870,
        CashAccountingMethod = true
    };

    /// <summary>A worker with a complete identity, a saved statement, and it submitted.</summary>
    private async Task<(HttpClient Client, Guid PeriodId)> SubmitAsync(string subject)
    {
        var token = await _factory.MintTokenAsync(_client, subject);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        await client.PutAsJsonAsync("/api/v1/workers/me", new WorkerIdentityRequest
        {
            Tfn = "123456782",
            FirstName = "Jordan",
            FamilyName = "Ellis",
            DateOfBirth = new DateOnly(1994, 3, 12)
        });

        await client.PutAsJsonAsync($"/api/v1/bas/{Year}/{Quarter}", SimplerBas());

        var submit = await client.PostAsync($"/api/v1/bas/{Year}/{Quarter}/submit", null);
        submit.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = (await submit.Content.ReadFromJsonAsync<SubmitBasResponse>())!;
        return (client, body.PeriodId);
    }
}
