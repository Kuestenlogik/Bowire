// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Registry of discovered protocol plugins. Scans loaded assemblies for IBowireProtocol implementations.
/// </summary>
public sealed class BowireProtocolRegistry
{
    private readonly List<IBowireProtocol> _protocols = [];

    public IReadOnlyList<IBowireProtocol> Protocols => _protocols;

    public void Register(IBowireProtocol protocol) => _protocols.Add(protocol);

    public IBowireProtocol? GetById(string id) => _protocols.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Returns the first registered protocol that also implements
    /// <see cref="IInlineHttpInvoker"/>, or null when no such plugin is loaded.
    /// Used by the /api/invoke endpoint's transcoded HTTP path so it can
    /// dispatch over HTTP without core having any HTTP-invocation code or
    /// dependencies of its own — the REST plugin owns it.
    /// </summary>
    public IInlineHttpInvoker? FindHttpInvoker()
    {
        foreach (var p in _protocols)
        {
            if (p is IInlineHttpInvoker invoker) return invoker;
        }
        return null;
    }

    /// <summary>
    /// Returns the first registered protocol that also implements
    /// <see cref="IInlineSseSubscriber"/>, or null when no such plugin is
    /// loaded. Used by plugins that want to consume an SSE stream without
    /// taking a hard compile-time dependency on the SSE plugin (the MCP
    /// plugin uses this for server-sent notifications, the GraphQL plugin
    /// uses it for graphql-sse subscriptions).
    /// </summary>
    public IInlineSseSubscriber? FindSseSubscriber()
    {
        foreach (var p in _protocols)
        {
            if (p is IInlineSseSubscriber sub) return sub;
        }
        return null;
    }

    /// <summary>
    /// Returns the first registered protocol that also implements
    /// <see cref="IInlineWebSocketChannel"/>, or null when no such plugin is
    /// loaded. Used by plugins that want to open a raw WebSocket channel
    /// (with optional sub-protocols and request headers) without taking a
    /// hard compile-time dependency on the WebSocket plugin. The GraphQL
    /// plugin uses this for graphql-transport-ws subscriptions.
    /// </summary>
    public IInlineWebSocketChannel? FindWebSocketChannel()
    {
        foreach (var p in _protocols)
        {
            if (p is IInlineWebSocketChannel ch) return ch;
        }
        return null;
    }

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
        ForceLoadReferencedBowireAssemblies(logger);

        var disabled = disabledPluginIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(disabledPluginIds, StringComparer.OrdinalIgnoreCase);

        var registry = new BowireProtocolRegistry();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName?.Contains("Bowire") != true) continue;
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!typeof(IBowireProtocol).IsAssignableFrom(type)) continue;
                    if (Activator.CreateInstance(type) is IBowireProtocol protocol)
                    {
                        if (disabled.Contains(protocol.Id))
                        {
#pragma warning disable CA1873 // logger is already null-checked
                            logger?.LogInformation(
                                "Skipping disabled protocol plugin '{PluginId}' (Bowire:DisabledPlugins).",
                                protocol.Id);
#pragma warning restore CA1873
                            continue;
                        }
                        registry.Register(protocol);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Skipped Bowire assembly during protocol scan: {Assembly}",
                    assembly.FullName);
            }
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
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "Couldn't determine entry assembly directory; plugin auto-load skipped");
            return;
        }
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return;

        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.FullName is { } name) loaded.Add(name.Split(',')[0].Trim());
        }

        foreach (var dll in Directory.EnumerateFiles(baseDir, "Kuestenlogik.Bowire*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(dll);
            if (loaded.Contains(simpleName)) continue;
            try
            {
                Assembly.LoadFrom(dll);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex,
                    "Failed to auto-load Bowire plugin assembly: {Dll}", dll);
            }
        }
    }
}
