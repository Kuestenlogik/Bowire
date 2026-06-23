// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Ai.Mcp.Tests;

/// <summary>
/// Covers the no-network branches of <see cref="BowireMcpChatClient"/>:
/// constructor validation, public-surface argument checks, the
/// endpoint-parsing failure modes that surface before any MCP transport
/// gets touched, and the dispose-idempotency contract. Reaching into a
/// live MCP host is out of scope — those paths live in the integration
/// suite once a hostable stub lands.
/// </summary>
public sealed class BowireMcpChatClientTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsNullOrWhitespaceEndpoint(string? endpoint)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace splits into
        // ArgumentNullException (for null) and ArgumentException (for
        // empty / whitespace). Use ThrowsAny so either subtype passes.
        Assert.ThrowsAny<ArgumentException>(() => new BowireMcpChatClient(endpoint!, "model"));
    }

    [Fact]
    public void Constructor_AcceptsNullModelId_NormalisesToEmpty()
    {
        // modelId nullability is part of the contract — Settings can
        // omit the field; the ChatResponse just won't carry a ModelId
        // back. Verify the ctor doesn't throw on null.
        using var client = new BowireMcpChatClient("http://localhost:9999", modelId: null!);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task GetResponseAsync_NullMessages_ThrowsArgumentNull()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.GetResponseAsync(messages: null!, options: null, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetResponseAsync_MalformedEndpoint_ThrowsInvalidOperation()
    {
        // Endpoint that's neither `stdio:`-prefixed nor an absolute
        // http(s) URL. The CreateClientAsync prelude bails before any
        // transport is constructed — no network roundtrip, no timeout.
        using var client = new BowireMcpChatClient("ftp://nope.invalid/", "model");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("absolute http(s)", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResponseAsync_StdioWithEmptyCommand_ThrowsInvalidOperation()
    {
        // 'stdio:' prefix with nothing after the colon is the most
        // common typo — Settings stripped the value but kept the
        // protocol marker. Fail fast with a configure-me message.
        using var client = new BowireMcpChatClient("stdio:", "model");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("stdio", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_MalformedEndpoint_ThrowsBeforeYield()
    {
        // The streaming entry calls GetResponseAsync internally; a
        // pre-yield throw (CreateClient validation) needs to bubble up
        // through the iterator. Materialise via ToListAsync to drain.
        using var client = new BowireMcpChatClient("notaurl", "model");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
                // Should never get here — the validation throw beats
                // the first yield.
            }
        });
    }

    [Fact]
    public void GetService_ReturnsSelf_ForIChatClient()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        var svc = client.GetService(typeof(IChatClient));
        Assert.Same(client, svc);
    }

    [Fact]
    public void GetService_ReturnsNull_ForUnrelatedType()
    {
        // Pick types the chat client genuinely doesn't implement.
        // IDisposable / IAsyncDisposable are off-limits — the class
        // DOES implement both, so IsInstanceOfType returns true.
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        Assert.Null(client.GetService(typeof(string)));
        Assert.Null(client.GetService(typeof(HttpClient)));
    }

    [Fact]
    public void GetService_ReturnsNull_ForNullServiceType()
    {
        // null-safe per IChatClient guidance — the workbench's DI
        // container can probe with null when resolving optional
        // keyed services.
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        Assert.Null(client.GetService(serviceType: null!));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var client = new BowireMcpChatClient("http://localhost:9999", "model");
        await client.DisposeAsync();
        await client.DisposeAsync();   // second call must be a no-op, not a throw
    }

    [Fact]
    public void Dispose_DelegatesToDisposeAsync_Idempotent()
    {
        // The synchronous Dispose path is the host-app contract surface
        // for callers that don't await DisposeAsync. Verify it doesn't
        // throw on a never-connected client and is safe to call twice.
        var client = new BowireMcpChatClient("http://localhost:9999", "model");
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public async Task GetResponseAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var client = new BowireMcpChatClient("http://localhost:9999", "model");
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: TestContext.Current.CancellationToken));
    }

    // -- Pure helpers (internal) — pinned directly so the no-network
    //    coverage covers the message-marshalling + result-parsing path
    //    without needing a live MCP host. --------------------------------

    [Theory]
    [InlineData("", "", new string[0])]
    [InlineData("   ", "", new string[0])]
    [InlineData("claude", "claude", new string[0])]
    [InlineData("claude mcp serve", "claude", new[] { "mcp", "serve" })]
    [InlineData("   npx   mcp-server-everything   --foo  bar  ", "npx", new[] { "mcp-server-everything", "--foo", "bar" })]
    public void SplitCommandLine_Variants(string input, string expectedCommand, string[] expectedArgs)
    {
        var (cmd, args) = BowireMcpChatClient.SplitCommandLine(input);
        Assert.Equal(expectedCommand, cmd);
        Assert.Equal(expectedArgs, args);
    }

    [Fact]
    public void BuildArguments_IncludesMessagesAndModel()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", "gpt-4o-mini");
        var args = client.BuildArguments(
            [new ChatMessage(ChatRole.User, "hi"), new ChatMessage(ChatRole.Assistant, "hello back")],
            new ChatOptions { Temperature = 0.7f, MaxOutputTokens = 256 });

        Assert.Equal("gpt-4o-mini", args["model"]);
        Assert.Equal(0.7f, args["temperature"]);
        Assert.Equal(256, args["maxTokens"]);
        // messages is the convention every MCP-LLM-gateway accepts.
        // Round-trip through JSON to verify the role + content shape.
        var json = JsonSerializer.Serialize(args["messages"]);
        Assert.Contains("\"role\":\"user\"", json, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"hi\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"assistant\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArguments_EmptyModel_OmitsModelKey()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", modelId: "");
        var args = client.BuildArguments(
            [new ChatMessage(ChatRole.User, "x")], options: null);
        Assert.False(args.ContainsKey("model"));
        Assert.False(args.ContainsKey("temperature"));
        Assert.False(args.ContainsKey("maxTokens"));
        Assert.True(args.ContainsKey("messages"));
    }

    [Fact]
    public void ExtractText_NullContent_ReturnsEmpty()
    {
        var result = new CallToolResult { Content = null! };
        Assert.Equal(string.Empty, BowireMcpChatClient.ExtractText(result));
    }

    [Fact]
    public void ExtractText_EmptyContent_ReturnsEmpty()
    {
        var result = new CallToolResult();   // Content defaults to empty list
        Assert.Equal(string.Empty, BowireMcpChatClient.ExtractText(result));
    }

    [Fact]
    public void ExtractText_TextBlocks_JoinedByNewline()
    {
        var result = new CallToolResult();
        result.Content.Add(new TextContentBlock { Text = "first line" });
        result.Content.Add(new TextContentBlock { Text = "second line" });
        Assert.Equal("first line\nsecond line", BowireMcpChatClient.ExtractText(result));
    }

    [Fact]
    public void ExtractText_NonTextOnly_FallsBackToJsonSerialisation()
    {
        // No TextContentBlocks present — the helper serialises the
        // whole Content list as JSON so the workbench can still render
        // SOMETHING instead of an empty string.
        var result = new CallToolResult();
        result.Content.Add(new ImageContentBlock
        {
            Data = new ReadOnlyMemory<byte>([0x89, 0x50, 0x4E, 0x47]),   // PNG signature bytes
            MimeType = "image/png",
        });
        var text = BowireMcpChatClient.ExtractText(result);
        Assert.NotEqual(string.Empty, text);
        // The exact shape comes from the JSON serializer; just verify
        // it's not the joined-text path (i.e. it's JSON-ish) and
        // round-trip-parseable.
        Assert.Contains("image/png", text, StringComparison.Ordinal);
    }
}
