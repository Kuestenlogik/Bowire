// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #117 — the English catalogue, for contract tests that used to pin a
/// sentence in the JS source.
/// </summary>
/// <remarks>
/// <para>
/// Several JS contract tests asserted on the English wording inside the
/// bundle: "Embedded — Bowire is mounted in-process", "to render `' + kind +
/// '`". Once those strings moved into <c>en.json</c> the tests failed, and the
/// obvious repair — pin the new wording instead — would make every one of them
/// a trap for the next translator, who is allowed to change English copy.
/// </para>
/// <para>
/// What those tests are actually about is the shape: two notes that differ by
/// mode, a message with the kind inside it. So they assert the key on the JS
/// side and read the text from here, where a changed sentence is fine and a
/// missing placeholder is not.
/// </para>
/// </remarks>
internal static class EnglishCatalogue
{
    private static readonly Lazy<Dictionary<string, string>> Catalogue = new(Load);

    /// <summary>Key → English text, read once per test run.</summary>
    public static IReadOnlyDictionary<string, string> Value => Catalogue.Value;

    private static Dictionary<string, string> Load()
    {
        var assembly = typeof(BowireServiceCollectionExtensions).Assembly;
        const string resourceName = "Kuestenlogik.Bowire.wwwroot.locales.en.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: {resourceName}.");
        using var reader = new StreamReader(stream);

        // The catalogues carry `// en: …` comment lines for translators and a
        // trailing comma after the last entry, so the reader has to allow both.
        // A `//` inside a value ("http://host") is safe: the tokenizer only
        // treats one as a comment outside a string.
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        using var doc = JsonDocument.Parse(reader.ReadToEnd(), options);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
                map[entry.Name] = entry.Value.GetString() ?? "";
        }
        return map;
    }
}
