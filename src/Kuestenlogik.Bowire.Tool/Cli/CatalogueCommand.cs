// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire catalogue</c> — inspect and select the URL/service
/// catalogue the workbench browses (#136 / #537).
///
/// <para>
/// Runs entirely in-process against
/// <see cref="BowireCatalogueProviderRegistry"/>; there is no server to
/// contact and no <c>bowire</c> instance that has to be running. The
/// resolution order mirrors what the host does at boot — the persisted
/// <c>~/.bowire/catalogue-config.json</c> override (written by
/// <c>catalogue use</c> or by the workbench's Settings → Catalogue
/// providers tab) wins over the flags passed here.
/// </para>
///
/// <list type="bullet">
///   <item><c>list</c> — fetch the active catalogue and print one row
///     per entry (or the raw snapshot with <c>--json</c>).</item>
///   <item><c>providers</c> — which provider implementations are loaded
///     in this install, so an operator can see whether the kubernetes /
///     agent sibling package is present before configuring it.</item>
///   <item><c>use</c> — persist a provider override.</item>
///   <item><c>clear</c> — drop the override and fall back to
///     appsettings.</item>
/// </list>
/// </summary>
internal static class CatalogueCommand
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Command Build()
    {
        var catalogue = new Command("catalogue",
            "Inspect and select the URL/service catalogue the workbench browses (#136). 'list' prints the entries the active provider returns, 'providers' shows which provider implementations are installed, 'use' persists a provider override to ~/.bowire/catalogue-config.json (the same file the workbench's Settings → Catalogue providers tab writes), and 'clear' drops it again.");
        catalogue.Add(BuildListCommand());
        catalogue.Add(BuildProvidersCommand());
        catalogue.Add(BuildUseCommand());
        catalogue.Add(BuildClearCommand());
        return catalogue;
    }

    // ---------- shared option shapes ----------

    private static Option<string?> ProviderOption() => new("--provider")
    {
        Description = "Provider id: local / http / consul / kubernetes / agent. kubernetes + agent require the matching Kuestenlogik.Bowire.Catalogue.* package (`bowire plugin install …`).",
    };

    private static Option<string?> PathOption() => new("--path")
    {
        Description = "Catalogue file for the 'local' provider. Defaults to ~/.bowire/catalogue.json.",
    };

    private static Option<string?> UrlOption() => new("--url")
    {
        Description = "Catalogue document URL for the 'http' provider.",
    };

    private static Option<string?> ConsulOption() => new("--consul")
    {
        Description = "Consul agent address for the 'consul' provider (e.g. http://localhost:8500).",
    };

    // ---------- list ----------

    private static Command BuildListCommand()
    {
        var list = new Command("list",
            "Fetch a catalogue and print its entries. With --provider (plus its --path / --url / --consul detail) it reads that provider without persisting anything; with no flags it reads whatever the workbench would — the persisted override from `catalogue use`, or nothing. Exits 1 when no provider is configured so a CI step can gate on it.");
        var providerOpt = ProviderOption();
        var pathOpt = PathOption();
        var urlOpt = UrlOption();
        var consulOpt = ConsulOption();
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Emit the raw snapshot as JSON ({ providerId, providerName, entries: [...] }) instead of the human table.",
        };
        list.Add(providerOpt);
        list.Add(pathOpt);
        list.Add(urlOpt);
        list.Add(consulOpt);
        list.Add(jsonOpt);

        list.SetAction((pr, ct) => RunListAsync(
            pr.GetValue(providerOpt), pr.GetValue(pathOpt), pr.GetValue(urlOpt), pr.GetValue(consulOpt),
            pr.GetValue(jsonOpt),
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct));
        return list;
    }

    internal static async Task<int> RunListAsync(
        string? provider, string? path, string? url, string? consul, bool json,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var io = CommandIo.Resolve(stdout, stderr);
        IBowireCatalogueProvider? active;
        try
        {
            active = ResolveActiveProvider(provider, path, url, consul);
        }
        catch (InvalidOperationException ex)
        {
            io.ErrLine($"catalogue list: {ex.Message}");
            return 78; // EX_CONFIG
        }

        if (active is null)
        {
            io.ErrLine("catalogue list: no catalogue provider configured.");
            io.ErrLine("  Pass --provider local (optionally with --path), or persist one with `bowire catalogue use local`.");
            return 1;
        }

        IReadOnlyList<BowireCatalogueEntry> entries;
        try
        {
            entries = await active.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            io.ErrLine($"catalogue list: {active.Name} ({active.Id}) fetch failed: {ex.Message}");
            return 70; // EX_SOFTWARE
        }

        if (json)
        {
            io.OutLine(JsonSerializer.Serialize(new
            {
                providerId = active.Id,
                providerName = active.Name,
                entries,
            }, JsonOut));
            return 0;
        }

        if (entries.Count == 0)
        {
            io.OutLine($"{active.Name} ({active.Id}) returned no entries.");
            return 0;
        }

        io.OutLine($"{active.Name} ({active.Id}) — {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");
        io.OutLine();
        foreach (var entry in entries)
        {
            io.OutLine($"  {ComposeEntryUrl(entry)}");
            var detail = new List<string>();
            if (!string.IsNullOrWhiteSpace(entry.Name)) detail.Add(entry.Name!);
            if (entry.Protocols is { Count: > 0 }) detail.Add(string.Join('/', entry.Protocols));
            if (entry.Tags is { Count: > 0 }) detail.Add(string.Join(' ', entry.Tags));
            if (detail.Count > 0) io.OutLine($"      {string.Join("  ·  ", detail)}");
            if (!string.IsNullOrWhiteSpace(entry.Schema)) io.OutLine($"      schema: {entry.Schema}");
        }
        return 0;
    }

    /// <summary>
    /// Compose the URL Bowire actually discovers against — the entry's
    /// first declared protocol becomes the <c>protocol@</c> hint unless
    /// the URL already carries one. Identical to what the workbench's
    /// <c>catalogueEntryUrl</c> and <c>scripts/ci/smoke-samples.mjs</c>
    /// do, so `bowire catalogue list` prints exactly the strings the UI
    /// would put in the Sources rail.
    /// </summary>
    internal static string ComposeEntryUrl(BowireCatalogueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var raw = entry.Url ?? string.Empty;
        if (raw.Length == 0) return raw;
        if (HasProtocolHint(raw)) return raw;
        var proto = entry.Protocols is { Count: > 0 } ? entry.Protocols[0] : null;
        return string.IsNullOrWhiteSpace(proto) ? raw : $"{proto}@{raw}";
    }

    private static bool HasProtocolHint(string url)
    {
        // "<alnum-ish>@rest" — matches the JS /^[a-z][a-z0-9]*@/i probe
        // without paying for a Regex. A scheme ("https://") can't match
        // because ':' and '/' are rejected before the '@'.
        for (var i = 0; i < url.Length; i++)
        {
            var c = url[i];
            if (c == '@') return i > 0;
            if (i == 0 ? !char.IsAsciiLetter(c) : !char.IsAsciiLetterOrDigit(c)) return false;
        }
        return false;
    }

    // ---------- providers ----------

    private static Command BuildProvidersCommand()
    {
        var providers = new Command("providers",
            "List the catalogue provider implementations loaded in this install. local / http / consul ship in the core package; kubernetes and agent only appear after `bowire plugin install Kuestenlogik.Bowire.Catalogue.Kubernetes` / `.Agent`.");
        var jsonOpt = new Option<bool>("--json") { Description = "Emit [{ id, name }] as JSON." };
        providers.Add(jsonOpt);
        providers.SetAction(pr => RunProviders(
            pr.GetValue(jsonOpt), pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error));
        return providers;
    }

    internal static int RunProviders(bool json, TextWriter stdout, TextWriter stderr)
    {
        var io = CommandIo.Resolve(stdout, stderr);
        var loaded = BowireCatalogueProviderRegistry.Discover()
            .Values
            .Select(p => new { id = p.Id, name = p.Name })
            .OrderBy(p => p.id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (json)
        {
            io.OutLine(JsonSerializer.Serialize(loaded, JsonOut));
            return 0;
        }

        if (loaded.Count == 0)
        {
            io.OutLine("No catalogue providers loaded.");
            return 0;
        }
        foreach (var p in loaded) io.OutLine($"  {p.id,-12} {p.name}");
        foreach (var missing in SiblingPackages.Keys)
        {
            if (loaded.Exists(p => string.Equals(p.id, missing, StringComparison.OrdinalIgnoreCase))) continue;
            io.OutLine($"  {missing,-12} (not installed — `bowire plugin install {SiblingPackages[missing]}`)");
        }
        return 0;
    }

    /// <summary>
    /// Catalogue providers that ship OUTSIDE the core package. Kept as
    /// data so the CLI, the error paths and the docs agree on which
    /// package name to print.
    /// </summary>
    private static readonly Dictionary<string, string> SiblingPackages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["kubernetes"] = "Kuestenlogik.Bowire.Catalogue.Kubernetes",
            ["agent"] = "Kuestenlogik.Bowire.Catalogue.Agent",
        };

    private static string InstallHintFor(string providerId)
        => SiblingPackages.TryGetValue(providerId, out var package)
            ? $"It ships as a separate package — `bowire plugin install {package}` and restart."
            : "Valid ids are local / http / consul, plus kubernetes / agent when their package is installed.";

    // ---------- use ----------

    private static Command BuildUseCommand()
    {
        var use = new Command("use",
            "Persist a catalogue-provider override to ~/.bowire/catalogue-config.json. The workbench reads the same file on boot, so this is the scriptable equivalent of picking a provider in Settings → Catalogue providers. Overrides whatever appsettings.json configures until `catalogue clear` runs.");
        var providerArg = new Argument<string>("provider")
        {
            Description = "Provider id to activate: local / http / consul / kubernetes / agent.",
        };
        providerArg.CompletionSources.Add("local", "http", "consul", "kubernetes", "agent");
        var pathOpt = PathOption();
        var urlOpt = UrlOption();
        var consulOpt = ConsulOption();
        var tokenOpt = new Option<string?>("--token")
        {
            Description = "Auth secret for the chosen provider — the Consul ACL token, or the HTTP provider's Authorization header value. Stored in plaintext in ~/.bowire/catalogue-config.json; prefer appsettings + a secret store on shared machines.",
        };
        use.Add(providerArg);
        use.Add(pathOpt);
        use.Add(urlOpt);
        use.Add(consulOpt);
        use.Add(tokenOpt);

        use.SetAction(pr => RunUse(
            pr.GetValue(providerArg)!, pr.GetValue(pathOpt), pr.GetValue(urlOpt),
            pr.GetValue(consulOpt), pr.GetValue(tokenOpt),
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error));
        return use;
    }

    internal static int RunUse(
        string providerId, string? path, string? url, string? consul, string? token,
        TextWriter stdout, TextWriter stderr)
    {
        var io = CommandIo.Resolve(stdout, stderr);
        if (string.IsNullOrWhiteSpace(providerId))
        {
            io.ErrLine("catalogue use: provider id is required.");
            return 64; // EX_USAGE
        }
        var id = providerId.Trim();

        var loaded = BowireCatalogueProviderRegistry.Discover();
        if (!loaded.ContainsKey(id))
        {
            var known = loaded.Count == 0 ? "(none loaded)" : string.Join(", ", loaded.Keys.Order(StringComparer.OrdinalIgnoreCase));
            io.ErrLine($"catalogue use: provider '{id}' is not loaded. Loaded providers: {known}.");
            io.ErrLine("  " + InstallHintFor(id));
            return 78; // EX_CONFIG
        }

        var payload = BuildOverride(id, path, url, consul, token);
        var accessor = new BowireCatalogueProviderAccessor(null);
        var store = new BowireCatalogueOverrideStore(accessor);
        store.Save(payload);

        var resolved = accessor.Provider;
        if (resolved is null)
        {
            io.ErrLine($"catalogue use: '{id}' persisted but did not resolve to a provider instance.");
            return 70; // EX_SOFTWARE
        }
        io.OutLine($"Catalogue provider set to {resolved.Name} ({resolved.Id}).");
        io.OutLine($"  → {BowireCatalogueOverrideStore.ResolvePath()}");
        io.OutLine("  → `bowire catalogue clear` restores the appsettings fallback.");
        return 0;
    }

    // ---------- clear ----------

    private static Command BuildClearCommand()
    {
        var clear = new Command("clear",
            "Delete the persisted catalogue override (~/.bowire/catalogue-config.json) so the host falls back to Bowire:Discovery:Catalogue in appsettings.json (or to no catalogue at all).");
        clear.SetAction(pr => RunClear(
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error));
        return clear;
    }

    internal static int RunClear(TextWriter stdout, TextWriter stderr)
    {
        var io = CommandIo.Resolve(stdout, stderr);
        var path = BowireCatalogueOverrideStore.ResolvePath();
        var existed = !string.IsNullOrEmpty(path) && File.Exists(path);

        var accessor = new BowireCatalogueProviderAccessor(null);
        var store = new BowireCatalogueOverrideStore(accessor);
        store.Clear();

        io.OutLine(existed
            ? $"Cleared the catalogue override at {path}."
            : "No catalogue override was set — nothing to clear.");
        return 0;
    }

    // ---------- resolution ----------

    /// <summary>
    /// Resolve which provider this invocation should read from.
    /// <para>
    /// Explicit flags win: <c>catalogue list --provider consul</c> means
    /// "look at Consul now", not "look at whatever I persisted last
    /// week". Without flags we fall back to the persisted override, so a
    /// bare <c>catalogue list</c> shows exactly what the workbench shows.
    /// Returns <c>null</c> when neither names a provider.
    /// </para>
    /// </summary>
    private static IBowireCatalogueProvider? ResolveActiveProvider(
        string? provider, string? path, string? url, string? consul)
    {
        // An explicit --path / --url / --consul with no --provider is
        // unambiguous, so infer the id rather than making the operator
        // repeat themselves.
        var id = provider?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            if (!string.IsNullOrWhiteSpace(path)) id = "local";
            else if (!string.IsNullOrWhiteSpace(url)) id = "http";
            else if (!string.IsNullOrWhiteSpace(consul)) id = "consul";
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            // Nothing on the command line — mirror the host: constructing
            // the store Load()s + applies ~/.bowire/catalogue-config.json.
            var accessor = new BowireCatalogueProviderAccessor(null);
            _ = new BowireCatalogueOverrideStore(accessor);
            return accessor.Provider;
        }

        var loaded = BowireCatalogueProviderRegistry.Discover();
        if (!loaded.ContainsKey(id))
        {
            var known = loaded.Count == 0 ? "(none loaded)" : string.Join(", ", loaded.Keys.Order(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture,
                    "provider '{0}' is not loaded. Loaded providers: {1}. {2}",
                    id, known, InstallHintFor(id)));
        }

        // Route through the override store's own DTO→provider builder so
        // the flag path and the persisted path construct providers
        // identically — one place decides what "provider: local, path: X"
        // means. Nothing is written to disk: `catalogue list --provider …`
        // is an inspection, not a configuration change.
        return BowireCatalogueOverrideStore.BuildProvider(
            BuildOverride(id, path, url, consul, token: null));
    }

    private static BowireCatalogueOverride BuildOverride(
        string providerId, string? path, string? url, string? consul, string? token)
    {
        var payload = new BowireCatalogueOverride { Provider = providerId };
        if (string.Equals(providerId, "local", StringComparison.OrdinalIgnoreCase))
        {
            payload.Local = new BowireLocalCatalogueOptions { Path = string.IsNullOrWhiteSpace(path) ? null : path };
        }
        else if (string.Equals(providerId, "http", StringComparison.OrdinalIgnoreCase))
        {
            payload.Http = new BowireHttpCatalogueOptions
            {
                Url = string.IsNullOrWhiteSpace(url) ? null : url,
                Authorization = string.IsNullOrWhiteSpace(token) ? null : token,
            };
        }
        else if (string.Equals(providerId, "consul", StringComparison.OrdinalIgnoreCase))
        {
            payload.Consul = new BowireConsulCatalogueOptions
            {
                Address = string.IsNullOrWhiteSpace(consul) ? null : consul,
                Token = string.IsNullOrWhiteSpace(token) ? null : token,
            };
        }
        return payload;
    }
}
