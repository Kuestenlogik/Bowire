// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Net;
using Kuestenlogik.Bowire.PluginLoading;
using Kuestenlogik.Bowire.Semantics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire;

/// <summary>
/// DI-container extensions that wire Bowire and its installed protocol
/// plugins into an ASP.NET application.
/// </summary>
/// <remarks>
/// Paired with
/// <see cref="BowireEndpointRouteBuilderExtensions.MapBowire(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, System.Action{BowireOptions})"/>:
/// call <c>AddBowire()</c> in <c>Program.cs</c> before <c>builder.Build()</c>,
/// then <c>MapBowire()</c> on the resulting app.
/// </remarks>
public static class BowireServiceCollectionExtensions
{
    /// <summary>
    /// Registers Bowire and auto-configures the DI prerequisites for every
    /// installed protocol plugin.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so calls can be chained
    /// (for example with <c>.AddBowire().AddAuthentication(…)</c>).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Scans every loaded assembly whose name starts with <c>Kuestenlogik.Bowire</c>
    /// for types that implement <see cref="IBowireProtocolServices"/> and
    /// invokes <see cref="IBowireProtocolServices.ConfigureServices"/> on
    /// each. Only protocols whose NuGet package is actually referenced by
    /// the host project are activated — no gRPC reference means no gRPC
    /// Server Reflection is registered, no SignalR reference means no hub
    /// enumeration, and so on.
    /// </para>
    /// <para>
    /// A preliminary pass force-loads every <c>Kuestenlogik.Bowire*.dll</c> from the
    /// application's output directory so that plugins shipped as runtime
    /// dependencies (but not touched by the CLR yet) are discovered too.
    /// Assemblies that fail to enumerate types are silently skipped to keep
    /// startup resilient in misconfigured deployments.
    /// </para>
    /// </remarks>
    /// <example>
    /// Without <c>AddBowire</c>, protocols must be configured by hand:
    /// <code>
    /// builder.Services.AddGrpc();
    /// builder.Services.AddGrpcReflection();
    /// // ... and every other protocol's own setup ...
    /// var app = builder.Build();
    /// app.MapGrpcReflectionService();
    /// app.MapBowire();
    /// </code>
    ///
    /// With <c>AddBowire</c>, a single line covers every referenced plugin:
    /// <code>
    /// builder.Services.AddBowire();
    /// var app = builder.Build();
    /// app.MapBowire();
    /// </code>
    /// </example>
    /// <seealso cref="IBowireProtocolServices"/>
    /// <seealso cref="BowireEndpointRouteBuilderExtensions.MapBowire(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, System.Action{BowireOptions})"/>
    public static IServiceCollection AddBowire(this IServiceCollection services)
        => AddBowire(services, configure: null);

    /// <summary>
    /// Overload that exposes a configuration callback for the subset of
    /// <see cref="BowireOptions"/> that needs to be settled at
    /// <c>AddServices</c> time rather than at
    /// <see cref="BowireEndpointRouteBuilderExtensions.MapBowire(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, System.Action{BowireOptions})"/>
    /// time. Today the only such option is
    /// <see cref="BowireOptions.SchemaHintsPath"/> — the user-local
    /// schema-hints file path that the
    /// <see cref="LayeredAnnotationStore"/> singleton needs at
    /// construction. Everything else still flows through the regular
    /// <c>MapBowire</c> callback.
    /// </summary>
    public static IServiceCollection AddBowire(
        this IServiceCollection services,
        Action<BowireOptions>? configure)
    {
        // Force-load all Kuestenlogik.Bowire*.dll assemblies from the output directory
        // so assembly scanning finds protocol plugins that haven't been touched
        // by the CLR yet (same logic as BowireProtocolRegistry.Discover).
        ForceLoadBowireAssemblies();

        // Materialise the bootstrap options. MapBowire builds its own
        // BowireOptions later — that's the one bound to the workbench
        // UI surface. The one here only carries the AddServices-time
        // settings (SchemaHintsPath today), so the two never drift.
        var bootstrapOptions = new BowireOptions();
        configure?.Invoke(bootstrapOptions);

        RegisterSemanticsStore(services, bootstrapOptions);

        // Named HttpClient for the OAuth proxy endpoints in
        // BowireAuthEndpoints. IHttpClientFactory pools the underlying
        // HttpMessageHandler (avoids socket-exhaustion under churn) and
        // gives tests a clean DI seam — ConfigurePrimaryHttpMessageHandler
        // can swap in a mock handler without touching the endpoint code.
        // The primary handler is built through BowireHttpClientFactory so
        // the same Bowire:TrustLocalhostCert / Bowire:oauth:TrustLocalhostCert
        // opt-in that the protocol plugins honour also covers OAuth-proxy
        // calls against a local IdP with a self-signed cert.
        services.AddHttpClient("bowire-oauth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(sp =>
            BowireHttpClientFactory.CreateHandler(
                sp.GetService<IConfiguration>(), "oauth"));

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.FullName?.Contains("Bowire") != true) continue;
            try
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!typeof(IBowireProtocolServices).IsAssignableFrom(type)) continue;
                    if (Activator.CreateInstance(type) is IBowireProtocolServices setup)
                    {
                        setup.ConfigureServices(services);
                    }
                }
            }
            catch
            {
                // Skip assemblies that fail to enumerate types (e.g. missing deps).
                // BowireProtocolRegistry.Discover logs these; we stay silent here
                // to avoid requiring a logger at the AddServices stage.
            }
        }

        return services;
    }

    /// <summary>
    /// Load every <c>.dll</c> under <paramref name="pluginDir"/> into the
    /// default <see cref="System.Runtime.Loader.AssemblyLoadContext"/> so
    /// the subsequent <see cref="AddBowire(IServiceCollection)"/> reflection pass picks the
    /// plugins up. Intended for embedded hosts that want to extend the
    /// workbench with out-of-tree protocol plugins without depending on
    /// the <c>bowire</c> CLI tool.
    /// </summary>
    /// <param name="services">The application's service collection (returned unchanged, for chaining).</param>
    /// <param name="pluginDir">
    /// Directory to scan. Per-package subdirectories
    /// (<c>pluginDir/&lt;package&gt;/*.dll</c>) are the primary layout —
    /// matches what <c>bowire plugin install</c> produces — but loose
    /// DLLs at the top level are picked up too.
    /// </param>
    /// <remarks>
    /// Non-existent paths are treated as empty (no throw) so callers can
    /// blindly pass a configured directory even when no plugins have been
    /// installed yet. DLLs that fail to load are silently skipped — the
    /// equivalent behaviour to the <c>bowire</c> CLI's plugin loader.
    /// Call this <i>before</i> <see cref="AddBowire(IServiceCollection)"/>.
    /// </remarks>
    public static IServiceCollection AddBowirePlugins(
        this IServiceCollection services, string pluginDir)
    {
        ArgumentNullException.ThrowIfNull(pluginDir);
        if (string.IsNullOrWhiteSpace(pluginDir)) return services;

        var absolute = Path.GetFullPath(pluginDir);
        if (!Directory.Exists(absolute)) return services;

        // One BowirePluginLoadContext per package subdirectory so
        // plugins with conflicting private deps don't clobber each
        // other. Shared prefixes (Kuestenlogik.Bowire*, System.*, Microsoft.*)
        // delegate to the default ALC — type identity of contract
        // interfaces stays intact across the boundary.
        //
        // The host is registered as a singleton so embedded hosts can
        // resolve it later and hot-reload plugins via
        // BowirePluginHost.Reload(...) after a disk-side update.
        var host = services.GetOrAddPluginHost();

        foreach (var sub in Directory.EnumerateDirectories(absolute))
        {
            TryLoadPlugin(host, sub);
        }

        // Loose DLLs directly at the plugin-root level get a context
        // named for the root dir. In practice this branch rarely fires
        // — `bowire plugin install` always writes into subdirs — but
        // embedded hosts that drop a single plugin into the root still
        // work.
        if (Directory.EnumerateFiles(absolute, "*.dll").Any())
        {
            TryLoadPlugin(host, absolute);
        }

        return services;
    }

    private static void TryLoadPlugin(BowirePluginHost host, string pluginDir)
    {
        try { host.Load(pluginDir); }
        catch { /* skip directories that fail to load */ }
    }

    private static BowirePluginHost GetOrAddPluginHost(this IServiceCollection services)
    {
        // Manual singleton bookkeeping because we need the instance
        // right now (to load plugins) — deferring to the DI provider
        // would require Build() which we can't do mid-ConfigureServices.
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(BowirePluginHost));
        if (descriptor?.ImplementationInstance is BowirePluginHost existing) return existing;

        var host = new BowirePluginHost();
        services.AddSingleton(host);
        return host;
    }

    /// <summary>
    /// Overload that reads the plugin directory from a bound
    /// <see cref="IConfiguration"/>. Looks at <c>Bowire:PluginDir</c>;
    /// returns unchanged when the key is unset so callers can wire this
    /// into their startup pipeline unconditionally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the preferred wiring for embedded hosts that already
    /// build an <see cref="IConfiguration"/> from <c>appsettings.json</c>
    /// or similar:
    /// </para>
    /// <code>
    /// builder.Services
    ///        .AddBowirePlugins(builder.Configuration)
    ///        .AddBowire();
    /// </code>
    /// <para>
    /// Plugins themselves read their own config section via the standard
    /// .NET options pattern:
    /// </para>
    /// <code>
    /// public void ConfigureServices(IServiceCollection services)
    /// {
    ///     services.AddOptions&lt;MyPluginOptions&gt;()
    ///             .BindConfiguration("Bowire:Plugins:MyPlugin");
    /// }
    /// </code>
    /// </remarks>
    public static IServiceCollection AddBowirePlugins(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var dir = configuration["Bowire:PluginDir"];
        return string.IsNullOrWhiteSpace(dir) ? services : services.AddBowirePlugins(dir);
    }

    /// <summary>
    /// Wire the <see cref="LayeredAnnotationStore"/> singleton plus
    /// supporting layers for the frame-semantics framework. The store
    /// is constructed lazily from a factory so the
    /// <see cref="BowireProtocolRegistry"/> doesn't have to exist at
    /// AddServices time — embedded hosts typically build it later,
    /// inside the request pipeline.
    /// </summary>
    private static void RegisterSemanticsStore(
        IServiceCollection services, BowireOptions bootstrapOptions)
    {
        // Resolve user / project file paths up-front. The empty-string
        // sentinel on SchemaHintsPath disables the user-local file
        // entirely — for hardened deployments that don't want any
        // disk side-effect from Bowire.
        var userFilePath = bootstrapOptions.SchemaHintsPath ?? DefaultUserSchemaHintsPath();
        var projectFilePath = DefaultProjectSchemaHintsPath();

        services.AddSingleton<LayeredAnnotationStore>(sp =>
        {
            // Empty user-file path is the opt-out: no user-local file
            // layer at all. Otherwise build the layer eagerly and let
            // its first Load happen lazily on first access.
            JsonFileAnnotationLayer? userLayer = string.IsNullOrEmpty(userFilePath)
                ? null
                : new JsonFileAnnotationLayer(userFilePath);

            JsonFileAnnotationLayer? projectLayer
                = projectFilePath is not null && File.Exists(projectFilePath)
                    ? new JsonFileAnnotationLayer(projectFilePath)
                    : null;

            // Plugin hints are pulled lazily from the registered
            // BowireProtocolRegistry (when one exists). The store
            // calls back through this lambda per (service, method)
            // query — caching is the registry's responsibility.
            IEnumerable<Annotation> PluginHints(string serviceId, string methodId)
            {
                var registry = sp.GetService<BowireProtocolRegistry>();
                if (registry is null) yield break;
                foreach (var protocol in registry.Protocols)
                {
                    if (protocol is not IBowireSchemaHints hints) continue;
                    foreach (var annotation in hints.GetSchemaHints(serviceId, methodId))
                    {
                        if (annotation is null) continue;
                        yield return annotation;
                    }
                }
            }

            return new LayeredAnnotationStore(
                userSessionLayer: new InMemoryAnnotationLayer(),
                userFileLayer: userLayer,
                projectFileLayer: projectLayer,
                autoDetectorLayer: new InMemoryAnnotationLayer(),
                pluginHints: PluginHints);
        });

        // Expose the read interface so consumers that only need to
        // resolve effective tags can take an IAnnotationStore without
        // depending on the layer concrete types.
        services.AddSingleton<IAnnotationStore>(sp => sp.GetRequiredService<LayeredAnnotationStore>());
    }

    /// <summary>
    /// Canonical default for the user-local schema-hints file:
    /// <c>~/.bowire/schema-hints.json</c>. Returns the empty string
    /// when the user-profile directory can't be resolved, signalling
    /// "no user-local layer" — Bowire degrades to session-only +
    /// project-file semantics rather than crashing.
    /// </summary>
    internal static string DefaultUserSchemaHintsPath()
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.None);
        if (string.IsNullOrEmpty(home)) return string.Empty;
        return Path.Combine(home, ".bowire", "schema-hints.json");
    }

    /// <summary>
    /// Canonical default for the project-local schema-hints file:
    /// <c>bowire.schema-hints.json</c> in the current working directory.
    /// Returns the path unconditionally (existence-check lives in the
    /// store factory), or <c>null</c> when the CWD can't be resolved.
    /// </summary>
    internal static string? DefaultProjectSchemaHintsPath()
    {
        try
        {
            return Path.Combine(Environment.CurrentDirectory, "bowire.schema-hints.json");
        }
        catch (Exception)
        {
            // Environment.CurrentDirectory can throw on platforms where
            // the CWD has been deleted out from under the process.
            // Treat that as "no project file."
            return null;
        }
    }

    private static void ForceLoadBowireAssemblies()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry is null) return;

        string? baseDir;
        try { baseDir = Path.GetDirectoryName(entry.Location); }
        catch { return; }
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
            try { Assembly.LoadFrom(dll); } catch { /* skip */ }
        }
    }
}
