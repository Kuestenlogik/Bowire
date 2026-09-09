    // #153 UI phase — Reverse-proxy launcher.
    //
    // Surfaces the existing BowireReverseProxyHost (Phase C of the
    // interceptor track, shipped via #307) on top of the workbench.
    // Two entry points:
    //   - Topbar → Tools menu → "Reverse proxy…" opens the start
    //     modal (upstream URL + port).
    //   - Settings → Tools → "Reverse proxy" lists every running
    //     proxy with a per-row Stop and a global "Stop all".
    //
    // Backend lives in BowireToolsEndpoints (POST start/stop, GET
    // list). The registry hooks IHostApplicationLifetime, so every
    // started proxy dies when bowire.exe exits — the modal +
    // settings list call that out so operators don't expect a daemon.

    var reverseProxyState = {
        loaded: false,
        loading: false,
        running: []     // [{ port, upstream, startedAt }]
    };

    function reverseProxyPrefix() {
        return (typeof config !== 'undefined' && config && config.prefix) ? config.prefix : '';
    }

    function refreshReverseProxies(opts) {
        opts = opts || {};
        if (reverseProxyState.loading) return;
        reverseProxyState.loading = true;
        var p = reverseProxyPrefix();
        return fetch(p + '/api/tools/reverse-proxy')
            .then(function (r) { return r.ok ? r.json() : { proxies: [] }; })
            .catch(function () { return { proxies: [] }; })
            .then(function (body) {
                reverseProxyState.loaded = true;
                reverseProxyState.loading = false;
                reverseProxyState.running = Array.isArray(body && body.proxies) ? body.proxies : [];
                if (opts.rerender !== false && typeof render === 'function') render();
            });
    }

    // Suggest the next free port in the 5200/5201/… window. We can't
    // probe TCP from the browser so this is a best-effort UI hint —
    // collisions surface as a 409 from POST /start, which the modal
    // displays inline.
    function suggestNextReverseProxyPort() {
        var inUse = {};
        (reverseProxyState.running || []).forEach(function (e) { inUse[e.port] = true; });
        for (var p = 5200; p < 5300; p++) {
            if (!inUse[p]) return p;
        }
        return 5200;
    }

    // Open the reverse-proxy modal. After a successful start the
    // modal swaps to a "Running on :NNNN → upstream" panel with a
    // Stop button instead of dismissing — the operator immediately
    // sees the bound port and the lifetime hint without having to
    // re-open Settings.
    function openReverseProxyModal() {
        var existing = document.querySelector('.bowire-reverse-proxy-overlay');
        if (existing) existing.remove();

        var startedEntry = null;     // { port, upstream, startedAt } once we got a 200
        var errorText = null;
        var busy = false;

        var overlay = el('div', {
            className: 'bowire-confirm-overlay bowire-reverse-proxy-overlay',
            onClick: function (e) { if (e.target === overlay) overlay.remove(); }
        });
        var dialog = el('div', {
            className: 'bowire-confirm-dialog bowire-reverse-proxy-dialog',
            role: 'dialog',
            'aria-modal': 'true'
        });
        overlay.appendChild(dialog);

        function rerender() {
            dialog.innerHTML = '';
            dialog.appendChild(el('div', {
                className: 'bowire-confirm-title',
                textContent: t('reverseProxy.title')
            }));

            if (startedEntry) {
                dialog.appendChild(el('div', {
                    className: 'bowire-confirm-message',
                    style: 'display:flex;flex-direction:column;gap:8px'
                },
                    el('div', { style: 'font-weight:500',
                        textContent: t('reverseProxy.running', {
                            port: startedEntry.port, upstream: startedEntry.upstream
                        }) }),
                    el('div', { style: 'font-size:12px;color:var(--bowire-text-tertiary)',
                        textContent: t('reverseProxy.stopsOnExit') })
                ));
                var stopBtn = el('button', {
                    className: 'bowire-confirm-btn',
                    textContent: busy ? 'Stopping…' : 'Stop',
                    disabled: busy ? 'disabled' : null,
                    onClick: function () {
                        if (busy) return;
                        busy = true; rerender();
                        fetch(reverseProxyPrefix() + '/api/tools/reverse-proxy/stop', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ port: startedEntry.port })
                        }).then(function () {
                            startedEntry = null;
                            busy = false;
                            refreshReverseProxies({ rerender: true });
                            overlay.remove();
                            if (typeof toast === 'function') toast(t('reverseProxy.stoppedToast'), 'info');
                        }).catch(function () {
                            busy = false;
                            errorText = t('reverseProxy.stopFailed');
                            rerender();
                        });
                    }
                });
                var closeBtn = el('button', {
                    className: 'bowire-confirm-btn cancel',
                    textContent: t('common.close'),
                    onClick: function () { overlay.remove(); }
                });
                dialog.appendChild(el('div', { className: 'bowire-confirm-actions' }, closeBtn, stopBtn));
                if (errorText) {
                    dialog.appendChild(el('div', {
                        className: 'bowire-reverse-proxy-error',
                        style: 'color:var(--bowire-text-danger,#dc2626);font-size:12px;margin-top:8px',
                        textContent: errorText
                    }));
                }
                return;
            }

            dialog.appendChild(el('div', {
                className: 'bowire-confirm-message',
                textContent: t('reverseProxy.lede')
            }));

            var upstreamInput = el('input', {
                type: 'url',
                className: 'bowire-settings-input',
                placeholder: t('invokeUrl.customPlaceholder'),
                style: 'width:100%;margin-bottom:8px',
                value: lastReverseProxyUpstream()
            });
            var portInput = el('input', {
                type: 'number',
                className: 'bowire-settings-input',
                min: '1',
                max: '65535',
                value: String(suggestNextReverseProxyPort()),
                style: 'width:120px'
            });

            dialog.appendChild(el('div', {
                className: 'bowire-parallel-field'
            },
                el('label', { className: 'bowire-parallel-field-label', textContent: t('reverseProxy.upstreamLabel') }),
                upstreamInput
            ));
            dialog.appendChild(el('div', {
                className: 'bowire-parallel-field'
            },
                el('label', { className: 'bowire-parallel-field-label', textContent: t('reverseProxy.portLabel') }),
                portInput,
                el('div', { className: 'bowire-parallel-field-hint',
                    textContent: t('reverseProxy.loopbackHint') })
            ));

            var cancelBtn = el('button', {
                className: 'bowire-confirm-btn cancel',
                textContent: t('common.cancel'),
                onClick: function () { overlay.remove(); }
            });
            var startBtn = el('button', {
                className: 'bowire-confirm-btn',
                textContent: busy ? t('reverseProxy.starting') : t('reverseProxy.start'),
                disabled: busy ? 'disabled' : null,
                onClick: function () {
                    if (busy) return;
                    var upstream = (upstreamInput.value || '').trim();
                    var port = parseInt(portInput.value, 10);
                    if (!upstream || !/^https?:\/\//i.test(upstream)) {
                        errorText = t('reverseProxy.badUpstream');
                        rerender();
                        return;
                    }
                    if (!port || port < 1 || port > 65535) {
                        errorText = t('reverseProxy.badPort');
                        rerender();
                        return;
                    }
                    errorText = null;
                    busy = true; rerender();
                    try { localStorage.setItem('bowire_reverse_proxy_upstream', upstream); }
                    catch { /* ignore */ }
                    fetch(reverseProxyPrefix() + '/api/tools/reverse-proxy/start', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ upstream: upstream, port: port })
                    }).then(function (r) {
                        return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; });
                    }).then(function (resp) {
                        busy = false;
                        if (resp.ok) {
                            startedEntry = resp.body;
                            refreshReverseProxies({ rerender: true });
                            if (typeof toast === 'function') {
                                toast(t('reverseProxy.startedToast', { port: startedEntry.port }), 'success');
                            }
                            rerender();
                        } else {
                            errorText = (resp.body && (resp.body.detail || resp.body.title))
                                || ('Start failed (HTTP ' + resp.status + ').');
                            rerender();
                        }
                    }).catch(function (err) {
                        busy = false;
                        errorText = (err && err.message) ? err.message : 'Network error.';
                        rerender();
                    });
                }
            });
            dialog.appendChild(el('div', { className: 'bowire-confirm-actions' }, cancelBtn, startBtn));
            if (errorText) {
                dialog.appendChild(el('div', {
                    className: 'bowire-reverse-proxy-error',
                    style: 'color:var(--bowire-text-danger,#dc2626);font-size:12px;margin-top:8px',
                    textContent: errorText
                }));
            }
        }

        rerender();
        document.body.appendChild(overlay);
    }

    function lastReverseProxyUpstream() {
        try { return localStorage.getItem('bowire_reverse_proxy_upstream') || ''; }
        catch { return ''; }
    }

    // Stop a single proxy from the Settings list. Idempotent on the
    // server side so a stale double-click doesn't error.
    function stopReverseProxy(port) {
        return fetch(reverseProxyPrefix() + '/api/tools/reverse-proxy/stop', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ port: port })
        }).then(function () {
            refreshReverseProxies({ rerender: true });
            if (typeof toast === 'function') toast(t('reverseProxy.stoppedOne', { port: port }), 'info');
        });
    }

    function stopAllReverseProxies() {
        var running = (reverseProxyState.running || []).slice();
        if (running.length === 0) return Promise.resolve();
        return Promise.all(running.map(function (e) {
            return fetch(reverseProxyPrefix() + '/api/tools/reverse-proxy/stop', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ port: e.port })
            }).catch(function () { /* keep going on per-row failure */ });
        })).then(function () {
            refreshReverseProxies({ rerender: true });
            if (typeof toast === 'function') toast(t('reverseProxy.stoppedAll'), 'info');
        });
    }

    // Expose for settings.js + render-env-auth.js (topbar entry).
    window.bowireOpenReverseProxyModal = openReverseProxyModal;
    window.bowireRefreshReverseProxies = refreshReverseProxies;
    window.bowireStopReverseProxy = stopReverseProxy;
    window.bowireStopAllReverseProxies = stopAllReverseProxies;
    window.bowireReverseProxyState = reverseProxyState;
