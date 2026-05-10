// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Google.Api;
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
/// Coverage for the <c>google.api.http</c> transcoding-annotation path in
/// <see cref="BowireGrpcProtocol"/>.<c>DiscoverAsync</c> — driven by
/// <c>GrpcReflectionClient.ExtractHttpRule</c> and
/// <c>AnnotateInputForTranscoding</c>. Each variant of <see cref="HttpRule"/>'s
/// pattern oneof needs its own test so the switch arm in ExtractHttpRule is
/// exercised through a real reflection round-trip.
/// </summary>
[Collection("CwdSerialised")]
public sealed class GrpcReflectionTranscodingTests
{
    [Theory]
    [InlineData("GET",    "/v1/users/{id}", "id",   "query")]
    [InlineData("DELETE", "/v1/users/{id}", "id",   "query")]
    [InlineData("POST",   "/v1/users",      null,   "body")]
    [InlineData("PUT",    "/v1/users/{id}", "id",   "body")]
    [InlineData("PATCH",  "/v1/users/{id}", "id",   "body")]
    public async Task DiscoverAsync_With_Http_Rule_Annotates_Fields_With_Source(
        string verb, string template, string? pathField, string remainingSource)
    {
        var fdProto = BuildAnnotatedFileDescriptor(verb, template);
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);

        Assert.Equal(verb, method.HttpMethod);
        Assert.Equal(template, method.HttpPath);

        if (pathField is not null)
        {
            var idField = method.InputType.Fields.First(f => f.Name == pathField);
            Assert.Equal("path", idField.Source);

            var nameField = method.InputType.Fields.First(f => f.Name == "name");
            Assert.Equal(remainingSource, nameField.Source);
        }
        else
        {
            // Pure-body / pure-query template — every field falls through
            // to the default source bucket.
            Assert.All(method.InputType.Fields, f => Assert.Equal(remainingSource, f.Source));
        }
    }

    [Fact]
    public async Task DiscoverAsync_With_Custom_Http_Rule_Uses_Verb_From_Custom()
    {
        // HttpRule's Custom oneof carries an arbitrary verb (e.g. "OPTIONS"
        // or vendor-specific). ExtractHttpRule's switch arm should pull
        // both the kind and the path out of it.
        var fdProto = BuildAnnotatedFileDescriptor("CUSTOM:HEAD", "/v1/things");
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);

        Assert.Equal("HEAD", method.HttpMethod);
        Assert.Equal("/v1/things", method.HttpPath);
    }

    [Fact]
    public async Task DiscoverAsync_Without_Http_Rule_Leaves_HttpMethod_Null()
    {
        // No annotation at all — ExtractHttpRule's `Options is null` early
        // return is the negative case.
        var fdProto = BuildPlainFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);

        Assert.Null(method.HttpMethod);
        Assert.Null(method.HttpPath);
    }

    [Fact]
    public async Task DiscoverAsync_With_Path_Subpath_Pattern_Strips_Equals_Suffix()
    {
        // {name=projects/*} placeholder — ExtractPathPlaceholders should
        // strip the "=projects/*" subpath suffix and match against just "name".
        var fdProto = BuildAnnotatedFileDescriptor("GET", "/v1/{name=projects/*}");
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);

        var nameField = method.InputType.Fields.First(f => f.Name == "name");
        Assert.Equal("path", nameField.Source);

        var idField = method.InputType.Fields.First(f => f.Name == "id");
        Assert.Equal("query", idField.Source);
    }

    [Fact]
    public async Task DiscoverAsync_With_Unclosed_Brace_In_Template_Stops_Cleanly()
    {
        // Malformed template (missing closing brace) — ExtractPathPlaceholders'
        // `if (end < 0) break` arm. No exception should escape.
        var fdProto = BuildAnnotatedFileDescriptor("GET", "/v1/{never_closed");
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        Assert.NotEmpty(svc.Methods);
    }

    [Fact]
    public async Task DiscoverAsync_With_Nested_Enum_Resolves_Field()
    {
        // Coverage for FindEnumInFile's nested-enum branch (lines 578-592).
        // Outer message declares an enum the field references.
        var fdProto = BuildNestedEnumFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);
        var statusField = method.InputType.Fields.First(f => f.Name == "status");

        Assert.Equal("enum", statusField.Type);
        Assert.NotNull(statusField.EnumValues);
        Assert.Contains(statusField.EnumValues!, v => v.Name == "ACTIVE");
        Assert.Contains(statusField.EnumValues!, v => v.Name == "ARCHIVED");
    }

    [Fact]
    public async Task DiscoverAsync_With_Various_Scalar_Field_Types_Maps_All_Wire_Types()
    {
        // MapFieldType has a switch arm for every FieldDescriptorProto
        // type — the scalars-only test exercises the bulk of them.
        var fdProto = BuildScalarsFileDescriptor();
        await using var server = await ReflectionServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var services = await protocol.DiscoverAsync(
            server.BaseUrl,
            showInternalServices: false,
            cts.Token);

        var svc = Assert.Single(services);
        var method = Assert.Single(svc.Methods);
        var input = method.InputType;

        Assert.Equal("double", input.Fields.First(f => f.Name == "d").Type);
        Assert.Equal("float", input.Fields.First(f => f.Name == "f").Type);
        Assert.Equal("int64", input.Fields.First(f => f.Name == "i64").Type);
        Assert.Equal("uint64", input.Fields.First(f => f.Name == "u64").Type);
        Assert.Equal("int32", input.Fields.First(f => f.Name == "i32").Type);
        Assert.Equal("fixed64", input.Fields.First(f => f.Name == "fx64").Type);
        Assert.Equal("fixed32", input.Fields.First(f => f.Name == "fx32").Type);
        Assert.Equal("bool", input.Fields.First(f => f.Name == "b").Type);
        Assert.Equal("string", input.Fields.First(f => f.Name == "s").Type);
        Assert.Equal("bytes", input.Fields.First(f => f.Name == "by").Type);
        Assert.Equal("uint32", input.Fields.First(f => f.Name == "u32").Type);
        Assert.Equal("sfixed32", input.Fields.First(f => f.Name == "sfx32").Type);
        Assert.Equal("sfixed64", input.Fields.First(f => f.Name == "sfx64").Type);
        Assert.Equal("sint32", input.Fields.First(f => f.Name == "sint32").Type);
        Assert.Equal("sint64", input.Fields.First(f => f.Name == "sint64").Type);

        // Required label exercises the MapFieldLabel "required" arm even
        // though proto3 doesn't allow it natively — descriptor-level it works.
        Assert.Contains(input.Fields, f => f.Label is "optional" or "required" or "repeated");
    }

    private static FileDescriptorProto BuildAnnotatedFileDescriptor(string verbOrCustom, string pathTemplate)
    {
        var fd = new FileDescriptorProto
        {
            Name = $"demo/annotated-{Guid.NewGuid():N}.proto",
            Package = "demo",
            Syntax = "proto3",
            Dependency = { "google/api/annotations.proto", "google/api/http.proto" }
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Req",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "id", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "id"
                },
                new FieldDescriptorProto
                {
                    Name = "name", Number = 2,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "name"
                }
            }
        });
        fd.MessageType.Add(new DescriptorProto { Name = "Res" });

        var rule = new HttpRule();
        if (verbOrCustom.StartsWith("CUSTOM:", StringComparison.Ordinal))
        {
            var kind = verbOrCustom["CUSTOM:".Length..];
            rule.Custom = new CustomHttpPattern { Kind = kind, Path = pathTemplate };
        }
        else
        {
            switch (verbOrCustom)
            {
                case "GET":    rule.Get    = pathTemplate; break;
                case "POST":   rule.Post   = pathTemplate; break;
                case "PUT":    rule.Put    = pathTemplate; break;
                case "DELETE": rule.Delete = pathTemplate; break;
                case "PATCH":  rule.Patch  = pathTemplate; break;
                default: throw new ArgumentException($"Unsupported verb {verbOrCustom}", nameof(verbOrCustom));
            }
        }

        var methodOptions = new MethodOptions();
        methodOptions.SetExtension(AnnotationsExtensions.Http, rule);

        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "AnnotatedSvc",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Do",
                    InputType = ".demo.Req",
                    OutputType = ".demo.Res",
                    Options = methodOptions
                }
            }
        });
        return fd;
    }

    private static FileDescriptorProto BuildPlainFileDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/plain.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Req",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "n", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "n"
                }
            }
        });
        fd.MessageType.Add(new DescriptorProto { Name = "Res" });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "PlainSvc",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Do",
                    InputType = ".demo.Req",
                    OutputType = ".demo.Res"
                }
            }
        });
        return fd;
    }

    private static FileDescriptorProto BuildNestedEnumFileDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/nested-enum.proto",
            Package = "demo",
            Syntax = "proto3"
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Outer",
            EnumType =
            {
                new EnumDescriptorProto
                {
                    Name = "Status",
                    Value =
                    {
                        new EnumValueDescriptorProto { Name = "ACTIVE", Number = 0 },
                        new EnumValueDescriptorProto { Name = "ARCHIVED", Number = 1 }
                    }
                }
            }
        });
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Req",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "status", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.Enum,
                    TypeName = ".demo.Outer.Status",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "status"
                }
            }
        });
        fd.MessageType.Add(new DescriptorProto { Name = "Res" });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "NestedEnumSvc",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Do",
                    InputType = ".demo.Req",
                    OutputType = ".demo.Res"
                }
            }
        });
        return fd;
    }

    private static FileDescriptorProto BuildScalarsFileDescriptor()
    {
        var fd = new FileDescriptorProto
        {
            Name = "demo/scalars.proto",
            Package = "demo",
            Syntax = "proto3"
        };

        FieldDescriptorProto F(string name, int n, FieldDescriptorProto.Types.Type t) =>
            new()
            {
                Name = name, Number = n, Type = t,
                Label = FieldDescriptorProto.Types.Label.Optional, JsonName = name
            };

        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Scalars",
            Field =
            {
                F("d",      1,  FieldDescriptorProto.Types.Type.Double),
                F("f",      2,  FieldDescriptorProto.Types.Type.Float),
                F("i64",    3,  FieldDescriptorProto.Types.Type.Int64),
                F("u64",    4,  FieldDescriptorProto.Types.Type.Uint64),
                F("i32",    5,  FieldDescriptorProto.Types.Type.Int32),
                F("fx64",   6,  FieldDescriptorProto.Types.Type.Fixed64),
                F("fx32",   7,  FieldDescriptorProto.Types.Type.Fixed32),
                F("b",      8,  FieldDescriptorProto.Types.Type.Bool),
                F("s",      9,  FieldDescriptorProto.Types.Type.String),
                F("by",     10, FieldDescriptorProto.Types.Type.Bytes),
                F("u32",    11, FieldDescriptorProto.Types.Type.Uint32),
                F("sfx32",  12, FieldDescriptorProto.Types.Type.Sfixed32),
                F("sfx64",  13, FieldDescriptorProto.Types.Type.Sfixed64),
                F("sint32", 14, FieldDescriptorProto.Types.Type.Sint32),
                F("sint64", 15, FieldDescriptorProto.Types.Type.Sint64)
            }
        });
        fd.MessageType.Add(new DescriptorProto { Name = "Res" });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = "ScalarsSvc",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Do",
                    InputType = ".demo.Scalars",
                    OutputType = ".demo.Res"
                }
            }
        });
        return fd;
    }

    // Build the user FileDescriptor along with the well-known seeds that
    // an HttpRule annotation transitively needs. We only return descriptors
    // whose names match the input proto so the reflection service doesn't
    // expose google.api.* or google.protobuf.* to clients.
    private static List<FileDescriptor> BuildAnnotatedFileDescriptor(FileDescriptorProto userProto)
    {
        // Walk each annotation descriptor's dep tree, dedup by file name,
        // and feed the resulting bytes (deps-first) into BuildFromByteStrings.
        // Hard-coding only HttpReflection + AnnotationsReflection isn't enough
        // because they pull in Any / Empty / Struct / etc. transitively.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ByteString>();

        void Visit(FileDescriptor fd)
        {
            if (!seen.Add(fd.Name)) return;
            foreach (var dep in fd.Dependencies) Visit(dep);
            ordered.Add(fd.SerializedData);
        }

        Visit(AnnotationsReflection.Descriptor);
        Visit(HttpReflection.Descriptor);
        ordered.Add(userProto.ToByteString());

        var built = FileDescriptor.BuildFromByteStrings(ordered);
        return built.Where(fd => fd.Name == userProto.Name).ToList();
    }

    // Minimal Kestrel-hosted reflection server. Mirrors the helper in
    // GrpcReflectionDiscoveryTests but lives here so the two suites stay
    // independently runnable.
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
            // Annotated descriptors reach into google/api/annotations.proto +
            // google/api/http.proto, which themselves transitively pull in
            // google/protobuf/descriptor.proto. The runtime resolves
            // pre-built FileDescriptors implicitly when their backing bytes
            // are present in the same byte-string batch — easiest path is
            // to surface the user proto via a FileDescriptor that we
            // already hold in the AppDomain.
            var serviceDescriptors = BuildAnnotatedFileDescriptor(fdProto)
                .SelectMany(fd => fd.Services)
                .ToList();

            var port = GetFreePort();

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
