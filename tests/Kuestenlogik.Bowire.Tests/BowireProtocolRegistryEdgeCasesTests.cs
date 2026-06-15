// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Plugins.Sidecar;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Edge-case tests for <see cref="BowireProtocolRegistry"/> focused on
/// the loader+logger surface: ensures the optional <see cref="ILogger"/>
/// hook receives the expected warnings on partial-failure paths, and
/// that <c>Discover()</c> registers sidecar plugins found in the user
/// plugin root. Together with <see cref="BowireProtocolRegistryTests"/>
/// these close the line-coverage gap on the registry's internal
/// <c>ForceLoadReferencedBowireAssemblies</c> auto-loader and the
/// sidecar Register branch inside Discover.
///
/// Tests that write into the real <c>~/.bowire/plugins/</c> directory or
/// into the test binary's own folder are gated on the
/// <see cref="CapturingLogger"/> capture state — they restore the
/// filesystem at the end of each test to avoid leaking state.
/// </summary>
// CwdSerialised — these tests mutate the entry-assembly directory and the
// user plugin root, so they must NOT run in parallel with other registry
// tests that call Discover() on the same paths.
[Collection("CwdSerialised")]
public sealed class BowireProtocolRegistryEdgeCasesTests
{
    private static readonly string[] s_disabledRestOnly = { "rest" };


    [Fact]
    public void Discover_CorruptBowireDllInEntryDir_LogsAutoLoadWarning()
    {
        // ForceLoadReferencedBowireAssemblies scans the entry assembly's
        // folder for `Kuestenlogik.Bowire*.dll` and force-loads any it
        // hasn't seen yet. A corrupt dll on that glob hits Assembly.LoadFrom
        // which throws BadImageFormatException; the catch must invoke
        // logger.LogWarning so operators can see the broken artifact in
        // /api/plugins/health rather than wondering why a plugin didn't
        // show up.
        var entryDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
        // Unique suffix so the matching glob (Kuestenlogik.Bowire*.dll)
        // picks it up, but the rest of the AppDomain hasn't already
        // loaded an assembly by that simple name.
        var bogusName = "Kuestenlogik.Bowire.EdgeCaseBogus_" + Guid.NewGuid().ToString("N");
        var bogusPath = SafePath.Combine(entryDir, bogusName + ".dll");
        File.WriteAllBytes(bogusPath, [0x00, 0x01, 0x02, 0x03]);

        var logger = new CapturingLogger();
        try
        {
            // Trigger the scan. We don't care about the registry contents
            // for this test — only the side-effect log entry.
            var registry = BowireProtocolRegistry.Discover(disabledPluginIds: null, logger: logger);
            Assert.NotNull(registry);
        }
        finally
        {
            try { File.Delete(bogusPath); } catch { /* best-effort */ }
        }

        // Expect at least one warning whose message references the
        // failing dll (the Bowire-prefix glob is the only code path that
        // would land such a message).
        var warning = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains("auto-load", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains(bogusName, StringComparison.Ordinal));
        Assert.NotNull(warning.Exception);
        // BadImageFormatException is the deterministic outcome for the
        // 4-byte garbage we wrote — pin it so a future runtime that
        // silently turned that into a different exception type still
        // triggers a review.
        Assert.IsAssignableFrom<BadImageFormatException>(warning.Exception);
    }

    [Fact]
    public void Discover_RegistersSidecarPluginsFromDefaultRoot()
    {
        // BowireProtocolRegistry.Discover always passes pluginRoot: null
        // to SidecarPluginDiscovery, which defaults to ~/.bowire/plugins/.
        // To exercise the sidecar Register branch (lines 165-167 of
        // BowireProtocolRegistry) we drop a manifest into that real
        // directory with a uniquely-named protocol id, run Discover, and
        // clean up immediately. The CwdSerialised collection prevents
        // races with other Discover()-calling tests.
        var defaultRoot = SidecarPluginDiscovery.DefaultPluginRoot;
        Directory.CreateDirectory(defaultRoot);
        var pluginId = "bowire-sidecar-edge-" + Guid.NewGuid().ToString("N");
        var pluginDir = SafePath.Combine(defaultRoot, pluginId);
        Directory.CreateDirectory(pluginDir);
        var manifestPath = SafePath.Combine(pluginDir, SidecarPluginManifest.FileName);
        File.WriteAllText(manifestPath,
            $$"""
            {
              "packageId": "{{pluginId}}",
              "protocol": { "id": "{{pluginId}}", "name": "Edge Sidecar" },
              "executable": "fake-sidecar.sh",
              "version": "1.0.0"
            }
            """);

        try
        {
            var registry = BowireProtocolRegistry.Discover();
            // The sidecar gets wrapped in a SidecarBowireProtocol that
            // exposes the manifest's protocol id. Verify the registry
            // actually saw it — that's the load-bearing assertion for
            // the foreach + Register branch we're trying to cover.
            var found = registry.Protocols.SingleOrDefault(
                p => string.Equals(p.Id, pluginId, StringComparison.Ordinal));
            Assert.NotNull(found);
            Assert.Equal("Edge Sidecar", found!.Name);
        }
        finally
        {
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Discover_SidecarDisabledViaList_NotRegistered()
    {
        // Companion to the test above: with the sidecar's protocol id in
        // the disabled list, SidecarPluginDiscovery skips it before the
        // Register branch — registry must NOT carry the protocol. Guards
        // the disabled-list semantics against accidental case-mismatch
        // regressions (the disable check is OrdinalIgnoreCase).
        var defaultRoot = SidecarPluginDiscovery.DefaultPluginRoot;
        Directory.CreateDirectory(defaultRoot);
        var pluginId = "bowire-sidecar-disabled-" + Guid.NewGuid().ToString("N");
        var pluginDir = SafePath.Combine(defaultRoot, pluginId);
        Directory.CreateDirectory(pluginDir);
        var manifestPath = SafePath.Combine(pluginDir, SidecarPluginManifest.FileName);
        File.WriteAllText(manifestPath,
            $$"""
            {
              "packageId": "{{pluginId}}",
              "protocol": { "id": "{{pluginId}}", "name": "Disabled Sidecar" },
              "executable": "fake-sidecar.sh"
            }
            """);

        try
        {
            // Pass the id in upper-case to additionally verify the
            // case-insensitive matching.
            var registry = BowireProtocolRegistry.Discover(
                disabledPluginIds: new[] { pluginId.ToUpperInvariant() });
            Assert.DoesNotContain(registry.Protocols,
                p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(pluginDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Discover_DisabledPluginIds_AreSkippedAndCountedAsDisabledOutcome()
    {
        // Mirrors the Discover_DisabledPluginIds_AreSkipped test in
        // BowireProtocolRegistryTests but adds verification that the
        // logger picks up the structured "Skipping disabled protocol
        // plugin" message — proves the optional logger hook is exercised
        // on the disabled-skip branch.
        var logger = new CapturingLogger();
        var registry = BowireProtocolRegistry.Discover(
            disabledPluginIds: s_disabledRestOnly,
            logger: logger);

        Assert.DoesNotContain(registry.Protocols, p => p.Id == "rest");
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains("Skipping disabled protocol plugin", StringComparison.Ordinal)
                && e.Message.Contains("rest", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_PassingPlainLoggerOnly_DoesNotThrow()
    {
        // Single-arg overload (logger only) routes through the two-arg
        // overload with disabledPluginIds: null — pin the contract.
        var logger = new CapturingLogger();
        var registry = BowireProtocolRegistry.Discover(logger);
        Assert.NotEmpty(registry.Protocols);
    }

    [Fact]
    public void Register_AcceptsCustomSubtypes_RetainedByReference()
    {
        // Sanity check that Register stores the exact reference (not a
        // wrapped/cloned copy) — load-bearing for the Find* lookups,
        // which rely on `is` pattern matching against the stored
        // reference's runtime type.
        var registry = new BowireProtocolRegistry();
        var custom = new EdgeStubProtocol("edge-stub", "Edge Stub");
        registry.Register(custom);

        Assert.Same(custom, registry.GetById("edge-stub"));
        // Iterating Protocols yields the same reference — important for
        // /api/plugins enumeration paths that do reference equality.
        Assert.Same(custom, registry.Protocols.Single(p => p.Id == "edge-stub"));
    }

    // ---- Helpers ----

    /// <summary>
    /// Minimal ILogger that captures every log call into an in-memory
    /// buffer. Used to assert that BowireProtocolRegistry routes its
    /// diagnostic messages through the logger hook rather than the
    /// process-global Console.Error.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class EdgeStubProtocol(string id, string name) : IBowireProtocol
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
    }
}
