using Microsoft.AspNetCore.Identity;

namespace Bas.Api.Admin;

/// <summary>
/// A person who administers this service. There are expected to be one or two.
///
/// <para>Local accounts rather than a federated identity: the practice runs on its own, and an
/// external directory would be one more thing that has to be reachable for David to suspend a
/// partner at 9pm on a Friday.</para>
/// </summary>
public sealed class AdminUser : IdentityUser
{
    /// <summary>What the audit log shows. Falls back to the email address when unset.</summary>
    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignedInAt { get; set; }
}
