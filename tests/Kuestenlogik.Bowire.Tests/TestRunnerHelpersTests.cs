// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json.Nodes;
using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Reaches the assertion-evaluation helpers inside <see cref="TestRunner"/>
/// (<c>EvaluateAssertion</c>, <c>WalkJsonPath</c>, <c>DeepEqualsLoose</c>,
/// <c>Contains</c>, <c>FormatActual</c>, <c>TypeOf</c>, <c>SubstituteVars</c>,
/// <c>MergeEnvironments</c>) via reflection. They're <c>private static</c> on
/// purpose — the runner is the only first-party caller — but they encode the
/// portable subset of the in-browser Tests-tab matcher and need direct
/// coverage. Using reflection avoids reshaping the production surface for
/// test-only access while still exercising every operator branch and JSON
/// shape the engine accepts.
/// </summary>
public sealed class TestRunnerHelpersTests
{
    private static readonly Type RunnerType =
        typeof(TestRunner);

    private static readonly Type AssertionType =
        Assembly.GetAssembly(typeof(TestRunner))!
            .GetType("Kuestenlogik.Bowire.App.Assertion", throwOnError: true)!;

    private static readonly Type AssertionResultType =
        Assembly.GetAssembly(typeof(TestRunner))!
            .GetType("Kuestenlogik.Bowire.App.AssertionResult", throwOnError: true)!;

    private static MethodInfo Method(string name) =>
        RunnerType.GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static T Invoke<T>(string method, params object?[] args) =>
        (T)Method(method).Invoke(null, args)!;

    private static JsonNode ParseObj(string json) => JsonNode.Parse(json)!;

    // -------------------- WalkJsonPath --------------------

    [Fact]
    public void WalkJsonPath_NullRoot_ReturnsNull()
    {
        var result = Method("WalkJsonPath").Invoke(null, [null, "anything"]);
        Assert.Null(result);
    }

    [Fact]
    public void WalkJsonPath_EmptyPath_ReturnsRoot()
    {
        var root = ParseObj("""{"a":1}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, ""]);
        Assert.Same(root, result);
    }

    [Fact]
    public void WalkJsonPath_SimpleObjectKey_ReturnsValue()
    {
        var root = ParseObj("""{"name":"Ada"}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "name"]);
        Assert.Equal("Ada", result!.GetValue<string>());
    }

    [Fact]
    public void WalkJsonPath_NestedObjectKey_ReturnsValue()
    {
        var root = ParseObj("""{"user":{"profile":{"age":30}}}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "user.profile.age"]);
        Assert.Equal(30, result!.GetValue<int>());
    }

    [Fact]
    public void WalkJsonPath_ArrayIndex_ReturnsElement()
    {
        var root = ParseObj("""{"items":[10,20,30]}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "items.1"]);
        Assert.Equal(20, result!.GetValue<int>());
    }

    [Fact]
    public void WalkJsonPath_ArrayNonNumericSegment_ReturnsNull()
    {
        var root = ParseObj("""{"items":[10,20]}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "items.x"]);
        Assert.Null(result);
    }

    [Fact]
    public void WalkJsonPath_ArrayOutOfRange_ReturnsNull()
    {
        var root = ParseObj("""{"items":[10]}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "items.5"]);
        Assert.Null(result);
    }

    [Fact]
    public void WalkJsonPath_ArrayNegativeIndex_ReturnsNull()
    {
        var root = ParseObj("""{"items":[10]}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "items.-1"]);
        Assert.Null(result);
    }

    [Fact]
    public void WalkJsonPath_PrimitiveAfterDescent_ReturnsNull()
    {
        // After landing on a string value, walking further has no object/
        // array to descend into → return null instead of throwing.
        var root = ParseObj("""{"a":"leaf"}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "a.deeper"]);
        Assert.Null(result);
    }

    [Fact]
    public void WalkJsonPath_MissingKey_ReturnsNull()
    {
        var root = ParseObj("""{"a":1}""");
        var result = (JsonNode?)Method("WalkJsonPath").Invoke(null, [root, "missing"]);
        Assert.Null(result);
    }

    // -------------------- TypeOf --------------------

    [Theory]
    [InlineData("string", "")]
    [InlineData("string", "hello")]
    public void TypeOf_StringViaJsonValue(string expected, string input)
    {
        Assert.Equal(expected, Invoke<string>("TypeOf", JsonValue.Create(input)));
    }

    [Fact]
    public void TypeOf_BoolJsonValueDirect_ReturnsBoolean()
    {
        // Direct JsonValue.Create<bool> stores CLR bool — TryGetValue<bool>
        // succeeds on this construction path even though it doesn't on
        // numeric ones (see TypeOf_NumericJsonValue_FromParsedJson).
        Assert.Equal("boolean", Invoke<string>("TypeOf", JsonValue.Create(true)));
        Assert.Equal("boolean", Invoke<string>("TypeOf", JsonValue.Create(false)));
    }

    [Fact]
    public void TypeOf_NumericJsonValue_FromParsedJson_ReturnsNumber()
    {
        // JsonValue.Create<int>(42) stores the value with CLR type int
        // and TryGetValue<double> returns false; the engine instead
        // sees these via JsonNode.Parse where the underlying token is
        // numeric and TryGetValue<double> succeeds.
        var fromInt = JsonNode.Parse("42")!;
        var fromDouble = JsonNode.Parse("3.14")!;
        Assert.Equal("number", Invoke<string>("TypeOf", fromInt));
        Assert.Equal("number", Invoke<string>("TypeOf", fromDouble));
    }

    [Fact]
    public void TypeOf_BoolFromParsedJson_ReturnsBoolean()
    {
        var node = JsonNode.Parse("true")!;
        Assert.Equal("boolean", Invoke<string>("TypeOf", node));
    }

    [Fact]
    public void TypeOf_StringFromParsedJson_ReturnsString()
    {
        var node = JsonNode.Parse("\"hello\"")!;
        Assert.Equal("string", Invoke<string>("TypeOf", node));
    }

    [Fact]
    public void TypeOf_JsonArray_ReturnsArray()
    {
        Assert.Equal("array", Invoke<string>("TypeOf", new JsonArray(1, 2)));
    }

    [Fact]
    public void TypeOf_JsonObject_ReturnsObject()
    {
        Assert.Equal("object", Invoke<string>("TypeOf", new JsonObject { ["a"] = 1 }));
    }

    [Fact]
    public void TypeOf_NullInput_ReturnsNull()
    {
        Assert.Equal("null", Invoke<string>("TypeOf", new object?[] { null }));
    }

    [Fact]
    public void TypeOf_PlainClrString_LowercaseClrTypeName()
    {
        // Non-JsonNode CLR objects fall through to the GetType().Name
        // lowercase branch — exercised here so the JS-typeof-compat
        // fallback is covered alongside the JsonValue variants above.
        Assert.Equal("string", Invoke<string>("TypeOf", "raw"));
    }

    // -------------------- FormatActual --------------------

    [Fact]
    public void FormatActual_Null_ReturnsEmpty()
    {
        Assert.Equal("", Invoke<string>("FormatActual", new object?[] { null }));
    }

    [Fact]
    public void FormatActual_StringJsonValue_ReturnsRawString()
    {
        Assert.Equal("hello", Invoke<string>("FormatActual", JsonValue.Create("hello")));
    }

    [Fact]
    public void FormatActual_NumericJsonValue_ReturnsJsonString()
    {
        Assert.Equal("42", Invoke<string>("FormatActual", JsonValue.Create(42)));
    }

    [Fact]
    public void FormatActual_JsonObject_ReturnsJsonString()
    {
        var obj = new JsonObject { ["a"] = 1 };
        Assert.Contains("\"a\":1", Invoke<string>("FormatActual", obj), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatActual_PlainObject_FallsBackToToString()
    {
        Assert.Equal("123", Invoke<string>("FormatActual", 123));
    }

    // -------------------- DeepEqualsLoose --------------------

    [Fact]
    public void DeepEqualsLoose_BothNull_True() =>
        Assert.True(Invoke<bool>("DeepEqualsLoose", new object?[] { null, null }));

    [Fact]
    public void DeepEqualsLoose_NullActualEmptyExpected_True() =>
        Assert.True(Invoke<bool>("DeepEqualsLoose", new object?[] { null, "" }));

    [Fact]
    public void DeepEqualsLoose_NullActualNonEmptyExpected_False() =>
        Assert.False(Invoke<bool>("DeepEqualsLoose", new object?[] { null, "x" }));

    [Fact]
    public void DeepEqualsLoose_NumericCoercion_True()
    {
        // JsonValue 42 vs string "42" — the numeric coercion branch
        // matches because TryNumber succeeds on both.
        Assert.True(Invoke<bool>("DeepEqualsLoose", JsonValue.Create(42), "42"));
    }

    [Fact]
    public void DeepEqualsLoose_BooleanCoercion_CaseInsensitive()
    {
        Assert.True(Invoke<bool>("DeepEqualsLoose", JsonValue.Create(true), "true"));
        Assert.True(Invoke<bool>("DeepEqualsLoose", JsonValue.Create(false), "FALSE"));
    }

    [Fact]
    public void DeepEqualsLoose_StringCompare_OrdinalCaseSensitive()
    {
        Assert.True(Invoke<bool>("DeepEqualsLoose", JsonValue.Create("hello"), "hello"));
        Assert.False(Invoke<bool>("DeepEqualsLoose", JsonValue.Create("hello"), "HELLO"));
    }

    // -------------------- Contains --------------------

    [Fact]
    public void Contains_StringSubstring_True()
    {
        Assert.True(Invoke<bool>("Contains", JsonValue.Create("hello world"), "world"));
    }

    [Fact]
    public void Contains_StringMissing_False()
    {
        Assert.False(Invoke<bool>("Contains", JsonValue.Create("hello"), "zzz"));
    }

    [Fact]
    public void Contains_JsonArrayHit_True()
    {
        var arr = new JsonArray("a", "b", "c");
        Assert.True(Invoke<bool>("Contains", arr, "b"));
    }

    [Fact]
    public void Contains_JsonArrayMiss_False()
    {
        var arr = new JsonArray("a", "b");
        Assert.False(Invoke<bool>("Contains", arr, "z"));
    }

    [Fact]
    public void Contains_NumericArrayCoerced()
    {
        var arr = new JsonArray(1, 2, 3);
        Assert.True(Invoke<bool>("Contains", arr, "2"));
    }

    // -------------------- SubstituteVars --------------------

    [Fact]
    public void SubstituteVars_NoTokens_ReturnsAsIs()
    {
        var env = new Dictionary<string, string>();
        Assert.Equal("plain text", Invoke<string>("SubstituteVars", "plain text", env));
    }

    [Fact]
    public void SubstituteVars_KnownKey_Replaced()
    {
        var env = new Dictionary<string, string> { ["host"] = "api.local" };
        Assert.Equal("https://api.local/v1", Invoke<string>("SubstituteVars", "https://${host}/v1", env));
    }

    [Fact]
    public void SubstituteVars_UnknownKey_LeavesTokenIntact()
    {
        var env = new Dictionary<string, string>();
        Assert.Equal("hi ${nope}", Invoke<string>("SubstituteVars", "hi ${nope}", env));
    }

    [Fact]
    public void SubstituteVars_DoubleDollar_EscapesToken()
    {
        var env = new Dictionary<string, string> { ["x"] = "wrong" };
        // $${x} → ${x} (literal, no substitution)
        Assert.Equal("${x}", Invoke<string>("SubstituteVars", "$${x}", env));
    }

    [Fact]
    public void SubstituteVars_NowVariable_ReplacedWithUnixSeconds()
    {
        var env = new Dictionary<string, string>();
        var result = Invoke<string>("SubstituteVars", "${now}", env);
        Assert.True(long.TryParse(result, out var n) && n > 1_000_000_000, $"expected unix-seconds, got {result}");
    }

    [Fact]
    public void SubstituteVars_NowMs_ReplacedWithUnixMillis()
    {
        var env = new Dictionary<string, string>();
        var result = Invoke<string>("SubstituteVars", "${nowMs}", env);
        Assert.True(long.TryParse(result, out var n) && n > 1_000_000_000_000, $"expected unix-millis, got {result}");
    }

    [Fact]
    public void SubstituteVars_TimestampVariable_RoundtripsAsIso8601()
    {
        var env = new Dictionary<string, string>();
        var result = Invoke<string>("SubstituteVars", "${timestamp}", env);
        Assert.True(DateTime.TryParse(result, out _), result);
    }

    [Fact]
    public void SubstituteVars_Uuid_ReplacedWithGuid()
    {
        var env = new Dictionary<string, string>();
        var result = Invoke<string>("SubstituteVars", "${uuid}", env);
        Assert.True(Guid.TryParse(result, out _));
    }

    [Fact]
    public void SubstituteVars_RandomReturnsParseableInt()
    {
        var env = new Dictionary<string, string>();
        var result = Invoke<string>("SubstituteVars", "${random}", env);
        Assert.True(int.TryParse(result, out _));
    }

    [Fact]
    public void SubstituteVars_NowPlusOffset()
    {
        var env = new Dictionary<string, string>();
        var nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = long.Parse(
            Invoke<string>("SubstituteVars", "${now+60}", env),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(result, nowS + 59, nowS + 61);
    }

    [Fact]
    public void SubstituteVars_NowMinusOffset()
    {
        var env = new Dictionary<string, string>();
        var nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = long.Parse(
            Invoke<string>("SubstituteVars", "${now-30}", env),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(result, nowS - 31, nowS - 29);
    }

    // -------------------- MergeEnvironments --------------------

    [Fact]
    public void MergeEnvironments_BothNull_ReturnsEmpty()
    {
        var merged = Invoke<Dictionary<string, string>>("MergeEnvironments", null, null);
        Assert.Empty(merged);
    }

    [Fact]
    public void MergeEnvironments_OnlyCollection_CopiesEntries()
    {
        var col = new Dictionary<string, string> { ["a"] = "1" };
        var merged = Invoke<Dictionary<string, string>>("MergeEnvironments", col, null);
        Assert.Equal("1", merged["a"]);
    }

    [Fact]
    public void MergeEnvironments_TestOverridesCollection()
    {
        var col = new Dictionary<string, string> { ["a"] = "from-coll", ["only-coll"] = "x" };
        var test = new Dictionary<string, string> { ["a"] = "from-test" };
        var merged = Invoke<Dictionary<string, string>>("MergeEnvironments", col, test);
        Assert.Equal("from-test", merged["a"]);
        Assert.Equal("x", merged["only-coll"]);
    }

    // -------------------- EvaluateAssertion --------------------

    private static object NewAssertion(string? path, string? op, string? expected)
    {
        var instance = Activator.CreateInstance(AssertionType)!;
        AssertionType.GetProperty("Path")!.SetValue(instance, path);
        AssertionType.GetProperty("Op")!.SetValue(instance, op);
        AssertionType.GetProperty("Expected")!.SetValue(instance, expected);
        return instance;
    }

    private static object NewAssertionResult()
    {
        var instance = Activator.CreateInstance(AssertionResultType)!;
        AssertionResultType.GetProperty("Path")!.SetValue(instance, "");
        AssertionResultType.GetProperty("Op")!.SetValue(instance, "");
        AssertionResultType.GetProperty("Expected")!.SetValue(instance, "");
        return instance;
    }

    private static bool RenderedPassed(object rendered) =>
        (bool)AssertionResultType.GetProperty("Passed")!.GetValue(rendered)!;

    private static string RenderedActualText(object rendered) =>
        (string)AssertionResultType.GetProperty("ActualText")!.GetValue(rendered)!;

    private static void Evaluate(object assertion, string status, JsonNode? parsed, string? raw,
        Dictionary<string, string> env, object rendered)
    {
        Method("EvaluateAssertion").Invoke(null, [assertion, status, parsed, raw, env, rendered]);
    }

    [Fact]
    public void EvaluateAssertion_Eq_StatusPath_TruePass()
    {
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("status", "eq", "OK"), "OK", null, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
        Assert.Equal("OK", RenderedActualText(rendered));
    }

    [Fact]
    public void EvaluateAssertion_Ne_DifferentValues_True()
    {
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("status", "ne", "Failed"), "OK", null, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_DefaultOp_IsEq()
    {
        var rendered = NewAssertionResult();
        // Op = null → defaults to "eq".
        Evaluate(NewAssertion("status", null, "OK"), "OK", null, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_ResponseFullBody_FallsBackToRawWhenJsonNull()
    {
        var rendered = NewAssertionResult();
        // path empty + parsedBody null → actual = raw string.
        Evaluate(NewAssertion("", "eq", "raw-bytes"), "OK", null, "raw-bytes",
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_ResponsePrefixStripped()
    {
        // path = "response.foo" → strip "response." then walk JSON.
        var parsed = ParseObj("""{"foo":"bar"}""");
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("response.foo", "eq", "bar"), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Theory]
    [InlineData("gt", "10", "5", true)]
    [InlineData("gt", "10", "20", false)]
    [InlineData("gte", "10", "10", true)]
    [InlineData("gte", "10", "20", false)]
    [InlineData("lt", "10", "20", true)]
    [InlineData("lt", "10", "5", false)]
    [InlineData("lte", "10", "10", true)]
    [InlineData("lte", "10", "5", false)]
    public void EvaluateAssertion_NumericOps(string op, string actual, string expected, bool pass)
    {
        var parsed = ParseObj($$"""{"v":{{actual}}}""");
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("v", op, expected), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.Equal(pass, RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_Contains_Matches()
    {
        var parsed = ParseObj("""{"msg":"hello world"}""");
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("msg", "contains", "world"), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_Matches_RegexHits()
    {
        var parsed = ParseObj("""{"v":"abc123"}""");
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("v", "matches", @"^abc\d+$"), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_Exists_ResolvedValue()
    {
        var parsed = ParseObj("""{"x":null}""");
        var rendered = NewAssertionResult();
        // The "x" key resolves to a null JsonNode value → exists fails.
        Evaluate(NewAssertion("x", "exists", ""), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.False(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_NotExists_MissingPath()
    {
        var parsed = ParseObj("""{"x":1}""");
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("y.z", "notexists", ""), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_TypeOp_MatchesString()
    {
        var parsed = ParseObj("""{"name":"x"}""");
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("name", "type", "string"), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.True(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_UnknownOp_Fails()
    {
        var rendered = NewAssertionResult();
        Evaluate(NewAssertion("status", "wat", "OK"), "OK", null, null,
            new Dictionary<string, string>(), rendered);
        Assert.False(RenderedPassed(rendered));
    }

    [Fact]
    public void EvaluateAssertion_BadRegex_RecordsError()
    {
        var parsed = ParseObj("""{"v":"x"}""");
        var rendered = NewAssertionResult();
        // "[" is an invalid regex — Regex.IsMatch throws, the catch
        // branch sets Passed=false + Error=message.
        Evaluate(NewAssertion("v", "matches", "["), "OK", parsed, null,
            new Dictionary<string, string>(), rendered);
        Assert.False(RenderedPassed(rendered));
        var err = (string?)AssertionResultType.GetProperty("Error")!.GetValue(rendered);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void EvaluateAssertion_ExpectedSubstitutesEnvVars()
    {
        var rendered = NewAssertionResult();
        var env = new Dictionary<string, string> { ["expectedStatus"] = "OK" };
        Evaluate(NewAssertion("status", "eq", "${expectedStatus}"), "OK", null, null,
            env, rendered);
        Assert.True(RenderedPassed(rendered));
    }
}
