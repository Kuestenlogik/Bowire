// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests <see cref="TestRunner.RunAsync"/>'s pre-invocation guards —
/// the paths that fail before any protocol call goes out (missing
/// path, missing file, empty / malformed JSON). The happy-path
/// "runs against a real server" is covered by the integration harness;
/// here we just keep the diagnostic exit codes (2 for usage, …)
/// honest so CI scripts can rely on them.
/// </summary>
public sealed class TestRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public TestRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-tr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => TestRunner.RunAsync(null!));
    }

    [Fact]
    public async Task RunAsync_NoCollectionPath_ReturnsUsageExit()
    {
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = null });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_EmptyCollectionPath_ReturnsUsageExit()
    {
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = "" });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_MissingFile_ReturnsUsageExit()
    {
        var path = Path.Combine(_tempDir, "absent.json");
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_MalformedJson_ReturnsUsageExit()
    {
        var path = Path.Combine(_tempDir, "broken.json");
        await File.WriteAllTextAsync(path, "{ this is not json", TestContext.Current.CancellationToken);
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_EmptyTestsArray_ReturnsUsageExit()
    {
        var path = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(path, """{ "name": "x", "tests": [] }""", TestContext.Current.CancellationToken);
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_NullTestsField_ReturnsUsageExit()
    {
        // tests: null deserialises to a null List<TestEntry>, the runner
        // surfaces the same "no tests" diagnostic as an explicit empty
        // array.
        var path = Path.Combine(_tempDir, "null-tests.json");
        await File.WriteAllTextAsync(path, """{ "name": "x", "tests": null }""", TestContext.Current.CancellationToken);
        var rc = await TestRunner.RunAsync(new TestCliOptions { CollectionPath = path });
        Assert.Equal(2, rc);
    }
}
