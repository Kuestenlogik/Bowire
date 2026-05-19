// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using YamlDotNet.RepresentationModel;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// Side-path around the Neuroglia SDK reader: it crashes on
/// <c>bindings.mqtt.qos: 2</c> and other enum-typed binding scalars
/// (asyncapi/net-sdk#76), so we walk the document ourselves with
/// YamlDotNet's representation-model API to pull out the binding
/// fields each operation declares. Returns a flat string-keyed map per
/// operation so resolvers can pick up the keys they care about
/// without taking a hard dependency on the typed binding model.
///
/// Only the fields we currently need are extracted — fully-typed
/// bindings (LastWill, AMQP exchanges, Kafka schema-registry refs)
/// stay as raw strings until a resolver asks for them. The walker
/// gracefully skips malformed nodes (anchor / alias / non-scalar
/// values) so a partially-broken document still yields what it can.
/// </summary>
internal static class AsyncApiBindingsExtractor
{
    /// <summary>
    /// Parse <paramref name="yaml"/> and pull every
    /// <c>operations.&lt;opKey&gt;.bindings.&lt;bindingId&gt;.&lt;field&gt;</c>
    /// scalar into a per-operation map.
    ///
    /// Result shape: <c>opKey → bindingId → field → value</c>. All
    /// values are coerced to their YAML scalar string form (e.g.
    /// integer <c>2</c> arrives as the string <c>"2"</c>) so consumers
    /// can deal with mixed types via simple parsing.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
        ExtractV3OperationBindings(string yaml)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(
            StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(yaml)) return result;

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Document doesn't parse as YAML — discovery elsewhere
            // already surfaces the error; we just contribute no
            // binding metadata.
            return result;
        }

        if (stream.Documents.Count == 0) return result;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return result;

        if (!TryGetMapping(root, "operations", out var operationsNode)) return result;

        foreach (var (opKeyNode, opValue) in operationsNode!.Children)
        {
            if (opKeyNode is not YamlScalarNode opKey) continue;
            if (opValue is not YamlMappingNode opMapping) continue;
            if (!TryGetMapping(opMapping, "bindings", out var bindingsNode)) continue;

            var bindingsByKey = new Dictionary<string, IReadOnlyDictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (bindingKeyNode, bindingValue) in bindingsNode!.Children)
            {
                if (bindingKeyNode is not YamlScalarNode bindingKey) continue;
                if (bindingValue is not YamlMappingNode bindingMapping) continue;
                bindingsByKey[bindingKey.Value ?? string.Empty] = FlattenScalarFields(bindingMapping);
            }

            if (bindingsByKey.Count > 0)
            {
                result[opKey.Value ?? string.Empty] = bindingsByKey;
            }
        }

        return result;
    }

    /// <summary>
    /// Pull every <c>operations.&lt;opKey&gt;.messages[].$ref</c> entry
    /// out of the document and return the trailing message-name segment
    /// (the bit after the last <c>/</c> in <c>#/components/messages/foo</c>
    /// or <c>#/channels/x/messages/foo</c>). Empty list for operations
    /// that don't declare a messages array, single-entry list when
    /// they declare one. <see cref="BowireAsyncApiProtocol"/> uses
    /// this to decide whether an operation should produce a single
    /// method or one overload per declared message.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ExtractV3OperationMessages(string yaml)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(yaml)) return result;

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException) { return result; }

        if (stream.Documents.Count == 0) return result;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return result;

        if (!TryGetMapping(root, "operations", out var operationsNode)) return result;

        foreach (var (opKeyNode, opValue) in operationsNode!.Children)
        {
            if (opKeyNode is not YamlScalarNode opKey) continue;
            if (opValue is not YamlMappingNode opMapping) continue;

            // Find the `messages:` slot on the operation. AsyncAPI 3
            // models it as a sequence of $ref objects; some authors
            // also write a single inline object as a shortcut.
            YamlNode? messagesNode = null;
            foreach (var (k, v) in opMapping.Children)
            {
                if (k is YamlScalarNode s
                    && string.Equals(s.Value, "messages", StringComparison.OrdinalIgnoreCase))
                {
                    messagesNode = v;
                    break;
                }
            }
            if (messagesNode is not YamlSequenceNode sequence) continue;

            var names = new List<string>();
            foreach (var item in sequence.Children)
            {
                if (item is not YamlMappingNode itemMapping) continue;
                foreach (var (k, v) in itemMapping.Children)
                {
                    if (k is YamlScalarNode keyScalar
                        && string.Equals(keyScalar.Value, "$ref", StringComparison.Ordinal)
                        && v is YamlScalarNode refScalar
                        && !string.IsNullOrWhiteSpace(refScalar.Value))
                    {
                        names.Add(LastPathSegment(refScalar.Value));
                        break;
                    }
                }
            }

            if (names.Count > 0) result[opKey.Value ?? string.Empty] = names;
        }

        return result;
    }

    /// <summary>
    /// V2 bindings live inline under <c>channels[].publish.bindings</c>
    /// and <c>channels[].subscribe.bindings</c> instead of V3's
    /// separate top-level operations block. Return shape:
    /// <c>channelKey → slot ("publish"|"subscribe") → bindingId → field → value</c>.
    /// </summary>
    public static IReadOnlyDictionary<string,
        IReadOnlyDictionary<string,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string, string>>>>
        ExtractV2ChannelBindings(string yaml)
    {
        var result = new Dictionary<string,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string, string>>>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(yaml)) return result;

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException) { return result; }

        if (stream.Documents.Count == 0) return result;
        if (stream.Documents[0].RootNode is not YamlMappingNode root) return result;
        if (!TryGetMapping(root, "channels", out var channelsNode)) return result;

        foreach (var (channelKeyNode, channelValue) in channelsNode!.Children)
        {
            if (channelKeyNode is not YamlScalarNode channelKey) continue;
            if (channelValue is not YamlMappingNode channelMapping) continue;

            var perSlot = new Dictionary<string,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var slot in new[] { "publish", "subscribe" })
            {
                if (!TryGetMapping(channelMapping, slot, out var slotMapping)) continue;
                if (!TryGetMapping(slotMapping!, "bindings", out var bindingsNode)) continue;

                var bindingsByKey = new Dictionary<string, IReadOnlyDictionary<string, string>>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (bindingKeyNode, bindingValue) in bindingsNode!.Children)
                {
                    if (bindingKeyNode is not YamlScalarNode bindingKey) continue;
                    if (bindingValue is not YamlMappingNode bindingMapping) continue;
                    bindingsByKey[bindingKey.Value ?? string.Empty] = FlattenScalarFields(bindingMapping);
                }
                if (bindingsByKey.Count > 0) perSlot[slot] = bindingsByKey;
            }

            if (perSlot.Count > 0) result[channelKey.Value ?? string.Empty] = perSlot;
        }

        return result;
    }

    private static string LastPathSegment(string reference)
    {
        var lastSlash = reference.LastIndexOf('/');
        return lastSlash < 0 ? reference : reference[(lastSlash + 1)..];
    }

    /// <summary>
    /// Collect every scalar-valued child of <paramref name="mapping"/>
    /// into a flat string map. Nested structures (e.g. <c>lastWill</c>
    /// inside an MQTT server binding) get serialised back to YAML so
    /// resolvers can re-parse them if they care; today's MQTT
    /// resolver only needs the flat fields (qos / retain) so this
    /// covers the realistic surface.
    /// </summary>
    private static Dictionary<string, string> FlattenScalarFields(YamlMappingNode mapping)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fieldKeyNode, fieldValue) in mapping.Children)
        {
            if (fieldKeyNode is not YamlScalarNode fieldKey) continue;
            if (fieldValue is YamlScalarNode scalar)
            {
                fields[fieldKey.Value ?? string.Empty] = scalar.Value ?? string.Empty;
            }
            // Non-scalar binding fields (lastWill mappings, etc.) are
            // skipped here; if a future resolver needs them we'll add
            // a typed accessor rather than smuggling raw YAML through
            // the string-only map.
        }
        return fields;
    }

    /// <summary>
    /// Lookup helper that tolerates key-name case / quoting variants
    /// the way YAML authors actually write them. Returns <c>false</c>
    /// for missing or non-mapping children.
    /// </summary>
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
}
