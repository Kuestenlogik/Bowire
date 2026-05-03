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

    // Official Socket.IO logo (simpleicons).
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="currentColor" width="16" height="16" aria-hidden="true"><path d="M11.9362.0137a12.1694 12.1694 0 00-2.9748.378C4.2816 1.5547.5678 5.7944.0918 10.6012c-.59 4.5488 1.7079 9.2856 5.6437 11.6345 3.8608 2.4179 9.0926 2.3199 12.8734-.223 3.3969-2.206 5.5118-6.2277 5.3858-10.2845-.058-4.0159-2.31-7.9167-5.7588-9.9796C16.354.5876 14.1431.0047 11.9362.0137zm-.063 1.696c4.9448-.007 9.7886 3.8137 10.2815 8.9245.945 5.6597-3.7528 11.4125-9.4875 11.5795-5.4538.544-10.7245-4.0798-10.8795-9.5566-.407-4.4338 2.5159-8.8346 6.6977-10.2995a9.1126 9.1126 0 013.3878-.647zm5.0908 3.2248c-2.6869 2.0849-5.2598 4.3078-7.8886 6.4567 1.2029.017 2.4118.016 3.6208.01 1.41-2.165 2.8589-4.3008 4.2678-6.4667zm-5.6647 7.6536c-1.41 2.166-2.86 4.3088-4.2699 6.4737 2.693-2.0799 5.2548-4.3198 7.9017-6.4557a255.4132 255.4132 0 00-3.6318-.018z"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider) { }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return [];
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
        var url = serverUrl.TrimEnd('/');
        var payload = jsonMessages.FirstOrDefault() ?? "{}";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var client = new SocketIOClient.SocketIO(new Uri(url), new SocketIOOptions
        { Reconnection = false, ConnectionTimeout = TimeSpan.FromSeconds(10) });

        string? responseData = null;
        var tcs = new TaskCompletionSource<string?>();

        await client.ConnectAsync();
        try
        {
            string eventName;
            string? eventData;
            try
            {
                var doc = JsonDocument.Parse(payload);
                eventName = doc.RootElement.TryGetProperty("event", out var evProp)
                    ? evProp.GetString() ?? "message" : "message";
                eventData = doc.RootElement.TryGetProperty("data", out var dataProp)
                    ? dataProp.ToString() : payload;
            }
            catch { eventName = "message"; eventData = payload; }

            client.OnAny((name, response) =>
            {
                tcs.TrySetResult(JsonSerializer.Serialize(new { @event = name, data = response?.ToString() }));
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
        var url = serverUrl.TrimEnd('/');
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();

        using var client = new SocketIOClient.SocketIO(new Uri(url), new SocketIOOptions
        { Reconnection = false, ConnectionTimeout = TimeSpan.FromSeconds(10) });

        var specificEvent = method != "listen" ? method : null;

        client.OnAny((name, response) =>
        {
            if (specificEvent != null && name != specificEvent) return Task.CompletedTask;
            channel.Writer.TryWrite(JsonSerializer.Serialize(new
            {
                @event = name, data = response?.ToString(), timestamp = DateTime.UtcNow
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
