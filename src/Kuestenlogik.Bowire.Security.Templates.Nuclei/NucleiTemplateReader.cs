// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using YamlDotNet.RepresentationModel;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei;

/// <summary>
/// Reads a Nuclei YAML template off disk (or any text source) and
/// fills a <see cref="NucleiTemplate"/>. Uses YamlDotNet's
/// representation-model API directly rather than a typed
/// deserialiser — Nuclei's schema is loose (fields polymorphic across
/// templates, optional sections everywhere), so walking the node
/// tree is more robust against the corpus's actual shape than
/// declaring strict POCOs the deserialiser must hit exactly.
///
/// Read methods are static + side-effect-free. Errors from
/// malformed YAML surface as <see cref="YamlDotNet.Core.YamlException"/>;
/// callers usually catch and report-then-skip the file so a single
/// broken template doesn't kill a 8000-file corpus walk.
/// </summary>
public static class NucleiTemplateReader
{
    /// <summary>Read + parse a Nuclei template file.</summary>
    public static NucleiTemplate ReadFile(string path)
    {
        return ReadText(File.ReadAllText(path));
    }

    /// <summary>Parse a Nuclei template from a raw YAML string.</summary>
    public static NucleiTemplate ReadText(string yaml)
    {
        var template = new NucleiTemplate();
        if (string.IsNullOrWhiteSpace(yaml)) return template;

        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);

        if (stream.Documents.Count == 0) return template;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return template;

        template.Id = GetScalar(root, "id") ?? "";

        if (TryGetMapping(root, "info", out var infoNode))
        {
            template.Info.Name = GetScalar(infoNode!, "name") ?? "";
            template.Info.Author = GetScalar(infoNode!, "author") ?? "";
            template.Info.Severity = GetScalar(infoNode!, "severity") ?? "";
            template.Info.Description = GetScalar(infoNode!, "description") ?? "";
            CollectStringList(infoNode!, "reference", template.Info.Reference);
            CollectStringList(infoNode!, "tags", template.Info.Tags);
        }

        if (TryGetSequence(root, "http", out var httpSeq))
        {
            foreach (var entry in httpSeq!.Children)
            {
                if (entry is not YamlMappingNode httpMapping) continue;
                template.Http.Add(ReadHttpRequest(httpMapping));
            }
        }

        return template;
    }

    private static NucleiHttpRequest ReadHttpRequest(YamlMappingNode mapping)
    {
        var req = new NucleiHttpRequest
        {
            Method = (GetScalar(mapping, "method") ?? "GET").ToUpperInvariant(),
            Body = GetScalar(mapping, "body") ?? "",
            MatchersCondition = GetScalar(mapping, "matchers-condition") ?? "or",
        };
        CollectStringList(mapping, "path", req.Path);

        if (TryGetSequence(mapping, "matchers", out var matchersSeq))
        {
            foreach (var entry in matchersSeq!.Children)
            {
                if (entry is not YamlMappingNode matcherMapping) continue;
                req.Matchers.Add(ReadMatcher(matcherMapping));
            }
        }

        // payloads: maps each variable name to a list of values.
        // Nuclei templates use these as {{varName}} placeholders the
        // converter expands into one recording per cross-product entry.
        if (TryGetMapping(mapping, "payloads", out var payloadsNode))
        {
            foreach (var (varKeyNode, varValue) in payloadsNode!.Children)
            {
                if (varKeyNode is not YamlScalarNode varKey) continue;
                if (varValue is not YamlSequenceNode valuesSeq) continue;
                var values = new List<string>();
                foreach (var v in valuesSeq.Children)
                {
                    if (v is YamlScalarNode scalar && scalar.Value is not null)
                    {
                        values.Add(scalar.Value);
                    }
                }
                if (values.Count > 0) req.Payloads[varKey.Value ?? ""] = values;
            }
        }
        return req;
    }

    private static NucleiMatcher ReadMatcher(YamlMappingNode mapping)
    {
        var matcher = new NucleiMatcher
        {
            Type = GetScalar(mapping, "type") ?? "",
            Condition = GetScalar(mapping, "condition") ?? "or",
            Part = GetScalar(mapping, "part") ?? "body",
            Negative = string.Equals(GetScalar(mapping, "negative"), "true", StringComparison.OrdinalIgnoreCase),
        };

        // `status:` is a list of integers.
        if (TryGetSequence(mapping, "status", out var statusSeq))
        {
            foreach (var s in statusSeq!.Children)
            {
                if (s is YamlScalarNode scalar
                    && int.TryParse(scalar.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var code))
                {
                    matcher.Status.Add(code);
                }
            }
        }

        CollectStringList(mapping, "words", matcher.Words);
        CollectStringList(mapping, "regex", matcher.Regex);

        return matcher;
    }

    // ------------- helper lookups -------------

    private static string? GetScalar(YamlMappingNode parent, string key)
    {
        foreach (var (k, v) in parent.Children)
        {
            if (k is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase)
                && v is YamlScalarNode value)
            {
                return value.Value;
            }
        }
        return null;
    }

    private static bool TryGetMapping(YamlMappingNode parent, string key, out YamlMappingNode? value)
    {
        foreach (var (k, v) in parent.Children)
        {
            if (k is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase)
                && v is YamlMappingNode mapping)
            {
                value = mapping;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static bool TryGetSequence(YamlMappingNode parent, string key, out YamlSequenceNode? value)
    {
        foreach (var (k, v) in parent.Children)
        {
            if (k is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase)
                && v is YamlSequenceNode sequence)
            {
                value = sequence;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static void CollectStringList(YamlMappingNode parent, string key, List<string> sink)
    {
        if (!TryGetSequence(parent, key, out var seq)) return;
        foreach (var item in seq!.Children)
        {
            if (item is YamlScalarNode scalar && scalar.Value is not null)
            {
                sink.Add(scalar.Value);
            }
        }
    }
}
