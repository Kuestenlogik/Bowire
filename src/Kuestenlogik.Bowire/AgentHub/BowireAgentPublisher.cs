// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using Kuestenlogik.Bowire.Sources;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.AgentHub;

/// <summary>
/// The agent side of #128: pushes this Bowire's registration to its hub at
/// start and on every heartbeat, and deregisters on shutdown.
/// </summary>
/// <remarks>
/// A hub that is down or refuses is logged and retried at the next
/// heartbeat — an agent never fails its host over the hub.
/// </remarks>
internal sealed partial class BowireAgentPublisher : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly BowireAgentOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Func<BowireAgentRegistration> _snapshot;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _agentId;
    private bool _lastFailed;

    public BowireAgentPublisher(BowireAgentOptions options, Func<BowireAgentRegistration> snapshot, ILogger logger, HttpMessageHandler? handler = null)
    {
        _options = options;
        _snapshot = snapshot;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>The id the hub gave this agent, once a registration went through.</summary>
    public string? AgentId => _agentId;

    private string HubBase => HubBaseFor(_options.HubUrl!);

    /// <summary>
    /// The hub's <c>/hub</c> root. Takes the hub's base URL, and also one
    /// that already ends in <c>/hub</c> — both get written.
    /// </summary>
    internal static string HubBaseFor(string hubUrl)
    {
        var trimmed = hubUrl.TrimEnd('/');
        return trimmed.EndsWith("/hub", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/hub";
    }

    /// <summary>Register now, then keep heartbeating until <see cref="StopAsync"/>.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            do
            {
                await PushOnceAsync(token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
    }

    /// <summary>Send one registration / heartbeat; false when the hub did not take it.</summary>
    public async Task<bool> PushOnceAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, HubBase + "/agents")
            {
                Content = JsonContent.Create(_snapshot(), options: s_json),
            };
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (!_lastFailed) Log.Refused(_logger, _options.HubUrl!, (int)response.StatusCode);
                _lastFailed = true;
                return false;
            }
            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (body.RootElement.TryGetProperty("agentId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var agentId = id.GetString()!;
                if (_agentId is null || _lastFailed) Log.Registered(_logger, _options.HubUrl!, agentId);
                _agentId = agentId;
            }
            _lastFailed = false;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // the hub being away must never reach the host
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Log the first failure of a streak only: a hub that is down for
            // an hour should not write 120 warnings.
            if (!_lastFailed) Log.Unreachable(_logger, _options.HubUrl!, ex.Message);
            _lastFailed = true;
            return false;
        }
    }

    /// <summary>Stop heartbeating and tell the hub this agent is gone.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is not null) await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        if (_agentId is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Delete, HubBase + "/agents/" + Uri.EscapeDataString(_agentId));
            Authorize(request);
            using var _ = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // shutting down: the hub drops a silent agent on its own
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.Token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// What the agent tells the hub: its name, where its workbench is, and
    /// the services it fronts — its configured server URLs, or in embedded
    /// mode the host itself.
    /// </summary>
    internal static BowireAgentRegistration BuildRegistration(BowireOptions options, string callbackUrl, string? applicationName)
    {
        var agent = options.Agent;
        var serviceName = agent.ServiceName ?? applicationName ?? "bowire";
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ServerUrl)) urls.Add(options.ServerUrl);
        urls.AddRange(options.ServerUrls.Where(u => !string.IsNullOrWhiteSpace(u)));
        if (urls.Count == 0 && options.Mode == BowireMode.Embedded && Uri.TryCreate(callbackUrl, UriKind.Absolute, out var cb))
        {
            urls.Add(cb.GetLeftPart(UriPartial.Authority));
        }
        var entries = urls.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(u => new BowireCatalogueEntry(u, serviceName))
            .ToList();
        return new BowireAgentRegistration(
            serviceName,
            agent.InstanceId ?? Environment.MachineName,
            callbackUrl,
            agent.Version,
            agent.Owner,
            [.. agent.Tags],
            entries,
            agent.HeartbeatInterval.TotalSeconds);
    }

    /// <summary>
    /// The workbench URL of this host: the first listening address, with a
    /// wildcard host (<c>0.0.0.0</c>, <c>[::]</c>, <c>+</c>, <c>*</c>)
    /// swapped for the machine name, plus the Bowire route prefix.
    /// </summary>
    internal static string? DeriveCallbackUrl(IReadOnlyCollection<string> addresses, string basePath)
    {
        var address = addresses.FirstOrDefault(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault();
        if (address is null) return null;
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return null;
        var rest = address[(schemeEnd + 3)..];
        var portStart = rest.LastIndexOf(':');
        var host = portStart > 0 && !rest.EndsWith(']') ? rest[..portStart] : rest;
        var port = portStart > 0 && !rest.EndsWith(']') ? rest[portStart..] : string.Empty;
        if (host is "0.0.0.0" or "[::]" or "+" or "*") host = Environment.MachineName;
        return address[..(schemeEnd + 3)] + host + port.TrimEnd('/') + basePath;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1280, Level = LogLevel.Information, Message = "Registered with Bowire hub {Hub} as {AgentId}")]
        public static partial void Registered(ILogger logger, string hub, string agentId);

        [LoggerMessage(EventId = 1281, Level = LogLevel.Warning, Message = "Bowire hub {Hub} refused the registration: HTTP {Status}. Retrying at every heartbeat.")]
        public static partial void Refused(ILogger logger, string hub, int status);

        [LoggerMessage(EventId = 1282, Level = LogLevel.Warning, Message = "Bowire hub {Hub} is not reachable ({Reason}). Retrying at every heartbeat.")]
        public static partial void Unreachable(ILogger logger, string hub, string reason);
    }
}
