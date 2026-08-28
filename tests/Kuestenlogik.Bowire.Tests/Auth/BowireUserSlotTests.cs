// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests.Auth;

/// <summary>
/// The mapping from an authenticated subject to the directory that holds its
/// state (#97).
/// </summary>
public sealed class BowireUserSlotTests
{
    [Fact]
    public void An_Operator_Can_Recognise_Whose_Slot_It_Is()
    {
        // The readable half is the whole reason this is not just a hash: a
        // support request that starts "delete my recordings" is answered by
        // finding a directory, not by querying a mapping table.
        var slug = BowireUserSlot.Slug("ada@example.com");

        Assert.StartsWith("ada-example.com-", slug, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_Subjects_That_Sanitise_Alike_Do_Not_Share_A_Slot()
    {
        // The one failure here that would be a security bug rather than an
        // annoyance: both of these sanitise to "a-b-example.com", and a shared
        // slot means each reads the other's environments — secrets included.
        var first = BowireUserSlot.Slug("a.b@example.com");
        var second = BowireUserSlot.Slug("a-b@example.com");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_Same_Subject_Always_Lands_In_The_Same_Slot()
    {
        // Not a nicety: a slug that varied per process would strand the
        // previous run's data in a directory nothing reads again.
        Assert.Equal(
            BowireUserSlot.Slug("auth0|5f3c9a"),
            BowireUserSlot.Slug("auth0|5f3c9a"));
    }

    [Fact]
    public void Surrounding_Whitespace_Does_Not_Make_A_Second_Slot()
        => Assert.Equal(
            BowireUserSlot.Slug("ada@example.com"),
            BowireUserSlot.Slug("  ada@example.com  "));

    [Theory]
    [InlineData("auth0|5f3c9a")]
    [InlineData("https://idp.example.com/users/17")]
    [InlineData("CN=Ada, OU=Eng")]
    [InlineData("../../etc/passwd")]
    [InlineData("ünïcodé")]
    public void Whatever_The_Provider_Issued_Becomes_One_Safe_Segment(string subject)
    {
        var slug = BowireUserSlot.Slug(subject);

        Assert.Equal(slug, Path.GetFileName(slug));
        Assert.False(Path.IsPathRooted(slug));
        Assert.DoesNotContain("..", slug, StringComparison.Ordinal);
        Assert.All(slug, ch => Assert.True(
            ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-',
            $"'{ch}' has no business in a directory name"));
    }

    [Fact]
    public void A_Slot_Name_Never_Ends_In_A_Dot()
    {
        // Windows strips a trailing dot when creating the directory, so a name
        // ending in one is not the name that exists — the lookup afterwards
        // misses and the slot looks empty.
        var slug = BowireUserSlot.Slug("ada.");

        Assert.StartsWith("ada-", slug, StringComparison.Ordinal);
        Assert.DoesNotContain(".-", slug, StringComparison.Ordinal);
        Assert.False(slug.EndsWith('.'));
    }

    [Fact]
    public void A_Subject_With_Nothing_Readable_In_It_Still_Gets_A_Slot()
    {
        // "|||" sanitises to nothing. A directory named after the empty string
        // is not a directory, so the fingerprint has to be able to stand alone.
        var slug = BowireUserSlot.Slug("|||");

        Assert.StartsWith("user-", slug, StringComparison.Ordinal);
        Assert.True(slug.Length > "user-".Length);
    }

    [Fact]
    public void A_Very_Long_Subject_Is_Shortened_Without_Colliding()
    {
        // Truncation is what makes two long subjects look alike; the
        // fingerprint is taken over the original, so it keeps them apart.
        var first = BowireUserSlot.Slug(new string('a', 300) + "-one");
        var second = BowireUserSlot.Slug(new string('a', 300) + "-two");

        Assert.True(first.Length < 80, $"slug is {first.Length} characters");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Case_Alone_Still_Separates_Two_Identities()
    {
        // The readable half is lower-cased, and on Windows two directories
        // differing only in case are one directory — so this pair would
        // collide if the fingerprint did not follow the untouched subject.
        Assert.NotEqual(BowireUserSlot.Slug("Ada"), BowireUserSlot.Slug("ada"));
    }

    [Fact]
    public void Nothing_At_All_Is_Refused_Rather_Than_Given_A_Slot()
    {
        Assert.Throws<ArgumentNullException>(() => BowireUserSlot.Slug(null!));
        Assert.Throws<ArgumentException>(() => BowireUserSlot.Slug("   "));
    }
}
