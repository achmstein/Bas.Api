using Bas.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bas.Api.Sync;

/// <summary>
/// Registration for everything under <c>Sync/</c>: the push into Practice Manager, and the ledger
/// that owns retrying it.
/// </summary>
public static class SyncServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddSync(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<PracticeManagerOptions>()
            .Bind(builder.Configuration.GetSection(PracticeManagerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<ReconcilerOptions>()
            .Bind(builder.Configuration.GetSection(ReconcilerOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => o.PollInterval > TimeSpan.Zero
                     && o.AwaitingStatementInterval > TimeSpan.Zero
                     && o.RetrySchedule.All(t => t > TimeSpan.Zero),
                "Reconciler intervals and every retry-schedule entry must be positive.")
            .ValidateOnStart();

        builder.Services
            .AddGrpcClient<PracticeManager.Api.Contracts.PracticeManagerApi.PracticeManagerApiClient>((sp, o) =>
                o.Address = new Uri(sp.GetRequiredService<IOptions<PracticeManagerOptions>>().Value.Endpoint));

        // TryAdd because the webhook slice registers it too — whichever runs first wins.
        builder.Services.TryAddSingleton<BasMetrics>();

        builder.Services.AddScoped<IPracticeManagerGateway, PracticeManagerGateway>();
        builder.Services.AddHostedService<BasReconciler>();

        return builder;
    }
}
