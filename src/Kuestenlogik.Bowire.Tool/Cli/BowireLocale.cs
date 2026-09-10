// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// The command line's half of the translation layer (#117).
/// </summary>
/// <remarks>
/// <para>
/// The workbench and the CLI read the <em>same</em> catalogues — the JSON
/// files under <c>wwwroot/locales/</c>, embedded into the assembly. One set of
/// files, one key namespace, one thing for a translator to edit. A parallel
/// <c>.resx</c> set would have meant translating every string twice and
/// keeping the two in step for ever, and <c>.resx</c>'s positional
/// <c>{0}</c> placeholders would have reintroduced exactly the
/// position-dependence that named ones exist to remove.
/// </para>
/// <para>
/// Locale resolution follows what a terminal user already expects:
/// <c>BOWIRE_LOCALE</c> wins because it is the specific setting, then
/// <c>LC_ALL</c> / <c>LANG</c> because that is how a POSIX shell says it, then
/// the OS's current culture, then English. A tag like <c>de_DE.UTF-8</c> is
/// narrowed — the encoding suffix, the modifier and the region come off in
/// turn — so a German shell finds <c>de.json</c> without anyone configuring
/// anything.
/// </para>
/// </remarks>
internal static class BowireLocale
{
    private const string Prefix = "Kuestenlogik.Bowire.wwwroot.locales.";

    /// <summary>What a resolved environment produced: an id and two catalogues.</summary>
    private sealed record Resolved(
        string Id,
        Dictionary<string, string> Active,
        Dictionary<string, string> English);

    // Lazy rather than a hand-written double-check: one initialisation, no
    // lock to reason about, and no "is it still null?" branch for an analyser
    // to correctly call unreachable.
    private static Lazy<Resolved> _state = new(Load, isThreadSafe: true);

    /// <summary>The resolved locale id, e.g. <c>de</c>. Defaults to <c>en</c>.</summary>
    public static string Active => _state.Value.Id;

    /// <summary>
    /// The translated string for <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// Same resolution order as the workbench's <c>t()</c>: the active locale,
    /// then English, then the key itself. Returning the key rather than an
    /// empty string means a missing translation is visible in the terminal
    /// instead of leaving a hole in a sentence.
    /// </remarks>
    public static string T(string key, params (string Name, object? Value)[] args)
    {
        var state = _state.Value;
        var text = Lookup(state.Active, key) ?? Lookup(state.English, key);
        if (text is null) return key;
        return args.Length == 0 ? text : Interpolate(text, args);
    }

    /// <summary>Re-read the environment. For tests, and for nothing else.</summary>
    internal static void Reset() => _state = new Lazy<Resolved>(Load, isThreadSafe: true);

    /// <summary>
    /// The catalogue id an environment resolves to.
    /// </summary>
    /// <param name="bowireLocale">The <c>BOWIRE_LOCALE</c> value, if set.</param>
    /// <param name="lang">The <c>LC_ALL</c> or <c>LANG</c> value, if set.</param>
    /// <param name="osCulture">The OS's current UI culture name.</param>
    /// <param name="canonical">
    /// Maps a candidate tag to the id of a catalogue that exists, or null.
    /// Injected so the order can be tested without touching the filesystem.
    /// </param>
    internal static string ResolveId(
        string? bowireLocale, string? lang, string? osCulture, Func<string, string?> canonical)
    {
        foreach (var candidate in new[] { bowireLocale, lang, osCulture })
        {
            foreach (var tag in Narrow(candidate))
            {
                var found = canonical(tag);
                if (found is not null) return found;
            }
        }
        return "en";
    }

    /// <summary>
    /// A POSIX locale string, narrowed step by step: <c>de_AT.UTF-8</c> yields
    /// <c>de-AT</c> then <c>de</c>. A regional variant is closer to its base
    /// language than to English, so trying both in that order is the point.
    /// </summary>
    private static IEnumerable<string> Narrow(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        var value = raw.Trim();
        // "de_DE.UTF-8@euro" -> "de_DE"
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0) value = value[..at];
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0) value = value[..dot];
        value = value.Replace('_', '-');
        if (value.Length == 0) yield break;

        // "C" and "POSIX" mean "no locale", not a language named C.
        if (string.Equals(value, "C", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "POSIX", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return value;
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0) yield return value[..dash];
    }

    private static Resolved Load()
    {
        var english = Read("en") ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var id = ResolveId(
            Environment.GetEnvironmentVariable("BOWIRE_LOCALE"),
            Environment.GetEnvironmentVariable("LC_ALL")
                ?? Environment.GetEnvironmentVariable("LANG"),
            System.Globalization.CultureInfo.CurrentUICulture.Name,
            Canonical);
        var active = id == "en" ? english : (Read(id) ?? english);
        return new Resolved(id, active, english);
    }

    /// <summary>
    /// The embedded catalogue ids, by lower-case tag.
    /// </summary>
    /// <remarks>
    /// Built by listing the resources rather than by lower-casing the incoming
    /// tag and hoping the file matches: <c>LANG=DE</c> should find
    /// <c>de.json</c>, and the id we report back is the catalogue's own,
    /// whatever case it ships in.
    /// </remarks>
    private static readonly Lazy<Dictionary<string, string>> Catalogues = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(Kuestenlogik.Bowire.BowireApiEndpoints).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".json", StringComparison.Ordinal)) continue;
            var id = name[Prefix.Length..^".json".Length];
            if (id.Length > 0) map[id] = id;
        }
        return map;
    }, isThreadSafe: true);

    private static string? Canonical(string tag)
        => Catalogues.Value.TryGetValue(tag, out var id) ? id : null;

    private static string? Lookup(Dictionary<string, string> catalogue, string key)
        => catalogue.TryGetValue(key, out var text) && text.Length > 0 ? text : null;

    // The catalogues are JSON with comments and trailing commas: `bowire docs
    // translations` writes the English text above each blank so a translator
    // sees what they are translating. Reading them has to tolerate that.
    private static readonly JsonDocumentOptions CatalogueJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static Dictionary<string, string>? Read(string locale)
    {
        var assembly = typeof(Kuestenlogik.Bowire.BowireApiEndpoints).Assembly;
        using var stream = assembly.GetManifestResourceStream(Prefix + locale + ".json");
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        JsonNode? node;
        try { node = JsonNode.Parse(reader.ReadToEnd(), documentOptions: CatalogueJson); }
        catch (JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in obj)
        {
            // Underscore keys are the file's note to translators, not UI text.
            if (pair.Key.StartsWith('_')) continue;
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result[pair.Key] = text;
            }
        }
        return result;
    }

    /// <summary>
    /// Substitute <c>{name}</c> placeholders, leaving <c>{{name}}</c> alone.
    /// </summary>
    /// <remarks>
    /// The doubled brace is Bowire's own variable syntax and appears literally
    /// inside catalogue strings, so it has to come out the other side
    /// untouched. A placeholder with no matching argument stays visible rather
    /// than becoming "": a stray <c>{count}</c> in the terminal is a bug
    /// report, an empty gap is a mystery.
    /// </remarks>
    internal static string Interpolate(string text, params (string Name, object? Value)[] args)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') { sb.Append(text[i]); continue; }
            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                // "{{" opens Bowire's own syntax - copy it and everything up
                // to the matching "}}" verbatim.
                var close = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (close >= 0)
                {
                    sb.Append(text, i, close + 2 - i);
                    i = close + 1;
                    continue;
                }
            }

            var end = text.IndexOf('}', i + 1);
            if (end < 0) { sb.Append(text[i]); continue; }

            var name = text[(i + 1)..end];
            var match = Array.FindIndex(args, a => a.Name == name);
            if (match < 0) { sb.Append(text[i]); continue; }

            sb.Append(args[match].Value);
            i = end;
        }
        return sb.ToString();
    }
}
