// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using SocketIOClient;

namespace Kuestenlogik.Bowire.Protocol.SocketIo;

/// <summary>
/// Bowire protocol plugin for Socket.IO 4.x servers via SocketIOClient.
/// </summary>
public sealed class BowireSocketIoProtocol : IBowireProtocol
{
    public string Name => "Socket.IO";
    public string Id => "socketio";

    /// <summary>
    /// Optional metadata header key that selects the Socket.IO namespace
    /// the client should connect to. Set to a path-prefixed value like
    /// <c>/harbor</c>. When absent, the plugin connects to the root
    /// namespace (<c>/</c>). Equivalent to passing
    /// <c>http://host:port/harbor</c> as the server URL — both forms
    /// work; the metadata header lets the user keep a clean base URL
    /// across multiple methods.
    /// </summary>
    public const string NamespaceMetadataKey = "X-Bowire-SocketIo-Namespace";

    // Official Socket.IO logo (simpleicons).
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="currentColor" width="16" height="16" aria-hidden="true"><path d="M11.9362.0137a12.1694 12.1694 0 00-2.9748.378C4.2816 1.5547.5678 5.7944.0918 10.6012c-.59 4.5488 1.7079 9.2856 5.6437 11.6345 3.8608 2.4179 9.0926 2.3199 12.8734-.223 3.3969-2.206 5.5118-6.2277 5.3858-10.2845-.058-4.0159-2.31-7.9167-5.7588-9.9796C16.354.5876 14.1431.0047 11.9362.0137zm-.063 1.696c4.9448-.007 9.7886 3.8137 10.2815 8.9245.945 5.6597-3.7528 11.4125-9.4875 11.5795-5.4538.544-10.7245-4.0798-10.8795-9.5566-.407-4.4338 2.5159-8.8346 6.6977-10.2995a9.1126 9.1126 0 013.3878-.647zm5.0908 3.2248c-2.6869 2.0849-5.2598 4.3078-7.8886 6.4567 1.2029.017 2.4118.016 3.6208.01 1.41-2.165 2.8589-4.3008 4.2678-6.4667zm-5.6647 7.6536c-1.41 2.166-2.86 4.3088-4.2699 6.4737 2.693-2.0799 5.2548-4.3198 7.9017-6.4557a255.4132 255.4132 0 00-3.6318-.018z"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider) { }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return [];
        // Discovery doesn't currently take metadata, so namespace selection
        // here is URL-only: a path component on serverUrl (e.g.
        // http://host:3000/harbor) is honoured. Metadata-based namespace
        // selection works on InvokeAsync / InvokeStreamAsync once the user
        // wires up the X-Bowire-SocketIo-Namespace header in the UI.
        var url = serverUrl.TrimEnd('/');
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return [];

        try
        {
            using var client = new SocketIOClient.SocketIO(new Uri(url), new SocketIOOptions
            {
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                Reconnection = false
            });

            var connected = false;
            var detectedEvents = new HashSet<string>();

            client.OnConnected += (_, _) => connected = true;
            client.OnAny((name, _) => { detectedEvents.Add(name); return Task.CompletedTask; });

            await client.ConnectAsync();
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { }
            await client.DisconnectAsync();

            if (!connected) return [];

            var methods = new List<BowireMethodInfo>
            {
                new("emit", "socketio/emit", false, false,
                    BuildEmitInput(), BuildEmptyOutput(), "Unary")
                { Summary = "Emit an event to the server" },

                new("listen", "socketio/listen", false, true,
                    BuildListenInput(), BuildEventOutput(), "ServerStreaming")
                { Summary = "Listen for events from the server" }
            };

            foreach (var ev in detectedEvents.OrderBy(e => e, StringComparer.Ordinal))
            {
                methods.Add(new BowireMethodInfo(
                    ev, $"socketio/{ev}/listen", false, true,
                    BuildEmptyInput(), BuildEventOutput(), "ServerStreaming")
                { Summary = $"Listen for '{ev}' events" });
            }

            return [new BowireServiceInfo("Socket.IO", "socketio", methods)
            {
                Source = "socketio", OriginUrl = serverUrl,
                Description = $"Socket.IO server at {url}"
            }];
        }
        catch { return []; }
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var url = ResolveUrl(serverUrl, metadata);
        var payload = jsonMessages.FirstOrDefault() ?? "{}";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var client = new SocketIOClient.SocketIO(new Uri(url), new SocketIOOptions
        { Reconnection = false, ConnectionTimeout = TimeSpan.FromSeconds(10) });

        string? responseData = null;
        var tcs = new TaskCompletionSource<string?>();

        await client.ConnectAsync();
        try
        {
            var (eventName, eventData) = ParseEmitPayload(payload);

            client.OnAny((name, response) =>
            {
                tcs.TrySetResult(JsonSerializer.Serialize(new
                {
                    @event = name,
                    data = ExtractPayload(response)
                }));
                return Task.CompletedTask;
            });

            await client.EmitAsync(eventName, new object[] { eventData ?? "{}" });
            await Task.WhenAny(tcs.Task, Task.Delay(3000, ct));
            responseData = tcs.Task.IsCompleted ? await tcs.Task : null;

            sw.Stop();
            return new InvokeResult(
                responseData ?? JsonSerializer.Serialize(new { @event = eventName, status = "emitted" }),
                sw.ElapsedMilliseconds, "OK",
                new Dictionary<string, string> { ["event"] = eventName });
        }
        finally { await client.DisconnectAsync(); }
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = ResolveUrl(serverUrl, metadata);
        var eventFilter = ExtractEventFilter(jsonMessages);

        // method == "listen" is the generic catch-all method. Dynamically
        // discovered events surface as their own per-event method, in which
        // case `method` is the event name itself.
        var specificEvent = eventFilter ?? (method != "listen" ? method : null);

        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();

        using var client = new SocketIOClient.SocketIO(new Uri(url), new SocketIOOptions
        { Reconnection = false, ConnectionTimeout = TimeSpan.FromSeconds(10) });

        client.OnAny((name, response) =>
        {
            if (specificEvent != null && name != specificEvent) return Task.CompletedTask;
            channel.Writer.TryWrite(JsonSerializer.Serialize(new
            {
                @event = name,
                // ctx.RawText is `["eventName", arg1, arg2, ...]`. The plugin
                // strips the leading event-name token so the user sees just
                // the payload they cared about; falls back to the raw text
                // when parsing fails.
                data = ExtractPayload(response),
                timestamp = DateTime.UtcNow
            }));
            return Task.CompletedTask;
        });

        await client.ConnectAsync();
        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(ct))
                yield return msg;
        }
        finally { await client.DisconnectAsync(); }
    }

    /// <summary>
    /// Resolve the effective Socket.IO connection URL by honouring the
    /// optional namespace header. <c>http://host:port</c> becomes
    /// <c>http://host:port/harbor</c> when metadata carries
    /// <c>X-Bowire-SocketIo-Namespace = /harbor</c>. If the URL already has
    /// a non-trivial path component (the user typed
    /// <c>http://host:port/harbor</c> directly), the path wins — metadata
    /// only fills in the gap.
    /// </summary>
    private static string ResolveUrl(string serverUrl, Dictionary<string, string>? metadata)
    {
        var trimmed = serverUrl.TrimEnd('/');
        if (metadata is null || !metadata.TryGetValue(NamespaceMetadataKey, out var ns)) return trimmed;
        if (string.IsNullOrWhiteSpace(ns)) return trimmed;

        // If the user already supplied a non-root path on the URL, leave it
        // alone — explicit URL beats the catch-all metadata fallback.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            && !string.IsNullOrEmpty(parsed.AbsolutePath)
            && parsed.AbsolutePath != "/")
        {
            return trimmed;
        }

        var nsPath = ns.StartsWith('/') ? ns : "/" + ns;
        return trimmed + nsPath;
    }

    /// <summary>
    /// Extract the user-visible payload from a SocketIOClient response.
    /// SocketIOClient 4.x exposes the full frame as RawText (the JSON
    /// array <c>["eventName", arg1, ...]</c>); ToString() returns the
    /// type name <c>SocketIOClient.EventContext</c> which is useless to
    /// users. Strip the event-name leading element and unwrap a single
    /// remaining argument so the streaming-pane shows the raw payload.
    /// </summary>
    private static JsonElement? ExtractPayload(SocketIOClient.IEventContext? ctx)
    {
        if (ctx is null) return null;
        var raw = ctx.RawText;
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return doc.RootElement.Clone();
            var args = doc.RootElement.EnumerateArray().Skip(1).ToList();
            return args.Count switch
            {
                0 => null,
                1 => args[0].Clone(),
                _ => JsonSerializer.SerializeToElement(args.Select(a => a.Clone()).ToArray())
            };
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw);
        }
    }

    /// <summary>
    /// Decode the first <c>jsonMessages</c> entry submitted to
    /// <see cref="InvokeAsync"/> into an <c>(eventName, eventData)</c>
    /// pair. The form sends <c>{"event":"…","data":"…"}</c> in the
    /// well-formed case; missing fields default to <c>"message"</c> /
    /// the raw payload, malformed JSON gracefully degrades to the
    /// same defaults so an emit never fails on payload-shape alone.
    /// </summary>
    private static (string EventName, string EventData) ParseEmitPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var eventName = doc.RootElement.TryGetProperty("event", out var evProp)
                ? evProp.GetString() ?? "message" : "message";
            var eventData = doc.RootElement.TryGetProperty("data", out var dataProp)
                ? dataProp.ToString() : payload;
            return (eventName, eventData);
        }
        catch (JsonException)
        {
            return ("message", payload);
        }
    }

    /// <summary>
    /// Pull the optional event-name filter from <see cref="InvokeStreamAsync"/>'s
    /// first message. When set, the streaming pane forwards only matching
    /// events; <c>null</c> (no filter, no event field, empty value, or
    /// malformed JSON) means "every event reaches the pane".
    /// </summary>
    private static string? ExtractEventFilter(List<string> jsonMessages)
    {
        if (jsonMessages.Count == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(jsonMessages[0]);
            if (!doc.RootElement.TryGetProperty("event", out var evProp)) return null;
            var value = evProp.GetString();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    private static BowireMessageInfo BuildEmitInput() => new(
        "SocketIoEmitRequest", "socketio.EmitRequest",
        [
            new BowireFieldInfo("event", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
            { Description = "Event name to emit", Required = true, Example = "\"message\"" },
            new BowireFieldInfo("data", 2, "string", "LABEL_OPTIONAL", false, false, null, null)
            { Description = "JSON payload to send with the event" }
        ]);
    private static BowireMessageInfo BuildListenInput() => new(
        "SocketIoListenRequest", "socketio.ListenRequest",
        [new BowireFieldInfo("event", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
         { Description = "Event name to listen for (empty = all events)" }]);
    private static BowireMessageInfo BuildEmptyInput() => new("Empty", "socketio.Empty", []);
    private static BowireMessageInfo BuildEmptyOutput() => new(
        "SocketIoEmitResponse", "socketio.EmitResponse",
        [
            new BowireFieldInfo("event", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("status", 2, "string", "LABEL_OPTIONAL", false, false, null, null)
        ]);
    private static BowireMessageInfo BuildEventOutput() => new(
        "SocketIoEvent", "socketio.Event",
        [
            new BowireFieldInfo("event", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("data", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("timestamp", 3, "string", "LABEL_OPTIONAL", false, false, null, null)
        ]);
}
