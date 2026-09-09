// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Behavioural coverage for <c>bowire docs translations</c> (#117).
/// </summary>
/// <remarks>
/// This command is the whole reason translating Bowire does not need a .NET
/// SDK or a clone: it scaffolds a locale from the English catalogue the tool
/// carries, and it tells a translator what is still missing. Both halves are
/// exercised end-to-end through the parser rather than against internals,
/// because the exit code is part of the contract — a translator's own pipeline
/// gates on <c>--check</c>.
///
/// Two properties get their own tests because they are promises the command
/// makes in prose and would otherwise be untested: the catalogues tolerate
/// comments and trailing commas, and a slip in a hand-edited file is reported
/// rather than thrown.
/// </remarks>
public sealed class TranslationsCommandTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("bowire-translations-").FullName;

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private static async Task<(int rc, string stdout, string stderr)> Invoke(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var docs = TranslationsCommand.Build();
        var parse = docs.Parse(args);
        var rc = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);

        return (rc, stdout.ToString(), stderr.ToString());
    }

    private string Path_(string name) => Path.Combine(_tempRoot, name);

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static Dictionary<string, string> ReadCatalogue(string text)
    {
        var obj = (JsonObject)JsonNode.Parse(text, documentOptions: Lenient)!;
        return obj.Where(p => !p.Key.StartsWith('_'))
            .ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
    }

    /// <summary>The shipped English catalogue, read the way the command reads it.</summary>
    private async Task<Dictionary<string, string>> EnglishAsync()
    {
        var path = Path_("en-roundtrip.json");
        var (rc, _, _) = await Invoke("translations", "en", "--out", path);
        Assert.Equal(0, rc);
        // The scaffold has the right keys with empty values; that is all the
        // other tests need from it.
        return ReadCatalogue(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    private async Task<string> WriteCatalogueAsync(string name, Action<Dictionary<string, string>> edit)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in (await EnglishAsync()).Keys) entries[key] = "übersetzt";
        edit(entries);

        var obj = new JsonObject();
        foreach (var pair in entries) obj[pair.Key] = pair.Value;
        var path = Path_(name);
        await File.WriteAllTextAsync(path, obj.ToJsonString(), TestContext.Current.CancellationToken);
        return path;
    }

    // -------------------------------------------------------------- scaffold

    [Fact]
    public async Task The_English_catalogue_is_embedded_in_the_build()
    {
        // The EmbeddedResource item in the csproj is easy to lose in a
        // packaging change, and losing it breaks the command completely
        // while the build stays green.
        var english = await EnglishAsync();
        Assert.NotEmpty(english);
        Assert.Contains("common.close", english.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Scaffold_writes_every_key_with_an_empty_value()
    {
        var path = Path_("fr.json");
        var (rc, stdout, _) = await Invoke("translations", "fr", "--out", path);

        Assert.Equal(0, rc);
        Assert.True(File.Exists(path));

        var written = ReadCatalogue(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal((await EnglishAsync()).Count, written.Count);
        Assert.All(written.Values, v => Assert.Equal(string.Empty, v));
        Assert.Contains($"{written.Count} keys, all empty", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_carries_the_English_text_as_a_comment_above_each_key()
    {
        // The point of the format: a translator sees what they are translating
        // without a second file open.
        var path = Path_("fr-comments.json");
        await Invoke("translations", "fr", "--out", path);
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains("// en: Close", text, StringComparison.Ordinal);
        Assert.Contains("\"common.close\": \"\"", text, StringComparison.Ordinal);
        Assert.Contains("\"_comment\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_writes_to_stdout_for_a_dash()
    {
        var (rc, stdout, _) = await Invoke("translations", "fr", "--out", "-");

        Assert.Equal(0, rc);
        Assert.Contains("\"common.close\": \"\"", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "-")));
    }

    [Fact]
    public async Task Scaffold_refuses_a_blank_locale()
    {
        var (rc, _, stderr) = await Invoke("translations", "  ");

        Assert.Equal(1, rc);
        Assert.Contains("locale tag is required", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scaffold_output_is_readable_by_check()
    {
        // The round trip is the command's own promise: what it writes is what
        // it reads back. A stub is a valid catalogue, just an unfinished one,
        // so --check reports every key as still empty rather than as broken.
        var path = Path_("round-trip.json");
        await Invoke("translations", "fr", "--out", path);

        var (rc, _, stderr) = await Invoke("translations", "fr", "--check", path);

        Assert.Equal(1, rc);
        Assert.Contains("Still empty", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Missing", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Not in English", stderr, StringComparison.Ordinal);
        Assert.Contains("0 of ", stderr, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- check

    [Fact]
    public async Task Check_passes_a_complete_catalogue()
    {
        var path = await WriteCatalogueAsync("complete.json", _ => { });

        var (rc, stdout, stderr) = await Invoke("translations", "de", "--check", path);

        Assert.Equal(0, rc);
        Assert.Contains("keys translated", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public async Task Check_names_the_keys_a_catalogue_is_missing()
    {
        var path = await WriteCatalogueAsync("missing.json", e => e.Remove("common.close"));

        var (rc, _, stderr) = await Invoke("translations", "de", "--check", path);

        Assert.Equal(1, rc);
        Assert.Contains("Missing (1)", stderr, StringComparison.Ordinal);
        Assert.Contains("common.close", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_names_the_keys_English_does_not_have()
    {
        // The direction that matters after a key is renamed or consolidated:
        // the translation keeps working, and the stale key rots unnoticed.
        var path = await WriteCatalogueAsync("extra.json", e => e["landing.gone.away"] = "veraltet");

        var (rc, _, stderr) = await Invoke("translations", "de", "--check", path);

        Assert.Equal(1, rc);
        Assert.Contains("Not in English (1)", stderr, StringComparison.Ordinal);
        Assert.Contains("landing.gone.away", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_names_the_values_left_empty()
    {
        // An empty value falls back to English at runtime, so the screen looks
        // translated to everyone except the person reading it.
        var path = await WriteCatalogueAsync("blank.json", e => e["common.close"] = "   ");

        var (rc, _, stderr) = await Invoke("translations", "de", "--check", path);

        Assert.Equal(1, rc);
        Assert.Contains("Still empty (1)", stderr, StringComparison.Ordinal);
        Assert.Contains("common.close", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_ignores_the_note_to_translators()
    {
        // "_comment" is the scaffold's own note. Reporting it as a key English
        // does not have would fail every catalogue the command itself wrote.
        var path = await WriteCatalogueAsync("noted.json", _ => { });
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var obj = (JsonObject)JsonNode.Parse(text)!;
        obj["_comment"] = "eine Notiz";
        await File.WriteAllTextAsync(path, obj.ToJsonString(), TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await Invoke("translations", "de", "--check", path);

        Assert.Equal(0, rc);
        Assert.DoesNotContain("_comment", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_accepts_comments_and_trailing_commas()
    {
        // The format's whole premise. If the reader were strict, every
        // catalogue the scaffold writes would be rejected by the checker,
        // because the scaffold writes `// en:` lines.
        var path = await WriteCatalogueAsync("lenient.json", _ => { });
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        text = "// a note from the translator, mentioning https://example.com/style"
            + Environment.NewLine
            + text.TrimEnd().TrimEnd('}').TrimEnd() + "," + Environment.NewLine + "}";
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);

        var (rc, stdout, _) = await Invoke("translations", "de", "--check", path);

        Assert.Equal(0, rc);
        Assert.Contains("keys translated", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_reports_a_missing_file_rather_than_throwing()
    {
        var (rc, _, stderr) = await Invoke("translations", "de", "--check", Path_("nope.json"));

        Assert.Equal(1, rc);
        Assert.Contains("No such catalogue", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]                       // an array, not an object
    [InlineData("{ \"a.b\": 42 }")]          // a number where a string belongs
    [InlineData("{ \"a.b\": null }")]        // a hole
    [InlineData("{ \"a.b\": { \"c\": 1 } }")] // nested, when the format is flat
    [InlineData("not json at all")]
    public async Task Check_reports_a_malformed_catalogue_rather_than_throwing(string content)
    {
        // This command is used on files people edit by hand, so every one of
        // these is a plausible Tuesday. A stack trace would be the worst of
        // the possible answers.
        var path = Path_("broken.json");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await Invoke("translations", "de", "--check", path);

        Assert.Equal(1, rc);
        Assert.Contains("not a flat JSON object of strings", stderr, StringComparison.Ordinal);
    }
}
