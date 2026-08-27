using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Bas.Api.Infrastructure;

/// <summary>
/// Describes the partner surface in the OpenAPI document, so the MyGigsters team can generate
/// their Next.js and Flutter clients from it rather than hand-writing request shapes.
/// </summary>
internal sealed class PartnerApiDocument : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Bas.Api — partner surface",
            Version = "v1",
            Description =
                "Activity-statement lodgement for partner platforms. Every endpoint except the " +
                "token exchange requires a bearer token obtained from " +
                "POST /api/v1/partner/token, and each declares the scope it needs.\n\n" +
                "The token exchange is server-to-server only: calling it from a browser would put " +
                "the partner's API key on a page."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "A short-lived access token from the partner token exchange. Roughly ten minutes; " +
                "renew by re-calling your own server route, never by storing a longer credential."
        };

        return Task.CompletedTask;
    }
}
