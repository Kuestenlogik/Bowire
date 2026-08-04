// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// One detected schema change, as observed by the workbench's schema
/// watch (#185). Entries are append-only: the watch client posts the
/// delta of each poll, and the store prunes anything older than the
/// retention window. Identity is by name, not by object reference —
/// <see cref="Service"/> and <see cref="Method"/> carry the same keys
/// the Discover sidebar uses (service name + method fullName), so a
/// click on a logged change can navigate back to the live method.
/// </summary>
public sealed record SchemaChangeEntry(
    DateTimeOffset At,
    string Type,
    string Service)
{
    /// <summary>
    /// Change classification. One of <c>added</c> / <c>removed</c> /
    /// <c>signature</c> / <c>deprecation</c> / <c>annotation</c> —
    /// mirrored by the workbench's diff classifier in api.js.
    /// </summary>
    public const string TypeAdded = "added";

    /// <summary>See <see cref="TypeAdded"/>.</summary>
    public const string TypeRemoved = "removed";

    /// <summary>See <see cref="TypeAdded"/>.</summary>
    public const string TypeSignature = "signature";

    /// <summary>See <see cref="TypeAdded"/>.</summary>
    public const string TypeDeprecation = "deprecation";

    /// <summary>See <see cref="TypeAdded"/>.</summary>
    public const string TypeAnnotation = "annotation";

    /// <summary>
    /// Method fullName within <see cref="Service"/>. Null for
    /// service-level changes (a whole service appeared / vanished).
    /// </summary>
    public string? Method { get; init; }

    /// <summary>
    /// Short human-readable explanation of what moved, e.g.
    /// <c>"route GET /pets → POST /pets"</c> or
    /// <c>"marked deprecated"</c>. Optional.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>True when <paramref name="type"/> is a known change classification.</summary>
    public static bool IsKnownType(string? type) => type is
        TypeAdded or TypeRemoved or TypeSignature or TypeDeprecation or TypeAnnotation;
}
