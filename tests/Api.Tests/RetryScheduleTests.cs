using Bas.Api.Infrastructure;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// The one backoff computation both ledgers share. The indexing convention matters: the reconciler
/// and the dispatcher used to disagree about it, each leaving its schedule's first entry dead.
/// </summary>
public sealed class RetryScheduleTests
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15)
    ];

    [Fact]
    public void The_first_failed_attempt_waits_the_first_entry()
    {
        RetrySchedule.Backoff(Schedule, 1).ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Each_failure_climbs_the_schedule()
    {
        RetrySchedule.Backoff(Schedule, 2).ShouldBe(TimeSpan.FromMinutes(5));
        RetrySchedule.Backoff(Schedule, 3).ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Failures_past_the_end_repeat_the_last_entry()
    {
        RetrySchedule.Backoff(Schedule, 4).ShouldBe(TimeSpan.FromMinutes(15));
        RetrySchedule.Backoff(Schedule, 100).ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void An_empty_schedule_falls_back_rather_than_throwing()
    {
        RetrySchedule.Backoff([], 1).ShouldBe(RetrySchedule.FallbackDelay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_count_that_should_not_occur_clamps_to_the_first_entry(int failedAttempts)
    {
        RetrySchedule.Backoff(Schedule, failedAttempts).ShouldBe(TimeSpan.FromMinutes(1));
    }
}
