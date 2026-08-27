using System.Text.Json;
using System.Text.Json.Serialization;
using Bas.Api.Statements;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Webhooks;

/// <summary>Event names a partner can receive.</summary>
public static class WebhookEvents
{
    /// <summary>An activity statement moved to a new status.</summary>
    public const string StatusChanged = "bas.status_changed";
}

/// <summary>
/// The body of a <see cref="WebhookEvents.StatusChanged"/> event.
///
/// <para>Deliberately thin. It says which statement changed and to what, and nothing else — the
/// partner already holds a token and can fetch the detail. Putting figures in a webhook means tax
/// data sitting in whatever logs the delivery passes through, for no gain.</para>
/// </summary>
public sealed record StatusChangedPayload
{
    [JsonPropertyName("event")]
    public required string Event { get; init; }

    /// <summary>Stable per delivery. Deduplicate on this: delivery is at least once.</summary>
    [JsonPropertyName("deliveryId")]
    public required Guid DeliveryId { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The worker, as this service ids them — the <c>sub</c> of their access tokens.</summary>
    [JsonPropertyName("workerId")]
    public required Guid WorkerId { get; init; }

    /// <summary>Your own id for that worker, so you need no lookup table.</summary>
    [JsonPropertyName("partnerSub")]
    public required string PartnerSub { get; init; }

    [JsonPropertyName("financialYear")]
    public required int FinancialYear { get; init; }

    [JsonPropertyName("quarter")]
    public required int Quarter { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("previousStatus")]
    public required string PreviousStatus { get; init; }

    /// <summary>Label 9 as Practice Manager computed it, when known.</summary>
    [JsonPropertyName("netAmount")]
    public int? NetAmount { get; init; }

    /// <summary>Why, when <c>status</c> is <c>failed</c>.</summary>
    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; init; }
}

/// <summary>Queues status changes for delivery to the partner that owns the worker.</summary>
public sealed class WebhookPublisher(BasDbContext db, TimeProvider timeProvider, ILogger<WebhookPublisher> logger)
{
    internal static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Records that <paramref name="period"/> moved from <paramref name="previousStatus"/>.
    ///
    /// <para>Adds to the current unit of work rather than saving — the caller is mid-transition,
    /// and the event must land in the same commit as the status it describes. A webhook for a
    /// status change that then rolled back would be worse than no webhook.</para>
    /// </summary>
    public async Task EnqueueStatusChangeAsync(
        BasPeriod period, BasPeriodStatus previousStatus, CancellationToken cancellationToken)
    {
        if (period.Status == previousStatus)
            return;

        // The link tells us both which partner to notify and what they call this worker.
        var link = await db.PartnerUserLinks
            .AsNoTracking()
            .Where(l => l.WorkerId == period.WorkerId)
            .OrderBy(l => l.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (link is null)
        {
            logger.LogWarning(
                "Statement {PeriodId} changed status but its worker has no partner link; nothing to notify.",
                period.Id);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var deliveryId = Guid.CreateVersion7();

        var payload = new StatusChangedPayload
        {
            Event = WebhookEvents.StatusChanged,
            DeliveryId = deliveryId,
            OccurredAt = now,
            WorkerId = period.WorkerId,
            PartnerSub = link.PartnerSub,
            FinancialYear = period.FinancialYear,
            Quarter = period.Quarter,
            Status = BasPeriodService.ToWireStatus(period.Status),
            PreviousStatus = BasPeriodService.ToWireStatus(previousStatus),
            NetAmount = period.NetAmount,
            FailureReason = period.FailureReason
        };

        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = deliveryId,
            PartnerId = link.PartnerId,
            BasPeriodId = period.Id,
            EventType = WebhookEvents.StatusChanged,
            Payload = JsonSerializer.Serialize(payload, PayloadJson),
            Status = WebhookDeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now
        });
    }
}
