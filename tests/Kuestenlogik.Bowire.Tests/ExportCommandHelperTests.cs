// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The decisions <c>bowire export</c> makes around the document builders.
/// </summary>
/// <remarks>
/// The export itself discovers a live URL, so what is covered here is
/// everything on either side of that call: which plugin a URL scheme picks,
/// how a recording file is read for the example bodies, and how the format
/// flag is parsed. Each of those fails quietly — the wrong protocol produces
/// an empty document, an unread recording produces one without examples, and
/// neither says anything went wrong.
/// </remarks>
public sealed class ExportCommandHelperTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string File_(string contents, string extension = ".json")
    {
        var path = Path.Combine(Path.GetTempPath(), $"bowire-export-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, contents);
        _files.Add(path);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- which plugin an AsyncAPI URL implies ----

    [Theory]
    [InlineData("mqtt://broker.example.com:1883", "mqtt")]
    [InlineData("mqtts://broker.example.com:8883", "mqtt")]
    [InlineData("nats://nats.example.com:4222", "nats")]
    [InlineData("kafka://kafka.example.com:9092", "kafka")]
    [InlineData("ws://api.example.com/socket", "websocket")]
    [InlineData("wss://api.example.com/socket", "websocket")]
    [InlineData("amqp://rabbit.example.com", "amqp")]
    [InlineData("pulsar://pulsar.example.com:6650", "pulsar")]
    [InlineData("https://api.example.com", "rest")]
    public void A_Url_Scheme_Picks_The_Plugin_That_Speaks_It(string url, string expected)
        => Assert.Equal(expected, ExportCommand.PickAsyncApiProtocolId(url));

    [Fact]
    public void Both_Amqp_Url_Spellings_Reach_The_Same_Plugin()
        // amqp1:// is the 1.0 spelling; one plugin serves both, and picking
        // nothing for it would refuse a broker Bowire can actually talk to.
        => Assert.Equal("amqp", ExportCommand.PickAsyncApiProtocolId("amqp1://broker.example.com"));

    [Fact]
    public void A_Scheme_Nothing_Speaks_Picks_No_Plugin()
        // Better than guessing: the caller reports "which --protocol?" instead
        // of exporting an empty document from the wrong client.
        => Assert.Null(ExportCommand.PickAsyncApiProtocolId("gopher://example.com"));

    [Fact]
    public void Something_That_Is_Not_A_Url_Picks_No_Plugin()
        => Assert.Null(ExportCommand.PickAsyncApiProtocolId("just some text"));

    // ---- reading the recording that supplies example bodies ----

    [Fact]
    public void No_Recording_Path_Means_No_Recording()
        => Assert.Null(ExportCommand.LoadRecording(null));

    [Fact]
    public void A_Recording_Path_That_Does_Not_Exist_Is_Not_An_Error()
        // The recording is informational — it adds examples to the exported
        // document. A missing one must not fail the export.
        => Assert.Null(ExportCommand.LoadRecording(
            Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json")));

    [Fact]
    public void A_Bare_Recording_File_Is_Read_As_Itself()
    {
        var path = File_("""{"id":"rec-1","name":"checkout","steps":[]}""");

        var recording = ExportCommand.LoadRecording(path);

        Assert.NotNull(recording);
        Assert.Equal("checkout", recording.Name);
    }

    [Fact]
    public void A_Recording_Store_Hands_Back_Its_First_Recording()
    {
        // The on-disk shape the mock server reads is a store, not a single
        // recording. Accepting both means an operator can pass whichever file
        // they have to hand.
        var path = File_("""
            {"recordings":[
              {"id":"rec-1","name":"first","steps":[]},
              {"id":"rec-2","name":"second","steps":[]}
            ]}
            """);

        Assert.Equal("first", ExportCommand.LoadRecording(path)!.Name);
    }

    [Fact]
    public void An_Empty_Store_Falls_Through_Rather_Than_Throwing()
    {
        // `{"recordings":[]}` deserialises as a recording with no steps —
        // the point is that it does not throw and does not take the export
        // down.
        var path = File_("""{"recordings":[]}""");

        var recording = ExportCommand.LoadRecording(path);

        Assert.True(recording is null || recording.Steps.Count == 0);
    }

    [Fact]
    public void A_Recording_File_That_Will_Not_Parse_Is_Ignored()
    {
        // Same reasoning as the missing file: a hand-edited recording costs
        // the examples, not the export.
        var path = File_("{ this is not json");

        Assert.Null(ExportCommand.LoadRecording(path));
    }

    // ---- the format flag ----

    [Fact]
    public void The_Options_Carry_The_Title_And_Version_Through()
    {
        // Both end up in the document's info block, which is what a consumer
        // of the exported spec sees first.
        var options = ExportCommand.BuildOpenApiOptions("json", "Orders API", "2.1.0");

        Assert.Equal("Orders API", options.Title);
        Assert.Equal("2.1.0", options.Version);
    }

    [Fact]
    public void An_Async_Api_Export_Carries_Them_Too()
    {
        var options = ExportCommand.BuildAsyncApiOptions("yaml", "Events", "1.0.0");

        Assert.Equal("Events", options.Title);
        Assert.Equal("1.0.0", options.Version);
    }

    [Fact]
    public void An_Unset_Title_And_Version_Stay_Unset_Rather_Than_Becoming_Empty()
    {
        // The builders fill in their own defaults; an empty string would
        // override them with nothing.
        var options = ExportCommand.BuildOpenApiOptions(null, null, null);

        Assert.Null(options.Title);
        Assert.Null(options.Version);
    }

    // ---- the arguments ----

    [Fact]
    public async Task Exporting_Without_A_Url_Prints_The_Usage()
    {
        using var err = new StringWriter();

        var exit = await ExportCommand.RunOpenApiAsync(
            "", output: null, format: null, recordingPath: null, title: null, versionOverride: null,
            Ct, TextWriter.Null, err);

        Assert.Equal(2, exit);
        Assert.Contains("export openapi", err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exporting_An_Async_Api_Without_A_Url_Prints_The_Usage_Too()
    {
        using var err = new StringWriter();

        var exit = await ExportCommand.RunAsyncApiAsync(
            "", output: null, format: null, recordingPath: null, title: null, versionOverride: null,
            Ct, TextWriter.Null, err);

        Assert.Equal(2, exit);
        Assert.NotEmpty(err.ToString());
    }
}
