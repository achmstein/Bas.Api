using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bas.Api.Admin;

/// <summary>Keys that may reach the admin surface.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// Named keys, for scripts and runbooks. The name is not a secret and exists so the audit log
    /// can say <em>which</em> key made a change — a single anonymous shared key turns every audit
    /// entry into "someone".
    /// </summary>
    public List<AdminKey> Keys { get; set; } = [];

    /// <summary>
    /// Accounts to create on first run. Bootstrap only: an existing account is never updated from
    /// configuration, so changing a password here does nothing.
    /// </summary>
    public List<AdminUserSeed> Users { get; set; } = [];
}

/// <summary>An admin account to create if it does not exist.</summary>
public sealed class AdminUserSeed
{
    public string Email { get; set; } = string.Empty;

    /// <summary>Replaced on first sign-in. From the environment, never committed.</summary>
    public string InitialPassword { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

/// <summary>One admin credential.</summary>
public sealed class AdminKey
{
    /// <summary>Who this key belongs to, e.g. <c>david</c> or <c>ops-runbook</c>. Recorded on every change.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The secret itself. From the environment, never committed.</summary>
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// API-key authentication for the admin surface.
///
/// <para><b>A separate scheme, deliberately.</b> Admin routes must not be reachable with a partner
/// access token — a partner holding <c>bas:write</c> should not be one claim away from suspending a
/// competitor, and no future scope should be able to grant it by accident. Making this a distinct
/// authentication scheme means a bearer JWT cannot satisfy an admin policy at all, rather than
/// merely failing a check someone has to remember to write.</para>
/// </summary>
public sealed class AdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<AdminOptions> adminOptions,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "AdminApiKey";

    public const string HeaderName = "x-admin-key";

    /// <summary>Claim carrying the key's name, for the audit log.</summary>
    public const string ActorClaim = "admin_actor";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = adminOptions.Value.Keys
            .Where(k => !string.IsNullOrWhiteSpace(k.Key))
            .ToList();

        if (configured.Count == 0)
        {
            // Closed by default. An admin surface that is open because nobody configured it is the
            // worst possible failure mode, so no keys means no access rather than no checking.
            Logger.LogWarning(
                "The admin surface was called but no keys are configured; refusing. Set {Section}:Keys.",
                AdminOptions.SectionName);

            return Task.FromResult(AuthenticateResult.Fail("The admin surface is not configured."));
        }

        if (!Request.Headers.TryGetValue(HeaderName, out var presented) || presented.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var supplied = presented.ToString();

        // Every key is compared, and always in constant time, so neither the answer nor how long it
        // took reveals how much of a key was right.
        AdminKey? matched = null;
        foreach (var key in configured)
        {
            if (KeysMatch(supplied, key.Key))
                matched = key;
        }

        if (matched is null)
        {
            Logger.LogWarning(
                "Admin request from {Address} presented an unrecognised key at {Timestamp:o}.",
                Context.Connection.RemoteIpAddress, timeProvider.GetUtcNow());

            return Task.FromResult(AuthenticateResult.Fail("Unrecognised admin key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ActorClaim, matched.Name), new Claim(ClaimTypes.Name, matched.Name)],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    private static bool KeysMatch(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

/// <summary>Who is making an admin change.</summary>
public interface IAdminActor
{
    /// <summary>The name of the key presented. Stamped on every audit entry.</summary>
    string Name { get; }
}

/// <inheritdoc />
public sealed class AdminActor(IHttpContextAccessor accessor) : IAdminActor
{
    public string Name
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated is not true)
                return "unknown";

            // A signed-in person is the better answer whenever there is one: "david@..." beats
            // "the key named ops-runbook" in an audit log every time.
            return user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? user.FindFirst(AdminAuthenticationHandler.ActorClaim)?.Value
                ?? "unknown";
        }
    }
}
