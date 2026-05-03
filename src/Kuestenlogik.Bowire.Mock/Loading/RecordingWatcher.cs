// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Loading;

/// <summary>
/// Watches a recording file on disk and invokes a callback with a freshly
/// parsed <see cref="BowireRecording"/> whenever the file changes. Changes
/// are debounced so a flurry of saves (editors often write-then-rename) only
/// produces a single reload. A parse failure after a change is logged but
/// never throws — the mock keeps serving the previously loaded recording.
/// </summary>
public sealed class RecordingWatcher : IDisposable
{
    private readonly string _path;
    private readonly string? _select;
    private readonly Action<BowireRecording> _onReload;
    private readonly ILogger? _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Lock _gate = new();
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _pending;

    public RecordingWatcher(
        string path,
        string? select,
        Action<BowireRecording> onReload,
        ILogger? logger = null,
        TimeSpan? debounce = null)
    {
        _path = Path.GetFullPath(path);
        _select = select;
        _onReload = onReload;
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(200);

        var dir = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException($"Recording path has no directory: {_path}", nameof(path));
        var name = Path.GetFileName(_path);

        _watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: cancel any pending reload, start a fresh timer. The save
        // that triggered this event may be one of several in a rename-write
        // sequence; only the last one should actually reload.
        CancellationTokenSource newCts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = new CancellationTokenSource();
            newCts = _pending;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, newCts.Token);
                var recording = RecordingLoader.Load(_path, _select);
                _onReload(recording);
                _logger?.LogInformation(
                    "Recording reloaded from {Path} (version={Version}, steps={StepCount})",
                    _path, recording.RecordingFormatVersion, recording.Steps.Count);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later write — ignore, the later reload will run.
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Failed to reload recording from {Path}; keeping previous version in memory.",
                    _path);
            }
        });
    }

    public void Dispose()
    {
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Renamed -= OnChanged;
        _watcher.Dispose();
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
