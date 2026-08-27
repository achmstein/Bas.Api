using System.Text.Json.Serialization;

namespace Bas.Api.Contracts.Partner;

/// <summary>
/// The partner token endpoint: <c>POST /api/v1/partner/token</c>.
///
/// <para>The partner's server sends its API key in the <c>x-partner-key</c> header and a JSON body
/// naming which of its users the token is for. Back comes a short-lived bearer token scoped to that
/// user, which is what their page hands to this API.</para>
///
/// <para>The API key authenticates the <em>platform</em> and must only ever be sent from their
/// server — never a browser or an app bundle. The short token exists precisely so that the thing
/// living on a page expires in minutes.</para>
/// </summary>
public static class PartnerTokens
{
    /// <summary>Header carrying the partner's API key.</summary>
    public const string HeaderName = "x-partner-key";

    /// <summary>Every key starts with this, so one is recognisable in a leak or a log scanner.</summary>
    public const string KeyPrefix = "bas_";
}

/// <summary>The request body.</summary>
public sealed record PartnerTokenRequest
{
    /// <summary>
    /// The partner's own stable id for the user. Never an email or anything the user can change —
    /// it is the permanent key to that person's tax records here.
    /// </summary>
    /// <remarks>
    /// Nullable rather than <c>required</c> so a body that omits it binds and reaches validation,
    /// which answers with a 400 that names the field — a <c>required</c> property fails during
    /// deserialisation instead, and the caller gets a 500 with nothing to act on.
    /// </remarks>
    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    /// <summary>Space-delimited scopes to narrow the token to. Omit for everything granted.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>A minted token.</summary>
public sealed record PartnerTokenResponse
{
    [JsonPropertyName("accessToken")]
    public required string AccessToken { get; init; }

    /// <summary>Always <c>Bearer</c>.</summary>
    [JsonPropertyName("tokenType")]
    public required string TokenType { get; init; }

    /// <summary>Lifetime in seconds. Minutes, not hours — this token lands on a partner's page.</summary>
    [JsonPropertyName("expiresIn")]
    public required int ExpiresIn { get; init; }

    /// <summary>The scopes actually granted, space-delimited.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }
}

/// <summary>A refusal. <see cref="PartnerTokenErrors"/> lists the codes.</summary>
public sealed record PartnerTokenError
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// The <c>error</c> codes the token endpoint returns. Deliberately coarse on the 401: whether a
/// given key exists is not something an unauthenticated caller gets to probe.
/// </summary>
public static class PartnerTokenErrors
{
    /// <summary>The key is missing, unknown, or the partner is suspended. HTTP 401.</summary>
    public const string InvalidKey = "invalid_key";

    /// <summary>The body is malformed — usually a missing <c>subject</c>. HTTP 400.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>A requested scope is unknown or not granted to this partner. HTTP 400.</summary>
    public const string InvalidScope = "invalid_scope";

    /// <summary>Too many token requests from this caller. HTTP 429; honour <c>Retry-After</c>.</summary>
    public const string RateLimited = "rate_limited";
}
