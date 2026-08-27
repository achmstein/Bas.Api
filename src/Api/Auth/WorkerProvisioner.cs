using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Auth;

/// <summary>
/// Resolves a partner's asserted user to a <see cref="Worker"/>, minting one on first contact.
///
/// <para>The lookup key is <c>(PartnerId, PartnerSub)</c> and nothing else. Not email, not name,
/// not phone. A partner assertion naming <c>victim@example.com</c> must never be able to land on
/// an existing worker — this is the one place that rule could be broken, so it is the one place it
/// is stated outright.</para>
/// </summary>
public sealed class WorkerProvisioner(
    BasDbContext db,
    TimeProvider timeProvider,
    ILogger<WorkerProvisioner> logger)
{
    /// <summary>
    /// Returns the worker for <paramref name="partnerSub"/> under <paramref name="partner"/>,
    /// creating the worker and the link if this subject has not been seen before.
    /// </summary>
    public async Task<Guid> ResolveOrProvisionAsync(
        Partner partner, string partnerSub, CancellationToken cancellationToken)
    {
        var existing = await db.PartnerUserLinks
            .SingleOrDefaultAsync(
                l => l.PartnerId == partner.Id && l.PartnerSub == partnerSub, cancellationToken);

        if (existing is not null)
        {
            existing.LastSeenAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return existing.WorkerId;
        }

        var now = timeProvider.GetUtcNow();
        var worker = new Worker { CreatedAt = now };
        var link = new PartnerUserLink
        {
            PartnerId = partner.Id,
            PartnerSub = partnerSub,
            Worker = worker,
            CreatedAt = now,
            LastSeenAt = now
        };

        db.Workers.Add(worker);
        db.PartnerUserLinks.Add(link);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Provisioned worker {WorkerId} for partner {ClientId} on first contact.",
                worker.Id, partner.ClientId);

            return worker.Id;
        }
        catch (DbUpdateException)
        {
            // Two requests for the same brand-new subject arrived together. The unique index on
            // (PartnerId, PartnerSub) is what decided it; re-read and use the winner rather than
            // leaving this partner user with two workers.
            db.ChangeTracker.Clear();

            var winner = await db.PartnerUserLinks
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    l => l.PartnerId == partner.Id && l.PartnerSub == partnerSub, cancellationToken);

            if (winner is null)
                throw;

            logger.LogInformation(
                "Concurrent provisioning for partner {ClientId} resolved to worker {WorkerId}.",
                partner.ClientId, winner.WorkerId);

            return winner.WorkerId;
        }
    }
}
