// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Testing;

/// <summary>
/// Telling this test's own requests apart from anything else that reaches
/// its loopback port (#737).
/// </summary>
/// <remarks>
/// <para>
/// A test host on <c>127.0.0.1:0</c> takes what arrives. Whether anything
/// ever arrives that the test did not send is not known: it shows only when
/// it happens to break an assertion, which is how #714 surfaced — two flows
/// for one request, reported as <c>Assert.Single</c> and carried for months
/// as an "unrelated network flake".
/// </para>
/// <para>
/// The way to know is not to reason about paths or ports but to stamp the
/// requests: <see cref="MarkedClient(string,string)"/> puts
/// <see cref="MarkerHeader"/> on everything the test sends, and
/// <see cref="UseArrivalLog"/> writes down what comes in. A request without
/// the stamp was not sent from here, whatever path it landed on.
/// </para>
/// <para>
/// A find is <em>reported</em>, not failed, and this is the decision #737
/// asked for. Failing would answer the question the first time it happens
/// and keep answering it — but it would also make the suite depend on the
/// machine: a security tool that probes freshly opened ports would turn a
/// run red with nothing wrong in Bowire. Reporting keeps the runs honest
/// about Bowire and still leaves a record; the risk it carries is being
/// overlooked, which is exactly what happened to this question once
/// already, so the record is a file that outlives the run rather than a
/// line in scrollback.
/// </para>
/// </remarks>
public static class ForeignArrivals
{
    /// <summary>The header a marked client stamps on every request.</summary>
    public const string MarkerHeader = "X-Bowire-Test";

    private static readonly ConcurrentQueue<string> s_foreign = new();
    private static readonly object s_fileLock = new();

    /// <summary>
    /// Everything seen so far that carried no marker, newest last.
    /// </summary>
    public static IReadOnlyList<string> Foreign => [.. s_foreign];

    /// <summary>
    /// Where a find is written. <c>BOWIRE_FOREIGN_LOG</c> overrides it;
    /// otherwise the repository's <c>artifacts/</c> directory, so a find
    /// outlives the run that made it.
    /// </summary>
    public static string ReportPath { get; } = ResolveReportPath();

    /// <summary>
    /// An <see cref="HttpClient"/> that stamps <see cref="MarkerHeader"/> on
    /// every request, so the host can tell its traffic from anybody else's.
    /// </summary>
    /// <param name="baseAddress">The host's address, as the host reported it.</param>
    /// <param name="marker">
    /// Usually the test's own name. It travels into the report, so a find
    /// names the test that was running rather than just the port.
    /// </param>
    public static HttpClient MarkedClient(string baseAddress, string marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAddress);
        return MarkedClient(new Uri(baseAddress), marker);
    }

    /// <inheritdoc cref="MarkedClient(string,string)"/>
    public static HttpClient MarkedClient(Uri baseAddress, string marker)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);

        var http = new HttpClient { BaseAddress = baseAddress };
        http.DefaultRequestHeaders.Add(MarkerHeader, marker);
        return http;
    }

    /// <summary>
    /// Write down every request that reaches this host. Mount it first, so
    /// it also sees the requests the host under test skips.
    /// </summary>
    /// <param name="app">The application being built.</param>
    /// <param name="sink">
    /// Optional per-host log the test can print in its own failure messages.
    /// Every arrival lands here, marked or not.
    /// </param>
    /// <param name="onForeign">
    /// Where an unmarked arrival goes. Null is the point of the class: the
    /// shared list plus the report file. A handler is for the tests of this
    /// mechanism itself, which must not write into the very record they
    /// exist to make trustworthy.
    /// </param>
    public static IApplicationBuilder UseArrivalLog(
        this IApplicationBuilder app,
        ConcurrentQueue<string>? sink = null,
        Action<string>? onForeign = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            var marker = (string?)ctx.Request.Headers[MarkerHeader];
            var line = Describe(ctx, marker);
            sink?.Enqueue(line);
            if (string.IsNullOrEmpty(marker))
            {
                if (onForeign is not null) onForeign(line);
                else Report(line);
            }
            await next(ctx).ConfigureAwait(false);
        });
        return app;
    }

    private static string Describe(HttpContext ctx, string? marker) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{ctx.Request.Method} {ctx.Request.Path} "
            + $"on :{ctx.Connection.LocalPort} "
            + $"from {ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort} "
            + $"conn={ctx.Connection.Id} "
            + $"marker={(string.IsNullOrEmpty(marker) ? "(none)" : marker)} "
            + $"ua={(string?)ctx.Request.Headers.UserAgent ?? "(none)"} "
            // The one mechanism in this repository that sends a loopback
            // request somewhere it did not mean to: the reverse-proxy host
            // forwards to a recorded 127.0.0.1:port, and YARP stamps this on
            // the way through. Set means forwarded, not sent here.
            + $"fwd={(string?)ctx.Request.Headers["X-Forwarded-For"] ?? "(none)"}");

    private static void Report(string line)
    {
        s_foreign.Enqueue(line);
        var stamped = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} {line}");
        try
        {
            // Appended the moment it is seen rather than at the end of the
            // run: a find during a run that later crashes is the one most
            // worth keeping.
            lock (s_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
                File.AppendAllText(ReportPath, stamped + Environment.NewLine);
            }
        }
        catch (IOException) { /* a report that cannot be written must not fail a test */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }

    private static string ResolveReportPath()
    {
        var configured = Environment.GetEnvironmentVariable("BOWIRE_FOREIGN_LOG");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        // Up from the test binary to the repository, recognised by the
        // solution file. Falls back to the binary's own directory so this
        // never throws in a layout it does not know.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "Kuestenlogik.Bowire.slnx")))
            {
                return Path.Combine(dir, "artifacts", "test-foreign-arrivals.log");
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(AppContext.BaseDirectory, "test-foreign-arrivals.log");
    }
}
