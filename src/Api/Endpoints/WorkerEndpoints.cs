using Bas.Api.Auth;
using Bas.Api.Contracts.Partner;

namespace Bas.Api.Endpoints;

/// <summary>
/// The smallest possible authenticated endpoint: it tells a caller who their token says they are.
///
/// <para>It earns its place twice over. Partners integrating for the first time need one route
/// that proves the token exchange worked before any BAS data exists to fetch, and the test suite
/// needs a real endpoint behind a real scope policy to prove the bearer pipeline actually
/// enforces something. The BAS resources themselves arrive in phase 3b.</para>
/// </summary>
public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/workers/me", (ICallerContext caller) => Results.Ok(new WorkerIdentityResponse
            {
                WorkerId = caller.WorkerId,
                PartnerId = caller.PartnerId
            }))
            .WithName("GetCurrentWorker")
            .WithSummary("The worker this access token was minted for")
            .WithDescription(
                "Confirms a token exchange worked end to end. The worker id is minted by this " +
                "service and is stable for a given (partner, partner subject) pair.")
            .WithTags("Workers")
            .RequireScope(BasScopes.BasRead)
            .Produces<WorkerIdentityResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}

/// <summary>Identity of the caller behind an access token.</summary>
public sealed record WorkerIdentityResponse
{
    /// <summary>This service's own id for the worker — the token's <c>sub</c>.</summary>
    public required Guid WorkerId { get; init; }

    /// <summary>The partner that vouched for them.</summary>
    public required string PartnerId { get; init; }
}
