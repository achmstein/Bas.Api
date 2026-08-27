namespace Bas.Api.Contracts.Partner;

/// <summary>
/// Scope is the real boundary on this service — which components a partner imports is cosmetic,
/// because a token holder can call any route. Every partner-facing endpoint therefore carries an
/// explicit scope requirement, checked server-side.
/// </summary>
public static class BasScopes
{
    /// <summary>Read and write the caller's own activity-statement drafts, and submit them.</summary>
    public const string BasWrite = "bas:write";

    /// <summary>Read the caller's own activity statements. Implied by <see cref="BasWrite"/>.</summary>
    public const string BasRead = "bas:read";

    /// <summary>Read and update the caller's own worker identity (TFN, ABN, name, DOB).</summary>
    public const string ProfileWrite = "profile:write";

    /// <summary>Every scope this service knows about. Anything outside it is <c>invalid_scope</c>.</summary>
    public static readonly IReadOnlyList<string> All = [BasRead, BasWrite, ProfileWrite];
}

/// <summary>Claim names on a Bas.Api access token, beyond the registered JWT claims.</summary>
public static class BasClaims
{
    /// <summary>The partner's <c>client_id</c>. Stamped on every request for audit.</summary>
    public const string PartnerId = "partner_id";

    /// <summary>Space-delimited granted scopes.</summary>
    public const string Scope = "scope";
}
