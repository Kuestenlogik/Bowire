// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Kuestenlogik.Bowire.ManualEntry.V1;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.ManualEntry.Tests;

/// <summary>
/// The handler behind <c>ListManualEntryForms</c> (#995): which plugins it
/// lists, in which order, how it pages, and which forms it refuses to serve.
/// </summary>
public sealed class ManualEntryRpcTests
{
    private static readonly BowireManualEntryForm s_valid = new(
        [new BowireManualCallKind("produce", "Produce", "Unary") { Service = "{topic}", Method = "produce" }])
    {
        Fields = [new BowireManualField("topic", "Topic", BowireManualFieldPlacement.Template) { Required = true }],
    };

    /// <summary>A protocol plugin that answers nothing; only its id and form matter here.</summary>
    private class Plugin(string id) : IBowireProtocol
    {
        public string Id => id;
        public string Name => id;
        public string IconSvg => "<svg/>";

        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool showInternalServices, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());

        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method, List<string> jsonMessages,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool showInternalServices, Dictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class Described(string id, BowireManualEntryForm? form) : Plugin(id), IBowireManualEntry
    {
        public BowireManualEntryForm? DescribeManualEntry() => form;
    }

    [Fact]
    public void A_Plugin_That_Describes_Nothing_Is_Left_Out_Without_A_Report()
    {
        // #1003 - a sidecar whose initialize reply carries no form.
        var rejected = new List<string>();

        var response = ManualEntryRpc.List(
            [new Described("sidecar", null), new Described("kafka", s_valid)],
            new ListManualEntryFormsRequest(),
            (id, _) => rejected.Add(id));

        Assert.Equal(["kafka"], response.Forms.Select(f => f.Protocol));
        Assert.Empty(rejected);
    }

    [Fact]
    public void Lists_The_Plugins_That_Describe_A_Form_Ordered_By_Id()
    {
        var response = ManualEntryRpc.List(
            [new Described("pulsar", s_valid), new Plugin("rest"), new Described("kafka", s_valid)],
            new ListManualEntryFormsRequest());

        Assert.Equal(["kafka", "pulsar"], response.Forms.Select(f => f.Protocol));
        Assert.Empty(response.NextPageToken);
    }

    [Fact]
    public void Carries_The_Whole_Form()
    {
        var form = ManualEntryRpc.List([new Described("kafka", s_valid)], new ListManualEntryFormsRequest()).Forms.Single();

        var kind = Assert.Single(form.CallKinds);
        Assert.Equal(("produce", "Unary", "{topic}", "produce"), (kind.Id, kind.MethodType, kind.Service, kind.Method));
        var field = Assert.Single(form.Fields);
        Assert.Equal(("topic", Placement.Template, true), (field.Key, field.Placement, field.Required));
    }

    [Fact]
    public void Pages_With_The_Last_Id_As_Token_And_Neither_Repeats_Nor_Skips()
    {
        var plugins = new List<IBowireProtocol> { new Described("a", s_valid), new Described("b", s_valid), new Described("c", s_valid) };

        var first = ManualEntryRpc.List(plugins, new ListManualEntryFormsRequest { PageSize = 2 });
        Assert.Equal(["a", "b"], first.Forms.Select(f => f.Protocol));
        Assert.Equal("b", first.NextPageToken);

        // A plugin that sorts before the token arrives between the two pages:
        // it is not on the second page, and nothing is seen twice.
        plugins.Add(new Described("aa", s_valid));
        var second = ManualEntryRpc.List(plugins, new ListManualEntryFormsRequest { PageSize = 2, PageToken = first.NextPageToken });
        Assert.Equal(["c"], second.Forms.Select(f => f.Protocol));
        Assert.Empty(second.NextPageToken);
    }

    [Fact]
    public void A_Negative_Page_Size_Is_Invalid()
    {
        var ex = Assert.Throws<RpcException>(() =>
            ManualEntryRpc.List([], new ListManualEntryFormsRequest { PageSize = -1 }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public void A_Form_With_A_Problem_Is_Left_Out_And_Reported()
    {
        var broken = new BowireManualEntryForm(
            [new BowireManualCallKind("produce", "Produce", "Sometimes") { Service = "{topic}" }]);
        var rejected = new List<string>();

        var response = ManualEntryRpc.List(
            [new Described("broken", broken), new Described("kafka", s_valid)],
            new ListManualEntryFormsRequest(),
            (id, problems) => rejected.Add(id + ": " + string.Join("; ", problems)));

        Assert.Equal(["kafka"], response.Forms.Select(f => f.Protocol));
        var report = Assert.Single(rejected);
        Assert.Contains("method type 'Sometimes'", report, StringComparison.Ordinal);
        Assert.Contains("placeholder 'topic'", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no call kind")]
    [InlineData("declared twice")]
    [InlineData("has no options")]
    [InlineData("unknown call kind")]
    public void The_Check_Names_Each_Problem(string expected)
    {
        var form = expected switch
        {
            "no call kind" => new BowireManualEntryForm([]),
            "declared twice" => new BowireManualEntryForm(
                [new BowireManualCallKind("a", "A", "Unary"), new BowireManualCallKind("a", "A", "Unary")]),
            "has no options" => new BowireManualEntryForm([new BowireManualCallKind("a", "A", "Unary")])
            {
                Fields = [new BowireManualField("v", "V", BowireManualFieldPlacement.Metadata) { Type = "select" }],
            },
            _ => new BowireManualEntryForm([new BowireManualCallKind("a", "A", "Unary")])
            {
                Fields = [new BowireManualField("v", "V", BowireManualFieldPlacement.Metadata) { CallKinds = ["b"] }],
            },
        };

        Assert.Contains(ManualEntryRpc.Problems(form), p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_Method_Type_Form_Names_One_Call_Kind_Per_Type()
    {
        var form = BowireManualEntryForm.ForMethodTypes("Unary", "ServerStreaming");

        Assert.Empty(ManualEntryRpc.Problems(form));
        Assert.Equal(["Unary", "ServerStreaming"], form.CallKinds.Select(k => k.MethodType));
    }
}
