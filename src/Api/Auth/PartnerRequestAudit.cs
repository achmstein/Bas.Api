using Bas.Api.Contracts.Partner;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Bas.Api.Auth;

/// <summary>
/// Stamps the partner, the worker and the token id onto every authenticated partner request.
///
/// <para>The mint is already logged, which answers "who was granted access to this person's data".
/// This answers the question that follows it — "and what did they then do with it" — which is what
/// a data-sharing agreement and the Privacy Act TFN Rule actually want when something has gone
/// wrong. Without it, a log line from inside a request cannot be traced back to the partner or the
/// token that authorised it.</para>
///
/// <para>A logging scope rather than rows in a table. Every partner request would be a write, and
/// the operational question is always "show me everything about this token", which a log query
/// answers and a table would only duplicate.</para>
/// </summary>
public sealed class PartnerRequestAudit(RequestDelegate next, ILogger<PartnerRequestAudit> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Only for tokens this service minted. An admin cookie or key is a different surface with
        // its own trail, and an anonymous request has nothing worth stamping.
        if (context.User.Identity?.IsAuthenticated is not true
            || context.User.Identity.AuthenticationType != JwtBearerDefaults.AuthenticationScheme)
        {
            await next(context);
            return;
        }

        var partnerId = context.User.FindFirst(BasClaims.PartnerId)?.Value;
        if (partnerId is null)
        {
            await next(context);
            return;
        }

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["partner_id"] = partnerId,
            ["worker_id"] = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value,
            ["jti"] = context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
        });

        await next(context);
    }
}

public static class PartnerRequestAuditExtensions
{
    /// <summary>
    /// Adds the partner audit scope. Must come after <c>UseAuthentication</c> — before it, there is
    /// no principal to read, and the scope would be silently empty on every request.
    /// </summary>
    public static IApplicationBuilder UsePartnerRequestAudit(this IApplicationBuilder app) =>
        app.UseMiddleware<PartnerRequestAudit>();
}
