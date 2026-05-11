// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Discovery + cache for <see cref="IBowireUiExtension"/> instances.
/// Mirrors <c>BowireProtocolRegistry</c>: a single static
/// <see cref="Discover()"/> sweeps every loaded
/// <c>Kuestenlogik.Bowire*</c> assembly, instantiates the types tagged
/// with <see cref="BowireExtensionAttribute"/> that implement
/// <see cref="IBowireUiExtension"/>, and exposes them as a flat list.
/// </summary>
/// <remarks>
/// <para>
/// The discovery sweep is intentionally permissive — types whose
/// constructor throws, or assemblies that fail to enumerate, are simply
/// skipped so a single broken extension never takes the whole workbench
/// down. Plugin-load failures show up as a missing extension in the
/// installed list, matching the existing protocol-plugin failure mode.
/// </para>
/// <para>
/// Server-side detectors register through a sibling contract (a future
/// <c>IBowireFieldDetector</c>); the discovery loop here only handles
/// UI extensions because that is what Phase 3 ships. Adding detector
/// discovery later is one extra branch in <see cref="Discover()"/>.
/// </para>
/// </remarks>
public sealed class BowireExtensionRegistry
{
    private readonly List<IBowireUiExtension> _uiExtensions = [];
    private readonly Dictionary<string, Assembly> _declaringAssemblies = new(StringComparer.Ordinal);

    /// <summary>All discovered UI extensions, registration order preserved.</summary>
    public IReadOnlyList<IBowireUiExtension> UiExtensions => _uiExtensions;

    /// <summary>
    /// Resolve the <see cref="Assembly"/> that contributed the extension
    /// with the given <paramref name="extensionId"/>, or <c>null</c> when
    /// no such extension is registered. Used by the asset-serving
    /// endpoint to load the embedded bundle out of the right assembly.
    /// </summary>
    public Assembly? GetDeclaringAssembly(string extensionId)
    {
        ArgumentNullException.ThrowIfNull(extensionId);
        return _declaringAssemblies.GetValueOrDefault(extensionId);
    }

    /// <summary>
    /// Lookup by id. Returns <c>null</c> when the id is unknown — the
    /// asset-serving endpoint maps that to <c>404 Not Found</c>.
    /// </summary>
    public IBowireUiExtension? GetUiExtension(string extensionId)
    {
        ArgumentNullException.ThrowIfNull(extensionId);
        foreach (var ext in _uiExtensions)
        {
            if (string.Equals(ext.Id, extensionId, StringComparison.Ordinal))
            {
                return ext;
            }
        }
        return null;
    }

    /// <summary>
    /// Scan every loaded <c>Kuestenlogik.Bowire*</c> assembly for
    /// extensions and return a fully-built registry. Idempotent — call
    /// twice and you get two independent registry instances, each
    /// pointing at fresh instances of the discovered descriptor types.
    /// </summary>
    public static BowireExtensionRegistry Discover()
    {
        var registry = new BowireExtensionRegistry();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName?.Contains("Bowire", StringComparison.Ordinal) != true) continue;

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                // Some types may fail to load (missing optional dep);
                // keep the partial list and skip nulls.
                types = [.. ex.Types.Where(t => t is not null).Cast<Type>()];
            }
            catch (Exception)
            {
                // Anything else — skip the whole assembly silently.
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetCustomAttribute<BowireExtensionAttribute>() is null) continue;

                if (typeof(IBowireUiExtension).IsAssignableFrom(type))
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IBowireUiExtension ext)
                        {
                            registry._uiExtensions.Add(ext);
                            registry._declaringAssemblies[ext.Id] = type.Assembly;
                        }
                    }
                    catch (Exception)
                    {
                        // Skip extensions whose constructor throws —
                        // matches BowireProtocolRegistry's behaviour for
                        // plugins that fail to instantiate.
                    }
                }
            }
        }

        return registry;
    }
}
