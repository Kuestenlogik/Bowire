// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Google.Api;
using Google.Protobuf.Reflection;

namespace Kuestenlogik.Bowire.Tests.Grpc;

/// <summary>
/// The house rule for Bowire's own gRPC services: every rpc also has an HTTP
/// form. Unary and server-streaming rpcs carry a <c>google.api.http</c>
/// option for JSON transcoding; a client- or bidi-streaming rpc either has a
/// hand-written HTTP form or a stated exception below.
/// <para>
/// Every <c>Kuestenlogik.Bowire*.dll</c> next to this test assembly is
/// scanned — the test project references the tool, and with it every
/// service package — so a new service is held to the rule without being
/// listed here.
/// </para>
/// </summary>
public sealed class RpcHttpFormRuleTests
{
    // "package.Service/Method" → why it has no transcoded HTTP form.
    private static readonly Dictionary<string, string> s_exceptions = new(StringComparer.Ordinal);

    public static IEnumerable<FileDescriptor> BowireProtoFiles()
    {
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Kuestenlogik.Bowire*.dll"))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            foreach (var type in types)
            {
                if (type?.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static) is { } property
                    && property.PropertyType == typeof(FileDescriptor)
                    && property.GetValue(null) is FileDescriptor file
                    && file.Package.StartsWith("bowire.", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    [Fact]
    public void Every_Bowire_Rpc_Has_An_Http_Form()
    {
        var files = BowireProtoFiles().ToList();
        // At least the Interop and DataSets services; an empty scan must not pass.
        Assert.Contains(files, f => f.Package == "bowire.interop.v1");
        Assert.Contains(files, f => f.Package == "bowire.datasets.v1");

        var missing = new List<string>();
        foreach (var method in files.SelectMany(f => f.Services).SelectMany(s => s.Methods))
        {
            var id = $"{method.Service.FullName}/{method.Name}";
            if (s_exceptions.ContainsKey(id)) continue;
            var http = method.GetOptions()?.GetExtension(AnnotationsExtensions.Http);
            var transcodable = !method.IsClientStreaming;
            if (!transcodable || http is null || http.PatternCase == HttpRule.PatternOneofCase.None)
                missing.Add(id + (transcodable ? " (no google.api.http)" : " (client/bidi streaming: needs a hand-written HTTP form or an exception)"));
        }
        Assert.Empty(missing);
    }
}
