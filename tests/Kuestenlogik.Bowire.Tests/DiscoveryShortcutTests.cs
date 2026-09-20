// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The first-run shortcut in <c>/api/services</c> (#732).
/// </summary>
/// <remarks>
/// <para>
/// A standalone tool with no <c>--url</c>, nothing uploaded and no URL on the
/// request has nothing to discover, so it answers an empty list rather than
/// waiting ~10 s for a gRPC handshake with a host that ships no gRPC.
/// </para>
/// <para>
/// The shortcut asked only <c>ProtoUploadStore.HasUploads</c>. The drop zone
/// takes a <c>.proto</c> and an OpenAPI document through the same control, so
/// dropping a <c>petstore.yaml</c> into a workbench with no URL returned an
/// empty list without probing at all — the document was stored, the REST
/// plugin would have read it, and the shortcut made sure it never got the
/// chance. A <c>.proto</c> in the same situation worked. The sidebar's answer
/// depended on which kind of file you dragged, and it said nothing either way.
/// </para>
/// <para>
/// Asserted on the decision rather than through the endpoint, and that is a
/// deliberate limit. The shortcut applies only to a standalone host, and in an
/// in-process test server the REST plugin's embedded discovery finds the
/// harness's own routes — so <c>/api/services</c> is never empty there whether
/// the shortcut fires or not, and a test driven through it passes for either
/// behaviour. Two attempts proved exactly that before this one.
/// </para>
/// </remarks>
public sealed class DiscoveryShortcutTests : IDisposable
{
    private const string Proto = """
        syntax = "proto3";
        package shortcut;
        service Beacon { rpc Ping (R) returns (R); }
        message R { string id = 1; }
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-shortcut-" + Guid.NewGuid().ToString("N"));

    private readonly IDisposable _userScope;

    public DiscoveryShortcutTests()
    {
        Directory.CreateDirectory(_root);
        _userScope = BowireUserContext.Enter(new DefaultBowireUserStore(_root));
        ProtoUploadStore.Clear();
        OpenApiUploadStore.Clear();
    }

    public void Dispose()
    {
        ProtoUploadStore.Clear();
        OpenApiUploadStore.Clear();
        _userScope.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static BowireOptions Standalone() => new() { Mode = BowireMode.Standalone };

    [Fact]
    public void A_First_Run_With_Nothing_To_Go_On_Skips_The_Probe()
    {
        // What the shortcut is for, and what makes the rest of this class
        // mean anything: if it never fired, every assertion below would hold
        // whichever stores it consulted.
        Assert.True(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), serverUrl: ""));
        Assert.True(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), serverUrl: null));
    }

    [Fact]
    public void An_Uploaded_OpenApi_Document_Is_Reason_Enough_To_Probe()
    {
        // The bug. The document is stored and the REST plugin reads it during
        // discovery; short-circuiting means it never gets asked.
        OpenApiUploadStore.Add("""{"openapi":"3.0.0","info":{"title":"p","version":"1"},"paths":{}}""", "petstore.json");

        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), serverUrl: ""));
    }

    [Fact]
    public void An_Uploaded_Proto_Is_Too()
    {
        // This half always worked. It is asserted beside the other because
        // the defect was that the two behaved differently — a test for only
        // the broken one would not say that.
        ProtoUploadStore.AddAndParse(Proto, "beacon.proto");

        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), serverUrl: ""));
    }

    [Fact]
    public void Either_Kind_Alone_Is_Enough_And_Both_Together_Are_Too()
    {
        ProtoUploadStore.AddAndParse(Proto, "beacon.proto");
        OpenApiUploadStore.Add("""{"openapi":"3.0.0","info":{"title":"p","version":"1"},"paths":{}}""", "petstore.json");

        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), serverUrl: ""));
    }

    [Fact]
    public void Clearing_The_Uploads_Brings_The_Shortcut_Back()
    {
        // The shortcut must not be lost for the rest of the process because
        // something was once uploaded: it is a per-request decision.
        OpenApiUploadStore.Add("{}", "petstore.json");
        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), serverUrl: ""));

        OpenApiUploadStore.Clear();

        Assert.True(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), serverUrl: ""));
    }

    // ---- the other conditions, so the fix did not widen the shortcut away ----

    [Fact]
    public void A_Url_On_The_Request_Means_There_Is_Something_To_Probe()
    {
        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(Standalone(), "https://api.example.com"));
    }

    [Fact]
    public void A_Configured_Url_Does_Too()
    {
        var options = Standalone();
        options.ServerUrls.Add("https://api.example.com");

        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(options, serverUrl: ""));
    }

    [Fact]
    public void A_Hosts_Own_Proto_Sources_Do_Too()
    {
        var options = Standalone();
        options.ProtoSources.Add(ProtoSource.FromContent(Proto));

        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(options, serverUrl: ""));
    }

    [Fact]
    public void An_Embedded_Host_Never_Takes_The_Shortcut()
    {
        // Embedded means the host's own API is the subject, so there is always
        // something to look at — and the ~10 s handshake the shortcut avoids
        // is not a risk there.
        Assert.False(BowireDiscoveryEndpoints.NothingToDiscover(
            new BowireOptions { Mode = BowireMode.Embedded }, serverUrl: ""));
    }

    [Fact]
    public void The_Decision_Refuses_A_Missing_Options_Object_Rather_Than_Guessing()
    {
        Assert.Throws<ArgumentNullException>(
            () => BowireDiscoveryEndpoints.NothingToDiscover(null!, serverUrl: ""));
    }
}
