// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
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
/// Non-unary steps are always skipped; the first matching step wins.
/// Phase 2 later adds a topic matcher for MQTT / Socket.IO wildcards as a
/// separate <see cref="IMockMatcher"/> implementation.
/// </summary>
public sealed class ExactMatcher : IMockMatcher
{
    // Cache compiled template regexes by template string so the matcher
    // doesn't recompile on every TryMatch. Concurrent because the matcher
    // is shared across every incoming request.
    private static readonly ConcurrentDictionary<string, Regex> s_templateCache = new(StringComparer.Ordinal);

    public bool TryMatch(MockRequest request, BowireRecording recording, out BowireRecordingStep matchedStep)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recording);

        foreach (var candidate in recording.Steps)
        {
            // Skip protocol-shaped steps that don't match the request family.
            // Replay-ability (unary vs streaming, sent-messages for duplex) is
            // the replayer's concern — here we only pair incoming wire shape
            // with recorded wire shape.
            if (request.IsGrpc)
            {
                if (!IsGrpcStep(candidate)) continue;
                if (!MatchesGrpcPath(candidate, request)) continue;
            }
            else if (IsSocketIoStep(candidate))
            {
                // Socket.IO recordings carry no HTTP routing metadata
                // (the plugin opens a WebSocket under the hood and
                // doesn't surface a path). Match by wire shape
                // instead: any GET upgrade on /socket.io/* pairs
                // against the first socketio step in the recording.
                if (!MatchesSocketIoRequest(request)) continue;
            }
            else
            {
                if (!IsRestStep(candidate)) continue;
                if (!MatchesRestVerbAndPath(candidate, request)) continue;
            }

            matchedStep = candidate;
            return true;
        }

        matchedStep = null!;
        return false;
    }

    private static bool IsRestStep(BowireRecordingStep s) =>
        !string.IsNullOrEmpty(s.HttpPath) &&
        !string.IsNullOrEmpty(s.HttpVerb);

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

        var template = step.HttpPath!;

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
