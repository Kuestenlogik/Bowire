// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Scim;

/// <summary>
/// What an install has said about letting an identity provider manage its
/// user list (#96, #28 Phase C). Bound from <c>Bowire:Scim</c>.
/// </summary>
public sealed class BowireScimOptions
{
    /// <summary>
    /// Whether the SCIM endpoints are mounted at all. Off by default.
    /// </summary>
    /// <remarks>
    /// A provisioning API is an administrative surface reachable with one
    /// shared secret. It exists when an operator asks for it and not a moment
    /// before — least of all as a side effect of upgrading.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// The bearer token the identity provider presents.
    /// </summary>
    /// <remarks>
    /// Enabling without one is refused at startup rather than served open:
    /// an unauthenticated provisioning endpoint is a way for anyone who can
    /// reach the host to create identities.
    /// </remarks>
    public string? Token { get; set; }

    /// <summary>Where the endpoints mount. <c>/scim/v2</c> by default.</summary>
    /// <remarks>
    /// Outside the workbench's route group on purpose. Those routes are gated
    /// by the workbench's own auth provider, and an IdP's provisioning
    /// connector has a shared secret, not a user session — it could never get
    /// through that gate.
    /// </remarks>
    public string BasePath { get; set; } = "/scim/v2";

    /// <summary>
    /// How long a deprovisioned identity's state is kept before it is deleted.
    /// 30 days by default; <see cref="TimeSpan.Zero"/> deletes immediately.
    /// </summary>
    /// <remarks>
    /// The window exists because deprovisioning is routinely undone — someone
    /// changes team, an IdP sync misfires, a contract is extended. Deleting on
    /// the DELETE call makes those recoverable only from a backup, if there is
    /// one.
    /// </remarks>
    public TimeSpan PurgeAfter { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How many resources a list response returns when the caller does not
    /// say. RFC 7644 §3.4.2.4 leaves this to the service provider.
    /// </summary>
    public int DefaultPageSize { get; set; } = 100;

    /// <summary>The largest page a caller can ask for.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>
    /// Whether a deactivated identity is refused at the door.
    /// </summary>
    /// <remarks>
    /// On by default, because it is the difference between provisioning and
    /// bookkeeping: an install where deprovisioning records a flag that
    /// nothing reads has not deprovisioned anybody. Turn it off only when
    /// something in front of Bowire already enforces the same thing.
    /// </remarks>
    public bool EnforceActive { get; set; } = true;

    /// <summary>
    /// Whether an identity the directory has never heard of is refused.
    /// </summary>
    /// <remarks>
    /// Off by default, and the default is the careful one: an IdP that has not
    /// finished its first sync would otherwise lock out the operator who just
    /// turned provisioning on, including their way back in to turn it off.
    /// Installs that want the directory to be the allow-list rather than an
    /// additional check set this once the first sync has landed.
    /// </remarks>
    public bool RequireProvisioned { get; set; }

    /// <summary>
    /// The group whose members Bowire treats as administrators.
    /// </summary>
    /// <remarks>
    /// Named rather than assumed: an IdP's group for this is called whatever
    /// the operator's directory calls it, and hard-coding <c>admin</c> would
    /// make provisioning work only for directories that happen to agree.
    /// </remarks>
    public string AdminGroup { get; set; } = "bowire-admins";
}
