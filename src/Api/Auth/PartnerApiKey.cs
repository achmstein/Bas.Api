using System.Security.Cryptography;
using System.Text;
using Bas.Api.Contracts.Partner;
using Microsoft.IdentityModel.Tokens;

namespace Bas.Api.Auth;

/// <summary>
/// Creation and verification of partner API keys.
///
/// <para>The key itself is never stored. The database holds its SHA-256 and a short prefix for
/// lookup, so a dump of this database yields nothing that can authenticate — the same bargain a
/// password hash makes. The full key exists exactly twice: in the response that issued it, and in
/// the partner's secret manager.</para>
/// </summary>
public static class PartnerApiKey
{
    /// <summary>How much of the key is kept readable, for lookup and for telling two keys apart.</summary>
    public const int PrefixLength = 12;

    /// <summary>A freshly minted key, with the parts of it the database keeps.</summary>
    public sealed record Issued(string Key, string Prefix, string Hash);

    /// <summary>Mints a key: <c>bas_</c> + 256 bits, base64url. 47 characters.</summary>
    public static Issued Generate()
    {
        var key = PartnerTokens.KeyPrefix + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

        return new Issued(key, PrefixOf(key), HashOf(key));
    }

    /// <summary>The stored lookup prefix of a presented key.</summary>
    public static string PrefixOf(string key) =>
        key.Length <= PrefixLength ? key : key[..PrefixLength];

    /// <summary>Lowercase hex SHA-256 of the whole key.</summary>
    public static string HashOf(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>
    /// Whether a presented key matches a stored hash. Constant-time over the digests, so neither
    /// the answer nor how long it took says how close a guess was.
    /// </summary>
    public static bool Matches(string presented, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            Convert.FromHexString(storedHash));
    }
}
