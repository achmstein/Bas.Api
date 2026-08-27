using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Bas.Api.Sync;
using Bas.Api.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// Status webhooks: what gets queued, what goes out, and what a partner can verify.
///
/// <para>A partner's endpoint is stubbed at the HTTP handler, so the signature and headers on a real
/// request are asserted rather than the dispatcher's intentions.</para>
/// </summary>
public sealed class WebhookTests(WebhookFactory factory) : IClassFixture<WebhookFactory>, IDisposable
{
    private const int Year = 2026;
    private const int Quarter = 4;

    private readonly WebhookFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Submitting_queues_a_status_change()
    {
        var (_, periodId) = await SubmitAsync("wh-submit");

        var delivery = await SingleDeliveryAsync(periodId);
        delivery.EventType.ShouldBe(WebhookEvents.StatusChanged);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);

        var payload = Payload(delivery);
        payload.PreviousStatus.ShouldBe(BasStatuses.Draft);
        payload.Status.ShouldBe(BasStatuses.Submitted);
        payload.PartnerSub.ShouldBe("wh-submit");
    }

    [Fact]
    public async Task A_push_queues_a_second_status_change()
    {
        var (_, periodId) = await SubmitAsync("wh-push");
        _factory.Gateway.Outcome = new PushOutcome.Pushed(1, 2, ["Gst"]);

        await SweepReconcilerAsync();

        var deliveries = await DeliveriesAsync(periodId);
        deliveries.Count.ShouldBe(2);
        Payload(deliveries[^1]).Status.ShouldBe(BasStatuses.Pushed);
    }

    [Fact]
    public async Task A_push_that_changes_nothing_queues_nothing()
    {
        // Unavailable leaves the statement at `submitted`, so there is no change to announce. A
        // webhook per retry would be noise a partner then has to filter.
        var (_, periodId) = await SubmitAsync("wh-nochange");
        _factory.Gateway.Outcome = new PushOutcome.Unavailable("session refused");

        await SweepReconcilerAsync();

        (await DeliveriesAsync(periodId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_payload_carries_no_tax_figures()
    {
        // A webhook passes through logs and proxies we do not control. The partner holds a token
        // and can fetch the detail, so there is nothing to gain by putting figures in it.
        var (_, periodId) = await SubmitAsync("wh-nofigures");

        var raw = (await SingleDeliveryAsync(periodId)).Payload;

        raw.ShouldNotContain("31900");
        raw.ShouldNotContain("2900");
        raw.ShouldNotContain("123456782");
        raw.ShouldNotContain("tfn", Case.Insensitive);
    }

    [Fact]
    public async Task A_delivery_is_signed_and_identified()
    {
        _factory.Endpoint.Respond = HttpStatusCode.OK;

        var (_, periodId) = await SubmitAsync("wh-signed");
        var deliveryId = (await SingleDeliveryAsync(periodId)).Id;

        await DispatchAsync();

        // Looked up by delivery id: a sweep sends everything that is due, so "the last request"
        // could belong to whichever test ran before this one.
        var request = _factory.Endpoint.RequestFor(deliveryId);
        request.ShouldNotBeNull();

        request["X-Bas-Event"].ShouldBe(WebhookEvents.StatusChanged);
        Guid.TryParse(request["X-Bas-Delivery"], out _).ShouldBeTrue();

        // The partner verifies exactly this way, so the test does too.
        var signature = request[WebhookDispatcher.SignatureHeader];
        var parts = signature.Split(',');
        var timestamp = parts[0]["t=".Length..];
        var provided = parts[1]["v1=".Length..];

        var expected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(await _factory.GetWebhookSecretAsync()),
            Encoding.UTF8.GetBytes($"{timestamp}.{request.Body}"))).ToLowerInvariant();

        provided.ShouldBe(expected);
        (await SingleDeliveryAsync(periodId)).Status.ShouldBe(WebhookDeliveryStatus.Delivered);
    }

    [Fact]
    public void The_signature_covers_the_timestamp_as_well_as_the_body()
    {
        // Without the timestamp inside the signed material, a captured request would verify for
        // ever and could be replayed at any point in the future.
        var at = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);

        var first = WebhookDispatcher.Sign("{}", "secret", at);
        var later = WebhookDispatcher.Sign("{}", "secret", at.AddSeconds(1));

        first.ShouldNotBe(later);
    }

    [Fact]
    public void A_different_secret_produces_a_different_signature()
    {
        var at = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);

        WebhookDispatcher.Sign("{}", "secret", at)
            .ShouldNotBe(WebhookDispatcher.Sign("{}", "other", at));
    }

    [Fact]
    public async Task A_failing_endpoint_is_retried()
    {
        var (_, periodId) = await SubmitAsync("wh-retry");
        _factory.Endpoint.Respond = HttpStatusCode.InternalServerError;

        await DispatchAsync();

        var delivery = await SingleDeliveryAsync(periodId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(1);
        delivery.NextAttemptAt.ShouldBeGreaterThan(_factory.Clock.GetUtcNow());
        delivery.LastError!.ShouldContain("500");

        _factory.Endpoint.Respond = HttpStatusCode.OK;
        await MakeDueAsync(periodId);
        await DispatchAsync();

        (await SingleDeliveryAsync(periodId)).Status.ShouldBe(WebhookDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task The_first_delivery_retry_waits_the_first_schedule_entry()
    {
        // The 30-second opening entry exists so a partner blip is retried almost immediately;
        // indexing past it would silently turn that into two minutes.
        var (_, periodId) = await SubmitAsync("wh-first-retry");
        _factory.Endpoint.Respond = HttpStatusCode.InternalServerError;

        await DispatchAsync();

        var delivery = await SingleDeliveryAsync(periodId);
        delivery.AttemptCount.ShouldBe(1);
        delivery.NextAttemptAt.ShouldBe(_factory.Clock.GetUtcNow() + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_rejected_shape_is_given_up_on_immediately()
    {
        // 400 means the partner does not want this request. Repeating it unchanged for six hours
        // would help nobody.
        var (_, periodId) = await SubmitAsync("wh-badrequest");
        _factory.Endpoint.Respond = HttpStatusCode.BadRequest;

        await DispatchAsync();

        var delivery = await SingleDeliveryAsync(periodId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Failed);
        delivery.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task Rate_limiting_is_retried_rather_than_abandoned()
    {
        var (_, periodId) = await SubmitAsync("wh-429");
        _factory.Endpoint.Respond = HttpStatusCode.TooManyRequests;

        await DispatchAsync();

        (await SingleDeliveryAsync(periodId)).Status.ShouldBe(WebhookDeliveryStatus.Pending);
    }

    [Fact]
    public async Task An_exhausted_delivery_gives_up_without_blocking_the_statement()
    {
        var (_, periodId) = await SubmitAsync("wh-exhausted");
        _factory.Endpoint.Respond = HttpStatusCode.ServiceUnavailable;

        for (var i = 0; i < 12; i++)
        {
            await MakeDueAsync(periodId);
            await DispatchAsync();
        }

        (await SingleDeliveryAsync(periodId)).Status.ShouldBe(WebhookDeliveryStatus.Failed);

        // The statement itself is untouched: a partner that cannot be reached can still poll.
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        (await db.BasPeriods.AsNoTracking().SingleAsync(p => p.Id == periodId))
            .Status.ShouldBe(BasPeriodStatus.Submitted);
    }

    [Fact]
    public async Task Settled_deliveries_are_swept_after_the_retention_window()
    {
        // The table keeps payloads, so without retention it grows without bound. Only settled
        // rows are swept: giving up on a pending row is the retry logic's decision, not age's.
        _factory.Endpoint.Respond = HttpStatusCode.OK;
        var (_, deliveredPeriod) = await SubmitAsync("wh-retention-settled");
        await DispatchAsync();
        (await SingleDeliveryAsync(deliveredPeriod)).Status.ShouldBe(WebhookDeliveryStatus.Delivered);

        // A second delivery that will still be pending when the window passes.
        var (_, pendingPeriod) = await SubmitAsync("wh-retention-pending");

        _factory.Clock.Advance(TimeSpan.FromDays(31));
        _factory.Endpoint.Respond = HttpStatusCode.InternalServerError;
        await DispatchAsync();

        (await DeliveriesAsync(deliveredPeriod)).ShouldBeEmpty();

        var pending = await SingleDeliveryAsync(pendingPeriod);
        pending.Status.ShouldBe(WebhookDeliveryStatus.Pending);
    }

    // ------------------------------------------------------------------------------ helpers

    private async Task DispatchAsync()
    {
        await using var scope = _factory.CreateDbScope();
        var dispatcher = scope.ServiceProvider.GetServices<IHostedService>().OfType<WebhookDispatcher>().Single();
        await dispatcher.SweepAsync(CancellationToken.None);
    }

    private async Task SweepReconcilerAsync()
    {
        await using var scope = _factory.CreateDbScope();
        var reconciler = scope.ServiceProvider.GetServices<IHostedService>().OfType<BasReconciler>().Single();
        await reconciler.SweepAsync(CancellationToken.None);
    }

    private async Task MakeDueAsync(Guid periodId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        foreach (var delivery in await db.WebhookDeliveries.Where(d => d.BasPeriodId == periodId).ToListAsync())
            delivery.NextAttemptAt = _factory.Clock.GetUtcNow();

        await db.SaveChangesAsync();
    }

    private async Task<List<WebhookDelivery>> DeliveriesAsync(Guid periodId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        return await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.BasPeriodId == periodId)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync();
    }

    private async Task<WebhookDelivery> SingleDeliveryAsync(Guid periodId) =>
        (await DeliveriesAsync(periodId)).Last();

    private static StatusChangedPayload Payload(WebhookDelivery delivery) =>
        JsonSerializer.Deserialize<StatusChangedPayload>(delivery.Payload)!;

    private async Task<(HttpClient Client, Guid PeriodId)> SubmitAsync(string subject)
    {
        // Configuring the webhook also ensures the partner exists, and captures the secret the
        // server issued - the one every delivery is signed with.
        await _factory.GetWebhookSecretAsync();

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

        await client.PutAsJsonAsync($"/api/v1/bas/{Year}/{Quarter}", new SaveBasRequest
        {
            TotalSales = 31900,
            GstOnSales = 2900,
            GstOnPurchases = 870
        });

        var submit = await client.PostAsync($"/api/v1/bas/{Year}/{Quarter}/submit", null);
        submit.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = (await submit.Content.ReadFromJsonAsync<SubmitBasResponse>())!;
        return (client, body.PeriodId);
    }
}
