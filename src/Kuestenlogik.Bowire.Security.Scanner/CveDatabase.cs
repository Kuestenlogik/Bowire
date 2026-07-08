// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// One known-vulnerability record keyed to a server product + version range —
/// the shape a <c>Bowire.VulnDb</c> CVE file publishes and the CVE-lookup probe
/// (<see cref="ServerCveProbe"/>, #187) consumes.
/// </summary>
/// <param name="Product">Product name matched (case-insensitively) against the parsed <c>Server</c> / <c>X-Powered-By</c> banner, e.g. <c>nginx</c>.</param>
/// <param name="Introduced">First affected version (inclusive). Null = unbounded below.</param>
/// <param name="Fixed">First fixed version (exclusive). Null = unbounded above.</param>
/// <param name="Cve">CVE identifier, e.g. <c>CVE-2021-23017</c>.</param>
/// <param name="Severity">low / medium / high / critical.</param>
/// <param name="Cvss">Optional CVSS base score.</param>
/// <param name="Title">One-line description.</param>
/// <param name="Reference">Advisory URL.</param>
public sealed record CveEntry(
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("cve")] string Cve,
    [property: JsonPropertyName("introduced")] string? Introduced = null,
    [property: JsonPropertyName("fixed")] string? Fixed = null,
    [property: JsonPropertyName("severity")] string Severity = "high",
    [property: JsonPropertyName("cvss")] double? Cvss = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("reference")] string? Reference = null);

/// <summary>
/// A loadable set of <see cref="CveEntry"/> records + version-range matching for
/// the CVE-lookup probe (#187). Load a curated <c>Bowire.VulnDb</c> file with
/// <see cref="Load"/> / <see cref="Parse"/>, or use <see cref="Seed"/> — a tiny
/// built-in set of well-known server CVEs so the probe does something useful
/// out of the box.
/// </summary>
public sealed class CveDatabase
{
    private readonly IReadOnlyList<CveEntry> _entries;

    public CveDatabase(IReadOnlyList<CveEntry> entries) => _entries = entries;

    public int Count => _entries.Count;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parse a VulnDb document — either a bare <c>[ … ]</c> array or <c>{ "entries": [ … ] }</c>.</summary>
    public static CveDatabase Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var trimmed = json.TrimStart();
        var entries = trimmed.StartsWith('[')
            ? JsonSerializer.Deserialize<List<CveEntry>>(json, s_json)
            : JsonSerializer.Deserialize<Document>(json, s_json)?.Entries;
        return new CveDatabase(entries ?? []);
    }

    /// <summary>Load a VulnDb file from disk.</summary>
    public static CveDatabase Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// CVEs whose product matches <paramref name="product"/> and whose range
    /// contains <paramref name="version"/> (<c>[introduced, fixed)</c>).
    /// </summary>
    public IReadOnlyList<CveEntry> Match(string product, string version)
    {
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(version)) return [];
        var hits = new List<CveEntry>();
        foreach (var e in _entries)
        {
            if (!string.Equals(e.Product, product, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Introduced is not null && CompareVersions(version, e.Introduced) < 0) continue;
            if (e.Fixed is not null && CompareVersions(version, e.Fixed) >= 0) continue;
            hits.Add(e);
        }
        return hits;
    }

    /// <summary>
    /// Dotted-numeric version compare (<c>1.18.0</c> vs <c>1.20.1</c>). Missing
    /// components count as 0; non-numeric components compare as 0 so a build
    /// suffix never makes a version look higher than a clean release.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = Split(a);
        var pb = Split(b);
        var n = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;

        static int[] Split(string v)
        {
            var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var nums = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                // Take the leading digits of each component ("8u151" → 8, "49" → 49).
                var digits = 0;
                var any = false;
                foreach (var c in parts[i])
                {
                    if (!char.IsDigit(c)) break;
                    digits = (digits * 10) + (c - '0');
                    any = true;
                }
                nums[i] = any ? digits : 0;
            }
            return nums;
        }
    }

    /// <summary>
    /// A tiny built-in set of well-known server CVEs — enough for the probe to
    /// be useful without a curated file. A full corpus lives in the community
    /// <c>Bowire.VulnDb</c> repo; load it with <c>--cve-db</c>.
    /// </summary>
    public static CveDatabase Seed() => new(
    [
        new("nginx", "CVE-2021-23017", Introduced: "0.6.18", Fixed: "1.21.0", Severity: "high", Cvss: 7.7,
            Title: "nginx resolver off-by-one heap write",
            Reference: "https://nvd.nist.gov/vuln/detail/CVE-2021-23017"),
        new("Apache", "CVE-2021-41773", Introduced: "2.4.49", Fixed: "2.4.50", Severity: "critical", Cvss: 9.8,
            Title: "Apache httpd path traversal + RCE (mod_cgi)",
            Reference: "https://nvd.nist.gov/vuln/detail/CVE-2021-41773"),
        new("Apache", "CVE-2021-42013", Introduced: "2.4.49", Fixed: "2.4.51", Severity: "critical", Cvss: 9.8,
            Title: "Apache httpd path traversal + RCE (incomplete 41773 fix)",
            Reference: "https://nvd.nist.gov/vuln/detail/CVE-2021-42013"),
        new("OpenSSH", "CVE-2024-6387", Introduced: "8.5", Fixed: "9.8", Severity: "high", Cvss: 8.1,
            Title: "OpenSSH sshd signal-handler race (regreSSHion)",
            Reference: "https://nvd.nist.gov/vuln/detail/CVE-2024-6387"),
    ]);

    private sealed record Document([property: JsonPropertyName("entries")] List<CveEntry>? Entries);
}
