// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// The <c>manualEntry</c> object a sidecar may put in its <c>initialize</c>
/// reply (#1003): the wire shape of <see cref="BowireManualEntryForm"/>, so a
/// sidecar describes how a call to it is entered by hand the way a .NET
/// plugin does through <see cref="IBowireManualEntry"/>.
/// </summary>
/// <remarks>
/// Every field but the ids and keys is optional, and fields the host does not
/// know are ignored, as for <c>settings</c>. A call kind without an id and a
/// field without a key are dropped here; anything else that is wrong with the
/// form is the ManualEntry service's check to find, which leaves the form out
/// and logs why.
/// </remarks>
internal sealed record SidecarManualEntry(
    IReadOnlyList<SidecarManualCallKind>? CallKinds = null,
    IReadOnlyList<SidecarManualField>? Fields = null,
    string? UrlPlaceholder = null);

/// <summary>Wire shape of <see cref="BowireManualCallKind"/>.</summary>
internal sealed record SidecarManualCallKind(
    string? Id,
    string? Label = null,
    string? MethodType = null,
    string? Service = null,
    string? Method = null,
    string? Description = null,
    string? BodyExample = null);

/// <summary>
/// Wire shape of <see cref="BowireManualField"/>. <c>placement</c> is
/// <c>"metadata"</c> (the default) or <c>"template"</c>.
/// </summary>
internal sealed record SidecarManualField(
    string? Key,
    string? Label = null,
    string? Placement = null,
    string? Description = null,
    string? Placeholder = null,
    bool Required = false,
    string? Type = null,
    IReadOnlyList<SidecarSettingOptionWire>? Options = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? CallKinds = null);

/// <summary>One choice of a <c>select</c> field on the wire.</summary>
internal sealed record SidecarSettingOptionWire(string? Value, string? Label = null);

/// <summary>Turns the wire shape into the host's form.</summary>
internal static class SidecarManualEntryMapper
{
    /// <summary>The host form, or <c>null</c> when the sidecar sent none or one without a usable call kind.</summary>
    public static BowireManualEntryForm? ToHostForm(SidecarManualEntry? wire)
    {
        if (wire?.CallKinds is not { Count: > 0 } kinds) return null;
        var callKinds = kinds
            .Where(k => !string.IsNullOrEmpty(k.Id))
            .Select(k => new BowireManualCallKind(
                k.Id!,
                string.IsNullOrEmpty(k.Label) ? k.Id! : k.Label,
                string.IsNullOrEmpty(k.MethodType) ? "Unary" : k.MethodType)
            {
                Service = k.Service ?? "",
                Method = k.Method ?? "",
                Description = k.Description,
                BodyExample = k.BodyExample,
            })
            .ToList();
        if (callKinds.Count == 0) return null;

        return new BowireManualEntryForm(callKinds)
        {
            UrlPlaceholder = wire.UrlPlaceholder,
            Fields = [.. (wire.Fields ?? [])
                .Where(f => !string.IsNullOrEmpty(f.Key))
                .Select(f => new BowireManualField(
                    f.Key!,
                    string.IsNullOrEmpty(f.Label) ? f.Key! : f.Label,
                    string.Equals(f.Placement, "template", StringComparison.OrdinalIgnoreCase)
                        ? BowireManualFieldPlacement.Template
                        : BowireManualFieldPlacement.Metadata)
                {
                    Description = f.Description,
                    Placeholder = f.Placeholder,
                    Required = f.Required,
                    Type = string.IsNullOrEmpty(f.Type) ? "string" : f.Type,
                    Options = [.. (f.Options ?? [])
                        .Where(o => o.Value is not null)
                        .Select(o => new BowireManualFieldOption(o.Value!, string.IsNullOrEmpty(o.Label) ? o.Value! : o.Label))],
                    DefaultValue = f.DefaultValue,
                    CallKinds = f.CallKinds ?? [],
                })],
        };
    }
}
