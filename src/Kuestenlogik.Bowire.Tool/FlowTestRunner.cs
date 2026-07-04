// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Flows;
using Kuestenlogik.Bowire.Flows.Expectations;
using Kuestenlogik.Bowire.Keyring;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// v2.2 CI runner (T2): executes a Flow JSON document end-to-end against a
/// real backend, evaluates the per-step <see cref="FlowExpectation"/> list
/// via <see cref="FlowExpectationEvaluator"/>, and surfaces a
/// <see cref="FlowRunReport"/> the JUnit XML + HTML emitters consume.
/// <para>
/// Protocol dispatch — option <b>B</b> from the design spike: the runner
/// hosts <see cref="BowireProtocolRegistry"/> in-process and calls
/// <see cref="IBowireProtocol.InvokeAsync"/> directly. Same path as the
/// existing recording-driven <see cref="TestRunner"/>; no HTTP detour, no
/// requirement that a Bowire UI is running on a sidecar port.
/// </para>
/// </summary>
internal static class FlowTestRunner
{
    /// <summary>
    /// JSON options for FlowDefinition deserialisation. Matches the
    /// kebab-case-lower enum convention the workbench writes
    /// (<c>"kind":"body-path"</c>, <c>"operator":"not-equals"</c>) so a
    /// flow file the in-browser editor saved round-trips verbatim through
    /// the CLI. Mirrors <c>FlowDefinitionTests.KebabEnumOptions</c>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private static bool UseColor(TextWriter writer)
        => ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected;

    /// <summary>
    /// Decide at parse time whether <paramref name="json"/> is a Flow JSON
    /// (v2.2 <c>{ "nodes": [...] }</c> shape) rather than a legacy
    /// recording / test-collection. Lets <see cref="TestRunner"/>
    /// auto-dispatch to the right runner without forcing the operator to
    /// pick a flag.
    /// </summary>
    public static bool LooksLikeFlow(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // The Flow document is the only shape that carries a top-level
            // "nodes" array — recordings use "tests" / "messages", test
            // collections use "tests". Quick + unambiguous discriminator.
            return root.TryGetProperty("nodes", out var nodes)
                && nodes.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Run a flow file. Returns the same exit-code contract as the recording
    /// runner so CI scripts can branch uniformly: 0 = all expectations
    /// passed, 1 = at least one expectation failed, 2 = a step errored
    /// before evaluation (backend down, malformed flow, …).
    /// </summary>
    public static async Task<int> RunAsync(
        FlowTestCliOptions cli,
        TextWriter? output = null,
        TextWriter? error = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;

        if (string.IsNullOrEmpty(cli.FlowPath))
        {
            await stderr.WriteLineAsync("error: Usage: bowire test <flow.json> [--report path.html] [--junit path.xml] [--base-url URL] [--env KEY=VALUE ...]").ConfigureAwait(false);
            return 2;
        }
        if (!File.Exists(cli.FlowPath))
        {
            await stderr.WriteLineAsync($"error: Flow file not found: {cli.FlowPath}").ConfigureAwait(false);
            return 2;
        }

        FlowDefinition? flow;
        string rawJson;
        try
        {
            rawJson = await File.ReadAllTextAsync(cli.FlowPath, ct).ConfigureAwait(false);
            flow = JsonSerializer.Deserialize<FlowDefinition>(rawJson, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            await stderr.WriteLineAsync($"error: Failed to parse flow: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        if (flow is null || flow.Nodes is null || flow.Nodes.Count == 0)
        {
            await stderr.WriteLineAsync("error: Flow has no nodes.").ConfigureAwait(false);
            return 2;
        }

        // In-process protocol registry. Same path as the recording runner —
        // every protocol plugin Bowire.Tool already loaded (REST, gRPC,
        // GraphQL, MQTT, SignalR, WS, SSE, MCP, OData, Socket.IO,
        // JSON-RPC) is callable. v0 emphasis is REST; gRPC / MCP / WS
        // ride along through the same IBowireProtocol contract without
        // any special-casing here.
        var registry = BowireProtocolRegistry.Discover();

        var report = new FlowRunReport
        {
            FlowId = flow.Id,
            FlowName = string.IsNullOrEmpty(flow.Name) ? Path.GetFileNameWithoutExtension(cli.FlowPath) : flow.Name,
            FlowPath = cli.FlowPath,
            StartedAt = DateTime.UtcNow,
        };

        await stdout.WriteLineAsync().ConfigureAwait(false);
        await stdout.WriteLineAsync($"  Bowire Flow Test Runner   flow: {report.FlowName}").ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);

        // #181 — --env-file entries seed the resolver first (files in CLI
        // order, later files win), then the explicit --env repeats override.
        var envPairs = new List<string>();
        foreach (var envFile in cli.EnvFiles)
        {
            try
            {
                envPairs.AddRange(ReadEnvFileLines(envFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                await stderr.WriteLineAsync($"error: Failed to read --env-file '{envFile}': {ex.Message}").ConfigureAwait(false);
                return 2;
            }
        }
        envPairs.AddRange(cli.EnvOverrides);
        var env = MergeEnv(envPairs);

        // #208 Phase 5 — opt-in OS-keyring resolution for CI. With
        // --keyring, every {{keyring.service/account}} / ${keyring.…} ref
        // in a step body or serverUrl is read from the runner's OS
        // credential store and seeded into the resolver env, so secrets
        // never live in the flow file or an --env-file. Misses / a
        // disabled store leave the placeholder intact and the step's
        // assertion fails loudly rather than sending an empty credential.
        if (cli.Keyring)
        {
            SeedKeyringVars(flow, env,
                new KeyringResolver(new KeyringOptions { Enabled = true }, new OsKeyringBackend()));
        }

        // #208 Phase 5 — deterministic {{ai.*}} resolution for CI. The
        // workbench resolves ai.* by calling a model (nondeterministic,
        // needs a provider); a CI run instead pins each ref to a stable
        // value derived from --ai-seed + the ref name, so the same flow
        // produces byte-identical requests on every run without any model.
        if (!string.IsNullOrEmpty(cli.AiSeed))
        {
            SeedAiVars(flow, env, cli.AiSeed);
        }

        var sw = Stopwatch.StartNew();
        var anyError = false;
        var anyExpectationFailed = false;

        var snapshotDir = SnapshotDirFor(cli.FlowPath);
        var flowDir = Path.GetDirectoryName(Path.GetFullPath(cli.FlowPath)) ?? ".";

        // Runs one step execution (one row of a data-driven step, or the
        // whole step when it carries no data source), evaluates its
        // snapshot, and folds the result into the report + console.
        async Task ExecuteAsync(FlowStep step, Dictionary<string, string> stepEnv, string? rowLabel)
        {
            var stepResult = await RunStepAsync(step, stepEnv, registry, cli.BaseUrl, rowLabel, ct).ConfigureAwait(false);
            if (step.Snapshot is { Enabled: true } && !stepResult.Skipped && string.IsNullOrEmpty(stepResult.Error))
            {
                await EvaluateSnapshotAsync(step, stepResult, snapshotDir, cli.UpdateSnapshots, ct).ConfigureAwait(false);
            }
            report.Steps.Add(stepResult);

            if (!string.IsNullOrEmpty(stepResult.Error)) anyError = true;
            if (stepResult.Expectations.Any(e => !e.Passed)) anyExpectationFailed = true;

            await PrintStepAsync(stdout, stepResult).ConfigureAwait(false);
        }

        foreach (var step in flow.Nodes)
        {
            // Data-driven expansion (#174): a step with a data source runs
            // once per row, the row's columns overriding the --env scope,
            // and reports as `stepId[label]` so JUnit / SARIF group the
            // parameterisation under one step family.
            if (step.Data is not null
                && string.Equals(step.Type, "request", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<FlowDataRow> rows;
                try
                {
                    rows = FlowDataSourceExpander.Expand(step.Data, flowDir);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    var invalid = new FlowStepRunResult
                    {
                        StepId = string.IsNullOrEmpty(step.Id) ? "(unnamed)" : step.Id,
                        StepType = step.Type ?? "request",
                        Service = step.Service ?? string.Empty,
                        Method = step.Method ?? string.Empty,
                        Error = $"data source invalid: {ex.Message}",
                    };
                    report.Steps.Add(invalid);
                    anyError = true;
                    await PrintStepAsync(stdout, invalid).ConfigureAwait(false);
                    continue;
                }

                foreach (var row in rows)
                {
                    var rowEnv = new Dictionary<string, string>(env, StringComparer.Ordinal);
                    foreach (var col in row.Values)
                    {
                        rowEnv[col.Key] = col.Value;
                    }
                    await ExecuteAsync(step, rowEnv, row.Label).ConfigureAwait(false);
                }
                continue;
            }

            await ExecuteAsync(step, env, rowLabel: null).ConfigureAwait(false);
        }

        sw.Stop();
        report.DurationMs = sw.ElapsedMilliseconds;
        report.TotalExpectations = report.Steps.Sum(s => s.Expectations.Count);
        report.PassedExpectations = report.Steps.Sum(s => s.Expectations.Count(e => e.Passed));
        report.FailedExpectations = report.TotalExpectations - report.PassedExpectations;
        report.StepErrors = report.Steps.Count(s => !string.IsNullOrEmpty(s.Error));

        await stdout.WriteLineAsync().ConfigureAwait(false);
        var summary = $"  {report.PassedExpectations}/{report.TotalExpectations} expectations passed   "
            + $"{report.Steps.Count - report.StepErrors}/{report.Steps.Count} steps invoked   "
            + $"in {sw.ElapsedMilliseconds} ms";
        await stdout.WriteLineAsync(summary).ConfigureAwait(false);
        await stdout.WriteLineAsync().ConfigureAwait(false);

        // Compute exit code per the v2.2 CLI contract:
        //   2 — a step errored before evaluation (backend unreachable,
        //       service/method blank, plugin missing). Highest precedence
        //       because a failed invocation invalidates downstream
        //       expectation accounting.
        //   1 — at least one expectation didn't hold.
        //   0 — every step invoked AND every expectation passed.
        var exitCode = anyError ? 2 : (anyExpectationFailed ? 1 : 0);
        report.ExitCode = exitCode;

        if (!string.IsNullOrEmpty(cli.ReportPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.ReportPath, FlowHtmlReport.Render(report), ct).ConfigureAwait(false);
                await stdout.WriteLineAsync($"  HTML report written to {cli.ReportPath}").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                await stderr.WriteLineAsync($"error: Failed to write HTML report: {ex.Message}").ConfigureAwait(false);
            }
        }
        if (!string.IsNullOrEmpty(cli.JUnitPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.JUnitPath, FlowJUnitReport.Render(report), ct).ConfigureAwait(false);
                await stdout.WriteLineAsync($"  JUnit XML written to {cli.JUnitPath}").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                await stderr.WriteLineAsync($"error: Failed to write JUnit report: {ex.Message}").ConfigureAwait(false);
            }
        }
        if (!string.IsNullOrEmpty(cli.SarifPath))
        {
            try
            {
                await File.WriteAllTextAsync(cli.SarifPath, TestSarifReport.Render(report), ct).ConfigureAwait(false);
                await stdout.WriteLineAsync($"  SARIF written to {cli.SarifPath}").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
            {
                await stderr.WriteLineAsync($"error: Failed to write SARIF report: {ex.Message}").ConfigureAwait(false);
            }
        }
        if (cli.Annotations)
        {
            await GitHubAnnotations.WriteAsync(stdout, report).ConfigureAwait(false);
        }

        // #181 — --fail-on gates the exit code. 'never' runs + reports but
        // always exits 0 (non-blocking pre-merge signal). A step ERROR
        // (exit 2) still escapes so a broken backend / malformed flow is
        // never masked by 'never' — only assertion failures are softened.
        if (string.Equals(cli.FailOn, "never", StringComparison.OrdinalIgnoreCase) && exitCode == 1)
        {
            return 0;
        }

        return exitCode;
    }

    private static async Task<FlowStepRunResult> RunStepAsync(
        FlowStep step, Dictionary<string, string> env, BowireProtocolRegistry registry,
        string? baseUrl, string? rowLabel, CancellationToken ct)
    {
        var baseId = string.IsNullOrEmpty(step.Id) ? "(unnamed)" : step.Id;
        var result = new FlowStepRunResult
        {
            StepId = rowLabel is null ? baseId : $"{baseId}[{rowLabel}]",
            StepType = step.Type ?? "request",
            Service = step.Service ?? string.Empty,
            Method = step.Method ?? string.Empty,
        };

        // Non-request steps (variable / delay / condition / loop) are
        // structural — T2 doesn't replay them in v0. They surface as
        // "skipped" zero-duration entries so a flow with control nodes
        // doesn't fail outright.
        if (!string.Equals(step.Type, "request", StringComparison.OrdinalIgnoreCase))
        {
            result.Skipped = true;
            return result;
        }
        // Method is required for any protocol — gRPC needs a method name,
        // REST needs the verb. Service may be empty: the REST plugin's
        // ad-hoc codepath (#256) keys off `IsNullOrEmpty(service)` to
        // route schema-free GET / POST / PUT / DELETE / PATCH calls; a
        // gRPC step keeps a non-empty service. Reject only the case
        // where the flow can't possibly invoke anything.
        if (string.IsNullOrEmpty(step.Method))
        {
            result.Error = "step missing method";
            return result;
        }

        // Variable resolution: step.body + step.serverUrl both run
        // through the same minimal resolver. {{var}} and ${var} both
        // resolve against the merged env map; unknown placeholders are
        // left intact so the operator sees the typo.
        var resolvedBody = FlowVariableResolver.Resolve(step.Body ?? "{}", env);
        var serverUrl = !string.IsNullOrEmpty(step.ServerUrl)
            ? FlowVariableResolver.Resolve(step.ServerUrl, env)
            : (baseUrl ?? string.Empty);

        var protocolId = step.Protocol;
        IBowireProtocol? protocol = string.IsNullOrEmpty(protocolId)
            ? (registry.Protocols.Count > 0 ? registry.Protocols[0] : null)
            : registry.GetById(protocolId);
        if (protocol is null)
        {
            result.Error = $"protocol '{protocolId ?? "<any>"}' not registered";
            return result;
        }

        try
        {
            await protocol.DiscoverAsync(serverUrl, showInternalServices: false, ct).ConfigureAwait(false);
        }
        // 3rd-party transport surface — soft-fail per step.
        catch (Exception ex) when (PluginBoundary.NonFatal(ex))
        {
            result.Error = $"discovery failed: {ex.Message}";
            return result;
        }

        InvokeResult? invocation;
        var sw = Stopwatch.StartNew();
        try
        {
            invocation = await protocol.InvokeAsync(
                serverUrl,
                step.Service!,
                step.Method!,
                new List<string> { resolvedBody },
                showInternalServices: false,
                metadata: null,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PluginBoundary.NonFatal(ex))
        {
            sw.Stop();
            result.LatencyMs = sw.ElapsedMilliseconds;
            result.Error = $"invocation failed: {ex.Message}";
            return result;
        }
        sw.Stop();
        result.LatencyMs = invocation.DurationMs > 0 ? invocation.DurationMs : sw.ElapsedMilliseconds;
        result.Status = invocation.Status;
        result.ResponseBody = invocation.Response;

        // Capture response headers verbatim, case-insensitive lookup so
        // kind=header expectations match irrespective of upstream
        // capitalisation.
        var headers = new Dictionary<string, string>(invocation.Metadata, StringComparer.OrdinalIgnoreCase);
        result.ResponseHeaders = headers;

        var envelope = new FlowRequestEnvelope
        {
            Status = invocation.Status,
            Body = invocation.Response,
            Headers = headers,
            LatencyMs = result.LatencyMs,
        };

        // Evaluate the merged v2.2 + v2.1-legacy expectation list. Empty
        // expectation list → step still "passes" (vacuously true) so a
        // flow can exist purely as a smoke-test sequence.
        var effective = step.EffectiveExpectations();
        var stepRollup = FlowExpectationEvaluator.EvaluateStep(result.StepId, effective, envelope);
        foreach (var e in stepRollup.Evaluations)
        {
            result.Expectations.Add(e);
        }

        return result;
    }

    /// <summary>
    /// Snapshot files live in <c>__snapshots__/&lt;flow-file-stem&gt;/</c>
    /// beside the flow file — checked into the repo alongside the flow, so
    /// baseline drift shows up in the diff of the PR that caused it (the
    /// Jest convention, which CI reviewers already know how to read).
    /// </summary>
    internal static string SnapshotDirFor(string flowPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(flowPath)) ?? ".";
        return Path.Combine(dir, "__snapshots__", Path.GetFileNameWithoutExtension(flowPath));
    }

    /// <summary>
    /// #171 — capture-once / diff-on-change. Missing baseline (or
    /// <c>--update-snapshots</c>) writes the actual response and reports a
    /// passing "captured" result; an existing baseline is diffed via
    /// <see cref="FlowSnapshotComparer"/> and every drift line lands as a
    /// failing expectation-shaped result, so JUnit / SARIF / annotations
    /// pick snapshot failures up without special-casing.
    /// </summary>
    private static async Task EvaluateSnapshotAsync(
        FlowStep step, FlowStepRunResult result, string snapshotDir, bool update, CancellationToken ct)
    {
        var cfg = step.Snapshot!;
        var file = Path.Combine(snapshotDir, SafeFileName(result.StepId) + ".snap.json");
        var actual = result.ResponseBody ?? string.Empty;

        try
        {
            if (update || !File.Exists(file))
            {
                Directory.CreateDirectory(snapshotDir);
                await File.WriteAllTextAsync(file, actual, ct).ConfigureAwait(false);
                result.Expectations.Add(new FlowExpectationResult
                {
                    Passed = true,
                    Kind = FlowExpectationKind.Snapshot,
                    Message = update
                        ? $"snapshot updated → {file}"
                        : $"snapshot captured → {file}",
                });
                return;
            }

            var baseline = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var diffs = FlowSnapshotComparer.Compare(baseline, actual, cfg.Mode, cfg.Ignore);
            if (diffs.Count == 0)
            {
                result.Expectations.Add(new FlowExpectationResult
                {
                    Passed = true,
                    Kind = FlowExpectationKind.Snapshot,
                    Message = cfg.Mode == FlowSnapshotMode.Structural
                        ? "snapshot matches (structural)"
                        : "snapshot matches (exact)",
                });
                return;
            }
            foreach (var diff in diffs)
            {
                result.Expectations.Add(new FlowExpectationResult
                {
                    Passed = false,
                    Kind = FlowExpectationKind.Snapshot,
                    Message = $"snapshot drift: {diff} (re-baseline via --update-snapshots)",
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            result.Expectations.Add(new FlowExpectationResult
            {
                Passed = false,
                Kind = FlowExpectationKind.Snapshot,
                Message = $"snapshot I/O failed: {ex.Message}",
            });
        }
    }

    /// <summary>Step ids are workbench-generated ("node_…") but a hand-written flow may carry anything — sanitise for the filesystem.</summary>
    private static string SafeFileName(string stepId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = stepId.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// #181 — read one <c>--env-file</c>: KEY=VALUE per line, dotenv-style.
    /// Blank lines and <c>#</c> comments are skipped; the surviving lines
    /// run through the same KEY=VALUE parsing as <c>--env</c> repeats, so
    /// malformed lines are ignored rather than fatal.
    /// </summary>
    internal static List<string> ReadEnvFileLines(string path)
    {
        var lines = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Build the resolver's variable map from the CLI <c>--env KEY=VALUE</c>
    /// repeats. Later occurrences win, matching the dotnet-style
    /// environment-merge convention.
    /// </summary>
    internal static Dictionary<string, string> MergeEnv(IEnumerable<string>? envOverrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (envOverrides is null) return merged;
        foreach (var raw in envOverrides)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var idx = raw.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0) continue;
            var key = raw[..idx].Trim();
            var value = raw[(idx + 1)..];
            if (key.Length == 0) continue;
            merged[key] = value;
        }
        return merged;
    }

    // #208 Phase 5 — keyring ref scanners. Match the two placeholder forms
    // the resolver understands; the captured group is the ref that follows
    // the `keyring.` prefix (e.g. 'github.com/deploy-bot').
    private static readonly Regex KeyringCurly = new(
        @"\{\{\s*keyring\.([^}\s]+)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex KeyringDollar = new(
        @"\$\{\s*keyring\.([^}\s]+)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Resolve every <c>{{keyring.X}}</c> / <c>${keyring.X}</c> ref the flow
    /// references against the OS credential store (once per distinct ref)
    /// and seed <paramref name="env"/> under the full <c>keyring.X</c> key
    /// so <see cref="FlowVariableResolver"/>'s bare-env fallback substitutes
    /// it. An explicit <c>--env</c> entry for the same key wins; an
    /// unresolved ref is left out so the placeholder survives to the
    /// assertion.
    /// </summary>
    private static void SeedKeyringVars(
        FlowDefinition flow, Dictionary<string, string> env, KeyringResolver resolver)
    {
        if (!resolver.Enabled || flow.Nodes is null) return;
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in flow.Nodes)
        {
            CollectKeyringRefs(step.Body, refs);
            CollectKeyringRefs(step.ServerUrl, refs);
        }
        foreach (var reference in refs)
        {
            var key = "keyring." + reference;
            if (env.ContainsKey(key)) continue; // explicit --env override wins
            var result = resolver.Resolve(reference);
            if (result.Status == KeyringReadStatus.Found && result.Value is not null)
            {
                env[key] = result.Value;
            }
        }
    }

    private static void CollectKeyringRefs(string? text, HashSet<string> refs)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match m in KeyringCurly.Matches(text)) refs.Add(m.Groups[1].Value);
        foreach (Match m in KeyringDollar.Matches(text)) refs.Add(m.Groups[1].Value);
    }

    // #208 Phase 5 — ai.* ref scanners, mirroring the keyring pair. The
    // captured group is the ref that follows the `ai.` prefix.
    private static readonly Regex AiCurly = new(
        @"\{\{\s*ai\.([^}\s]+)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static readonly Regex AiDollar = new(
        @"\$\{\s*ai\.([^}\s]+)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Seed every <c>{{ai.X}}</c> / <c>${ai.X}</c> ref the flow references
    /// with a deterministic value derived from <paramref name="seed"/> +
    /// the ref name, writing it into <paramref name="env"/> under the full
    /// <c>ai.X</c> key so <see cref="FlowVariableResolver"/>'s bare-env
    /// fallback substitutes it. An explicit <c>--env</c> entry wins.
    /// </summary>
    private static void SeedAiVars(FlowDefinition flow, Dictionary<string, string> env, string seed)
    {
        if (flow.Nodes is null) return;
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in flow.Nodes)
        {
            CollectAiRefs(step.Body, refs);
            CollectAiRefs(step.ServerUrl, refs);
        }
        foreach (var reference in refs)
        {
            var key = "ai." + reference;
            if (env.ContainsKey(key)) continue; // explicit --env override wins
            env[key] = DeterministicAiValue(seed, reference);
        }
    }

    private static void CollectAiRefs(string? text, HashSet<string> refs)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match m in AiCurly.Matches(text)) refs.Add(m.Groups[1].Value);
        foreach (Match m in AiDollar.Matches(text)) refs.Add(m.Groups[1].Value);
    }

    /// <summary>
    /// Deterministic, seed-derived stand-in for an AI-suggested value:
    /// <c>{name}-{hash}</c> where hash is FNV-1a-32 over
    /// <c>seed \x1f name</c>. Stable across runs and machines for a given
    /// (seed, name), so CI assertions can pin against it.
    /// </summary>
    internal static string DeterministicAiValue(string seed, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(seed + "" + name);
        uint hash = 2166136261; // FNV offset basis
        unchecked
        {
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= 16777619; // FNV prime — wrap on overflow
            }
        }
        return string.Create(CultureInfo.InvariantCulture, $"{name}-{hash:x8}");
    }

    private static async Task PrintStepAsync(TextWriter stdout, FlowStepRunResult step)
    {
        var useColor = UseColor(stdout);
        if (step.Skipped)
        {
            await stdout.WriteLineAsync($"  SKIP  {step.StepId}   (type: {step.StepType})").ConfigureAwait(false);
            return;
        }
        var passed = string.IsNullOrEmpty(step.Error) && step.Expectations.All(e => e.Passed);
        var marker = passed ? Green("PASS", useColor) : Red("FAIL", useColor);
        var endpoint = string.IsNullOrEmpty(step.Service) ? "" : $"{step.Service} / {step.Method}";
        await stdout.WriteLineAsync($"  {marker}  {step.StepId}   {endpoint}   {step.Status ?? ""} · {step.LatencyMs}ms").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(step.Error))
        {
            await stdout.WriteLineAsync($"        {Red("error: " + step.Error, useColor)}").ConfigureAwait(false);
            return;
        }
        foreach (var e in step.Expectations)
        {
            var icon = e.Passed ? Green("✓", useColor) : Red("✗", useColor);
            await stdout.WriteLineAsync($"        {icon} {e.Message}").ConfigureAwait(false);
        }
    }

    private static string Green(string s, bool useColor) => useColor ? $"\x1b[32m{s}\x1b[0m" : s;
    private static string Red(string s, bool useColor)   => useColor ? $"\x1b[31m{s}\x1b[0m" : s;
}

/// <summary>
/// Typed options carried from <see cref="Cli.BowireCli"/> into
/// <see cref="FlowTestRunner.RunAsync"/>. Lives separately from
/// <see cref="TestCliOptions"/> because the flow runner needs the
/// flow-specific <c>--base-url</c> and <c>--env</c> repeats the recording
/// runner doesn't.
/// </summary>
internal sealed class FlowTestCliOptions
{
    /// <summary>Path to the flow JSON file (positional arg).</summary>
    public string? FlowPath { get; set; }
    /// <summary>Optional HTML report output (<c>--report</c>).</summary>
    public string? ReportPath { get; set; }
    /// <summary>Optional JUnit XML report output (<c>--junit</c>).</summary>
    public string? JUnitPath { get; set; }
    /// <summary>Optional SARIF 2.1.0 report output (<c>--sarif</c>).</summary>
    public string? SarifPath { get; set; }
    /// <summary>#181 — <c>--fail-on</c> exit-code gate: <c>any</c> (default) or <c>never</c>.</summary>
    public string FailOn { get; set; } = "any";
    /// <summary>Emit GitHub Actions <c>::error</c> annotations per failure (<c>--annotations</c>).</summary>
    public bool Annotations { get; set; }
    /// <summary>Re-capture every snapshot baseline instead of diffing (<c>--update-snapshots</c>).</summary>
    public bool UpdateSnapshots { get; set; }
    /// <summary>Fallback server URL used when a step doesn't carry its own (<c>--base-url</c>).</summary>
    public string? BaseUrl { get; set; }
    /// <summary><c>KEY=VALUE</c> pairs that populate the variable resolver (<c>--env</c>, repeatable).</summary>
    public IReadOnlyList<string> EnvOverrides { get; set; } = Array.Empty<string>();
    /// <summary>Dotenv-style files whose lines seed the resolver before <see cref="EnvOverrides"/> (<c>--env-file</c>, repeatable).</summary>
    public IReadOnlyList<string> EnvFiles { get; set; } = Array.Empty<string>();
    /// <summary>
    /// #208 Phase 5 — resolve <c>{{keyring.service/account}}</c> refs from
    /// the runner's OS credential store (<c>--keyring</c>). Off by default so
    /// a CI job explicitly opts into reading the machine's secret store.
    /// </summary>
    public bool Keyring { get; set; }

    /// <summary>
    /// #208 Phase 5 — deterministic seed for <c>{{ai.*}}</c> refs
    /// (<c>--ai-seed</c>). When set, each ai ref resolves to a stable
    /// value derived from the seed + ref name (no model call), so CI runs
    /// are byte-reproducible. Null leaves ai refs unresolved (the workbench
    /// resolves them via a model; the CLI has none).
    /// </summary>
    public string? AiSeed { get; set; }
}

// ---- Run-report record types — consumed by FlowJUnitReport + FlowHtmlReport ----

internal sealed class FlowRunReport
{
    public string FlowId { get; set; } = string.Empty;
    public string FlowName { get; set; } = string.Empty;
    public string FlowPath { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public long DurationMs { get; set; }
    public int TotalExpectations { get; set; }
    public int PassedExpectations { get; set; }
    public int FailedExpectations { get; set; }
    public int StepErrors { get; set; }
    public int ExitCode { get; set; }
    public List<FlowStepRunResult> Steps { get; } = new();
}

internal sealed class FlowStepRunResult
{
    public string StepId { get; set; } = string.Empty;
    public string StepType { get; set; } = "request";
    public string Service { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string? Status { get; set; }
    public long LatencyMs { get; set; }
    public string? ResponseBody { get; set; }
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string? Error { get; set; }
    /// <summary>True when the step was a control-flow / variable node that T2 v0 skips.</summary>
    public bool Skipped { get; set; }
    public List<FlowExpectationResult> Expectations { get; } = new();
}
