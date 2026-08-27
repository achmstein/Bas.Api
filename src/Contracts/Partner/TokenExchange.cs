using System.Text.Json.Serialization;

namespace Bas.Api.Contracts.Partner;

/// <summary>
/// Constants for the RFC 8693 token-exchange grant as Bas.Api implements it.
///
/// The partner's server posts <c>application/x-www-form-urlencoded</c> to
/// <c>POST /api/v1/partner/token</c>. Two JWTs travel in that form and they answer different
/// questions: <c>client_assertion</c> proves <em>which partner</em> is calling (RFC 7523
/// <c>private_key_jwt</c>), <c>subject_token</c> asserts <em>which end user</em> the token is for.
/// Both are signed with the partner's own key — Bas.Api never holds a shared secret.
/// </summary>
public static class TokenExchange
{
    /// <summary>The only <c>grant_type</c> this endpoint accepts.</summary>
    public const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>The only <c>client_assertion_type</c> this endpoint accepts.</summary>
    public const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>The only <c>subject_token_type</c> this endpoint accepts.</summary>
    public const string SubjectTokenType = "urn:ietf:params:oauth:token-type:jwt";

    /// <summary>The <c>issued_token_type</c> returned on success.</summary>
    public const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>Form field names, so callers and server agree without magic strings.</summary>
    public static class Fields
    {
        public const string GrantType = "grant_type";
        public const string ClientAssertion = "client_assertion";
        public const string ClientAssertionType = "client_assertion_type";
        public const string SubjectToken = "subject_token";
        public const string SubjectTokenType = "subject_token_type";
        public const string Scope = "scope";
    }
}

/// <summary>
/// A successful token exchange. Field names follow RFC 6749 §5.1 / RFC 8693 §2.2.1 so a stock
/// OAuth client library can read the response without a custom deserialiser.
/// </summary>
public sealed record TokenExchangeResponse
{
    /// <summary>The bearer token. Short-lived by design — see <see cref="ExpiresIn"/>.</summary>
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    /// <summary>Always <see cref="TokenExchange.AccessTokenType"/>.</summary>
    [JsonPropertyName("issued_token_type")]
    public required string IssuedTokenType { get; init; }

    /// <summary>Always <c>Bearer</c>.</summary>
    [JsonPropertyName("token_type")]
    public required string TokenType { get; init; }

    /// <summary>Lifetime in seconds. Minutes, not hours — this token lands on a partner origin.</summary>
    [JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; init; }

    /// <summary>The scopes actually granted, space-delimited. May be narrower than requested.</summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }
}

/// <summary>An OAuth 2.0 error response (RFC 6749 §5.2). <see cref="TokenErrors"/> lists the codes.</summary>
public sealed record TokenErrorResponse
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

/// <summary>
/// The <c>error</c> codes this endpoint returns. Descriptions are deliberately coarse: a token
/// endpoint that explains precisely why a signature failed is a probing oracle.
/// </summary>
public static class TokenErrors
{
    /// <summary>Malformed request — a field is missing, empty or the wrong type.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>Client authentication failed: unknown partner, suspended, or bad assertion. HTTP 401.</summary>
    public const string InvalidClient = "invalid_client";

    /// <summary>The <c>subject_token</c> is not valid or no longer acceptable.</summary>
    public const string InvalidGrant = "invalid_grant";

    /// <summary><c>grant_type</c> is not token-exchange.</summary>
    public const string UnsupportedGrantType = "unsupported_grant_type";

    /// <summary>A requested scope is unknown or not granted to this partner.</summary>
    public const string InvalidScope = "invalid_scope";

    /// <summary>Something on our side failed. Retryable.</summary>
    public const string ServerError = "server_error";
}
