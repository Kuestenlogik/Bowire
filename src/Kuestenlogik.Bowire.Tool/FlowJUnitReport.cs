// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Stub — emits an empty Surefire skeleton so the runner can write
/// <c>--junit</c> output without coupling Phase 1 (flow execution) to
/// Phase 2 (report emission). The full Surefire shape lands in the
/// next commit; this skeleton exists only to make the file-write side
/// of the runner exercisable.
/// </summary>
internal static class FlowJUnitReport
{
    public static string Render(FlowRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><testsuites />";
    }
}
