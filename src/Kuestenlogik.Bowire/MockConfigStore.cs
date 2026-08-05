// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Disk-backed store for a schema mock's <see cref="MockConfiguration"/>
/// refinement sidecar (#558) — the per-field overrides, conditional rules,
/// and auth-requirement an operator authors on top of a schema-generated
/// mock. Mirrors <see cref="PresetStore"/>: one file per (workspace, mock)
/// at <c>workspaces/&lt;wsId&gt;/mocks/&lt;mockId&gt;.json</c>, resolved
/// through <see cref="BowireUserContext.GetWorkspacePath"/> so the
/// per-identity / per-storage-root seams keep working, and persisting to
/// disk (not browser localStorage) lets the config survive a browser reset,
/// ride the workspace export, and sync via git.
/// </summary>
/// <remarks>
/// The on-disk shape is the <see cref="MockConfiguration"/> JSON the
/// workbench writes, so the endpoint is a validated pass-through:
/// <see cref="Load"/> returns the raw document (or a default envelope when
/// absent), <see cref="Save"/> rejects anything that is not a parseable
/// configuration, and <see cref="LoadConfig"/> gives the host a typed view.
/// </remarks>
internal static partial class MockConfigStore
{
    private static string? _testStorePathOverride;

    // CodeQL cs/path-injection allow-list — same anchored pattern the
    // recordings + preset stores use so an user-supplied id can't escape
    // the store directory. The anchored-regex barrier is the form the
    // analyser recognises as a sanitiser.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeIdPattern();

    /// <summary>
    /// On-disk store location for a given (workspace, mock) pair. Tests can
    /// pin via <see cref="OverrideStorePathForTesting"/> to redirect into a
    /// temp directory.
    /// </summary>
    internal static string GetStorePath(string workspaceId, string? storageRoot, string mockId)
    {
        if (_testStorePathOverride is not null) return _testStorePathOverride;
        var safeMock = SanitiseMockId(mockId);
        // CodeQL cs/path-injection barrier — funnel workspaceId through
        // SanitiseWorkspaceId before it composes into the on-disk path, the
        // same shape the preset + recordings stores use.
        var safeWs = string.IsNullOrEmpty(workspaceId)
            ? string.Empty
            : SanitiseWorkspaceId(workspaceId);
        return BowireUserContext.GetWorkspacePath(
            safeWs,
            storageRoot,
            Path.Combine("mocks", safeMock + ".json"));
    }

    internal static void OverrideStorePathForTesting(string? path)
    {
        _testStorePathOverride = path;
    }

    private static readonly Lock FileLock = new();

    // A valid, empty MockConfiguration envelope — the shape callers get when
    // no config has been saved yet, so the UI/host always sees a well-formed
    // document.
    private static string EmptyEnvelope => new MockConfiguration().ToJson();

    /// <summary>
    /// Load the raw configuration document. Returns the default (empty)
    /// envelope when the file does not exist or is corrupt — never throws so
    /// the UI keeps working.
    /// </summary>
    public static string Load(string workspaceId, string? storageRoot, string mockId)
    {
        var path = GetStorePath(workspaceId, storageRoot, mockId);
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(path)) return EmptyEnvelope;
                var json = File.ReadAllText(path);
                using var _ = JsonDocument.Parse(json);
                return json;
            }
            catch
            {
                return EmptyEnvelope;
            }
        }
    }

    /// <summary>Typed view of <see cref="Load"/> for the CLI / mock host.</summary>
    public static MockConfiguration LoadConfig(string workspaceId, string? storageRoot, string mockId)
        => MockConfiguration.Parse(Load(workspaceId, storageRoot, mockId));

    /// <summary>
    /// Persist the configuration document, creating the parent directory on
    /// the way. Rejects anything that is not a parseable
    /// <see cref="MockConfiguration"/> so a corrupt PUT can't break the
    /// on-disk store.
    /// </summary>
    public static void Save(string workspaceId, string? storageRoot, string mockId, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("JSON payload required", nameof(json));

        // Validates JSON syntax + configuration shape; throws JsonException on either.
        _ = MockConfiguration.Parse(json);

        var path = GetStorePath(workspaceId, storageRoot, mockId);
        lock (FileLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
        }
    }

    private static string SanitiseMockId(string mockId)
    {
        if (string.IsNullOrWhiteSpace(mockId))
            throw new ArgumentException("Mock id is required", nameof(mockId));
        // The mock id becomes part of the filesystem path — strip everything
        // outside the safe character class, trim leading/trailing dots so
        // `..` can't escape upward, then assert via the anchored regex so
        // CodeQL drops the taint.
        var sb = new StringBuilder(mockId.Length);
        foreach (var c in mockId.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
        {
            sb.Append(c);
        }
        var result = sb.ToString().TrimStart('.').TrimEnd('.');
        if (string.IsNullOrEmpty(result) || !SafeIdPattern().IsMatch(result))
        {
            throw new ArgumentException(
                "Mock id must contain at least one ascii letter, digit, '-', '_' or '.': " + mockId,
                nameof(mockId));
        }
        return result;
    }

    private static string SanitiseWorkspaceId(string workspaceId)
    {
        // Mirrors PresetStore.SanitiseWorkspaceId / ChunkedRecordingStore —
        // strip to the safe class, trim edge dots, fall back to `anon`, then
        // assert via the anchored regex so CodeQL drops the taint.
        var sb = new StringBuilder(workspaceId.Length);
        foreach (var c in workspaceId.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
        {
            sb.Append(c);
        }
        var result = sb.ToString().TrimStart('.').TrimEnd('.');
        if (string.IsNullOrEmpty(result)) result = "anon";

        if (!SafeIdPattern().IsMatch(result))
        {
            throw new ArgumentException(
                "Sanitised workspace id failed the path-safety allow-list: " + workspaceId,
                nameof(workspaceId));
        }
        return result;
    }
}
