using Bas.Api.Admin;
using Bas.Api.Auth;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bas.Api.Data;

/// <summary>Controls what the service does to its schema on the way up.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Applies pending EF migrations at startup. True by design for this deployment: it is a
    /// single container on a single box, the same shape as PracticeManager.Api, so there is no
    /// rolling window during which two schema versions must coexist.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>
/// Brings the service to a state where it can answer a token request: schema migrated, partners
/// reconciled from configuration, and a signing key present.
///
/// <para>All of it happens before the first request rather than lazily, so a misconfiguration
/// surfaces as a failed deploy instead of as a partner's first token exchange failing.</para>
/// </summary>
public sealed class DatabaseStartupService(
    IServiceScopeFactory scopeFactory,
    ISigningKeyStore keyStore,
    IOptions<DatabaseOptions> databaseOptions,
    TimeProvider timeProvider,
    ILogger<DatabaseStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();

        if (databaseOptions.Value.MigrateOnStartup)
        {
            logger.LogInformation("Applying database migrations.");
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (!await db.Partners.AnyAsync(cancellationToken))
        {
            logger.LogInformation("No partners are registered yet. Register one from /admin/partners.");
        }

        // Admin accounts, so a fresh deployment has someone who can sign in.
        await scope.ServiceProvider.GetRequiredService<AdminIdentitySeeder>().SeedAsync(cancellationToken);

        // Last, because it writes through the schema the migration just established.
        await keyStore.EnsureCurrentAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
