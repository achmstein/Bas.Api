using Bas.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bas.Api.Webhooks;

/// <summary>
/// Registration for everything under <c>Webhooks/</c>. Optional for a partner - polling the status
/// route works whether or not they register a URL - so nothing here is on the critical path of a
/// lodgement.
/// </summary>
public static class WebhookServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddWebhooks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<WebhookOptions>()
            .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => o.PollInterval > TimeSpan.Zero
                     && o.RequestTimeout > TimeSpan.Zero
                     && o.Retention > TimeSpan.Zero
                     && o.RetrySchedule.All(t => t > TimeSpan.Zero),
                "Webhook intervals, retention and every retry-schedule entry must be positive.")
            .ValidateOnStart();

        builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A redirect would deliver a signed payload to an address the partner did not register.
                AllowAutoRedirect = false
            });

        // TryAdd because the sync slice registers it too — whichever runs first wins.
        builder.Services.TryAddSingleton<BasMetrics>();

        builder.Services.AddScoped<WebhookPublisher>();
        builder.Services.AddHostedService<WebhookDispatcher>();

        return builder;
    }
}
