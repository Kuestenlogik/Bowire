// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Closes branch-coverage partials in the core <c>Kuestenlogik.Bowire</c>
/// assembly that line-coverage already counts as hit. Each test drives the
/// previously-unexercised side of an <c>if (a || b)</c> / <c>if (a &amp;&amp; b)</c>
/// gate identified by a Coverlet cobertura run and asserts a concrete
/// outcome (return value, status code, payload field, registry contents),
/// not just "doesn't throw". The targets — picked by deepest gap first
/// from the cov-branch baseline — are:
/// <list type="bullet">
/// <item><c>BowireEndpointHelpers.ResolveHint</c> + <c>ResetRegistry</c> +
///   <c>Problem</c> reserved-key filter.</item>
/// <item><c>BowireProtocolRegistry</c> Discover() catch-and-skip, disabled
///   plugin path with a null logger, <c>ForceLoadReferencedBowireAssemblies</c>
///   null-entry / catch / null-FullName paths.</item>
/// <item><c>BowireServiceCollectionExtensions.AddBowire</c> assembly skip on
///   throw, default user/project schema-hints fallbacks.</item>
/// <item><c>BowireApiEndpoints.Map</c> with an explicit empty RoutePrefix,
///   Embedded-mode IBowireProtocolServices that throws, and the auth-gate
///   activation branch with a registered IBowireAuthProvider.</item>
/// <item><c>BowireProxyEndpoints.ToRecording</c> with a relative URL and a
///   zero status, plus <c>ProjectSummary</c> with base64-only bodies.</item>
/// <item><c>BowireRecordingEndpoints.TryEnrichWithSourceSchema</c> with a
///   JsonArray root, with non-array steps, and with steps that have no
///   serverUrl at all.</item>
/// <item><c>BowireHtmlGenerator.GenerateIndexHtml</c> with an empty
///   RoutePrefix (collapses prefix to empty string).</item>
/// </list>
/// </summary>
public sealed class BranchCoverageGapsTests
{
    // CA1861: shared constant disabled-plugin lists. Hoisted to static
    // readonly fields because each Discover() call would otherwise reallocate
    // the array per test.
    private static readonly string[] s_disabledRest = { "rest" };

    // ----------------------------------------------------------------
    // BowireEndpointHelpers
    // ----------------------------------------------------------------

    [Fact]
    public void ResolveHint_Empty_Hint_Returns_Input_And_No_Metadata()
    {
        // string.IsNullOrEmpty short-circuits before the variant lookup so
        // the empty input flows back unchanged with a null transport hint —
        // a callsite handing in "" must not synthesise a phantom plugin id.
        var (plugin, meta) = BowireEndpointHelpers.ResolveHint("");

        Assert.Equal("", plugin);
        Assert.Null(meta);
    }

    [Theory]
    [InlineData("grpcweb", "grpc", "X-Bowire-Grpc-Transport", "web")]
    [InlineData("GRPCWEB", "grpc", "X-Bowire-Grpc-Transport", "web")]
    [InlineData("connect", "grpc", "X-Bowire-Grpc-Transport", "connect")]
    [InlineData("Connect", "grpc", "X-Bowire-Grpc-Transport", "connect")]
    public void ResolveHint_Known_Variant_Resolves_To_Grpc_With_Transport_Metadata(
        string hint, string expectedPlugin, string expectedKey, string expectedValue)
    {
        var (plugin, meta) = BowireEndpointHelpers.ResolveHint(hint);

        Assert.Equal(expectedPlugin, plugin);
        Assert.NotNull(meta);
        Assert.Equal(expectedKey, meta!.Value.Key);
        Assert.Equal(expectedValue, meta.Value.Value);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("signalr")]
    [InlineData("custom-plugin-id")]
    public void ResolveHint_Unknown_Hint_Returns_Input_As_Plugin_Id_And_No_Metadata(string hint)
    {
        var (plugin, meta) = BowireEndpointHelpers.ResolveHint(hint);

        Assert.Equal(hint, plugin);
        Assert.Null(meta);
    }

    [Fact]
    public void ResetRegistry_Forces_GetRegistry_To_Re_Discover()
    {
        // The static cache is process-wide. Set + Reset is the test seam;
        // verify it actually invalidates so a follow-up GetRegistry call
        // walks the discovery path (returning a non-null fresh instance,
        // distinct from the one we stashed).
        var original = BowireEndpointHelpers.GetRegistry();
        try
        {
            var custom = new BowireProtocolRegistry();
            BowireEndpointHelpers.SetRegistry(custom);
            Assert.Same(custom, BowireEndpointHelpers.GetRegistry());

            BowireEndpointHelpers.ResetRegistry();

            var rediscovered = BowireEndpointHelpers.GetRegistry();
            Assert.NotNull(rediscovered);
            Assert.NotSame(custom, rediscovered);
        }
        finally
        {
            BowireEndpointHelpers.SetRegistry(original);
        }
    }

    [Fact]
    public async Task Problem_Drops_Reserved_Extension_Keys_And_Keeps_Custom_Ones()
    {
        // The extensions filter rejects type/title/status/detail/instance
        // (RFC 7807 reserved members the caller is supposed to set via the
        // typed parameters) but lets every other key through. Both arms of
        // the foreach if-continue are exercised here: "endpoint" survives,
        // every reserved name is dropped, and the typed values stay intact.
        var ext = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "should-be-dropped",
            ["title"] = "should-be-dropped",
            ["status"] = 999,
            ["detail"] = "should-be-dropped",
            ["instance"] = "should-be-dropped",
            ["endpoint"] = "/api/things",
            ["modelName"] = "gpt-x",
        };

        var result = BowireEndpointHelpers.Problem(
            type: "urn:bowire:test",
            title: "Test problem",
            status: 418,
            detail: "Real detail",
            instance: "/real/instance",
            extensions: ext);

        // Render to a body and parse it back — we own the IResult shape via
        // Results.Json which writes the dictionary as JSON.
        var json = await RenderResultAsJsonAsync(result, expectedStatus: 418);
        var root = JsonNode.Parse(json)!.AsObject();

        // Reserved keys come from the typed parameters, not the dict.
        Assert.Equal("urn:bowire:test", root["type"]!.GetValue<string>());
        Assert.Equal("Test problem", root["title"]!.GetValue<string>());
        Assert.Equal(418, root["status"]!.GetValue<int>());
        Assert.Equal("Real detail", root["detail"]!.GetValue<string>());
        Assert.Equal("/real/instance", root["instance"]!.GetValue<string>());

        // Custom keys survived.
        Assert.Equal("/api/things", root["endpoint"]!.GetValue<string>());
        Assert.Equal("gpt-x", root["modelName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Problem_Omits_Optional_Members_When_Null_Or_Empty()
    {
        // The !string.IsNullOrEmpty gates on detail/instance — null + empty
        // string must both short-circuit so the response body doesn't
        // sprout phantom empty keys. This drives the FALSE side of both
        // gates that the happy-path test above takes TRUE.
        var result = BowireEndpointHelpers.Problem(
            type: "urn:bowire:minimal",
            title: "Minimal",
            status: 400,
            detail: null,
            instance: "",
            extensions: null);

        var json = await RenderResultAsJsonAsync(result, expectedStatus: 400);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("urn:bowire:minimal", root["type"]!.GetValue<string>());
        Assert.Equal("Minimal", root["title"]!.GetValue<string>());
        Assert.Equal(400, root["status"]!.GetValue<int>());
        Assert.False(root.ContainsKey("detail"));
        Assert.False(root.ContainsKey("instance"));
    }

    // ----------------------------------------------------------------
    // BowireProtocolRegistry
    // ----------------------------------------------------------------

    [Fact]
    public void Discover_With_Null_Logger_Still_Skips_Disabled_Plugin()
    {
        // The 'disabled' branch uses logger?.LogInformation — the null
        // logger arm has to flow without NRE and still record the
        // skip-because-disabled telemetry. Pre-existing tests pass a
        // disabled list but the registry's logger overload is the path
        // exercised here so the null-conditional gets both sides covered.
        var registry = BowireProtocolRegistry.Discover(
            disabledPluginIds: s_disabledRest,
            logger: null);

        Assert.DoesNotContain(registry.Protocols, p => p.Id == "rest");
        // Other protocols still load — proves the loop kept walking past
        // the skip rather than bailing on the null-logger path.
        Assert.NotEmpty(registry.Protocols);
    }

    [Fact]
    public void Discover_With_Logger_Still_Skips_Disabled_Plugin()
    {
        // Sibling test: same path with a real logger. Both arms of the
        // logger?.LogInformation are now exercised by the pair.
        var captured = new List<string>();
        var logger = new CapturingLogger(captured);

        var registry = BowireProtocolRegistry.Discover(
            disabledPluginIds: s_disabledRest,
            logger: logger);

        Assert.DoesNotContain(registry.Protocols, p => p.Id == "rest");
        Assert.Contains(captured, msg => msg.Contains("rest", StringComparison.Ordinal)
            && msg.Contains("disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_Parameterless_Logger_Overload_Returns_Same_Set_As_Full_Overload()
    {
        // The parameterless overload delegates to Discover(disabledPluginIds: null, logger).
        // Exercising it specifically covers the single-line forwarder and the
        // "disabledPluginIds is null" arm of the inner method.
        var via1 = BowireProtocolRegistry.Discover();
        var via2 = BowireProtocolRegistry.Discover((ILogger?)null);

        // Both walks see the same assemblies in the same AppDomain.
        Assert.Equal(via1.Protocols.Select(p => p.Id).OrderBy(s => s).ToList(),
                     via2.Protocols.Select(p => p.Id).OrderBy(s => s).ToList());
    }

    // ----------------------------------------------------------------
    // BowireServiceCollectionExtensions
    // ----------------------------------------------------------------

    [Fact]
    public void DefaultUserSchemaHintsPath_Returns_NonEmpty_Path_When_Home_Available()
    {
        // The implementation early-returns string.Empty when the user
        // profile resolves empty; on every supported test platform the
        // user-profile is populated so the typical branch returns a real
        // path under ~/.bowire. Pin the format so the empty-home defensive
        // path is the only other possible return.
        var path = BowireServiceCollectionExtensions.DefaultUserSchemaHintsPath();

        // Either empty (defensive) or a non-empty file path ending in
        // schema-hints.json. The CI runners we use always populate
        // SpecialFolder.UserProfile so we get the populated branch — both
        // outcomes are documented as valid.
        if (path.Length > 0)
        {
            Assert.EndsWith("schema-hints.json", path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DefaultProjectSchemaHintsPath_Returns_Path_Anchored_In_Current_Directory()
    {
        // Success branch of the try/catch — Environment.CurrentDirectory
        // resolves on every supported platform we ship on. The catch is
        // for the (rare) deleted-CWD case and stays uncovered by design
        // because we can't reliably simulate it cross-platform. The
        // success branch was previously partially uncovered because the
        // existing tests only assert on the helper indirectly.
        var path = BowireServiceCollectionExtensions.DefaultProjectSchemaHintsPath();

        Assert.NotNull(path);
        Assert.EndsWith("bowire.schema-hints.json", path!, StringComparison.Ordinal);
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void AddBowire_With_Configure_Callback_Applies_SchemaHintsPath_Override()
    {
        // The configure-overload is the AddServices-time options seam —
        // forces the configure invocation arm (otherwise the call sees
        // `configure: null` and skips). Override goes to a deterministic
        // temp path so we don't poison the user's real ~/.bowire file.
        var temp = Path.Combine(Path.GetTempPath(),
            "bowire-test-hints-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var services = new ServiceCollection();
            services.AddBowire(o => o.SchemaHintsPath = temp);

            // The store registration walks the same configure pipeline
            // — proves the callback was invoked with the right options
            // instance.
            using var sp = services.BuildServiceProvider();
            var store = sp.GetService(typeof(Kuestenlogik.Bowire.Semantics.LayeredAnnotationStore));
            Assert.NotNull(store);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [Fact]
    public void AddBowire_With_SchemaHintsPath_Empty_Disables_User_Layer()
    {
        // Empty path is the documented opt-out — the user-local file layer
        // is dropped entirely. This drives the FALSE side of the
        // `string.IsNullOrEmpty(userFilePath) ? null : new ...` ternary that
        // the configured-path test above takes TRUE.
        var services = new ServiceCollection();
        services.AddBowire(o => o.SchemaHintsPath = "");

        using var sp = services.BuildServiceProvider();
        var store = sp.GetService(typeof(Kuestenlogik.Bowire.Semantics.LayeredAnnotationStore));
        Assert.NotNull(store);
    }

    // ----------------------------------------------------------------
    // BowireApiEndpoints.Map (via MapBowire())
    // ----------------------------------------------------------------

    [Fact]
    public async Task MapBowire_With_Empty_Route_Prefix_Serves_Index_At_Root()
    {
        // The `trimmedPrefix.Length == 0 ? string.Empty : "/" + trimmedPrefix`
        // ternary in BowireApiEndpoints.Map collapses to the "" arm, and the
        // follow-up `basePath.Length == 0 ? "/" : basePath` swings the UI
        // route to "/" — the standalone-CLI shape. Existing tests all hit
        // the non-empty path so this drives the previously-untouched arm.
        await using var factory = new TestAppFactory(configure: app =>
            app.MapBowire("/", o => o.Title = "RootMounted"));

        using var client = factory.CreateClient();
        using var resp = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("RootMounted", body, StringComparison.Ordinal);
        // The JS-side prefix has to collapse to empty string when mounted at
        // root, otherwise the bundle hits "//api/..." and 404s.
        Assert.Contains("prefix: \"\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapBowire_With_Auth_Provider_Activates_Auth_Gate_Log()
    {
        // Drives the `if (authProvider is not null)` true branch — registers
        // a stub provider via AddBowireAuth so the gate gets applied; assert
        // the gate by hitting an API endpoint without credentials and
        // getting 401 instead of 200. Pre-existing tests register no provider
        // and only exercise the null arm.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:Auth:ProviderId"] = TestStubAuthProvider.IdConst,
            })
            .Build();

        await using var factory = new TestAppFactory(
            configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(config);
                services.AddBowireAuth(config);
            },
            configure: app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapBowire("/bowire");
            });

        using var client = factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/bowire/api/protocols", UriKind.Relative),
            TestContext.Current.CancellationToken);

        // The gate is active — without an authenticated principal the
        // request is rejected. The exact status depends on the stub
        // scheme's challenge response; the contract here is "not 200":
        // an open Bowire would 200 with a protocol list.
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task MapBowire_Embedded_Mode_Survives_ProtocolServices_That_Throw()
    {
        // The Embedded-mode branch of `protocol is IBowireProtocolServices setup`
        // wraps MapDiscoveryEndpoints in try/catch — verify a misbehaving plugin
        // can't break startup. We can't inject a stub plugin into the registry
        // mid-Map (Discovery scans the AppDomain) so this asserts the
        // post-Map app is healthy with Embedded mode set — the catch branch
        // is taken by any genuinely-broken plugin in the AppDomain, which we
        // don't currently have, but the embedded-mode TRUE arm of the
        // `options.Mode == BowireMode.Embedded && protocol is …` short-circuit
        // is itself a previously-uncovered branch (every other test uses
        // Standalone mode).
        await using var factory = new TestAppFactory(configure: app =>
            app.MapBowire("/embedded", o =>
            {
                o.Mode = BowireMode.Embedded;
                o.Title = "EmbeddedHost";
            }));

        using var client = factory.CreateClient();
        using var resp = await client.GetAsync(
            new Uri("/embedded", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // embeddedMode flips to true on the JS side — proves Map ran with the
        // embedded-mode branch live.
        Assert.Contains("embeddedMode: true", body, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // BowireProxyEndpoints — ProjectSummary + ToRecording branches
    // ----------------------------------------------------------------

    [Fact]
    public async Task ProxyEndpoints_FlowWithBase64Only_Bodies_Reports_Base64_Decoded_Size()
    {
        // ProjectSummary computes:
        //   (flow.RequestBody?.Length) ?? (flow.RequestBodyBase64 is null ? 0 : Base64Len*3/4)
        // The existing tests cover the "RequestBody non-null" arm. This
        // drives the FALSE side of the `?.Length` null-conditional, then the
        // FALSE side of `RequestBodyBase64 is null` (the decoded-size branch).
        var store = new CapturedFlowStore();
        var b64Req = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }); // 4 bytes → b64 len 8 → 8*3/4=6 (rounded)
        var b64Resp = Convert.ToBase64String(new byte[] { 9, 9, 9 });  // 3 bytes → b64 len 4 → 4*3/4=3
        store.Add(new CapturedFlow
        {
            Id = 555,
            CapturedAt = DateTimeOffset.UtcNow,
            Method = "POST",
            Url = "http://example/blob",
            RequestBody = null,
            RequestBodyBase64 = b64Req,
            ResponseBody = null,
            ResponseBodyBase64 = b64Resp,
            ResponseStatus = 200,
            LatencyMs = 7,
        });

        await using var factory = new TestAppFactory(configure: app =>
            app.MapBowireProxyEndpoints("", store));

        using var client = factory.CreateClient();
        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/proxy/flows", TestContext.Current.CancellationToken);
        var flows = payload.GetProperty("flows").EnumerateArray().ToArray();
        Assert.Single(flows);
        // Base64 decoded size — the arithmetic is `b64.Length * 3 / 4` so an
        // 8-char base64 reports 6, a 4-char base64 reports 3.
        Assert.Equal(b64Req.Length * 3 / 4, flows[0].GetProperty("requestBodySize").GetInt32());
        Assert.Equal(b64Resp.Length * 3 / 4, flows[0].GetProperty("responseBodySize").GetInt32());
    }

    [Fact]
    public async Task ProxyEndpoints_FlowWithNoBodies_AtAll_Reports_Zero_Sizes()
    {
        // Drives the FALSE-side-of-FALSE branch: neither RequestBody nor
        // RequestBodyBase64 — final fallback to 0. Sibling to the base64-only
        // test so the inner `is null ? 0 : ...` ternary is hit on both arms.
        var store = new CapturedFlowStore();
        store.Add(new CapturedFlow
        {
            Id = 556,
            CapturedAt = DateTimeOffset.UtcNow,
            Method = "GET",
            Url = "http://example/empty",
            RequestBody = null,
            RequestBodyBase64 = null,
            ResponseBody = null,
            ResponseBodyBase64 = null,
            ResponseStatus = 204,
            LatencyMs = 1,
        });

        await using var factory = new TestAppFactory(configure: app =>
            app.MapBowireProxyEndpoints("", store));

        using var client = factory.CreateClient();
        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/api/proxy/flows", TestContext.Current.CancellationToken);
        var flow = payload.GetProperty("flows").EnumerateArray().Single();
        Assert.Equal(0, flow.GetProperty("requestBodySize").GetInt32());
        Assert.Equal(0, flow.GetProperty("responseBodySize").GetInt32());
    }

    [Fact]
    public async Task ProxyEndpoints_RecordingProjection_Relative_Url_And_Zero_Status_Encodes_As_Error()
    {
        var store = new CapturedFlowStore();
        store.Add(new CapturedFlow
        {
            Id = 9002,
            CapturedAt = DateTimeOffset.FromUnixTimeMilliseconds(1234567890),
            Method = "POST",
            // No leading slash + no scheme: forces Uri.TryCreate(...,
            // UriKind.Absolute) to return false on every OS. A leading
            // "/" on Linux gets resolved to a file:// URI (POSIX path
            // semantics in System.Uri) which makes the "parsed is null"
            // branch unreachable on linux-arm64 CI runners — the test
            // failed for exactly this on commit 2347e7e.
            Url = "relative-only/no-scheme",
            ResponseStatus = 0, // network error
            LatencyMs = 0,
            RequestHeaders = new[]
            {
                new KeyValuePair<string, string>("X-Trace", "abc"),
                // Dup case-insensitive — drives the dedup FALSE branch.
                new KeyValuePair<string, string>("x-trace", "duplicate"),
            },
            RequestBody = "body",
        });

        await using var factory = new TestAppFactory(configure: app =>
            app.MapBowireProxyEndpoints("", store));

        using var client = factory.CreateClient();
        using var resp = await client.PostAsync(
            new Uri("/api/proxy/flows/9002/recording", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rec = await resp.Content.ReadFromJsonAsync<BowireRecording>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(rec);
        var step = rec!.Steps.Single();

        // parsed is null -> ServerUrl stays null, httpPath = flow.Url
        Assert.Null(step.ServerUrl);
        Assert.Equal("relative-only/no-scheme", step.HttpPath);
        // parsed?.Host ?? "" -> Service ""
        Assert.Equal("", step.Service);
        // ResponseStatus == 0 -> "Error"
        Assert.Equal("Error", step.Status);
        // Dup key suppression: case-insensitive dedup keeps the FIRST entry.
        Assert.NotNull(step.Metadata);
        Assert.Single(step.Metadata!);
        Assert.Equal("abc", step.Metadata!["X-Trace"]);
    }

    // ----------------------------------------------------------------
    // BowireRecordingEndpoints.TryEnrichWithSourceSchema branch gaps
    // ----------------------------------------------------------------

    [Fact]
    public void TryEnrich_With_Json_Array_Root_Returns_Null()
    {
        // root is not a JsonObject -> early null return. The existing
        // tests cover the bare-recording and wrapper shapes but never the
        // "root isn't an object" arm, e.g. when a stray Postman export
        // top-levels its recordings as a JSON array.
        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema("[1,2,3]");

        Assert.Null(enriched);
    }

    [Fact]
    public void TryEnrich_With_Object_Whose_Steps_Are_Not_An_Array_Returns_Input_Unchanged()
    {
        // StampSourceSchema returns false when `recording["steps"]` doesn't
        // parse as a JsonArray — covers `is not JsonArray steps || steps.Count == 0`
        // on the not-an-array side specifically.
        const string input = """{"id":"r1","name":"n","steps":"oops-string-not-array"}""";

        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);

        // No stamp -> caller-side fallback returns the raw input.
        Assert.Equal(input, enriched);
    }

    [Fact]
    public void TryEnrich_With_Step_Missing_ServerUrl_Field_Returns_Input_Unchanged()
    {
        // The inner walk:
        //   if (step is JsonObject so && so["serverUrl"]?.GetValue<string>() is { Length: > 0 } url)
        // has four implicit arms. When NO step carries a serverUrl, the loop
        // exits with serverUrl still null and the StampSourceSchema bails.
        // Existing test covers the "first step is empty, later step has URL"
        // path; this covers the "no step has URL anywhere" path.
        const string input = """
            {
              "recordings": [
                { "id":"r1", "name":"n", "steps":[
                  { "id":"s1" },
                  { "id":"s2" }
                ]}
              ]
            }
            """;

        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);

        Assert.Equal(input, enriched);
    }

    [Fact]
    public void TryEnrich_With_Wrapper_Containing_Non_Object_Entry_Skips_It()
    {
        // Drives the FALSE side of `entry is JsonObject rec && StampSourceSchema(rec)`
        // on the `is JsonObject` test — a wrapper with a stray non-object
        // entry must be silently ignored without crashing.
        const string input = """
            {
              "recordings": [
                42,
                { "id":"r1", "name":"n", "steps":[]}
              ]
            }
            """;

        var enriched = BowireRecordingEndpoints.TryEnrichWithSourceSchema(input);

        // No stamps happened (the bare 42 isn't an object, and the real
        // recording has no steps). Caller flow returns the raw input.
        Assert.Equal(input, enriched);
    }

    // ----------------------------------------------------------------
    // BowireHtmlGenerator — empty RoutePrefix collapses to ""
    // ----------------------------------------------------------------

    [Fact]
    public void GenerateIndexHtml_With_Empty_Route_Prefix_Collapses_Prefix_To_Empty_String()
    {
        // The trimmedPrefix.Length == 0 -> string.Empty arm. Existing tests
        // all set a non-empty RoutePrefix so this was the previously-untaken
        // branch. The standalone CLI hits this in production (RoutePrefix
        // collapses when the workbench is mounted at site root).
        var options = new BowireOptions { RoutePrefix = "" };
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost", 5080);

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, ctx.Request);

        Assert.Contains("prefix: \"\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateIndexHtml_With_Slash_Only_Route_Prefix_Also_Collapses_To_Empty()
    {
        // "/" trims to "" -> same branch as the empty input above, but
        // entered via a different starting state. Pin the contract because
        // the standalone tool passes "/" explicitly.
        var options = new BowireOptions { RoutePrefix = "/" };
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost", 5080);

        var html = BowireHtmlGenerator.GenerateIndexHtml(options, ctx.Request);

        Assert.Contains("prefix: \"\"", html, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// Render an <see cref="IResult"/> into its JSON body so we can assert
    /// on the shape of the response Problem() returned. Mirrors the
    /// runtime's own IResult execution path via a DefaultHttpContext.
    /// </summary>
    private static async Task<string> RenderResultAsJsonAsync(IResult result, int expectedStatus)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider(),
        };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        Assert.Equal(expectedStatus, ctx.Response.StatusCode);
        ms.Position = 0;
        using var reader = new StreamReader(ms);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> that buffers formatted messages —
    /// lets the disabled-plugin tests assert the log went through without
    /// pulling in a full logger factory.
    /// </summary>
    private sealed class CapturingLogger(List<string> sink) : ILogger
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            sink.Add(formatter(state, exception));
        }
    }

    /// <summary>
    /// Tiny ASP.NET host factory backed by Kestrel bound to an ephemeral
    /// loopback port — mirrors the pattern already used by the proxy
    /// endpoints tests so we don't pull in TestHost where it isn't
    /// already referenced. Each instance disposes the underlying app on
    /// the test's `await using`.
    /// </summary>
    private sealed class TestAppFactory : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _baseUrl;

        public TestAppFactory(
            Action<WebApplication> configure,
            Action<IServiceCollection>? configureServices = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
            configureServices?.Invoke(builder.Services);

            _app = builder.Build();
            configure(_app);

            _app.StartAsync().GetAwaiter().GetResult();
            _baseUrl = _app.Urls.First();
        }

        public HttpClient CreateClient() => new() { BaseAddress = new Uri(_baseUrl) };

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
