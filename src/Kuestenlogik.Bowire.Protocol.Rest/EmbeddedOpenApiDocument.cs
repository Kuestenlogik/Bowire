// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// The OpenAPI documents an embedding host generates, read in-process
/// (#993).
/// </summary>
/// <remarks>
/// <para>
/// A host that registers OpenAPI already has the complete description of its
/// HTTP API in the process: schemas, examples, descriptions, and whatever its
/// document and schema transformers changed. Embedded discovery used to
/// ignore that and rebuild a description from ApiExplorer plus reflection
/// over the CLR types, which can only ever be an approximation of the
/// document the host actually publishes.
/// </para>
/// <para>
/// The way in is <c>Microsoft.Extensions.ApiDescriptions.IDocumentProvider</c>,
/// the contract build-time document generation uses. Every generator
/// registers its own copy of that interface under the same full name
/// (<c>Microsoft.AspNetCore.OpenApi</c>, Swashbuckle, NSwag), each internal
/// to its assembly, and the tooling finds it by name. So does this, which is
/// what keeps the REST plugin free of a reference to any of them: a
/// reference would tie the plugin to one generator and to one version of
/// <c>Microsoft.OpenApi</c>, the collision the adapter split exists to avoid.
/// </para>
/// <para>
/// No HTTP is involved. The provider generates the document from the host's
/// own endpoint metadata and writes it to a <see cref="TextWriter"/>.
/// </para>
/// </remarks>
internal static class EmbeddedOpenApiDocument
{
    private const string ProviderTypeName = "Microsoft.Extensions.ApiDescriptions.IDocumentProvider";

    /// <summary>
    /// Every document the host's generators offer, as name and JSON text.
    /// Empty when the host registers no generator.
    /// </summary>
    public static async Task<IReadOnlyList<(string Name, string Json)>> ReadAllAsync(
        IServiceProvider? services, CancellationToken ct)
    {
        if (services is null) return [];

        var documents = new List<(string, string)>();
        foreach (var contract in ProviderContracts())
        {
            if (services.GetService(contract) is not { } provider) continue;

            var names = contract.GetMethod("GetDocumentNames", Type.EmptyTypes)?.Invoke(provider, null)
                as IEnumerable<string>;
            var generate = contract.GetMethod("GenerateAsync", [typeof(string), typeof(TextWriter)]);
            if (names is null || generate is null) continue;

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                using var writer = new StringWriter(CultureInfo.InvariantCulture);
                if (generate.Invoke(provider, [name, writer]) is Task generating)
                    await generating.WaitAsync(ct).ConfigureAwait(false);

                var json = writer.ToString();
                if (!string.IsNullOrWhiteSpace(json)) documents.Add((name, json));
            }
        }

        return documents;
    }

    /// <summary>
    /// The <c>IDocumentProvider</c> interface of each loaded assembly that
    /// declares one.
    /// </summary>
    private static IEnumerable<Type> ProviderContracts()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic) continue;

            Type? contract;
            try
            {
                contract = assembly.GetType(ProviderTypeName, throwOnError: false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // An assembly whose own references do not load cannot have
                // registered a working provider either.
                continue;
            }

            if (contract is { IsInterface: true }) yield return contract;
        }
    }
}
