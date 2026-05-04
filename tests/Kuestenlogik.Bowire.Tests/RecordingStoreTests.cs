// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Read-only smoke tests for <see cref="RecordingStore"/>. The store
/// persists to <c>~/.bowire/recordings.json</c>; this test class stays
/// off the Save path so we don't clobber the developer's actual saved
/// recordings. Save is exercised by the integration tests that drive
/// the recording-endpoints surface.
/// </summary>
public class RecordingStoreTests
{
    [Fact]
    public void Load_Returns_Parseable_Json()
    {
        var json = RecordingStore.Load();

        Assert.False(string.IsNullOrEmpty(json));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Load_Result_Carries_Recordings_Key()
    {
        var json = RecordingStore.Load();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("recordings", out var recordings),
            "Loaded document is missing the 'recordings' key");
        Assert.Equal(JsonValueKind.Array, recordings.ValueKind);
    }
}
