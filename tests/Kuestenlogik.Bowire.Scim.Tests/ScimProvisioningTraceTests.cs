// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Scim;

namespace Kuestenlogik.Bowire.Scim.Tests;

/// <summary>
/// The instrument #639 needs.
/// </summary>
/// <remarks>
/// A live provisioning round-trip asks questions about the connector, not
/// about Bowire: how it pages a directory, what it sends that we do not
/// model, which PATCH dialect it really uses. `events.jsonl` cannot answer
/// them — it records mutations and their outcome, so the provider's reads
/// leave no trace, and the reads are most of what the exercise is for.
/// </remarks>
public sealed class ScimProvisioningTraceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scim-trace-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* the test already told us what it needed to */ }
    }

    private ScimProvisioningTrace NewTrace()
        => new(Path.Combine(_dir, "trace.jsonl"));

    private static List<JsonElement> Lines(ScimProvisioningTrace trace)
        => [.. File.ReadAllLines(trace.FilePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    // ---- what a read leaves behind -------------------------------------

    [Fact]
    public void RecordsAReadWithItsQuery()
    {
        // The whole point. A directory walk is GETs with paging parameters,
        // and none of it reaches events.jsonl.
        var trace = NewTrace();
        trace.Record("GET", "/scim/v2/Users", "?startIndex=101&count=100", 200, 12.4);

        var line = Assert.Single(Lines(trace));
        Assert.Equal("GET", line.GetProperty("method").GetString());
        Assert.Equal("/scim/v2/Users", line.GetProperty("path").GetString());
        Assert.Equal("?startIndex=101&count=100", line.GetProperty("query").GetString());
        Assert.Equal(200, line.GetProperty("status").GetInt32());
        Assert.True(line.TryGetProperty("ms", out _), "duration answers 'how long did it take to notice'");
    }

    [Fact]
    public void OmitsWhatIsNotThere()
    {
        // A line per request, not a line per field: an absent query or
        // dialect must not appear as null, or every reader has to learn
        // which nulls are meaningful.
        var trace = NewTrace();
        trace.Record("GET", "/scim/v2/Users/abc", null, 404, 1.0);

        var line = Assert.Single(Lines(trace));
        Assert.False(line.TryGetProperty("query", out _));
        Assert.False(line.TryGetProperty("patchDialect", out _));
        Assert.False(line.TryGetProperty("unmodelled", out _));
    }

    [Fact]
    public void KeepsLinesWholeUnderConcurrency()
    {
        // A connector's initial sync is the one moment this file is written
        // from several threads at once — and it is exactly when the evidence
        // matters. Interleaved half-lines would corrupt it.
        var trace = NewTrace();
        System.Threading.Tasks.Parallel.For(0, 200, i =>
            trace.Record("GET", "/scim/v2/Users", $"?startIndex={i}", 200, i));

        var lines = Lines(trace);
        Assert.Equal(200, lines.Count);
        Assert.All(lines, l => Assert.Equal("GET", l.GetProperty("method").GetString()));
    }

    [Fact]
    public void RefusesAnImpossiblePathAtStartup()
    {
        // Where a configuration mistake is actionable. Discovering it on the
        // first write would mean silently dropping every line of the exercise
        // the trace was turned on for.
        Assert.ThrowsAny<ArgumentException>(
            () => new ScimProvisioningTrace(Path.Combine(_dir, "nope\0bad", "trace.jsonl")));
    }

    [Fact]
    public void SurvivesAWriteItCannotMake()
    {
        // Best-effort by design: the connector is waiting on the call, and a
        // failed sync retries forever. Losing a line is the cheaper loss.
        //
        // A file where the directory should be — the shape a half-finished
        // storage root actually takes.
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "not a directory");

        var trace = new ScimProvisioningTrace(Path.Combine(blocked, "trace.jsonl"));
        var boom = Record.Exception(() => trace.Record("GET", "/x", null, 200, 1));
        Assert.Null(boom);
    }

    // ---- which connector actually called -------------------------------

    [Fact]
    public void NamesOktasPatchShape()
    {
        // Lower-case op, explicit path.
        var dialect = ScimProvisioningTrace.DetectPatchDialect("""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
             "Operations":[{"op":"replace","path":"active","value":false}]}
            """);
        Assert.Equal("okta", dialect);
    }

    [Fact]
    public void NamesEntrasPatchShape()
    {
        // Capitalised Op, and for deactivation no path — the value is an
        // object applied at the root.
        var dialect = ScimProvisioningTrace.DetectPatchDialect("""
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
             "Operations":[{"Op":"Replace","value":{"active":false}}]}
            """);
        Assert.Equal("entra", dialect);
    }

    [Fact]
    public void DoesNotForceAnUnknownShapeIntoOneOfThem()
    {
        // The interesting case: a third connector, or a version that changed
        // its mind. Calling it Okta would bury the finding this exists for.
        var dialect = ScimProvisioningTrace.DetectPatchDialect("""
            {"Operations":[{"op":"replace","path":"active","value":false},
                           {"Op":"Replace","value":{"active":true}}]}
            """);
        Assert.Equal("other", dialect);
    }

    [Fact]
    public void ReportsALowerCaseOpWithoutAPathAsEntraShaped()
    {
        // Neither exactly: the casing says Okta, the missing path says
        // Entra. Reported as what it is rather than resolved by preference.
        var dialect = ScimProvisioningTrace.DetectPatchDialect("""
            {"Operations":[{"op":"replace","value":{"active":false}}]}
            """);
        Assert.Equal("entra-shaped", dialect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"Operations":"not an array"}""")]
    public void ReportsNoDialectRatherThanGuessing(string body)
    {
        Assert.Null(ScimProvisioningTrace.DetectPatchDialect(body));
    }

    // ---- what the connector sent that we do not model -------------------

    [Fact]
    public void NamesAttributesBowireDoesNotModel()
    {
        // Kept verbatim and handed back, which is correct and completely
        // silent. An unmodelled attribute that turns out to matter would
        // otherwise be found by a customer.
        var found = ScimProvisioningTrace.UnmodelledAttributes("""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],
             "userName":"ada@example.com","active":true,
             "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User":{"department":"R&D"},
             "preferredLanguage":"en-GB"}
            """);

        Assert.Contains("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User", found);
        Assert.Contains("preferredLanguage", found);
        Assert.DoesNotContain("userName", found);
        Assert.DoesNotContain("schemas", found);
        Assert.DoesNotContain("active", found);
    }

    [Fact]
    public void SaysNothingWhenEverythingWasUnderstood()
    {
        var found = ScimProvisioningTrace.UnmodelledAttributes("""
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],
             "id":"1","externalId":"8f14e45f","userName":"ada@example.com",
             "name":{"givenName":"Ada"},"displayName":"Ada","active":true,
             "emails":[{"value":"ada@example.com","primary":true}],"meta":{}}
            """);
        Assert.Empty(found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("broken")]
    [InlineData("[1,2,3]")]
    public void ReportsNothingForAPayloadItCannotRead(string body)
    {
        Assert.Empty(ScimProvisioningTrace.UnmodelledAttributes(body));
    }
}
