using System.Text.Json;

namespace Bas.Api.Infrastructure;

/// <summary>
/// A JSON result that also sets response headers.
///
/// <para>It exists because the auth surface has real header requirements that
/// <see cref="Results.Json{TValue}(TValue, JsonSerializerOptions, string, int?)"/> cannot express:
/// the token response must be <c>no-store</c> (it carries a credential), a 401 owes the caller a
/// <c>WWW-Authenticate</c> challenge, and the JWKS wants a positive cache lifetime.</para>
/// </summary>
public sealed class JsonWithHeaders<T>(T value, int statusCode, (string Name, string Value)[] headers) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        foreach (var (name, headerValue) in headers)
            httpContext.Response.Headers[name] = headerValue;

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        await httpContext.Response.WriteAsJsonAsync(
            value, httpContext.RequestServices.GetService<JsonSerializerOptions>(), httpContext.RequestAborted);
    }
}

/// <summary>Factory for <see cref="JsonWithHeaders{T}"/>, so call sites read as one expression.</summary>
public static class JsonWithHeaders
{
    /// <summary>For any response carrying a credential: nothing on the way may cache it.</summary>
    public static readonly (string Name, string Value)[] NoStore =
    [
        ("Cache-Control", "no-store"),
        ("Pragma", "no-cache")
    ];

    public static IResult Create<T>(T value, int statusCode, (string Name, string Value)[] headers) =>
        new JsonWithHeaders<T>(value, statusCode, headers);
}
