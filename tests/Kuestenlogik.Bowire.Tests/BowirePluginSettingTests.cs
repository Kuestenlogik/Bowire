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
    // ---- #691: catalogue keys beside the text ----
    //
    // The labels travel to the browser already rendered, so the workbench
    // cannot tell they are English. A plugin says "the catalogue has a
    // translation for this" by naming a key; both are optional, and a
    // plugin that names neither has to behave exactly as it did before —
    // which is every third-party plugin, because they have no entry in
    // Bowire's catalogue and no way to add one.

    [Fact]
    public void Setting_Without_Keys_Leaves_Them_Null()
    {
        var setting = new BowirePluginSetting("autoInterpretJson", "Auto-interpret JSON",
            "Parse JSON payloads", "bool", true);

        Assert.Null(setting.LabelKey);
        Assert.Null(setting.DescriptionKey);
    }

    [Fact]
    public void Setting_Carries_Catalogue_Keys_Beside_The_Text()
    {
        var setting = new BowirePluginSetting(
            Key: "scanDuration",
            Label: "Subject scan duration",
            Description: "How long to subscribe during discovery",
            Type: "number",
            DefaultValue: 3)
        {
            LabelKey = "plugin.nats.scanDuration.label",
            DescriptionKey = "plugin.nats.scanDuration.desc",
        };

        // The English text stays put: it is the fallback, not a leftover.
        Assert.Equal("Subject scan duration", setting.Label);
        Assert.Equal("How long to subscribe during discovery", setting.Description);
        Assert.Equal("plugin.nats.scanDuration.label", setting.LabelKey);
        Assert.Equal("plugin.nats.scanDuration.desc", setting.DescriptionKey);
    }

    [Fact]
    public void Option_Carries_A_Key_And_Defaults_To_None()
    {
        Assert.Null(new BowirePluginSettingOption("1.1", "SOAP 1.1").LabelKey);
        Assert.Equal("plugin.soap.v11",
            new BowirePluginSettingOption("1.1", "SOAP 1.1")
            { LabelKey = "plugin.soap.v11" }.LabelKey);
    }

    [Fact]
    public void The_Primary_Constructor_Is_Unchanged_For_Plugins_Built_Before_691()
    {
        // The shape every already-installed plugin was compiled against.
        // The keys are init properties rather than constructor parameters
        // precisely so this signature survives: appending two parameters
        // would delete the six-parameter .ctor from the metadata, and an
        // old binary calling it throws MissingMethodException at runtime.
        // The installed DIS plugin did, and took /api/protocols with it.
        var setting = new BowirePluginSetting("k", "L", "D", "select", "a",
            [new BowirePluginSettingOption("a", "A")]);

        Assert.Equal("select", setting.Type);
        Assert.Single(setting.Options!);
        Assert.Null(setting.LabelKey);
    }
}
