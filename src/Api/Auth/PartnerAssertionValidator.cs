using Bas.Api.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bas.Api.Auth;

/// <summary>A client assertion that proved which partner is calling (RFC 7523 §3).</summary>
public sealed record ValidatedClientAssertion(string ClientId, string Jti);

/// <summary>A subject token that asserted which end user the exchange is for.</summary>
/// <param name="Subject">
/// The partner's own stable id for the user. Half of the only identity key this service accepts —
/// see <see cref="PartnerUserLink"/>.
/// </param>
public sealed record ValidatedSubjectToken(string Subject, string Jti);

/// <summary>Why an assertion was refused. Deliberately coarse — see <see cref="PartnerAssertionValidator"/>.</summary>
public sealed record AssertionFailure(string Reason);

/// <summary>
/// Validates the two partner-signed JWTs that make up a token exchange.
///
/// <para>Failure reasons are logged in full but returned to the caller only as a category. A token
/// endpoint that reports precisely which check failed is an oracle: it tells someone probing it
/// whether a partner exists, whether a key matched, and whether an id had been seen before.</para>
/// </summary>
public sealed class PartnerAssertionValidator(
    IPartnerKeyStore keyStore,
    IAssertionReplayGuard replayGuard,
    IOptions<PartnerAuthOptions> options,
    TimeProvider timeProvider,
    ILogger<PartnerAssertionValidator> logger)
{
    private readonly PartnerAuthOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>
    /// Reads the <c>iss</c> of a client assertion <b>without validating it</b>, so the partner —
    /// and therefore the key to check the signature against — can be looked up.
    /// </summary>
    /// <remarks>
    /// Nothing read here is trusted. It selects which key to verify with; the signature check that
    /// follows is what makes the claim mean anything.
    /// </remarks>
    public bool TryReadClientId(string clientAssertion, out string clientId)
    {
        clientId = string.Empty;

        try
        {
            var token = new JsonWebToken(clientAssertion);
            if (string.IsNullOrEmpty(token.Issuer))
                return false;

            clientId = token.Issuer;
            return true;
        }
        catch (ArgumentException)
        {
            // Not a well-formed JWT at all.
            return false;
        }
    }

    /// <summary>Validates the client assertion against <paramref name="partner"/>'s registered key.</summary>
    public async Task<(ValidatedClientAssertion? Assertion, AssertionFailure? Failure)> ValidateClientAssertionAsync(
        Partner partner, string clientAssertion, CancellationToken cancellationToken)
    {
        var key = keyStore.GetKey(partner);
        if (key is null)
            return (null, Fail(partner, "no usable public key is registered for this partner"));

        var result = await _handler.ValidateTokenAsync(clientAssertion, BuildParameters(partner, key));

        if (!result.IsValid)
            return (null, Fail(partner, $"client assertion rejected: {result.Exception?.Message}"));

        var token = (JsonWebToken)result.SecurityToken;

        // RFC 7523 §3: for client authentication the assertion is issued by the client about
        // itself, so iss and sub must both be the client_id. Without this a partner could relay an
        // assertion minted for some other purpose.
        if (!string.Equals(token.Subject, partner.ClientId, StringComparison.Ordinal))
            return (null, Fail(partner, "client assertion 'sub' does not equal the client_id"));

        var (jti, lifetimeFailure) = ValidateJtiAndLifetime(
            partner, token, MemoryAssertionReplayGuard.ClientAssertionPurpose);

        return lifetimeFailure is not null
            ? (null, lifetimeFailure)
            : (new ValidatedClientAssertion(partner.ClientId, jti!), null);
    }

    /// <summary>
    /// Validates the subject token. Signed by the same partner and checked against the same keys —
    /// a partner may only assert its own users.
    /// </summary>
    public async Task<(ValidatedSubjectToken? Subject, AssertionFailure? Failure)> ValidateSubjectTokenAsync(
        Partner partner, string subjectToken, CancellationToken cancellationToken)
    {
        var key = keyStore.GetKey(partner);
        if (key is null)
            return (null, Fail(partner, "no usable public key is registered for this partner"));

        var result = await _handler.ValidateTokenAsync(subjectToken, BuildParameters(partner, key));

        if (!result.IsValid)
            return (null, Fail(partner, $"subject token rejected: {result.Exception?.Message}"));

        var token = (JsonWebToken)result.SecurityToken;

        // The end user's identity, and the only thing this service will resolve them by.
        if (string.IsNullOrWhiteSpace(token.Subject))
            return (null, Fail(partner, "subject token carries no 'sub'"));

        var (jti, lifetimeFailure) = ValidateJtiAndLifetime(
            partner, token, MemoryAssertionReplayGuard.SubjectTokenPurpose);

        return lifetimeFailure is not null
            ? (null, lifetimeFailure)
            : (new ValidatedSubjectToken(token.Subject, jti!), null);
    }

    private TokenValidationParameters BuildParameters(Partner partner, SecurityKey key) =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuer = partner.ClientId,

            // Accept either the service identity or the token endpoint itself, the two values
            // partner libraries commonly default to. Both are ours; neither is a wildcard.
            ValidateAudience = true,
            ValidAudiences = [_options.Issuer, _options.Audience, TokenEndpointAudience],

            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = _options.ClockSkew,

            // IdentityModel would otherwise compare against DateTime.UtcNow directly. Routing the
            // comparison through the injected TimeProvider makes this service's clock the single
            // authority on what "now" means, which is what lets expiry be tested without waiting.
            LifetimeValidator = ValidateLifetime,

            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            IssuerSigningKey = key,

            // The allow-list is the defence against 'alg: none' and against an RSA public key
            // being replayed as an HMAC secret. Never let the token's own header choose.
            ValidAlgorithms = _options.AcceptedAssertionAlgorithms,

            // Replay is handled below, per purpose, against our own store.
            ValidateTokenReplay = false
        };

    private bool ValidateLifetime(
        DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (notBefore.HasValue && now + _options.ClockSkew < notBefore.Value)
            return false;

        return !expires.HasValue || now - _options.ClockSkew <= expires.Value;
    }

    /// <summary>The token endpoint path partners may name as the assertion audience.</summary>
    private string TokenEndpointAudience => $"{_options.Issuer.TrimEnd('/')}/api/v1/partner/token";

    private (string? Jti, AssertionFailure? Failure) ValidateJtiAndLifetime(
        Partner partner, JsonWebToken token, string purpose)
    {
        if (string.IsNullOrWhiteSpace(token.Id))
            return (null, Fail(partner, $"{purpose} carries no 'jti'"));

        // A partner minting long-lived assertions quietly converts a single captured assertion
        // into a long impersonation window, so the ceiling is ours to enforce, not theirs.
        if (token.ValidTo == DateTime.MinValue || token.IssuedAt == DateTime.MinValue)
            return (null, Fail(partner, $"{purpose} must carry both 'iat' and 'exp'"));

        var lifetime = token.ValidTo - token.IssuedAt;
        if (lifetime > _options.MaxAssertionLifetime + _options.ClockSkew)
            return (null, Fail(partner, $"{purpose} lifetime {lifetime} exceeds the permitted maximum"));

        if (!replayGuard.TryConsume(purpose, token.Id, new DateTimeOffset(token.ValidTo, TimeSpan.Zero)))
            return (null, Fail(partner, $"{purpose} 'jti' has already been used"));

        return (token.Id, null);
    }

    private AssertionFailure Fail(Partner partner, string reason)
    {
        // Full detail to the log, a category to the caller.
        logger.LogWarning(
            "Token exchange for partner {ClientId} refused at {Timestamp:o}: {Reason}",
            partner.ClientId, timeProvider.GetUtcNow(), reason);

        return new AssertionFailure(reason);
    }
}
