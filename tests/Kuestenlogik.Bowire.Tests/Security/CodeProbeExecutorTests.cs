// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// #491 (#35 Phase 2g) — the code: transport pass.
///
/// The gate matters more than the plumbing here, so most of these assert what
/// the executor REFUSES. The engine lookup is injected, so the refusals are
/// tested without depending on which interpreters this machine happens to
/// have — and without running anything.
/// </summary>
public sealed class CodeProbeExecutorTests
{
    private static BowireRecordingStep Step(string engines, string source) => new()
    {
        Id = "probe-1",
        Protocol = "code",
        Service = engines,
        Method = "RUN",
        MethodType = "Unary",
        Body = source,
        Status = "OK",
    };

    /// <summary>Claims every engine is installed, without running one.</summary>
    private static string? AllInstalled(string engine) => "/usr/bin/" + engine;

    private static string? NoneInstalled(string engine) => null;

    [Fact]
    public async Task Refuses_A_JavaScript_Template_With_The_Actual_Reason()
    {
        // Handing this to node fails on `require("nuclei/mysql")`. Saying so is
        // more useful than a stack trace out of node, and it stops anyone
        // concluding the template ran and found nothing.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("nuclei-javascript", "var m = require('nuclei/mysql');"),
            resolveEngine: AllInstalled,
            ct: TestContext.Current.CancellationToken));

        Assert.Contains("embedded JS runtime", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refuses_An_Engine_Outside_The_Allow_List()
    {
        // `engine:` is a program name that gets executed, so the template picks
        // the binary as well as the payload unless something constrains it.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("curl", "whatever"),
            resolveEngine: AllInstalled,
            ct: TestContext.Current.CancellationToken));

        Assert.Contains("allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Keeps_The_Allowed_Engine_When_A_Template_Lists_Several()
    {
        // A mixed list must not be refused wholesale — nuclei templates
        // commonly name several interpreters as fallbacks.
        var seen = new List<string>();
        string? Resolve(string engine)
        {
            seen.Add(engine);
            return engine == "python3" ? "/usr/bin/python3" : null;
        }

        // Nothing is installed for the allowed candidates other than python3,
        // and `curl` must never be considered at all.
        await Assert.ThrowsAnyAsync<Exception>(() => CodeProbeExecutor.ExecuteAsync(
            Step("curl,python3", "print(1)"),
            resolveEngine: Resolve,
            ct: TestContext.Current.CancellationToken));

        Assert.Contains("python3", seen);
        Assert.DoesNotContain("curl", seen);
    }

    [Fact]
    public async Task Reports_When_No_Usable_Engine_Is_Installed()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("python3,bash", "print(1)"),
            resolveEngine: NoneInstalled,
            ct: TestContext.Current.CancellationToken));

        Assert.Contains("installed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refuses_An_Empty_Source()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("bash", "   "),
            resolveEngine: AllInstalled,
            ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refuses_A_Template_That_Names_No_Engine()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("", "print(1)"),
            resolveEngine: AllInstalled,
            ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("curl")]
    [InlineData("rm")]
    [InlineData("cmd")]
    [InlineData("wget")]
    public void The_Default_Set_Excludes_Non_Interpreters(string engine)
    {
        Assert.DoesNotContain(engine, CodeProbeExecutor.DefaultEngines);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("python3")]
    [InlineData("node")]
    [InlineData("pwsh")]
    public void The_Default_Set_Covers_What_The_Corpus_Uses(string engine)
    {
        Assert.Contains(engine, CodeProbeExecutor.DefaultEngines);
    }

    [Fact]
    public async Task An_Explicit_Interpreter_Set_Can_Widen_Beyond_The_Defaults()
    {
        // The point of the switch: `deno` is not in the default set, and
        // needing a Bowire release to add one would be the wrong shape.
        var seen = new List<string>();
        string? Resolve(string engine) { seen.Add(engine); return null; }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("deno", "console.log(1)"),
            allowedEngines: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "deno" },
            resolveEngine: Resolve,
            ct: TestContext.Current.CancellationToken));

        // It got as far as looking for deno, i.e. the allow-check passed.
        Assert.Contains("deno", seen);
        Assert.Contains("installed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_Explicit_Interpreter_Set_Replaces_Rather_Than_Extends()
    {
        // Narrowing is the direction an additive switch could not express, and
        // it is the one an operator hardening a runner actually wants.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("bash", "echo hi"),
            allowedEngines: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "python3" },
            resolveEngine: AllInstalled,
            ct: TestContext.Current.CancellationToken));

        Assert.Contains("allowed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bash", CodeProbeExecutor.DefaultEngines);
    }

    [Fact]
    public async Task An_Empty_Interpreter_Set_Falls_Back_To_The_Defaults()
    {
        // "not configured" must not read as "nothing permitted".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CodeProbeExecutor.ExecuteAsync(
            Step("bash", "echo hi"),
            allowedEngines: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolveEngine: NoneInstalled,
            ct: TestContext.Current.CancellationToken));

        // Reached the lookup, so bash was permitted by the default set.
        Assert.Contains("installed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("py", ".py")]
    [InlineData("python3", ".py")]
    [InlineData("node", ".js")]
    [InlineData("pwsh", ".ps1")]
    [InlineData("bash", ".sh")]
    public void Script_Extension_Follows_The_Engine(string engine, string expected)
    {
        // Several interpreters pick their mode from the extension, and Windows
        // refuses to launch an extensionless script through some of them.
        Assert.Equal(expected, CodeProbeExecutor.ExtensionFor(engine));
    }
}
