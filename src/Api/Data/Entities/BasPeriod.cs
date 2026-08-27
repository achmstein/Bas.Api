namespace Bas.Api.Data.Entities;

/// <summary>Where an activity statement has got to on its way to the ATO.</summary>
public enum BasPeriodStatus
{
    /// <summary>Being filled in. The only state in which figures can be changed.</summary>
    Draft = 0,

    /// <summary>The worker has submitted it. Queued for the practice; no longer editable.</summary>
    Submitted = 1,

    /// <summary>
    /// Submitted, but the ATO has not issued the statement for this period yet - or Practice
    /// Manager has not retrieved it. Retried on a slow cadence; not a failure.
    /// </summary>
    AwaitingStatement = 6,

    /// <summary>Written into Practice Manager, waiting for the agent.</summary>
    Pushed = 2,

    /// <summary>The agent has it open.</summary>
    InReview = 3,

    /// <summary>Lodged with the ATO.</summary>
    Lodged = 4,

    /// <summary>Failed on the way to the practice. <see cref="BasPeriod.FailureReason"/> says why.</summary>
    Failed = 5
}

/// <summary>
/// One worker's activity statement for one quarter.
///
/// <para><b>Every figure is nullable, and the distinction is load-bearing.</b> A worker with no
/// PAYG instalment obligation has no T section on their statement at all — which is not a T section
/// of zero. Practice Manager personalises each statement to what the ATO actually issued, so
/// writing a zero into a label this taxpayer does not have produces a different statement from the
/// one they were sent.</para>
///
/// <para>Amounts are whole-dollar <see cref="int"/>. The ATO drops cents on an activity
/// statement, so carrying decimals would only invite a rounding disagreement with the ATO's own
/// arithmetic.</para>
/// </summary>
public sealed class BasPeriod
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid WorkerId { get; set; }

    public Worker? Worker { get; set; }

    /// <summary>Named for the year it ends — FY2027 starts 1 Jul 2026.</summary>
    public int FinancialYear { get; set; }

    /// <summary>1-4, where Q1 is Jul-Sep.</summary>
    public int Quarter { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    /// <summary>Period end plus 28 days.</summary>
    public DateOnly DueDate { get; set; }

    public BasPeriodStatus Status { get; set; } = BasPeriodStatus.Draft;

    /// <summary>
    /// The ATO's statement type letter, as read back from the statement the ATO actually issued.
    /// Null until the reconciler has found that statement in Practice Manager.
    /// </summary>
    /// <remarks>
    /// Deliberately not settable by a partner, and deliberately never defaulted. The ATO issues the
    /// statement and chooses its type from obligations we cannot see; anyone upstream of the ATO
    /// asserting a letter is guessing, and the failure is silent - Practice Manager will create a
    /// statement of whatever type it is told, and nobody notices until the agent opens the wrong
    /// one. Phase 3c finds the issued statement rather than creating one.
    /// </remarks>
    public string? StatementType { get; set; }

    // ----- GST. Under Simpler BAS - mandatory below $10m turnover, so every gig worker - only
    // G1, 1A and 1B are lodged.

    /// <summary>G1 - total sales, GST inclusive.</summary>
    public int? TotalSales { get; set; }

    /// <summary>1A - GST on sales.</summary>
    public int? GstOnSales { get; set; }

    /// <summary>1B - GST on purchases.</summary>
    public int? GstOnPurchases { get; set; }

    /// <summary>Total purchases. Held for the worker's records; not lodged - it derives 1B.</summary>
    public int? TotalPurchases { get; set; }

    /// <summary>Cash rather than accruals.</summary>
    public bool? CashAccountingMethod { get; set; }

    // ----- PAYG instalments.

    /// <summary>T1 - instalment income.</summary>
    public int? InstalmentIncome { get; set; }

    /// <summary>T7 - the instalment amount the ATO worked out.</summary>
    public int? AtoInstalmentAmount { get; set; }

    /// <summary>T9 - varied instalment amount for the quarter.</summary>
    public int? VariedInstalmentAmount { get; set; }

    /// <summary>T4 - ATO reason code for a variation.</summary>
    public string? VariationReasonCode { get; set; }

    // ----- PAYG withholding.

    /// <summary>W1 - total salary, wages and other payments.</summary>
    public int? TotalSalaryWages { get; set; }

    /// <summary>W2 - amounts withheld from W1.</summary>
    public int? AmountWithheld { get; set; }

    // ----- Lifecycle.

    /// <summary>
    /// Label 9, as Practice Manager computed it. Read back after the push, never calculated here:
    /// if our arithmetic and the ATO's ever disagree, ours is the one that is wrong.
    /// </summary>
    public int? NetAmount { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>Why a <see cref="BasPeriodStatus.Failed"/> statement failed.</summary>
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Figures can only change while the worker still owns the statement. Once submitted it is
    /// queued for the practice, and an edit after that would mean the agent reviews one set of
    /// numbers while the worker believes another was sent.
    /// </summary>
    public bool IsEditable => Status is BasPeriodStatus.Draft or BasPeriodStatus.Failed;
}
