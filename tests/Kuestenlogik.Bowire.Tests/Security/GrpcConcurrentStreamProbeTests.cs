// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for the active gRPC concurrent-stream fork-bomb probe (#399):
/// discovery of a server-streaming method, the "all opened / no limit at N"
/// finding, the RESOURCE_EXHAUSTED-before-N Safe verdict, and the skip paths —
/// driven through a fake gRPC protocol so no live server is needed.
/// </summary>
public sealed class GrpcConcurrentStreamProbeTests
{
    private static readonly string[] s_auth = ["Authorization: Bearer x"];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ActiveScanOptions N(int n) => new() { Concurrency = n };

    [Fact]
    public async Task NonHttpTarget_Silent()
        => Assert.Empty(await new GrpcConcurrentStreamProbe().RunAsync("mqtt://x:1883", new GrpcFake(), s_auth, N(4), Ct));

    [Fact]
    public async Task NoServerStreamingMethod_Skips()
    {
        var fake = new GrpcFake { HasServerStreaming = false };
        var f = Assert.Single(await new GrpcConcurrentStreamProbe().RunAsync("https://x", fake, s_auth, N(4), Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("STREAM-NO-METHOD", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllStreamsOpen_FlagsNoLimitAtN()
    {
        var fake = new GrpcFake(); // every stream opens
        var f = Assert.Single(await new GrpcConcurrentStreamProbe().RunAsync("https://x", fake, s_auth, N(6), Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-GRPC-CONCURRENT-STREAMS", f.Template.Recording.Vulnerability?.Id);
        Assert.Contains("N=6", f.Detail, StringComparison.Ordinal);
        Assert.Equal(6, fake.OpenAttempts);
    }

    [Fact]
    public async Task ResourceExhaustedBeforeN_ReportsLimited()
    {
        var fake = new GrpcFake { RejectCount = 3 }; // server rejects some with RESOURCE_EXHAUSTED
        var f = Assert.Single(await new GrpcConcurrentStreamProbe().RunAsync("https://x", fake, s_auth, N(8), Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("STREAM-LIMITED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllStreamsFailUnrelated_Inconclusive()
    {
        var fake = new GrpcFake { FailAllOther = true };
        var f = Assert.Single(await new GrpcConcurrentStreamProbe().RunAsync("https://x", fake, s_auth, N(4), Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("STREAM-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryThrows_Inconclusive()
    {
        var fake = new GrpcFake { DiscoverThrows = true };
        var f = Assert.Single(await new GrpcConcurrentStreamProbe().RunAsync("https://x", fake, s_auth, N(4), Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("STREAM-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    private sealed class GrpcFake : IBowireProtocol
    {
        public bool HasServerStreaming { get; init; } = true;
        public bool DiscoverThrows { get; init; }
        public bool FailAllOther { get; init; }
        public int RejectCount { get; init; }
        private int _attempts;
        public int OpenAttempts => _attempts;

        public string Id => "grpc";
        public string Name => "grpc";
        public string IconSvg => "";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
        {
            if (DiscoverThrows) throw new InvalidOperationException("reflection unavailable");
            var msg = new BowireMessageInfo("M", "M", []);
            var methods = new List<BowireMethodInfo>
            {
                new("Unary", "Unary", false, false, msg, msg, "Unary"),
            };
            if (HasServerStreaming)
                methods.Add(new("StreamThings", "StreamThings", false, true, msg, msg, "ServerStreaming"));
            return Task.FromResult(new List<BowireServiceInfo> { new("pkg.Svc", "", methods) });
        }

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", []));

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var index = Interlocked.Increment(ref _attempts);
            await Task.Yield();
            if (FailAllOther) throw new InvalidOperationException("Status(StatusCode=\"Unavailable\")");
            if (index <= RejectCount) throw new InvalidOperationException("Status(StatusCode=\"ResourceExhausted\", Detail=\"too many streams\")");
            yield return "{\"frame\":1}";
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }
}
