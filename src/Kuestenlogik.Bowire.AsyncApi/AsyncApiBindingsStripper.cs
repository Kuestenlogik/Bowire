// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using YamlDotNet.RepresentationModel;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// Companion to <see cref="AsyncApiBindingsExtractor"/> that produces
/// a binding-less copy of the document for the SDK reader to chew on.
/// The Neuroglia reader crashes on typed binding scalars
/// (asyncapi/net-sdk#76, the second prong — <c>bindings.mqtt.qos: 2</c>
/// blows up its <c>StringEnumDeserializer</c>), so we feed it a stream
/// with every <c>bindings:</c> key removed. The extractor still sees
/// the original YAML on its own side-path; together they recover the
/// full document semantics without touching the upstream SDK.
///
/// Strip semantics:
/// <list type="bullet">
///   <item>Walks the AsyncAPI objects that carry bindings — servers,
///     channels, operations (V3 and V2 publish/subscribe), messages
///     (incl. V2 <c>oneOf</c>), operation and message traits, and
///     their <c>components</c> counterparts — and drops their
///     <c>bindings</c> field. Schemas, examples and map keys named
///     <c>bindings</c> are the author's data and stay.</item>
///   <item>Operates on the YamlDotNet representation model so
///     comments, anchors, and key ordering survive everywhere
///     <em>except</em> inside the removed branches.</item>
///   <item>If parsing fails the original string comes back unchanged
///     — discovery surfaces the parse error elsewhere; the stripper
///     just contributes "no transformation" on bad input.</item>
/// </list>
/// </summary>
internal static class AsyncApiBindingsStripper
{
    public static string Strip(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml;

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return yaml;
        }

        if (stream.Documents.Count == 0) return yaml;

        var removed = false;
        foreach (var root in stream.Documents
            .Select(d => d.RootNode)
            .OfType<YamlMappingNode>())
        {
            if (StripDocument(root)) removed = true;
        }

        if (!removed) return yaml;

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    // Only the AsyncAPI objects that carry a `bindings` field are
    // walked (#910). Recursing into every mapping also deleted schema
    // properties, example values and channels / operations whose key
    // happens to be `bindings` — the author's data, not a binding.
    private static bool StripDocument(YamlMappingNode root)
    {
        var removed = false;
        removed |= ForEachEntry(root, "servers", RemoveBindings);
        removed |= ForEachEntry(root, "channels", StripChannel);
        removed |= ForEachEntry(root, "operations", StripOperation);
        if (TryGetMapping(root, "components", out var components))
        {
            removed |= ForEachEntry(components, "servers", RemoveBindings);
            removed |= ForEachEntry(components, "channels", StripChannel);
            removed |= ForEachEntry(components, "operations", StripOperation);
            removed |= ForEachEntry(components, "messages", StripMessage);
            removed |= ForEachEntry(components, "operationTraits", RemoveBindings);
            removed |= ForEachEntry(components, "messageTraits", RemoveBindings);
        }
        return removed;
    }

    // V3 channels list their messages; V2 channels inline the
    // publish / subscribe operations.
    private static bool StripChannel(YamlMappingNode channel)
    {
        var removed = RemoveBindings(channel);
        removed |= ForEachEntry(channel, "messages", StripMessage);
        if (TryGetMapping(channel, "publish", out var publish)) removed |= StripOperation(publish);
        if (TryGetMapping(channel, "subscribe", out var subscribe)) removed |= StripOperation(subscribe);
        return removed;
    }

    // V3 operations reference their messages by $ref; V2 operations
    // carry an inline `message` (a single one or a `oneOf` list).
    private static bool StripOperation(YamlMappingNode operation)
    {
        var removed = RemoveBindings(operation);
        removed |= ForEachItem(operation, "traits", RemoveBindings);
        if (TryGetMapping(operation, "message", out var message)) removed |= StripMessage(message);
        return removed;
    }

    private static bool StripMessage(YamlMappingNode message)
    {
        var removed = RemoveBindings(message);
        removed |= ForEachItem(message, "traits", RemoveBindings);
        removed |= ForEachItem(message, "oneOf", StripMessage);
        return removed;
    }

    private static bool RemoveBindings(YamlMappingNode node)
    {
        // Collect first — removing while enumerating would throw.
        var keys = node.Children.Keys
            .OfType<YamlScalarNode>()
            .Where(k => string.Equals(k.Value, "bindings", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys) node.Children.Remove(key);
        return keys.Count > 0;
    }

    // Applies `strip` to every value of the named map. The map's keys
    // are the author's names (channel ids, message ids, …) and are
    // never stripped themselves.
    private static bool ForEachEntry(YamlMappingNode parent, string key, Func<YamlMappingNode, bool> strip)
    {
        if (!TryGetMapping(parent, key, out var map)) return false;
        var removed = false;
        foreach (var entry in map.Children.Values.OfType<YamlMappingNode>())
        {
            if (strip(entry)) removed = true;
        }
        return removed;
    }

    private static bool ForEachItem(YamlMappingNode parent, string key, Func<YamlMappingNode, bool> strip)
    {
        if (TryGetChild(parent, key) is not YamlSequenceNode sequence) return false;
        var removed = false;
        foreach (var item in sequence.Children.OfType<YamlMappingNode>())
        {
            if (strip(item)) removed = true;
        }
        return removed;
    }

    private static bool TryGetMapping(
        YamlMappingNode parent, string key, [NotNullWhen(true)] out YamlMappingNode? mapping)
    {
        mapping = TryGetChild(parent, key) as YamlMappingNode;
        return mapping is not null;
    }

    // Key lookup ignores case, matching the extractor and the removal
    // rule above.
    private static YamlNode? TryGetChild(YamlMappingNode parent, string key)
    {
        foreach (var (k, v) in parent.Children)
        {
            if (k is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return null;
    }
}
