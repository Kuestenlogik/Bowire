// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.App;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Coverage for the HAR → BowireRecording mapping. The HAR fixtures
/// are tiny inline strings so each test asserts on a focused mapping
/// concern (status mapping, header extraction, body fall-throughs,
/// timing, service / method derivation, error paths).
/// </summary>
public sealed class HarImporterTests
{
    [Fact]
    public void Convert_Maps_Single_Get_Entry_To_One_Step()
    {
        var har = """
            {
              "log": {
                "version": "1.2",
                "creator": { "name": "Test", "version": "1" },
                "entries": [
                  {
                    "startedDateTime": "2026-04-01T10:00:00.000Z",
                    "time": 42,
                    "request": {
                      "method": "GET",
                      "url": "https://api.example.com/users/42",
                      "headers": [
                        { "name": "Accept", "value": "application/json" }
                      ]
                    },
                    "response": {
                      "status": 200,
                      "statusText": "OK",
                      "content": {
                        "size": 12,
                        "mimeType": "application/json",
                        "text": "{\"id\":42}"
                      }
                    }
                  }
                ]
              }
            }
            """;

        var rec = HarImporter.Convert(har, recordingName: "Users API");

        Assert.Equal("Users API", rec.Name);
        Assert.Equal(2, rec.RecordingFormatVersion);
        var step = Assert.Single(rec.Steps);
        Assert.Equal("rest", step.Protocol);
        Assert.Equal("GET", step.HttpVerb);
        Assert.Equal("/users/42", step.HttpPath);
        Assert.Equal("Unary", step.MethodType);
        Assert.Equal("OK", step.Status);
        Assert.Equal("https://api.example.com", step.ServerUrl);
        Assert.Equal(42, step.DurationMs);
        Assert.Equal("{\"id\":42}", step.Response);
        Assert.NotNull(step.Metadata);
        Assert.Equal("application/json", step.Metadata!["Accept"]);
    }

    [Fact]
    public void Convert_Numeric_Tail_Segment_Uses_Parent_Segment_For_Method()
    {
        // GET /users/42 → service "users", method "GET_users" because the
        // numeric tail looks like an id and using it would noise up the tree.
        var har = MakeMinimal("GET", "https://api.example.com/users/42");

        var rec = HarImporter.Convert(har);
        var step = Assert.Single(rec.Steps);

        Assert.Equal("http", step.Service);  // 3-segment fallback (parent of "users")
        Assert.Equal("GET_users", step.Method);
    }

    [Fact]
    public void Convert_Non_Numeric_Tail_Builds_Service_And_Method_From_Path()
    {
        // GET /api/users/list → service "users", method "GET_list".
        var har = MakeMinimal("GET", "https://api.example.com/api/users/list");

        var rec = HarImporter.Convert(har);
        var step = Assert.Single(rec.Steps);

        Assert.Equal("users", step.Service);
        Assert.Equal("GET_list", step.Method);
    }

    [Fact]
    public void Convert_Empty_Path_Falls_Back_To_Http_Service()
    {
        // GET https://example.com/ — no segments, fall back to a sensible
        // catch-all rather than empty strings the UI would render as blanks.
        var har = MakeMinimal("GET", "https://example.com/");

        var rec = HarImporter.Convert(har);
        var step = Assert.Single(rec.Steps);

        Assert.Equal("http", step.Service);
        Assert.Equal("GET", step.Method);
    }

    [Fact]
    public void Convert_Maps_Post_Body_To_Step_Body_And_Messages()
    {
        var har = """
            {
              "log": {
                "version": "1.2",
                "entries": [
                  {
                    "startedDateTime": "2026-04-01T10:00:00.000Z",
                    "time": 10,
                    "request": {
                      "method": "POST",
                      "url": "https://api.example.com/orders",
                      "headers": [{ "name": "Content-Type", "value": "application/json" }],
                      "postData": {
                        "mimeType": "application/json",
                        "text": "{\"sku\":\"A1\"}"
                      }
                    },
                    "response": { "status": 201, "statusText": "Created", "content": { "size": 0 } }
                  }
                ]
              }
            }
            """;

        var rec = HarImporter.Convert(har);
        var step = Assert.Single(rec.Steps);

        Assert.Equal("POST", step.HttpVerb);
        Assert.Equal("{\"sku\":\"A1\"}", step.Body);
        // Body double-tracks into Messages so streaming-aware downstream
        // tooling sees the exact same shape native captures use.
        Assert.Single(step.Messages);
        Assert.Equal("{\"sku\":\"A1\"}", step.Messages[0]);
    }

    [Fact]
    public void Convert_Non_2xx_Status_Surfaces_Numeric_Code()
    {
        // 404 stays a literal "404" so mock-replay mismatches stay visible.
        var har = MakeMinimal("GET", "https://api.example.com/missing", status: 404);

        var rec = HarImporter.Convert(har);
        var step = Assert.Single(rec.Steps);

        Assert.Equal("404", step.Status);
    }

    [Fact]
    public void Convert_Falls_Back_To_Summed_Timings_When_Top_Time_Missing()
    {
        var har = """
            {
              "log": {
                "version": "1.2",
                "entries": [
                  {
                    "startedDateTime": "2026-04-01T10:00:00.000Z",
                    "request": { "method": "GET", "url": "https://api.example.com/x" },
                    "response": { "status": 200, "content": { "size": 0 } },
                    "timings": {
                      "blocked": 1,
                      "dns": 2,
                      "connect": 3,
                      "send": 4,
                      "wait": 50,
                      "receive": 5
                    }
                  }
                ]
              }
            }
            """;

        var rec = HarImporter.Convert(har);
        var step = Assert.Single(rec.Steps);

        Assert.Equal(65, step.DurationMs);  // 1+2+3+4+50+5
    }

    [Fact]
    public void Convert_Skips_Entries_Missing_Method_Or_Url()
    {
        var har = """
            {
              "log": {
                "version": "1.2",
                "entries": [
                  { "request": { "method": "GET" }, "response": { "status": 200 } },
                  { "request": { "url": "https://api.example.com/x" }, "response": { "status": 200 } },
                  { "request": { "method": "GET", "url": "https://api.example.com/ok" },
                    "response": { "status": 200, "content": { "size": 0 } } }
                ]
              }
            }
            """;

        var rec = HarImporter.Convert(har);

        // Only the third entry is round-trippable; the first two are dropped.
        var step = Assert.Single(rec.Steps);
        Assert.Equal("/ok", step.HttpPath);
    }

    [Fact]
    public void Convert_Default_Name_Falls_Back_To_Creator_Then_Imported_Har()
    {
        var har = """
            {
              "log": {
                "version": "1.2",
                "creator": { "name": "Playwright", "version": "1" },
                "entries": []
              }
            }
            """;

        var rec = HarImporter.Convert(har);

        Assert.Equal("Playwright", rec.Name);
    }

    [Fact]
    public void Convert_Default_Name_Imported_Har_When_Creator_Missing()
    {
        var har = """{ "log": { "version": "1.2", "entries": [] } }""";

        var rec = HarImporter.Convert(har);

        Assert.Equal("Imported HAR", rec.Name);
    }

    [Fact]
    public void Convert_Throws_On_Invalid_Json()
    {
        var ex = Assert.Throws<HarImportException>(() => HarImporter.Convert("not-json"));
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_Throws_When_Log_Object_Missing()
    {
        Assert.Throws<HarImportException>(() => HarImporter.Convert("{}"));
    }

    [Fact]
    public void Convert_Throws_When_Entries_Array_Missing()
    {
        Assert.Throws<HarImportException>(() =>
            HarImporter.Convert("""{ "log": { "version": "1.2" } }"""));
    }

    [Fact]
    public void SplitUrl_Absolute_Url_Returns_Path_And_Host()
    {
        var (path, host) = HarImporter.SplitUrl("https://api.example.com:8080/foo?bar=1");

        Assert.Equal("/foo?bar=1", path);
        Assert.Equal("https://api.example.com:8080", host);
    }

    [Fact]
    public void SplitUrl_Relative_Path_Returns_Path_With_Null_Host()
    {
        var (path, host) = HarImporter.SplitUrl("/foo/bar");

        Assert.Equal("/foo/bar", path);
        Assert.Null(host);
    }

    [Fact]
    public void SplitUrl_Bare_Word_Gets_Leading_Slash_Added()
    {
        var (path, host) = HarImporter.SplitUrl("foo");

        Assert.Equal("/foo", path);
        Assert.Null(host);
    }

    [Fact]
    public void DeriveServiceAndMethod_Guid_Tail_Treated_As_Id()
    {
        var (service, method) = HarImporter.DeriveServiceAndMethod(
            "/api/users/A2DA34CC-1234-5678-9ABC-DEF012345678", "DELETE");

        // GUID tail → use the parent ("users") as the method-name source.
        Assert.Equal("api", service);
        Assert.Equal("DELETE_users", method);
    }

    [Fact]
    public async Task ImportAsync_Writes_Recording_To_File()
    {
        var har = MakeMinimal("GET", "https://api.example.com/health");
        var harPath = Path.GetTempFileName();
        var outPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(harPath, har, TestContext.Current.CancellationToken);

            using var stderr = new StringWriter();
            var exit = await HarImporter.ImportAsync(harPath, outPath, recordingName: "Health", stderr);

            Assert.Equal(0, exit);
            var written = await File.ReadAllTextAsync(outPath, TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(written);
            Assert.Equal("Health", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("steps").GetArrayLength());
        }
        finally
        {
            try { File.Delete(harPath); } catch { /* best-effort */ }
            try { File.Delete(outPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ImportAsync_Reports_Missing_File_With_Exit_Code_1()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.har");
        var outPath = Path.GetTempFileName();
        try
        {
            using var stderr = new StringWriter();

            var exit = await HarImporter.ImportAsync(missing, outPath, recordingName: null, stderr);

            Assert.Equal(1, exit);
            Assert.Contains("not found", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(outPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ImportAsync_Reports_Malformed_Har_With_Exit_Code_1()
    {
        var harPath = Path.GetTempFileName();
        var outPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(harPath, "{ broken", TestContext.Current.CancellationToken);

            using var stderr = new StringWriter();
            var exit = await HarImporter.ImportAsync(harPath, outPath, recordingName: null, stderr);

            Assert.Equal(1, exit);
            Assert.Contains("HAR import failed", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(harPath); } catch { /* best-effort */ }
            try { File.Delete(outPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Build a tiny HAR fixture with one entry. Keeps the per-test setup
    /// short so each test reads at a glance.
    /// </summary>
    private static string MakeMinimal(string method, string url, int status = 200) => $$"""
        {
          "log": {
            "version": "1.2",
            "entries": [
              {
                "startedDateTime": "2026-04-01T10:00:00.000Z",
                "time": 10,
                "request": { "method": "{{method}}", "url": "{{url}}", "headers": [] },
                "response": { "status": {{status}}, "content": { "size": 0 } }
              }
            ]
          }
        }
        """;
}
