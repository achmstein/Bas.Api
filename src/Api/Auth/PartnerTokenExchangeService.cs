using Bas.Api.Contracts.Partner;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bas.Api.Auth;

/// <summary>The form a partner posts to the token endpoint.</summary>
public sealed record TokenExchangeRequest(
    string? GrantType,
    string? ClientAssertionType,
    string? ClientAssertion,
    string? SubjectTokenType,
    string? SubjectToken,
    string? Scope);

/// <summary>The outcome of an exchange: a token, or an OAuth error with the status to return.</summary>
public abstract record TokenExchangeOutcome
{
    private TokenExchangeOutcome() { }

    public sealed record Success(TokenExchangeResponse Response, Guid WorkerId) : TokenExchangeOutcome;

    public sealed record Failure(string Error, string Description, int StatusCode) : TokenExchangeOutcome;

    public static TokenExchangeOutcome Invalid(string description) =>
        new Failure(TokenErrors.InvalidRequest, description, StatusCodes.Status400BadRequest);

    /// <summary>RFC 6749 §5.2 puts client-authentication failures at 401.</summary>
    public static TokenExchangeOutcome InvalidClient(string description) =>
        new Failure(TokenErrors.InvalidClient, description, StatusCodes.Status401Unauthorized);

    public static TokenExchangeOutcome InvalidGrant(string description) =>
        new Failure(TokenErrors.InvalidGrant, description, StatusCodes.Status400BadRequest);

    public static TokenExchangeOutcome InvalidScope(string description) =>
        new Failure(TokenErrors.InvalidScope, description, StatusCodes.Status400BadRequest);

    public static TokenExchangeOutcome UnsupportedGrant(string description) =>
        new Failure(TokenErrors.UnsupportedGrantType, description, StatusCodes.Status400BadRequest);
}

/// <summary>
/// The RFC 8693 token exchange: partner proves who it is, asserts which of its users this is, and
/// receives a short-lived bearer token scoped to that user.
/// </summary>
public sealed class PartnerTokenExchangeService(
    BasDbContext db,
    PartnerAssertionValidator validator,
    WorkerProvisioner provisioner,
    AccessTokenIssuer issuer,
    IOptions<PartnerAuthOptions> options,
    ILogger<PartnerTokenExchangeService> logger)
{
    private readonly PartnerAuthOptions _options = options.Value;

    public async Task<TokenExchangeOutcome> ExchangeAsync(
        TokenExchangeRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.GrantType, TokenExchange.GrantType, StringComparison.Ordinal))
            return TokenExchangeOutcome.UnsupportedGrant("grant_type must be the token-exchange grant.");

        if (!string.Equals(request.ClientAssertionType, TokenExchange.ClientAssertionType, StringComparison.Ordinal))
            return TokenExchangeOutcome.Invalid("client_assertion_type must be the jwt-bearer assertion type.");

        if (!string.Equals(request.SubjectTokenType, TokenExchange.SubjectTokenType, StringComparison.Ordinal))
            return TokenExchangeOutcome.Invalid("subject_token_type must be the JWT token type.");

        if (string.IsNullOrWhiteSpace(request.ClientAssertion))
            return TokenExchangeOutcome.Invalid("client_assertion is required.");

        if (string.IsNullOrWhiteSpace(request.SubjectToken))
            return TokenExchangeOutcome.Invalid("subject_token is required.");

        // Read the claimed issuer to find the partner, then verify the signature against the key
        // that partner registered. Nothing read before verification is trusted.
        if (!validator.TryReadClientId(request.ClientAssertion, out var clientId))
            return TokenExchangeOutcome.InvalidClient("Client authentication failed.");

        var partner = await db.Partners
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        if (partner is null)
        {
            // Same response as a bad signature: whether a given client_id is registered is not
            // something an unauthenticated caller gets to enumerate.
            logger.LogWarning("Token exchange for unknown client_id {ClientId}.", clientId);
            return TokenExchangeOutcome.InvalidClient("Client authentication failed.");
        }

        if (partner.Status is not PartnerStatus.Active)
        {
            logger.LogWarning("Token exchange refused: partner {ClientId} is {Status}.", clientId, partner.Status);
            return TokenExchangeOutcome.InvalidClient("Client authentication failed.");
        }

        var (assertion, assertionFailure) = await validator.ValidateClientAssertionAsync(
            partner, request.ClientAssertion, cancellationToken);

        if (assertionFailure is not null || assertion is null)
            return TokenExchangeOutcome.InvalidClient("Client authentication failed.");

        var (subject, subjectFailure) = await validator.ValidateSubjectTokenAsync(
            partner, request.SubjectToken, cancellationToken);

        if (subjectFailure is not null || subject is null)
            return TokenExchangeOutcome.InvalidGrant("The subject token is not valid.");

        var (scopes, scopeFailure) = ResolveScopes(partner, request.Scope);
        if (scopeFailure is not null)
            return scopeFailure;

        var workerId = await provisioner.ResolveOrProvisionAsync(partner, subject.Subject, cancellationToken);

        var token = await issuer.IssueAsync(workerId, partner.ClientId, scopes, cancellationToken);

        // Every mint is logged: the data-sharing agreement and the Privacy Act TFN Rule both want
        // an answer to "who was issued access to this person's data, and when".
        logger.LogInformation(
            "Minted access token {Jti} for worker {WorkerId} to partner {ClientId} with scope [{Scope}]; " +
            "assertion jti {AssertionJti}.",
            token.Jti, workerId, partner.ClientId, string.Join(' ', scopes), assertion.Jti);

        return new TokenExchangeOutcome.Success(
            new TokenExchangeResponse
            {
                AccessToken = token.Token,
                IssuedTokenType = TokenExchange.AccessTokenType,
                TokenType = "Bearer",
                ExpiresIn = (int)token.Lifetime.TotalSeconds,
                Scope = string.Join(' ', scopes)
            },
            workerId);
    }

    /// <summary>
    /// Narrows the request to what the partner was granted at registration. A request may ask for
    /// less than it holds — that is good practice — but never for more.
    /// </summary>
    private (IReadOnlyList<string> Scopes, TokenExchangeOutcome? Failure) ResolveScopes(
        Partner partner, string? requested)
    {
        var granted = partner.AllowedScopeList;

        if (granted.Count == 0)
            return ([], TokenExchangeOutcome.InvalidScope("This client has no scopes granted."));

        if (string.IsNullOrWhiteSpace(requested))
            return (granted, null);

        var asked = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var scope in asked)
        {
            if (!BasScopes.All.Contains(scope, StringComparer.Ordinal))
                return ([], TokenExchangeOutcome.InvalidScope($"Unknown scope '{scope}'."));

            if (!granted.Contains(scope, StringComparer.Ordinal))
                return ([], TokenExchangeOutcome.InvalidScope($"Scope '{scope}' is not granted to this client."));
        }

        return (asked, null);
    }

    /// <summary>Exposed for the endpoint's <c>expires_in</c> documentation.</summary>
    public TimeSpan AccessTokenLifetime => _options.AccessTokenLifetime;
}
