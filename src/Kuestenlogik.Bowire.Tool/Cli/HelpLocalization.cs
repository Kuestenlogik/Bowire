// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Text;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// #690 — `--help` in one language. The descriptions come from Bowire's
/// catalogues; the section headings ("Usage:", "Options:") from
/// System.CommandLine's own resources, which follow the UI culture. Two
/// things keep them together:
/// </summary>
/// <list type="bullet">
/// <item>the process's UI culture follows <see cref="BowireLocale.Active"/>,
/// so `BOWIRE_LOCALE=en` on a German Windows prints "Usage:", not "Nutzung:";</item>
/// <item>System.CommandLine's German resources leave the heading
/// "Description:" untranslated (2.0.14), and the help layout API that could
/// replace it takes an internal type. So a help invocation, and only that,
/// writes through <see cref="HeadingWriter"/>, which swaps that one line for
/// the catalogue's text.</item>
/// </list>
internal static class HelpLocalization
{
    /// <summary>The heading as System.CommandLine writes it in every language it ships.</summary>
    internal const string UntranslatedHeading = "Description:";

    /// <summary>Make the UI culture, and so System.CommandLine's headings, follow Bowire's locale.</summary>
    public static void ApplyCulture()
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(BowireLocale.Active);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // A catalogue id the runtime does not know as a culture: the
            // headings stay in the system's language, the texts are Bowire's.
        }
    }

    /// <summary>The writer for a help invocation: <paramref name="inner"/> with the heading translated.</summary>
    public static TextWriter ForHelp(ParseResult parseResult, TextWriter inner)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return parseResult.Action is HelpAction ? new HeadingWriter(inner, BowireLocale.T("cli.help.description")) : inner;
    }

    /// <summary>
    /// Passes every line through, except one that is exactly the untranslated
    /// heading. Buffers to the end of a line, so a heading written in pieces
    /// is still recognised.
    /// </summary>
    internal sealed class HeadingWriter(TextWriter inner, string heading) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var text = _line.ToString();
                var cr = text.EndsWith('\r');
                var bare = cr ? text[..^1] : text;
                inner.Write(bare == UntranslatedHeading ? heading : bare);
                inner.Write(cr ? "\r\n" : "\n");
                _line.Clear();
                return;
            }
            _line.Append(value);
        }

        public override void Flush()
        {
            if (_line.Length > 0)
            {
                inner.Write(_line.ToString());
                _line.Clear();
            }
            inner.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Flush();
            base.Dispose(disposing);
        }
    }
}
