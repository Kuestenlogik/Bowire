// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins.Sidecar;

namespace Kuestenlogik.Bowire.Tests.Plugins;

/// <summary>
/// Shared handling of the fake sidecar that
/// <c>tests/Kuestenlogik.Bowire.SidecarFake/</c> builds — a tiny .NET exe,
/// so a test can spawn a real subprocess without needing Python, Node or
/// Go on the host.
/// </summary>
internal static class SidecarFake
{
    /// <summary>
    /// Resolve the fake-sidecar executable's on-disk path.
    /// </summary>
    /// <remarks>
    /// The fake exe's output layout under artifacts/bin varies with how it
    /// was built — flat (<c>SidecarFake/bowire-sidecar-fake</c>) when
    /// pulled in as a P2P dependency, or nested under a Debug/Release[/tfm]
    /// folder for a standalone build. Rather than reconstruct the exact
    /// path (which differs between local Debug and CI Release), walk up to
    /// artifacts/bin and search the fake's tree for the apphost binary.
    /// </remarks>
    public static string Locate()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
        // current build configuration so a Release test run doesn't pick up
        // a stale Debug binary (and vice-versa).
        var configSegment = Path.DirectorySeparatorChar + BuildConfiguration + Path.DirectorySeparatorChar;
        var preferred = matches.FirstOrDefault(m =>
            m.Contains(configSegment, StringComparison.OrdinalIgnoreCase));
        return preferred ?? matches[0];
    }

    /// <summary>
    /// Stop the subprocess a test started. <see cref="SidecarBowireProtocol"/>
    /// is not disposable — the transport it owns is — so reach the transport
    /// the same way the adapter does. Without this the test process keeps
    /// every fake sidecar it spawned alive until GC.
    /// </summary>
    public static async Task ShutdownAsync(SidecarBowireProtocol plugin)
    {
        try
        {
            var transport = await plugin.EnsureStartedAsync(CancellationToken.None);
            await transport.DisposeAsync();
        }
        catch
        {
            // Best-effort — if EnsureStartedAsync itself failed there was no
            // process to dispose anyway.
        }
    }

    private static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}
