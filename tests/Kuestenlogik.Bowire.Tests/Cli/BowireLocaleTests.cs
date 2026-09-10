// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// The command line's half of the translation layer (#117).
/// </summary>
/// <remarks>
/// Two things worth pinning. The resolution order, because a terminal user's
/// expectation about <c>LANG</c> is not a preference we get to reinterpret.
/// And the interpolation, because it shares its one hard rule with the
/// workbench: <c>{{name}}</c> is Bowire's own variable syntax and must survive
/// a translated string unexpanded.
/// </remarks>
public sealed class BowireLocaleTests
{
    // A catalogue that has en and de, and nothing else. Case-insensitive,
    // like the real resource lookup: LANG=DE has to find de.json.
    private static Func<string, string?> Catalogue(params string[] ids)
        => tag => Array.Find(ids, id => string.Equals(id, tag, StringComparison.OrdinalIgnoreCase));

    private static readonly Func<string, string?> Available = Catalogue("en", "de");

    /// <summary>The locales that actually ship today.</summary>
    private static readonly string[] Shipped = ["en", "de"];

    [Fact]
    public void BOWIRE_LOCALE_wins_over_LANG()
    {
        // The specific setting beats the general one. Somebody who sets
        // BOWIRE_LOCALE has said something about Bowire, not about their shell.
        Assert.Equal("de", BowireLocale.ResolveId("de", "en_GB.UTF-8", "en-US", Available));
    }

    [Fact]
    public void LANG_decides_when_BOWIRE_LOCALE_is_unset()
    {
        Assert.Equal("de", BowireLocale.ResolveId(null, "de_DE.UTF-8", "en-US", Available));
    }

    [Fact]
    public void The_OS_culture_is_the_third_choice()
    {
        Assert.Equal("de", BowireLocale.ResolveId(null, null, "de-AT", Available));
    }

    [Fact]
    public void Everything_unset_lands_on_English()
    {
        Assert.Equal("en", BowireLocale.ResolveId(null, null, null, Available));
    }

    [Theory]
    [InlineData("de_DE.UTF-8")]
    [InlineData("de_DE")]
    [InlineData("de-DE")]
    [InlineData("de")]
    [InlineData("DE")]
    [InlineData("de_DE.UTF-8@euro")]
    public void A_POSIX_locale_string_narrows_to_its_language(string lang)
    {
        // The encoding suffix, the modifier and the region come off in turn.
        // A German shell should find de.json without configuring anything.
        Assert.Equal("de", BowireLocale.ResolveId(null, lang, null, Available));
    }

    [Fact]
    public void A_regional_catalogue_beats_its_base_language()
    {
        // de-AT exists here, so an Austrian shell gets it rather than de.
        Assert.Equal("de-AT", BowireLocale.ResolveId(
            null, "de_AT.UTF-8", null, Catalogue("en", "de", "de-AT")));
    }

    [Fact]
    public void An_unknown_language_falls_back_to_English()
    {
        Assert.Equal("en", BowireLocale.ResolveId(null, "ja_JP.UTF-8", null, Available));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("POSIX")]
    [InlineData("")]
    [InlineData("   ")]
    public void C_and_POSIX_mean_no_locale_rather_than_a_language(string lang)
    {
        // "C" is the absence of a locale. Reading it as a language named C
        // would look for c.json and, worse, might one day find one.
        Assert.Equal("en", BowireLocale.ResolveId(null, lang, null, Available));
    }

    // ---- interpolation ----

    [Fact]
    public void Placeholders_are_substituted()
    {
        Assert.Equal("Read 3 now",
            BowireLocale.Interpolate("Read {count} now", ("count", 3)));
    }

    [Fact]
    public void An_unknown_placeholder_stays_visible()
    {
        // A visible {count} in the terminal is a bug report; an empty gap is
        // a mystery, and "" is a lie.
        Assert.Equal("Read {count} now",
            BowireLocale.Interpolate("Read {count} now", ("other", 3)));
    }

    [Fact]
    public void A_Bowire_variable_survives_translation()
    {
        // The hard rule, shared with the workbench: {{token}} is Bowire's own
        // syntax and appears literally inside UI strings. It has to come out
        // the other side untouched even when an argument shares its name.
        Assert.Equal("{{token}} resolves against staging",
            BowireLocale.Interpolate(
                "{{token}} resolves against {env}", ("token", "X"), ("env", "staging")));
    }

    [Fact]
    public void An_unclosed_brace_is_copied_rather_than_swallowed()
    {
        Assert.Equal("half {open", BowireLocale.Interpolate("half {open", ("open", "x")));
    }

    [Fact]
    public void No_arguments_means_no_scanning()
    {
        var text = "A sentence with {braces} and {{doubles}} left alone.";
        Assert.Equal(text, BowireLocale.Interpolate(text));
    }

    // ---- the embedded catalogues ----

    [Fact]
    public void The_catalogues_are_embedded_and_readable()
    {
        // Both locales ship in the assembly now, not just English: the CLI
        // reads whichever one the environment asks for.
        //
        // Which is why this asserts the mechanism rather than a language. The
        // first version expected "Close" and failed on a German development
        // machine with "Schließen" — the feature working, the test wrong. A
        // test that only passes on an English machine is a test that will fail
        // on somebody else's, and CI is not the only place code runs.
        BowireLocale.Reset();

        var text = BowireLocale.T("common.close");
        Assert.NotEqual("common.close", text);
        Assert.NotEmpty(text);
        Assert.Contains(BowireLocale.Active, Shipped);
    }

    [Fact]
    public void A_missing_key_renders_as_itself()
    {
        BowireLocale.Reset();
        Assert.Equal("nothing.here.at.all", BowireLocale.T("nothing.here.at.all"));
    }
}
