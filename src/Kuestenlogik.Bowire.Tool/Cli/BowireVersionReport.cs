// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.InteropServices;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Builds the text for <c>bowire version</c>: the running Bowire version and
/// runtime, and — with <c>--plugins</c> — the loaded protocol plugins and their
/// versions. Split out from <see cref="BowireCli"/> so the formatting is
/// unit-testable without spinning the command-line pipeline.
/// </summary>
internal static class BowireVersionReport
{
    /// <summary>
    /// The running Bowire version — the Tool assembly's informational version,
    /// the same value <c>bowire --version</c> prints (the SourceLink
    /// <c>+&lt;sha&gt;</c> suffix stripped).
    /// </summary>
    public static string AppVersion() => AssemblyVersion(typeof(BowireCli).Assembly);

    /// <summary>Runtime + RID line, e.g. <c>.NET 10.0.0 — win-x64</c>.</summary>
    public static string RuntimeLine()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        return string.IsNullOrEmpty(rid)
            ? RuntimeInformation.FrameworkDescription
            : RuntimeInformation.FrameworkDescription + " — " + rid;
    }

    /// <summary>
    /// Loaded protocol plugins as <c>(id, name, version)</c>, de-duplicated by
    /// id and ordered. The version is each protocol's declaring-assembly
    /// version, so a bundled protocol shows the host version while a
    /// directory-installed third-party plugin shows its own.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name, string Version)> Protocols(IEnumerable<IBowireProtocol> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        return protocols
            .Select(p => (
                p.Id,
                Name: string.IsNullOrEmpty(p.Name) ? p.Id : p.Name,
                Version: AssemblyVersion(p.GetType().Assembly)))
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The full report. When <paramref name="includePlugins"/> is set, a
    /// protocol-plugin table is appended; otherwise only the version + runtime
    /// lines are returned.
    /// </summary>
    public static string Render(bool includePlugins, IEnumerable<IBowireProtocol> protocols)
    {
        var lines = new List<string>
        {
            "Bowire " + AppVersion(),
            RuntimeLine(),
        };

        if (includePlugins)
        {
            var protos = Protocols(protocols);
            lines.Add(string.Empty);
            lines.Add($"Protocol plugins ({protos.Count}):");
            if (protos.Count == 0)
            {
                lines.Add("  (none)");
            }
            else
            {
                var idWidth = protos.Max(p => p.Id.Length);
                var nameWidth = protos.Max(p => p.Name.Length);
                foreach (var p in protos)
                {
                    lines.Add($"  {p.Id.PadRight(idWidth)}  {p.Name.PadRight(nameWidth)}  {p.Version}");
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string AssemblyVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // Drop the SourceLink `+<git-sha>` build-metadata suffix.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
