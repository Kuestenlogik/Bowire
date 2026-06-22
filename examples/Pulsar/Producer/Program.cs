// Sample Pulsar producer for the Bowire Pulsar plugin demo.
// Publishes one message per second to persistent://public/default/bowire-demo
// until cancelled. Run alongside `docker compose up` on the same
// folder's compose file.

using DotPulsar;
using DotPulsar.Extensions;

var brokerUrl = args.Length > 0 ? args[0] : "pulsar://localhost:6650";
var topic = args.Length > 1 ? args[1] : "persistent://public/default/bowire-demo";

Console.WriteLine($"[producer] connecting to {brokerUrl}, topic {topic}");

await using var client = PulsarClient.Builder()
    .ServiceUrl(new Uri(brokerUrl))
    .Build();

await using var producer = client.NewProducer(Schema.String)
    .Topic(topic)
    .Create();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var i = 0;
while (!cts.IsCancellationRequested)
{
    var message = $"hello from bowire sample #{++i} @ {DateTime.UtcNow:O}";
    var id = await producer.Send(message, cts.Token);
    Console.WriteLine($"[producer] sent {id}: {message}");
    try { await Task.Delay(TimeSpan.FromSeconds(1), cts.Token); }
    catch (OperationCanceledException) { break; }
}

Console.WriteLine("[producer] stopped.");
