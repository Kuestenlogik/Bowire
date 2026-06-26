// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Catalogue.Kubernetes;

/// <summary>
/// Options for <see cref="KubernetesCatalogueProvider"/> (#305 Phase D).
/// Bound from <c>Bowire:Discovery:Catalogue:Kubernetes</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three sources of API-server coordinates are tried in order:
/// </para>
/// <list type="number">
///   <item>
///     Explicit <see cref="ApiServerUrl"/> + <see cref="Token"/> from
///     this options block — useful for tests, sidecars, or any host
///     that already has a bearer token in hand.
///   </item>
///   <item>
///     In-cluster service account: <c>/var/run/secrets/kubernetes.io/serviceaccount/{token,ca.crt,namespace}</c>
///     paired with the well-known <c>KUBERNETES_SERVICE_HOST</c> /
///     <c>KUBERNETES_SERVICE_PORT</c> env-vars. Picked up automatically
///     when Bowire runs inside a pod.
///   </item>
///   <item>
///     Mounted kubeconfig at <see cref="KubeconfigPath"/> (or the
///     standard <c>$KUBECONFIG</c> / <c>~/.kube/config</c> fallback).
///     Only the current-context's <c>cluster.server</c> +
///     <c>user.token</c> are read — full kubeconfig parsing (exec
///     plugins, client-cert auth, multiple contexts) is out of scope
///     for v1; operators that need that path stand up a relay or
///     supply an explicit token.
///   </item>
/// </list>
/// <para>
/// TLS verification against the cluster's CA is on by default. Set
/// <see cref="SkipTlsVerification"/> to <c>true</c> only for local
/// kind / k3d / minikube test clusters with self-signed certs.
/// </para>
/// </remarks>
public sealed class BowireKubernetesCatalogueOptions
{
    /// <summary>
    /// Explicit API-server URL — e.g. <c>"https://kubernetes.default.svc"</c>
    /// or <c>"https://10.0.0.1:6443"</c>. When null the provider
    /// derives the URL from in-cluster env-vars or the kubeconfig
    /// current-context, in that order.
    /// </summary>
    public string? ApiServerUrl { get; set; }

    /// <summary>
    /// Bearer token sent in the <c>Authorization</c> header. When
    /// null the provider falls back to the in-cluster service-account
    /// token file or the kubeconfig user token.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Path to a kubeconfig file. When null the provider falls back
    /// to the <c>KUBECONFIG</c> env-var, then to <c>~/.kube/config</c>.
    /// Only consulted when neither <see cref="ApiServerUrl"/> nor the
    /// in-cluster service-account path resolves.
    /// </summary>
    public string? KubeconfigPath { get; set; }

    /// <summary>
    /// Namespace to query. Defaults to <c>"default"</c> when running
    /// outside a pod; when running in-cluster the provider falls back
    /// to the pod's namespace from
    /// <c>/var/run/secrets/kubernetes.io/serviceaccount/namespace</c>
    /// if this is null.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Optional label selector — the standard k8s comma-separated
    /// <c>key=value,key2=value2</c> syntax. Forwarded verbatim as
    /// the <c>labelSelector</c> query parameter. Useful for scoping
    /// the catalogue to services tagged for Bowire discovery (e.g.
    /// <c>"bowire/discoverable=true"</c>).
    /// </summary>
    public string? LabelSelector { get; set; }

    /// <summary>
    /// URL scheme to use when materialising the per-service URL.
    /// Defaults to <c>"http"</c> — k8s Services don't carry the
    /// scheme intrinsically. Set to <c>"https"</c> for TLS-fronted
    /// in-cluster services.
    /// </summary>
    public string Scheme { get; set; } = "http";

    /// <summary>
    /// PEM-encoded CA bundle used to verify the API server's TLS
    /// certificate. When null the provider falls back to
    /// <c>/var/run/secrets/kubernetes.io/serviceaccount/ca.crt</c>
    /// (in-cluster path) and finally to the OS trust store.
    /// </summary>
    public string? CaCertificatePem { get; set; }

    /// <summary>
    /// Skip TLS verification against the cluster CA. Defaults to
    /// <c>false</c>. ONLY enable for local test clusters with
    /// self-signed certs — production clusters should always verify.
    /// </summary>
    public bool SkipTlsVerification { get; set; }

    /// <summary>
    /// Per-fetch timeout. Defaults to 10 s, mirroring the http /
    /// consul providers in core.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
