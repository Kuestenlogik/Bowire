// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Security.Cryptography;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using Kuestenlogik.Bowire.Oast.Server;

namespace Kuestenlogik.Bowire.Oast.Tests;

/// <summary>
/// The DNS half of the OAST catcher: what a lookup against the delegated
/// zone records, and what it answers.
/// </summary>
/// <remarks>
/// This sat at 20.7% — the constructor and little else. The parts worth
/// pinning are the ones that decide whether an out-of-band finding is
/// real: which names count as ours, which are noise, and what a resolver
/// is told so a follow-up fetch reaches the HTTP catcher.
/// </remarks>
public sealed class OastDnsCatcherTests
{
    private const string Domain = "oast.example.com";
    private const string CorrelationId = "00000000000000000000";
    private static readonly IPAddress PublicIp = IPAddress.Parse("203.0.113.7");

    private static (OastDnsCatcher Catcher, OastInteractionStore Store, List<string> Log) Build()
    {
        var store = new OastInteractionStore();
        using var rsa = RSA.Create(2048);
        Assert.True(store.TryRegister(CorrelationId, "secret", rsa.ExportSubjectPublicKeyInfoPem()));

        var log = new List<string>();
        return (new OastDnsCatcher(Domain, PublicIp, store, log.Add), store, log);
    }

    private static async Task<IResponse> AskAsync(OastDnsCatcher catcher, string name, RecordType type)
    {
        var request = new Request();
        request.Questions.Add(new Question(new Domain(name), type));
        return await catcher.Resolve(request, TestContext.Current.CancellationToken);
    }

    private static List<OastInteraction> Drain(OastInteractionStore store)
        => store.Poll(CorrelationId, "secret")?.Interactions ?? [];

    [Fact]
    public async Task A_Lookup_Under_The_Zone_Is_Recorded_As_Evidence()
    {
        var (catcher, store, log) = Build();

        await AskAsync(catcher, $"{CorrelationId}.{Domain}", RecordType.A);

        var interaction = Assert.Single(Drain(store));
        Assert.Equal("dns", interaction.Protocol);
        Assert.Equal(CorrelationId, interaction.UniqueId);
        Assert.Equal($"{CorrelationId}.{Domain}", interaction.FullId);
        Assert.Equal("A", interaction.QType);
        // The raw request is what a report shows a reader; it has to name
        // the question, not just say one arrived.
        Assert.Contains(CorrelationId, interaction.RawRequest, StringComparison.Ordinal);
        Assert.Single(log);
    }

    [Fact]
    public async Task An_A_Query_Is_Answered_With_Our_Address()
    {
        // The address is what makes a follow-up fetch land on the HTTP
        // catcher, which is how a blind finding becomes a confirmed one.
        var (catcher, _, _) = Build();

        var response = await AskAsync(catcher, $"{CorrelationId}.{Domain}", RecordType.A);

        Assert.True(response.AuthorativeServer);
        var record = Assert.IsType<IPAddressResourceRecord>(Assert.Single(response.AnswerRecords));
        Assert.Equal(PublicIp, record.IPAddress);
    }

    [Fact]
    public async Task An_ANY_Query_Is_Answered_The_Same_Way()
    {
        var (catcher, _, _) = Build();

        var response = await AskAsync(catcher, $"{CorrelationId}.{Domain}", RecordType.ANY);

        Assert.Single(response.AnswerRecords);
    }

    [Theory]
    [InlineData(RecordType.TXT)]
    [InlineData(RecordType.MX)]
    [InlineData(RecordType.CNAME)]
    public async Task Another_Type_Is_Recorded_But_Answered_Empty(RecordType type)
    {
        // NOERROR with no answer is a true statement about a name we own.
        // Inventing a record would be a lie, and the interaction is the
        // thing being collected anyway.
        var (catcher, store, _) = Build();

        var response = await AskAsync(catcher, $"{CorrelationId}.{Domain}", type);

        Assert.True(response.AuthorativeServer);
        Assert.Empty(response.AnswerRecords);
        Assert.Single(Drain(store));
    }

    [Fact]
    public async Task A_Name_Outside_The_Zone_Is_Neither_Recorded_Nor_Answered()
    {
        // Port 53 is scanned around the clock; traffic nobody registered
        // for is noise, not evidence.
        var (catcher, store, log) = Build();

        var response = await AskAsync(catcher, "www.example.org", RecordType.A);

        Assert.Empty(response.AnswerRecords);
        Assert.Empty(Drain(store));
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_Lookalike_Domain_Does_Not_Pass_For_Ours()
    {
        // The trap a plain EndsWith walks into: `evil-oast.example.com`
        // ends with `oast.example.com` and is a different zone entirely.
        var (catcher, store, _) = Build();

        var response = await AskAsync(catcher, $"{CorrelationId}.evil-{Domain}", RecordType.A);

        Assert.Empty(response.AnswerRecords);
        Assert.Empty(Drain(store));
    }

    [Fact]
    public async Task The_Zone_Apex_Itself_Counts_As_Ours()
    {
        var (catcher, _, _) = Build();

        var response = await AskAsync(catcher, Domain, RecordType.A);

        // Answered — but not recorded, because the apex carries no
        // correlation id and belongs to no session.
        Assert.Single(response.AnswerRecords);
    }

    [Fact]
    public async Task A_Name_Nobody_Registered_Is_Answered_But_Not_Kept()
    {
        // The address still goes out: a resolver asking about our zone
        // gets a straight answer. What does not happen is evidence being
        // attributed to a session that never planted the host.
        var (catcher, store, log) = Build();

        var response = await AskAsync(catcher, $"11111111111111111111.{Domain}", RecordType.A);

        Assert.Single(response.AnswerRecords);
        Assert.Empty(Drain(store));
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_Trailing_Dot_Is_Not_Part_Of_The_Name()
    {
        // Resolvers send the fully-qualified form; the stored id must not
        // carry the root label or it will not match the session.
        var (catcher, store, _) = Build();

        var request = new Request();
        request.Questions.Add(new Question(new Domain($"{CorrelationId}.{Domain}."), RecordType.A));
        await catcher.Resolve(request, TestContext.Current.CancellationToken);

        var interaction = Assert.Single(Drain(store));
        // The root label is gone, so the name ends at the zone.
        Assert.EndsWith(Domain, interaction.FullId, StringComparison.Ordinal);
        Assert.False(interaction.FullId!.EndsWith('.'), interaction.FullId);
    }

    [Fact]
    public async Task Several_Questions_In_One_Request_Are_All_Considered()
    {
        var (catcher, store, _) = Build();
        var request = new Request();
        request.Questions.Add(new Question(new Domain($"{CorrelationId}.{Domain}"), RecordType.A));
        request.Questions.Add(new Question(new Domain("elsewhere.example.org"), RecordType.A));
        request.Questions.Add(new Question(new Domain($"{CorrelationId}.{Domain}"), RecordType.TXT));

        var response = await catcher.Resolve(request, TestContext.Current.CancellationToken);

        // Two of ours recorded, one foreign ignored; one answer, because
        // only the A question asks for an address.
        Assert.Equal(2, Drain(store).Count);
        Assert.Single(response.AnswerRecords);
    }

    [Fact]
    public async Task Matching_Is_Case_Insensitive()
    {
        // Resolvers randomise case as an anti-spoofing measure (0x20
        // encoding), so a case-sensitive zone check would drop real hits.
        var (catcher, _, _) = Build();

        var response = await AskAsync(catcher, $"{CorrelationId}.OAST.Example.COM", RecordType.A);

        Assert.Single(response.AnswerRecords);
    }

    [Fact]
    public async Task A_Catcher_Without_A_Log_Still_Records()
    {
        // The log callback is optional; the evidence is not.
        var store = new OastInteractionStore();
        using var rsa = RSA.Create(2048);
        store.TryRegister(CorrelationId, "secret", rsa.ExportSubjectPublicKeyInfoPem());
        var catcher = new OastDnsCatcher(Domain, PublicIp, store);

        await AskAsync(catcher, $"{CorrelationId}.{Domain}", RecordType.A);

        Assert.Single(store.Poll(CorrelationId, "secret")!.Value.Interactions);
    }
}
