// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// A Bruno collection in the Bru format (#674): a folder with
/// <c>bruno.json</c>, an optional <c>collection.bru</c>, one <c>.bru</c> file
/// per request, <c>folder.bru</c> per folder and <c>environments/*.bru</c>.
/// </summary>
internal sealed class BrunoImporter : IInteropImporter
{
    private static readonly HashSet<string> s_methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "options", "head", "trace", "connect",
    };

    public string Id => "bruno";

    public bool CanRead(IReadOnlyList<SourceFile> files) =>
        files.Any(f => f.Path.EndsWith(".bru", StringComparison.OrdinalIgnoreCase) || FileName(f.Path).Equals("bruno.json", StringComparison.OrdinalIgnoreCase));

    public ImportCollectionResponse Read(IReadOnlyList<SourceFile> files, string? name, ReportBuilder report)
    {
        var source = new BrunoCollection();
        var config = files.Where(f => FileName(f.Path).Equals("bruno.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path.Length).FirstOrDefault();
        if (config is not null) source.Name = CollectionName(config) ?? source.Name;

        // Paths relative to the folder that holds bruno.json, so picking its parent works too.
        var root = config is null ? "" : Directory(config.Path);
        files = files
            .Where(f => root.Length == 0 || f.Path.Replace('\\', '/').StartsWith(root + "/", StringComparison.Ordinal))
            .Select(f => root.Length == 0 ? f : new SourceFile { Path = f.Path.Replace('\\', '/')[(root.Length + 1)..], Content = f.Content })
            .ToList();

        // Folder display names come from folder.bru when it has one.
        var folderNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => FileName(f.Path).Equals("folder.bru", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = Directory(file.Path);
            var blocks = BruFile.Parse(file.Content);
            folderNames[dir] = blocks.FirstOrDefault(b => b.Name == "meta")?.Get("name") ?? LastSegment(dir);
        }

        var requests = new List<(string Dir, double Seq, BrunoRequest Request)>();
        foreach (var file in files.Where(f => f.Path.EndsWith(".bru", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = FileName(file.Path);
            var dir = Directory(file.Path);
            var blocks = BruFile.Parse(file.Content);

            if (dir.Equals("environments", StringComparison.OrdinalIgnoreCase))
            {
                source.Environments.Add(ReadEnvironment(Path.GetFileNameWithoutExtension(fileName), blocks));
            }
            else if (fileName.Equals("collection.bru", StringComparison.OrdinalIgnoreCase) && dir.Length == 0)
            {
                ReadDefaults(blocks, source.Root);
            }
            else if (fileName.Equals("folder.bru", StringComparison.OrdinalIgnoreCase))
            {
                var defaults = new BrunoDefaults();
                ReadDefaults(blocks, defaults);
                source.Folders[string.Join("/", DisplayPath(dir, folderNames))] = defaults;
            }
            else if (ReadRequest(blocks, DisplayPath(dir, folderNames), Path.GetFileNameWithoutExtension(fileName), out var seq) is { } request)
            {
                requests.Add((dir, seq, request));
            }
            else
            {
                report.Skipped(file.Path, "no request block (get, post, …) in this .bru file");
            }
        }

        foreach (var (_, _, request) in requests.OrderBy(r => r.Dir, StringComparer.Ordinal).ThenBy(r => r.Seq))
            source.Requests.Add(request);
        foreach (var other in files.Where(f => FileName(f.Path).Equals(".env", StringComparison.OrdinalIgnoreCase)))
            report.Skipped(other.Path, "the .env file is not imported: its values are secrets; move them into Bowire secrets");

        var response = new ImportCollectionResponse { Format = Id };
        BrunoMapper.Emit(source, name, response, report);
        return response;
    }

    private static BrunoRequest? ReadRequest(List<BruBlock> blocks, List<string> folders, string fallbackName, out double seq)
    {
        seq = 0;
        var verb = blocks.FirstOrDefault(b => s_methods.Contains(b.Name));
        var meta = blocks.FirstOrDefault(b => b.Name == "meta");
        var type = meta?.Get("type") ?? "http";
        if (verb is null && type is not ("grpc" or "ws" or "websocket")) return null;
        _ = double.TryParse(meta?.Get("seq"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seq);

        var request = new BrunoRequest
        {
            Name = meta?.Get("name") ?? fallbackName,
            Folders = folders,
            Kind = type,
            Method = verb?.Name.ToUpperInvariant() ?? "GET",
            Url = verb?.Get("url") ?? "",
        };
        if (verb is null) return request;

        request.Headers.AddRange(Block(blocks, "headers")?.Pairs ?? []);
        request.Query.AddRange(Block(blocks, "params:query")?.Pairs ?? []);
        request.PathParams.AddRange(Block(blocks, "params:path")?.Pairs ?? []);

        var bodyMode = verb.Get("body") ?? (blocks.Any(b => b.Name is "body" or "body:json") ? "json" : "none");
        request.BodyType = bodyMode switch
        {
            "formUrlEncoded" => "form-urlencoded",
            "multipartForm" => "multipart-form",
            _ => bodyMode,
        };
        switch (request.BodyType)
        {
            case "form-urlencoded":
                request.Form.AddRange(Block(blocks, "body:form-urlencoded")?.Pairs ?? []);
                break;
            case "graphql":
                request.BodyText = Block(blocks, "body:graphql")?.Text;
                request.GraphQlVariables = Block(blocks, "body:graphql:vars")?.Text;
                break;
            case "json":
                request.BodyText = (Block(blocks, "body:json") ?? Block(blocks, "body"))?.Text;
                break;
            default:
                request.BodyText = Block(blocks, "body:" + request.BodyType)?.Text;
                break;
        }

        var authMode = verb.Get("auth") ?? "none";
        request.Auth = ReadAuth(authMode, blocks);

        if (Block(blocks, "script:pre-request") is { Text.Length: > 0 }) request.LeftOut.Add("pre-request script: Bowire scripts use their own ctx.* API; port it by hand");
        if (Block(blocks, "script:post-response") is { Text.Length: > 0 }) request.LeftOut.Add("post-response script: write it as Bowire assertions or a post-script");
        if (Block(blocks, "tests") is { Text.Length: > 0 }) request.LeftOut.Add("tests: write them as Bowire assertions");
        if (Block(blocks, "assert") is { Pairs.Count: > 0 }) request.LeftOut.Add("assertions: Bowire's assertions use their own operators; re-create them on the item");
        if (Block(blocks, "vars:pre-request") is { Pairs.Count: > 0 }) request.LeftOut.Add("request variables: Bowire has no per-request variables; put them in the environment");
        if (Block(blocks, "vars:post-response") is { Pairs.Count: > 0 }) request.LeftOut.Add("post-response variables: use {{response.path}} chaining in Bowire");
        return request;
    }

    private static void ReadDefaults(List<BruBlock> blocks, BrunoDefaults defaults)
    {
        defaults.Headers.AddRange(Block(blocks, "headers")?.Pairs ?? []);
        defaults.Variables.AddRange(Block(blocks, "vars:pre-request")?.Pairs ?? []);
        if (Block(blocks, "auth")?.Get("mode") is { } mode) defaults.Auth = ReadAuth(mode, blocks);
        defaults.HasScripts = blocks.Any(b => b.Name.StartsWith("script:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(b.Text));
    }

    private static BrunoAuth ReadAuth(string mode, List<BruBlock> blocks)
    {
        var auth = new BrunoAuth { Type = mode };
        foreach (var pair in Block(blocks, "auth:" + mode)?.Pairs.Where(p => !p.Disabled) ?? []) auth.Fields[pair.Name] = pair.Value;
        return auth;
    }

    private static BrunoEnvironment ReadEnvironment(string name, List<BruBlock> blocks)
    {
        var env = new BrunoEnvironment { Name = name };
        env.Variables.AddRange(Block(blocks, "vars")?.Pairs ?? []);
        env.SecretNames.AddRange(Block(blocks, "vars:secret")?.Items.Where(i => !i.Disabled).Select(i => i.Name) ?? []);
        return env;
    }

    private static BruBlock? Block(List<BruBlock> blocks, string name) => blocks.FirstOrDefault(b => b.Name == name);

    private static string? CollectionName(SourceFile config)
    {
        try
        {
            using var doc = JsonDocument.Parse(config.Content);
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (JsonException ex)
        {
            throw new InteropFormatException("bruno.json is not valid JSON: " + ex.Message, ex);
        }
    }

    private static List<string> DisplayPath(string dir, Dictionary<string, string> folderNames)
    {
        var result = new List<string>();
        if (dir.Length == 0) return result;
        var parts = dir.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            var sub = string.Join("/", parts.Take(i));
            result.Add(folderNames.GetValueOrDefault(sub) ?? parts[i - 1]);
        }
        return result;
    }

    private static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static string Directory(string path)
    {
        var normal = path.Replace('\\', '/');
        var slash = normal.LastIndexOf('/');
        return slash < 0 ? "" : normal[..slash];
    }

    private static string LastSegment(string dir) => dir[(dir.LastIndexOf('/') + 1)..];
}
