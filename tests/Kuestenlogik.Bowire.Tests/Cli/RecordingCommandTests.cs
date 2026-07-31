// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.App.Cli;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Behavioural coverage for <c>bowire recording validate</c> (#210).
/// Output-capture pattern with concrete substring assertions on stdout
/// / stderr per the WorkspaceCommand* test suite. Exit codes pin the
/// sysexits contract the docs promise.
/// </summary>
public sealed class RecordingCommandTests : IDisposable
{
    private const int ExitOk = 0;
    private const int ExitDataErr = 65;
    private const int ExitNoInput = 66;

    private readonly string _tempRoot;

    public RecordingCommandTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-recording-tests-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    // ----------------------------------------------------------------
    // Command-shape: Build() factory advertises the subcommand
    // ----------------------------------------------------------------

    [Fact]
    public void Build_advertises_validate_subcommand_with_path_arg()
    {
        var recording = RecordingCommand.Build();
        var validate = recording.Subcommands.SingleOrDefault(s => s.Name == "validate");
        Assert.NotNull(validate);
        Assert.Contains(validate!.Arguments, a => a.Name == "path");
        // Pin the help-text intent — operators searching
        // `bowire recording --help` should find the validate command by
        // its purpose words.
        Assert.Contains("schema-check", validate.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("responseRef", validate.Description, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Happy path: store-wrapped, single recording, all steps inlined
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_store_wrapped_single_recording_exits_zero()
    {
        var path = Path.Combine(_tempRoot, "ok-store.bwr");
        await File.WriteAllTextAsync(path, """
            {"recordings":[{
              "id":"rec_one",
              "name":"happy",
              "recordingFormatVersion":2,
              "steps":[
                {"id":"s1","protocol":"rest","service":"S","method":"M",
                 "httpVerb":"GET","httpPath":"/probe","status":"OK",
                 "response":"ok"}
              ]
            }]}
            """, TestContext.Current.CancellationToken);

        var (rc, stdout, stderr) = await InvokeValidate(path);
        Assert.Equal(ExitOk, rc);
        Assert.Empty(stderr);
        Assert.Contains("OK:", stdout, StringComparison.Ordinal);
        Assert.Contains("'happy' (rec_one)", stdout, StringComparison.Ordinal);
        Assert.Contains("1 step(s)", stdout, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Happy path: single-recording top-level shape
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_single_recording_shape_exits_zero()
    {
        var path = Path.Combine(_tempRoot, "ok-single.bwr");
        await File.WriteAllTextAsync(path, """
            {
              "id":"rec_single",
              "name":"by itself",
              "recordingFormatVersion":2,
              "steps":[
                {"id":"s1","protocol":"rest","service":"S","method":"M",
                 "httpVerb":"GET","httpPath":"/x","status":"OK","response":"r"}
              ]
            }
            """, TestContext.Current.CancellationToken);

        var (rc, stdout, _) = await InvokeValidate(path);
        Assert.Equal(ExitOk, rc);
        Assert.Contains("formatVersion 2", stdout, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Argument validation: missing path -> EX_USAGE 64
    //
    // System.CommandLine treats the missing required positional as a
    // parser failure (exit code from the framework, not our handler).
    // The handler still surfaces its own usage message when
    // the parse delivers null, so we test both paths.
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_missing_path_exits_usage()
    {
        var (rc, _, stderr) = await InvokeValidate(args: ["validate"]);
        Assert.NotEqual(ExitOk, rc);
        Assert.NotEmpty(stderr);
    }

    // ----------------------------------------------------------------
    // File-not-found -> EX_NOINPUT 66
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_missing_file_exits_no_input()
    {
        var path = Path.Combine(_tempRoot, "nope-doesnt-exist.bwr");
        var (rc, _, stderr) = await InvokeValidate(path);
        Assert.Equal(ExitNoInput, rc);
        Assert.Contains(path, stderr, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Format-version mismatch -> EX_DATAERR 65
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_unsupported_format_version_exits_data_err()
    {
        var path = Path.Combine(_tempRoot, "from-the-future.bwr");
        await File.WriteAllTextAsync(path, """
            {"id":"x","name":"x","recordingFormatVersion":9999,
             "steps":[{"id":"s","protocol":"rest","service":"S","method":"M","status":"OK","response":""}]}
            """, TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await InvokeValidate(path);
        Assert.Equal(ExitDataErr, rc);
        Assert.Contains("9999", stderr, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Empty steps -> EX_DATAERR 65
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_no_steps_exits_data_err()
    {
        var path = Path.Combine(_tempRoot, "no-steps.bwr");
        await File.WriteAllTextAsync(path, """
            {"id":"x","name":"x","recordingFormatVersion":2,"steps":[]}
            """, TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await InvokeValidate(path);
        Assert.Equal(ExitDataErr, rc);
        Assert.Contains("no steps", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------
    // Self-containment: responseRef forbidden in standalone .bwr
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_step_with_responseRef_exits_data_err()
    {
        var path = Path.Combine(_tempRoot, "has-ref.bwr");
        await File.WriteAllTextAsync(path, """
            {"id":"x","name":"x","recordingFormatVersion":2,
             "steps":[
               {"id":"s_ref","protocol":"rest","service":"S","method":"M",
                "httpVerb":"GET","httpPath":"/p","status":"OK",
                "responseRef":"deadbeef0000000000000000000000000000000000000000000000000000feed"}
             ]}
            """, TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await InvokeValidate(path);
        Assert.Equal(ExitDataErr, rc);
        Assert.Contains("responseRef", stderr, StringComparison.Ordinal);
        Assert.Contains("s_ref", stderr, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Store envelope with multiple recordings and no --name picker
    // -> EX_DATAERR with a hint that listing the names + ids back.
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_multi_recording_envelope_without_picker_exits_data_err()
    {
        var path = Path.Combine(_tempRoot, "ambiguous.bwr");
        await File.WriteAllTextAsync(path, """
            {"recordings":[
              {"id":"rec_a","name":"A","recordingFormatVersion":2,
               "steps":[{"id":"s","protocol":"rest","service":"S","method":"M","status":"OK","response":""}]},
              {"id":"rec_b","name":"B","recordingFormatVersion":2,
               "steps":[{"id":"s","protocol":"rest","service":"S","method":"M","status":"OK","response":""}]}
            ]}
            """, TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await InvokeValidate(path);
        Assert.Equal(ExitDataErr, rc);
        Assert.Contains("rec_a", stderr, StringComparison.Ordinal);
        Assert.Contains("rec_b", stderr, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // --name disambiguation works against a multi-recording envelope.
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_multi_recording_envelope_with_picker_exits_ok()
    {
        var path = Path.Combine(_tempRoot, "two.bwr");
        await File.WriteAllTextAsync(path, """
            {"recordings":[
              {"id":"rec_a","name":"A","recordingFormatVersion":2,
               "steps":[{"id":"sa","protocol":"rest","service":"S","method":"M","status":"OK","response":"a"}]},
              {"id":"rec_b","name":"B","recordingFormatVersion":2,
               "steps":[{"id":"sb","protocol":"rest","service":"S","method":"M","status":"OK","response":"b"}]}
            ]}
            """, TestContext.Current.CancellationToken);

        var (rc, stdout, _) = await InvokeValidate(args: ["validate", path, "--name", "B"]);
        Assert.Equal(ExitOk, rc);
        Assert.Contains("(rec_b)", stdout, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Malformed JSON -> EX_DATAERR
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_malformed_json_exits_data_err()
    {
        var path = Path.Combine(_tempRoot, "broken.bwr");
        await File.WriteAllTextAsync(path, "{ this is not json",
            TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await InvokeValidate(path);
        Assert.Equal(ExitDataErr, rc);
        Assert.NotEmpty(stderr);
    }

    // ----------------------------------------------------------------
    // #210 acceptance — end-to-end: a .bwr written by the workbench's
    // primary producer (ChunkedRecordingStore.LoadAll, which assembles
    // the chunked workspace layout back into the single-document shape
    // the workbench export endpoint serves) validates clean through
    // the standalone CLI. Proves the producer/consumer contract holds.
    // ----------------------------------------------------------------

    [Fact]
    public async Task Validate_accepts_bwr_produced_by_ChunkedRecordingStore_LoadAll()
    {
        // Use storageRoot to isolate the producer state from the
        // global ChunkedRecordingStore.RootPath static — parallel xUnit
        // classes race on that field. storageRoot resolves the
        // recordings root through BowireUserContext.GetWorkspacePath
        // without touching the static, so this test is parallel-safe.
        var storageRoot = Path.Combine(_tempRoot, "ws-roundtrip");
        Directory.CreateDirectory(storageRoot);

        // Producer side: a recording with a body large enough to hit
        // the content-addressed body store. LoadAll must resolve it
        // back inline before the assembled JSON leaves the store
        // (otherwise the .bwr carries a workspace-only responseRef and
        // the standalone consumer rejects it).
        var bigBody = new string('y', 2 * 1024 * 1024);
        var input = """
            {
              "recordings": [{
                "id": "rt_ok",
                "name": "roundtrip",
                "recordingFormatVersion": 2,
                "steps": [{
                  "id": "s1",
                  "protocol": "rest",
                  "service": "S",
                  "method": "M",
                  "httpVerb": "GET",
                  "httpPath": "/x",
                  "status": "OK",
                  "response": "REPLACE_ME"
                }]
              }]
            }
            """.Replace("REPLACE_ME", bigBody, StringComparison.Ordinal);
        ChunkedRecordingStore.SaveAll(input, workspaceId: null, storageRoot: storageRoot);

        // Assembled output is what the workbench export endpoint
        // serves; dropping it as a .bwr next to the recording is the
        // documented standalone shape.
        var bwrPath = Path.Combine(_tempRoot, "from-store.bwr");
        await File.WriteAllTextAsync(bwrPath,
            ChunkedRecordingStore.LoadAll(workspaceId: null, manifestOnly: false, storageRoot: storageRoot),
            TestContext.Current.CancellationToken);

        var (rc, stdout, stderr) = await InvokeValidate(bwrPath);
        Assert.Equal(ExitOk, rc);
        Assert.Empty(stderr);
        Assert.Contains("(rt_ok)", stdout, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // `bowire recording correlate` (#539)
    // ----------------------------------------------------------------

    private const int ExitUsage = 64;

    // A miniature of the harbor flagship: `shipId` is spelled two ways
    // across two protocols (that pair is what makes it a *candidate*),
    // one step reaches it only by suffix (`onShipId`), one carries the
    // value on a bare `id` (the weak tier), and one is unrelated.
    private const string CorrelateFixture = """
        {
          "id": "rec_corr",
          "name": "port call",
          "recordingFormatVersion": 2,
          "steps": [
            { "id": "s1", "protocol": "grpc", "service": "Fleet", "method": "GetShip",
              "capturedAt": 0, "durationMs": 6, "status": "OK",
              "body": "{\"id\":101}", "response": "{\"id\":101,\"name\":\"Nordstern\"}" },
            { "id": "s2", "protocol": "rest", "service": "Containers", "method": "List",
              "capturedAt": 150, "durationMs": 5, "status": "200",
              "response": "[{\"id\":\"MSCU1\",\"onShipId\":101}]" },
            { "id": "s3", "protocol": "websocket", "service": "ais", "method": "subscribe",
              "capturedAt": 300, "durationMs": 30, "status": "OK",
              "response": "{\"ShipId\":101,\"speedKnots\":8}" },
            { "id": "s4", "protocol": "signalr", "service": "OpsHub", "method": "Stream",
              "capturedAt": 450, "durationMs": 30, "status": "OK",
              "response": "{\"shipId\":101,\"latitude\":54.03}" },
            { "id": "s5", "protocol": "mqtt", "service": "crane", "method": "receive",
              "capturedAt": 600, "durationMs": 20, "status": "OK",
              "response": "{\"craneId\":1}" }
          ]
        }
        """;

    [Fact]
    public void Build_advertises_correlate_subcommand_with_its_flags()
    {
        var recording = RecordingCommand.Build();
        var correlate = recording.Subcommands.SingleOrDefault(s => s.Name == "correlate");

        Assert.NotNull(correlate);
        Assert.Contains(correlate!.Arguments, a => a.Name == "path");
        Assert.Contains(correlate.Options, o => o.Name == "--key");
        Assert.Contains(correlate.Options, o => o.Name == "--json");
        Assert.Contains(correlate.Options, o => o.Name == "--name");
    }

    [Fact]
    public async Task Correlate_resolves_the_shared_id_and_reports_the_honest_count()
    {
        var path = Path.Combine(_tempRoot, "corr.bwr");
        await File.WriteAllTextAsync(path, CorrelateFixture, TestContext.Current.CancellationToken);

        var (rc, stdout, stderr) = await InvokeValidate(["correlate", path]);

        Assert.Equal(ExitOk, rc);
        Assert.Empty(stderr);
        // rest / websocket / signalr name the key (onShipId matches by
        // suffix); the gRPC step only carries a bare `id` with the same
        // value, which is the weak tier; mqtt has neither. 4 of 5, and
        // the CLI says 4 of 5 rather than rounding up.
        Assert.Contains("matched:  4/5 step(s) across 4/5 protocol(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("strong", stdout, StringComparison.Ordinal);
        Assert.Contains("weak", stdout, StringComparison.Ordinal);
        Assert.Contains("relative timebase", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correlate_honours_an_explicit_key()
    {
        var path = Path.Combine(_tempRoot, "corr-key.bwr");
        await File.WriteAllTextAsync(path, CorrelateFixture, TestContext.Current.CancellationToken);

        var (rc, stdout, _) = await InvokeValidate(["correlate", path, "--key", "craneId=1"]);

        Assert.Equal(ExitOk, rc);
        Assert.Contains("key:      craneId = 1", stdout, StringComparison.Ordinal);
        Assert.Contains("matched:  1/5 step(s)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correlate_json_emits_the_same_model_the_endpoint_returns()
    {
        var path = Path.Combine(_tempRoot, "corr-json.bwr");
        await File.WriteAllTextAsync(path, CorrelateFixture, TestContext.Current.CancellationToken);

        var (rc, stdout, _) = await InvokeValidate(["correlate", path, "--json"]);

        Assert.Equal(ExitOk, rc);
        // camelCase, same property names the workbench consumes.
        Assert.Contains("\"matchedStepCount\": 4", stdout, StringComparison.Ordinal);
        Assert.Contains("\"timebase\": \"relative\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"lanes\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correlate_rejects_a_malformed_key_as_a_usage_error()
    {
        var path = Path.Combine(_tempRoot, "corr-badkey.bwr");
        await File.WriteAllTextAsync(path, CorrelateFixture, TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await InvokeValidate(["correlate", path, "--key", "shipId"]);

        Assert.Equal(ExitUsage, rc);
        Assert.Contains("name=value", stderr, StringComparison.Ordinal);
    }

    // The same miniature, plus a BFF that calls the transaction by its
    // own id and shares only the container manifest — the #545 case. The
    // container id is long enough to be evidence; the crane's `1` is not.
    private const string CorrelateJoinFixture = """
        {
          "id": "rec_join",
          "name": "port call",
          "recordingFormatVersion": 2,
          "steps": [
            { "id": "s1", "protocol": "rest", "service": "Containers", "method": "List",
              "capturedAt": 0, "durationMs": 5, "status": "200",
              "response": "[{\"id\":\"MSCU1234567\",\"onShipId\":101}]" },
            { "id": "s2", "protocol": "websocket", "service": "ais", "method": "subscribe",
              "capturedAt": 150, "durationMs": 30, "status": "OK",
              "response": "{\"ShipId\":101,\"speedKnots\":8}" },
            { "id": "s3", "protocol": "signalr", "service": "OpsHub", "method": "Stream",
              "capturedAt": 300, "durationMs": 30, "status": "OK",
              "response": "{\"shipId\":101,\"portCallId\":1,\"latitude\":54.03}" },
            { "id": "s4", "protocol": "graphql", "service": "Query", "method": "portCall",
              "capturedAt": 450, "durationMs": 38, "status": "OK",
              "response": "{\"data\":{\"portCall\":{\"id\":1,\"containers\":[{\"id\":\"MSCU1234567\"}]}}}" },
            { "id": "s5", "protocol": "mqtt", "service": "crane", "method": "receive",
              "capturedAt": 600, "durationMs": 20, "status": "OK",
              "response": "{\"craneId\":1}" }
          ]
        }
        """;

    [Fact]
    public async Task Correlate_names_the_bridge_value_in_a_via_column()
    {
        var path = Path.Combine(_tempRoot, "corr-join.bwr");
        await File.WriteAllTextAsync(path, CorrelateJoinFixture, TestContext.Current.CancellationToken);

        var (rc, stdout, stderr) = await InvokeValidate(["correlate", path]);

        Assert.Equal(ExitOk, rc);
        Assert.Empty(stderr);
        Assert.Contains("matched:  4/5 step(s) across 4/5 protocol(s), 1 joined through a bridge value",
            stdout, StringComparison.Ordinal);
        Assert.Contains("VIA", stdout, StringComparison.Ordinal);
        // A derived step names its tier AND its bridge; a directly matched
        // step names its tier and shows `–` for VIA. Both states have to
        // be readable off one row.
        Assert.Contains("derived  id = MSCU1234567 (rest)", stdout, StringComparison.Ordinal);
        Assert.Contains("derived links (depth 2", stdout, StringComparison.Ordinal);
        Assert.Contains("linked by id = MSCU1234567, shared with rest step 1", stdout, StringComparison.Ordinal);
        // The crane shares only `1`, and the table says so instead of
        // leaving the operator to wonder.
        Assert.Contains("craneId = 1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correlate_missing_file_exits_no_input()
    {
        var (rc, _, stderr) = await InvokeValidate(
            ["correlate", Path.Combine(_tempRoot, "nope.bwr")]);

        Assert.Equal(ExitNoInput, rc);
        Assert.Contains("bowire recording correlate:", stderr, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static Task<(int rc, string stdout, string stderr)> InvokeValidate(string path)
        => InvokeValidate(args: ["validate", path]);

    private static async Task<(int rc, string stdout, string stderr)> InvokeValidate(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var recording = RecordingCommand.Build();
        var parse = recording.Parse(args);
        var rc = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);

        return (rc, stdout.ToString(), stderr.ToString());
    }
}
