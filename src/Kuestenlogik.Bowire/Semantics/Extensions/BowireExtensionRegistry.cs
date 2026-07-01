// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Discovery + cache for <see cref="IBowireUiExtension"/> and
/// <see cref="IBowireFieldDetector"/> instances. Mirrors
/// <c>BowireProtocolRegistry</c>: a single static <see cref="Discover()"/>
/// sweeps every loaded <c>Kuestenlogik.Bowire*</c> assembly, instantiates
/// the types tagged with <see cref="BowireExtensionAttribute"/> that
/// implement one of the supported extension interfaces, and exposes them
/// as flat lists.
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
/// Both UI extensions and server-side detectors are picked up in the
/// same sweep — a single package can ship a
/// <c>[BowireExtension]</c>-marked detector alongside a
/// <c>[BowireExtension]</c>-marked viewer. Detector discovery is
/// additive to the manual <c>AddSingleton&lt;IBowireFieldDetector, ...&gt;()</c>
/// path: hosts that hand-register a detector continue to work, and the
/// DI wiring in
/// <see cref="BowireServiceCollectionExtensions.AddBowire(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{BowireOptions})"/>
/// suppresses duplicates by concrete type so a built-in that is both
/// marker-tagged AND explicitly registered does not fire twice.
/// </para>
/// </remarks>
public sealed class BowireExtensionRegistry
{
    private readonly List<IBowireUiExtension> _uiExtensions = [];
    private readonly List<IBowireFieldDetector> _fieldDetectors = [];
    private readonly Dictionary<string, Assembly> _declaringAssemblies = new(StringComparer.Ordinal);

    /// <summary>All discovered UI extensions, registration order preserved.</summary>
    public IReadOnlyList<IBowireUiExtension> UiExtensions => _uiExtensions;

    /// <summary>
    /// All discovered field detectors, registration order preserved.
    /// The DI wiring in
    /// <see cref="BowireServiceCollectionExtensions.AddBowire(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{BowireOptions})"/>
    /// reads this list and registers each entry as an
    /// <see cref="IBowireFieldDetector"/> singleton, additively with any
    /// hand-registered detectors already in the container.
    /// </summary>
    public IReadOnlyList<IBowireFieldDetector> FieldDetectors => _fieldDetectors;

    /// <summary>
    /// Resolve the <see cref="Assembly"/> that contributed the extension
    /// (UI extension or field detector) with the given
    /// <paramref name="extensionId"/>, or <c>null</c> when no such
    /// extension is registered. Used by the asset-serving endpoint to
    /// load the embedded bundle out of the right assembly.
    /// </summary>
    public Assembly? GetDeclaringAssembly(string extensionId)
    {
        ArgumentNullException.ThrowIfNull(extensionId);
        return _declaringAssemblies.GetValueOrDefault(extensionId);
    }

    /// <summary>
    /// Lookup a UI extension by id. Returns <c>null</c> when the id is
    /// unknown — the asset-serving endpoint maps that to
    /// <c>404 Not Found</c>.
    /// </summary>
    public IBowireUiExtension? GetUiExtension(string extensionId)
    {
        ArgumentNullException.ThrowIfNull(extensionId);
        return _uiExtensions.FirstOrDefault(ext => string.Equals(ext.Id, extensionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Lookup a field detector by id. Returns <c>null</c> when the id
    /// is unknown.
    /// </summary>
    public IBowireFieldDetector? GetFieldDetector(string detectorId)
    {
        ArgumentNullException.ThrowIfNull(detectorId);
        return _fieldDetectors.FirstOrDefault(det => string.Equals(det.Id, detectorId, StringComparison.Ordinal));
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

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Bowire", StringComparison.Ordinal) == true))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                // Some types may fail to load (missing optional dep);
                // keep the partial list and skip nulls.
                types = [.. ex.Types.Where(t => t is not null).Cast<Type>()];
            }
            catch (Exception ex) when (ex is TypeLoadException or FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                // Anything else — skip the whole assembly silently.
                _ = ex;
                continue;
            }

            foreach (var type in types
                .Where(t => !t.IsAbstract && !t.IsInterface && t.GetCustomAttribute<BowireExtensionAttribute>() is not null))
            {
                if (typeof(IBowireUiExtension).IsAssignableFrom(type))
                {
                    TryInstantiate(type, instance =>
                    {
                        var ext = (IBowireUiExtension)instance;
                        registry._uiExtensions.Add(ext);
                        registry._declaringAssemblies[ext.Id] = type.Assembly;
                    });
                }

                if (typeof(IBowireFieldDetector).IsAssignableFrom(type))
                {
                    TryInstantiate(type, instance =>
                    {
                        var det = (IBowireFieldDetector)instance;
                        registry._fieldDetectors.Add(det);
                        // Only claim the id slot when a UI extension
                        // hasn't already registered a bundle-carrying
                        // assembly under it (detectors don't own an
                        // asset-serving surface).
                        if (!registry._declaringAssemblies.ContainsKey(det.Id))
                        {
                            registry._declaringAssemblies[det.Id] = type.Assembly;
                        }
                    });
                }
            }
        }

        return registry;
    }

    /// <summary>
    /// Activator wrapper that swallows any exception a 3rd-party
    /// extension's parameterless constructor throws. Matches the
    /// <c>BowireProtocolRegistry</c> failure mode: one bad extension
    /// must not block the rest.
    /// </summary>
    private static void TryInstantiate(Type type, Action<object> onInstance)
    {
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            var instance = Activator.CreateInstance(type);
            if (instance is not null) onInstance(instance);
        }
        catch (Exception ex)
        {
            // Skip extensions whose constructor throws — matches
            // BowireProtocolRegistry's behaviour for plugins that fail
            // to instantiate.
            _ = ex;
        }
#pragma warning restore CA1031
    }
}
