using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Bas.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bas.Api.Webhooks;

/// <summary>How hard this service tries to reach a partner.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    /// <summary>Per-request timeout. Short: a partner's endpoint should acknowledge, not process.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Backoff by attempt number; the last entry repeats. Runs to hours, because a partner's
    /// outage is usually measured in those and polling remains available throughout.
    /// </summary>
    public TimeSpan[] RetrySchedule { get; set; } =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6)
    ];

    /// <summary>Attempts before giving up. The partner can still poll for the status.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 10;

    /// <summary>How long a delivered or failed row is kept before it is swept away.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Delivers queued webhooks to partners.
///
/// <para>Deliveries are signed rather than merely posted, because a partner has no other way to
/// know a request came from us — the URL is the only thing an attacker would need otherwise, and
/// URLs leak. The signature is HMAC-SHA256 over <c>timestamp.payload</c>, which is the shape most
/// partner engineers have already implemented for Stripe or GitHub.</para>
///
/// <para>A shared secret is right here in a way it would be wrong on the token endpoint. There the
/// consequence of a leak is that someone mints tokens for any worker and reads their tax data. Here
/// it is that someone sends a partner a false status update — bounded, and not a disclosure.</para>
/// </summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookOptions> options,
    BasMetrics metrics,
    TimeProvider timeProvider,
    ILogger<WebhookDispatcher> logger) : SweepingBackgroundService(timeProvider, logger)
{
    /// <summary>Name of the outbound <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "partner-webhook";

    /// <summary>Header carrying the signature, e.g. <c>t=1756272000,v1=9f2c…</c>.</summary>
    public const string SignatureHeader = "X-Bas-Signature";

    private readonly WebhookOptions _options = options.Value;

    /// <summary>Last time settled rows were swept away. Retention runs at most hourly.</summary>
    private DateTimeOffset _lastRetentionSweep = DateTimeOffset.MinValue;

    protected override bool Enabled => _options.Enabled;

    protected override TimeSpan PollInterval => _options.PollInterval;

    protected override string DisabledMessage => "partners will have to poll.";

    public override async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();

        var due = await db.WebhookDeliveries
            .Include(d => d.Partner)
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var delivery in due)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await AttemptAsync(db, delivery, cancellationToken);
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(cancellationToken);

        await SweepRetentionAsync(db, now, cancellationToken);
    }

    /// <summary>
    /// Deletes settled rows — delivered or given up — older than the retention window, so the
    /// table (payloads included) does not grow without bound. Set-based; payloads never load.
    /// Pending rows are exempt regardless of age: giving up is <see cref="Retry"/>'s decision.
    /// </summary>
    private async Task SweepRetentionAsync(BasDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now - _lastRetentionSweep < TimeSpan.FromHours(1))
            return;

        _lastRetentionSweep = now;
        var cutoff = now - _options.Retention;

        var removed = await db.WebhookDeliveries
            .Where(d => d.Status != WebhookDeliveryStatus.Pending && d.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
            Logger.LogInformation("Swept {Count} settled webhook deliveries older than {Retention}.",
                removed, _options.Retention);
    }

    private async Task AttemptAsync(BasDbContext db, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var now = TimeProvider.GetUtcNow();
        var partner = delivery.Partner;

        if (partner is null || string.IsNullOrWhiteSpace(partner.WebhookUrl))
        {
            // A partner that has not asked for webhooks is not a failure to retry - they poll.
            delivery.Status = WebhookDeliveryStatus.Failed;
            delivery.LastError = "No webhook URL is registered for this partner.";
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, partner.WebhookUrl)
            {
                Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
            };

            request.Headers.Add("X-Bas-Event", delivery.EventType);
            request.Headers.Add("X-Bas-Delivery", delivery.Id.ToString());
            request.Headers.Add(SignatureHeader, Sign(delivery.Payload, partner.WebhookSecret, now));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);

            var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = WebhookDeliveryStatus.Delivered;
                delivery.DeliveredAt = now;
                delivery.LastError = null;

                Logger.LogInformation(
                    "Delivered {Event} {DeliveryId} to partner {ClientId}.",
                    delivery.EventType, delivery.Id, partner.ClientId);
                return;
            }

            Retry(delivery, $"HTTP {(int)response.StatusCode}", now, partner.ClientId,
                // 4xx other than 408/429 means the partner does not want this shape of request;
                // repeating it unchanged will not help, so give up quickly rather than spend hours.
                giveUpNow: IsPermanent(response.StatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                                      or UriFormatException)
        {
            Retry(delivery, ex.Message, now, partner.ClientId, giveUpNow: false);
        }
    }

    private static bool IsPermanent(HttpStatusCode status) =>
        (int)status is >= 400 and < 500
        && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    private void Retry(
        WebhookDelivery delivery, string reason, DateTimeOffset now, string clientId, bool giveUpNow)
    {
        delivery.AttemptCount++;
        delivery.LastError = reason;

        if (giveUpNow || delivery.AttemptCount >= _options.MaxAttempts)
        {
            metrics.WebhookAbandoned();
            delivery.Status = WebhookDeliveryStatus.Failed;

            // Warning rather than error: the partner can still poll, so a statement is never stuck
            // because a webhook did not arrive.
            Logger.LogWarning(
                "Giving up on {Event} {DeliveryId} to partner {ClientId} after {Attempts} attempt(s): {Reason}. " +
                "They can still poll for the status.",
                delivery.EventType, delivery.Id, clientId, delivery.AttemptCount, reason);
            return;
        }

        delivery.NextAttemptAt = now + RetrySchedule.Backoff(_options.RetrySchedule, delivery.AttemptCount);

        Logger.LogWarning(
            "Delivery of {Event} {DeliveryId} to partner {ClientId} failed ({Reason}); retrying at {Next:o}.",
            delivery.EventType, delivery.Id, clientId, reason, delivery.NextAttemptAt);
    }

    /// <summary>
    /// <c>t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.payload"&gt;</c>.
    ///
    /// <para>The timestamp is inside the signed material on purpose: without it a captured request
    /// could be replayed at any point in the future and still verify.</para>
    /// </summary>
    internal static string Sign(string payload, string? secret, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds();
        var signed = $"{timestamp}.{payload}";

        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret ?? string.Empty), Encoding.UTF8.GetBytes(signed));

        return $"t={timestamp},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }
}
