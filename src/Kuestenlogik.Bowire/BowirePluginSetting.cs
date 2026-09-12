// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Describes a single setting that a protocol plugin contributes to
/// the Bowire Settings dialog. The UI renders a control (toggle, text,
/// number, select) based on the <see cref="Type"/> and persists the
/// value in localStorage under <c>bowire_plugin_{pluginId}_{Key}</c>.
/// </summary>
/// <remarks>
/// <para>
/// #691 — the label and description travel to the browser already
/// rendered, so no guard on the JavaScript side can see that they are
/// English: the strings are not in the JavaScript. <c>LabelKey</c> and
/// <c>DescriptionKey</c> are how a plugin says "the catalogue has a
/// translation for this". Both are optional: a plugin that supplies
/// neither renders exactly as it did before, which is what third-party
/// plugins need — they have no entry in Bowire's catalogue and no way to
/// add one.
/// </para>
/// <para>
/// They are <c>init</c> properties rather than positional parameters, and
/// that is not a style choice. Appending two parameters to the primary
/// constructor is source-compatible but NOT binary-compatible: the
/// six-parameter <c>.ctor</c> stops existing, and every already-installed
/// plugin compiled against it throws <c>MissingMethodException</c> the
/// first time the settings endpoint touches it. The installed DIS plugin
/// did exactly that, and took <c>/api/protocols</c> down with it — a
/// blank Settings page for a translation nobody had asked for. A property
/// changes no constructor, so an old binary keeps working untouched.
/// </para>
/// </remarks>
/// <param name="Key">Unique key within this plugin (e.g. "autoInterpretJson").</param>
/// <param name="Label">Human-readable label shown in the settings UI.</param>
/// <param name="Description">Optional description shown below the label.</param>
/// <param name="Type">Control type: "bool", "string", "number", "select".</param>
/// <param name="DefaultValue">Default value (bool, string, number, or string for select).</param>
/// <param name="Options">For "select" type: list of { value, label } pairs.</param>
public sealed record BowirePluginSetting(
    string Key,
    string Label,
    string? Description = null,
    string Type = "bool",
    object? DefaultValue = null,
    IReadOnlyList<BowirePluginSettingOption>? Options = null)
{
    /// <summary>
    /// #691 — optional catalogue key for <see cref="Label"/>. When the
    /// workbench's active locale has an entry for it, that entry is shown;
    /// otherwise <see cref="Label"/> is, exactly as before.
    /// </summary>
    public string? LabelKey { get; init; }

    /// <summary>The same, for <see cref="Description"/>.</summary>
    public string? DescriptionKey { get; init; }
}

/// <summary>
/// Option entry for a "select" type plugin setting.
/// </summary>
/// <param name="Value">The stored value.</param>
/// <param name="Label">What the operator reads.</param>
public sealed record BowirePluginSettingOption(string Value, string Label)
{
    /// <summary>
    /// #691 — optional catalogue key for <see cref="Label"/>; a property
    /// rather than a parameter for the binary-compatibility reason spelled
    /// out on <see cref="BowirePluginSetting"/>.
    /// </summary>
    public string? LabelKey { get; init; }
}
