// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Interop.Formats;

namespace Kuestenlogik.Bowire.Interop.Tests;

/// <summary>
/// The C# half of the "Copy as .http" contract (#673). The workbench renders
/// one request as a .http block in JS (http-file-export.js); its output is
/// pinned in <c>Fixtures/http-snippet-golden.json</c>, which
/// <c>wwwroot-js/http-file-export.test.mjs</c> replays. Here every block goes
/// through the importer's parser: what the workbench writes must read back
/// as one request with the verb and URL the scenario names.
/// </summary>
public sealed class HttpSnippetGoldenTests
{
    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in Golden().GetProperty("scenarios").EnumerateArray())
            data.Add(scenario.GetProperty("scenario").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void The_Workbench_Snippet_Parses_As_One_Request(string name)
    {
        var scenario = Golden().GetProperty("scenarios").EnumerateArray()
            .Single(s => s.GetProperty("scenario").GetString() == name);
        var document = HttpFile.Parse(scenario.GetProperty("expected").GetString()!);

        var request = Assert.Single(document.Requests);
        Assert.Empty(request.Unsupported);
        Assert.Equal(scenario.GetProperty("verb").GetString(), request.Method);
        Assert.Equal(scenario.GetProperty("url").GetString(), request.Url);

        // A JSON body reads back as JSON: a header mistaken for body text,
        // or body text mistaken for a header, breaks this.
        var contentType = request.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
        if (contentType == "application/json")
        {
            using var _ = JsonDocument.Parse(request.Body);
        }
    }

    [Fact]
    public void Fixture_Is_Not_Empty()
    {
        Assert.True(Golden().GetProperty("scenarios").GetArrayLength() >= 6);
    }

    private static JsonElement Golden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "http-snippet-golden.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }
}
