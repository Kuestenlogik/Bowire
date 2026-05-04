// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Wire-shape tests for the internal <see cref="BowireWorkspaceEndpoints.WorkspaceFile"/>
/// record — the on-disk container the <c>.blw</c> workspace endpoint
/// reads and writes. Camel-cased property names are required so the
/// JS client can serialise / deserialise the file with idiomatic field
/// names; these tests pin both the default shape and the serializer
/// contract.
/// </summary>
public class WorkspaceFileTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    { WriteIndented = true, PropertyNameCaseInsensitive = true };

    [Fact]
    public void Default_Workspace_Has_Empty_Collections_And_Globals()
    {
        var ws = new BowireWorkspaceEndpoints.WorkspaceFile();

        Assert.NotNull(ws.Urls);
        Assert.Empty(ws.Urls);
        Assert.NotNull(ws.Environments);
        Assert.Empty(ws.Environments);
        Assert.NotNull(ws.Globals);
        Assert.Empty(ws.Globals);
        Assert.NotNull(ws.Collections);
        Assert.Empty(ws.Collections);
    }

    [Fact]
    public void Workspace_Round_Trips_Through_JsonSerializer_Without_Loss()
    {
        var globals = new Dictionary<string, string>
        {
            ["env"] = "staging",
            ["region"] = "eu-west-1",
        };
        var ws = new BowireWorkspaceEndpoints.WorkspaceFile
        {
            Urls = new List<string> { "https://api.example.com" },
            Globals = globals,
            Environments = new List<JsonElement>(),
            Collections = new List<JsonElement>(),
        };

        var json = JsonSerializer.Serialize(ws, JsonOpts);
        var parsed = JsonSerializer.Deserialize<BowireWorkspaceEndpoints.WorkspaceFile>(json, JsonOpts);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Urls);
        Assert.Equal("https://api.example.com", parsed.Urls[0]);
        Assert.Equal(2, parsed.Globals.Count);
        Assert.Equal("staging", parsed.Globals["env"]);
        Assert.Equal("eu-west-1", parsed.Globals["region"]);
    }

    [Fact]
    public void Workspace_Deserializes_Minimal_Json_With_Only_Urls()
    {
        const string json = """
            {
              "urls": ["https://api.local:5000"]
            }
            """;
        var ws = JsonSerializer.Deserialize<BowireWorkspaceEndpoints.WorkspaceFile>(json, JsonOpts);

        Assert.NotNull(ws);
        Assert.Single(ws!.Urls);
        Assert.Equal("https://api.local:5000", ws.Urls[0]);
        Assert.Empty(ws.Environments);
        Assert.Empty(ws.Globals);
        Assert.Empty(ws.Collections);
    }

    [Fact]
    public void Workspace_Empty_Json_Object_Round_Trips_To_Defaults()
    {
        var ws = JsonSerializer.Deserialize<BowireWorkspaceEndpoints.WorkspaceFile>("{}", JsonOpts);

        Assert.NotNull(ws);
        Assert.Empty(ws!.Urls);
        Assert.Empty(ws.Environments);
        Assert.Empty(ws.Globals);
        Assert.Empty(ws.Collections);
    }

    [Fact]
    public void Workspace_Carries_Nested_JsonElement_Environments_Through_RoundTrip()
    {
        const string json = """
            {
              "urls": [],
              "environments": [
                { "id": "env-1", "name": "Staging", "vars": { "token": "abc" } }
              ],
              "collections": [
                { "id": "col-1", "name": "Auth tests" }
              ]
            }
            """;

        var ws = JsonSerializer.Deserialize<BowireWorkspaceEndpoints.WorkspaceFile>(json, JsonOpts);

        Assert.NotNull(ws);
        Assert.Single(ws!.Environments);
        Assert.Equal("env-1", ws.Environments[0].GetProperty("id").GetString());
        Assert.Single(ws.Collections);
        Assert.Equal("col-1", ws.Collections[0].GetProperty("id").GetString());
    }
}
