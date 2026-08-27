namespace Bas.Api.Infrastructure;

/// <summary>
/// Backoff arithmetic shared by the reconciler and the webhook dispatcher, so the two ledgers
/// cannot drift into disagreeing about what a schedule means.
/// </summary>
public static class RetrySchedule
{
    /// <summary>Used when a schedule is configured empty rather than left at its default.</summary>
    public static readonly TimeSpan FallbackDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Delay before the next attempt, given how many attempts have already failed. The first
    /// retry (<paramref name="failedAttempts"/> == 1) waits <c>schedule[0]</c>; the last entry
    /// repeats forever.
    /// </summary>
    public static TimeSpan Backoff(IReadOnlyList<TimeSpan> schedule, int failedAttempts) =>
        schedule.Count == 0
            ? FallbackDelay
            : schedule[Math.Clamp(failedAttempts - 1, 0, schedule.Count - 1)];
}
