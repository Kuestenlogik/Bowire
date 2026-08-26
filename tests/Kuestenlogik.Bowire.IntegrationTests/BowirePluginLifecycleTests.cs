// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Plugins;
using Kuestenlogik.Bowire.Projects;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// The Settings → Plugins buttons: load, unload, reset-storage — and the
/// guards on the install / uninstall endpoints behind them.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these mutates something that outlives the request: the live
/// protocol registry, the persisted disabled list, a plugin's state directory.
/// So each test roots both the storage resolver and the user store at a temp
/// tree — a test that wrote to the developer's real <c>~/.bowire/</c> would
/// disable plugins in their actual install.
/// </para>
/// <para>
/// The install / update / uninstall endpoints shell out to the <c>bowire</c>
/// CLI, so only their refusals are exercised here. That is deliberate rather
/// than a gap: the refusals are the security-relevant half — packageId and
/// version come straight off the request body and end up in a child process's
/// argument list.
/// </para>
/// </remarks>
[Collection("BowireStorageRoot")]
public sealed class BowirePluginLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-lifecycle-" + Guid.NewGuid().ToString("N"));
    /// <summary>
    /// A plugin id no other test uses.
    /// </summary>
    /// <remarks>
    /// The disabled-plugins store keeps its set in a process-global static, so
    /// a shared id would make the second test to run a no-op: <c>Disable</c>
    /// returns early when the id is already in the set, and the file this test
    /// looks for is then never written to <em>its</em> temp root.
    /// </remarks>
    private readonly string _pluginId = "Test.Plugin." + Guid.NewGuid().ToString("N")[..8];
    private readonly IBowirePathResolver _previousPaths = BowirePaths.Current;
    private readonly IBowireUserStore _previousUsers = BowireUserContext.Current;

    public BowirePluginLifecycleTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        BowirePaths.Current = new BowirePathResolver(
            name => name == BowirePathResolver.DataDirVariable ? _root : null,
            () => _root);
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
    }

    public void Dispose()
    {
        BowirePaths.Current = _previousPaths;
        BowireUserContext.Current = _previousUsers;
        BowireEndpointHelpers.ResetRegistry();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowirePluginEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Lifecycle(
        IHost host, string pluginId, string action)
    {
        using var resp = await host.GetTestClient().PostAsync(
            new Uri($"/api/plugins/{pluginId}/lifecycle/{action}", UriKind.Relative),
            content: null, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    // ---- the dispatcher ----

    [Fact]
    public async Task An_Action_Nothing_Answers_To_Lists_The_Ones_That_Exist()
    {
        // The panel sends fixed strings, so an unknown action means a client
        // and a server that disagree — naming the valid set is what makes
        // that diagnosable from a network tab.
        using var host = await BuildHost();

        var (status, body) = await Lifecycle(host, _pluginId, "explode");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var error = body.GetProperty("error").GetString()!;
        Assert.Contains("restart", error, StringComparison.Ordinal);
        Assert.Contains("unload", error, StringComparison.Ordinal);
        Assert.Contains("load", error, StringComparison.Ordinal);
        Assert.Contains("reset-storage", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Action_Is_Matched_Whatever_Its_Casing()
    {
        // Normalised server-side rather than trusted from the client.
        using var host = await BuildHost();

        var (status, _) = await Lifecycle(host, _pluginId, "RESET-STORAGE");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Every_Refusal_Carries_The_Plugin_And_Action_It_Was_About()
    {
        // The panel renders per-row; a problem document with no id cannot be
        // attributed to the row that produced it.
        using var host = await BuildHost();

        var (_, body) = await Lifecycle(host, _pluginId, "explode");

        Assert.Equal(_pluginId, body.GetProperty("pluginId").GetString());
        Assert.Equal("explode", body.GetProperty("action").GetString());
        Assert.False(body.GetProperty("ok").GetBoolean());
    }

    // ---- restart ----

    [Fact]
    public async Task Restarting_Something_The_Registry_Does_Not_Hold_Points_At_Load()
    {
        // The distinction matters: restart operates on a live instance, and a
        // plugin that was disabled at start-up has none. Saying "use load" is
        // the difference between a dead end and a next step.
        using var host = await BuildHost();

        var (status, body) = await Lifecycle(host, "Nothing.Answers.To.This", "restart");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("load", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    // ---- unload / load ----

    [Fact]
    public async Task Unloading_A_Plugin_That_Was_Not_Active_Still_Records_The_Decision()
    {
        // "Unload" is a persisted intent, not just an in-memory operation:
        // the id goes on the disabled list so it stays off across restarts.
        // Reporting failure because it happened not to be loaded would leave
        // the operator's decision unrecorded.
        using var host = await BuildHost();

        var (status, body) = await Lifecycle(host, _pluginId, "unload");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.False(body.GetProperty("wasActive").GetBoolean());
        Assert.True(BowireDisabledPluginsStore.IsDisabled(_pluginId));
    }

    [Fact]
    public async Task Unloading_Twice_Is_Not_An_Error()
    {
        // Double-click on the button, or two operators in two tabs.
        using var host = await BuildHost();

        await Lifecycle(host, _pluginId, "unload");
        var (status, _) = await Lifecycle(host, _pluginId, "unload");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(BowireDisabledPluginsStore.IsDisabled(_pluginId));
    }

    [Fact]
    public async Task Loading_Takes_A_Plugin_Off_The_Disabled_List_Even_When_It_Cannot_Be_Loaded()
    {
        // `load` re-runs discovery over the assemblies already in-process, so
        // an id with no type behind it ends in a 404 that points at install.
        //
        // Worth pinning: the disabled-list entry is dropped *before* that
        // discovery, so the id comes back enabled anyway. That is the right
        // order — the operator said "stop keeping this off" and the store
        // records the decision whether or not the assembly happens to be
        // present — but it is not what the 404 suggests on its own.
        using var host = await BuildHost();
        await Lifecycle(host, _pluginId, "unload");

        var (status, body) = await Lifecycle(host, _pluginId, "load");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("install", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.False(BowireDisabledPluginsStore.IsDisabled(_pluginId));
    }

    [Fact]
    public async Task The_Disabled_List_Survives_As_A_File_Not_Just_In_Memory()
    {
        // The whole point of persisting: a plugin unloaded today is still
        // unloaded after a restart.
        using var host = await BuildHost();

        await Lifecycle(host, _pluginId, "unload");

        var file = Path.Combine(_root, "disabled-plugins.json");
        Assert.True(File.Exists(file), $"expected {file} to be written");
        Assert.Contains(_pluginId,
            await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // ---- reset-storage ----

    [Fact]
    public async Task Resetting_Storage_Clears_The_State_Directory_And_Says_So()
    {
        using var host = await BuildHost();
        var stateDir = Path.Combine(_root, "plugins", _pluginId, "state");
        Directory.CreateDirectory(stateDir);
        await File.WriteAllTextAsync(Path.Combine(stateDir, "state.json"), "{}",
            TestContext.Current.CancellationToken);

        var (status, body) = await Lifecycle(host, _pluginId, "reset-storage");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("diskCleared").GetBoolean());
        Assert.False(Directory.Exists(stateDir));
    }

    [Fact]
    public async Task Resetting_Storage_With_Nothing_On_Disk_Is_Still_A_Success()
    {
        // The browser half of the reset (a localStorage prefix) has to happen
        // either way, so a plugin that only ever stored client-side must not
        // get an error here.
        using var host = await BuildHost();

        var (status, body) = await Lifecycle(host, _pluginId, "reset-storage");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("diskCleared").GetBoolean());
    }

    [Fact]
    public async Task Resetting_Storage_Hands_Back_The_Local_Storage_Prefix_To_Flush()
    {
        // The server cannot reach the browser's localStorage, so the response
        // carries the key prefix the JS side is expected to clear. If this
        // ever stopped matching what the plugin writes, the reset would look
        // like it worked and leave half the state behind.
        using var host = await BuildHost();

        var (_, body) = await Lifecycle(host, _pluginId, "reset-storage");

        Assert.Equal($"bowire_plugin_{_pluginId}_",
            body.GetProperty("localStorageKeyPrefix").GetString());
        Assert.Contains(_pluginId,
            body.GetProperty("stateDirectory").GetString()!, StringComparison.Ordinal);
    }

    // ---- the guards on install / update / uninstall ----

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Install(IHost host, object body)
    {
        using var resp = await host.GetTestClient().PostAsJsonAsync(
            new Uri("/api/plugins/install", UriKind.Relative), body,
            TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (resp.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task Installing_Nothing_In_Particular_Is_Refused()
    {
        using var host = await BuildHost();

        var (status, body) = await Install(host, new { });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("packageId", body.GetProperty("title").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Acme.Plugin; rm -rf /")]
    [InlineData("../../etc/passwd")]
    [InlineData("--version")]
    [InlineData("Acme Plugin")]
    [InlineData("$(whoami)")]
    public async Task A_Package_Id_That_Is_Not_Nuget_Shaped_Never_Reaches_The_Child_Process(string packageId)
    {
        // The argument list already prevents shell parsing; this whitelist is
        // the second line, and it is the one that keeps a leading `--` from
        // being read as a flag by the CLI's own parser.
        using var host = await BuildHost();

        var (status, body) = await Install(host, new { packageId });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("urn:bowire:plugin:invalid-id", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_Version_That_Is_Not_Nuget_Shaped_Is_Refused_Too()
    {
        using var host = await BuildHost();

        var (status, body) = await Install(host, new { packageId = "Acme.Bowire.Protocol.Widget", version = "1.0.0 && curl evil" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("urn:bowire:plugin:invalid-version", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_Blank_Package_Id_Is_Refused_Before_The_Whitelist_Sees_It()
    {
        // Empty is the "update everything" signal internally, so it must not
        // reach that path from an install request.
        using var host = await BuildHost();

        var (status, _) = await Install(host, new { packageId = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Uninstalling_Something_Unsafe_Is_Refused_The_Same_Way()
    {
        using var host = await BuildHost();

        using var resp = await host.GetTestClient().DeleteAsync(
            new Uri("/api/plugins/..%2F..%2Fetc", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- lifecycle against a plugin that is actually loaded ----
    //
    // The tests above drive ids the registry has never heard of. These
    // register a live one first, because restart and unload only do their
    // real work when there is an instance to replace or dispose — and
    // "restart" replacing nothing while reporting success is the failure the
    // Settings button would hide.

    /// <summary>A protocol with a public parameterless ctor, as restart requires.</summary>
    public sealed class RestartableProtocol : IBowireProtocol, IDisposable
    {
        public static int Constructed { get; set; }
        public static int Disposed { get; set; }

        public RestartableProtocol() => Constructed++;

        public string Id => "restartable-stub";
        public string Name => "Restartable";
        public string IconSvg => "<svg/>";

        public void Dispose() => Disposed++;

        public Task<List<BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

#pragma warning disable CS1998 // Nothing to stream.
        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
    }

    private static void RegisterLivePlugin()
    {
        var registry = new BowireProtocolRegistry();
        registry.Register(new RestartableProtocol());
        BowireEndpointHelpers.SetRegistry(registry);
    }

    [Fact]
    public async Task Restarting_A_Live_Plugin_Builds_A_Fresh_Instance_And_Disposes_The_Old_One()
    {
        // Both halves matter: a restart that never constructed a replacement
        // would leave the plugin dead, and one that never disposed the old
        // instance leaks whatever it held open — a socket, a file, a client.
        RegisterLivePlugin();
        // Reset *after* registering: constructing the live instance counts too,
        // and what is being measured is what the restart itself did.
        RestartableProtocol.Constructed = 0;
        RestartableProtocol.Disposed = 0;
        using var host = await BuildHost();

        var (status, body) = await Lifecycle(host, "restartable-stub", "restart");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(1, RestartableProtocol.Constructed);
        Assert.Equal(1, RestartableProtocol.Disposed);
        Assert.NotNull(BowireEndpointHelpers.GetRegistry().GetById("restartable-stub"));
    }

    [Fact]
    public async Task Unloading_A_Live_Plugin_Takes_It_Out_Of_The_Registry_And_Disposes_It()
    {
        RegisterLivePlugin();
        RestartableProtocol.Disposed = 0;
        using var host = await BuildHost();

        var (status, body) = await Lifecycle(host, "restartable-stub", "unload");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("wasActive").GetBoolean());
        Assert.Equal(1, RestartableProtocol.Disposed);
        Assert.Null(BowireEndpointHelpers.GetRegistry().GetById("restartable-stub"));

        // And it is remembered, so a restart does not quietly bring it back.
        Assert.True(BowireDisabledPluginsStore.IsDisabled("restartable-stub"));
        BowireDisabledPluginsStore.Enable("restartable-stub");
    }

    [Fact]
    public async Task Restarting_A_Plugin_That_Was_Just_Unloaded_Points_At_Load()
    {
        // The sequence an operator actually performs when a plugin misbehaves.
        RegisterLivePlugin();
        using var host = await BuildHost();
        await Lifecycle(host, "restartable-stub", "unload");

        var (status, _) = await Lifecycle(host, "restartable-stub", "restart");

        Assert.Equal(HttpStatusCode.NotFound, status);
        BowireDisabledPluginsStore.Enable("restartable-stub");
    }
}
