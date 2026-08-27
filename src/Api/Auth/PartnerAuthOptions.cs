using System.ComponentModel.DataAnnotations;

namespace Bas.Api.Auth;

/// <summary>Configuration for the partner token exchange and the tokens it mints.</summary>
public sealed class PartnerAuthOptions
{
    public const string SectionName = "PartnerAuth";

    /// <summary>The <c>iss</c> stamped on the access tokens this service mints.</summary>
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

    /// <summary>Tolerance when validating our own tokens' lifetimes.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Settings for the key that signs access tokens.</summary>
public sealed class SigningKeyOptions
{
    public const string SectionName = "SigningKeys";

    /// <summary>RSA modulus size. 2048 is the floor most JWT libraries accept.</summary>
    public int KeySizeBits { get; set; } = 2048;
}
