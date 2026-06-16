// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Workspace.Git;

namespace Kuestenlogik.Bowire.Workspace.Git.Tests;

/// <summary>
/// Behavioural coverage for <see cref="SecretOverlayMerger"/> — the
/// <c>&lt;env&gt;.json</c> + <c>&lt;env&gt;.secrets.json</c> merge
/// helper shipped in #196 Phase 2.5. Pins override semantics so the
/// secret-separation convention stays predictable for operators.
/// </summary>
public sealed class SecretOverlayMergerTests
{
    [Fact]
    public void Merge_With_Null_Overlay_Returns_Base_Verbatim()
    {
        const string baseJson = """{"id":"staging","name":"S"}""";
        Assert.Equal(baseJson, SecretOverlayMerger.Merge(baseJson, null));
        Assert.Equal(baseJson, SecretOverlayMerger.Merge(baseJson, ""));
        Assert.Equal(baseJson, SecretOverlayMerger.Merge(baseJson, "   "));
    }

    [Fact]
    public void Merge_With_Null_Base_Returns_Reserialised_Overlay()
    {
        const string overlay = """{"variables":{"API_KEY":"sk-1"}}""";
        var merged = SecretOverlayMerger.Merge(null, overlay);
        Assert.NotNull(merged);
        using var doc = JsonDocument.Parse(merged!);
        Assert.Equal("sk-1", doc.RootElement.GetProperty("variables").GetProperty("API_KEY").GetString());
    }

    [Fact]
    public void Merge_Nested_Variables_Adds_Without_Replacing_Base_Keys()
    {
        const string baseJson = """{"id":"staging","variables":{"API_HOST":"api.staging.example.com"}}""";
        const string overlay = """{"variables":{"API_KEY":"sk-2"}}""";

        var merged = SecretOverlayMerger.Merge(baseJson, overlay);
        Assert.NotNull(merged);
        using var doc = JsonDocument.Parse(merged!);
        var vars = doc.RootElement.GetProperty("variables");
        Assert.Equal("api.staging.example.com", vars.GetProperty("API_HOST").GetString());
        Assert.Equal("sk-2", vars.GetProperty("API_KEY").GetString());
        // id stays — overlay never carried it.
        Assert.Equal("staging", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Merge_Overlay_Variable_Overrides_Base_On_Collision()
    {
        const string baseJson = """{"variables":{"API_KEY":"placeholder","API_HOST":"a"}}""";
        const string overlay = """{"variables":{"API_KEY":"sk-real"}}""";

        var merged = SecretOverlayMerger.Merge(baseJson, overlay);
        using var doc = JsonDocument.Parse(merged!);
        var vars = doc.RootElement.GetProperty("variables");
        Assert.Equal("sk-real", vars.GetProperty("API_KEY").GetString());
        Assert.Equal("a", vars.GetProperty("API_HOST").GetString());
    }

    [Fact]
    public void Merge_Top_Level_Scalar_From_Overlay_Replaces_Base_Scalar()
    {
        const string baseJson = """{"id":"staging","name":"Staging"}""";
        const string overlay = """{"name":"Staging (Secrets-overridden)"}""";

        var merged = SecretOverlayMerger.Merge(baseJson, overlay);
        using var doc = JsonDocument.Parse(merged!);
        Assert.Equal("Staging (Secrets-overridden)", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("staging", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Merge_With_Non_Object_Base_Throws_JsonException()
    {
        Assert.Throws<JsonException>(() =>
            SecretOverlayMerger.Merge("[1,2,3]", """{"variables":{"A":"1"}}"""));
    }

    [Fact]
    public void Merge_With_Non_Object_Overlay_Throws_JsonException()
    {
        Assert.Throws<JsonException>(() =>
            SecretOverlayMerger.Merge("""{"id":"x"}""", "[1,2,3]"));
    }
}
