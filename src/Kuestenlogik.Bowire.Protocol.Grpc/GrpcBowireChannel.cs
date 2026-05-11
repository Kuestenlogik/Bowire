// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Protocol.Grpc;

/// <summary>
/// gRPC implementation of <see cref="IBowireChannel"/> for duplex and client-streaming calls.
/// </summary>
internal sealed class GrpcBowireChannel : IBowireChannel
{
    private readonly string _serviceName;
    private readonly string _methodName;
    private readonly Dictionary<string, string>? _metadata;

    // All state is readonly + non-nullable. The async factory below resolves
    // the descriptors and opens the gRPC channel before returning the
    // instance, so by the time anyone holds a reference everything is wired.
    private readonly GrpcChannel _grpcChannel;
    // The HTTP message handler is owned by the channel: GrpcChannelBuilder.BuildChannel
    // pins DisposeHttpClient=false so the GrpcChannel can be shared, so we
    // dispose the handler ourselves in DisposeAsync.
    private readonly HttpMessageHandler _ownedHandler;
    private readonly MessageDescriptor _inputType;
    private readonly MessageDescriptor _outputType;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
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

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsClientStreaming { get; }
    public bool IsServerStreaming { get; }
    public int SentCount { get; private set; }
    public bool IsClosed { get; private set; }
    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

    private GrpcBowireChannel(
        GrpcChannel grpcChannel,
        HttpMessageHandler ownedHandler,
        string serviceName,
        string methodName,
        MessageDescriptor inputType,
        MessageDescriptor outputType,
        bool isClientStreaming,
        bool isServerStreaming,
        Dictionary<string, string>? metadata,
        CancellationToken ct)
    {
        _grpcChannel = grpcChannel;
        _ownedHandler = ownedHandler;
        _serviceName = serviceName;
        _methodName = methodName;
        _inputType = inputType;
        _outputType = outputType;
        IsClientStreaming = isClientStreaming;
        IsServerStreaming = isServerStreaming;
        _metadata = metadata;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stopwatch = Stopwatch.StartNew();

        var rawMethod = new Method<byte[], byte[]>(
            (IsClientStreaming, IsServerStreaming) switch
            {
                (true, true) => MethodType.DuplexStreaming,
                (true, false) => MethodType.ClientStreaming,
                (false, true) => MethodType.ServerStreaming,
                _ => MethodType.Unary
            },
            _serviceName,
            _methodName,
            Marshallers.Create(static data => data, static data => data),
            Marshallers.Create(static data => data, static data => data));

        // Start the gRPC call pump
        _ = Task.Run(async () =>
        {
            try
            {
                if (IsClientStreaming && IsServerStreaming)
                    await RunDuplexAsync(rawMethod);
                else if (IsClientStreaming)
                    await RunClientStreamingAsync(rawMethod);
            }
            catch (OperationCanceledException)
            {
                // Expected on close
            }
            catch (RpcException ex)
            {
                await _responses.Writer.WriteAsync($"{{\"error\":\"{EscapeJson(ex.Status.Detail)}\"}}");
            }
            catch (Exception ex)
            {
                await _responses.Writer.WriteAsync($"{{\"error\":\"{EscapeJson(ex.Message)}\"}}");
            }
            finally
            {
                _responses.Writer.TryComplete();
            }
        }, _cts.Token);
    }

    private CallOptions BuildCallOptions(CancellationToken ct)
    {
        var headers = new Metadata();
        if (_metadata is not null)
        {
            foreach (var (key, value) in _metadata)
                headers.Add(key, value);
        }
        return new CallOptions(headers: headers, cancellationToken: ct);
    }

    /// <summary>
    /// Resolve the gRPC method descriptors via reflection, open the channel,
    /// and return a fully-wired <see cref="GrpcBowireChannel"/>.
    /// <para>
    /// Returns <c>null</c> when the caller asks for gRPC-Web transport (via
    /// <see cref="BowireGrpcProtocol.TransportMetadataKey"/>). gRPC-Web's
    /// HTTP/1.1 framing can't carry duplex traffic, and even
    /// <c>GrpcWebMode.GrpcWebText</c>'s client-streaming round-trip behaves
    /// inconsistently across servers (Envoy, grpc-web-proxy, ASP.NET's
    /// <c>UseGrpcWeb</c> all disagree on how to flush request frames). The
    /// Bowire workbench surfaces this back to the UI as "duplex not
    /// supported on gRPC-Web" rather than silently buffering forever.
    /// Future tracks may revisit once the spec stabilises upstream.
    /// </para>
    /// </summary>
    public static async Task<GrpcBowireChannel?> CreateAsync(
        string serverUrl,
        string serviceName,
        string methodName,
        bool showInternalServices,
        Dictionary<string, string>? metadata,
        CancellationToken ct,
        IConfiguration? configuration = null)
    {
        var transportMode = GrpcChannelBuilder.ResolveMode(metadata);
        if (transportMode == GrpcTransportMode.Web)
        {
            // Surface a null channel; BowireGrpcProtocol.OpenChannelAsync
            // returns it to the workbench unchanged. Bowire's channel
            // endpoint already treats null as "this method/transport
            // combination isn't supported by the plugin" and ships a
            // friendly error to the UI.
            return null;
        }

        var sanitisedMetadata = GrpcChannelBuilder.StripTransportMarker(metadata);
        using var reflectionClient = new GrpcReflectionClient(
            serverUrl, showInternalServices, mtlsConfig: null, configuration: configuration);

        var fileDescProtos = await reflectionClient.ResolveAllDescriptorsAsync(serviceName, ct);
        if (fileDescProtos.Count == 0)
            throw new InvalidOperationException($"No file descriptors for '{serviceName}'.");

        var fileDescriptors = GrpcInvoker.BuildFileDescriptorsPublic(fileDescProtos);
        if (fileDescriptors.Count == 0)
            throw new InvalidOperationException($"Failed to build FileDescriptors for '{serviceName}'.");

        ServiceDescriptor? svcDesc = null;
        foreach (var fd in fileDescriptors)
        {
            svcDesc = fd.Services.FirstOrDefault(s => s.FullName == serviceName);
            if (svcDesc is not null) break;
        }

        if (svcDesc is null)
            throw new InvalidOperationException($"Service '{serviceName}' not found.");

        var methodDesc = svcDesc.Methods.FirstOrDefault(m => m.Name == methodName)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found.");

        var inputType = methodDesc.InputType
            ?? throw new InvalidOperationException($"InputType is null for {serviceName}/{methodName}.");
        var outputType = methodDesc.OutputType
            ?? throw new InvalidOperationException($"OutputType is null for {serviceName}/{methodName}.");

#pragma warning disable CA2000
        // Ownership of the SocketsHttpHandler transfers to the
        // GrpcBowireChannel on success (disposed by DisposeAsync); on the
        // failure path the catch arm disposes it before re-throwing.
        // Same shape as MtlsHandlerOwner.CreateSocketsHttpHandler — the
        // analyzer can't follow ownership across the try/catch + factory
        // hand-off.
        var handler = BowireHttpClientFactory.CreateSocketsHttpHandler(configuration, "grpc", serverUrl);
#pragma warning restore CA2000
        GrpcChannel? grpcChannel = null;
        try
        {
            grpcChannel = GrpcChannelBuilder.BuildChannel(serverUrl, handler, GrpcTransportMode.Native);
            return new GrpcBowireChannel(
                grpcChannel,
                handler,
                serviceName,
                methodName,
                inputType,
                outputType,
                methodDesc.IsClientStreaming,
                methodDesc.IsServerStreaming,
                sanitisedMetadata,
                ct);
        }
        catch
        {
            grpcChannel?.Dispose();
            handler.Dispose();
            throw;
        }
    }

    public Task<bool> SendAsync(string jsonMessage, CancellationToken ct)
    {
        if (IsClosed) return Task.FromResult(false);

        var bytes = GrpcInvoker.JsonToProtobufPublic(jsonMessage, _inputType);
        _outgoing.Writer.TryWrite(bytes);
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
        _grpcChannel.Dispose();
        _ownedHandler.Dispose();
    }

    private async Task RunDuplexAsync(Method<byte[], byte[]> rawMethod)
    {
        var token = _cts.Token;
        using var call = _grpcChannel.CreateCallInvoker()
            .AsyncDuplexStreamingCall(rawMethod, null, BuildCallOptions(token));

        var sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in _outgoing.Reader.ReadAllAsync(token))
                    await call.RequestStream.WriteAsync(msg);
                await call.RequestStream.CompleteAsync();
            }
            catch (OperationCanceledException) { }
        }, token);

        await foreach (var responseBytes in call.ResponseStream.ReadAllAsync(token))
        {
            var json = GrpcInvoker.FormatResponsePublic(responseBytes, _outputType);
            await _responses.Writer.WriteAsync(json);
        }

        await sendTask;
    }

    private async Task RunClientStreamingAsync(Method<byte[], byte[]> rawMethod)
    {
        var token = _cts.Token;
        using var call = _grpcChannel.CreateCallInvoker()
            .AsyncClientStreamingCall(rawMethod, null, BuildCallOptions(token));

        await foreach (var msg in _outgoing.Reader.ReadAllAsync(token))
            await call.RequestStream.WriteAsync(msg);

        await call.RequestStream.CompleteAsync();

        var responseBytes = await call.ResponseAsync;
        var json = GrpcInvoker.FormatResponsePublic(responseBytes, _outputType);
        await _responses.Writer.WriteAsync(json);
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
