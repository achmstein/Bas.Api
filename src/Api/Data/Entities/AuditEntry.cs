namespace Bas.Api.Data.Entities;

/// <summary>
/// One change made through the admin surface.
///
/// <para>Append-only, and written in the same transaction as the change it records - an audit entry
/// that can be omitted when someone is in a hurry is not an audit log. The data-sharing agreement
/// and the Privacy Act TFN Rule both want an answer to "who changed a partner's access, and when",
/// and this is it.</para>
///
/// <para>Reads are not recorded. Every partner-facing request already logs its partner id and token
/// id, and recording every admin GET would bury the handful of entries that matter.</para>
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>e.g. <c>partner.suspended</c>, <c>partner.key_rotated</c>, <c>lodgement.retried</c>.</summary>
    public required string Action { get; set; }

    /// <summary>The name of the admin key used. Not a secret - that is the point of naming keys.</summary>
    public required string Actor { get; set; }

    /// <summary>What was acted on: a partner client id, a period id.</summary>
    public string? Subject { get; set; }

    /// <summary>
    /// What changed, as free text. Never contains a secret or a TFN: a key rotation records that
    /// the key changed and its readable prefix, never the key.
    /// </summary>
    public string? Detail { get; set; }

    public DateTimeOffset At { get; set; }
}
