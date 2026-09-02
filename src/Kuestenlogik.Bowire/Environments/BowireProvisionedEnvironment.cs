// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Environments;

/// <summary>
/// An environment the embedding host declares, rather than one a person typed
/// into the workbench (#49).
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes: an embedded host already has its own configuration —
/// base URLs, tenant ids, whatever <c>IOptions&lt;T&gt;</c> resolved for the
/// environment it is running in — and until now the only way to use those in
/// the workbench was to read them out of <c>appsettings.json</c> and type them
/// in again. Two copies, one of which is silently wrong the moment the other
/// changes.
/// </para>
/// <para>
/// <b>Declared, not stored.</b> These are contributed on every start and never
/// written to <c>environments.json</c>. A host that changes its configuration
/// changes the environment by restarting, and nothing stale is left behind. It
/// also means the workbench must not save them back — see
/// <see cref="BowireProvisionedEnvironments"/>, which strips them on the way
/// in for exactly that reason.
/// </para>
/// </remarks>
public sealed class BowireProvisionedEnvironment
{
    /// <summary>
    /// The id these are addressed by, derived from the name.
    /// </summary>
    /// <remarks>
    /// Prefixed so it cannot collide with the random ids the workbench mints,
    /// and stable across restarts so a request pinned to this environment
    /// keeps pointing at it.
    /// </remarks>
    public string Id => "host:" + Name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>What the environment is called in the switcher.</summary>
    public required string Name { get; init; }

    /// <summary>The variables, resolved by the host at start-up.</summary>
    public Dictionary<string, string> Variables { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Set <paramref name="key"/> from a value the host already has.
    /// </summary>
    /// <remarks>
    /// Null and empty are kept rather than skipped: a variable the host meant
    /// to provide and could not resolve is worth seeing as empty in the
    /// switcher, because the alternative is a request that fails with an
    /// unsubstituted <c>{{placeholder}}</c> and no clue why.
    /// </remarks>
    public BowireProvisionedEnvironment Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Variables[key] = value ?? string.Empty;
        return this;
    }

    /// <summary>Set <paramref name="key"/> from a value that is not a string.</summary>
    public BowireProvisionedEnvironment Set(string key, IFormattable? value)
        => Set(key, value?.ToString(null, CultureInfo.InvariantCulture));
}
