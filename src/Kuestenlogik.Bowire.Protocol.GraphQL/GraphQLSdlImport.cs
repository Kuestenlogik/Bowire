// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GraphQLParser;
using GraphQLParser.Exceptions;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.GraphQL.Mock;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// An uploaded GraphQL schema in SDL, read into the services introspection
/// would have returned (#994).
/// </summary>
/// <remarks>
/// <para>
/// For the server that answers operations and refuses introspection, which is
/// the usual production setting. Without a schema the plugin can still send
/// an operation, but it cannot offer a form for its arguments, and its
/// selection set is <c>{ __typename }</c>. With the SDL uploaded, the
/// workbench lists the root operations and builds the same operation it
/// would after an introspection.
/// </para>
/// <para>
/// The SDL goes through the introspection document the mock already builds
/// from it and then through the mapper that reads a server's answer, so an
/// uploaded schema and an introspected one cannot come out in two shapes.
/// </para>
/// </remarks>
internal static partial class GraphQLSdlImport
{
    /// <summary>The stored kind.</summary>
    internal const string KindId = "graphql-sdl";

    internal static readonly BowireUploadedSchemaKind Kind =
        new(KindId, "GraphQL SDL", [".graphql", ".graphqls", ".gql"]);

    private static readonly Lock CacheLock = new();
    private static string? _cachedFor;
    private static List<BowireServiceInfo> _cached = [];

    // A type-system definition at the start of a line. `type: object` in a
    // YAML document and `"type": "object"` in JSON do not match: the keyword
    // has to be followed by a name, not by a colon.
    [GeneratedRegex(
        @"^\s*(?:extend\s+)?(?:(?:type|interface|input|enum|union|scalar)\s+[_A-Za-z]\w*\s*(?:[{@=]|implements\b|$)|schema\s*[{@]|directive\s+@)",
        RegexOptions.Multiline)]
    private static partial Regex DefinitionPattern();

    /// <summary>
    /// Whether the document reads as SDL: asked when the file name does not
    /// say, e.g. a schema saved as <c>.txt</c>.
    /// </summary>
    public static bool LooksLikeSdl(ReadOnlyMemory<byte> document)
    {
        // The head is enough: SDL opens with definitions, give or take a
        // comment block.
        var head = Encoding.UTF8.GetString(document.Span[..Math.Min(document.Length, 16 * 1024)]);
        var first = head.AsSpan().TrimStart(['﻿', ' ', '\t', '\r', '\n']);
        if (first.Length == 0 || first[0] is '{' or '[' or '<') return false;
        return DefinitionPattern().IsMatch(head);
    }

    /// <summary>
    /// The root operation services an SDL document declares.
    /// </summary>
    /// <exception cref="FormatException">The document is not valid SDL.</exception>
    public static List<BowireServiceInfo> Read(ReadOnlyMemory<byte> document)
    {
        string sdl;
        try
        {
            sdl = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(document.Span).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("The file is not text: GraphQL SDL is a UTF-8 document.");
        }

        JsonElement schema;
        try
        {
            var introspection = GraphQlIntrospection.Build(Parser.Parse(sdl));
            using var json = JsonDocument.Parse(introspection["data"]!.ToJsonString());
            schema = json.RootElement.Clone();
        }
        catch (GraphQLSyntaxErrorException ex)
        {
            // The parser's own sentence carries the line and column, which
            // is what somebody fixing the file needs.
            throw new FormatException("Not GraphQL SDL: " + ex.Message, ex);
        }

        var services = new GraphQLSchemaMapper().Map(schema, serverUrl: string.Empty);
        // No origin of their own: an SDL file names no server, so the URL
        // the workbench is pointed at decides where a call goes.
        foreach (var service in services) service.OriginUrl = null;
        return services;
    }

    /// <summary>
    /// The services of every stored SDL document, for building a call
    /// against a server that was never introspected.
    /// </summary>
    /// <remarks>
    /// Cached on the ids of what is stored, which change with the workspace,
    /// the identity and every upload.
    /// </remarks>
    public static List<BowireServiceInfo> Stored()
    {
        var docs = BowireUploadedSchemas.GetAll(KindId);
        if (docs.Count == 0) return [];
        var key = string.Join(",", docs.Select(d => d.Id));

        lock (CacheLock)
        {
            if (string.Equals(_cachedFor, key, StringComparison.Ordinal)) return _cached;

            var services = new List<BowireServiceInfo>();
            foreach (var doc in docs)
            {
                try
                {
                    services.AddRange(Read(doc.Document));
                }
                catch (FormatException)
                {
                    // Refused at upload if it never read; skipped if it
                    // stopped reading since.
                }
            }

            _cachedFor = key;
            _cached = services;
            return services;
        }
    }
}
