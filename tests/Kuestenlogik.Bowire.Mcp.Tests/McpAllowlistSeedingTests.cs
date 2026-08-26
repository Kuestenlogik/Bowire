// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Recording;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// Which URLs an MCP agent is allowed to reach, and how a recording mode
/// crosses the tool boundary.
/// </summary>
/// <remarks>
/// <para>
/// The allowlist is the line between "an agent can drive Bowire" and "an agent
/// can make Bowire send a request anywhere". It is seeded from a place the
/// <em>user</em> has already pointed Bowire at — their <c>environments.json</c>
/// — so an agent inherits reach rather than being granted it.
/// </para>
/// <para>
/// A seeding bug is silent in the dangerous direction: one extra URL picked up
/// from a nested object nobody meant as configuration widens what the agent may
/// do, and nothing in any response says so.
/// </para>
/// <para>
/// In <see cref="BowireConfigFixture"/> because the seeding test flips the
/// process-global <c>HomeDirOverride</c>.
/// </para>
/// </remarks>
[Collection(nameof(BowireConfigFixture))]
public sealed class McpAllowlistSeedingTests
{
    private static List<string> Walk(string json)
    {
        var options = new BowireMcpOptions();
        var seen = new HashSet<string>(options.AllowedServerUrls, StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        BowireMcpTools.WalkForServerUrls(doc.RootElement, options, seen);
        return [.. options.AllowedServerUrls];
    }

    [Fact]
    public void A_Server_Url_At_The_Top_Level_Is_Picked_Up()
        => Assert.Contains("https://api.example.com", Walk("""{"serverUrl":"https://api.example.com"}"""));

    [Fact]
    public void A_Server_Url_Nested_In_An_Environment_Is_Picked_Up()
    {
        // environments.json is a document of documents: the URLs sit inside
        // per-environment objects, never at the root.
        var urls = Walk("""
            {"environments":[
               {"name":"staging","values":{"serverUrl":"https://staging.example.com"}},
               {"name":"prod","values":{"serverUrl":"https://prod.example.com"}}
            ]}
            """);

        Assert.Contains("https://staging.example.com", urls);
        Assert.Contains("https://prod.example.com", urls);
    }

    [Fact]
    public void The_Field_Name_Is_Matched_Without_Regard_To_Case()
        // Hand-edited files and older writers both occur.
        => Assert.Contains("https://api.example.com", Walk("""{"ServerURL":"https://api.example.com"}"""));

    [Fact]
    public void The_Same_Url_Twice_Is_Added_Once()
    {
        var urls = Walk("""
            {"a":{"serverUrl":"https://api.example.com"},
             "b":{"serverUrl":"https://api.example.com"}}
            """);

        Assert.Single(urls);
    }

    [Fact]
    public void A_Server_Url_That_Is_Not_A_String_Is_Ignored()
    {
        // A number or an object under that key is not a URL, and coercing one
        // would put something meaningless on a security boundary.
        Assert.Empty(Walk("""{"serverUrl":42,"other":{"serverUrl":{"nested":"x"}}}"""));
    }

    [Fact]
    public void An_Empty_Server_Url_Is_Ignored()
        => Assert.Empty(Walk("""{"serverUrl":""}"""));

    [Fact]
    public void Fields_That_Merely_Look_Like_Urls_Are_Not_Collected()
    {
        // Only `serverUrl` widens the allowlist. A `url`, an `endpoint` or a
        // documentation link in the same file must not — the list is meant to
        // hold the places the user chose as servers, not every URL they wrote
        // down.
        Assert.Empty(Walk("""
            {"url":"https://elsewhere.example.com",
             "endpoint":"https://also-elsewhere.example.com",
             "docs":"https://docs.example.com"}
            """));
    }

    [Fact]
    public void An_Empty_Document_Adds_Nothing()
        => Assert.Empty(Walk("{}"));

    [Fact]
    public void A_Scalar_Document_Adds_Nothing()
        // Neither object nor array: the walk has to fall through rather than
        // reach for enumerators the element does not have.
        => Assert.Empty(Walk("\"just a string\""));

    [Fact]
    public void An_Array_At_The_Root_Is_Walked_Too()
        => Assert.Contains("https://api.example.com",
            Walk("""[{"serverUrl":"https://api.example.com"}]"""));

    [Fact]
    public void A_Url_The_Host_Configured_Is_Not_Duplicated_By_Seeding()
    {
        // The embedding host may have set the allowlist itself; seeding is
        // additive on top of that, and `seen` starts from what is already there.
        var options = new BowireMcpOptions();
        options.AllowedServerUrls.Add("https://api.example.com");
        var seen = new HashSet<string>(options.AllowedServerUrls, StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse("""{"serverUrl":"https://API.example.com"}""");

        BowireMcpTools.WalkForServerUrls(doc.RootElement, options, seen);

        Assert.Single(options.AllowedServerUrls);
    }

    // ---- seeding from the user's environments file ----

    private static void WithHome(string? environmentsJson, Action<BowireMcpOptions> assert)
    {
        var previous = BowireMcpTools.HomeDirOverride;
        var home = SafePath.Combine(Path.GetTempPath(), $"bowire-mcp-allow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(SafePath.Combine(home, ".bowire"));
        if (environmentsJson is not null)
            File.WriteAllText(SafePath.Combine(home, ".bowire", "environments.json"), environmentsJson);
        BowireMcpTools.HomeDirOverride = home;
        try
        {
            var options = new BowireMcpOptions();
            BowireMcpTools.SeedAllowlistFromEnvironments(options);
            assert(options);
        }
        finally
        {
            BowireMcpTools.HomeDirOverride = previous;
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Seeding_Reads_The_Users_Environments_File()
        => WithHome("""
            {"environments":[{"name":"prod","values":{"serverUrl":"https://prod.example.com"}}]}
            """,
            options => Assert.Contains("https://prod.example.com", options.AllowedServerUrls));

    [Fact]
    public void Seeding_From_A_Missing_Environments_File_Is_A_No_Op()
        // A fresh install has no environments.json. That must leave the
        // allowlist as the host configured it, not throw during start-up.
        => WithHome(environmentsJson: null, options => Assert.Empty(options.AllowedServerUrls));

    // ---- recording mode on the wire ----

    [Theory]
    [InlineData("proxy", BowireRecordingMode.Proxy)]
    [InlineData("PROXY", BowireRecordingMode.Proxy)]
    [InlineData("  replay  ", BowireRecordingMode.Replay)]
    [InlineData("capture", BowireRecordingMode.Capture)]
    public void A_Mode_Is_Parsed_However_An_Agent_Spells_It(string wire, BowireRecordingMode expected)
        => Assert.Equal(expected, BowireMcpTools.ParseMode(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void An_Unrecognised_Mode_Falls_Back_To_Capture(string? wire)
        // Capture is the harmless one of the three: an agent that sends
        // something unexpected must not end up proxying or replaying.
        => Assert.Equal(BowireRecordingMode.Capture, BowireMcpTools.ParseMode(wire));

    [Theory]
    [InlineData(BowireRecordingMode.Capture, "capture")]
    [InlineData(BowireRecordingMode.Proxy, "proxy")]
    [InlineData(BowireRecordingMode.Replay, "replay")]
    public void The_Wire_Name_Comes_From_A_Fixed_Table_Not_The_Enum_Name(BowireRecordingMode mode, string expected)
        // Deliberately a table rather than ToString().ToLowerInvariant(), so a
        // rename of an enum case cannot quietly change the tool contract.
        => Assert.Equal(expected, BowireMcpTools.ModeWireName(mode));

    [Fact]
    public void Every_Mode_Round_Trips_Through_Its_Wire_Name()
    {
        foreach (var mode in Enum.GetValues<BowireRecordingMode>())
        {
            Assert.Equal(mode, BowireMcpTools.ParseMode(BowireMcpTools.ModeWireName(mode)));
        }
    }
}
