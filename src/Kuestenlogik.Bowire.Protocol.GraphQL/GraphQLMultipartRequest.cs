// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// One file a caller wants to upload with a GraphQL operation (#713).
/// </summary>
/// <param name="VariablePath">
/// Where the file belongs in the operation, in the dotted form the
/// multipart spec uses — <c>variables.file</c>, or
/// <c>variables.files.0</c> for an entry in a list.
/// </param>
/// <param name="FileName">The name the server sees.</param>
/// <param name="ContentType">
/// The part's media type. <c>application/octet-stream</c> when the caller
/// does not know, which is the honest answer rather than a guess from the
/// extension.
/// </param>
/// <param name="Content">The bytes.</param>
internal sealed record GraphQLUpload(
    string VariablePath,
    string FileName,
    string? ContentType,
    byte[] Content);

/// <summary>
/// Builds a <c>graphql-multipart-request-spec</c> body (#713).
/// </summary>
/// <remarks>
/// <para>
/// The spec is three parts in one form. <c>operations</c> is the ordinary
/// GraphQL request with <c>null</c> standing where each file goes;
/// <c>map</c> says which later part fills which of those holes; then the
/// files themselves, named by their index in the map.
/// </para>
/// <para>
/// The indirection exists because a file cannot sit inside JSON. The
/// alternative — base64 inside the variables — doubles the bytes on the
/// wire and is what this avoids; a server implementing the spec streams
/// the part straight to storage.
/// </para>
/// <para>
/// A caller reaches this by putting a <c>files</c> array beside
/// <c>query</c> and <c>variables</c> in the invoke message. That is
/// Bowire's own shape, not the spec's: the spec describes what goes on the
/// wire, and something has to carry the bytes from the browser to here
/// first.
/// </para>
/// </remarks>
internal static class GraphQLMultipartRequest
{
    private static readonly JsonSerializerOptions s_compact = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The uploads a caller declared, or an empty list when the message
    /// carries none.
    /// </summary>
    /// <remarks>
    /// An entry without usable bytes or without a variable path is skipped
    /// rather than rejected: a half-filled file picker should not fail the
    /// whole operation, and the server's own "expected a file here" is the
    /// clearer message when the variable stays null.
    /// </remarks>
    public static IReadOnlyList<GraphQLUpload> Uploads(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return [];
        if (!message.Contains("\"files\"", StringComparison.Ordinal)) return [];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(message); }
        catch (JsonException) { return []; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("files", out var files)) return [];
            if (files.ValueKind != JsonValueKind.Array) return [];

            var result = new List<GraphQLUpload>();
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;

                var path = Text(file, "variablePath");
                var base64 = Text(file, "base64");
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(base64)) continue;

                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch (FormatException) { continue; }

                result.Add(new GraphQLUpload(
                    path,
                    Text(file, "name") ?? "file",
                    Text(file, "contentType"),
                    bytes));
            }
            return result;
        }
    }

    /// <summary>
    /// The multipart body for an operation and its files.
    /// </summary>
    /// <remarks>
    /// CA2000 is suppressed rather than satisfied: every part created here
    /// is handed to the <see cref="MultipartFormDataContent"/>, which owns
    /// and disposes them. Disposing them here would tear down the body
    /// before it is sent.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Parts are owned and disposed by the returned MultipartFormDataContent.")]
    public static MultipartFormDataContent Build(
        string query, JsonElement? variables, string? operationName, IReadOnlyList<GraphQLUpload> uploads)
    {
        ArgumentNullException.ThrowIfNull(uploads);

        var operations = new JsonObject { ["query"] = query };
        if (variables.HasValue && variables.Value.ValueKind == JsonValueKind.Object)
            operations["variables"] = JsonNode.Parse(variables.Value.GetRawText());
        if (!string.IsNullOrWhiteSpace(operationName))
            operations["operationName"] = operationName;

        // Each file's slot is set to null. The spec requires the key to
        // exist and be null, not to be absent: a server walks the map and
        // expects to find something to replace.
        var map = new JsonObject();
        for (var i = 0; i < uploads.Count; i++)
        {
            NullOutPath(operations, uploads[i].VariablePath);
            map[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new JsonArray(uploads[i].VariablePath);
        }

        var form = new MultipartFormDataContent
        {
            { new StringContent(operations.ToJsonString(s_compact), Encoding.UTF8, "application/json"), "operations" },
            { new StringContent(map.ToJsonString(s_compact), Encoding.UTF8, "application/json"), "map" },
        };

        for (var i = 0; i < uploads.Count; i++)
        {
            var part = new ByteArrayContent(uploads[i].Content);
            var mediaType = uploads[i].ContentType;
            part.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType);
            form.Add(part, i.ToString(System.Globalization.CultureInfo.InvariantCulture), uploads[i].FileName);
        }

        return form;
    }

    /// <summary>
    /// Sets a dotted path to null, creating the objects on the way.
    /// </summary>
    /// <remarks>
    /// A numeric segment addresses a list entry, which is how the spec
    /// writes a multi-file variable (<c>variables.files.0</c>). Lists are
    /// grown to fit rather than indexed into blindly — the caller's map is
    /// the only description of the shape, and a caller that names index 2
    /// means the list has three slots.
    /// </remarks>
    private static void NullOutPath(JsonObject root, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;

        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            var nextIsIndex = int.TryParse(
                segments[i + 1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _);

            current = Step(current, segment, nextIsIndex);
        }

        Assign(current, segments[^1], null);
    }

    private static JsonNode Step(JsonNode current, string segment, bool nextIsIndex)
    {
        if (int.TryParse(segment, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index)
            && current is JsonArray array)
        {
            while (array.Count <= index) array.Add(null);
            array[index] ??= nextIsIndex ? new JsonArray() : new JsonObject();
            return array[index]!;
        }

        var obj = current as JsonObject ?? new JsonObject();
        if (obj[segment] is null)
            obj[segment] = nextIsIndex ? new JsonArray() : new JsonObject();
        return obj[segment]!;
    }

    private static void Assign(JsonNode current, string segment, JsonNode? value)
    {
        if (int.TryParse(segment, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index)
            && current is JsonArray array)
        {
            while (array.Count <= index) array.Add(null);
            array[index] = value;
            return;
        }

        if (current is JsonObject obj) obj[segment] = value;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
