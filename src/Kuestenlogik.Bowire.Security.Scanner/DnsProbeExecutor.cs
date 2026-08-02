// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using DnsClient;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// One resolved DNS response, flattened to the two things a translated
/// Nuclei matcher can see: the response code and the answer records.
/// </summary>
public sealed class DnsProbeAnswer
{
    /// <summary>DNS RCODE — 0 NOERROR, 3 NXDOMAIN, … Surfaces as
    /// <see cref="AttackProbeResponse.Status"/>, so a template's
    /// <c>type: status</c> matcher reads the rcode rather than an HTTP status.</summary>
    public int ResponseCode { get; init; }

    /// <summary>Answer-section records, one pre-rendered line each
    /// (<c>name. ttl CLASS TYPE rdata</c>, the shape `dig` prints).</summary>
    public IReadOnlyList<string> Answers { get; init; } = [];
}

/// <summary>
/// The seam between the scan loop and a real resolver. Exists so the DNS
/// branch of <c>bowire scan</c> can be tested against fixed answers: a
/// test that has to reach a live nameserver tests the network, and the
/// thing worth pinning here is the translation from answer to verdict.
/// </summary>
public interface IDnsAnswerSource
{
    /// <param name="name">Fully-qualified name to resolve.</param>
    /// <param name="recordType">A / AAAA / CNAME / TXT / MX / NS / SOA / PTR / …</param>
    /// <param name="ct">Cancels the in-flight query.</param>
    Task<DnsProbeAnswer> QueryAsync(string name, string recordType, CancellationToken ct);
}

/// <summary>
/// Default <see cref="IDnsAnswerSource"/> — DnsClient.NET against the
/// system resolvers, or an explicit server when the operator names one.
/// </summary>
public sealed class DnsClientAnswerSource : IDnsAnswerSource
{
    private readonly LookupClient _client;

    /// <param name="timeoutSeconds">Per-query timeout. The scan's own
    /// <c>--timeout</c> flows in here so a black-holed nameserver cannot
    /// stall the whole run.</param>
    /// <param name="server">Optional resolver address. Null uses the
    /// machine's configured nameservers.</param>
    public DnsClientAnswerSource(int timeoutSeconds = 10, string? server = null)
    {
        var options = string.IsNullOrWhiteSpace(server)
            ? new LookupClientOptions()
            : new LookupClientOptions(System.Net.IPAddress.Parse(server));

        options.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        // One shot per query: the scan loop already reports failures per
        // template, and a silent internal retry doubles the wall-clock of
        // a corpus run against an unreachable resolver.
        options.Retries = 0;
        // A truncated UDP answer that silently stayed truncated would drop
        // records the matcher is looking for, which reads as "not
        // vulnerable" — the one wrong answer to give.
        options.UseTcpFallback = true;
        _client = new LookupClient(options);
    }

    public async Task<DnsProbeAnswer> QueryAsync(string name, string recordType, CancellationToken ct)
    {
        var type = DnsProbeExecutor.ParseRecordType(recordType);
        var result = await _client.QueryAsync(name, type, cancellationToken: ct).ConfigureAwait(false);

        return new DnsProbeAnswer
        {
            ResponseCode = (int)result.Header.ResponseCode,
            Answers = result.Answers
                .Select(r => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{r.DomainName} {r.TimeToLive} {r.RecordClass} {r.RecordType} {DnsProbeExecutor.RenderRecordData(r)}"))
                .ToList(),
        };
    }
}

/// <summary>
/// Runs a translated Nuclei <c>dns:</c> template (#491, #35 Phase 2g).
/// </summary>
/// <remarks>
/// <para>
/// The recording step carries the query: <see cref="BowireRecordingStep.Service"/>
/// is the name to resolve and <see cref="BowireRecordingStep.Method"/> the
/// record type, as written by <c>NucleiTemplateConverter</c>.
/// </para>
/// <para>
/// <b>What the matcher sees.</b> <see cref="AttackProbeResponse.Body"/>
/// carries the <b>answer section only</b>, one record per line. That is a
/// deliberate narrowing of Nuclei, whose DNS matchers can address
/// <c>question</c> / <c>authority</c> / <c>additional</c> / <c>raw</c>
/// separately while Bowire's predicate model has a single body. Rendering
/// the whole response instead would let a word match the echoed question
/// section — and since the question contains the very name the template
/// asked for, templates like "does this CNAME point at
/// <c>{{FQDN}}</c>-something" would fire on every lookup. Refusing to
/// widen costs a detection; widening invents one, which is the same trade
/// <c>NucleiMatcherTranslator</c> already makes for dropped conjuncts.
/// </para>
/// </remarks>
public static class DnsProbeExecutor
{
    /// <summary>
    /// Resolve the step's query and shape the answer into the response the
    /// predicate evaluator consumes.
    /// </summary>
    public static async Task<AttackProbeResponse> ExecuteAsync(
        BowireRecordingStep probe,
        IDnsAnswerSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(source);

        var name = (probe.Service ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("DNS template carries no name to resolve.");
        }

        // An unresolved placeholder means the template wanted a variable the
        // scan never bound (no --target, or a {{…}} we do not substitute).
        // Querying "{{FQDN}}" literally would come back NXDOMAIN and read as
        // a clean "not vulnerable", which is worse than saying so.
        if (name.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"DNS template name still holds an unresolved placeholder ({name}) — bind a target so it can be substituted.");
        }

        var recordType = string.IsNullOrWhiteSpace(probe.Method) ? "A" : probe.Method.Trim();

        var sw = Stopwatch.StartNew();
        var answer = await source.QueryAsync(name, recordType, ct).ConfigureAwait(false);
        sw.Stop();

        return new AttackProbeResponse
        {
            Status = answer.ResponseCode,
            Body = string.Join('\n', answer.Answers),
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Map a Nuclei record-type string onto DnsClient's query type.
    /// Throws rather than silently falling back to <c>A</c>: a template
    /// asking for TXT and being answered with A records would be judged
    /// against the wrong data.
    /// </summary>
    public static QueryType ParseRecordType(string recordType)
    {
        if (string.IsNullOrWhiteSpace(recordType)) return QueryType.A;
        if (Enum.TryParse<QueryType>(recordType.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        throw new InvalidOperationException(
            $"Unsupported DNS record type '{recordType}'. Known types: {string.Join(", ", Enum.GetNames<QueryType>().Take(20))}, …");
    }

    /// <summary>
    /// Render one record's rdata. DnsClient's own <c>ToString()</c> prefixes
    /// the full header again, which would repeat the name on every line and
    /// give <c>word</c> matchers a second place to hit it.
    /// </summary>
    internal static string RenderRecordData(DnsClient.Protocol.DnsResourceRecord record)
    {
        var full = record.ToString();
        // DnsClient renders "<name> <ttl> <class> <type> <rdata>"; strip the
        // header it shares with our own prefix and keep the payload.
        var parts = full.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 ? parts[4] : full;
    }
}
