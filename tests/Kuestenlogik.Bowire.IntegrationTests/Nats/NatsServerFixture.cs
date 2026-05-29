// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Kuestenlogik.Bowire.IntegrationTests.Nats;

/// <summary>
/// Boots a real <c>nats-server -js</c> by downloading the official
/// release binary from the nats-io GitHub releases and running it as a
/// subprocess — no Docker daemon required.
/// </summary>
/// <remarks>
/// <para>
/// This is the Docker-free alternative to a Testcontainers fixture.
/// Testcontainers needs a Docker daemon and pulls <c>nats:2-alpine</c>
/// from Docker Hub, whose hosted-runner pulls are rate-limited and flake
/// the CI. The nats-server release artifacts on GitHub aren't subject to
/// that limit, so the live JetStream + Services round-trip suite can run
/// on every CI pass instead of opting out via <c>Category=Docker</c>.
/// </para>
/// <para>
/// The binary is cached under the temp dir keyed by version + RID, so
/// repeated local runs download once. JetStream storage + the server log
/// live in a per-instance temp dir that's removed on dispose.
/// </para>
/// </remarks>
public sealed class NatsServerFixture : IAsyncLifetime
{
    // Pin a specific nats-server release so CI is reproducible. Bump
    // deliberately. v2.10.x is the current stable line.
    private const string Version = "2.10.22";

    private Process? _process;
    private string? _workDir;

    /// <summary>The <c>nats://...</c> URL the plugin can use as serverUrl.</summary>
    public string ServerUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var exe = await EnsureBinaryAsync().ConfigureAwait(false);

        _workDir = Path.Combine(Path.GetTempPath(), "bowire-nats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        var port = GetFreeTcpPort();
        var jsDir = Path.Combine(_workDir, "js");
        var logFile = Path.Combine(_workDir, "nats.log");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = _workDir,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        // -js JetStream, bind loopback only, our chosen port, isolated
        // storage + log to file so stdout stays quiet (no drain needed).
        psi.ArgumentList.Add("-js");
        psi.ArgumentList.Add("-a"); psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-sd"); psi.ArgumentList.Add(jsDir);
        psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(logFile);

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for nats-server.");

        await WaitForPortAsync(port, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        ServerUrl = $"nats://127.0.0.1:{port}";
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.Dispose();
        }
        catch { /* best-effort */ }

        try
        {
            if (_workDir is not null && Directory.Exists(_workDir))
                Directory.Delete(_workDir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }

        return ValueTask.CompletedTask;
    }

    // ---- binary acquisition --------------------------------------------

    private static async Task<string> EnsureBinaryAsync()
    {
        var (osToken, archToken) = ResolvePlatform();
        var isWindows = OperatingSystem.IsWindows();
        var ext = isWindows ? "zip" : "tar.gz";
        var assetStem = $"nats-server-v{Version}-{osToken}-{archToken}";
        var exeName = isWindows ? "nats-server.exe" : "nats-server";

        var cacheRoot = Path.Combine(Path.GetTempPath(), "bowire-nats-server", Version, $"{osToken}-{archToken}");
        var cachedExe = Path.Combine(cacheRoot, exeName);
        if (File.Exists(cachedExe)) return cachedExe;

        Directory.CreateDirectory(cacheRoot);
        var url = $"https://github.com/nats-io/nats-server/releases/download/v{Version}/{assetStem}.{ext}";
        var archivePath = Path.Combine(cacheRoot, $"{assetStem}.{ext}");

        await DownloadWithRetryAsync(url, archivePath).ConfigureAwait(false);

        if (isWindows)
            ExtractExeFromZip(archivePath, exeName, cachedExe);
        else
            await ExtractExeFromTarGzAsync(archivePath, exeName, cachedExe).ConfigureAwait(false);

        try { File.Delete(archivePath); } catch { /* keep cache lean, non-fatal */ }

        if (!OperatingSystem.IsWindows() && File.Exists(cachedExe))
        {
            var mode = File.GetUnixFileMode(cachedExe);
            File.SetUnixFileMode(cachedExe,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        if (!File.Exists(cachedExe))
            throw new InvalidOperationException(
                $"nats-server binary '{exeName}' not found in {assetStem}.{ext}");
        return cachedExe;
    }

    private static (string os, string arch) ResolvePlatform()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "darwin"
            : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            _ => throw new PlatformNotSupportedException(
                $"No nats-server asset for {RuntimeInformation.OSArchitecture}"),
        };
        return (os, arch);
    }

    private static async Task DownloadWithRetryAsync(string url, string destPath)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var uri = new Uri(url);
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using var src = await http.GetStreamAsync(uri).ConfigureAwait(false);
                await using var dst = File.Create(destPath);
                await src.CopyToAsync(dst).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException($"Failed to download nats-server from {url} after 3 attempts", last);
    }

    private static void ExtractExeFromZip(string archivePath, string exeName, string destExe)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{exeName} not in {archivePath}");
        entry.ExtractToFile(destExe, overwrite: true);
    }

    private static async Task ExtractExeFromTarGzAsync(string archivePath, string exeName, string destExe)
    {
        await using var fs = File.OpenRead(archivePath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await using var tar = new TarReader(gz);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync().ConfigureAwait(false)) is not null)
        {
            // Entries look like nats-server-v2.10.22-linux-amd64/nats-server
            if (entry.EntryType is TarEntryType.RegularFile
                && Path.GetFileName(entry.Name).Equals(exeName, StringComparison.Ordinal))
            {
                await entry.ExtractToFileAsync(destExe, overwrite: true).ConfigureAwait(false);
                return;
            }
        }
        throw new InvalidOperationException($"{exeName} not in {archivePath}");
    }

    // ---- process readiness ---------------------------------------------

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException(
                    $"nats-server exited early (code {_process.ExitCode}). Check the server log in the work dir.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
                if (client.Connected) return;
            }
            catch (SocketException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"nats-server did not start listening on port {port} within {timeout}.");
    }
}
