// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Parses .proto file text into <see cref="BowireServiceInfo"/> models.
/// Extracts package, service/method definitions, message/enum definitions
/// using regex-based parsing (not a full protobuf compiler).
/// </summary>
internal static partial class ProtoFileParser
{
    /// <summary>
    /// Parse all configured <see cref="ProtoSource"/> entries and return service info.
    /// </summary>
    public static List<BowireServiceInfo> ParseAll(List<ProtoSource> sources)
    {
        var services = new List<BowireServiceInfo>();

        foreach (var source in sources)
        {
            var content = ResolveContent(source);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            services.AddRange(Parse(content));
        }

        return services;
    }

    /// <summary>
    /// Parse a single .proto file content string into service info models.
    /// </summary>
    public static List<BowireServiceInfo> Parse(string protoContent)
    {
        var stripped = StripComments(protoContent);
        var package = ParsePackage(stripped);
        var enums = ParseEnums(stripped, package);
        var messages = ParseMessages(stripped, package);
        var services = ParseServices(stripped, package, messages, enums);

        return services;
    }

    private static string ResolveContent(ProtoSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.Content))
            return source.Content;

        if (!string.IsNullOrWhiteSpace(source.FilePath) && File.Exists(source.FilePath))
            return File.ReadAllText(source.FilePath);

        return string.Empty;
    }

    /// <summary>
    /// Strip single-line (//) and multi-line (/* */) comments.
    /// </summary>
    private static string StripComments(string input)
    {
        // Remove multi-line comments first
        var result = MultiLineCommentRegex().Replace(input, " ");
        // Remove single-line comments
        result = SingleLineCommentRegex().Replace(result, "");
        return result;
    }

    private static string ParsePackage(string content)
    {
        var match = PackageRegex().Match(content);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>
    /// Parse top-level enum definitions.
    /// </summary>
    private static Dictionary<string, List<BowireEnumValue>> ParseEnums(string content, string package)
    {
        var enums = new Dictionary<string, List<BowireEnumValue>>();

        foreach (Match match in EnumBlockRegex().Matches(content))
        {
            var enumName = match.Groups[1].Value.Trim();
            var body = match.Groups[2].Value;
            var fullName = string.IsNullOrEmpty(package) ? enumName : $"{package}.{enumName}";

            var values = new List<BowireEnumValue>();
            foreach (Match valMatch in EnumValueRegex().Matches(body))
            {
                var name = valMatch.Groups[1].Value.Trim();
                var number = int.Parse(valMatch.Groups[2].Value.Trim(), CultureInfo.InvariantCulture);
                values.Add(new BowireEnumValue(name, number));
            }

            enums[fullName] = values;
        }

        return enums;
    }

    /// <summary>
    /// Parse top-level and nested message definitions.
    /// </summary>
    private static Dictionary<string, BowireMessageInfo> ParseMessages(string content, string package)
    {
        var messages = new Dictionary<string, BowireMessageInfo>();
        ParseMessagesRecursive(content, package, messages);
        return messages;
    }

    private static void ParseMessagesRecursive(
        string content, string parentPrefix, Dictionary<string, BowireMessageInfo> messages)
    {
        var index = 0;
        while (index < content.Length)
        {
            var match = MessageStartRegex().Match(content, index);
            if (!match.Success) break;

            var messageName = match.Groups[1].Value.Trim();
            var braceStart = match.Index + match.Length - 1; // Position of '{'
            var body = ExtractBraceBlock(content, braceStart);
            if (body is null)
            {
                index = match.Index + match.Length;
                continue;
            }

            var fullName = string.IsNullOrEmpty(parentPrefix) ? messageName : $"{parentPrefix}.{messageName}";

            // Recursively parse nested messages
            ParseMessagesRecursive(body, fullName, messages);

            // Parse fields from the body
            var fields = ParseFields(body, fullName, messages);
            var info = new BowireMessageInfo(messageName, fullName, fields);
            messages[fullName] = info;

            index = braceStart + body.Length + 2; // Skip past closing brace
        }
    }

    private static List<BowireFieldInfo> ParseFields(
        string messageBody, string messageFullName, Dictionary<string, BowireMessageInfo> knownMessages)
    {
        var fields = new List<BowireFieldInfo>();

        // Match map fields: map<KeyType, ValueType> name = number;
        foreach (Match match in MapFieldRegex().Matches(messageBody))
        {
            var keyType = match.Groups[1].Value.Trim();
            var valueType = match.Groups[2].Value.Trim();
            var name = match.Groups[3].Value.Trim();
            var number = int.Parse(match.Groups[4].Value.Trim(), CultureInfo.InvariantCulture);

            fields.Add(new BowireFieldInfo(
                Name: name,
                Number: number,
                Type: $"map<{keyType}, {valueType}>",
                Label: "LABEL_OPTIONAL",
                IsMap: true,
                IsRepeated: false,
                MessageType: null,
                EnumValues: null));
        }

        // Match regular fields: [optional|repeated] type name = number;
        foreach (Match match in FieldRegex().Matches(messageBody))
        {
            var label = match.Groups[1].Value.Trim();
            var type = match.Groups[2].Value.Trim();
            var name = match.Groups[3].Value.Trim();
            var number = int.Parse(match.Groups[4].Value.Trim(), CultureInfo.InvariantCulture);

            // Skip if this is a map field (already parsed)
            if (fields.Any(f => f.Number == number)) continue;

            // Skip nested message/enum/oneof keywords that might match
            if (type is "message" or "enum" or "oneof" or "reserved" or "option" or "extensions")
                continue;

            var isRepeated = label == "repeated";
            var fieldLabel = isRepeated ? "LABEL_REPEATED" : "LABEL_OPTIONAL";
            var protoType = MapProtoType(type);

            BowireMessageInfo? nestedMsg = null;
            if (protoType == "TYPE_MESSAGE")
            {
                // Try to resolve as a nested message of the current message, or a known message
                nestedMsg = ResolveMessageReference(type, messageFullName, knownMessages);
            }

            fields.Add(new BowireFieldInfo(
                Name: name,
                Number: number,
                Type: protoType,
                Label: fieldLabel,
                IsMap: false,
                IsRepeated: isRepeated,
                MessageType: nestedMsg,
                EnumValues: null));
        }

        return fields;
    }

    private static BowireMessageInfo? ResolveMessageReference(
        string typeName, string currentMessageFullName, Dictionary<string, BowireMessageInfo> knownMessages)
    {
        // Try fully qualified name first
        if (knownMessages.TryGetValue(typeName, out var msg))
            return msg;

        // Try as nested message of current message
        var nestedName = $"{currentMessageFullName}.{typeName}";
        if (knownMessages.TryGetValue(nestedName, out msg))
            return msg;

        // Try with package prefix derived from current message
        var lastDot = currentMessageFullName.LastIndexOf('.');
        if (lastDot > 0)
        {
            var packagePrefix = currentMessageFullName[..lastDot];
            var qualifiedName = $"{packagePrefix}.{typeName}";
            if (knownMessages.TryGetValue(qualifiedName, out msg))
                return msg;
        }

        // Return a stub for unresolved messages
        return new BowireMessageInfo(typeName, typeName, []);
    }

    private static List<BowireServiceInfo> ParseServices(
        string content, string package,
        Dictionary<string, BowireMessageInfo> messages,
        Dictionary<string, List<BowireEnumValue>> enums)
    {
        var services = new List<BowireServiceInfo>();

        foreach (Match match in ServiceBlockRegex().Matches(content))
        {
            var serviceName = match.Groups[1].Value.Trim();
            var body = match.Groups[2].Value;
            var fullName = string.IsNullOrEmpty(package) ? serviceName : $"{package}.{serviceName}";

            var methods = new List<BowireMethodInfo>();
            foreach (Match rpcMatch in RpcRegex().Matches(body))
            {
                var methodName = rpcMatch.Groups[1].Value.Trim();
                var inputStream = rpcMatch.Groups[2].Value.Trim();
                var inputTypeName = rpcMatch.Groups[3].Value.Trim();
                var outputStream = rpcMatch.Groups[4].Value.Trim();
                var outputTypeName = rpcMatch.Groups[5].Value.Trim();

                var clientStreaming = inputStream == "stream";
                var serverStreaming = outputStream == "stream";

                var inputType = ResolveTypeForService(inputTypeName, package, messages, enums);
                var outputType = ResolveTypeForService(outputTypeName, package, messages, enums);

                var methodType = (clientStreaming, serverStreaming) switch
                {
                    (false, false) => "Unary",
                    (false, true) => "ServerStreaming",
                    (true, false) => "ClientStreaming",
                    (true, true) => "Duplex"
                };

                methods.Add(new BowireMethodInfo(
                    Name: methodName,
                    FullName: $"{fullName}/{methodName}",
                    ClientStreaming: clientStreaming,
                    ServerStreaming: serverStreaming,
                    InputType: inputType,
                    OutputType: outputType,
                    MethodType: methodType));
            }

            if (methods.Count > 0)
            {
                services.Add(new BowireServiceInfo(
                    Name: fullName,
                    Package: package,
                    Methods: methods)
                {
                    Source = "proto"
                });
            }
        }

        return services;
    }

    private static BowireMessageInfo ResolveTypeForService(
        string typeName, string package,
        Dictionary<string, BowireMessageInfo> messages,
        Dictionary<string, List<BowireEnumValue>> enums)
    {
        // Try fully qualified
        if (messages.TryGetValue(typeName, out var msg))
            return msg;

        // Try with package prefix
        if (!string.IsNullOrEmpty(package))
        {
            var qualified = $"{package}.{typeName}";
            if (messages.TryGetValue(qualified, out msg))
                return msg;
        }

        // Return stub with name only
        return new BowireMessageInfo(typeName, typeName, []);
    }

    /// <summary>
    /// Extract the content between matching braces starting at the given position.
    /// Returns the content inside the braces (excluding the braces themselves).
    /// </summary>
    private static string? ExtractBraceBlock(string content, int openBraceIndex)
    {
        if (openBraceIndex >= content.Length || content[openBraceIndex] != '{')
            return null;

        var depth = 1;
        var start = openBraceIndex + 1;

        for (var i = start; i < content.Length; i++)
        {
            switch (content[i])
            {
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return content[start..i];
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Map a proto type name to the TYPE_ enum string used by BowireFieldInfo.
    /// </summary>
    private static string MapProtoType(string protoType) => protoType switch
    {
        "double" => "TYPE_DOUBLE",
        "float" => "TYPE_FLOAT",
        "int32" => "TYPE_INT32",
        "int64" => "TYPE_INT64",
        "uint32" => "TYPE_UINT32",
        "uint64" => "TYPE_UINT64",
        "sint32" => "TYPE_SINT32",
        "sint64" => "TYPE_SINT64",
        "fixed32" => "TYPE_FIXED32",
        "fixed64" => "TYPE_FIXED64",
        "sfixed32" => "TYPE_SFIXED32",
        "sfixed64" => "TYPE_SFIXED64",
        "bool" => "TYPE_BOOL",
        "string" => "TYPE_STRING",
        "bytes" => "TYPE_BYTES",
        _ => "TYPE_MESSAGE" // Assume unknown types are message references
    };

    // ---- Compiled Regex Patterns ----

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex MultiLineCommentRegex();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex SingleLineCommentRegex();

    [GeneratedRegex(@"package\s+([\w.]+)\s*;")]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"enum\s+(\w+)\s*\{([^}]*)\}")]
    private static partial Regex EnumBlockRegex();

    [GeneratedRegex(@"(\w+)\s*=\s*(-?\d+)\s*;")]
    private static partial Regex EnumValueRegex();

    [GeneratedRegex(@"message\s+(\w+)\s*\{")]
    private static partial Regex MessageStartRegex();

    [GeneratedRegex(@"map\s*<\s*(\w+)\s*,\s*(\w+)\s*>\s+(\w+)\s*=\s*(\d+)\s*;")]
    private static partial Regex MapFieldRegex();

    [GeneratedRegex(@"(?:^|\n)\s*(optional|repeated|required)?\s*(\w[\w.]*)\s+(\w+)\s*=\s*(\d+)\s*[;\[]")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"service\s+(\w+)\s*\{((?:[^{}]|\{[^{}]*\})*)\}")]
    private static partial Regex ServiceBlockRegex();

    [GeneratedRegex(@"rpc\s+(\w+)\s*\(\s*(stream\s+)?(\w[\w.]*)\s*\)\s*returns\s*\(\s*(stream\s+)?(\w[\w.]*)\s*\)")]
    private static partial Regex RpcRegex();
}
