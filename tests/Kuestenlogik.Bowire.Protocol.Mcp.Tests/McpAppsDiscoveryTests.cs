// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Protocol.Mcp.Tests;

/// <summary>
/// #810 — Bowire hosts MCP Apps: it says so to the server, and it marks the
/// tools that come with one.
/// </summary>
public sealed class McpAppsDiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_Marks_A_Tool_With_A_Ui_Resource_And_Leaves_The_Others()
    {
        await using var server = await AppsServer.StartAsync(Ct);

        var services = await new BowireMcpProtocol().DiscoverAsync(server.Url, showInternalServices: false, Ct);

        var tools = Assert.Single(services, s => s.Name == "Tools").Methods.ToDictionary(m => m.Name);
        Assert.Equal("ui://test/board.html", tools["board"].UiResourceUri);
        Assert.Equal("ui://test/legacy.html", tools["legacy_board"].UiResourceUri);
        Assert.Null(tools["capabilities"].UiResourceUri);
    }

    [Fact]
    public async Task Discovery_Announces_The_Apps_Extension_With_Its_Mime_Type()
    {
        await using var server = await AppsServer.StartAsync(Ct);

        await new BowireMcpProtocol().DiscoverAsync(server.Url, showInternalServices: false, Ct);

        var mimeTypes = server.ListedFor!["extensions"]![BowireMcpProtocol.AppsExtensionId]!["mimeTypes"]!.AsArray();
        Assert.Equal(BowireMcpProtocol.AppsMimeType, Assert.Single(mimeTypes)!.GetValue<string>());
    }

    [Fact]
    public async Task A_Call_Declares_No_Session_Extension_So_Tasks_Keep_Their_Opt_In()
    {
        // csharp-sdk#1920: a session extension would overwrite the per-request
        // Tasks opt-in (#801), so the Apps extension stays with discovery.
        await using var server = await AppsServer.StartAsync(Ct);

        var result = await new BowireMcpProtocol().InvokeAsync(server.Url, "Tools", "capabilities", ["{}"], false, null, Ct);

        using var doc = JsonDocument.Parse(result.Response!);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Null(JsonNode.Parse(text)?["extensions"]?[BowireMcpProtocol.AppsExtensionId]);
    }

    [Theory]
    [InlineData("""{"ui":{"resourceUri":"ui://a/b.html"}}""", "ui://a/b.html")]
    [InlineData("""{"ui/resourceUri":"ui://a/legacy.html"}""", "ui://a/legacy.html")]
    [InlineData("""{"ui":{"resourceUri":"ui://a/new.html"},"ui/resourceUri":"ui://a/old.html"}""", "ui://a/new.html")]
    [InlineData("""{"ui":{"resourceUri":"  "}}""", null)]
    [InlineData("""{"ui":{"resourceUri":42}}""", null)]
    [InlineData("""{"other":true}""", null)]
    public void UiResourceUriOf_Reads_The_Nested_Then_The_Flat_Form(string meta, string? expected)
        => Assert.Equal(expected, BowireMcpProtocol.UiResourceUriOf(JsonNode.Parse(meta)!.AsObject()));

    [Fact]
    public void UiResourceUriOf_Takes_No_Meta() => Assert.Null(BowireMcpProtocol.UiResourceUriOf(null));

    /// <summary>
    /// Lists its App tools only to a client that announced the extension, the
    /// way a server offering a tool with and without a UI does.
    /// </summary>
    private sealed class AppsServer(WebApplication app, string url, Func<JsonNode?> listedFor) : IAsyncDisposable
    {
        public string Url { get; } = url;

        /// <summary>The client capabilities the last tools/list came with.</summary>
        public JsonNode? ListedFor => listedFor();

        public static async Task<AppsServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Logging.ClearProviders();
            JsonNode? seen = null;
            builder.Services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (ctx, token) =>
                {
                    var capabilities = ctx.Server.ClientCapabilities;
                    seen = capabilities is null ? null : JsonSerializer.SerializeToNode(capabilities, McpJsonOptions);
                    var result = await next(ctx, token);
                    if (capabilities?.Extensions?.ContainsKey(BowireMcpProtocol.AppsExtensionId) != true)
                        result.Tools = [.. result.Tools.Where(t => t.Meta is null)];
                    return result;
                }))
                .WithTools(
                [
                    McpServerTool.Create(() => "board", new McpServerToolCreateOptions
                    {
                        Name = "board",
                        Meta = new JsonObject { ["ui"] = new JsonObject { ["resourceUri"] = "ui://test/board.html" } },
                    }),
                    McpServerTool.Create(() => "legacy", new McpServerToolCreateOptions
                    {
                        Name = "legacy_board",
                        Meta = new JsonObject { ["ui/resourceUri"] = "ui://test/legacy.html" },
                    }),
                    McpServerTool.Create(
                        (McpServer mcp) => JsonSerializer.Serialize(mcp.ClientCapabilities, McpJsonOptions),
                        new McpServerToolCreateOptions { Name = "capabilities" }),
                ]);
            var app = builder.Build();
            app.MapMcp();
            await app.StartAsync(ct);
            return new AppsServer(app, app.Urls.First(), () => seen);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static JsonSerializerOptions McpJsonOptions => ModelContextProtocol.McpJsonUtilities.DefaultOptions;
}
