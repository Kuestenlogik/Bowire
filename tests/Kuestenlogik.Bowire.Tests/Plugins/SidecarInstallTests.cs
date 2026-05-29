// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Kuestenlogik.Bowire.App;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Coverage for the sidecar install path (<c>bowire plugin install
/// --file foo.zip</c>) and the <c>plugin list</c> kind column. All
/// local — no network, no real subprocess (the installed files are
/// inert stubs; spawning is exercised by the integration suite).
/// </summary>
[Collection("ConsoleOutSerialised")]
public sealed class SidecarInstallTests : IDisposable
{
    private readonly string _pluginDir;

    public SidecarInstallTests()
    {
        _pluginDir = Path.Combine(Path.GetTempPath(), "bowire-sidecar-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_pluginDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static string MakeSidecarZip(string manifestJson, string exeName = "sidecar.bin")
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "bowire-sc-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var manifestEntry = zip.CreateEntry("sidecar.json");
        using (var w = new StreamWriter(manifestEntry.Open())) w.Write(manifestJson);

        var exeEntry = zip.CreateEntry(exeName);
        using (var w = new StreamWriter(exeEntry.Open())) w.Write("#!/bin/sh\necho stub\n");

        return zipPath;
    }

    [Fact]
    public async Task InstallSidecarFromZip_Unpacks_Into_PackageId_Dir()
    {
        var zip = MakeSidecarZip("""
            { "packageId":"Acme.Bowire.Protocol.Zenoh",
              "protocol":{"id":"zenoh","name":"Zenoh"},
              "executable":"sidecar.bin", "version":"1.2.3" }
            """);
        try
        {
            var code = await PluginManager.InstallSidecarFromZipAsync(zip, _pluginDir,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, code);

            var installed = Path.Combine(_pluginDir, "Acme.Bowire.Protocol.Zenoh");
            Assert.True(Directory.Exists(installed));
            Assert.True(File.Exists(Path.Combine(installed, "sidecar.json")));
            Assert.True(File.Exists(Path.Combine(installed, "sidecar.bin")));
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task InstallSidecarFromZip_Rejects_Already_Installed()
    {
        var zip = MakeSidecarZip("""
            { "packageId":"Dup", "protocol":{"id":"dup","name":"Dup"}, "executable":"sidecar.bin" }
            """);
        try
        {
            var first = await PluginManager.InstallSidecarFromZipAsync(zip, _pluginDir,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, first);
            var second = await PluginManager.InstallSidecarFromZipAsync(zip, _pluginDir,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, second);
        }
        finally { File.Delete(zip); }
    }

    private static string MakeZipWithoutManifest()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "bowire-nomanifest-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var e = zip.CreateEntry("readme.txt");
        using var w = new StreamWriter(e.Open());
        w.Write("no manifest here");
        return zipPath;
    }

    [Fact]
    public async Task InstallSidecarFromZip_Rejects_Zip_Without_Manifest()
    {
        var zipPath = MakeZipWithoutManifest();
        try
        {
            var code = await PluginManager.InstallSidecarFromZipAsync(zipPath, _pluginDir,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, code);
        }
        finally { File.Delete(zipPath); }
    }

    [Fact]
    public async Task InstallSidecarFromZip_Rejects_Manifest_Missing_Executable()
    {
        var zip = MakeSidecarZip("""
            { "packageId":"Bad", "protocol":{"id":"bad","name":"Bad"}, "executable":"" }
            """);
        try
        {
            var code = await PluginManager.InstallSidecarFromZipAsync(zip, _pluginDir,
                TestContext.Current.CancellationToken);
            Assert.Equal(1, code);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public async Task InstallSidecarFromZip_Rejects_Missing_File()
    {
        var code = await PluginManager.InstallSidecarFromZipAsync(
            Path.Combine(_pluginDir, "nope.zip"), _pluginDir, TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task List_Tags_Sidecar_And_Nuget_Kinds()
    {
        // Install a sidecar...
        var zip = MakeSidecarZip("""
            { "packageId":"Acme.Sidecar", "protocol":{"id":"acme","name":"Acme"},
              "executable":"sidecar.bin", "version":"0.9.0" }
            """);
        // ...and fake a NuGet-style plugin dir (DLL + plugin.json metadata).
        var nugetDir = Path.Combine(_pluginDir, "Acme.Nuget");
        Directory.CreateDirectory(nugetDir);
        await File.WriteAllTextAsync(Path.Combine(nugetDir, "Acme.Nuget.dll"), "MZ-stub",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(nugetDir, "plugin.json"),
            """{ "packageId":"Acme.Nuget", "version":"2.0.0", "resolvedVersion":"2.0.0" }""",
            TestContext.Current.CancellationToken);

        try
        {
            await PluginManager.InstallSidecarFromZipAsync(zip, _pluginDir,
                TestContext.Current.CancellationToken);

            var original = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try { PluginManager.List(_pluginDir, verbose: false); }
            finally { Console.SetOut(original); }

            var output = sw.ToString();
            Assert.Contains("Acme.Sidecar", output);
            Assert.Contains("[sidecar: acme]", output);
            Assert.Contains("v0.9.0", output);
            Assert.Contains("Acme.Nuget", output);
            Assert.Contains("[nuget:", output);
            Assert.Contains("v2.0.0", output);
        }
        finally { File.Delete(zip); }
    }
}
