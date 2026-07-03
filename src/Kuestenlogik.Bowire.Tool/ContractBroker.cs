// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Minimal Pact Broker REST client (#191). Only two operations —
/// publish a consumer contract and retrieve the latest contract for a
/// provider — over the broker's documented, stable URL scheme. Reused
/// rather than reinvented, per the issue.
/// <para>
/// Every method here reaches the network, so the whole broker path is
/// gated behind an explicit <c>--broker-url</c> on the CLI: outbound
/// calls stay opt-in, never a default.
/// </para>
/// </summary>
internal static class ContractBroker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Publish a consumer contract:
    /// <c>PUT {broker}/pacts/provider/{P}/consumer/{C}/version/{version}</c>.
    /// When <paramref name="tag"/> is set, also tag the consumer version:
    /// <c>PUT {broker}/pacticipants/{C}/versions/{version}/tags/{tag}</c>.
    /// </summary>
    public static async Task PublishAsync(
        HttpClient http, string brokerUrl, PactContract contract, string version, string? tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(contract);

        var baseUri = brokerUrl.TrimEnd('/');
        var provider = Uri.EscapeDataString(contract.Provider.Name);
        var consumer = Uri.EscapeDataString(contract.Consumer.Name);
        var url = $"{baseUri}/pacts/provider/{provider}/consumer/{consumer}/version/{Uri.EscapeDataString(version)}";

        var json = JsonSerializer.Serialize(contract, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.PutAsync(new Uri(url), content, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new ContractBrokerException($"broker rejected the publish ({(int)resp.StatusCode}): {detail}");
        }

        if (!string.IsNullOrEmpty(tag))
        {
            var tagUrl = $"{baseUri}/pacticipants/{consumer}/versions/{Uri.EscapeDataString(version)}/tags/{Uri.EscapeDataString(tag)}";
            using var tagContent = new StringContent("{}", Encoding.UTF8, "application/json");
            using var tagResp = await http.PutAsync(new Uri(tagUrl), tagContent, ct).ConfigureAwait(false);
            if (!tagResp.IsSuccessStatusCode)
            {
                var detail = await SafeReadAsync(tagResp, ct).ConfigureAwait(false);
                throw new ContractBrokerException($"broker rejected the version tag ({(int)tagResp.StatusCode}): {detail}");
            }
        }
    }

    /// <summary>
    /// Retrieve the latest contract for a provider:
    /// <c>GET {broker}/pacts/provider/{P}/latest</c>, or
    /// <c>.../latest/{tag}</c> when <paramref name="tag"/> is set.
    /// Returns the parsed contract, or throws
    /// <see cref="ContractBrokerException"/> on a non-success status.
    /// </summary>
    public static async Task<PactContract> FetchLatestAsync(
        HttpClient http, string brokerUrl, string provider, string? tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var baseUri = brokerUrl.TrimEnd('/');
        var url = string.IsNullOrEmpty(tag)
            ? $"{baseUri}/pacts/provider/{Uri.EscapeDataString(provider)}/latest"
            : $"{baseUri}/pacts/provider/{Uri.EscapeDataString(provider)}/latest/{Uri.EscapeDataString(tag)}";

        using var resp = await http.GetAsync(new Uri(url), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(resp, ct).ConfigureAwait(false);
            throw new ContractBrokerException($"broker returned {(int)resp.StatusCode} for {url}: {detail}");
        }

        var contract = await resp.Content.ReadFromJsonAsync<PactContract>(JsonOpts, ct).ConfigureAwait(false);
        return contract ?? throw new ContractBrokerException("broker returned an empty / unparseable contract.");
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 200 ? body[..200] + "…" : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return resp.ReasonPhrase ?? "(no detail)";
        }
    }
}

/// <summary>Broker interaction failed (non-success status or unparseable body).</summary>
internal sealed class ContractBrokerException : Exception
{
    public ContractBrokerException(string message) : base(message) { }
    public ContractBrokerException(string message, Exception inner) : base(message, inner) { }
    public ContractBrokerException() { }
}
