// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the MCP tool-call-injection static-inventory probe (#400):
/// destructive-tool detection over the anonymously-listed tool set, the
/// name-token heuristic (snake_case / camelCase / dotted, and false-positive
/// avoidance), and the skip paths. Deterministic — driven through a fake MCP
/// protocol, no AI oracle, no live server.
/// </summary>
public sealed class McpToolInjectionProbeTests
{
    private static readonly string[] s_noAuth = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BowireServiceInfo Tools(params string[] toolNames)
        => new("Tools", "", toolNames.Select(n =>
            new BowireMethodInfo(n, n, false, false, new BowireMessageInfo("In", "In", []), new BowireMessageInfo("Out", "Out", []), "Unary")).ToList());

    [Fact]
    public async Task DestructiveTools_FlagsInjection()
    {
        var proto = new McpFake { Discover = () => [Tools("search", "delete_file", "runShell", "get_user")] };
        var f = Assert.Single(await new McpToolInjectionProbe().RunAsync("http://x/mcp", proto, s_noAuth, Ct));

        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API5-MCP-TOOL-INJECTION", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("CWE-862", f.Template.Recording.Vulnerability?.Cwe);
        Assert.Contains("delete_file", f.Detail, StringComparison.Ordinal);
        Assert.Contains("runShell", f.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("delete_file")]     // snake_case
    [InlineData("runShell")]        // camelCase hump
    [InlineData("Files.Delete")]    // dotted
    [InlineData("purgeCache")]
    [InlineData("execCommand")]
    [InlineData("create_user")]
    public async Task DestructiveNameShapes_AreFlagged(string toolName)
    {
        var proto = new McpFake { Discover = () => [Tools("search", toolName)] };
        var f = Assert.Single(await new McpToolInjectionProbe().RunAsync("http://x/mcp", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("get_user")]
    [InlineData("list_files")]
    [InlineData("read_document")]
    [InlineData("createdAt")]        // 'created' token != 'create' verb → no false positive
    [InlineData("summary")]
    public async Task NonDestructiveNames_ReportSafe(string toolName)
    {
        var proto = new McpFake { Discover = () => [Tools("fetch", toolName)] };
        var f = Assert.Single(await new McpToolInjectionProbe().RunAsync("http://x/mcp", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("NO-DESTRUCTIVE-TOOLS", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTools_Skips()
    {
        var proto = new McpFake { Discover = () => [] };
        var f = Assert.Single(await new McpToolInjectionProbe().RunAsync("http://x/mcp", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("MCP-NO-TOOLS", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverThrows_Skips()
    {
        var proto = new McpFake { Discover = () => throw new InvalidOperationException("gated") };
        var f = Assert.Single(await new McpToolInjectionProbe().RunAsync("http://x/mcp", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("MCP-UNREACHABLE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    private sealed class McpFake : IBowireProtocol
    {
        public Func<List<BowireServiceInfo>>? Discover { get; init; }
        public string Id => "mcp";
        public string Name => "mcp";
        public string IconSvg => "";
        public Task<List<BowireServiceInfo>> DiscoverAsync(string s, bool i, CancellationToken ct = default)
            => Discover is null ? throw new InvalidOperationException("not configured") : Task.FromResult(Discover());
        public Task<InvokeResult> InvokeAsync(string s, string sv, string m, List<string> j, bool i, Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", []));
        public async IAsyncEnumerable<string> InvokeStreamAsync(string s, string sv, string m, List<string> j, bool i, Dictionary<string, string>? md = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<IBowireChannel?> OpenChannelAsync(string s, string sv, string m, bool i, Dictionary<string, string>? md = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
