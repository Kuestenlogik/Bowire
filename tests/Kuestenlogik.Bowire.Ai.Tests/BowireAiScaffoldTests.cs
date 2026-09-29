// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Ai;
using Microsoft.Extensions.AI;

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// #177 — the assistant writes the scaffold spec, never the files; a model
/// that fails or answers nonsense hands over to the next one, and the
/// parser catches what no model delivered.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Scripted clients hold nothing")]
public sealed class BowireAiScaffoldTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedChatClient(Func<string> answer) : IChatClient
    {
        public int Calls { get; private set; }
        public string? LastUser { get; private set; }
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastUser = messages.Last().Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => Disposed = true;
    }

    private const string GoodAnswer = """
        Sure! Here is the spec:
        ```json
        {"entity": "user", "protocol": "rest", "fields": [
          {"name": "email", "type": "string", "required": true},
          {"name": "Role", "type": "text"},
          {"name": "id", "type": "uuid"}
        ]}
        ```
        """;

    [Fact]
    public void A_spec_is_read_out_of_prose_and_a_code_fence_and_normalized()
    {
        var (spec, errors) = BowireAiScaffold.TryParseSpec(GoodAnswer, null);
        Assert.Empty(errors);
        Assert.Equal("User", spec!.Entity);
        Assert.Equal([("email", "string", true), ("role", "string", false)], spec.Fields.Select(f => (f.Name, f.Type, f.Required)));
    }

    [Theory]
    [InlineData("I cannot help with that.", "no JSON object")]
    [InlineData("{\"entity\": 42}", "does not read as a spec")]
    [InlineData("{\"entity\": \"User\", \"fields\": [{\"name\": \"email\", \"type\": \"money\"}]}", "money")]
    public void An_unusable_answer_says_why(string answer, string expected)
    {
        var (spec, errors) = BowireAiScaffold.TryParseSpec(answer, null);
        Assert.Null(spec);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_protocol_the_operator_picked_wins_over_the_model()
    {
        var (spec, _) = BowireAiScaffold.TryParseSpec(GoodAnswer, "grpc");
        Assert.Equal("grpc", spec!.Protocol);
    }

    [Fact]
    public async Task The_first_model_that_answers_usefully_writes_the_spec()
    {
        var broken = new ScriptedChatClient(() => throw new HttpRequestException("connection refused"));
        var nonsense = new ScriptedChatClient(() => "no idea");
        var good = new ScriptedChatClient(() => GoodAnswer);
        var local = new ScriptedChatClient(() => GoodAnswer);

        var proposal = await BowireAiScaffold.ProposeAsync(
            [new("ollama:llama3.2:3b", broken, local), new("lmstudio:x", nonsense), new("anthropic:claude", good)],
            "REST CRUD for User with email + role", null, Ct);

        Assert.Equal("ai", proposal.Source);
        Assert.Equal("anthropic:claude", proposal.Model);
        Assert.Equal(2, proposal.Notes.Count);
        Assert.Contains("connection refused", proposal.Notes[0], StringComparison.Ordinal);
        Assert.Contains("not with a usable spec", proposal.Notes[1], StringComparison.Ordinal);
        Assert.Contains("Description: REST CRUD for User", good.LastUser, StringComparison.Ordinal);
        Assert.True(local.Disposed); // a client built for the call is disposed after it
    }

    [Fact]
    public async Task Without_a_useful_model_the_parser_writes_the_spec()
    {
        var proposal = await BowireAiScaffold.ProposeAsync([], "gRPC service for orders with customer (required)", null, Ct);
        Assert.Equal("parser", proposal.Source);
        Assert.Null(proposal.Model);
        Assert.Equal("Order", proposal.Spec.Entity);
        Assert.Equal("grpc", proposal.Spec.Protocol);
    }

    [Fact]
    public async Task A_configured_local_model_is_asked_without_probing_for_another()
    {
        var configured = new ScriptedChatClient(() => GoodAnswer);
        var models = await BowireAiScaffold.ModelsAsync(
            new BowireAiOptions { ProviderId = "ollama", Model = "qwen2.5:7b" }, configured, [], preferLocal: true, Ct);
        var only = Assert.Single(models);
        Assert.Equal("ollama:qwen2.5:7b", only.Label);
        Assert.Same(configured, only.Client);
    }

    [Fact]
    public async Task With_PreferLocal_off_a_cloud_provider_is_asked_alone()
    {
        var configured = new ScriptedChatClient(() => GoodAnswer);
        var models = await BowireAiScaffold.ModelsAsync(
            new BowireAiOptions { ProviderId = "anthropic", Model = "claude-opus-4-7" }, configured, [], preferLocal: false, Ct);
        Assert.Equal("anthropic:claude-opus-4-7", Assert.Single(models).Label);
    }
}
