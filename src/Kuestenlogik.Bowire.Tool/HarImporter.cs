// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// CLI shell around <see cref="BowireHarConverter"/> — adds file-IO,
/// stdout/stderr plumbing, and the exit-code shape the
/// <c>bowire import har</c> subcommand expects. The mapping logic lives
/// in the core <c>BowireHarConverter</c> so both this CLI and the MCP
/// tool (<c>bowire.har.import</c>) share one converter.
/// </summary>
internal static class HarImporter
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Convert a HAR document string. Re-exposed here so the existing
    /// CLI tests keep their existing entry point — delegates to
    /// <see cref="BowireHarConverter.Convert"/> and re-wraps the core
    /// exception so callers see the CLI-namespaced <see cref="HarImportException"/>.
    /// </summary>
    public static BowireRecording Convert(string harJson, string? recordingName = null)
    {
        try
        {
            return BowireHarConverter.Convert(harJson, recordingName);
        }
        catch (BowireHarImportException ex)
        {
            throw new HarImportException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Test seam — delegates to <see cref="BowireHarConverter.SplitUrl"/>.
    /// </summary>
    internal static (string Path, string? Host) SplitUrl(string url)
        => BowireHarConverter.SplitUrl(url);

    /// <summary>
    /// Test seam — delegates to <see cref="BowireHarConverter.DeriveServiceAndMethod"/>.
    /// </summary>
    internal static (string Service, string Method) DeriveServiceAndMethod(string path, string verb)
        => BowireHarConverter.DeriveServiceAndMethod(path, verb);

    /// <summary>
    /// Convenience wrapper that reads a HAR file from disk and writes the
    /// resulting recording as JSON to <paramref name="outPath"/>. Pass
    /// <c>"-"</c> as <paramref name="outPath"/> to stream to stdout (useful
    /// for piping into <c>bowire mock --recording -</c>).
    /// </summary>
    /// <returns>0 on success, non-zero on failure (CLI exit-code shape).</returns>
    public static async Task<int> ImportAsync(
        string harPath, string outPath, string? recordingName,
        TextWriter? stdout = null, TextWriter? stderr = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outPath);
        var outW = stdout ?? Console.Out;
        var errW = stderr ?? Console.Error;

        if (!File.Exists(harPath))
        {
            await errW.WriteLineAsync($"HAR file not found: {harPath}").ConfigureAwait(false);
            return 1;
        }

        BowireRecording recording;
        try
        {
            var content = await File.ReadAllTextAsync(harPath).ConfigureAwait(false);
            recording = BowireHarConverter.Convert(content, recordingName);
        }
        catch (BowireHarImportException ex)
        {
            await errW.WriteLineAsync($"HAR import failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var json = JsonSerializer.Serialize(recording, IndentedJson);

        if (outPath == "-")
        {
            await outW.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outPath, json).ConfigureAwait(false);
            await outW.WriteLineAsync(
                $"Imported {recording.Steps.Count} {(recording.Steps.Count == 1 ? "step" : "steps")} → {outPath}")
                .ConfigureAwait(false);
        }

        return 0;
    }
}

/// <summary>
/// Compat alias — the public type test code references. Delegates to
/// <see cref="BowireHarImportException"/> in the core library, but kept
/// as a distinct type so existing <c>Assert.Throws&lt;HarImportException&gt;</c>
/// tests stay valid.
/// </summary>
internal sealed class HarImportException : Exception
{
    public HarImportException() { }
    public HarImportException(string message) : base(message) { }
    public HarImportException(string message, Exception inner) : base(message, inner) { }
}
