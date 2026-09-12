// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins.Sidecar;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// The two-step resolution of a manifest's <c>executable</c>: the plugin
/// directory first, <c>PATH</c> second and only for a bare name. Without
/// the second step no interpreted sidecar (Python, Node, Ruby) has a
/// manifest that works on more than one platform — see #692.
/// </summary>
public sealed class SidecarExecutableResolutionTests : IDisposable
{
    private readonly string _pluginDir = Path.Combine(
        Path.GetTempPath(), "bowire-sidecar-resolve-" + Guid.NewGuid().ToString("n"));

    public SidecarExecutableResolutionTests() => Directory.CreateDirectory(_pluginDir);

    public void Dispose()
    {
        if (Directory.Exists(_pluginDir)) Directory.Delete(_pluginDir, recursive: true);
    }

    /// <summary>A command that exists on every runner, named without its extension
    /// so the PATH case also covers Windows' PATHEXT lookup.</summary>
    private static string BareCommandOnPath => OperatingSystem.IsWindows() ? "cmd" : "sh";

    /// <summary>Args that make <see cref="BareCommandOnPath"/> exit immediately
    /// instead of waiting on the stdin we redirect.</summary>
    private static IReadOnlyList<string> ExitImmediatelyArgs =>
        OperatingSystem.IsWindows() ? ["/c", "exit"] : ["-c", "exit"];

    private string TouchInPluginDir(string relativePath)
    {
        var full = Path.Combine(_pluginDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    [Fact]
    public void A_Bare_Name_The_Plugin_Does_Not_Ship_Falls_Back_To_Path()
    {
        var (fileName, source) = SidecarJsonRpcTransport.ResolveExecutable("python3", _pluginDir);

        // Handed to the OS unchanged — a name with no separator is what
        // makes .NET search PATH instead of the working directory.
        Assert.Equal("python3", fileName);
        Assert.Equal(SidecarJsonRpcTransport.ExecutableSource.SearchPath, source);
    }

    [Fact]
    public void A_File_The_Plugin_Ships_Wins_Over_A_Path_Namesake()
    {
        // The security-relevant half: the plugin directory is the trusted
        // location, so a shipped binary is never shadowed by a program of
        // the same name that happens to be on PATH.
        var shipped = TouchInPluginDir(BareCommandOnPath);

        var (fileName, source) = SidecarJsonRpcTransport.ResolveExecutable(
            BareCommandOnPath, _pluginDir);

        Assert.Equal(shipped, fileName);
        Assert.Equal(SidecarJsonRpcTransport.ExecutableSource.PluginDirectory, source);
    }

    [Fact]
    public void A_Relative_Path_Resolves_Against_The_Plugin_Directory()
    {
        var shipped = TouchInPluginDir(Path.Combine("bin", "zenoh-sidecar"));

        var (fileName, source) = SidecarJsonRpcTransport.ResolveExecutable(
            "bin/zenoh-sidecar", _pluginDir);

        // Path.Combine keeps whichever separator the manifest wrote, so
        // compare the file this points at rather than its spelling.
        Assert.Equal(Path.GetFullPath(shipped), Path.GetFullPath(fileName));
        Assert.True(File.Exists(fileName));
        Assert.Equal(SidecarJsonRpcTransport.ExecutableSource.PluginDirectory, source);
    }

    [Theory]
    [InlineData("bin/missing-sidecar")]
    [InlineData("bin\\missing-sidecar")]
    public void A_Missing_Relative_Path_Does_Not_Reach_Path(string executable)
    {
        // Both separators count, on both platforms: a manifest is written
        // once and installed everywhere, so "bin\sidecar" must not be
        // mistaken for a bare command on Linux.
        var (fileName, source) = SidecarJsonRpcTransport.ResolveExecutable(executable, _pluginDir);

        Assert.Equal(Path.Combine(_pluginDir, executable), fileName);
        Assert.Equal(SidecarJsonRpcTransport.ExecutableSource.PluginDirectory, source);
    }

    [Fact]
    public void A_Rooted_Path_Is_Taken_As_Given()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "elsewhere", "sidecar");

        var (fileName, source) = SidecarJsonRpcTransport.ResolveExecutable(rooted, _pluginDir);

        Assert.Equal(rooted, fileName);
        Assert.Equal(SidecarJsonRpcTransport.ExecutableSource.PluginDirectory, source);
    }

    [Fact]
    public async Task An_Interpreter_On_Path_Actually_Spawns()
    {
        // The end of #692: a manifest that names a command rather than a
        // shipped file starts on Linux and on Windows. The plugin
        // directory holds no such file, so this only passes through the
        // PATH step.
        var (_, source) = SidecarJsonRpcTransport.ResolveExecutable(BareCommandOnPath, _pluginDir);
        Assert.Equal(SidecarJsonRpcTransport.ExecutableSource.SearchPath, source);

        var manifest = new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.PathLookup",
            Protocol: new SidecarProtocolMetadata("path-lookup", "Path lookup"),
            Executable: BareCommandOnPath,
            Args: ExitImmediatelyArgs,
            ShutdownTimeoutMs: 500);

        // Start returning at all is the assertion. Before #692 this threw,
        // because the name resolved to a <pluginDir>/<command> that no
        // plugin ships.
        await using var transport = SidecarJsonRpcTransport.Start(manifest, _pluginDir);
    }

    [Fact]
    public void A_Name_In_Neither_Place_Names_Both_In_The_Error()
    {
        var manifest = new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.NoSuchCommand",
            Protocol: new SidecarProtocolMetadata("missing", "Missing"),
            Executable: "bowire-no-such-command-" + Guid.NewGuid().ToString("n"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => SidecarJsonRpcTransport.Start(manifest, _pluginDir));

        // The OS error alone doesn't say which of the two steps was
        // taken, which is exactly what the reader needs.
        Assert.Contains(_pluginDir, ex.Message, StringComparison.Ordinal);
        Assert.Contains("PATH", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void A_Missing_Relative_Path_Says_Why_Path_Was_Not_Searched()
    {
        var manifest = new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.NoSuchFile",
            Protocol: new SidecarProtocolMetadata("missing", "Missing"),
            Executable: "bin/no-such-sidecar");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SidecarJsonRpcTransport.Start(manifest, _pluginDir));

        Assert.Contains("no-such-sidecar", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bare command", ex.Message, StringComparison.Ordinal);
    }
}
