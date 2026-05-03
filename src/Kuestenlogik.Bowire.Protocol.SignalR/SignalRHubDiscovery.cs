// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Threading.Channels;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Discovers SignalR hubs from the application's endpoint routing.
/// This only works in embedded mode (same process).
/// </summary>
internal static class SignalRHubDiscovery
{
    /// <summary>
    /// Discovers SignalR hubs from the application's endpoint data sources.
    /// </summary>
    public static List<BowireServiceInfo> DiscoverHubs(IServiceProvider? serviceProvider)
    {
        if (serviceProvider is null) return [];

        var endpointSources = serviceProvider.GetService<IEnumerable<EndpointDataSource>>();
        if (endpointSources is null) return [];

        // MapHub<T>("/path") registers TWO endpoints carrying the same
        // HubMetadata: the hub route itself ("/path") and the negotiate
        // handshake route ("/path/negotiate"). Without filtering, the
        // sidebar would list every hub twice. Strategy:
        //   1. Skip anything ending in /negotiate — it's the SignalR
        //      handshake endpoint, not an addressable hub.
        //   2. Deduplicate by hub type via a HashSet in case MapHub has
        //      been called multiple times for the same type (rare but
        //      valid — e.g. to expose a hub on two paths).
        var services = new List<BowireServiceInfo>();
        var seenHubTypes = new HashSet<Type>();

        foreach (var source in endpointSources)
        {
            foreach (var endpoint in source.Endpoints)
            {
                var hubMetadata = endpoint.Metadata.GetMetadata<HubMetadata>();
                if (hubMetadata is null) continue;

                var hubType = hubMetadata.HubType;
                var hubName = hubType.Name;
                var path = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? $"/{hubName}";

                if (path.EndsWith("/negotiate", StringComparison.Ordinal)) continue;
                if (!seenHubTypes.Add(hubType)) continue;

                var methods = DiscoverHubMethods(hubType);

                services.Add(new BowireServiceInfo(
                    Name: hubName,
                    Package: path,
                    Methods: methods)
                { Source = "signalr" });
            }
        }

        return services;
    }

    private static List<BowireMethodInfo> DiscoverHubMethods(Type hubType)
    {
        var methods = new List<BowireMethodInfo>();

        var hubMethods = hubType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName);

        foreach (var method in hubMethods)
        {
            var isServerStream = IsStreamingReturn(method.ReturnType);

            var isClientStream = method.GetParameters()
                .Any(p => IsAsyncEnumerable(p.ParameterType) || IsChannelReader(p.ParameterType));

            var methodType = (isClientStream, isServerStream) switch
            {
                (false, false) => "Unary",
                (false, true) => "ServerStreaming",
                (true, false) => "ClientStreaming",
                (true, true) => "Duplex"
            };

            var inputType = BuildInputType(method);
            var outputType = BuildOutputType(method);

            methods.Add(new BowireMethodInfo(
                Name: method.Name,
                FullName: $"{hubType.Name}/{method.Name}",
                ClientStreaming: isClientStream,
                ServerStreaming: isServerStream,
                InputType: inputType,
                OutputType: outputType,
                MethodType: methodType));
        }

        return methods;
    }

    /// <summary>
    /// Checks if the return type represents a streaming response:
    /// IAsyncEnumerable&lt;T&gt;, Task&lt;IAsyncEnumerable&lt;T&gt;&gt;,
    /// ChannelReader&lt;T&gt;, Task&lt;ChannelReader&lt;T&gt;&gt;.
    /// </summary>
    private static bool IsStreamingReturn(Type returnType)
    {
        var unwrapped = UnwrapTask(returnType);
        return IsAsyncEnumerable(unwrapped) || IsChannelReader(unwrapped);
    }

    private static bool IsAsyncEnumerable(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            return true;

        return type.GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
    }

    private static bool IsChannelReader(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ChannelReader<>))
            return true;

        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ChannelReader<>))
                return true;
            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Builds a <see cref="BowireMessageInfo"/> from the method's parameters.
    /// Each parameter becomes a field. Streaming parameters (IAsyncEnumerable, ChannelReader)
    /// are unwrapped to their element type. CancellationToken is excluded.
    /// </summary>
    private static BowireMessageInfo BuildInputType(MethodInfo method)
    {
        var fields = new List<BowireFieldInfo>();
        var fieldNumber = 1;

        foreach (var param in method.GetParameters())
        {
            if (param.ParameterType == typeof(CancellationToken))
                continue;

            var paramType = param.ParameterType;

            // Unwrap streaming parameter types to get the element type
            if (IsAsyncEnumerable(paramType))
                paramType = GetAsyncEnumerableElementType(paramType) ?? paramType;
            else if (IsChannelReader(paramType))
                paramType = GetChannelReaderElementType(paramType) ?? paramType;

            fields.Add(new BowireFieldInfo(
                Name: param.Name ?? $"arg{fieldNumber}",
                Number: fieldNumber,
                Type: MapClrType(paramType),
                Label: "LABEL_OPTIONAL",
                IsMap: false,
                IsRepeated: IsCollectionType(paramType),
                MessageType: IsComplexType(paramType) ? BuildMessageInfoFromType(paramType) : null,
                EnumValues: paramType.IsEnum ? BuildEnumValues(paramType) : null));

            fieldNumber++;
        }

        var messageName = $"{method.Name}Request";
        return new BowireMessageInfo(
            Name: messageName,
            FullName: $"{method.DeclaringType?.Name}.{messageName}",
            Fields: fields);
    }

    /// <summary>
    /// Builds a <see cref="BowireMessageInfo"/> from the method's return type.
    /// Unwraps Task&lt;T&gt;, IAsyncEnumerable&lt;T&gt;, ChannelReader&lt;T&gt; etc.
    /// </summary>
    private static BowireMessageInfo BuildOutputType(MethodInfo method)
    {
        var returnType = method.ReturnType;
        var unwrapped = UnwrapReturnType(returnType);

        var fields = new List<BowireFieldInfo>();

        if (unwrapped != typeof(void) && unwrapped != typeof(object))
        {
            if (IsComplexType(unwrapped))
            {
                return BuildMessageInfoFromType(unwrapped);
            }

            fields.Add(new BowireFieldInfo(
                Name: "result",
                Number: 1,
                Type: MapClrType(unwrapped),
                Label: "LABEL_OPTIONAL",
                IsMap: false,
                IsRepeated: false,
                MessageType: null,
                EnumValues: unwrapped.IsEnum ? BuildEnumValues(unwrapped) : null));
        }

        var messageName = $"{method.Name}Response";
        return new BowireMessageInfo(
            Name: messageName,
            FullName: $"{method.DeclaringType?.Name}.{messageName}",
            Fields: fields);
    }

    /// <summary>
    /// Unwraps Task&lt;T&gt; to T. Returns the type itself if not a Task.
    /// </summary>
    private static Type UnwrapTask(Type type)
    {
        if (type == typeof(Task) || type == typeof(ValueTask))
            return typeof(void);

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
                return type.GetGenericArguments()[0];
        }

        return type;
    }

    /// <summary>
    /// Fully unwraps a return type: Task&lt;IAsyncEnumerable&lt;T&gt;&gt; -> T,
    /// IAsyncEnumerable&lt;T&gt; -> T, ChannelReader&lt;T&gt; -> T, Task&lt;T&gt; -> T.
    /// </summary>
    private static Type UnwrapReturnType(Type type)
    {
        var unwrapped = UnwrapTask(type);

        if (IsAsyncEnumerable(unwrapped))
            return GetAsyncEnumerableElementType(unwrapped) ?? typeof(object);

        if (IsChannelReader(unwrapped))
            return GetChannelReaderElementType(unwrapped) ?? typeof(object);

        return unwrapped;
    }

    private static Type? GetAsyncEnumerableElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            return type.GetGenericArguments()[0];

        var iface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));

        return iface?.GetGenericArguments()[0];
    }

    private static Type? GetChannelReaderElementType(Type type)
    {
        var current = type;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ChannelReader<>))
                return current.GetGenericArguments()[0];
            current = current.BaseType;
        }

        return null;
    }

    /// <summary>
    /// Maps a CLR type to a proto-like type name for display.
    /// </summary>
    internal static string MapClrType(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int32";
        if (type == typeof(long)) return "int64";
        if (type == typeof(uint)) return "uint32";
        if (type == typeof(ulong)) return "uint64";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte[])) return "bytes";
        if (type == typeof(byte)) return "uint32";
        if (type == typeof(short)) return "int32";
        if (type == typeof(ushort)) return "uint32";
        if (type == typeof(decimal)) return "double";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "google.protobuf.Timestamp";
        if (type == typeof(TimeSpan)) return "google.protobuf.Duration";
        if (type == typeof(Guid)) return "string";
        if (type == typeof(void)) return "google.protobuf.Empty";
        if (type == typeof(object)) return "google.protobuf.Any";

        // Nullable<T> -> unwrap
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
            return MapClrType(nullable);

        // Collections
        if (type.IsArray)
            return $"repeated {MapClrType(type.GetElementType()!)}";

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();

            if (def == typeof(List<>) || def == typeof(IList<>) ||
                def == typeof(IEnumerable<>) || def == typeof(ICollection<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
                return $"repeated {MapClrType(type.GetGenericArguments()[0])}";

            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) ||
                def == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                return $"map<{MapClrType(args[0])}, {MapClrType(args[1])}>";
            }
        }

        if (type.IsEnum) return "enum";

        return type.Name;
    }

    private static bool IsCollectionType(Type type)
    {
        if (type.IsArray) return true;
        if (!type.IsGenericType) return false;

        var def = type.GetGenericTypeDefinition();
        return def == typeof(List<>) || def == typeof(IList<>) ||
               def == typeof(IEnumerable<>) || def == typeof(ICollection<>) ||
               def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>);
    }

    private static bool IsComplexType(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) || type == typeof(Guid) ||
            type == typeof(byte[]) || type == typeof(void) || type == typeof(object))
            return false;

        if (Nullable.GetUnderlyingType(type) is not null) return false;
        if (type.IsEnum) return false;

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) ||
                def == typeof(IEnumerable<>) || def == typeof(ICollection<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>) ||
                def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) ||
                def == typeof(IReadOnlyDictionary<,>))
                return false;
        }

        return type.IsClass || type.IsValueType;
    }

    /// <summary>
    /// Builds a <see cref="BowireMessageInfo"/> by reflecting over the public properties of a complex type.
    /// </summary>
    private static BowireMessageInfo BuildMessageInfoFromType(Type type)
    {
        var fields = new List<BowireFieldInfo>();
        var fieldNumber = 1;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;

            fields.Add(new BowireFieldInfo(
                Name: prop.Name,
                Number: fieldNumber,
                Type: MapClrType(prop.PropertyType),
                Label: "LABEL_OPTIONAL",
                IsMap: IsDictionaryType(prop.PropertyType),
                IsRepeated: IsCollectionType(prop.PropertyType),
                MessageType: IsComplexType(prop.PropertyType) ? BuildMessageInfoFromType(prop.PropertyType) : null,
                EnumValues: prop.PropertyType.IsEnum ? BuildEnumValues(prop.PropertyType) : null));

            fieldNumber++;
        }

        return new BowireMessageInfo(
            Name: type.Name,
            FullName: type.FullName ?? type.Name,
            Fields: fields);
    }

    private static bool IsDictionaryType(Type type)
    {
        if (!type.IsGenericType) return false;
        var def = type.GetGenericTypeDefinition();
        return def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) ||
               def == typeof(IReadOnlyDictionary<,>);
    }

    private static List<BowireEnumValue> BuildEnumValues(Type enumType)
    {
        return Enum.GetValues(enumType)
            .Cast<object>()
            .Select(v => new BowireEnumValue(v.ToString()!, (int)v))
            .ToList();
    }
}
