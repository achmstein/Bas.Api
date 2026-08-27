using Bas.Api.Auth;
using Bas.Api.Statements;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Microsoft.AspNetCore.Mvc;

namespace Bas.Api.Statements;

/// <summary>
/// The activity-statement surface: list, read, save, submit, check.
///
/// <para>Every route is scoped to the caller's own worker, taken from the token's subject. There is
/// no route here that names a worker, so there is nothing to tamper with.</para>
/// </summary>
public static class BasEndpoints
{
    public static IEndpointRouteBuilder MapBasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/bas").WithTags("Activity statements");

        group.MapGet("/", ListAsync)
            .WithName("ListBasPeriods")
            .WithSummary("The worker's activity statements, newest first")
            .WithDescription(
                "Includes quarters the worker has never opened, as empty drafts, so a first visit " +
                "shows the quarter they are meant to be lodging rather than an empty list. Those " +
                "carry an all-zero id until the first save creates them.")
            .RequireScope(BasScopes.BasRead)
            .Produces<IReadOnlyList<BasPeriodSummary>>();

        group.MapGet("/{financialYear:int}/{quarter:int}", GetAsync)
            .WithName("GetBasPeriod")
            .WithSummary("One activity statement")
            .WithDescription(
                "An untouched quarter comes back as an empty draft rather than a 404 - the worker " +
                "is entitled to that quarter whether or not they have opened it. Financial years " +
                "are named for the year they end, so FY2027 Q1 is Jul-Sep 2026.")
            .RequireScope(BasScopes.BasRead)
            .Produces<BasPeriodResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);

        group.MapPut("/{financialYear:int}/{quarter:int}", SaveAsync)
            .WithName("SaveBasPeriod")
            .WithSummary("Save the statement's figures")
            .WithDescription(
                "A FULL REPLACEMENT. Send every label the worker has a value for on each save, not " +
                "only the ones that changed: an absent label means the statement has no such label, " +
                "which is different from zero. A worker with no PAYG instalment obligation has no T " +
                "section at all, and writing zeros into one would be wrong.")
            .RequireScope(BasScopes.BasWrite)
            .Produces<BasPeriodResponse>()
            .ProducesValidationProblem()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapPost("/{financialYear:int}/{quarter:int}/submit", SubmitAsync)
            .WithName("SubmitBasPeriod")
            .WithSummary("Queue the statement for the practice")
            .WithDescription(
                "Returns 202 Accepted. Never lodges inline: Practice Manager is one browser session " +
                "behind a queue of one, and every worker lodges inside the same 72 hours each " +
                "quarter. Poll the status route, or wait for the webhook. Submitting twice returns " +
                "the original acknowledgement rather than an error.")
            .RequireScope(BasScopes.BasWrite)
            .Produces<SubmitBasResponse>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status409Conflict);

        group.MapGet("/{financialYear:int}/{quarter:int}/status", GetStatusAsync)
            .WithName("GetBasPeriodStatus")
            .WithSummary("Status and the net amount Practice Manager computed")
            .WithDescription(
                "netAmount is label 9 as Practice Manager calculated it, not as we did - it is null " +
                "until the statement has been pushed and read back. Status runs draft, submitted, " +
                "pushed, in_review, lodged; failed carries a reason.")
            .RequireScope(BasScopes.BasRead)
            .Produces<BasStatusResponse>()
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> ListAsync(
        ICallerContext caller, BasPeriodService periods, CancellationToken cancellationToken) =>
        Results.Ok(await periods.ListAsync(caller.WorkerId, cancellationToken));

    private static async Task<IResult> GetAsync(
        int financialYear,
        int quarter,
        ICallerContext caller,
        BasPeriodService periods,
        CancellationToken cancellationToken)
    {
        var (period, error) = await periods.GetAsync(
            caller.WorkerId, financialYear, quarter, cancellationToken);

        return error is not null ? error.ToResult() : Results.Ok(period);
    }

    private static async Task<IResult> SaveAsync(
        int financialYear,
        int quarter,
        SaveBasRequest request,
        ICallerContext caller,
        BasPeriodService periods,
        CancellationToken cancellationToken)
    {
        var (period, error) = await periods.SaveAsync(
            caller.WorkerId, financialYear, quarter, request, cancellationToken);

        return error is not null ? error.ToResult() : Results.Ok(period);
    }

    private static async Task<IResult> SubmitAsync(
        int financialYear,
        int quarter,
        ICallerContext caller,
        BasPeriodService periods,
        CancellationToken cancellationToken)
    {
        var (response, error) = await periods.SubmitAsync(
            caller.WorkerId, financialYear, quarter, cancellationToken);

        if (error is not null)
            return error.ToResult();

        // 202, not 200: the practice has not seen it yet. Saying "done" here would be a lie the
        // partner would have to un-tell their user.
        return Results.Accepted(
            $"/api/v1/bas/{financialYear}/{quarter}/status", response);
    }

    private static async Task<IResult> GetStatusAsync(
        int financialYear,
        int quarter,
        ICallerContext caller,
        BasPeriodService periods,
        CancellationToken cancellationToken)
    {
        var (status, error) = await periods.GetStatusAsync(
            caller.WorkerId, financialYear, quarter, cancellationToken);

        return error is not null ? error.ToResult() : Results.Ok(status);
    }
}
