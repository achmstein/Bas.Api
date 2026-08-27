using Microsoft.Extensions.Caching.Memory;

namespace Bas.Api.Auth;

/// <summary>
/// Remembers the <c>jti</c> of every assertion already spent, so a captured one cannot be
/// presented twice inside its own validity window.
/// </summary>
public interface IAssertionReplayGuard
{
    /// <summary>
    /// Records <paramref name="jti"/> as spent and reports whether it was fresh.
    /// </summary>
    /// <param name="purpose">
    /// Namespace for the id. The client assertion and the subject token are separate documents
    /// that may legitimately share a <c>jti</c>; keeping them apart stops one invalidating the other.
    /// </param>
    /// <param name="expiresAt">
    /// The assertion's own expiry — how long the id must be remembered. Past that the assertion
    /// fails on <c>exp</c> anyway, so there is nothing left to replay.
    /// </param>
    /// <returns><see langword="true"/> if this is the first use.</returns>
    bool TryConsume(string purpose, string jti, DateTimeOffset expiresAt);
}

/// <summary>
/// In-memory replay guard, sized to this service's actual deployment: a single container, the
/// same shape as PracticeManager.Api.
///
/// <para>The trade is explicit. Restarting the process forgets spent ids, and a second instance
/// would not see the first's — both windows are bounded by
/// <see cref="PartnerAuthOptions.MaxAssertionLifetime"/>, which is minutes. Scaling out means
/// replacing this with a shared store, which is why callers depend on the interface.</para>
/// </summary>
public sealed class MemoryAssertionReplayGuard(IMemoryCache cache, TimeProvider timeProvider)
    : IAssertionReplayGuard
{
    /// <summary>Purpose namespace for the RFC 7523 client assertion.</summary>
    public const string ClientAssertionPurpose = "client-assertion";

    /// <summary>Purpose namespace for the subject token.</summary>
    public const string SubjectTokenPurpose = "subject-token";

    public bool TryConsume(string purpose, string jti, DateTimeOffset expiresAt)
    {
        var key = $"jti:{purpose}:{jti}";

        // An id whose window has already closed is not "fresh" — but it will fail lifetime
        // validation regardless, so treat it as spent rather than caching a dead entry.
        var remaining = expiresAt - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return false;

        lock (cache)
        {
            if (cache.TryGetValue(key, out _))
                return false;

            cache.Set(key, true, remaining);
            return true;
        }
    }
}
