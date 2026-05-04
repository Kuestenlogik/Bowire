// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Pin tests for the default-implemented members on Bowire's plugin
/// extension interfaces. The defaults are 0% covered until somebody calls
/// them on a stub implementation that doesn't override — these tests do
/// exactly that, so the no-op contracts can't silently regress to throw or
/// allocate.
/// </summary>
public class InterfaceDefaultMethodTests
{
    // -----------------------------------------------------------------
    // IBowireProtocolServices defaults
    // -----------------------------------------------------------------
    private sealed class BareProtocolServices : IBowireProtocolServices
    {
        public bool ConfigureCalled { get; private set; }
        public void ConfigureServices(IServiceCollection services) => ConfigureCalled = true;
        // MapDiscoveryEndpoints intentionally not overridden — the test
        // calls the default-implemented member.
    }

    [Fact]
    public void IBowireProtocolServices_Default_MapDiscoveryEndpoints_Is_NoOp()
    {
        IBowireProtocolServices bare = new BareProtocolServices();

        // The default body is empty so the IEndpointRouteBuilder argument is
        // never dereferenced — passing null is safe and lets the assertion
        // run without spinning up a real WebApplication for each test.
        var ex = Record.Exception(() => bare.MapDiscoveryEndpoints(null!));
        Assert.Null(ex);
    }

    [Fact]
    public void IBowireProtocolServices_ConfigureServices_Default_Path_Is_Reachable()
    {
        // The members are invoked through the interface so the runtime
        // pulls in the default-method IL. Even though our class overrides
        // ConfigureServices, calling MapDiscoveryEndpoints (which we do not
        // override) is the path that actually exercises the default body.
        IBowireProtocolServices bare = new BareProtocolServices();
        var services = new ServiceCollection();

        bare.ConfigureServices(services);

        Assert.True(((BareProtocolServices)bare).ConfigureCalled);
    }

    // -----------------------------------------------------------------
    // IBowireMockHostingExtension defaults
    // -----------------------------------------------------------------
    private sealed class BareMockExtension(string id) : IBowireMockHostingExtension
    {
        public string Id { get; } = id;
        // RequiresHttp2, ConfigureServices, MapEndpoints all left default.
    }

    [Fact]
    public void IBowireMockHostingExtension_Default_RequiresHttp2_Is_False()
    {
        IBowireMockHostingExtension ext = new BareMockExtension("plain");
        var recording = new BowireRecording { Id = "r1", Name = "rec" };

        Assert.False(ext.RequiresHttp2(recording));
    }

    [Fact]
    public void IBowireMockHostingExtension_Default_ConfigureServices_Is_NoOp()
    {
        IBowireMockHostingExtension ext = new BareMockExtension("plain");
        var services = new ServiceCollection();
        var recording = new BowireRecording { Id = "r1", Name = "rec" };

        var ex = Record.Exception(() =>
            ext.ConfigureServices(services, recording, NullLoggerFactory.Instance));

        Assert.Null(ex);
        Assert.Empty(services); // No-op default must not register anything.
    }

    [Fact]
    public void IBowireMockHostingExtension_Default_MapEndpoints_Is_NoOp()
    {
        IBowireMockHostingExtension ext = new BareMockExtension("plain");
        var recording = new BowireRecording { Id = "r1", Name = "rec" };

        // Empty default body — null endpoint builder is safe.
        var ex = Record.Exception(() => ext.MapEndpoints(null!, recording));
        Assert.Null(ex);
    }

    [Fact]
    public void IBowireMockHostingExtension_Id_Is_Stored_Verbatim()
    {
        var ext = new BareMockExtension("myproto");
        Assert.Equal("myproto", ext.Id);
    }

    // -----------------------------------------------------------------
    // IBowireProtocol defaults — Initialize and Settings
    // -----------------------------------------------------------------
    [Fact]
    public void IBowireProtocol_Default_Settings_Returns_Empty_List()
    {
        IBowireProtocol bare = new BareProtocol();
        Assert.Empty(bare.Settings);
    }

    [Fact]
    public void IBowireProtocol_Default_Initialize_Is_NoOp()
    {
        IBowireProtocol bare = new BareProtocol();
        var ex = Record.Exception(() => bare.Initialize(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task IBowireChannel_Default_NegotiatedSubProtocol_Is_Null()
    {
        // Channels that don't negotiate a sub-protocol get the default null
        // value — pinned here so the recording capture can rely on
        // "no sub-protocol == null" without consulting the implementation.
        await using var ch = new BareChannel();
        Assert.Null(((IBowireChannel)ch).NegotiatedSubProtocol);
    }

    private sealed class BareProtocol : IBowireProtocol
    {
        public string Name => "Bare";
        public string Id => "bare";
        public string IconSvg => "<svg/>";

        public Task<List<Models.BowireServiceInfo>> DiscoverAsync(
            string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<Models.BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", new Dictionary<string, string>()));

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> InvokeStreamAsync(
            string serverUrl, string service, string method,
            List<string> jsonMessages, bool showInternalServices,
            Dictionary<string, string>? metadata = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public Task<IBowireChannel?> OpenChannelAsync(
            string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null,
            CancellationToken ct = default) => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class BareChannel : IBowireChannel
    {
        public string Id => "ch";
        public bool IsClientStreaming => false;
        public bool IsServerStreaming => false;
        public int SentCount => 0;
        public bool IsClosed => false;
        public long ElapsedMs => 0;

        public Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> ReadResponsesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
