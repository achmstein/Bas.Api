namespace Bas.Api.Data.Entities;

/// <summary>
/// The person whose activity statement this service lodges. Minted just-in-time by the token
/// exchange the first time a partner presents a new subject, then filled in through
/// <c>PUT /api/v1/workers/me</c>.
///
/// <para>Practice Manager will not create a client without a structurally valid TFN, so
/// <see cref="IsCompleteForLodgement"/> is what stands between a half-filled profile and a
/// reconciler that retries forever against a push that can never succeed.</para>
/// </summary>
public sealed class Worker
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The tax file number, digits only.
    ///
    /// <para>Stored as written. It is still TFN information under the Privacy Act TFN Rule, so it
    /// never leaves this service in full - every response and log line carries the masked form -
    /// and a database dump is now a disclosure if it escapes. Protect the backups accordingly.</para>
    /// </summary>
    public string? Tfn { get; set; }

    /// <summary>Last three digits, so a masked value can be rendered without touching the TFN itself.</summary>
    public string? TfnLast3 { get; set; }

    /// <summary>Digits only. Public information — the ABR publishes it — so it is not encrypted.</summary>
    public string? Abn { get; set; }

    public string? FirstName { get; set; }

    public string? FamilyName { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<PartnerUserLink> PartnerLinks { get; set; } = [];

    public ICollection<BasPeriod> BasPeriods { get; set; } = [];

    /// <summary>Whether Practice Manager has everything it needs to create a client for this worker.</summary>
    public bool IsCompleteForLodgement =>
        !string.IsNullOrEmpty(Tfn)
        && !string.IsNullOrWhiteSpace(FirstName)
        && !string.IsNullOrWhiteSpace(FamilyName)
        && DateOfBirth is not null;
}
