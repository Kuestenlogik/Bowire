// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// The values behind the settings plugins declare (#640).
/// </summary>
/// <remarks>
/// <para>
/// Before this, <c>IBowireProtocol.Settings</c> was a schema the workbench
/// rendered into the browser's local storage and nothing else: no value ever
/// reached the plugin that declared it. Four plugins shipped a control that
/// persisted across reloads and changed nothing, which is worse than an absent
/// one — someone widens a probe window, watches the value stick, and concludes
/// the thing they were looking for is not there.
/// </para>
/// <para>
/// What these pin is the two properties that make it trustworthy: a value
/// reaches the plugin that asked for it, and it reaches only the workspace it
/// was set in.
/// </para>
/// </remarks>
[Collection("BowireUserContext")]
public sealed class BowirePluginSettingsTests : IDisposable
{
    private const string Dis = "dis";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-plugin-settings-" + Guid.NewGuid().ToString("N"));

    private readonly IBowireUserStore _previousUsers = BowireUserContext.Current;

    public BowirePluginSettingsTests()
    {
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new DefaultBowireUserStore(_root);
    }

    public void Dispose()
    {
        BowireUserContext.Current = _previousUsers;
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AValueSetInAWorkspaceReachesThePluginThatAskedForIt()
    {
        // The whole point, in one test.
        var store = new BowirePluginSettingsStore();

        using (BowirePluginSettingsScope.Enter("lab"))
        {
            store.Set(Dis, "probeDuration", "9");

            Assert.Equal("9", store.GetValue(Dis, "probeDuration"));
            Assert.Equal(TimeSpan.FromSeconds(9),
                ((IBowirePluginSettings)store).GetSeconds(Dis, "probeDuration", TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void OneWorkspacesSettingIsNotAnothers()
    {
        // A slow lab network is a property of what you are pointed at. The
        // cache is keyed by workspace path for the same reason the disabled-
        // plugins list stopped being per identity (#284): one process serves
        // several, and a single cache hands one workspace's answer to another.
        var store = new BowirePluginSettingsStore();

        using (BowirePluginSettingsScope.Enter("lab")) store.Set(Dis, "probeDuration", "9");

        using (BowirePluginSettingsScope.Enter("production"))
        {
            Assert.Null(store.GetValue(Dis, "probeDuration"));
            Assert.Equal(TimeSpan.FromSeconds(3),
                ((IBowirePluginSettings)store).GetSeconds(Dis, "probeDuration", TimeSpan.FromSeconds(3)));
        }

        using (BowirePluginSettingsScope.Enter("lab"))
        {
            Assert.Equal("9", store.GetValue(Dis, "probeDuration"));
        }
    }

    [Fact]
    public void WithNoWorkspaceInScopeAPluginGetsItsDeclaredDefault()
    {
        // The CLI, a test, an embedded host that never adopted workspaces.
        // This is the ordinary case and must not be an error: a plugin has to
        // work when nobody has configured it.
        var store = new BowirePluginSettingsStore();

        Assert.Null(store.GetValue(Dis, "probeDuration"));
        Assert.Equal(TimeSpan.FromSeconds(3),
            ((IBowirePluginSettings)store).GetSeconds(Dis, "probeDuration", TimeSpan.FromSeconds(3)));
        Assert.False(store.Set(Dis, "probeDuration", "9"));
    }

    [Fact]
    public void ClearingIsDifferentFromSettingNothing()
    {
        var store = new BowirePluginSettingsStore();

        using (BowirePluginSettingsScope.Enter("lab"))
        {
            store.Set(Dis, "probeDuration", "9");
            Assert.True(store.Set(Dis, "probeDuration", null));

            // Back to the declared default, not to an empty string the plugin
            // would have to guess about.
            Assert.Null(store.GetValue(Dis, "probeDuration"));
        }
    }

    [Fact]
    public void SettingTheSameValueTwiceChangesNothing()
    {
        var store = new BowirePluginSettingsStore();

        using (BowirePluginSettingsScope.Enter("lab"))
        {
            Assert.True(store.Set(Dis, "probeDuration", "9"));
            Assert.False(store.Set(Dis, "probeDuration", "9"));
        }
    }

    [Fact]
    public void ItSurvivesTheProcessForgetting()
    {
        using (BowirePluginSettingsScope.Enter("lab"))
        {
            new BowirePluginSettingsStore().Set(Dis, "probeDuration", "9");

            // A second store over the same root is what a restart looks like.
            Assert.Equal("9", new BowirePluginSettingsStore().GetValue(Dis, "probeDuration"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("0")]
    [InlineData("-4")]
    public void ADurationThatIsNotOneFallsBack(string? configured)
    {
        // Zero and negative especially: a probe window of nothing is not a
        // configuration, it is a mistake, and honouring it would look exactly
        // like the bug this seam exists to fix — discovery finding nothing
        // and no explanation on the page.
        var store = new BowirePluginSettingsStore();

        using (BowirePluginSettingsScope.Enter("lab"))
        {
            if (configured is not null) store.Set(Dis, "probeDuration", configured);

            Assert.Equal(TimeSpan.FromSeconds(3),
                ((IBowirePluginSettings)store).GetSeconds(Dis, "probeDuration", TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void AnUnreadableFileMeansNothingIsConfigured()
    {
        // One corrupt file must not stop the workbench discovering anything.
        using (BowirePluginSettingsScope.Enter("lab"))
        {
            var path = BowireUserContext.GetWorkspacePath("lab", null, "plugin-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            Assert.Null(new BowirePluginSettingsStore().GetValue(Dis, "probeDuration"));
        }
    }

    [Fact]
    public void AHandEditedNumberIsReadAsWrittenRatherThanRefused()
    {
        // The store writes strings; a person editing the file will write 5.
        // Losing every other setting in the file over a quoting detail would
        // be a poor trade.
        using (BowirePluginSettingsScope.Enter("lab"))
        {
            var path = BowireUserContext.GetWorkspacePath("lab", null, "plugin-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "dis": { "probeDuration": 5 } }""");

            Assert.Equal(TimeSpan.FromSeconds(5),
                ((IBowirePluginSettings)new BowirePluginSettingsStore())
                    .GetSeconds(Dis, "probeDuration", TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void LeavingAScopeRestoresTheOneOutside()
    {
        using (BowirePluginSettingsScope.Enter("outer"))
        {
            using (BowirePluginSettingsScope.Enter("inner"))
            {
                Assert.Equal("inner", BowirePluginSettingsScope.Current?.WorkspaceId);
            }

            Assert.Equal("outer", BowirePluginSettingsScope.Current?.WorkspaceId);
        }

        Assert.Null(BowirePluginSettingsScope.Current);
    }
}
