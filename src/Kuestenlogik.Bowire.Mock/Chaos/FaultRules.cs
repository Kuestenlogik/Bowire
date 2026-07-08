// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Mock.Chaos;

/// <summary>
/// Per-method fault-injection rules (#170) — the structured successor to
/// the global <see cref="ChaosOptions"/> knobs. A rule pairs a method
/// matcher with a latency shape and a fault kind; the first enabled rule
/// whose matcher hits a request handles it. Default off: an empty rule
/// set injects nothing, and <see cref="ChaosOptions"/> keeps working
/// unchanged next to this.
/// </summary>
/// <remarks>
/// Sidecar file shape (<c>mock-faults.json</c>, CLI <c>--faults</c>):
/// <code>
/// {
///   "rules": [
///     { "method": "UserService/*", "kind": "error", "rate": 0.25, "errorStatusCode": 503 },
///     { "method": "*/Download", "kind": "partial-response", "partialBytes": 512 },
///     { "kind": "latency-only", "latency": { "distribution": "normal", "meanMs": 200, "stdDevMs": 50 } }
///   ]
/// }
/// </code>
/// </remarks>
public sealed class FaultRuleSet
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Rules in declaration order; first enabled match wins. Settable so
    /// the management endpoint can swap the live rule list on a running
    /// mock — reference assignment is atomic, in-flight requests keep
    /// the list they already picked up.
    /// </summary>
    [JsonPropertyName("rules")]
    public IReadOnlyList<FaultRule> Rules { get; set; } = Array.Empty<FaultRule>();

    /// <summary>True when at least one rule can fire.</summary>
    [JsonIgnore]
    public bool IsActive => Rules.Any(r => r.Enabled);

    /// <summary>
    /// First enabled non-<c>onMiss</c> rule matching the step's service/method,
    /// or null. <c>onMiss</c> rules never fire on a matched step — they're for
    /// unmatched requests (see <see cref="FirstMissMatch"/>).
    /// </summary>
    public FaultRule? FirstMatch(string? service, string? method)
        => Rules.FirstOrDefault(r => r.Enabled && !r.OnMiss && r.MatchesMethod(service, method));

    /// <summary>First enabled <c>onMiss</c> rule (#411), applied to requests that matched no stub.</summary>
    public FaultRule? FirstMissMatch()
        => Rules.FirstOrDefault(r => r.Enabled && r.OnMiss);

    /// <summary>
    /// Serialize in the exact <c>mock-faults.json</c> shape
    /// <see cref="LoadJson"/> accepts (kebab-case enums), indented for
    /// the UI editor — so GET → edit → PUT round-trips byte-stable.
    /// </summary>
    public string ToJson()
        => JsonSerializer.Serialize(this, WriteOpts);

    private static readonly JsonSerializerOptions WriteOpts = new(JsonOpts) { WriteIndented = true };

    /// <summary>
    /// Parse a <c>mock-faults.json</c> document. Throws
    /// <see cref="FormatException"/> with a pointed message on structural
    /// or semantic problems so CLI users see the offending rule early.
    /// <paramref name="allowEmpty"/> permits an empty rules array — the
    /// management endpoint uses that to clear a running mock's rules,
    /// while the CLI keeps rejecting a pointless sidecar file.
    /// </summary>
    public static FaultRuleSet LoadJson(string json, bool allowEmpty = false)
    {
        FaultRuleSet? set;
        try
        {
            set = JsonSerializer.Deserialize<FaultRuleSet>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"mock-faults: invalid JSON — {ex.Message}", ex);
        }
        if (set is null || set.Rules.Count == 0)
        {
            if (!allowEmpty)
            {
                throw new FormatException("mock-faults: document has no rules (expected { \"rules\": [ … ] }).");
            }
            return set ?? new FaultRuleSet();
        }
        for (var i = 0; i < set.Rules.Count; i++)
        {
            set.Rules[i].Validate($"rules[{i}]");
        }
        return set;
    }
}

/// <summary>
/// Uniform randomness for chaos decisions, built on
/// <see cref="System.Security.Cryptography.RandomNumberGenerator"/>.
/// Chaos jitter is not a security boundary — the crypto source is used
/// because it satisfies analyzer CA5394 without a suppression, and the
/// per-request cost is irrelevant next to an injected delay.
/// </summary>
public static class FaultRandom
{
    private const int Buckets = 1 << 30;

    /// <summary>Uniform double in [0, 1).</summary>
    public static double NextDouble()
        => System.Security.Cryptography.RandomNumberGenerator.GetInt32(Buckets) / (double)Buckets;
}

/// <summary>What a matched rule does to the request (beyond its latency shape).</summary>
public enum FaultKind
{
    /// <summary>Delay only — the replayer still serves the full response.</summary>
    LatencyOnly,
    /// <summary>Short-circuit with <see cref="FaultRule.ErrorStatusCode"/> before the replayer runs.</summary>
    Error,
    /// <summary>Serve only the first <see cref="FaultRule.PartialBytes"/> response-body bytes, then end the response cleanly.</summary>
    PartialResponse,
    /// <summary>Serve <see cref="FaultRule.PartialBytes"/> bytes (0 = nothing), then abort the TCP connection mid-response.</summary>
    ConnectionDrop,
    /// <summary>Emit <see cref="FaultRule.PartialBytes"/> bytes of garbage under a JSON content-type — the recorded body is never written, so the client's parse fails on nonsense (#411, the analog of WireMock's MALFORMED_RESPONSE_CHUNK).</summary>
    MalformedResponse,
}

/// <summary>One fault rule: method matcher + latency shape + fault behaviour.</summary>
public sealed class FaultRule
{
    private Regex? _methodPattern;

    /// <summary>Toggle without deleting the rule. Default on — presence in the file opts in.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// <c>Service/Method</c> glob, <c>*</c> spanning any run of characters
    /// (e.g. <c>UserService/*</c>, <c>*/Get*</c>). Null / empty / <c>*</c>
    /// matches every method. Case-insensitive.
    /// </summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>
    /// Probability the <see cref="Kind"/> fires per matched request
    /// (0..1, default 1). The latency shape applies on every match
    /// regardless — latency models a slow dependency, the rate models an
    /// intermittent failure.
    /// </summary>
    [JsonPropertyName("rate")]
    public double Rate { get; init; } = 1.0;

    /// <summary>Fault behaviour. Default <see cref="FaultKind.LatencyOnly"/>.</summary>
    [JsonPropertyName("kind")]
    public FaultKind Kind { get; init; } = FaultKind.LatencyOnly;

    /// <summary>
    /// #411: when true, this rule applies to requests that matched NO stub
    /// (instead of a matched step) — chaos-testing a client's handling of an
    /// unknown/failing endpoint. The <see cref="Method"/> glob is ignored for
    /// <c>onMiss</c> rules (a miss has no service/method); the first enabled
    /// <c>onMiss</c> rule wins. Meaningful kinds: latency-only, error,
    /// connection-drop, malformed-response (partial-response needs a body).
    /// </summary>
    [JsonPropertyName("onMiss")]
    public bool OnMiss { get; init; }

    /// <summary>Status returned by <see cref="FaultKind.Error"/> hits. Default 503.</summary>
    [JsonPropertyName("errorStatusCode")]
    public int ErrorStatusCode { get; init; } = 503;

    /// <summary>
    /// Response-body bytes forwarded before a
    /// <see cref="FaultKind.PartialResponse"/> truncates or a
    /// <see cref="FaultKind.ConnectionDrop"/> aborts. Default 1024.
    /// </summary>
    [JsonPropertyName("partialBytes")]
    public int PartialBytes { get; init; } = 1024;

    /// <summary>Latency to inject on every match. Null → no delay.</summary>
    [JsonPropertyName("latency")]
    public FaultLatency? Latency { get; init; }

    /// <summary>Case-insensitive glob match against <c>service/method</c>.</summary>
    public bool MatchesMethod(string? service, string? method)
    {
        if (string.IsNullOrEmpty(Method) || Method == "*") return true;
        _methodPattern ??= new Regex(
            "^" + Regex.Escape(Method).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var candidate = $"{service}/{method}";
        return _methodPattern.IsMatch(candidate);
    }

    internal void Validate(string where)
    {
        if (Rate is < 0 or > 1)
            throw new FormatException($"mock-faults: {where}.rate must be between 0 and 1, got {Rate}.");
        if (ErrorStatusCode is < 100 or > 599)
            throw new FormatException($"mock-faults: {where}.errorStatusCode must be a valid HTTP status, got {ErrorStatusCode}.");
        if (PartialBytes < 0)
            throw new FormatException($"mock-faults: {where}.partialBytes must be >= 0, got {PartialBytes}.");
        Latency?.Validate(where + ".latency");
    }

    /// <summary>One-line description for the audit trail ("error 503 @ rate 0.25 + uniform 100-500ms").</summary>
    public string Describe()
    {
        var kind = Kind switch
        {
            FaultKind.Error => $"error {ErrorStatusCode}",
            FaultKind.PartialResponse => $"partial-response after {PartialBytes}B",
            FaultKind.ConnectionDrop => $"connection-drop after {PartialBytes}B",
            FaultKind.MalformedResponse => $"malformed-response {PartialBytes}B",
            _ => "latency-only",
        };
        var missTag = OnMiss ? "on-miss " : "";
        var rate = Rate < 1 && Kind != FaultKind.LatencyOnly ? $" @ rate {Rate}" : "";
        var latency = Latency is null ? "" : $" + {Latency.Describe()}";
        return missTag + kind + rate + latency;
    }
}

/// <summary>
/// Latency shape: <c>fixed</c>, <c>uniform</c>, <c>normal</c>
/// (Box-Muller), or <c>exponential</c>. Samples clamp to
/// [0, <see cref="CapMs"/>] so a fat tail can't hang a request forever.
/// </summary>
public sealed class FaultLatency
{
    /// <summary>Hard ceiling on any sampled delay.</summary>
    public const int CapMs = 120_000;

    /// <summary><c>fixed</c> | <c>uniform</c> | <c>normal</c> | <c>exponential</c>.</summary>
    [JsonPropertyName("distribution")]
    public string Distribution { get; init; } = "fixed";

    /// <summary>Delay for <c>fixed</c>.</summary>
    [JsonPropertyName("valueMs")]
    public int ValueMs { get; init; }

    /// <summary>Lower bound for <c>uniform</c>.</summary>
    [JsonPropertyName("minMs")]
    public int MinMs { get; init; }

    /// <summary>Upper bound for <c>uniform</c>, inclusive.</summary>
    [JsonPropertyName("maxMs")]
    public int MaxMs { get; init; }

    /// <summary>Mean for <c>normal</c> / <c>exponential</c>.</summary>
    [JsonPropertyName("meanMs")]
    public int MeanMs { get; init; }

    /// <summary>Standard deviation for <c>normal</c>.</summary>
    [JsonPropertyName("stdDevMs")]
    public int StdDevMs { get; init; }

    internal void Validate(string where)
    {
        switch (Distribution)
        {
            case "fixed" when ValueMs < 0:
                throw new FormatException($"mock-faults: {where}.valueMs must be >= 0.");
            case "fixed":
                break;
            case "uniform" when MinMs < 0 || MaxMs < MinMs:
                throw new FormatException($"mock-faults: {where} uniform needs 0 <= minMs <= maxMs.");
            case "uniform":
                break;
            case "normal" when MeanMs < 0 || StdDevMs < 0:
                throw new FormatException($"mock-faults: {where} normal needs meanMs >= 0 and stdDevMs >= 0.");
            case "normal":
                break;
            case "exponential" when MeanMs <= 0:
                throw new FormatException($"mock-faults: {where} exponential needs meanMs > 0.");
            case "exponential":
                break;
            default:
                throw new FormatException(
                    $"mock-faults: {where}.distribution '{Distribution}' unknown (fixed | uniform | normal | exponential).");
        }
    }

    /// <summary>
    /// Draw one delay from the configured distribution, clamped to
    /// [0, <see cref="CapMs"/>]. <paramref name="uniform01"/> supplies
    /// uniform doubles in [0,1) — production passes
    /// <see cref="FaultRandom.NextDouble"/>, tests pass a canned sequence
    /// for exact assertions.
    /// </summary>
    public int SampleMs(Func<double> uniform01)
    {
        ArgumentNullException.ThrowIfNull(uniform01);
        double sample = Distribution switch
        {
            "uniform" => MinMs + Math.Floor(uniform01() * (MaxMs - MinMs + 1)),
            "normal" => SampleNormal(uniform01),
            "exponential" => -MeanMs * Math.Log(1.0 - uniform01()),
            _ => ValueMs,
        };
        return (int)Math.Clamp(sample, 0, CapMs);
    }

    private double SampleNormal(Func<double> uniform01)
    {
        // Box-Muller — two uniforms → one standard normal.
        var u1 = 1.0 - uniform01();
        var u2 = uniform01();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return MeanMs + StdDevMs * z;
    }

    /// <summary>Compact human description ("uniform 100-500ms").</summary>
    public string Describe() => Distribution switch
    {
        "uniform" => $"uniform {MinMs}-{MaxMs}ms",
        "normal" => $"normal μ{MeanMs}ms σ{StdDevMs}ms",
        "exponential" => $"exponential μ{MeanMs}ms",
        _ => $"fixed {ValueMs}ms",
    };
}
