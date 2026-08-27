using Bas.Api.Auth;
using Bas.Api.Statements;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Bas.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Bas.Api.Statements;

/// <summary>
/// The worker's own identity — the details Practice Manager needs before it will create a client.
/// </summary>
public static class WorkerEndpoints
{
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workers").WithTags("Worker");

        group.MapGet("/me", GetAsync)
            .WithName("GetCurrentWorker")
            .WithSummary("The worker this access token was minted for")
            .WithDescription(
                "The worker id is minted by this service and is stable for a given (partner, " +
                "partner subject) pair, so the same user gets the same id every quarter. The TFN " +
                "is always masked.")
            .RequireScope(BasScopes.BasRead)
            .Produces<WorkerIdentityResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/me", SaveAsync)
            .WithName("SaveCurrentWorker")
            .WithSummary("Set the worker's identity")
            .WithDescription(
                "Required before an activity statement can be submitted: Practice Manager will not " +
                "create a client without a structurally valid TFN. The TFN is checked against the " +
                "ATO algorithm here so the worker is told while they are still on the form, rather " +
                "than a quarter later when the push fails.")
            .RequireScope(BasScopes.ProfileWrite)
            .Produces<WorkerIdentityResponse>()
            .ProducesValidationProblem()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> GetAsync(
        ICallerContext caller,
        WorkerIdentityService identity,
        CancellationToken cancellationToken)
    {
        var worker = await identity.GetAsync(caller.WorkerId, caller.PartnerId, cancellationToken);

        return worker is null
            ? Results.Problem(title: "Unknown worker", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(worker);
    }

    private static async Task<IResult> SaveAsync(
        WorkerIdentityRequest request,
        ICallerContext caller,
        WorkerIdentityService identity,
        CancellationToken cancellationToken)
    {
        var (saved, error) = await identity.SaveAsync(
            caller.WorkerId, caller.PartnerId, request, cancellationToken);

        return error is not null ? error.ToResult() : Results.Ok(saved);
    }
}
