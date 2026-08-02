// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using DnsClient;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// #491 (#35 Phase 2g) — the DNS transport pass.
///
/// These run against a stubbed <see cref="IDnsAnswerSource"/> rather than a
/// live resolver: a test that queries the real DNS tests the network and the
/// weather, while the thing worth pinning is the step-to-query mapping and
/// what a matcher ends up seeing.
/// </summary>
public sealed class DnsProbeExecutorTests
{
    private sealed class StubSource(DnsProbeAnswer answer) : IDnsAnswerSource
    {
        public string? LastName { get; private set; }
        public string? LastRecordType { get; private set; }

        public Task<DnsProbeAnswer> QueryAsync(string name, string recordType, CancellationToken ct)
        {
            LastName = name;
            LastRecordType = recordType;
            return Task.FromResult(answer);
        }
    }

    private static BowireRecordingStep Step(string name, string recordType = "A") => new()
    {
        Id = "probe-1",
        Protocol = "dns",
        Service = name,
        Method = recordType,
        MethodType = "Unary",
        Status = "OK",
    };

    [Fact]
    public async Task Sends_The_Name_And_Record_Type_From_The_Step()
    {
        // The converter writes the query into Service/Method; that contract is
        // load-bearing and nothing else in the pipeline restates it.
        var stub = new StubSource(new DnsProbeAnswer { ResponseCode = 0, Answers = [] });

        await DnsProbeExecutor.ExecuteAsync(Step("shop.example.com", "CNAME"), stub, TestContext.Current.CancellationToken);

        Assert.Equal("shop.example.com", stub.LastName);
        Assert.Equal("CNAME", stub.LastRecordType);
    }

    [Fact]
    public async Task Defaults_To_An_A_Query_When_The_Step_Names_No_Record_Type()
    {
        var stub = new StubSource(new DnsProbeAnswer { ResponseCode = 0, Answers = [] });

        var step = Step("example.com");
        step.Method = "";
        await DnsProbeExecutor.ExecuteAsync(step, stub, TestContext.Current.CancellationToken);

        Assert.Equal("A", stub.LastRecordType);
    }

    [Fact]
    public async Task Body_Carries_One_Answer_Record_Per_Line()
    {
        var stub = new StubSource(new DnsProbeAnswer
        {
            ResponseCode = 0,
            Answers =
            [
                "shop.example.com. 300 IN CNAME shop.myshopify.com.",
                "shop.myshopify.com. 300 IN A 23.227.38.65",
            ],
        });

        var response = await DnsProbeExecutor.ExecuteAsync(
            Step("shop.example.com", "CNAME"), stub, TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Body.Split('\n').Length);
        Assert.Contains("shop.myshopify.com", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_Carries_The_Rcode_Not_An_Http_Code()
    {
        // 3 = NXDOMAIN. A `type: status` matcher on a dns: template means the
        // rcode, so the predicate slot has to hold that and not 200.
        var stub = new StubSource(new DnsProbeAnswer { ResponseCode = 3, Answers = [] });

        var response = await DnsProbeExecutor.ExecuteAsync(
            Step("gone.example.com"), stub, TestContext.Current.CancellationToken);

        Assert.Equal(3, response.Status);
    }

    [Fact]
    public async Task Refuses_A_Name_That_Still_Holds_A_Placeholder()
    {
        // Querying "{{FQDN}}" literally comes back NXDOMAIN, which the scan
        // would then report as a clean result for a target it never tested.
        var stub = new StubSource(new DnsProbeAnswer { ResponseCode = 0, Answers = [] });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DnsProbeExecutor.ExecuteAsync(Step("{{FQDN}}"), stub, TestContext.Current.CancellationToken));

        Assert.Contains("placeholder", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(stub.LastName);
    }

    [Fact]
    public async Task Refuses_An_Empty_Name()
    {
        var stub = new StubSource(new DnsProbeAnswer { ResponseCode = 0, Answers = [] });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DnsProbeExecutor.ExecuteAsync(Step(""), stub, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("A", QueryType.A)]
    [InlineData("aaaa", QueryType.AAAA)]
    [InlineData("CNAME", QueryType.CNAME)]
    [InlineData("txt", QueryType.TXT)]
    [InlineData("MX", QueryType.MX)]
    [InlineData("NS", QueryType.NS)]
    public void ParseRecordType_Maps_The_Types_Templates_Actually_Use(string input, QueryType expected)
    {
        Assert.Equal(expected, DnsProbeExecutor.ParseRecordType(input));
    }

    [Fact]
    public void ParseRecordType_Refuses_An_Unknown_Type_Rather_Than_Falling_Back_To_A()
    {
        // Silently answering a TXT template with A records would judge it
        // against the wrong data and call it clean.
        Assert.Throws<InvalidOperationException>(() => DnsProbeExecutor.ParseRecordType("NOTAREALTYPE"));
    }

    [Fact]
    public async Task A_Translated_Takeover_Matcher_Fires_On_The_Answer()
    {
        // End to end over the seam that matters: the predicate a subdomain-
        // takeover template translates to, evaluated against a real answer shape.
        var stub = new StubSource(new DnsProbeAnswer
        {
            ResponseCode = 0,
            Answers = ["shop.example.com. 300 IN CNAME shop.myshopify.com."],
        });

        var response = await DnsProbeExecutor.ExecuteAsync(
            Step("shop.example.com", "CNAME"), stub, TestContext.Current.CancellationToken);

        var predicate = new AttackPredicate { BodyContains = "myshopify.com" };
        Assert.True(AttackPredicateEvaluator.Evaluate(predicate, response));

        var other = new AttackPredicate { BodyContains = "herokuapp.com" };
        Assert.False(AttackPredicateEvaluator.Evaluate(other, response));
    }
}
