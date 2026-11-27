// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Scripting;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Scripting;

/// <summary>
/// #1062 — one reader of <c>Bowire:Scripting:*</c> for <c>bowire test</c> and
/// the workbench, so a limit means the same in both.
/// </summary>
public sealed class ScriptLimitsConfigurationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void Unset_Keys_Keep_The_Defaults()
    {
        Assert.Equal(new BowireScriptLimits(), BowireScriptLimits.FromConfiguration(Config()));
        Assert.Equal(new BowireScriptLimits(), BowireScriptLimits.FromConfiguration(null));
    }

    [Fact]
    public void Set_Keys_Override_And_Nonsense_Is_Ignored()
    {
        var limits = BowireScriptLimits.FromConfiguration(Config(
            ("Bowire:Scripting:TimeoutMs", "250"),
            ("Bowire:Scripting:MemoryMb", "16"),
            ("Bowire:Scripting:MaxStatements", "-3")));

        Assert.Equal(TimeSpan.FromMilliseconds(250), limits.Timeout);
        Assert.Equal(16L * 1024 * 1024, limits.MemoryBytes);
        Assert.Equal(new BowireScriptLimits().MaxStatements, limits.MaxStatements);
    }

    [Fact]
    public void The_Workbench_Page_Carries_The_Limits()
    {
        var html = BowireHtmlGenerator.GenerateIndexHtml(new BowireOptions
        {
            ScriptLimits = new BowireScriptLimits { Timeout = TimeSpan.FromMilliseconds(1500), MemoryBytes = 32L * 1024 * 1024 },
        }, new Microsoft.AspNetCore.Http.DefaultHttpContext().Request, "nonce");

        Assert.Contains("scriptLimits: { timeoutMs: 1500, memoryBytes: 33554432 }", html, StringComparison.Ordinal);
    }
}
