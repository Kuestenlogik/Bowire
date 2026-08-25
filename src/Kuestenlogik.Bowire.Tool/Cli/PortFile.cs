// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// The document <c>--port-file</c> writes once the workbench is bound.
/// </summary>
/// <remarks>
/// JSON rather than a bare port number so the shape can grow without a
/// format break, and versioned so a reader can say "I don't know this"
/// instead of guessing. <see cref="Pid"/> is what lets a reader tell a
/// live file from one a killed process left behind.
/// </remarks>
internal sealed record PortFileDocument
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>The address Kestrel actually bound, with its real port.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    /// <summary>Process that owns this file — a reader's staleness check.</summary>
    [JsonPropertyName("pid")]
    public int Pid { get; init; }
}

/// <summary>
/// Publishes the bound workbench URL to a file a caller named with
/// <c>--port-file</c> (#615).
/// </summary>
/// <remarks>
/// <para>
/// The console banner cannot carry this: it is a log line, so it is gone at
/// a quieter level, and it is printed before the bind is known to have
/// worked — a Bowire started on a taken port announces a URL and then throws
/// <c>AddressInUseException</c>. Anything that trusts it opens a dead page.
/// </para>
/// <para>
/// The contract this class implements instead is the one Chrome's
/// <c>DevToolsActivePort</c> and Jupyter's <c>jpserver-&lt;pid&gt;.json</c>
/// use, and it is deliberately narrow: <b>the file exists if and only if the
/// workbench is bound</b>. Existence is therefore the readiness signal, which
/// is what makes <c>--port 0</c> usable — the OS picks a free port, so the
/// race between choosing a port and binding it disappears, and the caller
/// reads back where it landed.
/// </para>
/// <para>
/// Holding that contract takes work at both ends of the process lifetime:
/// </para>
/// <list type="bullet">
///   <item><see cref="Clear"/> before binding, so a file from a previous run
///   can never be mistaken for this one. Whatever the last process left — a
///   stale URL, a port nothing serves — is gone before we try.</item>
///   <item><see cref="Write"/> only after the bind, atomically (temp +
///   replace) so a reader polling for the file never catches it half
///   written.</item>
///   <item><see cref="Clear"/> again on shutdown, wired to both the graceful
///   stop and <c>ProcessExit</c>.</item>
/// </list>
/// <para>
/// A hard kill (SIGKILL, Task Manager, a pulled plug) still leaves the file:
/// no in-process cleanup survives that, which is why <see cref="PortFileDocument.Pid"/> is in
/// the document. A reader that finds a file it did not just watch appear
/// should treat it as a hint — check the pid, then probe the URL — rather
/// than as proof. The reader that starts Bowire itself has the stronger
/// guarantee available and should use it: delete the file, then wait for it
/// to reappear.
/// </para>
/// </remarks>
internal static class PortFile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Write the bound URL, atomically. Returns false if the path could not
    /// be written — a caller that asked for a port file and did not get one
    /// deserves to hear about it, but not by losing a workbench that is
    /// otherwise running fine.
    /// </summary>
    public static bool Write(string path, string url, int pid)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var payload = JsonSerializer.Serialize(
                new PortFileDocument { Url = url, Pid = pid }, Json);

            // Temp + move, not a plain write: a reader is polling for this
            // path and would otherwise be able to open it between create and
            // flush, and read valid-looking JSON that is missing its tail.
            // The temp file is a sibling so the move stays on one volume,
            // where it is atomic.
            var temp = path + ".tmp" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(temp, payload);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Remove the file if it is there. Used both before binding (so a
    /// previous run's leftovers cannot be read as ours) and on shutdown.
    /// </summary>
    public static void Clear(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Best effort on both call sites. Refusing to start because a
            // stale file is locked would be worse than starting, and failing
            // a shutdown over it worse still.
        }
    }
}
