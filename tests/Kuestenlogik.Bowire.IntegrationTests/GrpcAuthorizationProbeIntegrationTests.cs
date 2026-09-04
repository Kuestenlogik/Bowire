// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Kuestenlogik.Bowire.IntegrationTests.Services;
using Kuestenlogik.Bowire.Protocol.Grpc;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// A gRPC server that authenticates and does not authorize
/// (Bowire.VulnDb#22).
/// </summary>
/// <remarks>
/// The vulnerable host below is the CVE's shape reduced to its mechanism: an
/// interceptor that proves a credential exists, and a handler that runs
/// whatever the credential was. That is the failure the probe exists to name,
/// and it is invisible to every check that asks "can a stranger get in?" —
/// the stranger cannot.
/// </remarks>
public sealed class GrpcAuthorizationProbeIntegrationTests
{
    private const string TokenA = "Bearer token-a";
    private const string TokenB = "Bearer token-b";

    [Fact]
    public async Task TwoIdentitiesReachingAManagementMethodIsReported()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(AuthMode.AnyCredentialAccepted);

        var findings = await new GrpcAuthorizationProbe().RunAsync(new OwaspProbeContext
        {
            Target = host.Url,
            Protocol = new BowireGrpcProtocol(),
            AuthHeaders = [$"authorization: {TokenA}"],
            AuthHeadersB = [$"authorization: {TokenB}"],
        }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API5-GRPC-NOAUTHZ", f.Template.Recording.Vulnerability?.Id);
        Assert.Contains("ListPolicies", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatDistinguishesIdentitiesIsNotReported()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(AuthMode.OnlyIdentityAAccepted);

        var findings = await new GrpcAuthorizationProbe().RunAsync(new OwaspProbeContext
        {
            Target = host.Url,
            Protocol = new BowireGrpcProtocol(),
            AuthHeaders = [$"authorization: {TokenA}"],
            AuthHeadersB = [$"authorization: {TokenB}"],
        }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("distinguishes", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublicMethodIsLeftToTheAuthenticationCheck()
    {
        // Two probes reporting one hole is how a report stops being read, and
        // "B got in" is a claim that would be true of any caller at all here.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(AuthMode.NoAuthAtAll);

        var findings = await new GrpcAuthorizationProbe().RunAsync(new OwaspProbeContext
        {
            Target = host.Url,
            Protocol = new BowireGrpcProtocol(),
            AuthHeaders = [$"authorization: {TokenA}"],
            AuthHeadersB = [$"authorization: {TokenB}"],
        }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
    }

    [Fact]
    public async Task WithoutASecondIdentityTheCheckSaysNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(AuthMode.AnyCredentialAccepted);

        var findings = await new GrpcAuthorizationProbe().RunAsync(new OwaspProbeContext
        {
            Target = host.Url,
            Protocol = new BowireGrpcProtocol(),
            AuthHeaders = [$"authorization: {TokenA}"],
        }, ct);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task TheSameCredentialTwiceProvesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(AuthMode.AnyCredentialAccepted);

        var findings = await new GrpcAuthorizationProbe().RunAsync(new OwaspProbeContext
        {
            Target = host.Url,
            Protocol = new BowireGrpcProtocol(),
            AuthHeaders = [$"authorization: {TokenA}"],
            AuthHeadersB = [$"authorization: {TokenA}"],
        }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("same credential", f.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReflectionLessServerIsReachedThroughTheDescriptorSet()
    {
        // The combination that matters in production: reflection off, which is
        // what the scanner itself recommends, and a supplied set naming the
        // methods to test (#653).
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(AuthMode.AnyCredentialAccepted, reflection: false);

        var findings = await new GrpcAuthorizationProbe().RunAsync(new OwaspProbeContext
        {
            Target = host.Url,
            Protocol = new BowireGrpcProtocol(),
            AuthHeaders = [$"authorization: {TokenA}"],
            AuthHeadersB = [$"authorization: {TokenB}"],
            ProtocolMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BowireMetadataKeys.GrpcDescriptorSet] = GrpcDescriptorSetIntegrationTests.InlineMarker(),
            },
        }, ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API5-GRPC-NOAUTHZ", f.Template.Recording.Vulnerability?.Id);
    }

    private enum AuthMode
    {
        /// <summary>The CVE's shape: a credential is required, and never examined.</summary>
        AnyCredentialAccepted,

        /// <summary>What a correct server does: the credential decides.</summary>
        OnlyIdentityAAccepted,

        /// <summary>No gate at all — the authentication probe's finding, not this one's.</summary>
        NoAuthAtAll,
    }

    // Fully qualified: Bowire has its own Kuestenlogik.Bowire.Interceptor
    // namespace, and from inside Kuestenlogik.Bowire.IntegrationTests that
    // sibling namespace wins over the imported type name.
    /// <summary>
    /// A holder, because ActivatorUtilities resolves constructor parameters
    /// from the container and an enum cannot be registered as a service.
    /// </summary>
    private sealed record AuthPolicy(AuthMode Mode);

    private sealed class AuthInterceptor(AuthPolicy policy) : Grpc.Core.Interceptors.Interceptor
    {
        private readonly AuthMode mode = policy.Mode;

        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request, ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            if (mode == AuthMode.NoAuthAtAll) return continuation(request, context);

            var token = context.RequestHeaders.GetValue("authorization");
            if (string.IsNullOrEmpty(token))
                throw new RpcException(new Status(StatusCode.Unauthenticated, "no credential"));

            if (mode == AuthMode.OnlyIdentityAAccepted && !string.Equals(token, TokenA, StringComparison.Ordinal))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "not entitled"));

            return continuation(request, context);
        }
    }

    private static async Task<GreeterHost> StartAsync(AuthMode mode, bool reflection = true)
    {
        var url = $"http://127.0.0.1:{GetFreeTcpPort()}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(opts =>
            opts.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc(o => o.Interceptors.Add<AuthInterceptor>());
        // gRPC activates the interceptor through ActivatorUtilities, so the
        // mode has to be resolvable rather than captured.
        builder.Services.AddSingleton(new AuthPolicy(mode));
        if (reflection) builder.Services.AddGrpcReflection();

        var app = builder.Build();
        app.MapGrpcService<GreeterService>();
        if (reflection) app.MapGrpcReflectionService();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return new GreeterHost(app, url);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class GreeterHost(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
