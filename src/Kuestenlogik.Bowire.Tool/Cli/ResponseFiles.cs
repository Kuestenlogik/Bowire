// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Response files (#976): <c>bowire @ci.rsp …</c> reads arguments from a file.
/// </summary>
/// <remarks>
/// <para>
/// Only a token <c>@&lt;path&gt;.rsp</c> is one. System.CommandLine's own
/// replacer took every <c>@</c> token, so <c>-d @request.json</c> reached
/// <c>call</c> as that file split on whitespace with its quotes stripped
/// (#974). Every other <c>@</c> token reaches the command as written, and
/// the command reads the file.
/// </para>
/// <para>
/// One argument per line, as it stands: a JSON body with spaces stays one
/// argument, quotes included. Blank lines and lines starting with <c>#</c>
/// are skipped; a line's surrounding whitespace is trimmed. A response file
/// does not expand further response files.
/// </para>
/// <para>
/// Program.cs expands them before anything reads the arguments, so the
/// bootstrap configuration sees <c>--proxy-url</c>, <c>--ca-bundle</c> or
/// <c>--plugin-dir</c> from the file exactly as from the command line.
/// </para>
/// </remarks>
internal static class ResponseFiles
{
    /// <summary>
    /// <paramref name="args"/> with every <c>@file.rsp</c> before <c>--</c>
    /// replaced by its arguments.
    /// </summary>
    /// <returns>The expanded arguments, or <c>null</c> with <paramref name="error"/> set.</returns>
    public static string[]? Expand(string[] args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var result = new List<string>(args.Length);
        var literal = false;
        foreach (var arg in args)
        {
            if (literal || !IsResponseFile(arg))
            {
                literal |= arg == "--";
                result.Add(arg);
                continue;
            }
            var path = arg[1..];
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var token = line.Trim();
                    if (token.Length == 0 || token.StartsWith('#')) continue;
                    result.Add(token);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex is FileNotFoundException or DirectoryNotFoundException
                    ? $"Response file not found: {path}"
                    : $"Failed to read response file '{path}': {ex.Message}";
                return null;
            }
        }
        return [.. result];
    }

    private static bool IsResponseFile(string arg) =>
        arg.Length > 5 && arg[0] == '@' && arg.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase);
}
