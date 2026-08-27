using System.ComponentModel.DataAnnotations;

namespace Bas.Api.Auth;

/// <summary>Configuration for the partner token exchange and the tokens it mints.</summary>
public sealed class PartnerAuthOptions
{
    public const string SectionName = "PartnerAuth";

    /// <summary>
    /// The <c>iss</c> stamped on our access tokens and the value partners must target as the
    /// <c>aud</c> of their assertions. Use the service's public origin, e.g.
    /// <c>https://bas.nighttax.com.au</c>.
    /// </summary>
    [Required]
    public string Issuer { get; set; } = "https://bas.nighttax.com.au";

    /// <summary>The <c>aud</c> of tokens we mint, and what the bearer middleware requires.</summary>
    [Required]
    public string Audience { get; set; } = "bas-api";

    /// <summary>
    /// Access-token lifetime. Minutes, deliberately: this token lives on a partner's page, so an
    /// XSS there buys an attacker one window, not a session. Refresh is the partner re-calling
    /// their own token route, which their session already guards.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Longest <c>exp - iat</c> we accept on a partner assertion or subject token. A partner
    /// minting hour-long assertions turns a stolen assertion into an hour of impersonation.
    /// </summary>
    public TimeSpan MaxAssertionLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Tolerance for clock drift between us and the partner. Small on purpose.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Signature algorithms accepted on partner JWTs. Allow-listed rather than "whatever the
    /// header says" — that is what stops <c>alg: none</c> and the HMAC confusion attack where a
    /// token is signed with our own public key as if it were a shared secret.
    /// </summary>
    public string[] AcceptedAssertionAlgorithms { get; set; } =
        ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

}

/// <summary>Settings for the key that signs access tokens.</summary>
public sealed class SigningKeyOptions
{
    public const string SectionName = "SigningKeys";

    /// <summary>RSA modulus size. 2048 is the floor most JWT libraries accept.</summary>
    public int KeySizeBits { get; set; } = 2048;
}
