// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Wire-shape tests for the <see cref="CookieSnapshot"/> record — the
/// shape returned by the cookie-jar inspect endpoint. Covers ctor +
/// equality + with-expression so a silent rename in <c>CookieJar.cs</c>
/// can't break the JS-side cookie display without a corresponding test
/// failure.
/// </summary>
public class CookieSnapshotTests
{
    [Fact]
    public void Snapshot_Carries_All_Fields()
    {
        var expires = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var snap = new CookieSnapshot(
            Domain: ".example.com",
            Path: "/",
            Name: "session",
            Value: "abc123",
            Expires: expires,
            Secure: true,
            HttpOnly: true);

        Assert.Equal(".example.com", snap.Domain);
        Assert.Equal("/", snap.Path);
        Assert.Equal("session", snap.Name);
        Assert.Equal("abc123", snap.Value);
        Assert.Equal(expires, snap.Expires);
        Assert.True(snap.Secure);
        Assert.True(snap.HttpOnly);
    }

    [Fact]
    public void Snapshot_Equality_By_Value()
    {
        var t = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = new CookieSnapshot("d", "/", "n", "v", t, false, false);
        var b = new CookieSnapshot("d", "/", "n", "v", t, false, false);
        var c = new CookieSnapshot("d", "/", "n", "different", t, false, false);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Snapshot_With_Expression_Replaces_Single_Field()
    {
        var t = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var original = new CookieSnapshot("a.example", "/", "x", "y", t, false, false);
        var rotated = original with { Value = "rotated" };

        Assert.Equal("a.example", rotated.Domain);
        Assert.Equal("rotated", rotated.Value);
        Assert.NotEqual(original, rotated);
    }

    [Fact]
    public void Snapshot_Insecure_HttpOnly_Flags_Persist_Independently()
    {
        var t = DateTime.UtcNow;

        var bothFalse = new CookieSnapshot("d", "/", "n", "v", t, false, false);
        var secureOnly = new CookieSnapshot("d", "/", "n", "v", t, true, false);
        var httpOnlyOnly = new CookieSnapshot("d", "/", "n", "v", t, false, true);
        var bothTrue = new CookieSnapshot("d", "/", "n", "v", t, true, true);

        Assert.False(bothFalse.Secure); Assert.False(bothFalse.HttpOnly);
        Assert.True(secureOnly.Secure); Assert.False(secureOnly.HttpOnly);
        Assert.False(httpOnlyOnly.Secure); Assert.True(httpOnlyOnly.HttpOnly);
        Assert.True(bothTrue.Secure); Assert.True(bothTrue.HttpOnly);
    }
}
