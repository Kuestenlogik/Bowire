// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>Options for <c>bowire vulndb update</c>.</summary>
public sealed record VulnDbUpdateOptions
{
    /// <summary>
    /// Where to pull the templates from. Four accepted shapes:
    /// <list type="bullet">
    /// <item>unset → the latest <c>Kuestenlogik/Bowire.VulnDb</c> GitHub release
    /// (or the <see cref="Ref"/> tag) — the only shape that makes an outbound call;</item>
    /// <item>a local <b>directory</b> (a repo checkout / air-gapped mirror) — copied verbatim;</item>
    /// <item>a local <c>.tar.gz</c> <b>file</b> (a downloaded release tarball) — extracted;</item>
    /// <item>an <c>http(s)</c> <b>URL</b> to a <c>.tar.gz</c> — downloaded + extracted.</item>
    /// </list>
    /// </summary>
    public string? Source { get; init; }

    /// <summary>Cache root to write into; unset → <c>~/.bowire/vulndb</c>.</summary>
    public string? Dest { get; init; }

    /// <summary>
    /// Release tag to pin when resolving from GitHub (e.g. <c>v0.1.0</c>);
    /// unset → the latest release. Ignored for directory / file / URL sources.
    /// </summary>
    public string? Ref { get; init; }
}

/// <summary>
/// <c>bowire vulndb update</c> — refresh the local template cache
/// (<c>~/.bowire/vulndb</c>) from the curated <c>Kuestenlogik/Bowire.VulnDb</c>
/// template set. Split out of the CLI command so it's drivable with a pinned
/// <see cref="TextWriter"/> and an injected <see cref="HttpMessageHandler"/>;
/// the directory- and tarball-source paths need no network at all, so the core
/// logic is unit-tested offline.
/// </summary>
public static class VulnDbUpdateCommand
{
    private const string DefaultRepo = "Kuestenlogik/Bowire.VulnDb";
    private const string TarballAssetPrefix = "bowire-vulndb-templates-";

    /// <summary>
    /// Run the update. Returns a process exit code: 0 = the cache was
    /// refreshed, 1 = the source couldn't be resolved / fetched, 2 = a usage
    /// error (e.g. a source path that doesn't exist).
    /// </summary>
    public static async Task<int> RunAsync(
        VulnDbUpdateOptions options,
        CancellationToken ct,
        TextWriter? output = null,
        TextWriter? error = null,
        HttpMessageHandler? httpHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        var dest = string.IsNullOrWhiteSpace(options.Dest) ? VulnDbCache.DefaultRoot() : options.Dest;

        // Stage into an isolated temp dir first, then swap into the cache —
        // so a mid-fetch failure can't leave the operator's cache
        // half-overwritten, and we copy only the entries we expect
        // (templates/ + templates-index.json), never whatever else a
        // tarball happens to carry.
        var staging = Path.Combine(Path.GetTempPath(), "bowire-vulndb-" + Guid.NewGuid().ToString("N"));
        HttpClient? http = null;
        try
        {
            Directory.CreateDirectory(staging);
            var source = options.Source;

            string? resolvedRef = null;
            if (string.IsNullOrWhiteSpace(source))
            {
                // Default: resolve the GitHub release tarball. This is the
                // only branch that reaches the network — and it only runs
                // because the operator explicitly typed `vulndb update`.
                http = CreateClient(httpHandler);
                var (tarballUrl, tag) = await ResolveReleaseTarballAsync(http, options.Ref, stdout, ct).ConfigureAwait(false);
                resolvedRef = tag;
                await stdout.WriteLineAsync($"  Downloading {tag} template set…").ConfigureAwait(false);
                await using var netStream = await http.GetStreamAsync(new Uri(tarballUrl), ct).ConfigureAwait(false);
                await ExtractTarGzAsync(netStream, staging, ct).ConfigureAwait(false);
            }
            else if (Directory.Exists(source))
            {
                // Copy only the templates/ tree + index sidecar — never the
                // whole source (a repo checkout carries .git / bin / obj with
                // read-only objects Windows won't let us re-delete, and we'd
                // just discard them at the swap anyway).
                var srcTemplates = VulnDbCache.TemplatesDir(source);
                if (Directory.Exists(srcTemplates))
                {
                    CopyTree(srcTemplates, VulnDbCache.TemplatesDir(staging));
                }
                var srcIndex = VulnDbCache.IndexPath(source);
                if (File.Exists(srcIndex))
                {
                    File.Copy(srcIndex, VulnDbCache.IndexPath(staging), overwrite: true);
                }
            }
            else if (File.Exists(source))
            {
                await using var fileStream = File.OpenRead(source);
                await ExtractTarGzAsync(fileStream, staging, ct).ConfigureAwait(false);
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                http = CreateClient(httpHandler);
                await stdout.WriteLineAsync($"  Downloading {source}…").ConfigureAwait(false);
                await using var netStream = await http.GetStreamAsync(uri, ct).ConfigureAwait(false);
                await ExtractTarGzAsync(netStream, staging, ct).ConfigureAwait(false);
            }
            else
            {
                await stderr.WriteLineAsync($"  Source not found: {source} (expected a directory, a .tar.gz file, or an http(s) URL).").ConfigureAwait(false);
                return 2;
            }

            // The staged payload must carry a templates/ tree — otherwise the
            // source pointed at the wrong thing and we'd be about to wipe the
            // operator's cache for nothing.
            var stagedTemplates = VulnDbCache.TemplatesDir(staging);
            if (!Directory.Exists(stagedTemplates)
                || !Directory.EnumerateFiles(stagedTemplates, "*.json", SearchOption.AllDirectories).Any())
            {
                await stderr.WriteLineAsync("  No templates/ directory found in the source — nothing to install. Cache left unchanged.").ConfigureAwait(false);
                return 1;
            }

            // Swap into the cache: replace <dest>/templates wholesale so a
            // template removed upstream doesn't linger, then refresh the
            // index sidecar if the source shipped one.
            Directory.CreateDirectory(dest);
            var destTemplates = VulnDbCache.TemplatesDir(dest);
            if (Directory.Exists(destTemplates)) Directory.Delete(destTemplates, recursive: true);
            CopyTree(stagedTemplates, destTemplates);

            var stagedIndex = VulnDbCache.IndexPath(staging);
            if (File.Exists(stagedIndex))
            {
                File.Copy(stagedIndex, VulnDbCache.IndexPath(dest), overwrite: true);
            }

            var count = VulnDbCache.CountTemplates(dest);
            var refSuffix = resolvedRef is null ? "" : $" ({resolvedRef})";
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"  Updated {count} template(s){refSuffix} → {dest}")).ConfigureAwait(false);
            await stdout.WriteLineAsync("  Run `bowire scan --target <url>` to scan with them, or `bowire vulndb list` to review.").ConfigureAwait(false);
            return 0;
        }
        catch (HttpRequestException ex)
        {
            await stderr.WriteLineAsync($"  Could not fetch templates: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"  Update failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            // Always dispose the client. When a handler was injected (tests)
            // the client was built with disposeHandler:false, so the caller's
            // handler survives; when we created the handler it's disposed with
            // the client.
            http?.Dispose();
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Build the update client. An injected handler (tests) is not owned —
    /// the client is built with <c>disposeHandler:false</c> so the caller
    /// keeps ownership. A self-created <see cref="HttpClientHandler"/> is
    /// disposed with the client, and on a failed client construction the
    /// handler is disposed here so it never leaks.
    /// </summary>
    // The single boundary helper that owns HttpClient/handler creation for
    // the update path — CA2000 (handler ownership) and CA5399 (CRL) are the
    // known false-positives on the `disposeHandler:true` pattern, documented
    // + suppressed here exactly as ScanCommand.BuildHttpClient does for the
    // scan path, so the suppression stays in one named place.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient(handler, disposeHandler: true) takes ownership — the handler is disposed with the client; the client is disposed by RunAsync.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient is created without enabling CheckCertificateRevocationList",
        Justification = "CheckCertificateRevocationList is set explicitly on the self-created handler below.")]
    private static HttpClient CreateClient(HttpMessageHandler? injected)
    {
        // An injected handler (tests) is not owned: disposeHandler:false so
        // the caller keeps it. The client itself is disposed by RunAsync.
        if (injected is not null)
        {
            var injectedClient = new HttpClient(injected, disposeHandler: false);
            injectedClient.DefaultRequestHeaders.UserAgent.ParseAdd("bowire-vulndb-update");
            return injectedClient;
        }

        // Own handler → disposeHandler:true transfers ownership to the
        // returned client. CRL checking is enabled so the update fetch
        // validates the release host's cert chain.
        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        var client = new HttpClient(handler, disposeHandler: true);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("bowire-vulndb-update");
        return client;
    }

    /// <summary>
    /// Resolve the release tarball asset URL + tag from the GitHub releases
    /// API. Pins <see cref="VulnDbUpdateOptions.Ref"/> when given, else the
    /// latest release. Throws <see cref="InvalidOperationException"/> when no
    /// tarball asset is attached to the release.
    /// </summary>
    private static async Task<(string Url, string Tag)> ResolveReleaseTarballAsync(
        HttpClient http, string? tag, TextWriter stdout, CancellationToken ct)
    {
        var apiUrl = string.IsNullOrWhiteSpace(tag)
            ? $"https://api.github.com/repos/{DefaultRepo}/releases/latest"
            : $"https://api.github.com/repos/{DefaultRepo}/releases/tags/{tag}";
        await stdout.WriteLineAsync(string.IsNullOrWhiteSpace(tag)
            ? "  Resolving latest Bowire.VulnDb release…"
            : $"  Resolving Bowire.VulnDb release {tag}…").ConfigureAwait(false);

        using var doc = JsonDocument.Parse(await http.GetStringAsync(new Uri(apiUrl), ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var resolvedTag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "latest" : "latest";

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null) continue;
                if (name.StartsWith(TarballAssetPrefix, StringComparison.Ordinal)
                    && name.EndsWith(".tar.gz", StringComparison.Ordinal)
                    && asset.TryGetProperty("browser_download_url", out var url)
                    && url.GetString() is { } dl)
                {
                    return (dl, resolvedTag);
                }
            }
        }
        throw new InvalidOperationException(
            $"Release {resolvedTag} has no {TarballAssetPrefix}*.tar.gz asset.");
    }

    /// <summary>
    /// Extract a gzip-compressed tar stream into <paramref name="destDir"/>.
    /// <c>TarFile.ExtractToDirectoryAsync</c> rejects entries whose path
    /// escapes the destination, so a malicious <c>../</c> entry can't write
    /// outside the isolated staging directory.
    /// </summary>
    private static async Task ExtractTarGzAsync(Stream source, string destDir, CancellationToken ct)
    {
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destDir, overwriteFiles: true, ct).ConfigureAwait(false);
    }

    /// <summary>Recursively copy <paramref name="from"/> into <paramref name="to"/> (created on demand).</summary>
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string dir)
    {
        // Best-effort temp cleanup — must never crash the command. Read-only
        // files (e.g. a git-object tree a directory source might carry) throw
        // UnauthorizedAccessException on Windows, so both are swallowed.
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the temp dir behind rather than fail the update.
        }
    }
}
