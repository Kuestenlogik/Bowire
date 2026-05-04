// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Read-only smoke tests for <see cref="EnvironmentStore"/>. The store
/// persists to <c>~/.bowire/environments.json</c>; we deliberately stay
/// off the Save path so we don't clobber the developer's actual settings.
/// Load is safe — it either returns the user's existing file content or
/// the empty default shape when the file is missing.
/// </summary>
public class EnvironmentStoreTests
{
    [Fact]
    public void Load_Returns_Valid_Json_Document()
    {
        // Whatever the file contains (or doesn't), Load must produce a
        // parseable JSON document. The catch-all in the impl falls back
        // to the empty-shape literal on read or parse failure.
        var json = EnvironmentStore.Load();

        Assert.False(string.IsNullOrEmpty(json));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Load_Returns_Document_With_Globals_And_Environments_Keys()
    {
        // The shape contract: the document carries at minimum the
        // "globals" and "environments" keys (plus optionally
        // "activeEnvId"). The empty-default literal in the impl uses
        // exactly that shape, so we can pin the keys without depending
        // on whether the user has saved data or not.
        var json = EnvironmentStore.Load();

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("globals", out _),
            "Loaded document is missing the 'globals' key");
        Assert.True(doc.RootElement.TryGetProperty("environments", out _),
            "Loaded document is missing the 'environments' key");
    }
}
