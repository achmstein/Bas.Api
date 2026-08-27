using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bas.Api.Data.Entities;
using Microsoft.IdentityModel.Tokens;

namespace Bas.Api.Auth;

/// <summary>Supplies the public key a partner signs its JWTs with.</summary>
public interface IPartnerKeyStore
{
    /// <summary>
    /// The verification key registered for <paramref name="partner"/>, or <see langword="null"/>
    /// if what was registered cannot be parsed.
    /// </summary>
    SecurityKey? GetKey(Partner partner);
}

/// <summary>
/// Reads the partner's public key straight off their registration.
///
/// <para>An earlier draft fetched this from a JWKS URL the partner hosted. That is the standard
/// arrangement and it buys self-service key rotation — but it also buys an outbound HTTP call to
/// an address someone else chose, and everything that has to surround one: a cache, a negative
/// cache, a response size cap, an SSRF guard, and rate-limited refresh on an unrecognised key id.
/// For a handful of partners rotating keys about as often as they change bank accounts, holding
/// the key directly is the better trade. Rotation becomes: they send the new public key, we
/// redeploy configuration.</para>
///
/// <para>What has <em>not</em> changed is the property that matters — the partner signs, we only
/// ever verify. Nothing secret is exchanged, so nothing secret can leak from either side.</para>
/// </summary>
public sealed class PartnerKeyStore(ILogger<PartnerKeyStore> logger) : IPartnerKeyStore
{
    // Parsing a PEM allocates a key object; the result is immutable and safe to share, and this
    // runs on every token request.
    private readonly ConcurrentDictionary<string, SecurityKey?> _cache = new(StringComparer.Ordinal);

    public SecurityKey? GetKey(Partner partner) =>
        _cache.GetOrAdd(CacheKey(partner), _ => Parse(partner));

    private SecurityKey? Parse(Partner partner)
    {
        if (string.IsNullOrWhiteSpace(partner.PublicKeyPem))
        {
            logger.LogError("Partner {ClientId} has no public key registered.", partner.ClientId);
            return null;
        }

        // ImportFromPem accepts a PRIVATE KEY block just as readily as a public one, so without
        // this a partner who pasted the wrong half would hand us their signing key and everything
        // would appear to work. Refuse loudly: it is their secret, and we should never hold it.
        if (partner.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError(
                "Partner {ClientId} is registered with a PRIVATE key. Only the public half belongs " +
                "here — treat that key as compromised and ask the partner to rotate it.",
                partner.ClientId);
            return null;
        }

        // RSA and ECDSA both arrive as SubjectPublicKeyInfo inside a PEM block, so try each rather
        // than making partners tell us which family they chose.
        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(partner.PublicKeyPem);
            return new RsaSecurityKey(rsa) { KeyId = partner.ClientId };
        }
        catch (ArgumentException)
        {
            // Not RSA. Fall through.
        }
        catch (CryptographicException)
        {
        }

        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(partner.PublicKeyPem);
            return new ECDsaSecurityKey(ecdsa) { KeyId = partner.ClientId };
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            logger.LogError(
                ex, "Partner {ClientId} has a public key that is neither RSA nor ECDSA PEM.", partner.ClientId);
            return null;
        }
    }

    /// <summary>
    /// Keyed on the material as well as the client id, so updating a partner's key in
    /// configuration takes effect on the next request rather than at the next restart.
    /// </summary>
    private static string CacheKey(Partner partner) =>
        $"{partner.ClientId}:{partner.PublicKeyPem.GetHashCode(StringComparison.Ordinal)}";
}
