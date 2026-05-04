// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// <see cref="IBowireChannel"/> on top of <see cref="ClientWebSocket"/>.
/// Treats the connection as a raw bidirectional frame stream: outgoing
/// messages from the UI are forwarded to the server, incoming frames are
/// pushed back as JSON envelopes containing the type (text / binary), the
/// payload, and (for binary frames) a base64-encoded blob plus byte count.
/// </summary>
internal sealed class WebSocketBowireChannel : IBowireChannel
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    // All state is readonly + non-nullable. The async factory below opens
    // the WebSocket and starts the loops before returning the instance, so
    // by the time anyone holds a reference everything is fully wired.
    private readonly ClientWebSocket _socket;
    private readonly Channel<OutgoingFrame> _outgoing = Channel.CreateUnbounded<OutgoingFrame>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<string> _responses = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });
    private readonly Stopwatch _stopwatch;
    private readonly CancellationTokenSource _cts;
    private readonly Task _receiveLoop;
    private readonly Task _sendLoop;
    private readonly MtlsCertOwner? _mtlsOwner;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsClientStreaming => true;
    public bool IsServerStreaming => true;
    public int SentCount { get; private set; }
    public bool IsClosed { get; private set; }
    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;
    public string? NegotiatedSubProtocol => string.IsNullOrEmpty(_socket.SubProtocol) ? null : _socket.SubProtocol;

    private WebSocketBowireChannel(ClientWebSocket socket, CancellationTokenSource cts, MtlsCertOwner? mtlsOwner)
    {
        _socket = socket;
        _cts = cts;
        _mtlsOwner = mtlsOwner;
        _stopwatch = Stopwatch.StartNew();
        _receiveLoop = Task.Run(ReceiveLoopAsync, _cts.Token);
        _sendLoop = Task.Run(SendLoopAsync, _cts.Token);
    }

    /// <summary>
    /// Open a WebSocket to <paramref name="uri"/> and return a fully wired
    /// channel ready to send and receive frames. Headers, sub-protocols and
    /// optional mTLS client cert are applied before the handshake.
    /// </summary>
    public static async Task<WebSocketBowireChannel> CreateAsync(
        Uri uri,
        Dictionary<string, string>? headers,
        IReadOnlyList<string>? subProtocols,
        CancellationToken ct,
        MtlsConfig? mtlsConfig = null,
        bool trustLocalhostCert = false)
    {
        var socket = new ClientWebSocket();
        MtlsCertOwner? mtlsOwner = null;
        try
        {
            if (mtlsConfig is not null)
            {
                mtlsOwner = MtlsCertOwner.Load(mtlsConfig, out var mtlsError);
                if (mtlsOwner is null)
                {
                    throw new InvalidOperationException(mtlsError ?? "mTLS configuration invalid");
                }
                socket.Options.ClientCertificates.Add(mtlsOwner.ClientCert);
                if (mtlsOwner.Validator is not null)
                {
                    var validator = mtlsOwner.Validator;
                    socket.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errs) =>
                        validator(sender, cert as System.Security.Cryptography.X509Certificates.X509Certificate2, chain, errs);
                }
            }
            else if (trustLocalhostCert && LocalhostCertTrust.IsLocalhostUrl(uri.ToString()))
            {
                // Localhost dev-cert opt-in (Bowire:TrustLocalhostCert or
                // Bowire:websocket:TrustLocalhostCert). Same defence-in-
                // depth as SignalR — relaxed validator never fires on a
                // non-loopback URL even if the flag is on.
#pragma warning disable CA5359
                socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            }

            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                    socket.Options.SetRequestHeader(key, value);
            }
            if (subProtocols is not null)
            {
                foreach (var sp in subProtocols)
                {
                    if (!string.IsNullOrEmpty(sp)) socket.Options.AddSubProtocol(sp);
                }
            }

            await socket.ConnectAsync(uri, ct);
        }
        catch
        {
            socket.Dispose();
            mtlsOwner?.Dispose();
            throw;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return new WebSocketBowireChannel(socket, cts, mtlsOwner);
    }

    public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
    {
        if (_outgoing is null || IsClosed) return Task.FromResult(false);

        // The JS layer wraps every send into the same JSON shape we use for
        // incoming frames. Recognise the wrapped shape and unpack it back to
        // raw text or raw binary; otherwise treat the whole string as a text
        // frame so the simplest call ("just send this string") still works.
        var frame = ParseOutgoingFrame(jsonMessage);
        _outgoing.Writer.TryWrite(frame);
        SentCount++;
        return Task.FromResult(true);
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (IsClosed) return;
        IsClosed = true;

        _outgoing.Writer.TryComplete();

        if (_socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closed", ct);
            }
            catch
            {
                // Best-effort close
            }
        }
    }

    public async IAsyncEnumerable<string> ReadResponsesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var response in _responses.Reader.ReadAllAsync(ct))
            yield return response;
    }

    public async ValueTask DisposeAsync()
    {
        IsClosed = true;

        await _cts.CancelAsync();

        try { await _receiveLoop; } catch { /* loop cancelled */ }
        try { await _sendLoop; }    catch { /* loop cancelled */ }

        _socket.Dispose();
        _cts.Dispose();
        _mtlsOwner?.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _responses.Writer.WriteAsync(JsonSerializer.Serialize(new
                        {
                            type = "close",
                            status = (int?)result.CloseStatus,
                            description = result.CloseStatusDescription
                        }, s_indented), token);
                        return;
                    }

                    await ms.WriteAsync(buffer.AsMemory(0, result.Count), token);
                } while (!result.EndOfMessage);

                var bytes = ms.ToArray();
                string envelope;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(bytes);
                    // If the text is valid JSON, embed it as a parsed
                    // object so the UI shows clean nested JSON instead
                    // of an escaped string with backslash-quotes.
                    object textValue;
                    try
                    {
                        textValue = JsonSerializer.Deserialize<JsonElement>(text);
                    }
                    catch
                    {
                        textValue = text;
                    }
                    envelope = JsonSerializer.Serialize(new
                    {
                        type = "text",
                        text = textValue,
                        bytes = bytes.Length
                    }, s_indented);
                }
                else
                {
                    envelope = JsonSerializer.Serialize(new
                    {
                        type = "binary",
                        bytes = bytes.Length,
                        base64 = Convert.ToBase64String(bytes)
                    }, s_indented);
                }

                await _responses.Writer.WriteAsync(envelope, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on close
        }
        catch (Exception ex)
        {
            try
            {
                await _responses.Writer.WriteAsync(JsonSerializer.Serialize(new
                {
                    type = "error",
                    message = ex.Message
                }, s_indented));
            }
            catch { /* responses already closed */ }
        }
        finally
        {
            _responses.Writer.TryComplete();
        }
    }

    private async Task SendLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(token))
            {
                if (_socket.State != WebSocketState.Open) break;

                if (frame.IsBinary && frame.Bytes is { } bin)
                {
                    await _socket.SendAsync(
                        bin,
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        token);
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(frame.Text ?? "");
                    await _socket.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on close
        }
    }

    private static OutgoingFrame ParseOutgoingFrame(string jsonMessage)
    {
        // The JS channelSend path always wraps the user's input as
        //   { "type": "text", "text": "..." }   or
        //   { "type": "binary", "base64": "..." }
        // Recognise that explicitly so the user keeps full control over the
        // frame type. Anything else is treated as a raw text frame for
        // ad-hoc curl-style usage.
        if (string.IsNullOrWhiteSpace(jsonMessage))
            return new OutgoingFrame(IsBinary: false, Text: "", Bytes: null);

        try
        {
            using var doc = JsonDocument.Parse(jsonMessage);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                var t = typeProp.GetString();
                if (t == "binary" && doc.RootElement.TryGetProperty("base64", out var b64) && b64.ValueKind == JsonValueKind.String)
                {
                    var bytes = Convert.FromBase64String(b64.GetString() ?? "");
                    return new OutgoingFrame(IsBinary: true, Text: null, Bytes: bytes);
                }

                if (t == "text" && doc.RootElement.TryGetProperty("text", out var textProp))
                {
                    return new OutgoingFrame(IsBinary: false, Text: textProp.GetString() ?? "", Bytes: null);
                }

                // Fall-through to "data" key — convenience for the basic form
                if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.String)
                {
                    return new OutgoingFrame(IsBinary: false, Text: dataProp.GetString() ?? "", Bytes: null);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON — treat as raw text
        }

        return new OutgoingFrame(IsBinary: false, Text: jsonMessage, Bytes: null);
    }

    private readonly record struct OutgoingFrame(bool IsBinary, string? Text, byte[]? Bytes);
}
