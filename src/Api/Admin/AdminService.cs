using System.Security.Cryptography;
using System.Text;
using Bas.Api.Auth;
using Bas.Api.Statements;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Admin;

/// <summary>Everything the admin surface can do, and the audit trail it leaves.</summary>
public sealed class AdminService(
    BasDbContext db,
    IPartnerKeyStore keyStore,
    IAdminActor actor,
    TimeProvider timeProvider,
    ILogger<AdminService> logger)
{
    // ------------------------------------------------------------------------------ partners

    public async Task<IReadOnlyList<AdminPartnerResponse>> ListPartnersAsync(CancellationToken cancellationToken)
    {
        var partners = await db.Partners.AsNoTracking().OrderBy(p => p.ClientId).ToListAsync(cancellationToken);

        var counts = await db.PartnerUserLinks
            .AsNoTracking()
            .GroupBy(l => l.PartnerId)
            .Select(g => new { PartnerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PartnerId, x => x.Count, cancellationToken);

        return partners
            .Select(p => ToResponse(p, counts.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<AdminPartnerResponse?> GetPartnerAsync(string clientId, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.AsNoTracking()
            .SingleOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (partner is null)
            return null;

        var workers = await db.PartnerUserLinks.CountAsync(l => l.PartnerId == partner.Id, cancellationToken);
        return ToResponse(partner, workers);
    }

    public async Task<(AdminPartnerResponse? Partner, BasError? Error)> CreatePartnerAsync(
        CreatePartnerRequest request, CancellationToken cancellationToken)
    {
        if (await db.Partners.AnyAsync(p => p.ClientId == request.ClientId, cancellationToken))
            return (null, Conflict($"A partner with client id '{request.ClientId}' already exists."));

        if (Validate(request.PublicKeyPem, request.AllowedScopes) is { } invalid)
            return (null, invalid);

        var now = timeProvider.GetUtcNow();
        var partner = new Partner
        {
            ClientId = request.ClientId,
            Name = request.Name,
            PublicKeyPem = request.PublicKeyPem,
            AllowedScopes = request.AllowedScopes,
            WebhookUrl = request.WebhookUrl,
            WebhookSecret = request.WebhookSecret,
            Status = PartnerStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Partners.Add(partner);
        Audit("partner.created", partner.ClientId,
            $"scopes [{partner.AllowedScopes}], key {Fingerprint(partner.PublicKeyPem)}");

        await db.SaveChangesAsync(cancellationToken);

        return (ToResponse(partner, 0), null);
    }

    /// <summary>
    /// Replaces the partner's signing key. The operation a suspected leak needs, which is why it
    /// takes effect on the next request rather than the next deploy.
    /// </summary>
    public async Task<(AdminPartnerResponse? Partner, BasError? Error)> RotateKeyAsync(
        string clientId, RotateKeyRequest request, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);
        if (partner is null)
            return (null, NotFound(clientId));

        if (Validate(request.PublicKeyPem, partner.AllowedScopes) is { } invalid)
            return (null, invalid);

        var previous = Fingerprint(partner.PublicKeyPem);

        partner.PublicKeyPem = request.PublicKeyPem;
        partner.UpdatedAt = timeProvider.GetUtcNow();

        // The fingerprints, never the key itself. A public key is not a secret, but an audit log
        // full of PEM blocks is unreadable and invites the habit of pasting key material into one.
        Audit("partner.key_rotated", clientId,
            $"{previous} -> {Fingerprint(partner.PublicKeyPem)}");

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Partner {ClientId} signing key rotated by {Actor}. Assertions signed with the old key will " +
            "now be refused.", clientId, actor.Name);

        return (ToResponse(partner, await WorkerCountAsync(partner.Id, cancellationToken)), null);
    }

    /// <summary>
    /// The kill switch. Token exchange starts failing immediately; tokens already minted expire on
    /// their own within minutes, which is what the short lifetime is for.
    /// </summary>
    public async Task<(AdminPartnerResponse? Partner, BasError? Error)> SetStatusAsync(
        string clientId, bool active, string? reason, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);
        if (partner is null)
            return (null, NotFound(clientId));

        partner.Status = active ? PartnerStatus.Active : PartnerStatus.Suspended;
        partner.UpdatedAt = timeProvider.GetUtcNow();

        Audit(active ? "partner.resumed" : "partner.suspended", clientId, reason);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Partner {ClientId} {Action} by {Actor}. Reason: {Reason}",
            clientId, active ? "resumed" : "SUSPENDED", actor.Name, reason ?? "(none given)");

        return (ToResponse(partner, await WorkerCountAsync(partner.Id, cancellationToken)), null);
    }

    // ---------------------------------------------------------------------------- lodgements

    /// <summary>
    /// What is in flight, newest first. TFNs are never included — Practice Manager holds the real
    /// one, and no operational question needs it.
    /// </summary>
    public async Task<IReadOnlyList<AdminLodgementResponse>> ListLodgementsAsync(
        string? status, int limit, CancellationToken cancellationToken)
    {
        var query =
            from period in db.BasPeriods.AsNoTracking()
            join sync in db.SyncStates.AsNoTracking() on period.Id equals sync.BasPeriodId into syncs
            from sync in syncs.DefaultIfEmpty()
            join link in db.PartnerUserLinks.AsNoTracking() on period.WorkerId equals link.WorkerId into links
            from link in links.DefaultIfEmpty()
            join partner in db.Partners.AsNoTracking() on link.PartnerId equals partner.Id into partners
            from partner in partners.DefaultIfEmpty()
            select new { period, sync, partner, link };

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<BasPeriodStatus>(status.Replace("_", ""), ignoreCase: true, out var parsed))
        {
            query = query.Where(x => x.period.Status == parsed);
        }

        var rows = await query
            .OrderByDescending(x => x.period.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return rows.Select(x => new AdminLodgementResponse
        {
            PeriodId = x.period.Id,
            WorkerId = x.period.WorkerId,
            PartnerId = x.partner?.ClientId,
            PartnerSub = x.link?.PartnerSub,
            FinancialYear = x.period.FinancialYear,
            Quarter = x.period.Quarter,
            Status = BasPeriodService.ToWireStatus(x.period.Status),
            NetAmount = x.period.NetAmount,
            SubmittedAt = x.period.SubmittedAt,
            UpdatedAt = x.period.UpdatedAt,
            FailureReason = x.period.FailureReason,
            SyncStatus = x.sync?.Status.ToString(),
            AttemptCount = x.sync?.AttemptCount,
            NextAttemptAt = x.sync?.NextAttemptAt,
            LastError = x.sync?.LastError
        }).ToList();
    }

    /// <summary>
    /// Puts a statement back in front of the reconciler now, clearing whatever backoff it was
    /// serving. The button you want after fixing whatever was actually wrong.
    /// </summary>
    public async Task<(AdminLodgementResponse? Lodgement, BasError? Error)> RetryLodgementAsync(
        Guid periodId, CancellationToken cancellationToken)
    {
        var period = await db.BasPeriods.SingleOrDefaultAsync(p => p.Id == periodId, cancellationToken);
        if (period is null)
            return (null, new BasError(StatusCodes.Status404NotFound, "Unknown statement", "No such statement."));

        if (period.Status is BasPeriodStatus.Draft)
        {
            return (null, Conflict(
                "This statement is still a draft. There is nothing to retry until the worker submits it."));
        }

        var now = timeProvider.GetUtcNow();
        var state = await db.SyncStates.SingleOrDefaultAsync(s => s.BasPeriodId == periodId, cancellationToken);

        if (state is null)
        {
            state = new SyncState { BasPeriodId = periodId, CreatedAt = now };
            db.SyncStates.Add(state);
        }

        state.Status = SyncStatus.Pending;
        state.AttemptCount = 0;
        state.LastError = null;
        state.NextAttemptAt = now;
        state.DirtyAt = now;
        state.UpdatedAt = now;

        // Back to submitted so the reconciler treats it as work, and so the partner sees it move.
        if (period.Status is BasPeriodStatus.Failed)
        {
            period.Status = BasPeriodStatus.Submitted;
            period.FailureReason = null;
            period.UpdatedAt = now;
        }

        Audit("lodgement.retried", periodId.ToString(), $"FY{period.FinancialYear} Q{period.Quarter}");
        await db.SaveChangesAsync(cancellationToken);

        var refreshed = await ListLodgementsAsync(null, 200, cancellationToken);
        return (refreshed.FirstOrDefault(l => l.PeriodId == periodId), null);
    }

    // --------------------------------------------------------------------------------- audit

    public async Task<IReadOnlyList<AuditEntryResponse>> ListAuditAsync(
        string? subject, int limit, CancellationToken cancellationToken)
    {
        var query = db.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(e => e.Subject == subject);

        return await query
            .OrderByDescending(e => e.At)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(e => new AuditEntryResponse
            {
                At = e.At,
                Action = e.Action,
                Actor = e.Actor,
                Subject = e.Subject,
                Detail = e.Detail
            })
            .ToListAsync(cancellationToken);
    }

    // ----------------------------------------------------------------------------- internals

    /// <summary>
    /// Adds the entry to the current unit of work. Never saved separately: an audit trail that can
    /// commit while the change it describes rolls back is worse than none.
    /// </summary>
    private void Audit(string action, string? subject, string? detail) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Action = action,
            Actor = actor.Name,
            Subject = subject,
            Detail = detail,
            At = timeProvider.GetUtcNow()
        });

    private Task<int> WorkerCountAsync(Guid partnerId, CancellationToken cancellationToken) =>
        db.PartnerUserLinks.CountAsync(l => l.PartnerId == partnerId, cancellationToken);

    private BasError? Validate(string publicKeyPem, string allowedScopes)
    {
        var probe = new Partner
        {
            ClientId = "validation",
            Name = "validation",
            PublicKeyPem = publicKeyPem,
            AllowedScopes = allowedScopes
        };

        // Parsed now rather than discovered at the partner's next token exchange.
        if (keyStore.GetKey(probe) is null)
        {
            return new BasError(
                StatusCodes.Status400BadRequest,
                "Invalid public key",
                "publicKeyPem must be a PEM-encoded RSA or ECDSA PUBLIC key. A private key is refused.");
        }

        var scopes = allowedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Length == 0)
            return new BasError(StatusCodes.Status400BadRequest, "No scopes", "allowedScopes cannot be empty.");

        foreach (var scope in scopes)
        {
            if (!BasScopes.All.Contains(scope, StringComparer.Ordinal))
                return new BasError(StatusCodes.Status400BadRequest, "Unknown scope", $"'{scope}' is not a known scope.");
        }

        return null;
    }

    /// <summary>A short, stable fingerprint of a public key — enough to tell two apart in a log.</summary>
    internal static string Fingerprint(string publicKeyPem)
    {
        var normalised = new string(publicKeyPem.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));

        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static BasError NotFound(string clientId) =>
        new(StatusCodes.Status404NotFound, "Unknown partner", $"No partner with client id '{clientId}'.");

    private static BasError Conflict(string detail) =>
        new(StatusCodes.Status409Conflict, "Conflict", detail);

    private static AdminPartnerResponse ToResponse(Partner p, int workerCount) => new()
    {
        ClientId = p.ClientId,
        Name = p.Name,
        Status = p.Status.ToString().ToLowerInvariant(),
        AllowedScopes = p.AllowedScopes,
        PublicKeyFingerprint = Fingerprint(p.PublicKeyPem),
        WebhookUrl = p.WebhookUrl,
        // Whether a secret is set, never the secret.
        HasWebhookSecret = !string.IsNullOrEmpty(p.WebhookSecret),
        WorkerCount = workerCount,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt
    };
}
