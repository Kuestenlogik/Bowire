// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Kuestenlogik.Bowire.DataSets.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.DataSets;

/// <summary>
/// The DataSets rpcs (#675). The gRPC service, <c>bowire test --data</c> and
/// the MCP tools all call this handler, so a data file reads — and fails —
/// the same way everywhere.
/// </summary>
public static class DataSetRpc
{
    /// <summary>Read a CSV or JSON data set into its rows.</summary>
    /// <exception cref="RpcException"><see cref="StatusCode.InvalidArgument"/> when the file is empty or malformed; the detail names the line or item.</exception>
    public static ParseDataSetResponse ParseDataSet(ParseDataSetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = string.IsNullOrWhiteSpace(request.FileName) ? "data set" : request.FileName.Trim();
        try
        {
            return DataSetParser.Parse(name, request.Content ?? "", request.Format);
        }
        catch (DataSetFormatException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }
}

/// <summary>The gRPC face of <see cref="DataSetRpc"/>; nothing of its own.</summary>
internal sealed class DataSetsGrpcService : V1.DataSets.DataSetsBase
{
    public override Task<ParseDataSetResponse> ParseDataSet(ParseDataSetRequest request, ServerCallContext context) =>
        Task.FromResult(DataSetRpc.ParseDataSet(request));
}

/// <summary>Registers gRPC with JSON transcoding for the DataSets service.</summary>
public sealed class BowireDataSetsServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGrpc().AddJsonTranscoding();
    }
}

/// <summary>Maps the DataSets service inside the workbench's route group, behind its auth gate.</summary>
public sealed class BowireDataSetsEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = string.IsNullOrEmpty(basePath) ? endpoints : endpoints.MapGroup(basePath);
        group.MapGrpcService<DataSetsGrpcService>();
    }
}
