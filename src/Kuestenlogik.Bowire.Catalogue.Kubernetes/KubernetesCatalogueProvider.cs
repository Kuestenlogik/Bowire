// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Catalogue.Kubernetes;

/// <summary>
/// Queries the Kubernetes API server for <c>Service</c> objects and
/// materialises each into a <see cref="BowireCatalogueEntry"/> — Phase
/// D of the catalogue-provider seam (#305 / #136).
/// </summary>
/// <remarks>
/// <para>
/// Talks to the API server over plain HTTP (Kubernetes' v1 REST
/// surface is stable across releases) instead of pulling the full
/// KubernetesClient package (~4 MB). Bowire needs exactly one verb —
/// <c>GET /api/v1/namespaces/{namespace}/services</c> — and the
/// 25-line JSON shape below is the entirety of the contract we care
/// about. Operators that need watch streams or richer object access
/// can stand up an aggregator and point Bowire at it via the cheaper
/// <c>http</c> provider.
/// </para>
/// <para>
/// Auth + URL discovery walks three sources in order:
/// </para>
/// <list type="number">
///   <item>Explicit options block (tests, sidecars).</item>
///   <item>In-cluster service-account files +
///   <c>KUBERNETES_SERVICE_HOST</c> / <c>_PORT</c> env-vars (the
///   default when running inside a pod).</item>
///   <item>Mounted kubeconfig file (developer laptops; current
///   context's cluster.server + user.token only).</item>
/// </list>
/// <para>
/// Each Service materialises into one entry per declared port. The
/// URL is built as
/// <c>{scheme}://{name}.{namespace}.svc.cluster.local:{port}</c> —
/// the canonical in-cluster DNS form. Service labels surface as
/// catalogue tags (<c>label-key:value</c>) so the workbench's filter
/// popup keys on them the same way the Consul provider's tags do.
/// </para>
/// </remarks>
public sealed class KubernetesCatalogueProvider : IBowireCatalogueProvider
{
    // ServiceAccount mount points are standardised across kubelet
    // versions; hard-coding the path is the documented in-cluster
    // pattern (see Kubernetes docs: "Accessing the API from within a Pod").
    private const string ServiceAccountTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string ServiceAccountCaPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
    private const string ServiceAccountNamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    private readonly Func<BowireKubernetesCatalogueOptions> _optionsResolver;
    private readonly Func<HttpMessageHandler, HttpClient> _clientFactory;
    private readonly IKubernetesEnvironment _environment;

    /// <summary>
    /// Parameterless ctor for the assembly-scan discovery path. Uses
    /// the default options resolver (no explicit settings) and the
    /// real filesystem / env-var environment.
    /// </summary>
    public KubernetesCatalogueProvider() : this(
        () => new BowireKubernetesCatalogueOptions(),
        (handler) => new HttpClient(handler, disposeHandler: false),
        new DefaultKubernetesEnvironment())
    { }

    /// <summary>
    /// Test seam — pass explicit option, HTTP-client, and environment
    /// abstractions so tests don't depend on the in-cluster files /
    /// env-vars or make real network calls.
    /// </summary>
    internal KubernetesCatalogueProvider(
        Func<BowireKubernetesCatalogueOptions> optionsResolver,
        Func<HttpMessageHandler, HttpClient> clientFactory,
        IKubernetesEnvironment environment)
    {
        _optionsResolver = optionsResolver;
        _clientFactory = clientFactory;
        _environment = environment;
    }

    /// <inheritdoc/>
    public string Id => "kubernetes";

    /// <inheritdoc/>
    public string Name => "Kubernetes";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BowireCatalogueEntry>> FetchAsync(CancellationToken cancellationToken)
    {
        var options = _optionsResolver();
        var connection = ResolveConnection(options);
        if (connection is null)
        {
            // No API-server coordinates available — treat as "no
            // catalogue" rather than throwing. Matches the http
            // provider's behaviour when its URL is null.
            return Array.Empty<BowireCatalogueEntry>();
        }

        var ns = !string.IsNullOrWhiteSpace(options.Namespace)
            ? options.Namespace!
            : connection.Namespace;
        var scheme = string.IsNullOrWhiteSpace(options.Scheme) ? "http" : options.Scheme!;
        var selectorSuffix = string.IsNullOrWhiteSpace(options.LabelSelector)
            ? string.Empty
            : $"?labelSelector={Uri.EscapeDataString(options.LabelSelector!)}";

        // CA cert is owned at this scope so we can dispose it after
        // the fetch — the handler's validation callback holds onto
        // it for the duration of the call. CA1859 wants the
        // concrete handler type.
        using var ca = ResolveCaCertificate(connection, options);
        using var handler = BuildHandler(options, ca);
        using var client = _clientFactory(handler);
        client.Timeout = options.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(10)
            : options.Timeout;
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {connection.Token}");
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var listUrl = new Uri(
            $"{connection.ApiServerUrl.TrimEnd('/')}/api/v1/namespaces/{Uri.EscapeDataString(ns)}/services{selectorSuffix}");

        ServiceList? services;
        using (var response = await client.GetAsync(listUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            services = await response.Content.ReadFromJsonAsync<ServiceList>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }

        if (services?.Items is null || services.Items.Count == 0)
        {
            return Array.Empty<BowireCatalogueEntry>();
        }

        var entries = new List<BowireCatalogueEntry>();
        foreach (var svc in services.Items)
        {
            if (svc.Metadata is null || string.IsNullOrWhiteSpace(svc.Metadata.Name)) continue;
            if (svc.Spec?.Ports is null || svc.Spec.Ports.Count == 0) continue;

            var entryName = svc.Metadata.Name!;
            var entryNamespace = string.IsNullOrWhiteSpace(svc.Metadata.Namespace)
                ? ns
                : svc.Metadata.Namespace!;

            // Promote labels to "key:value" tags so the workbench's
            // filter popup keys on them the same way the Consul
            // provider's tags do. We keep the unprefixed
            // service-account namespace alongside as a convenient
            // filter target.
            var tags = new List<string> { $"namespace:{entryNamespace}" };
            if (svc.Metadata.Labels is not null)
            {
                foreach (var (key, value) in svc.Metadata.Labels)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    tags.Add(string.IsNullOrEmpty(value) ? key : $"{key}:{value}");
                }
            }

            foreach (var port in svc.Spec.Ports)
            {
                if (port.Port <= 0) continue;
                var url = $"{scheme}://{entryName}.{entryNamespace}.svc.cluster.local:{port.Port}";
                // Multi-port services get one entry per port so each
                // surface lands in the workbench with its own URL.
                // Tack the port's name onto the entry name when set so
                // the operator can tell the rows apart.
                var label = string.IsNullOrWhiteSpace(port.Name)
                    ? entryName
                    : $"{entryName} ({port.Name})";

                entries.Add(new BowireCatalogueEntry(
                    Url: url,
                    Name: label,
                    Tags: tags));
            }
        }
        return entries;
    }

    /// <summary>
    /// Walk the three connection sources (explicit options →
    /// in-cluster service-account → kubeconfig) and return the first
    /// one that resolves to a full
    /// <see cref="KubernetesConnection"/>. Returns <c>null</c> when
    /// none do — the provider treats that as an empty catalogue.
    /// </summary>
    internal KubernetesConnection? ResolveConnection(BowireKubernetesCatalogueOptions options)
    {
        // 1) Explicit options block.
        if (!string.IsNullOrWhiteSpace(options.ApiServerUrl) && !string.IsNullOrWhiteSpace(options.Token))
        {
            return new KubernetesConnection(
                ApiServerUrl: options.ApiServerUrl!,
                Token: options.Token!,
                CaCertificatePem: options.CaCertificatePem,
                Namespace: options.Namespace ?? "default");
        }

        // 2) In-cluster service-account.
        var inClusterHost = _environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var inClusterPort = _environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT");
        if (!string.IsNullOrWhiteSpace(inClusterHost)
            && _environment.FileExists(ServiceAccountTokenPath))
        {
            var token = _environment.ReadAllText(ServiceAccountTokenPath).Trim();
            var port = string.IsNullOrWhiteSpace(inClusterPort) ? "443" : inClusterPort!;
            var apiServerUrl = $"https://{inClusterHost}:{port}";
            var ca = _environment.FileExists(ServiceAccountCaPath)
                ? _environment.ReadAllText(ServiceAccountCaPath)
                : null;
            var ns = _environment.FileExists(ServiceAccountNamespacePath)
                ? _environment.ReadAllText(ServiceAccountNamespacePath).Trim()
                : "default";
            return new KubernetesConnection(
                ApiServerUrl: apiServerUrl,
                Token: token,
                CaCertificatePem: options.CaCertificatePem ?? ca,
                Namespace: options.Namespace ?? ns);
        }

        // 3) Mounted kubeconfig — minimal current-context parse:
        //    cluster.server + user.token. Anything more (exec plugins,
        //    client-cert auth) is out of scope for v1.
        var kubeconfigPath = !string.IsNullOrWhiteSpace(options.KubeconfigPath)
            ? options.KubeconfigPath
            : _environment.GetEnvironmentVariable("KUBECONFIG");
        if (string.IsNullOrWhiteSpace(kubeconfigPath))
        {
            var home = _environment.GetEnvironmentVariable("HOME")
                       ?? _environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(home))
            {
                kubeconfigPath = System.IO.Path.Combine(home!, ".kube", "config");
            }
        }
        if (!string.IsNullOrWhiteSpace(kubeconfigPath) && _environment.FileExists(kubeconfigPath!))
        {
            var parsed = TryParseKubeconfig(_environment.ReadAllText(kubeconfigPath!));
            if (parsed is not null)
            {
                return new KubernetesConnection(
                    ApiServerUrl: parsed.Value.Server,
                    Token: parsed.Value.Token,
                    CaCertificatePem: options.CaCertificatePem,
                    Namespace: options.Namespace ?? "default");
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve the operator-supplied CA cert (or <c>null</c> when no
    /// CA pinning is requested). Owned by <see cref="FetchAsync"/>
    /// for the duration of the call — disposed before the next
    /// fetch through the surrounding <c>using</c>.
    /// </summary>
    private static X509Certificate2? ResolveCaCertificate(
        KubernetesConnection connection,
        BowireKubernetesCatalogueOptions options)
    {
        if (options.SkipTlsVerification) return null;
        var caPem = !string.IsNullOrWhiteSpace(options.CaCertificatePem)
            ? options.CaCertificatePem
            : connection.CaCertificatePem;
        if (string.IsNullOrWhiteSpace(caPem)) return null;
        return X509Certificate2.CreateFromPem(caPem!);
    }

    /// <summary>
    /// Build the HTTP handler for the API-server call: optional CA
    /// trust + the explicit skip-verification escape hatch.
    /// </summary>
    private static HttpClientHandler BuildHandler(
        BowireKubernetesCatalogueOptions options,
        X509Certificate2? caCertificate)
    {
        var handler = new HttpClientHandler();
        if (options.SkipTlsVerification)
        {
            // Test clusters only — explicit operator opt-in.
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            return handler;
        }

        if (caCertificate is not null)
        {
            // Pin the cluster CA so we don't depend on the OS trust
            // store carrying the in-cluster CA cert. The chain object
            // passed to the callback already holds the leaf — we
            // re-validate against a custom trust anchor seeded with
            // the operator-supplied CA.
            var ca = caCertificate;
            handler.ServerCertificateCustomValidationCallback = (_, leaf, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (leaf is null || chain is null) return false;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                return chain.Build(leaf);
            };
        }
        return handler;
    }

    /// <summary>
    /// JSON options shared with the rest of the provider. Same shape
    /// as <c>LocalCatalogueProvider.JsonOptions</c> (case-insensitive
    /// + tolerant of trailing commas / comments) but locally owned so
    /// the sibling assembly doesn't need a private reach into core.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Minimal kubeconfig parser — pulls
    /// <c>current-context</c> → <c>contexts[*].context.{cluster,user}</c>
    /// → <c>clusters[*].cluster.server</c> + <c>users[*].user.token</c>.
    /// Returns <c>null</c> when the file shape doesn't match (no
    /// current context, missing token, exec plugin instead of a
    /// static token, ...). Operators on a non-token kubeconfig pass
    /// the token via the explicit options block instead.
    /// </summary>
    internal static (string Server, string Token)? TryParseKubeconfig(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        // Lightweight YAML reader: kubeconfig is a flat 2-level
        // mapping of named lists, which the line-oriented walker below
        // handles without a YAML dependency. Anything more exotic
        // (anchors, flow style) returns null and the operator falls
        // back to the explicit options path.
        string? currentContext = null;
        var contexts = new Dictionary<string, (string Cluster, string User)>(StringComparer.Ordinal);
        var clusters = new Dictionary<string, string>(StringComparer.Ordinal);
        var users = new Dictionary<string, string>(StringComparer.Ordinal);

        string? section = null;     // "contexts" | "clusters" | "users"
        string? entryName = null;
        string? cluster = null;
        string? user = null;
        string? server = null;
        string? token = null;

        void Commit()
        {
            if (entryName is null) return;
            switch (section)
            {
                case "contexts" when cluster is not null && user is not null:
                    contexts[entryName] = (cluster, user); break;
                case "clusters" when server is not null:
                    clusters[entryName] = server; break;
                case "users" when token is not null:
                    users[entryName] = token; break;
            }
            entryName = null; cluster = null; user = null; server = null; token = null;
        }

        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;

            var trimmed = line.TrimStart();

            // List-item markers take precedence — `- name: foo`
            // opens a new record inside the current section. The `-`
            // character can sit at column 0 (top-level lists) or at
            // any indent under a parent mapping; either way the
            // record-open semantics are the same.
            if (trimmed.StartsWith("- name:", StringComparison.Ordinal))
            {
                if (section is not null)
                {
                    Commit();
                    entryName = trimmed["- name:".Length..].Trim().Trim('"');
                }
                continue;
            }

            // Top-level keys (no indent + not a list item handled above).
            if (!char.IsWhiteSpace(line[0]) && trimmed.Contains(':', StringComparison.Ordinal))
            {
                Commit();
                var kv = trimmed.Split(':', 2);
                var key = kv[0].Trim();
                var value = kv.Length > 1 ? kv[1].Trim().Trim('"') : string.Empty;
                if (key == "current-context") { currentContext = value; section = null; continue; }
                if (key is "contexts" or "clusters" or "users") { section = key; continue; }
                section = null;
                continue;
            }
            if (section is null) continue;

            // Inner fields under the current entry. Order matters: a
            // bare `cluster:` or `user:` mapping marker (no value) is
            // skipped by the empty-value guard; an inline
            // `cluster: dev-cluster` carries the reference.
            if (trimmed.StartsWith("server:", StringComparison.Ordinal))
                server = trimmed["server:".Length..].Trim().Trim('"');
            else if (trimmed.StartsWith("token:", StringComparison.Ordinal))
                token = trimmed["token:".Length..].Trim().Trim('"');
            else if (trimmed.StartsWith("cluster:", StringComparison.Ordinal))
            {
                var v = trimmed["cluster:".Length..].Trim().Trim('"');
                if (!string.IsNullOrEmpty(v)) cluster = v;
            }
            else if (trimmed.StartsWith("user:", StringComparison.Ordinal))
            {
                var v = trimmed["user:".Length..].Trim().Trim('"');
                if (!string.IsNullOrEmpty(v)) user = v;
            }
        }
        Commit();

        if (string.IsNullOrEmpty(currentContext)) return null;
        if (!contexts.TryGetValue(currentContext!, out var ctx)) return null;
        if (!clusters.TryGetValue(ctx.Cluster, out var ctxServer)) return null;
        if (!users.TryGetValue(ctx.User, out var ctxToken)) return null;
        return (ctxServer, ctxToken);
    }

    /// <summary>
    /// Resolved coordinates for a single API-server connection.
    /// </summary>
    internal sealed record KubernetesConnection(
        string ApiServerUrl,
        string Token,
        string? CaCertificatePem,
        string Namespace);

    // === Wire shape — minimal subset of the k8s v1 Service list. ===

    private sealed record ServiceList(
        [property: JsonPropertyName("items")] List<ServiceItem>? Items);

    private sealed record ServiceItem(
        [property: JsonPropertyName("metadata")] ObjectMeta? Metadata,
        [property: JsonPropertyName("spec")] ServiceSpec? Spec);

    private sealed record ObjectMeta(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("namespace")] string? Namespace,
        [property: JsonPropertyName("labels")] Dictionary<string, string>? Labels);

    private sealed record ServiceSpec(
        [property: JsonPropertyName("ports")] List<ServicePort>? Ports);

    private sealed record ServicePort(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("port")] int Port);
}

/// <summary>
/// Filesystem / env-var seam — defaulted to the real OS in
/// production, overridden by tests with stub data.
/// </summary>
internal interface IKubernetesEnvironment
{
    string? GetEnvironmentVariable(string name);
    bool FileExists(string path);
    string ReadAllText(string path);
}

internal sealed class DefaultKubernetesEnvironment : IKubernetesEnvironment
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
}
