// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mock.Loading;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock;

/// <summary>
/// ASP.NET pipeline extensions for mounting a Bowire mock replay handler.
/// Intended for test fixtures and any scenario where the mock coexists with
/// the real service on the same host.
/// </summary>
public static class BowireMockApplicationBuilderExtensions
{
    /// <summary>
    /// Mount the mock handler, loading the recording from disk.
    /// </summary>
    /// <param name="app">The ASP.NET application pipeline.</param>
    /// <param name="recordingPath">Absolute or relative path to a recording JSON file.</param>
    /// <param name="configure">Optional callback to tune the <see cref="MockOptions"/>.</param>
    public static IApplicationBuilder UseBowireMock(
        this IApplicationBuilder app,
        string recordingPath,
        Action<MockOptions>? configure = null)
    {
        var options = new MockOptions();
        configure?.Invoke(options);

        var recording = RecordingLoader.Load(recordingPath, options.Select);
        var logger = ResolveLogger(app, options);
        // Pass the path so the runtime-scenario-switch control
        // endpoint can reload the same file under a different Select
        // or swap to a related file via relative path resolution.
        var handler = new MockHandler(recording, options, logger, recordingPath);

        if (options.Watch)
        {
            // CA2000: watcher ownership is captured by the
            // ApplicationStopping callback below — `lifetime.Register(() =>
            // watcher.Dispose())` keeps the closure alive until shutdown,
            // at which point Dispose runs exactly once. Roslyn can't see
            // a `Register(() => x.Dispose())` lambda as an ownership
            // transfer, and an early-failure path doesn't exist here:
            // RecordingWatcher's ctor either succeeds or throws (no
            // partially-initialised state), and the `lifetime?.Register`
            // call after it is non-throwing for non-null lifetimes. On
            // the null-lifetime branch (no IHostApplicationLifetime in
            // the SP) the watcher does outlive the registration — but
            // that's a misconfigured embedded host, and Watch=true is
            // an opt-in flag the caller chose anyway.
#pragma warning disable CA2000
            var watcher = new RecordingWatcher(
                path: recordingPath,
                select: options.Select,
                onReload: handler.ReplaceRecording,
                logger: logger);
#pragma warning restore CA2000

            var lifetime = app.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStopping.Register(() => watcher.Dispose());
        }

        return app.Use((ctx, next) => handler.HandleAsync(ctx, next));
    }

    /// <summary>
    /// Mount the mock handler with an already-loaded recording. Primarily
    /// for tests that build a recording in-memory.
    /// </summary>
    public static IApplicationBuilder UseBowireMock(
        this IApplicationBuilder app,
        BowireRecording recording,
        Action<MockOptions>? configure = null)
    {
        var options = new MockOptions();
        configure?.Invoke(options);
        var logger = ResolveLogger(app, options);
        var handler = new MockHandler(recording, options, logger);

        return app.Use((ctx, next) => handler.HandleAsync(ctx, next));
    }

    private static ILogger ResolveLogger(IApplicationBuilder app, MockOptions options)
    {
        if (options.Logger is not null) return options.Logger;
        var factory = app.ApplicationServices.GetService<ILoggerFactory>();
        return factory?.CreateLogger("Kuestenlogik.Bowire.Mock") ?? NullMockLogger.Instance;
    }
}

internal sealed class NullMockLogger : ILogger
{
    public static readonly NullMockLogger Instance = new();
    private NullMockLogger() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
