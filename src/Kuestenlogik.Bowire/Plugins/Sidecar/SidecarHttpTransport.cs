// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// HTTP/SSE transport for a sidecar plugin — the MCP-style streamable-HTTP
/// shape. Requests are JSON-RPC envelopes POSTed to the manifest's
/// <c>url</c>; the HTTP response body carries the matching JSON-RPC
/// response. Server-initiated notifications (<c>$/stream/*</c>,
/// <c>$/channel/*</c>) arrive over one long-lived SSE <c>GET</c> stream
/// and route through the shared <see cref="SidecarSubscriptionHub"/>.
/// </summary>
/// <remarks>
/// Unlike the stdio transport, the sidecar here is a (possibly remote)
/// service Bowire does <em>not</em> own — there's no process to spawn or
/// kill. Disposing just closes the SSE stream + sends a best-effort
/// <c>shutdown</c> POST the service may ignore. Suits hosted / shared
/// deployments where a sidecar runs once and serves many hosts.
/// </remarks>
internal sealed class SidecarHttpTransport : ISidecarTransport
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly SidecarSubscriptionHub _hub = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _sseLoop;
    private volatile bool _sseEnded;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private SidecarHttpTransport(HttpClient http, Uri endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    public bool HasExited => _sseEnded;

    public ChannelReader<JsonObject> Subscribe(string id) => _hub.Subscribe(id);

    public void Unsubscribe(string id) => _hub.Unsubscribe(id);

    /// <summary>
    /// Open the transport for <paramref name="manifest"/> (must declare
    /// the http transport + a url) and start the SSE notification loop.
    /// </summary>
    public static async Task<SidecarHttpTransport> StartAsync(SidecarPluginManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrEmpty(manifest.Url))
            throw new InvalidOperationException("http sidecar manifest has no url.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException($"http sidecar url '{manifest.Url}' is not an absolute URI.");

        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new SidecarHttpTransport(http, endpoint);
        await transport.OpenSseAsync(ct).ConfigureAwait(false);
        return transport;
    }

    // -- requests (POST) ----------------------------------------------

    public async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct)
    {
        var envelope = new { jsonrpc = "2.0", id = 1, method, @params };
        using var content = new StringContent(
            JsonSerializer.Serialize(envelope, s_jsonOpts), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { node = null; }
        if (node is not JsonObject obj)
            throw new SidecarJsonRpcException(-32000, $"http sidecar returned non-JSON-RPC body (HTTP {(int)resp.StatusCode})", body);

        if (obj["error"] is JsonObject err)
        {
            var code = err["code"]?.GetValue<int>() ?? -32000;
            var msg = err["message"]?.GetValue<string>() ?? "sidecar error";
            throw new SidecarJsonRpcException(code, msg, err.ToJsonString());
        }
        if (obj["result"] is { } result)
            return JsonSerializer.SerializeToElement(result);
        // No result/error — malformed; hand back an empty object so
        // callers don't NRE.
        return JsonSerializer.SerializeToElement(new { });
    }

    // -- notifications (SSE) ------------------------------------------

    private async Task OpenSseAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var req = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        // ResponseHeadersRead so we get the stream as soon as the
        // headers land, not after the (infinite) body completes.
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        // resp + stream ownership transfers to the read loop, which
        // disposes resp in its finally (CA2025: the loop owns their
        // lifetime, not this method).
#pragma warning disable CA2025
        _sseLoop = Task.Run(() => SseReadLoopAsync(resp, stream), CancellationToken.None);
#pragma warning restore CA2025
    }

    private async Task SseReadLoopAsync(HttpResponseMessage resp, Stream stream)
    {
        var data = new StringBuilder();
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
                if (line is null) break; // stream closed

                if (line.Length == 0)
                {
                    // Event boundary — dispatch the accumulated data.
                    if (data.Length > 0)
                    {
                        DispatchSseData(data.ToString());
                        data.Clear();
                    }
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    // Per the SSE spec a single leading space after the
                    // colon is stripped.
                    var value = line[5..];
                    if (value.StartsWith(' ')) value = value[1..];
                    data.Append(value);
                }
                // other SSE fields (event:/id:/retry:/comments) ignored
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (IOException) { /* connection dropped */ }
        catch (HttpRequestException) { /* connection dropped */ }
        finally
        {
            _sseEnded = true;
            _hub.CompleteAll();
            resp.Dispose();
        }
    }

    private void DispatchSseData(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return; }
        if (node is JsonObject obj) _hub.Route(obj);
    }

    public async ValueTask DisposeAsync()
    {
        // Best-effort tell the service we're leaving — it may ignore
        // this (a shared service keeps running for other hosts).
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RequestAsync("shutdown", null, cts.Token).ConfigureAwait(false);
        }
        catch { /* swallow */ }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_sseLoop is not null)
        {
            try { await _sseLoop.ConfigureAwait(false); } catch { }
        }
        _lifetime.Dispose();
        _http.Dispose();
    }
}
