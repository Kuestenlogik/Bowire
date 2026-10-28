// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// <c>bowire test --data</c> (#675) end to end against the stub protocol,
/// which echoes the request's <c>id</c> back: one iteration per row, the row
/// bound as variables over the environment, failures naming the iteration and
/// the row, <c>--fail-fast</c>, and a bad data file refused before any request.
/// </summary>
public sealed class TestRunnerDataSetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bowire-tr-data-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // Sends {{id}} and expects {{expected}}: a row where they differ fails.
    private async Task<string> CollectionAsync(string environment = "")
    {
        var path = Path.Combine(_dir, "suite.json");
        await File.WriteAllTextAsync(path, $$$"""
            {
              "name": "echo-suite",
              "protocol": "_test_runner_stub",
              "serverUrl": "http://stub.local",
              {{{environment}}}
              "tests": [
                {
                  "name": "echo",
                  "service": "Svc",
                  "method": "Echo",
                  "messages": ["{\"id\": \"{{id}}\"}"],
                  "assert": [ { "path": "echo", "op": "eq", "expected": "{{expected}}" } ]
                }
              ]
            }
            """, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<string> DataAsync(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<(int Code, string Out, string Err, XDocument? JUnit)> RunAsync(TestCliOptions options)
    {
        var junit = Path.Combine(_dir, "junit.xml");
        options.JUnitPath = junit;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await TestRunner.RunAsync(options, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString(), File.Exists(junit) ? XDocument.Load(junit) : null);
    }

    [Fact]
    public async Task Each_Row_Is_An_Iteration_And_A_Failure_Names_Its_Row()
    {
        var data = await DataAsync("rows.csv", "id,expected\n1,1\n2,999\n3,3\n");

        var (code, stdout, _, junit) = await RunAsync(new TestCliOptions { CollectionPath = await CollectionAsync(), DataPath = data });

        Assert.Equal(1, code);
        Assert.Contains("iteration 2/3 · rows.csv line 3", stdout, StringComparison.Ordinal);
        Assert.Contains("3/3 iterations", stdout, StringComparison.Ordinal);

        // JUnit: one suite per iteration, one case per assertion, the failure naming the row.
        var suites = junit!.Root!.Elements("testsuite").ToList();
        Assert.Equal(3, suites.Count);
        Assert.Contains("iteration 2 (rows.csv line 3)", (string)suites[1].Attribute("name")!, StringComparison.Ordinal);
        var failure = Assert.Single(junit.Descendants("failure"));
        Assert.Contains("rows.csv line 3", (string)failure.Attribute("message")!, StringComparison.Ordinal);
        Assert.Equal(3, junit.Descendants("testcase").Count());
    }

    [Fact]
    public async Task Fail_Fast_Stops_After_The_First_Failing_Iteration()
    {
        var data = await DataAsync("rows.json", """[{"id":"1","expected":"x"},{"id":"2","expected":"2"}]""");

        var (code, stdout, _, junit) = await RunAsync(new TestCliOptions { CollectionPath = await CollectionAsync(), DataPath = data, FailFast = true });

        Assert.Equal(1, code);
        Assert.Contains("stopped after iteration 1 of 2 (--fail-fast)", stdout, StringComparison.Ordinal);
        Assert.Single(junit!.Root!.Elements("testsuite"));
        Assert.Contains("rows.json item 1", (string)junit.Descendants("failure").Single().Attribute("message")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Row_Shadows_The_Environment()
    {
        // The environment says id=env, expected=env; every row overrides both.
        var collection = await CollectionAsync("""
            "environment": { "expected": "env", "id": "env" },
            """);
        var data = await DataAsync("rows.csv", "id,expected\n5,5\n7,7\n");

        var (code, _, _, _) = await RunAsync(new TestCliOptions { CollectionPath = collection, DataPath = data });

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Iteration_And_IterationCount_Are_Bound_Without_A_Column()
    {
        var path = Path.Combine(_dir, "iter.json");
        await File.WriteAllTextAsync(path, """
            {
              "protocol": "_test_runner_stub", "serverUrl": "http://stub.local",
              "tests": [ { "service": "Svc", "method": "Echo", "messages": ["{\"id\": \"{{iteration}}/{{iterationCount}}\"}"],
                           "assert": [ { "path": "echo", "op": "eq", "expected": "{{expected}}" } ] } ]
            }
            """, TestContext.Current.CancellationToken);
        var data = await DataAsync("rows.csv", "expected\n1/2\n2/2\n");

        var (code, _, _, _) = await RunAsync(new TestCliOptions { CollectionPath = path, DataPath = data });

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task A_Malformed_Data_File_Fails_Before_Any_Request()
    {
        var data = await DataAsync("rows.csv", "id,expected\n1\n");

        var (code, stdout, stderr, junit) = await RunAsync(new TestCliOptions { CollectionPath = await CollectionAsync(), DataPath = data });

        Assert.Equal(2, code);
        Assert.Contains("rows.csv line 2: 1 value(s) where the header has 2 column(s)", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", stdout, StringComparison.Ordinal);
        Assert.Null(junit);
    }

    [Fact]
    public async Task Without_A_Data_Set_Iteration_Is_One_Of_One()
    {
        var path = Path.Combine(_dir, "plain.json");
        await File.WriteAllTextAsync(path, """
            {
              "protocol": "_test_runner_stub", "serverUrl": "http://stub.local",
              "tests": [ { "service": "Svc", "method": "Echo", "messages": ["{\"id\": \"{{iteration}}/{{iterationCount}}\"}"],
                           "assert": [ { "path": "echo", "op": "eq", "expected": "1/1" } ] } ]
            }
            """, TestContext.Current.CancellationToken);

        var (code, _, _, junit) = await RunAsync(new TestCliOptions { CollectionPath = path });

        Assert.Equal(0, code);
        // The plain run keeps its one-suite JUnit shape.
        Assert.Single(junit!.Root!.Elements("testsuite"));
    }
}
