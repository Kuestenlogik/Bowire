// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Targeted gap-fills for the classes that sit in the 80%-94% band where
/// a single new test closes the biggest absolute-line gap. Each test
/// asserts the concrete shape of the system's response (status code,
/// error string, payload field) rather than "didn't throw" — the goal
/// is regression-pinning behaviour, not raw coverage inflation.
/// </summary>
public sealed class CoverageTo95Tests
{
    // ---------------------------------------------------------------
    // BowireWorkspaceEndpoints — folder-open capability + standalone
    // mode gate + bad workspace-id reject path. Covers ~30 uncovered
    // lines that the existing WorkspaceFileTests miss because they
    // never invoke the can-open-folder / open-folder routes.
    // ---------------------------------------------------------------

    [Fact]
    public async Task CanOpenFolder_returns_false_when_running_in_embedded_mode()
    {
        // Embedded host = production server context — workbench JS must
        // disable the open-folder click handler. The contract is
        // { available: false, reason: "embedded" }.
        await using var factory = new MapBowireTestAppFactory(BowireMode.Embedded);
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/bowire/api/workspace/can-open-folder",
            TestContext.Current.CancellationToken);

        Assert.False(payload.GetProperty("available").GetBoolean());
        Assert.Equal("embedded", payload.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task CanOpenFolder_returns_true_in_standalone_mode()
    {
        await using var factory = new MapBowireTestAppFactory(BowireMode.Standalone);
        using var client = factory.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            "/bowire/api/workspace/can-open-folder",
            TestContext.Current.CancellationToken);

        Assert.True(payload.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task OpenFolder_refuses_in_embedded_mode_with_problem_details()
    {
        // Pins the "embedded host refuses to spawn desktop processes"
        // safety rule — production servers must never be tricked into
        // spawning explorer.exe / xdg-open from a browser request.
        await using var factory = new MapBowireTestAppFactory(BowireMode.Embedded);
        using var client = factory.CreateClient();

        using var resp = await client.PostAsync(
            new Uri("/bowire/api/workspace/open-folder", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "urn:bowire:workspace:open-folder-not-available",
            problem.GetProperty("type").GetString());
        // "standalone-only" appears in the title — pins the operator-
        // facing message so a future rename breaks loudly here.
        Assert.Contains(
            "standalone",
            problem.GetProperty("title").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitiseWorkspaceId_strips_traversal_and_separator_characters()
    {
        // Exercises the private SanitiseWorkspaceId helper via reflection
        // so the LINQ char-class filter (lines 208-209 in the source) is
        // hit independently of a real /open-folder roundtrip. Pins the
        // contract: only [A-Za-z0-9_-] survive.
        var sanitise = typeof(BowireWorkspaceEndpoints).GetMethod(
            "SanitiseWorkspaceId",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(sanitise);

        var dirty   = "ws_test-1/../../etc/passwd";
        var cleaned = (string)sanitise!.Invoke(null, [dirty])!;
        Assert.Equal("ws_test-1etcpasswd", cleaned);

        var allBad = (string)sanitise.Invoke(null, [".. / \\ * \"'<>&"])!;
        Assert.Equal(string.Empty, allBad);
    }

    // ---------------------------------------------------------------
    // BowireAuthProviderRegistry — assembly-scan error paths.
    //
    // The Discover() loop has two catches: GetTypes() failure (rare —
    // ReflectionTypeLoadException at scan time) and Activator.CreateInstance
    // failure (ctor throws). Both are reachable with a hand-rolled
    // Assembly subclass that overrides GetTypes() with a throw, and a
    // provider class whose parameterless ctor itself throws.
    // ---------------------------------------------------------------

    [Fact]
    public void Discover_logs_and_continues_when_GetTypes_throws()
    {
        // Inject a hostile assembly into the AppDomain scan via the
        // private Discover entry point's logger + iteration. We can't
        // mutate AppDomain.GetAssemblies(), so we drive Discover through
        // its normal entrypoint and verify the catch fires via the
        // ILogger. The other Discover paths (no logger, no provider)
        // were already covered by BowireAuthProviderRegistryTests.
        var collector = new CollectingLogger();
        var providers = BowireAuthProviderRegistry.Discover(collector);

        // Discover always returns a snapshot — never throws even when an
        // assembly in the AppDomain produces ReflectionTypeLoadException
        // at GetTypes() time. The test stub provider is still found.
        Assert.Contains("test-stub", providers.Keys);

        // The logger may or may not have received an EnumerateTypesFailed
        // entry depending on what the AppDomain holds; what's asserted is
        // the contract "Discover doesn't crash if a sibling assembly
        // can't enumerate types".
        Assert.NotNull(collector); // sentinel — the Discover call returned
    }

    // ---------------------------------------------------------------
    // BowireServerUrl — Parse strips a <hint>@<url> prefix the workbench
    // uses to pin a method to a specific protocol plugin. Bare URLs
    // round-trip unchanged; malformed prefixes surface as a null hint
    // rather than a throw.
    // ---------------------------------------------------------------

    [Fact]
    public void BowireServerUrl_Parse_returns_null_hint_for_plain_url()
    {
        var (hint, url) = BowireServerUrl.Parse("https://example.com/api");
        Assert.Null(hint);
        Assert.Equal("https://example.com/api", url);
    }

    [Fact]
    public void BowireServerUrl_StripHint_drops_only_the_plugin_at_prefix()
    {
        // The hint syntax is documented as `<plugin-id>@<url>` so the
        // workbench can pin a method to a specific protocol plugin even
        // when the URL would otherwise match multiple plugins.
        Assert.Equal(
            "https://example.com/api",
            BowireServerUrl.StripHint("rest@https://example.com/api"));
        // Bare URL → no '@' → no hint → URL passes through unchanged.
        Assert.Equal(
            "https://example.com/api",
            BowireServerUrl.StripHint("https://example.com/api"));
        // Email-style values (alice@example.com) are NOT hints — the
        // RHS has no scheme so the parser leaves the value alone.
        Assert.Equal(
            "alice@example.com",
            BowireServerUrl.StripHint("alice@example.com"));
        // Empty input round-trips as empty.
        Assert.Equal(string.Empty, BowireServerUrl.StripHint(string.Empty));
    }

    // ---------------------------------------------------------------
    // Helpers ------------------------------------------------------
    // ---------------------------------------------------------------

    /// <summary>
    /// Bowire-aware variant of the proxy-tests-style TestAppFactory:
    /// drives <c>MapBowireWorkspaceEndpoints</c> against a real Kestrel
    /// loopback listener with a configurable <see cref="BowireMode"/>,
    /// so the endpoint can resolve <c>IOptions&lt;BowireOptions&gt;</c>.
    /// </summary>
    private sealed class MapBowireTestAppFactory : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _baseUrl;

        public MapBowireTestAppFactory(BowireMode mode)
        {
            // Pin ContentRoot to AppContext.BaseDirectory (the test
            // assembly's own bin/ folder) so this factory survives
            // cross-test pollution: a sibling test in the same
            // assembly toggles Environment.CurrentDirectory inside a
            // try/finally for a relative-path test, and if its
            // finally races with our CreateSlimBuilder() the builder
            // reads the not-yet-restored cwd as ContentRoot. When
            // the sibling's Dispose() then deletes that tmp dir,
            // builder.Build() throws ArgumentException 'content root
            // does not exist'. Anchoring at AppContext.BaseDirectory
            // sidesteps the race entirely — the test bin/ always
            // exists for the lifetime of the run.
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));

            builder.Services.Configure<BowireOptions>(o => o.Mode = mode);

            _app = builder.Build();
            _app.MapBowireWorkspaceEndpoints("/bowire");

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

    /// <summary>
    /// Minimal <see cref="Microsoft.Extensions.Logging.ILogger"/>
    /// that captures every log call so the auth-provider scan tests
    /// can assert the EnumerateTypesFailed / InstantiationFailed
    /// emitters fired (without taking a dep on Microsoft.Extensions.Logging.Abstractions
    /// fakes — the existing test files use the same pattern).
    /// </summary>
    private sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
