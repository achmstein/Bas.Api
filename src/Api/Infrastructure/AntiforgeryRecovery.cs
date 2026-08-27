using Microsoft.AspNetCore.Antiforgery;

namespace Bas.Api.Infrastructure;

/// <summary>
/// Turns an unusable antiforgery token into a fresh form rather than a bare HTTP 400.
///
/// <para>An antiforgery token stops being valid for entirely ordinary reasons: the page was left
/// open, the browser kept a cookie from before a deploy, or the Data Protection keys changed. None
/// of those are attacks, and none of them are anything the person at the keyboard can act on when
/// the answer is a blank <c>HTTP ERROR 400</c> page — the browser will not even say which request
/// failed.</para>
///
/// <para>So for a browser: drop the stale cookies and redirect back to the same page, which issues
/// a new token. The submission is lost, which is correct — that is exactly what the check is for —
/// but the retry succeeds instead of looping. For anything else, the 400 stands: an API client
/// posting a bad token should be told plainly, not redirected.</para>
/// </summary>
public sealed class AntiforgeryRecovery(RequestDelegate next, ILogger<AntiforgeryRecovery> logger)
{
    /// <summary>Marks the redirect, so the page can explain why the form came back empty.</summary>
    public const string ExpiredQueryKey = "expired";

    private const string CookiePrefix = ".AspNetCore.Antiforgery.";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AntiforgeryValidationException ex)
        {
            // Nothing can be done once the response is on the wire.
            if (context.Response.HasStarted)
                throw;

            if (!WantsHtml(context.Request))
                throw;

            logger.LogInformation(
                "Antiforgery token rejected for {Path}; sending the browser back for a fresh one. {Reason}",
                context.Request.Path, ex.Message);

            // The stale cookie would fail the same way on the retry, so it has to go too.
            foreach (var name in context.Request.Cookies.Keys)
            {
                if (name.StartsWith(CookiePrefix, StringComparison.Ordinal))
                    context.Response.Cookies.Delete(name);
            }

            var query = context.Request.Query.ContainsKey(ExpiredQueryKey)
                ? context.Request.QueryString
                : context.Request.QueryString.Add(ExpiredQueryKey, "1");

            context.Response.Redirect(context.Request.Path + query);
        }
    }

    private static bool WantsHtml(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
}

public static class AntiforgeryRecoveryExtensions
{
    /// <summary>
    /// Adds antiforgery recovery. Must be registered <b>before</b> <c>UseAntiforgery</c>, since it
    /// works by catching what that middleware throws.
    /// </summary>
    public static IApplicationBuilder UseAntiforgeryRecovery(this IApplicationBuilder app) =>
        app.UseMiddleware<AntiforgeryRecovery>();
}
