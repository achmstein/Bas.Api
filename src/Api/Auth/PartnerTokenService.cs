using Bas.Api.Contracts.Partner;
using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Auth;

/// <summary>What the token endpoint answers: a token, or an error with the status it carries.</summary>
public sealed record PartnerTokenOutcome(
    PartnerTokenResponse? Token, string? Error, string? Message, int StatusCode)
{
    public static PartnerTokenOutcome Ok(PartnerTokenResponse token) =>
        new(token, null, null, StatusCodes.Status200OK);

    /// <summary>
    /// One answer for a missing key, an unknown key and a suspended partner — which of those it
    /// was is not something an unauthenticated caller gets to learn.
    /// </summary>
    public static PartnerTokenOutcome InvalidKey() =>
        new(null, PartnerTokenErrors.InvalidKey, "The partner key was not accepted.", StatusCodes.Status401Unauthorized);

    public static PartnerTokenOutcome Invalid(string message) =>
        new(null, PartnerTokenErrors.InvalidRequest, message, StatusCodes.Status400BadRequest);

    public static PartnerTokenOutcome InvalidScope(string message) =>
        new(null, PartnerTokenErrors.InvalidScope, message, StatusCodes.Status400BadRequest);
}

/// <summary>
/// Turns a partner's API key and one of their user ids into a short-lived, worker-scoped token.
///
/// <para>The key authenticates the platform; the <c>subject</c> names the user, and resolves on
/// <c>(partner, subject)</c> only — never on email — because anything able to call this endpoint
/// must not be able to reach an existing person's records by naming their address.</para>
/// </summary>
public sealed class PartnerTokenService(
    BasDbContext db,
    WorkerProvisioner provisioner,
    AccessTokenIssuer issuer,
    ILogger<PartnerTokenService> logger)
{
    public async Task<PartnerTokenOutcome> MintAsync(
        string? apiKey, PartnerTokenRequest? request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return PartnerTokenOutcome.InvalidKey();

        if (request is null || string.IsNullOrWhiteSpace(request.Subject))
            return PartnerTokenOutcome.Invalid("subject is required: your own stable id for the user.");

        // Looked up by prefix, decided by a constant-time hash comparison. The loop tolerates a
        // prefix collision, which at twelve characters is a curiosity rather than a plan.
        var prefix = PartnerApiKey.PrefixOf(apiKey);
        var candidates = await db.Partners
            .AsNoTracking()
            .Where(p => p.ApiKeyPrefix == prefix)
            .ToListAsync(cancellationToken);

        var partner = candidates.FirstOrDefault(p => PartnerApiKey.Matches(apiKey, p.ApiKeyHash));

        if (partner is null)
        {
            logger.LogWarning("Token request with an unrecognised partner key (prefix {Prefix}).", prefix);
            return PartnerTokenOutcome.InvalidKey();
        }

        if (partner.Status is not PartnerStatus.Active)
        {
            logger.LogWarning("Token request refused: partner {ClientId} is {Status}.", partner.ClientId, partner.Status);
            return PartnerTokenOutcome.InvalidKey();
        }

        var (scopes, scopeError) = ResolveScopes(partner, request.Scope);
        if (scopeError is not null)
            return scopeError;

        var workerId = await provisioner.ResolveOrProvisionAsync(partner, request.Subject, cancellationToken);

        var token = await issuer.IssueAsync(workerId, partner.ClientId, scopes, cancellationToken);

        // Every mint is logged: the data-sharing agreement and the Privacy Act TFN Rule both want
        // an answer to "who was issued access to this person's data, and when".
        logger.LogInformation(
            "Minted access token {Jti} for worker {WorkerId} to partner {ClientId} with scope [{Scope}].",
            token.Jti, workerId, partner.ClientId, string.Join(' ', scopes));

        return PartnerTokenOutcome.Ok(new PartnerTokenResponse
        {
            AccessToken = token.Token,
            TokenType = "Bearer",
            ExpiresIn = (int)token.Lifetime.TotalSeconds,
            Scope = string.Join(' ', scopes)
        });
    }

    /// <summary>
    /// Narrows to what the partner was granted at registration. A request may ask for less than it
    /// holds — good practice — but never for more.
    /// </summary>
    private static (IReadOnlyList<string> Scopes, PartnerTokenOutcome? Error) ResolveScopes(
        Partner partner, string? requested)
    {
        var granted = partner.AllowedScopeList;

        if (granted.Count == 0)
            return ([], PartnerTokenOutcome.InvalidScope("This partner has no scopes granted."));

        if (string.IsNullOrWhiteSpace(requested))
            return (granted, null);

        var asked = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var scope in asked)
        {
            if (!BasScopes.All.Contains(scope, StringComparer.Ordinal))
                return ([], PartnerTokenOutcome.InvalidScope($"Unknown scope '{scope}'."));

            if (!granted.Contains(scope, StringComparer.Ordinal))
                return ([], PartnerTokenOutcome.InvalidScope($"Scope '{scope}' is not granted to this partner."));
        }

        return (asked, null);
    }
}
