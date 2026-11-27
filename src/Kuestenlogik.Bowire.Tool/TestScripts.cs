// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Scripting;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// The pre-request and post-response scripts of one <c>bowire test</c> run
/// (#672): the runner, the values scripts share across the run
/// (<c>ctx.vars.captured</c>) and the sandbox limits from configuration.
/// </summary>
internal sealed class TestScripts(BowireScriptLimits limits, IBowireScriptRunner? runner)
{
    private string _capturedJson = "{}";

    /// <summary>The installed runner, with limits read from <c>Bowire:Scripting:*</c>.</summary>
    public static TestScripts FromConfiguration(Microsoft.Extensions.Configuration.IConfiguration? cfg)
        => new(BowireScriptLimits.FromConfiguration(cfg), BowireScriptRunners.Find());

    /// <summary>
    /// Run <paramref name="source"/> before the call. Returns the failure to
    /// report, or <c>null</c>. Without an installed engine a script is a
    /// failure, never skipped: a run that ignored it would report a request
    /// that was not the one tested.
    /// </summary>
    public string? RunPre(string source, string protocol, string service, string method,
        BowireScriptRequest request, IDictionary<string, string> environment, ICollection<string> log)
    {
        if (runner is null) return BowireScriptRunners.NotInstalled;
        var script = Call(source, protocol, service, method, request, null, environment);
        var result = runner.RunPreRequest(script);
        Finish(script, result, environment, log);
        return result.Ok ? null : "pre-request script: " + result.Error;
    }

    /// <summary>Run <paramref name="source"/> against the response. Returns the failure to report, or <c>null</c>.</summary>
    public string? RunPost(string source, string protocol, string service, string method,
        BowireScriptRequest request, BowireScriptResponse response, IDictionary<string, string> environment, ICollection<string> log)
    {
        if (runner is null) return BowireScriptRunners.NotInstalled;
        var script = Call(source, protocol, service, method, request, response, environment);
        var result = runner.RunPostResponse(script);
        Finish(script, result, environment, log);
        return result.Ok ? null : "post-response script: " + result.Error;
    }

    private BowireScriptCall Call(string source, string protocol, string service, string method,
        BowireScriptRequest request, BowireScriptResponse? response, IDictionary<string, string> environment) => new()
    {
        Source = source,
        Shape = ShapeOf(protocol),
        Service = service,
        Method = method,
        Request = request,
        Response = response,
        Environment = new Dictionary<string, string>(environment, StringComparer.Ordinal),
        CapturedJson = _capturedJson,
        Limits = limits,
    };

    private void Finish(BowireScriptCall script, BowireScriptResult result, IDictionary<string, string> environment, ICollection<string> log)
    {
        _capturedJson = script.CapturedJson;
        // ctx.env.set writes through, so later tests of the run see it, as
        // the workbench keeps it in the active environment.
        foreach (var (key, value) in script.Environment) environment[key] = value;
        foreach (var line in result.Log) log.Add(line);
    }

    // The shapes scripts.js knows; everything else gets the REST surface,
    // as detectScriptProtocolShape does in the browser.
    internal static string ShapeOf(string? protocol) => (protocol ?? "").ToUpperInvariant() switch
    {
        "GRPC" or "GRPC-WEB" => "grpc",
        "MQTT" => "mqtt",
        "WEBSOCKET" or "WS" or "SOCKET.IO" or "SOCKETIO" => "websocket",
        _ => "rest",
    };
}
