// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.AsyncApi;
using System.Reflection;

namespace Kuestenlogik.Bowire.AsyncApi.Tests;

/// <summary>
/// Unit tests for the YAML pre-normaliser that works around
/// asyncapi/net-sdk#76. Verifies that unquoted `asyncapi:` and `version:`
/// values get quoted, already-quoted ones are left alone, and the
/// transform is idempotent + comment-preserving.
/// </summary>
public sealed class AsyncApiYamlPreNormaliserTests
{
    // Reflection-backed call so the test project doesn't need the
    // internal class to be `public`. Single helper used by all asserts.
    private static string Normalise(string yaml)
    {
        var assembly = typeof(BowireAsyncApiProtocol).Assembly;
        var type = assembly.GetType("Kuestenlogik.Bowire.AsyncApi.AsyncApiYamlPreNormaliser")
            ?? throw new InvalidOperationException("Pre-normaliser type not found.");
        var method = type.GetMethod("Normalise", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Normalise method not found.");
        return (string)method.Invoke(null, new object[] { yaml })!;
    }

    [Fact]
    public void Quotes_unquoted_asyncapi_version()
    {
        var input = "asyncapi: 3.0.0\ninfo:\n  title: X\n";
        var output = Normalise(input);
        Assert.Contains("asyncapi: '3.0.0'", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_unquoted_info_version()
    {
        var input = "asyncapi: '3.0.0'\ninfo:\n  title: X\n  version: 1.2.3\n";
        var output = Normalise(input);
        Assert.Contains("version: '1.2.3'", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_already_single_quoted_values_unchanged()
    {
        var input = "asyncapi: '3.0.0'\ninfo:\n  version: '1.2.3'\n";
        var output = Normalise(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Leaves_already_double_quoted_values_unchanged()
    {
        var input = "asyncapi: \"3.0.0\"\n";
        var output = Normalise(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Is_idempotent()
    {
        var input = "asyncapi: 3.0.0\ninfo:\n  version: 1.2.3\n";
        var once = Normalise(input);
        var twice = Normalise(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Preserves_trailing_comments()
    {
        var input = "asyncapi: 3.0.0  # the spec version\n";
        var output = Normalise(input);
        Assert.Contains("asyncapi: '3.0.0'", output, StringComparison.Ordinal);
        Assert.Contains("# the spec version", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, Normalise(string.Empty));
    }

    [Fact]
    public void Lowercases_upper_case_binding_keys()
    {
        // Authors who write `Kafka:` / `MQTT:` under bindings hit the
        // SDK's case-sensitive lookup. The pre-normaliser rewrites
        // those to the spec-canonical lower-case form before the
        // reader sees the document.
        var input = "operations:\n  op:\n    bindings:\n      Kafka:\n        topic: t\n      MQTT:\n        qos: 1\n";
        var output = Normalise(input);
        Assert.Contains("      kafka:", output, StringComparison.Ordinal);
        Assert.Contains("      mqtt:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("      Kafka:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("      MQTT:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_websocket_alias_to_ws()
    {
        // AsyncAPI 2.x docs sometimes write `websocket:` where the
        // spec mandates `ws:`. Alias resolution kicks in after the
        // lowercase pass so `WebSocket` also lands on `ws`.
        var input = "channels:\n  c:\n    bindings:\n      websocket:\n        method: GET\n";
        var output = Normalise(input);
        Assert.Contains("      ws:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("websocket:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_canonical_binding_keys_unchanged()
    {
        var input = "operations:\n  op:\n    bindings:\n      kafka:\n        topic: t\n";
        var output = Normalise(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Does_not_rewrite_binding_like_names_outside_bindings_block()
    {
        // A channel key that happens to spell out a binding-id should
        // stay untouched. The pre-normaliser only rewrites direct
        // children of a `bindings:` header.
        var input = "channels:\n  Kafka:\n    address: 't'\n";
        var output = Normalise(input);
        Assert.Contains("  Kafka:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_nested_binding_fields_alone()
    {
        // Direct children of bindings: get rewritten (the binding-id
        // keys themselves). Their nested-deeper field bag does NOT —
        // those are arbitrary author-defined fields (qos, retain,
        // schemaIdLocation, …) and have nothing to do with the
        // binding-id name registry.
        var input = "operations:\n  op:\n    bindings:\n      Kafka:\n        Kafka: should-stay\n";
        var output = Normalise(input);
        Assert.Contains("      kafka:", output, StringComparison.Ordinal);
        Assert.Contains("        Kafka: should-stay", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_key_normalisation_is_idempotent()
    {
        var input = "operations:\n  op:\n    bindings:\n      Kafka:\n        topic: t\n      WebSocket:\n        method: GET\n";
        var once = Normalise(input);
        var twice = Normalise(once);
        Assert.Equal(once, twice);
    }
}
