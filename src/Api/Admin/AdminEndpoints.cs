using Bas.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bas.Api.Admin;

/// <summary>
/// The operations a person runs against this service.
///
/// <para>Kept out of the partner OpenAPI document: a partner generating a client from
/// <c>/openapi/v1.json</c> should not end up with a <c>suspendPartner()</c> in their SDK.</para>
/// </summary>
public static class AdminEndpoints
{
    /// <summary>Policy for the REST surface: a signed-in person, or a named key for scripts.</summary>
    public const string PolicyName = "admin";

    /// <summary>
    /// Policy for the browser console: a signed-in person only.
    ///
    /// <para>Separate from <see cref="PolicyName"/> because failure has to mean different things.
    /// When a policy names several schemes, every one of them is challenged, and the API key
    /// handler's bare 401 lands on top of the cookie handler's redirect — so an operator whose
    /// cookie expired mid-task got a blank 401 instead of the sign-in page. A page also has no
    /// business being reachable with a header, and a script has no business being handed an HTML
    /// login form.</para>
    /// </summary>
    public const string UiPolicyName = "admin-ui";

    /// <summary>OpenAPI document name, served separately at <c>/openapi/admin.json</c>.</summary>
    public const string DocumentName = "admin";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/v1")
            .WithGroupName(DocumentName)
            .WithTags("Admin")
            .RequireAuthorization(PolicyName);

        // ───── partners ─────

        group.MapGet("/partners", (AdminService admin, CancellationToken ct) => admin.ListPartnersAsync(ct))
            .WithSummary("Every registered partner");

        group.MapGet("/partners/{clientId}", async (
                string clientId, AdminService admin, CancellationToken ct) =>
            await admin.GetPartnerAsync(clientId, ct) is { } partner
                ? Results.Ok(partner)
                : Results.Problem(title: "Unknown partner", statusCode: StatusCodes.Status404NotFound))
            .WithSummary("One partner");

        group.MapPost("/partners", async (
            CreatePartnerRequest request, AdminService admin, CancellationToken ct) =>
        {
            var (created, error) = await admin.CreatePartnerAsync(request, ct);
            if (error is not null)
                return error.ToResult();

            // no-store: this body may carry a freshly issued key, and it must not sit in a proxy
            // or a browser cache on its way to being copied once.
            return JsonWithHeaders.Create(created, StatusCodes.Status201Created, JsonWithHeaders.NoStore);
        })
            .WithSummary("Register a partner and issue their API key")
            .WithDescription(
                "The key comes back in this response and is never stored - only its hash is - so it " +
                "cannot be retrieved again. Save it here or rotate for a new one.");

        group.MapPut("/partners/{clientId}/key", async (
            string clientId, AdminService admin, CancellationToken ct) =>
        {
            var (result, error) = await admin.RotateKeyAsync(clientId, ct);

            return error is not null
                ? error.ToResult()
                : JsonWithHeaders.Create(result, StatusCodes.Status200OK, JsonWithHeaders.NoStore);
        })
            .WithSummary("Issue a new API key, refusing the old one immediately")
            .WithDescription(
                "The response is the only place the new key ever appears. Coordinate with the " +
                "partner unless this is a response to a leak - their calls fail until they switch.");

        group.MapPost("/partners/{clientId}/suspend", async (
            string clientId, [FromBody] SuspendRequest? request, AdminService admin, CancellationToken ct) =>
        {
            var (partner, error) = await admin.SetStatusAsync(clientId, active: false, request?.Reason, ct);
            return error is not null ? error.ToResult() : Results.Ok(partner);
        })
            .WithSummary("Suspend a partner")
            .WithDescription(
                "The kill switch. Token exchange starts failing immediately; tokens already minted " +
                "expire on their own within minutes, which is what the short lifetime is for.");

        group.MapPost("/partners/{clientId}/resume", async (
            string clientId, [FromBody] SuspendRequest? request, AdminService admin, CancellationToken ct) =>
        {
            var (partner, error) = await admin.SetStatusAsync(clientId, active: true, request?.Reason, ct);
            return error is not null ? error.ToResult() : Results.Ok(partner);
        })
            .WithSummary("Resume a suspended partner");

        group.MapPut("/partners/{clientId}/webhook", async (
            string clientId, SetWebhookRequest request, AdminService admin, CancellationToken ct) =>
        {
            var (result, error) = await admin.SetWebhookAsync(clientId, request.Url, request.NewSecret, ct);

            // The body may carry a freshly issued secret.
            return error is not null
                ? error.ToResult()
                : JsonWithHeaders.Create(result, StatusCodes.Status200OK, JsonWithHeaders.NoStore);
        })
            .WithSummary("Set or clear where a partner's status changes are delivered")
            .WithDescription(
                "An empty url removes the webhook and its secret; the partner then polls, which " +
                "always works. A new secret is issued only when asked for, or when there is none " +
                "yet - changing the address alone must not silently break their signature checks.");

        // ───── lodgements ─────

        group.MapGet("/lodgements", (
                [FromQuery] string? status, [FromQuery] int? limit, AdminService admin, CancellationToken ct) =>
            admin.ListLodgementsAsync(status, limit ?? 50, ct))
            .WithSummary("Activity statements in flight, newest first")
            .WithDescription(
                "Filter with ?status=submitted|awaiting_statement|pushed|failed|lodged. Includes the " +
                "sync ledger's attempt count and last error, which is usually the thing you actually " +
                "want. TFNs are never included.");

        group.MapPost("/lodgements/{periodId:guid}/retry", async (
            Guid periodId, AdminService admin, CancellationToken ct) =>
        {
            var (lodgement, error) = await admin.RetryLodgementAsync(periodId, ct);
            return error is not null ? error.ToResult() : Results.Ok(lodgement);
        })
            .WithSummary("Put a statement back in front of the reconciler now")
            .WithDescription("Clears the backoff and the attempt count. Use after fixing whatever was wrong.");

        // ───── audit ─────

        group.MapGet("/audit", (
                [FromQuery] string? subject, [FromQuery] int? limit, AdminService admin, CancellationToken ct) =>
            admin.ListAuditAsync(subject, limit ?? 100, ct))
            .WithSummary("Admin changes, newest first")
            .WithDescription("Reads are not recorded - only changes. Narrow with ?subject=<client id or period id>.");

        return app;
    }

    /// <summary>
    /// Two ways in, and a partner bearer token is neither: a signed-in person (cookie) for the
    /// console, and a named key for scripts and runbooks. Naming the schemes explicitly means a
    /// partner token cannot satisfy this policy even if a future scope were added by mistake — it
    /// is not a check someone has to remember to write.
    /// </summary>
    public static AuthorizationBuilder AddAdminPolicy(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(PolicyName, policy => policy
                .AddAuthenticationSchemes(
                    Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme,
                    AdminAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser())
            .AddPolicy(UiPolicyName, policy => policy
                .AddAuthenticationSchemes(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser());
}
