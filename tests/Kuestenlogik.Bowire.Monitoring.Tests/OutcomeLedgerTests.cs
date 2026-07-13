// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Monitoring.Tests;

/// <summary>
/// Coverage for <see cref="OutcomeLedger"/> — the append-only jsonl store and
/// the last-row read that drives restart resume + transition detection.
/// </summary>
public sealed class OutcomeLedgerTests : IDisposable
{
    private readonly string _dir;

    public OutcomeLedgerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bowire-lh-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private static ProbeOutcome Outcome(long t, ProbeResult r) => new() { TimestampUnixMs = t, Result = r };

    [Fact]
    public void LastOutcome_is_null_when_never_run()
    {
        var ledger = new OutcomeLedger(_dir);
        Assert.Null(ledger.LastOutcome("nope"));
    }

    [Fact]
    public void Append_then_LastOutcome_returns_the_last_row()
    {
        var ledger = new OutcomeLedger(_dir);
        ledger.Append("p", Outcome(100, ProbeResult.Pass));
        ledger.Append("p", Outcome(200, ProbeResult.Fail));
        ledger.Append("p", Outcome(300, ProbeResult.Pass));

        var last = ledger.LastOutcome("p");
        Assert.NotNull(last);
        Assert.Equal(300, last!.TimestampUnixMs);
        Assert.Equal(ProbeResult.Pass, last.Result);
    }

    [Fact]
    public void Result_enum_serialises_as_a_readable_string()
    {
        var ledger = new OutcomeLedger(_dir);
        ledger.Append("p", new ProbeOutcome { TimestampUnixMs = 1, Result = ProbeResult.Error, Error = "boom" });
        var line = File.ReadAllText(ledger.PathFor("p"));
        Assert.Contains("\"error\"", line, StringComparison.Ordinal);
        Assert.Contains("boom", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Assertion_verdicts_serialise_camelCase()
    {
        var ledger = new OutcomeLedger(_dir);
        ledger.Append("p", new ProbeOutcome
        {
            TimestampUnixMs = 1,
            Result = ProbeResult.Fail,
            Assertions = [new ProbeAssertionVerdict(false, "status 500 == 200")],
        });
        var line = File.ReadAllText(ledger.PathFor("p"));
        // Uniform camelCase — nested verdict keys follow the top-level shape.
        Assert.Contains("\"passed\"", line, StringComparison.Ordinal);
        Assert.Contains("\"description\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Passed\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void LastOutcome_skips_a_corrupt_trailing_line()
    {
        var ledger = new OutcomeLedger(_dir);
        ledger.Append("p", Outcome(100, ProbeResult.Pass));
        // Simulate a half-written trailing row (crash mid-append).
        File.AppendAllText(ledger.PathFor("p"), "{ this is not valid json\n");

        var last = ledger.LastOutcome("p");
        Assert.NotNull(last);
        Assert.Equal(100, last!.TimestampUnixMs);
    }

    [Fact]
    public void PathFor_sanitises_the_probe_name()
    {
        var ledger = new OutcomeLedger(_dir);
        var path = ledger.PathFor("payments api/v2");
        Assert.EndsWith("payments_api_v2.jsonl", path, StringComparison.Ordinal);
    }
}
