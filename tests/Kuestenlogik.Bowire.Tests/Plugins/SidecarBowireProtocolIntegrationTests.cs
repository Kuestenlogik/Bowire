// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins.Sidecar;
using Microsoft.Extensions.Logging;
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
        // The fake exe's output layout under artifacts/bin varies with
        // how it was built — flat (`SidecarFake/bowire-sidecar-fake`)
        // when pulled in as a P2P dependency, or nested under a
        // Debug/Release[/tfm] folder for a standalone build. Rather than
        // reconstruct the exact path (which differs between local Debug
        // and CI Release), walk up to artifacts/bin and recursively
        // search the fake's tree for the apphost binary.
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        DirectoryInfo? binRoot = new(baseDir);
        while (binRoot is not null && binRoot.Name != "bin")
            binRoot = binRoot.Parent;
        if (binRoot is null)
            throw new InvalidOperationException("Could not locate artifacts/bin from " + baseDir);

        var fakeRoot = Path.Combine(binRoot.FullName, "Kuestenlogik.Bowire.SidecarFake");
        if (!Directory.Exists(fakeRoot))
            throw new InvalidOperationException("Fake sidecar bin dir missing: " + fakeRoot);

        var exeName = OperatingSystem.IsWindows() ? "bowire-sidecar-fake.exe" : "bowire-sidecar-fake";
        var matches = Directory.GetFiles(fakeRoot, exeName, SearchOption.AllDirectories);
        if (matches.Length == 0)
            throw new InvalidOperationException(
                $"Fake sidecar exe '{exeName}' not found anywhere under {fakeRoot}");

        // When several configs were built, prefer the one matching the
        // current build configuration so a Release test run doesn't pick
        // up a stale Debug binary (and vice-versa).
        var config = GetBuildConfiguration();
        var configSegment = Path.DirectorySeparatorChar + config + Path.DirectorySeparatorChar;
        var preferred = matches.FirstOrDefault(m =>
            m.Contains(configSegment, StringComparison.OrdinalIgnoreCase));
        return preferred ?? matches[0];
    }

    private static string GetBuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static SidecarBowireProtocol BuildPlugin(
        IReadOnlyList<string>? args = null, ILogger? logger = null)
    {
        var exe = LocateFakeExecutable();
        var pluginDir = Path.GetDirectoryName(exe)!;
        var manifest = new SidecarPluginManifest(
            PackageId: "Kuestenlogik.Bowire.Tests.SidecarFake",
            Protocol: new SidecarProtocolMetadata("fake", "Fake"),
            Executable: Path.GetFileName(exe),
            Args: args,
            EnvPrefix: "BOWIRE_FAKE_",
            ShutdownTimeoutMs: 2000);
        return new SidecarBowireProtocol(manifest, pluginDir, logger);
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

    // ---------------- #416: protocol-version + capabilities handshake ----------------

    [Fact]
    public async Task IncompatibleProtocolVersion_Is_Rejected_At_Handshake()
    {
        // The fake advertises a version far above what the host supports →
        // initialize must fail cleanly, not proceed to the first call.
        var plugin = BuildPlugin(args: ["--protocol-version", "999"]);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                plugin.DiscoverAsync("fake://x", false, TestContext.Current.CancellationToken));
            Assert.Contains("protocol version 999", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Update Bowire", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task LegacySidecar_Without_ProtocolVersion_Is_Tolerated_And_Warns()
    {
        var logger = new CapturingLogger();
        var plugin = BuildPlugin(args: ["--legacy"], logger: logger);
        try
        {
            // A pre-#416 sidecar (no protocolVersion / capabilities) still works.
            var services = await plugin.DiscoverAsync("fake://x", false, TestContext.Current.CancellationToken);
            Assert.Single(services);
            Assert.Contains(logger.Warnings, w =>
                w.Contains("did not advertise a protocol version", StringComparison.Ordinal));
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    [Fact]
    public async Task Sidecar_Advertising_No_Channels_Returns_Null_Without_Round_Trip()
    {
        var plugin = BuildPlugin(args: ["--no-channels"]);
        try
        {
            // Force the initialize handshake so capabilities are known.
            _ = await plugin.DiscoverAsync("fake://x", false, TestContext.Current.CancellationToken);

            var channel = await plugin.OpenChannelAsync(
                "fake://x", "Echo", "echo", false, null, TestContext.Current.CancellationToken);
            Assert.Null(channel);
        }
        finally
        {
            await ShutdownAsync(plugin);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
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
