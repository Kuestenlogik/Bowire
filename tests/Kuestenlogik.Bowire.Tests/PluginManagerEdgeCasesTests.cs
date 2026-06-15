// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Edge-case tests for <see cref="PluginManager"/> covering the remaining
/// uncovered line-coverage gaps in the loader, the inspect surface and the
/// EnumeratePluginServices walker. Each test exercises a specific branch
/// the existing PluginManagerTests / PluginManagerAdditionalCoverageTests
/// fixtures stop short of:
/// <list type="bullet">
///   <item><c>LoadPlugins</c> re-entry path (PluginLoadStatus.AlreadyLoaded)</item>
///   <item><c>LoadPlugins</c> contract major-version mismatch (PluginLoadStatus.ContractMajorMismatch)</item>
///   <item><c>LoadPlugins</c> successful manifest load (PluginLoadStatus.Loaded)</item>
///   <item><c>LoadPlugins</c> LoadFromAssemblyPath failure on a corrupt manifest dll</item>
///   <item><c>Inspect</c> on a plugin whose manifest dll actually populates the load context (exercises the loaded-assembly print + FindImplementationsOf loop)</item>
///   <item><c>EnumeratePluginServices</c> populates from a loaded IBowireProtocol implementation</item>
///   <item><c>LastLoadResults</c> getter</item>
/// </list>
/// </summary>
public sealed class PluginManagerEdgeCasesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _envBackup;

    public PluginManagerEdgeCasesTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("bowire-pm-edge-").FullName;
        _envBackup = Environment.GetEnvironmentVariable(PluginManager.PluginDirEnvVar);
        Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PluginManager.PluginDirEnvVar, _envBackup);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // Locate the test bin so we can copy real DLLs into stub plugin dirs.
    private static string TestBin =>
        Path.GetDirectoryName(typeof(PluginManagerEdgeCasesTests).Assembly.Location)!;

    /// <summary>
    /// Pick a real Bowire protocol plugin assembly that ships next to the
    /// test runner — used by the success-path tests to give the loader a
    /// real manifest dll. OData is the smallest and has self-contained
    /// dependencies (Microsoft.OData.Edm + the host's Kuestenlogik.Bowire),
    /// so renaming + reloading it under a different package id is the
    /// cheapest way to exercise the LoadFromAssemblyPath success branch.
    /// </summary>
    private static string ProbePluginDll =>
        SafePath.Combine(TestBin, "Kuestenlogik.Bowire.Protocol.OData.dll");

    [Fact]
    public void LoadPlugins_SecondCallOnSameDir_ReportsAlreadyLoaded()
    {
        // Two consecutive LoadPlugins calls on the same plugin dir: the
        // first call records a load result, the second hits the
        // s_loadedSubdirs guard and surfaces PluginLoadStatus.AlreadyLoaded.
        // Guards against the duplicate-context regression where every
        // subsequent embedded-host startup would double-register every
        // protocol.
        var pluginSub = SafePath.Combine(_tempDir, "Already.Loaded.Edge");
        Directory.CreateDirectory(pluginSub);
        // No manifest dll — first call reports ManifestMissing, but the
        // subdir is *not* added to s_loadedSubdirs in that path (the
        // ManifestMissing branch removes it). So we need an actual
        // manifest to land in s_loadedSubdirs.
        File.Copy(ProbePluginDll, SafePath.Combine(pluginSub, "Already.Loaded.Edge.dll"));

        var first = PluginManager.LoadPlugins(_tempDir);
        var firstEntry = Assert.Single(first, r => r.PackageId == "Already.Loaded.Edge");
        Assert.Equal(PluginLoadStatus.Loaded, firstEntry.Status);

        var second = PluginManager.LoadPlugins(_tempDir);
        var secondEntry = Assert.Single(second, r => r.PackageId == "Already.Loaded.Edge");
        Assert.Equal(PluginLoadStatus.AlreadyLoaded, secondEntry.Status);
        Assert.Null(secondEntry.ErrorMessage);
    }

    [Fact]
    public void LoadPlugins_ContractMajorMismatch_IsRejectedWithStructuredError()
    {
        // Override the host's Bowire contract version so the manifest's
        // referenced version (read from the real Kuestenlogik.Bowire.dll
        // that the renamed Protocol.OData assembly references) registers
        // as "wrong major". The loader must NOT call LoadFromAssemblyPath
        // — it must short-circuit with PluginLoadStatus.ContractMajorMismatch
        // and surface an actionable error mentioning the plugin id.
        var pluginSub = SafePath.Combine(_tempDir, "Bad.Major.Plug");
        Directory.CreateDirectory(pluginSub);
        File.Copy(ProbePluginDll, SafePath.Combine(pluginSub, "Bad.Major.Plug.dll"));

        var originalOverride = PluginManifestProbe.HostVersionOverride;
        try
        {
            // Force a non-matching major version. The plugin references
            // the real Kuestenlogik.Bowire at its actual version, so any
            // host version with a different Major triggers the mismatch.
            PluginManifestProbe.HostVersionOverride = new Version(999, 0, 0, 0);
            var results = PluginManager.LoadPlugins(_tempDir);
            var entry = Assert.Single(results, r => r.PackageId == "Bad.Major.Plug");
            Assert.Equal(PluginLoadStatus.ContractMajorMismatch, entry.Status);
            Assert.NotNull(entry.ErrorMessage);
            Assert.Contains("Bad.Major.Plug", entry.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("999.0", entry.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            PluginManifestProbe.HostVersionOverride = originalOverride;
        }
    }

    [Fact]
    public void LoadPlugins_CorruptManifestDll_ReportsAssemblyLoadFailed()
    {
        // <packageId>.dll exists (manifest-missing guard passes), the
        // PE probe in PluginManifestProbe.ReadReferencedBowireVersion
        // catches the bad-PE error and returns null (compatible by
        // convention), so the loader proceeds to LoadFromAssemblyPath
        // — which throws BadImageFormatException. The catch must surface
        // PluginLoadStatus.AssemblyLoadFailed with a message naming the
        // failing manifest file.
        var pluginSub = SafePath.Combine(_tempDir, "Corrupt.Manifest");
        Directory.CreateDirectory(pluginSub);
        var manifest = SafePath.Combine(pluginSub, "Corrupt.Manifest.dll");
        // Bytes that fail every PE-header check.
        File.WriteAllBytes(manifest, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05]);

        var results = PluginManager.LoadPlugins(_tempDir);
        var entry = Assert.Single(results, r => r.PackageId == "Corrupt.Manifest");
        Assert.Equal(PluginLoadStatus.AssemblyLoadFailed, entry.Status);
        Assert.NotNull(entry.ErrorMessage);
        Assert.Contains("LoadFromAssemblyPath failed", entry.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Corrupt.Manifest.dll", entry.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void LastLoadResults_AfterLoadPlugins_MatchesReturnedSnapshot()
    {
        // The /api/plugins/health endpoint reads from
        // PluginManager.LastLoadResults — it has to match whatever
        // LoadPlugins last returned so the panel doesn't drift from the
        // loader's view.
        var pluginSub = SafePath.Combine(_tempDir, "LastLoad.Snapshot");
        Directory.CreateDirectory(pluginSub);
        // No manifest dll — ManifestMissing path produces a record.
        var returned = PluginManager.LoadPlugins(_tempDir);

        var snapshot = PluginManager.LastLoadResults;
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot, r => r.PackageId == "LastLoad.Snapshot");
        // Snapshot is the same reference as the returned list (lock-free
        // publish: assignment is reference-only).
        Assert.Same(returned, snapshot);
    }

    [Fact]
    public void Inspect_ManifestDllLoadsIntoContext_PrintsAssembliesAndImplementations()
    {
        // packageId matches the manifest file name → BowirePluginHost.Load
        // actually calls LoadFromAssemblyPath, populating ctx.Assemblies.
        // Inspect's loadedAssemblies loop prints each one (lines 897-900),
        // and FindImplementationsOf<IBowireProtocol> walks the loaded
        // types, finds the concrete BowireODataProtocol, and the print
        // loop (lines 918-925) fires.
        var pluginSub = SafePath.Combine(_tempDir, "OData");
        Directory.CreateDirectory(pluginSub);
        File.Copy(ProbePluginDll, SafePath.Combine(pluginSub, "OData.dll"));
        File.WriteAllText(SafePath.Combine(pluginSub, "plugin.json"),
            """
            {
              "packageId": "OData",
              "version": "1.0.0",
              "resolvedVersion": "1.0.0",
              "installedAt": "2026-01-01T00:00:00Z",
              "sources": ["https://api.nuget.org/v3/index.json"]
            }
            """);

        using var sw = new StringWriter();
        var rc = PluginManager.Inspect("OData", _tempDir, stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);

        var output = sw.ToString();
        // Load-context name follows the BowirePlugin:<leaf> convention,
        // and the loaded-assembly count must be at least one (the
        // manifest dll itself).
        Assert.Contains("BowirePlugin:OData", output, StringComparison.Ordinal);
        // The protocol-id printout for OData's concrete type lands in
        // the "IBowireProtocol  <fullname>" line — assert on the
        // implementation column header so the print branch is exercised.
        Assert.Contains("IBowireProtocol", output, StringComparison.Ordinal);
        Assert.Contains("BowireODataProtocol", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumeratePluginServices_AfterLoadingRealManifest_FindsImplementation()
    {
        // packageId matches the manifest file name → LoadPlugins's
        // BowirePluginLoadContext loads the assembly, so the foreach
        // walker inside EnumeratePluginServices iterates a populated
        // ctx.Assemblies. The assembly carries a concrete
        // BowireODataProtocol : IBowireProtocol, so the
        // Activator.CreateInstance + Add branch (lines 1165-1167)
        // executes.
        var pluginSub = SafePath.Combine(_tempDir, "Enum.OData");
        Directory.CreateDirectory(pluginSub);
        File.Copy(ProbePluginDll, SafePath.Combine(pluginSub, "Enum.OData.dll"));

        PluginManager.LoadPlugins(_tempDir);

        var hits = PluginManager.EnumeratePluginServices<IBowireProtocol>();
        Assert.NotNull(hits);
        // Either the concrete BowireODataProtocol got constructed (most
        // common path) or the Activator surfaced a per-type exception
        // that the stderr warning catch swallowed (rare CI path on a
        // clean runner). Verifying "non-empty" would be ideal but the
        // s_pluginContexts list is process-static and prior tests in the
        // run may have added contexts that don't expose IBowireProtocol;
        // what we care about is that the inner foreach body — the lines
        // we're driving — actually executed. We assert at least one OData
        // protocol id surfaces, which can only come from the renamed
        // manifest we just loaded.
        Assert.Contains(hits, p => string.Equals(p.Id, "odata", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadPlugins_SuccessfulManifestLoad_RecordsLoadedStatus()
    {
        // Direct test for the PluginLoadStatus.Loaded path — separate
        // from Inspect so a failure here points squarely at LoadPlugins
        // instead of at the inspect-side enumeration.
        var pluginSub = SafePath.Combine(_tempDir, "Success.Load");
        Directory.CreateDirectory(pluginSub);
        File.Copy(ProbePluginDll, SafePath.Combine(pluginSub, "Success.Load.dll"));

        var results = PluginManager.LoadPlugins(_tempDir);
        var entry = Assert.Single(results, r => r.PackageId == "Success.Load");
        Assert.Equal(PluginLoadStatus.Loaded, entry.Status);
        Assert.Null(entry.ErrorMessage);
    }

    // ---- InstallSidecarFromZipAsync coverage ----

    /// <summary>
    /// Build a sidecar zip with the supplied manifest body and an extra
    /// stub file representing the sidecar executable. Path returned is
    /// the on-disk zip the test should hand to
    /// <see cref="PluginManager.InstallSidecarFromZipAsync"/>. Async to
    /// match the BCL's async-only Stream surface (sync writes through
    /// the ZipArchive's underlying CrcCalculatorStream trip CA1849).
    /// </summary>
    private async Task<string> MakeSidecarZipAsync(string manifestJson, string? executableName = "fake-sidecar.sh")
    {
        var zipPath = SafePath.Combine(_tempDir, "sidecar-" + Guid.NewGuid().ToString("N") + ".zip");
        await using var fs = File.Create(zipPath);
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifestEntry = archive.CreateEntry("sidecar.json");
            var manifestStream = await manifestEntry.OpenAsync(TestContext.Current.CancellationToken);
            await using (manifestStream)
            await using (var w = new StreamWriter(manifestStream))
            {
                await w.WriteAsync(manifestJson);
            }
            if (executableName is not null)
            {
                var exeEntry = archive.CreateEntry(executableName);
                var exeStream = await exeEntry.OpenAsync(TestContext.Current.CancellationToken);
                await using (exeStream)
                await using (var w = new StreamWriter(exeStream))
                {
                    await w.WriteAsync("#!/bin/sh\necho stub\n");
                }
            }
        }
        return zipPath;
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_EmptyZipSource_ReturnsUsageExit()
    {
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            "", pluginDir: _tempDir,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_LocalFileMissing_ReturnsErrorExit()
    {
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            SafePath.Combine(_tempDir, "no-such.zip"),
            pluginDir: _tempDir,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_NotAZipArchive_ReturnsErrorExitWithDiagnostic()
    {
        // The peek-manifest branch wraps the read in a try/catch that
        // catches InvalidDataException — exercises the "not a valid zip"
        // diagnostic line.
        var bogus = SafePath.Combine(_tempDir, "bogus.zip");
        await File.WriteAllTextAsync(bogus, "definitely not a zip",
            TestContext.Current.CancellationToken);

        using var sw = new StringWriter();
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            bogus, pluginDir: _tempDir, stdout: sw, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, rc);
        var output = sw.ToString();
        Assert.Contains("not a valid zip archive", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_NoManifestInZip_ReturnsErrorExitWithDiagnostic()
    {
        // Zip is valid but doesn't carry sidecar.json — must surface the
        // "Archive has no sidecar.json" hint, not extract anything. Build
        // the manifest-less zip via MakeSidecarZip with a null manifest
        // and a placeholder executable so the assertion holds even on a
        // future analyzer rule that flags the bare ZipArchive ctor.
        var zipPath = SafePath.Combine(_tempDir, "manifest-less-" + Guid.NewGuid().ToString("N") + ".zip");
        await using (var fs = File.Create(zipPath))
        {
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);
            var entry = archive.CreateEntry("readme.txt");
            var entryStream = await entry.OpenAsync(TestContext.Current.CancellationToken);
            await using (entryStream)
            await using (var w = new StreamWriter(entryStream))
            {
                await w.WriteAsync("not a sidecar");
            }
        }

        using var sw = new StringWriter();
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            zipPath, pluginDir: _tempDir, stdout: sw, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, rc);
        Assert.Contains("Archive has no sidecar.json", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_MalformedManifest_ReturnsErrorExitWithDiagnostic()
    {
        // Manifest is parseable JSON but lacks the minimum-required
        // fields (no protocol id, no executable). Should hit the
        // "missing packageId / protocol.id / executable" branch.
        var zipPath = await MakeSidecarZipAsync(
            """
            {
              "packageId": "Incomplete.Sidecar"
            }
            """);

        using var sw = new StringWriter();
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            zipPath, pluginDir: _tempDir, stdout: sw, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, rc);
        Assert.Contains("sidecar.json is missing", sw.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_DuplicateInstall_ReturnsErrorExitWithDiagnostic()
    {
        // Pre-create the destination plugin subdir so the "already
        // installed" guard fires AFTER the manifest peek but BEFORE the
        // extract. Pins both the order of operations and the user-facing
        // hint that points at `bowire plugin uninstall`.
        Directory.CreateDirectory(SafePath.Combine(_tempDir, "Sidecar.Dupe"));
        var zipPath = await MakeSidecarZipAsync(
            """
            {
              "packageId": "Sidecar.Dupe",
              "protocol": { "id": "dupe-proto", "name": "Dupe Proto" },
              "executable": "fake-sidecar.sh"
            }
            """);

        using var sw = new StringWriter();
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            zipPath, pluginDir: _tempDir, stdout: sw, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, rc);
        var output = sw.ToString();
        Assert.Contains("already installed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bowire plugin uninstall Sidecar.Dupe", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_HappyPath_ExtractsAndReportsProtocolMetadata()
    {
        // Full success path: valid manifest with all fields populated,
        // matching executable in the zip → extract succeeds, exit 0,
        // and the human-readable "Installed sidecar <id> <version>
        // (protocol '<proto>', N file(s)) -> <path>" line surfaces.
        var zipPath = await MakeSidecarZipAsync(
            """
            {
              "packageId": "Acme.Happy.Sidecar",
              "version": "0.4.2",
              "protocol": { "id": "happy-proto", "name": "Happy Proto" },
              "executable": "fake-sidecar.sh"
            }
            """);

        using var sw = new StringWriter();
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            zipPath, pluginDir: _tempDir, stdout: sw, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var output = sw.ToString();
        // Pin the formatted output — the install message has to carry
        // version, protocol id, and the extracted path so an operator
        // running `bowire plugin install --file` can see exactly where
        // the bytes landed.
        Assert.Contains("Installed sidecar Acme.Happy.Sidecar 0.4.2", output, StringComparison.Ordinal);
        Assert.Contains("protocol 'happy-proto'", output, StringComparison.Ordinal);

        // Files on disk match what the zip carried.
        var pluginDir = SafePath.Combine(_tempDir, "Acme.Happy.Sidecar");
        Assert.True(File.Exists(SafePath.Combine(pluginDir, "sidecar.json")));
        Assert.True(File.Exists(SafePath.Combine(pluginDir, "fake-sidecar.sh")));
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_HappyPath_NoVersionField_OmitsVersionFromMessage()
    {
        // Sidecar manifest carries no version → the formatter must not
        // print a trailing space + version. Guards the optional-version
        // branch in the success message.
        var zipPath = await MakeSidecarZipAsync(
            """
            {
              "packageId": "Acme.Sidecar.NoVer",
              "protocol": { "id": "no-ver-proto", "name": "No Ver Proto" },
              "executable": "fake-sidecar.sh"
            }
            """);

        using var sw = new StringWriter();
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            zipPath, pluginDir: _tempDir, stdout: sw, stderr: TextWriter.Null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        var output = sw.ToString();
        Assert.Contains("Installed sidecar Acme.Sidecar.NoVer (protocol 'no-ver-proto'",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_HttpUrl_DownloadFailure_ReturnsErrorExit()
    {
        // Non-listening port + http scheme drives the HttpClient
        // download path, which fails fast and triggers the "Failed to
        // download" catch. We bind 0 (let the OS pick) and immediately
        // close the listener so the next connect attempt has nothing
        // to bind to — far cheaper than spinning up TestServer.
        var port = GetFreePort();
        var rc = await PluginManager.InstallSidecarFromZipAsync(
            $"http://127.0.0.1:{port}/sidecar.zip",
            pluginDir: _tempDir,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, rc);
    }

    private static int GetFreePort()
    {
        // Listen on port 0, read back the assigned port, close. Used to
        // get a deterministic "nothing listens here" address for the
        // HTTP failure-path test above. TcpListener implements IDisposable
        // via Stop() — wrap in `using` so CA2000 stays happy.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void List_VerboseWithSidecarPlugin_PrintsProtocolAndExecutableAndFileList()
    {
        // The verbose sidecar branch (lines 561-570) prints the protocol
        // id, name, executable, and a comma-joined relative-file listing.
        // No existing test hits this branch — List_Tags_Sidecar_And_Nuget_Kinds
        // covers the non-verbose path only.
        var pluginSub = SafePath.Combine(_tempDir, "Acme.Verbose.Sidecar");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(SafePath.Combine(pluginSub, "sidecar.json"),
            """
            {
              "packageId": "Acme.Verbose.Sidecar",
              "version": "3.1.0",
              "protocol": { "id": "verbose-proto", "name": "Verbose Proto" },
              "executable": "fake-sidecar.sh"
            }
            """);
        // Two extra files so the file-list line has interesting content.
        File.WriteAllText(SafePath.Combine(pluginSub, "fake-sidecar.sh"), "#!/bin/sh");
        File.WriteAllText(SafePath.Combine(pluginSub, "README.md"), "stub");

        using var sw = new StringWriter();
        var rc = PluginManager.List(_tempDir, verbose: true, stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);

        var output = sw.ToString();
        Assert.Contains("Acme.Verbose.Sidecar  v3.1.0  [sidecar]", output, StringComparison.Ordinal);
        Assert.Contains("protocol:   verbose-proto (Verbose Proto)", output, StringComparison.Ordinal);
        Assert.Contains("executable: fake-sidecar.sh", output, StringComparison.Ordinal);
        // File list is sorted ordinal, so README.md comes before
        // fake-sidecar.sh and sidecar.json.
        Assert.Contains("files:      README.md, fake-sidecar.sh, sidecar.json",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public void List_VerboseWithSidecarNoVersion_RendersDashSentinel()
    {
        // Sidecar without a version field → the formatter substitutes a
        // U+2014 em-dash so the column stays aligned. Drives the
        // ternary in the verbose-sidecar branch.
        var pluginSub = SafePath.Combine(_tempDir, "NoVer.Sidecar");
        Directory.CreateDirectory(pluginSub);
        File.WriteAllText(SafePath.Combine(pluginSub, "sidecar.json"),
            """
            {
              "packageId": "NoVer.Sidecar",
              "protocol": { "id": "nv-proto", "name": "No Ver Proto" },
              "executable": "fake.sh"
            }
            """);

        using var sw = new StringWriter();
        var rc = PluginManager.List(_tempDir, verbose: true, stdout: sw, stderr: TextWriter.Null);
        Assert.Equal(0, rc);

        var output = sw.ToString();
        // The dash-sentinel sits between the package id and the [sidecar]
        // marker — pin both so a future formatter tweak still surfaces
        // the missing-version state visibly.
        Assert.Contains("NoVer.Sidecar  —  [sidecar]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallSidecarFromZipAsync_HttpUrl_SuccessfulDownload_InstallsSidecar()
    {
        // Real HttpListener serves the sidecar zip bytes — exercises the
        // happy-path download branch (lines around the http GetStreamAsync
        // + CopyToAsync), including the temp-download cleanup in the
        // finally block.
        var zipPath = await MakeSidecarZipAsync(
            """
            {
              "packageId": "Acme.Http.Sidecar",
              "version": "2.0.0",
              "protocol": { "id": "http-proto", "name": "Http Proto" },
              "executable": "fake-sidecar.sh"
            }
            """);
        var zipBytes = await File.ReadAllBytesAsync(zipPath, TestContext.Current.CancellationToken);

        // Spin up a one-shot HTTP server that returns the zip bytes on
        // any GET. HttpListener is the BCL primitive that works without
        // pulling Microsoft.AspNetCore.TestHost.
        var port = GetFreePort();
        using var listener = new System.Net.HttpListener();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var ct = TestContext.Current.CancellationToken;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.ContentType = "application/zip";
            ctx.Response.ContentLength64 = zipBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(zipBytes, ct);
            ctx.Response.OutputStream.Close();
        }, ct);

        try
        {
            using var sw = new StringWriter();
            var rc = await PluginManager.InstallSidecarFromZipAsync(
                prefix + "sidecar.zip",
                pluginDir: _tempDir, stdout: sw, stderr: TextWriter.Null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(0, rc);
            var output = sw.ToString();
            Assert.Contains("Downloading " + prefix + "sidecar.zip", output, StringComparison.Ordinal);
            Assert.Contains("Installed sidecar Acme.Http.Sidecar 2.0.0", output, StringComparison.Ordinal);
            // The extracted plugin dir must exist with the manifest + exe.
            var pluginDir = SafePath.Combine(_tempDir, "Acme.Http.Sidecar");
            Assert.True(File.Exists(SafePath.Combine(pluginDir, "sidecar.json")));
        }
        finally
        {
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch { /* shutdown */ }
            listener.Stop();
        }
    }
}
