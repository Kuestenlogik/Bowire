// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Periodically runs <see cref="PluginUpdateCheckService.CheckAsync"/>
/// when the operator has opted in via the
/// <c>Bowire:PluginUpdateCheck</c> configuration section (bound to
/// <see cref="BowirePluginUpdateCheckOptions"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in by design.</b> When
/// <see cref="BowirePluginUpdateCheckOptions.Enabled"/> is
/// <c>false</c> (the default) this service is still registered but
/// short-circuits on startup — no network calls are made.
/// </para>
/// <para>
/// First run happens immediately on host start (so a fresh install
/// surfaces "updates available" without waiting a full day).
/// Subsequent runs honour
/// <see cref="BowirePluginUpdateCheckOptions.IntervalHours"/>.
/// </para>
/// </remarks>
public sealed class PluginUpdateCheckHostedService : BackgroundService
{
    private readonly PluginUpdateCheckService _service;
    private readonly IOptions<BowirePluginUpdateCheckOptions> _options;
    private readonly ILogger<PluginUpdateCheckHostedService> _logger;

    public PluginUpdateCheckHostedService(
        PluginUpdateCheckService service,
        IOptions<BowirePluginUpdateCheckOptions> options,
        ILogger<PluginUpdateCheckHostedService> logger)
    {
        _service = service;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = _options.Value;
        if (!cfg.Enabled)
        {
            _logger.LogDebug(
                "Plugin update check disabled (opt-in). Set Bowire:PluginUpdateCheck:Enabled=true or pass --update-check to enable.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, cfg.IntervalHours));
        BackgroundUpdateCheckLog.Starting(_logger, interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _service.CheckAsync(cfg.IncludePrerelease, stoppingToken)
                    .ConfigureAwait(false);
                var pending = 0;
                foreach (var r in snapshot.Results) if (r.UpdateAvailable) pending++;
                BackgroundUpdateCheckLog.Finished(_logger, snapshot.Results.Count, pending);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin update check failed — will retry at next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

/// <summary>
/// Configuration for the periodic plugin update check. Off by
/// default — outbound network calls are opt-in.
/// </summary>
public sealed class BowirePluginUpdateCheckOptions
{
    /// <summary>
    /// <c>true</c> to run a daily check against nuget.org for newer
    /// versions of every installed sibling plugin. Default
    /// <c>false</c> — the check is opt-in to keep air-gapped /
    /// privacy-sensitive deployments quiet by default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Hours between background checks. Default 24. Floor of 1 — the
    /// hosted service clamps lower values up to 1 hour to keep
    /// nuget.org happy.
    /// </summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// When <c>true</c>, the check considers pre-release versions
    /// (1.0.0-rc.1, &amp;c) as upgrade candidates. Default
    /// <c>false</c> matches the install path's default (stable-only).
    /// </summary>
    public bool IncludePrerelease { get; set; }
}

/// <summary>
/// LoggerMessage-source-generated wrappers around the hot-path log
/// calls so CA1873 stops flagging the boxing of value-typed args.
/// </summary>
internal static partial class BackgroundUpdateCheckLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Plugin update check enabled — running on startup, then every {Interval}.")]
    public static partial void Starting(ILogger logger, TimeSpan interval);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Plugin update check finished: {Plugins} checked, {Pending} update(s) available.")]
    public static partial void Finished(ILogger logger, int plugins, int pending);
}
