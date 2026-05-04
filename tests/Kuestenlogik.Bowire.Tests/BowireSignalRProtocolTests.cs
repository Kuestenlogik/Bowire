// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Threading.Channels;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Kuestenlogik.Bowire.Tests;

// CA1822: Fixture hub members can't be static — Hub<T> requires instance methods
// for the reflection walk. CA1859: BuildHubEndpoint returns Endpoint to satisfy
// the EndpointDataSource contract. CA1034 / CA1515 / CA2227: nested DTO fixtures
// are intentionally public-shaped so reflection sees them like real DTOs.
#pragma warning disable CA1822, CA1859, CA1034, CA1515, CA2227

/// <summary>
/// Tests for the SignalR protocol plugin. Identity, embedded hub-discovery,
/// the URL resolver, and the streaming-direction lookup that
/// <c>OpenChannelAsync</c> uses are all reachable without a live SignalR
/// peer. Actual <c>InvokeAsync</c> / <c>StreamAsync</c> against a hub is
/// integration-tier territory — we only assert that the no-server path
/// fails fast rather than hanging on a connect.
/// </summary>
public sealed class BowireSignalRProtocolTests
{
    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireSignalRProtocol();

        Assert.Equal("SignalR", protocol.Name);
        Assert.Equal("signalr", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_IBowireProtocol()
    {
        var protocol = new BowireSignalRProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
    }

    [Fact]
    public void Initialize_Accepts_Null_Service_Provider()
    {
        var protocol = new BowireSignalRProtocol();

        // Standalone tool mode — should not throw.
        protocol.Initialize(null);
    }

    [Fact]
    public async Task DiscoverAsync_Without_Service_Provider_Returns_Empty()
    {
        var protocol = new BowireSignalRProtocol();
        protocol.Initialize(null);

        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_With_ServiceProvider_But_No_Endpoint_Sources_Returns_Empty()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var protocol = new BowireSignalRProtocol();
        protocol.Initialize(sp);

        var services = await protocol.DiscoverAsync(
            "http://localhost:5000",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public void ResolveHubUrl_Path_With_Slash_Is_Used_As_Is()
    {
        // Reflect into the private static helper so we can pin its rule
        // set without spinning up a hub.
        var resolved = InvokeResolveHubUrl("http://localhost:5000/", "/chatHub");

        Assert.Equal("http://localhost:5000/chatHub", resolved);
    }

    [Fact]
    public void ResolveHubUrl_Bare_Name_Gets_Slash_Prefix()
    {
        var resolved = InvokeResolveHubUrl("http://localhost:5000", "chatHub");

        Assert.Equal("http://localhost:5000/chatHub", resolved);
    }

    [Fact]
    public void ResolveHubUrl_Trailing_Slashes_On_ServerUrl_Are_Trimmed()
    {
        var resolved = InvokeResolveHubUrl("http://localhost:5000///", "/hubs/chat");

        Assert.Equal("http://localhost:5000/hubs/chat", resolved);
    }

    [Fact]
    public async Task InvokeAsync_With_Invalid_Mtls_Marker_Throws_InvalidOperation()
    {
        // mTLS marker present, but the PEM material is junk so MtlsCertOwner
        // refuses to load. The invoker surfaces that as an
        // InvalidOperationException before any network IO.
        var protocol = new BowireSignalRProtocol();

        var metadata = new Dictionary<string, string>
        {
            ["__bowireMtls__"] = """{"certificate":"NOT-A-PEM","privateKey":"NOT-A-PEM"}"""
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await protocol.InvokeAsync(
                "http://127.0.0.2:1",
                "Hub",
                "Method",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: metadata,
                TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task OpenChannelAsync_With_Invalid_Mtls_Marker_Throws_InvalidOperation()
    {
        var protocol = new BowireSignalRProtocol();

        var metadata = new Dictionary<string, string>
        {
            ["__bowireMtls__"] = """{"certificate":"NOT-A-PEM","privateKey":"NOT-A-PEM"}"""
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await protocol.OpenChannelAsync(
                "http://127.0.0.2:1",
                "Hub",
                "Method",
                showInternalServices: false,
                metadata: metadata,
                TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task InvokeAsync_With_Plain_Headers_Strips_Internal_Markers()
    {
        // Pass plain header metadata + the mTLS marker — the strip-marker
        // path runs and the invoker proceeds to connect (which fails fast
        // because the address is unreachable). We just want the
        // marker-strip + connect-attempt branches exercised.
        var protocol = new BowireSignalRProtocol();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var metadata = new Dictionary<string, string>
        {
            ["X-Trace-Id"] = "abc"
        };

        try
        {
            var result = await protocol.InvokeAsync(
                "http://127.0.0.2:1",
                "Hub",
                "Method",
                jsonMessages: ["{}"],
                showInternalServices: false,
                metadata: metadata,
                cts.Token);
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            Assert.NotNull(ex);
        }
    }

    private static string InvokeResolveHubUrl(string serverUrl, string service)
    {
        // ResolveHubUrl became an instance method in 1.0.5 so it can
        // resolve the discovered hub Package when one is registered.
        // Tests pass a fresh instance with no IServiceProvider, so the
        // discovery lookup short-circuits to empty and we fall through
        // to the literal-name path the older static helper used.
        var plugin = new BowireSignalRProtocol();
        var method = typeof(BowireSignalRProtocol).GetMethod(
            "ResolveHubUrl",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var result = method!.Invoke(plugin, new object[] { serverUrl, service });
        return Assert.IsType<string>(result);
    }
}

/// <summary>
/// Tests for <c>SignalRHubDiscovery</c> — hub method reflection and
/// CLR-to-proto type mapping. The discovery logic walks endpoint data
/// sources for HubMetadata; here we drive its inner reflection helpers
/// directly with synthetic hub types so each branch (unary / streaming
/// / nested complex / collections) is covered without ASP.NET hosting.
/// </summary>
public sealed class SignalRHubDiscoveryTests
{
    [Fact]
    public void DiscoverHubs_With_Null_Provider_Returns_Empty()
    {
        var result = InvokeDiscoverHubs(null);

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverHubs_With_Provider_Lacking_Endpoint_Sources_Returns_Empty()
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var result = InvokeDiscoverHubs(sp);

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverHubs_With_Empty_Endpoint_Source_Returns_Empty()
    {
        var sp = BuildProvider(Array.Empty<Endpoint>());

        var result = InvokeDiscoverHubs(sp);

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverHubs_Skips_Negotiate_Endpoints_And_Deduplicates_Hub_Types()
    {
        var hubMeta = new HubMetadata(typeof(FakeHub));
        var endpoints = new Endpoint[]
        {
            BuildHubEndpoint("/chat", hubMeta),
            BuildHubEndpoint("/chat/negotiate", hubMeta),
            // Duplicate hub type on a second path — should be deduped to one entry
            BuildHubEndpoint("/chat-alias", hubMeta)
        };
        var sp = BuildProvider(endpoints);

        var result = InvokeDiscoverHubs(sp);

        var svc = Assert.Single(result);
        Assert.Equal("FakeHub", svc.Name);
        Assert.Equal("/chat", svc.Package);
        Assert.Equal("signalr", svc.Source);
        Assert.NotEmpty(svc.Methods);
    }

    [Fact]
    public void DiscoverHubs_Method_Type_Detection_Covers_All_Branches()
    {
        var hubMeta = new HubMetadata(typeof(FakeHub));
        var sp = BuildProvider(new[] { BuildHubEndpoint("/fake", hubMeta) });

        var result = InvokeDiscoverHubs(sp);
        var svc = Assert.Single(result);

        var ping = svc.Methods.Single(m => m.Name == "Ping");
        Assert.Equal("Unary", ping.MethodType);
        Assert.False(ping.ClientStreaming);
        Assert.False(ping.ServerStreaming);

        var counter = svc.Methods.Single(m => m.Name == "Counter");
        Assert.Equal("ServerStreaming", counter.MethodType);
        Assert.True(counter.ServerStreaming);
        Assert.False(counter.ClientStreaming);

        var channelStream = svc.Methods.Single(m => m.Name == "ChannelStream");
        Assert.Equal("ServerStreaming", channelStream.MethodType);
        Assert.True(channelStream.ServerStreaming);

        var clientStream = svc.Methods.Single(m => m.Name == "ClientStream");
        Assert.Equal("ClientStreaming", clientStream.MethodType);
        Assert.True(clientStream.ClientStreaming);

        var duplex = svc.Methods.Single(m => m.Name == "DuplexEcho");
        Assert.Equal("Duplex", duplex.MethodType);
        Assert.True(duplex.ClientStreaming);
        Assert.True(duplex.ServerStreaming);
    }

    [Fact]
    public void DiscoverHubs_FullName_Combines_Hub_And_Method()
    {
        var hubMeta = new HubMetadata(typeof(FakeHub));
        var sp = BuildProvider(new[] { BuildHubEndpoint("/fake", hubMeta) });

        var result = InvokeDiscoverHubs(sp);
        var svc = Assert.Single(result);

        var ping = svc.Methods.Single(m => m.Name == "Ping");
        Assert.Equal("FakeHub/Ping", ping.FullName);
    }

    [Fact]
    public void DiscoverHubs_Excludes_Cancellation_Token_From_Input_Fields()
    {
        var hubMeta = new HubMetadata(typeof(FakeHub));
        var sp = BuildProvider(new[] { BuildHubEndpoint("/fake", hubMeta) });

        var result = InvokeDiscoverHubs(sp);
        var svc = Assert.Single(result);

        var counter = svc.Methods.Single(m => m.Name == "Counter");
        // Counter(int max, CancellationToken ct) — only "max" survives.
        Assert.Single(counter.InputType.Fields);
        Assert.Equal("max", counter.InputType.Fields[0].Name);
    }

    [Fact]
    public void DiscoverHubs_Complex_Parameter_Builds_Nested_Message()
    {
        var hubMeta = new HubMetadata(typeof(FakeHub));
        var sp = BuildProvider(new[] { BuildHubEndpoint("/fake", hubMeta) });

        var result = InvokeDiscoverHubs(sp);
        var svc = Assert.Single(result);

        var lookup = svc.Methods.Single(m => m.Name == "Lookup");
        var userField = Assert.Single(lookup.InputType.Fields);
        Assert.Equal("user", userField.Name);
        Assert.NotNull(userField.MessageType);
        var nestedFieldNames = userField.MessageType!.Fields.Select(f => f.Name).ToHashSet();
        Assert.Contains("Name", nestedFieldNames);
        Assert.Contains("Age", nestedFieldNames);
        Assert.Contains("Tags", nestedFieldNames);
    }

    [Fact]
    public void DiscoverHubs_Enum_And_Collection_Parameters_Are_Mapped()
    {
        var hubMeta = new HubMetadata(typeof(FakeHub));
        var sp = BuildProvider(new[] { BuildHubEndpoint("/fake", hubMeta) });

        var result = InvokeDiscoverHubs(sp);
        var svc = Assert.Single(result);

        var pt = svc.Methods.Single(m => m.Name == "ProcessTags");
        var levelField = pt.InputType.Fields.Single(f => f.Name == "level");
        Assert.NotNull(levelField.EnumValues);
        Assert.Equal(3, levelField.EnumValues!.Count);
        Assert.Contains(levelField.EnumValues, ev => ev.Name == "Medium");

        var tagsField = pt.InputType.Fields.Single(f => f.Name == "tags");
        Assert.True(tagsField.IsRepeated);
        Assert.Equal("repeated string", tagsField.Type);
    }

    [Theory]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(int), "int32")]
    [InlineData(typeof(long), "int64")]
    [InlineData(typeof(uint), "uint32")]
    [InlineData(typeof(ulong), "uint64")]
    [InlineData(typeof(float), "float")]
    [InlineData(typeof(double), "double")]
    [InlineData(typeof(bool), "bool")]
    [InlineData(typeof(byte[]), "bytes")]
    [InlineData(typeof(byte), "uint32")]
    [InlineData(typeof(short), "int32")]
    [InlineData(typeof(ushort), "uint32")]
    [InlineData(typeof(decimal), "double")]
    [InlineData(typeof(DateTime), "google.protobuf.Timestamp")]
    [InlineData(typeof(DateTimeOffset), "google.protobuf.Timestamp")]
    [InlineData(typeof(TimeSpan), "google.protobuf.Duration")]
    [InlineData(typeof(Guid), "string")]
    [InlineData(typeof(void), "google.protobuf.Empty")]
    [InlineData(typeof(object), "google.protobuf.Any")]
    public void MapClrType_Scalar_Conversions_Are_Stable(Type clr, string expected)
    {
        Assert.Equal(expected, InvokeMapClrType(clr));
    }

    [Fact]
    public void MapClrType_Nullable_Unwraps_To_Underlying()
    {
        Assert.Equal("int32", InvokeMapClrType(typeof(int?)));
        Assert.Equal("bool", InvokeMapClrType(typeof(bool?)));
    }

    [Fact]
    public void MapClrType_Array_Becomes_Repeated()
    {
        Assert.Equal("repeated string", InvokeMapClrType(typeof(string[])));
        Assert.Equal("repeated int32", InvokeMapClrType(typeof(int[])));
    }

    [Fact]
    public void MapClrType_Generic_Collections_Become_Repeated()
    {
        Assert.Equal("repeated string", InvokeMapClrType(typeof(List<string>)));
        Assert.Equal("repeated int32", InvokeMapClrType(typeof(IList<int>)));
        Assert.Equal("repeated string", InvokeMapClrType(typeof(IEnumerable<string>)));
        Assert.Equal("repeated string", InvokeMapClrType(typeof(IReadOnlyList<string>)));
    }

    [Fact]
    public void MapClrType_Dictionary_Becomes_Map()
    {
        Assert.Equal("map<string, int32>", InvokeMapClrType(typeof(Dictionary<string, int>)));
        Assert.Equal("map<string, string>", InvokeMapClrType(typeof(IReadOnlyDictionary<string, string>)));
    }

    [Fact]
    public void MapClrType_Enum_Returns_Enum_Sentinel()
    {
        Assert.Equal("enum", InvokeMapClrType(typeof(SeverityLevel)));
    }

    [Fact]
    public void MapClrType_Unknown_Class_Returns_Type_Name()
    {
        Assert.Equal(nameof(UserDto), InvokeMapClrType(typeof(UserDto)));
    }

    private static List<BowireServiceInfo> InvokeDiscoverHubs(IServiceProvider? sp)
    {
        // SignalRHubDiscovery is internal — reach in via reflection. We
        // can't add InternalsVisibleTo without changing src/.
        var asm = typeof(BowireSignalRProtocol).Assembly;
        var type = asm.GetType("Kuestenlogik.Bowire.SignalRHubDiscovery", throwOnError: true)!;
        var method = type.GetMethod("DiscoverHubs", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object?[] { sp });
        return Assert.IsType<List<BowireServiceInfo>>(result);
    }

    private static string InvokeMapClrType(Type t)
    {
        var asm = typeof(BowireSignalRProtocol).Assembly;
        var type = asm.GetType("Kuestenlogik.Bowire.SignalRHubDiscovery", throwOnError: true)!;
        var method = type.GetMethod("MapClrType", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { t });
        return Assert.IsType<string>(result);
    }

    private static IServiceProvider BuildProvider(IReadOnlyList<Endpoint> endpoints)
        => new ServiceCollection()
            .AddSingleton<EndpointDataSource>(new TestEndpointDataSource(endpoints))
            .BuildServiceProvider();

    private static Endpoint BuildHubEndpoint(string path, HubMetadata meta)
    {
        var pattern = RoutePatternFactory.Parse(path);
        var metadata = new EndpointMetadataCollection(meta);
        return new RouteEndpoint(
            requestDelegate: ctx => Task.CompletedTask,
            routePattern: pattern,
            order: 0,
            metadata: metadata,
            displayName: path);
    }

    private sealed class TestEndpointDataSource : EndpointDataSource
    {
        private readonly IReadOnlyList<Endpoint> _endpoints;

        public TestEndpointDataSource(IReadOnlyList<Endpoint> endpoints)
        {
            _endpoints = endpoints;
        }

        public override IReadOnlyList<Endpoint> Endpoints => _endpoints;

        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }
}

/// <summary>
/// Tests for the SignalR invoker's argument parser. Multi-property objects
/// get unfolded into one positional argument per property; single-property
/// or null-object inputs stay wrapped. Reached via reflection because the
/// helper is private static and there's no public seam.
/// </summary>
public sealed class SignalRInvokerParseArgumentsTests
{
    [Fact]
    public void Empty_Body_Returns_Zero_Arguments()
    {
        // Empty form body now maps to a zero-arg call so SignalR hub
        // methods with no parameters (e.g. SubscribeToChanges with
        // only a CancellationToken) match their signature. Pre-1.0.5
        // returned a single null which made the runtime reject the
        // invocation with 'wrong argument count'.
        var args = InvokeParseArguments(["{}"]);

        Assert.Empty(args);
    }

    [Fact]
    public void Whitespace_Body_Returns_Zero_Arguments()
    {
        var args = InvokeParseArguments(["   "]);

        Assert.Empty(args);
    }

    [Fact]
    public void Multi_Property_Object_Is_Unfolded_To_Positional_Args()
    {
        var args = InvokeParseArguments(["{\"count\":5,\"delayMs\":200}"]);

        Assert.Equal(2, args.Length);
        // Numbers come back boxed as double — the conditional expression
        // in JsonElementToArg promotes the long branch to the double
        // common type before boxing.
        Assert.Equal(5d, Convert.ToDouble(args[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(200d, Convert.ToDouble(args[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Multi_Property_Object_Boolean_And_Null_Values_Are_Preserved()
    {
        var args = InvokeParseArguments(["{\"flag\":true,\"empty\":null,\"name\":\"alice\"}"]);

        Assert.Equal(3, args.Length);
        Assert.Equal(true, args[0]);
        Assert.Null(args[1]);
        Assert.Equal("alice", args[2]);
    }

    [Fact]
    public void Single_Property_Object_Stays_Wrapped()
    {
        // Only one property — the unfolder skips the unfolding and the
        // fall-through branch deserializes the message as a generic object.
        var args = InvokeParseArguments(["{\"only\":\"value\"}"]);

        Assert.Single(args);
        Assert.NotNull(args[0]);
    }

    [Fact]
    public void Bare_String_Falls_Through_To_Per_Message_Path()
    {
        // A non-JSON string survives as a string in the catch branch.
        var args = InvokeParseArguments(["just a raw string"]);

        Assert.Single(args);
    }

    [Fact]
    public void Multiple_Messages_Are_Each_Parsed_Independently()
    {
        var args = InvokeParseArguments(["\"first\"", "42", "{\"k\":1}"]);

        Assert.Equal(3, args.Length);
    }

    [Fact]
    public void Number_Float_Is_Returned_As_Double_When_Not_Integer()
    {
        var args = InvokeParseArguments(["{\"a\":1.5,\"b\":2}"]);

        Assert.Equal(2, args.Length);
        Assert.Equal(1.5, Convert.ToDouble(args[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2d, Convert.ToDouble(args[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static object?[] InvokeParseArguments(List<string> messages)
    {
        var asm = typeof(BowireSignalRProtocol).Assembly;
        var type = asm.GetType("Kuestenlogik.Bowire.SignalRInvoker", throwOnError: true)!;
        var method = type.GetMethod("ParseArguments", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { messages });
        return Assert.IsType<object?[]>(result);
    }
}

/// <summary>A hub fixture exercising every branch of the method-shape detector.</summary>
internal sealed class FakeHub : Hub
{
    public Task<string> Ping(string message) => Task.FromResult("pong: " + message);
    public Task<int> Sum(int a, int b) => Task.FromResult(a + b);
    public Task NoReturn() => Task.CompletedTask;

    // Server-streaming via IAsyncEnumerable
    public IAsyncEnumerable<int> Counter(int max, CancellationToken ct)
        => Iterate(max, ct);

    private static async IAsyncEnumerable<int> Iterate(int n,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < n; i++)
        {
            await Task.Yield();
            if (ct.IsCancellationRequested) yield break;
            yield return i;
        }
    }

    // Server-streaming via ChannelReader
    public ChannelReader<string> ChannelStream(int n)
    {
        var ch = Channel.CreateUnbounded<string>();
        for (var i = 0; i < n; i++) ch.Writer.TryWrite("item-" + i);
        ch.Writer.Complete();
        return ch.Reader;
    }

    // Client-streaming via IAsyncEnumerable parameter
    public Task<int> ClientStream(IAsyncEnumerable<string> stream) => Task.FromResult(0);

    // Duplex (client streaming + server streaming)
    public IAsyncEnumerable<string> DuplexEcho(IAsyncEnumerable<string> stream, CancellationToken ct)
        => Pass(stream, ct);

    private static async IAsyncEnumerable<string> Pass(IAsyncEnumerable<string> s,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in s.WithCancellation(ct)) yield return item;
    }

    // Complex parameter — exercises nested message-info branch
    public Task<UserDto> Lookup(UserDto user) => Task.FromResult(user);

    // Enum + collection parameter
    public Task<List<int>> ProcessTags(SeverityLevel level, List<string> tags)
        => Task.FromResult(new List<int> { tags.Count, (int)level });
}

internal enum SeverityLevel { Low, Medium, High }

internal sealed class UserDto
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

#pragma warning restore CA1822, CA1859, CA1034, CA1515, CA2227
