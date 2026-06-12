// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Soap;

/// <summary>
/// Bowire protocol plugin for SOAP / WSDL 1.1 endpoints. Discovery
/// fetches the WSDL document (either the URL the user typed directly,
/// or the URL with <c>?wsdl</c> appended), parses PortType operations
/// + SOAP bindings into <see cref="BowireServiceInfo"/> entries, and
/// invocation wraps the JSON-form payload in a SOAP envelope before
/// POSTing it to the operation endpoint.
/// </summary>
/// <remarks>
/// <para>
/// SOAP 1.1 is the default wire version; passing <c>soap_version=1.2</c>
/// in the invoke metadata flips both the envelope namespace and the
/// Content-Type header per WS-I BP 1.1.
/// </para>
/// <para>
/// Streaming and channel surfaces aren't part of the SOAP wire — every
/// SOAP operation is request/response. The plugin returns empty
/// streams + null channels accordingly.
/// </para>
/// </remarks>
// CA1001: _http lives as long as the protocol registry (process lifetime).
// Adding IDisposable to IBowireProtocol just to clean up a singleton at
// shutdown isn't worth the ripple through every plugin.
#pragma warning disable CA1001
public sealed class BowireSoapProtocol : IBowireProtocol
#pragma warning restore CA1001
{
    private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Name => "SOAP";
    public string Description => "Legacy SOAP services — WSDL discovery + envelope invoke.";
    public string Id => "soap";

    public void Initialize(IServiceProvider? serviceProvider)
    {
        var config = serviceProvider?.GetService<IConfiguration>();
        _http = BowireHttpClientFactory.Create(config, Id, TimeSpan.FromSeconds(30));
    }

    // Generic SOAP mark — envelope outline with the "SOAP" word inside,
    // no vendor branding. Kept monochrome so it picks up the sidebar's
    // currentColor.
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" width="16" height="16" aria-hidden="true"><path d="M3 6l9 5 9-5"/><rect x="3" y="6" width="18" height="13" rx="1"/><path d="M8 13h8"/><path d="M8 16h5"/></svg>""";

    public IReadOnlyList<BowirePluginSetting> Settings =>
    [
        new("defaultSoapVersion", "Default SOAP version",
            "Envelope namespace used when the WSDL doesn't pin one",
            "string", "1.1"),
    ];

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (!TryResolveWsdlUri(serverUrl, out var wsdlUri)) return [];

        string text;
        try
        {
            using var resp = await _http.GetAsync(wsdlUri, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];
            text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }

        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (System.Xml.XmlException) { return []; }

        return WsdlParser.Parse(doc, serverUrl);
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        if (!TryResolveEndpointUri(serverUrl, out var endpoint))
        {
            return new InvokeResult(null, 0,
                "Could not parse SOAP endpoint URL '" + serverUrl + "'", new());
        }

        // The user can override the wire endpoint via metadata (handy
        // when the WSDL was on host A but the actual service lives on
        // host B — common with on-prem mirrors).
        if (metadata?.TryGetValue("endpoint_url", out var overrideUrl) == true
            && Uri.TryCreate(overrideUrl, UriKind.Absolute, out var overrideUri))
        {
            endpoint = overrideUri;
        }

        var soapVersion = metadata?.GetValueOrDefault("soap_version") ?? "1.1";
        var soapAction = metadata?.GetValueOrDefault("soap_action") ?? "";
        var targetNamespace = metadata?.GetValueOrDefault("target_namespace") ?? "";

        var bodyXml = jsonMessages.FirstOrDefault() ?? "";
        var operation = ExtractOperationName(method);
        var envelope = SoapEnvelopeBuilder.BuildRequestEnvelope(
            operation, targetNamespace, bodyXml, soapVersion);

        using var content = new StringContent(envelope, SoapEnvelopeBuilder.Utf8NoBom);
        content.Headers.ContentType =
            MediaTypeHeaderValue.Parse(SoapEnvelopeBuilder.ContentTypeFor(soapVersion, soapAction));

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        // SOAP 1.1 spec: the SOAPAction header is required (may be empty
        // string, but the header itself must be present). SOAP 1.2 folds
        // it into Content-Type so we leave it off there.
        if (soapVersion != "1.2")
            req.Headers.TryAddWithoutValidation("SOAPAction", "\"" + soapAction + "\"");

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var responseText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = SoapEnvelopeBuilder.ParseResponseEnvelope(responseText);
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            var meta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["http_status"] = ((int)resp.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["soap_version"] = soapVersion,
            };
            if (!string.IsNullOrEmpty(soapAction)) meta["soap_action"] = soapAction;

            // SOAP Faults surface as Status="Fault" + the fault XML in
            // the response field — same shape the JSON-RPC plugin uses
            // for application-level errors.
            var status = parsed.IsFault
                ? "Fault"
                : resp.IsSuccessStatusCode ? "OK" : "HTTP " + (int)resp.StatusCode;

            return new InvokeResult(parsed.Body, elapsedMs, status, meta);
        }
        catch (Exception ex)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            return new InvokeResult(null, elapsedMs, ex.Message, new());
        }
    }

    public IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // SOAP has no streaming primitive — every operation is unary
        // request/response. WS-RM exists but is out of scope.
        return AsyncEnumerable.Empty<string>();
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
        => Task.FromResult<IBowireChannel?>(null);

    // ----- helpers ------------------------------------------------------

    /// <summary>
    /// Resolve the URL to fetch the WSDL from. If the user already
    /// typed a URL ending in <c>?wsdl</c> / <c>.wsdl</c>, take it
    /// verbatim. Otherwise append <c>?wsdl</c> — the most common
    /// convention across .NET/Java SOAP stacks.
    /// </summary>
    internal static bool TryResolveWsdlUri(string serverUrl, out Uri wsdlUri)
    {
        wsdlUri = null!;
        if (string.IsNullOrWhiteSpace(serverUrl)) return false;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not "http" and not "https") return false;

        var hasWsdl = uri.Query.Contains("wsdl", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".wsdl", StringComparison.OrdinalIgnoreCase);
        wsdlUri = hasWsdl
            ? uri
            : new Uri(uri, uri.Query.Length == 0 ? uri.AbsolutePath + "?wsdl" : uri.AbsolutePath + uri.Query + "&wsdl");
        return true;
    }

    /// <summary>
    /// Resolve the URL the invoke POST should hit. We strip <c>?wsdl</c>
    /// off so the same workbench-side URL (the one the user pasted)
    /// works for both discovery and invocation. Callers can still
    /// override via the <c>endpoint_url</c> metadata field.
    /// </summary>
    internal static bool TryResolveEndpointUri(string serverUrl, out Uri endpointUri)
    {
        endpointUri = null!;
        if (string.IsNullOrWhiteSpace(serverUrl)) return false;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not "http" and not "https") return false;

        if (uri.Query.Contains("wsdl", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri) { Query = "" };
            endpointUri = builder.Uri;
            return true;
        }
        endpointUri = uri;
        return true;
    }

    /// <summary>
    /// FullName comes through as <c>PortType/Operation</c> from
    /// discovery. Anything else is treated as a bare operation name
    /// the user typed.
    /// </summary>
    internal static string ExtractOperationName(string method)
    {
        if (string.IsNullOrEmpty(method)) return "";
        var slash = method.LastIndexOf('/');
        return slash >= 0 ? method[(slash + 1)..] : method;
    }
}
