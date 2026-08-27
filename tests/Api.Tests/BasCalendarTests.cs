using Bas.Api.Bas;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// The Australian financial-year conventions, pinned down.
///
/// <para>Two of them are counter-intuitive enough that a partner will get them wrong at least once:
/// a financial year is named for the year it <em>ends</em>, and quarters are numbered from July. A
/// partner numbering from January is a quarter and a year out, and the failure is silent — the
/// figures land on the wrong statement rather than erroring.</para>
/// </summary>
public sealed class BasCalendarTests
{
    [Theory]
    // FY2027 runs 1 Jul 2026 to 30 Jun 2027.
    [InlineData(2027, 1, "2026-07-01", "2026-09-30", "2026-10-28")]
    [InlineData(2027, 2, "2026-10-01", "2026-12-31", "2027-01-28")]
    [InlineData(2027, 3, "2027-01-01", "2027-03-31", "2027-04-28")]
    [InlineData(2027, 4, "2027-04-01", "2027-06-30", "2027-07-28")]
    public void Quarters_run_from_July_and_are_due_28_days_after_they_end(
        int financialYear, int quarter, string start, string end, string due)
    {
        var result = BasCalendar.Create(financialYear, quarter);

        result.PeriodStart.ShouldBe(DateOnly.Parse(start));
        result.PeriodEnd.ShouldBe(DateOnly.Parse(end));
        result.DueDate.ShouldBe(DateOnly.Parse(due));
    }

    [Fact]
    public void The_example_from_the_plan_holds()
    {
        // docs/bas-gateway.md quotes a due date of 2026-10-28 for the first quarter of FY2027.
        BasCalendar.Create(2027, 1).DueDate.ShouldBe(new DateOnly(2026, 10, 28));
    }

    [Theory]
    [InlineData("2026-07-01", 2027, 1)]
    [InlineData("2026-09-30", 2027, 1)]
    [InlineData("2026-10-01", 2027, 2)]
    [InlineData("2026-12-31", 2027, 2)]
    [InlineData("2027-01-01", 2027, 3)]
    [InlineData("2027-06-30", 2027, 4)]
    // 30 June and 1 July are one day apart and in different financial years.
    [InlineData("2026-06-30", 2026, 4)]
    public void Maps_a_date_to_its_quarter(string date, int expectedYear, int expectedQuarter)
    {
        var result = BasCalendar.QuarterFor(DateOnly.Parse(date));

        result.FinancialYear.ShouldBe(expectedYear);
        result.Quarter.ShouldBe(expectedQuarter);
    }

    [Fact]
    public void Leap_year_February_lands_in_the_right_quarter_and_ends_on_the_29th()
    {
        var q3 = BasCalendar.Create(2028, 3);

        q3.PeriodStart.ShouldBe(new DateOnly(2028, 1, 1));
        q3.PeriodEnd.ShouldBe(new DateOnly(2028, 3, 31));
        BasCalendar.QuarterFor(new DateOnly(2028, 2, 29)).Quarter.ShouldBe(3);
    }

    [Fact]
    public void Previous_of_Q1_is_Q4_of_the_year_before()
    {
        var previous = BasCalendar.Previous(BasCalendar.Create(2027, 1));

        previous.FinancialYear.ShouldBe(2026);
        previous.Quarter.ShouldBe(4);
        previous.PeriodEnd.ShouldBe(new DateOnly(2026, 6, 30));
    }

    [Fact]
    public void Recent_quarters_start_with_the_one_that_most_recently_ended()
    {
        // Mid-Q2 of FY2027. The current quarter is excluded: a BAS cannot be lodged before its
        // period ends, so offering it would invite a statement that gets refused at submit.
        var quarters = BasCalendar.RecentQuarters(new DateOnly(2026, 11, 15), 4).ToList();

        quarters[0].FinancialYear.ShouldBe(2027);
        quarters[0].Quarter.ShouldBe(1);
        quarters[1].Quarter.ShouldBe(4);
        quarters[1].FinancialYear.ShouldBe(2026);
        quarters.Count.ShouldBe(4);
        quarters.ShouldAllBe(q => q.PeriodEnd < new DateOnly(2026, 11, 15));
    }

    [Fact]
    public void A_period_has_not_ended_on_its_final_day()
    {
        var q1 = BasCalendar.Create(2027, 1);

        BasCalendar.HasEnded(q1, new DateOnly(2026, 9, 30)).ShouldBeFalse();
        BasCalendar.HasEnded(q1, new DateOnly(2026, 10, 1)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(2027, 0)]
    [InlineData(2027, 5)]
    [InlineData(1999, 1)]
    [InlineData(2200, 1)]
    public void Out_of_range_input_is_refused_rather_than_guessed_at(int financialYear, int quarter)
    {
        BasCalendar.TryCreate(financialYear, quarter, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }
}
