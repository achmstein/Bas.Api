using System.Security.Claims;
using Bas.Api.Contracts.Partner;
using Microsoft.AspNetCore.Authorization;

namespace Bas.Api.Auth;

/// <summary>Requires a single scope on the caller's access token.</summary>
public sealed class ScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

/// <summary>
/// Checks the space-delimited <c>scope</c> claim.
///
/// <para>Scope is the actual boundary on this service. Which components a partner imports is
/// cosmetic — a token holder can call any route it can name — so every partner-facing endpoint
/// carries an explicit requirement and it is enforced here, server-side.</para>
/// </summary>
public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        var granted = context.User.FindFirstValue(BasClaims.Scope);

        if (granted is not null && granted
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(requirement.Scope, StringComparer.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class ScopeAuthorizationExtensions
{
    /// <summary>
    /// Registers one authorization policy per known scope, named after the scope itself. Declared
    /// up front rather than resolved dynamically, so an endpoint asking for a scope that does not
    /// exist fails at startup instead of silently authorising nobody.
    /// </summary>
    public static AuthorizationBuilder AddScopePolicies(this AuthorizationBuilder builder)
    {
        foreach (var scope in BasScopes.All)
            builder.AddPolicy(PolicyName(scope), policy => policy.AddRequirements(new ScopeRequirement(scope)));

        return builder;
    }

    /// <summary>Requires <paramref name="scope"/> on the caller's token for this endpoint.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!BasScopes.All.Contains(scope, StringComparer.Ordinal))
            throw new ArgumentException($"'{scope}' is not a known scope.", nameof(scope));

        builder.RequireAuthorization(PolicyName(scope));
        return builder;
    }

    private static string PolicyName(string scope) => $"scope:{scope}";
}
