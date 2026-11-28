// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// #1069 — a reflection service that fails the stream for a file it does not
/// know, as tonic-reflection (Rust) does. Bowire asked it for a file named
/// like the service, and every call to a tonic server failed.
/// </summary>
public sealed class GrpcTonicReflectionTests
{
    [Fact]
    public async Task Resolving_a_service_asks_only_for_files_that_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await TonicLikeReflection.StartAsync(
            [Google.Protobuf.WellKnownTypes.EmptyReflection.Descriptor, Kuestenlogik.Bowire.Protocol.Grpc.Tests.Proto.GapgreeterReflection.Descriptor]);
        using var client = new GrpcReflectionClient(server.BaseUrl, showInternalServices: false,
            mtlsConfig: null, configuration: null, transportMode: GrpcTransportMode.Native);

        var files = await client.ResolveAllDescriptorsAsync("gaptest.GapGreeter", ct);

        Assert.Contains(files, f => f.Name == Kuestenlogik.Bowire.Protocol.Grpc.Tests.Proto.GapgreeterReflection.Descriptor.Name);
        Assert.DoesNotContain(server.FilesAskedFor, name => name == "gaptest.GapGreeter");
    }

    /// <summary>Serves reflection the way tonic-reflection does: an unknown file fails the stream with NOT_FOUND.</summary>
    private sealed class TonicLikeReflection : ServerReflection.ServerReflectionBase, IAsyncDisposable
    {
        private readonly Dictionary<string, FileDescriptor> _files;
        private readonly Dictionary<string, FileDescriptor> _symbols;
        private WebApplication? _app;

        public ConcurrentBag<string> FilesAskedFor { get; } = [];
        public string BaseUrl { get; private set; } = "";

        private TonicLikeReflection(IEnumerable<FileDescriptor> files)
        {
            _files = files.ToDictionary(f => f.Name, StringComparer.Ordinal);
            _symbols = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal);
            foreach (var file in _files.Values)
            {
                foreach (var s in file.Services) _symbols[s.FullName] = file;
                foreach (var m in file.MessageTypes) _symbols[m.FullName] = file;
            }
        }

        public static async Task<TonicLikeReflection> StartAsync(IEnumerable<FileDescriptor> files)
        {
            var reflection = new TonicLikeReflection(files);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.GetTempPath() });
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(reflection);
            var app = builder.Build();
            app.MapGrpcService<TonicLikeReflection>();
            await app.StartAsync(TestContext.Current.CancellationToken);
            reflection._app = app;
            reflection.BaseUrl = LoopbackHost.BaseAddress(app.Services);
            return reflection;
        }

        public override async Task ServerReflectionInfo(
            IAsyncStreamReader<ServerReflectionRequest> requestStream,
            IServerStreamWriter<ServerReflectionResponse> responseStream,
            ServerCallContext context)
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                FileDescriptor? file;
                switch (request.MessageRequestCase)
                {
                    case ServerReflectionRequest.MessageRequestOneofCase.FileByFilename:
                        FilesAskedFor.Add(request.FileByFilename);
                        if (!_files.TryGetValue(request.FileByFilename, out file))
                            throw new RpcException(new Status(StatusCode.NotFound, $"file '{request.FileByFilename}' not found"));
                        break;
                    case ServerReflectionRequest.MessageRequestOneofCase.FileContainingSymbol:
                        if (!_symbols.TryGetValue(request.FileContainingSymbol, out file))
                            throw new RpcException(new Status(StatusCode.NotFound, $"symbol '{request.FileContainingSymbol}' not found"));
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.Unimplemented, "not in this test"));
                }
                var response = new ServerReflectionResponse { FileDescriptorResponse = new FileDescriptorResponse() };
                response.FileDescriptorResponse.FileDescriptorProto.Add(file.SerializedData);
                await responseStream.WriteAsync(response, context.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is null) return;
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
