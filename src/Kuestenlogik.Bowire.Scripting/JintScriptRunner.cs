// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Runtime;

namespace Kuestenlogik.Bowire.Scripting;

/// <summary>
/// Runs scripts with the workbench's own runtime, <c>wwwroot/js/scripts.js</c>,
/// in Jint (#672). The browser and <c>bowire test</c> therefore build the same
/// <c>ctx</c> from the same code: what the conformance suite checks is the
/// engine, not two implementations of one API.
/// </summary>
/// <remarks>
/// <para>
/// Sandbox: Jint exposes no .NET types unless asked to, so a script has no
/// file system, no process and no network. Every run gets a fresh engine with
/// a wall-clock timeout, a memory ceiling, a statement limit and a recursion
/// limit; reaching one stops the script with a .NET exception the script
/// cannot catch, and the run reports it.
/// </para>
/// </remarks>
public sealed class JintScriptRunner : IBowireScriptRunner
{
    private static readonly Lazy<Prepared<Acornima.Ast.Script>> s_runtime = new(() =>
    {
        using var stream = typeof(JintScriptRunner).Assembly.GetManifestResourceStream("Kuestenlogik.Bowire.Scripting.scripts.js")
            ?? throw new InvalidOperationException("scripts.js is not embedded");
        using var reader = new StreamReader(stream);
        // scripts.js is a fragment of the workbench's bundle: it expects the
        // bundle's `t` (only its lint calls it) and exports through `window`.
        var source = "var window = {};\nfunction t(key) { return key; }\n" + reader.ReadToEnd();
        return Engine.PrepareScript(source, "scripts.js");
    });

    // The bridge between the JSON this runner hands in and the runtime's
    // own entry points; the result comes back as JSON as well.
    private const string Bridge = """
        (function (inputJson) {
            var input = JSON.parse(inputJson);
            var api = window.__bowireScripts;
            var opts = {
                source: input.source,
                protocolShape: input.shape,
                service: input.service,
                method: input.method,
                request: input.request,
                response: input.response,
                env: input.env,
                captured: input.captured
            };
            var result = input.phase === 'pre' ? api.runPreScript(opts) : api.runPostScript(opts);
            return JSON.stringify({
                ok: !!result.ok,
                error: result.error || null,
                assertFail: !!result.assertFail,
                request: input.request,
                env: input.env,
                captured: input.captured,
                log: api.getScriptConsole(input.service, input.method).map(function (e) { return e.message; })
            });
        })
        """;

    public BowireScriptResult RunPreRequest(BowireScriptCall script) => Run(script, "pre");

    public BowireScriptResult RunPostResponse(BowireScriptCall script) => Run(script, "post");

    private static BowireScriptResult Run(BowireScriptCall call, string phase)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrWhiteSpace(call.Source)) return new BowireScriptResult(true, null, false, []);

        var limits = call.Limits;
        using var engine = new Engine(o => o
            .TimeoutInterval(limits.Timeout)
            .LimitMemory(limits.MemoryBytes)
            .MaxStatements(limits.MaxStatements)
            .LimitRecursion(256));

        string resultJson;
        try
        {
            engine.Execute(s_runtime.Value);
            var bridge = engine.Evaluate(Bridge);
            resultJson = engine.Invoke(bridge, InputJson(call, phase)).AsString();
        }
        catch (TimeoutException)
        {
            return Stopped(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"the script ran longer than {limits.Timeout.TotalSeconds:0.###} s and was stopped"));
        }
        catch (MemoryLimitExceededException)
        {
            return Stopped(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"the script allocated more than {limits.MemoryBytes / (1024 * 1024)} MB and was stopped"));
        }
        catch (StatementsCountOverflowException)
        {
            return Stopped(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"the script ran more than {limits.MaxStatements} statements and was stopped"));
        }
        catch (RecursionDepthOverflowException)
        {
            return Stopped("the script recursed too deeply and was stopped");
        }

        return Apply(call, JsonNode.Parse(resultJson)!.AsObject());
    }

    private static BowireScriptResult Stopped(string why) => new(false, why, false, []);

    private static string InputJson(BowireScriptCall call, string phase)
    {
        var request = new JsonObject
        {
            ["url"] = call.Request.Url,
            ["method"] = call.Request.Method,
            ["body"] = call.Request.Body,
            ["headers"] = ToObject(call.Request.Headers),
            ["query"] = ToObject(call.Request.Query),
        };
        if (call.Request.DeadlineSeconds > 0) request["deadlineSeconds"] = call.Request.DeadlineSeconds;

        JsonObject? response = call.Response is null ? null : new JsonObject
        {
            ["body"] = call.Response.Body,
            ["status"] = call.Response.Status,
            ["durationMs"] = call.Response.DurationMs,
            ["headers"] = ToObject(call.Response.Headers),
        };

        JsonNode captured;
        try { captured = JsonNode.Parse(string.IsNullOrWhiteSpace(call.CapturedJson) ? "{}" : call.CapturedJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { captured = new JsonObject(); }

        return new JsonObject
        {
            ["phase"] = phase,
            ["source"] = call.Source,
            ["shape"] = call.Shape,
            ["service"] = call.Service,
            ["method"] = call.Method,
            ["request"] = request,
            ["response"] = response,
            ["env"] = ToObject(call.Environment),
            ["captured"] = captured,
        }.ToJsonString();
    }

    // What the script left behind: a pre-request script's request, the
    // environment and the captured values, back onto the call.
    private static BowireScriptResult Apply(BowireScriptCall call, JsonObject result)
    {
        if (result["request"] is JsonObject request)
        {
            call.Request.Url = Text(request["url"]) ?? call.Request.Url;
            call.Request.Method = Text(request["method"]) ?? call.Request.Method;
            // A script may set the body to an object; the wire takes its JSON.
            call.Request.Body = request["body"] is JsonValue v && v.TryGetValue<string>(out var s) ? s
                : request["body"]?.ToJsonString() ?? "";
            Replace(call.Request.Headers, request["headers"] as JsonObject);
            Replace(call.Request.Query, request["query"] as JsonObject);
            if (request["deadlineSeconds"] is JsonValue d && d.TryGetValue<double>(out var seconds)) call.Request.DeadlineSeconds = seconds;
        }
        Replace(call.Environment, result["env"] as JsonObject);
        call.CapturedJson = result["captured"]?.ToJsonString() ?? "{}";

        var log = (result["log"] as JsonArray ?? []).Select(e => Text(e) ?? "").ToList();
        return new BowireScriptResult(
            result["ok"]?.GetValue<bool>() ?? false,
            Text(result["error"]),
            result["assertFail"]?.GetValue<bool>() ?? false,
            log);
    }

    private static JsonObject ToObject(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in pairs) obj[key] = value;
        return obj;
    }

    private static void Replace(IDictionary<string, string> target, JsonObject? source)
    {
        if (source is null) return;
        target.Clear();
        foreach (var (key, value) in source)
        {
            target[key] = value switch
            {
                null => "",
                JsonValue jv when jv.TryGetValue<string>(out var s) => s,
                _ => value.ToJsonString(),
            };
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString();
}
