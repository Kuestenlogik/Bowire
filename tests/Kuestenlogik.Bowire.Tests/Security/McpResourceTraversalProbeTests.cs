// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for <see cref="McpResourceTraversalProbe"/> (rolls up to
/// <c>API1:2023 — Broken Object Level Authorization</c>, CWE-22 path traversal).
/// Driven through a self-contained <see cref="FakeProtocol"/> that scripts the
/// MCP discovery handshake and the per-URI resource reads, so the verdict logic
/// is exercised without a live MCP server.
/// </summary>
public sealed class McpResourceTraversalProbeTests
{
    private static readonly string[] s_noAuth = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BowireServiceInfo Service(string name, params string[] methodNames)
        => new(name, "", methodNames.Select(m => Method(m)).ToList());

    private static BowireMethodInfo Method(string name)
        => new(name, name, false, false,
            new BowireMessageInfo("In", "In", []), new BowireMessageInfo("Out", "Out", []), "Unary");

    [Fact]
    public async Task Traversal_ReadReturnsContent_FlagsApi1()
    {
        var invoked = new List<string>();
        var probe = new McpResourceTraversalProbe();
        var proto = new FakeProtocol
        {
            Discover = (_, _) => [Service("Resources", "file://a")],
            InvokeResource = uri =>
            {
                invoked.Add(uri);
                // First traversal URI succeeds with a populated contents array.
                return new InvokeResult("{\"contents\":[{\"text\":\"root:x:0:0:\"}]}", 2, "OK", []);
            },
        };

        var f = Assert.Single(await probe.RunAsync("http://x/mcp", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API1-MCP-TRAVERSAL", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("API1-2023-BOLA", f.Template.Recording.Vulnerability?.OwaspApi);
        Assert.Equal("CWE-22", f.Template.Recording.Vulnerability?.Cwe);
        // Stopped at the first hit — later traversal URIs were not tried.
        Assert.Single(invoked);
    }

    [Fact]
    public async Task Traversal_EveryReadRejected_ReportsBlocked()
    {
        var probe = new McpResourceTraversalProbe();
        var proto = new FakeProtocol
        {
            Discover = (_, _) => [Service("Resources", "file://a")],
            InvokeResource = _ => new InvokeResult(null, 1, "resource not found", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x/mcp", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API1-MCP-TRAVERSAL-BLOCKED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_Empty_Skips_AndNeverReads()
    {
        var probe = new McpResourceTraversalProbe();
        var proto = new FakeProtocol
        {
            Discover = (_, _) => [],
            InvokeResource = _ => throw new InvalidOperationException("resource read must not be attempted"),
        };

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API1-MCP-NOT-MCP", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Traversal_ResponseWithoutContents_TreatedAsRejected()
    {
        var probe = new McpResourceTraversalProbe();
        var proto = new FakeProtocol
        {
            Discover = (_, _) => [Service("Resources", "file://a")],
            // Non-null Response but no resource content: an empty contents array
            // or an error envelope. Must NOT produce a false Vulnerable.
            InvokeResource = uri => uri.Contains("win.ini", StringComparison.Ordinal)
                ? new InvokeResult("{\"isError\":true}", 1, "OK", [])
                : new InvokeResult("{\"contents\":[]}", 1, "OK", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x/mcp", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API1-MCP-TRAVERSAL-BLOCKED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_Throws_SkipsUnreachable()
    {
        var probe = new McpResourceTraversalProbe();
        var proto = new FakeProtocol
        {
            Discover = (_, _) => throw new InvalidOperationException("boom"),
            InvokeResource = _ => throw new InvalidOperationException("unreached"),
        };

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API1-MCP-UNREACHABLE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    // ---- self-contained fake ----

    private sealed class FakeProtocol : IBowireProtocol
    {
        public string Id => "mcp";
        public string Name => "mcp";
        public string IconSvg => "";

        /// <summary>Scripts the MCP discovery handshake result.</summary>
        public Func<string, bool, List<BowireServiceInfo>>? Discover { get; init; }

        /// <summary>Scripts a resource read keyed on the URI (the <c>method</c> arg).</summary>
        public Func<string, InvokeResult>? InvokeResource { get; init; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Discover is null ? throw new InvalidOperationException("Discover not configured") : Task.FromResult(Discover(serverUrl, showInternalServices));

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            if (InvokeResource is null) throw new InvalidOperationException("InvokeResource not configured");
            Assert.Equal("Resources", service);
            return Task.FromResult(InvokeResource(method));
        }

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
