// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Scripting;

namespace Kuestenlogik.Bowire.Tests.Scripting;

/// <summary>
/// #672 — the server half of the script conformance suite: every case of
/// <c>Fixtures/scripts/conformance.json</c> through <see cref="JintScriptRunner"/>,
/// held to the outcome wwwroot-js/script-conformance.test.mjs holds the
/// browser's scripts.js to.
/// </summary>
public sealed class ScriptConformanceTests
{
    private static readonly JsonArray s_cases = (JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "scripts", "conformance.json")))!["cases"] as JsonArray)!;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in s_cases) data.Add(c!["name"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_server_engine_reaches_the_expected_outcome(string name)
    {
        var c = s_cases.First(x => x!["name"]!.GetValue<string>() == name)!.AsObject();
        var request = c["request"]!.AsObject();
        var script = new BowireScriptCall
        {
            Source = c["source"]!.GetValue<string>(),
            Shape = c["shape"]!.GetValue<string>(),
            Service = "conformance",
            Method = name,
            Request = new BowireScriptRequest
            {
                Url = Text(request["url"]),
                Method = Text(request["method"]),
                Body = Text(request["body"]),
                Headers = Map(request["headers"]),
                Query = Map(request["query"]),
            },
            Response = c["response"] is JsonObject r ? new BowireScriptResponse
            {
                Body = Text(r["body"]),
                Status = Text(r["status"]),
                DurationMs = r["durationMs"]?.GetValue<long>() ?? 0,
                Headers = Map(r["headers"]).ToDictionary(),
            } : null,
            Environment = Map(c["env"]),
            CapturedJson = c["captured"]?.ToJsonString() ?? "{}",
        };

        var runner = new JintScriptRunner();
        var result = c["phase"]!.GetValue<string>() == "pre" ? runner.RunPreRequest(script) : runner.RunPostResponse(script);

        var e = c["expect"]!.AsObject();
        Assert.Equal(e["ok"]!.GetValue<bool>(), result.Ok);
        if (e["assertFail"] is { } assertFail) Assert.Equal(assertFail.GetValue<bool>(), result.AssertFail);
        if (e["error"] is { } error) Assert.Equal(error.GetValue<string>(), result.Error);
        if (e["errorContains"] is { } contains) Assert.Contains(contains.GetValue<string>(), result.Error ?? "", StringComparison.Ordinal);
        if (e["request"] is JsonObject want)
        {
            foreach (var (field, value) in want)
            {
                switch (field)
                {
                    case "url": Assert.Equal(value!.GetValue<string>(), script.Request.Url); break;
                    case "method": Assert.Equal(value!.GetValue<string>(), script.Request.Method); break;
                    case "body": Assert.Equal(value!.GetValue<string>(), script.Request.Body); break;
                    case "headers": Assert.Equal(Map(value), script.Request.Headers); break;
                    case "query": Assert.Equal(Map(value), script.Request.Query); break;
                }
            }
        }
        if (e["env"] is { } env) Assert.Equal(Map(env), script.Environment);
        if (e["captured"] is { } captured)
            Assert.True(JsonNode.DeepEquals(captured, JsonNode.Parse(script.CapturedJson)), script.CapturedJson);
        if (e["log"] is JsonArray log)
            Assert.Equal(log.Select(x => x!.GetValue<string>()), result.Log.Take(log.Count));
    }

    [Fact]
    public void A_runaway_script_is_stopped_and_reported()
    {
        var script = new BowireScriptCall
        {
            Source = "while (true) {}",
            Limits = new BowireScriptLimits { Timeout = TimeSpan.FromMilliseconds(300) },
        };
        var result = new JintScriptRunner().RunPreRequest(script);
        Assert.False(result.Ok);
        Assert.Contains("was stopped", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_allocation_storm_is_stopped_and_reported()
    {
        var script = new BowireScriptCall
        {
            Source = "var a = []; while (true) { a.push(new Array(100000).join('x')); }",
            Limits = new BowireScriptLimits { MemoryBytes = 16L * 1024 * 1024, Timeout = TimeSpan.FromSeconds(30) },
        };
        var result = new JintScriptRunner().RunPreRequest(script);
        Assert.False(result.Ok);
        Assert.Contains("was stopped", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installed_runner_is_found()
        => Assert.IsType<JintScriptRunner>(BowireScriptRunners.Find());

    private static string Text(JsonNode? node) => node?.GetValue<string>() ?? "";

    private static Dictionary<string, string> Map(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj) map[key] = value?.GetValue<string>() ?? "";
        }
        return map;
    }
}
