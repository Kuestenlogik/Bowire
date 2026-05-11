// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Bitmask of capability flags exposed by an <see cref="IBowireUiExtension"/>.
/// Drives the workbench's mount decision: a coordinate.wgs84 extension that
/// only declares <see cref="Viewer"/> never appears as a request-side
/// editor; one that declares both flags can serve either role on the same
/// kind.
/// </summary>
/// <remarks>
/// The flag set is intentionally tight in v1.0 — anything beyond viewer /
/// editor would lock the contract into decisions we haven't validated yet.
/// New capabilities (detector, recorder, …) get added additively in minor
/// versions; <see cref="None"/> is reserved for the empty-bitmask case and
/// is not a valid registration.
/// </remarks>
[Flags]
public enum ExtensionCapabilities
{
    /// <summary>No capability declared. Reserved sentinel; not a valid
    /// registration value.</summary>
    None = 0,

    /// <summary>
    /// Extension mounts as a response-side viewer (read-only frame
    /// rendering — map pins, image bytes, audio waveform, …).
    /// </summary>
    Viewer = 1 << 0,

    /// <summary>
    /// Extension mounts as a request-side editor (interactive widget that
    /// patches request fields — drag-a-pin map, image-byte picker, …).
    /// </summary>
    Editor = 1 << 1,
}
