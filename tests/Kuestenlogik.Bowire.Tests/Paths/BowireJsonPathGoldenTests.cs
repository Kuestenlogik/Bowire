// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Paths;

namespace Kuestenlogik.Bowire.Tests.Paths;

/// <summary>
/// The C# half of the one path resolver (#676). <c>json-path-golden.json</c>
/// pins what every path selects; <c>wwwroot-js/json-path.test.mjs</c> replays
/// the same file through the browser's json-path.js, so a path means the same
/// thing in the Tests tab and under <c>bowire test</c>.
/// </summary>
public sealed class BowireJsonPathGoldenTests
{
    private static readonly Lazy<JsonObject> s_golden = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Paths", "json-path-golden.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    });

    public static TheoryData<string> Paths()
    {
        var data = new TheoryData<string>();
        foreach (var c in s_golden.Value["cases"]!.AsArray()) data.Add((string)c!["path"]!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void The_Path_Selects_What_The_Fixture_Says(string path)
    {
        var expected = s_golden.Value["cases"]!.AsArray().Single(c => (string)c!["path"]! == path)!.AsObject();
        var document = s_golden.Value["document"];

        if (expected["error"] is { } position)
        {
            var ex = Assert.Throws<JsonPathException>(() => BowireJsonPath.TryResolve(document, path, out _));
            Assert.Equal((int)position, ex.Position);
            Assert.StartsWith($"Invalid JSONPath at position {ex.Position}:", ex.Message, StringComparison.Ordinal);
            return;
        }

        var found = BowireJsonPath.TryResolve(document, path, out var value);
        Assert.Equal((bool)expected["found"]!, found);
        if (found) Assert.True(JsonNode.DeepEquals(expected["value"], value), $"{path}: expected {expected["value"]?.ToJsonString() ?? "null"}, got {value?.ToJsonString() ?? "null"}");
    }

    [Fact]
    public void The_Fixture_Covers_Every_Selector()
    {
        var paths = s_golden.Value["cases"]!.AsArray().Select(c => (string)c!["path"]!).ToList();
        Assert.True(paths.Count >= 40);
        foreach (var needle in (string[])["[?", "..", "[*]", ".*", "::", "[-1]", ",", "&&", "||", "!@", "$.store.items[0].id", "'"])
            Assert.Contains(paths, p => p.Contains(needle, StringComparison.Ordinal));
        Assert.Contains(s_golden.Value["cases"]!.AsArray(), c => c!["error"] is not null);
    }

    [Fact]
    public void Matches_Say_Where_They_Sit_So_A_Writer_Can_Set_Them()
    {
        var doc = JsonNode.Parse("""{"items":[{"done":true,"n":1},{"done":false,"n":2}]}""");

        var matches = BowireJsonPath.Select(doc, "$.items[?(@.done == true)].n");

        var match = Assert.Single(matches);
        Assert.Equal("n", match.Name);
        Assert.Same(doc!["items"]![0], match.Parent);
    }

    [Fact]
    public void Simple_Paths_Are_Told_Apart_From_Queries()
    {
        Assert.True(BowireJsonPath.IsSimple("items.0.sku"));
        Assert.True(BowireJsonPath.IsSimple("$.items[0]['sku']"));
        Assert.False(BowireJsonPath.IsSimple("$.items[*].sku"));
        Assert.Equal(["items", "0", "sku"], BowireJsonPath.SimpleSteps("$.items[0].sku"));
    }
}
