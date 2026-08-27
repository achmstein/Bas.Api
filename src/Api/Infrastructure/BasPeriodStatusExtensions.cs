using Bas.Api.Contracts.Bas;
using Bas.Api.Data.Entities;

namespace Bas.Api.Infrastructure;

/// <summary>
/// The one mapping from the entity status to the wire vocabulary partners read. Statements, Admin
/// and Webhooks all speak it, so it lives with the other cross-slice plumbing rather than in any
/// one of them.
/// </summary>
public static class BasPeriodStatusExtensions
{
    public static string ToWireStatus(this BasPeriodStatus status) => status switch
    {
        BasPeriodStatus.Draft => BasStatuses.Draft,
        BasPeriodStatus.Submitted => BasStatuses.Submitted,
        BasPeriodStatus.AwaitingStatement => BasStatuses.AwaitingStatement,
        BasPeriodStatus.Pushed => BasStatuses.Pushed,
        BasPeriodStatus.InReview => BasStatuses.InReview,
        BasPeriodStatus.Lodged => BasStatuses.Lodged,
        BasPeriodStatus.Failed => BasStatuses.Failed,
        _ => BasStatuses.Draft
    };
}
