using Bas.Api.Contracts.Partner;
using Bas.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Bas.Api.Auth;

/// <summary>The token endpoint — the whole of the partner auth surface.</summary>
public static class PartnerAuthEndpoints
{
    /// <summary>Rate-limiter policy protecting the token endpoint.</summary>
    public const string TokenRateLimitPolicy = "partner-token";

    public static IEndpointRouteBuilder MapPartnerAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/partner/token", MintAsync)
            .WithName("PartnerToken")
            .WithSummary("Exchange your partner key for a short-lived, user-scoped token")
            .WithDescription(
                "Server-to-server only: send your API key in the x-partner-key header, and the id " +
                "of your user in the body. The key must never reach a browser or an app bundle - " +
                "the short-lived token this returns is what your page uses.")
            .WithTags("Partner authentication")
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(TokenRateLimitPolicy)
            .Produces<PartnerTokenResponse>()
            .Produces<PartnerTokenError>(StatusCodes.Status400BadRequest)
            .Produces<PartnerTokenError>(StatusCodes.Status401Unauthorized)
            .Produces<PartnerTokenError>(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> MintAsync(
        [FromHeader(Name = PartnerTokens.HeaderName)] string? apiKey,
        PartnerTokenRequest? request,
        PartnerTokenService tokens,
        CancellationToken cancellationToken)
    {
        var outcome = await tokens.MintAsync(apiKey, request, cancellationToken);

        // A token is a credential, so nothing on the way may cache this response.
        if (outcome.Token is not null)
            return JsonWithHeaders.Create(outcome.Token, StatusCodes.Status200OK, NoStore);

        return JsonWithHeaders.Create(
            new PartnerTokenError { Error = outcome.Error!, Message = outcome.Message },
            outcome.StatusCode,
            NoStore);
    }

    private static readonly (string Name, string Value)[] NoStore =
    [
        ("Cache-Control", "no-store"),
        ("Pragma", "no-cache")
    ];
}
