// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Cli;

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
/// <para>
/// Here, in the CLI contract assembly, because more than one assembly
/// contributes commands that take <c>--schema</c>: the tool's own, and
/// <c>bowire scan</c> from the scanner (#1016). One reader means one answer
/// to what a file is and one wording for a refusal.
/// </para>
/// </remarks>
internal static class CliSchemaInputs
{
    /// <summary>Which schema kinds a command takes.</summary>
    /// <remarks>
    /// A statement per command rather than a flag per kind: the kinds come
    /// from the loaded plugins (#994, #1018), so a command says which of
    /// them it would use, and a file of any other kind is refused instead of
    /// accepted and quietly ignored.
    /// </remarks>
    internal sealed class Taken
    {
        private readonly Func<string, bool> _takes;

        private Taken(Func<string, bool> takes) => _takes = takes;

        /// <summary>Every kind a loaded plugin imports: <c>discover</c>.</summary>
        internal static Taken Any { get; } = new(_ => true);

        /// <summary>
        /// Every kind but a <c>.proto</c>: the commands that marshal
        /// messages. A .proto gives names and no layouts, so it cannot serve
        /// them; its compiled descriptor set can.
        /// </summary>
        internal static Taken ForCalls { get; } =
            new(kind => !string.Equals(kind, SchemaUploadStore.ProtoKind, StringComparison.OrdinalIgnoreCase));

        /// <summary>The named kinds and no others.</summary>
        internal static Taken Only(params string[] kinds)
            => new(kind => kinds.Contains(kind, StringComparer.OrdinalIgnoreCase));

        internal bool Takes(string kind) => _takes(kind);
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
    /// <para>
    /// A file's kind is decided the way the workbench's upload decides it
    /// (<see cref="UploadedSchemaImports.Route"/>): by extension where one
    /// kind declares it, by content where several share it or none does. So
    /// the command line takes what the drop zone takes, and an AsyncAPI
    /// document under <c>.yaml</c> is not handed to the REST plugin as
    /// OpenAPI. A file of a plugin's kind is read once here, so that one
    /// which does not read stops the command with its path instead of
    /// surfacing mid-run.
    /// </para>
    /// <para>
    /// This reads and hands back; it deliberately does not open the scope
    /// itself. <see cref="SchemaUploadStore.EnterExplicit"/> sets an
    /// <see cref="AsyncLocal{T}"/>, and a value set inside an <c>async</c>
    /// method is discarded when that method returns — the caller gets the
    /// disposable and none of the effect. It has to be opened in the method
    /// that goes on to do the work, which is the command's own action.
    /// </para>
    /// </remarks>
    /// <param name="paths">The <c>--schema</c> values, in the order given.</param>
    /// <param name="stderr">Where a path that cannot be used is reported.</param>
    /// <param name="registry">The loaded plugins: they declare and read the kinds.</param>
    /// <param name="taken">The kinds this command uses; every kind when null.</param>
    internal static async Task<(IReadOnlyList<SchemaUploadStore.ExplicitSchema>? Schemas, int ExitCode)> ReadAsync(
        string[]? paths, TextWriter stderr, BowireProtocolRegistry registry, Taken? taken = null)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(registry);
        if (paths is null || paths.Length == 0) return (null, 0);
        taken ??= Taken.Any;

        // Only the kinds this command takes are named in a refusal: naming
        // one it would refuse a moment later sends the caller round twice.
        var takenHere = UploadedSchemaImports.Describe(
            [.. UploadedSchemaImports.Kinds(registry).Where(k => taken.Takes(k.Kind.Kind))]);

        var schemas = new List<SchemaUploadStore.ExplicitSchema>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Naming the path and the reason: --schema is typed by hand
                // or written into a pipeline, and both get paths wrong.
                await stderr.WriteLineAsync($"bowire: --schema {path}: {ex.Message}").ConfigureAwait(false);
                return (null, 2);
            }

            if (Array.TrueForAll(bytes, b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                // An empty file discovers nothing, and silently finding
                // nothing is the outcome --schema exists to prevent.
                await stderr.WriteLineAsync($"bowire: --schema {path}: the file is empty.").ConfigureAwait(false);
                return (null, 2);
            }

            var fileName = Path.GetFileName(path);
            var importable = UploadedSchemaImports.Route(registry, fileName, bytes, out _);
            if (importable is null)
            {
                await stderr.WriteLineAsync(
                    $"bowire: --schema {path}: not a schema this command reads. It takes: {takenHere}.")
                    .ConfigureAwait(false);
                return (null, 2);
            }

            var kind = importable.Kind.Kind;
            if (!taken.Takes(kind))
            {
                var reason = string.Equals(kind, SchemaUploadStore.ProtoKind, StringComparison.OrdinalIgnoreCase)
                    ? "a .proto gives names only, and this command marshals messages. "
                        + "Name its compiled descriptor set instead: --schema api.protoset "
                        + "(`protoc --descriptor_set_out=api.protoset --include_imports`). "
                        + "`bowire discover --schema` reads a .proto directly."
                    : $"this command does not use {importable.Kind.DisplayName}. It takes: {takenHere}.";
                await stderr.WriteLineAsync($"bowire: --schema {path}: {reason}").ConfigureAwait(false);
                return (null, 2);
            }

            if (importable.Reader is { } reader)
            {
                try
                {
                    reader.ReadUploadedSchema(kind, bytes, fileName);
                }
                catch (FormatException ex)
                {
                    await stderr.WriteLineAsync($"bowire: --schema {path}: {ex.Message}").ConfigureAwait(false);
                    return (null, 2);
                }

                schemas.Add(new SchemaUploadStore.ExplicitSchema(kind, "", fileName, bytes));
                continue;
            }

            // Core's two kinds are read as text; the reader honours a byte
            // order mark, which the bytes still carry.
            using var text = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            schemas.Add(new SchemaUploadStore.ExplicitSchema(
                kind, await text.ReadToEndAsync().ConfigureAwait(false), fileName));
        }

        return schemas.Count == 0 ? (null, 0) : (schemas, 0);
    }
}
