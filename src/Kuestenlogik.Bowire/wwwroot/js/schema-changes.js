    // ---- #185 — Schema-change log (diff-aware schema watch) ----
    //
    // The schema watch (#48, api.js) diffs two discovery results and
    // shows the delta in the sidebar — but the delta was ephemeral:
    // dismissed, reloaded, gone. This module makes it durable and
    // ambient:
    //
    //   * every published delta is appended to a per-workspace change
    //     log on the server (7-day retention, survives reloads and is
    //     shared by every client of the workspace),
    //   * a statusbar pill reports "N changes since HH:MM" and expands
    //     into the chronological log,
    //   * the Discover rail icon carries a gentle pulse while there
    //     are unread changes,
    //   * clicking a change navigates to the affected method.
    //
    // "Unread" is derived from the server's lastReadAt watermark; the
    // watermark moves when the operator opens the dropdown (opening IS
    // reading — no separate mark-read chore).
    //
    // localStorage is deliberately NOT used here: the log's value is
    // exactly that it outlives the browser, so the server file is the
    // only source of truth and this cache is memory-only.

    var schemaChangeLog = [];            // entries, oldest first (server order)
    var schemaChangeLastReadAt = null;   // ISO string or null
    var _schemaChangeHydrated = false;
    var _schemaChangeWsCache = null;     // workspace id the cache belongs to
    // Bumped on every local mutation (append, mark-read, workspace
    // switch). A fetch captures the value at send time; a response
    // whose value no longer matches was snapshotted before a newer
    // local action and must not clobber it.
    var _schemaChangeSeq = 0;

    var SCHEMA_CHANGE_RETENTION_DAYS = 7;

    // deleteWorkspace / createWorkspace switch activeWorkspaceId IN
    // PLACE (only switchWorkspace reloads the page), so every entry
    // point re-checks whether the cache still belongs to the active
    // workspace — otherwise workspace A's unread pulse survives into
    // workspace B and opening the log would mark B's real entries read
    // while showing A's.
    function _schemaChangeSyncWorkspace() {
        var cur = (typeof activeWorkspaceId === 'string' && activeWorkspaceId)
            ? activeWorkspaceId : '';
        if (_schemaChangeWsCache === cur) return;
        _schemaChangeWsCache = cur;
        schemaChangeLog = [];
        schemaChangeLastReadAt = null;
        _schemaChangeHydrated = false;
        _schemaChangeSeq++;
    }

    // #212 — a browser-only workspace promises that no entity data
    // reaches the server's disk. The change log honours that: no GET,
    // no POST, the log stays session-local.
    function _schemaChangeDiskAllowed() {
        if (typeof getWorkspaceStorageMode !== 'function') return true;
        try { return getWorkspaceStorageMode() !== 'browser-only'; }
        catch (_) { return true; }
    }

    // Workspace scoping for the server calls — same convention as the
    // presets disk-sync. storageRoot rides along for git-backed
    // workspaces so the log lands inside the checked-out folder.
    function _schemaChangeWsQuery() {
        var wsId = (typeof activeWorkspaceId === 'string' && activeWorkspaceId)
            ? activeWorkspaceId : '';
        if (!wsId) return '';
        var ws = (typeof activeWorkspace === 'function') ? activeWorkspace() : null;
        return '?workspaceId=' + encodeURIComponent(wsId)
            + (ws && ws.storageRoot ? '&storageRoot=' + encodeURIComponent(ws.storageRoot) : '');
    }

    // Adopt a server envelope. Guards, in order: a response that is
    // stale (a local mutation happened after it was sent) may not
    // replace the entries; the read watermark only ever moves FORWARD
    // (a slow GET carrying yesterday's lastReadAt must not flip
    // just-read entries back to unread and re-light the rail pulse).
    // opts.watermarkOnly is for the mark-read response: its entries
    // are a snapshot that may predate a concurrent append, and
    // adopting them could vanish an optimistically-shown change.
    function _schemaChangeAdopt(envelope, seqAtSend, opts) {
        if (!envelope || typeof envelope !== 'object') return;
        var entriesOk = seqAtSend === _schemaChangeSeq && !(opts && opts.watermarkOnly);
        if (entriesOk && Array.isArray(envelope.entries)) {
            schemaChangeLog = envelope.entries;
        }
        if (envelope.lastReadAt) {
            var incoming = Date.parse(envelope.lastReadAt);
            var current = schemaChangeLastReadAt ? Date.parse(schemaChangeLastReadAt) : -Infinity;
            if (isFinite(incoming) && incoming > current) {
                schemaChangeLastReadAt = envelope.lastReadAt;
            }
        }
    }

    // One-shot boot hydration, kicked off lazily from the first pill /
    // badge render. Silent when the server doesn't answer (offline,
    // older embedded host without the endpoint) — the log then only
    // holds what this session observed.
    function ensureSchemaChangeLogLoaded() {
        _schemaChangeSyncWorkspace();
        if (_schemaChangeHydrated) return;
        _schemaChangeHydrated = true;
        if (typeof fetch !== 'function' || !_schemaChangeDiskAllowed()) return;
        var seq = _schemaChangeSeq;
        fetch(config.prefix + '/api/schema-changes' + _schemaChangeWsQuery())
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (envelope) {
                if (!envelope) return;
                _schemaChangeAdopt(envelope, seq);
                if ((schemaChangeLog.length > 0) && typeof render === 'function') render();
            })
            .catch(function () { /* silent — session-local log still works */ });
    }

    // The log as the UI should see it: the retention window applied at
    // read time. The cache itself is pruned only when new entries
    // arrive, so a tab that idles past an entry's 7th day would
    // otherwise show a pill whose dropdown is empty.
    function _schemaChangeCurrent() {
        return _schemaChangePrune(schemaChangeLog, Date.now());
    }

    // Client-side mirror of the server's retention prune, so a session
    // that stays open for days doesn't accumulate a stale tail while
    // the server file has already been trimmed.
    function _schemaChangePrune(entries, nowMs) {
        var cutoff = nowMs - SCHEMA_CHANGE_RETENTION_DAYS * 24 * 60 * 60 * 1000;
        return entries.filter(function (e) {
            var t = Date.parse(e && e.at);
            return isFinite(t) && t >= cutoff;
        });
    }

    // Flatten a schema-watch delta into change-log entries. Change
    // types mirror the server's allow-list: added / removed /
    // signature / deprecation / annotation. A method key is
    // '<service> <fullName>' (schemaMethodKey), so the method part is
    // everything after the service prefix.
    function _schemaChangeEntriesFromDelta(delta) {
        var at = (delta.at instanceof Date) ? delta.at.toISOString()
            : (delta.at || new Date().toISOString());
        var entries = [];
        function methodOf(m) { return m.key.slice(m.service.length + 1); }
        (delta.addedServices || []).forEach(function (s) {
            entries.push({ at: at, type: 'added', service: s });
        });
        (delta.removedServices || []).forEach(function (s) {
            entries.push({ at: at, type: 'removed', service: s });
        });
        (delta.addedMethods || []).forEach(function (m) {
            entries.push({ at: at, type: 'added', service: m.service, method: methodOf(m) });
        });
        (delta.removedMethods || []).forEach(function (m) {
            entries.push({ at: at, type: 'removed', service: m.service, method: methodOf(m) });
        });
        (delta.changedMethods || []).forEach(function (m) {
            entries.push({
                at: at,
                type: m.type === 'deprecation' ? 'deprecation' : 'signature',
                service: m.service, method: methodOf(m),
                detail: m.detail || null
            });
        });
        (delta.annotatedMethods || []).forEach(function (m) {
            entries.push({
                at: at, type: 'annotation',
                service: m.service, method: methodOf(m),
                detail: m.detail || 'description updated'
            });
        });
        return entries;
    }

    // Called by the schema-watch tick (api.js) for every published
    // delta. Appends locally first — the pill must tick even when the
    // server is unreachable — then posts, adopting the authoritative
    // envelope on success. (The server re-stamps every entry with its
    // own clock, so the adopted copy replaces our client-stamped one.)
    function schemaChangeLogRecord(delta) {
        _schemaChangeSyncWorkspace();
        var entries = _schemaChangeEntriesFromDelta(delta);
        if (entries.length === 0) return;
        schemaChangeLog = _schemaChangePrune(schemaChangeLog.concat(entries), Date.now());
        _schemaChangeSeq++;
        // A tick can land while the operator has the log open — the
        // dropdown lives outside the morphdom tree, so refresh it in
        // place and count the addition as read (it is on screen).
        var dd = document.getElementById('bowire-schema-changes-dropdown');
        if (dd) {
            _rebuildSchemaChangesDropdownInto(dd, _schemaChangeOpenWatermark);
            schemaChangeMarkRead();
        }
        if (typeof fetch !== 'function' || !_schemaChangeDiskAllowed()) return;
        var seq = _schemaChangeSeq;
        fetch(config.prefix + '/api/schema-changes' + _schemaChangeWsQuery(), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ entries: entries })
        })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (envelope) { _schemaChangeAdopt(envelope, seq); })
            .catch(function () { /* silent — local log already has it */ });
    }

    function schemaChangeUnreadCount() {
        _schemaChangeSyncWorkspace();
        var entries = _schemaChangeCurrent();
        if (entries.length === 0) return 0;
        var watermark = schemaChangeLastReadAt ? Date.parse(schemaChangeLastReadAt) : -Infinity;
        if (!isFinite(watermark) && watermark !== -Infinity) watermark = -Infinity;
        var n = 0;
        for (var i = 0; i < entries.length; i++) {
            var t = Date.parse(entries[i].at);
            if (isFinite(t) && t > watermark) n++;
        }
        return n;
    }

    // Move the read watermark: everything currently in the log becomes
    // read. Optimistic locally (the rail pulse should die the moment
    // the operator opens the log), then confirmed server-side. The
    // response is adopted watermark-only: its entries snapshot may
    // predate a concurrent append and must not vanish it.
    function schemaChangeMarkRead() {
        _schemaChangeSyncWorkspace();
        schemaChangeLastReadAt = new Date().toISOString();
        _schemaChangeSeq++;
        if (typeof fetch === 'function' && _schemaChangeDiskAllowed()) {
            var seq = _schemaChangeSeq;
            fetch(config.prefix + '/api/schema-changes/read' + _schemaChangeWsQuery(), { method: 'POST' })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (envelope) { _schemaChangeAdopt(envelope, seq, { watermarkOnly: true }); })
                .catch(function () { /* silent */ });
        }
        if (typeof render === 'function') render();
    }

    function _schemaChangeTimeShort(ms) {
        var d = new Date(ms);
        var hh = (d.getHours() < 10 ? '0' : '') + d.getHours();
        var mm = (d.getMinutes() < 10 ? '0' : '') + d.getMinutes();
        var now = new Date();
        var sameDay = d.getFullYear() === now.getFullYear()
            && d.getMonth() === now.getMonth() && d.getDate() === now.getDate();
        if (sameDay) return hh + ':' + mm;
        var weekday;
        try { weekday = d.toLocaleDateString(undefined, { weekday: 'short' }); }
        catch (_) { weekday = ''; }
        return (weekday ? weekday + ' ' : '') + hh + ':' + mm;
    }

    function _schemaChangeAge(ms, nowMs) {
        var s = Math.max(0, Math.round((nowMs - ms) / 1000));
        if (s < 60) return 'now';
        if (s < 3600) return Math.floor(s / 60) + 'm';
        if (s < 86400) return Math.floor(s / 3600) + 'h';
        return Math.floor(s / 86400) + 'd';
    }

    // Pill wording. Unread changes lead with the count and the time of
    // the OLDEST unread entry — that is the honest reading of "3
    // changes since 14:30" (nothing before 14:30 is included). With
    // everything read, the pill decays to a quiet total so the log
    // stays reachable without claiming attention.
    function _schemaChangePillLabel() {
        var unread = schemaChangeUnreadCount();
        var entries = _schemaChangeCurrent();
        if (unread > 0) {
            var watermark = schemaChangeLastReadAt ? Date.parse(schemaChangeLastReadAt) : -Infinity;
            var oldest = Infinity;
            for (var i = 0; i < entries.length; i++) {
                var t = Date.parse(entries[i].at);
                if (isFinite(t) && t > watermark && t < oldest) oldest = t;
            }
            return unread + ' change' + (unread !== 1 ? 's' : '')
                + (isFinite(oldest) ? ' since ' + _schemaChangeTimeShort(oldest) : '');
        }
        return entries.length + ' change' + (entries.length !== 1 ? 's' : '') + ' · 7d';
    }

    // Statusbar pill. Null when the log is empty — a "0 changes" pill
    // would be chrome without information.
    function renderSchemaChangePill() {
        ensureSchemaChangeLogLoaded();
        if (_schemaChangeCurrent().length === 0) return null;
        var unread = schemaChangeUnreadCount();
        var pill = el('button', {
            type: 'button',
            id: 'bowire-schema-changes-pill',
            className: 'bowire-schema-changes-pill'
                + (unread > 0 ? ' bowire-schema-changes-pill-unread' : ''),
            title: (unread > 0
                ? unread + ' unread schema change' + (unread !== 1 ? 's' : '')
                : 'Schema changes in the last ' + SCHEMA_CHANGE_RETENTION_DAYS + ' days')
                + ' — click for the change log',
            onClick: function (e) {
                e.stopPropagation();
                toggleSchemaChangesDropdown();
            }
        });
        pill.appendChild(el('span', {
            className: 'bowire-schema-changes-pill-dot'
                + (unread > 0 ? ' bowire-schema-changes-pill-dot-unread' : '')
        }));
        pill.appendChild(el('span', {
            className: 'bowire-schema-changes-pill-label',
            textContent: _schemaChangePillLabel()
        }));
        return pill;
    }

    var _SCHEMA_CHANGE_GLYPHS = {
        added: '+', removed: '−', signature: '~', deprecation: '!', annotation: '±'
    };

    // Unread boundary snapshotted when the dropdown opened — rows the
    // operator hadn't seen keep their accent while it stays open, and
    // a tick-triggered refresh styles its new rows against the same
    // boundary.
    var _schemaChangeOpenWatermark = -Infinity;

    function toggleSchemaChangesDropdown() {
        var existing = document.getElementById('bowire-schema-changes-dropdown');
        if (existing) { existing.remove(); return; }
        var anchor = document.getElementById('bowire-schema-changes-pill');
        if (!anchor) return;

        // Snapshot the unread boundary BEFORE marking read.
        _schemaChangeOpenWatermark = schemaChangeLastReadAt
            ? Date.parse(schemaChangeLastReadAt) : -Infinity;

        var dd = el('div', {
            id: 'bowire-schema-changes-dropdown',
            className: 'bowire-schema-changes-dropdown'
        });
        _rebuildSchemaChangesDropdownInto(dd, _schemaChangeOpenWatermark);
        document.body.appendChild(dd);
        // Statusbar lives at the bottom — open upward, anchored to the
        // pill (same geometry as the subscriptions dropdown).
        var rect = anchor.getBoundingClientRect();
        dd.style.right = Math.max(8, window.innerWidth - rect.right) + 'px';
        dd.style.bottom = (window.innerHeight - rect.top + 6) + 'px';
        var outside = function (e) {
            if (dd.contains(e.target) || anchor.contains(e.target)) return;
            dd.remove();
            document.removeEventListener('mousedown', outside, true);
        };
        setTimeout(function () {
            document.addEventListener('mousedown', outside, true);
        }, 0);

        // Opening the log IS reading it.
        if (schemaChangeUnreadCount() > 0) schemaChangeMarkRead();
    }

    function _rebuildSchemaChangesDropdownInto(dd, watermark) {
        dd.innerHTML = '';
        var nowMs = Date.now();
        var entries = _schemaChangePrune(schemaChangeLog.slice(), nowMs);
        // Newest first — "what just happened" is the question the
        // operator opened this to answer.
        entries.sort(function (a, b) { return Date.parse(b.at) - Date.parse(a.at); });

        dd.appendChild(el('div', { className: 'bowire-schema-changes-dropdown-header' },
            el('span', { textContent: 'Schema changes' }),
            el('span', {
                className: 'bowire-schema-changes-dropdown-count',
                textContent: '(' + entries.length + ' in ' + SCHEMA_CHANGE_RETENTION_DAYS + ' days)'
            })
        ));

        if (entries.length === 0) {
            dd.appendChild(el('div', {
                className: 'bowire-schema-changes-dropdown-empty',
                textContent: 'No schema changes detected in the last '
                    + SCHEMA_CHANGE_RETENTION_DAYS + ' days.'
            }));
            return;
        }

        var list = el('div', { className: 'bowire-schema-changes-dropdown-list' });
        entries.forEach(function (entry) {
            var t = Date.parse(entry.at);
            var isUnread = isFinite(t) && t > watermark;
            var removed = entry.type === 'removed';
            var row = el('div', {
                className: 'bowire-schema-changes-row'
                    + (isUnread ? ' is-unread' : '')
                    + (removed ? ' is-gone' : ''),
                title: removed
                    ? 'Removed from the schema — nothing to navigate to'
                    : 'Open ' + entry.service + (entry.method ? ' / ' + entry.method : '') + ' in Discover',
                onClick: removed ? null : function () {
                    var dd2 = document.getElementById('bowire-schema-changes-dropdown');
                    if (dd2) dd2.remove();
                    navigateToSchemaChange(entry);
                }
            });
            row.appendChild(el('span', {
                className: 'bowire-schema-changes-row-glyph bowire-schema-changes-type-' + entry.type,
                textContent: _SCHEMA_CHANGE_GLYPHS[entry.type] || '~'
            }));
            var label = el('span', { className: 'bowire-schema-changes-row-label' });
            label.appendChild(el('span', {
                className: 'bowire-schema-changes-row-service',
                textContent: entry.service
            }));
            if (entry.method) {
                label.appendChild(el('span', { className: 'bowire-schema-changes-row-sep', textContent: ' · ' }));
                label.appendChild(el('span', {
                    className: 'bowire-schema-changes-row-method',
                    textContent: entry.method
                }));
            }
            if (entry.detail) {
                label.appendChild(el('span', {
                    className: 'bowire-schema-changes-row-detail',
                    textContent: ' — ' + entry.detail
                }));
            }
            row.appendChild(label);
            row.appendChild(el('span', {
                className: 'bowire-schema-changes-row-meta',
                textContent: isFinite(t) ? _schemaChangeAge(t, nowMs) : ''
            }));
            list.appendChild(row);
        });
        dd.appendChild(list);

        dd.appendChild(el('div', { className: 'bowire-schema-changes-dropdown-footer' },
            el('span', {
                textContent: 'Kept for ' + SCHEMA_CHANGE_RETENTION_DAYS
                    + ' days per workspace · detected by Schema Watch'
            })
        ));
    }

    // Navigate to the method a change touched. The Discover tree keys
    // methods by fullName, so the stored names are enough to find the
    // live objects — when they still exist. A removed method has no
    // row to land on; the service group is the best remaining anchor.
    function navigateToSchemaChange(entry) {
        railMode = 'discover';
        try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch (_) { /* ignore */ }
        sidebarView = 'services';
        var svc = null;
        var list = (typeof services !== 'undefined' && Array.isArray(services)) ? services : [];
        for (var i = 0; i < list.length; i++) {
            if (list[i].name === entry.service) { svc = list[i]; break; }
        }
        if (!svc) {
            if (typeof toast === 'function') {
                toast('Service "' + entry.service + '" is not in the current schema', 'info');
            }
            render();
            return;
        }
        if (entry.method) {
            var ms = svc.methods || [];
            for (var j = 0; j < ms.length; j++) {
                if (ms[j].fullName === entry.method || ms[j].name === entry.method) {
                    if (typeof openTab === 'function') { openTab(svc, ms[j]); return; }
                }
            }
            if (typeof toast === 'function') {
                toast('Method "' + entry.method + '" no longer exists — showing its service', 'info');
            }
        }
        expandedServices.add(svc.name);
        if (typeof persistExpandedServices === 'function') persistExpandedServices();
        render();
    }
