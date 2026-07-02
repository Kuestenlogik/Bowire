// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
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
    public void ExtractText_MixedTextAndNonText_JoinsOnlyTextBlocks()
    {
        // The TextContentBlock filter is non-empty AND TextContentBlock,
        // so image blocks interleaved with text blocks fall out of the
        // joined-text path — only the genuine text survives.
        var result = new CallToolResult();
        result.Content.Add(new TextContentBlock { Text = "intro line" });
        result.Content.Add(new ImageContentBlock
        {
            Data = new ReadOnlyMemory<byte>([0x89, 0x50, 0x4E, 0x47]),
            MimeType = "image/png",
        });
        result.Content.Add(new TextContentBlock { Text = "trailing line" });
        Assert.Equal("intro line\ntrailing line", BowireMcpChatClient.ExtractText(result));
    }

    [Fact]
    public void ExtractText_EmptyStringTextBlocks_FallsBackToJsonSerialisation()
    {
        // Empty / whitespace TextContentBlocks fail the
        // !string.IsNullOrEmpty filter so the joined-text path emits
        // nothing — the helper then falls back to the JSON snapshot
        // of the raw Content list rather than returning an
        // unhelpful empty string.
        var result = new CallToolResult();
        result.Content.Add(new TextContentBlock { Text = string.Empty });
        result.Content.Add(new TextContentBlock { Text = null! });
        var text = BowireMcpChatClient.ExtractText(result);
        Assert.NotEqual(string.Empty, text);
        Assert.StartsWith("[", text, StringComparison.Ordinal);   // JSON array shape
    }

    // -- FindChatTool — only the empty-list path is reachable from
    //    test code (McpClientTool is sealed + needs an internal McpClient
    //    to construct, neither of which we can fake without the SDK).
    //    Empty list still exercises the outer foreach + early-out, which
    //    is the path coverlet was missing. --

    [Fact]
    public void FindChatTool_EmptyToolList_ReturnsNull()
    {
        var result = BowireMcpChatClient.FindChatTool(new List<McpClientTool>());
        Assert.Null(result);
    }

    // -- More BuildArguments edge cases for tighter branch coverage --

    [Fact]
    public void BuildArguments_OnlyTemperature_OmitsMaxTokens()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", "m");
        var args = client.BuildArguments(
            [new ChatMessage(ChatRole.User, "x")],
            new ChatOptions { Temperature = 0.5f });
        Assert.True(args.ContainsKey("temperature"));
        Assert.False(args.ContainsKey("maxTokens"));
    }

    [Fact]
    public void BuildArguments_OnlyMaxTokens_OmitsTemperature()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", "m");
        var args = client.BuildArguments(
            [new ChatMessage(ChatRole.User, "x")],
            new ChatOptions { MaxOutputTokens = 100 });
        Assert.False(args.ContainsKey("temperature"));
        Assert.True(args.ContainsKey("maxTokens"));
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

    // -- GetService positive paths — the sync GetService returns `this`
    //    for every type the client is assignable to (IsInstanceOfType).
    //    The existing suite pins the null/unrelated-type negatives; these
    //    pin the assignable-type positives + the ignored-serviceKey
    //    branch so the whole conditional is exercised. --

    [Fact]
    public void GetService_ReturnsSelf_ForIDisposable()
    {
        // The class implements IDisposable, so IsInstanceOfType is true
        // and the host's DI container can resolve it as a disposable.
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        Assert.Same(client, client.GetService(typeof(IDisposable)));
    }

    [Fact]
    public void GetService_ReturnsNull_ForIAsyncDisposable()
    {
        // Subtle: the client exposes a public DisposeAsync but does NOT
        // declare IAsyncDisposable — IChatClient only extends IDisposable.
        // So an IAsyncDisposable probe is genuinely unassignable and the
        // GetService type check returns null, not self.
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        Assert.Null(client.GetService(typeof(IAsyncDisposable)));
    }

    [Fact]
    public void GetService_ReturnsSelf_ForConcreteType()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        Assert.Same(client, client.GetService(typeof(BowireMcpChatClient)));
    }

    [Fact]
    public void GetService_ReturnsSelf_ForObjectType()
    {
        // Everything is instance-of object, so the client answers a
        // typeof(object) probe with itself rather than null.
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        Assert.Same(client, client.GetService(typeof(object)));
    }

    [Fact]
    public void GetService_IgnoresServiceKey_StillReturnsSelf()
    {
        // The adapter has no keyed services; a non-null serviceKey is
        // ignored and the matching-type probe still resolves to self.
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        Assert.Same(client, client.GetService(typeof(IChatClient), serviceKey: "any-key"));
    }

    // -- Streaming surface — mirrors the non-streaming argument /
    //    disposal / endpoint-shape guards, but reached through the async
    //    iterator so the guards must survive the deferred-enumeration
    //    boundary rather than throwing synchronously. --

    [Fact]
    public async Task GetStreamingResponseAsync_NullMessages_ThrowsArgumentNull()
    {
        // The iterator forwards to GetResponseAsync, whose null-check is
        // captured in the returned task and only surfaces once the
        // stream is drained.
        using var client = new BowireMcpChatClient("http://localhost:9999", "model");
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                messages: null!, cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task GetStreamingResponseAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var client = new BowireMcpChatClient("http://localhost:9999", "model");
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "x")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StdioEmptyCommand_ThrowsInvalidOperation()
    {
        using var client = new BowireMcpChatClient("stdio:", "model");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                cancellationToken: TestContext.Current.CancellationToken))
            {
            }
        });
        Assert.Contains("command line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -- Endpoint-shape parsing — extra failure modes that bail in the
    //    CreateClientAsync prelude before any transport / process is
    //    touched (still fully offline). --

    [Fact]
    public async Task GetResponseAsync_UppercaseStdioMarker_EmptyCommand_Throws()
    {
        // The 'stdio:' marker match is OrdinalIgnoreCase — an uppercased
        // marker still routes into the stdio branch and fails on the
        // empty command line rather than falling through to the URL
        // parser.
        using var client = new BowireMcpChatClient("STDIO:", "model");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("command line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResponseAsync_StdioWhitespaceCommand_Throws()
    {
        // Whitespace after the marker Trim()s to empty, hitting the same
        // configure-me guard as a bare 'stdio:'.
        using var client = new BowireMcpChatClient("stdio:    ", "model");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("command line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("some/relative/path")]   // not absolute — Uri.TryCreate(Absolute) fails
    [InlineData("ws://localhost/mcp")]   // absolute but non-http(s) scheme
    [InlineData("mailto:nope@example.com")]
    public async Task GetResponseAsync_NonHttpNonStdioEndpoint_ThrowsInvalidOperation(string endpoint)
    {
        using var client = new BowireMcpChatClient(endpoint, "model");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("absolute http(s)", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -- Dispose ordering — the existing suite pins same-entry
    //    idempotency (Dispose×2, DisposeAsync×2); these pin the mixed
    //    ordering so the shared _disposed guard is exercised across both
    //    entry points. --

    [Fact]
    public async Task DisposeAsync_ThenDispose_Idempotent()
    {
        var client = new BowireMcpChatClient("http://localhost:9999", "model");
        await client.DisposeAsync();
        // Deliberately exercising the sync Dispose entry after async
        // teardown — CA1849's prefer-async guidance doesn't apply when
        // the sync path is the unit under test.
#pragma warning disable CA1849
        client.Dispose();   // must be a no-op, not a throw
#pragma warning restore CA1849
    }

    [Fact]
    public async Task Dispose_ThenDisposeAsync_Idempotent()
    {
        var client = new BowireMcpChatClient("http://localhost:9999", "model");
#pragma warning disable CA1849
        client.Dispose();
#pragma warning restore CA1849
        await client.DisposeAsync();   // async entry after sync teardown must be a no-op
    }

    // -- BuildArguments — empty conversation still carries a (empty)
    //    messages key so the upstream tool sees a well-formed argument
    //    envelope. --

    [Fact]
    public void BuildArguments_EmptyMessages_EmitsEmptyMessagesArray()
    {
        using var client = new BowireMcpChatClient("http://localhost:9999", "m");
        var args = client.BuildArguments(Array.Empty<ChatMessage>(), options: null);
        Assert.True(args.ContainsKey("messages"));
        var messages = Assert.IsType<object[]>(args["messages"], exactMatch: false);
        Assert.Empty(messages);
    }

    [Fact]
    public void Constructor_AcceptsStdioEndpoint_LazyConnect()
    {
        // Construction stays cheap for both endpoint shapes; the stdio
        // command line isn't split / spawned until the first chat call.
        using var client = new BowireMcpChatClient("stdio:claude mcp serve", "model");
        Assert.NotNull(client);
    }
}
