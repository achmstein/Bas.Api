namespace Bas.Api.Data.Entities;

/// <summary>
/// The one and only mapping from a partner's user to a <see cref="Worker"/> in this service.
///
/// <para><b>Identity resolves on (PartnerId, PartnerSub) only — never on email.</b> That rule is
/// load-bearing rather than stylistic: if a partner assertion could resolve to an account by
/// matching an email address, anyone able to sign an assertion could name a victim's address and
/// be handed that person's TFN and figures. The unique index below is what enforces it.</para>
/// </summary>
public sealed class PartnerUserLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PartnerId { get; set; }

    public Partner? Partner { get; set; }

    /// <summary>The partner's stable internal id for this user — the <c>sub</c> of the subject token.</summary>
    public required string PartnerSub { get; set; }

    public Guid WorkerId { get; set; }

    public Worker? Worker { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last successful token exchange for this link. Useful for support and for spotting
    /// dormant links; carries no authorisation meaning.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
