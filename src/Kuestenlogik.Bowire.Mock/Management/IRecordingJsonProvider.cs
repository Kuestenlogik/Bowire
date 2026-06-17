// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// Recording-lookup seam consumed by <c>POST /api/mocks</c> when the
/// caller passes a <c>{ recordingId, label }</c> body shape ("Use as
/// mock"). The Mock package doesn't know how the workbench stores
/// recordings — each host registers an adapter that bridges to its
/// own store (the standalone CLI's <c>WorkbenchRecordingJsonProvider</c>
/// scans every per-workspace <c>ChunkedRecordingStore</c>; embedded
/// hosts plug their own implementation).
/// </summary>
public interface IRecordingJsonProvider
{
    /// <summary>Return the verbatim JSON for a single recording, or null when not found.</summary>
    string? TryGetRecordingJson(string recordingId);
}
