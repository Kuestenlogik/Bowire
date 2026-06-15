// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.App.Cli;
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

    // Color heuristic: same source as the pre-refactor static property —
    // "no colour when stdout looks redirected" — but evaluated per writer
    // so a test-supplied StringWriter (which isn't a TTY) gets plain
    // text while the production Console.Out keeps its ANSI sequences.
    private static bool UseColor(TextWriter writer) =>
        ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected;

    public static async Task<int> ListAsync(CliCommandOptions cli, TextWriter? stdout = null, TextWriter? stderr = null)
        => await RunWithErrorHandling(cli, CommandIo.Resolve(stdout, stderr), ListImplAsync).ConfigureAwait(false);
    public static async Task<int> DescribeAsync(CliCommandOptions cli, TextWriter? stdout = null, TextWriter? stderr = null)
        => await RunWithErrorHandling(cli, CommandIo.Resolve(stdout, stderr), DescribeImplAsync).ConfigureAwait(false);
    public static async Task<int> CallAsync(CliCommandOptions cli, TextWriter? stdout = null, TextWriter? stderr = null)
        => await RunWithErrorHandling(cli, CommandIo.Resolve(stdout, stderr), CallImplAsync).ConfigureAwait(false);

    private static async Task<int> RunWithErrorHandling(CliCommandOptions cli, CommandIo io,
        Func<CliCommandOptions, CommandIo, Task<int>> impl)
    {
        ArgumentNullException.ThrowIfNull(cli);
        // Top-level CLI error handler: anything thrown by an
        // impl (gRPC reflection, transcoding, JSON parse, plugin
        // call) gets rendered to stderr with exit 1.
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            return await impl(cli, io).ConfigureAwait(false);
        }
        catch (Exception ex)
#pragma warning restore CA1031
        {
            WriteError(io, ex.Message);
            if (ex.InnerException is not null)
                WriteError(io, $"  {ex.InnerException.Message}");
            return 1;
        }
    }

    private static async Task<int> ListImplAsync(CliCommandOptions cli, CommandIo io)
    {
        using var client = new GrpcReflectionClient(cli.Url, showInternalServices: false);
        var services = await client.ListServicesAsync();

        if (services.Count == 0)
        {
            WriteWarning(io, "No gRPC services found. Is server reflection enabled?");
            return 0;
        }

        var color = UseColor(io.Out);
        foreach (var svc in services)
        {
            var methodCount = svc.Methods.Count;
            Write(io, $"{Cyan(color, svc.Name)}{Dim(color, $"  ({methodCount} method{(methodCount != 1 ? "s" : "")})")}");

            if (cli.Verbose)
            {
                foreach (var method in svc.Methods)
                {
                    var tag = method.MethodType switch
                    {
                        "Unary" => "",
                        "ServerStreaming" => Dim(color, " [server-streaming]"),
                        "ClientStreaming" => Dim(color, " [client-streaming]"),
                        "Duplex" => Dim(color, " [duplex]"),
                        _ => ""
                    };
                    Write(io, $"  {method.Name}{tag}");
                }
            }
        }

        return 0;
    }

    private static async Task<int> DescribeImplAsync(CliCommandOptions cli, CommandIo io)
    {
        if (cli.Target is null)
        {
            WriteError(io, "Usage: bowire describe --url <url> <service>[/<method>]");
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
                WriteError(io, $"Service '{serviceName}' not found.");
                return 2;
            }

            var method = svc.Methods.FirstOrDefault(m => m.Name == methodName);
            if (method is null)
            {
                WriteError(io, $"Method '{methodName}' not found in service '{serviceName}'.");
                return 2;
            }

            DescribeMethod(io, method, detailed: true);
        }
        else
        {
            var services = await client.ListServicesAsync();
            var svc = services.FirstOrDefault(s => s.Name == cli.Target);
            if (svc is null)
            {
                WriteError(io, $"Service '{cli.Target}' not found.");
                return 2;
            }

            DescribeService(io, svc);
        }

        return 0;
    }

    private static async Task<int> CallImplAsync(CliCommandOptions cli, CommandIo io)
    {
        if (cli.Target is null || !cli.Target.Contains('/'))
        {
            WriteError(io, "Usage: bowire call --url <url> <service>/<method> -d '<json>'");
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
                WriteError(io, $"File not found: {filePath}");
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
                WriteJsonResponse(io, frame.Json, cli.Compact);
            }
            return 0;
        }

        if (result.Status != "OK")
        {
            WriteError(io, $"gRPC error: {result.Status}");
            if (result.Response is not null)
                WriteError(io, $"  {result.Response}");

            if (result.Metadata.Count > 0)
            {
                WriteError(io, "  Trailers:");
                foreach (var entry in result.Metadata)
                    WriteError(io, $"    {entry.Key}: {entry.Value}");
            }

            return 2;
        }

        // Print response
        if (result.Response is not null)
            WriteJsonResponse(io, result.Response, cli.Compact);

        // Print timing to stderr (so it doesn't interfere with piped output).
        // Only suppress for production-Console stderr when the OS reports
        // a redirect; the test-supplied StringWriter falls through and
        // always receives the timing line.
        if (!ReferenceEquals(io.Err, Console.Error) || !Console.IsErrorRedirected)
            await io.Err.WriteLineAsync(Dim(UseColor(io.Err), $"  {result.DurationMs}ms")).ConfigureAwait(false);

        return 0;
    }

    private static void WriteJsonResponse(CommandIo io, string json, bool compact)
    {
        if (compact)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                io.OutLine(JsonSerializer.Serialize(doc.RootElement, CompactJson));
            }
            catch
            {
                io.OutLine(json);
            }
        }
        else
        {
            io.OutLine(json);
        }
    }

    private static void DescribeService(CommandIo io, BowireServiceInfo svc)
    {
        var color = UseColor(io.Out);
        Write(io, $"{Bold(color, Cyan(color, svc.Name))}");
        if (!string.IsNullOrEmpty(svc.Package))
            Write(io, $"{Dim(color, $"  package: {svc.Package}")}");
        Write(io, "");

        foreach (var method in svc.Methods)
            DescribeMethod(io, method, detailed: false);
    }

    private static void DescribeMethod(CommandIo io, BowireMethodInfo method, bool detailed)
    {
        var color = UseColor(io.Out);
        var streamTag = method.MethodType switch
        {
            "Unary" => Dim(color, "unary"),
            "ServerStreaming" => Dim(color, "server-streaming"),
            "ClientStreaming" => Dim(color, "client-streaming"),
            "Duplex" => Dim(color, "duplex"),
            _ => Dim(color, method.MethodType)
        };

        Write(io, $"  {Bold(color, method.Name)} {streamTag}");
        Write(io, $"    {Dim(color, "rpc")} {method.Name}({Cyan(color, method.InputType.Name)}) {Dim(color, "returns")} ({Cyan(color, method.OutputType.Name)})");

        if (detailed)
        {
            Write(io, "");
            Write(io, $"  {Bold(color, "Request:")} {Cyan(color, method.InputType.FullName)}");
            DescribeMessage(io, method.InputType, indent: 4, visited: []);
            Write(io, "");
            Write(io, $"  {Bold(color, "Response:")} {Cyan(color, method.OutputType.FullName)}");
            DescribeMessage(io, method.OutputType, indent: 4, visited: []);
        }

        Write(io, "");
    }

    private static void DescribeMessage(CommandIo io, BowireMessageInfo msg, int indent, HashSet<string> visited)
    {
        if (msg.Fields.Count == 0)
            return;

        var color = UseColor(io.Out);
        if (!visited.Add(msg.FullName))
        {
            Write(io, $"{new string(' ', indent)}{Dim(color, $"(recursive: {msg.Name})")}");
            return;
        }

        foreach (var field in msg.Fields)
        {
            var prefix = new string(' ', indent);
            var label = field.IsRepeated ? "repeated " : field.IsMap ? "map " : "";
            var typeName = field.Type;

            if (field.MessageType is not null)
                typeName = Cyan(color, field.MessageType.Name);
            else if (field.EnumValues is not null)
                typeName = Cyan(color, field.Type);

            Write(io, $"{prefix}{Dim(color, label)}{typeName} {field.Name}{Dim(color, $" = {field.Number}")}");

            if (field.EnumValues is not null)
            {
                foreach (var ev in field.EnumValues)
                    Write(io, $"{prefix}  {Dim(color, $"{ev.Name} = {ev.Number}")}");
            }

            if (field.MessageType is not null && field.MessageType.Fields.Count > 0)
                DescribeMessage(io, field.MessageType, indent + 2, visited);
        }
    }


    // ---- Console formatting helpers ----

    private static void Write(CommandIo io, string text) => io.OutLine(text);

    private static void WriteError(CommandIo io, string text)
    {
        if (UseColor(io.Err))
            io.ErrLine($"\x1b[31m{text}\x1b[0m");
        else
            io.ErrLine(text);
    }

    private static void WriteWarning(CommandIo io, string text)
    {
        if (UseColor(io.Err))
            io.ErrLine($"\x1b[33m{text}\x1b[0m");
        else
            io.ErrLine(text);
    }

    private static string Cyan(bool useColor, string text) =>
        useColor ? $"\x1b[36m{text}\x1b[0m" : text;

    private static string Bold(bool useColor, string text) =>
        useColor ? $"\x1b[1m{text}\x1b[0m" : text;

    private static string Dim(bool useColor, string text) =>
        useColor ? $"\x1b[2m{text}\x1b[0m" : text;
}
