using Bas.Api.Contracts.Partner;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bas.Api.Auth;

/// <summary>A minted access token and how long the caller may use it.</summary>
public sealed record IssuedAccessToken(string Token, TimeSpan Lifetime, string Jti);

/// <summary>
/// Mints the bearer tokens partners hand to a browser or a Flutter app.
///
/// <para>Signed asymmetrically so the private half never leaves this service, and short-lived
/// because the token's resting place is a page on someone else's origin. There is deliberately no
/// refresh token: renewal is the partner's component
/// re-calling the partner's own token route, which their session already guards — so a worker
/// logging out of MyGigsters revokes our access for free.</para>
/// </summary>
public sealed class AccessTokenIssuer(
    ISigningKeyStore keyStore,
    IOptions<PartnerAuthOptions> options,
    TimeProvider timeProvider)
{
    private readonly PartnerAuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public async Task<IssuedAccessToken> IssueAsync(
        Guid workerId,
        string partnerClientId,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        var signingKey = await keyStore.GetActiveAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var expires = now + _options.AccessTokenLifetime;
        var jti = Guid.CreateVersion7().ToString("n");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = signingKey.Credentials,
            Claims = new Dictionary<string, object>
            {
                // The subject is a Worker.Id this service minted. That is the point of running
                // this outside NightTax: there is no pre-existing consumer account for a partner
                // assertion to land on.
                [JwtRegisteredClaimNames.Sub] = workerId.ToString(),
                [JwtRegisteredClaimNames.Jti] = jti,
                [BasClaims.PartnerId] = partnerClientId,
                [BasClaims.Scope] = string.Join(' ', scopes)
            }
        };

        return new IssuedAccessToken(
            _handler.CreateToken(descriptor), _options.AccessTokenLifetime, jti);
    }
}
