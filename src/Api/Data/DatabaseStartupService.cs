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
    IOptions<PartnerRegistrationOptions> partnerOptions,
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

        await ReconcilePartnersAsync(db, cancellationToken);

        // Last, because it writes through the schema the migration just established.
        await keyStore.EnsureCurrentAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ReconcilePartnersAsync(BasDbContext db, CancellationToken cancellationToken)
    {
        var registrations = partnerOptions.Value.Registrations;
        if (registrations.Count == 0)
        {
            logger.LogWarning(
                "No partners are registered. Token exchange will refuse every caller until a " +
                "partner is configured under the '{Section}' section.", PartnerRegistrationOptions.SectionName);
            return;
        }

        var now = timeProvider.GetUtcNow();

        foreach (var registration in registrations)
        {
            var status = registration.Active ? PartnerStatus.Active : PartnerStatus.Suspended;

            var existing = await db.Partners
                .SingleOrDefaultAsync(p => p.ClientId == registration.ClientId, cancellationToken);

            if (existing is null)
            {
                db.Partners.Add(new Partner
                {
                    ClientId = registration.ClientId,
                    Name = registration.Name,
                    PublicKeyPem = registration.PublicKeyPem,
                    AllowedScopes = registration.AllowedScopes,
                    Status = status,
                    CreatedAt = now,
                    UpdatedAt = now
                });

                logger.LogInformation(
                    "Registered partner {ClientId} ({Name}) with scopes [{Scopes}].",
                    registration.ClientId, registration.Name, registration.AllowedScopes);

                continue;
            }

            var changed =
                existing.Name != registration.Name ||
                existing.PublicKeyPem != registration.PublicKeyPem ||
                existing.AllowedScopes != registration.AllowedScopes ||
                existing.Status != status;

            if (!changed)
                continue;

            existing.Name = registration.Name;
            existing.PublicKeyPem = registration.PublicKeyPem;
            existing.AllowedScopes = registration.AllowedScopes;
            existing.Status = status;
            existing.UpdatedAt = now;

            logger.LogInformation(
                "Updated partner {ClientId}: scopes [{Scopes}], status {Status}.",
                registration.ClientId, registration.AllowedScopes, status);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
