// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>
/// JSON or YAML text as one <see cref="JsonNode"/> tree, so an importer reads
/// both spellings of a format (Insomnia exports either; OpenCollection is
/// YAML) with the same code.
/// </summary>
internal static class StructuredDocument
{
    /// <summary>The document, or <c>null</c> when it is neither JSON nor YAML with a mapping at its root.</summary>
    public static JsonObject? TryLoad(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                return JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
            return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode map
                ? (JsonObject)Convert(map)!
                : null;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    // Scalars stay strings: the formats read here carry text, and a value
    // like "007" or "1.10" must not turn into a number on the way.
    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(c =>
            KeyValuePair.Create(((YamlScalarNode)c.Key).Value ?? "", Convert(c.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar when scalar.Value is null || (scalar.Style == ScalarStyle.Plain && scalar.Value is "~" or "null") => null,
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => null,
    };

    /// <summary>A node's text, whatever JSON type it has; <c>null</c> for objects and arrays.</summary>
    public static string? Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonValue value => value.ToJsonString(),
        _ => null,
    };

    /// <summary>Whether a node says true: JSON <c>true</c> or the YAML string "true".</summary>
    public static bool IsTrue(JsonNode? node) =>
        node is JsonValue value
        && ((value.TryGetValue<bool>(out var b) && b) || (value.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase)));
}
