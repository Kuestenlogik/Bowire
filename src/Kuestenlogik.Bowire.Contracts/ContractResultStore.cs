// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Contracts;

/// <summary>
/// On-disk store of contract-verification results (#364), under
/// <c>.bowire/contract-results/</c>. One JSON file per consumer × provider
/// pair, overwritten by the newest run.
/// <para>
/// This is what makes the matrix possible without the workbench reaching
/// out on its own: <c>bowire contract verify</c> (locally or in CI) writes
/// results here, and the matrix endpoint only ever reads them. Rendering a
/// grid never triggers an outbound call to someone's provider — outbound
/// stays opt-in and explicit, exactly as on the CLI.
/// </para>
/// </summary>
public static class ContractResultStore
{
    /// <summary>Directory name under the project's <c>.bowire</c> folder.</summary>
    public const string DirectoryName = "contract-results";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Resolve the results directory for <paramref name="rootPath"/> (the
    /// project root; defaults to the current directory).
    /// </summary>
    public static string ResolveDirectory(string? rootPath = null)
        => Path.Combine(rootPath ?? Directory.GetCurrentDirectory(), ".bowire", DirectoryName);

    /// <summary>
    /// Write <paramref name="report"/> as the latest result for its
    /// consumer × provider pair, creating the directory when needed.
    /// Returns the file written.
    /// </summary>
    public static async Task<string> SaveAsync(
        ContractVerificationReport report, string? rootPath = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var dir = ResolveDirectory(rootPath);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileNameFor(report.Consumer, report.Provider));
        var json = JsonSerializer.Serialize(report, JsonOpts);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Read every stored result. Missing directory yields an empty list —
    /// an operator who has never run a verification sees an empty matrix,
    /// not an error. Individual unreadable / malformed files are skipped so
    /// one bad file can't blank the whole grid.
    /// </summary>
    public static async Task<List<ContractVerificationReport>> LoadAllAsync(
        string? rootPath = null, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(rootPath);
        var reports = new List<ContractVerificationReport>();
        if (!Directory.Exists(dir)) return reports;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var report = JsonSerializer.Deserialize<ContractVerificationReport>(json, JsonOpts);
                if (report is not null) reports.Add(report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Skip the unreadable file; the rest of the matrix stands.
            }
        }
        return reports;
    }

    /// <summary>
    /// Stable, filesystem-safe file name for a consumer × provider pair.
    /// Characters that are illegal in a path are replaced with '_'.
    /// </summary>
    internal static string FileNameFor(string consumer, string provider)
        => Sanitise(consumer) + "__" + Sanitise(provider) + ".json";

    private static string Sanitise(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c).ToArray();
        return new string(chars);
    }
}
