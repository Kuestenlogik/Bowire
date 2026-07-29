// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit-level coverage for the gRPC-centric CLI handlers (list / describe /
/// call). The happy paths talk to a live gRPC server with reflection and
/// belong in the integration harness; here we exercise the synchronous
/// argument-validation branches and the catch-all error reporter (URL that
/// resolves but isn't a gRPC server). Tests assert exit codes only —
/// stderr capture would be racy with xUnit's parallel runner because
/// <see cref="Console.SetError"/> is process-wide.
/// </summary>
public sealed class CliHandlerTests
{
    // 127.0.0.1:1 reliably refuses TCP connections without resolving DNS,
    // so reflection fails fast through HttpClient rather than blocking.
    private const string DeadUrl = "http://127.0.0.1:1";

    [Fact]
    public async Task ListAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CliHandler.ListAsync(null!));
    }

    [Fact]
    public async Task DescribeAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CliHandler.DescribeAsync(null!));
    }

    [Fact]
    public async Task CallAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CliHandler.CallAsync(null!, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DescribeAsync_NoTarget_ReturnsUsageExit()
    {
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = null,
        });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task CallAsync_NoTarget_ReturnsUsageExit()
    {
        var rc = await CliHandler.CallAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = null,
        }, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task CallAsync_TargetWithoutSlash_ReturnsUsageExit()
    {
        // Call requires service/method, not just a service name.
        var rc = await CliHandler.CallAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService",
        }, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task CallAsync_AtFileReferenceMissing_ReturnsErrorExit()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"bowire-call-{Guid.NewGuid():N}.json");

        var cli = new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Get",
        };
        cli.Data.Add("@" + bogus);

        var rc = await CliHandler.CallAsync(cli, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task CallAsync_AtFileReferenceLoadsFromDisk_BeforeFailingOnDeadUrl()
    {
        // Existing @file gets read; the call then fails when the dead URL
        // doesn't accept gRPC reflection. Net effect: exit 1, the error
        // reporter ran (so we covered RunWithErrorHandling's catch path
        // for CallAsync after data expansion + header parsing).
        var dataPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(dataPath, "{\"id\":1}", TestContext.Current.CancellationToken);

            var cli = new CliCommandOptions
            {
                Url = DeadUrl,
                Target = "users.UserService/Get",
            };
            cli.Data.Add("@" + dataPath);
            cli.Headers.Add("authorization: bearer x");
            cli.Headers.Add("malformed-no-colon");

            var rc = await CliHandler.CallAsync(cli, null, null, TestContext.Current.CancellationToken);
            Assert.Equal(1, rc);
        }
        finally
        {
            try { File.Delete(dataPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ListAsync_DeadUrl_ReturnsErrorExit()
    {
        // Reflection call on a dead port → handler catches, prints,
        // returns 1.
        var rc = await CliHandler.ListAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Verbose = true,
        });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task DescribeAsync_DeadUrl_ServiceTarget_ReturnsErrorExit()
    {
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService",
        });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task DescribeAsync_DeadUrl_MethodTarget_ReturnsErrorExit()
    {
        // service/method shape → goes through the method-describe branch,
        // still surfaces the network failure as exit 1.
        var rc = await CliHandler.DescribeAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Get",
        });
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task CallAsync_DeadUrlNoData_DefaultsToEmptyObjectThenFails()
    {
        // No -d → impl injects "{}" as the single message before the
        // dead-URL invocation fails. Exercises the default-message
        // branch alongside the catch path.
        var rc = await CliHandler.CallAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Get",
        }, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task CallAsync_HeadersWithEmptyKey_StripQuietlyAndStillFails()
    {
        // Header without a colon prefix gets dropped (colonIdx <= 0); a
        // header whose key trims to empty also gets dropped — both
        // exercise the silent-skip branch in the metadata parser.
        var cli = new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Get",
        };
        cli.Headers.Add(":   value-only");          // empty key after trim
        cli.Headers.Add("no-colon-at-all");          // no colon
        cli.Headers.Add("good-key: good-value");     // accepted

        var rc = await CliHandler.CallAsync(cli, null, null, TestContext.Current.CancellationToken);
        // Dead URL still fails, but we covered the parser branches above.
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task CallAsync_DataNotStartingWithAt_PassesThrough()
    {
        // Plain JSON -d (no @file prefix) — exercises the
        // "skip @-expansion" branch before the network failure.
        var cli = new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Get",
        };
        cli.Data.Add("{\"id\":1}");

        var rc = await CliHandler.CallAsync(cli, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
    }

    // ---------------- #538: the protocol-generic branch ----------------
    //
    // These assert stderr as well as the exit code, which the class note
    // above rules out for the Console-wide path — but CallAsync takes
    // explicit writers, so nothing process-global is touched and the
    // parallel runner stays safe.

    [Fact]
    public async Task CallAsync_UnknownProtocol_NamesTheLoadedPluginsAndExits2()
    {
        using var err = new StringWriter();
        var rc = await CliHandler.CallAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Get",
            Protocol = "definitely-not-a-plugin",
        }, TextWriter.Null, err, TestContext.Current.CancellationToken);

        Assert.Equal(2, rc);
        // Either "Unknown protocol …" (plugins loaded) or "No protocol
        // plugins are loaded." — both must point at `plugin install`
        // rather than leaving the operator guessing what ids exist.
        Assert.Contains("plugin", err.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallAsync_ExplicitGrpcWithoutStream_StaysOnTheFastPath()
    {
        // `--protocol grpc` means what the default means, so it must NOT
        // start paying for BowireProtocolRegistry.Discover()'s assembly
        // scan. The observable proof is the error shape: the fast path
        // surfaces the gRPC transport failure through RunWithErrorHandling
        // as exit 1, where the registry path would report a discovery
        // verdict as exit 2.
        using var err = new StringWriter();
        var rc = await CliHandler.CallAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Get",
            Protocol = "grpc",
        }, TextWriter.Null, err, TestContext.Current.CancellationToken);

        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task CallAsync_EnvFileMissing_ReportsAndExits2()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"bowire-env-{Guid.NewGuid():N}.env");
        using var err = new StringWriter();

        var cli = new CliCommandOptions { Url = DeadUrl, Target = "users.UserService/Get" };
        cli.VarFiles.Add(bogus);

        var rc = await CliHandler.CallAsync(cli, TextWriter.Null, err, TestContext.Current.CancellationToken);

        // A silently-empty variable map would send a body with literal
        // {{token}} in it, which is far worse than failing.
        Assert.Equal(2, rc);
        Assert.Contains("--env-file", err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallAsync_VarsResolveInBodyAndUrl()
    {
        // The resolver runs before the invoker, so the proof of
        // substitution is that the call reaches (and fails at) the
        // SUBSTITUTED host rather than erroring on a malformed URL.
        var cli = new CliCommandOptions
        {
            Url = "http://{{host}}:1",
            Target = "users.UserService/Get",
        };
        cli.Data.Add("{\"id\":\"{{who}}\"}");
        cli.Vars.Add("host=127.0.0.1");
        cli.Vars.Add("who=42");

        var rc = await CliHandler.CallAsync(cli, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, rc);
        Assert.Equal("http://127.0.0.1:1", cli.Url);
    }

    [Fact]
    public async Task CallAsync_Stream_NeverBothPrintsNothingAndSucceeds()
    {
        // The contract --stream must hold no matter which plugin answers:
        // it never both produces no output AND exits 0. In a pipeline the
        // two together are indistinguishable from success, which is why
        // StreamViaProtocolAsync warns and exits 1 on an empty stream
        // instead of returning silently.
        //
        // The assertion is deliberately about that invariant rather than
        // about one plugin's error text, because the plugin that answers
        // here is NOT the real one. BowireProtocolRegistry.Discover()
        // assembly-scans, and this test assembly declares several
        // IBowireProtocol stubs — including one in
        // Security/GrpcConcurrentStreamProbeTests with `Id => "grpc"` and
        // a parameterless ctor, which the scan instantiates and which wins
        // the id lookup. It discovers a fake service and yields a canned
        // frame, so in-process this exercises the "stream delivered
        // something" arm; against the real plugin (verified from the built
        // tool) the dead URL raises Unavailable and the catch renders it.
        // Both arms satisfy the invariant; neither may be silent-and-zero.
        using var outSw = new StringWriter();
        using var err = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var rc = await CliHandler.CallAsync(new CliCommandOptions
        {
            Url = DeadUrl,
            Target = "users.UserService/Watch",
            Protocol = "grpc",
            Stream = true,
        }, outSw, err, cts.Token);

        var printedNothing = outSw.ToString().Length == 0 && err.ToString().Length == 0;
        Assert.False(rc == 0 && printedNothing,
            "--stream exited 0 without printing anything, which a pipeline cannot tell "
            + "apart from success. stdout=<" + outSw + "> stderr=<" + err + ">");
    }
}
