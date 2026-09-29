// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// #669 — the CLI asks for a star once per machine, after a first run that
/// worked, and stays out of every place where a line of prose would get in
/// the way of someone reading the output as data.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "StringWriter holds no resource")]
public sealed class StarHintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bowire-star-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private StarHint.Environment Env(bool outRedirected = false, bool errRedirected = false, params (string Key, string Value)[] vars)
    {
        var map = vars.ToDictionary(v => v.Key, v => v.Value);
        return new StarHint.Environment(outRedirected, errRedirected, k => map.GetValueOrDefault(k), Path.Combine(_dir, StarHint.MarkerFile));
    }

    [Fact]
    public void A_first_successful_run_shows_it_once_and_never_again()
    {
        var env = Env();
        var first = new StringWriter();
        Assert.True(StarHint.MaybeShow(0, compact: false, first, env));
        Assert.Contains("star on GitHub", first.ToString(), StringComparison.Ordinal);
        Assert.Contains("watching", first.ToString(), StringComparison.Ordinal);

        var second = new StringWriter();
        Assert.False(StarHint.MaybeShow(0, compact: false, second, env));
        Assert.Equal(string.Empty, second.ToString());
    }

    [Fact]
    public void A_failed_run_neither_shows_it_nor_uses_it_up()
    {
        var env = Env();
        Assert.False(StarHint.MaybeShow(1, compact: false, new StringWriter(), env));
        Assert.False(File.Exists(env.MarkerPath));
        Assert.True(StarHint.MaybeShow(0, compact: false, new StringWriter(), env));
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, "CI")]
    [InlineData(false, false, "GITHUB_ACTIONS")]
    [InlineData(false, false, "TF_BUILD")]
    [InlineData(false, false, "BOWIRE_NO_HINTS")]
    public void Redirected_output_CI_and_the_opt_out_keep_it_quiet(bool outRedirected, bool errRedirected, string? variable)
    {
        var env = variable is null ? Env(outRedirected, errRedirected) : Env(outRedirected, errRedirected, (variable, "true"));
        var err = new StringWriter();
        Assert.False(StarHint.MaybeShow(0, compact: false, err, env));
        Assert.Equal(string.Empty, err.ToString());
        Assert.False(File.Exists(env.MarkerPath));
    }

    [Fact]
    public void Compact_output_is_for_machines_and_gets_no_prose()
    {
        var env = Env();
        Assert.False(StarHint.MaybeShow(0, compact: true, new StringWriter(), env));
    }

    [Fact]
    public void When_the_marker_cannot_be_written_the_hint_stays_away_rather_than_repeating()
    {
        // A file where the marker's directory should be.
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "not a directory");
        try
        {
            var err = new StringWriter();
            Assert.False(StarHint.MaybeShow(0, compact: false, err, Env()));
            Assert.Equal(string.Empty, err.ToString());
        }
        finally
        {
            File.Delete(_dir);
        }
    }
}
