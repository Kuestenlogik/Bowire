// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei;

/// <summary>
/// Resolve Nuclei's <c>{{VariableName}}</c> placeholders against a
/// concrete scan target. Nuclei expands these placeholders before
/// sending the HTTP probe; Bowire's scanner doesn't speak the
/// Nuclei DSL, so we replace them at conversion time once the target
/// URL is known.
///
/// Phase 2c — the common subset of Nuclei's variable surface:
/// <list type="bullet">
///   <item><c>{{BaseURL}}</c> — full base URL: scheme + host + port + base path.
///     <c>https://api.example.com:8443/v2</c> → <c>https://api.example.com:8443/v2</c>.</item>
///   <item><c>{{Hostname}}</c> — host + port: <c>api.example.com:8443</c>.</item>
///   <item><c>{{Host}}</c> — host only: <c>api.example.com</c>.</item>
///   <item><c>{{Port}}</c> — port if specified, else default (80/443) by scheme.</item>
///   <item><c>{{Path}}</c> — base-URL path component (rarely used standalone).</item>
///   <item><c>{{RandStr}}</c> — random alphanumeric string, default length 8.</item>
///   <item><c>{{RandStr_N}}</c> — random alphanumeric string of length N (e.g. <c>{{RandStr_16}}</c>).</item>
///   <item><c>{{RandInt}}</c> / <c>{{RandInt_N}}</c> — random integer with N digits (default 6).</item>
/// </list>
///
/// Phase 2c+ surface (deferred):
/// <list type="bullet">
///   <item>DSL helper functions: <c>{{md5(BaseURL)}}</c>, <c>{{base64(...)}}</c>,
///     <c>{{rand_text_alpha(N)}}</c>, <c>{{to_lower(...)}}</c>.</item>
/// </list>
///
/// Phase 2f (shipped): <c>{{interactsh-url}}</c> resolves to an out-of-band
/// callback host when an interaction server is configured
/// (<see cref="NucleiVariableContext.InteractshUrlFactory"/>). Without one it
/// passes through untouched, so an OAST template yields no usable probe rather
/// than a meaningless one.
/// </summary>
public static class NucleiVariableResolver
{
    // Hyphens are part of the name: `{{interactsh-url}}` is the OAST
    // placeholder (#35 Phase 2f) and would otherwise not even be seen by the
    // resolver. Widening the class is safe for everything else — an unknown
    // name still falls through to pass-through below.
    private static readonly Regex VariablePattern = new(
        @"\{\{(?<name>[A-Za-z][A-Za-z0-9_-]*)\}\}",
        RegexOptions.Compiled);

    private static readonly Regex RandStrPattern = new(
        @"^RandStr(?:_(?<n>\d+))?$",
        RegexOptions.Compiled);

    private static readonly Regex RandIntPattern = new(
        @"^RandInt(?:_(?<n>\d+))?$",
        RegexOptions.Compiled);

    private const string Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Replace every recognised <c>{{...}}</c> placeholder in
    /// <paramref name="input"/> with its resolved value from
    /// <paramref name="context"/>. Unrecognised placeholders pass
    /// through literally — the surrounding template-runner may
    /// surface them as "unresolved variable" findings, or the
    /// caller may decide whether they're a hard error.
    /// </summary>
    public static string Resolve(string input, NucleiVariableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(input)) return input;

        return VariablePattern.Replace(input, match =>
        {
            var name = match.Groups["name"].Value;
            var resolved = ResolveName(name, context);
            return resolved ?? match.Value; // pass-through on unknown
        });
    }

    private static string? ResolveName(string name, NucleiVariableContext context)
    {
        return name switch
        {
            // #35 Phase 2f — the OAST callback host. Null when no interaction
            // server is configured, which makes the placeholder pass through
            // untouched and leaves the template unusable on purpose: sending a
            // literal {{interactsh-url}} to a target would probe nothing and
            // prove nothing.
            "interactsh-url" => context.InteractshUrl(),
            "BaseURL" => context.BaseUrl,
            "Hostname" => context.Hostname,
            "Host" => context.Host,
            "Port" => context.Port,
            "Path" => context.Path,
            _ => ResolveRandom(name, context),
        };
    }

    private static string? ResolveRandom(string name, NucleiVariableContext context)
    {
        var randStrMatch = RandStrPattern.Match(name);
        if (randStrMatch.Success)
        {
            var length = randStrMatch.Groups["n"].Success
                ? int.Parse(randStrMatch.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 8;
            return context.RandomString(name, length);
        }

        var randIntMatch = RandIntPattern.Match(name);
        if (randIntMatch.Success)
        {
            var digits = randIntMatch.Groups["n"].Success
                ? int.Parse(randIntMatch.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 6;
            return context.RandomInteger(name, digits);
        }

        return null;
    }

    /// <summary>
    /// Random alphanumeric string from <paramref name="random"/> of
    /// the requested length. Exposed for tests / contexts that want
    /// a fresh value outside the cache.
    /// </summary>
    internal static string RandomAlphanumeric(Random random, int length)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (length <= 0) return string.Empty;
        return string.Create(length, random, (buffer, rng) =>
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = Alphabet[rng.Next(Alphabet.Length)];
            }
        });
    }

    internal static string RandomDigits(Random random, int digits)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (digits <= 0) return string.Empty;
        return string.Create(digits, random, (buffer, rng) =>
        {
            // First digit non-zero so the result reads as a proper
            // N-digit integer rather than a zero-padded one.
            buffer[0] = (char)('1' + rng.Next(9));
            for (var i = 1; i < buffer.Length; i++)
            {
                buffer[i] = (char)('0' + rng.Next(10));
            }
        });
    }
}

/// <summary>
/// Carries the per-template values the resolver substitutes plus the
/// random source for generated placeholders. Build with
/// <see cref="FromTarget"/> for a target URL; the random source
/// defaults to seeded so a given context produces reproducible
/// placeholders across invocations (essential for SARIF diff +
/// CI dashboards).
/// </summary>
public sealed class NucleiVariableContext
{
    private readonly Random _random;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Allocates an out-of-band callback host for <c>{{interactsh-url}}</c>
    /// (#35 Phase 2f). Null (the default) means no interaction server is
    /// configured and OAST templates cannot run.
    /// </summary>
    /// <remarks>
    /// Invoked at most once per context, and the value is memoised — a
    /// template referencing the placeholder twice must plant the SAME host, or
    /// the callback could not be attributed back to it. Give each template its
    /// own context so two templates never share a host, otherwise a callback
    /// cannot be pinned to the probe that caused it.
    /// </remarks>
    public Func<string>? InteractshUrlFactory { get; init; }

    /// <summary>
    /// The callback host this context handed to <c>{{interactsh-url}}</c>, or
    /// null if the placeholder was never resolved. The scanner reads it back
    /// to correlate a recorded interaction to the template that planted it.
    /// </summary>
    public string? AllocatedInteractshUrl =>
        _cache.TryGetValue(InteractshCacheKey, out var v) ? v : null;

    private const string InteractshCacheKey = "interactsh-url";

    /// <summary>Memoised callback host, or null when no factory is configured.</summary>
    internal string? InteractshUrl()
    {
        if (InteractshUrlFactory is null) return null;
        if (_cache.TryGetValue(InteractshCacheKey, out var existing)) return existing;
        var value = InteractshUrlFactory();
        _cache[InteractshCacheKey] = value;
        return value;
    }

    public string BaseUrl { get; }
    public string Hostname { get; }
    public string Host { get; }
    public string Port { get; }
    public string Path { get; }

    public NucleiVariableContext(
        string baseUrl, string hostname, string host, string port, string path, Random random)
    {
        BaseUrl = baseUrl;
        Hostname = hostname;
        Host = host;
        Port = port;
        Path = path;
        _random = random;
    }

    /// <summary>
    /// Build a context from a fully-qualified target URL like
    /// <c>https://api.example.com:8443/v2</c>. The <paramref name="seed"/>
    /// parameter feeds the random source so repeated runs of the same
    /// scan produce the same placeholder values — keeps SARIF diff +
    /// CI dashboards stable.
    /// </summary>
    public static NucleiVariableContext FromTarget(
        string target, int? seed = null, Func<string>? interactshUrlFactory = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target must be a non-empty URL.", nameof(target));

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Target '{target}' is not a valid absolute URL.", nameof(target));

        // BaseURL is the canonical "scheme://host[:port][/path]" form
        // with no trailing slash so concatenation in templates stays
        // predictable: `{{BaseURL}}/admin` → `https://x/admin`.
        var basePath = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
        var portSegment = IsDefaultPort(uri) ? string.Empty : $":{uri.Port}";
        var baseUrl = $"{uri.Scheme}://{uri.Host}{portSegment}{basePath}";
        var hostname = string.IsNullOrEmpty(portSegment) ? uri.Host : $"{uri.Host}{portSegment}";
        var port = uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var random = seed.HasValue ? new Random(seed.Value) : new Random();
        return new NucleiVariableContext(baseUrl, hostname, uri.Host, port, basePath, random)
        {
            InteractshUrlFactory = interactshUrlFactory,
        };
    }

    /// <summary>
    /// Return the cached random value for <paramref name="placeholderName"/>
    /// if seen already, otherwise generate + cache. Caching means
    /// <c>{{RandStr}}</c> referenced twice in the same template
    /// resolves to the same value (matches Nuclei's per-template
    /// memoisation contract).
    /// </summary>
    internal string RandomString(string placeholderName, int length)
    {
        if (_cache.TryGetValue(placeholderName, out var existing)) return existing;
        var value = NucleiVariableResolver.RandomAlphanumeric(_random, length);
        _cache[placeholderName] = value;
        return value;
    }

    internal string RandomInteger(string placeholderName, int digits)
    {
        if (_cache.TryGetValue(placeholderName, out var existing)) return existing;
        var value = NucleiVariableResolver.RandomDigits(_random, digits);
        _cache[placeholderName] = value;
        return value;
    }

    private static bool IsDefaultPort(Uri uri)
    {
        return (uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80)
            || (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443);
    }
}
