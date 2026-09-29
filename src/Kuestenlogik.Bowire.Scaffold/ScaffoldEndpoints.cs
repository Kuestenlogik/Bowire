// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Scaffold;

/// <summary>
/// Mounts the scaffold endpoints inside the workbench's auth-gated group
/// (#177), found by core's <see cref="IBowireEndpointContribution"/> scan.
/// </summary>
public sealed class BowireScaffoldEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapBowireScaffoldEndpoints(basePath);
    }
}

/// <summary>A sentence to read a spec from.</summary>
public sealed record ScaffoldParseRequest(
    [property: JsonPropertyName("intent")] string? Intent,
    [property: JsonPropertyName("protocol")] string? Protocol = null);

/// <summary>A spec to generate from.</summary>
public sealed record ScaffoldGenerateRequest(
    [property: JsonPropertyName("spec")] ScaffoldSpec? Spec);

/// <summary>Files to put into the workspace, as the operator left them after editing.</summary>
public sealed record ScaffoldWriteRequest(
    [property: JsonPropertyName("folder")] string? Folder,
    [property: JsonPropertyName("files")] IReadOnlyList<ScaffoldFile>? Files,
    [property: JsonPropertyName("overwrite")] bool Overwrite = false);

/// <summary>The scaffold HTTP surface.</summary>
public static class BowireScaffoldEndpoints
{
    /// <summary>Most files one write takes.</summary>
    public const int MaxFiles = 32;

    /// <summary>Largest file one write takes, in characters.</summary>
    public const int MaxFileChars = 512 * 1024;

    private static readonly string[] s_intentRequired = ["intent is required"];
    private static readonly string[] s_specRequired = ["spec is required"];
    private static readonly string[] s_leavesWorkspace = ["folder leaves the workspace"];

    private static readonly Regex s_safePath = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]*(/[A-Za-z0-9_][A-Za-z0-9_.-]*)*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// <c>POST {basePath}/api/scaffold/parse</c> — a spec from a sentence,
    /// without a model; <c>/generate</c> — the files for a spec;
    /// <c>/write</c> — the (edited) files into the workspace's
    /// <c>scaffold/&lt;folder&gt;/</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapBowireScaffoldEndpoints(this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{basePath}/api/scaffold/parse", (ScaffoldParseRequest? request) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Intent))
                return Results.BadRequest(new { errors = s_intentRequired });
            var (spec, notes) = ScaffoldIntentParser.Parse(request.Intent, request.Protocol);
            return Results.Ok(new { spec, notes, source = "parser" });
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/scaffold/generate", (ScaffoldGenerateRequest? request) =>
        {
            if (request?.Spec is null) return Results.BadRequest(new { errors = s_specRequired });
            var (spec, errors) = request.Spec.Normalize();
            if (spec is null) return Results.BadRequest(new { errors });
            return Results.Ok(new { spec, files = ScaffoldGenerator.Generate(spec) });
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/scaffold/write", (ScaffoldWriteRequest? request) =>
        {
            var (folder, errors) = ValidateWrite(request);
            if (folder is null) return Results.BadRequest(new { errors });

            var root = ScaffoldRoot();
            var target = Path.GetFullPath(Path.Combine(root, folder));
            if (!target.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { errors = s_leavesWorkspace });
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !request!.Overwrite)
                return Results.Conflict(new { errors = new[] { $"scaffold/{folder} already exists; pass overwrite to replace its files" }, folder = target });

            var written = new List<string>();
            foreach (var file in request!.Files!)
            {
                var path = Path.GetFullPath(Path.Combine(target, file.Path));
                if (!path.StartsWith(target, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
                written.Add(file.Path);
            }
            return Results.Ok(new { folder = target, written });
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>Why a write request is refused, or the folder it writes to.</summary>
    internal static (string? Folder, IReadOnlyList<string> Errors) ValidateWrite(ScaffoldWriteRequest? request)
    {
        var errors = new List<string>();
        var folder = request?.Folder?.Trim().Trim('/') ?? string.Empty;
        if (!s_safePath.IsMatch(folder) || folder.Contains('/', StringComparison.Ordinal))
            errors.Add("folder must be one plain name (letters, digits, . _ -)");
        var files = request?.Files ?? [];
        if (files.Count == 0) errors.Add("no files");
        if (files.Count > MaxFiles) errors.Add($"at most {MaxFiles} files");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (f is null || !s_safePath.IsMatch(f.Path ?? string.Empty) || f.Path!.Split('/').Any(p => p is "." or ".."))
            {
                errors.Add($"'{f?.Path}' is not a relative path inside the folder");
                continue;
            }
            if (!seen.Add(f.Path)) errors.Add($"'{f.Path}' appears twice");
            if ((f.Content ?? string.Empty).Length > MaxFileChars) errors.Add($"'{f.Path}' is larger than {MaxFileChars} characters");
        }
        return errors.Count > 0 ? (null, errors) : (folder, errors);
    }

    /// <summary>
    /// <c>scaffold/</c> in the workspace being served, or in the identity's
    /// own slot when the call names no workspace — the same resolution
    /// schema uploads use.
    /// </summary>
    internal static string ScaffoldRoot() =>
        BowirePluginSettingsScope.Current is { } scope
            ? BowireUserContext.GetWorkspacePath(scope.WorkspaceId, scope.StorageRoot, "scaffold")
            : BowireUserContext.GetUserPath("scaffold");
}
