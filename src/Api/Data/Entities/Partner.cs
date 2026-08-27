namespace Bas.Api.Data.Entities;

/// <summary>Whether a partner may exchange tokens right now.</summary>
public enum PartnerStatus
{
    /// <summary>Token exchange permitted.</summary>
    Active = 0,

    /// <summary>Kill switch. Exchange fails with <c>invalid_client</c>; existing tokens still
    /// expire on their own within minutes, which is the point of the short TTL.</summary>
    Suspended = 1
}

/// <summary>
/// A registered API partner — MyGigsters is the first. Partners authenticate with an API key this
/// service issues; the key itself is never stored — only its hash — so this table cannot leak one.
/// </summary>
public sealed class Partner
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The <c>iss</c>/<c>sub</c> a partner puts in its client assertion. Unique.</summary>
    public required string ClientId { get; set; }

    /// <summary>Human label for logs and the (phase 3e) admin surface.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// SHA-256 of the API key, lowercase hex. The key itself exists only in the response that
    /// issued it and in the partner's secret manager — a dump of this table authenticates nobody.
    /// Null means no key has been issued yet, and every token request is refused.
    /// </summary>
    public string? ApiKeyHash { get; set; }

    /// <summary>
    /// The first characters of the key (<c>bas_xxxxxxxx</c>), kept readable for lookup and so an
    /// operator can tell which key a partner holds without ever seeing it.
    /// </summary>
    public string? ApiKeyPrefix { get; set; }

    public PartnerStatus Status { get; set; } = PartnerStatus.Active;

    /// <summary>
    /// Space-delimited scopes this partner may request. A token exchange can narrow this but never
    /// widen it, so a partner cannot mint itself a capability it was not granted at registration.
    /// </summary>
    public required string AllowedScopes { get; set; }

    /// <summary>
    /// Where status changes are POSTed, if the partner wants them. Optional: polling
    /// <c>GET /api/v1/bas/{fy}/{q}/status</c> works whether or not this is set, so a partner is
    /// never blocked on standing up an endpoint.
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// Shared secret for the HMAC signature on outbound webhooks. A secret is appropriate here in a
    /// way it would not be on the token endpoint: leaking it lets someone send this partner a false
    /// status update, which is bounded, rather than mint tokens for any worker.
    /// </summary>
    public string? WebhookSecret { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<PartnerUserLink> UserLinks { get; set; } = [];

    /// <summary>The granted scope list, split from <see cref="AllowedScopes"/>.</summary>
    public IReadOnlyList<string> AllowedScopeList =>
        AllowedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
