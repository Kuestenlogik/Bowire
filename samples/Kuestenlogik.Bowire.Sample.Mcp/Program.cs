// Combined MCP sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     mcp-catalogue.json seeds the Sources rail with this host's /mcp
//     endpoint, discovered over the streamable-HTTP transport.
//   * Separate — it is a real MCP server, so point an external workbench
//     or `bowire --url mcp@http://localhost:5190/mcp` at it.
//
// The server covers all three surfaces the Bowire MCP plugin lists and
// invokes, so the sidebar shows one service per category:
//
//   * Tools     — echo, add, and record_readings, whose input schema is
//                 deliberately non-trivial (a required array of nested
//                 objects plus an optional settings object) so Bowire's
//                 schema → form mapping has something to chew on.
//   * Resources — a direct one (bowire://sample/sensors) and a templated
//                 one (bowire://sample/sensors/{sensorId}/readings).
//   * Prompts   — summarise_sensor, with one required and one optional
//                 argument.
//
// Everything is stateless: the tool computes its answer from the batch it
// is handed and the resources derive their readings from the sensor id,
// so the sample stays a single project with nothing to reset. As of MCP
// revision 2026-07-28 the *transport* is stateless too — the revision
// dropped Mcp-Session-Id and the initialize handshake, so there is no
// session to resume and `Stateless = true` below is pinned deliberately
// rather than inherited from the SDK default that now happens to match.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Mcp
//   → open http://localhost:5190/bowire

using System.ComponentModel;
using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5190");

builder.Services
    .AddMcpServer()
    // Pinned, not inherited: SDK 2.0.0 flipped HttpServerTransportOptions
    // .Stateless from false to true. Stating it here keeps the sample's
    // topology readable in source instead of tracking an SDK default.
    // Do not set false — a stateful server answers a 2026-07-28 request
    // with UnsupportedProtocolVersion to push the client back onto the
    // legacy initialize handshake, costing every modern client a wasted
    // round trip.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<SampleTools>()
    .WithResources<SampleResources>()
    .WithPrompts<SamplePrompts>();

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.MapMcp("/mcp");

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

[McpServerToolType]
internal sealed class SampleTools
{
    [McpServerTool, Description("Echo the input text back to the caller.")]
    public static string Echo(string text) => "echo: " + text;

    [McpServerTool, Description("Add two integers.")]
    public static int Add(int a, int b) => a + b;

    // The interesting one: parameters without a default land in the
    // schema's `required[]`, the reading type nests an object schema, and
    // the list of them becomes an array — which is what turns Bowire's
    // form UI from "two text boxes" into a repeated message field.
    //
    // The two optional parameters take a non-null default rather than a
    // nullable type on purpose: for `BatchSource? source = null` the SDK
    // emits `"type": ["object", "null"]`, and a JSON-Schema type *union*
    // is not something every schema reader copes with — Bowire's own tool
    // mapper reads `type` as a plain string and fails the whole discovery
    // pass on one. Nested properties (`at` below) are free to be nullable;
    // only the top level is walked.
    [McpServerTool(Name = "record_readings")]
    [Description("Record a batch of sensor readings and return what was accepted.")]
    public static string RecordReadings(
        [Description("The readings to record. At least one is required.")]
        IReadOnlyList<SensorReading> readings,
        [Description("Where the batch came from.")]
        BatchSource source,
        [Description("Drop readings outside the calibrated -50..150 range instead of recording them.")]
        bool dropOutOfRange = true,
        [Description("Free-form tag stamped on every reading in the batch.")]
        string tag = "")
    {
        if (readings.Count == 0)
            return "no readings supplied — nothing recorded.";

        var accepted = dropOutOfRange
            ? readings.Where(r => r.Value is >= -50 and <= 150).ToList()
            : readings.ToList();

        return JsonSerializer.Serialize(new
        {
            accepted = accepted.Count,
            rejected = readings.Count - accepted.Count,
            sensors = accepted.Select(r => r.SensorId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            station = source.Station,
            shift = source.Shift,
            tag,
        });
    }
}

/// <summary>One measurement — nested inside the tool's input schema.</summary>
internal sealed record SensorReading(
    [property: Description("Sensor that produced the reading, e.g. 'dock-1'.")]
    string SensorId,
    [property: Description("Measured value in the sensor's own unit (degrees Celsius here).")]
    double Value,
    [property: Description("Unix epoch milliseconds. Omit for 'now'.")]
    long? At = null);

/// <summary>Provenance of a batch — the nested object in the input schema.</summary>
internal sealed record BatchSource(
    [property: Description("Station that submitted the batch, e.g. 'quay-north'.")]
    string Station,
    [property: Description("Shift the batch was taken on: 'day' or 'night'.")]
    string Shift = "day");

[McpServerResourceType]
internal sealed class SampleResources
{
    // Direct resource — no variable in the template, so it shows up in
    // `resources/list` and therefore in Bowire's Resources service.
    [McpServerResource(
        UriTemplate = "bowire://sample/sensors",
        Name = "sensors",
        MimeType = "application/json")]
    [Description("The sensor ids this sample knows about.")]
    public static string Sensors() =>
        JsonSerializer.Serialize(new { sensors = SampleData.SensorIds, unit = "degC" });

    // Templated resource — the {sensorId} variable binds to the parameter
    // of the same name. Templates are advertised via
    // `resources/templates/list` rather than `resources/list`, so this one
    // is addressed by expanding the template (e.g.
    // bowire://sample/sensors/dock-1/readings) rather than picked from the
    // Resources list.
    [McpServerResource(
        UriTemplate = "bowire://sample/sensors/{sensorId}/readings",
        Name = "sensor-readings",
        MimeType = "application/json")]
    [Description("The last five readings for one sensor.")]
    public static string Readings(string sensorId) => SampleData.ReadingsJson(sensorId);
}

[McpServerPromptType]
internal sealed class SamplePrompts
{
    // Prompt arguments map straight onto Bowire's form: `sensorId` has no
    // default so it is advertised as required, `tone` has one so it is
    // optional. Both descriptions travel with them.
    [McpServerPrompt(Name = "summarise_sensor")]
    [Description("Draft a short status summary for one sensor from its recent readings.")]
    public static string SummariseSensor(
        [Description("Sensor to summarise, e.g. 'dock-1'.")] string sensorId,
        [Description("Voice for the summary: 'brief' or 'detailed'. Defaults to 'brief'.")] string tone = "brief")
        => $"""
            Summarise the state of sensor '{sensorId}' for an operator, in a {tone} tone.
            Call out any value that looks out of the ordinary and say what you would check next.

            Readings:
            {SampleData.ReadingsJson(sensorId)}
            """;
}

/// <summary>
/// Deterministic stand-in data. Derived from the sensor id rather than
/// stored, so the tool, the resources and the prompt agree with each other
/// without the sample owning any state.
/// </summary>
internal static class SampleData
{
    public static readonly string[] SensorIds = ["dock-1", "dock-2", "gantry-7"];

    public static string ReadingsJson(string sensorId)
    {
        var seed = sensorId.Sum(c => (int)c);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var readings = Enumerable.Range(0, 5).Select(i => new
        {
            value = Math.Round(10.0 + ((seed + (i * 7)) % 40) * 0.5, 1),
            at = now - ((4 - i) * 60_000L),
        });
        return JsonSerializer.Serialize(new { sensorId, unit = "degC", readings });
    }
}
