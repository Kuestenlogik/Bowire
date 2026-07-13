// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// The on-disk template cache the <c>bowire vulndb</c> commands manage and
/// <c>bowire scan</c> reads by default: <c>~/.bowire/vulndb</c>, mirroring
/// the layout of the <c>Kuestenlogik/Bowire.VulnDb</c> repo / release
/// tarball — a <c>templates/&lt;protocol&gt;/&lt;name&gt;.json</c> tree plus
/// the <c>templates-index.json</c> metadata sidecar. <c>bowire vulndb update</c>
/// populates it; <c>bowire vulndb list</c> reads it; a template-source-less
/// <c>bowire scan</c> falls back to it so operators run one update and then
/// scan without repeating <c>--templates</c>.
/// </summary>
public static class VulnDbCache
{
    /// <summary>
    /// The cache root — <c>~/.bowire/vulndb</c>, or a temp fallback when the
    /// user-profile folder can't be resolved (headless / sandboxed CI).
    /// Mirrors the <c>~/.bowire/monitoring</c> resolution used elsewhere.
    /// </summary>
    public static string DefaultRoot()
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.None);
        return string.IsNullOrEmpty(home)
            ? Path.Combine(Path.GetTempPath(), "bowire", "vulndb")
            : Path.Combine(home, ".bowire", "vulndb");
    }

    /// <summary>The <c>templates/</c> tree under a cache root.</summary>
    public static string TemplatesDir(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(root, "templates");
    }

    /// <summary>The <c>templates-index.json</c> sidecar path under a cache root.</summary>
    public static string IndexPath(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(root, "templates-index.json");
    }

    /// <summary>
    /// True when the cache root holds a usable template tree — the
    /// <c>templates/</c> directory exists and contains at least one
    /// <c>*.json</c> file. The scan default-resolution gate.
    /// </summary>
    public static bool HasTemplates(string root)
    {
        var dir = TemplatesDir(root);
        if (!Directory.Exists(dir)) return false;
        try
        {
            return Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Any();
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Number of <c>*.json</c> templates under the cache's tree (0 when absent).</summary>
    public static int CountTemplates(string root)
    {
        var dir = TemplatesDir(root);
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Count();
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static readonly JsonSerializerOptions s_indexJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Read the cache's <c>templates-index.json</c>, or <c>null</c> when it's
    /// absent / unparseable — the list command falls back to walking the tree
    /// so a cache populated from a bare <c>git clone</c> (no generated index)
    /// still lists.
    /// </summary>
    public static VulnDbIndex? ReadIndex(string root)
    {
        var path = IndexPath(root);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<VulnDbIndex>(File.ReadAllText(path), s_indexJson);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerate the cached templates' summary rows. Prefers the index; when
    /// it's missing, walks <c>templates/</c> and reads each file's
    /// identifying fields directly. Ordered by protocol then id so the list
    /// output is stable.
    /// </summary>
    public static IReadOnlyList<VulnDbIndexEntry> EnumerateTemplates(string root)
    {
        var index = ReadIndex(root);
        if (index?.Templates is { Count: > 0 } fromIndex)
        {
            return [.. fromIndex
                .OrderBy(e => e.Protocol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)];
        }

        var dir = TemplatesDir(root);
        if (!Directory.Exists(dir)) return [];

        var rows = new List<VulnDbIndexEntry>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var v = r.TryGetProperty("vulnerability", out var vuln) ? vuln : default;
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                rows.Add(new VulnDbIndexEntry
                {
                    Id = r.TryGetProperty("id", out var id) ? id.GetString() : null,
                    Name = r.TryGetProperty("name", out var name) ? name.GetString() : null,
                    Path = rel,
                    Protocol = rel.Split('/')[0],
                    Severity = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("severity", out var sev)
                        ? sev.GetString() : null,
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Skip an unreadable / malformed file — the list is
                // best-effort over whatever the cache actually holds.
            }
        }
        return [.. rows
            .OrderBy(e => e.Protocol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>The <c>templates-index.json</c> document shape (schema 1).</summary>
public sealed record VulnDbIndex
{
    [JsonPropertyName("schema")] public int Schema { get; init; }
    [JsonPropertyName("generatedAt")] public long GeneratedAt { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("templates")] public IReadOnlyList<VulnDbIndexEntry> Templates { get; init; } = [];
}

/// <summary>One row of the template index — the per-template summary metadata.</summary>
public sealed record VulnDbIndexEntry
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("protocol")] public string? Protocol { get; init; }
    [JsonPropertyName("protocols")] public IReadOnlyList<string> Protocols { get; init; } = [];
    [JsonPropertyName("severity")] public string? Severity { get; init; }
    [JsonPropertyName("cvss")] public double? Cvss { get; init; }
    [JsonPropertyName("cwe")] public string? Cwe { get; init; }
    [JsonPropertyName("owaspApi")] public string? OwaspApi { get; init; }
}
