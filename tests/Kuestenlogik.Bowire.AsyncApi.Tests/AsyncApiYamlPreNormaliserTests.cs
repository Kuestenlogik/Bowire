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
}
