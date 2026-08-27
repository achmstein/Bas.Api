using Bas.Api.Data.Entities;
using Bas.Api.Sync;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PracticeManager.Api.Contracts;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// The status-code → <see cref="PushOutcome"/> mapping, with the gRPC client faked at the
/// generated-client seam — no server, no channel.
///
/// <para>This mapping is what decides whether a failure spends the retry budget, so a mistake here
/// is not a wrong log line: it is a statement quietly marked failed during an outage, or a hot
/// loop against a downstream that said "never". The reconciler tests cover what each outcome does;
/// these cover which outcome each answer becomes.</para>
/// </summary>
public sealed class PracticeManagerGatewayTests
{
    [Fact]
    public async Task A_refused_session_is_unavailable_not_rejected()
    {
        // PracticeManager.Api maps "PM will not let us in" to FailedPrecondition precisely so
        // callers can tell it apart from a broken push and stop burning their retry budget.
        var (gateway, client) = CreateGateway();
        client.Throws = new RpcException(new Status(StatusCode.FailedPrecondition, "browser session refused"));

        var outcome = await PushAsync(gateway);

        var unavailable = outcome.ShouldBeOfType<PushOutcome.Unavailable>();
        unavailable.Reason.ShouldContain("session unavailable");
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.DeadlineExceeded)]
    public async Task An_outage_shaped_status_is_unavailable(StatusCode status)
    {
        var (gateway, client) = CreateGateway();
        client.Throws = new RpcException(new Status(status, "gone"));

        (await PushAsync(gateway)).ShouldBeOfType<PushOutcome.Unavailable>();
    }

    [Theory]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.PermissionDenied)]
    public async Task A_bad_api_key_is_rejected_because_retrying_cannot_fix_it(StatusCode status)
    {
        var (gateway, client) = CreateGateway();
        client.Throws = new RpcException(new Status(status, "key refused"));

        var rejected = (await PushAsync(gateway)).ShouldBeOfType<PushOutcome.Rejected>();
        rejected.Reason.ShouldContain("Not authenticated");
    }

    [Theory]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.Internal)]
    public async Task Any_other_grpc_failure_is_rejected_on_its_merits(StatusCode status)
    {
        var (gateway, client) = CreateGateway();
        client.Throws = new RpcException(new Status(status, "no"));

        (await PushAsync(gateway)).ShouldBeOfType<PushOutcome.Rejected>();
    }

    [Fact]
    public async Task A_statement_the_ATO_has_not_issued_is_awaiting_not_failed()
    {
        // taxReturnId == 0 is the documented "find-only found nothing" signal. The client may
        // still have been created, which is what lets PM raise the statement later.
        var (gateway, client) = CreateGateway();
        client.Response = new SyncActivityStatementResponse { ClientId = 4242, TaxReturnId = 0 };

        var awaiting = (await PushAsync(gateway)).ShouldBeOfType<PushOutcome.AwaitingStatement>();
        awaiting.ClientId.ShouldBe(4242);
    }

    [Fact]
    public async Task A_successful_push_carries_the_readback()
    {
        var (gateway, client) = CreateGateway();
        client.Response = new SyncActivityStatementResponse
        {
            ClientId = 4242,
            TaxReturnId = 9001,
            StatementType = "C",
            NetAmount = 2030,
            HasGst = true
        };
        client.Response.SectionsPushed.Add("Gst");

        var pushed = (await PushAsync(gateway)).ShouldBeOfType<PushOutcome.Pushed>();
        pushed.ClientId.ShouldBe(4242);
        pushed.StatementId.ShouldBe(9001);
        pushed.SectionsPushed.ShouldBe(["Gst"]);
        pushed.Readback.ShouldNotBeNull();
        pushed.Readback!.StatementType.ShouldBe("C");
        pushed.Readback.NetAmount.ShouldBe(2030);
    }

    [Fact]
    public async Task Sections_sent_but_not_carried_are_reported_and_unsent_ones_are_not()
    {
        // PM accepts figures for a section the statement lacks and discards them - the readback's
        // missing list is the only thing standing between that and silence. A section we never
        // sent must not be reported, or every GST-only worker would see PAYG noise.
        var (gateway, client) = CreateGateway();
        client.Response = new SyncActivityStatementResponse
        {
            ClientId = 1,
            TaxReturnId = 2,
            HasGst = false,
            HasPaygInstalments = false,
            HasPaygWithholding = false
        };

        var pushed = (await PushAsync(gateway)).ShouldBeOfType<PushOutcome.Pushed>();

        pushed.Readback!.SectionsMissing.ShouldBe(["GST"]);
    }

    [Fact]
    public async Task The_snapshot_never_asserts_a_type_and_leaves_absent_figures_unset()
    {
        // Blank type means "find the statement the ATO issued; never create one of a guessed
        // type". And absent-vs-zero is load-bearing: a zero written into a T section a worker
        // does not have would be a different statement from the one the ATO issued.
        var (gateway, client) = CreateGateway();
        client.Response = new SyncActivityStatementResponse { ClientId = 1, TaxReturnId = 2 };

        await PushAsync(gateway);

        var snapshot = client.LastRequest!.Snapshot;
        snapshot.StatementType.ShouldBe(string.Empty);
        snapshot.TotalSales.ShouldNotBeNull();
        snapshot.GstOnSales.ShouldNotBeNull();
        snapshot.InstalmentIncome.ShouldBeNull();
        snapshot.TotalSalaryWages.ShouldBeNull();
        snapshot.Tfn.ShouldBe("123456782");
        snapshot.TotalSalesIncludesGst.ShouldBe(true);
        snapshot.GstReportingOption.ShouldBe("2");
    }

    // ------------------------------------------------------------------------------ plumbing

    private static Task<PushOutcome> PushAsync(PracticeManagerGateway gateway) =>
        gateway.PushAsync(
            new BasPeriod
            {
                FinancialYear = 2026,
                Quarter = 4,
                PeriodStart = new DateOnly(2026, 4, 1),
                PeriodEnd = new DateOnly(2026, 6, 30),
                TotalSales = 31900,
                GstOnSales = 2900,
                GstOnPurchases = 870
            },
            new Worker { FirstName = "Jordan", FamilyName = "Ellis" },
            tfn: "123456782",
            CancellationToken.None);

    private static (PracticeManagerGateway Gateway, FakeClient Client) CreateGateway()
    {
        var client = new FakeClient();
        var gateway = new PracticeManagerGateway(
            client,
            Options.Create(new PracticeManagerOptions { Endpoint = "http://practicemanager.invalid:8081" }),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<PracticeManagerGateway>.Instance);

        return (gateway, client);
    }

    /// <summary>The generated client's async method is virtual for exactly this purpose.</summary>
    private sealed class FakeClient : PracticeManagerApi.PracticeManagerApiClient
    {
        public SyncActivityStatementResponse? Response { get; set; }

        public RpcException? Throws { get; set; }

        public SyncActivityStatementRequest? LastRequest { get; private set; }

        public override AsyncUnaryCall<SyncActivityStatementResponse> SyncActivityStatementAsync(
            SyncActivityStatementRequest request,
            Metadata? headers = null,
            DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            var response = Throws is not null
                ? Task.FromException<SyncActivityStatementResponse>(Throws)
                : Task.FromResult(Response!);

            return new AsyncUnaryCall<SyncActivityStatementResponse>(
                response,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }
    }
}
