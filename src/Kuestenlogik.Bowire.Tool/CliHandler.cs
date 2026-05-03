// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.App.Configuration;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// Handles CLI subcommands: list, describe, call.
/// Reuses GrpcReflectionClient and GrpcInvoker from the library.
/// </summary>
internal static class CliHandler
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private static bool UseColor => !Console.IsOutputRedirected;

    public static async Task<int> ListAsync(CliCommandOptions cli) => await RunWithErrorHandling(cli, ListImplAsync).ConfigureAwait(false);
    public static async Task<int> DescribeAsync(CliCommandOptions cli) => await RunWithErrorHandling(cli, DescribeImplAsync).ConfigureAwait(false);
    public static async Task<int> CallAsync(CliCommandOptions cli) => await RunWithErrorHandling(cli, CallImplAsync).ConfigureAwait(false);

    private static async Task<int> RunWithErrorHandling(CliCommandOptions cli, Func<CliCommandOptions, Task<int>> impl)
    {
        ArgumentNullException.ThrowIfNull(cli);
        try
        {
            return await impl(cli).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            if (ex.InnerException is not null)
                WriteError($"  {ex.InnerException.Message}");
            return 1;
        }
    }

    private static async Task<int> ListImplAsync(CliCommandOptions cli)
    {
        using var client = new GrpcReflectionClient(cli.Url, showInternalServices: false);
        var services = await client.ListServicesAsync();

        if (services.Count == 0)
        {
            WriteWarning("No gRPC services found. Is server reflection enabled?");
            return 0;
        }

        foreach (var svc in services)
        {
            var methodCount = svc.Methods.Count;
            Write($"{Cyan(svc.Name)}{Dim($"  ({methodCount} method{(methodCount != 1 ? "s" : "")})")}");

            if (cli.Verbose)
            {
                foreach (var method in svc.Methods)
                {
                    var tag = method.MethodType switch
                    {
                        "Unary" => "",
                        "ServerStreaming" => Dim(" [server-streaming]"),
                        "ClientStreaming" => Dim(" [client-streaming]"),
                        "Duplex" => Dim(" [duplex]"),
                        _ => ""
                    };
                    Write($"  {method.Name}{tag}");
                }
            }
        }

        return 0;
    }

    private static async Task<int> DescribeImplAsync(CliCommandOptions cli)
    {
        if (cli.Target is null)
        {
            WriteError("Usage: bowire describe --url <url> <service>[/<method>]");
            return 2;
        }

        using var client = new GrpcReflectionClient(cli.Url, showInternalServices: false);

        // Check if target contains a method name (service/method)
        if (cli.Target.Contains('/'))
        {
            var parts = cli.Target.Split('/', 2);
            var serviceName = parts[0];
            var methodName = parts[1];

            var services = await client.ListServicesAsync();
            var svc = services.FirstOrDefault(s => s.Name == serviceName);
            if (svc is null)
            {
                WriteError($"Service '{serviceName}' not found.");
                return 2;
            }

            var method = svc.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method is null)
            {
                WriteError($"Method '{methodName}' not found in service '{serviceName}'.");
                return 2;
            }

            DescribeMethod(method, detailed: true);
        }
        else
        {
            var services = await client.ListServicesAsync();
            var svc = services.FirstOrDefault(s => s.Name == cli.Target);
            if (svc is null)
            {
                WriteError($"Service '{cli.Target}' not found.");
                return 2;
            }

            DescribeService(svc);
        }

        return 0;
    }

    private static async Task<int> CallImplAsync(CliCommandOptions cli)
    {
        if (cli.Target is null || !cli.Target.Contains('/'))
        {
            WriteError("Usage: bowire call --url <url> <service>/<method> -d '<json>'");
            return 2;
        }

        var parts = cli.Target.Split('/', 2);
        var serviceName = parts[0];
        var methodName = parts[1];

        // Default to an empty JSON object when the user doesn't pass -d.
        // Unary calls take the first message; client-streaming calls
        // carry every -d as a separate frame.
        var messages = cli.Data.Count > 0 ? new List<string>(cli.Data) : ["{}"];

        // Expand @filename references in place so downstream invokers
        // see the concrete payload.
        for (var i = 0; i < messages.Count; i++)
        {
            if (!messages[i].StartsWith('@')) continue;
            var filePath = messages[i][1..];
            if (!File.Exists(filePath))
            {
                WriteError($"File not found: {filePath}");
                return 1;
            }
            messages[i] = await File.ReadAllTextAsync(filePath);
        }

        // Parse metadata headers "key: value"
        Dictionary<string, string>? metadata = null;
        if (cli.Headers.Count > 0)
        {
            metadata = new Dictionary<string, string>();
            foreach (var h in cli.Headers)
            {
                var colonIdx = h.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx > 0)
                {
                    var key = h[..colonIdx].Trim();
                    var value = h[(colonIdx + 1)..].Trim();
                    metadata[key] = value;
                }
            }
        }

        using var reflectionClient = new GrpcReflectionClient(cli.Url, showInternalServices: false);
        using var invoker = new GrpcInvoker(cli.Url, reflectionClient);

        // Try unary first, then streaming
        var result = await invoker.InvokeUnaryAsync(serviceName, methodName, messages, metadata);

        if (result.Status == "Use the streaming endpoint for server-streaming and duplex calls.")
        {
            // Server streaming or duplex -- use streaming invocation.
            // The CLI only needs the JSON rendering; the binary side of
            // the frame is for the mock-server recorder path.
            await foreach (var frame in invoker.InvokeStreamingWithFramesAsync(
                serviceName, methodName, messages, metadata))
            {
                WriteJsonResponse(frame.Json, cli.Compact);
            }
            return 0;
        }

        if (result.Status != "OK")
        {
            WriteError($"gRPC error: {result.Status}");
            if (result.Response is not null)
                WriteError($"  {result.Response}");

            if (result.Metadata.Count > 0)
            {
                WriteError("  Trailers:");
                foreach (var entry in result.Metadata)
                    WriteError($"    {entry.Key}: {entry.Value}");
            }

            return 2;
        }

        // Print response
        if (result.Response is not null)
            WriteJsonResponse(result.Response, cli.Compact);

        // Print timing to stderr (so it doesn't interfere with piped output)
        if (!Console.IsErrorRedirected)
            await Console.Error.WriteLineAsync(Dim($"  {result.DurationMs}ms"));

        return 0;
    }

    private static void WriteJsonResponse(string json, bool compact)
    {
        if (compact)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, CompactJson));
            }
            catch
            {
                Console.WriteLine(json);
            }
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    private static void DescribeService(BowireServiceInfo svc)
    {
        Write($"{Bold(Cyan(svc.Name))}");
        if (!string.IsNullOrEmpty(svc.Package))
            Write($"{Dim($"  package: {svc.Package}")}");
        Write("");

        foreach (var method in svc.Methods)
            DescribeMethod(method, detailed: false);
    }

    private static void DescribeMethod(BowireMethodInfo method, bool detailed)
    {
        var streamTag = method.MethodType switch
        {
            "Unary" => Dim("unary"),
            "ServerStreaming" => Dim("server-streaming"),
            "ClientStreaming" => Dim("client-streaming"),
            "Duplex" => Dim("duplex"),
            _ => Dim(method.MethodType)
        };

        Write($"  {Bold(method.Name)} {streamTag}");
        Write($"    {Dim("rpc")} {method.Name}({Cyan(method.InputType.Name)}) {Dim("returns")} ({Cyan(method.OutputType.Name)})");

        if (detailed)
        {
            Write("");
            Write($"  {Bold("Request:")} {Cyan(method.InputType.FullName)}");
            DescribeMessage(method.InputType, indent: 4, visited: []);
            Write("");
            Write($"  {Bold("Response:")} {Cyan(method.OutputType.FullName)}");
            DescribeMessage(method.OutputType, indent: 4, visited: []);
        }

        Write("");
    }

    private static void DescribeMessage(BowireMessageInfo msg, int indent, HashSet<string> visited)
    {
        if (msg.Fields.Count == 0)
            return;

        if (!visited.Add(msg.FullName))
        {
            Write($"{new string(' ', indent)}{Dim($"(recursive: {msg.Name})")}");
            return;
        }

        foreach (var field in msg.Fields)
        {
            var prefix = new string(' ', indent);
            var label = field.IsRepeated ? "repeated " : field.IsMap ? "map " : "";
            var typeName = field.Type;

            if (field.MessageType is not null)
                typeName = Cyan(field.MessageType.Name);
            else if (field.EnumValues is not null)
                typeName = Cyan(field.Type);

            Write($"{prefix}{Dim(label)}{typeName} {field.Name}{Dim($" = {field.Number}")}");

            if (field.EnumValues is not null)
            {
                foreach (var ev in field.EnumValues)
                    Write($"{prefix}  {Dim($"{ev.Name} = {ev.Number}")}");
            }

            if (field.MessageType is not null && field.MessageType.Fields.Count > 0)
                DescribeMessage(field.MessageType, indent + 2, visited);
        }
    }


    // ---- Console formatting helpers ----

    private static void Write(string text) => Console.WriteLine(text);

    private static void WriteError(string text)
    {
        if (UseColor)
            Console.Error.WriteLine($"\x1b[31m{text}\x1b[0m");
        else
            Console.Error.WriteLine(text);
    }

    private static void WriteWarning(string text)
    {
        if (UseColor)
            Console.Error.WriteLine($"\x1b[33m{text}\x1b[0m");
        else
            Console.Error.WriteLine(text);
    }

    private static string Cyan(string text) =>
        UseColor ? $"\x1b[36m{text}\x1b[0m" : text;

    private static string Bold(string text) =>
        UseColor ? $"\x1b[1m{text}\x1b[0m" : text;

    private static string Dim(string text) =>
        UseColor ? $"\x1b[2m{text}\x1b[0m" : text;
}
