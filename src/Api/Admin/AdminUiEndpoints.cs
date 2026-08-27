using Bas.Api.Components;
using Microsoft.AspNetCore.Identity;

namespace Bas.Api.Admin;

/// <summary>The browser-facing half of the admin surface.</summary>
public static class AdminUiEndpoints
{
    public static IEndpointRouteBuilder MapAdminUi(this IEndpointRouteBuilder app)
    {
        app.MapRazorComponents<App>();

        // Sign-out is a POST, not a link: a GET that ends a session can be triggered by an image
        // tag on any page the operator happens to visit.
        app.MapPost("/admin/logout", async (SignInManager<AdminUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Redirect("/admin/login");
        }).AllowAnonymous();

        return app;
    }
}
