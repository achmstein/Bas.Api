namespace Bas.Api.Statements;

/// <summary>Registration for everything under <c>Statements/</c>.</summary>
public static class StatementsServiceCollectionExtensions
{
    public static IHostApplicationBuilder AddStatements(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<WorkerIdentityService>();
        builder.Services.AddScoped<BasPeriodService>();

        return builder;
    }
}
