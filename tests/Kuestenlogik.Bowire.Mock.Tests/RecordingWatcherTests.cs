// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Exercises the <see cref="RecordingWatcher"/> against real on-disk files.
/// Uses a very short debounce so tests don't wait seconds for the reload
/// to fire.
/// </summary>
public sealed class RecordingWatcherTests : IDisposable
{
    private readonly string _tempDir;

    public RecordingWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteRecording(string name, string body)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, body);
        return path;
    }

    private static string SingleRecordingJson(string responseBody) => $$"""
    {
      "id": "rec_1",
      "name": "sample",
      "recordingFormatVersion": 1,
      "steps": [
        {
          "id": "step_1",
          "protocol": "rest",
          "service": "S",
          "method": "M",
          "methodType": "Unary",
          "status": "OK",
          "httpPath": "/x",
          "httpVerb": "GET",
          "response": {{System.Text.Json.JsonSerializer.Serialize(responseBody)}}
        }
      ]
    }
    """;

    [Fact]
    public async Task OnFileChange_InvokesCallbackWithUpdatedRecording()
    {
        var path = WriteRecording("r.json", SingleRecordingJson("first"));

        var updates = new List<BowireRecording>();
        var fired = new TaskCompletionSource<BowireRecording>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new RecordingWatcher(
            path,
            select: null,
            onReload: r => { updates.Add(r); fired.TrySetResult(r); },
            logger: null,
            debounce: TimeSpan.FromMilliseconds(50));

        // Rewrite the file with a new response body. Give the watcher a
        // generous window to observe the change (FileSystemWatcher on
        // Windows is occasionally slow to fire).
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, SingleRecordingJson("second"), TestContext.Current.CancellationToken);

        var reload = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(reload.Steps);
        Assert.Equal("second", reload.Steps[0].Response);
    }

    [Fact]
    public async Task OnInvalidJson_KeepsPreviousInMemory()
    {
        var path = WriteRecording("r.json", SingleRecordingJson("ok"));

        var errors = 0;
        var reloads = 0;
        using var watcher = new RecordingWatcher(
            path,
            select: null,
            onReload: _ => { reloads++; },
            logger: new RecordingErrorCountingLogger(() => errors++),
            debounce: TimeSpan.FromMilliseconds(50));

        await Task.Delay(100, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "not json at all", TestContext.Current.CancellationToken);

        // Wait enough time for the watcher to observe + debounce + reload-attempt.
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // No successful reload fired; the error path logged.
        Assert.Equal(0, reloads);
        Assert.True(errors >= 1);
    }
}

internal sealed class RecordingErrorCountingLogger(Action onError) : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error) onError();
    }
}
