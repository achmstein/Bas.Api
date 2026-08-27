using System.ComponentModel.DataAnnotations;

namespace Bas.Api.Admin;

// The admin surface's request and response shapes. They live in the Api project rather than the
// Contracts package deliberately: that package is the partner wire contract, and a partner
// generating a client from it should never end up with a suspendPartner() in their SDK.

/// <summary>Registration details for a new partner.</summary>
public sealed record CreatePartnerRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string ClientId { get; init; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>Space-delimited. Every scope must be one this service knows.</summary>
    [Required]
    public required string AllowedScopes { get; init; }

    public string? WebhookUrl { get; init; }

    public string? WebhookSecret { get; init; }
}

/// <summary>Where to deliver a partner's status changes.</summary>
public sealed record SetWebhookRequest
{
    /// <summary>An absolute https URL. Empty removes the webhook and its secret.</summary>
    public string? Url { get; init; }

    /// <summary>Issue a new signing secret. The old one stops working immediately.</summary>
    public bool NewSecret { get; init; }
}

/// <summary>A partner's webhook settings, and the secret if one was just issued.</summary>
public sealed record WebhookResult
{
    public required AdminPartnerResponse Partner { get; init; }

    /// <summary>Present only when a secret was just issued. Shown once; send it to the partner.</summary>
    public string? Secret { get; init; }
}

/// <summary>Why a partner was suspended or resumed. Recorded in the audit log.</summary>
public sealed record SuspendRequest
{
    public string? Reason { get; init; }
}

/// <summary>A newly registered partner, or one whose key was just rotated.</summary>
public sealed record CreatePartnerResult
{
    public required AdminPartnerResponse Partner { get; init; }

    /// <summary>
    /// The API key, present only in this one response. Nothing stores it — the database keeps its
    /// hash — so it cannot be shown again.
    /// </summary>
    public required string ApiKey { get; init; }
}

/// <summary>A partner, as an operator sees them. Carries no secret.</summary>
public sealed record AdminPartnerResponse
{
    public required string ClientId { get; init; }

    public required string Name { get; init; }

    /// <summary><c>active</c> or <c>suspended</c>.</summary>
    public required string Status { get; init; }

    public required string AllowedScopes { get; init; }

    /// <summary>The readable start of their key (<c>bas_xxxxxxxx</c>). Null until one is issued.</summary>
    public string? ApiKeyPrefix { get; init; }

    public string? WebhookUrl { get; init; }

    /// <summary>Whether a webhook secret is set. Never the secret.</summary>
    public required bool HasWebhookSecret { get; init; }

    /// <summary>How many of their users have been provisioned here.</summary>
    public required int WorkerCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One statement, with the sync ledger's view of it alongside.</summary>
public sealed record AdminLodgementResponse
{
    public required Guid PeriodId { get; init; }

    public required Guid WorkerId { get; init; }

    public string? PartnerId { get; init; }

    /// <summary>The partner's own id for the worker, for support conversations.</summary>
    public string? PartnerSub { get; init; }

    public required int FinancialYear { get; init; }

    public required int Quarter { get; init; }

    public required string Status { get; init; }

    public int? NetAmount { get; init; }

    public DateTimeOffset? SubmittedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>The retry ledger's status: Pending, Synced, AwaitingStatement, Failed.</summary>
    public string? SyncStatus { get; init; }

    public int? AttemptCount { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>The last push failure. Usually the thing you actually came here for.</summary>
    public string? LastError { get; init; }
}

/// <summary>One recorded admin change.</summary>
public sealed record AuditEntryResponse
{
    public required DateTimeOffset At { get; init; }

    public required string Action { get; init; }

    /// <summary>The name of the admin key used.</summary>
    public required string Actor { get; init; }

    public string? Subject { get; init; }

    public string? Detail { get; init; }
}
