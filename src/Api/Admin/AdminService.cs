using System.Security.Cryptography;
using Bas.Api.Auth;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Bas.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Admin;

/// <summary>Everything the admin surface can do, and the audit trail it leaves.</summary>
public sealed class AdminService(
    BasDbContext db,
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

    /// <summary>
    /// Registers a partner and issues their API key.
    ///
    /// <para>The key comes back in the result and is <b>never stored</b> — the database keeps its
    /// hash and a short prefix. It cannot be shown again: if it could, it would have to be stored,
    /// and a dump of this database would authenticate as every partner at once. The operator gets
    /// one chance to save it, the same bargain AWS and GitHub make for a secret key.</para>
    /// </summary>
    public async Task<(CreatePartnerResult? Result, BasError? Error)> CreatePartnerAsync(
        CreatePartnerRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId))
            return (null, new BasError(StatusCodes.Status400BadRequest, "Client id required", "clientId cannot be empty."));

        if (await db.Partners.AnyAsync(p => p.ClientId == request.ClientId, cancellationToken))
            return (null, Conflict($"A partner with client id '{request.ClientId}' already exists."));

        if (ValidateScopes(request.AllowedScopes) is { } invalid)
            return (null, invalid);

        var issued = PartnerApiKey.Generate();

        var now = timeProvider.GetUtcNow();
        var partner = new Partner
        {
            ClientId = request.ClientId,
            Name = request.Name,
            ApiKeyHash = issued.Hash,
            ApiKeyPrefix = issued.Prefix,
            AllowedScopes = request.AllowedScopes,
            WebhookUrl = request.WebhookUrl,
            WebhookSecret = request.WebhookSecret,
            Status = PartnerStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Partners.Add(partner);

        // The prefix, never the key. An audit log is read by more people than a database is.
        Audit("partner.created", partner.ClientId,
            $"scopes [{partner.AllowedScopes}], key {issued.Prefix}…");

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Partner {ClientId} registered by {Actor} with key {Prefix}…",
            partner.ClientId, actor.Name, issued.Prefix);

        return (new CreatePartnerResult
        {
            Partner = ToResponse(partner, 0),
            ApiKey = issued.Key
        }, null);
    }

    /// <summary>
    /// Replaces the partner's API key. The operation a suspected leak needs: the old key stops
    /// working the moment this commits, and the new one is returned once, never stored.
    /// </summary>
    public async Task<(CreatePartnerResult? Result, BasError? Error)> RotateKeyAsync(
        string clientId, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);
        if (partner is null)
            return (null, NotFound(clientId));

        var previous = partner.ApiKeyPrefix ?? "(none)";
        var issued = PartnerApiKey.Generate();

        partner.ApiKeyHash = issued.Hash;
        partner.ApiKeyPrefix = issued.Prefix;
        partner.UpdatedAt = timeProvider.GetUtcNow();

        Audit("partner.key_rotated", clientId, $"{previous}… -> {issued.Prefix}…");

        await db.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Partner {ClientId} API key rotated by {Actor}. The previous key is refused from now.",
            clientId, actor.Name);

        return (new CreatePartnerResult
        {
            Partner = ToResponse(partner, await WorkerCountAsync(partner.Id, cancellationToken)),
            ApiKey = issued.Key
        }, null);
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

    /// <summary>
    /// Sets where a partner's status changes are delivered, and optionally issues a new signing
    /// secret for them.
    ///
    /// <para>Changing the URL never rotates the secret on its own. If it did, correcting a typo in
    /// the address would silently break every signature the partner verifies, and they would see
    /// deliveries start failing with nothing to explain it.</para>
    /// </summary>
    public async Task<(WebhookResult? Result, BasError? Error)> SetWebhookAsync(
        string clientId, string? url, bool newSecret, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);
        if (partner is null)
            return (null, NotFound(clientId));

        url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();

        if (url is not null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                return (null, new BasError(StatusCodes.Status400BadRequest, "Invalid webhook URL", "It must be an absolute https URL."));
        }

        string? issued = null;

        if (url is null)
        {
            // Clearing the address retires the secret with it - a secret for an endpoint that no
            // longer exists is just something else to leak.
            partner.WebhookUrl = null;
            partner.WebhookSecret = null;
        }
        else
        {
            partner.WebhookUrl = url;

            if (newSecret || string.IsNullOrEmpty(partner.WebhookSecret))
            {
                issued = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .Replace("+", "").Replace("/", "").Replace("=", "");
                partner.WebhookSecret = issued;
            }
        }

        partner.UpdatedAt = timeProvider.GetUtcNow();

        Audit("partner.webhook_changed", clientId,
            url is null ? "removed" : $"{url}{(issued is null ? "" : ", new secret issued")}");

        await db.SaveChangesAsync(cancellationToken);

        return (new WebhookResult
        {
            Partner = ToResponse(partner, await WorkerCountAsync(partner.Id, cancellationToken)),
            Secret = issued
        }, null);
    }

    // ---------------------------------------------------------------------------- lodgements

    /// <summary>
    /// What is in flight, newest first. TFNs are never included — Practice Manager holds the real
    /// one, and no operational question needs it.
    /// </summary>
    public async Task<IReadOnlyList<AdminLodgementResponse>> ListLodgementsAsync(
        string? status, int limit, CancellationToken cancellationToken)
    {
        var query = LodgementQuery(db.BasPeriods.AsNoTracking());

        if (!string.IsNullOrWhiteSpace(status)
            && Enum.TryParse<BasPeriodStatus>(status.Replace("_", ""), ignoreCase: true, out var parsed))
        {
            query = query.Where(x => x.Period.Status == parsed);
        }

        var rows = await query
            .OrderByDescending(x => x.Period.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        return rows.Select(ToLodgement).ToList();
    }

    /// <summary>One lodgement by its period id, in the same shape the list uses.</summary>
    public async Task<AdminLodgementResponse?> GetLodgementAsync(Guid periodId, CancellationToken cancellationToken)
    {
        var row = await LodgementQuery(db.BasPeriods.AsNoTracking().Where(p => p.Id == periodId))
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : ToLodgement(row);
    }

    // The link join cannot fan out and skew Take: WorkerId is unique in partner_user_links.
    private IQueryable<LodgementRow> LodgementQuery(IQueryable<BasPeriod> periods) =>
        from period in periods
        join sync in db.SyncStates.AsNoTracking() on period.Id equals sync.BasPeriodId into syncs
        from sync in syncs.DefaultIfEmpty()
        join link in db.PartnerUserLinks.AsNoTracking() on period.WorkerId equals link.WorkerId into links
        from link in links.DefaultIfEmpty()
        join partner in db.Partners.AsNoTracking() on link.PartnerId equals partner.Id into partners
        from partner in partners.DefaultIfEmpty()
        select new LodgementRow { Period = period, Sync = sync, Partner = partner, Link = link };

    private static AdminLodgementResponse ToLodgement(LodgementRow x) => new()
    {
        PeriodId = x.Period.Id,
        WorkerId = x.Period.WorkerId,
        PartnerId = x.Partner?.ClientId,
        PartnerSub = x.Link?.PartnerSub,
        FinancialYear = x.Period.FinancialYear,
        Quarter = x.Period.Quarter,
        Status = x.Period.Status.ToWireStatus(),
        NetAmount = x.Period.NetAmount,
        SubmittedAt = x.Period.SubmittedAt,
        UpdatedAt = x.Period.UpdatedAt,
        FailureReason = x.Period.FailureReason,
        SyncStatus = x.Sync?.Status.ToString(),
        AttemptCount = x.Sync?.AttemptCount,
        NextAttemptAt = x.Sync?.NextAttemptAt,
        LastError = x.Sync?.LastError
    };

    private sealed class LodgementRow
    {
        public required BasPeriod Period { get; init; }
        public SyncState? Sync { get; init; }
        public Partner? Partner { get; init; }
        public PartnerUserLink? Link { get; init; }
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
        state.TransientAttemptCount = 0;
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

        // Looked up directly: fishing the row back out of the recent-200 list returned null for a
        // mutation that had succeeded whenever the period fell outside it.
        return (await GetLodgementAsync(periodId, cancellationToken), null);
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

    private static BasError? ValidateScopes(string allowedScopes)
    {
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
        // The prefix, so an operator can tell which key a partner holds without ever seeing it.
        ApiKeyPrefix = p.ApiKeyPrefix,
        WebhookUrl = p.WebhookUrl,
        // Whether a secret is set, never the secret.
        HasWebhookSecret = !string.IsNullOrEmpty(p.WebhookSecret),
        WorkerCount = workerCount,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt
    };
}
