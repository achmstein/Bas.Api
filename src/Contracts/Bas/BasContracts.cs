using System.ComponentModel.DataAnnotations;

namespace Bas.Api.Contracts.Bas;

/// <summary>Where an activity statement has got to.</summary>
public static class BasStatuses
{
    /// <summary>Being filled in. The only state in which figures can be changed.</summary>
    public const string Draft = "draft";

    /// <summary>The worker has submitted it. Queued for the practice; no longer editable.</summary>
    public const string Submitted = "submitted";

    /// <summary>Written into Practice Manager, waiting for the agent to look at it.</summary>
    public const string Pushed = "pushed";

    /// <summary>The agent has it open.</summary>
    public const string InReview = "in_review";

    /// <summary>Lodged with the ATO.</summary>
    public const string Lodged = "lodged";

    /// <summary>
    /// Submitted, but the ATO has not yet issued the statement for this period — or Practice
    /// Manager has not yet retrieved it.
    ///
    /// <para>Not an error, and not something the worker can act on. The ATO issues an activity
    /// statement shortly after the period ends, and the type it chooses is the only authority on
    /// what statement this taxpayer has. Waiting is correct; creating one on a guessed type would
    /// put a wrong statement into the live practice.</para>
    /// </summary>
    public const string AwaitingStatement = "awaiting_statement";

    /// <summary>Something went wrong on the way to the practice. Carries a reason.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// The worker's identity, as Practice Manager needs it to create a client.
///
/// <para>Sent by the partner on the worker's behalf. Practice Manager will not create a client
/// without a structurally valid TFN, so this has to be complete before anything can be lodged.</para>
/// </summary>
public sealed record WorkerIdentityRequest
{
    /// <summary>Nine digits. Spaces are fine; we strip them. Checked against the ATO algorithm.</summary>
    [Required]
    public required string Tfn { get; init; }

    /// <summary>Eleven digits, if the worker has one. Sole traders lodging a BAS usually will.</summary>
    public string? Abn { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string FirstName { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string FamilyName { get; init; }

    [Required]
    public required DateOnly DateOfBirth { get; init; }

    [EmailAddress]
    public string? Email { get; init; }

    public string? Phone { get; init; }
}

/// <summary>
/// The worker behind an access token.
///
/// <para>The TFN comes back masked, always. No screen or log needs the full value: Practice Manager
/// holds it, and the only thing showing it achieves is putting it somewhere it can be
/// screenshotted.</para>
/// </summary>
public sealed record WorkerIdentityResponse
{
    /// <summary>This service's id for the worker — the <c>sub</c> of the access token.</summary>
    public required Guid WorkerId { get; init; }

    /// <summary>The partner that vouched for them.</summary>
    public required string PartnerId { get; init; }

    /// <summary>Masked, e.g. <c>******789</c>. Null when no TFN has been supplied yet.</summary>
    public string? TfnMasked { get; init; }

    public string? Abn { get; init; }

    public string? FirstName { get; init; }

    public string? FamilyName { get; init; }

    public DateOnly? DateOfBirth { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    /// <summary>
    /// False while anything Practice Manager requires is still missing. Submitting an activity
    /// statement is refused until this is true.
    /// </summary>
    public required bool IsCompleteForLodgement { get; init; }
}

/// <summary>
/// One activity statement's figures.
///
/// <para><b>This is a full replacement.</b> Send every label the worker has a value for on each
/// save, not just the ones that changed — an absent label means "this statement has no such label",
/// which is a different statement from one where the label is zero. A worker with no PAYG
/// instalment obligation has no T section at all; writing zeros into it would be wrong.</para>
///
/// <para>All amounts are whole dollars. The ATO drops cents on an activity statement.</para>
/// </summary>
public sealed record SaveBasRequest
{
    // ----- GST. Under Simpler BAS - mandatory below $10m turnover, so every gig worker - only
    // G1, 1A and 1B are lodged.

    /// <summary>G1 — total sales, GST inclusive.</summary>
    [Range(0, int.MaxValue)]
    public int? TotalSales { get; init; }

    /// <summary>1A — GST on sales. Not derived from G1: GST-free sales sit in G1 and add nothing here.</summary>
    [Range(0, int.MaxValue)]
    public int? GstOnSales { get; init; }

    /// <summary>1B — GST on purchases.</summary>
    [Range(0, int.MaxValue)]
    public int? GstOnPurchases { get; init; }

    /// <summary>Total purchases. Stored for the worker's own records; not lodged — it derives 1B.</summary>
    [Range(0, int.MaxValue)]
    public int? TotalPurchases { get; init; }

    /// <summary>Cash rather than accruals. Changes what belongs in G1 for the period.</summary>
    public bool? CashAccountingMethod { get; init; }

    // ----- PAYG instalments. Present only when the ATO has entered this taxpayer into the
    // instalment system. Leave the whole section null otherwise.

    /// <summary>T1 — instalment income.</summary>
    [Range(0, int.MaxValue)]
    public int? InstalmentIncome { get; init; }

    /// <summary>T7 — the instalment amount the ATO worked out.</summary>
    [Range(0, int.MaxValue)]
    public int? AtoInstalmentAmount { get; init; }

    /// <summary>T9 — varied instalment amount for the quarter.</summary>
    [Range(0, int.MaxValue)]
    public int? VariedInstalmentAmount { get; init; }

    /// <summary>T4 — ATO reason code for a variation. Required whenever T9 is used.</summary>
    public string? VariationReasonCode { get; init; }

    // ----- PAYG withholding. Only when the worker employs someone.

    /// <summary>W1 — total salary, wages and other payments.</summary>
    [Range(0, int.MaxValue)]
    public int? TotalSalaryWages { get; init; }

    /// <summary>W2 — amounts withheld from W1.</summary>
    [Range(0, int.MaxValue)]
    public int? AmountWithheld { get; init; }
}

/// <summary>One activity statement in full.</summary>
public sealed record BasPeriodResponse
{
    public required Guid Id { get; init; }

    /// <summary>Australian financial year, named for the year it ends — FY2027 starts 1 Jul 2026.</summary>
    public required int FinancialYear { get; init; }

    /// <summary>1–4. Q1 is Jul–Sep.</summary>
    public required int Quarter { get; init; }

    public required DateOnly PeriodStart { get; init; }

    public required DateOnly PeriodEnd { get; init; }

    /// <summary>
    /// Period end plus 28 days. A registered agent's lodgment program extends most quarters beyond
    /// this — going through the practice is what buys that extra time.
    /// </summary>
    public required DateOnly DueDate { get; init; }

    /// <summary>One of <see cref="BasStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// The ATO's statement type letter, <b>read back from the statement the ATO issued</b> — never
    /// supplied by the partner and never guessed here. Null until the statement has been found in
    /// Practice Manager.
    /// </summary>
    public string? StatementType { get; init; }

    public int? TotalSales { get; init; }

    public int? GstOnSales { get; init; }

    public int? GstOnPurchases { get; init; }

    public int? TotalPurchases { get; init; }

    public bool? CashAccountingMethod { get; init; }

    public int? InstalmentIncome { get; init; }

    public int? AtoInstalmentAmount { get; init; }

    public int? VariedInstalmentAmount { get; init; }

    public string? VariationReasonCode { get; init; }

    public int? TotalSalaryWages { get; init; }

    public int? AmountWithheld { get; init; }

    /// <summary>
    /// Label 9 — what is owed or refunded, <b>as Practice Manager computed it</b>. Null until the
    /// statement has been pushed and read back. Never calculated here: if our arithmetic and the
    /// ATO's disagree, the ATO's is the one that matters.
    /// </summary>
    public int? NetAmount { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    /// <summary>Why a <c>failed</c> statement failed. Null otherwise.</summary>
    public string? FailureReason { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A statement in a list — enough to render a row without the figures.</summary>
public sealed record BasPeriodSummary
{
    public required Guid Id { get; init; }

    public required int FinancialYear { get; init; }

    public required int Quarter { get; init; }

    public required DateOnly PeriodStart { get; init; }

    public required DateOnly PeriodEnd { get; init; }

    public required DateOnly DueDate { get; init; }

    public required string Status { get; init; }

    public int? NetAmount { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }
}

/// <summary>Where a statement has got to, and what it will cost.</summary>
public sealed record BasStatusResponse
{
    public required string Status { get; init; }

    /// <summary>Practice Manager's computed label 9. Null until it has been pushed and read back.</summary>
    public int? NetAmount { get; init; }

    public required DateOnly DueDate { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public string? FailureReason { get; init; }
}

/// <summary>Acknowledgement that a statement has been queued for the practice.</summary>
public sealed record SubmitBasResponse
{
    public required Guid PeriodId { get; init; }

    /// <summary>Always <c>submitted</c> at this point.</summary>
    public required string Status { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}
