// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Mock.Management;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Adapter that lets the Mock package's
/// <see cref="BowireMockHostEndpoints"/> resolve recordings without
/// taking a hard reference on the workbench's internal
/// <see cref="RecordingStore"/>. Standalone tool registers this
/// at startup (#94).
/// </summary>
internal sealed class WorkbenchRecordingJsonProvider : IRecordingJsonProvider
{
    public string? TryGetRecordingJson(string recordingId)
    {
        var envelope = RecordingStore.Load();
        using var doc = JsonDocument.Parse(envelope);
        if (!doc.RootElement.TryGetProperty("recordings", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var rec in arr.EnumerateArray()
                     .Where(rec => rec.TryGetProperty("id", out var idProp) &&
                                   idProp.ValueKind == JsonValueKind.String &&
                                   string.Equals(idProp.GetString(), recordingId, StringComparison.Ordinal)))
        {
            return rec.GetRawText();
        }
        return null;
    }
}
