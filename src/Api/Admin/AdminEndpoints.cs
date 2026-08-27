using System.ComponentModel.DataAnnotations;
using Bas.Api.Statements;
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
            var (partner, error) = await admin.CreatePartnerAsync(request, ct);
            return error is not null ? error.ToResult() : Results.Created($"/admin/v1/partners/{request.ClientId}", partner);
        })
            .WithSummary("Register a partner")
            .WithDescription("The public key is parsed here, so a bad one is refused now rather than at their first token exchange.");

        group.MapPut("/partners/{clientId}/key", async (
            string clientId, RotateKeyRequest request, AdminService admin, CancellationToken ct) =>
        {
            var (partner, error) = await admin.RotateKeyAsync(clientId, request, ct);
            return error is not null ? error.ToResult() : Results.Ok(partner);
        })
            .WithSummary("Replace the partner's signing key")
            .WithDescription(
                "Takes effect on the next request. Assertions signed with the old key are refused " +
                "immediately, so coordinate with the partner unless this is a response to a leak.");

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

/// <summary>Registration details for a new partner.</summary>
public sealed record CreatePartnerRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string ClientId { get; init; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>PEM-encoded RSA or ECDSA <b>public</b> key. A private key is refused.</summary>
    [Required]
    public required string PublicKeyPem { get; init; }

    /// <summary>Space-delimited. Every scope must be one this service knows.</summary>
    [Required]
    public required string AllowedScopes { get; init; }

    public string? WebhookUrl { get; init; }

    public string? WebhookSecret { get; init; }
}

/// <summary>A replacement signing key.</summary>
public sealed record RotateKeyRequest
{
    [Required]
    public required string PublicKeyPem { get; init; }
}

/// <summary>Why a partner was suspended or resumed. Recorded in the audit log.</summary>
public sealed record SuspendRequest
{
    public string? Reason { get; init; }
}

/// <summary>A partner, as an operator sees them. Carries no secret.</summary>
public sealed record AdminPartnerResponse
{
    public required string ClientId { get; init; }

    public required string Name { get; init; }

    /// <summary><c>active</c> or <c>suspended</c>.</summary>
    public required string Status { get; init; }

    public required string AllowedScopes { get; init; }

    /// <summary>Short hash of the registered public key — enough to confirm a rotation took.</summary>
    public required string PublicKeyFingerprint { get; init; }

    public string? WebhookUrl { get; init; }

    /// <summary>Whether a webhook secret is set. Never the secret.</summary>
    public required bool HasWebhookSecret { get; init; }

    /// <summary>How many of their users have been provisioned here.</summary>
    public required int WorkerCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One statement, with the sync ledger's view of it alongside.</summary>
public sealed record AdminLodgementResponse
{
    public required Guid PeriodId { get; init; }

    public required Guid WorkerId { get; init; }

    public string? PartnerId { get; init; }

    /// <summary>The partner's own id for the worker, for support conversations.</summary>
    public string? PartnerSub { get; init; }

    public required int FinancialYear { get; init; }

    public required int Quarter { get; init; }

    public required string Status { get; init; }

    public int? NetAmount { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>The retry ledger's status: Pending, Synced, AwaitingStatement, Failed.</summary>
    public string? SyncStatus { get; init; }

    public int? AttemptCount { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>The last push failure. Usually the thing you actually came here for.</summary>
    public string? LastError { get; init; }
}

/// <summary>One recorded admin change.</summary>
public sealed record AuditEntryResponse
{
    public required DateTimeOffset At { get; init; }

    public required string Action { get; init; }

    /// <summary>The name of the admin key used.</summary>
    public required string Actor { get; init; }

    public string? Subject { get; init; }

    public string? Detail { get; init; }
}
