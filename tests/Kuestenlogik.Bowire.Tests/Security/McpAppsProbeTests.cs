// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// #812 — <see cref="McpAppsProbe"/>: what an MCP App's declaration gets
/// reported for. A scripted protocol plays the server: discovery lists tools
/// with their apps, a resource read returns the app's contents.
/// </summary>
public sealed class McpAppsProbeTests
{
    private static readonly string[] s_noAuth = [];
    private static readonly string[] s_anyOrigin = ["*"];
    private static readonly string[] s_cdnAndEval = ["https://cdn.example.com", "'unsafe-eval'"];
    private static readonly string[] s_api = ["https://api.example.com"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BowireMethodInfo Tool(string name, string? ui = null, bool destructive = false, IReadOnlyList<string>? visibility = null)
        => new(name, name, false, true, new BowireMessageInfo("In", "In", []), new BowireMessageInfo("Out", "Out", []), "ServerStreaming")
        {
            UiResourceUri = ui,
            Destructive = destructive,
            UiVisibility = visibility,
        };

    private static List<BowireServiceInfo> Tools(params BowireMethodInfo[] tools) => [new("Tools", "", [.. tools])];

    private static InvokeResult App(object meta, string html = "<p>app</p>") =>
        new(JsonSerializer.Serialize(new { contents = new[] { new { uri = "ui://x/app.html", text = html, _meta = meta } } }), 1, "OK", []);

    private static async Task<IReadOnlyList<ScanFinding>> ScanAsync(List<BowireServiceInfo> discovered, Func<string, InvokeResult> read)
        => await new McpAppsProbe().RunAsync("http://x/mcp", new FakeProtocol { Discover = (_, _) => discovered, InvokeResource = read }, s_noAuth, Ct);

    private static ScanFinding Only(IReadOnlyList<ScanFinding> findings, string id) =>
        Assert.Single(findings, f => f.Template.Recording.Vulnerability?.Id == id);

    [Fact]
    public async Task A_Csp_That_Allows_Any_Origin_Is_Reported_With_The_Tool_And_The_Directive()
    {
        var findings = await ScanAsync(Tools(Tool("board", "ui://x/app.html")),
            _ => App(new { ui = new { csp = new { connectDomains = s_anyOrigin } } }));

        var f = Only(findings, "BWR-OWASP-API8-MCP-APP-CSP-ANY-ORIGIN");
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("API8-2023-SECMISCONF", f.Template.Recording.Vulnerability?.OwaspApi);
        Assert.Contains("board", f.Detail, StringComparison.Ordinal);
        Assert.Contains("connect-src", f.Detail, StringComparison.Ordinal);
        Assert.Contains("`*`", f.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("https:", true)]
    [InlineData("https://*", true)]
    [InlineData("wss:", true)]
    [InlineData("https://api.example.com", false)]
    [InlineData("https://*.example.com", false)]
    public void What_Counts_As_Any_Origin(string value, bool any) => Assert.Equal(any, McpAppsProbe.IsAnyOrigin(value));

    [Fact]
    public async Task Eval_Sensitive_Permissions_And_An_Undeclared_Script_Are_Each_Reported()
    {
        var findings = await ScanAsync(Tools(Tool("board", "ui://x/app.html")),
            _ => App(new
            {
                ui = new
                {
                    csp = new { resourceDomains = s_cdnAndEval },
                    permissions = new { camera = new { }, clipboardWrite = new { } },
                },
            }, html: "<script src=\"https://cdn.example.com/a.js\"></script><script src='https://evil.example.net/b.js'></script>"));

        Assert.Contains("'unsafe-eval'", Only(findings, "BWR-OWASP-API8-MCP-APP-CSP-UNSAFE").Detail, StringComparison.Ordinal);
        var permissions = Only(findings, "BWR-OWASP-API8-MCP-APP-PERMISSIONS").Detail;
        Assert.Contains("camera", permissions, StringComparison.Ordinal);
        Assert.DoesNotContain("clipboardWrite", permissions, StringComparison.Ordinal);
        var script = Only(findings, "BWR-OWASP-API8-MCP-APP-UNDECLARED-SCRIPT").Detail;
        Assert.Contains("https://evil.example.net", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example.com,", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Destructive_Tool_Open_To_Apps_Is_Reported_And_One_For_The_Model_Only_Is_Not()
    {
        var findings = await ScanAsync(
            Tools(Tool("board", "ui://x/app.html"), Tool("delete_all", destructive: true), Tool("drop_db", destructive: true, visibility: ["model"])),
            _ => App(new { ui = new { csp = new { } } }));

        var f = Only(findings, "BWR-OWASP-API8-MCP-APP-DESTRUCTIVE-TOOL");
        Assert.Contains("delete_all", f.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("drop_db", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Narrow_App_Is_Safe()
    {
        var findings = await ScanAsync(Tools(Tool("board", "ui://x/app.html")),
            _ => App(new { ui = new { csp = new { connectDomains = s_api } } }));

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
    }

    [Fact]
    public async Task A_Server_Without_Apps_Skips_And_Reads_Nothing()
    {
        var findings = await ScanAsync(Tools(Tool("echo")), _ => throw new InvalidOperationException("no read expected"));

        Assert.Equal(ScanFindingStatus.Skipped, Assert.Single(findings).Status);
    }

    [Fact]
    public async Task An_App_Whose_Resource_Cannot_Be_Read_Is_Skipped_Not_Passed()
    {
        var findings = await ScanAsync(Tools(Tool("board", "ui://x/app.html")), _ => new InvokeResult(null, 1, "not found", []));

        Assert.Contains(findings, f => f.Status == ScanFindingStatus.Skipped);
        Assert.DoesNotContain(findings, f => f.Status == ScanFindingStatus.Safe);
    }

    private sealed class FakeProtocol : IBowireProtocol
    {
        public string Id => "mcp";
        public string Name => "mcp";
        public string IconSvg => "";
        public Func<string, bool, List<BowireServiceInfo>>? Discover { get; init; }
        public Func<string, InvokeResult>? InvokeResource { get; init; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            // Explicit, not `Discover!`: the CLI's plugin scan instantiates every
            // IBowireProtocol in this assembly and calls it unconfigured.
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
