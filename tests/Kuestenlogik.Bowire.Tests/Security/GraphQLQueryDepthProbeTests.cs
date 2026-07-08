// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for <see cref="GraphQLQueryDepthProbe"/> — the query-depth sibling of
/// the alias-batching resource-limit probe, rolling up to <c>API4:2023</c>. Each
/// case drives the probe through a self-contained <see cref="DepthFakeProtocol"/>
/// that routes the three stages (<c>__typename</c> preflight,
/// <c>introspectionProbe</c> gate, <c>deepQuery</c> amplification) to per-label
/// <see cref="InvokeResult"/>s, so every stage is scriptable in isolation.
/// </summary>
public sealed class GraphQLQueryDepthProbeTests
{
    private static readonly string[] s_noAuth = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PreflightOk = "{\"data\":{\"__typename\":\"Query\"}}";
    private const string IntrospectionEnabled = "{\"data\":{\"__schema\":{\"queryType\":{\"name\":\"Query\"}}}}";
    private const string IntrospectionDisabled = "{\"errors\":[{\"message\":\"introspection is disabled\"}]}";

    [Fact]
    public async Task PreflightNotGraphQl_Skips()
    {
        var probe = new GraphQLQueryDepthProbe();
        var proto = new DepthFakeProtocol
        {
            Preflight = new InvokeResult("{\"message\":\"nope\"}", 1, "OK", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-GRAPHQL-DEPTH-NOT-GRAPHQL", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntrospectionDisabled_Skips_AndDeepQueryNeverRuns()
    {
        var probe = new GraphQLQueryDepthProbe();
        var proto = new DepthFakeProtocol
        {
            Preflight = new InvokeResult(PreflightOk, 1, "OK", []),
            Introspection = new InvokeResult(IntrospectionDisabled, 1, "OK", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-GRAPHQL-DEPTH-NO-INTROSPECTION", f.Template.Recording.Id, StringComparison.Ordinal);
        Assert.False(proto.DeepQueryInvoked, "deepQuery must not run when introspection is disabled");
    }

    [Fact]
    public async Task DeepQueryResolvedInFull_FlagsApi4()
    {
        var probe = new GraphQLQueryDepthProbe();
        var proto = new DepthFakeProtocol
        {
            Preflight = new InvokeResult(PreflightOk, 1, "OK", []),
            Introspection = new InvokeResult(IntrospectionEnabled, 1, "OK", []),
            DeepQuery = new InvokeResult("{\"data\":{\"__schema\":{\"types\":[]}}}", 2, "OK", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API4-GRAPHQL-DEPTH", f.Template.Recording.Vulnerability?.Id);
        Assert.Equal("API4-2023-RESOURCE", f.Template.Recording.Vulnerability?.OwaspApi);
        Assert.Equal("CWE-770", f.Template.Recording.Vulnerability?.Cwe);
    }

    [Fact]
    public async Task DeepQueryRejectedWithErrors_ReportsLimitEnforced()
    {
        var probe = new GraphQLQueryDepthProbe();
        var proto = new DepthFakeProtocol
        {
            Preflight = new InvokeResult(PreflightOk, 1, "OK", []),
            Introspection = new InvokeResult(IntrospectionEnabled, 1, "OK", []),
            DeepQuery = new InvokeResult("{\"errors\":[{\"message\":\"query exceeds maximum depth of 10\"}]}", 2, "OK", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API4-GRAPHQL-DEPTH-LIMIT-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepQueryNullResponseWith400_ReportsLimitEnforced()
    {
        var probe = new GraphQLQueryDepthProbe();
        var proto = new DepthFakeProtocol
        {
            Preflight = new InvokeResult(PreflightOk, 1, "OK", []),
            Introspection = new InvokeResult(IntrospectionEnabled, 1, "OK", []),
            // Plugin maps a non-2xx to a null-response InvokeResult carrying the message.
            DeepQuery = new InvokeResult(null, 2, "Response status code does not indicate success: 400 (Bad Request).", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("API4-GRAPHQL-DEPTH-LIMIT-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepQueryNeitherDataNorErrors_Inconclusive()
    {
        var probe = new GraphQLQueryDepthProbe();
        var proto = new DepthFakeProtocol
        {
            Preflight = new InvokeResult(PreflightOk, 1, "OK", []),
            Introspection = new InvokeResult(IntrospectionEnabled, 1, "OK", []),
            DeepQuery = new InvokeResult("{}", 2, "OK", []),
        };

        var f = Assert.Single(await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct));
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("API4-GRAPHQL-DEPTH-INCONCLUSIVE", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeepQuery_IsBalanced_AndNestsAtLeast15OfType()
    {
        var probe = new GraphQLQueryDepthProbe();
        var proto = new DepthFakeProtocol
        {
            Preflight = new InvokeResult(PreflightOk, 1, "OK", []),
            Introspection = new InvokeResult(IntrospectionEnabled, 1, "OK", []),
            DeepQuery = new InvokeResult("{\"data\":{\"__schema\":{\"types\":[]}}}", 2, "OK", []),
        };

        await probe.RunAsync("http://x/graphql", proto, s_noAuth, Ct);

        // The probe wraps the operation as { "query": "<op>" }; the raw request
        // sent for the deepQuery stage is captured so we can sanity-check the
        // generated nesting is balanced and deep enough.
        Assert.NotNull(proto.DeepQueryRequest);
        var request = proto.DeepQueryRequest!;
        Assert.Equal(CountOccurrences(request, '{'), CountOccurrences(request, '}'));
        Assert.True(CountOccurrences(request, "ofType") >= 15,
            $"expected at least 15 ofType occurrences, found {CountOccurrences(request, "ofType")}");
    }

    private static int CountOccurrences(string haystack, char needle)
    {
        var n = 0;
        foreach (var c in haystack)
        {
            if (c == needle) n++;
        }
        return n;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }

    // ---------- fake ----------

    // Routes InvokeAsync by the method-label arg the probe passes for each stage
    // (__typename / introspectionProbe / deepQuery) to a per-label InvokeResult,
    // records whether the deepQuery stage ran, and captures its raw request body.
    private sealed class DepthFakeProtocol : IBowireProtocol
    {
        public string Id => "graphql";
        public string Name => "graphql";
        public string IconSvg => "";

        public InvokeResult? Preflight { get; init; }
        public InvokeResult? Introspection { get; init; }
        public InvokeResult? DeepQuery { get; init; }

        public bool DeepQueryInvoked { get; private set; }
        public string? DeepQueryRequest { get; private set; }

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => throw new InvalidOperationException("Discover not configured");

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
        {
            var result = method switch
            {
                "__typename" => Preflight,
                "introspectionProbe" => Introspection,
                "deepQuery" => DeepQuery,
                _ => null,
            };

            if (method == "deepQuery")
            {
                DeepQueryInvoked = true;
                DeepQueryRequest = jsonMessages.Count > 0 ? jsonMessages[0] : null;
            }

            return result is null
                ? throw new InvalidOperationException($"No scripted result for stage '{method}'")
                : Task.FromResult(result);
        }

        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new InvalidOperationException("Open not configured");
    }
}
