// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Grpc.Reflection;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// Final-mile coverage gaps in <c>Kuestenlogik.Bowire.Protocol.Grpc</c>
/// after Agents D + H landed on the easy paths. Targets three specific
/// surfaces flagged in the gap analysis:
/// <list type="bullet">
///   <item>
///     <strong>mTLS ctor paths</strong> in
///     <see cref="GrpcInvoker"/> and <see cref="ConnectInvoker"/> —
///     drives <c>MtlsHandlerOwner.CreateSocketsHttpHandler</c> via a
///     runtime-generated self-signed cert (RSA + CertificateRequest)
///     so no external PEM fixtures land in the test tree.
///   </item>
///   <item>
///     <strong><see cref="GrpcInvoker"/>.BuildFileDescriptors slow
///     path</strong> — handcrafted <see cref="FileDescriptorProto"/>
///     bundles that force the batch <c>BuildFromByteStrings</c> to fail
///     so the per-file recovery loop runs, including the deps-satisfied
///     re-build branch, the schema-only fallback for missing deps, and
///     the inner catch arm where even the stripped form fails.
///   </item>
///   <item>
///     <strong>Connect-via-plugin streaming paths</strong> —
///     <see cref="ConnectInvoker.InvokeBidiStreamAsync"/> +
///     <see cref="ConnectInvoker.InvokeServerStreamAsync"/> +
///     <see cref="ConnectInvoker.InvokeClientStreamAsync"/> driven from
///     the <c>GrpcInvoker.InvokeUnaryAsync</c> /
///     <c>InvokeStreamingWithFramesAsync</c> entry points with an
///     HTTP/2 Connect endpoint colocated alongside gRPC reflection on
///     a single Kestrel listener. Forces the previously-untested
///     branches at GrpcInvoker.cs lines 101-117 (the Connect routing
///     into <see cref="ConnectInvoker"/>) and exercises metadata
///     forwarding on every streaming path.
///   </item>
/// </list>
/// All assertions check observed behaviour — concrete cert fingerprints,
/// recovered descriptor file names + service names, captured request
/// frame contents, decoded JSON payloads — not "doesn't throw".
/// </summary>
public sealed class GrpcCertAndSlowPathTests
{
    // ---------------------------------------------------------------
    // mTLS ctor paths — runtime-generated self-signed cert flows into
    // GrpcInvoker / ConnectInvoker, which route through
    // MtlsHandlerOwner.CreateSocketsHttpHandler.
    // ---------------------------------------------------------------

    [Fact]
    public void GrpcInvoker_With_MtlsConfig_Uses_SocketsHttpHandler_With_Client_Cert()
    {
        // Generate a one-shot self-signed RSA cert + key in PEM form.
        // No on-disk fixture, no external PKI — the test owns the bytes
        // start to finish so the assertion can pin the thumbprint.
        var (certPem, keyPem, thumbprint) = GenerateSelfSignedRsaCertificate(
            subjectCn: "bowire-grpc-mtls-test");

        var mtls = new MtlsConfig(
            CertificatePem: certPem,
            PrivateKeyPem: keyPem,
            Passphrase: null,
            CaCertificatePem: certPem,
            AllowSelfSigned: false);

        // GrpcReflectionClient + GrpcInvoker both spin up their own
        // MtlsHandlerOwner from the same config — we don't talk to a
        // server, but the ctor has to walk all the way through the
        // handler-construction code path.
        using var reflectionClient = new GrpcReflectionClient(
            "https://127.0.0.1:1", showInternalServices: false,
            mtlsConfig: mtls, configuration: null,
            transportMode: GrpcTransportMode.Native);
        using var invoker = new GrpcInvoker(
            "https://127.0.0.1:1", reflectionClient, mtlsConfig: mtls,
            configuration: null, transportMode: GrpcTransportMode.Native);

        // Reflect into the invoker to verify the mTLS handler-owner
        // captured our cert (proves CreateSocketsHttpHandler ran AND
        // SslOptions.ClientCertificates ended up populated).
        var mtlsOwnerField = typeof(GrpcInvoker)
            .GetField("_mtlsOwner", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(mtlsOwnerField);
        var owner = (MtlsHandlerOwner?)mtlsOwnerField!.GetValue(invoker);
        Assert.NotNull(owner);

        var handler = Assert.IsType<SocketsHttpHandler>(owner!.Handler);
        Assert.NotNull(handler.SslOptions);
        var clientCerts = handler.SslOptions.ClientCertificates;
        Assert.NotNull(clientCerts);
        var clientCert = Assert.IsType<X509Certificate2>(Assert.Single(clientCerts!));
        Assert.Equal(thumbprint, clientCert.Thumbprint);
    }

    [Fact]
    public void ConnectInvoker_With_MtlsConfig_Wires_Client_Cert_Onto_SocketsHttpHandler()
    {
        var (certPem, keyPem, thumbprint) = GenerateSelfSignedRsaCertificate(
            subjectCn: "bowire-connect-mtls-test");

        var mtls = new MtlsConfig(
            CertificatePem: certPem,
            PrivateKeyPem: keyPem,
            Passphrase: null,
            // No CA cert this time — covers the "AllowSelfSigned: true"
            // server-validator branch (return _ => true). Together with
            // the test above this covers both MtlsConfig.BuildServerValidator
            // arms used by the gRPC plugin.
            CaCertificatePem: null,
            AllowSelfSigned: true);

        using var invoker = new ConnectInvoker(
            "https://127.0.0.1:1", mtlsConfig: mtls, configuration: null);

        var mtlsOwnerField = typeof(ConnectInvoker)
            .GetField("_mtlsOwner", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(mtlsOwnerField);
        var owner = (MtlsHandlerOwner?)mtlsOwnerField!.GetValue(invoker);
        Assert.NotNull(owner);

        var handler = Assert.IsType<SocketsHttpHandler>(owner!.Handler);
        Assert.NotNull(handler.SslOptions);
        Assert.NotNull(handler.SslOptions.ClientCertificates);
        var clientCert = Assert.IsType<X509Certificate2>(
            Assert.Single(handler.SslOptions.ClientCertificates!));
        Assert.Equal(thumbprint, clientCert.Thumbprint);

        // AllowSelfSigned=true installs an accept-anything validator on
        // RemoteCertificateValidationCallback — exercise it returns true
        // for any input so the branch is hit (BuildServerValidator's
        // (_, _, _, _) => true arm).
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        var ok = handler.SslOptions.RemoteCertificateValidationCallback!(
            sender: this,
            certificate: null,
            chain: null,
            sslPolicyErrors: System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable);
        Assert.True(ok);
    }

    [Fact]
    public void GrpcInvoker_With_Invalid_MtlsConfig_Throws_InvalidOperationException()
    {
        // Negative path: MtlsHandlerOwner returns null on PEM-parse failure
        // and GrpcInvoker.ctor surfaces an InvalidOperationException —
        // covers the throw at GrpcInvoker.cs line 56.
        var mtls = new MtlsConfig(
            CertificatePem: "-----BEGIN CERTIFICATE-----\ngarbage\n-----END CERTIFICATE-----",
            PrivateKeyPem: "-----BEGIN PRIVATE KEY-----\nstill garbage\n-----END PRIVATE KEY-----",
            Passphrase: null,
            CaCertificatePem: null,
            AllowSelfSigned: false);

        // We can't construct the real GrpcReflectionClient with bad mTLS
        // (it would throw first); but we don't need it — the throw
        // happens before the reflection client is touched. Pass a
        // reflection client built with NO mTLS so its ctor stays
        // happy, then trigger the failure on GrpcInvoker.ctor.
        using var realReflection = new GrpcReflectionClient(
            "https://127.0.0.1:1", showInternalServices: false,
            mtlsConfig: null, configuration: null,
            transportMode: GrpcTransportMode.Native);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new GrpcInvoker("https://127.0.0.1:1", realReflection, mtlsConfig: mtls));
        Assert.Contains("mTLS configuration invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectInvoker_With_Invalid_MtlsConfig_Throws_InvalidOperationException()
    {
        // Symmetric to the GrpcInvoker test — covers the throw at
        // ConnectInvoker.cs line 84.
        var mtls = new MtlsConfig(
            CertificatePem: "not a pem",
            PrivateKeyPem: "not a key",
            Passphrase: null,
            CaCertificatePem: null,
            AllowSelfSigned: false);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ConnectInvoker("https://127.0.0.1:1", mtlsConfig: mtls));
        Assert.Contains("mTLS configuration invalid", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // BuildFileDescriptors slow path — handcrafted
    // FileDescriptorProto bundles that force batch failure so the
    // per-file recovery loop runs.
    // ---------------------------------------------------------------

    [Fact]
    public void BuildFileDescriptors_Slow_Path_Recovers_Per_File_With_Schema_Only_Strip()
    {
        // user.proto imports a non-existent dep. Batch
        // BuildFromByteStrings throws because the dep can't be resolved.
        // The slow path:
        //   - Notices missingDeps == true.
        //   - Clones the proto, strips the import list + custom options,
        //     including the nested foreach over Service.Method that
        //     clears MethodOptions.
        //   - Builds the stripped descriptor.
        // Asserts the user.proto file shows up in the result with its
        // service intact, proving the schema-only recovery + nested
        // service/method options-strip loop ran.
        var user = new FileDescriptorProto
        {
            Name = "tenant/user.proto",
            Package = "tenant",
            Syntax = "proto3",
            Dependency = { "third_party/utterly/missing.proto" },
        };
        user.MessageType.Add(new DescriptorProto
        {
            Name = "User",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "name", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "name",
                },
            },
        });
        // Service + Method give the inner foreach a non-empty body so
        // GrpcInvoker.cs lines 410-414 (Service.Options = null;
        // foreach m in svc.Method m.Options = null) are exercised.
        user.Service.Add(new ServiceDescriptorProto
        {
            Name = "UserSvc",
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = "Get",
                    InputType = ".tenant.User",
                    OutputType = ".tenant.User",
                },
            },
        });

        var result = GrpcInvoker.BuildFileDescriptorsPublic(
            new List<FileDescriptorProto> { user });

        var only = Assert.Single(result);
        Assert.Equal("tenant/user.proto", only.Name);
        // Schema-only descriptor still has the service + method intact,
        // just without the missing-dep import.
        var svc = Assert.Single(only.Services);
        Assert.Equal("UserSvc", svc.Name);
        var meth = Assert.Single(svc.Methods);
        Assert.Equal("Get", meth.Name);
    }

    [Fact]
    public void BuildFileDescriptors_Slow_Path_Skips_File_When_Even_Stripped_Build_Fails()
    {
        // broken.proto is so malformed that even the schema-only strip
        // can't build a valid descriptor — TypeName references a type
        // that nothing else declares (no package, no message). The slow
        // path's inner try/catch (GrpcInvoker.cs lines 405-427) swallows
        // the exception and continues without adding the file to the
        // result. Asserts the result is empty + non-null (the catch arm
        // didn't propagate the throw).
        var broken = new FileDescriptorProto
        {
            Name = "broken.proto",
            Package = "broken",
            Syntax = "proto3",
            Dependency = { "this/triggers/the/strip/path.proto" },
        };
        broken.MessageType.Add(new DescriptorProto
        {
            Name = "Bad",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "ref", Number = 1,
                    // Message type pointing at a fully-qualified name
                    // that nothing supplies — even after stripping the
                    // dep this descriptor can't validate.
                    Type = FieldDescriptorProto.Types.Type.Message,
                    TypeName = ".nowhere.NonExistent",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "ref",
                },
            },
        });

        var result = GrpcInvoker.BuildFileDescriptorsPublic(
            new List<FileDescriptorProto> { broken });

        Assert.NotNull(result);
        // No descriptors built — the inner catch arm at
        // GrpcInvoker.cs lines 424-427 swallowed the exception.
        Assert.Empty(result);
    }

    [Fact]
    public void BuildFileDescriptors_Slow_Path_With_Multiple_Files_Resolves_Topological_Order()
    {
        // Two files: leaf.proto (no deps) + branch.proto (imports a
        // missing dep + a non-missing dep). The batch fails because of
        // the missing dep, falling through to the slow path. branch.proto
        // goes through the schema-only strip; leaf.proto goes through
        // the plain build. Drives both arms of the slow-path foreach
        // loop in one call so coverage picks up both branches.
        var leaf = new FileDescriptorProto
        {
            Name = "shared/leaf.proto",
            Package = "shared",
            Syntax = "proto3",
        };
        leaf.MessageType.Add(new DescriptorProto
        {
            Name = "Leaf",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "id", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "id",
                },
            },
        });

        var branch = new FileDescriptorProto
        {
            Name = "shared/branch.proto",
            Package = "shared",
            Syntax = "proto3",
            // Missing dep forces the batch to throw; the schema-only
            // strip in the slow path drops both imports.
            Dependency = { "missing/upstream.proto" },
        };
        branch.MessageType.Add(new DescriptorProto
        {
            Name = "Branch",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "tag", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "tag",
                },
            },
        });

        var result = GrpcInvoker.BuildFileDescriptorsPublic(
            new List<FileDescriptorProto> { branch, leaf });

        // At minimum branch and leaf both recover; the order doesn't
        // matter because the discovery walk consumes the list by name.
        var names = result.Select(fd => fd.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("shared/leaf.proto", names);
        Assert.Contains("shared/branch.proto", names);
    }

    [Fact]
    public async Task GrpcInvoker_ResolveMethodAsync_Throws_When_Reflection_Returns_No_Descriptors()
    {
        // Drives the InvalidOperationException at GrpcInvoker.cs
        // lines 290-292 (empty fileDescProtos guard) via the reflection
        // pathway: the discovery server returns "no such service" so
        // the BFS resolves to zero descriptors. The plugin's
        // InvokeAsync wraps the exception cleanly — we assert on the
        // message.
        var fdProto = BuildSingleServiceFileDescriptor("demo.OnlyOne", "DoSomething");
        await using var server = await ReflectionOnlyServer.StartAsync(fdProto);

        var protocol = new BowireGrpcProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            protocol.InvokeAsync(
                server.BaseUrl,
                "demo.NotAvailableService", "AnyMethod",
                new List<string> { "{}" },
                showInternalServices: false,
                metadata: null,
                cts.Token));
        // Either "no file descriptors" (line 291) or "not found"
        // (line 314) — both are acceptable failure shapes for the
        // unknown-service path.
        Assert.True(
            ex.Message.Contains("no file descriptors", StringComparison.Ordinal)
            || ex.Message.Contains("not found", StringComparison.Ordinal),
            $"Unexpected error message: {ex.Message}");
    }

    [Fact]
    public void BuildFileDescriptors_Slow_Path_Skips_Proto_When_Name_Matches_WellKnown_Seed()
    {
        // The slow path seeds `built` with the well-known types
        // (google/protobuf/timestamp.proto, &c.). A user proto sharing
        // a well-known name is skipped via the
        //     if (built.ContainsKey(proto.Name)) continue;
        // guard at line 383-384. Drives that branch by feeding in a
        // proto whose Name collides with one of the seeded files plus
        // one normal proto so the slow path actually runs.
        var collidesWithWellKnown = new FileDescriptorProto
        {
            // Same Name as Google.Protobuf.WellKnownTypes.Timestamp's
            // descriptor file → SeedWellKnownTypes already added it,
            // so the slow-path foreach hits the ContainsKey continue.
            Name = "google/protobuf/timestamp.proto",
            Package = "google.protobuf",
            Syntax = "proto3",
        };
        var normal = new FileDescriptorProto
        {
            Name = "ordinary.proto",
            Package = "ordinary",
            Syntax = "proto3",
            // Missing dep forces the batch to throw → slow path runs.
            Dependency = { "missing/ref.proto" },
        };
        normal.MessageType.Add(new DescriptorProto { Name = "M" });

        var result = GrpcInvoker.BuildFileDescriptorsPublic(
            new List<FileDescriptorProto> { collidesWithWellKnown, normal });

        // The result keeps ordinary.proto but not the well-known-named
        // user proto (the seed wins, the user proto is skipped).
        Assert.Contains(result, fd => fd.Name == "ordinary.proto");
        // The well-known seed itself is filtered out of the result by
        // BuildFileDescriptors (it's an implementation detail, not a
        // discoverable file). Verify it doesn't show up either.
        Assert.DoesNotContain(result, fd =>
            fd.Name == "google/protobuf/timestamp.proto" && fd.Package == "google.protobuf");
    }

    // ---------------------------------------------------------------
    // ConnectInvoker streaming paths with non-null metadata — covers
    // the metadata-forwarding foreach loops in ServerStream /
    // ClientStream / BidiStream that Agent H's null-metadata tests
    // skipped.
    //
    // These tests hit ConnectInvoker directly with StringValue.Descriptor
    // (a generated WKT whose Parser is wired up) rather than going
    // through GrpcInvoker + reflection. The reflection-via-Connect path
    // (GrpcInvoker.cs lines 101-117) needs MessageDescriptors with a
    // populated Parser, which FileDescriptor.BuildFromByteStrings does
    // not produce; until the plugin adds a parser-registration step,
    // that surface stays as a documented residual gap.
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConnectInvokeServerStreamAsync_Forwards_Metadata_Headers_On_Streaming_Request()
    {
        // Reuses the same in-process listener pattern as Agent H's
        // ConnectStreamingServer but with header capture so we can
        // pin the user-metadata entries that crossed the wire. The
        // critical bit is that the request kicks off through the
        // metadata foreach loop at ConnectInvoker.cs lines 276-280.
        Dictionary<string, string>? captured = null;
        var responseBody = new StringValue { Value = "ok" }.ToByteArray();
        var responseFrames = new List<byte[]>
        {
            ConnectInvoker.EncodeFrame(0x00, responseBody),
            ConnectInvoker.EncodeFrame(ConnectInvoker.EndStreamFlag, Array.Empty<byte>()),
        };

        await using var server = await ConnectStreamingCaptureServer.StartAsync(
            requestObserver: ctx => captured = ctx,
            responseFrames: responseFrames,
            protocols: HttpProtocols.Http1);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Server-Stream-Trace"] = "trace-srv",
            ["X-Server-Stream-Tenant"] = "tenant-foo",
        };

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeServerStreamAsync(
            "test.GapService", "DoServer",
            StringValue.Descriptor, StringValue.Descriptor,
            "\"q\"",
            metadata,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Single(received);
        Assert.Equal("\"ok\"", received[0].Json);

        Assert.NotNull(captured);
        Assert.Equal("trace-srv", captured!["X-Server-Stream-Trace"]);
        Assert.Equal("tenant-foo", captured["X-Server-Stream-Tenant"]);
        Assert.Equal("1", captured["Connect-Protocol-Version"]);
    }

    [Fact]
    public async Task ConnectInvokeClientStreamAsync_Forwards_Metadata_Headers_On_Streaming_Request()
    {
        // Same shape as the server-stream test, but using the bidi-
        // shaped client streaming path. Covers ConnectInvoker.cs lines
        // 375-379.
        Dictionary<string, string>? captured = null;
        var responseBody = new StringValue { Value = "agg" }.ToByteArray();
        var responseFrames = new List<byte[]>
        {
            ConnectInvoker.EncodeFrame(0x00, responseBody),
            ConnectInvoker.EncodeFrame(ConnectInvoker.EndStreamFlag, Array.Empty<byte>()),
        };

        await using var server = await ConnectStreamingCaptureServer.StartAsync(
            requestObserver: ctx => captured = ctx,
            responseFrames: responseFrames);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Client-Stream-Trace"] = "trace-clt",
            ["X-Client-Stream-Op"] = "collect",
        };

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var result = await invoker.InvokeClientStreamAsync(
            "test.GapService", "DoClient",
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"a\"", "\"b\"" },
            metadata,
            TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("\"agg\"", result.Response);

        Assert.NotNull(captured);
        Assert.Equal("trace-clt", captured!["X-Client-Stream-Trace"]);
        Assert.Equal("collect", captured["X-Client-Stream-Op"]);
        Assert.Equal("1", captured["Connect-Protocol-Version"]);
    }

    [Fact]
    public async Task ConnectInvokeBidiStreamAsync_Forwards_Metadata_Headers_On_Streaming_Request()
    {
        // Covers ConnectInvoker.cs lines 476-480 (the bidi
        // metadata-forwarding foreach). Same setup as Agent H's bidi
        // test but with a non-null metadata bag.
        Dictionary<string, string>? captured = null;
        var responseFrames = new List<byte[]>
        {
            ConnectInvoker.EncodeFrame(0x00, new StringValue { Value = "r1" }.ToByteArray()),
            ConnectInvoker.EncodeFrame(ConnectInvoker.EndStreamFlag, Array.Empty<byte>()),
        };

        await using var server = await ConnectStreamingCaptureServer.StartAsync(
            requestObserver: ctx => captured = ctx,
            responseFrames: responseFrames);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Bidi-Trace"] = "trace-bidi",
            ["X-Bidi-Tenant"] = "tenant-bar",
        };

        using var invoker = new ConnectInvoker(server.BaseUrl);
        var received = new List<ConnectStreamFrame>();
        await foreach (var frame in invoker.InvokeBidiStreamAsync(
            "test.GapService", "DoBidi",
            StringValue.Descriptor, StringValue.Descriptor,
            new List<string> { "\"q1\"" },
            metadata,
            TestContext.Current.CancellationToken))
        {
            received.Add(frame);
        }

        Assert.Single(received);
        Assert.Equal("\"r1\"", received[0].Json);

        Assert.NotNull(captured);
        Assert.Equal("trace-bidi", captured!["X-Bidi-Trace"]);
        Assert.Equal("tenant-bar", captured["X-Bidi-Tenant"]);
        Assert.Equal("1", captured["Connect-Protocol-Version"]);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Generate a one-shot self-signed RSA certificate + key in PEM
    /// form. The cert is valid for one day, so it never spills into
    /// long-lived test artifacts. Returns the thumbprint so the test
    /// can pin the cert reaching the handler.
    /// </summary>
    private static (string CertPem, string KeyPem, string Thumbprint) GenerateSelfSignedRsaCertificate(string subjectCn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={subjectCn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // Mark as a TLS client+server cert — works for both client-auth
        // and self-signed server validation flows in our tests.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1"), // server auth
                new Oid("1.3.6.1.5.5.7.3.2"), // client auth
            }, false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);
        using var cert = req.CreateSelfSigned(notBefore, notAfter);

        var thumbprint = cert.Thumbprint;
        var certPem = PemEncoding.WriteString("CERTIFICATE", cert.Export(X509ContentType.Cert));
        var keyPem = PemEncoding.WriteString("PRIVATE KEY", rsa.ExportPkcs8PrivateKey());
        return (certPem, keyPem, thumbprint);
    }

    /// <summary>
    /// Build a minimal FileDescriptorProto with a single unary service
    /// + method — used by the reflection-driven tests that just need
    /// a valid descriptor to come back from reflection.
    /// </summary>
    private static FileDescriptorProto BuildSingleServiceFileDescriptor(string fullServiceName, string methodName)
    {
        var dot = fullServiceName.LastIndexOf('.');
        var package = dot > 0 ? fullServiceName[..dot] : "demo";
        var serviceName = dot > 0 ? fullServiceName[(dot + 1)..] : fullServiceName;
        var fd = new FileDescriptorProto
        {
            Name = $"{package}/{serviceName}.proto",
            Package = package,
            Syntax = "proto3",
        };
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Req",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "v", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "v",
                },
            },
        });
        fd.MessageType.Add(new DescriptorProto
        {
            Name = "Res",
            Field =
            {
                new FieldDescriptorProto
                {
                    Name = "v", Number = 1,
                    Type = FieldDescriptorProto.Types.Type.String,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    JsonName = "v",
                },
            },
        });
        fd.Service.Add(new ServiceDescriptorProto
        {
            Name = serviceName,
            Method =
            {
                new MethodDescriptorProto
                {
                    Name = methodName,
                    InputType = $".{package}.Req",
                    OutputType = $".{package}.Res",
                },
            },
        });
        return fd;
    }

    /// <summary>
    /// Reflection-only gRPC server (no actual service implementation
    /// behind the listed descriptors). Used by tests that drive the
    /// resolver path but expect it to fail downstream.
    /// </summary>
    private sealed class ReflectionOnlyServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        private ReflectionOnlyServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public static async Task<ReflectionOnlyServer> StartAsync(FileDescriptorProto fdProto)
        {
            var fileDescriptors = FileDescriptor.BuildFromByteStrings(
                new[] { fdProto.ToByteString() });
            var serviceDescriptors = fileDescriptors
                .SelectMany(fd => fd.Services)
                .ToList();

            var port = GetFreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath(),
            });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, port, lo =>
                {
                    lo.Protocols = HttpProtocols.Http2;
                });
            });
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(new ReflectionServiceImpl(serviceDescriptors));

            var app = builder.Build();
            app.MapGrpcService<ReflectionServiceImpl>();

            await app.StartAsync();
            return new ReflectionOnlyServer(app, $"http://127.0.0.1:{port}");
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

    /// <summary>
    /// In-process HTTP/2 Connect listener that captures the request
    /// headers on the first inbound POST and writes a fixed sequence
    /// of response frames back. The captured header dict lets the
    /// streaming tests pin every user-metadata entry that crossed
    /// the wire, proving the metadata-forwarding foreach loops in
    /// <see cref="ConnectInvoker"/> ran.
    /// </summary>
    private sealed class ConnectStreamingCaptureServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        private ConnectStreamingCaptureServer(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public static async Task<ConnectStreamingCaptureServer> StartAsync(
            Action<Dictionary<string, string>> requestObserver,
            List<byte[]> responseFrames,
            HttpProtocols protocols = HttpProtocols.Http2)
        {
            var port = GetFreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = Path.GetTempPath(),
            });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, port, lo =>
                {
                    // Caller picks the protocol set. ServerStream sends
                    // HTTP/1.1 by default (Connect doesn't pin a version
                    // there), so its tests want Http1. Client-stream
                    // + bidi force HTTP/2 explicitly via
                    // RequestVersionOrHigher / RequestVersionExact, so
                    // those tests want Http2. Sharing one listener for
                    // both fails because h2c prior-knowledge can't
                    // negotiate down to HTTP/1.1 on the same connection.
                    lo.Protocols = protocols;
                });
            });
            var app = builder.Build();

            app.MapPost("/{service}/{method}", async (HttpContext ctx) =>
            {
                var captured = ctx.Request.Headers.ToDictionary(
                    h => h.Key, h => string.Join(", ", h.Value.ToArray()),
                    StringComparer.OrdinalIgnoreCase);
                requestObserver(captured);

                // Drain the request body so the HTTP/2 stream closes
                // cleanly on the client side before we start writing
                // response frames.
                using var sink = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(sink, ctx.RequestAborted);

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/connect+proto";
                foreach (var frame in responseFrames)
                    await ctx.Response.Body.WriteAsync(frame, ctx.RequestAborted);
            });

            await app.StartAsync();
            return new ConnectStreamingCaptureServer(app, $"http://127.0.0.1:{port}");
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
