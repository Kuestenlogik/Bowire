// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Kuestenlogik.Bowire.Plugins.Sidecar;
using Kuestenlogik.Bowire.Telemetry;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Registry of discovered protocol plugins. Scans loaded assemblies for IBowireProtocol implementations.
/// </summary>
public sealed class BowireProtocolRegistry
{
    // Discover() walks AppDomain.CurrentDomain.GetAssemblies() and
    // calls Assembly.LoadFrom on missing siblings — both side-effecting
    // operations the CLR safely interleaves between threads MOST of
    // the time. But the assembly-load chain triggered by GetTypes() +
    // Activator.CreateInstance can race when xUnit runs MockCommand
    // tests in parallel: one thread mid-LoadFrom while another
    // enumerates the same load-triggered side-collection produces
    // 'Collection was modified; enumeration operation may not execute.'
    // (CI run 28410978369, RunAsync_RecordingWithUnknownProtocol_…).
    //
    // Serialise the static discover entry point on a single gate.
    // Production callers hit Discover() once at startup so the lock
    // cost is irrelevant; the test suite is the loud one + benefits
    // most from determinism.
    private static readonly Lock _discoveryGate = new();

    private readonly List<IBowireProtocol> _protocols = [];

    public IReadOnlyList<IBowireProtocol> Protocols => _protocols;

    public void Register(IBowireProtocol protocol) => _protocols.Add(protocol);

    /// <summary>
    /// Remove the protocol with the given id from the live registry.
    /// Used by the <c>/api/plugins/{id}/lifecycle/unload</c> endpoint to
    /// hide a plugin without rebooting the host. Returns the removed
    /// instance (so the caller can dispose it when applicable), or
    /// <c>null</c> when no such id was registered.
    /// </summary>
    public IBowireProtocol? Unregister(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var existing = _protocols.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;
        _protocols.Remove(existing);
        return existing;
    }

    /// <summary>
    /// Replace an existing protocol instance with a fresh one. Used by
    /// the <c>/api/plugins/{id}/lifecycle/restart</c> endpoint to swap a
    /// stateful plugin for a re-constructed instance without changing
    /// its registry position. Falls back to <see cref="Register"/> when
    /// no instance is currently registered for the id.
    /// </summary>
    public void Replace(IBowireProtocol fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        for (var i = 0; i < _protocols.Count; i++)
        {
            if (string.Equals(_protocols[i].Id, fresh.Id, StringComparison.OrdinalIgnoreCase))
            {
                _protocols[i] = fresh;
                return;
            }
        }
        _protocols.Add(fresh);
    }

    public IBowireProtocol? GetById(string id) => _protocols.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Returns the first registered protocol that also implements
    /// <see cref="IInlineHttpInvoker"/>, or null when no such plugin is loaded.
    /// Used by the /api/invoke endpoint's transcoded HTTP path so it can
    /// dispatch over HTTP without core having any HTTP-invocation code or
    /// dependencies of its own — the REST plugin owns it.
    /// </summary>
    public IInlineHttpInvoker? FindHttpInvoker()
        => _protocols.OfType<IInlineHttpInvoker>().FirstOrDefault();

    /// <summary>
    /// Returns the first registered protocol that also implements
    /// <see cref="IInlineSseSubscriber"/>, or null when no such plugin is
    /// loaded. Used by plugins that want to consume an SSE stream without
    /// taking a hard compile-time dependency on the SSE plugin (the MCP
    /// plugin uses this for server-sent notifications, the GraphQL plugin
    /// uses it for graphql-sse subscriptions).
    /// </summary>
    public IInlineSseSubscriber? FindSseSubscriber()
        => _protocols.OfType<IInlineSseSubscriber>().FirstOrDefault();

    /// <summary>
    /// Returns the first registered protocol that also implements
    /// <see cref="IInlineWebSocketChannel"/>, or null when no such plugin is
    /// loaded. Used by plugins that want to open a raw WebSocket channel
    /// (with optional sub-protocols and request headers) without taking a
    /// hard compile-time dependency on the WebSocket plugin. The GraphQL
    /// plugin uses this for graphql-transport-ws subscriptions.
    /// </summary>
    public IInlineWebSocketChannel? FindWebSocketChannel()
        => _protocols.OfType<IInlineWebSocketChannel>().FirstOrDefault();

    /// <summary>
    /// Auto-discover protocol plugins from loaded assemblies. Pass an
    /// <see cref="ILogger"/> to surface load failures (a buggy plugin
    /// DLL or a missing dependency would otherwise vanish silently into
    /// the catch block, leaving the user wondering why their plugin
    /// doesn't show up).
    /// </summary>
    public static BowireProtocolRegistry Discover(ILogger? logger = null)
        => Discover(disabledPluginIds: null, logger: logger);

    /// <summary>
    /// Same as <see cref="Discover(ILogger?)"/> but also accepts a list
    /// of plugin ids to skip during the scan. Used for the
    /// <c>--disable-plugin</c> CLI flag and the
    /// <c>Bowire:DisabledPlugins</c> appsettings option — handy when a
    /// plugin's load path is broken or its discovery probe is too
    /// expensive for the current host's network.
    /// </summary>
    /// <param name="disabledPluginIds">
    /// Plugin ids to exclude. Matched case-insensitively against
    /// <see cref="IBowireProtocol.Id"/>. <c>null</c> or empty means
    /// "scan everything", same as the parameterless overload.
    /// </param>
    /// <param name="logger">Optional warning logger.</param>
    public static BowireProtocolRegistry Discover(
        IEnumerable<string>? disabledPluginIds,
        ILogger? logger = null)
    {
        // See _discoveryGate comment for the parallel-xUnit race this
        // serialises. Holding the lock across the WHOLE method also
        // covers Activator.CreateInstance side-effects on shared
        // plugin static ctors.
        lock (_discoveryGate)
        {
            return DiscoverLocked(disabledPluginIds, logger);
        }
    }

    private static BowireProtocolRegistry DiscoverLocked(
        IEnumerable<string>? disabledPluginIds,
        ILogger? logger)
    {
        ForceLoadReferencedBowireAssemblies(logger);

        var disabled = disabledPluginIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(disabledPluginIds, StringComparer.OrdinalIgnoreCase);

        var registry = new BowireProtocolRegistry();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Bowire") == true))
        {
            try
            {
                foreach (var type in assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IBowireProtocol).IsAssignableFrom(t)))
                {
                    if (Activator.CreateInstance(type) is IBowireProtocol protocol)
                    {
                        if (disabled.Contains(protocol.Id))
                        {
                            if (logger is not null)
                                ProtocolRegistryLog.SkippingDisabledPlugin(logger, protocol.Id);
                            // #29 -- still record the load attempt so the
                            // 'skipped because disabled' branch shows up
                            // in the Grafana panel separately from
                            // 'failed because instance construction
                            // threw'.
                            BowireTelemetry.PluginLoad.Add(1, new TagList
                            {
                                { "plugin", protocol.Id },
                                { "outcome", "disabled" },
                            });
                            continue;
                        }
                        registry.Register(protocol);
                        BowireTelemetry.PluginLoad.Add(1, new TagList
                        {
                            { "plugin", protocol.Id },
                            { "outcome", "loaded" },
                        });
                    }
                }
            }
            // Plugin discovery has to tolerate anything a 3rd-party DLL
            // throws from its static ctor or default ctor:
            // ReflectionTypeLoadException, TypeInitializationException,
            // MissingMethodException, BadImageFormatException,
            // FileLoadException, plus whatever Activator wraps. A single
            // bad plugin must not abort scanning the rest.
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger?.LogWarning(ex,
                    "Skipped Bowire assembly during protocol scan: {Assembly}",
                    assembly.FullName);
            }
        }

        // Sidecar plugins — any-language executables in
        // ~/.bowire/plugins/<id>/ marked by a plugin.json manifest.
        // Registered alongside the .NET plugins; same disable list
        // applies on the manifest's protocol.id.
        foreach (var sidecar in SidecarPluginDiscovery.Discover(
            pluginRoot: null,
            disabledPluginIds: disabled,
            logger: logger))
        {
            registry.Register(sidecar);
        }

        return registry;
    }

    /// <summary>
    /// .NET only loads referenced assemblies on first type touch. The C#
    /// compiler strips unused references from the metadata table altogether,
    /// so walking <c>GetReferencedAssemblies()</c> isn't enough — only the
    /// references actually touched in code show up there.
    ///
    /// To find every Bowire plugin that's been deployed alongside the host,
    /// scan the entry assembly's directory for <c>Kuestenlogik.Bowire*.dll</c> and
    /// force-load any that aren't already in the AppDomain. This works for
    /// both the standalone tool and embedded mode where plugins live next to
    /// the host's binary.
    /// </summary>
    private static void ForceLoadReferencedBowireAssemblies(ILogger? logger)
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry is null) return;

        string? baseDir;
        try { baseDir = Path.GetDirectoryName(entry.Location); }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or PathTooLongException)
        {
            // Single-file-published or bundled assemblies have an empty
            // Location, or Path.GetDirectoryName rejects the format —
            // skip the auto-load step in those cases.
            logger?.LogDebug(ex,
                "Couldn't determine entry assembly directory; plugin auto-load skipped");
            return;
        }
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;

        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.FullName)
            .Where(n => n is not null))
        {
            loaded.Add(name!.Split(',')[0].Trim());
        }

        foreach (var dll in Directory.EnumerateFiles(baseDir, "Kuestenlogik.Bowire*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(dll);
            if (loaded.Contains(simpleName)) continue;
            try
            {
                Assembly.LoadFrom(dll);
            }
            catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                // Common load failures for a sidecar DLL: corrupt assembly,
                // missing transitive ref, locked file, ACL denial. Keep
                // going so one bad DLL doesn't break the rest of the scan.
                logger?.LogWarning(ex,
                    "Failed to auto-load Bowire plugin assembly: {Dll}", dll);
            }
        }
    }
}

/// <summary>
/// Source-generated logger wrappers for
/// <see cref="BowireProtocolRegistry"/>. Spinning these out of the
/// inline calls keeps CA1873 happy (the generator emits the
/// <c>IsEnabled</c>-gated dispatch the analyzer wants and avoids
/// boxing of the <c>{PluginId}</c> tag when the level isn't enabled).
/// </summary>
internal static partial class ProtocolRegistryLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Skipping disabled protocol plugin '{PluginId}' (Bowire:DisabledPlugins).")]
    public static partial void SkippingDisabledPlugin(ILogger logger, string pluginId);
}
