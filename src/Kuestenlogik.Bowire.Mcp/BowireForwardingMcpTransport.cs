// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol.Client;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// MCP-over-MCP forwarder (#286). Wraps an outbound <see cref="McpClient"/>
/// connected to a parent Bowire process's MCP endpoint so an incoming
/// JSON-RPC request handled by this server is marshalled to the parent
/// verbatim and the parent's response is relayed back to the caller.
/// </summary>
/// <remarks>
/// <para>
/// Use case: a remote LLM agent talks to a thin Bowire process (e.g. on a
/// CI runner or in a container) that delegates the actual work to a heavier
/// Bowire running on the operator's workstation. <c>bowire mcp serve
/// --attach &lt;parent-addr&gt;</c> boots the server in forwarder mode —
/// no tools registered locally, every incoming MCP request is forwarded
/// to the parent.
/// </para>
/// <para>
/// The connection to the parent is established lazily on the first
/// handler call so a child can boot even when the parent isn't reachable
/// yet (e.g. the parent starts a moment later). Failures surface as MCP
/// errors back to the child's caller; the connection is retried on the
/// next handler call.
/// </para>
/// <para>
/// Lifecycle: <see cref="DisposeAsync"/> closes the parent <see cref="McpClient"/>
/// + its transport so a graceful child shutdown (SIGTERM, Ctrl+C, host
/// stop) doesn't leak the upstream connection.
/// </para>
/// </remarks>
public sealed class BowireForwardingMcpTransport : IAsyncDisposable
{
    private readonly Uri _parentEndpoint;
    private readonly string? _bearerToken;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private McpClient? _client;
    private HttpClientTransport? _transport;
    private bool _disposed;

    /// <summary>
    /// Build a forwarder targeting the given parent MCP endpoint URI.
    /// </summary>
    /// <param name="parentEndpoint">
    /// HTTP(S) URI of the parent Bowire MCP endpoint (e.g.
    /// <c>http://localhost:5198/bowire/mcp</c>).
    /// </param>
    /// <param name="bearerToken">
    /// Optional bearer token to attach to every request to the parent
    /// (<c>Authorization: Bearer &lt;secret&gt;</c>). Required when the
    /// parent was started with <c>--token &lt;secret&gt;</c>; ignored when
    /// the parent has no token configured.
    /// </param>
    public BowireForwardingMcpTransport(Uri parentEndpoint, string? bearerToken = null)
    {
        ArgumentNullException.ThrowIfNull(parentEndpoint);
        if (parentEndpoint.Scheme != Uri.UriSchemeHttp && parentEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"Parent MCP endpoint must be an http(s) URI; got '{parentEndpoint}'.",
                nameof(parentEndpoint));
        }
        _parentEndpoint = parentEndpoint;
        _bearerToken = bearerToken;
    }

    /// <summary>
    /// The endpoint this forwarder was configured against. Exposed for
    /// diagnostics (the CLI banner, tests).
    /// </summary>
    public Uri ParentEndpoint => _parentEndpoint;

    /// <summary>
    /// Parse the documented <c>--attach</c> argument shapes into a
    /// concrete parent MCP endpoint URI. Accepts:
    /// <list type="bullet">
    ///   <item><c>host:port</c> — expanded to <c>http://host:port/bowire/mcp</c>
    ///         (the path Bowire's HTTP-bind serves MCP at).</item>
    ///   <item>An absolute <c>http(s)</c> URI — used as-is.</item>
    /// </list>
    /// Returns <c>false</c> + a human-readable reason for malformed input;
    /// the caller surfaces the message to the operator.
    /// </summary>
    public static bool TryParseAttachEndpoint(string? raw, out Uri? endpoint, out string error)
    {
        endpoint = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "--attach: empty address.";
            return false;
        }
        var trimmed = raw.Trim();
        // Absolute http(s) URI — use as-is.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            endpoint = parsed;
            return true;
        }
        // host:port shorthand — Bowire's HTTP-bind mounts MCP at
        // /bowire/mcp so we expand to that without forcing the operator
        // to type it. We restrict the shorthand to a literal "host:port"
        // (no slashes, no extra colons) so a malformed input like
        // "ftp://parent/" doesn't sneak through after the http(s) check
        // rejected it.
        if (LooksLikeHostPort(trimmed)
            && Uri.TryCreate($"http://{trimmed}", UriKind.Absolute, out var shorthand)
            && shorthand.Scheme == Uri.UriSchemeHttp
            && shorthand.Port > 0
            && !string.IsNullOrWhiteSpace(shorthand.Host)
            && (shorthand.AbsolutePath == "/" || shorthand.AbsolutePath.Length == 0))
        {
            endpoint = new Uri($"http://{shorthand.Host}:{shorthand.Port}/bowire/mcp");
            return true;
        }
        error = $"--attach: '{raw}' is neither host:port nor an absolute http(s) URI.";
        return false;
    }

    private static bool LooksLikeHostPort(string s)
    {
        // Reject anything that doesn't look like exactly "host:port" —
        // no slashes (would be a path), no scheme separator ("://"), no
        // user-info ("@"), and at least one colon for the port. The
        // explicit checks beat regex for readability + perf.
        if (s.Contains('/', StringComparison.Ordinal)) return false;
        if (s.Contains('@', StringComparison.Ordinal)) return false;
        var colon = s.LastIndexOf(':');
        if (colon <= 0 || colon == s.Length - 1) return false;
        return s.AsSpan(colon + 1).TrimStart().Length > 0;
    }

    /// <summary>
    /// Whether a bearer token was supplied at construction. Exposed for
    /// diagnostics — doesn't surface the token itself.
    /// </summary>
    public bool HasBearerToken => !string.IsNullOrEmpty(_bearerToken);

    /// <summary>
    /// Lazily establish + return the underlying MCP client. Repeated
    /// callers share the same client; if a previous call left the client
    /// in a faulted state the caller can call <see cref="ResetAsync"/>
    /// before retrying. Thread-safe via an init semaphore.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Transport ownership is handed to the McpClient on CreateAsync; we dispose both in DisposeAsync. On failure before hand-off we dispose the transport explicitly in the catch.")]
    [SuppressMessage("Reliability", "CA1508:Avoid dead conditional code",
        Justification = "Double-checked locking — _client is non-null assigned under the semaphore; the inner re-check covers a concurrent initialiser that finished while we were waiting.")]
    public async Task<McpClient> GetClientAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null) return _client;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;

            var options = new HttpClientTransportOptions
            {
                Endpoint = _parentEndpoint,
                TransportMode = HttpTransportMode.AutoDetect,
            };
            if (!string.IsNullOrEmpty(_bearerToken))
            {
                // The SDK pipes AdditionalHeaders into every outgoing
                // POST + GET; bearer auth on the parent gets honoured
                // exactly like any reverse-proxy in front of an MCP host.
                options.AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = $"Bearer {_bearerToken}",
                };
            }

            var transport = new HttpClientTransport(options);
            try
            {
                _client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
                _transport = transport;
                return _client;
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Tear down the current client + transport so the next
    /// <see cref="GetClientAsync"/> call rebuilds them. Useful after a
    /// connection error so a transient parent restart doesn't sink the
    /// child for its remaining lifetime.
    /// </summary>
    public async Task ResetAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
        _initLock.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort teardown — surfacing a transport dispose error would mask the real shutdown reason.")]
    private async Task CloseAsync()
    {
        if (_client is not null)
        {
            try { await _client.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
            _client = null;
        }
        if (_transport is not null)
        {
            try { await _transport.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
            _transport = null;
        }
    }
}
