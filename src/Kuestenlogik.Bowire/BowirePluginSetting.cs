// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Describes a single setting that a protocol plugin contributes to
/// the Bowire Settings dialog. The UI renders a control (toggle, text,
/// number, select) based on the <see cref="Type"/> and persists the
/// value in localStorage under <c>bowire_plugin_{pluginId}_{Key}</c>.
/// </summary>
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
    IReadOnlyList<BowirePluginSettingOption>? Options = null);

/// <summary>
/// Option entry for a "select" type plugin setting.
/// </summary>
public sealed record BowirePluginSettingOption(string Value, string Label);
