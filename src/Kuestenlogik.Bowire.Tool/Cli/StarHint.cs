// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// #669 — one line, once per machine, after the first <c>discover</c> or
/// <c>call</c> that worked: star Bowire and watch it.
/// </summary>
/// <remarks>
/// <para>
/// Where it would get in the way, it stays out: never in CI, never when
/// stdout or stderr is redirected (someone is scripting, or reading the
/// output as data), never with <c>--compact</c> (the machine-shaped
/// output), never with <c>BOWIRE_NO_HINTS</c> set. It goes to stderr, so
/// the result on stdout stays exactly what it was.
/// </para>
/// <para>
/// It makes no network request — outbound calls are opt-in in Bowire, and
/// a hint asking for a star is no exception. "Shown" is a marker file in
/// the user's Bowire folder; when that cannot be written, the hint is not
/// shown either, so it can never repeat on every run.
/// </para>
/// </remarks>
internal static class StarHint
{
    internal const string MarkerFile = "star-hint-shown";

    internal const string Text =
        "Bowire useful? A star on GitHub helps others find it, and watching brings you each release: https://github.com/Kuestenlogik/Bowire";

    // The CI systems whose runners set a variable of their own, beside the
    // near-universal CI=true.
    private static readonly string[] s_ciVariables =
        ["CI", "GITHUB_ACTIONS", "TF_BUILD", "GITLAB_CI", "JENKINS_URL", "TEAMCITY_VERSION", "BUILDKITE", "CIRCLECI", "APPVEYOR"];

    /// <summary>What the hint depends on, so tests can set it.</summary>
    internal sealed record Environment(
        bool OutputRedirected,
        bool ErrorRedirected,
        Func<string, string?> Variable,
        string MarkerPath);

    internal static Environment Current() => new(
        Console.IsOutputRedirected,
        Console.IsErrorRedirected,
        System.Environment.GetEnvironmentVariable,
        BowireUserContext.GetUserPath(MarkerFile));

    /// <summary>
    /// Write the hint when this run earned it and nothing speaks against
    /// it; returns whether it was written.
    /// </summary>
    internal static bool MaybeShow(int exitCode, bool compact, TextWriter stderr, Environment? env = null)
    {
        if (exitCode != 0 || compact) return false;
        env ??= Current();
        if (env.OutputRedirected || env.ErrorRedirected) return false;
        if (!string.IsNullOrEmpty(env.Variable("BOWIRE_NO_HINTS"))) return false;
        if (s_ciVariables.Any(v => !string.IsNullOrEmpty(env.Variable(v)))) return false;
        if (File.Exists(env.MarkerPath)) return false;

        // Mark first: a hint that cannot remember it was shown is not shown.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(env.MarkerPath)!);
            File.WriteAllText(env.MarkerPath, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        stderr.WriteLine();
        stderr.WriteLine(Text);
        return true;
    }
}
