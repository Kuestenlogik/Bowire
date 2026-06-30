// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Stub — emits a minimal HTML skeleton so the runner can write
/// <c>--report</c> output without coupling Phase 1 (flow execution) to
/// Phase 2 (report emission). The full layout lands in the next commit.
/// </summary>
internal static class FlowHtmlReport
{
    public static string Render(FlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return "<!doctype html><html><head><title>Bowire Flow Report</title></head><body><p>Stub.</p></body></html>";
    }
}
