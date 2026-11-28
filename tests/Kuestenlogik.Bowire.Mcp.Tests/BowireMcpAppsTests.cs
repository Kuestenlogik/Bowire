// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Mcp.Apps;
using Kuestenlogik.Bowire.Mcp.Tasks;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// #811 — Bowire's own MCP server answers an MCP Apps host with UI:
/// <c>bowire.test.run</c> and <c>bowire.discover</c> name their apps, the
/// <c>ui://</c> resources load nothing from anywhere, and a client that did
/// not declare the extension sees exactly what it saw before.
/// </summary>
public sealed class BowireMcpAppsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tasks = Directory.CreateTempSubdirectory("bowire-mcp-apps-").FullName;
    private readonly ReportingRunner _runner = new();
    private WebApplication _app = null!;
    private Uri _endpoint = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        builder.Services.AddSingleton<IBowireTestRunner>(_runner);
        builder.Services
            .AddBowireMcp(o => { o.LoadAllowlistFromEnvironments = false; o.TaskStorePath = _tasks; })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<BowireMcpTools>()
            .WithTools<BowireMcpTestTools>()
            .WithResources<BowireMcpResources>()
            .WithBowireMcpApps();
        _app = builder.Build();
        _app.MapBowireMcp("/mcp");
        await _app.StartAsync(Ct);
        _endpoint = new Uri($"http://127.0.0.1:{LoopbackHost.Port(_app.Services)}/mcp");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        try { Directory.Delete(_tasks, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private async Task<McpClient> ConnectAsync(bool hostsApps)
    {
        var options = hostsApps
            ? new McpClientOptions
            {
                Capabilities = new ClientCapabilities
                {
                    Extensions = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["io.modelcontextprotocol/ui"] = new JsonObject { ["mimeTypes"] = new JsonArray(BowireMcpApp.MimeType) },
                    },
                },
            }
            : null;
#pragma warning disable CA2000 // McpClient owns and disposes the transport.
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = _endpoint, TransportMode = HttpTransportMode.StreamableHttp });
#pragma warning restore CA2000
        return await McpClient.CreateAsync(transport, options, cancellationToken: Ct);
    }

    private static string? UiOf(McpClientTool tool) =>
        tool.ProtocolTool.Meta?["ui"]?["resourceUri"]?.GetValue<string>();

    [Fact]
    public async Task An_Apps_Host_Sees_Which_Tools_Have_An_App_And_Lists_The_Apps()
    {
        await using var client = await ConnectAsync(hostsApps: true);

        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name);
        Assert.Equal(BowireMcpApps.TestRunUri, UiOf(tools[BowireMcpTestTools.TestRunName]));
        Assert.Equal(BowireMcpApps.DiscoverUri, UiOf(tools["bowire.discover"]));
        Assert.Null(UiOf(tools["bowire.invoke"]));

        var resources = (await client.ListResourcesAsync(cancellationToken: Ct)).Select(r => r.Uri).ToList();
        Assert.Contains(BowireMcpApps.TestRunUri, resources);
        Assert.Contains(BowireMcpApps.DiscoverUri, resources);
    }

    [Fact]
    public async Task A_Client_Without_The_Extension_Sees_No_App_And_The_Server_Keeps_Its_Own()
    {
        await using (var plain = await ConnectAsync(hostsApps: false))
        {
            var tools = await plain.ListToolsAsync(cancellationToken: Ct);
            Assert.All(tools, t => Assert.Null(UiOf(t)));
            var resources = (await plain.ListResourcesAsync(cancellationToken: Ct)).Select(r => r.Uri).ToList();
            Assert.DoesNotContain(BowireMcpApps.TestRunUri, resources);
            Assert.DoesNotContain(BowireMcpApps.DiscoverUri, resources);
        }

        // Stripping went to copies: the next host still sees the app.
        await using var host = await ConnectAsync(hostsApps: true);
        var again = (await host.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == BowireMcpTestTools.TestRunName);
        Assert.Equal(BowireMcpApps.TestRunUri, UiOf(again));
    }

    [Theory]
    [InlineData(BowireMcpApps.TestRunUri)]
    [InlineData(BowireMcpApps.DiscoverUri)]
    public async Task An_App_Is_One_Html_Document_That_Loads_Nothing_And_Asks_For_Nothing(string uri)
    {
        await using var client = await ConnectAsync(hostsApps: true);

        var read = await client.ReadResourceAsync(new Uri(uri), cancellationToken: Ct);

        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal(BowireMcpApp.MimeType, contents.MimeType);
        Assert.Contains("BowireMcpAppView", contents.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(BowireMcpApp.ViewPlaceholder, contents.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src", contents.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", contents.Text, StringComparison.OrdinalIgnoreCase);
        // Two script elements, the view library and the app: nothing inside them ends one early.
        Assert.Equal(2, contents.Text.Split("</script>", StringSplitOptions.None).Length - 1);

        var csp = contents.Meta!["ui"]!["csp"]!.AsObject();
        Assert.All(["connectDomains", "resourceDomains", "frameDomains", "baseUriDomains"],
            key => Assert.Empty(csp[key]!.AsArray()));
        Assert.Null(contents.Meta["ui"]!["permissions"]);
    }

    [Fact]
    public async Task The_Test_Run_Answers_With_Its_Report_And_Re_Runs_One_Test_By_Name()
    {
        await using var client = await ConnectAsync(hostsApps: true);

        var result = await client.CallToolAsync(BowireMcpTestTools.TestRunName,
            new Dictionary<string, object?> { ["path"] = "suite.json", ["test"] = "Get user" }, cancellationToken: Ct);

        Assert.Equal("Get user", _runner.LastRequest!.Test);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("suite.json", doc.RootElement.GetProperty("path").GetString());
        var report = doc.RootElement.GetProperty("report");
        Assert.Equal("collection", report.GetProperty("kind").GetString());
        var step = Assert.Single(report.GetProperty("steps").EnumerateArray().ToList());
        Assert.Equal("Get user", step.GetProperty("name").GetString());
        Assert.False(step.GetProperty("passed").GetBoolean());
        Assert.Equal("$.id", step.GetProperty("checks")[0].GetProperty("subject").GetString());
    }

    private sealed class ReportingRunner : IBowireTestRunner
    {
        public BowireTestRunRequest? LastRequest { get; private set; }

        public Task<BowireTestRunOutcome> RunAsync(BowireTestRunRequest request, Func<string, Task> progress, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var report = new BowireTestRunReport("collection", "suite", 12, 1,
            [
                new BowireTestStepOutcome("Get user", "Users", "Get", "OK", 7, Passed: false, Skipped: false, null, 0, null,
                    [new BowireTestCheckOutcome("$.id", "eq", "1", "2", Passed: false, null)]),
            ]);
            return Task.FromResult(new BowireTestRunOutcome(1, "  ✗ Get user", "", report));
        }
    }
}
