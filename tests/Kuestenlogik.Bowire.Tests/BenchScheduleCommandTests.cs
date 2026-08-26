// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire bench schedule</c> — add, list, pause, resume, remove.
/// </summary>
/// <remarks>
/// <para>
/// A stored schedule is a promise to run something later without anybody
/// watching, so the refusals matter more than the happy path: a cron the
/// parser cannot read, or a threshold nobody can evaluate, would produce an
/// entry that either never fires or fires and reports nothing. Both are
/// invisible until someone goes looking, which is why they are rejected at
/// `add` time rather than at fire time.
/// </para>
/// <para>
/// Driven through the real CLI entry point, so what is asserted is what an
/// operator types. The schedule store lives under the working directory's
/// <c>.bowire/</c>, hence the temp cwd and the serialised collection.
/// </para>
/// </remarks>
[Collection("CwdSerialised")]
public sealed class BenchScheduleCommandTests : IDisposable
{
    private readonly string _cwd = Directory.GetCurrentDirectory();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-bench-" + Guid.NewGuid().ToString("N"));

    public BenchScheduleCommandTests()
    {
        Directory.CreateDirectory(_root);
        Directory.SetCurrentDirectory(_root);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_cwd);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    private static async Task<(int Exit, string Out, string Err)> Cli(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await BowireCli.RunAsync(
            args, EmptyConfig(), plugins: TestPluginLoaders.None(), stdout: stdout, stderr: stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static Task<(int Exit, string Out, string Err)> Add(
        string id = "nightly", string cron = "0 3 * * *", string target = "orders.v1.OrderService/GetOrder",
        string url = "grpc@localhost:5001", params string[] extra)
        => Cli([.. new[] { "bench", "schedule", "add", id, "--cron", cron, "--target", target, "--url", url }, .. extra]);

    // ---- add ----

    [Fact]
    public async Task A_Stored_Schedule_Reports_Where_It_Landed_And_When_It_Fires()
    {
        var (exit, output, _) = await Add();

        Assert.Equal(0, exit);
        Assert.Contains("Stored nightly", output, StringComparison.Ordinal);
        // The next firing time is the one thing that tells the operator the
        // cron they typed means what they think it means.
        Assert.Contains("Next run:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Next run: —", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orders.v1.OrderService")]      // no method
    [InlineData("/GetOrder")]                   // no service
    [InlineData("orders.v1.OrderService/")]     // trailing slash
    public async Task A_Target_That_Is_Not_Service_Slash_Method_Is_Refused(string target)
    {
        var (exit, _, err) = await Add(target: target);

        Assert.NotEqual(0, exit);
        Assert.Contains("service/method", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Url_With_No_Protocol_Anywhere_Is_Refused()
    {
        // Nothing downstream can guess a plugin from a bare host:port, and a
        // schedule that cannot resolve one would fail every night in silence.
        var (exit, _, err) = await Add(url: "localhost:5001");

        Assert.NotEqual(0, exit);
        Assert.Contains("--protocol", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Explicit_Protocol_Flag_Replaces_The_Prefix_Form()
    {
        var (exit, _, _) = await Add(url: "localhost:5001", extra: ["--protocol", "grpc"]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task A_Cron_Nothing_Can_Parse_Is_Refused_Before_It_Is_Stored()
    {
        // The failure this prevents: an entry that sits in the list forever
        // showing "invalid cron" and never runs.
        var (exit, _, err) = await Add(cron: "every tuesday-ish");

        Assert.NotEqual(0, exit);
        Assert.Contains("bad --cron", err, StringComparison.Ordinal);

        var (_, listing, _) = await Cli("bench", "schedule", "list");
        Assert.DoesNotContain("nightly", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Threshold_That_Cannot_Be_Parsed_Is_Refused_Too()
    {
        // A threshold is the whole point of an unattended run — it is what
        // turns a number into a pass or a fail. Note that the unit is not
        // part of the spec: "p95<250ms" fails the same way, because the
        // budget is parsed as a plain number of milliseconds.
        var (exit, _, err) = await Add(extra: ["--threshold", "p95 should be fastish"]);

        Assert.NotEqual(0, exit);
        Assert.Contains("bad --threshold", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Well_Formed_Threshold_Is_Kept_With_The_Schedule()
    {
        await Add(extra: ["--threshold", "p95<250"]);

        var (_, json, _) = await Cli("bench", "schedule", "list", "--json");

        Assert.Contains("p95<250", json, StringComparison.Ordinal);
    }

    // ---- list ----

    [Fact]
    public async Task Listing_Nothing_Says_Where_Schedules_Would_Live()
    {
        // A first-run message that names the directory and the command that
        // fills it, rather than an empty table.
        var (exit, output, _) = await Cli("bench", "schedule", "list");

        Assert.Equal(0, exit);
        Assert.Contains("No schedules", output, StringComparison.Ordinal);
        Assert.Contains("schedule add", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Stored_Schedule_Shows_Its_Target_And_That_It_Never_Ran()
    {
        await Add();

        var (exit, output, _) = await Cli("bench", "schedule", "list");

        Assert.Equal(0, exit);
        Assert.Contains("nightly", output, StringComparison.Ordinal);
        Assert.Contains("orders.v1.OrderService/GetOrder", output, StringComparison.Ordinal);
        Assert.Contains("never run", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Json_Listing_Is_An_Array_Of_Rows()
    {
        // This is the form a CI job or a dashboard reads; it has to be a
        // document, not the table with the decoration stripped off.
        await Add();

        var (exit, output, _) = await Cli("bench", "schedule", "list", "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateArray());
    }

    // ---- pause / resume ----

    [Fact]
    public async Task A_Paused_Schedule_Says_Paused_Instead_Of_A_Next_Time()
    {
        // Blank would read as "broken cron"; the two are different problems.
        await Add();

        var (exit, output, _) = await Cli("bench", "schedule", "pause", "nightly");
        Assert.Equal(0, exit);
        Assert.Contains("now paused", output, StringComparison.Ordinal);

        var (_, listing, _) = await Cli("bench", "schedule", "list");
        Assert.Contains("next: paused", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resuming_Puts_A_Next_Time_Back()
    {
        await Add();
        await Cli("bench", "schedule", "pause", "nightly");

        var (exit, output, _) = await Cli("bench", "schedule", "resume", "nightly");

        Assert.Equal(0, exit);
        Assert.Contains("now active", output, StringComparison.Ordinal);
        var (_, listing, _) = await Cli("bench", "schedule", "list");
        Assert.DoesNotContain("next: paused", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pausing_Something_That_Is_Not_Stored_Is_A_Usage_Error()
    {
        // Not a silent no-op: someone who mistypes an id has to hear about it,
        // or they will assume the schedule is off when it is still firing.
        var (exit, _, err) = await Cli("bench", "schedule", "pause", "typo");

        Assert.NotEqual(0, exit);
        Assert.Contains("'typo' is not stored", err, StringComparison.Ordinal);
    }

    // ---- remove ----

    [Fact]
    public async Task Removing_A_Schedule_Takes_It_Out_Of_The_Listing()
    {
        await Add();

        var (exit, output, _) = await Cli("bench", "schedule", "remove", "nightly");

        Assert.Equal(0, exit);
        Assert.Contains("Removed nightly", output, StringComparison.Ordinal);
        var (_, listing, _) = await Cli("bench", "schedule", "list");
        Assert.Contains("No schedules", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_Something_That_Is_Not_There_Reports_It()
        => Assert.NotEqual(0, (await Cli("bench", "schedule", "remove", "ghost")).Exit);

    [Fact]
    public async Task Two_Schedules_Are_Listed_Side_By_Side()
    {
        await Add(id: "nightly");
        await Add(id: "hourly", cron: "0 * * * *");

        var (_, output, _) = await Cli("bench", "schedule", "list");

        Assert.Contains("nightly", output, StringComparison.Ordinal);
        Assert.Contains("hourly", output, StringComparison.Ordinal);
    }
}
