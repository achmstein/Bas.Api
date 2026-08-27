using System.ComponentModel.DataAnnotations;
using Bas.Api.Data.Entities;
using Grpc.Core;
using Microsoft.Extensions.Options;
using PracticeManager.Api.Contracts;

namespace Bas.Api.Sync;

/// <summary>Connection settings for the Practice Manager service.</summary>
public sealed class PracticeManagerOptions
{
    public const string SectionName = "PracticeManager";

    /// <summary>Base address of PracticeManager.Api's native gRPC endpoint.</summary>
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The shared key it expects in the <c>x-api-key</c> metadata header.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Deadline for one push. Generous on purpose: a cold push does a real browser login against
    /// Xero's bot defence before any data moves, which routinely takes minutes.
    /// </summary>
    public TimeSpan PushTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>What came back from a push attempt.</summary>
public abstract record PushOutcome
{
    private PushOutcome() { }

    /// <summary>Written into Practice Manager and waiting for the agent.</summary>
    public sealed record Pushed(int ClientId, int StatementId, IReadOnlyList<string> SectionsPushed) : PushOutcome;

    /// <summary>
    /// Practice Manager was reachable, but the ATO has not issued a statement for this period yet.
    /// Not a failure — the ATO issues shortly after the period ends.
    /// </summary>
    public sealed record AwaitingStatement(int ClientId) : PushOutcome;

    /// <summary>
    /// Practice Manager could not act right now — its session is refused, or it is unreachable.
    /// Worth retrying without counting against the attempt budget.
    /// </summary>
    public sealed record Unavailable(string Reason) : PushOutcome;

    /// <summary>The push was rejected on its merits. Retrying the same content will not help much.</summary>
    public sealed record Rejected(string Reason) : PushOutcome;
}

/// <summary>Pushes a statement into Practice Manager.</summary>
public interface IPracticeManagerGateway
{
    Task<PushOutcome> PushAsync(BasPeriod period, Worker worker, string tfn, CancellationToken cancellationToken);
}

/// <summary>
/// The gRPC client for <c>SyncActivityStatement</c>.
///
/// <para><b>The snapshot deliberately carries no statement type.</b> Blank means "find the
/// statement the ATO issued; do not create one" — see the proto comment. The ATO chooses the type
/// from obligations neither service can see, and Practice Manager will create a statement of
/// whatever type it is told without complaint, so a guess here produces a wrong statement in the
/// live practice that nobody notices until the agent opens it.</para>
/// </summary>
public sealed class PracticeManagerGateway(
    PracticeManagerApi.PracticeManagerApiClient client,
    IOptions<PracticeManagerOptions> options,
    TimeProvider timeProvider,
    ILogger<PracticeManagerGateway> logger) : IPracticeManagerGateway
{
    private readonly PracticeManagerOptions _options = options.Value;

    public async Task<PushOutcome> PushAsync(
        BasPeriod period, Worker worker, string tfn, CancellationToken cancellationToken)
    {
        var request = new SyncActivityStatementRequest { Snapshot = BuildSnapshot(period, worker, tfn) };

        var metadata = new Metadata();
        if (!string.IsNullOrEmpty(_options.ApiKey))
            metadata.Add("x-api-key", _options.ApiKey);

        try
        {
            var response = await client.SyncActivityStatementAsync(
                request,
                metadata,
                deadline: timeProvider.GetUtcNow().UtcDateTime + _options.PushTimeout,
                cancellationToken: cancellationToken);

            // taxReturnId == 0 is the documented signal that find-only found nothing: the ATO has
            // not issued this statement yet. The client may still have been created, which is both
            // safe and useful - it is what lets Practice Manager raise the statement later.
            if (response.TaxReturnId == 0)
                return new PushOutcome.AwaitingStatement(response.ClientId);

            return new PushOutcome.Pushed(
                response.ClientId, response.TaxReturnId, response.SectionsPushed.ToList());
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.FailedPrecondition)
        {
            // PracticeManager.Api maps "PM will not let us in" to FailedPrecondition precisely so
            // callers can tell it apart from a broken push and stop burning their retry budget.
            return new PushOutcome.Unavailable($"Practice Manager session unavailable: {ex.Status.Detail}");
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return new PushOutcome.Unavailable($"{ex.StatusCode}: {ex.Status.Detail}");
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            // Our key is wrong. Retrying will not fix it, and it should be loud.
            logger.LogError("Practice Manager rejected our API key: {Status}", ex.Status.Detail);
            return new PushOutcome.Rejected($"Not authenticated to Practice Manager: {ex.Status.Detail}");
        }
        catch (RpcException ex)
        {
            return new PushOutcome.Rejected($"{ex.StatusCode}: {ex.Status.Detail}");
        }
    }

    private static ActivityStatementSnapshot BuildSnapshot(BasPeriod period, Worker worker, string tfn)
    {
        var snapshot = new ActivityStatementSnapshot
        {
            Tfn = tfn,

            // Blank on purpose. See the class remarks: find, never create.
            StatementType = string.Empty,

            PeriodStart = period.PeriodStart.ToString("yyyy-MM-dd"),
            PeriodEnd = period.PeriodEnd.ToString("yyyy-MM-dd")
        };

        if (worker.FirstName is not null) snapshot.FirstName = worker.FirstName;
        if (worker.FamilyName is not null) snapshot.FamilyName = worker.FamilyName;
        if (worker.DateOfBirth is not null) snapshot.DateOfBirth = worker.DateOfBirth.Value.ToString("yyyy-MM-dd");
        if (worker.Phone is not null) snapshot.Phone = worker.Phone;
        if (worker.Email is not null) snapshot.Email = worker.Email;

        // Every figure is optional, and an unset one means "leave Practice Manager's value alone".
        // A worker with no PAYG instalment obligation has no T section at all, so writing a zero
        // into one would be a different statement from the one the ATO issued.
        if (period.TotalSales is not null) snapshot.TotalSales = period.TotalSales.Value;
        if (period.GstOnSales is not null) snapshot.GstOnSales = period.GstOnSales.Value;
        if (period.GstOnPurchases is not null) snapshot.GstOnPurchases = period.GstOnPurchases.Value;
        if (period.CashAccountingMethod is not null) snapshot.CashAccountingMethod = period.CashAccountingMethod.Value;

        if (period.InstalmentIncome is not null) snapshot.InstalmentIncome = period.InstalmentIncome.Value;
        if (period.AtoInstalmentAmount is not null) snapshot.AtoInstalmentAmount = period.AtoInstalmentAmount.Value;
        if (period.VariedInstalmentAmount is not null) snapshot.VariedInstalmentAmount = period.VariedInstalmentAmount.Value;
        if (period.VariationReasonCode is not null) snapshot.VariationReasonCode = period.VariationReasonCode;

        if (period.TotalSalaryWages is not null) snapshot.TotalSalaryWages = period.TotalSalaryWages.Value;
        if (period.AmountWithheld is not null) snapshot.AmountWithheld = period.AmountWithheld.Value;

        // G1 is GST-inclusive under Simpler BAS, which is mandatory below $10m turnover and so
        // covers every gig worker this service will ever see.
        snapshot.TotalSalesIncludesGst = true;
        snapshot.GstReportingOption = "2";

        return snapshot;
    }
}
