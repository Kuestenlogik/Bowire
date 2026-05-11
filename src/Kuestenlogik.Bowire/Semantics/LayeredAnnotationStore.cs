// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics;

/// <summary>
/// The composed annotation store: a session-scope
/// <see cref="InMemoryAnnotationLayer"/> at user priority, a user-local
/// JSON file, a project-local JSON file, and a plugin-hints callback.
/// Implements the resolution priority pinned by the ADR
/// (<c>User &gt; Plugin &gt; Auto</c>) so callers get the effective
/// answer from one call.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 of the frame-semantics framework does not yet wire detectors
/// in, so the <see cref="AnnotationSource.Auto"/> layer is an explicit
/// <see cref="InMemoryAnnotationLayer"/> that detector code in Phase 2
/// can populate without reshaping the store.
/// </para>
/// <para>
/// The plugin-hints surface is a <see cref="Func{T1, T2, TResult}"/>
/// rather than an <see cref="IBowireSchemaHints"/> reference so the
/// store stays decoupled from the plugin registry — wiring up the
/// callback against the actual <see cref="BowireProtocolRegistry"/>
/// happens in <see cref="BowireServiceCollectionExtensions.AddBowire(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </para>
/// </remarks>
public sealed class LayeredAnnotationStore : IAnnotationStore
{
    private readonly Func<string, string, IEnumerable<Annotation>> _pluginHints;

    /// <summary>
    /// In-memory layer at <see cref="AnnotationSource.User"/> priority.
    /// Manual right-click edits that have not yet been promoted to
    /// disk live here for the session lifetime.
    /// </summary>
    public InMemoryAnnotationLayer UserSessionLayer { get; }

    /// <summary>
    /// User-local JSON-file layer at <see cref="AnnotationSource.User"/>
    /// priority. Backs <c>~/.bowire/schema-hints.json</c> in the
    /// default <see cref="BowireServiceCollectionExtensions.AddBowire(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>
    /// wiring. May be <c>null</c> when the host opts out of user
    /// persistence entirely.
    /// </summary>
    public JsonFileAnnotationLayer? UserFileLayer { get; }

    /// <summary>
    /// Project-local JSON-file layer at <see cref="AnnotationSource.User"/>
    /// priority — files versioned alongside source code
    /// (<c>bowire.schema-hints.json</c> at the repo root). May be
    /// <c>null</c> when no project file is configured.
    /// </summary>
    public JsonFileAnnotationLayer? ProjectFileLayer { get; }

    /// <summary>
    /// In-memory layer at <see cref="AnnotationSource.Auto"/> priority.
    /// Phase-2 detectors will populate this; Phase 1 leaves it empty.
    /// </summary>
    public InMemoryAnnotationLayer AutoDetectorLayer { get; }

    /// <summary>
    /// Construct a store from explicit layers.
    /// </summary>
    /// <param name="userSessionLayer">
    /// Required. The in-memory layer at user priority — manual session
    /// edits that have not yet been escalated to a file.
    /// </param>
    /// <param name="userFileLayer">
    /// Optional. User-local JSON file. Pass <c>null</c> to opt out of
    /// user-scope persistence (e.g. hardened deployments).
    /// </param>
    /// <param name="projectFileLayer">
    /// Optional. Project-local JSON file. Pass <c>null</c> when no
    /// project hint file exists.
    /// </param>
    /// <param name="autoDetectorLayer">
    /// Required. Phase-2 detector output lands here. Pass an empty
    /// layer in Phase 1.
    /// </param>
    /// <param name="pluginHints">
    /// Required. Callback that returns plugin-supplied annotations for
    /// a given <c>(serviceId, methodId)</c> pair. The framework calls
    /// it lazily during <see cref="GetEffective"/> and
    /// <see cref="EnumerateEffective"/>.
    /// </param>
    public LayeredAnnotationStore(
        InMemoryAnnotationLayer userSessionLayer,
        JsonFileAnnotationLayer? userFileLayer,
        JsonFileAnnotationLayer? projectFileLayer,
        InMemoryAnnotationLayer autoDetectorLayer,
        Func<string, string, IEnumerable<Annotation>> pluginHints)
    {
        ArgumentNullException.ThrowIfNull(userSessionLayer);
        ArgumentNullException.ThrowIfNull(autoDetectorLayer);
        ArgumentNullException.ThrowIfNull(pluginHints);

        UserSessionLayer = userSessionLayer;
        UserFileLayer = userFileLayer;
        ProjectFileLayer = projectFileLayer;
        AutoDetectorLayer = autoDetectorLayer;
        _pluginHints = pluginHints;
    }

    /// <inheritdoc/>
    public SemanticTag? GetEffective(AnnotationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Resolution priority: User > Plugin > Auto. Within the user
        // tier, the in-memory session layer beats project, which beats
        // user-local — the more transient + the more specific overrides
        // the long-lived + the more general. Project files are
        // team-shared (versioned alongside code) so they outrank
        // user-local hints.
        var fromUser = ReadHighestUserSource(key);
        if (fromUser is not null) return fromUser;

        var fromPlugin = ReadFirstPluginHint(key);
        if (fromPlugin is not null) return fromPlugin;

        return AutoDetectorLayer.Get(key);
    }

    /// <inheritdoc/>
    public IEnumerable<Annotation> EnumerateEffective()
    {
        // Walk every unique key visible across the layers and resolve
        // each one through GetEffective so the priority rule applies
        // uniformly. The plugin-hint callback is not key-discoverable
        // without a (service, method) pair, so we drive enumeration
        // from the (service, method) pairs we DO see in the other
        // layers and ask the callback for hints on each unique pair.
        var seen = new HashSet<AnnotationKey>();

        IEnumerable<Annotation> ResolveKey(AnnotationKey key)
        {
            if (!seen.Add(key)) yield break;
            var tag = GetEffective(key);
            if (tag is null) yield break;
            yield return new Annotation(key, tag, GetEffectiveSource(key));
        }

        foreach (var kv in UserSessionLayer.Snapshot())
            foreach (var a in ResolveKey(kv.Key)) yield return a;

        if (ProjectFileLayer is not null)
        {
            foreach (var kv in ProjectFileLayer.Entries)
                foreach (var a in ResolveKey(kv.Key)) yield return a;
        }
        if (UserFileLayer is not null)
        {
            foreach (var kv in UserFileLayer.Entries)
                foreach (var a in ResolveKey(kv.Key)) yield return a;
        }
        foreach (var kv in AutoDetectorLayer.Snapshot())
            foreach (var a in ResolveKey(kv.Key)) yield return a;

        // Plugin hints — enumerate every (service, method) pair seen
        // above and ask the callback for hints on each. Plugin
        // annotations under keys nobody else has touched would
        // otherwise be invisible to EnumerateEffective; this loop
        // pulls them in.
        var pairs = new HashSet<(string, string)>();
        foreach (var key in seen) pairs.Add((key.ServiceId, key.MethodId));
        foreach (var (svc, mth) in pairs)
        {
            foreach (var annotation in _pluginHints(svc, mth))
            {
                if (annotation is null) continue;
                foreach (var a in ResolveKey(annotation.Key)) yield return a;
            }
        }
    }

    /// <summary>
    /// Resolve the <see cref="AnnotationSource"/> that supplied the
    /// effective tag for <paramref name="key"/>. Returns
    /// <see cref="AnnotationSource.None"/> when no source claims the
    /// key. Useful for the UI to badge an annotation as "(auto)",
    /// "(plugin)", or "(user)".
    /// </summary>
    public AnnotationSource GetEffectiveSource(AnnotationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (ReadHighestUserSource(key) is not null) return AnnotationSource.User;
        if (ReadFirstPluginHint(key) is not null) return AnnotationSource.Plugin;
        if (AutoDetectorLayer.Get(key) is not null) return AnnotationSource.Auto;
        return AnnotationSource.None;
    }

    private SemanticTag? ReadHighestUserSource(AnnotationKey key)
    {
        // Session edits win over both file layers.
        var session = UserSessionLayer.Get(key);
        if (session is not null) return session;

        // Project file outranks user-local (team-shared > personal).
        var project = ProjectFileLayer?.Get(key);
        if (project is not null) return project;

        return UserFileLayer?.Get(key);
    }

    private SemanticTag? ReadFirstPluginHint(AnnotationKey key)
    {
        foreach (var hint in _pluginHints(key.ServiceId, key.MethodId))
        {
            if (hint is null) continue;
            if (hint.Key == key) return hint.Semantic;
        }
        return null;
    }

}
