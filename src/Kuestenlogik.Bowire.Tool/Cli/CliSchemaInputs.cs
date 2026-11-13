// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// <c>--schema</c> — the documents a command was handed, instead of the ones
/// a workbench uploaded (#654).
/// </summary>
/// <remarks>
/// <para>
/// Discovery can read a <c>.proto</c> or an OpenAPI document that never came
/// off a server. In the workbench those arrive by dropping a file on the
/// sidebar and are stored, because the person will come back to them
/// tomorrow. The command line is the other case: one invocation, inputs named
/// on the line, and in CI the schema is a file in the repository already.
/// </para>
/// <para>
/// So the CLI names the file rather than reaching for a workspace. That was
/// the alternative, and it is worse in the way that matters for CI: it makes
/// a run depend on state another process wrote, on a machine the pipeline
/// does not control, to find something the caller could simply have said.
/// </para>
/// </remarks>
internal static class CliSchemaInputs
{
    /// <summary>What a path's extension says it is.</summary>
    /// <remarks>
    /// The same split the workbench's drop zone makes: <c>.proto</c> is
    /// protobuf source, <c>.protoset</c> / <c>.pb</c> / <c>.desc</c> /
    /// <c>.binpb</c> its compiled descriptor set (#991), JSON and YAML are
    /// OpenAPI. Anything else is refused rather than guessed — the drop zone
    /// can fall back to "probably OpenAPI" because a person is watching the
    /// result, and a pipeline is not.
    /// </remarks>
    internal static string? KindOf(string path)
    {
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".PROTO" => SchemaUploadStore.ProtoKind,
            ".PROTOSET" or ".PB" or ".DESC" or ".BINPB" => SchemaUploadStore.ProtoSetKind,
            ".JSON" or ".YAML" or ".YML" => SchemaUploadStore.OpenApiKind,
            _ => null,
        };
    }

    /// <summary>
    /// Read the documents named by <c>--schema</c>.
    /// </summary>
    /// <returns>
    /// The documents and an exit code. A null list with a non-zero code means
    /// a path could not be used and the reason has been written to
    /// <paramref name="stderr"/>; a null list with zero means no
    /// <c>--schema</c> was given and the command reads what is stored.
    /// </returns>
    /// <remarks>
    /// This reads and hands back; it deliberately does not open the scope
    /// itself. <see cref="SchemaUploadStore.EnterExplicit"/> sets an
    /// <see cref="AsyncLocal{T}"/>, and a value set inside an <c>async</c>
    /// method is discarded when that method returns — the caller gets the
    /// disposable and none of the effect. It has to be opened in the method
    /// that goes on to do the work, which is the command's own action.
    /// </remarks>
    /// <param name="paths">The <c>--schema</c> values, in the order given.</param>
    /// <param name="stderr">Where a path that cannot be used is reported.</param>
    /// <param name="allowProto">
    /// False for the commands that marshal messages. A .proto gives names
    /// and no layouts, so it cannot serve them; its compiled descriptor set
    /// can, and they take that through this same option. Refused with a
    /// pointer rather than accepted and quietly ignored.
    /// </param>
    internal static async Task<(IReadOnlyList<SchemaUploadStore.ExplicitSchema>? Schemas, int ExitCode)> ReadAsync(
        string[]? paths, TextWriter stderr, bool allowProto = true)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        if (paths is null || paths.Length == 0) return (null, 0);

        var schemas = new List<SchemaUploadStore.ExplicitSchema>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var kind = KindOf(path);
            if (kind is null)
            {
                await stderr.WriteLineAsync(
                    $"bowire: --schema {path}: expected a .proto, a descriptor set "
                    + "(.protoset, .pb, .desc, .binpb), or an OpenAPI .json, .yaml or .yml.")
                    .ConfigureAwait(false);
                return (null, 2);
            }

            var isDescriptorSet = string.Equals(kind, SchemaUploadStore.ProtoSetKind, StringComparison.Ordinal);
            string content = "";
            byte[]? bytes = null;
            try
            {
                if (isDescriptorSet) bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                else content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Naming the path and the reason: --schema is typed by hand
                // or written into a pipeline, and both get paths wrong.
                await stderr.WriteLineAsync($"bowire: --schema {path}: {ex.Message}").ConfigureAwait(false);
                return (null, 2);
            }

            if (bytes is { Length: 0 } || (bytes is null && string.IsNullOrWhiteSpace(content)))
            {
                // An empty file discovers nothing, and silently finding
                // nothing is the outcome this whole ticket was about.
                await stderr.WriteLineAsync($"bowire: --schema {path}: the file is empty.").ConfigureAwait(false);
                return (null, 2);
            }

            if (!allowProto && string.Equals(kind, SchemaUploadStore.ProtoKind, StringComparison.Ordinal))
            {
                await stderr.WriteLineAsync(
                    $"bowire: --schema {path}: a .proto gives names only, and this command marshals messages. "
                    + "Name its compiled descriptor set instead: --schema api.protoset "
                    + "(`protoc --descriptor_set_out=api.protoset --include_imports`). "
                    + "`bowire discover --schema` reads a .proto directly.")
                    .ConfigureAwait(false);
                return (null, 2);
            }

            if (bytes is not null)
            {
                // Read once here, so a file that is not a descriptor set, or
                // one compiled without its imports, is refused with its path
                // instead of surfacing as a marshalling error mid-run.
                try
                {
                    Kuestenlogik.Bowire.Protocol.Grpc.GrpcDescriptorSet.Parse(bytes);
                }
                catch (FormatException ex)
                {
                    await stderr.WriteLineAsync($"bowire: --schema {path}: {ex.Message}").ConfigureAwait(false);
                    return (null, 2);
                }
            }

            schemas.Add(new SchemaUploadStore.ExplicitSchema(
                kind, content, Path.GetFileName(path), bytes));
        }

        return schemas.Count == 0 ? (null, 0) : (schemas, 0);
    }
}
