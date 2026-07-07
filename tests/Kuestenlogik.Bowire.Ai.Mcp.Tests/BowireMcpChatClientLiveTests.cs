// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Ai.Mcp.Tests;

/// <summary>
/// Live-path coverage for <see cref="BowireMcpChatClient"/> against an
/// in-memory MCP server. The pure/no-network surface is covered in
/// <c>BowireMcpChatClientTests</c>; this exercises the parts that only run once
/// a real MCP endpoint answers: the lazy connect + tool discovery
/// (<c>EnsureConnectedAsync</c> / <c>FindChatTool</c>), the unary + streaming
/// success paths, and disposal of a connected client.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait", Justification = "Test scope")]
public sealed class BowireMcpChatClientLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetResponseAsync_ChatToolPresent_ReturnsToolTextAndModelId()
    {
        await using var server = await StartServerAsync<ChatTools>(Ct);
        await using var client = new BowireMcpChatClient(server.Url, "test-model");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: Ct);

        Assert.Contains("chat-response-sentinel", response.Text, StringComparison.Ordinal);
        Assert.Equal("test-model", response.ModelId);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ChatToolPresent_YieldsSingleChunk()
    {
        await using var server = await StartServerAsync<ChatTools>(Ct);
        await using var client = new BowireMcpChatClient(server.Url, modelId: "");

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct))
            chunks.Add(u);

        var chunk = Assert.Single(chunks);
        Assert.Contains("chat-response-sentinel", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_NoChatTool_Throws()
    {
        await using var server = await StartServerAsync<NonChatTools>(Ct);
        await using var client = new BowireMcpChatClient(server.Url, "m");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct));
    }

    // ---- in-memory MCP server ----

    private static async Task<McpServerHost> StartServerAsync<TTools>(CancellationToken ct) where TTools : class
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<TTools>();

        var app = builder.Build();
        app.MapMcp();
        await app.StartAsync(ct);
        var url = app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
        return new McpServerHost(app, url);
    }

    private sealed class McpServerHost(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [McpServerToolType]
    internal sealed class ChatTools
    {
        [McpServerTool(Name = "chat")]
        [Description("Test chat gateway tool.")]
        public static string Chat(string? model = null) => $"chat-response-sentinel model={model}";
    }

    [McpServerToolType]
    internal sealed class NonChatTools
    {
        [McpServerTool(Name = "ping")]
        [Description("A tool whose name doesn't read as a chat gateway.")]
        public static string Ping() => "pong";
    }
}
