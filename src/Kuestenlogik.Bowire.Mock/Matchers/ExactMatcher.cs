// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Mock.Matchers;

/// <summary>
/// Default matcher for the Phase-1 / Phase-2 mock. Handles two protocol
/// families:
/// <list type="bullet">
/// <item>
/// <strong>REST</strong> — matches recorded steps by <c>(httpVerb, httpPath)</c>.
/// The path is either a literal (<c>/weather</c>, exact string match) or
/// an OpenAPI-style template (<c>/users/{id}</c>, each <c>{name}</c>
/// segment binds to one path segment). Verb case-insensitive, literal-path
/// case-sensitive per the HTTP spec.
/// </item>
/// <item>
/// <strong>gRPC</strong> — matches requests carrying an
/// <c>application/grpc</c>-family content type by the
/// <c>/{service}/{method}</c> URL path.
/// </item>
/// </list>
/// Non-unary steps are always skipped. When several recorded steps share
/// the same verb + path template (e.g. three captures of
/// <c>GET /pet/{petId}</c> with <c>petId</c> = 3, 5, 10), the matcher
/// scores each candidate by how well its recorded request body matches the
/// incoming path-bindings and picks the best hit — so a mock call against
/// <c>/pet/5</c> returns the response for <c>petId = 5</c> instead of
/// always handing back the first capture. Ties (and the historical
/// single-template path) keep the original "first match wins" order.
/// Phase 2 later adds a topic matcher for MQTT / Socket.IO wildcards as a
/// separate <see cref="IMockMatcher"/> implementation.
/// </summary>
public sealed class ExactMatcher : IMockMatcher
{
    // Cache compiled template regexes by template string so the matcher
    // doesn't recompile on every TryMatch. Concurrent because the matcher
    // is shared across every incoming request.
    private static readonly ConcurrentDictionary<string, Regex> s_templateCache = new(StringComparer.Ordinal);

    // Score handed back for a non-template match (literal path equality or
    // a gRPC/Socket.IO candidate where the matcher has no further axis to
    // rank on). Higher than any plausible body-binding count so a literal
    // hit short-circuits before the matcher inspects the rest of the list,
    // preserving the original "first literal match wins" contract.
    private const int LiteralOrNonRestMatchScore = 1_000_000;

    // Weight of an explicit stub priority (#402). Larger than any base score
    // so a declared priority dominates the implicit literal-beats-template /
    // body-binding ranking. `long` arithmetic keeps priorities beyond ±2 from
    // overflowing.
    private const long PriorityMultiplier = 1_000_000_000L;

    public bool TryMatch(MockRequest request, BowireRecording recording, out BowireRecordingStep matchedStep)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recording);

        BowireRecordingStep? bestStep = null;
        var bestScore = long.MinValue;

        foreach (var candidate in recording.Steps)
        {
            int baseScore;
            // Skip protocol-shaped steps that don't match the request family.
            // Replay-ability (unary vs streaming, sent-messages for duplex) is
            // the replayer's concern — here we only pair incoming wire shape
            // with recorded wire shape.
            if (request.IsGrpc)
            {
                if (!IsGrpcStep(candidate)) continue;
                if (!MatchesGrpcPath(candidate, request)) continue;
                baseScore = LiteralOrNonRestMatchScore;
            }
            else if (IsSocketIoStep(candidate))
            {
                // Socket.IO recordings carry no HTTP routing metadata
                // (the plugin opens a WebSocket under the hood and
                // doesn't surface a path). Match by wire shape
                // instead: any GET upgrade on /socket.io/* pairs
                // against the first socketio step in the recording.
                if (!MatchesSocketIoRequest(request)) continue;
                baseScore = LiteralOrNonRestMatchScore;
            }
            else
            {
                if (!IsRestStep(candidate)) continue;
                if (!MatchesRestVerbAndPath(candidate, request)) continue;
                // #402: the optional query / header / cookie predicates all
                // have to pass or the step isn't a candidate at all.
                if (candidate.Match is { } m && !MockMatchPredicates.AllPredicatesPass(m, request)) continue;
                baseScore = ScoreRestCandidate(candidate, request);
            }

            // #402: an explicit stub priority dominates the implicit
            // literal-beats-template / body-binding heuristic; ties keep the
            // capture order (strict >, first match wins).
            var score = (long)(candidate.Match?.Priority ?? 0) * PriorityMultiplier + baseScore;
            if (score > bestScore)
            {
                bestStep = candidate;
                bestScore = score;
            }
        }

        if (bestStep is not null)
        {
            matchedStep = bestStep;
            return true;
        }

        matchedStep = null!;
        return false;
    }

    /// <summary>
    /// Rank a REST candidate that already passed verb + path-or-template
    /// matching. Literal-path hits always outrank template hits (no body
    /// inspection needed). For template hits, the score counts how many of
    /// the captured path bindings (e.g. <c>petId = 5</c>) line up with the
    /// recorded request body — a step recorded against <c>petId = 5</c>
    /// outranks a sibling recorded against <c>petId = 3</c> when the
    /// incoming request asks for pet 5. Missing / unparseable bodies score
    /// zero and the historical "first match wins" tie-break takes over.
    /// </summary>
    private static int ScoreRestCandidate(BowireRecordingStep step, MockRequest request)
    {
        var template = step.HttpPath;
        // A literal path, or a step whose path came from a #402 regex / glob
        // pattern (no httpPath template), is a strong exact hit with no further
        // body-binding signal to rank on.
        if (string.IsNullOrEmpty(template) || !IsTemplate(template))
        {
            // Literal-path equality already filtered out non-matches in
            // MatchesRestVerbAndPath; everyone here is an exact hit.
            return LiteralOrNonRestMatchScore;
        }

        var bindings = ExtractTemplateBindings(template, request.Path);
        if (bindings is null || bindings.Count == 0) return 0;
        if (string.IsNullOrWhiteSpace(step.Body)) return 0;

        Dictionary<string, string>? bodyValues;
        try
        {
            bodyValues = ParseBodyStringValues(step.Body!);
        }
        catch (JsonException)
        {
            return 0;
        }

        if (bodyValues is null || bodyValues.Count == 0) return 0;

        var matched = 0;
        foreach (var (name, value) in bindings)
        {
            if (bodyValues.TryGetValue(name, out var recorded)
                && string.Equals(recorded, value, StringComparison.Ordinal))
            {
                matched++;
            }
        }
        return matched;
    }

    /// <summary>
    /// Flatten a step body into a name → string-form map. The recorder
    /// keeps the captured input as the JSON the user submitted (e.g.
    /// <c>{"petId":3}</c>); we coerce primitive values to their canonical
    /// text form so the matcher can compare them against URL-decoded path
    /// segments without caring whether the user typed a number or a string.
    /// </summary>
    private static Dictionary<string, string>? ParseBodyStringValues(string bodyJson)
    {
        using var doc = JsonDocument.Parse(bodyJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var v = prop.Value;
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    map[prop.Name] = v.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    map[prop.Name] = v.GetRawText();
                    break;
                default:
                    // Arrays / objects don't appear in path bindings — skip.
                    break;
            }
        }
        return map;
    }

    // A REST step needs a verb plus SOME path source: the classic httpPath
    // literal/template, or (#402) a regex / glob path pattern on its match.
    private static bool IsRestStep(BowireRecordingStep s) =>
        !string.IsNullOrEmpty(s.HttpVerb) &&
        (!string.IsNullOrEmpty(s.HttpPath)
         || !string.IsNullOrEmpty(s.Match?.PathRegex)
         || !string.IsNullOrEmpty(s.Match?.PathGlob));

    private static bool IsGrpcStep(BowireRecordingStep s) =>
        string.Equals(s.Protocol, "grpc", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(s.Service) &&
        !string.IsNullOrEmpty(s.Method);

    private static bool IsSocketIoStep(BowireRecordingStep s) =>
        string.Equals(s.Protocol, "socketio", StringComparison.OrdinalIgnoreCase);

    // Socket.IO servers conventionally live at /socket.io/. Clients (at
    // least the JavaScript, Python, and SocketIOClient .NET libraries)
    // always GET that path to open the engine.io session. Matching on
    // path-prefix + GET is enough; the query string carries the
    // EIO/transport details the replayer cares about.
    private static bool MatchesSocketIoRequest(MockRequest request) =>
        string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
        request.Path.StartsWith("/socket.io/", StringComparison.Ordinal);

    private static bool MatchesRestVerbAndPath(BowireRecordingStep step, MockRequest request)
    {
        if (!string.Equals(step.HttpVerb, request.HttpMethod, StringComparison.OrdinalIgnoreCase))
            return false;

        // #402: a regex / glob path pattern on the match overrides the
        // httpPath template for path matching (a single stub answers a family
        // of paths). Regex wins over glob when both are set.
        var m = step.Match;
        if (!string.IsNullOrEmpty(m?.PathRegex))
            return MockMatchPredicates.PathRegexMatches(m.PathRegex, request.Path);
        if (!string.IsNullOrEmpty(m?.PathGlob))
            return MockMatchPredicates.PathGlobMatches(m.PathGlob, request.Path);

        var template = step.HttpPath;
        if (string.IsNullOrEmpty(template)) return false;

        // Fast path: literal templates match via string equality. Case-sensitive
        // per the HTTP spec. This also covers paths that happen to contain a
        // brace but no closing brace — those fall through as literals.
        if (!IsTemplate(template))
            return string.Equals(template, request.Path, StringComparison.Ordinal);

        // Slow path: compiled regex. Each {name} binds to one path segment
        // ([^/]+). The full path must match end-to-end.
        var regex = s_templateCache.GetOrAdd(template, CompileTemplate);
        return regex.IsMatch(request.Path);
    }

    /// <summary>
    /// Extract named captures from a matched template. Returns
    /// <c>null</c> when <paramref name="template"/> has no
    /// placeholders (nothing to bind) or doesn't match the path (caller
    /// got here via a different matcher). Uses the same cached regex
    /// as <see cref="MatchesRestVerbAndPath"/> so hot paths don't pay
    /// for a second compile.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ExtractTemplateBindings(string template, string path)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(path);
        if (!IsTemplate(template)) return null;

        var regex = s_templateCache.GetOrAdd(template, CompileTemplate);
        var match = regex.Match(path);
        if (!match.Success) return null;

        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in regex.GetGroupNames())
        {
            // Group 0 is the whole match — skip. Named groups only.
            if (int.TryParse(name, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                continue;
            }
            var group = match.Groups[name];
            if (group.Success) bindings[name] = group.Value;
        }
        return bindings;
    }

    // gRPC URL form is always /{package.Service}/{Method} — service name is
    // the fully-qualified protobuf service, method is the RPC method name.
    // Match on the full path rather than split segments so variants like
    // '/pkg.v1.Svc/M' (package with dots) work without extra parsing.
    private static bool MatchesGrpcPath(BowireRecordingStep step, MockRequest request) =>
        string.Equals(request.Path, "/" + step.Service + "/" + step.Method, StringComparison.Ordinal);

    private static bool IsTemplate(string path)
    {
        var open = path.IndexOf('{');
        if (open < 0) return false;
        // Require a closing brace after the open, in the same segment. Defensive
        // against paths that contain literal '{' for some reason — without a
        // matching '}', we treat the whole path as literal.
        return path.IndexOf('}', open + 1) > open;
    }

    // Build a regex from an OpenAPI-style template:
    //   /users/{id}            → ^/users/(?<id>[^/]+)$
    //   /users/{id}/posts/{p}  → ^/users/(?<id>[^/]+)/posts/(?<p>[^/]+)$
    //
    // Literal segments are Regex-escaped so path characters like '.' don't
    // turn into regex metacharacters. Placeholders with duplicate names are
    // allowed; the last binding wins — we don't enforce uniqueness because
    // OpenAPI specs occasionally allow it and this matcher has no stake in
    // rejecting what OpenAPI accepts.
    private static Regex CompileTemplate(string template)
    {
        var pattern = new System.Text.StringBuilder("^");
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                pattern.Append(Regex.Escape(template[i..]));
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                // Unterminated '{' — treat the rest as literal to avoid throwing
                // at compile-time inside a hot matcher path.
                pattern.Append(Regex.Escape(template[i..]));
                break;
            }

            pattern.Append(Regex.Escape(template[i..open]));

            var name = template.Substring(open + 1, close - open - 1);
            // Drop any OpenAPI modifiers like '+' or '*' for now — Phase 2 may
            // add catch-all support; Phase 2a stays segment-scoped.
            var trimmed = name.TrimEnd('+', '*');
            pattern.Append("(?<").Append(Regex.Escape(trimmed)).Append(">[^/]+)");

            i = close + 1;
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
