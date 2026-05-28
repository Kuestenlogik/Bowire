// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins.Sidecar;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// End-to-end coverage for the sidecar JSON-RPC transport against a
/// real subprocess. The fake sidecar lives at
/// <c>tests/Kuestenlogik.Bowire.SidecarFake/</c> and ships as a tiny
/// .NET exe so we don't need Python / Node / Go on the test host.
/// </summary>
public class SidecarBowireProtocolIntegrationTests
{
    /// <summary>
    /// Resolve the fake-sidecar executable's on-disk path. The two
    /// projects share artifacts/bin/ layout under the shared
    /// Directory.Build.props, so we navigate from the test assembly's
    /// own location up to <c>artifacts/bin/Kuestenlogik.Bowire.SidecarFake</c>
    /// and pick the matching TFM + Debug/Release folder.
    /// </summary>
    private static string LocateFakeExecutable()
    {
        // typical baseDir:
        //   .../artifacts/bin/Kuestenlogik.Bowire.Tests/Debug/net10.0/
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = new DirectoryInfo(baseDir);

        // Walk up to artifacts/bin/
        DirectoryInfo? binRoot = dir;
        while (binRoot is not null && binRoot.Name != "bin")
            binRoot = binRoot.Parent;
        if (binRoot is null)
            throw new InvalidOperationException("Could not locate artifacts/bin from " + baseDir);

        var fakeRoot = Path.Combine(binRoot.FullName, "Kuestenlogik.Bowire.SidecarFake");
        if (!Directory.Exists(fakeRoot))
            throw new InvalidOperationException("Fake sidecar bin dir missing: " + fakeRoot);

        // Pick the same Debug/Release + TFM the test was built with —
        // navigate the analogous tail of the test's own path.
        var relativeTail = Path.GetRelativePath(
            Path.Combine(binRoot.FullName, "Kuestenlogik.Bowire.Tests"),
            baseDir);
        var fakeBuildDir = Path.Combine(fakeRoot, relativeTail);
        if (!Directory.Exists(fakeBuildDir))
            throw new InvalidOperationException("Fake sidecar build dir missing: " + fakeBuildDir);

        var exeName = OperatingSystem.IsWindows() ? "bowire-sidecar-fake.exe" : "bowire-sidecar-fake";
        var exePath = Path.Combine(fakeBuildDir, exeName);
        if (!File.Exists(exePath))
            throw new InvalidOperationException("Fake sidecar exe missing: " + exePath);
        return exePath;
    }

    private static SidecarBowireProtocol BuildPlugin()
    {
        var exe = LocateFakeExecutable();
        var pluginDir = Path.GetDirectoryName(exe)!;
        var manifest = new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.SidecarFake",
            Protocol: new SidecarProtocolMetadata("fake", "Fake"),
            Executable: Path.GetFileName(exe),
            Args: null,
            EnvPrefix: "BOWIRE_FAKE_",
            ShutdownTimeoutMs: 2000);
        return new SidecarBowireProtocol(manifest, pluginDir);
    }

    [Fact]
    public async Task DiscoverAsync_Round_Trips_Through_The_Sidecar()
    {
        var plugin = BuildPlugin();
        try
        {
            var services = await plugin.DiscoverAsync(
                serverUrl: "fake://demo",
                showInternalServices: false,
                ct: TestContext.Current.CancellationToken);

            var svc = Assert.Single(services);
            Assert.Equal("Echo", svc.Name);
            Assert.Equal("fake", svc.Source);
            Assert.Equal("fake://demo", svc.OriginUrl);
            var method = Assert.Single(svc.Methods);
            Assert.Equal("echo", method.Name);
            Assert.Equal("Echo/echo", method.FullName);
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task InvokeAsync_Round_Trips_Through_The_Sidecar()
    {
        var plugin = BuildPlugin();
        try
        {
            var result = await plugin.InvokeAsync(
                serverUrl: "fake://demo",
                service: "Echo",
                method: "Echo/echo",
                jsonMessages: ["hello"],
                showInternalServices: false,
                metadata: null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Equal("echo: hello", result.Response);
            Assert.Equal("fake", result.Metadata["source"]);
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_Notifications_Until_End()
    {
        var plugin = BuildPlugin();
        try
        {
            var received = new List<string>();
            await foreach (var msg in plugin.InvokeStreamAsync(
                serverUrl: "fake://demo",
                service: "Echo",
                method: "Echo/echo",
                jsonMessages: [],
                showInternalServices: false,
                metadata: null,
                ct: TestContext.Current.CancellationToken))
            {
                received.Add(msg);
                if (received.Count >= 3) break;
            }

            Assert.Equal(3, received.Count);
            Assert.Equal("tick-1", received[0]);
            Assert.Equal("tick-2", received[1]);
            Assert.Equal("tick-3", received[2]);
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task OpenChannel_Round_Trips_Send_And_Receive()
    {
        var ct = TestContext.Current.CancellationToken;
        var plugin = BuildPlugin();
        try
        {
            var channel = await plugin.OpenChannelAsync(
                serverUrl: "fake://demo",
                service: "Echo",
                method: "Echo/echo",
                showInternalServices: false,
                metadata: null,
                ct: ct);

            Assert.NotNull(channel);
            await using var ch = channel!;
            Assert.True(ch.IsClientStreaming);
            Assert.True(ch.IsServerStreaming);

            // Read the echoed inbound frames in the background; the fake
            // sidecar replies to each channel.send with a $/channel/data
            // "ack: ..." notification.
            var received = new List<string>();
            var readTask = Task.Run(async () =>
            {
                await foreach (var msg in ch.ReadResponsesAsync(ct))
                {
                    received.Add(msg);
                    if (received.Count >= 2) break;
                }
            }, ct);

            Assert.True(await ch.SendAsync("hello", ct));
            Assert.True(await ch.SendAsync("world", ct));

            await readTask.WaitAsync(TimeSpan.FromSeconds(10), ct);

            Assert.Equal(2, ch.SentCount);
            Assert.Equal(2, received.Count);
            Assert.Equal("ack: hello", received[0]);
            Assert.Equal("ack: world", received[1]);
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task First_Call_Initializes_And_Reflects_Sidecar_Metadata()
    {
        var plugin = BuildPlugin();
        try
        {
            // Before any call, plugin reads manifest metadata.
            Assert.Equal("Fake", plugin.Name);

            // Drive a call to force initialize handshake.
            _ = await plugin.DiscoverAsync("fake://x", false, TestContext.Current.CancellationToken);

            // Same id/name (manifest matched what sidecar reported);
            // iconSvg now picks up the sidecar's "<svg/>" override.
            Assert.Equal("fake", plugin.Id);
            Assert.Equal("Fake", plugin.Name);
            Assert.Equal("<svg/>", plugin.IconSvg);
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    /// <summary>
    /// SidecarBowireProtocol holds the transport internally — call
    /// EnsureStartedAsync to grab it and dispose so each test releases
    /// its subprocess cleanly. Without this the test process keeps the
    /// fake sidecar alive until GC.
    /// </summary>
    private static async Task ShutdownAsync(SidecarBowireProtocol plugin)
    {
        try
        {
            var transport = await plugin.EnsureStartedAsync(CancellationToken.None);
            await transport.DisposeAsync();
        }
        catch
        {
            // Best-effort — if EnsureStartedAsync itself failed there
            // was no process to dispose anyway.
        }
    }
}
