namespace Bas.Api.Bas;

/// <summary>One quarter of an Australian financial year, with the dates the ATO works to.</summary>
/// <param name="FinancialYear">Named for the year it <em>ends</em> — FY2027 runs 1 Jul 2026 to 30 Jun 2027.</param>
/// <param name="Quarter">1–4, where Q1 is Jul–Sep.</param>
public readonly record struct BasQuarter(
    int FinancialYear, int Quarter, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueDate);

/// <summary>
/// The Australian activity-statement calendar.
///
/// <para>Two conventions here catch people out often enough to be worth stating plainly. A
/// financial year is named for the year it <em>ends</em>, so FY2027 begins on 1 July 2026. And
/// quarters are numbered from July, so Q1 is Jul–Sep and Q3 is Jan–Mar — a partner numbering from
/// January will be a quarter and a year out.</para>
/// </summary>
public static class BasCalendar
{
    /// <summary>Earliest financial year this service will accept. Guards against a typo'd year.</summary>
    public const int MinimumFinancialYear = 2020;

    /// <summary>
    /// Statutory due date is period end plus 28 days. A registered agent's lodgment program extends
    /// most quarters well beyond it — that concession is one of the real benefits of lodging
    /// through the practice, and it is not modelled here because the practice, not this service,
    /// decides when it applies.
    /// </summary>
    private const int DueDays = 28;

    /// <summary>Builds the quarter, or returns false if the year or quarter is out of range.</summary>
    public static bool TryCreate(int financialYear, int quarter, out BasQuarter result, out string? error)
    {
        result = default;

        if (financialYear is < MinimumFinancialYear or > 2100)
        {
            error = $"Financial year must be between {MinimumFinancialYear} and 2100.";
            return false;
        }

        if (quarter is < 1 or > 4)
        {
            error = "Quarter must be 1, 2, 3 or 4, where Q1 is Jul-Sep.";
            return false;
        }

        result = Create(financialYear, quarter);
        error = null;
        return true;
    }

    /// <summary>Builds the quarter. Throws if out of range; prefer <see cref="TryCreate"/> at a boundary.</summary>
    public static BasQuarter Create(int financialYear, int quarter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quarter, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quarter, 4);

        // Q1 starts in July of the *previous* calendar year, because the year is named for its end.
        var startMonth = quarter switch
        {
            1 => 7,
            2 => 10,
            3 => 1,
            4 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(quarter))
        };

        // Q1 and Q2 fall before Christmas, in the year the financial year began.
        var startYear = quarter <= 2 ? financialYear - 1 : financialYear;

        var start = new DateOnly(startYear, startMonth, 1);
        var end = start.AddMonths(3).AddDays(-1);

        return new BasQuarter(financialYear, quarter, start, end, end.AddDays(DueDays));
    }

    /// <summary>The quarter <paramref name="date"/> falls in.</summary>
    public static BasQuarter QuarterFor(DateOnly date)
    {
        // July onwards belongs to the financial year ending next calendar year.
        var financialYear = date.Month >= 7 ? date.Year + 1 : date.Year;

        var quarter = date.Month switch
        {
            >= 7 and <= 9 => 1,
            >= 10 and <= 12 => 2,
            >= 1 and <= 3 => 3,
            _ => 4
        };

        return Create(financialYear, quarter);
    }

    /// <summary>
    /// The quarters a worker could reasonably be looking at: everything from
    /// <paramref name="count"/> quarters ago up to the one that has most recently ended.
    /// </summary>
    /// <remarks>
    /// The current quarter is excluded on purpose. A BAS cannot be lodged before its period ends,
    /// so listing an in-progress quarter would invite a worker to fill in a statement that will be
    /// refused at submit.
    /// </remarks>
    public static IEnumerable<BasQuarter> RecentQuarters(DateOnly today, int count)
    {
        var quarter = QuarterFor(today);

        // Step back one, so the most recent entry is a quarter that has actually finished.
        var cursor = Previous(quarter);

        for (var i = 0; i < count; i++)
        {
            yield return cursor;
            cursor = Previous(cursor);
        }
    }

    /// <summary>The quarter before <paramref name="quarter"/>.</summary>
    public static BasQuarter Previous(BasQuarter quarter) =>
        quarter.Quarter == 1
            ? Create(quarter.FinancialYear - 1, 4)
            : Create(quarter.FinancialYear, quarter.Quarter - 1);

    /// <summary>Whether the period has finished, and so can be lodged.</summary>
    public static bool HasEnded(BasQuarter quarter, DateOnly today) => today > quarter.PeriodEnd;
}
