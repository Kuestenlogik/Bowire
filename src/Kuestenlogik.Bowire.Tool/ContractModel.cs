// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Pact-compatible consumer contract (#191). A deliberately small subset
/// of the Pact v3 specification — enough that a Pact Broker accepts a
/// published document and that <c>bowire contract verify</c> can replay
/// each interaction against a live provider. Serialised camelCase so the
/// wire shape matches what brokers expect
/// (<c>consumer.name</c> / <c>provider.name</c> / <c>interactions[]</c>).
/// </summary>
internal sealed class PactContract
{
    [JsonPropertyName("consumer")]
    public PactParty Consumer { get; set; } = new();

    [JsonPropertyName("provider")]
    public PactParty Provider { get; set; } = new();

    [JsonPropertyName("interactions")]
    public List<PactInteraction> Interactions { get; set; } = [];

    /// <summary>
    /// Pact metadata block. Carries the spec version brokers key off plus
    /// a <c>bowire</c> provenance stamp so a round-tripped contract is
    /// traceable to the recording it came from.
    /// </summary>
    [JsonPropertyName("metadata")]
    public PactMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Build a consumer contract from a captured recording. Only REST /
    /// HTTP steps become interactions — Pact is an HTTP contract format
    /// and brokers reject non-HTTP shapes; other protocols are skipped
    /// and reported by the caller. Each step's request (verb + path +
    /// body + header metadata) and recorded response (status + body)
    /// become one interaction.
    /// </summary>
    public static PactContract FromRecording(BowireRecording recording, string consumer, string provider)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var contract = new PactContract
        {
            Consumer = new PactParty { Name = consumer },
            Provider = new PactParty { Name = provider },
        };

        foreach (var step in recording.Steps)
        {
            if (!IsHttp(step)) continue;

            var reqBody = step.Body ?? (step.Messages.Count > 0 ? step.Messages[0] : null);
            contract.Interactions.Add(new PactInteraction
            {
                Description = string.IsNullOrEmpty(step.Method)
                    ? (step.HttpVerb ?? "GET") + " " + (step.HttpPath ?? "/")
                    : step.Method,
                Request = new PactRequest
                {
                    Method = (step.HttpVerb ?? "GET").ToUpperInvariant(),
                    Path = PathOf(step),
                    Headers = FilterHttpHeaders(step.Metadata),
                    Body = ParseJson(reqBody),
                },
                Response = new PactResponse
                {
                    Status = StatusCodeOf(step.Status),
                    Body = ParseJson(step.Response),
                },
            });
        }

        return contract;
    }

    private static bool IsHttp(BowireRecordingStep step)
        => string.Equals(step.Protocol, "rest", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(step.HttpVerb)
            || !string.IsNullOrEmpty(step.HttpPath);

    /// <summary>
    /// The request path for an interaction. Prefers the captured
    /// <c>HttpPath</c>; falls back to the path component of the step's
    /// <c>ServerUrl</c> so a recording that only stored the full URL
    /// still yields a broker-valid relative path.
    /// </summary>
    internal static string PathOf(BowireRecordingStep step)
    {
        if (!string.IsNullOrEmpty(step.HttpPath))
        {
            return step.HttpPath.StartsWith('/') ? step.HttpPath : "/" + step.HttpPath;
        }
        if (!string.IsNullOrEmpty(step.ServerUrl)
            && Uri.TryCreate(step.ServerUrl, UriKind.Absolute, out var uri))
        {
            return uri.PathAndQuery;
        }
        return "/";
    }

    /// <summary>
    /// Map Bowire's status string to a numeric HTTP code brokers expect.
    /// The recorder stores the <see cref="HttpStatusCode"/> name ("OK",
    /// "NotFound", …); a numeric string ("200") also parses; anything
    /// unrecognised defaults to 200 so a happy-path recording without an
    /// explicit status still produces a valid contract.
    /// </summary>
    internal static int StatusCodeOf(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return 200;
        if (int.TryParse(status, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (Enum.TryParse<HttpStatusCode>(status, ignoreCase: true, out var code)) return (int)code;
        return 200;
    }

    /// <summary>
    /// Keep only real HTTP request headers from a step's metadata bag.
    /// gRPC / protocol metadata leaks pseudo-headers (":path", "grpc-*")
    /// and Bowire-internal keys that a broker / provider shouldn't see.
    /// </summary>
    internal static Dictionary<string, string>? FilterHttpHeaders(IDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in metadata)
        {
            if (string.IsNullOrEmpty(k) || k.StartsWith(':') || k.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase))
                continue;
            headers[k] = v;
        }
        return headers.Count > 0 ? headers : null;
    }

    private static JsonNode? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON body — carry it as a JSON string so the contract
            // stays valid JSON and verify can still compare it.
            return JsonValue.Create(text);
        }
    }
}

internal sealed class PactParty
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed class PactInteraction
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("request")]
    public PactRequest Request { get; set; } = new();

    [JsonPropertyName("response")]
    public PactResponse Response { get; set; } = new();
}

internal sealed class PactRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public JsonNode? Body { get; set; }
}

internal sealed class PactResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; } = 200;

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public JsonNode? Body { get; set; }
}

internal sealed class PactMetadata
{
    [JsonPropertyName("pactSpecification")]
    public PactSpecification PactSpecification { get; set; } = new();

    [JsonPropertyName("bowire")]
    public PactBowireStamp Bowire { get; set; } = new();
}

internal sealed class PactSpecification
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "3.0.0";
}

internal sealed class PactBowireStamp
{
    [JsonPropertyName("generatedBy")]
    public string GeneratedBy { get; set; } = "bowire contract publish";
}
