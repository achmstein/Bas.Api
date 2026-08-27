namespace Bas.Api.Infrastructure;

/// <summary>
/// A refusal the caller can act on, carrying the HTTP status it should produce. The error currency
/// of every service in this API — endpoints translate it with <see cref="BasErrorExtensions.ToResult"/>.
/// </summary>
public sealed record BasError(int StatusCode, string Title, string Detail);

/// <summary>Turns a service-level refusal into the response it describes.</summary>
internal static class BasErrorExtensions
{
    public static IResult ToResult(this BasError error) =>
        Results.Problem(title: error.Title, detail: error.Detail, statusCode: error.StatusCode);
}
