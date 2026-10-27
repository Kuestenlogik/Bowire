// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Kuestenlogik.Bowire.Interop.Formats;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop;

/// <summary>
/// The Interop rpcs (#673, #674). The gRPC service, the CLI and the MCP tools
/// all call this one handler with the proto messages, so every surface reads
/// the same formats, reports the same skips and fails with the same errors.
/// </summary>
public static class InteropRpc
{
    // Sniffing order matters: the structured formats recognise themselves by
    // a marker; .http is the fallback for plain text with request lines.
    private static readonly IInteropImporter[] s_importers =
    [
        new InsomniaImporter(),
        new HttpFileImporter(),
    ];

    /// <summary>The importer ids <c>format</c> accepts.</summary>
    public static IReadOnlyList<string> Formats => s_importers.Select(i => i.Id).ToArray();

    /// <summary>Read one export into Bowire collections and environments.</summary>
    /// <exception cref="RpcException">
    /// <see cref="StatusCode.InvalidArgument"/> when there is nothing to read,
    /// the format is unknown, or the file does not parse.
    /// </exception>
    public static ImportCollectionResponse ImportCollection(ImportCollectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = request.Files.Where(f => !string.IsNullOrEmpty(f.Content)).ToList();
        if (files.Count == 0) throw Invalid("No file to import.");

        IInteropImporter importer;
        if (!string.IsNullOrWhiteSpace(request.Format))
        {
            importer = s_importers.FirstOrDefault(i => string.Equals(i.Id, request.Format.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid($"Unknown format '{request.Format}'. Known: {string.Join(", ", Formats)}.");
        }
        else
        {
            importer = s_importers.FirstOrDefault(i => i.CanRead(files))
                ?? throw Invalid("Not a format Bowire imports: expected an Insomnia export (v4 JSON/YAML or v5 YAML) or a .http file (with its http-client.env.json, if any).");
        }

        var report = new ReportBuilder();
        try
        {
            var response = importer.Read(files, string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(), report);
            response.Report = report.Build();
            return response;
        }
        catch (InteropFormatException ex)
        {
            throw Invalid($"{importer.Id}: {ex.Message}");
        }
    }

    /// <summary>Write Bowire collections as a .http file plus http-client.env.json.</summary>
    public static ExportHttpResponse ExportHttp(ExportHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialect = string.IsNullOrWhiteSpace(request.Dialect) ? HttpDialect.JetBrains
            : request.Dialect.Trim().ToUpperInvariant() switch
            {
                "JETBRAINS" or "RIDER" or "VISUALSTUDIO" or "VS" => HttpDialect.JetBrains,
                "VSCODE" or "RESTCLIENT" => HttpDialect.VsCode,
                _ => throw Invalid($"Unknown dialect '{request.Dialect}'. Known: jetbrains, vscode."),
            };
        var report = new ReportBuilder();
        var (http, envJson) = HttpFileExporter.Write(request.Collections, request.Environments, dialect, report);
        return new ExportHttpResponse { Http = http, EnvJson = envJson, Report = report.Build() };
    }

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));
}
