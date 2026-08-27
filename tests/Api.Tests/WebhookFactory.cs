using System.Net;
using Bas.Api.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bas.Api.Tests;

/// <summary>
/// A host with a registered webhook URL and a stubbed partner endpoint, plus the fake Practice
/// Manager the reconciler tests use.
///
/// <para>The stub sits at the HTTP handler rather than replacing the dispatcher, so what a test
/// asserts is the request as it would actually go over the wire — headers, signature and body.</para>
/// </summary>
public sealed class WebhookFactory : ReconcilerFactory
{
    public const string WebhookUrl = "https://partner.test/hooks/bas";
    public const string WebhookSecret = "a-test-webhook-secret";

    /// <summary>The stubbed partner endpoint.</summary>
    public StubPartnerEndpoint Partner { get; } = new();

    /// <summary>The partner's signing key. Named apart from <see cref="Partner"/>, which is the endpoint.</summary>
    public PartnerSigner Partner_ => base.Partner;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Partners:Registrations:0:WebhookUrl"] = WebhookUrl,
                ["Partners:Registrations:0:WebhookSecret"] = WebhookSecret,
                // Driven by the tests, not by a timer.
                ["Webhooks:Enabled"] = "false"
            }));

        builder.ConfigureServices(services =>
        {
            // Replacing the factory outright rather than calling ConfigurePrimaryHttpMessageHandler
            // again: whether the test's registration runs before or after the app's is not
            // guaranteed, and losing that race means the dispatcher quietly makes real network
            // calls to partner.test - which is slow, flaky, and passes for the wrong reason.
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(Partner));
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
    /// <summary>What the endpoint answers next.</summary>
    public HttpStatusCode Respond { get; set; } = HttpStatusCode.OK;

    private readonly List<RecordedRequest> _requests = [];

    /// <summary>Every request that has arrived, in order.</summary>
    /// <remarks>
    /// Kept as a list rather than a "last request" because the fixture is shared and a dispatch
    /// sweep sends every delivery that is due - including ones another test left pending. A test
    /// that reasoned about "the last request" would be reading someone else's, depending on the
    /// order the tests happened to run in.
    /// </remarks>
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
/// <param name="Headers">Case-insensitive; missing headers read as empty rather than throwing.</param>
public sealed record RecordedRequest(string Url, Dictionary<string, string> Headers, string Body)
{
    public string this[string header] => Headers.TryGetValue(header, out var value) ? value : string.Empty;
}
