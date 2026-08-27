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
/// A registered API partner — MyGigsters is the first. There is deliberately <b>no secret</b>
/// here: partners authenticate by signing a JWT with their own private key and we hold only the
/// public half, so nothing secret ever crosses the wire or sits in this table.
/// </summary>
public sealed class Partner
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The <c>iss</c>/<c>sub</c> a partner puts in its client assertion. Unique.</summary>
    public required string ClientId { get; set; }

    /// <summary>Human label for logs and the (phase 3e) admin surface.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The partner's public signing key, PEM-encoded (RSA or ECDSA). Public by definition — this
    /// column is not a secret and does not need protecting. We only ever verify with it.
    /// </summary>
    public required string PublicKeyPem { get; set; }

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
