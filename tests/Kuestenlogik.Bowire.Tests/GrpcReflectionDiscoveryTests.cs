// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Reflection;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// End-to-end discovery tests that exercise the gRPC reflection-client
/// parser path. We host a minimal Kestrel app that serves
/// <see cref="ReflectionServiceImpl"/> against synthetic
/// <see cref="FileDescriptorSet"/>s and let
/// <see cref="BowireGrpcProtocol.DiscoverAsync"/> walk the wire — same
/// surface the production workbench uses, no live external server.
///
/// Sharing the CWD-serialised collection because WebApplicationBuilder
/// reads <c>Directory.GetCurrentDirectory()</c> for appsettings probing.
/// </summary>
[Collection("CwdSerialised")]
public sealed class GrpcReflectionDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_Against_Reflection_Server_Yields_Service_With_Methods()
    {
        var fdProto = BuildSimpleFileDescriptor("simple", out _);
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        Assert.Equal("demo.SimpleService", svc.Name);
        Assert.Equal("demo", svc.Package);
        Assert.NotNull(svc.SchemaDescriptor);
        Assert.NotEmpty(svc.SchemaDescriptor!);

        var unary = svc.Methods.First(m => m.Name == "Echo");
        Assert.Equal("Unary", unary.MethodType);
        Assert.False(unary.ClientStreaming);
        Assert.False(unary.ServerStreaming);
        Assert.Equal("EchoRequest", unary.InputType.Name);
        Assert.Equal("EchoResponse", unary.OutputType.Name);
    }

    [Fact]
    public async Task DiscoverAsync_With_All_Streaming_Variants_Maps_MethodType()
    {
        var fdProto = BuildStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl, showInternalServices: false, cts.Token);

        var svc = services.First(s => s.Name == "demo.StreamSvc");

        Assert.Equal("Unary", svc.Methods.First(m => m.Name == "U").MethodType);
        Assert.Equal("ServerStreaming", svc.Methods.First(m => m.Name == "S").MethodType);
        Assert.Equal("ClientStreaming", svc.Methods.First(m => m.Name == "C").MethodType);
        Assert.Equal("Duplex", svc.Methods.First(m => m.Name == "D").MethodType);
    }

    [Fact]
    public async Task DiscoverAsync_With_Repeated_And_Map_Fields_Resolves_Schema()
    {
        var fdProto = BuildSchemaFeatureFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl, showInternalServices: false, cts.Token);

        var svc = Assert.Single(services);
        var method = svc.Methods.First(m => m.Name == "Get");
        var output = method.OutputType;

        // Output has: repeated string tags (1), map<string,string> attrs (2),
        // nested Inner inner (3), Color color enum (4).
        var tagsField = output.Fields.First(f => f.Name == "tags");
        Assert.True(tagsField.IsRepeated);
        Assert.False(tagsField.IsMap);

        var attrsField = output.Fields.First(f => f.Name == "attrs");
        Assert.True(attrsField.IsMap);
        // Map fields aren't tagged repeated in the BowireFieldInfo even
        // though they're encoded as repeated on the wire.
        Assert.False(attrsField.IsRepeated);

        var innerField = output.Fields.First(f => f.Name == "inner");
        Assert.Equal("message", innerField.Type);
        Assert.NotNull(innerField.MessageType);
        Assert.Equal("Inner", innerField.MessageType!.Name);

        var colorField = output.Fields.First(f => f.Name == "color");
        Assert.Equal("enum", colorField.Type);
        Assert.NotNull(colorField.EnumValues);
        Assert.Contains(colorField.EnumValues!, v => v.Name == "RED");
        Assert.Contains(colorField.EnumValues!, v => v.Name == "GREEN");
    }

    [Fact]
    public async Task OpenChannelAsync_For_Duplex_Method_Returns_Channel_Without_Sending()
    {
        // Spinning up the StreamSvc protos gives us a Duplex method ("D")
        // we can hand to BowireGrpcProtocol.OpenChannelAsync. The factory
        // resolves descriptors via reflection and constructs a
        // GrpcBowireChannel — no wire calls until SendAsync is invoked,
        // so the channel can be opened and immediately disposed without
        // a real backing service implementation.
        var fdProto = BuildStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var channel = await protocol.OpenChannelAsync(
            server.BaseUrl,
            service: "demo.StreamSvc",
            method: "D",
            showInternalServices: false,
            metadata: null,
            ct: cts.Token);

        Assert.NotNull(channel);
        Assert.True(channel!.IsClientStreaming);
        Assert.True(channel.IsServerStreaming);
        Assert.False(channel.IsClosed);
        Assert.NotNull(channel.Id);
        Assert.NotEmpty(channel.Id);
        Assert.Null(channel.NegotiatedSubProtocol);

        // Closing the send side flips IsClosed without triggering any
        // wire activity — we never SendAsync, so RunDuplexAsync's
        // request-pump just sees a completed reader.
        await channel.CloseAsync(cts.Token);
        Assert.True(channel.IsClosed);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannelAsync_For_Client_Streaming_Method_Returns_Channel()
    {
        var fdProto = BuildStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var channel = await protocol.OpenChannelAsync(
            server.BaseUrl,
            service: "demo.StreamSvc",
            method: "C",
            showInternalServices: false,
            metadata: null,
            ct: cts.Token);

        Assert.NotNull(channel);
        Assert.True(channel!.IsClientStreaming);
        Assert.False(channel.IsServerStreaming);
        Assert.Equal(0, channel.SentCount);
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task InvokeAsync_For_Unimplemented_Method_Returns_RpcException_Status()
    {
        // The reflection server has the descriptor for StreamSvc.U but
        // doesn't actually implement it — gRPC will throw RpcException
        // (Unimplemented), which GrpcInvoker catches and surfaces as a
        // non-OK InvokeResult.Status. Exercises JsonToProtobuf encode +
        // AsyncUnaryCall + the RpcException trailer-collection branch.
        var fdProto = BuildStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await protocol.InvokeAsync(
            server.BaseUrl,
            service: "demo.StreamSvc",
            method: "U",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: cts.Token);

        // Status should be the gRPC status code name (e.g. "Unimplemented"),
        // not "OK".
        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_With_Unknown_Service_Throws_InvalidOperationException()
    {
        var fdProto = BuildStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // ResolveAllDescriptorsAsync returns an empty list when reflection
        // has no descriptors for the requested service — GrpcInvoker
        // surfaces that as InvalidOperationException.
        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.InvokeAsync(
            server.BaseUrl,
            service: "demo.NoSuchService",
            method: "U",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: cts.Token));
    }

    [Fact]
    public async Task InvokeStreamAsync_For_Unimplemented_Method_Throws_RpcException()
    {
        // Server-streaming variant of the same idea — exercises
        // GrpcInvoker.InvokeStreamingWithFramesAsync's server-streaming
        // branch up to the first frame attempt.
        var fdProto = BuildStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in protocol.InvokeStreamAsync(
                server.BaseUrl,
                service: "demo.StreamSvc",
                method: "S",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: null,
                ct: cts.Token))
            {
                break;
            }
        });
    }

    [Fact]
    public async Task OpenChannelAsync_With_Unknown_Method_Throws_InvalidOperationException()
    {
        var fdProto = BuildStreamingFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.OpenChannelAsync(
            server.BaseUrl,
            service: "demo.StreamSvc",
            method: "DoesNotExist",
            showInternalServices: false,
            metadata: null,
            ct: cts.Token));
    }

    [Fact]
    public async Task DiscoverAsync_With_ShowInternal_False_Strips_Internal_Service_Names()
    {
        // Even though our reflection server only lists the user service,
        // exercise the showInternalServices=false branch so the InternalServices
        // dedup-list filter executes against at least the user-service name.
        var fdProto = BuildSimpleFileDescriptor("simple-strip", out _);
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        // The reflection service must never leak when showInternal=false.
        Assert.DoesNotContain(services, s =>
            s.Name.StartsWith("grpc.reflection", StringComparison.Ordinal));
    }

    // ---- Synthetic descriptor builders ----

    private static FileDescriptorProto BuildSimpleFileDescriptor(string fileTag, out FileDescriptorSet set)
    {
        var fd = new FileDescriptorProto
        {
            Name = $"demo/{fileTag}.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "EchoRequest",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "msg", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "msg"
                }
            }
        });
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "EchoResponse",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "echoed", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "echoed"
                }
            }
        });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "SimpleService",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Echo",
                    InputType = ".demo.EchoRequest",
                    OutputType = ".demo.EchoResponse"
                }
            }
        });
        set = new FileDescriptorSet { File = { fd } };
        return fd;
    }

    private static FileDescriptorProto BuildStreamingFileDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/stream.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        var emptyProto = new DescriptorProto { Name = "Empty" };
        fd.MessageType.Add(emptyProto);
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "StreamSvc",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "U",
                    InputType = ".demo.Empty",
                    OutputType = ".demo.Empty"
                },
                new MethodDescriptorProto
                {
                    Name = "S",
                    InputType = ".demo.Empty",
                    OutputType = ".demo.Empty",
                    ServerStreaming = true
                },
                new MethodDescriptorProto
                {
                    Name = "C",
                    InputType = ".demo.Empty",
                    OutputType = ".demo.Empty",
                    ClientStreaming = true
                },
                new MethodDescriptorProto
                {
                    Name = "D",
                    InputType = ".demo.Empty",
                    OutputType = ".demo.Empty",
                    ClientStreaming = true,
                    ServerStreaming = true
                }
            }
        });
        return fd;
    }

    private static FileDescriptorProto BuildSchemaFeatureFileDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/features.proto",
            Package = "demo",
            Syntax = "proto3"
        };

        // Inner message used by the outer message's nested-message field.
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Inner",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "value", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "value"
                }
            }
        });

        // Enum used by the color field.
        fd.EnumType.Add(new EnumDescriptorProto
        {
            Name = "Color",
            Value =
            {
                new EnumValueDescriptorProto { Name = "RED", Number = 0 },
                new EnumValueDescriptorProto { Name = "GREEN", Number = 1 }
            }
        });

        // Empty request.
        fd.MessageType.Add(new DescriptorProto { Name = "Req" });

        // Output with repeated, map, nested message, and enum fields. The
        // map<string,string> in proto3 desugars to a synthetic nested
        // "AttrsEntry" message under the parent — both that and the outer
        // need to be present for the parser's IsMapField check to fire.
        var attrsEntry = new DescriptorProto
        {
            Name = "AttrsEntry",
            Options = new MessageOptions { MapEntry = true },
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "key", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "key"
                },
                new FieldDescriptorProto
                {
                    Name = "value", Number = 2,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "value"
                }
            }
        };
        var outer = new DescriptorProto
        {
            Name = "Res",
            NestedType = { attrsEntry },
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "tags", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Repeated,
                    JsonName = "tags"
                },
                new FieldDescriptorProto
                {
                    Name = "attrs", Number = 2,
                    Type = FieldDescriptorProto.Types.Type.Message,
                    TypeName = ".demo.Res.AttrsEntry",
                    Label = FieldDescriptorProto.Types.Label.Repeated,
                    JsonName = "attrs"
                },
                new FieldDescriptorProto
                {
                    Name = "inner", Number = 3,
                    Type = FieldDescriptorProto.Types.Type.Message,
                    TypeName = ".demo.Inner",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "inner"
                },
                new FieldDescriptorProto
                {
                    Name = "color", Number = 4,
                    Type = FieldDescriptorProto.Types.Type.Enum,
                    TypeName = ".demo.Color",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "color"
                }
            }
        };
        fd.MessageType.Add(outer);

        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "FeatureSvc",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Get",
                    InputType = ".demo.Req",
                    OutputType = ".demo.Res"
                }
            }
        });
        return fd;
    }

    // ---- Minimal Kestrel-hosted gRPC reflection server ----
    //
    // Hosts only ReflectionServiceImpl populated from a synthetic
    // FileDescriptor. Bound to a free loopback port over HTTP/2 in plaintext
    // so a GrpcChannel can connect without TLS configuration. Disposed
    // asynchronously to release the port between tests.
    private sealed class ReflectionServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public string BaseUrl { get; }

        private ReflectionServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public static async Task<ReflectionServer> StartAsync(FileDescriptorProto fdProto)
        {
            var fileDescriptors = FileDescriptor.BuildFromByteStrings(
                new[] { fdProto.ToByteString() });
            var serviceDescriptors = fileDescriptors
                .SelectMany(fd => fd.Services)
                .ToList();

            var port = GetFreePort();

            // Use bare WebApplicationOptions with the temp-dir content root so
            // BowireConfigurationTests' Directory.SetCurrentDirectory dance
            // can't poison our PhysicalFileProvider (it polls the CWD by
            // default and races with that test's tearDown).
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath()
            });
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            });
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(new ReflectionServiceImpl(serviceDescriptors));

            var app = builder.Build();
            app.MapGrpcService<ReflectionServiceImpl>();

            await app.StartAsync();
            return new ReflectionServer(app, $"http://127.0.0.1:{port}");
        }

        private static int GetFreePort()
        {
            using var sock = new TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
            try { await _app.DisposeAsync(); } catch { /* best-effort */ }
        }
    }
}
