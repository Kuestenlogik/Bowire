// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.SignalR.Client;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Manages a SignalR <see cref="HubConnection"/> and invokes hub methods dynamically.
/// </summary>
internal sealed class SignalRInvoker : IAsyncDisposable
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private HubConnection? _connection;
    private MtlsCertOwner? _mtlsOwner;

    public Task ConnectAsync(string hubUrl, Dictionary<string, string>? headers, CancellationToken ct)
        => ConnectAsync(hubUrl, headers, mtlsConfig: null, ct);

    public async Task ConnectAsync(string hubUrl, Dictionary<string, string>? headers, MtlsConfig? mtlsConfig, CancellationToken ct, bool trustLocalhostCert = false)
    {
        if (mtlsConfig is not null)
        {
            _mtlsOwner = MtlsCertOwner.Load(mtlsConfig, out var mtlsError);
            if (_mtlsOwner is null)
            {
                throw new InvalidOperationException(mtlsError ?? "mTLS configuration invalid");
            }
        }

        // The plugin resolved the trust flag through LocalhostCertTrust.
        // Loopback double-guard mirrors SignalRBowireChannel.
        var allowSelfSigned = trustLocalhostCert && LocalhostCertTrust.IsLocalhostUrl(hubUrl) && _mtlsOwner is null;

        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (headers is not null)
                {
                    foreach (var (key, value) in headers)
                        options.Headers[key] = value;
                }
                if (_mtlsOwner is not null)
                {
                    var owner = _mtlsOwner;
                    // Long-polling / SSE / negotiate path: install a fresh
                    // HttpClientHandler with the client cert. SignalR disposes
                    // this handler when the HubConnection goes away; the X509
                    // resources we keep around in _mtlsOwner outlive the
                    // factory call so re-connects don't refer to disposed
                    // certificates.
                    options.HttpMessageHandlerFactory = inner =>
                    {
#pragma warning disable CA2000, CA5400
                        var mtlsHandler = new HttpClientHandler
                        {
                            ClientCertificateOptions = ClientCertificateOption.Manual,
                            CheckCertificateRevocationList = false
                        };
#pragma warning restore CA2000, CA5400
                        mtlsHandler.ClientCertificates.Add(owner.ClientCert);
                        if (owner.Validator is not null)
                        {
                            var validator = owner.Validator;
                            mtlsHandler.ServerCertificateCustomValidationCallback = (req, cert, chain, errs) =>
                                validator(req, cert, chain, errs);
                        }
                        inner.Dispose();
                        return mtlsHandler;
                    };
                    // WebSocket transport: ClientWebSocketOptions exposes its
                    // own cert + validator, separate from the HttpMessageHandler.
                    options.WebSocketConfiguration = ws =>
                    {
                        ws.ClientCertificates.Add(owner.ClientCert);
                        if (owner.Validator is not null)
                        {
                            var validator = owner.Validator;
                            ws.RemoteCertificateValidationCallback = (sender, cert, chain, errs) =>
                                validator(sender, cert as System.Security.Cryptography.X509Certificates.X509Certificate2, chain, errs);
                        }
                    };
                }
                else if (allowSelfSigned)
                {
                    // See SignalRBowireChannel for why CA5359 is
                    // suppressed: outer guard scopes this to the
                    // opt-in localhost path only.
                    options.HttpMessageHandlerFactory = inner =>
                    {
#pragma warning disable CA2000, CA5400, CA5359
                        var handler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                            CheckCertificateRevocationList = false,
                        };
#pragma warning restore CA2000, CA5400, CA5359
                        inner.Dispose();
                        return handler;
                    };
                    options.WebSocketConfiguration = ws =>
                    {
#pragma warning disable CA5359
                        ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
                    };
                }
            })
            .WithAutomaticReconnect();

        _connection = builder.Build();
        await _connection.StartAsync(ct);
    }

    public Task<InvokeResult> InvokeAsync(
        string method, List<string> jsonMessages, CancellationToken ct, int? expectedParameterCount = null)
        => InvokeWithArgsAsync(method, ParseArguments(jsonMessages, expectedParameterCount), ct);

    /// <summary>
    /// Invoke with pre-built positional arguments. Used by the ad-hoc
    /// separate-target path, which parses the hub method + args out of
    /// its own payload shape and must NOT round-trip through
    /// <see cref="ParseArguments"/> (whose single-object unfold would
    /// split a lone complex argument into per-property args).
    /// </summary>
    public async Task<InvokeResult> InvokeWithArgsAsync(
        string method, object?[] args, CancellationToken ct)
    {
        if (_connection is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var sw = Stopwatch.StartNew();

        try
        {
            var result = await _connection.InvokeCoreAsync<object?>(method, args, ct);
            sw.Stop();

            var response = result is not null
                ? JsonSerializer.Serialize(result, IndentedJson)
                : "null";

            return new InvokeResult(response, sw.ElapsedMilliseconds, "OK", []);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new InvokeResult(
                Response: ex.Message,
                DurationMs: sw.ElapsedMilliseconds,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["exceptionType"] = ex.GetType().Name
                });
        }
    }

    public IAsyncEnumerable<string> StreamAsync(
        string method, List<string> jsonMessages, CancellationToken ct, int? expectedParameterCount = null)
        => StreamWithArgsAsync(method, ParseArguments(jsonMessages, expectedParameterCount), ct);

    /// <summary>
    /// Streaming twin of <see cref="InvokeWithArgsAsync"/> — see there
    /// for why the ad-hoc path bypasses <see cref="ParseArguments"/>.
    /// </summary>
    public async IAsyncEnumerable<string> StreamWithArgsAsync(
        string method, object?[] args,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_connection is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        await foreach (var item in _connection.StreamAsyncCore<object?>(method, args, ct))
        {
            yield return JsonSerializer.Serialize(item, IndentedJson);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _mtlsOwner?.Dispose();
        _mtlsOwner = null;
    }

    private static object?[] ParseArguments(List<string> jsonMessages, int? expectedParameterCount = null)
    {
        // No-arg streaming methods (e.g. SubscribeToChanges() with only
        // the implicit CancellationToken) receive an empty body "{}" from
        // the form pane. The fallback path below would map that to a
        // single positional null and SignalR would reject the invocation
        // with "Failed to invoke … wrong argument count". Detect the empty
        // object up front and return zero args.
        if (jsonMessages.Count == 1 && jsonMessages[0]?.Trim() is "{}" or "" or null)
        {
            return [];
        }

        // Form-mode sends ONE JSON object keyed by parameter name —
        // {"count": 5, "delayMs": 200} for Counter(int count, int delayMs),
        // {"text": "hi"} for Echo(string text). Unwrap it into one
        // positional arg per property so the hub sees Counter(5, 200)
        // rather than Counter({count:5,delayMs:200}).
        //
        // The decision needs the method's arity, because the payload alone
        // is ambiguous: {"text":"hi"} is BOTH "one string parameter named
        // text" and "a DTO with a text field". Unfolding only when the
        // property count matches the parameter count settles it:
        //   Echo(string text)      + {"text":"hi"}          1==1 → Echo("hi")
        //   Send(ChatMessage m)    + {"m":{…}}              1==1 → Send({…})
        //   Send(ChatMessage m)    + {"user":…,"body":…}    1!=2 → Send({…})
        // Before this, unfolding required MORE than one property, so every
        // single-parameter hub method — the common case — received the
        // wrapper object and answered HubException "Failed to invoke".
        // Without a known arity (caller didn't supply one) the old
        // >1 heuristic still applies.
        if (jsonMessages.Count == 1 && !string.IsNullOrWhiteSpace(jsonMessages[0]))
        {
            var only = jsonMessages[0];
            if (only != "{}")
            {
                try
                {
                    using var doc = JsonDocument.Parse(only);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var props = doc.RootElement.EnumerateObject().ToArray();
                        var unfold = expectedParameterCount is { } expected
                            ? props.Length == expected
                            : props.Length > 1;
                        if (unfold)
                        {
                            var unfolded = new object?[props.Length];
                            for (var i = 0; i < props.Length; i++)
                                unfolded[i] = JsonElementToArg(props[i].Value);
                            return unfolded;
                        }
                    }
                }
                catch { /* fall through to per-message parsing */ }
            }
        }

        return jsonMessages.Select(json =>
        {
            if (string.IsNullOrWhiteSpace(json) || json == "{}")
                return null;

            try
            {
                return JsonSerializer.Deserialize<object>(json);
            }
            catch
            {
                return (object?)json;
            }
        }).ToArray();
    }

    internal static object? JsonElementToArg(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        // Complex payloads (objects / arrays) stay as a JsonElement — SignalR's
        // serializer handles them on the way out.
        _ => el.Clone()
    };
}
