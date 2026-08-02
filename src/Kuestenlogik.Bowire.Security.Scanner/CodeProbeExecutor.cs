// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Runs a translated Nuclei <c>code:</c> template (#491, #35 Phase 2g) in a
/// child process.
/// </summary>
/// <remarks>
/// <para>
/// <b>This executes attacker-authored code on the machine running the scan.</b>
/// Nuclei's corpus is community-supplied and a <c>code:</c> template is a
/// program, not a request — running one is materially different from sending a
/// packet. Nothing here happens unless the operator passes
/// <c>--allow-code-templates</c>; the scan loop refuses first and this type is
/// never reached otherwise.
/// </para>
/// <para>
/// <b>What the child process actually buys.</b> A separate process bounds the
/// blast radius of a hang or a crash, lets the timeout be enforced by killing
/// something, and keeps the scanner's own memory out of reach. It is not a
/// sandbox: the child inherits the caller's user, filesystem access and
/// network. Saying otherwise would be worse than saying nothing. What is
/// enforced here is an interpreter allow-list, a wall-clock kill, an output
/// cap, and a scratch working directory.
/// </para>
/// </remarks>
public static class CodeProbeExecutor
{
    /// <summary>
    /// Interpreters a template may name when the operator has not said
    /// otherwise. Covers what the corpus uses.
    /// </summary>
    /// <remarks>
    /// This is a guardrail, not a security boundary, and it is worth being
    /// exact about which. Once <c>--allow-code-templates</c> is on, the entries
    /// here already run arbitrary code with the caller's rights — <c>bash</c>
    /// and <c>python3</c> are on the list. What the list actually prevents is a
    /// template naming some *other* binary and getting it launched with our
    /// scratch script as its single argument. The boundary is the opt-in flag;
    /// this just keeps the set of launched programs predictable, which is why
    /// the operator may replace it (<c>--code-template-interpreters</c>)
    /// without that being a hole in anything.
    /// </remarks>
    public static readonly IReadOnlySet<string> DefaultEngines =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sh", "bash", "zsh",
            "py", "python", "python3",
            "node",
            "ruby", "perl",
            "pwsh", "powershell",
        };

    /// <summary>Output cap. A template that prints without end would otherwise
    /// take the scanner's memory with it.</summary>
    public const int MaxOutputChars = 256 * 1024;

    /// <summary>
    /// Run the step's program and shape its output for the matchers.
    /// </summary>
    /// <param name="probe">Service holds the engine list, Body the program.</param>
    /// <param name="timeoutSeconds">Wall-clock budget; the child is killed past it.</param>
    /// <param name="allowedEngines">
    /// Interpreters this run may launch. Null uses <see cref="DefaultEngines"/>.
    /// Replacing rather than extending is deliberate: it lets an operator
    /// NARROW the set (only <c>python3</c> here, thanks) as easily as widen it,
    /// and narrowing is the direction an additive switch could not express.
    /// </param>
    /// <param name="resolveEngine">
    /// Maps an engine name to an executable path, or null when it is not
    /// installed. Injected so the decision logic is testable without depending
    /// on which interpreters the test machine happens to have.
    /// </param>
    /// <param name="ct">Cancels the probe.</param>
    public static async Task<AttackProbeResponse> ExecuteAsync(
        BowireRecordingStep probe,
        int timeoutSeconds = 30,
        IReadOnlySet<string>? allowedEngines = null,
        Func<string, string?>? resolveEngine = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var service = probe.Service ?? string.Empty;
        if (string.Equals(service, "nuclei-javascript", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "javascript: templates run inside Nuclei's embedded JS runtime and call its nuclei/* module library; "
                + "handing that source to node would fail on the first require(). Not executable here.");
        }

        var source = probe.Body ?? string.Empty;
        if (source.Trim().Length == 0)
        {
            throw new InvalidOperationException("code template carries no source to run.");
        }

        var engines = service
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (engines.Length == 0)
        {
            throw new InvalidOperationException("code template names no engine to run its source with.");
        }

        var allowed = allowedEngines is { Count: > 0 } ? allowedEngines : DefaultEngines;
        var rejected = engines.Where(e => !allowed.Contains(e)).ToList();
        var candidates = engines.Where(allowed.Contains).ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"code template asks for engine(s) '{string.Join(", ", rejected)}', none of which are allowed. "
                + $"Permitted: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}. "
                + "Change the set with --code-template-interpreters.");
        }

        resolveEngine ??= FindOnPath;
        var chosen = candidates.Select(e => (Engine: e, Path: resolveEngine(e)))
            .FirstOrDefault(x => x.Path is not null);
        if (chosen.Path is null)
        {
            throw new InvalidOperationException(
                $"none of the engines this template can use are installed: {string.Join(", ", candidates)}.");
        }

        var scratch = Directory.CreateTempSubdirectory("bowire-code-");
        try
        {
            var scriptPath = Path.Combine(scratch.FullName, "template" + ExtensionFor(chosen.Engine));
            await File.WriteAllTextAsync(scriptPath, source, ct).ConfigureAwait(false);

            return await RunAsync(chosen.Path, scriptPath, scratch.FullName, timeoutSeconds, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { scratch.Delete(recursive: true); }
            catch (IOException) { /* the child may still hold a handle; not worth failing the scan over */ }
            catch (UnauthorizedAccessException) { /* same */ }
        }
    }

    private static async Task<AttackProbeResponse> RunAsync(
        string enginePath, string scriptPath, string workingDirectory, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = enginePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // As an argument, never interpolated into a command line: the path is
        // ours, but making that a habit is what keeps the next edit safe.
        psi.ArgumentList.Add(scriptPath);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);

        var sw = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Close stdin so a program that reads from it fails fast instead of
        // blocking until the timeout.
        process.StandardInput.Close();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
            if (!timedOut) throw;
        }
        sw.Stop();

        var body = new StringBuilder(stdout.ToString());
        if (stderr.Length > 0)
        {
            // Kept rather than dropped: a template whose finding lands on
            // stderr would otherwise read as a clean run. The separator marks
            // the seam so a reader can tell which stream a match came from.
            body.Append("\n--- stderr ---\n").Append(stderr);
        }
        if (timedOut)
        {
            body.Append("\n--- killed after ").Append(timeoutSeconds).Append("s ---\n");
        }

        return new AttackProbeResponse
        {
            // Exit code, so a `type: status` matcher on a code: template reads
            // what the program returned. -1 when it had to be killed.
            Status = timedOut ? -1 : SafeExitCode(process),
            Body = Truncate(body.ToString()),
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };
    }

    private static void Append(StringBuilder sink, string? line)
    {
        if (line is null) return;
        if (sink.Length >= MaxOutputChars) return;
        sink.Append(line).Append('\n');
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxOutputChars
            ? value
            : value[..MaxOutputChars] + "\n--- output truncated ---\n";
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>
    /// Extension per engine. A lookup rather than a lowercased switch: casing
    /// belongs to the comparer, not to a transformation of the input.
    /// </summary>
    private static readonly Dictionary<string, string> s_extensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["py"] = ".py",
            ["python"] = ".py",
            ["python3"] = ".py",
            ["node"] = ".js",
            ["ruby"] = ".rb",
            ["perl"] = ".pl",
            ["pwsh"] = ".ps1",
            ["powershell"] = ".ps1",
        };

    /// <summary>File extension for the scratch script. Some interpreters pick
    /// their mode from it, and Windows refuses to launch an extensionless
    /// script through several of them.</summary>
    public static string ExtensionFor(string engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return s_extensions.TryGetValue(engine, out var extension) ? extension : ".sh";
    }

    /// <summary>Locate an interpreter on PATH, honouring PATHEXT on Windows.</summary>
    public static string? FindOnPath(string engine)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(directory.Trim(), engine + extension); }
                catch (ArgumentException) { continue; } // a malformed PATH entry
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
