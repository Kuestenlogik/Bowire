// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

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
///   <item>Walks the entire YAML mapping tree (channels, operations,
///     servers, messages, components) and drops any child node named
///     <c>bindings</c>. Independent of nesting depth — covers
///     channel-level, operation-level, message-level, server-level
///     bindings in one pass.</item>
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
            if (StripBindingsRecursive(root)) removed = true;
        }

        if (!removed) return yaml;

        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static bool StripBindingsRecursive(YamlMappingNode node)
    {
        // First collect the keys we're going to remove — modifying
        // the dictionary during enumeration would throw.
        var toRemove = new List<YamlNode>();
        foreach (var (keyNode, _) in node.Children)
        {
            if (keyNode is YamlScalarNode scalar
                && string.Equals(scalar.Value, "bindings", StringComparison.OrdinalIgnoreCase))
            {
                toRemove.Add(keyNode);
            }
        }

        var removedAny = false;
        foreach (var key in toRemove)
        {
            node.Children.Remove(key);
            removedAny = true;
        }

        // Recurse into every surviving child so nested `bindings:`
        // blocks (channel-level, server-level, message-level) get
        // caught too.
        foreach (var child in node.Children.Values)
        {
            switch (child)
            {
                case YamlMappingNode mapping:
                    if (StripBindingsRecursive(mapping)) removedAny = true;
                    break;
                case YamlSequenceNode sequence:
                    foreach (var itemMapping in sequence.Children.OfType<YamlMappingNode>())
                    {
                        if (StripBindingsRecursive(itemMapping)) removedAny = true;
                    }
                    break;
            }
        }

        return removedAny;
    }
}
