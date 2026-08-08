// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Projects;

/// <summary>
/// The checked-in <c>.bowire/project.json</c> convention (#172): a
/// version-controlled MANIFEST that makes a repository the source of truth for
/// "what this repo's API looks like". It does not carry the artifacts itself —
/// it POINTS at them (schemas, collection/suite files, an auth flow, a rules
/// file) with project-relative paths, so CI, the PR bot, and onboarding all
/// resolve the same setup automatically instead of re-configuring per job.
/// </summary>
/// <remarks>
/// Parsing mirrors <see cref="Kuestenlogik.Bowire.Mocking.AuthRecording"/>:
/// <see cref="Parse"/> throws <see cref="JsonException"/> on malformed JSON or
/// an unknown field, and <see cref="ArgumentException"/> on a shape violation
/// (a missing <c>version</c>). Softer, actionable checks — an unsupported
/// version, an absolute path where a project-relative one is expected, an empty
/// source URL — are surfaced by <see cref="Validate"/> as a list of messages so
/// a caller can report every problem at once rather than one-throw-at-a-time.
/// No network access: the hosted <c>$schema</c> is never fetched.
/// </remarks>
public sealed class BowireProjectFile
{
    /// <summary>The current (and only) supported manifest version.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Optional JSON-Schema hint (<c>https://bowire.io/schema/project.v1.json</c>). Never fetched.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Manifest schema version. Required; must be <see cref="SupportedVersion"/>.</summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>Human project name (e.g. <c>order-service</c>). Optional.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The API sources this project exposes — each a base URL plus zero or more schema files.</summary>
    [JsonPropertyName("sources")]
    public List<BowireProjectSource> Sources { get; init; } = [];

    /// <summary>Named test suites/collections, keyed by a short id (e.g. <c>smoke</c>), each a project-relative path.</summary>
    [JsonPropertyName("suites")]
    public Dictionary<string, string> Suites { get; init; } = [];

    /// <summary>Security configuration — the auth flow to run and the scan profiles to apply.</summary>
    [JsonPropertyName("security")]
    public BowireProjectSecurity? Security { get; set; }

    /// <summary>Project-relative path to a rules file. Optional.</summary>
    [JsonPropertyName("rules")]
    public string? Rules { get; set; }

    private static readonly JsonSerializerOptions SerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Unknown top-level / nested fields are a manifest authoring mistake, not
        // silently-tolerated noise: surface them as a JsonException so a typo like
        // "suite" instead of "suites" is caught at load, matching the "known
        // fields" contract in #172.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>
    /// Parse a manifest document. Throws <see cref="JsonException"/> when the
    /// payload is not a JSON object of the expected shape (malformed JSON, a
    /// type mismatch, or an unknown field), and <see cref="ArgumentException"/>
    /// on a shape violation the serializer can't express — a missing
    /// <c>version</c>. Softer validity questions belong to <see cref="Validate"/>.
    /// </summary>
    public static BowireProjectFile Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("The project file is empty — a .bowire/project.json must at least declare \"version\": 1.", nameof(json));

        var parsed = JsonSerializer.Deserialize<BowireProjectFile>(json, SerOptions)
            ?? throw new JsonException("The project file did not deserialize to an object.");

        if (parsed.Version is null)
            throw new ArgumentException("The project file is missing the required \"version\" field (expected \"version\": 1).", nameof(json));

        return parsed;
    }

    /// <summary>Serialize to the canonical on-disk JSON shape.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerOptions);

    /// <summary>
    /// Run the softer, non-fatal schema checks and return one actionable
    /// message per problem (empty when the manifest is valid). Each message
    /// names the offending field and what's wrong. Called after a successful
    /// <see cref="Parse"/>, so <c>version</c> is guaranteed non-null here.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Version != SupportedVersion)
            errors.Add($"version: unsupported manifest version {Version} — this build understands version {SupportedVersion}.");

        for (var i = 0; i < Sources.Count; i++)
        {
            var source = Sources[i];
            if (string.IsNullOrWhiteSpace(source.Url))
                errors.Add($"sources[{i}].url: a source must declare a non-empty url.");
            for (var j = 0; j < source.Schemas.Count; j++)
                CheckRelativePath($"sources[{i}].schemas[{j}]", source.Schemas[j], errors);
        }

        foreach (var (key, path) in Suites)
            CheckRelativePath($"suites.{key}", path, errors);

        if (Security is not null)
            CheckRelativePath("security.auth", Security.Auth, errors);

        CheckRelativePath("rules", Rules, errors);

        return errors;
    }

    /// <summary>
    /// Flag a path that isn't project-relative. A manifest is shared across
    /// machines via git, so an absolute / rooted path (or a Windows drive path)
    /// can't resolve on another checkout — it must be relative to the project
    /// root. Null / empty is not an error here (the field is optional); only a
    /// present-but-non-relative value is.
    /// </summary>
    private static void CheckRelativePath(string field, string? path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\'))
            errors.Add($"{field}: expected a project-relative path, got the rooted path '{path}'.");
    }
}

/// <summary>An API source the project exposes: a base URL and its schema files.</summary>
public sealed class BowireProjectSource
{
    /// <summary>Base URL of the service (e.g. <c>https://api.example.com</c>).</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Project-relative schema files describing the source (e.g. <c>./proto/orders.proto</c>).</summary>
    [JsonPropertyName("schemas")]
    public List<string> Schemas { get; init; } = [];
}

/// <summary>The <c>security</c> block: the auth flow to run and the scan profiles to apply.</summary>
public sealed class BowireProjectSecurity
{
    /// <summary>Project-relative path to an auth-flow definition (e.g. <c>./bowire/auth/login.flow.json</c>).</summary>
    [JsonPropertyName("auth")]
    public string? Auth { get; set; }

    /// <summary>Scan profiles to run (e.g. <c>owasp-api</c>, <c>cve</c>, <c>protocol</c>).</summary>
    [JsonPropertyName("scan")]
    public List<string> Scan { get; init; } = [];
}
