// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the <see cref="BowirePluginSetting"/> and
/// <see cref="BowirePluginSettingOption"/> records — the settings-schema
/// shape that protocol plugins contribute to the workbench's Settings dialog.
/// Records are auto-generated, but property accessors and equality semantics
/// still need a smoke test to catch silently-broken renames during refactors.
/// </summary>
public class BowirePluginSettingTests
{
    [Fact]
    public void Setting_Defaults_Match_Documented_Shape()
    {
        var setting = new BowirePluginSetting("autoInterpretJson", "Auto-interpret JSON");

        Assert.Equal("autoInterpretJson", setting.Key);
        Assert.Equal("Auto-interpret JSON", setting.Label);
        Assert.Null(setting.Description);
        Assert.Equal("bool", setting.Type);
        Assert.Null(setting.DefaultValue);
        Assert.Null(setting.Options);
    }

    [Fact]
    public void Setting_Carries_All_Optional_Fields()
    {
        var options = new List<BowirePluginSettingOption>
        {
            new("v1", "Version 1"),
            new("v2", "Version 2"),
        };
        var setting = new BowirePluginSetting(
            Key: "apiVersion",
            Label: "API Version",
            Description: "Pick the API version to target",
            Type: "select",
            DefaultValue: "v2",
            Options: options);

        Assert.Equal("apiVersion", setting.Key);
        Assert.Equal("API Version", setting.Label);
        Assert.Equal("Pick the API version to target", setting.Description);
        Assert.Equal("select", setting.Type);
        Assert.Equal("v2", setting.DefaultValue);
        Assert.NotNull(setting.Options);
        Assert.Equal(2, setting.Options!.Count);
    }

    [Fact]
    public void Setting_Equality_Matches_All_Fields()
    {
        var a = new BowirePluginSetting("k", "L");
        var b = new BowirePluginSetting("k", "L");
        var c = new BowirePluginSetting("k", "Different");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void SettingOption_Carries_Value_And_Label()
    {
        var option = new BowirePluginSettingOption("v1", "Version 1");

        Assert.Equal("v1", option.Value);
        Assert.Equal("Version 1", option.Label);
    }

    [Fact]
    public void SettingOption_Equality_Matches_All_Fields()
    {
        var a = new BowirePluginSettingOption("a", "Alpha");
        var b = new BowirePluginSettingOption("a", "Alpha");
        var c = new BowirePluginSettingOption("a", "Different");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void SettingOption_With_Expression_Replaces_Label()
    {
        var original = new BowirePluginSettingOption("v1", "Version 1");
        var renamed = original with { Label = "Production" };

        Assert.Equal("v1", renamed.Value);
        Assert.Equal("Production", renamed.Label);
        Assert.NotEqual(original, renamed);
    }
}
