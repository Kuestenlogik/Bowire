// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Kuestenlogik.Bowire.Auth;
using Microsoft.AspNetCore.SignalR.Client;

namespace Kuestenlogik.Bowire;

/// <summary>
/// SignalR implementation of <see cref="IBowireChannel"/> for duplex and client-streaming calls.
/// Buffers outgoing messages and streams responses from the hub.
/// </summary>
internal sealed class SignalRBowireChannel : IBowireChannel
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly string _methodName;

    // All state is readonly + non-nullable. The async factory below opens
    // the HubConnection and starts the pump task before returning, so by
    // the time anyone holds a reference everything is fully wired.
    private readonly HubConnection _connection;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
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
    private readonly MtlsCertOwner? _mtlsOwner;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsClientStreaming { get; }
    public bool IsServerStreaming { get; }
    public int SentCount { get; private set; }
    public bool IsClosed { get; private set; }
    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

    private SignalRBowireChannel(
        HubConnection connection,
        string methodName,
        bool isClientStreaming,
        bool isServerStreaming,
        CancellationTokenSource cts,
        MtlsCertOwner? mtlsOwner)
    {
        _connection = connection;
        _methodName = methodName;
        IsClientStreaming = isClientStreaming;
        IsServerStreaming = isServerStreaming;
        _cts = cts;
        _mtlsOwner = mtlsOwner;
        _stopwatch = Stopwatch.StartNew();

        _ = Task.Run(async () =>
        {
            try
            {
                if (IsServerStreaming)
                    await RunServerStreamingAsync();
                else
                    await RunClientStreamingAsync();
            }
            catch (OperationCanceledException)
            {
                // Expected on close
            }
            catch (Exception ex)
            {
                await _responses.Writer.WriteAsync(
                    JsonSerializer.Serialize(new { error = ex.Message }, IndentedJson));
            }
            finally
            {
                _responses.Writer.TryComplete();
            }
        }, _cts.Token);
    }

    /// <summary>
    /// Build a HubConnection, start it, and return a fully-wired channel.
    /// </summary>
    public static async Task<SignalRBowireChannel> CreateAsync(
        string hubUrl,
        string methodName,
        bool isClientStreaming,
        bool isServerStreaming,
        Dictionary<string, string>? headers,
        CancellationToken ct,
        MtlsConfig? mtlsConfig = null,
        bool trustLocalhostCert = false)
    {
        MtlsCertOwner? mtlsOwner = null;
        if (mtlsConfig is not null)
        {
            mtlsOwner = MtlsCertOwner.Load(mtlsConfig, out var mtlsError);
            if (mtlsOwner is null)
            {
                throw new InvalidOperationException(mtlsError ?? "mTLS configuration invalid");
            }
        }

        // Trust the self-signed ASP.NET Core dev cert (and any other
        // localhost-served cert) only when the consuming host has
        // explicitly opted in via Bowire:SignalR:TrustLocalhostCert =
        // true. Off by default — production URLs must always go
        // through the OS trust store. We never apply this for non-
        // localhost hosts no matter the flag.
        var allowSelfSigned = trustLocalhostCert && IsLocalhostUrl(hubUrl) && mtlsOwner is null;

        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (headers is not null)
                {
                    foreach (var (key, value) in headers)
                        options.Headers[key] = value;
                }
                if (mtlsOwner is not null)
                {
                    var owner = mtlsOwner;
                    // Long-polling / SSE / negotiate path: install a fresh
                    // HttpClientHandler with the client cert. SignalR disposes
                    // this handler when the HubConnection goes away; the X509
                    // resources we keep around in mtlsOwner outlive the
                    // factory call.
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
                    // Localhost dev-cert opt-in (Bowire:SignalR:TrustLocalhostCert).
                    // CA5359 fires here because the callback returns true
                    // unconditionally — we suppress it explicitly: the
                    // outer guard `allowSelfSigned` already requires the
                    // host to be loopback AND the consumer to have
                    // opted in via configuration; a relaxed validator
                    // for any other host wouldn't reach this branch.
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

        var connection = builder.Build();
        try
        {
            await connection.StartAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            mtlsOwner?.Dispose();
            throw;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return new SignalRBowireChannel(connection, methodName, isClientStreaming, isServerStreaming, cts, mtlsOwner);
    }

    public Task<bool> SendAsync(string jsonMessage, CancellationToken ct)
    {
        if (IsClosed) return Task.FromResult(false);

        _outgoing.Writer.TryWrite(jsonMessage);
        SentCount++;
        return Task.FromResult(true);
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        _outgoing.Writer.TryComplete();
        IsClosed = true;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadResponsesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var response in _responses.Reader.ReadAllAsync(ct))
            yield return response;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
        await _connection.DisposeAsync();
        _mtlsOwner?.Dispose();
    }

    /// <summary>
    /// For server-streaming: send buffered messages as arguments, then stream responses.
    /// SignalR streaming uses the client's StreamAsyncCore to receive a server stream.
    /// </summary>
    private async Task RunServerStreamingAsync()
    {
        var token = _cts.Token;
        // Collect all outgoing messages first (they become method arguments)
        var args = new List<object?>();
        await foreach (var msg in _outgoing.Reader.ReadAllAsync(token))
        {
            try
            {
                args.Add(JsonSerializer.Deserialize<object>(msg));
            }
            catch
            {
                args.Add(msg);
            }
        }

        await foreach (var item in _connection.StreamAsyncCore<object?>(
            _methodName, args.ToArray(), token))
        {
            var json = JsonSerializer.Serialize(item, IndentedJson);
            await _responses.Writer.WriteAsync(json, token);
        }
    }

    /// <summary>
    /// For client-streaming: collect all outgoing messages, invoke as a batch.
    /// </summary>
    private async Task RunClientStreamingAsync()
    {
        var token = _cts.Token;
        var args = new List<object?>();
        await foreach (var msg in _outgoing.Reader.ReadAllAsync(token))
        {
            try
            {
                args.Add(JsonSerializer.Deserialize<object>(msg));
            }
            catch
            {
                args.Add(msg);
            }
        }

        var result = await _connection.InvokeCoreAsync<object?>(
            _methodName, args.ToArray(), token);

        var json = result is not null
            ? JsonSerializer.Serialize(result, IndentedJson)
            : "null";

        await _responses.Writer.WriteAsync(json, token);
    }

    /// <summary>
    /// True when the URL points at localhost / 127.0.0.1 / ::1. Used to
    /// scope the trust-localhost-cert opt-in: the relaxed validation
    /// callback only ever fires when the URL is loopback, even if the
    /// flag was accidentally enabled in production. Defence in depth.
    /// </summary>
    internal static bool IsLocalhostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1"
            || host == "[::1]";
    }
}
