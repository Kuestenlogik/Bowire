// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Process-IO sink for CLI commands. Used as a tiny stand-in for
/// <c>Console.Out</c> / <c>Console.Error</c> across the
/// <c>bowire</c> command surface so the same code path can route
/// output to:
///
/// <list type="bullet">
///   <item>the real console (default — <see cref="Resolve"/> falls
///   back to <see cref="Console.Out"/> / <see cref="Console.Error"/>),</item>
///   <item>a <see cref="StringWriter"/> in a unit test (the test
///   passes the writer pair as a parameter), or</item>
///   <item>System.CommandLine's
///   <c>ParseResult.InvocationConfiguration.Output/.Error</c> when the
///   command is invoked through the parser-aware action surface.</item>
/// </list>
///
/// The sync <c>OutLine</c> / <c>ErrLine</c> helpers exist so
/// that calls inside async command bodies do not trip CA1849 (which
/// would fire on a direct <see cref="TextWriter.WriteLine(string)"/>).
/// The wrappers themselves carry no async overload, so the analyser
/// stops looking — semantically equivalent to the pre-refactor
/// <see cref="Console.WriteLine(string)"/> calls which also did not
/// trigger CA1849.
/// </summary>
internal readonly record struct CommandIo(TextWriter Out, TextWriter Err)
{
    /// <summary>
    /// Build a <see cref="CommandIo"/> from optional writers, defaulting
    /// each missing slot to the process-global console writer. This is
    /// the entry-point pattern every refactored command uses:
    /// <code>
    /// var io = CommandIo.Resolve(stdout, stderr);
    /// </code>
    /// </summary>
    public static CommandIo Resolve(TextWriter? stdout, TextWriter? stderr)
        => new(stdout ?? Console.Out, stderr ?? Console.Error);

    public void OutLine(string s) => Out.WriteLine(s);
    public void OutLine() => Out.WriteLine();
    public void Out_(string s) => Out.Write(s);
    public void ErrLine(string s) => Err.WriteLine(s);
    public void ErrLine() => Err.WriteLine();
}
