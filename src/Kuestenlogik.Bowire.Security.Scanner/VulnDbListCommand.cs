// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>Options for <c>bowire vulndb list</c>.</summary>
public sealed record VulnDbListOptions
{
    /// <summary>Cache root to read; unset → <c>~/.bowire/vulndb</c>.</summary>
    public string? Dest { get; init; }

    /// <summary>Only list templates whose folder-protocol equals this (case-insensitive).</summary>
    public string? Protocol { get; init; }
}

/// <summary>
/// <c>bowire vulndb list</c> — show what's in the local template cache
/// (<c>~/.bowire/vulndb</c>): one row per template with its protocol,
/// severity, id, and name, plus a total. Reads the <c>templates-index.json</c>
/// sidecar when present, else walks the tree — so a cache populated by a bare
/// <c>git clone</c> still lists.
/// </summary>
public static class VulnDbListCommand
{
    /// <summary>Run the list. Returns 0 always — an empty cache is reported, not an error.</summary>
    public static async Task<int> RunAsync(
        VulnDbListOptions options,
        CancellationToken ct,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = ct;
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        var dest = string.IsNullOrWhiteSpace(options.Dest) ? VulnDbCache.DefaultRoot() : options.Dest;

        if (!VulnDbCache.HasTemplates(dest))
        {
            await stderr.WriteLineAsync($"  No template cache at {dest}. Run `bowire vulndb update` to populate it.").ConfigureAwait(false);
            return 0;
        }

        var rows = VulnDbCache.EnumerateTemplates(dest);
        if (!string.IsNullOrWhiteSpace(options.Protocol))
        {
            rows = [.. rows.Where(r =>
                string.Equals(r.Protocol, options.Protocol, StringComparison.OrdinalIgnoreCase))];
        }

        if (rows.Count == 0)
        {
            await stdout.WriteLineAsync(string.IsNullOrWhiteSpace(options.Protocol)
                ? $"  Cache at {dest} holds no templates."
                : $"  No '{options.Protocol}' templates in the cache at {dest}.").ConfigureAwait(false);
            return 0;
        }

        // Left-pad the two fixed columns to the widest value so the id/name
        // start on aligned columns regardless of protocol/severity length.
        var protoWidth = rows.Max(r => (r.Protocol ?? "").Length);
        var sevWidth = rows.Max(r => (r.Severity ?? "").Length);

        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"  {rows.Count} template(s) in {dest}:")).ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);
        foreach (var r in rows)
        {
            var proto = (r.Protocol ?? "").PadRight(protoWidth);
            var sev = (r.Severity ?? "").PadRight(sevWidth);
            await stdout.WriteLineAsync($"  {proto}  {sev}  {r.Id}").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(r.Name))
            {
                await stdout.WriteLineAsync($"  {new string(' ', protoWidth)}  {new string(' ', sevWidth)}  {r.Name}").ConfigureAwait(false);
            }
        }
        return 0;
    }
}
