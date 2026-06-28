// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Sources;

/// <summary>
/// Wire shape for a UI-driven catalogue-provider override (#309).
/// Sent by the Settings → Catalogue providers UI to
/// <c>POST /api/catalogue/config</c>. Persists to
/// <c>~/.bowire/catalogue-config.json</c> so the override survives a
/// process restart; absent file ⇒ <c>appsettings.json</c> fallback.
/// </summary>
/// <remarks>
/// The override is process-wide. Per-process at-most-one-provider is
/// the architectural intent of <see cref="IBowireCatalogueProvider"/>
/// — UI persistence is per-workspace on the client (so an operator
/// who switches workspaces re-applies the right config) but the
/// applied state on the host is shared.
/// </remarks>
public sealed class BowireCatalogueOverride
{
    /// <summary>Provider id (e.g. <c>"local"</c>, <c>"http"</c>, <c>"consul"</c>).
    /// Null / empty clears the override and falls back to appsettings.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>Inline options snapshot for the selected provider —
    /// only the fields relevant to <see cref="Provider"/> are read.</summary>
    [JsonPropertyName("local")]
    public BowireLocalCatalogueOptions? Local { get; set; }

    /// <summary>HTTP-provider options (URL + Authorization header).</summary>
    [JsonPropertyName("http")]
    public BowireHttpCatalogueOptions? Http { get; set; }

    /// <summary>Consul-provider options (address + token + DC + tag).</summary>
    [JsonPropertyName("consul")]
    public BowireConsulCatalogueOptions? Consul { get; set; }

    /// <summary>
    /// Kubernetes-provider options. Shape mirrors
    /// <c>BowireKubernetesCatalogueOptions</c> in the
    /// <c>Kuestenlogik.Bowire.Catalogue.Kubernetes</c> package, but the
    /// override surface keeps a private DTO so the core package
    /// doesn't take a hard reference on the sibling assembly — values
    /// are forwarded to the provider via reflection at apply time.
    /// </summary>
    [JsonPropertyName("kubernetes")]
    public BowireKubernetesCatalogueOverrideOptions? Kubernetes { get; set; }

    /// <summary>
    /// Agent-hub provider options. Shape mirrors
    /// <c>BowireAgentCatalogueOptions</c> in the
    /// <c>Kuestenlogik.Bowire.Catalogue.Agent</c> package; see
    /// <see cref="Kubernetes"/> for the cross-assembly seam reasoning.
    /// </summary>
    [JsonPropertyName("agent")]
    public BowireAgentCatalogueOverrideOptions? Agent { get; set; }
}

/// <summary>
/// Core-side mirror of <c>BowireKubernetesCatalogueOptions</c> — the
/// Settings UI sends this shape; the override store forwards values
/// to the actual provider via reflection.
/// </summary>
public sealed class BowireKubernetesCatalogueOverrideOptions
{
    [JsonPropertyName("apiServerUrl")] public string? ApiServerUrl { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("kubeconfigPath")] public string? KubeconfigPath { get; set; }
    [JsonPropertyName("namespace")] public string? Namespace { get; set; }
    [JsonPropertyName("labelSelector")] public string? LabelSelector { get; set; }
    [JsonPropertyName("scheme")] public string? Scheme { get; set; }
    [JsonPropertyName("caCertificatePem")] public string? CaCertificatePem { get; set; }
    [JsonPropertyName("skipTlsVerification")] public bool SkipTlsVerification { get; set; }
}

/// <summary>
/// Core-side mirror of <c>BowireAgentCatalogueOptions</c>.
/// </summary>
public sealed class BowireAgentCatalogueOverrideOptions
{
    [JsonPropertyName("hubUrl")] public string? HubUrl { get; set; }
    [JsonPropertyName("bootstrapToken")] public string? BootstrapToken { get; set; }
    [JsonPropertyName("stubResponse")] public string? StubResponse { get; set; }
}

/// <summary>
/// File-backed override store. Reads / writes
/// <c>~/.bowire/catalogue-config.json</c> and hydrates the
/// <see cref="BowireCatalogueProviderAccessor"/> on first construction.
/// </summary>
public sealed class BowireCatalogueOverrideStore
{
    private readonly BowireCatalogueProviderAccessor _accessor;
    private readonly ILogger? _logger;
    private readonly object _writeLock = new();
    private BowireCatalogueOverride? _current;

    /// <summary>Construct the store + re-hydrate any persisted
    /// override.</summary>
    public BowireCatalogueOverrideStore(
        BowireCatalogueProviderAccessor accessor,
        ILoggerFactory? loggerFactory = null)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _logger = loggerFactory?.CreateLogger("Kuestenlogik.Bowire.Catalogue.Override");
        _current = Load();
        if (_current is not null) ApplyOverride(_current);
    }

    /// <summary>Snapshot of the persisted override (or <c>null</c>
    /// when none).</summary>
    public BowireCatalogueOverride? Current
    {
        get { lock (_writeLock) return _current; }
    }

    /// <summary>Persist + apply a new override.</summary>
    public void Save(BowireCatalogueOverride payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_writeLock)
        {
            _current = payload;
            try { File.WriteAllText(ResolvePath(), JsonSerializer.Serialize(payload, JsonOptions)); }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_logger is not null) PersistFailed(_logger, ex);
            }
            ApplyOverride(payload);
        }
    }

    /// <summary>Clear the override + restore the appsettings fallback.</summary>
    public void Clear()
    {
        lock (_writeLock)
        {
            _current = null;
            try
            {
                var path = ResolvePath();
                if (File.Exists(path)) File.Delete(path);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_logger is not null) PersistFailed(_logger, ex);
            }
            _accessor.SetOverride(null);
        }
    }

    private void ApplyOverride(BowireCatalogueOverride payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Provider))
        {
            _accessor.SetOverride(null);
            return;
        }
        var provider = BuildProvider(payload);
        _accessor.SetOverride(provider);
    }

    private static IBowireCatalogueProvider? BuildProvider(BowireCatalogueOverride payload)
    {
        // Compare case-insensitively without round-tripping through
        // ToLowerInvariant (CA1308) — the wire shape is documented as
        // lower-case but we accept any casing the operator types.
        var providerId = payload.Provider ?? string.Empty;
        if (string.Equals(providerId, "local", StringComparison.OrdinalIgnoreCase))
        {
            var opts = payload.Local ?? new BowireLocalCatalogueOptions();
            return new LocalCatalogueProvider(() =>
                string.IsNullOrWhiteSpace(opts.Path)
                    ? LocalCatalogueProvider.ResolveDefaultPath()
                    : opts.Path!);
        }
        if (string.Equals(providerId, "http", StringComparison.OrdinalIgnoreCase))
        {
            var opts = payload.Http ?? new BowireHttpCatalogueOptions();
            return new HttpCatalogueProvider(() => opts, () => new HttpClient());
        }
        if (string.Equals(providerId, "consul", StringComparison.OrdinalIgnoreCase))
        {
            var opts = payload.Consul ?? new BowireConsulCatalogueOptions();
            return new ConsulCatalogueProvider(() => opts, () => new HttpClient());
        }
        if (string.Equals(providerId, "kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            // Sibling-assembly seam — the Kubernetes provider ships in
            // its own package so core doesn't take a hard reference on
            // it. We discover it via assembly-scan (mirrors the auto-
            // discovery the BowireCatalogueProviderRegistry does at
            // startup) and project the override DTO onto the provider's
            // own options class via reflection so the package's API
            // stays the single source of truth for field naming.
            return ResolveSiblingProvider("kubernetes", payload.Kubernetes);
        }
        if (string.Equals(providerId, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSiblingProvider("agent", payload.Agent);
        }
        return null;
    }

    /// <summary>
    /// Resolve a sibling-assembly catalogue provider by id and
    /// re-bind its options object via the public setters on its
    /// internal options class. The provider's parameterless ctor (the
    /// one assembly-scan uses) reads options from a resolver
    /// delegate; we can't reach that delegate from out here, so we
    /// instead copy our override DTO into a fresh options instance
    /// and construct the provider through the public ctor overload
    /// that takes (Func&lt;Options&gt;, ...) where it exists. For the
    /// shipped k8s + agent providers that overload is internal —
    /// reflection over the type is the pragmatic seam without
    /// promoting both options classes into the core package.
    /// </summary>
    private static IBowireCatalogueProvider? ResolveSiblingProvider(string providerId, object? overrideDto)
    {
        // Step 1 — find the provider type by Id == providerId in any
        // loaded Kuestenlogik.Bowire* assembly.
        Type? providerType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith("Kuestenlogik.Bowire", StringComparison.OrdinalIgnoreCase)) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (Exception ex) when (ex is ReflectionTypeLoadException or TypeLoadException or FileLoadException or FileNotFoundException or BadImageFormatException)
            { continue; }
            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IBowireCatalogueProvider).IsAssignableFrom(t)) continue;
#pragma warning disable CA1031 // Do not catch general exception types
                try
                {
                    var instance = Activator.CreateInstance(t) as IBowireCatalogueProvider;
                    if (instance is null) continue;
                    if (string.Equals(instance.Id, providerId, StringComparison.OrdinalIgnoreCase))
                    {
                        providerType = t;
                        if (overrideDto is null) return instance;
                        break;
                    }
                }
                catch { /* skip — provider ctor threw */ }
#pragma warning restore CA1031
            }
            if (providerType is not null) break;
        }
        if (providerType is null) return null;
        if (overrideDto is null)
        {
#pragma warning disable CA1031
            try { return Activator.CreateInstance(providerType) as IBowireCatalogueProvider; }
            catch { return null; }
#pragma warning restore CA1031
        }

        // Step 2 — locate the provider's options class in the same
        // assembly. By convention each sibling provider lives next to
        // a BowireXxxCatalogueOptions class; we match on the class
        // name so the lookup survives a rename of the provider class
        // itself (it's the options shape we're projecting onto).
        var siblingAsm = providerType.Assembly;
        var optionsTypeName = providerId.Equals("kubernetes", StringComparison.OrdinalIgnoreCase)
            ? "BowireKubernetesCatalogueOptions"
            : providerId.Equals("agent", StringComparison.OrdinalIgnoreCase)
                ? "BowireAgentCatalogueOptions"
                : null;
        if (optionsTypeName is null)
        {
#pragma warning disable CA1031
            try { return Activator.CreateInstance(providerType) as IBowireCatalogueProvider; }
            catch { return null; }
#pragma warning restore CA1031
        }
        var optionsType = siblingAsm.GetTypes()
            .FirstOrDefault(t => string.Equals(t.Name, optionsTypeName, StringComparison.Ordinal));
        if (optionsType is null)
        {
#pragma warning disable CA1031
            try { return Activator.CreateInstance(providerType) as IBowireCatalogueProvider; }
            catch { return null; }
#pragma warning restore CA1031
        }

        // Step 3 — build a fresh options instance + project the
        // override DTO's public properties onto it. Only set the
        // sibling option when the source DTO carried a non-null /
        // non-empty value so unset fields fall back to the provider's
        // own defaults.
        object? optionsInstance;
#pragma warning disable CA1031
        try { optionsInstance = Activator.CreateInstance(optionsType); }
        catch { return null; }
#pragma warning restore CA1031
        if (optionsInstance is null) return null;
        var dtoProps = overrideDto.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var dtoProp in dtoProps)
        {
            var sibling = optionsType.GetProperty(dtoProp.Name, BindingFlags.Public | BindingFlags.Instance);
            if (sibling is null || !sibling.CanWrite) continue;
            var value = dtoProp.GetValue(overrideDto);
            if (value is null) continue;
            if (value is string s && string.IsNullOrEmpty(s)) continue;
            try { sibling.SetValue(optionsInstance, value); }
#pragma warning disable CA1031
            catch { /* type mismatch — leave provider default */ }
#pragma warning restore CA1031
        }

        // Step 4 — find an internal ctor on the provider that takes a
        // Func<Options, ...> resolver (the test seam every sibling
        // provider exposes); fall back to the parameterless ctor when
        // none is found, in which case the override values are
        // silently ignored. Phase-1 sibling providers all carry the
        // resolver ctor so the fallback path is for future / 3rd-party
        // providers.
        var funcResolverType = typeof(Func<>).MakeGenericType(optionsType);
        // Build a delegate that always returns this options instance.
        // CreateDelegate over a closure isn't trivial — use a small
        // anonymous-method shim via Delegate.CreateDelegate against
        // a captured field. Simpler: dynamic Lambda compile.
        var resolverDelegate = BuildResolver(optionsType, optionsInstance);

        var ctors = providerType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var ctor in ctors)
        {
            var parameters = ctor.GetParameters();
            if (parameters.Length == 0) continue;
            if (parameters[0].ParameterType != funcResolverType) continue;
            // The k8s + agent providers' resolver ctors take additional
            // dependencies (HttpClient factory, env seam) all of which
            // expose a no-arg default we can synthesise via the
            // parameterless overload of the same provider. We don't
            // need full reflection over those — instead, only try this
            // ctor when every other parameter has a default value.
            var args = new object?[parameters.Length];
            args[0] = resolverDelegate;
            var ok = true;
            for (var i = 1; i < parameters.Length; i++)
            {
                if (!parameters[i].HasDefaultValue) { ok = false; break; }
                args[i] = parameters[i].DefaultValue;
            }
            if (!ok)
            {
                // Try synthesising the remaining args: most sibling
                // providers' resolver ctor accepts simple factory
                // delegates (Func<HttpClient>, &c). We supply default
                // factories so the override path stays functional.
                ok = true;
                for (var i = 1; i < parameters.Length; i++)
                {
                    var pt = parameters[i].ParameterType;
                    args[i] = TrySynthesiseDefault(pt);
                    if (args[i] is null) { ok = false; break; }
                }
            }
            if (!ok) continue;
#pragma warning disable CA1031
            try { return ctor.Invoke(args) as IBowireCatalogueProvider; }
            catch { /* try next ctor */ }
#pragma warning restore CA1031
        }
        // Last-resort fallback: parameterless ctor (override values
        // silently ignored, but the provider still loads).
#pragma warning disable CA1031
        try { return Activator.CreateInstance(providerType) as IBowireCatalogueProvider; }
        catch { return null; }
#pragma warning restore CA1031
    }

    private static Delegate BuildResolver(Type optionsType, object optionsInstance)
    {
        // Func<T> from a captured instance — uses a closure compiled
        // via Delegate.CreateDelegate over a generic static helper.
        var method = typeof(BowireCatalogueOverrideStore)
            .GetMethod(nameof(ReturnCaptured), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(optionsType);
        return Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(optionsType), optionsInstance, method);
    }

    private static T ReturnCaptured<T>(T captured) => captured;

    private static object? TrySynthesiseDefault(Type t)
    {
        // Synthesise common factory shapes the sibling providers'
        // resolver ctors take. Anything we don't recognise returns
        // null so the caller skips that ctor.
        if (t == typeof(Func<HttpClient>)) return new Func<HttpClient>(() => new HttpClient());
        if (t == typeof(Func<HttpMessageHandler, HttpClient>))
            return new Func<HttpMessageHandler, HttpClient>(h => new HttpClient(h, disposeHandler: false));
        // Allow nullable / interface parameters to default to null —
        // the sibling providers cope with that via their own internal
        // fallbacks (the test seam was built for this).
        if (!t.IsValueType) return null;
        try { return Activator.CreateInstance(t); }
#pragma warning disable CA1031
        catch { return null; }
#pragma warning restore CA1031
    }

    private static BowireCatalogueOverride? Load()
    {
        try
        {
            var path = ResolvePath();
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonSerializer.Deserialize<BowireCatalogueOverride>(raw, JsonOptions);
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch
#pragma warning restore CA1031
        {
            return null;
        }
    }

    internal static string ResolvePath()
    {
        var envOverride = Environment.GetEnvironmentVariable("BOWIRE_CATALOGUE_CONFIG_PATH");
        if (!string.IsNullOrEmpty(envOverride)) return envOverride;
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.None);
        if (string.IsNullOrEmpty(home)) return string.Empty;
        var dir = Path.Combine(home, ".bowire");
        try { Directory.CreateDirectory(dir); }
#pragma warning disable CA1031
        catch { /* best-effort */ }
#pragma warning restore CA1031
        return Path.Combine(dir, "catalogue-config.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Action<ILogger, Exception> PersistFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(PersistFailed)),
            "Failed to persist catalogue-config override to disk.");
}
