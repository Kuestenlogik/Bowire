    // ---- Recordings ----
    // Postman-style scenario recorder. The user clicks Record, makes a few
    // calls (any protocol — gRPC, REST, GraphQL, WebSocket, MCP, ...), clicks
    // Stop, and the captured sequence becomes a named, replayable recording.
    // The capture hook lives in invokeUnary / invokeStreaming / channelConnect
    // — anywhere addHistory is called we also push the same payload into the
    // active recording when isRecording() is true. Persistence is the same
    // localStorage-cache-with-disk-sync pattern used by environments.

    function isRecording() {
        return recordingActiveId !== null;
    }

    function nextRecordingId() {
        return 'rec_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    }

    function nextStepId() {
        return 'step_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    }

    function loadRecordings() {
        try {
            var raw = localStorage.getItem(wsKey(RECORDINGS_KEY));
            var list = raw ? JSON.parse(raw) : [];
            return Array.isArray(list) ? list : [];
        } catch {
            return [];
        }
    }

    function persistRecordings() {
        try {
            localStorage.setItem(wsKey(RECORDINGS_KEY), JSON.stringify(recordingsList));
        } catch { /* localStorage full / disabled — fall through */ }
        scheduleRecordingsDiskSync();
    }

    // Debounced PUT to /api/recordings — same 400ms debounce as environments
    // so the disk file stays in sync without thrashing on every step append.
    var _recordingsDiskSyncTimer = null;
    // #144 Phase 1.6 — append workspaceId so the disk store routes
    // captures into ~/.bowire/workspaces/<wsId>/recordings/. Falls
    // back to empty when the prologue hasn't loaded the workspace
    // yet (very early bootstrap) so the legacy unscoped path
    // applies.
    // #212 Phase 0 — workspace-level storage decision drives every
    // disk-touch point. Two modes: 'disk' (default, source of truth
    // on disk + best-effort localStorage cache) and 'browser-only'
    // (localStorage only). The retired 'disk-only' mode's value
    // (skip localStorage to avoid quota errors) folds into the
    // default disk mode automatically — every localStorage write is
    // already wrapped in try/catch so quota errors degrade silently.
    function _recordingsModeAllowsDisk() {
        try {
            if (typeof getWorkspaceStorageMode === 'function') {
                return getWorkspaceStorageMode() !== 'browser-only';
            }
            if (typeof getRecordingStorageMode === 'function') {
                return getRecordingStorageMode() !== 'browser-only';
            }
        } catch { /* fall through */ }
        return true;
    }
    // Always true now — localStorage is best-effort in disk mode and
    // the source of truth in browser-only mode, so every callsite
    // can write through and let the try/catch around the write
    // handle quota / disabled-storage failure.
    function _recordingsModeAllowsLocalStorage() { return true; }

    // #144 Phase 1.8 — lazy-fetch step bodies for a manifest-only
    // recording. Called when the operator opens a recording's
    // detail view; walks the stepsManifest entries and fetches
    // each step from /api/recordings/<id>/step/<n>, replacing the
    // manifest array with the assembled steps[] in place.
    // Returns a promise so callers can await before reading
    // rec.steps. Idempotent — re-running on a hydrated recording
    // is a no-op.
    function hydrateRecording(rec) {
        if (!rec) return Promise.resolve(rec);
        if (Array.isArray(rec.steps) && rec.steps.length > 0) {
            // Already hydrated; nothing to do.
            return Promise.resolve(rec);
        }
        var manifest = Array.isArray(rec.stepsManifest) ? rec.stepsManifest : [];
        if (manifest.length === 0) {
            rec.steps = [];
            return Promise.resolve(rec);
        }
        var prefix = config.prefix;
        var wsParam = _recordingsWsParam();
        var promises = manifest.map(function (entry, idx) {
            var url = prefix + '/api/recordings/'
                + encodeURIComponent(rec.id) + '/step/' + idx + wsParam;
            return fetch(url)
                .then(function (r) { return r.ok ? r.json() : null; })
                .catch(function () { return null; });
        });
        return Promise.all(promises).then(function (steps) {
            rec.steps = steps.filter(function (s) { return !!s; });
            // Keep stepsManifest around so the operator can switch
            // back to manifest-only view if we add one later.
            return rec;
        });
    }

    // #144 Phase 1.7 — POST a single captured step to the chunked
    // store so capture writes are O(1) per step instead of O(n)
    // rewrites of the entire document. Fire-and-forget; if the
    // request fails the localStorage cache still has the step and
    // the next debounced full-document PUT (triggered by any other
    // mutation) syncs the disk back into shape.
    function appendStepToDisk(rec, step) {
        // #144 Phase 1.7+ — workspace storage-mode gate. When the
        // active workspace opted into 'browser-only', skip every
        // backend write — localStorage is the source of truth.
        if (!_recordingsModeAllowsDisk()) return;
        var url = config.prefix + '/api/recordings/'
            + encodeURIComponent(rec.id) + '/step' + _recordingsWsParam();
        var meta = {
            id: rec.id,
            name: rec.name,
            description: rec.description,
            createdAt: rec.createdAt,
            recordingFormatVersion: rec.recordingFormatVersion,
            schemaSnapshot: rec.schemaSnapshot,
        };
        // #125 Phase 4 — sanitise before the disk write so secret
        // values never land in the recording file. The resolver
        // hands the real value to the upstream; if an API echoes the
        // bearer token back in a response field, this strip prevents
        // the recording from carrying it.
        var safeStep = (typeof sanitiseForExport === 'function')
            ? sanitiseForExport(step) : step;
        fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ step: safeStep, metadata: meta }),
        }).catch(function () { /* offline — localStorage still has it */ });
    }

    function _recordingsWsParam() {
        try {
            if (typeof activeWorkspaceId === 'string' && activeWorkspaceId) {
                var qs = '?workspaceId=' + encodeURIComponent(activeWorkspaceId);
                // #196 Phase 2.3 — when the active workspace carries a
                // storageRoot override (git-backed checked-out folder),
                // forward it so the backend resolver routes recordings
                // under <storageRoot>/recordings/ instead of the legacy
                // ~/.bowire/workspaces/<id>/recordings/.
                if (typeof getWorkspaceStorageRoot === 'function') {
                    var root = getWorkspaceStorageRoot();
                    if (root) qs += '&storageRoot=' + encodeURIComponent(root);
                }
                return qs;
            }
        } catch { /* ignore */ }
        return '';
    }
    function scheduleRecordingsDiskSync() {
        // Skip the debounced PUT when this workspace is browser-only.
        // localStorage is the source of truth; no backend call.
        if (!_recordingsModeAllowsDisk()) return;
        if (_recordingsDiskSyncTimer) clearTimeout(_recordingsDiskSyncTimer);
        _recordingsDiskSyncTimer = setTimeout(function () {
            _recordingsDiskSyncTimer = null;
            // #125 Phase 4 — sanitise secret values before the
            // full-document PUT. Same contract as appendStepToDisk.
            var safeRecordings = (typeof sanitiseForExport === 'function')
                ? sanitiseForExport(recordingsList) : recordingsList;
            var payload = JSON.stringify({ recordings: safeRecordings });
            fetch(config.prefix + '/api/recordings' + _recordingsWsParam(), {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: payload
            }).catch(function () { /* offline — localStorage still has it */ });
        }, 400);
    }

    // Pull recordings from disk on init so the list survives browser changes
    // and CLI usage. The disk file wins over the localStorage cache (the
    // localStorage cache only matters for instant updates between roundtrips).
    function loadRecordingsFromDisk() {
        if (!_recordingsModeAllowsDisk()) {
            // Browser-only workspace: skip the disk fetch and read
            // the localStorage cache directly. Cleaner than a fetch
            // that'd be a no-op anyway.
            try { recordingsList = loadRecordings(); }
            catch { recordingsList = []; }
            return Promise.resolve();
        }
        // #212 Phase 0 — disk mode unified. Initial fetch grabs the
        // recordings from /api/recordings; localStorage write is
        // best-effort (try/catch). The old 'disk-only' manifest-only
        // optimisation moves to #144 Phase 2 — there it stays as an
        // internal optimisation triggered by recordings size, not as
        // a user-facing mode.
        var url = config.prefix + '/api/recordings' + _recordingsWsParam();
        return fetch(url)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && Array.isArray(data.recordings)) {
                    recordingsList = data.recordings;
                    try { localStorage.setItem(wsKey(RECORDINGS_KEY), JSON.stringify(recordingsList)); } catch { /* quota / disabled — fall through */ }
                } else {
                    recordingsList = loadRecordings();
                }
            })
            .catch(function () {
                recordingsList = loadRecordings();
            });
    }

    function startRecording(name) {
        // If the most recent recording is empty (0 steps), reuse it
        // instead of piling another empty 'Recording <timestamp>'
        // entry onto the list. Operators who tap Start Recording
        // several times in a row before capturing anything were
        // ending up with multiple stale 0-step entries in the
        // Recordings sidebar — now they get one slot that resets
        // its name + activates again.
        var lastIdx = recordingsList.length - 1;
        var lastEmpty = lastIdx >= 0
            && (!recordingsList[lastIdx].steps || recordingsList[lastIdx].steps.length === 0)
            ? recordingsList[lastIdx]
            : null;
        if (lastEmpty) {
            if (name) lastEmpty.name = name;
            else lastEmpty.name = 'Recording ' + new Date().toLocaleString();
            lastEmpty.createdAt = Date.now();
            recordingActiveId = lastEmpty.id;
            persistRecordings();
            addConsoleEntry({ type: 'response', method: lastEmpty.name, status: 'Recording resumed (empty)' });
            render();
            return;
        }
        var rec = {
            id: nextRecordingId(),
            name: name || ('Recording ' + new Date().toLocaleString()),
            description: '',
            createdAt: Date.now(),
            // Forward-looking version tag — bumped when later phases add
            // fields the mock depends on. v1 = Phase-1a (REST unary only);
            // v2 = Phase-1b (adds responseBinary for gRPC-unary replay).
            // The mock-server loader accepts the versions it was built for
            // and rejects anything newer. See docs/concepts/mock-server-phase1.md.
            recordingFormatVersion: 2,
            // Phase-5 schema sidecar — accumulated as steps arrive so the
            // recording remembers which annotations the workbench saw
            // active for each (service, method) at capture time. Replay
            // mounts widgets off the sidecar instead of the replayer's
            // local annotation store, so a recording made by one user
            // renders identically for another whose annotations differ.
            schemaSnapshot: { annotations: [] },
            steps: []
        };
        recordingsList.push(rec);
        recordingActiveId = rec.id;
        persistRecordings();
        addConsoleEntry({ type: 'response', method: rec.name, status: 'Recording started' });
        render();
    }

    // Resume capturing into an existing recording — new requests
    // get appended to its steps list instead of starting a fresh
    // entry. Idempotent: re-targeting the currently-active recording
    // is a no-op; pointing at a finalised one re-arms the capture
    // hook. Used by the Recordings sidebar's "Continue" affordance.
    function continueRecording(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;
        recordingActiveId = rec.id;
        if (typeof recordingManagerSelectedId !== 'undefined') {
            recordingManagerSelectedId = rec.id;
        }
        persistRecordings();
        addConsoleEntry({ type: 'response', method: rec.name, status: 'Recording resumed' });
        render();
    }

    // Rename a saved recording — used by both the sidebar inline
    // rename affordance and the context menu's "Rename…" entry.
    // Silently ignores blank names so the caller can do
    // `if (!renameRecording(id, newName)) toast(...)` without
    // checking the prompt result twice.
    function renameRecording(id, name) {
        var trimmed = String(name || '').trim();
        if (!trimmed) return false;
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return false;
        rec.name = trimmed;
        persistRecordings();
        render();
        return true;
    }

    // Project a recording's captured steps onto Collection-item
    // shape — each unary step becomes one item, channel / streaming
    // steps are skipped (collections only know unary today). Returns
    // the items so callers can route them either into an existing
    // collection or a freshly-created one.
    function recordingToCollectionItems(rec) {
        if (!rec || !Array.isArray(rec.steps)) return [];
        var out = [];
        rec.steps.forEach(function (s) {
            if (s.methodType && s.methodType !== 'Unary') return;
            out.push({
                service: s.service,
                method: s.method,
                methodType: s.methodType || 'Unary',
                protocol: s.protocol,
                body: s.body || (Array.isArray(s.messages) ? s.messages[0] : '{}'),
                messages: Array.isArray(s.messages) ? s.messages.slice() : undefined,
                metadata: s.metadata || null,
                serverUrl: s.serverUrl || null
            });
        });
        return out;
    }

    function stopRecording() {
        var rec = recordingsList.find(function (r) { return r.id === recordingActiveId; });
        var name = rec ? rec.name : 'Recording';
        var stepCount = rec ? rec.steps.length : 0;
        recordingActiveId = null;
        // If the recording captured zero steps, drop it instead of
        // saving an empty entry the operator would just have to
        // clean up later. Matches the 'reuse latest empty' behaviour
        // in startRecording — empty slots are noise, not state.
        if (rec && stepCount === 0) {
            var idx = recordingsList.indexOf(rec);
            if (idx >= 0) recordingsList.splice(idx, 1);
            if (recordingManagerSelectedId === rec.id) recordingManagerSelectedId = null;
        }
        persistRecordings();
        if (stepCount === 0) {
            addConsoleEntry({ type: 'response', method: name, status: 'Recording dropped (0 steps)' });
        } else {
            addConsoleEntry({ type: 'response', method: name, status: 'Recording stopped (' + stepCount + ' step' + (stepCount !== 1 ? 's' : '') + ')' });
        }
        render();
    }

    /**
     * Append a captured step to the currently active recording. Called from
     * the addHistory call sites in invokeUnary / invokeStreaming /
     * channelConnect — same payload, just routed into the recording too.
     * Silent no-op when there is no active recording.
     */
    function captureRecordingStep(entry) {
        if (!isRecording()) return;
        var rec = recordingsList.find(function (r) { return r.id === recordingActiveId; });
        if (!rec) return;
        var step = Object.assign({
            id: nextStepId(),
            capturedAt: Date.now()
        }, entry);
        rec.steps.push(step);
        // Phase-5 schema-snapshot accumulation — fold the cached effective
        // annotations for (entry.service, entry.method) into the recording's
        // sidecar. The cache is populated by extensions.js whenever the
        // workbench fetches /api/semantics/effective for a method; if the
        // method has never been opened (no widgets mounted) the cache may
        // be empty and the snapshot just skips that pair. De-dupes by the
        // four-dimensional addressing tuple so re-recording the same
        // method twice doesn't double-count.
        try {
            mergeRecordingSchemaSnapshot(rec, entry.service, entry.method);
        } catch (_) { /* snapshot is a side-channel — recording stays valid */ }
        // #144 Phase 1.7 — append the single step via POST instead of
        // PUT-ing the whole list. localStorage still gets the updated
        // recordingsList for instant-render + offline fallback, but
        // the disk hit goes through the chunked-store append path —
        // O(1) write per step instead of O(n) rewrite of the entire
        // document. The debounced PUT still triggers as a safety net
        // for the case where the per-step POST fails (offline,
        // server hiccup); it then re-syncs the whole list.
        if (_recordingsModeAllowsLocalStorage()) {
            try { localStorage.setItem(wsKey(RECORDINGS_KEY), JSON.stringify(recordingsList)); markSaved('recordings'); }
            catch (e) { markSaveFailed('recordings', e); }
        }
        appendStepToDisk(rec, step);
        // Don't re-render here — addHistory's caller already calls render()
        // after the response has been processed.
    }

    function mergeRecordingSchemaSnapshot(rec, service, method) {
        if (!rec || !service || !method) return;
        if (!rec.schemaSnapshot) rec.schemaSnapshot = { annotations: [] };
        if (!Array.isArray(rec.schemaSnapshot.annotations)) rec.schemaSnapshot.annotations = [];

        // Walk the JS-side effective-cache populated by extensions.js. The
        // cache is keyed by (service, method) and shaped exactly like the
        // /api/semantics/effective response. When the workbench hasn't
        // touched this method yet the cache is empty; we leave the
        // sidecar untouched rather than firing a synchronous fetch from
        // the recorder.
        var cached = (window.__bowireExtFramework
            && typeof window.__bowireExtFramework.effectiveCacheFor === 'function')
            ? window.__bowireExtFramework.effectiveCacheFor(service, method)
            : null;
        if (!cached || !Array.isArray(cached)) return;

        // Index existing entries so the merge stays O(annotations) instead
        // of O(annotations²) on long recordings.
        var seen = {};
        for (var i = 0; i < rec.schemaSnapshot.annotations.length; i++) {
            var a = rec.schemaSnapshot.annotations[i];
            seen[a.service + '/' + a.method + '/' + (a.messageType || '*') + '/' + a.jsonPath] = true;
        }
        for (var j = 0; j < cached.length; j++) {
            var n = cached[j];
            if (!n || !n.jsonPath) continue;
            var k = service + '/' + method + '/' + (n.messageType || '*') + '/' + n.jsonPath;
            if (seen[k]) continue;
            rec.schemaSnapshot.annotations.push({
                service: service,
                method: method,
                messageType: n.messageType || '*',
                jsonPath: n.jsonPath,
                semantic: n.semantic
            });
            seen[k] = true;
        }
    }

    function deleteRecording(id) {
        var idx = recordingsList.findIndex(function (r) { return r.id === id; });
        if (idx < 0) return;
        var removed = recordingsList[idx];
        recordingsList.splice(idx, 1);
        if (recordingActiveId === id) recordingActiveId = null;
        if (recordingManagerSelectedId === id) recordingManagerSelectedId = null;
        persistRecordings();
        // Soft-delete: route the entry through recordingsTrash so the
        // 'Recently deleted' section at the bottom of the Recordings
        // sidebar can restore it. Mirrors the inline-row delete path
        // in render-sidebar.js — central deleteRecording() was
        // hard-deleting and bypassing the affordance.
        if (typeof recordingsTrash !== 'undefined' && removed) {
            recordingsTrash.unshift({
                entry: removed,
                deletedAt: Date.now(),
                originalIdx: idx
            });
            if (typeof persistRecordingsTrash === 'function') persistRecordingsTrash();
        }
        render();
    }

    function renameRecording(id, name) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;
        rec.name = name;
        persistRecordings();
    }

    function deleteRecordingStep(recordingId, stepId) {
        var rec = recordingsList.find(function (r) { return r.id === recordingId; });
        if (!rec) return;
        var idx = rec.steps.findIndex(function (s) { return s.id === stepId; });
        if (idx < 0) return;
        rec.steps.splice(idx, 1);
        persistRecordings();
        render();
    }

    // ---- Replay ----
    // Walk every captured step in order, re-invoke it through /api/invoke,
    // and accumulate pass/fail. Variable substitution uses the CURRENT env,
    // not the one that was active when the recording was made — recordings
    // are deliberately portable across environments. Streaming methods are
    // not replayed (they need a different endpoint and the user usually
    // wants the final response anyway), they're skipped with a status.

    // Plugin-check state. Non-null while the "missing plugins" modal
    // is open, holds: { recordingId, missing: [{id,packageId}],
    // installing: bool, installedAll: bool, error: string | null,
    // perPackage: { packageId → 'pending'|'installing'|'done'|'failed' } }.
    // Mirrors recordingReplayState's shape so re-renders are uniform.
    var pluginCheckState = null;

    async function fetchInstalledProtocols() {
        try {
            var r = await fetch(config.prefix + '/api/plugins/protocols');
            if (!r.ok) return null;
            return await r.json();
        } catch (e) {
            return null;
        }
    }

    function distinctRecordingProtocols(rec) {
        var seen = {};
        var out = [];
        for (var i = 0; i < rec.steps.length; i++) {
            var p = (rec.steps[i].protocol || '').trim().toLowerCase();
            if (!p || seen[p]) continue;
            seen[p] = true;
            out.push(p);
        }
        return out;
    }

    async function checkMissingPlugins(rec) {
        var info = await fetchInstalledProtocols();
        if (!info) return { missing: [], catalog: {} }; // assume ok on fetch failure
        var loaded = {};
        for (var i = 0; i < (info.loaded || []).length; i++) loaded[info.loaded[i].toLowerCase()] = true;
        var catalog = info.catalog || {};
        var protocols = distinctRecordingProtocols(rec);
        var missing = [];
        for (var j = 0; j < protocols.length; j++) {
            var id = protocols[j];
            if (loaded[id]) continue;
            missing.push({ id: id, packageId: catalog[id] || null });
        }
        return { missing: missing, catalog: catalog };
    }

    async function installMissingPlugin(packageId) {
        var resp = await fetch(config.prefix + '/api/plugins/install', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ packageId: packageId })
        });
        if (!resp.ok) {
            var msg = 'HTTP ' + resp.status;
            try { var body = await resp.json(); if (body) msg = problemTitle(body, msg); } catch {}
            throw new Error(msg);
        }
        return await resp.json();
    }

    async function installAllMissing() {
        if (!pluginCheckState || pluginCheckState.installing) return;
        pluginCheckState.installing = true;
        pluginCheckState.error = null;
        render();
        var anyFailed = false;
        for (var i = 0; i < pluginCheckState.missing.length; i++) {
            var entry = pluginCheckState.missing[i];
            if (!entry.packageId) {
                pluginCheckState.perPackage[entry.id] = 'failed';
                anyFailed = true;
                continue;
            }
            pluginCheckState.perPackage[entry.id] = 'installing';
            render();
            try {
                await installMissingPlugin(entry.packageId);
                pluginCheckState.perPackage[entry.id] = 'done';
            } catch (e) {
                pluginCheckState.perPackage[entry.id] = 'failed';
                anyFailed = true;
            }
            render();
        }
        pluginCheckState.installing = false;
        pluginCheckState.installedAll = !anyFailed;
        if (anyFailed) {
            pluginCheckState.error = 'Some installs failed. Check the workbench logs.';
        }
        render();
    }

    function dismissPluginCheck() {
        pluginCheckState = null;
        render();
    }

    async function replayRecording(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec || rec.steps.length === 0) return;

        // Pre-flight: scan the recording for protocols whose plugins
        // aren't loaded in this Bowire host. If any are missing, pop
        // the "install plugins?" modal and bail; the modal's install
        // button retriggers the replay flow once the install lands +
        // the host is restarted.
        var check = await checkMissingPlugins(rec);
        if (check.missing.length > 0) {
            pluginCheckState = {
                recordingId: id,
                missing: check.missing,
                installing: false,
                installedAll: false,
                error: null,
                perPackage: {}
            };
            render();
            return;
        }

        recordingReplayState = {
            recordingId: id,
            stepIndex: -1,
            status: 'running',
            results: []
        };
        render();

        for (var i = 0; i < rec.steps.length; i++) {
            recordingReplayState.stepIndex = i;
            render();

            var step = rec.steps[i];
            try {
                var result = await replaySingleStep(step);
                recordingReplayState.results.push({
                    stepId: step.id,
                    pass: result.pass,
                    status: result.status,
                    durationMs: result.durationMs,
                    response: result.response,
                    error: result.error
                });
            } catch (e) {
                recordingReplayState.results.push({
                    stepId: step.id,
                    pass: false,
                    status: 'NetworkError',
                    error: e.message
                });
            }
            render();
        }

        recordingReplayState.status = 'done';
        render();
    }

    function replaySingleStep(step) {
        // We only know how to replay unary calls server-side via /api/invoke.
        // Streaming and channel methods need their own dispatch and aren't
        // worth the wire complexity for a Postman-parity feature.
        if (step.methodType && step.methodType !== 'Unary') {
            return Promise.resolve({
                pass: false,
                status: 'Skipped (' + step.methodType + ')',
                durationMs: 0,
                error: 'Only Unary methods are replayed; streaming methods are skipped.'
            });
        }

        var messages = (step.messages && step.messages.length > 0)
            ? step.messages
            : [step.body || '{}'];

        // Apply current env-var substitution to each message + every metadata
        // value, so a recording made against ${baseUrl}/Dev replays cleanly
        // against ${baseUrl}/Prod after switching env.
        var substitutedMessages = messages.map(function (m) { return substituteVars(m); });
        var substitutedMeta = step.metadata
            ? Object.fromEntries(Object.entries(step.metadata).map(function (kv) {
                return [kv[0], substituteVars(String(kv[1]))];
            }))
            : null;

        // Server URL: prefer the step's recorded origin URL when present, else
        // fall back to the currently active server URL. This lets a single
        // recording span multiple URLs (e.g. orchestration scenario across
        // two services) and still replay correctly.
        var serverUrl = step.serverUrl || (serverUrls.length > 0 ? serverUrls[0] : '');

        var body = {
            service: step.service,
            method: step.method,
            messages: substitutedMessages,
            metadata: substitutedMeta,
            protocol: step.protocol || null
        };

        var url = config.prefix + '/api/invoke'
            + (serverUrl ? '?serverUrl=' + encodeURIComponent(serverUrl) : '');

        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        }).then(function (resp) {
            return resp.json().then(function (json) {
                var ok = resp.ok && !json.title;
                return {
                    pass: ok,
                    status: json.status || (ok ? 'OK' : 'Error'),
                    durationMs: json.duration_ms || 0,
                    response: json.response,
                    error: json.title || null
                };
            });
        });
    }

    // ---- Export: Bowire JSON ----
    function exportRecordingAsJson(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;
        var blob = new Blob([JSON.stringify(rec, null, 2)], { type: 'application/json' });
        downloadBlob(blob, sanitizeFilename(rec.name) + '.bwr');
    }

    // ---- Export: HAR 1.2 ----
    // HTTP Archive format — recognised by Chrome DevTools, Firefox DevTools,
    // Postman, Insomnia, Charles Proxy, Fiddler, etc. Each recorded step
    // becomes one HAR entry with synthetic request + response. We tag the
    // protocol name on the request comment so the gRPC/MCP/etc. context isn't
    // lost in translation, even though HAR itself only knows about HTTP.
    function exportRecordingAsHar(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;

        var entries = rec.steps.map(function (step) {
            var url = (step.serverUrl || (serverUrls[0] || 'http://recording.local'))
                + '/' + (step.service || '') + '/' + (step.method || '');
            var headers = step.metadata
                ? Object.entries(step.metadata).map(function (kv) {
                    return { name: kv[0], value: String(kv[1]) };
                })
                : [];
            var body = step.body || (step.messages && step.messages[0]) || '{}';
            var responseText = typeof step.response === 'string'
                ? step.response
                : JSON.stringify(step.response || {}, null, 2);
            var statusName = String(step.status || 'OK');
            var statusCode = parseInt(statusName, 10);
            if (isNaN(statusCode)) statusCode = (statusName === 'OK') ? 200 : 500;

            return {
                startedDateTime: new Date(step.capturedAt || Date.now()).toISOString(),
                time: step.durationMs || 0,
                request: {
                    method: 'POST',
                    url: url,
                    httpVersion: 'HTTP/1.1',
                    cookies: [],
                    headers: headers.concat([{ name: 'Content-Type', value: 'application/json' }]),
                    queryString: [],
                    headersSize: -1,
                    bodySize: body.length,
                    postData: {
                        mimeType: 'application/json',
                        text: body
                    },
                    comment: 'Bowire recorded step (protocol=' + (step.protocol || 'unknown') + ')'
                },
                response: {
                    status: statusCode,
                    statusText: statusName,
                    httpVersion: 'HTTP/1.1',
                    cookies: [],
                    headers: [{ name: 'Content-Type', value: 'application/json' }],
                    content: {
                        size: responseText.length,
                        mimeType: 'application/json',
                        text: responseText
                    },
                    redirectURL: '',
                    headersSize: -1,
                    bodySize: responseText.length
                },
                cache: {},
                timings: {
                    send: 0,
                    wait: step.durationMs || 0,
                    receive: 0
                }
            };
        });

        var har = {
            log: {
                version: '1.2',
                creator: { name: 'Bowire', version: '0.9.0' },
                pages: [],
                entries: entries,
                comment: 'Recording: ' + rec.name + ' (' + rec.steps.length + ' step' + (rec.steps.length !== 1 ? 's' : '') + ')'
            }
        };

        var blob = new Blob([JSON.stringify(har, null, 2)], { type: 'application/json' });
        downloadBlob(blob, sanitizeFilename(rec.name) + '.har');
    }

    // ---- Export: CI HTML Report ----
    // Self-contained HTML file with embedded styles. Designed to be
    // dropped straight into a CI artifact bucket — open in any browser
    // to inspect the recording: pass/fail summary at the top, per-step
    // detail with collapsed request/response below. If a replay has
    // been run for this recording the report uses those results;
    // otherwise it falls back to the captured statuses from the
    // original capture pass.
    function exportRecordingAsHtml(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;

        // Replay results win over captured results when present, because
        // they reflect the freshest pass against the current environment.
        var hasReplay = recordingReplayState
            && recordingReplayState.recordingId === id
            && recordingReplayState.results
            && recordingReplayState.results.length > 0;

        function statusForStep(step) {
            if (hasReplay) {
                var r = recordingReplayState.results.find(function (x) { return x.stepId === step.id; });
                if (r) {
                    return {
                        pass: !!r.pass,
                        statusText: r.status || (r.pass ? 'PASS' : 'FAIL'),
                        durationMs: r.durationMs || 0,
                        response: r.response,
                        error: r.error || null
                    };
                }
            }
            // No replay — derive pass from captured status name. Same
            // mapping isHistoryEntryOk uses elsewhere so the report
            // agrees with the in-app history view.
            var s = String(step.status || '');
            var pass = s === 'OK' || s === 'Connected' || s === 'Completed';
            var num = parseInt(s, 10);
            if (!isNaN(num)) pass = num < 400;
            return {
                pass: pass,
                statusText: s || 'unknown',
                durationMs: step.durationMs || 0,
                response: step.response,
                error: null
            };
        }

        var rows = rec.steps.map(function (step, i) { return { step: step, idx: i, st: statusForStep(step) }; });
        var passCount = rows.filter(function (r) { return r.st.pass; }).length;
        var failCount = rows.length - passCount;
        var totalDuration = rows.reduce(function (acc, r) { return acc + (r.st.durationMs || 0); }, 0);

        function esc(s) {
            return String(s == null ? '' : s)
                .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
        }
        function fmtJson(v) {
            if (v == null) return '';
            if (typeof v === 'string') {
                try { return JSON.stringify(JSON.parse(v), null, 2); }
                catch { return v; }
            }
            try { return JSON.stringify(v, null, 2); }
            catch { return String(v); }
        }

        var generated = new Date();
        var rowsHtml = rows.map(function (r) {
            var bodyJson = fmtJson(r.step.body || (r.step.messages && r.step.messages[0]) || '');
            var respJson = fmtJson(r.st.response);
            var metaHtml = '';
            if (r.step.metadata && Object.keys(r.step.metadata).length > 0) {
                metaHtml = '<div class="meta"><strong>Headers</strong><pre>'
                    + esc(JSON.stringify(r.step.metadata, null, 2)) + '</pre></div>';
            }
            return ''
                + '<details class="step ' + (r.st.pass ? 'pass' : 'fail') + '">'
                + '<summary>'
                +   '<span class="idx">' + (r.idx + 1) + '</span>'
                +   '<span class="badge ' + (r.st.pass ? 'b-pass' : 'b-fail') + '">'
                +     (r.st.pass ? 'PASS' : 'FAIL') + '</span>'
                +   '<span class="proto">' + esc(r.step.protocol || 'grpc') + '</span>'
                +   '<span class="method">' + esc(r.step.service || '') + ' / ' + esc(r.step.method || '') + '</span>'
                +   '<span class="status">' + esc(r.st.statusText) + '</span>'
                +   '<span class="dur">' + (r.st.durationMs || 0) + ' ms</span>'
                + '</summary>'
                + '<div class="body">'
                +   (r.st.error ? '<div class="err"><strong>Error</strong><pre>' + esc(r.st.error) + '</pre></div>' : '')
                +   metaHtml
                +   '<div class="req"><strong>Request</strong><pre>' + esc(bodyJson) + '</pre></div>'
                +   '<div class="resp"><strong>Response</strong><pre>' + esc(respJson) + '</pre></div>'
                + '</div>'
                + '</details>';
        }).join('\n');

        var html = ''
            + '<!DOCTYPE html>\n<html lang="en"><head>'
            + '<meta charset="utf-8">'
            + '<title>' + esc(rec.name) + ' \u2014 Bowire Recording Report</title>'
            + '<style>'
            + 'body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif; '
            +   'background: #0e0e16; color: #e6e6f0; margin: 0; padding: 24px; line-height: 1.5; }'
            + 'h1 { font-size: 22px; margin: 0 0 4px; }'
            + 'h1 .name { color: #a78bfa; }'
            + '.subtitle { color: #8888a0; font-size: 13px; margin-bottom: 24px; }'
            + '.summary { display: flex; gap: 12px; margin-bottom: 24px; flex-wrap: wrap; }'
            + '.card { background: #1a1a25; border: 1px solid #2a2a3a; border-radius: 6px; padding: 12px 16px; min-width: 110px; }'
            + '.card .label { font-size: 11px; text-transform: uppercase; color: #8888a0; letter-spacing: 0.06em; }'
            + '.card .value { font-size: 22px; font-weight: 600; margin-top: 4px; }'
            + '.card.ok .value { color: #34d399; }'
            + '.card.bad .value { color: #f87171; }'
            + '.card.neutral .value { color: #fb923c; }'
            + '.step { background: #1a1a25; border: 1px solid #2a2a3a; border-left-width: 3px; '
            +   'border-radius: 4px; margin-bottom: 8px; }'
            + '.step.pass { border-left-color: #34d399; }'
            + '.step.fail { border-left-color: #f87171; }'
            + '.step summary { padding: 10px 14px; cursor: pointer; display: flex; align-items: center; gap: 12px; '
            +   'font-size: 13px; user-select: none; }'
            + '.step summary::-webkit-details-marker { display: none; }'
            + '.step .idx { color: #8888a0; min-width: 28px; }'
            + '.step .badge { padding: 2px 8px; border-radius: 3px; font-size: 10px; font-weight: 700; '
            +   'letter-spacing: 0.06em; }'
            + '.step .b-pass { background: #064e3b; color: #34d399; }'
            + '.step .b-fail { background: #7f1d1d; color: #f87171; }'
            + '.step .proto { background: #312e81; color: #c7d2fe; padding: 2px 8px; border-radius: 3px; '
            +   'font-size: 10px; text-transform: uppercase; }'
            + '.step .method { flex: 1; font-family: ui-monospace, "SF Mono", Consolas, monospace; }'
            + '.step .status { color: #c7c7d8; font-family: ui-monospace, monospace; font-size: 11px; }'
            + '.step .dur { color: #8888a0; font-family: ui-monospace, monospace; font-size: 11px; }'
            + '.step .body { padding: 0 14px 14px; border-top: 1px solid #2a2a3a; }'
            + '.step .body pre { background: #0e0e16; border: 1px solid #2a2a3a; border-radius: 4px; '
            +   'padding: 10px; overflow-x: auto; font-size: 12px; line-height: 1.45; }'
            + '.step .body strong { display: block; font-size: 11px; color: #8888a0; margin: 12px 0 4px; '
            +   'text-transform: uppercase; letter-spacing: 0.06em; }'
            + '.step .err pre { color: #f87171; border-color: #7f1d1d; }'
            + 'footer { color: #6a6a82; font-size: 11px; margin-top: 32px; text-align: center; }'
            + '</style></head><body>'
            + '<h1>Recording report \u2014 <span class="name">' + esc(rec.name) + '</span></h1>'
            + '<div class="subtitle">Generated ' + esc(generated.toISOString())
            +   ' \u00b7 ' + (hasReplay ? 'Replay results' : 'Captured statuses')
            +   ' \u00b7 ' + rec.steps.length + ' step' + (rec.steps.length !== 1 ? 's' : '') + '</div>'
            + '<div class="summary">'
            +   '<div class="card ' + (failCount === 0 ? 'ok' : 'bad') + '">'
            +     '<div class="label">Result</div>'
            +     '<div class="value">' + (failCount === 0 ? 'PASS' : 'FAIL') + '</div>'
            +   '</div>'
            +   '<div class="card ok"><div class="label">Passed</div><div class="value">' + passCount + '</div></div>'
            +   '<div class="card ' + (failCount === 0 ? 'neutral' : 'bad') + '">'
            +     '<div class="label">Failed</div><div class="value">' + failCount + '</div></div>'
            +   '<div class="card neutral"><div class="label">Total time</div><div class="value">' + totalDuration + ' ms</div></div>'
            + '</div>'
            + rowsHtml
            + '<footer>Generated by Bowire \u2014 https://github.com/Kuestenlogik/Bowire</footer>'
            + '</body></html>';

        var blob = new Blob([html], { type: 'text/html' });
        downloadBlob(blob, sanitizeFilename(rec.name) + '.report.html');
    }

    // ---- Convert recording → tests ----
    // For each step, write two assertions onto the (service, method) pair:
    //   1) status equals the captured status name
    //   2) response equals the captured response (deep eq)
    // Existing assertions on those methods are LEFT IN PLACE — we append, so
    // a user who's already written manual tests doesn't lose them. Returns
    // the number of new assertions added so the caller can show a toast.
    function convertRecordingToTests(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return 0;

        var added = 0;
        for (var i = 0; i < rec.steps.length; i++) {
            var step = rec.steps[i];
            var existing = getTestsFor(step.service, step.method);

            existing.push({
                id: nextTestId(),
                path: 'status',
                op: 'eq',
                expected: String(step.status || 'OK')
            });
            added++;

            if (step.response !== undefined && step.response !== null) {
                existing.push({
                    id: nextTestId(),
                    path: 'response',
                    op: 'eq',
                    expected: typeof step.response === 'string'
                        ? step.response
                        : JSON.stringify(step.response)
                });
                added++;
            }

            setTestsFor(step.service, step.method, existing);
        }
        return added;
    }

    // ---- Small helpers ----
    function sanitizeFilename(name) {
        return String(name || 'recording').replace(/[^a-zA-Z0-9._-]/g, '_').slice(0, 80);
    }

    function downloadBlob(blob, filename) {
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        a.click();
        URL.revokeObjectURL(a.href);
    }

    // ---- #94 — Use Recording as Mock ----
    // POST /api/mocks with the recording id. Backend looks the recording
    // up via IRecordingJsonProvider, boots a MockServer on a free local
    // port, returns { mockId, url, port }. Toast surfaces the URL + a
    // copy button so the user can paste it into another tool with one
    // click. (Consolidated with the legacy /api/mock/from-recording
    // surface under #223.)
    function useRecordingAsMock(rec) {
        if (!rec || !rec.id) return;
        fetch(config.prefix + '/api/mocks', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ recordingId: rec.id, label: rec.name }),
        }).then(function (resp) {
            return resp.json().then(function (body) {
                return { ok: resp.ok, status: resp.status, body: body };
            });
        }).then(function (resp) {
            if (!resp.ok) {
                toast('Use as mock failed: ' + problemTitle(resp.body, 'HTTP ' + resp.status), 'error');
                return;
            }
            var url = resp.body && resp.body.url ? resp.body.url : null;
            if (!url) {
                toast('Mock started but no URL returned', 'error');
                return;
            }
            if (navigator.clipboard) {
                navigator.clipboard.writeText(url).then(function () {
                    toast('Mock URL copied: ' + url, 'success');
                }).catch(function () {
                    toast('Mock running at ' + url + ' (copy failed)', 'success');
                });
            } else {
                toast('Mock running at ' + url, 'success');
            }
            addConsoleEntry({
                type: 'response',
                method: rec.name,
                status: 'Mock running on port ' + resp.body.port,
                body: url,
            });
        }).catch(function (err) {
            toast('Use as mock failed: ' + (err && err.message ? err.message : err), 'error');
        });
    }

    // renderRecordingManager modal retired in #133 Phase 3.
    // The rail mode owns the same workflow; entry paths jump
    // directly to railMode = 'recordings'.

    function renderPluginCheckModal() {
        if (!pluginCheckState) return;

        var allDone = pluginCheckState.installedAll;
        var modal = el('div', { className: 'bowire-env-modal bowire-plugin-check-modal' });

        // Header
        modal.appendChild(el('div', { className: 'bowire-env-modal-header' },
            el('h2', { className: 'bowire-env-modal-title',
                textContent: allDone ? 'Plugins installed' : 'Plugins required for replay' })));

        var body = el('div', { className: 'bowire-env-modal-body bowire-plugin-check-body' });

        if (allDone) {
            // Post-install success: tell the user the next step.
            body.appendChild(el('p', { className: 'bowire-plugin-check-intro',
                textContent: 'The plugins were installed into ~/.bowire/plugins/. Restart Bowire so the new protocols are picked up by the registry.' }));
        } else {
            body.appendChild(el('p', { className: 'bowire-plugin-check-intro',
                textContent: 'This recording references protocols that aren’t loaded in your Bowire host. Install the matching plugins to replay it.' }));
        }

        var listEl = el('ul', { className: 'bowire-plugin-check-list' });
        pluginCheckState.missing.forEach(function (entry) {
            var status = pluginCheckState.perPackage[entry.id] || (entry.packageId ? 'pending' : 'unknown');
            var statusBadge;
            if (status === 'installing') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status installing', textContent: 'installing…' });
            } else if (status === 'done') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status done', textContent: '✓ installed' });
            } else if (status === 'failed') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status failed', textContent: '✗ failed' });
            } else if (status === 'unknown') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status unknown',
                    textContent: 'third-party — install manually' });
            } else {
                statusBadge = el('span', { className: 'bowire-plugin-check-status pending', textContent: 'pending' });
            }

            var row = el('li', { className: 'bowire-plugin-check-row' },
                el('div', { className: 'bowire-plugin-check-row-text' },
                    el('strong', { textContent: entry.id }),
                    el('span', { className: 'bowire-plugin-check-pkg',
                        textContent: entry.packageId || '(no canonical package — bring your own NuGet)' })),
                statusBadge
            );
            listEl.appendChild(row);
        });
        body.appendChild(listEl);

        if (pluginCheckState.error) {
            body.appendChild(el('p', { className: 'bowire-plugin-check-error', textContent: pluginCheckState.error }));
        }

        // Footer buttons
        var footer = el('div', { className: 'bowire-plugin-check-footer' });
        if (allDone) {
            footer.appendChild(el('button', {
                className: 'bowire-env-modal-btn bowire-env-modal-btn-primary',
                textContent: 'Close',
                onClick: dismissPluginCheck
            }));
        } else {
            var installable = pluginCheckState.missing.some(function (m) { return m.packageId; });
            var installBtn = el('button', {
                className: 'bowire-env-modal-btn bowire-env-modal-btn-primary',
                textContent: pluginCheckState.installing ? 'Installing…' : 'Install plugins',
                disabled: !installable || pluginCheckState.installing,
                onClick: installAllMissing
            });
            footer.appendChild(installBtn);
            footer.appendChild(el('button', {
                className: 'bowire-env-modal-btn',
                textContent: 'Cancel',
                disabled: pluginCheckState.installing,
                onClick: dismissPluginCheck
            }));
        }
        modal.appendChild(body);
        modal.appendChild(footer);

        var overlay = el('div', {
            className: 'bowire-env-modal-overlay bowire-plugin-check-overlay',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': 'Plugin check',
            onClick: function (e) {
                if (e.target === overlay && !pluginCheckState.installing) dismissPluginCheck();
            }
        }, modal);

        document.body.appendChild(overlay);
    }

    function renderRecordingDetail(rec) {
        var pane = el('div', { className: 'bowire-recording-detail' });

        // Header — adopts the shared .bowire-pane-header pattern
        // (icon + heading on the left, tool cluster on the right).
        // Rename uses the same bowirePrompt as workspace + collection
        // rename, not a bare always-on text input. Delete is a
        // header-tool here (state-mutating + rare) instead of yet
        // another button in the action toolbar.
        var header = el('div', { className: 'bowire-pane-header bowire-recording-detail-header' });
        header.appendChild(el('div', { className: 'bowire-pane-header-main' },
            el('span', {
                className: 'bowire-recording-detail-icon',
                innerHTML: svgIcon('recording')
            }),
            el('h2', { className: 'bowire-pane-title', textContent: rec.name || 'Recording' })
        ));
        header.appendChild(el('div', { className: 'bowire-pane-actions' },
            el('button', {
                type: 'button',
                className: 'bowire-pane-header-tool',
                title: 'Rename recording',
                'aria-label': 'Rename recording',
                innerHTML: svgIcon('pencil'),
                onClick: function () {
                    var current = rec.name || '';
                    bowirePrompt('Rename recording', {
                        title: 'Rename',
                        defaultValue: current,
                        confirmText: 'Rename',
                        validator: function (val) {
                            var trimmed = String(val || '').trim();
                            if (!trimmed) return 'Name required';
                            return null;
                        }
                    }).then(function (next) {
                        if (!next) return;
                        var trimmed = String(next).trim();
                        if (!trimmed || trimmed === current) return;
                        renameRecording(rec.id, trimmed);
                        render();
                    });
                }
            }),
            el('button', {
                type: 'button',
                className: 'bowire-pane-header-tool bowire-pane-header-tool-danger',
                title: 'Delete recording',
                'aria-label': 'Delete recording',
                innerHTML: svgIcon('trash'),
                onClick: function () {
                    bowireConfirm('Delete recording "' + rec.name + '"?', function () {
                        deleteRecording(rec.id);
                        toast('Recording deleted', 'success');
                    }, { title: 'Delete Recording', danger: true, confirmText: 'Delete' });
                }
            })
        ));
        pane.appendChild(header);

        // Action toolbar — three groups separated by thin dividers:
        //   Run     : Replay, Parallel sessions
        //   Build   : Use as mock, Run as mock, Benchmark, Convert→Tests, Convert→Flow
        //   Export  : HAR, HTML report, JSON
        // Delete moved into the header tool cluster above (danger
        // actions live next to rename, not in the run-actions strip).
        var toolbar = el('div', { className: 'bowire-recording-toolbar' });
        var runGroup = el('div', { className: 'bowire-recording-toolbar-group' });
        var buildGroup = el('div', { className: 'bowire-recording-toolbar-group' });
        var exportGroup = el('div', { className: 'bowire-recording-toolbar-group' });
        var isReplaying = recordingReplayState && recordingReplayState.recordingId === rec.id && recordingReplayState.status === 'running';

        // Toolbar gates on "does this recording carry any steps". Three
        // shapes are accepted (matches the sidebar list's stepN logic):
        //   1) Hydrated steps[] with entries — the inline shape returned
        //      by the default GET /api/recordings.
        //   2) stepsManifest[] entries — the manifest-only shape returned
        //      under #144 Phase 1.8's lazy-load mode.
        //   3) Numeric stepCount — emitted by the chunked store so the
        //      sidebar can render a "3 steps" badge before hydration
        //      runs. We treat it the same as the other two — the
        //      backend IRecordingJsonProvider re-assembles the full
        //      body on the actual "use as mock" call.
        var _hasSteps = (Array.isArray(rec.steps) && rec.steps.length > 0)
            || (Array.isArray(rec.stepsManifest) && rec.stepsManifest.length > 0)
            || (typeof rec.stepCount === 'number' && rec.stepCount > 0);

        // ---- Run group ----
        runGroup.appendChild(el('button', {
            className: 'bowire-recording-action-btn bowire-recording-action-primary',
            disabled: isReplaying || !_hasSteps,
            title: 'Replay every step in order with the current environment variables',
            onClick: function () { replayRecording(rec.id); }
        },
            el('span', { innerHTML: svgIcon('replay') }),
            el('span', { textContent: isReplaying ? 'Replaying…' : 'Replay' })
        ));
        // #132 minimal — fire N parallel sessions of this recording.
        runGroup.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: !_hasSteps,
            title: 'Replay this recording in N parallel sessions at once',
            onClick: function () { _promptAndRunParallel('recording', rec.id, rec.name); }
        },
            el('span', { innerHTML: svgIcon('repeat') }),
            el('span', { textContent: 'Parallel sessions' })
        ));
        toolbar.appendChild(runGroup);

        // ---- Build group ----
        // #94 — one-click "Use as mock". Boots a local MockServer
        // against this recording and copies the resulting URL to the
        // clipboard.
        buildGroup.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: !_hasSteps,
            title: 'Spin up a local mock server that returns this recording verbatim. URL is copied to your clipboard.',
            onClick: function () { useRecordingAsMock(rec); }
        },
            el('span', { innerHTML: svgIcon('server') }),
            el('span', { textContent: 'Use as mock' })
        ));
        // #56 Phase 2 — start a mock from this recording. POST hits
        // /api/mocks; on success the workbench switches to the Mocks
        // overlay so the user sees the running mock immediately.
        buildGroup.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: rec.steps.length === 0 || !window.__bowireMocks,
            title: 'Boot a mock server from this recording on a fresh port. View it in the Mocks panel.',
            onClick: function () {
                if (!window.__bowireMocks) return;
                window.__bowireMocks.startFromRecording(rec, 0).then(function () {
                    window.__bowireMocks.open();
                }).catch(function () { /* toast already shown */ });
            }
        },
            el('span', { innerHTML: svgIcon('plug') }),
            el('span', { textContent: 'Run as mock' })
        ));
        // #131 Phase 3 — "Benchmark" opens an Add-to-envelope picker.
        if (typeof addTargetToEnvelopePicker === 'function') {
            buildGroup.appendChild(el('button', {
                className: 'bowire-recording-action-btn',
                disabled: !_hasSteps,
                title: 'Add this recording to a benchmark envelope',
                onClick: function (e) {
                    addTargetToEnvelopePicker(e.clientX, e.clientY,
                        { type: 'recording-ref', recordingId: rec.id, stepIndex: null },
                        { name: 'Benchmark: ' + (rec.name || 'recording') }
                    );
                }
            },
                el('span', { innerHTML: svgIcon('lightning') }),
                el('span', { textContent: 'Benchmark' })
            ));
        }
        buildGroup.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: !_hasSteps,
            title: 'Append status + response equality assertions to every (service, method) in this recording',
            onClick: function () {
                var added = convertRecordingToTests(rec.id);
                addConsoleEntry({ type: 'response', method: rec.name, status: 'Added ' + added + ' test assertions' });
                render();
            }
        },
            el('span', { innerHTML: svgIcon('check') }),
            el('span', { textContent: 'Convert to Tests' })
        ));
        buildGroup.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: !_hasSteps,
            title: 'Project this recording into a visual flow (Request nodes per step). Edit + replay from the Flow tab.',
            onClick: function () {
                var flowId = convertRecordingToFlow(rec.id);
                if (!flowId) return;
                railMode = 'flows';
                try { localStorage.setItem('bowire_rail_mode', 'flows'); } catch { /* ignore */ }
                if (typeof setSidebarView === 'function') setSidebarView('flows');
                flowEditorSelectedId = flowId;
                render();
                toast('Flow created from recording', 'success');
            }
        },
            el('span', { innerHTML: svgIcon('flow') }),
            el('span', { textContent: 'Convert to Flow' })
        ));
        toolbar.appendChild(buildGroup);

        // ---- Export group ----
        // Three formats (HAR, HTML report, JSON) folded behind one
        // "Export ▾" split-button-style menu so the toolbar doesn't
        // grow three near-identical buttons. Click the chevron (or
        // the button) → drops a small menu anchored to the button.
        var exportMenuOpen = recordingExportMenuOpenId === rec.id;
        var exportBtnWrapper = el('div', { className: 'bowire-recording-export-wrapper' });
        var exportBtn = el('button', {
            className: 'bowire-recording-action-btn bowire-recording-action-export',
            disabled: !_hasSteps,
            title: 'Export this recording',
            onClick: function (e) {
                e.stopPropagation();
                recordingExportMenuOpenId = exportMenuOpen ? null : rec.id;
                render();
            }
        },
            el('span', { innerHTML: svgIcon('download') }),
            el('span', { textContent: 'Export' }),
            el('span', { className: 'bowire-recording-export-caret', innerHTML: svgIcon('chevron') })
        );
        exportBtnWrapper.appendChild(exportBtn);
        if (exportMenuOpen) {
            var exportMenu = el('div', { className: 'bowire-recording-export-menu', role: 'menu' });
            function _exportItem(label, hint, fn) {
                exportMenu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-recording-export-menu-item',
                    title: hint,
                    onClick: function (e) {
                        e.stopPropagation();
                        recordingExportMenuOpenId = null;
                        fn();
                        render();
                    }
                },
                    el('span', { className: 'bowire-recording-export-menu-item-label', textContent: label }),
                    el('span', { className: 'bowire-recording-export-menu-item-hint', textContent: hint })
                ));
            }
            _exportItem('HAR', 'Chrome DevTools / Postman / Insomnia compatible',
                function () { exportRecordingAsHar(rec.id); });
            _exportItem('HTML report', 'Self-contained CI artifact',
                function () { exportRecordingAsHtml(rec.id); });
            _exportItem('JSON', 'Raw Bowire recording format',
                function () { exportRecordingAsJson(rec.id); });
            exportBtnWrapper.appendChild(exportMenu);
        }
        exportGroup.appendChild(exportBtnWrapper);
        toolbar.appendChild(exportGroup);

        pane.appendChild(toolbar);

        // Step list
        var stepsPane = el('div', { className: 'bowire-recording-steps' });
        if (rec.steps.length === 0) {
            stepsPane.appendChild(el('div', { className: 'bowire-recording-empty',
                textContent: rec.id === recordingActiveId
                    ? 'Recording in progress. Make a few calls and they will appear here.'
                    : 'This recording has no steps yet.' }));
        } else {
            for (var i = 0; i < rec.steps.length; i++) {
                stepsPane.appendChild(renderRecordingStepRow(rec, i));
            }
        }
        pane.appendChild(stepsPane);

        return pane;
    }

    function renderRecordingStepRow(rec, idx) {
        var step = rec.steps[idx];
        var replayResult = null;
        if (recordingReplayState && recordingReplayState.recordingId === rec.id) {
            replayResult = recordingReplayState.results.find(function (r) { return r.stepId === step.id; });
        }

        var statusBadge = el('span', {
            className: 'bowire-recording-step-status'
                + (replayResult ? (replayResult.pass ? ' pass' : ' fail') : ''),
            textContent: replayResult ? (replayResult.pass ? 'PASS' : 'FAIL') : (step.status || '?')
        });

        var protocolBadge = el('span', {
            className: 'bowire-recording-step-protocol',
            textContent: step.protocol || 'grpc'
        });

        var label = el('div', { className: 'bowire-recording-step-label' },
            el('span', { className: 'bowire-recording-step-index', textContent: String(idx + 1) + '.' }),
            protocolBadge,
            el('span', { className: 'bowire-recording-step-method',
                textContent: (step.service || '') + ' / ' + (step.method || '') }),
            statusBadge,
            step.durationMs ? el('span', { className: 'bowire-recording-step-duration',
                textContent: step.durationMs + 'ms' }) : null
        );

        var deleteBtn = el('button', {
            className: 'bowire-recording-step-delete',
            innerHTML: svgIcon('trash'),
            title: 'Remove this step',
            'aria-label': 'Remove this step',
            onClick: function (e) {
                e.stopPropagation();
                deleteRecordingStep(rec.id, step.id);
            }
        });

        var row = el('div', { className: 'bowire-recording-step' }, label, deleteBtn);
        return row;
    }

    // #285 — Server-side recording-session SSE listener. The workbench
    // already drives capture/replay locally via recordingActiveId +
    // recordingsList — this listener is purely informational, surfacing
    // server-initiated transitions (e.g. an MCP agent calling
    // bowire.record.start while the user has a browser tab open) so the
    // UI can react in a future iteration. State is exposed on the global
    // window.__bowireRecordingSession; consumers can subscribe via the
    // 'bowire:recording-session' DOM event. Best-effort connect — falls
    // silent if EventSource is unavailable or the server is older than
    // #285. Zero behavioural change in the existing recorder flow.
    function _initRecordingSessionStream() {
        if (typeof window === 'undefined' || typeof EventSource === 'undefined') return;
        if (window.__bowireRecordingSessionStream) return;
        try {
            var url = (config && config.prefix ? config.prefix : '') + '/api/recording/session/events';
            var es = new EventSource(url);
            window.__bowireRecordingSessionStream = es;
            window.__bowireRecordingSession = window.__bowireRecordingSession || null;
            function dispatch(kind, detail) {
                window.__bowireRecordingSession = detail || null;
                try {
                    window.dispatchEvent(new CustomEvent('bowire:recording-session', {
                        detail: { kind: kind, session: detail }
                    }));
                } catch (_) { /* IE11-era guard; harmless in modern browsers */ }
            }
            function parse(e) {
                try { return JSON.parse(e.data); } catch (_) { return null; }
            }
            es.addEventListener('snapshot', function (e) { dispatch('snapshot', parse(e)); });
            es.addEventListener('started', function (e) {
                var p = parse(e); dispatch('started', p && p.session ? p.session : null);
            });
            es.addEventListener('step', function (e) {
                var p = parse(e); dispatch('step', p && p.session ? p.session : null);
            });
            es.addEventListener('mode', function (e) {
                var p = parse(e); dispatch('mode', p && p.session ? p.session : null);
            });
            es.addEventListener('stopped', function (e) {
                var p = parse(e); dispatch('stopped', null);
                window.__bowireRecordingSession = null;
            });
            es.onerror = function () { /* EventSource auto-reconnects; nothing to do */ };
        } catch (_) {
            // Server doesn't expose the endpoint, or browser blocked EventSource —
            // silently disable. The existing localStorage-backed recorder flow
            // keeps working unchanged.
        }
    }
    _initRecordingSessionStream();
