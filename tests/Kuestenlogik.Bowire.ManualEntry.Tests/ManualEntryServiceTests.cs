// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Nats;
using Kuestenlogik.Bowire.Protocol.Pulsar;
using Kuestenlogik.Bowire.Protocol.SignalR;
using Kuestenlogik.Bowire.Protocol.Soap;
using Kuestenlogik.Bowire.Protocol.SocketIo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.ManualEntry.Tests;

/// <summary>
/// The ManualEntry service as the workbench reaches it — the JSON transcoding
/// under Bowire's base path — over the real protocol plugins of this
/// repository that describe a form (#995).
/// </summary>
public sealed class ManualEntryServiceTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        // The contribution and the protocol scans read loaded assemblies.
        _ = typeof(ManualEntryRpc).Assembly;
        _ = typeof(BowireSoapProtocol).Assembly;
        _ = typeof(BowireSignalRProtocol).Assembly;
        _ = typeof(BowireNatsProtocol).Assembly;
        _ = typeof(BowirePulsarProtocol).Assembly;
        _ = typeof(BowireSocketIoProtocol).Assembly;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Services.AddBowire();
        _app = builder.Build();
        _app.MapBowire("bowire");
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<JsonElement> Forms()
    {
        using var response = await _http.GetAsync(
            new Uri("/bowire/api/v1/manual-entry-forms?pageSize=200", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("forms").Clone();
    }

    [Theory]
    [InlineData("soap", "call", "Unary", "{operation}")]
    [InlineData("signalr", "channel", "Duplex", "{hubMethod}")]
    [InlineData("nats", "request", "Unary", "{subject}/request")]
    [InlineData("pulsar", "subscribe", "ServerStreaming", "subscribe")]
    [InlineData("socketio", "listen-event", "ServerStreaming", "{event}")]
    public async Task Each_Plugin_Form_Is_Served_With_Its_Call_Kinds(
        string protocol, string kindId, string methodType, string method)
    {
        var form = (await Forms()).EnumerateArray().Single(f => f.GetProperty("protocol").GetString() == protocol);

        var kind = form.GetProperty("callKinds").EnumerateArray().Single(k => k.GetProperty("id").GetString() == kindId);
        Assert.Equal(methodType, kind.GetProperty("methodType").GetString());
        Assert.Equal(method, kind.GetProperty("method").GetString());
    }

    [Fact]
    public async Task A_Metadata_Field_Is_Transcoded_With_Its_Placement()
    {
        var soap = (await Forms()).EnumerateArray().Single(f => f.GetProperty("protocol").GetString() == "soap");

        var action = soap.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "soap_action");
        Assert.Equal("PLACEMENT_METADATA", action.GetProperty("placement").GetString());
    }

    [Theory]
    [MemberData(nameof(PluginForms))]
    public void Every_Plugin_Form_Of_This_Repository_Passes_The_Check(string protocol, BowireManualEntryForm form)
    {
        Assert.True(ManualEntryRpc.Problems(form).Count == 0,
            protocol + ": " + string.Join("; ", ManualEntryRpc.Problems(form)));
    }

    public static TheoryData<string, BowireManualEntryForm> PluginForms()
    {
        using var soap = new BowireSoapProtocol();
        using var pulsar = new BowirePulsarProtocol();
        return new TheoryData<string, BowireManualEntryForm>
        {
            { soap.Id, soap.DescribeManualEntry() },
            { "signalr", new BowireSignalRProtocol().DescribeManualEntry() },
            { "nats", new BowireNatsProtocol().DescribeManualEntry() },
            { pulsar.Id, pulsar.DescribeManualEntry() },
            { "socketio", new BowireSocketIoProtocol().DescribeManualEntry() },
        };
    }
}
