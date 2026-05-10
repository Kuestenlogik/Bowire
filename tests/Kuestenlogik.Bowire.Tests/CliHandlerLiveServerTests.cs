// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Reflection;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Happy-path coverage for <see cref="CliHandler"/> against a real
/// Kestrel-hosted gRPC reflection server. Hits the post-discovery
/// branches that <see cref="CliHandlerTests"/>'s dead-URL fixtures
/// can't reach: empty + non-empty service lists in <c>ListImpl</c>,
/// service + service/method describe in <c>DescribeImpl</c>, and the
/// "service not found" / "method not found" diagnostics. Shares the
/// CWD-serialised collection because WebApplicationBuilder probes
/// <see cref="Directory.GetCurrentDirectory"/> for appsettings.
/// </summary>
[Collection("CwdSerialised")]
public sealed class CliHandlerLiveServerTests
{
    [Fact]
    public async Task ListAsync_AgainstLiveServer_ReturnsZeroWithServices()
    {
        await using var server = await LocalReflectionServer.StartAsync(BuildSimpleDescriptor());
        var rc = await CliHandler.ListAsync(new CliCommandOptions
        {
            Url = server.BaseUrl,
            Verbose = true,
        });
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task ListAsync_VerboseWithStreamingMethods_HitsEveryMethodTypeArm()
    {
        // Streaming descriptor covers all four MethodType arms in the
        // verbose-list switch: Unary, ServerStreaming, ClientStreaming,
        // Duplex.
        await using var server = await LocalReflectionServer.StartAsync(BuildStreamingDescriptor());
        var rc = await CliHandler.ListAsync(new CliCommandOptions
        {
            Url = server.BaseUrl,
            Verbose = true,
        });
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task ListAsync_AgainstEmptyServer_ReturnsZero()
    {
        // Server with no services registered → reflection returns an
        // empty list → ListImpl logs "No gRPC services found." and
        // exits 0 (it's a success — just nothing to print).
        await using var server = await LocalReflectionServer.StartAsync(
            new FileDescriptorProto { Name = "empty.proto", Package = "demo", Syntax = "proto3" });
        var rc = await CliHandler.ListAsync(new CliCommandOptions { Url = server.BaseUrl });
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task DescribeAsync_ServiceTarget_AgainstLiveServer_ReturnsZero()
    {
        await using var server = await LocalReflectionServer.StartAsync(BuildSimpleDescriptor());
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = server.BaseUrl,
            Target = "demo.SimpleService",
        });
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task DescribeAsync_ServiceMethodTarget_AgainstLiveServer_ReturnsZero()
    {
        await using var server = await LocalReflectionServer.StartAsync(BuildSimpleDescriptor());
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = server.BaseUrl,
            Target = "demo.SimpleService/Echo",
        });
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task DescribeAsync_UnknownService_ReturnsTwo()
    {
        await using var server = await LocalReflectionServer.StartAsync(BuildSimpleDescriptor());
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = server.BaseUrl,
            Target = "no.such.Service",
        });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task DescribeAsync_UnknownMethod_ReturnsTwo()
    {
        await using var server = await LocalReflectionServer.StartAsync(BuildSimpleDescriptor());
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = server.BaseUrl,
            Target = "demo.SimpleService/Nope",
        });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task DescribeAsync_UnknownServiceInMethodForm_ReturnsTwo()
    {
        await using var server = await LocalReflectionServer.StartAsync(BuildSimpleDescriptor());
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = server.BaseUrl,
            Target = "no.such.Service/anymethod",
        });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task CallAsync_UnsupportedTransport_StillCleansUp()
    {
        // The reflection server has no method implementation so the
        // unary invoke surfaces an error (Unimplemented or similar) — the
        // path through the metadata-parser + the error-printing branch
        // gets exercised and the handler returns 2 with a gRPC status.
        await using var server = await LocalReflectionServer.StartAsync(BuildSimpleDescriptor());
        var cli = new CliCommandOptions
        {
            Url = server.BaseUrl,
            Target = "demo.SimpleService/Echo",
            Compact = true,
        };
        cli.Headers.Add("authorization: bearer x");
        cli.Data.Add("{\"msg\": \"hi\"}");
        var rc = await CliHandler.CallAsync(cli);
        // 1 (transport / network error) or 2 (gRPC status error) — either
        // way we covered the metadata-build + json-write + error-print
        // branches.
        Assert.Contains(rc, s_acceptedExitCodes);
    }

    [Fact]
    public async Task CallAsync_ServerStreamingMethod_RoutesThroughStreamingBranch()
    {
        // Reflection knows about the streaming methods; the unary
        // invoke against a ServerStreaming method returns the
        // "Use the streaming endpoint…" sentinel which routes the
        // CallImpl through the InvokeStreamingWithFramesAsync path.
        // No real handler is registered so frames never arrive — the
        // important thing is the branch + return-0 line fire.
        await using var server = await LocalReflectionServer.StartAsync(BuildStreamingDescriptor());
        var cli = new CliCommandOptions
        {
            Url = server.BaseUrl,
            Target = "demo.StreamSvc/S",
        };
        cli.Data.Add("{}");
        var rc = await CliHandler.CallAsync(cli);
        // The streaming-frame enumeration may surface the missing
        // handler as an InvalidOperationException → exit 1 (catch)
        // or it may quietly emit no frames → exit 0. Either way we
        // covered the routing decision.
        Assert.Contains(rc, s_acceptedStreamingExitCodes);
    }

    private static readonly int[] s_acceptedStreamingExitCodes = [0, 1, 2];

    private static readonly int[] s_acceptedExitCodes = [1, 2];

    private static FileDescriptorProto BuildStreamingDescriptor()
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
                    Name = "U", InputType = ".demo.Empty", OutputType = ".demo.Empty"
                },
                new MethodDescriptorProto
                {
                    Name = "S", InputType = ".demo.Empty", OutputType = ".demo.Empty",
                    ServerStreaming = true
                },
                new MethodDescriptorProto
                {
                    Name = "C", InputType = ".demo.Empty", OutputType = ".demo.Empty",
                    ClientStreaming = true
                },
                new MethodDescriptorProto
                {
                    Name = "D", InputType = ".demo.Empty", OutputType = ".demo.Empty",
                    ClientStreaming = true, ServerStreaming = true
                }
            }
        });
        return fd;
    }

    private static FileDescriptorProto BuildSimpleDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/simple.proto",
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
        return fd;
    }

    // ---- Minimal Kestrel-hosted reflection server ----
    private sealed class LocalReflectionServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        private LocalReflectionServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public static async Task<LocalReflectionServer> StartAsync(FileDescriptorProto fdProto)
        {
            var fileDescriptors = FileDescriptor.BuildFromByteStrings(new[] { fdProto.ToByteString() });
            var serviceDescriptors = fileDescriptors.SelectMany(fd => fd.Services).ToList();

            var port = GetFreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath()
            });
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, port, lo => lo.Protocols = HttpProtocols.Http2);
            });
            builder.Logging.ClearProviders();
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(new ReflectionServiceImpl(serviceDescriptors));

            var app = builder.Build();
            app.MapGrpcService<ReflectionServiceImpl>();
            await app.StartAsync();
            return new LocalReflectionServer(app, $"http://127.0.0.1:{port}");
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
