// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Net;
using Kuestenlogik.Bowire.PluginLoading;
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
    {
        // Force-load all Kuestenlogik.Bowire*.dll assemblies from the output directory
        // so assembly scanning finds protocol plugins that haven't been touched
        // by the CLR yet (same logic as BowireProtocolRegistry.Discover).
        ForceLoadBowireAssemblies();

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
    /// the subsequent <see cref="AddBowire"/> reflection pass picks the
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
    /// Call this <i>before</i> <see cref="AddBowire"/>.
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
