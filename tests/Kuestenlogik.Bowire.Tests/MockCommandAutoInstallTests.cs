// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.PluginLoading;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Exercises <see cref="MockCommand"/>'s auto-install fan-out — the
/// path that fires when a recording references a protocol whose plugin
/// is missing and <c>--auto-install</c> is set. The
/// <see cref="MockCommand.AutoInstallInvoker"/> seam lets us intercept
/// the actual install call so we can drive both success and failure
/// branches offline.
/// </summary>
// Joins CwdSerialised rather than carrying a private collection (#543).
// These tests read BOWIRE_PLUGIN_DIR through MockCommand; BowireConfigurationTests
// clears that same variable to exercise the fallback. A private collection
// serialises this class against itself but runs it in parallel with THAT one,
// so the clear landed mid-test, MockCommand fell back to ~/.bowire/plugins, and
// an installed plugin made a protocol look present. CwdSerialisedCollectionDefinition
// exists for exactly this and already covers the config tests.
[Collection("CwdSerialised")]
public sealed class MockCommandAutoInstallTests : IDisposable
{
    private const string PluginDirVar = "BOWIRE_PLUGIN_DIR";

    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("bowire-mock-ai-").FullName;

    private string? _previousPluginDir;
    private bool _pluginDirOverridden;

    /// <summary>
    /// Guarantee an empty plugin directory at the moment
    /// <see cref="MockCommand.RunAsync"/> resolves one, and put the
    /// previous value back afterwards.
    /// </summary>
    /// <remarks>
    /// Setting this in the constructor is not enough, and that is measured
    /// rather than assumed (#543): <c>BowireConfigurationTests</c> clears
    /// <c>BOWIRE_PLUGIN_DIR</c> to exercise the fallback, and a run of the
    /// full assembly still found the variable <c>&lt;null&gt;</c> inside this
    /// test — long after this class's constructor had set it. Writing it
    /// immediately before the call closes the window that matters.
    /// The assembly-wide redirect in <see cref="TestPluginIsolation"/> and
    /// the CwdSerialised collection remain the outer layers; this is the one
    /// that makes the assertion depend on nothing but itself.
    /// </remarks>
    /// <summary>
    /// Pick <paramref name="count"/> protocol ids from Bowire's package
    /// catalogue that this process does NOT already have registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tests used to hard-code "kafka" and "dis". That silently assumed
    /// nobody had those installed — and the assumption broke the moment a
    /// developer ran <c>bowire plugin install Kuestenlogik.Bowire.Protocol.Dis</c>
    /// (#543). Worse, it is not fixable by pointing BOWIRE_PLUGIN_DIR
    /// somewhere empty: once ANY test in the run has loaded a plugin
    /// assembly, it stays visible through
    /// <c>AppDomain.CurrentDomain.GetAssemblies()</c> for the rest of the
    /// process, and <c>BowireProtocolRegistry.Discover</c> reports it as
    /// present regardless of directories. Measured, repeatedly.
    /// </para>
    /// <para>
    /// What these tests actually verify is the auto-install fan-out — that
    /// N missing protocols produce N install calls — not any particular
    /// protocol name. Choosing the ids at runtime tests exactly that and
    /// nothing about the developer's machine.
    /// </para>
    /// </remarks>
    private static List<string> PickMissingProtocols(int count)
    {
        var registered = BowireProtocolRegistry.Discover().Protocols
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. PluginPackageMap.Snapshot().Keys
            .Where(id => !registered.Contains(id))
            .Take(count)];
    }

    private string IsolatedPluginDir()
    {
        // Capture once: a test may call this more than once, and the second
        // capture would record OUR value as the thing to restore.
        if (!_pluginDirOverridden)
        {
            _previousPluginDir = Environment.GetEnvironmentVariable(PluginDirVar);
            _pluginDirOverridden = true;
        }

        var dir = Path.Combine(_tempDir, "plugins");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(PluginDirVar, dir);
        return dir;
    }

    public void Dispose()
    {
        // Hand the variable back exactly as we found it. Leaving our temp
        // path behind would be the same defect that made this test flaky in
        // the first place — a test writing process-global state that the
        // next one silently inherits, except pointing at a directory that
        // is about to be deleted.
        if (_pluginDirOverridden)
        {
            Environment.SetEnvironmentVariable(PluginDirVar, _previousPluginDir);
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_AutoInstall_KnownProtocol_InvokesInstaller()
    {
        // Recording references "kafka" (mapped to Kuestenlogik.Bowire.Protocol.Kafka
        // in PluginPackageMap; not loaded by the host). With
        // AutoInstall=true and the installer stubbed to "succeed",
        // TryAutoInstallAsync exits true, MockCommand reloads plugins,
        // re-enumerates emitters/hosts, then MockServer.StartAsync
        // tries to bring up the recording — it'll still fail at the
        // protocol-bind step because the stub doesn't actually drop a
        // DLL in pluginDir, but the auto-install foreach + reload
        // branches all ran. Pre-cancel the token so any server that
        // does come up shuts down immediately instead of waiting on
        // Ctrl+C.
        var prev = MockCommand.AutoInstallInvoker;
        var seenPackageIds = new List<string>();
        try
        {
            MockCommand.AutoInstallInvoker = (pkg, _, _, _) =>
            {
                seenPackageIds.Add(pkg);
                return Task.FromResult(0);
            };

            var rec = Path.Combine(_tempDir, "rec.json");
            await File.WriteAllTextAsync(rec, MakeRecordingJson("kafka"),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions
            {
                RecordingPath = rec,
                AutoInstall = true,
                Host = "127.0.0.1",
                Port = 0,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            IsolatedPluginDir();
            var rc = await MockCommand.RunAsync(cli, ct: cts.Token);

            Assert.Contains(rc, s_acceptedExitCodes);
            Assert.Single(seenPackageIds);
            Assert.Equal("Kuestenlogik.Bowire.Protocol.Kafka", seenPackageIds[0]);
        }
        finally
        {
            MockCommand.AutoInstallInvoker = prev;
        }
    }

    [Fact]
    public async Task RunAsync_AutoInstall_InstallerFails_ReturnsOne()
    {
        // Stubbed installer returns non-zero → failures > 0 →
        // TryAutoInstallAsync returns false → MockCommand returns 1.
        var prev = MockCommand.AutoInstallInvoker;
        try
        {
            MockCommand.AutoInstallInvoker = (_, _, _, _) => Task.FromResult(99);

            var rec = Path.Combine(_tempDir, "rec.json");
            await File.WriteAllTextAsync(rec, MakeRecordingJson("kafka"),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions { RecordingPath = rec, AutoInstall = true };
            IsolatedPluginDir();
            var rc = await MockCommand.RunAsync(cli, ct: TestContext.Current.CancellationToken);

            Assert.Equal(1, rc);
        }
        finally
        {
            MockCommand.AutoInstallInvoker = prev;
        }
    }

    [Fact]
    public async Task RunAsync_AutoInstall_MultipleMissing_InstallsEach()
    {
        // Recording references two mapped protocols this process does not
        // have → TryAutoInstallAsync iterates both, the stub records each
        // install call. The ids are chosen at runtime, see PickMissingProtocols.
        var missing = PickMissingProtocols(2);
        Assert.SkipWhen(missing.Count < 2,
            "Needs two catalogue protocols that are not registered in this "
            + "process; this host has too many of them loaded.");

        var prev = MockCommand.AutoInstallInvoker;
        var seen = new List<string>();
        try
        {
            MockCommand.AutoInstallInvoker = (pkg, _, _, _) =>
            {
                seen.Add(pkg);
                return Task.FromResult(0);
            };

            var rec = Path.Combine(_tempDir, "multi.json");
            await File.WriteAllTextAsync(rec, MakeMultiRecordingJson(missing[0], missing[1]),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions
            {
                RecordingPath = rec,
                AutoInstall = true,
                Host = "127.0.0.1",
                Port = 0,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            IsolatedPluginDir();
            await MockCommand.RunAsync(cli, ct: cts.Token);

            // Self-explaining failure: "Expected 2, Actual 1" says nothing
            // about WHY a protocol stopped counting as missing, and the
            // answer is always environmental (#543) — a plugin dir that
            // isn't the one we think, or a protocol already registered in
            // this process.
            // Self-explaining failure: "Expected 2, Actual 1" says nothing
            // about WHY a protocol stopped counting as missing, and the
            // answer is always environmental (#543).
            Assert.True(seen.Count == 2,
                $"expected 2 installs for [{string.Join(", ", missing)}], saw {seen.Count}: "
                + $"[{string.Join(", ", seen)}]"
                + $"; BOWIRE_PLUGIN_DIR={Environment.GetEnvironmentVariable(PluginDirVar) ?? "<null>"}"
                + $"; registry knows [{string.Join(", ", BowireProtocolRegistry.Discover().Protocols.Select(p => p.Id))}]");
            Assert.Contains(PluginPackageMap.TryGetPackageId(missing[0]), seen);
            Assert.Contains(PluginPackageMap.TryGetPackageId(missing[1]), seen);
        }
        finally
        {
            MockCommand.AutoInstallInvoker = prev;
        }
    }

    private static readonly int[] s_acceptedExitCodes = [0, 1];

    private static string MakeRecordingJson(string protocolId) => $$"""
        {
          "id": "rec_test",
          "name": "Test recording",
          "createdAt": 0,
          "recordingFormatVersion": 2,
          "steps": [
            {
              "id": "step_1",
              "capturedAt": 0,
              "protocol": "{{protocolId}}",
              "service": "svc",
              "method": "m",
              "methodType": "Unary",
              "serverUrl": "http://localhost:1",
              "httpVerb": "GET",
              "httpPath": "/x",
              "status": "OK"
            }
          ]
        }
        """;

    private static string MakeMultiRecordingJson(string a, string b) => $$"""
        {
          "id": "rec_test",
          "name": "Test recording",
          "createdAt": 0,
          "recordingFormatVersion": 2,
          "steps": [
            {
              "id": "step_a",
              "capturedAt": 0,
              "protocol": "{{a}}",
              "service": "svc",
              "method": "m",
              "methodType": "Unary",
              "serverUrl": "http://localhost:1",
              "httpVerb": "GET",
              "httpPath": "/a",
              "status": "OK"
            },
            {
              "id": "step_b",
              "capturedAt": 0,
              "protocol": "{{b}}",
              "service": "svc",
              "method": "m",
              "methodType": "Unary",
              "serverUrl": "http://localhost:1",
              "httpVerb": "GET",
              "httpPath": "/b",
              "status": "OK"
            }
          ]
        }
        """;
}

// The private collection this class used to define is gone: it serialised
// these tests against themselves while leaving them free to run alongside
// BowireConfigurationTests, which is precisely what broke them (#543).
// CwdSerialisedCollectionDefinition owns that serialisation now.
