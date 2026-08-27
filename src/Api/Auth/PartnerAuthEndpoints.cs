using System.Diagnostics;
using Bas.Api.Contracts.Partner;
using Bas.Api.Infrastructure;

namespace Bas.Api.Auth;

/// <summary>The token endpoint — the whole of the partner auth surface.</summary>
public static class PartnerAuthEndpoints
{
    /// <summary>Rate-limiter policy protecting the token endpoint.</summary>
    public const string TokenRateLimitPolicy = "partner-token";

    public static IEndpointRouteBuilder MapPartnerAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/partner/token", ExchangeTokenAsync)
            .WithName("PartnerTokenExchange")
            .WithSummary("Exchange a partner assertion for a worker-scoped access token")
            .WithDescription(
                "Server-to-server only. Post application/x-www-form-urlencoded with grant_type, " +
                "client_assertion_type, client_assertion, subject_token_type, subject_token and an " +
                "optional scope. Never call this from a browser — it would put the partner's " +
                "signing key on a page.")
            .WithTags("Partner authentication")
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(TokenRateLimitPolicy)
            .Produces<TokenExchangeResponse>()
            .Produces<TokenErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<TokenErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<TokenErrorResponse>(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> ExchangeTokenAsync(
        HttpContext context,
        PartnerTokenExchangeService exchange,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Error(
                TokenErrors.InvalidRequest,
                "The token endpoint takes application/x-www-form-urlencoded.",
                StatusCodes.Status400BadRequest);
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);

        var request = new TokenExchangeRequest(
            form[TokenExchange.Fields.GrantType],
            form[TokenExchange.Fields.ClientAssertionType],
            form[TokenExchange.Fields.ClientAssertion],
            form[TokenExchange.Fields.SubjectTokenType],
            form[TokenExchange.Fields.SubjectToken],
            form[TokenExchange.Fields.Scope]);

        var outcome = await exchange.ExchangeAsync(request, cancellationToken);

        return outcome switch
        {
            TokenExchangeOutcome.Success success => TokenResult(success.Response),
            TokenExchangeOutcome.Failure failure => Error(failure.Error, failure.Description, failure.StatusCode),
            _ => throw new UnreachableException()
        };
    }

    /// <summary>
    /// RFC 6749 §5.1 requires the token response to be non-cacheable — it carries a credential, and
    /// an intermediary holding on to one would hand it to whoever asks next.
    /// </summary>
    private static IResult TokenResult(TokenExchangeResponse response) =>
        JsonWithHeaders.Create(response, StatusCodes.Status200OK, NoStore);

    private static IResult Error(string error, string description, int statusCode)
    {
        var body = new TokenErrorResponse { Error = error, ErrorDescription = description };

        // RFC 6749 §5.2: a 401 from the token endpoint carries a WWW-Authenticate challenge.
        return statusCode == StatusCodes.Status401Unauthorized
            ? JsonWithHeaders.Create(body, statusCode, NoStore.Concat(
                [("WWW-Authenticate", "Bearer error=\"invalid_client\"")]).ToArray())
            : JsonWithHeaders.Create(body, statusCode, NoStore);
    }

    private static readonly (string Name, string Value)[] NoStore =
    [
        ("Cache-Control", "no-store"),
        ("Pragma", "no-cache")
    ];
}
