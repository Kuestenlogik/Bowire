// Combined MQTT sample for Bowire — fully self-contained, no docker.
// MQTT has a pure-.NET embeddable broker (MQTTnet.Server), so one project
// runs all three pieces and tells both stories:
//
//   * Embedded — an in-process broker on :1883, a publisher emitting one
//     retained sensor reading per second, and the workbench at /bowire
//     with the broker already seeded into the Sources rail.
//   * Separate — the broker is a real listener, so point an external
//     workbench or `bowire --url mqtt://localhost:1883` at it.
//
// (NATS and Pulsar have no .NET-embeddable broker, so their samples take
// an external docker broker instead — see those samples.)
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mqtt
//   → open http://localhost:5192/bowire

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5192");

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);
// The in-process broker + publisher run as a hosted service so their
// async lifecycle (start → publish loop → awaited stop + dispose) is
// deterministic.
builder.Services.AddHostedService<MqttSampleBroker>();

var app = builder.Build();
app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

// Owns the embedded MQTT broker on :1883 and a publisher that emits one
// retained reading per second on bowire/sample/sensor (retained so the
// workbench gets the latest value the moment it subscribes).
sealed class MqttSampleBroker : BackgroundService
{
    private MqttServer? _broker;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var brokerFactory = new MqttServerFactory();
        _broker = brokerFactory.CreateMqttServer(
            new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(1883)
                .Build());
        await _broker.StartAsync();

        var clientFactory = new MqttClientFactory();
        using var pub = clientFactory.CreateMqttClient();
        await pub.ConnectAsync(new MqttClientOptionsBuilder()
            .WithClientId("bowire-sample-publisher")
            .WithTcpServer("localhost", 1883)
            .Build(), stoppingToken);

        var i = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var payload = JsonSerializer.Serialize(new
            {
                seq = ++i,
                tempC = Math.Round(18.0 + (i % 12) * 0.5, 1),
                at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            await pub.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic("bowire/sample/sensor")
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag()
                .Build(), stoppingToken);
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel ExecuteAsync first (stops the publish loop + disposes the
        // client), then stop the broker — awaited so the stop task
        // completes before Dispose tears the server down.
        await base.StopAsync(cancellationToken);
        if (_broker is not null) await _broker.StopAsync();
    }

    public override void Dispose()
    {
        _broker?.Dispose();
        base.Dispose();
    }
}
