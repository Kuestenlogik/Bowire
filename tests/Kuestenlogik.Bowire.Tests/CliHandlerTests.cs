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
        await Assert.ThrowsAsync<ArgumentNullException>(() => CliHandler.CallAsync(null!));
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
        });
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
        });
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

        var rc = await CliHandler.CallAsync(cli);
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

            var rc = await CliHandler.CallAsync(cli);
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
}
