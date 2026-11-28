// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// #690 — the CLI's help reads from the locale catalogues, all of it. Checked
/// on the sources and the catalogues rather than by rendering `--help` under
/// another locale: the locale is process-wide, and switching it would change
/// what parallel tests see.
/// </summary>
public sealed partial class CliHelpTranslationTests
{
    // The command definitions: the tool's CLI and the plugins that add commands to it.
    private static readonly string[] s_sources =
    [
        "src/Kuestenlogik.Bowire.Tool",
        "src/Kuestenlogik.Bowire.Monitoring/Cli",
        "src/Kuestenlogik.Bowire.Oast/Cli",
        "src/Kuestenlogik.Bowire.Security.Scanner/Cli",
    ];

    [GeneratedRegex("""BowireLocale\.T\("(cli\.[^"]+)"\)""")]
    private static partial Regex CliKey();

    // A help text written as a literal: `Description = "…"`, or a command's
    // description as the second argument of `new Command("name", "…")`.
    [GeneratedRegex(@"Description\s*=\s*\$?@?""|new\s+(?:Root)?Command\(\s*""[^""]*""\s*,\s*\$?@?""")]
    private static partial Regex HelpLiteral();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kuestenlogik.Bowire.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<(string File, string Text)> Sources()
    {
        var root = RepoRoot();
        foreach (var source in s_sources)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, source), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
                yield return (relative, File.ReadAllText(file));
            }
        }
    }

    private static Dictionary<string, string> Catalogue(string locale)
    {
        var path = Path.Combine(RepoRoot(), "src", "Kuestenlogik.Bowire", "wwwroot", "locales", locale + ".json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    [Fact]
    public void No_Help_Text_Is_Written_As_A_Literal()
    {
        var literals = Sources()
            .SelectMany(s => HelpLiteral().Matches(s.Text).Select(m => s.File + ": " + m.Value))
            .ToList();

        Assert.True(literals.Count == 0, "Help texts must come from the catalogue (BowireLocale.T):\n" + string.Join("\n", literals));
    }

    [Fact]
    public void Every_Help_Key_Has_An_English_And_A_German_Text()
    {
        var keys = Sources().SelectMany(s => CliKey().Matches(s.Text).Select(m => m.Groups[1].Value)).Distinct().ToList();
        var en = Catalogue("en");
        var de = Catalogue("de");

        Assert.True(keys.Count > 300, $"expected the whole help surface, found {keys.Count} keys");
        AssertNone(keys.Where(k => !en.ContainsKey(k)), "without an English text");
        AssertNone(keys.Where(k => !de.TryGetValue(k, out var text) || string.IsNullOrWhiteSpace(text) || text == k), "without a German text");
    }

    [Fact]
    public void A_Help_Catalogue_Entry_Without_A_Source_Is_Not_Left_Behind()
    {
        var used = Sources().SelectMany(s => CliKey().Matches(s.Text).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);

        AssertNone(Catalogue("en").Keys.Where(k => k.StartsWith("cli.", StringComparison.Ordinal) && !used.Contains(k)), "in en.json that no command reads");
        AssertNone(Catalogue("de").Keys.Where(k => k.StartsWith("cli.", StringComparison.Ordinal) && !used.Contains(k)), "in de.json that no command reads");
    }

    private static void AssertNone(IEnumerable<string> keys, string what)
    {
        var list = keys.ToList();
        Assert.True(list.Count == 0, $"{list.Count} help key(s) {what}:\n" + string.Join("\n", list));
    }

    [Fact]
    public void The_Help_Writer_Translates_The_Untranslated_Heading_And_Nothing_Else()
    {
        // System.CommandLine writes "Description:" in every language it ships.
        using var target = new StringWriter();
        using (var writer = new Kuestenlogik.Bowire.App.Cli.HelpLocalization.HeadingWriter(target, "Beschreibung:"))
        {
            writer.Write("Descr");
            writer.Write("iption:\r\n");
            writer.WriteLine("  Description: is a word here, not the heading.");
            writer.Write("Nutzung:\n  bowire [command]");
        }

        Assert.Equal("Beschreibung:\r\n  Description: is a word here, not the heading." + Environment.NewLine + "Nutzung:\n  bowire [command]",
            target.ToString());
    }

    [Fact]
    public void A_Text_The_Active_Locale_Lacks_Renders_In_English_Never_As_Its_Key()
    {
        var english = new Dictionary<string, string>(StringComparer.Ordinal) { ["cli.root.port"] = "Port to listen on." };
        var german = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Equal("Port to listen on.", BowireLocale.Pick(german, english, "cli.root.port"));
        german["cli.root.port"] = "Port, auf dem gelauscht wird.";
        Assert.Equal("Port, auf dem gelauscht wird.", BowireLocale.Pick(german, english, "cli.root.port"));
    }
}
