using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bas.Api.Tests;

/// <summary>
/// Stands in for a partner's server: holds a key pair, publishes the public half as PEM, and mints
/// the two JWTs a token exchange needs.
///
/// <para>Everything here is what MyGigsters will implement on their side, so it doubles as an
/// executable specification of the integration.</para>
/// </summary>
public sealed class PartnerSigner : IDisposable
{
    private readonly RSA _rsa;
    private readonly SigningCredentials _credentials;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public PartnerSigner(string clientId, string audience)
    {
        ClientId = clientId;
        Audience = audience;

        _rsa = RSA.Create(2048);
        _credentials = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256);
    }

    public string ClientId { get; }

    public string Audience { get; }

    /// <summary>What the partner sends us at registration.</summary>
    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    /// <summary>"I am this partner" — RFC 7523 §3 requires iss and sub to both be the client id.</summary>
    public string CreateClientAssertion(
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        string? jti = null,
        string? issuer = null,
        string? subject = null,
        string? audience = null) =>
        Create(
            issuer ?? ClientId,
            subject ?? issuer ?? ClientId,
            audience ?? Audience,
            now,
            lifetime ?? TimeSpan.FromMinutes(2),
            jti);

    /// <summary>"This is my user &lt;subject&gt;".</summary>
    public string CreateSubjectToken(
        string subject,
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        string? jti = null,
        string? issuer = null,
        string? audience = null) =>
        Create(
            issuer ?? ClientId,
            subject,
            audience ?? Audience,
            now,
            lifetime ?? TimeSpan.FromMinutes(2),
            jti);

    private string Create(
        string issuer, string subject, string audience, DateTimeOffset now, TimeSpan lifetime, string? jti)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(lifetime).UtcDateTime,
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = subject,
                [JwtRegisteredClaimNames.Jti] = jti ?? Guid.NewGuid().ToString("n")
            }
        };

        return _handler.CreateToken(descriptor);
    }

    public void Dispose() => _rsa.Dispose();
}
