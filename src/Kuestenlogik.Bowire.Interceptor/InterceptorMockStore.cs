// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// In-memory registry of <see cref="InterceptorMockRule"/>s consulted by
/// the interceptor middleware on every request when
/// <c>BowireInterceptorOptions.MocksEnabled</c> is on (#308, Phase D).
/// Rules are matched in insertion order — the first match wins, so the
/// workbench can compose specific overrides before broad catch-alls.
/// </summary>
/// <remarks>
/// <para>
/// Singleton-scoped (registered next to
/// <see cref="InterceptedFlowStore"/>): one rule set per host process.
/// The workbench's "Mocks" sub-tab in the Intercepted rail is the
/// canonical CRUD surface — see
/// <c>BowireInterceptorEndpoints.MapBowireInterceptorEndpoints</c>.
/// </para>
/// <para>
/// State is process-local — surviving a restart is out of scope for
/// Phase D; if an operator wants a persisted set they export to a
/// recording via the Mocks rail and reload it at startup. Phase E will
/// add a JSON sidecar.
/// </para>
/// </remarks>
public sealed class InterceptorMockStore
{
    private readonly Lock _gate = new();
    private readonly List<InterceptorMockRule> _rules = new();
    private long _nextId;

    /// <summary>Snapshot of currently-registered rules in insertion order.</summary>
    public IReadOnlyList<InterceptorMockRule> Snapshot()
    {
        lock (_gate)
        {
            return _rules.ToArray();
        }
    }

    /// <summary>
    /// Look up the first matching rule for a method + path. Returns
    /// <c>null</c> when nothing matches; the middleware then falls back
    /// to forwarding the request to the host's pipeline.
    /// </summary>
    public InterceptorMockRule? FindMatch(string method, string path)
    {
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(path)) return null;
        lock (_gate)
        {
            foreach (var rule in _rules)
            {
                if (rule.Matches(method, path)) return rule;
            }
        }
        return null;
    }

    /// <summary>
    /// Add a rule. When the caller leaves <see cref="InterceptorMockRule.Id"/>
    /// blank, a fresh monotonic id is assigned. Returns the persisted
    /// rule (with id filled in) so the workbench can echo it back to
    /// the UI without a second round-trip.
    /// </summary>
    public InterceptorMockRule Add(InterceptorMockRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var id = string.IsNullOrEmpty(rule.Id) ? NextId() : rule.Id;
        var name = string.IsNullOrWhiteSpace(rule.Name)
            ? $"{rule.Method} {rule.PathPattern}"
            : rule.Name;
        var persisted = new InterceptorMockRule
        {
            Id = id,
            Name = name,
            PathPattern = string.IsNullOrEmpty(rule.PathPattern) ? "*" : rule.PathPattern,
            Method = string.IsNullOrEmpty(rule.Method) ? "*" : rule.Method,
            ResponseStatus = rule.ResponseStatus <= 0 ? 200 : rule.ResponseStatus,
            ResponseHeaders = rule.ResponseHeaders ?? Array.Empty<KeyValuePair<string, string>>(),
            ResponseBody = rule.ResponseBody,
            ResponseBodyBase64 = rule.ResponseBodyBase64,
            DelayMs = rule.DelayMs < 0 ? 0 : rule.DelayMs,
            Enabled = rule.Enabled,
        };
        lock (_gate)
        {
            // Replace if a rule with the same id already exists — this
            // is the "edit" path; the workbench addresses rules by id.
            for (var i = 0; i < _rules.Count; i++)
            {
                if (string.Equals(_rules[i].Id, persisted.Id, StringComparison.Ordinal))
                {
                    _rules[i] = persisted;
                    return persisted;
                }
            }
            _rules.Add(persisted);
        }
        return persisted;
    }

    /// <summary>Remove a rule by id. Returns whether the rule existed.</summary>
    public bool Remove(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_gate)
        {
            for (var i = 0; i < _rules.Count; i++)
            {
                if (string.Equals(_rules[i].Id, id, StringComparison.Ordinal))
                {
                    _rules.RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Drop every rule (workbench "Clear all" button + tests).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
        }
    }

    private string NextId() =>
        "mock_" + Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
