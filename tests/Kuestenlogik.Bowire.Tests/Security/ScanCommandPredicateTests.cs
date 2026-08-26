// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// The decisions <c>bowire scan</c> makes before and after a probe runs.
/// </summary>
/// <remarks>
/// Each of these decides either whether a probe runs at all or how its finding
/// reaches GitHub. Getting one wrong does not throw — it produces a scan that
/// looks like it worked: a probe silently skipped, a template proving nothing,
/// or a SARIF file GitHub rejects on ingest.
/// </remarks>
public sealed class ScanCommandPredicateTests
{
    // ---- severity → SARIF ----

    [Theory]
    [InlineData("CRITICAL", 9.5)]
    [InlineData("HIGH", 7.5)]
    [InlineData("MEDIUM", 5.5)]
    [InlineData("LOW", 3.5)]
    [InlineData("INFO", 0.0)]
    [InlineData("anything else", 0.0)]
    public void A_Severity_Label_Becomes_A_Number_For_Code_Scanning(string severity, double expected)
        // GitHub's SARIF ingest rejects a non-numeric `security-severity`
        // with "invalid security severity value, is not a number" — the whole
        // upload fails, not the one finding.
        => Assert.Equal(expected, ScanCommand.SeverityToScore(severity));

    [Theory]
    [InlineData("critical")]
    [InlineData("Critical")]
    [InlineData("CrItIcAl")]
    public void Severity_Casing_Comes_From_The_Template_And_Must_Not_Matter(string severity)
        // Nuclei templates are community-written; the label arrives however
        // its author typed it.
        => Assert.Equal(9.5, ScanCommand.SeverityToScore(severity));

    [Fact]
    public void Severity_Ranking_Orders_Worst_First()
    {
        Assert.True(ScanCommand.SeverityRank("CRITICAL") > ScanCommand.SeverityRank("HIGH"));
        Assert.True(ScanCommand.SeverityRank("HIGH") > ScanCommand.SeverityRank("MEDIUM"));
        Assert.True(ScanCommand.SeverityRank("MEDIUM") > ScanCommand.SeverityRank("LOW"));
        Assert.True(ScanCommand.SeverityRank("LOW") > ScanCommand.SeverityRank("INFO"));
    }

    [Fact]
    public void An_Unknown_Severity_Ranks_Lowest_Rather_Than_Highest()
        // A template with a typo in its severity must not float to the top of
        // a report and displace a real critical.
        => Assert.Equal(0, ScanCommand.SeverityRank("wat"));

    // ---- the OAST server ----

    [Theory]
    [InlineData("https://oast.example.com")]
    [InlineData("http://localhost:8080")]
    public void An_Http_Interaction_Server_Is_Usable(string url)
        => Assert.True(ScanCommand.IsUsableOastServer(url));

    [Theory]
    [InlineData("")]
    [InlineData("oast.example.com")]          // no scheme
    [InlineData("ftp://oast.example.com")]    // absolute, wrong scheme
    [InlineData("/local/path")]               // an absolute path is an absolute URI on Unix
    [InlineData("not a url at all")]
    public void Anything_That_Is_Not_An_Http_Url_Is_Not_An_Interaction_Server(string url)
        // The scheme check matters beyond tidiness: on Unix an absolute file
        // path parses as an absolute URI, so absoluteness alone would accept
        // "/local/path" as a callback host.
        => Assert.False(ScanCommand.IsUsableOastServer(url));

    // ---- templates that need an out-of-band callback ----

    private static BowireRecording WithStep(BowireRecordingStep step)
        => new() { Steps = [step] };

    [Fact]
    public void A_Placeholder_In_The_Path_Means_The_Probe_Would_Prove_Nothing()
    {
        // Without an interaction server the probe carries the literal
        // {{interactsh-url}} and can never observe a callback — so it would
        // report "no finding" for a target that might well be vulnerable.
        var rec = WithStep(new BowireRecordingStep { HttpPath = "/redirect?to={{interactsh-url}}" });

        Assert.True(ScanCommand.NeedsOastButHasNone(rec));
    }

    [Fact]
    public void A_Placeholder_In_The_Body_Counts_Too()
        => Assert.True(ScanCommand.NeedsOastButHasNone(
            WithStep(new BowireRecordingStep { Body = """{"callback":"{{interactsh-url}}"}""" })));

    [Fact]
    public void A_Placeholder_In_Metadata_Counts_Too()
    {
        // Headers land in metadata, and an OAST payload in a header is the
        // commonest shape for SSRF templates.
        var rec = WithStep(new BowireRecordingStep
        {
            Metadata = new Dictionary<string, string> { ["X-Forwarded-Host"] = "{{interactsh-url}}" },
        });

        Assert.True(ScanCommand.NeedsOastButHasNone(rec));
    }

    [Fact]
    public void The_Placeholder_Is_Matched_Whatever_Its_Casing()
        => Assert.True(ScanCommand.NeedsOastButHasNone(
            WithStep(new BowireRecordingStep { HttpPath = "/x?u={{INTERACTSH-URL}}" })));

    [Fact]
    public void A_Recording_Without_The_Placeholder_Needs_No_Server()
        => Assert.False(ScanCommand.NeedsOastButHasNone(
            WithStep(new BowireRecordingStep { HttpPath = "/health", Body = "{}" })));

    [Fact]
    public void A_Recording_With_No_Steps_Needs_No_Server()
        => Assert.False(ScanCommand.NeedsOastButHasNone(new BowireRecording { Steps = [] }));

    // ---- which protocols a template can be replayed against ----

    [Theory]
    [InlineData("REST")]
    [InlineData("GRAPHQL")]
    [InlineData("ODATA")]
    [InlineData("HTTP")]
    [InlineData("SSE")]
    [InlineData("SIGNALR")]
    [InlineData("SOCKETIO")]
    [InlineData("MCP")]
    public void Protocols_Whose_Probed_Request_Is_An_Http_Request_Are_In_Scope(string protocol)
        // SignalR's negotiate, Socket.IO's Engine.IO handshake and MCP's
        // Streamable-HTTP transport are all plain HTTP for the request a
        // template probes — the upgraded connection afterwards is not, but
        // that is not what these templates look at.
        => Assert.True(ScanCommand.IsHttpClassProtocol(protocol));

    [Fact]
    public void A_Protocol_Nothing_Knows_Is_Out_Of_Scope()
        => Assert.False(ScanCommand.IsHttpClassProtocol("SOMETHING-ELSE"));

    // ---- target scheme ----

    [Theory]
    [InlineData("http://api.example.com")]
    [InlineData("https://api.example.com")]
    public void An_Http_Target_Is_Accepted(string target)
        => Assert.True(ScanCommand.IsHttpScheme(target));

    [Fact]
    public void A_Bare_Host_Without_A_Port_Is_Accepted()
        // Not a URI at all, so the guard lets it through for a later stage to
        // resolve.
        => Assert.True(ScanCommand.IsHttpScheme("api.example.com"));

    [Fact]
    public void A_Host_Port_Pair_Is_Refused_Because_Dotnet_Reads_The_Host_As_A_Scheme()
    {
        // Worth pinning as a fact about the platform rather than a wish:
        // Uri.TryCreate("localhost:5001", Absolute) SUCCEEDS, parsing
        // "localhost" as the scheme and "5001" as the path. So the guard sees
        // a scheme that is neither http nor https and says no.
        //
        // That is defensible for a scanner whose templates replay HTTP
        // requests — a bare host:port is how a gRPC target is written, and
        // those go down a different path. But it is not obvious from reading
        // the guard, which is exactly why it is written down here.
        Assert.False(ScanCommand.IsHttpScheme("localhost:5001"));
    }

    [Fact]
    public void A_Target_With_A_Different_Scheme_Is_Refused()
        => Assert.False(ScanCommand.IsHttpScheme("ftp://files.example.com"));
}
