// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Text;
using Kuestenlogik.Bowire.Catalogue.Kubernetes;
using Kuestenlogik.Bowire.Sources;

namespace Kuestenlogik.Bowire.Catalogue.Kubernetes.Tests;

/// <summary>
/// Tests for <see cref="KubernetesCatalogueProvider"/> — Phase D of
/// the catalogue-provider seam (#305).
/// </summary>
public sealed class KubernetesCatalogueProviderTests
{
    [Fact]
    public void Id_And_Name_Match_Documented_Wire_Surface()
    {
        var provider = new KubernetesCatalogueProvider();
        Assert.Equal("kubernetes", provider.Id);
        Assert.Equal("Kubernetes", provider.Name);
    }

    [Fact]
    public void Discover_Picks_Up_Provider_From_Sibling_Assembly()
    {
        // Force the sibling assembly into the load set so the registry
        // sees it during AppDomain enumeration.
        _ = new KubernetesCatalogueProvider();
        var providers = BowireCatalogueProviderRegistry.Discover();
        Assert.Contains("kubernetes", providers.Keys);
    }

    [Fact]
    public async Task FetchAsync_Returns_Empty_When_No_Connection_Resolves()
    {
        var provider = new KubernetesCatalogueProvider(
            () => new BowireKubernetesCatalogueOptions(),
            handler => new HttpClient(handler, disposeHandler: false),
            new StubEnvironment());

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_Materialises_Entries_From_Api_Server()
    {
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(
                "/api/v1/namespaces/default/services",
                req.RequestUri!.AbsolutePath);
            Assert.Equal(
                "Bearer t0k3n",
                req.Headers.GetValues("Authorization").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "metadata": {
                            "name": "payments",
                            "namespace": "default",
                            "labels": { "app": "payments", "tier": "backend" }
                          },
                          "spec": {
                            "ports": [
                              { "name": "http", "port": 8080 },
                              { "name": "grpc", "port": 50051 }
                            ]
                          }
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json"),
            };
        });

        var provider = new KubernetesCatalogueProvider(
            () => new BowireKubernetesCatalogueOptions
            {
                ApiServerUrl = "https://api.test:6443",
                Token = "t0k3n",
                Namespace = "default",
                Scheme = "http",
                SkipTlsVerification = true,
            },
            _ => new HttpClient(handler, disposeHandler: false),
            new StubEnvironment());

        var entries = await provider.FetchAsync(TestContext.Current.CancellationToken);

        // One entry per declared port — both URLs land.
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Url == "http://payments.default.svc.cluster.local:8080");
        Assert.Contains(entries, e => e.Url == "http://payments.default.svc.cluster.local:50051");
        // Labels promoted to tags + the synthetic namespace tag.
        Assert.All(entries, e =>
        {
            Assert.Contains("namespace:default", e.Tags!);
            Assert.Contains("app:payments", e.Tags!);
            Assert.Contains("tier:backend", e.Tags!);
        });
        // Port name disambiguates multi-port services in the label.
        Assert.Contains(entries, e => e.Name == "payments (http)");
        Assert.Contains(entries, e => e.Name == "payments (grpc)");
    }

    [Fact]
    public async Task FetchAsync_Forwards_Label_Selector_To_Api_Server()
    {
        string? observedQuery = null;
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            observedQuery = req.RequestUri!.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"items":[]}""", Encoding.UTF8, "application/json"),
            };
        });

        var provider = new KubernetesCatalogueProvider(
            () => new BowireKubernetesCatalogueOptions
            {
                ApiServerUrl = "https://api.test:6443",
                Token = "t0k3n",
                Namespace = "default",
                LabelSelector = "bowire/discoverable=true,env=staging",
                SkipTlsVerification = true,
            },
            _ => new HttpClient(handler, disposeHandler: false),
            new StubEnvironment());

        await provider.FetchAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(observedQuery);
        // Selector is URL-escaped but the operator-supplied form
        // round-trips through Uri.UnescapeDataString.
        Assert.Contains("labelSelector=", observedQuery!, StringComparison.Ordinal);
        Assert.Equal(
            "bowire/discoverable=true,env=staging",
            Uri.UnescapeDataString(observedQuery!.Replace("?labelSelector=", "", StringComparison.Ordinal)));
    }

    [Fact]
    public void ResolveConnection_Uses_Explicit_Options_When_Provided()
    {
        var provider = new KubernetesCatalogueProvider(
            () => new BowireKubernetesCatalogueOptions(),
            _ => new HttpClient(),
            new StubEnvironment());

        var conn = provider.ResolveConnection(new BowireKubernetesCatalogueOptions
        {
            ApiServerUrl = "https://api.test:6443",
            Token = "abc",
            Namespace = "demo",
        });

        Assert.NotNull(conn);
        Assert.Equal("https://api.test:6443", conn!.ApiServerUrl);
        Assert.Equal("abc", conn.Token);
        Assert.Equal("demo", conn.Namespace);
    }

    [Fact]
    public void ResolveConnection_Falls_Back_To_In_Cluster_Files()
    {
        var env = new StubEnvironment
        {
            EnvVars =
            {
                ["KUBERNETES_SERVICE_HOST"] = "10.0.0.1",
                ["KUBERNETES_SERVICE_PORT"] = "443",
            },
            Files =
            {
                ["/var/run/secrets/kubernetes.io/serviceaccount/token"] = "in-cluster-token",
                ["/var/run/secrets/kubernetes.io/serviceaccount/ca.crt"] = "<pem>",
                ["/var/run/secrets/kubernetes.io/serviceaccount/namespace"] = "bowire",
            },
        };
        var provider = new KubernetesCatalogueProvider(
            () => new BowireKubernetesCatalogueOptions(),
            _ => new HttpClient(),
            env);

        var conn = provider.ResolveConnection(new BowireKubernetesCatalogueOptions());

        Assert.NotNull(conn);
        Assert.Equal("https://10.0.0.1:443", conn!.ApiServerUrl);
        Assert.Equal("in-cluster-token", conn.Token);
        Assert.Equal("bowire", conn.Namespace);
    }

    [Fact]
    public void TryParseKubeconfig_Extracts_Current_Context_Server_And_Token()
    {
        var yaml = """
            apiVersion: v1
            kind: Config
            current-context: dev
            contexts:
            - name: dev
              context:
                cluster: dev-cluster
                user: dev-user
            clusters:
            - name: dev-cluster
              cluster:
                server: https://dev.example.com:6443
            users:
            - name: dev-user
              user:
                token: parsed-token
            """;

        var result = KubernetesCatalogueProvider.TryParseKubeconfig(yaml);

        Assert.NotNull(result);
        Assert.Equal("https://dev.example.com:6443", result!.Value.Server);
        Assert.Equal("parsed-token", result.Value.Token);
    }

    [Fact]
    public void TryParseKubeconfig_Returns_Null_For_Missing_Current_Context()
    {
        var result = KubernetesCatalogueProvider.TryParseKubeconfig("apiVersion: v1\nkind: Config\n");
        Assert.Null(result);
    }
}

/// <summary>Stub environment for the connection-resolution tests.</summary>
internal sealed class StubEnvironment : IKubernetesEnvironment
{
    public Dictionary<string, string> EnvVars { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public string? GetEnvironmentVariable(string name) =>
        EnvVars.TryGetValue(name, out var v) ? v : null;
    public bool FileExists(string path) => Files.ContainsKey(path);
    public string ReadAllText(string path) => Files[path];
}

/// <summary>Mock <see cref="HttpMessageHandler"/> mirroring the core test pattern.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_handler(request, cancellationToken));
}
