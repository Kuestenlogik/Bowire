// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Contracts;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire contract publish</c> and <c>bowire contract matrix</c> — the two
/// halves that touch nothing but files.
/// </summary>
/// <remarks>
/// <para>
/// Publishing turns a recording into a Pact contract on disk; the broker push
/// only happens when <c>--broker-url</c> is passed, which is the opt-in rule
/// for every outbound call in Bowire. Nothing here passes it, so nothing here
/// leaves the machine.
/// </para>
/// <para>
/// The matrix is what a reviewer reads in a CI log to decide whether a
/// contract broke, and what a job gates on with <c>--fail-on-failures</c>. A
/// gate that stays green over a failing cell is the failure worth preventing.
/// </para>
/// </remarks>
[Collection("CwdSerialised")]
public sealed class ContractPublishAndMatrixTests : IDisposable
{
    private readonly string _cwd = Directory.GetCurrentDirectory();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-contract-cli-" + Guid.NewGuid().ToString("N"));

    public ContractPublishAndMatrixTests()
    {
        Directory.CreateDirectory(_root);
        // Both subcommands resolve `.bowire/contract-results` and their output
        // files relative to the working directory.
        Directory.SetCurrentDirectory(_root);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_cwd);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Exit, string Out, string Err)> Cli(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await BowireCli.RunAsync(
            args, new ConfigurationBuilder().Build(),
            plugins: TestPluginLoaders.None(), stdout: stdout, stderr: stderr,
            cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A recording with one REST step — the only kind Pact can carry.</summary>
    /// <remarks>
    /// <c>recordingFormatVersion</c> is not decoration: the loader refuses a
    /// recording whose version it was not built for, and a fixture without one
    /// fails as "unsupported version" rather than as whatever the test meant
    /// to check.
    /// </remarks>
    private async Task<string> RestRecording(string name = "checkout")
    {
        var path = Path.Combine(_root, $"{name}.json");
        await File.WriteAllTextAsync(path, $$"""
            {"recordingFormatVersion":2,"id":"rec-1","name":"{{name}}","steps":[
              {"protocolId":"rest","service":"orders","method":"GetOrder",
               "httpMethod":"GET","httpPath":"/orders/42",
               "responseStatus":200,"responseBody":"{\"id\":42}"}
            ]}
            """, Ct);
        return path;
    }

    private async Task<string> NonHttpRecording()
    {
        var path = Path.Combine(_root, "grpc.json");
        await File.WriteAllTextAsync(path, """
            {"recordingFormatVersion":2,"id":"rec-2","name":"grpc-only","steps":[
              {"protocolId":"grpc","service":"orders.v1.OrderService","method":"GetOrder"}
            ]}
            """, Ct);
        return path;
    }

    private static ContractVerificationReport Report(string consumer, string provider, bool passed)
        => new()
        {
            Consumer = consumer,
            Provider = provider,
            StartedAt = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc),
            TotalAssertions = 1,
            PassedAssertions = passed ? 1 : 0,
            FailedInteractions = passed ? 0 : 1,
            Interactions =
            {
                new ContractInteractionResult
                {
                    Description = "GET /orders/42",
                    Method = "GET",
                    Error = passed ? null : "expected 200, got 500",
                },
            },
        };

    // ---- publish ----

    [Fact]
    public async Task Publishing_Without_A_Provider_Is_A_Usage_Error()
    {
        // A Pact contract is a statement about one provider; there is no
        // sensible default to guess.
        var recording = await RestRecording();

        var (exit, _, err) = await Cli("contract", "publish", recording);

        Assert.Equal(64, exit);
        Assert.Contains("--provider is required", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_A_Recording_That_Is_Not_There_Names_The_Path()
    {
        var (exit, _, err) = await Cli(
            "contract", "publish", Path.Combine(_root, "no-such.json"), "--provider", "orders");

        Assert.NotEqual(0, exit);
        Assert.Contains("recording not found", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_Writes_A_Contract_Named_For_Both_Parties()
    {
        // The default file name is what a CI job globs for, so it is part of
        // the interface even though nobody typed it.
        var recording = await RestRecording();

        var (exit, output, _) = await Cli(
            "contract", "publish", recording, "--provider", "orders", "--consumer", "web");

        Assert.Equal(0, exit);
        Assert.Contains("1 interaction", output, StringComparison.Ordinal);
        var written = Path.Combine(_root, "web-orders.pact.json");
        Assert.True(File.Exists(written), $"expected {written}");
    }

    [Fact]
    public async Task The_Written_Contract_Carries_Both_Party_Names_And_The_Interaction()
    {
        var recording = await RestRecording();

        await Cli("contract", "publish", recording, "--provider", "orders", "--consumer", "web");

        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_root, "web-orders.pact.json"), Ct));
        Assert.Equal("web", doc.RootElement.GetProperty("consumer").GetProperty("name").GetString());
        Assert.Equal("orders", doc.RootElement.GetProperty("provider").GetProperty("name").GetString());
        Assert.Single(doc.RootElement.GetProperty("interactions").EnumerateArray());
    }

    [Fact]
    public async Task Without_A_Consumer_The_Recording_Name_Is_Used()
    {
        // Recordings are named by the person who captured them; that name is
        // a better default than the literal word "consumer".
        var recording = await RestRecording("mobile-app");

        await Cli("contract", "publish", recording, "--provider", "orders");

        Assert.True(File.Exists(Path.Combine(_root, "mobile-app-orders.pact.json")));
    }

    [Fact]
    public async Task An_Explicit_Output_Path_Is_Honoured()
    {
        var recording = await RestRecording();
        var target = Path.Combine(_root, "artifacts", "contract.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var (exit, _, _) = await Cli(
            "contract", "publish", recording, "--provider", "orders", "--out", target);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task A_Recording_With_No_Http_Steps_Says_Why_Nothing_Was_Written()
    {
        // Pact is an HTTP contract format and brokers reject anything else.
        // Writing an empty contract would publish a promise about nothing.
        var recording = await NonHttpRecording();

        var (exit, _, err) = await Cli("contract", "publish", recording, "--provider", "orders");

        Assert.NotEqual(0, exit);
        Assert.Contains("HTTP-only", err, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_root, "*.pact.json"));
    }

    // ---- matrix ----

    [Fact]
    public async Task An_Empty_Matrix_Points_At_The_Command_That_Fills_It()
    {
        // First run in a repo. Naming the directory and the next command is
        // what turns "empty" into a next step.
        var (exit, _, err) = await Cli("contract", "matrix");

        Assert.Equal(0, exit);
        Assert.Contains("contract verify", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Stored_Verdict_Shows_Up_In_The_Grid()
    {
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);

        var (exit, output, _) = await Cli("contract", "matrix");

        Assert.Equal(0, exit);
        Assert.Contains("web", output, StringComparison.Ordinal);
        Assert.Contains("orders", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Json_Matrix_Spells_The_Status_Out()
    {
        // Serialising the object graph directly would emit the status as an
        // enum ordinal while the HTTP endpoint and the MCP tool emit words —
        // three surfaces, one vocabulary.
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);

        var (exit, output, _) = await Cli("contract", "matrix", "--json");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var cell = doc.RootElement.GetProperty("cells").EnumerateArray().First();
        Assert.Equal("pass", cell.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_Failing_Cell_Fails_A_Job_That_Asked_For_A_Gate()
    {
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: false), _root, Ct);

        var (exit, _, _) = await Cli("contract", "matrix", "--fail-on-failures");

        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task A_Failing_Cell_Does_Not_Fail_A_Job_That_Did_Not_Ask()
    {
        // Reporting-only is the default; a team adds the gate deliberately.
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: false), _root, Ct);

        var (exit, _, _) = await Cli("contract", "matrix");

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task A_Passing_Matrix_Passes_The_Gate()
    {
        await ContractResultStore.SaveAsync(Report("web", "orders", passed: true), _root, Ct);

        var (exit, _, _) = await Cli("contract", "matrix", "--fail-on-failures");

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task A_Recording_Whose_Format_The_Loader_Refuses_Is_Reported_Not_Thrown()
    {
        // The bug this pins: RecordingLoader signals every rejection with
        // InvalidDataException, which lives in System.IO but derives from
        // SystemException — so the catch filter missed it and a hand-edited
        // recording met the operator as an unhandled-exception stack trace.
        var path = Path.Combine(_root, "from-the-future.json");
        await File.WriteAllTextAsync(path, """
            {"recordingFormatVersion":99,"id":"rec-9","name":"future","steps":[]}
            """, Ct);

        var (exit, _, err) = await Cli("contract", "publish", path, "--provider", "orders");

        Assert.Equal(65, exit);
        Assert.Contains("could not load recording", err, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", err, StringComparison.Ordinal);
    }
}
