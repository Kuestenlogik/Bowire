// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

/// <summary>
/// Resolution-priority tests for <see cref="LayeredAnnotationStore"/>.
/// The ADR pins the order <c>User &gt; Plugin &gt; Auto</c> with explicit
/// <see cref="BuiltInSemanticTags.None"/> suppression at any tier; these
/// tests walk every (present-or-absent × user/plugin/auto) combination
/// to make sure the resolver agrees.
/// </summary>
public sealed class LayeredAnnotationStoreTests
{
    private static readonly AnnotationKey s_key
        = AnnotationKey.ForSingleType("svc", "m", "$.x");

    // The three "distinctive" tags used to identify which layer's
    // value the resolver picked.
    private static readonly SemanticTag s_userTag = new("user.kind");
    private static readonly SemanticTag s_pluginTag = new("plugin.kind");
    private static readonly SemanticTag s_autoTag = new("auto.kind");

    private static LayeredAnnotationStore Build(
        SemanticTag? user = null,
        SemanticTag? plugin = null,
        SemanticTag? auto = null)
    {
        var userLayer = new InMemoryAnnotationLayer();
        if (user is not null) userLayer.Set(s_key, user);

        var autoLayer = new InMemoryAnnotationLayer();
        if (auto is not null) autoLayer.Set(s_key, auto);

        Func<string, string, IEnumerable<Annotation>> hints = (svc, mth) =>
            plugin is null
                ? []
                : [new Annotation(s_key, plugin, AnnotationSource.Plugin)];

        return new LayeredAnnotationStore(
            userSessionLayer: userLayer,
            userFileLayer: null,
            projectFileLayer: null,
            autoDetectorLayer: autoLayer,
            pluginHints: hints);
    }

    // ----------------------------------------------------------------
    // Exhaustive presence matrix: 2³ = 8 combinations.
    // ----------------------------------------------------------------

    [Fact]
    public void Empty_Store_Returns_Null()
    {
        var store = Build();
        Assert.Null(store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.None, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void Auto_Only_Wins()
    {
        var store = Build(auto: s_autoTag);
        Assert.Equal(s_autoTag, store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.Auto, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void Plugin_Only_Wins()
    {
        var store = Build(plugin: s_pluginTag);
        Assert.Equal(s_pluginTag, store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.Plugin, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void User_Only_Wins()
    {
        var store = Build(user: s_userTag);
        Assert.Equal(s_userTag, store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.User, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void Plugin_Beats_Auto()
    {
        var store = Build(plugin: s_pluginTag, auto: s_autoTag);
        Assert.Equal(s_pluginTag, store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.Plugin, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void User_Beats_Auto()
    {
        var store = Build(user: s_userTag, auto: s_autoTag);
        Assert.Equal(s_userTag, store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.User, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void User_Beats_Plugin()
    {
        var store = Build(user: s_userTag, plugin: s_pluginTag);
        Assert.Equal(s_userTag, store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.User, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void User_Beats_Plugin_Beats_Auto()
    {
        var store = Build(user: s_userTag, plugin: s_pluginTag, auto: s_autoTag);
        Assert.Equal(s_userTag, store.GetEffective(s_key));
        Assert.Equal(AnnotationSource.User, store.GetEffectiveSource(s_key));
    }

    // ----------------------------------------------------------------
    // SemanticTag.None suppression at each tier.
    // ----------------------------------------------------------------

    [Fact]
    public void User_None_Suppresses_Plugin_And_Auto()
    {
        var store = Build(user: BuiltInSemanticTags.None, plugin: s_pluginTag, auto: s_autoTag);
        // User says "no" — that's still a real annotation; the
        // resolver returns BuiltInSemanticTags.None, not null. The UI
        // uses this to render "explicitly suppressed."
        var effective = store.GetEffective(s_key);
        Assert.NotNull(effective);
        Assert.True(effective!.IsNone);
        Assert.Equal(AnnotationSource.User, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void Plugin_None_Suppresses_Auto()
    {
        var store = Build(plugin: BuiltInSemanticTags.None, auto: s_autoTag);
        var effective = store.GetEffective(s_key);
        Assert.NotNull(effective);
        Assert.True(effective!.IsNone);
        Assert.Equal(AnnotationSource.Plugin, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void Auto_None_Stands_When_No_Higher_Layer()
    {
        var store = Build(auto: BuiltInSemanticTags.None);
        var effective = store.GetEffective(s_key);
        Assert.NotNull(effective);
        Assert.True(effective!.IsNone);
        Assert.Equal(AnnotationSource.Auto, store.GetEffectiveSource(s_key));
    }

    [Fact]
    public void Plugin_Overrides_User_None_Only_When_There_Is_No_User_Entry()
    {
        // Counter-test: a User-tier "none" suppression must not be
        // accidentally overruled by a Plugin tag. (Documents what
        // "User > Plugin" means in the suppression case.)
        var store = Build(user: BuiltInSemanticTags.None, plugin: s_pluginTag);
        var effective = store.GetEffective(s_key);
        Assert.NotNull(effective);
        Assert.True(effective!.IsNone);
    }

    // ----------------------------------------------------------------
    // Multi-file behaviour: session > project > user.
    // ----------------------------------------------------------------

    [Fact]
    public void Within_User_Tier_Session_Beats_Project_Beats_UserFile()
    {
        var tempUser = Path.Combine(Path.GetTempPath(), $"bw-user-{Guid.NewGuid():N}.json");
        var tempProject = Path.Combine(Path.GetTempPath(), $"bw-project-{Guid.NewGuid():N}.json");
        try
        {
            using var userFile = new JsonFileAnnotationLayer(tempUser);
            using var projectFile = new JsonFileAnnotationLayer(tempProject);
            userFile.Replace([new(s_key, new("from.user.file"))]);
            projectFile.Replace([new(s_key, new("from.project.file"))]);

            var session = new InMemoryAnnotationLayer();
            session.Set(s_key, new("from.session"));

            var store = new LayeredAnnotationStore(
                userSessionLayer: session,
                userFileLayer: userFile,
                projectFileLayer: projectFile,
                autoDetectorLayer: new InMemoryAnnotationLayer(),
                pluginHints: (_, _) => []);

            Assert.Equal("from.session", store.GetEffective(s_key)?.Kind);

            // Drop the session and the project file beats the user file.
            session.Remove(s_key);
            Assert.Equal("from.project.file", store.GetEffective(s_key)?.Kind);

            // Drop the project too and the user-local file is the last
            // user-priority survivor.
            projectFile.Replace([]);
            Assert.Equal("from.user.file", store.GetEffective(s_key)?.Kind);
        }
        finally
        {
            try { if (File.Exists(tempUser)) File.Delete(tempUser); } catch { }
            try { if (File.Exists(tempProject)) File.Delete(tempProject); } catch { }
        }
    }

    [Fact]
    public void Constructor_Allows_Null_File_Layers()
    {
        // The DI registration code in BowireServiceCollectionExtensions
        // passes null for either file layer when the user opts out;
        // the store must accept that without throwing.
        var store = new LayeredAnnotationStore(
            userSessionLayer: new InMemoryAnnotationLayer(),
            userFileLayer: null,
            projectFileLayer: null,
            autoDetectorLayer: new InMemoryAnnotationLayer(),
            pluginHints: (_, _) => []);

        Assert.Null(store.GetEffective(s_key));
    }

    [Fact]
    public void Constructor_Throws_On_Null_Required_Layers()
    {
        Assert.Throws<ArgumentNullException>(() => new LayeredAnnotationStore(
            userSessionLayer: null!,
            userFileLayer: null,
            projectFileLayer: null,
            autoDetectorLayer: new InMemoryAnnotationLayer(),
            pluginHints: (_, _) => []));

        Assert.Throws<ArgumentNullException>(() => new LayeredAnnotationStore(
            userSessionLayer: new InMemoryAnnotationLayer(),
            userFileLayer: null,
            projectFileLayer: null,
            autoDetectorLayer: null!,
            pluginHints: (_, _) => []));

        Assert.Throws<ArgumentNullException>(() => new LayeredAnnotationStore(
            userSessionLayer: new InMemoryAnnotationLayer(),
            userFileLayer: null,
            projectFileLayer: null,
            autoDetectorLayer: new InMemoryAnnotationLayer(),
            pluginHints: null!));
    }

    // ----------------------------------------------------------------
    // Enumeration semantics.
    // ----------------------------------------------------------------

    [Fact]
    public void EnumerateEffective_Returns_One_Entry_Per_Distinct_Key()
    {
        // Same key seen by both User and Auto must produce ONE entry,
        // sourced as User (the winner).
        var store = Build(user: s_userTag, auto: s_autoTag);
        var all = store.EnumerateEffective().ToList();

        Assert.Single(all);
        Assert.Equal(s_userTag, all[0].Semantic);
        Assert.Equal(AnnotationSource.User, all[0].Source);
    }

    [Fact]
    public void EnumerateEffective_Includes_Plugin_Only_Keys()
    {
        // Plugin annotations on a key nobody else touches must still
        // surface — the resolver pulls them in by walking the
        // (service, method) pairs we already saw.
        var key1 = AnnotationKey.ForSingleType("svc", "m", "$.a");
        var key2 = AnnotationKey.ForSingleType("svc", "m", "$.b");

        var userLayer = new InMemoryAnnotationLayer();
        userLayer.Set(key1, s_userTag);

        Func<string, string, IEnumerable<Annotation>> hints = (_, _) =>
            [new(key2, s_pluginTag, AnnotationSource.Plugin)];

        var store = new LayeredAnnotationStore(
            userSessionLayer: userLayer,
            userFileLayer: null,
            projectFileLayer: null,
            autoDetectorLayer: new InMemoryAnnotationLayer(),
            pluginHints: hints);

        var all = store.EnumerateEffective().ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.Key == key1 && a.Source == AnnotationSource.User);
        Assert.Contains(all, a => a.Key == key2 && a.Source == AnnotationSource.Plugin);
    }
}
