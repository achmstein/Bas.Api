using System.ComponentModel.DataAnnotations;

namespace Bas.Api.Data;

/// <summary>
/// Partners declared in configuration and reconciled into the database at startup.
///
/// <para>The admin surface for registering, rotating and suspending partners is phase 3e. Until it
/// exists this is how MyGigsters gets registered — and it is a reasonable permanent arrangement
/// too, because none of it is secret: a client id, a display name, a public JWKS URL and a scope
/// grant. Nothing here would be dangerous in a repository, though the values themselves live in
/// deployment configuration.</para>
///
/// <para>Reconciliation is deliberately additive: a partner in configuration is created or
/// updated, but a partner <em>absent</em> from configuration is left alone rather than deleted. A
/// truncated config file should not silently orphan every worker link a partner owns.</para>
/// </summary>
public sealed class PartnerRegistrationOptions
{
    public const string SectionName = "Partners";

    public List<PartnerRegistration> Registrations { get; set; } = [];
}

/// <summary>One partner's registration record.</summary>
public sealed class PartnerRegistration
{
    /// <summary>The <c>iss</c> the partner puts in its client assertion.</summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>The partner's PEM-encoded public signing key, RSA or ECDSA.</summary>
    [Required]
    public string PublicKeyPem { get; set; } = string.Empty;

    /// <summary>Space-delimited scopes. A token exchange can narrow this, never widen it.</summary>
    [Required]
    public string AllowedScopes { get; set; } = string.Empty;

    /// <summary>Optional. Where to POST status changes. Polling works regardless.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Shared secret for the webhook HMAC signature. Required if a URL is set.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Set false to suspend without removing the registration — the kill switch.</summary>
    public bool Active { get; set; } = true;
}
