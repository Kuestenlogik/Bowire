// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>Raised when an auth flow (#190) is misconfigured or fails to yield a token.</summary>
public sealed class AuthFlowException : Exception
{
    public AuthFlowException(string message) : base(message) { }
    public AuthFlowException(string message, Exception inner) : base(message, inner) { }
    public AuthFlowException() { }
}

/// <summary>
/// Runs an <see cref="AuthFlowDefinition"/> headlessly (#190): executes each
/// step, substitutes <c>{{var}}</c> (earlier captures) and <c>{{env.NAME}}</c>
/// (process environment — where secrets live) into the requests, extracts the
/// configured token, and returns a ready-to-inject auth header. No browser, so
/// it covers the CI-relevant grants (client-credentials, password, and any
/// scriptable login → token chain).
/// </summary>
public static class AuthFlowRunner
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly TimeSpan s_regexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Parse a flow definition from JSON.</summary>
    public static AuthFlowDefinition Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<AuthFlowDefinition>(json, s_json)
            ?? throw new AuthFlowException("Auth flow file parsed to null.");
    }

    /// <summary>Load a flow definition from disk.</summary>
    public static AuthFlowDefinition Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Execute the flow and return the captured token + a <c>Name: value</c>
    /// header line to inject. Throws <see cref="AuthFlowException"/> on any
    /// misconfiguration or when no token is captured (fail closed — a scan
    /// against an authenticated API must not silently proceed unauthenticated).
    /// </summary>
    public static async Task<AuthFlowResult> RunAsync(AuthFlowDefinition flow, HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(http);
        if (flow.Steps.Count == 0) throw new AuthFlowException("Auth flow has no steps.");

        var vars = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < flow.Steps.Count; i++)
        {
            var step = flow.Steps[i];
            if (string.IsNullOrWhiteSpace(step.Url))
                throw new AuthFlowException($"Auth flow step {i + 1} has no url.");

            using var req = BuildRequest(step, vars, i);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (step.Capture is { Count: > 0 })
                ApplyCaptures(step.Capture, resp, body, vars, i);
        }

        var tokenVar = ResolveTokenVar(flow, vars);
        var token = vars[tokenVar];
        var headerLine = $"{flow.InjectHeader}: {flow.InjectPrefix}{token}";
        return new AuthFlowResult(token, headerLine, vars);
    }

    private static HttpRequestMessage BuildRequest(AuthStep step, Dictionary<string, string> vars, int index)
    {
        var url = Substitute(step.Url, vars, index);
        var method = new HttpMethod(string.IsNullOrWhiteSpace(step.Method) ? "POST" : step.Method.ToUpperInvariant());
        var req = new HttpRequestMessage(method, url);

        if (step.Headers is { Count: > 0 })
        {
            foreach (var (name, value) in step.Headers)
                req.Headers.TryAddWithoutValidation(name, Substitute(value, vars, index));
        }

        // Body precedence: form (OAuth token endpoint) → json → raw body.
        if (step.Form is { Count: > 0 })
        {
            var pairs = new List<KeyValuePair<string, string>>(step.Form.Count);
            foreach (var (k, v) in step.Form)
                pairs.Add(new(k, Substitute(v, vars, index)));
            req.Content = new FormUrlEncodedContent(pairs);
        }
        else if (step.Json is not null)
        {
            req.Content = new StringContent(Substitute(step.Json, vars, index), Encoding.UTF8, "application/json");
        }
        else if (step.Body is not null)
        {
            req.Content = new StringContent(Substitute(step.Body, vars, index), Encoding.UTF8,
                string.IsNullOrWhiteSpace(step.ContentType) ? "text/plain" : step.ContentType);
        }

        return req;
    }

    private static void ApplyCaptures(
        IReadOnlyList<AuthCapture> captures, HttpResponseMessage resp, string body,
        Dictionary<string, string> vars, int index)
    {
        foreach (var cap in captures)
        {
            if (string.IsNullOrWhiteSpace(cap.Var))
                throw new AuthFlowException($"Auth flow step {index + 1} has a capture with no 'var' name.");

            var value = Extract(cap, resp, body)
                ?? throw new AuthFlowException(
                    $"Auth flow step {index + 1}: capture '{cap.Var}' matched nothing in the response.");
            vars[cap.Var] = value;
        }
    }

    private static string? Extract(AuthCapture cap, HttpResponseMessage resp, string body)
    {
        if (!string.IsNullOrWhiteSpace(cap.Json)) return ExtractJson(body, cap.Json);
        if (!string.IsNullOrWhiteSpace(cap.Regex)) return ExtractRegex(body, cap.Regex);
        if (!string.IsNullOrWhiteSpace(cap.Header)) return ExtractHeader(resp, cap.Header);
        if (!string.IsNullOrWhiteSpace(cap.Cookie)) return ExtractCookie(resp, cap.Cookie);
        throw new AuthFlowException($"Capture '{cap.Var}' has no source (json / regex / header / cookie).");
    }

    // Dotted JSON path: "access_token", "$.data.token", "tokens[0].jwt".
    private static string? ExtractJson(string body, string path)
    {
        JsonElement el;
        try
        {
            using var doc = JsonDocument.Parse(body);
            el = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        var trimmed = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..]
            : path.StartsWith('$') ? path[1..]
            : path;

        foreach (var segment in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment;
            var index = -1;
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0 && segment.EndsWith(']'))
            {
                var idxText = segment[(bracket + 1)..^1];
                if (!int.TryParse(idxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) return null;
                name = segment[..bracket];
            }

            if (name.Length > 0 && (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out el))) return null;
            if (index >= 0)
            {
                if (el.ValueKind != JsonValueKind.Array || index >= el.GetArrayLength()) return null;
                el = el[index];
            }
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
            _ => null,
        };
    }

    private static string? ExtractRegex(string body, string pattern)
    {
        Match m;
        try { m = Regex.Match(body, pattern, RegexOptions.None, s_regexTimeout); }
        catch (RegexParseException ex) { throw new AuthFlowException($"Invalid capture regex '{pattern}': {ex.Message}", ex); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;
        return m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
    }

    private static string? ExtractHeader(HttpResponseMessage resp, string header)
    {
        if (resp.Headers.TryGetValues(header, out var v)) return v.FirstOrDefault();
        if (resp.Content.Headers.TryGetValues(header, out var cv)) return cv.FirstOrDefault();
        return null;
    }

    private static string? ExtractCookie(HttpResponseMessage resp, string cookieName)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var raw in cookies)
        {
            // "name=value; Path=/; HttpOnly" — take the first "name=value" pair.
            var semi = raw.IndexOf(';', StringComparison.Ordinal);
            var pair = semi >= 0 ? raw[..semi] : raw;
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            if (string.Equals(pair[..eq].Trim(), cookieName, StringComparison.OrdinalIgnoreCase))
                return pair[(eq + 1)..].Trim();
        }
        return null;
    }

    // Pick which captured variable holds the token: explicit `token` field wins,
    // else common OAuth/OIDC names in priority order.
    private static string ResolveTokenVar(AuthFlowDefinition flow, Dictionary<string, string> vars)
    {
        if (!string.IsNullOrWhiteSpace(flow.Token))
        {
            return vars.ContainsKey(flow.Token)
                ? flow.Token
                : throw new AuthFlowException($"Auth flow declares token '{flow.Token}' but no step captured it.");
        }

        foreach (var candidate in s_tokenNames)
            if (vars.ContainsKey(candidate)) return candidate;

        throw new AuthFlowException(
            "Auth flow captured no recognisable token. Add a capture named 'access_token'/'token', or set the flow's 'token' field.");
    }

    private static readonly string[] s_tokenNames = ["access_token", "id_token", "token", "jwt"];

    // Replace {{var}} / {{env.NAME}} occurrences. Unknown references throw so a
    // missing secret surfaces immediately instead of sending an empty credential.
    private static string Substitute(string input, Dictionary<string, string> vars, int index)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains("{{", StringComparison.Ordinal)) return input;

        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var open = input.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(input, i, input.Length - i); break; }
            var close = input.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { sb.Append(input, i, input.Length - i); break; }

            sb.Append(input, i, open - i);
            var name = input[(open + 2)..close].Trim();
            sb.Append(Resolve(name, vars, index));
            i = close + 2;
        }
        return sb.ToString();
    }

    private static string Resolve(string name, Dictionary<string, string> vars, int index)
    {
        if (name.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
        {
            var envName = name[4..];
            return Environment.GetEnvironmentVariable(envName)
                ?? throw new AuthFlowException(
                    $"Auth flow step {index + 1} references {{{{env.{envName}}}}} but that environment variable is not set. "
                    + "Secrets are read from the environment, never inlined in the flow file.");
        }

        return vars.TryGetValue(name, out var v)
            ? v
            : throw new AuthFlowException(
                $"Auth flow step {index + 1} references {{{{{name}}}}} but no earlier step captured it.");
    }
}
