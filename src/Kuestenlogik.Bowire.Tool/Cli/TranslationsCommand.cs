// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.CommandLine;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire docs translations</c> — scaffold or check a UI locale (#117).
/// </summary>
/// <remarks>
/// Translating Bowire has to be possible without a .NET SDK: the catalogues
/// are JSON (with comments and trailing commas tolerated) and the only hard
/// part is knowing which keys exist and which of them are still missing. This command answers both from the English
/// catalogue the tool already carries, so a translator never has to clone the
/// repository to find out what is left.
/// </remarks>
internal static class TranslationsCommand
{
    private const string EnglishResource = "Kuestenlogik.Bowire.wwwroot.locales.en.json";

    public static Command Build()
    {
        var docs = new Command("docs", "Documentation and translation helpers.");

        var translations = new Command(
            "translations",
            "Scaffold a UI locale from the English catalogue, or report what a locale is missing.");

        var localeArg = new Argument<string>("locale")
        {
            Description = "BCP-47 tag for the locale, e.g. 'fr' or 'pt-BR'.",
        };
        var outOpt = new Option<string>("--out")
        {
            Description = "Where to write the stub. Defaults to <locale>.json in the current directory. "
                        + "Use '-' for stdout.",
        };
        var checkOpt = new Option<string>("--check")
        {
            Description = "Instead of scaffolding, read this catalogue and report keys it is missing, "
                        + "keys English does not have, and values left empty. Exits non-zero when "
                        + "anything is off, so CI can run it.",
        };

        translations.Add(localeArg);
        translations.Add(outOpt);
        translations.Add(checkOpt);

        translations.SetAction(async (pr, ct) =>
        {
            var io = CommandIo.Resolve(pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error);
            var locale = pr.GetValue(localeArg) ?? string.Empty;
            var check = pr.GetValue(checkOpt);
            var target = pr.GetValue(outOpt);

            var english = ReadEnglish();
            if (english is null)
            {
                await io.Err.WriteLineAsync("  The English catalogue is not embedded in this build.")
                    .ConfigureAwait(false);
                return 1;
            }

            return check is { Length: > 0 }
                ? await CheckAsync(io, english, check, ct).ConfigureAwait(false)
                : await ScaffoldAsync(io, english, locale, target, ct).ConfigureAwait(false);
        });

        docs.Add(translations);
        return docs;
    }

    /// <summary>The English catalogue, minus the notes-to-translators keys.</summary>
    private static SortedDictionary<string, string>? ReadEnglish()
    {
        var assembly = typeof(Kuestenlogik.Bowire.BowireApiEndpoints).Assembly;
        using var stream = assembly.GetManifestResourceStream(EnglishResource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        return Flatten(json);
    }

    // The catalogues are JSON with comments: the scaffold writes the English
    // text above each blank so a translator sees what they are translating
    // without a second file open, and that is worth more than strict JSON
    // conformance in a file only Bowire reads. Trailing commas are allowed for
    // the same reason -- a translator adding a line should not be tripped by
    // punctuation.
    private static readonly JsonDocumentOptions CatalogueJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static SortedDictionary<string, string>? Flatten(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: CatalogueJson); }
        catch (JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in obj)
        {
            // Underscore keys are the file's note to translators, not UI text.
            if (pair.Key.StartsWith('_')) continue;
            result[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
        }
        return result;
    }

    private static async Task<int> ScaffoldAsync(
        CommandIo io, SortedDictionary<string, string> english,
        string locale, string? target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            await io.Err.WriteLineAsync("  A locale tag is required, e.g. `bowire docs translations fr`.")
                .ConfigureAwait(false);
            return 1;
        }

        // The English text rides along as a comment per key so the translator
        // sees what they are translating without a second file open. Values
        // start empty: an empty value falls back to English at runtime, so a
        // half-finished catalogue is usable rather than broken.
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine(
            "  \"_comment\": \"UI catalogue for '" + locale + "'. Same key set as en.json; "
            + "the locale-parity test fails a build where they diverge. Placeholders like {name} "
            + "must survive translation unchanged. An empty value falls back to English, so this "
            + "file is usable before it is finished.\",");
        sb.AppendLine();

        var i = 0;
        foreach (var pair in english)
        {
            i++;
            sb.AppendLine("  // en: " + pair.Value.Replace("\n", " ", StringComparison.Ordinal));
            sb.Append("  ").Append(JsonSerializer.Serialize(pair.Key)).Append(": \"\"");
            sb.AppendLine(i < english.Count ? "," : string.Empty);
        }
        sb.AppendLine("}");

        var text = sb.ToString();
        if (target == "-")
        {
            io.OutLine(text);
            return 0;
        }

        var path = string.IsNullOrWhiteSpace(target) ? locale + ".json" : target;
        await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
        io.OutLine();
        io.OutLine($"  Wrote {path} — {english.Count} keys, all empty.");
        io.OutLine("  Fill in the values and drop it into wwwroot/locales/ to see it in the workbench.");
        io.OutLine();
        return 0;
    }

    private static async Task<int> CheckAsync(
        CommandIo io, SortedDictionary<string, string> english, string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            await io.Err.WriteLineAsync($"  No such catalogue: {path}").ConfigureAwait(false);
            return 1;
        }

        var candidate = Flatten(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        if (candidate is null)
        {
            await io.Err.WriteLineAsync($"  {path} is not a JSON object.").ConfigureAwait(false);
            return 1;
        }

        var missing = english.Keys.Where(k => !candidate.ContainsKey(k)).ToList();
        var unknown = candidate.Keys.Where(k => !english.ContainsKey(k)).ToList();
        var empty = candidate.Where(p => english.ContainsKey(p.Key) && string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p.Key).ToList();

        async Task ReportAsync(string label, List<string> keys)
        {
            if (keys.Count == 0) return;
            await io.Err.WriteLineAsync($"  {label} ({keys.Count}):").ConfigureAwait(false);
            foreach (var key in keys) await io.Err.WriteLineAsync("    " + key).ConfigureAwait(false);
        }

        await ReportAsync("Missing", missing).ConfigureAwait(false);
        await ReportAsync("Not in English", unknown).ConfigureAwait(false);
        await ReportAsync("Still empty", empty).ConfigureAwait(false);

        if (missing.Count + unknown.Count + empty.Count == 0)
        {
            io.OutLine($"  {path}: all {english.Count} keys translated.");
            return 0;
        }

        // Non-zero so a translator's own pipeline can gate on it. The counts
        // are on stderr above; this line is the summary a human reads first.
        await io.Err.WriteLineAsync(
            $"  {path}: {english.Count - missing.Count - empty.Count} of {english.Count} keys translated.")
            .ConfigureAwait(false);
        return 1;
    }
}
