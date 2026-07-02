// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Parsing;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Pretty-prints System.CommandLine parse failures (#38 — CLI Phase 3).
/// Replaces the framework's default <c>ParseErrorAction</c> rendering,
/// which dumps the raw message plus a full help screen to <b>stdout</b>.
/// This renderer instead:
///
/// <list type="bullet">
///   <item>routes every error line to <b>stderr</b> so a piped
///   <c>bowire call … | jq</c> keeps a clean stdout,</item>
///   <item>colourises the marker in red when writing to a real TTY
///   (suppressed when stderr is redirected / captured, so logs and
///   test assertions stay ANSI-free), and</item>
///   <item>ends with a single dim hint pointing at the most specific
///   command's <c>--help</c> rather than reprinting the whole usage.</item>
/// </list>
///
/// Invoked from <see cref="BowireCli.RunAsync"/> after
/// <c>root.Parse(args)</c> when <see cref="ParseResult.Errors"/> is
/// non-empty. Returns the process exit code (<c>1</c>, matching the
/// framework default for parse failures).
/// </summary>
internal static class CliErrorRenderer
{
    private const string Red = "\x1b[31;1m";
    private const string Dim = "\x1b[2m";
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Render <paramref name="parseResult"/>'s errors to
    /// <paramref name="error"/>. <paramref name="useColor"/> gates the
    /// ANSI escapes — callers pass <c>false</c> whenever the stream is
    /// redirected or a test writer so captured output stays plain.
    /// </summary>
    public static int Render(ParseResult parseResult, TextWriter error, bool useColor)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(error);

        string Colour(string code, string s) => useColor ? code + s + Reset : s;

        foreach (var e in parseResult.Errors)
        {
            error.WriteLine(Colour(Red, "✗ ") + e.Message);
        }

        var path = CommandPath(parseResult);
        error.WriteLine();
        error.WriteLine(Colour(Dim, $"Run '{path} --help' for usage."));
        return 1;
    }

    /// <summary>
    /// Space-joined command path from the root down to the deepest
    /// command the parser resolved (e.g. <c>bowire plugin install</c>),
    /// so the help hint targets the sub-command the user was actually
    /// typing rather than the bare root.
    /// </summary>
    private static string CommandPath(ParseResult parseResult)
    {
        var names = new List<string>();
        for (SymbolResult? cr = parseResult.CommandResult; cr is CommandResult c; cr = c.Parent)
        {
            names.Insert(0, c.Command.Name);
        }
        return names.Count == 0 ? "bowire" : string.Join(' ', names);
    }
}
