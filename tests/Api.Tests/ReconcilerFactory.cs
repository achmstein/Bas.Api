using System.Net;
using System.Net.Http.Json;
using Bas.Api.Admin;
using Bas.Api.Data.Entities;
using Bas.Api.Sync;

namespace Bas.Api.Tests;

/// <summary>A Practice Manager that answers whatever the test tells it to.</summary>
public sealed class FakePracticeManager : IPracticeManagerGateway
{
    private int _calls;

    /// <summary>What the next push returns.</summary>
    public PushOutcome Outcome { get; set; } = new PushOutcome.Pushed(1, 1, ["Gst"]);

    /// <summary>How many pushes have been attempted. Proves what was, and was not, sent.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The statement of the most recent push.</summary>
    public BasPeriod? LastPeriod { get; private set; }

    /// <summary>The TFN of the most recent push.</summary>
    public string? LastTfn { get; private set; }

    public Task<PushOutcome> PushAsync(
        BasPeriod period, Worker worker, string tfn, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        LastPeriod = period;
        LastTfn = tfn;

        return Task.FromResult(Outcome);
    }
}

/// <summary>
/// A host whose partner has a webhook configured — through the real admin endpoint, so the signing
/// secret is the one the server issued, exactly as a production partner's would be.
/// </summary>
public sealed class WebhookFactory : ReconcilerFactory
{
    public const string WebhookUrl = "https://partner.test/hooks/bas";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _webhookSecret;

    /// <summary>The stubbed partner endpoint every delivery lands on.</summary>
    public StubPartnerEndpoint Endpoint { get; } = new();

    /// <summary>The signing secret the server issued when the webhook was configured.</summary>
    public async Task<string> GetWebhookSecretAsync()
    {
        if (_webhookSecret is not null)
            return _webhookSecret;

        await _gate.WaitAsync();
        try
        {
            if (_webhookSecret is not null)
                return _webhookSecret;

            // The partner has to exist before its webhook can.
            await GetPartnerApiKeyAsync();

            using var client = CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Put, $"/admin/v1/partners/{PartnerClientId}/webhook")
            {
                Content = JsonContent.Create(new SetWebhookRequest { Url = WebhookUrl })
            };
            request.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminKey);

            using var response = await client.SendAsync(request);
            var result = await response.Content.ReadFromJsonAsync<WebhookResult>();

            _webhookSecret = result!.Secret
                ?? throw new InvalidOperationException("Setting the webhook issued no secret.");
            return _webhookSecret;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Replacing the factory outright rather than reconfiguring the named client: losing
            // that registration race means the dispatcher quietly makes real network calls to
            // partner.test, which is slow, flaky, and passes for the wrong reason.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .RemoveAll<IHttpClientFactory>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
                .AddSingleton<IHttpClientFactory>(services, new StubHttpClientFactory(Endpoint));
        });
    }
}

/// <summary>Hands out clients that all speak to the stub.</summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    // Deliberately not disposing the handler with the client: one handler records every request
    // across a test, and the default HttpClient would dispose it after the first.
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>A partner's webhook endpoint, recorded rather than real.</summary>
public sealed class StubPartnerEndpoint : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>What the endpoint answers next.</summary>
    public HttpStatusCode Respond { get; set; } = HttpStatusCode.OK;

    /// <summary>
    /// Every request that has arrived, in order. A list rather than "the last one": the fixture is
    /// shared and a dispatch sweep sends every delivery that is due, including ones another test
    /// left pending, so "last" depends on run order.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    /// <summary>The request carrying <paramref name="deliveryId"/>, if it has arrived.</summary>
    public RecordedRequest? RequestFor(Guid deliveryId) =>
        Requests.LastOrDefault(r => r["X-Bas-Delivery"] == deliveryId.ToString());

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(
            h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        var recorded = new RecordedRequest(
            request.RequestUri?.ToString() ?? string.Empty,
            headers,
            request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        lock (_requests)
            _requests.Add(recorded);

        return new HttpResponseMessage(Respond);
    }
}

/// <summary>One captured webhook request.</summary>
/// <param name="Headers">Case-insensitive; a missing header reads as empty rather than throwing.</param>
public sealed record RecordedRequest(string Url, Dictionary<string, string> Headers, string Body)
{
    public string this[string header] => Headers.TryGetValue(header, out var value) ? value : string.Empty;
}
