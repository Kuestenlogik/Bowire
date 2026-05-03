// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Rest;

/// <summary>
/// Builds Bowire services directly from the host's <see cref="IApiDescriptionGroupCollectionProvider"/>
/// when REST plugin runs inside an ASP.NET Core process. No HTTP, no OpenAPI parsing —
/// the plugin reads the same metadata that Microsoft.AspNetCore.OpenApi uses to generate
/// its document, so discovery is instant and there's no risk of stale schemas.
/// </summary>
internal static class EmbeddedDiscovery
{
    /// <summary>
    /// Returns true and a populated list when the supplied service provider exposes
    /// API descriptions. Returns false to signal "fall back to URL discovery".
    /// </summary>
    public static bool TryDiscover(IServiceProvider? serviceProvider, out List<BowireServiceInfo> services)
    {
        services = [];
        if (serviceProvider is null) return false;

        var apiExplorer = serviceProvider.GetService<IApiDescriptionGroupCollectionProvider>();
        if (apiExplorer is null) return false;

        var groups = apiExplorer.ApiDescriptionGroups;
        if (groups is null) return false;

        // Group operations by tag (Minimal API .WithTags() / [Tags] attribute) or by
        // the ApiDescription's GroupName as a fallback. Operations without any tag
        // info land in "Default".
        var byTag = new Dictionary<string, List<BowireMethodInfo>>(StringComparer.Ordinal);

        foreach (var group in groups.Items)
        {
            foreach (var api in group.Items)
            {
                var method = BuildMethod(api);
                if (method is null) continue;

                var tag = ExtractTag(api) ?? group.GroupName ?? "Default";
                if (!byTag.TryGetValue(tag, out var list))
                {
                    list = [];
                    byTag[tag] = list;
                }
                list.Add(method);
            }
        }

        if (byTag.Count == 0) return false;

        foreach (var (tag, methods) in byTag)
        {
            methods.Sort(static (a, b) => string.CompareOrdinal(a.HttpPath, b.HttpPath));
            services.Add(new BowireServiceInfo(
                Name: tag,
                Package: "REST",
                Methods: methods)
            {
                Source = "rest"
            });
        }

        return true;
    }

    private static BowireMethodInfo? BuildMethod(ApiDescription api)
    {
        if (string.IsNullOrEmpty(api.HttpMethod) || api.RelativePath is null) return null;

        var verb = api.HttpMethod.ToUpperInvariant();
        // Path templates from ApiExplorer don't have a leading slash; add one to
        // match what the OpenAPI parser path produces.
        var path = api.RelativePath.StartsWith('/') ? api.RelativePath : "/" + api.RelativePath;
        // ASP.NET Core's RelativePath includes route constraints inside placeholders
        // (e.g. "{id:int}", "{slug:regex(...)}"). Strip them so the placeholder name
        // matches the API description's parameter name.
        path = StripRouteConstraints(path);

        var fields = new List<BowireFieldInfo>();
        var fieldNumber = 1;

        if (api.ParameterDescriptions is not null)
        {
            foreach (var param in api.ParameterDescriptions)
            {
                if (string.IsNullOrEmpty(param.Name)) continue;
                var location = MapBindingSource(param.Source);
                if (location is null) continue;

                // Body parameters with a complex CLR type get flattened into one
                // field per public property, mirroring how OpenApiDiscovery handles
                // a body schema's Properties. Nested complex types recurse so the
                // form-based UI can edit deep request bodies without falling back
                // to raw JSON.
                if (location == "body" && param.Type is { } type && IsComplexType(type))
                {
                    foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (!prop.CanRead) continue;
                        fields.Add(BuildBodyField(prop.Name, prop.PropertyType, fieldNumber++, param.IsRequired));
                    }
                    continue;
                }

                fields.Add(new BowireFieldInfo(
                    Name: param.Name,
                    Number: fieldNumber++,
                    Type: MapType(param.Type),
                    Label: param.IsRequired ? "required" : "optional",
                    IsMap: false,
                    IsRepeated: false,
                    MessageType: null,
                    EnumValues: null)
                {
                    Source = location,
                    Required = param.IsRequired,
                    Description = null
                });
            }
        }

        // Method name comes from a few places — prefer explicit endpoint name,
        // then the C# method name, then a path-derived CamelCase name.
        var name = ExtractEndpointName(api)
                ?? ExtractMethodName(api)
                ?? DeriveNameFromPath(verb, path);

        var summary = ExtractMetadata<IEndpointSummaryMetadata>(api)?.Summary;
        var description = ExtractMetadata<IEndpointDescriptionMetadata>(api)?.Description;
        var deprecated = IsDeprecated(api);

        var inputType = new BowireMessageInfo(
            Name: name + "Request",
            FullName: name + "Request",
            Fields: fields);
        var outputType = new BowireMessageInfo(
            Name: name + "Response",
            FullName: name + "Response",
            Fields: []);

        return new BowireMethodInfo(
            Name: name,
            FullName: verb + " " + path,
            ClientStreaming: false,
            ServerStreaming: false,
            InputType: inputType,
            OutputType: outputType,
            MethodType: "Unary")
        {
            HttpMethod = verb,
            HttpPath = path,
            Summary = summary,
            Description = description,
            Deprecated = deprecated
        };
    }

    /// <summary>
    /// Detects whether the endpoint behind an ApiDescription has been marked
    /// deprecated. Two sources are checked:
    ///
    /// 1. <see cref="ObsoleteAttribute"/> in the endpoint metadata. ASP.NET Core
    ///    propagates this from a Minimal API delegate's method-level <c>[Obsolete]</c>
    ///    attribute (and from controller actions) into the endpoint metadata bag.
    ///
    /// 2. ControllerActionDescriptor.MethodInfo's ObsoleteAttribute, as a
    ///    fallback for older MVC pipelines.
    /// </summary>
    private static bool IsDeprecated(ApiDescription api)
    {
        var endpointMetadata = api.ActionDescriptor?.EndpointMetadata;
        if (endpointMetadata is not null)
        {
            foreach (var item in endpointMetadata)
            {
                if (item is ObsoleteAttribute) return true;
            }
        }

        // ControllerActionDescriptor exposes the underlying MethodInfo so we can
        // walk attributes directly. Use reflection by name so we don't take a
        // hard reference on Microsoft.AspNetCore.Mvc.Core just for this fallback.
        var ad = api.ActionDescriptor;
        if (ad is not null)
        {
            var methodInfoProp = ad.GetType().GetProperty("MethodInfo");
            if (methodInfoProp?.GetValue(ad) is System.Reflection.MethodInfo mi)
            {
                if (mi.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: true).Length > 0) return true;
                if (mi.DeclaringType?.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: true).Length > 0) return true;
            }
        }

        return false;
    }

    private static string? ExtractTag(ApiDescription api)
    {
        var tagsMetadata = ExtractMetadata<ITagsMetadata>(api);
        if (tagsMetadata?.Tags is { Count: > 0 } tags)
        {
            return tags[0];
        }
        return null;
    }

    private static string? ExtractEndpointName(ApiDescription api)
    {
        var nameMetadata = ExtractMetadata<IEndpointNameMetadata>(api);
        return string.IsNullOrEmpty(nameMetadata?.EndpointName) ? null : nameMetadata.EndpointName;
    }

    /// <summary>
    /// Returns the C# method name for controller-based endpoints — e.g.
    /// "GetForecast" / "GetCities". Skips compiler-generated lambda names
    /// (starting with an angle bracket), which Minimal APIs surface
    /// without a WithName().
    /// </summary>
    private static string? ExtractMethodName(ApiDescription api)
    {
        var ad = api.ActionDescriptor;
        if (ad is null) return null;

        // ControllerActionDescriptor has a public MethodInfo; we read it
        // reflectively so this project stays free of Mvc.Controllers deps.
        var methodInfoProp = ad.GetType().GetProperty("MethodInfo");
        if (methodInfoProp?.GetValue(ad) is not System.Reflection.MethodInfo mi) return null;

        var name = mi.Name;
        // Lambda / compiler-generated names look like "<<Main>$>b__0_0" — useless as labels.
        if (name.StartsWith('<') || name.Contains("b__", StringComparison.Ordinal)) return null;
        return name;
    }

    /// <summary>
    /// CamelCase name derived from the HTTP verb and the last meaningful path segment:
    /// GET /cities becomes GetCities, POST /api/todos becomes PostTodos, GET / becomes GetRoot.
    /// Route placeholders are dropped so "/forecast/{city}" becomes "GetForecast".
    /// </summary>
    private static string DeriveNameFromPath(string verb, string path)
    {
        // Normalise the verb to "Get" / "Post" / etc. — uppercase first letter, rest lowercased.
        // We target ASCII here (HTTP verbs are ASCII), so Ordinal casing dodges the CA1308
        // invariant-lowercase warning without changing the output for any real verb.
        var verbPart = verb.Length switch
        {
            0 => "",
            1 => char.ToUpperInvariant(verb[0]).ToString(),
            _ => char.ToUpperInvariant(verb[0]) + LowercaseAscii(verb[1..])
        };

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Walk back from the end and take the first non-placeholder segment.
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var seg = segments[i];
            if (seg.StartsWith('{') && seg.EndsWith('}')) continue;
            return verbPart + Capitalize(seg);
        }
        return verbPart + "Root";
    }

    private static string Capitalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Keep mixed-case intact ("WeatherForecast"); only force the first letter up.
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>
    /// ASCII-only lowercase. Avoids CA1308 (ToLowerInvariant) because the only
    /// callers are HTTP verbs, which are pure ASCII by spec.
    /// </summary>
    private static string LowercaseAscii(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var buf = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
        }
        return new string(buf);
    }

    private static T? ExtractMetadata<T>(ApiDescription api) where T : class
    {
        var endpointMetadata = api.ActionDescriptor?.EndpointMetadata;
        if (endpointMetadata is null) return null;
        foreach (var item in endpointMetadata)
        {
            if (item is T match) return match;
        }
        return null;
    }

    private static string? MapBindingSource(BindingSource? source)
    {
        if (source is null) return null;
        if (source == BindingSource.Path) return "path";
        if (source == BindingSource.Query) return "query";
        if (source == BindingSource.Header) return "header";
        if (source == BindingSource.Body) return "body";
        if (source == BindingSource.Form) return "body";
        // Special / Custom — skip
        return null;
    }

    /// <summary>
    /// Builds a body field for a CLR property. Recurses into complex types so
    /// nested classes (e.g. <c>Address</c> on a <c>CreateOrder</c>) become a
    /// nested <see cref="BowireMessageInfo"/> the form UI can render. Arrays
    /// of complex types become repeated message fields. Recursion depth is
    /// capped at 4 to keep cyclic types from blowing up the field tree.
    /// </summary>
    private static BowireFieldInfo BuildBodyField(string name, Type clrType, int number, bool required, int depth = 0)
    {
        var camelName = ToCamelCase(name);
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        // Detect array / collection of T — treat as repeated of element type.
        Type? elementType = null;
        var isRepeated = false;
        if (underlying != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying))
        {
            elementType = GetEnumerableElementType(underlying);
            if (elementType is not null) isRepeated = true;
        }

        var workingType = elementType ?? underlying;

        // Scalar (or array of scalars) — emit as a flat field
        if (!IsComplexType(workingType) || depth >= 4)
        {
            return new BowireFieldInfo(
                Name: camelName,
                Number: number,
                Type: MapType(workingType),
                Label: required ? "required" : "optional",
                IsMap: false,
                IsRepeated: isRepeated,
                MessageType: null,
                EnumValues: null)
            {
                Source = "body",
                Required = required
            };
        }

        // Complex type — walk its public properties into a nested BowireMessageInfo.
        var nested = BuildNestedMessage(workingType, depth + 1);
        return new BowireFieldInfo(
            Name: camelName,
            Number: number,
            Type: "message",
            Label: required ? "required" : "optional",
            IsMap: false,
            IsRepeated: isRepeated,
            MessageType: nested,
            EnumValues: null)
        {
            Source = "body",
            Required = required
        };
    }

    private static BowireMessageInfo BuildNestedMessage(Type type, int depth)
    {
        var fields = new List<BowireFieldInfo>();
        var n = 1;
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            // Required-ness for nested fields isn't tracked at the CLR level here;
            // default to false so the form doesn't lie about validation.
            fields.Add(BuildBodyField(prop.Name, prop.PropertyType, n++, required: false, depth: depth));
        }
        return new BowireMessageInfo(
            Name: type.Name,
            FullName: type.FullName ?? type.Name,
            Fields: fields);
    }

    private static Type? GetEnumerableElementType(Type collectionType)
    {
        if (collectionType.IsArray) return collectionType.GetElementType();
        if (collectionType.IsGenericType)
        {
            var args = collectionType.GetGenericArguments();
            if (args.Length == 1) return args[0];
        }
        // Walk implemented interfaces for IEnumerable<T>
        foreach (var iface in collectionType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }

    private static bool IsComplexType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsPrimitive) return false;
        if (underlying == typeof(string) || underlying == typeof(decimal)
            || underlying == typeof(Guid) || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset) || underlying == typeof(TimeSpan))
        {
            return false;
        }
        if (underlying.IsEnum) return false;
        // Arrays / collections are not "complex" for our flattening purposes —
        // the form-based UI handles repeated fields with a single body field.
        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying)) return false;
        return underlying.IsClass || (underlying.IsValueType && !underlying.IsPrimitive);
    }

    private static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s) || char.IsLower(s[0])) return s;
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    private static string MapType(Type? type)
    {
        if (type is null) return "string";
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return "string";
        if (underlying == typeof(bool)) return "bool";
        if (underlying == typeof(int) || underlying == typeof(short) || underlying == typeof(byte)) return "int32";
        if (underlying == typeof(long)) return "int64";
        if (underlying == typeof(float)) return "float";
        if (underlying == typeof(double) || underlying == typeof(decimal)) return "double";
        if (underlying == typeof(Guid)) return "string";
        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)) return "string";
        if (underlying.IsEnum) return "string";
        // Complex body types stay as "message" — JSON tab still works for editing
        return "message";
    }

    private static string SanitizeName(string s)
    {
        var chars = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            chars[i] = char.IsLetterOrDigit(c) ? c : '_';
        }
        return new string(chars);
    }

    /// <summary>
    /// Removes route constraint suffixes from a path template:
    /// <c>/users/{id:int}</c> → <c>/users/{id}</c>,
    /// <c>/users/{slug:regex(^[a-z]+$)}</c> → <c>/users/{slug}</c>.
    /// Also handles default values: <c>{name=foo}</c> → <c>{name}</c>.
    /// </summary>
    private static string StripRouteConstraints(string path)
    {
        if (path.IndexOf(':', StringComparison.Ordinal) < 0
            && path.IndexOf('=', StringComparison.Ordinal) < 0
            && path.IndexOf('?', StringComparison.Ordinal) < 0)
        {
            return path;
        }

        var sb = new System.Text.StringBuilder(path.Length);
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '{')
            {
                // Find the matching closing brace, accounting for nested braces in regex constraints
                var depth = 1;
                var end = i + 1;
                while (end < path.Length && depth > 0)
                {
                    if (path[end] == '{') depth++;
                    else if (path[end] == '}') depth--;
                    if (depth > 0) end++;
                }
                if (end >= path.Length)
                {
                    sb.Append(path, i, path.Length - i);
                    break;
                }

                // Inside the placeholder: take only up to the first ':', '=', or '?'
                var inner = path.AsSpan(i + 1, end - i - 1);
                var sepIndex = inner.Length;
                for (var k = 0; k < inner.Length; k++)
                {
                    var ch = inner[k];
                    if (ch == ':' || ch == '=' || ch == '?')
                    {
                        sepIndex = k;
                        break;
                    }
                }

                sb.Append('{');
                sb.Append(inner[..sepIndex]);
                sb.Append('}');
                i = end + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }
}
