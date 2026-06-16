// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kuestenlogik.Bowire.Ai.Mcp;

/// <summary>
/// <see cref="IChatClient"/> backed by the user's MCP host (#25 Phase 4 —
/// MCP-client reversal). Bowire becomes the MCP client; the upstream
/// MCP host carries the model affinity, auth, and rate-limiting the
/// user has already configured for their other tools (Claude Desktop,
/// Cursor, a corporate gateway).
/// </summary>
/// <remarks>
/// <para>
/// <b>How chat lands as an MCP call.</b> Standard MCP exposes
/// <c>tools</c> rather than a chat-completion verb, so this adapter
/// scans the upstream's tool list for the first one whose name reads as
/// a chat / completion / sampling gateway — <c>chat</c>, <c>completion</c>
/// / <c>complete</c>, <c>llm</c>, <c>generate</c>, <c>sampling</c> are all
/// matched case-insensitively. Each <see cref="GetResponseAsync"/> call
/// serialises the conversation into the tool's argument dictionary
/// (<c>messages</c> + optional <c>model</c>) and parses the tool's text
/// content as the assistant reply.
/// </para>
/// <para>
/// <b>Endpoint shape.</b> The
/// <see cref="BowireAiOptions.Endpoint"/> string is either an absolute
/// HTTP(S) URL — taken as a Streamable-HTTP / SSE MCP endpoint — or a
/// <c>stdio:</c> prefix followed by a command line spawned via stdio
/// (<c>stdio:claude mcp serve</c>). Empty / malformed endpoints
/// surface as an <see cref="InvalidOperationException"/> from the
/// first chat call.
/// </para>
/// <para>
/// <b>Connection lifecycle.</b> The MCP client is established lazily on
/// the first call and held for the lifetime of this instance. The
/// <see cref="BowireAiRuntime"/> disposes the chat client on hot-swap
/// and shutdown; that flows into <see cref="DisposeAsync"/> here which
/// closes the MCP client + transport. No connection pooling — Settings-
/// UI saves rebuild the client.
/// </para>
/// </remarks>
public sealed class BowireMcpChatClient : IChatClient
{
    private static readonly JsonSerializerOptions s_messageJson = new(JsonSerializerDefaults.Web);

    private static readonly string[] s_chatToolNameTokens =
        ["chat", "completion", "complete", "llm", "generate", "sampling"];

    private readonly string _endpoint;
    private readonly string _modelId;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private McpClient? _client;
    private McpClientTool? _chatTool;
    private bool _disposed;

    public BowireMcpChatClient(string endpoint, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        _endpoint = endpoint;
        _modelId = modelId ?? string.Empty;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (client, tool) = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var arguments = BuildArguments(messages, options);
        var result = await client
            .CallToolAsync(tool.Name, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var text = ExtractText(result);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = string.IsNullOrEmpty(_modelId) ? null : _modelId,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // MCP CallTool is unary; we surface the response as a single
        // chunk so callers iterating the stream still terminate cleanly.
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Text)
            {
                ModelId = response.ModelId,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
        // The synchronous path delegates to DisposeAsync so the
        // SemaphoreSlim and transport teardown both run regardless of
        // which dispose entry the host calls. CA1816 — suppress the
        // sync-only Dispose enforcer; DisposeAsync is canonical.
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client is not null)
        {
            try { await _client.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort teardown */ }
        }
        _initLock.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Surfacing the upstream error verbatim — the chat path translates this into a 502 the workbench renders.")]
    private async Task<(McpClient Client, McpClientTool Tool)> EnsureConnectedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null && _chatTool is not null) return (_client, _chatTool);

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null && _chatTool is not null) return (_client, _chatTool);

            _client ??= await CreateClientAsync(_endpoint, ct).ConfigureAwait(false);

            IList<McpClientTool> tools;
            try
            {
                tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Bowire AI: MCP host at '{_endpoint}' rejected the tools/list request — {ex.Message}", ex);
            }

            _chatTool = FindChatTool(tools)
                ?? throw new InvalidOperationException(
                    $"Bowire AI: the MCP host at '{_endpoint}' exposes no tool whose name reads as a chat / completion gateway. "
                    + "Configure your MCP host to expose a tool named 'chat', 'completion', 'llm', 'generate', or 'sampling' "
                    + "that accepts a 'messages' argument.");

            return (_client, _chatTool);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static McpClientTool? FindChatTool(IList<McpClientTool> tools)
    {
        foreach (var token in s_chatToolNameTokens)
        {
            foreach (var tool in tools)
            {
                if (tool.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return tool;
            }
        }
        return null;
    }

    private Dictionary<string, object?> BuildArguments(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        // The upstream tool's argument shape is intentionally
        // unspecified — every MCP-host-as-LLM-gateway in the wild
        // invents its own. We send the convention every wrapper we've
        // seen accepts ({ messages: [...], model?, temperature?,
        // maxTokens? }); the server is expected to ignore unknown keys.
        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messages"] = messages.Select(m => new
            {
                role = m.Role.Value,
                content = m.Text,
            }).ToArray(),
        };

        if (!string.IsNullOrEmpty(_modelId)) args["model"] = _modelId;
        if (options?.Temperature is float t) args["temperature"] = t;
        if (options?.MaxOutputTokens is int max) args["maxTokens"] = max;

        return args;
    }

    private static string ExtractText(CallToolResult result)
    {
        // The MCP content schema is a discriminated union; we ignore
        // image / resource blocks since the chat surface expects prose.
        if (result.Content is null || result.Content.Count == 0)
            return string.Empty;

        var parts = new List<string>(result.Content.Count);
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text && !string.IsNullOrEmpty(text.Text))
                parts.Add(text.Text);
        }
        return parts.Count == 0
            ? JsonSerializer.Serialize(result.Content, s_messageJson)
            : string.Join("\n", parts);
    }

    private static async Task<McpClient> CreateClientAsync(string endpoint, CancellationToken ct)
    {
        if (endpoint.StartsWith("stdio:", StringComparison.OrdinalIgnoreCase))
            return await CreateStdioClientAsync(endpoint[6..].Trim(), ct).ConfigureAwait(false);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Bowire AI: MCP endpoint '{endpoint}' is neither an absolute http(s) URL nor a 'stdio:'-prefixed command.");
        }

        return await CreateHttpClientAsync(uri, ct).ConfigureAwait(false);
    }

    private static async Task<McpClient> CreateHttpClientAsync(Uri endpoint, CancellationToken ct)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.AutoDetect,
        };

#pragma warning disable CA2000 // CA2000 doesn't track ownership hand-off across awaits.
        var transport = new HttpClientTransport(options);
#pragma warning restore CA2000
        try
        {
            return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<McpClient> CreateStdioClientAsync(string commandLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            throw new InvalidOperationException(
                "Bowire AI: MCP endpoint 'stdio:' prefix requires a command line after the colon (e.g. 'stdio:claude mcp serve').");

        var (command, arguments) = SplitCommandLine(commandLine);
        var options = new StdioClientTransportOptions
        {
            Name = command,
            Command = command,
            Arguments = arguments,
        };

        // StdioClientTransport isn't IDisposable in the public SDK
        // surface — the child-process lifetime is owned by McpClient
        // once CreateAsync hands ownership over. On the rare throw
        // before hand-off the process is collected when the transport
        // goes out of scope; we don't have a dispose seam to call.
        var transport = new StdioClientTransport(options);
        return await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
    }

    private static (string Command, string[] Arguments) SplitCommandLine(string commandLine)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0
            ? (string.Empty, [])
            : (parts[0], parts.Length > 1 ? parts[1..] : []);
    }
}
