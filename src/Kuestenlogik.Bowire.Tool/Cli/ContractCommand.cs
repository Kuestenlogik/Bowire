// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Text.Json;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Mock.Loading;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>bowire contract</c> — Pact-style consumer contract testing (#191).
/// Two subcommands:
/// <list type="bullet">
///   <item><c>publish</c> — turn a recording into a consumer contract
///     (JSON file) and optionally push it to a Pact Broker.</item>
///   <item><c>verify</c> — replay a contract's interactions against a
///     live provider and fail the build on any mismatch. Same report
///     surface + exit codes as <c>bowire test</c>.</item>
/// </list>
/// <para>
/// The broker path (publish push / verify pull) is gated behind an
/// explicit <c>--broker-url</c>: outbound calls stay opt-in.
/// </para>
/// </summary>
internal static class ContractCommand
{
    private const int ExitOk = 0;
    private const int ExitFail = 1;
    private const int ExitUsage = 64;
    private const int ExitDataErr = 65;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Build()
    {
        var contract = new Command("contract",
            "Pact-style consumer contract testing. `publish` builds a contract from a recording (+ optional broker push); `verify` replays it against a live provider.");
        contract.Add(BuildPublishCommand());
        contract.Add(BuildVerifyCommand());
        return contract;
    }

    // -------------------- publish --------------------

    private static Command BuildPublishCommand()
    {
        var cmd = new Command("publish",
            "Build a consumer contract from a recording and write it to a file (and, with --broker-url, push it to a Pact Broker).");

        var recordingArg = new Argument<string>("recording")
        { Description = "Path to a recording / .bwr file captured on the consumer side." };
        var provider = new Option<string?>("--provider")
        { Description = "Provider name the contract is against (required)." };
        var consumer = new Option<string?>("--consumer")
        { Description = "Consumer name. Defaults to the recording's name." };
        var outPath = new Option<string?>("--out")
        { Description = "Output contract file path. Defaults to <consumer>-<provider>.pact.json." };
        var brokerUrl = new Option<string?>("--broker-url")
        { Description = "Pact Broker base URL to PUT the contract to (outbound — opt-in). Omit to only write the file." };
        var version = new Option<string?>("--consumer-version")
        { Description = "Consumer version for the broker publish. Defaults to a timestamp." };
        var tag = new Option<string?>("--tag")
        { Description = "Tag the published consumer version on the broker (e.g. a branch name)." };

        cmd.Add(recordingArg); cmd.Add(provider); cmd.Add(consumer); cmd.Add(outPath);
        cmd.Add(brokerUrl); cmd.Add(version); cmd.Add(tag);

        cmd.SetAction((pr, ct) => RunPublishAsync(
            pr.GetValue(recordingArg)!, pr.GetValue(provider), pr.GetValue(consumer),
            pr.GetValue(outPath), pr.GetValue(brokerUrl), pr.GetValue(version), pr.GetValue(tag),
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct));
        return cmd;
    }

    private static async Task<int> RunPublishAsync(
        string recordingPath, string? provider, string? consumer, string? outPath,
        string? brokerUrl, string? version, string? tag,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            await stderr.WriteLineAsync("bowire contract publish: --provider is required.").ConfigureAwait(false);
            return ExitUsage;
        }
        if (!File.Exists(recordingPath))
        {
            await stderr.WriteLineAsync($"bowire contract publish: recording not found: {recordingPath}").ConfigureAwait(false);
            return ExitDataErr;
        }

        Mocking.BowireRecording recording;
        try
        {
            recording = RecordingLoader.Load(recordingPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or NotSupportedException)
        {
            await stderr.WriteLineAsync($"bowire contract publish: could not load recording: {ex.Message}").ConfigureAwait(false);
            return ExitDataErr;
        }

        var consumerName = !string.IsNullOrWhiteSpace(consumer)
            ? consumer!
            : (!string.IsNullOrWhiteSpace(recording.Name) ? recording.Name : "consumer");
        var contract = PactContract.FromRecording(recording, consumerName, provider!);

        if (contract.Interactions.Count == 0)
        {
            await stderr.WriteLineAsync("bowire contract publish: no HTTP / REST steps in the recording — Pact contracts are HTTP-only, nothing to publish.").ConfigureAwait(false);
            return ExitDataErr;
        }

        var target = !string.IsNullOrWhiteSpace(outPath)
            ? outPath!
            : $"{Sanitize(consumerName)}-{Sanitize(provider!)}.pact.json";
        try
        {
            await File.WriteAllTextAsync(target, JsonSerializer.Serialize(contract, WriteOpts), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            await stderr.WriteLineAsync($"bowire contract publish: could not write {target}: {ex.Message}").ConfigureAwait(false);
            return ExitDataErr;
        }
        await stdout.WriteLineAsync($"  Contract written to {target} ({contract.Interactions.Count} interaction{(contract.Interactions.Count == 1 ? "" : "s")}).").ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(brokerUrl))
        {
            var ver = !string.IsNullOrWhiteSpace(version)
                ? version!
                : DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            using var http = new HttpClient();
            try
            {
                await ContractBroker.PublishAsync(http, brokerUrl!, contract, ver, tag, ct).ConfigureAwait(false);
                await stdout.WriteLineAsync($"  Published to broker {brokerUrl} (version {ver}{(string.IsNullOrEmpty(tag) ? "" : ", tag " + tag)}).").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ContractBrokerException or HttpRequestException or TaskCanceledException)
            {
                await stderr.WriteLineAsync($"bowire contract publish: broker push failed: {ex.Message}").ConfigureAwait(false);
                return ExitFail;
            }
        }
        return ExitOk;
    }

    // -------------------- verify --------------------

    private static Command BuildVerifyCommand()
    {
        var cmd = new Command("verify",
            "Replay a consumer contract against a live provider and fail on any mismatch. Exit 0 = all held, 1 = a mismatch, 2 = a request errored.");

        var contractArg = new Argument<string?>("contract")
        { Description = "Path to a contract file. Omit and use --broker-url + --provider to pull the latest from a broker." };
        var providerUrl = new Option<string?>("--provider-url")
        { Description = "Base URL of the live provider to replay against (required)." };
        var brokerUrl = new Option<string?>("--broker-url")
        { Description = "Pact Broker base URL to pull the latest contract from (outbound — opt-in). Requires --provider." };
        var provider = new Option<string?>("--provider")
        { Description = "Provider name to pull from the broker (with --broker-url)." };
        var tag = new Option<string?>("--tag")
        { Description = "Pull the latest contract carrying this tag (with --broker-url)." };
        var junit = new Option<string?>("--junit")
        { Description = "Write a JUnit XML report to this path." };
        var sarif = new Option<string?>("--sarif")
        { Description = "Write a SARIF 2.1.0 report to this path." };

        cmd.Add(contractArg); cmd.Add(providerUrl); cmd.Add(brokerUrl); cmd.Add(provider);
        cmd.Add(tag); cmd.Add(junit); cmd.Add(sarif);

        cmd.SetAction((pr, ct) => RunVerifyAsync(
            pr.GetValue(contractArg), pr.GetValue(providerUrl), pr.GetValue(brokerUrl),
            pr.GetValue(provider), pr.GetValue(tag), pr.GetValue(junit), pr.GetValue(sarif),
            pr.InvocationConfiguration.Output, pr.InvocationConfiguration.Error, ct));
        return cmd;
    }

    private static async Task<int> RunVerifyAsync(
        string? contractPath, string? providerUrl, string? brokerUrl, string? provider, string? tag,
        string? junitPath, string? sarifPath,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerUrl))
        {
            await stderr.WriteLineAsync("bowire contract verify: --provider-url is required.").ConfigureAwait(false);
            return ExitUsage;
        }

        using var http = new HttpClient();
        PactContract contract;
        if (!string.IsNullOrWhiteSpace(contractPath))
        {
            if (!File.Exists(contractPath))
            {
                await stderr.WriteLineAsync($"bowire contract verify: contract not found: {contractPath}").ConfigureAwait(false);
                return ExitDataErr;
            }
            try
            {
                var json = await File.ReadAllTextAsync(contractPath!, ct).ConfigureAwait(false);
                contract = JsonSerializer.Deserialize<PactContract>(json, WriteOpts)
                    ?? throw new JsonException("empty contract");
            }
            catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
            {
                await stderr.WriteLineAsync($"bowire contract verify: could not read contract: {ex.Message}").ConfigureAwait(false);
                return ExitDataErr;
            }
        }
        else if (!string.IsNullOrWhiteSpace(brokerUrl) && !string.IsNullOrWhiteSpace(provider))
        {
            try
            {
                contract = await ContractBroker.FetchLatestAsync(http, brokerUrl!, provider!, tag, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ContractBrokerException or HttpRequestException or TaskCanceledException)
            {
                await stderr.WriteLineAsync($"bowire contract verify: broker pull failed: {ex.Message}").ConfigureAwait(false);
                return ExitFail;
            }
        }
        else
        {
            await stderr.WriteLineAsync("bowire contract verify: pass a contract file, or --broker-url + --provider to pull one.").ConfigureAwait(false);
            return ExitUsage;
        }

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"  Contract verify   {contract.Consumer.Name} → {contract.Provider.Name}   ({contract.Interactions.Count} interactions)").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);

        var report = await ContractVerifier.VerifyAsync(http, contract, providerUrl!, stdout, ct).ConfigureAwait(false);

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"  {report.Tests.Count - report.FailedTests}/{report.Tests.Count} interactions held   "
            + $"{report.PassedAssertions}/{report.TotalAssertions} checks   in {report.DurationMs} ms").ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(junitPath))
        {
            await SafeWriteAsync(junitPath!, JUnitReport.Render(report), "JUnit", stdout, stderr).ConfigureAwait(false);
        }
        if (!string.IsNullOrWhiteSpace(sarifPath))
        {
            await SafeWriteAsync(sarifPath!, TestSarifReport.Render(report), "SARIF", stdout, stderr).ConfigureAwait(false);
        }

        return report.FailedTests > 0 ? ExitFail : ExitOk;
    }

    private static async Task SafeWriteAsync(string path, string content, string kind, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
            await stdout.WriteLineAsync($"  {kind} report written to {path}").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            await stderr.WriteLineAsync($"bowire contract verify: could not write {kind} report: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
