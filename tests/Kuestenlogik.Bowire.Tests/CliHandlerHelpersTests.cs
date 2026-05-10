// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Reaches the private console-formatting helpers in <see cref="CliHandler"/>
/// (<c>WriteJsonResponse</c>, <c>DescribeService</c>, <c>DescribeMethod</c>,
/// <c>DescribeMessage</c>) via reflection. The async <c>List/Describe/Call</c>
/// entry points are public and covered by <see cref="CliHandlerTests"/>; this
/// suite drives the formatter branches directly so the recursion guard for
/// self-referential message types and the field-source fallbacks don't
/// require a live gRPC server to exercise.
/// </summary>
public sealed class CliHandlerHelpersTests
{
    private static MethodInfo Method(string name, params Type[] paramTypes) =>
        typeof(CliHandler).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: paramTypes, modifiers: null)!;

    private static BowireMessageInfo EmptyMessage() =>
        new("Empty", "demo.Empty", []);

    private static BowireMessageInfo SimpleMessage() =>
        new("Pet", "demo.Pet",
        [
            new BowireFieldInfo("id", 1, "int32", "OPTIONAL", false, false, null, null),
            new BowireFieldInfo("tags", 2, "string", "REPEATED", false, true, null, null),
            new BowireFieldInfo("attributes", 3, "string", "REPEATED", true, true, null, null),
        ]);

    private static BowireMessageInfo MessageWithEnum()
    {
        var enumValues = new List<BowireEnumValue>
        {
            new("ACTIVE", 0),
            new("DISABLED", 1),
        };
        return new BowireMessageInfo("Account", "demo.Account",
        [
            new BowireFieldInfo("status", 1, "Status", "OPTIONAL",
                false, false, null, enumValues),
        ]);
    }

    private static BowireMethodInfo MakeMethod(BowireMessageInfo input, BowireMessageInfo output, string methodType = "Unary") =>
        new("DoIt", "demo.Service.DoIt", false, false, input, output, methodType);

    private static BowireServiceInfo MakeService(BowireMessageInfo input, BowireMessageInfo output, string package = "demo") =>
        new("demo.Service", package, [MakeMethod(input, output)]);

    // We invoke without redirecting Console.Out — capturing the global
    // stream would race with other test classes running in parallel
    // under xUnit v3. Coverage still records line execution; the asserts
    // below verify the helpers reach completion without throwing on
    // each branch we want to hit.

    [Fact]
    public void WriteJsonResponse_NonCompact_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Method("WriteJsonResponse", typeof(string), typeof(bool))
                .Invoke(null, ["{ \"a\" : 1 }", false]));
        Assert.Null(ex);
    }

    [Fact]
    public void WriteJsonResponse_Compact_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Method("WriteJsonResponse", typeof(string), typeof(bool))
                .Invoke(null, ["{ \"a\" : 1, \"b\" : [1, 2] }", true]));
        Assert.Null(ex);
    }

    [Fact]
    public void WriteJsonResponse_Compact_InvalidJsonFallsBackToRawAndDoesNotThrow()
    {
        var ex = Record.Exception(() =>
            Method("WriteJsonResponse", typeof(string), typeof(bool))
                .Invoke(null, ["not json", true]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeService_EmptyPackage_DoesNotThrow()
    {
        var svc = MakeService(EmptyMessage(), EmptyMessage(), package: "");
        var ex = Record.Exception(() =>
            Method("DescribeService", typeof(BowireServiceInfo)).Invoke(null, [svc]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeService_WithPackage_DoesNotThrow()
    {
        var svc = MakeService(EmptyMessage(), EmptyMessage());
        var ex = Record.Exception(() =>
            Method("DescribeService", typeof(BowireServiceInfo)).Invoke(null, [svc]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeMethod_DetailedFalse_DoesNotThrow()
    {
        var method = MakeMethod(SimpleMessage(), SimpleMessage());
        var ex = Record.Exception(() =>
            Method("DescribeMethod", typeof(BowireMethodInfo), typeof(bool)).Invoke(null, [method, false]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeMethod_DetailedTrue_DoesNotThrow()
    {
        var method = MakeMethod(SimpleMessage(), SimpleMessage());
        var ex = Record.Exception(() =>
            Method("DescribeMethod", typeof(BowireMethodInfo), typeof(bool)).Invoke(null, [method, true]));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("ServerStreaming")]
    [InlineData("ClientStreaming")]
    [InlineData("Duplex")]
    [InlineData("WeirdCustomKind")]
    public void DescribeMethod_StreamTags_RenderedForEveryMethodType(string methodType)
    {
        // Each method-type triggers a different switch arm in the
        // streamTag computation — the default case covers the
        // fallthrough branch (Dim(method.MethodType)).
        var method = MakeMethod(EmptyMessage(), EmptyMessage(), methodType);
        var ex = Record.Exception(() =>
            Method("DescribeMethod", typeof(BowireMethodInfo), typeof(bool)).Invoke(null, [method, false]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeMethod_DetailedTrue_EnumValues_DoesNotThrow()
    {
        // Enum-bearing field exercises the enum-value emission branch
        // inside DescribeMessage (foreach over EnumValues).
        var method = MakeMethod(MessageWithEnum(), EmptyMessage());
        var ex = Record.Exception(() =>
            Method("DescribeMethod", typeof(BowireMethodInfo), typeof(bool)).Invoke(null, [method, true]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeMessage_RecursiveSelfReference_GuardsAgainstStackOverflow()
    {
        // A message whose field references back to itself (cycle).
        // The visited-set guard should short-circuit at the second
        // descent rather than blowing the call stack.
        var self = new BowireMessageInfo("Node", "demo.Node", []);
        var withCycle = self with
        {
            Fields = new List<BowireFieldInfo>
            {
                new("next", 1, "Node", "OPTIONAL", false, false, self, null),
            }
        };
        var cycle = withCycle with
        {
            Fields = new List<BowireFieldInfo>
            {
                new("next", 1, "Node", "OPTIONAL", false, false, withCycle, null),
            }
        };

        var ex = Record.Exception(() =>
            Method("DescribeMessage", typeof(BowireMessageInfo), typeof(int), typeof(HashSet<string>))
                .Invoke(null, [cycle, 0, new HashSet<string>()]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeMessage_AlreadyVisited_NoOps()
    {
        var msg = SimpleMessage();
        var visited = new HashSet<string> { msg.FullName };
        var ex = Record.Exception(() =>
            Method("DescribeMessage", typeof(BowireMessageInfo), typeof(int), typeof(HashSet<string>))
                .Invoke(null, [msg, 4, visited]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeMessage_EmptyFields_NoOp()
    {
        var ex = Record.Exception(() =>
            Method("DescribeMessage", typeof(BowireMessageInfo), typeof(int), typeof(HashSet<string>))
                .Invoke(null, [EmptyMessage(), 4, new HashSet<string>()]));
        Assert.Null(ex);
    }

    [Fact]
    public void DescribeMessage_NestedMessageField_RecursesIntoChild()
    {
        // Field with a non-null MessageType + non-empty children
        // exercises the inner DescribeMessage(field.MessageType, ...) call.
        var child = new BowireMessageInfo("Inner", "demo.Inner",
            [new BowireFieldInfo("flag", 1, "bool", "OPTIONAL", false, false, null, null)]);
        var parent = new BowireMessageInfo("Outer", "demo.Outer",
            [new BowireFieldInfo("inner", 1, "Inner", "OPTIONAL", false, false, child, null)]);

        var ex = Record.Exception(() =>
            Method("DescribeMessage", typeof(BowireMessageInfo), typeof(int), typeof(HashSet<string>))
                .Invoke(null, [parent, 4, new HashSet<string>()]));
        Assert.Null(ex);
    }
}
