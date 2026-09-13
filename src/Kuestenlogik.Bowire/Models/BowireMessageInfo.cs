// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// Describes a protobuf message type with its fields.
/// </summary>
public sealed record BowireMessageInfo(
    string Name,
    string FullName,
    List<BowireFieldInfo> Fields)
{
    /// <summary>
    /// True when this shape was deliberately not expanded here — because it
    /// closes a cycle, sits past the depth cap, or arrived after the walk's
    /// expansion budget ran out (#694).
    /// </summary>
    /// <remarks>
    /// Without this flag an unexpanded shape is indistinguishable from a
    /// message that genuinely has no fields, and a form renderer has no way
    /// to tell "nothing to fill in" from "not shown here". Omitted from JSON
    /// when false, so the common case costs no bytes.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; init; }
}
