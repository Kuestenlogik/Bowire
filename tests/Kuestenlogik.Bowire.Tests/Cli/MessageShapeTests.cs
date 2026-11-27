// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// What <c>bowire docs translations --check</c> holds a translation to (#688):
/// the same placeholders as English, and plural blocks with exactly the
/// branches the translation's language has.
/// </summary>
public sealed class MessageShapeTests
{
    private const string EnglishRuns = "{count, plural, one {# run} other {# runs}} in {days} days";

    [Fact]
    public void A_Translation_With_Its_Languages_Categories_Passes()
    {
        Assert.Empty(MessageShape.Problems(EnglishRuns, "{count, plural, one {# Lauf} other {# Läufe}} in {days} Tagen", "de"));
        Assert.Empty(MessageShape.Problems(EnglishRuns,
            "{count, plural, one {# przebieg} few {# przebiegi} many {# przebiegów} other {# przebiegu}} w {days} dni", "pl"));
    }

    [Fact]
    public void A_Dropped_Branch_Fails()
    {
        var problem = Assert.Single(MessageShape.Problems(EnglishRuns, "{count, plural, other {# Läufe}} in {days} Tagen", "de"));
        Assert.Contains("needs one other in de, has other", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Category_The_Language_Lacks_Fails()
    {
        var problem = Assert.Single(MessageShape.Problems(EnglishRuns,
            "{count, plural, one {# Lauf} few {# Läufe} other {# Läufe}} in {days} Tagen", "de"));
        Assert.Contains("has few one other", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void English_Exact_Branches_Must_Stay()
    {
        const string english = "{n, plural, =0 {nothing} one {# left} other {# left}}";
        Assert.NotEmpty(MessageShape.Problems(english, "{n, plural, one {# übrig} other {# übrig}}", "de"));
        Assert.Empty(MessageShape.Problems(english, "{n, plural, =0 {nichts} one {# übrig} other {# übrig}}", "de")); // lang-exempt: a German translation is the data under test
    }

    [Fact]
    public void A_Select_Keeps_Its_Values_And_A_Placeholder_Its_Name()
    {
        const string english = "Delete {kind, select, file {the file} other {it}} in {place}?";
        Assert.Empty(MessageShape.Problems(english, "{kind, select, file {Die Datei} other {Es}} in {place} löschen?", "de")); // lang-exempt: a German translation is the data under test
        Assert.Equal(2, MessageShape.Problems(english, "{kind, select, folder {Den Ordner} other {Es}} in {ort} löschen?", "de").Count); // lang-exempt: a German translation is the data under test
    }

    [Fact]
    public void A_Region_Falls_Back_To_Its_Language_And_An_Unknown_Language_Needs_Other()
    {
        Assert.Equal(["one", "other"], MessageShape.CategoriesOf("de-AT"));
        Assert.Null(MessageShape.CategoriesOf("tlh"));
        Assert.Empty(MessageShape.Problems(EnglishRuns, "{count, plural, one {# x} other {# y}} {days}", "tlh"));
        Assert.NotEmpty(MessageShape.Problems(EnglishRuns, "{count, plural, one {# x} lots {# y}} {days}", "tlh"));
    }

    [Fact]
    public void A_Bowire_Variable_Is_Not_A_Placeholder()
    {
        Assert.Equal(["env"], MessageShape.Placeholders("{{token}} resolves against {env}"));
    }
}
