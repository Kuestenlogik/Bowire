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
    // mocksManagerOpen retired — Mocks rail mode owns the surface.
    var mocksLoadInFlight = false;
    // #57 per-mock log state: mockId -> { total, capacity, entries, pollTimer }
    var mockLogState = {};
    var mockLogOpenFor = null;       // mockId currently expanded in the manager

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
                return r.json().catch(function () { return { title: 'Mock start failed (' + r.status + ')' }; })
                    .then(function (err) { throw new Error(problemTitle(err, 'Mock start failed')); });
            }
            return r.json();
        }).then(function (summary) {
            mocksList = [summary].concat(mocksList.filter(function (m) { return m.mockId !== summary.mockId; }));
            toast('Mock running on port ' + summary.port, 'success');
            if (railMode === 'mocks') render();
            return summary;
        }).catch(function (err) {
            toast(err.message || 'Mock start failed', 'error');
            throw err;
        });
    }

    // #57: pull a window of the per-mock request log. Poll loop keeps
    // the open drawer fresh; backend supports `since=<lastSeq>` for
    // tail behaviour, used here to merge new entries onto the front.
    function loadMockLog(mockId) {
        var st = mockLogState[mockId] || (mockLogState[mockId] = { entries: [], total: 0, capacity: 0, lastSeq: 0 });
        var qs = 'limit=200' + (st.lastSeq ? '&since=' + st.lastSeq : '');
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId) + '/requests?' + qs)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data || !Array.isArray(data.entries)) return st;
                st.total = data.total;
                st.capacity = data.capacity;
                if (data.entries.length) {
                    // Newest first from backend. Prepend, cap at capacity.
                    st.entries = data.entries.concat(st.entries).slice(0, st.capacity || 200);
                    st.lastSeq = Math.max(st.lastSeq, data.entries[0].sequence || 0);
                }
                return st;
            }).catch(function () { return st; });
    }

    function startMockLogPolling(mockId) {
        var st = mockLogState[mockId] || (mockLogState[mockId] = { entries: [], total: 0, capacity: 0, lastSeq: 0 });
        if (st.pollTimer) return;
        var tick = function () {
            loadMockLog(mockId).then(function () {
                if (mockLogOpenFor === mockId && railMode === 'mocks') render();
            });
            st.pollTimer = setTimeout(tick, 2000);
        };
        tick();
    }

    function stopMockLogPolling(mockId) {
        var st = mockLogState[mockId];
        if (st && st.pollTimer) { clearTimeout(st.pollTimer); st.pollTimer = null; }
    }

    function stopMock(mockId) {
        return fetch(config.prefix + '/api/mocks/' + encodeURIComponent(mockId), { method: 'DELETE' })
            .then(function (r) {
                if (r.ok || r.status === 404) {
                    mocksList = mocksList.filter(function (m) { return m.mockId !== mockId; });
                    stopMockLogPolling(mockId);
                    delete mockLogState[mockId];
                    if (mockLogOpenFor === mockId) mockLogOpenFor = null;
                    if (railMode === 'mocks') render();
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
    // renderMocksManager modal retired in #133 Phase 3.
    // openMocksManager() now jumps to railMode = 'mocks'.

    function openMocksManager() {
        // #133 Phase 3 — modal retired; jump straight to the Mocks
        // rail mode and refresh the list. Callers that still call
        // openMocksManager() (recording's "Run as mock" success,
        // sidebar 'new mock' shortcut) keep working without code
        // change.
        railMode = 'mocks';
        try { localStorage.setItem('bowire_rail_mode', 'mocks'); } catch { /* ignore */ }
        loadMocks().then(function () { render(); });
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
