    // #128 — the Bowire hub, seen from its own workbench.
    //
    // A Bowire with Bowire:Hub:Enabled keeps a list of agents: other
    // Bowires that push their registration to it. The workbench shows that
    // list (with a link to each agent's own workbench — the hub does not
    // proxy) and offers the agents tagged `parallel-executor` as executors
    // in the Parallel sessions dialog.
    //
    // Everything goes through {prefix}/api/hub/agents, which answers
    // { enabled: false } on a Bowire that is not a hub, so this is a no-op
    // there. `var` for the same reason as catalogue.js: renderers in
    // earlier fragments read this state.
    var hubInfo = { enabled: false, agents: [] };
    var hubFetchedAt = 0;
    var hubFetching = null;

    // How old the list may get before showing it fetches it again. The
    // "seen … ago" line would otherwise count from the last fetch, not
    // from the agent's last heartbeat.
    var HUB_REFRESH_MS = 10000;

    var HUB_EXECUTOR_TAG = 'parallel-executor';

    function fetchHubAgents() {
        if (!hubFetching) {
            hubFetching = _fetchHubAgents().finally(function () { hubFetching = null; });
        }
        return hubFetching;
    }

    async function _fetchHubAgents() {
        try {
            var resp = await fetch(config.prefix + '/api/hub/agents');
            if (!resp.ok) return hubInfo;
            var body = await resp.json();
            hubInfo = {
                enabled: !!(body && body.enabled),
                agents: (body && Array.isArray(body.agents)) ? body.agents : []
            };
            hubFetchedAt = Date.now();
        } catch (e) {
            // A hub view that cannot load leaves the workbench as it was.
        }
        return hubInfo;
    }

    function hubIsEnabled() {
        return !!(hubInfo && hubInfo.enabled);
    }

    // Only http(s) callback URLs become links or executor hosts. The hub
    // already refuses anything else; this is the second fence, because the
    // value came from another machine.
    function _hubSafeUrl(url) {
        return typeof url === 'string' && /^https?:\/\//i.test(url) ? url : null;
    }

    // The live agents that offer themselves as parallel executors.
    function hubExecutorsFrom(agents) {
        return (Array.isArray(agents) ? agents : []).filter(function (a) {
            return a && a.live && _hubSafeUrl(a.callbackUrl)
                && Array.isArray(a.tags) && a.tags.indexOf(HUB_EXECUTOR_TAG) >= 0;
        });
    }

    // Add a host to the comma-separated hosts field, once.
    function hubAppendHost(value, url) {
        var hosts = String(value || '').split(',')
            .map(function (h) { return h.trim(); })
            .filter(function (h) { return h.length > 0; });
        var norm = function (h) { return h.replace(/\/+$/, '').toLowerCase(); };
        if (!hosts.some(function (h) { return norm(h) === norm(url); })) hosts.push(url);
        return hosts.join(', ');
    }

    function _hubAgentLabel(a) {
        return a.serviceName + (a.instanceId ? ' · ' + a.instanceId : '');
    }

    // The executor picker under the Hosts field of the Parallel sessions
    // dialog: one chip per live executor agent, a click adds its URL.
    // Returns a node that fills itself once the hub has answered, and stays
    // empty on a Bowire that is not a hub.
    function renderHubExecutorPicker(hostsInput) {
        var slot = el('div', { className: 'bowire-hub-executors' });
        fetchHubAgents().then(function (info) {
            if (!info.enabled) return;
            var executors = hubExecutorsFrom(info.agents);
            slot.appendChild(el('span', {
                className: 'bowire-parallel-field-hint',
                textContent: executors.length > 0 ? t('hub.executors.pick') : t('hub.executors.none')
            }));
            executors.forEach(function (a) {
                slot.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-hub-executor-chip',
                    title: a.callbackUrl,
                    textContent: '+ ' + _hubAgentLabel(a),
                    onClick: function () {
                        hostsInput.value = hubAppendHost(hostsInput.value, a.callbackUrl);
                    }
                }));
            });
        });
        return slot;
    }

    function _hubAgo(iso) {
        var ms = Date.now() - Date.parse(iso);
        if (!isFinite(ms)) return '';
        var s = Math.max(0, Math.round(ms / 1000));
        if (s < 90) return t('hub.agents.secondsAgo', { count: s });
        return t('hub.agents.minutesAgo', { count: Math.round(s / 60) });
    }

    // The agent list for the workspace detail. Null on a Bowire that is
    // not a hub.
    function renderHubAgentList() {
        if (!hubIsEnabled()) return null;
        if (Date.now() - hubFetchedAt > HUB_REFRESH_MS && !hubFetching) {
            fetchHubAgents().then(function () { render(); });
        }
        var wrap = el('div', { className: 'bowire-hub-agents' });
        var head = el('div', { style: 'display:flex;align-items:center;gap:8px;margin-top:14px' },
            el('div', {
                className: 'bowire-ws-detail-section-label',
                style: 'margin:0',
                textContent: t('hub.agents.title', { count: hubInfo.agents.length })
            }),
            el('button', {
                className: 'bowire-ws-detail-action',
                textContent: t('sidebar.sources.refresh'),
                onClick: function () { fetchHubAgents().then(function () { render(); }); }
            })
        );
        wrap.appendChild(head);
        if (hubInfo.agents.length === 0) {
            wrap.appendChild(el('div', {
                className: 'bowire-ws-detail-stat-hint',
                textContent: t('hub.agents.empty')
            }));
            return wrap;
        }
        hubInfo.agents.forEach(function (a) {
            var url = _hubSafeUrl(a.callbackUrl);
            var meta = [a.version, a.owner].filter(function (x) { return !!x; }).join(' · ');
            wrap.appendChild(el('div', { className: 'bowire-hub-agent' + (a.live ? '' : ' stale') },
                el('span', {
                    className: 'bowire-hub-agent-dot',
                    title: a.live ? t('hub.agents.live') : t('hub.agents.stale')
                }),
                el('div', { className: 'bowire-hub-agent-main' },
                    el('div', { className: 'bowire-hub-agent-name', textContent: _hubAgentLabel(a) }),
                    el('div', {
                        className: 'bowire-ws-detail-stat-hint',
                        textContent: [meta, (a.tags || []).join(', '), t('hub.agents.lastSeen', { ago: _hubAgo(a.lastSeen) })]
                            .filter(function (x) { return !!x; }).join(' — ')
                    })
                ),
                url ? el('a', {
                    className: 'bowire-hub-agent-open',
                    href: url,
                    target: '_blank',
                    rel: 'noopener noreferrer',
                    textContent: t('hub.agents.open')
                }) : null
            ));
        });
        return wrap;
    }
