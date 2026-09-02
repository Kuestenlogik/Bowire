// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// What a plugin's declared settings are actually set to (#640).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IBowireProtocol.Settings"/> lets a plugin contribute a schema to
/// <c>Settings → Plugins</c>. Until this existed, that was all it did: the
/// workbench rendered the controls and wrote them to the browser's local
/// storage, and no value ever reached the plugin that declared it. Four
/// plugins shipped a control that looked like it worked and did not.
/// </para>
/// <para>
/// <b>Pull, not push.</b> A plugin resolves this from the provider it is handed
/// in <see cref="IBowireProtocol.Initialize"/> and asks when it needs a value.
/// The alternative — adding a settings argument to <c>DiscoverAsync</c> — would
/// change the contract every protocol plugin implements, in every repository,
/// to deliver something most calls do not want. Asking also means a long-lived
/// plugin sees a change without being restarted.
/// </para>
/// <para>
/// <b>Values are workspace-scoped.</b> A probe window is a property of what you
/// are pointed at — a slow lab network — rather than of who you are. Which
/// workspace is being served comes from
/// <see cref="BowirePluginSettingsScope"/>, because <c>DiscoverAsync</c> has no
/// <c>HttpContext</c> to read it from.
/// </para>
/// </remarks>
public interface IBowirePluginSettings
{
    /// <summary>
    /// The raw value for <paramref name="key"/>, or <c>null</c> when nobody
    /// set one.
    /// </summary>
    /// <param name="pluginId">The declaring plugin's <c>Id</c>.</param>
    /// <param name="key">The setting's key, as declared.</param>
    string? GetValue(string pluginId, string key);

    /// <summary>The value as a whole number, or <paramref name="fallback"/>.</summary>
    int GetInt(string pluginId, string key, int fallback)
        => int.TryParse(GetValue(pluginId, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    /// <summary>The value as a flag, or <paramref name="fallback"/>.</summary>
    bool GetBool(string pluginId, string key, bool fallback)
        => bool.TryParse(GetValue(pluginId, key), out var parsed) ? parsed : fallback;

    /// <summary>
    /// The value read as a number of seconds, or <paramref name="fallback"/>.
    /// </summary>
    /// <remarks>
    /// Seconds because that is what the schema's <c>"number"</c> settings mean
    /// wherever a duration is declared, and because a plugin that has to
    /// remember which unit its own setting is in will eventually get it wrong.
    /// Zero and negative values fall back: a probe window of nothing is not a
    /// configuration, it is a mistake, and honouring it would look exactly
    /// like the bug this seam exists to fix.
    /// </remarks>
    TimeSpan GetSeconds(string pluginId, string key, TimeSpan fallback)
    {
        var seconds = GetValue(pluginId, key);
        return double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
            && parsed <= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(parsed)
                : fallback;
    }
}
