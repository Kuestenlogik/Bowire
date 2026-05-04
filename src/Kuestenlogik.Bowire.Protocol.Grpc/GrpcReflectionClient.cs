// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Google.Api;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Queries gRPC Server Reflection to discover services, methods, and message schemas.
/// </summary>
internal sealed class GrpcReflectionClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ServerReflection.ServerReflectionClient _client;
    private readonly bool _showInternalServices;
    private readonly MtlsHandlerOwner? _mtlsOwner;

    private static readonly HashSet<string> InternalServices =
    [
        "grpc.reflection.v1alpha.ServerReflection",
        "grpc.reflection.v1.ServerReflection",
        "grpc.health.v1.Health",
        "grpc.channelz.v1.Channelz"
    ];

    /// <summary>
    /// Extension registry that knows about the <c>google.api.http</c> custom
    /// option. Without this, Google.Protobuf C# stores extension bytes as
    /// unknown fields and <c>GetExtension(...)</c> returns null. Cached as a
    /// shared parser instance for the whole client.
    /// </summary>
    private static readonly MessageParser<FileDescriptorProto> DescriptorParser =
        FileDescriptorProto.Parser.WithExtensionRegistry(new ExtensionRegistry
        {
            AnnotationsExtensions.Http
        });

    public GrpcReflectionClient(
        string serverUrl,
        bool showInternalServices,
        MtlsConfig? mtlsConfig = null,
        IConfiguration? configuration = null)
    {
        _showInternalServices = showInternalServices;

        SocketsHttpHandler httpHandler;
        if (mtlsConfig is not null)
        {
            // mTLS path stays on its dedicated handler-owner — that already
            // installs cert presentation + chain validation against the
            // client/CA pair the user configured, so localhost-cert
            // relaxation would be both redundant and confusing.
            _mtlsOwner = MtlsHandlerOwner.CreateSocketsHttpHandler(mtlsConfig, out var mtlsError);
            if (_mtlsOwner is null)
            {
                throw new InvalidOperationException(mtlsError ?? "mTLS configuration invalid");
            }
            httpHandler = (SocketsHttpHandler)_mtlsOwner.Handler;
        }
        else
        {
            // Non-mTLS path picks up the same Bowire:TrustLocalhostCert
            // opt-in the HttpClient-based plugins use. Configuration null
            // (test paths) yields a vanilla handler with no relaxation.
            httpHandler = BowireHttpClientFactory.CreateSocketsHttpHandler(
                configuration, "grpc", serverUrl);
        }

        _channel = GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions
        {
            HttpHandler = httpHandler,
            DisposeHttpClient = false  // owned by us / by _mtlsOwner
        });
        _client = new ServerReflection.ServerReflectionClient(_channel);
    }

    /// <summary>
    /// Lists all gRPC services available on the server via reflection.
    /// </summary>
    public async Task<List<BowireServiceInfo>> ListServicesAsync(CancellationToken ct = default)
    {
        var services = new List<BowireServiceInfo>();

        using var call = _client.ServerReflectionInfo(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new ServerReflectionRequest
        {
            ListServices = ""
        }, ct);

        await call.RequestStream.CompleteAsync();

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response.ListServicesResponse is null)
                continue;

            foreach (var svc in response.ListServicesResponse.Service)
            {
                if (!_showInternalServices && InternalServices.Contains(svc.Name))
                    continue;

                var serviceInfo = await GetServiceInfoAsync(svc.Name, ct);
                if (serviceInfo is not null)
                    services.Add(serviceInfo);
            }
        }

        return services.OrderBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Gets detailed service information including methods and message schemas.
    /// </summary>
    private async Task<BowireServiceInfo?> GetServiceInfoAsync(string serviceName, CancellationToken ct)
    {
        using var call = _client.ServerReflectionInfo(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new ServerReflectionRequest
        {
            FileContainingSymbol = serviceName
        }, ct);

        await call.RequestStream.CompleteAsync();

        var fileDescriptors = new List<FileDescriptorProto>();

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response.FileDescriptorResponse is null)
                continue;

            foreach (var bytes in response.FileDescriptorResponse.FileDescriptorProto)
            {
                var proto = DescriptorParser.ParseFrom(bytes);
                fileDescriptors.Add(proto);
            }
        }

        return BuildServiceInfo(serviceName, fileDescriptors);
    }

    /// <summary>
    /// Gets file descriptors for a service (used by the invoker to build dynamic messages).
    /// </summary>
    public async Task<List<FileDescriptorProto>> GetFileDescriptorsAsync(
        string serviceName, CancellationToken ct = default)
    {
        using var call = _client.ServerReflectionInfo(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new ServerReflectionRequest
        {
            FileContainingSymbol = serviceName
        }, ct);

        await call.RequestStream.CompleteAsync();

        var fileDescriptors = new List<FileDescriptorProto>();

        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response.FileDescriptorResponse is null)
                continue;

            foreach (var bytes in response.FileDescriptorResponse.FileDescriptorProto)
            {
                var proto = DescriptorParser.ParseFrom(bytes);
                fileDescriptors.Add(proto);
            }
        }

        return fileDescriptors;
    }

    /// <summary>
    /// Resolves transitive file descriptor dependencies for a given symbol.
    /// </summary>
    public async Task<List<FileDescriptorProto>> ResolveAllDescriptorsAsync(
        string serviceName, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, FileDescriptorProto>();
        var toResolve = new Queue<string>();
        toResolve.Enqueue(serviceName);

        // First, get file descriptors for the service symbol
        var initial = await GetFileDescriptorsAsync(serviceName, ct);
        foreach (var fd in initial)
        {
            if (!resolved.ContainsKey(fd.Name))
            {
                resolved[fd.Name] = fd;
                foreach (var dep in fd.Dependency)
                {
                    if (!resolved.ContainsKey(dep))
                        toResolve.Enqueue(dep);
                }
            }
        }

        // Resolve transitive dependencies by file name
        while (toResolve.Count > 0)
        {
            var name = toResolve.Dequeue();
            if (resolved.ContainsKey(name))
                continue;

            var deps = await GetFileDescriptorsByNameAsync(name, ct);
            foreach (var fd in deps)
            {
                if (!resolved.ContainsKey(fd.Name))
                {
                    resolved[fd.Name] = fd;
                    foreach (var dep in fd.Dependency)
                    {
                        if (!resolved.ContainsKey(dep))
                            toResolve.Enqueue(dep);
                    }
                }
            }
        }

        return [.. resolved.Values];
    }

    private async Task<List<FileDescriptorProto>> GetFileDescriptorsByNameAsync(
        string fileName, CancellationToken ct)
    {
        using var call = _client.ServerReflectionInfo(cancellationToken: ct);

        await call.RequestStream.WriteAsync(new ServerReflectionRequest
        {
            FileByFilename = fileName
        }, ct);

        await call.RequestStream.CompleteAsync();

        var result = new List<FileDescriptorProto>();
        await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
        {
            if (response.FileDescriptorResponse is null)
                continue;

            foreach (var bytes in response.FileDescriptorResponse.FileDescriptorProto)
            {
                result.Add(DescriptorParser.ParseFrom(bytes));
            }
        }

        return result;
    }

    private static BowireServiceInfo? BuildServiceInfo(
        string serviceName, List<FileDescriptorProto> fileDescriptors)
    {
        // Find the service definition across all file descriptors
        foreach (var fd in fileDescriptors)
        {
            foreach (var svc in fd.Service)
            {
                var fullName = string.IsNullOrEmpty(fd.Package)
                    ? svc.Name
                    : $"{fd.Package}.{svc.Name}";

                if (fullName != serviceName)
                    continue;

                var methods = new List<BowireMethodInfo>();
                foreach (var method in svc.Method)
                {
                    var inputType = ResolveMessageType(method.InputType, fileDescriptors);
                    var outputType = ResolveMessageType(method.OutputType, fileDescriptors);

                    var clientStreaming = method.ClientStreaming;
                    var serverStreaming = method.ServerStreaming;

                    var methodType = (clientStreaming, serverStreaming) switch
                    {
                        (false, false) => "Unary",
                        (false, true) => "ServerStreaming",
                        (true, false) => "ClientStreaming",
                        (true, true) => "Duplex"
                    };

                    // Pull google.api.http transcoding annotation if the proto
                    // declared one — Bowire reuses the existing HttpMethod /
                    // HttpPath fields so the sidebar can show an HTTP verb
                    // badge alongside the gRPC method type.
                    var (httpMethod, httpPath) = ExtractHttpRule(method);

                    // When transcoding is present, mark each input field with
                    // its HTTP source (path/query/body) so RestInvoker can route
                    // it correctly when the user picks "via HTTP" mode.
                    var annotatedInput = httpMethod is not null && httpPath is not null
                        ? AnnotateInputForTranscoding(inputType, httpMethod, httpPath)
                        : inputType;

                    methods.Add(new BowireMethodInfo(
                        Name: method.Name,
                        FullName: $"{fullName}/{method.Name}",
                        ClientStreaming: clientStreaming,
                        ServerStreaming: serverStreaming,
                        InputType: annotatedInput,
                        OutputType: outputType,
                        MethodType: methodType)
                    {
                        HttpMethod = httpMethod,
                        HttpPath = httpPath
                    });
                }

                // Bundle every file descriptor we pulled (the service's own
                // proto plus every transitively-resolved dependency) into a
                // single FileDescriptorSet and attach it to the service.
                // The Phase-1c mock server uses this to expose gRPC Server
                // Reflection so a second Bowire workbench can auto-discover
                // the mocked services.
                var descriptorSet = new FileDescriptorSet();
                descriptorSet.File.AddRange(fileDescriptors);

                return new BowireServiceInfo(
                    Name: fullName,
                    Package: fd.Package,
                    Methods: methods)
                {
                    SchemaDescriptor = descriptorSet.ToByteArray()
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the <c>google.api.http</c> annotation from a gRPC method
    /// descriptor when present, returning (HttpVerb, PathTemplate) — or
    /// (null, null) when the method has no transcoding annotation.
    ///
    /// The annotation is a custom proto extension on <see cref="MethodOptions"/>.
    /// Google.Protobuf C# stores extension bytes in the message's unknown
    /// fields collection on first deserialization, then lazy-decodes them
    /// when <c>GetExtension(...)</c> is called with the typed extension.
    /// Requires <c>Google.Api.CommonProtos</c> to provide
    /// <see cref="AnnotationsExtensions.Http"/>.
    /// </summary>
    private static (string? HttpMethod, string? HttpPath) ExtractHttpRule(MethodDescriptorProto method)
    {
        if (method.Options is null) return (null, null);
        try
        {
            var rule = method.Options.GetExtension(AnnotationsExtensions.Http);
            if (rule is null) return (null, null);

            return rule.PatternCase switch
            {
                HttpRule.PatternOneofCase.Get    => ("GET",     rule.Get),
                HttpRule.PatternOneofCase.Post   => ("POST",    rule.Post),
                HttpRule.PatternOneofCase.Put    => ("PUT",     rule.Put),
                HttpRule.PatternOneofCase.Delete => ("DELETE",  rule.Delete),
                HttpRule.PatternOneofCase.Patch  => ("PATCH",   rule.Patch),
                HttpRule.PatternOneofCase.Custom => (rule.Custom?.Kind?.ToUpperInvariant(), rule.Custom?.Path),
                _ => (null, null)
            };
        }
        catch
        {
            // Extension not present or unreadable — silently fall through
            return (null, null);
        }
    }

    /// <summary>
    /// Marks each top-level input field with its HTTP source bucket so the
    /// REST invoker can route values correctly when the user picks "via HTTP"
    /// mode for a transcoded gRPC method:
    ///
    ///   - Fields whose name matches a <c>{placeholder}</c> in the path → "path"
    ///   - For GET/DELETE/HEAD/OPTIONS, remaining fields → "query"
    ///   - For POST/PUT/PATCH, remaining fields → "body"
    ///
    /// Returns a new <see cref="BowireMessageInfo"/> with the annotated fields.
    /// </summary>
    private static BowireMessageInfo AnnotateInputForTranscoding(
        BowireMessageInfo input, string httpMethod, string pathTemplate)
    {
        var pathPlaceholders = ExtractPathPlaceholders(pathTemplate);
        var defaultSource = httpMethod switch
        {
            "GET" or "DELETE" or "HEAD" or "OPTIONS" => "query",
            _ => "body"
        };

        var newFields = new List<BowireFieldInfo>(input.Fields.Count);
        foreach (var f in input.Fields)
        {
            var source = pathPlaceholders.Contains(f.Name) ? "path" : defaultSource;
            newFields.Add(f with { Source = source });
        }
        return input with { Fields = newFields };
    }

    private static HashSet<string> ExtractPathPlaceholders(string template)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while ((i = template.IndexOf('{', i)) >= 0)
        {
            var end = template.IndexOf('}', i + 1);
            if (end < 0) break;
            // Strip any =subpath suffixes ({name=foo/*}) — gRPC allows them but
            // Bowire matches by the bare name.
            var inner = template.Substring(i + 1, end - i - 1);
            var equalsIdx = inner.IndexOf('=', StringComparison.Ordinal);
            var name = equalsIdx >= 0 ? inner.Substring(0, equalsIdx) : inner;
            result.Add(name);
            i = end + 1;
        }
        return result;
    }

    private static BowireMessageInfo ResolveMessageType(
        string typeName, List<FileDescriptorProto> fileDescriptors)
    {
        // Strip leading dot from fully-qualified type name
        var name = typeName.TrimStart('.');
        var visited = new HashSet<string>();
        return ResolveMessageTypeRecursive(name, fileDescriptors, visited);
    }

    private static BowireMessageInfo ResolveMessageTypeRecursive(
        string fullName, List<FileDescriptorProto> fileDescriptors, HashSet<string> visited)
    {
        if (!visited.Add(fullName))
            return new BowireMessageInfo(fullName.Split('.').Last(), fullName, []);

        foreach (var fd in fileDescriptors)
        {
            var msg = FindMessageInFile(fd.Package, fd.MessageType, fullName);
            if (msg is not null)
            {
                var fields = new List<BowireFieldInfo>();
                foreach (var field in msg.Field)
                {
                    BowireMessageInfo? nestedMsg = null;
                    List<BowireEnumValue>? enumValues = null;

                    if (field.Type == FieldDescriptorProto.Types.Type.Message)
                    {
                        var nestedName = field.TypeName.TrimStart('.');

                        // Check if this is a map field
                        var isMap = IsMapField(msg, field);
                        if (!isMap)
                        {
                            nestedMsg = ResolveMessageTypeRecursive(
                                nestedName, fileDescriptors, visited);
                        }
                        else
                        {
                            nestedMsg = ResolveMessageTypeRecursive(
                                nestedName, fileDescriptors, visited);
                        }
                    }
                    else if (field.Type == FieldDescriptorProto.Types.Type.Enum)
                    {
                        enumValues = ResolveEnumValues(
                            field.TypeName.TrimStart('.'), fileDescriptors);
                    }

                    var isRepeated =
                        field.Label == FieldDescriptorProto.Types.Label.Repeated;
                    var isMap2 = IsMapField(msg, field);

                    fields.Add(new BowireFieldInfo(
                        Name: field.Name,
                        Number: field.Number,
                        Type: MapFieldType(field.Type),
                        Label: MapFieldLabel(field.Label),
                        IsMap: isMap2,
                        IsRepeated: isRepeated && !isMap2,
                        MessageType: nestedMsg,
                        EnumValues: enumValues));
                }

                return new BowireMessageInfo(
                    Name: msg.Name,
                    FullName: fullName,
                    Fields: fields);
            }
        }

        // Type not found in descriptors - return stub
        return new BowireMessageInfo(fullName.Split('.').Last(), fullName, []);
    }

    private static DescriptorProto? FindMessageInFile(
        string packageName, IList<DescriptorProto> messages, string fullName)
    {
        foreach (var msg in messages)
        {
            var msgFullName = string.IsNullOrEmpty(packageName)
                ? msg.Name
                : $"{packageName}.{msg.Name}";

            if (msgFullName == fullName)
                return msg;

            // Search nested types
            var nested = FindMessageInFile(msgFullName, msg.NestedType, fullName);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static bool IsMapField(DescriptorProto parent, FieldDescriptorProto field)
    {
        if (field.Type != FieldDescriptorProto.Types.Type.Message
            || field.Label != FieldDescriptorProto.Types.Label.Repeated)
            return false;

        // Map fields use a synthetic nested type named "<FieldName>Entry"
        var entryName = $"{ToPascalCase(field.Name)}Entry";
        return parent.NestedType.Any(
            n => n.Name == entryName && n.Options is { MapEntry: true });
    }

    private static string ToPascalCase(string snakeCase)
    {
        return string.Concat(
            snakeCase.Split('_')
                .Select(s => s.Length > 0
                    ? char.ToUpperInvariant(s[0]) + s[1..]
                    : s));
    }

    private static List<BowireEnumValue>? ResolveEnumValues(
        string fullName, List<FileDescriptorProto> fileDescriptors)
    {
        foreach (var fd in fileDescriptors)
        {
            var enumType = FindEnumInFile(fd.Package, fd.EnumType, fd.MessageType, fullName);
            if (enumType is not null)
            {
                return enumType.Value.Select(v => new BowireEnumValue(v.Name, v.Number)).ToList();
            }
        }

        return null;
    }

    private static EnumDescriptorProto? FindEnumInFile(
        string packageName,
        IList<EnumDescriptorProto> enums,
        IList<DescriptorProto> messages,
        string fullName)
    {
        foreach (var e in enums)
        {
            var enumFullName = string.IsNullOrEmpty(packageName)
                ? e.Name
                : $"{packageName}.{e.Name}";

            if (enumFullName == fullName)
                return e;
        }

        // Search inside messages for nested enums
        foreach (var msg in messages)
        {
            var msgFullName = string.IsNullOrEmpty(packageName)
                ? msg.Name
                : $"{packageName}.{msg.Name}";

            foreach (var e in msg.EnumType)
            {
                if ($"{msgFullName}.{e.Name}" == fullName)
                    return e;
            }

            var nested = FindEnumInFile(msgFullName, [], msg.NestedType, fullName);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static string MapFieldType(FieldDescriptorProto.Types.Type type) => type switch
    {
        FieldDescriptorProto.Types.Type.Double => "double",
        FieldDescriptorProto.Types.Type.Float => "float",
        FieldDescriptorProto.Types.Type.Int64 => "int64",
        FieldDescriptorProto.Types.Type.Uint64 => "uint64",
        FieldDescriptorProto.Types.Type.Int32 => "int32",
        FieldDescriptorProto.Types.Type.Fixed64 => "fixed64",
        FieldDescriptorProto.Types.Type.Fixed32 => "fixed32",
        FieldDescriptorProto.Types.Type.Bool => "bool",
        FieldDescriptorProto.Types.Type.String => "string",
        FieldDescriptorProto.Types.Type.Group => "group",
        FieldDescriptorProto.Types.Type.Message => "message",
        FieldDescriptorProto.Types.Type.Bytes => "bytes",
        FieldDescriptorProto.Types.Type.Uint32 => "uint32",
        FieldDescriptorProto.Types.Type.Enum => "enum",
        FieldDescriptorProto.Types.Type.Sfixed32 => "sfixed32",
        FieldDescriptorProto.Types.Type.Sfixed64 => "sfixed64",
        FieldDescriptorProto.Types.Type.Sint32 => "sint32",
        FieldDescriptorProto.Types.Type.Sint64 => "sint64",
        _ => "unknown"
    };

    private static string MapFieldLabel(FieldDescriptorProto.Types.Label label) => label switch
    {
        FieldDescriptorProto.Types.Label.Optional => "optional",
        FieldDescriptorProto.Types.Label.Required => "required",
        FieldDescriptorProto.Types.Label.Repeated => "repeated",
        _ => "unknown"
    };

    public GrpcChannel Channel => _channel;

    public void Dispose()
    {
        _channel.Dispose();
        _mtlsOwner?.Dispose();
    }
}
