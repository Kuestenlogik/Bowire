// mocks.js — workbench-side controller for UI-driven mock servers (#56).
//
// Backend surface (BowireMockManagementEndpoints in Bowire.Mock):
//   POST   /api/mocks            -> start mock from a recording payload
//   GET    /api/mocks            -> list running mocks
//   GET    /api/mocks/{id}       -> single mock detail
//   DELETE /api/mocks/{id}       -> stop mock
//
// This file ships only the read/start/stop loop + a minimal Mocks
// overlay. The Mocks-as-sidebar-view (replacing/joining Recordings)
// + topbar status indicator land in a follow-up; the start-from-
// recording button below already unblocks the "I captured something,
// now run it as a mock without dropping to a terminal" path.
//
// Issue #57 (per-mock request log + SSE tail) is wired against the
// same MockRegistry but the endpoint + UI lands separately.

    // ---------- state ----------
    var mocksList = [];                  // [{ mockId, recordingName, port, startedAt }]
    var mocksManagerOpen = false;
    var mocksLoadInFlight = false;

    function loadMocks() {
        if (mocksLoadInFlight) return Promise.resolve(mocksList);
        mocksLoadInFlight = true;
        return fetch(config.prefix + '/api/mocks')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && Array.isArray(data.mocks)) mocksList = data.mocks;
                return mocksList;
            })
            .catch(function () { return mocksList; })
            .finally(function () { mocksLoadInFlight = false; });
    }

    // ---------- actions ----------
    // Start a mock from a recording shape (matching wwwroot's recordingsList
    // item: { id, name, steps, sourceSchema?, ... }). Wraps it into the
    // BowireRecording document shape the backend / MockServer expect.
    function startMockFromRecording(rec, port) {
        if (!rec) return Promise.reject(new Error('No recording'));
        var doc = JSON.stringify({ recordings: [rec] });
        return fetch(config.prefix + '/api/mocks', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                recording: doc,
                name: rec.name || ('recording-' + rec.id),
                port: port || 0    // 0 = OS-assigned
            })
        }).then(function (r) {
            if (!r.ok) {
                return r.json().catch(function () { return { error: 'Mock start failed (' + r.status + ')' }; })
                    .then(function (err) { throw new Error(err && err.error || 'Mock start failed'); });
            }
            return r.json();
        }).then(function (summary) {
            mocksList = [summary].concat(mocksList.filter(function (m) { return m.mockId !== summary.mockId; }));
            toast('Mock running on port ' + summary.port, 'success');
            if (mocksManagerOpen) renderMocksManager();
            return summary;
        }).catch(function (err) {
            toast(err.message || 'Mock start failed', 'error');
            throw err;
        });
    }

    function stopMock(mockId) {
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId), { method: 'DELETE' })
            .then(function (r) {
                if (r.ok || r.status === 404) {
                    mocksList = mocksList.filter(function (m) { return m.mockId !== mockId; });
                    if (mocksManagerOpen) renderMocksManager();
                    return true;
                }
                throw new Error('Stop failed (' + r.status + ')');
            }).catch(function (err) {
                toast(err.message, 'error');
                return false;
            });
    }

    // ---------- UI overlay ----------
    // Minimal full-page overlay (same display pattern as the Recordings
    // manager): table of running mocks + Stop per row + a Refresh
    // affordance. No start-from-here flow yet — start happens via the
    // "Run as mock" button on each Recording row.
    function renderMocksManager() {
        var existing = document.getElementById('bowire-mocks-manager-overlay');
        if (!mocksManagerOpen) {
            if (existing) existing.remove();
            return;
        }

        var overlay = existing || el('div', {
            id: 'bowire-mocks-manager-overlay',
            className: 'bowire-recording-manager-overlay' // reuse recording overlay styling
        });
        overlay.innerHTML = '';

        var panel = el('div', { className: 'bowire-recording-manager-panel' });

        // Header
        var header = el('div', { className: 'bowire-recording-manager-header' });
        header.appendChild(el('h2', { textContent: 'Running mocks' }));
        header.appendChild(el('button', {
            className: 'bowire-recording-manager-close',
            innerHTML: svgIcon('x'),
            title: 'Close',
            onClick: function () { mocksManagerOpen = false; renderMocksManager(); }
        }));
        panel.appendChild(header);

        // Body
        var body = el('div', { className: 'bowire-recording-manager-body' });

        if (mocksList.length === 0) {
            body.appendChild(el('p', {
                className: 'bowire-recording-manager-empty',
                textContent: 'No mocks running. Open Recordings → kebab menu → Run as mock.'
            }));
        } else {
            var table = el('table', { className: 'bowire-mocks-table' });
            var thead = el('thead');
            thead.appendChild(el('tr', {},
                el('th', { textContent: 'Recording' }),
                el('th', { textContent: 'Port' }),
                el('th', { textContent: 'Started' }),
                el('th', { textContent: 'URL' }),
                el('th', { textContent: '' })
            ));
            table.appendChild(thead);

            var tbody = el('tbody');
            mocksList.forEach(function (m) {
                var url = 'http://127.0.0.1:' + m.port;
                var startedAgo = (function () {
                    var t = new Date(m.startedAt).getTime();
                    if (!t) return '—';
                    var s = Math.max(0, Math.round((Date.now() - t) / 1000));
                    if (s < 60) return s + 's ago';
                    var mins = Math.round(s / 60);
                    if (mins < 60) return mins + 'm ago';
                    return Math.round(mins / 60) + 'h ago';
                })();
                tbody.appendChild(el('tr', {},
                    el('td', { textContent: m.recordingName }),
                    el('td', { textContent: String(m.port) }),
                    el('td', { textContent: startedAgo, title: m.startedAt }),
                    el('td', {},
                        el('a', { href: url, target: '_blank', rel: 'noopener', textContent: url })
                    ),
                    el('td', {},
                        el('button', {
                            className: 'bowire-recording-action-btn bowire-recording-action-danger',
                            textContent: 'Stop',
                            onClick: function () { stopMock(m.mockId); }
                        })
                    )
                ));
            });
            table.appendChild(tbody);
            body.appendChild(table);
        }

        // Footer with refresh
        var footer = el('div', { className: 'bowire-recording-manager-footer' });
        footer.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            textContent: 'Refresh',
            onClick: function () { loadMocks().then(renderMocksManager); }
        }));
        panel.appendChild(body);
        panel.appendChild(footer);

        overlay.appendChild(panel);
        if (!existing) document.body.appendChild(overlay);
    }

    function openMocksManager() {
        mocksManagerOpen = true;
        loadMocks().then(renderMocksManager);
    }

    // Expose for recording.js + render-sidebar.js to call from their
    // own UI hooks. The minimal v1 plumbing — full sidebar integration
    // (topbar pill, drag-to-pin, etc.) is a follow-up.
    window.__bowireMocks = {
        list: function () { return mocksList; },
        load: loadMocks,
        startFromRecording: startMockFromRecording,
        stop: stopMock,
        open: openMocksManager
    };
