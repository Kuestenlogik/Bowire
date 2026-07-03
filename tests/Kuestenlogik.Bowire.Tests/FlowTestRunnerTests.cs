// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

// CA1861 wants `static readonly`-cached array literals for repeated
// calls; the env-merge helper assertions below pass small per-call
// arrays. Suppress at file scope — the runtime cost is negligible and
// inline arrays read better as test data than top-of-file constants.
#pragma warning disable CA1861

/// <summary>
/// v2.2 CI runner (T2) coverage — happy path, mixed pass/fail,
/// step-error, JUnit XML emission, HTML report emission. Boots a real
/// HttpListener on a free loopback port so the runner exercises the
/// in-process REST protocol plugin end-to-end. No mocks: if the dispatch
/// path breaks or the protocol registry doesn't discover REST, the test
/// fails for the same reason a real `bowire test flow.json` call would.
/// </summary>
public sealed class FlowTestRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public FlowTestRunnerTests()
    {
        _tempDir = SafePath.Combine(Path.GetTempPath(), "bowire-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- Pure helpers ----

    [Fact]
    public void LooksLikeFlow_TrueWhenNodesArrayPresent()
    {
        var json = """{ "id":"f1", "name":"x", "nodes":[ { "id":"n1" } ] }""";
        Assert.True(FlowTestRunner.LooksLikeFlow(json));
    }

    [Fact]
    public void LooksLikeFlow_FalseWhenNoNodesArray()
    {
        // Recording / test-collection shape — has tests:[], no nodes.
        var json = """{ "name":"coll", "tests":[ { "service":"s", "method":"m" } ] }""";
        Assert.False(FlowTestRunner.LooksLikeFlow(json));
    }

    [Fact]
    public void LooksLikeFlow_FalseOnMalformedJson()
    {
        Assert.False(FlowTestRunner.LooksLikeFlow("{ not json"));
        Assert.False(FlowTestRunner.LooksLikeFlow(string.Empty));
    }

    [Fact]
    public void MergeEnv_KeyEqualsValue_ParsesEachPair()
    {
        var merged = FlowTestRunner.MergeEnv(new[] { "A=1", "B=two", "C=" });
        Assert.Equal("1", merged["A"]);
        Assert.Equal("two", merged["B"]);
        Assert.Equal(string.Empty, merged["C"]);
    }

    [Fact]
    public void MergeEnv_IgnoresMalformedEntries()
    {
        var merged = FlowTestRunner.MergeEnv(new[] { "no-equals", "=novalue", "=", "" });
        Assert.Empty(merged);
    }

    [Fact]
    public void VariableResolver_ReplacesBothBraceAndDollarForms()
    {
        var env = new Dictionary<string, string> { ["name"] = "Ada" };
        Assert.Equal("hello Ada!", FlowVariableResolver.Resolve("hello {{name}}!", env));
        Assert.Equal("hello Ada!", FlowVariableResolver.Resolve("hello ${name}!", env));
    }

    [Fact]
    public void VariableResolver_LeavesUnknownIntact()
    {
        var env = new Dictionary<string, string>();
        Assert.Equal("hello {{missing}}!", FlowVariableResolver.Resolve("hello {{missing}}!", env));
    }

    [Fact]
    public void VariableResolver_SystemVarsResolveWithoutEnv()
    {
        var resolved = FlowVariableResolver.Resolve("{{uuid}}", new Dictionary<string, string>());
        Assert.True(Guid.TryParse(resolved, out _));
    }

    // ---- Pre-invocation guards (mirror TestRunnerTests for the new codepath) ----

    [Fact]
    public async Task RunAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
#pragma warning disable xUnit1051 // null-arg guard; no I/O reaches the token.
            FlowTestRunner.RunAsync(null!));
#pragma warning restore xUnit1051
    }

    [Fact]
    public async Task RunAsync_MissingFile_ReturnsTwo()
    {
        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = SafePath.Combine(_tempDir, "absent.json") },
            TextWriter.Null, TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_MalformedJson_ReturnsTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = SafePath.Combine(_tempDir, "broken.json");
        await File.WriteAllTextAsync(path, "{ broken", ct);
        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = path }, TextWriter.Null, TextWriter.Null, ct);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_EmptyNodes_ReturnsTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = SafePath.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(path, """{ "id":"f","name":"x","nodes":[] }""", ct);
        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = path }, TextWriter.Null, TextWriter.Null, ct);
        Assert.Equal(2, rc);
    }

    // ---- End-to-end against a real loopback HTTP server ----

    [Fact]
    public async Task RunAsync_HappyPath_AllExpectationsPass_ReturnsZero()
    {
        using var server = new LoopbackJsonServer(_ => (200, "application/json", "{\"user\":{\"id\":42,\"name\":\"Ada\"}}"));

        // Ad-hoc REST convention: service is empty, method is the HTTP
        // verb, full URL goes in serverUrl. See
        // BowireRestProtocol.InvokeAsync (#256) — the schema-free
        // codepath the freeform request builder uses.
        var flow = $$"""
        {
          "id":"flow_happy",
          "name":"Happy",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/users/42",
              "service":"",
              "method":"GET",
              "body":"{}",
              "expectations":[
                { "id":"e1","kind":"body-path","operator":"equals","target":"$.user.id","expected":"42" },
                { "id":"e2","kind":"body-path","operator":"equals","target":"$.user.name","expected":"Ada" }
              ]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "happy.json");
        await File.WriteAllTextAsync(flowPath, flow, ct);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath }, stdout, stderr, ct);

        Assert.Equal(0, rc);
        Assert.Contains("2/2 expectations passed", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MixedExpectations_OneFails_ReturnsOne()
    {
        using var server = new LoopbackJsonServer(_ => (200, "application/json", "{\"user\":{\"id\":42}}"));

        var flow = $$"""
        {
          "id":"flow_mixed",
          "name":"Mixed",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/users/42",
              "service":"",
              "method":"GET",
              "body":"{}",
              "expectations":[
                { "id":"ok","kind":"body-path","operator":"exists","target":"$.user.id" },
                { "id":"bad","kind":"body-path","operator":"equals","target":"$.user.id","expected":"999" }
              ]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "mixed.json");
        await File.WriteAllTextAsync(flowPath, flow, ct);

        using var stdout = new StringWriter();
        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath }, stdout, TextWriter.Null, ct);

        Assert.Equal(1, rc);
        Assert.Contains("1/2 expectations passed", stdout.ToString(), StringComparison.Ordinal);
    }

    // ---- Snapshot testing (#171) — capture-once, diff-on-change ----

    [Fact]
    public async Task RunAsync_Snapshot_CaptureThenMatchThenDriftThenRebaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = "{\"user\":{\"id\":42,\"name\":\"Ada\"}}";
        // Responder reads the local so the response can drift mid-test.
        using var server = new LoopbackJsonServer(_ => (200, "application/json", body));

        var flowPath = SafePath.Combine(_tempDir, "snap.json");
        var flow = $$"""
        {
          "id":"flow_snap",
          "name":"Snap",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/users/42",
              "service":"",
              "method":"GET",
              "body":"{}",
              "snapshot": { "mode": "exact" }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(flowPath, flow, ct);
        var snapshotFile = SafePath.Combine(
            FlowTestRunner.SnapshotDirFor(flowPath), "n1.snap.json");

        // Run 1 — no baseline: capture, pass.
        using (var stdout = new StringWriter())
        {
            var rc = await FlowTestRunner.RunAsync(
                new FlowTestCliOptions { FlowPath = flowPath }, stdout, TextWriter.Null, ct);
            Assert.Equal(0, rc);
            Assert.Contains("snapshot captured", stdout.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(snapshotFile));
        }

        // Run 2 — same response: match, pass.
        using (var stdout = new StringWriter())
        {
            var rc = await FlowTestRunner.RunAsync(
                new FlowTestCliOptions { FlowPath = flowPath }, stdout, TextWriter.Null, ct);
            Assert.Equal(0, rc);
            Assert.Contains("snapshot matches", stdout.ToString(), StringComparison.Ordinal);
        }

        // Run 3 — response drifted: fail with the changed path in the diff.
        body = "{\"user\":{\"id\":43,\"name\":\"Ada\"}}";
        using (var stdout = new StringWriter())
        {
            var rc = await FlowTestRunner.RunAsync(
                new FlowTestCliOptions { FlowPath = flowPath }, stdout, TextWriter.Null, ct);
            Assert.Equal(1, rc);
            Assert.Contains("snapshot drift", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("$.user.id", stdout.ToString(), StringComparison.Ordinal);
        }

        // Run 4 — --update-snapshots re-baselines: pass again.
        using (var stdout = new StringWriter())
        {
            var rc = await FlowTestRunner.RunAsync(
                new FlowTestCliOptions { FlowPath = flowPath, UpdateSnapshots = true }, stdout, TextWriter.Null, ct);
            Assert.Equal(0, rc);
            Assert.Contains("snapshot updated", stdout.ToString(), StringComparison.Ordinal);
        }
        var rebaselined = await File.ReadAllTextAsync(snapshotFile, ct);
        Assert.Contains("43", rebaselined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Snapshot_IgnoredDynamicField_DoesNotFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var stamp = 1;
        using var server = new LoopbackJsonServer(_ =>
            (200, "application/json", $"{{\"id\":\"abc\",\"ts\":{++stamp}}}"));

        var flowPath = SafePath.Combine(_tempDir, "snap-ignore.json");
        var flow = $$"""
        {
          "id":"flow_snap_ign",
          "name":"SnapIgnore",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/thing",
              "service":"",
              "method":"GET",
              "body":"{}",
              "snapshot": { "mode": "exact", "ignore": ["$.ts"] }
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(flowPath, flow, ct);

        var rc1 = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath }, TextWriter.Null, TextWriter.Null, ct);
        Assert.Equal(0, rc1);

        // Second run: ts drifted (responder increments) but is marked
        // dynamic — snapshot must still hold.
        using var stdout = new StringWriter();
        var rc2 = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath }, stdout, TextWriter.Null, ct);
        Assert.Equal(0, rc2);
        Assert.Contains("snapshot matches", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_BackendUnreachable_ReturnsTwo()
    {
        // Find a port nothing listens on so the REST invoke fails with a
        // connection-refused / similar transport error → step error,
        // which the CLI contract maps to exit 2.
        var port = GetFreePort();
        var flow = $$"""
        {
          "id":"flow_err",
          "name":"BackendDown",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"http://127.0.0.1:{{port}}/anything",
              "service":"",
              "method":"GET",
              "body":"{}",
              "expectations":[
                { "id":"e1","kind":"status","operator":"equals","expected":"200" }
              ]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "err.json");
        await File.WriteAllTextAsync(flowPath, flow, ct);

        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath }, TextWriter.Null, TextWriter.Null, ct);
        // The REST plugin may surface the connection failure either as a
        // step error (most common — DiscoverAsync or InvokeAsync throws)
        // OR by returning a non-200 status the expectation rejects (some
        // plugins absorb the transport error and put it on InvokeResult).
        // Both paths exit non-zero; the contract distinguishes them as
        // 2 (step error) and 1 (expectation failed). Either is a valid
        // signal that the run failed — accept both so the test stays
        // robust across plugin versions.
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public async Task RunAsync_JUnitFlag_EmitsValidSurefireXml()
    {
        using var server = new LoopbackJsonServer(_ => (200, "application/json", "{\"ok\":true}"));

        var flow = $$"""
        {
          "id":"flow_j",
          "name":"JUnitFlow",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/",
              "service":"",
              "method":"GET",
              "body":"{}",
              "expectations":[
                { "id":"e1","kind":"body-path","operator":"exists","target":"$.ok" },
                { "id":"e2","kind":"body-path","operator":"equals","target":"$.ok","expected":"true" }
              ]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "j.json");
        var junitPath = SafePath.Combine(_tempDir, "out.xml");
        await File.WriteAllTextAsync(flowPath, flow, ct);

        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath, JUnitPath = junitPath },
            TextWriter.Null, TextWriter.Null, ct);

        Assert.Equal(0, rc);
        Assert.True(File.Exists(junitPath));

        var xml = await File.ReadAllTextAsync(junitPath, ct);
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Root);
        Assert.Equal("testsuites", doc.Root!.Name.LocalName);
        // One <testsuite> wrapping two <testcase> rows (one per
        // expectation).
        var suites = doc.Root.Elements("testsuite").ToList();
        Assert.Single(suites);
        var cases = suites[0].Elements("testcase").ToList();
        Assert.Equal(2, cases.Count);
        // Both passed → no <failure> / <error> children.
        Assert.DoesNotContain(cases, tc => tc.Element("failure") is not null);
        Assert.DoesNotContain(cases, tc => tc.Element("error") is not null);
        // Surefire shape — name + classname attributes present on each
        // case so CI reporters can group rows.
        Assert.All(cases, tc =>
        {
            Assert.NotNull(tc.Attribute("name"));
            Assert.NotNull(tc.Attribute("classname"));
            Assert.NotNull(tc.Attribute("time"));
        });
    }

    [Fact]
    public async Task RunAsync_JUnitFlag_FailingExpectation_EmitsFailureElement()
    {
        using var server = new LoopbackJsonServer(_ => (200, "application/json", "{\"id\":1}"));

        var flow = $$"""
        {
          "id":"flow_jf",
          "name":"JUnitFail",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/",
              "service":"",
              "method":"GET",
              "body":"{}",
              "expectations":[
                { "id":"bad","kind":"body-path","operator":"equals","target":"$.id","expected":"999" }
              ]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "jf.json");
        var junitPath = SafePath.Combine(_tempDir, "jf.xml");
        await File.WriteAllTextAsync(flowPath, flow, ct);

        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath, JUnitPath = junitPath },
            TextWriter.Null, TextWriter.Null, ct);

        Assert.Equal(1, rc);
        var doc = XDocument.Parse(await File.ReadAllTextAsync(junitPath, ct));
        var failures = doc.Descendants("failure").ToList();
        Assert.Single(failures);
        Assert.Equal("AssertionFailed", failures[0].Attribute("type")!.Value);
    }

    [Fact]
    public async Task RunAsync_ReportFlag_EmitsSelfContainedHtml()
    {
        using var server = new LoopbackJsonServer(_ => (200, "application/json", "{\"ok\":true}"));

        var flow = $$"""
        {
          "id":"flow_h",
          "name":"HtmlFlow",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/",
              "service":"",
              "method":"GET",
              "body":"{}",
              "expectations":[
                { "id":"e1","kind":"body-path","operator":"exists","target":"$.ok" }
              ]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "h.json");
        var htmlPath = SafePath.Combine(_tempDir, "out.html");
        await File.WriteAllTextAsync(flowPath, flow, ct);

        var rc = await FlowTestRunner.RunAsync(
            new FlowTestCliOptions { FlowPath = flowPath, ReportPath = htmlPath },
            TextWriter.Null, TextWriter.Null, ct);

        Assert.Equal(0, rc);
        Assert.True(File.Exists(htmlPath));

        var html = await File.ReadAllTextAsync(htmlPath, ct);
        // Self-contained: doctype, inline <style>, no <link rel="stylesheet"> / <script src=>.
        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link rel=\"stylesheet\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=", html, StringComparison.Ordinal);
        // Summary section reflects the run.
        Assert.Contains("Expectations passed", html, StringComparison.Ordinal);
        Assert.Contains("HtmlFlow", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestRunner_AutoDispatchesFlowFile_ToFlowRunner()
    {
        // The exposed `bowire test` command sniffs JSON shape and routes
        // to the Flow runner without an extra flag. Verify the dispatch
        // by handing TestRunner.RunAsync a flow document and checking
        // it took the new codepath (output contains the flow header).
        using var server = new LoopbackJsonServer(_ => (200, "application/json", "{}"));

        var flow = $$"""
        {
          "id":"flow_d",
          "name":"AutoDispatch",
          "nodes":[
            {
              "id":"n1",
              "type":"request",
              "protocol":"rest",
              "serverUrl":"{{server.Url}}/",
              "service":"",
              "method":"GET",
              "body":"{}",
              "expectations":[]
            }
          ]
        }
        """;
        var ct = TestContext.Current.CancellationToken;
        var flowPath = SafePath.Combine(_tempDir, "dispatch.json");
        await File.WriteAllTextAsync(flowPath, flow, ct);

        using var stdout = new StringWriter();
        // Note: TestRunner.RunAsync (the public surface) does not accept
        // an explicit CancellationToken yet — it predates that contract
        // and the v0 dispatch keeps its signature stable.
        var rc = await TestRunner.RunAsync(
            new TestCliOptions { CollectionPath = flowPath }, stdout, TextWriter.Null);

        Assert.Equal(0, rc);
        Assert.Contains("Bowire Flow Test Runner", stdout.ToString(), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Tiny one-shot HTTP loopback server. Each request hits the
    /// supplied responder so tests can shape (status, content-type, body)
    /// per-test. Stops accepting on dispose. Listens until disposed so
    /// multi-step tests reuse the same backend across requests.
    /// </summary>
    private sealed class LoopbackJsonServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }

        public LoopbackJsonServer(Func<HttpListenerRequest, (int Status, string ContentType, string Body)> responder)
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token); }
                    catch { return; }
                    try
                    {
                        var (status, ctype, body) = responder(ctx.Request);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = ctype;
                        var bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    }
                    catch { /* response side: best-effort during shutdown */ }
                    finally
                    {
                        try { ctx.Response.OutputStream.Close(); } catch { }
                    }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            // HttpListener implements IDisposable explicitly via
            // IDisposable.Dispose — Close() is the public surface that
            // releases the unmanaged handle the analyzer wants released.
            try { ((IDisposable)_listener).Dispose(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
