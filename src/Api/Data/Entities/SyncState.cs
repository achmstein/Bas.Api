namespace Bas.Api.Data.Entities;

/// <summary>Where a subject stands with the practice, mechanically.</summary>
public enum SyncStatus
{
    /// <summary>Waiting to be pushed. The reconciler will pick it up when <see cref="SyncState.NextAttemptAt"/> arrives.</summary>
    Pending = 0,

    /// <summary>Pushed successfully, and the content has not changed since.</summary>
    Synced = 1,

    /// <summary>
    /// The push reached Practice Manager, but the ATO has not issued the statement for this period
    /// yet. Retried on a slow cadence; not a failure.
    /// </summary>
    AwaitingStatement = 2,

    /// <summary>Attempts are exhausted. Needs a human, or a fresh save from the worker.</summary>
    Failed = 3
}

/// <summary>
/// The retry ledger for one <see cref="BasPeriod"/>.
///
/// <para><b>Why this exists at all.</b> PracticeManager.Api has no job queue by design — "the
/// caller already owns a durable record of what needs syncing, and duplicating that server-side
/// would mean two systems disagreeing about the same work". This service is that caller, so retry
/// is ours to own. The shape is lifted from NightTax's own sync ledger, which has been running the
/// same bargain against the same downstream for a while.</para>
///
/// <para><b>Why it is separate from <see cref="BasPeriod.Status"/>.</b> That status is the
/// business fact a partner reads: draft, submitted, lodged. This is mechanical bookkeeping —
/// attempt counts, backoff, the last error. Merging them would leak retry plumbing into the
/// partner contract and make every schedule change a wire change.</para>
/// </summary>
public sealed class SyncState
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The statement this ledger row tracks. One row per period.</summary>
    public Guid BasPeriodId { get; set; }

    public BasPeriod? BasPeriod { get; set; }

    public SyncStatus Status { get; set; } = SyncStatus.Pending;

    /// <summary>
    /// When the subject last changed in a way that needs pushing. Set on submit; compared against
    /// <see cref="LastSyncedAt"/> so a re-submitted statement is picked up again.
    /// </summary>
    public DateTimeOffset DirtyAt { get; set; }

    /// <summary>
    /// Hash of what was last pushed successfully. The reconciler skips a subject whose content
    /// still hashes to this — a redeploy or a spurious wake-up then costs nothing, which matters
    /// when the downstream is a single browser session with a ten-minute cold start.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>Consecutive failed attempts. Reset by a success, and by a fresh save.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Consecutive outage-shaped failures. Drives backoff only — never counted against the
    /// reconciler's attempt budget, because an outage says nothing about this statement.
    /// Reset whenever Practice Manager answers, whatever the answer is.
    /// </summary>
    public int TransientAttemptCount { get; set; }

    /// <summary>Not before this. The whole of the backoff schedule lives in this one column.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>The last failure, for support. Never carries a TFN.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
