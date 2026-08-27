namespace Bas.Api.Data.Entities;

/// <summary>Where one webhook delivery has got to.</summary>
public enum WebhookDeliveryStatus
{
    /// <summary>Not yet delivered. Due at <see cref="WebhookDelivery.NextAttemptAt"/>.</summary>
    Pending = 0,

    /// <summary>The partner answered 2xx.</summary>
    Delivered = 1,

    /// <summary>Attempts exhausted. The partner will have to poll for this one.</summary>
    Failed = 2
}

/// <summary>
/// One status change, queued for delivery to a partner.
///
/// <para><b>Why this is a table and not an HTTP call.</b> The status change that produces it happens
/// inside a reconciler sweep. Calling a partner's endpoint inline would mean a slow or unreachable
/// partner delaying the queue behind PracticeManager, which is a single browser session and the
/// scarcest thing in the system. It would also mean a delivery lost forever if the process
/// restarted mid-call.</para>
///
/// <para>Delivery is <b>at least once</b>. A partner that answers slowly, or answers 200 after the
/// connection has dropped, will be told twice — which is why every delivery carries a stable id
/// for them to deduplicate on.</para>
/// </summary>
public sealed class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The partner to notify. Their URL and secret come from their registration.</summary>
    public Guid PartnerId { get; set; }

    public Partner? Partner { get; set; }

    /// <summary>The statement whose status changed.</summary>
    public Guid BasPeriodId { get; set; }

    public BasPeriod? BasPeriod { get; set; }

    /// <summary>e.g. <c>bas.status_changed</c>.</summary>
    public required string EventType { get; set; }

    /// <summary>
    /// The body, serialised at enqueue time rather than at send time. A delivery describes the
    /// change that happened, not the state as it is when it finally goes out — a retry hours later
    /// must not quietly report a newer status than the one the event was about.
    /// </summary>
    public required string Payload { get; set; }

    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;

    public int AttemptCount { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>The last failure. Never carries a TFN — nothing in a payload does.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }
}
