    // ---- Sidebar Keyboard Navigation ----
    // Walks the same flat (svc, method) list the sidebar paints, so
    // J/K cycle through exactly what the user can see — respecting
    // the source-mode toggle, the protocol filter, and the search
    // query. Selection wraps at both ends so a long-press doesn't
    // dead-end at the bottom.
    function buildVisibleMethodSequence() {
        var visibleServices = getFilteredServices();
        var query = (searchQuery || '').trim().toLowerCase();
        var queryTokens = query ? query.split(/\s+/).filter(Boolean) : [];

        function matches(svc, m) {
            if (queryTokens.length === 0) return true;
            var hay = (
                (m.name || '') + ' ' +
                (m.fullName || '') + ' ' +
                (m.description || '') + ' ' +
                (m.summary || '') + ' ' +
                (m.httpPath || '') + ' ' +
                (m.httpMethod || '') + ' ' +
                (m.methodType || '') + ' ' +
                (svc.name || '') + ' ' +
                (svc.description || '')
            ).toLowerCase();
            for (var i = 0; i < queryTokens.length; i++) {
                if (hay.indexOf(queryTokens[i]) === -1) return false;
            }
            return true;
        }

        var seq = [];
        for (var i = 0; i < visibleServices.length; i++) {
            var svc = visibleServices[i];
            if (!svc.methods) continue;
            for (var j = 0; j < svc.methods.length; j++) {
                var mj = svc.methods[j];
                if (methodTypeFilter.size > 0 && !methodTypeFilter.has(mj.methodType || 'Unary')) continue;
                if (!matches(svc, mj)) continue;
                seq.push({ svc: svc, method: mj });
            }
        }
        return seq;
    }

    function navigateMethodSequence(direction) {
        var seq = buildVisibleMethodSequence();
        if (seq.length === 0) return;

        var curIdx = -1;
        if (selectedMethod && selectedService) {
            for (var i = 0; i < seq.length; i++) {
                if (seq[i].svc.name === selectedService.name
                    && seq[i].method.name === selectedMethod.name) {
                    curIdx = i;
                    break;
                }
            }
        }

        var nextIdx;
        if (curIdx === -1) {
            // Nothing selected yet — land on the first or last item
            // depending on direction so the keypress feels predictable.
            nextIdx = direction > 0 ? 0 : seq.length - 1;
        } else {
            nextIdx = (curIdx + direction + seq.length) % seq.length;
        }

        var target = seq[nextIdx];
        openTab(target.svc, target.method);
    }

    // ---- Source Selector (URL reflection vs. .proto file upload) ----

    /**
     * Renders the multi-URL list. In locked mode (config.lockServerUrl) the
     * URLs are read-only and there's no add/remove. Otherwise the user can
     * add, edit and remove URLs; each row has its own connection-status dot.
     */
    function renderUrlBarRow(locked) {
        var bar = el('div', { className: 'bowire-url-bar bowire-url-list' });

        if (serverUrls.length === 0) {
            // Empty state — one inline input that becomes the first URL on Enter
            bar.appendChild(renderUrlInputRow('', false, locked, function (newUrl) {
                if (!newUrl) return;
                serverUrls = [newUrl];
                connectionStatuses[newUrl] = 'connecting';
                ensureAliasForUrl(newUrl);
                persistServerUrls();
                render();
                fetchServices();
            }));
        } else {
            for (var i = 0; i < serverUrls.length; i++) {
                (function (idx, url) {
                    bar.appendChild(renderUrlInputRow(url, true, locked, function (newUrl) {
                        if (!newUrl) {
                            // Treating an empty edit as "remove this URL"
                            serverUrls.splice(idx, 1);
                            delete connectionStatuses[url];
                            removeAliasForUrl(url);
                        } else if (newUrl !== url) {
                            serverUrls[idx] = newUrl;
                            delete connectionStatuses[url];
                            connectionStatuses[newUrl] = 'connecting';
                            renameAliasForUrl(url, newUrl);
                        }
                        persistServerUrls();
                        render();
                        fetchServices();
                    }, function () {
                        // Remove button
                        serverUrls.splice(idx, 1);
                        delete connectionStatuses[url];
                        removeAliasForUrl(url);
                        persistServerUrls();
                        render();
                        fetchServices();
                    }));
                })(i, serverUrls[i]);
            }
        }

        if (!locked) {
            // Footer with "Add URL" and "Refresh all" actions
            var footer = el('div', { className: 'bowire-url-list-footer' },
                el('button', {
                    id: 'bowire-url-add-btn',
                    className: 'bowire-url-list-action',
                    title: 'Add another discovery URL',
                    onClick: function () {
                        serverUrls.push('');
                        render();
                        // Focus the new (empty) input
                        requestAnimationFrame(function () {
                            var inputs = $$('.bowire-url-input');
                            if (inputs.length > 0) inputs[inputs.length - 1].focus();
                        });
                    }
                },
                    el('span', { innerHTML: svgIcon('plus'), className: 'bowire-url-list-action-icon' }),
                    el('span', { textContent: 'Add URL' })
                ),
                el('button', {
                    id: 'bowire-url-refresh-btn',
                    className: 'bowire-url-list-action',
                    title: 'Re-discover from all URLs',
                    onClick: function () {
                        serverUrls.forEach(function (u) { connectionStatuses[u] = 'connecting'; });
                        render();
                        fetchServices();
                    }
                },
                    el('span', { innerHTML: svgIcon('repeat'), className: 'bowire-url-list-action-icon' }),
                    el('span', { textContent: 'Refresh' })
                )
            );
            bar.appendChild(footer);
        }

        return bar;
    }

    /**
     * Renders a single editable URL row with status dot, alias input,
     * URL input, and (optional) remove button. Calls onCommit with the
     * trimmed URL value when the user presses Enter or blurs the URL
     * input. The alias is persisted directly through setUrlAlias —
     * it has its own commit / validation path because the URL the
     * alias is keyed by can race with an in-flight URL edit.
     */
    function renderUrlInputRow(url, hasRemoveButton, locked, onCommit, onRemove) {
        var status = connectionStatuses[url] || 'disconnected';
        var row = el('div', { className: 'bowire-url-row' });

        row.appendChild(el('div', { className: 'bowire-url-status' },
            el('span', {
                className: 'bowire-url-dot ' + status,
                title: 'Status: ' + status
            })
        ));

        // ---- Alias input ----
        // Sits left of the URL so the operator's eye lands on the
        // friendly short name first; the URL itself becomes secondary
        // context. Empty when the URL hasn't been committed yet (the
        // alias auto-fills on first commit in renderUrlBarRow).
        var aliasErrSlot = el('div', { className: 'bowire-url-alias-err' });
        var aliasValue = url ? aliasForUrl(url) : '';
        // aliasForUrl falls back to the URL itself for un-aliased
        // entries (so other call sites always print SOMETHING). Don't
        // surface that in the input — it would just look like the URL
        // was duplicated.
        if (aliasValue === url) aliasValue = '';
        var aliasInput = el('input', {
            className: 'bowire-url-alias-input' + (locked ? ' locked' : ''),
            type: 'text',
            value: aliasValue,
            placeholder: 'alias',
            title: locked ? 'Alias locked — URL is fixed via --url parameter'
                          : 'Short name for this URL (unique per workspace). Used in the discover tree, connection popover, and filters.',
            spellcheck: 'false',
            autocomplete: 'off'
        });
        if (locked) {
            aliasInput.readOnly = true;
        } else {
            var commitAlias = function (nextRaw) {
                aliasErrSlot.textContent = '';
                if (!url) return;  // alias makes no sense before the URL is set
                var nextTrim = (nextRaw || '').trim();
                var current = serverUrlAliases[url] || '';
                if (nextTrim === current) return;
                if (!nextTrim) {
                    // Empty input → drop the user's custom alias and
                    // fall back to the auto-suggested one so we never
                    // leave the URL anonymous in other views.
                    removeAliasForUrl(url);
                    ensureAliasForUrl(url);
                    render();
                    return;
                }
                var res = setUrlAlias(url, nextTrim);
                if (!res.ok) {
                    aliasErrSlot.textContent = res.error;
                    aliasInput.classList.add('invalid');
                    return;
                }
                aliasInput.classList.remove('invalid');
                render();
            };
            aliasInput.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') { e.preventDefault(); commitAlias(e.target.value); }
                else if (e.key === 'Escape') {
                    aliasErrSlot.textContent = '';
                    aliasInput.classList.remove('invalid');
                    aliasInput.value = aliasValue;
                }
            });
            aliasInput.addEventListener('blur', function (e) { commitAlias(e.target.value); });
        }

        // ---- URL input ----
        // Build the attrs map conditionally — el()'s default path is
        // setAttribute(k, v) for every key, and setAttribute(k, undefined)
        // sets the attribute to the literal string "undefined" which the
        // browser then treats as truthy. That made the URL input look
        // read-only when locked was false. Build the attrs object so
        // only the keys that matter are passed.
        var inputAttrs = {
            className: 'bowire-url-input' + (locked ? ' locked' : ''),
            type: 'text',
            value: url,
            placeholder: 'https://api.example.com/openapi.json',
            title: locked ? 'URL is fixed via --url parameter' : 'Discovery URL — gRPC server, OpenAPI doc, SignalR hub, ...'
        };
        if (locked) {
            inputAttrs.readOnly = 'readonly';
        } else {
            inputAttrs.onKeydown = function (e) {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    onCommit(e.target.value.trim());
                }
            };
            inputAttrs.onBlur = function (e) {
                var v = e.target.value.trim();
                if (v !== url) onCommit(v);
            };
        }
        var input = el('input', inputAttrs);

        // Wrap alias + URL inputs in a single column so the per-row
        // validation message can slip in below the alias without
        // pushing the URL input out of alignment.
        var inputs = el('div', { className: 'bowire-url-inputs' },
            el('div', { className: 'bowire-url-fields' }, aliasInput, input),
            aliasErrSlot
        );
        row.appendChild(inputs);

        if (locked) {
            row.appendChild(el('span', {
                className: 'bowire-url-locked-icon',
                title: 'Locked via --url parameter',
                innerHTML: svgIcon('lock')
            }));
        } else if (hasRemoveButton) {
            row.appendChild(el('button', {
                id: 'bowire-url-remove-' + url.replace(/[^a-zA-Z0-9]/g, '_').slice(0, 40),
                className: 'bowire-url-remove',
                title: 'Remove this URL',
                'aria-label': 'Remove this URL',
                innerHTML: svgIcon('close'),
                onClick: onRemove
            }));
        }
        return row;
    }

    // #297 — Cross-feature state index. Walks the workspace's
    // collections / recordings / benchmarks / presets ONCE per render
    // and returns a Map keyed by 'service|method' so the sidebar's
    // method rows can render at-a-glance indicators without paying
    // per-row scan cost. Pills surface in renderMethodCrossFeatureBadges.
    function buildCrossFeatureIndex() {
        var idx = new Map();
        function bump(svc, mth, field, delta) {
            if (!svc || !mth) return;
            var key = svc + '|' + mth;
            var entry = idx.get(key);
            if (!entry) { entry = { collections: 0, recordings: 0, benchmark: 0, presets: 0 }; idx.set(key, entry); }
            entry[field] += (delta || 1);
        }
        // Collections: every saved item carries service + method.
        try {
            if (Array.isArray(collectionsList)) {
                for (var ci = 0; ci < collectionsList.length; ci++) {
                    var col = collectionsList[ci];
                    if (!col || !Array.isArray(col.items)) continue;
                    for (var ii = 0; ii < col.items.length; ii++) {
                        var it = col.items[ii];
                        if (it) bump(it.service, it.method, 'collections', 1);
                    }
                }
            }
        } catch { /* ignore */ }
        // Recordings: count DISTINCT recordings that include the method
        // (not raw step count — a recording with 50 repeats of the same
        // call would otherwise drown out other lighter ones).
        try {
            if (Array.isArray(recordingsList)) {
                for (var ri = 0; ri < recordingsList.length; ri++) {
                    var rec = recordingsList[ri];
                    if (!rec || !Array.isArray(rec.steps)) continue;
                    var seen = new Set();
                    for (var si = 0; si < rec.steps.length; si++) {
                        var st = rec.steps[si];
                        if (!st) continue;
                        var k = (st.service || '') + '|' + (st.method || '');
                        if (seen.has(k)) continue;
                        seen.add(k);
                        bump(st.service, st.method, 'recordings', 1);
                    }
                }
            }
        } catch { /* ignore */ }
        // Benchmarks: one spec → one entry.
        try {
            if (Array.isArray(benchmarksList)) {
                for (var bi = 0; bi < benchmarksList.length; bi++) {
                    var bs = benchmarksList[bi];
                    if (bs) bump(bs.service, bs.method, 'benchmark', 1);
                }
            }
        } catch { /* ignore */ }
        // Presets: scoped to 'discover' mode (where method-targeted
        // presets land via #296). Each preset's config carries service
        // + method via the snapshot. Other modes' presets aren't
        // method-bound and stay out of the index.
        try {
            if (typeof loadPresets === 'function') {
                var presetList = loadPresets('discover') || [];
                for (var pi = 0; pi < presetList.length; pi++) {
                    var pr = presetList[pi];
                    if (pr && pr.config) bump(pr.config.service, pr.config.method, 'presets', 1);
                }
            }
        } catch { /* ignore */ }
        return idx;
    }

    // Builds the inline badge row that goes inside a method item. The
    // strip stays empty (returns null) when no cross-feature artifacts
    // exist for the method — keeping rows uncluttered for the bulk of
    // methods that nothing has touched yet.
    function renderMethodCrossFeatureBadges(xfIndex, svcName, methodName) {
        if (!xfIndex) return null;
        var entry = xfIndex.get(svcName + '|' + methodName);
        if (!entry) return null;
        if (!entry.collections && !entry.recordings && !entry.benchmark && !entry.presets) return null;
        var strip = el('span', { className: 'bowire-method-xf-strip' });
        function pill(letter, count, hue, title, onClick) {
            var p = el('button', {
                className: 'bowire-method-xf-pill bowire-method-xf-pill-' + hue,
                title: title,
                'aria-label': title,
                onClick: function (e) {
                    e.stopPropagation();
                    if (typeof onClick === 'function') onClick();
                }
            },
                el('span', { className: 'bowire-method-xf-letter', textContent: letter }),
                count > 1 ? el('span', { className: 'bowire-method-xf-count', textContent: String(count) }) : null
            );
            strip.appendChild(p);
        }
        if (entry.collections) {
            pill('C', entry.collections, 'collection',
                entry.collections + ' collection' + (entry.collections === 1 ? '' : 's') + ' contain this method',
                function () {
                    railMode = 'collections';
                    try { localStorage.setItem('bowire_rail_mode', 'collections'); } catch { /* ignore */ }
                    render();
                });
        }
        if (entry.recordings) {
            pill('R', entry.recordings, 'recording',
                entry.recordings + ' recording' + (entry.recordings === 1 ? '' : 's') + ' include this method',
                function () {
                    railMode = 'recordings';
                    try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                    render();
                });
        }
        if (entry.benchmark) {
            pill('B', entry.benchmark, 'benchmark',
                entry.benchmark + ' benchmark spec' + (entry.benchmark === 1 ? '' : 's') + ' target this method',
                function () {
                    railMode = 'benchmarks';
                    try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); } catch { /* ignore */ }
                    render();
                });
        }
        if (entry.presets) {
            pill('P', entry.presets, 'preset',
                entry.presets + ' preset' + (entry.presets === 1 ? '' : 's') + ' for this method',
                null);
        }
        return strip;
    }

    function renderProtoUploadPanel() {
        var panel = el('div', { className: 'bowire-proto-panel' });

        // Count both proto and rest schemas — both flow through this drop zone now.
        var schemaCount = services.filter(function (s) {
            return s.source === 'proto' || s.source === 'rest';
        }).length;

        var dropZone = el('div', {
            id: 'bowire-proto-dropzone',
            className: 'bowire-proto-dropzone',
            onClick: function () {
                var input = document.createElement('input');
                input.type = 'file';
                input.accept = '.proto,.json,.yaml,.yml';
                input.multiple = true;
                input.onchange = async function () {
                    if (!input.files || input.files.length === 0) return;
                    var protoCount = 0, openapiCount = 0;
                    for (var file of input.files) {
                        var content = await file.text();
                        var lower = file.name.toLowerCase();
                        var endpoint;
                        if (lower.endsWith('.proto')) {
                            endpoint = '/api/proto/upload';
                            protoCount++;
                        } else {
                            // .json / .yaml / .yml — treat as OpenAPI/Swagger
                            endpoint = '/api/openapi/upload?name=' + encodeURIComponent(file.name);
                            openapiCount++;
                        }
                        await fetch(config.prefix + endpoint, { method: 'POST', body: content });
                    }
                    var msg = [];
                    if (protoCount > 0) msg.push(protoCount + ' .proto');
                    if (openapiCount > 0) msg.push(openapiCount + ' OpenAPI');
                    toast(msg.join(' + ') + ' imported', 'success');
                    fetchServices();
                };
                input.click();
            }
        },
            el('div', { className: 'bowire-proto-dropzone-icon', innerHTML: svgIcon('upload') }),
            el('div', { className: 'bowire-proto-dropzone-title', textContent: 'Upload schema files' }),
            el('div', { className: 'bowire-proto-dropzone-hint', textContent: '.proto for gRPC  ·  .json / .yaml for OpenAPI / Swagger' })
        );
        panel.appendChild(dropZone);

        if (schemaCount > 0) {
            panel.appendChild(el('div', { className: 'bowire-proto-status' },
                el('span', {
                    className: 'bowire-proto-status-count',
                    textContent: schemaCount + ' service' + (schemaCount !== 1 ? 's' : '') + ' loaded'
                }),
                el('button', {
                    id: 'bowire-proto-clear-btn',
                    className: 'bowire-proto-clear',
                    textContent: 'Clear',
                    title: 'Remove all uploaded schema files',
                    onClick: async function () {
                        await Promise.all([
                            fetch(config.prefix + '/api/proto/upload', { method: 'DELETE' }),
                            fetch(config.prefix + '/api/openapi/upload', { method: 'DELETE' })
                        ]);
                        toast('Schema files cleared', 'success');
                        fetchServices();
                    }
                })
            ));
        }

        return panel;
    }

    // #294 — renderSourceSelector retired. URL bar + schema-files tabs
    // moved to workspace-detail (#155 / #290). renderUrlBarRow and
    // renderProtoUploadPanel live on as the helpers that workspace-
    // detail mounts.

    // Renders the flat "Favorites" view into the given list container.
    // Each row: protocol badge → method type badge → service.method name
    // → explicit × button to remove the entry from favorites. The × uses
    // stopPropagation so it doesn't also trigger the row's click-to-open
    // handler.
    //
    // Services for a favorite may have been filtered out of the current
    // result set (e.g. the user deleted a URL, or the discovered service
    // list hasn't loaded yet). Those entries still show — greyed out with
    // an "unavailable" hint — because the user still wants to manage them
    // (at minimum to remove them).
    function renderFavoritesListInto(list) {
        var favs = getFavorites();

        if (favs.length === 0) {
            list.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px 24px' },
                el('div', { className: 'bowire-empty-title', textContent: 'No favorites yet' }),
                el('div', { className: 'bowire-empty-desc', textContent:
                    'Click the ★ on a method in the Services view to pin it here.' })
            ));
            return;
        }

        // Resolve each favorite against the currently loaded services so we
        // can show the right badges and hand a live (svc, method) pair to
        // the click handler. Favorites whose service or method isn't in the
        // current discovery result get a stub so the row still renders.
        var rows = [];
        for (var fi = 0; fi < favs.length; fi++) {
            var f = favs[fi];
            var svc = services.find(function (s) { return s.name === f.service; });
            var method = svc && svc.methods.find(function (mt) { return mt.name === f.method; });
            rows.push({ fav: f, svc: svc || null, method: method || null });
        }

        // #297 — cross-feature index shared across favorite rows.
        var favXfIndex = buildCrossFeatureIndex();
        var favGroup = el('div', { className: 'bowire-service-group bowire-favorites-group' });
        for (var ri = 0; ri < rows.length; ri++) {
            (function (row) {
                var available = row.svc && row.method;
                var isActive = available && selectedMethod
                    && selectedMethod.fullName === row.method.fullName;
                // Stable per-favorite DOM id so morphdom keyed-matches
                // the row between renders. Without this, removing the
                // first favorite from a list of several (e.g. [A, B, C]
                // → delete A) would leave morphdom position-matching
                // the now-shifted rows: the old row[0]=A node would be
                // reused to render B, the old row[1]=B would render C,
                // and so on. The old onClick closures still pointed at
                // A and B respectively, so the next × click removed
                // the wrong favorite. With a stable id, morphdom
                // correctly drops the removed row's node and keeps the
                // survivors' listeners bound to the right entries.
                var safeKey = (row.fav.service + '__' + row.fav.method)
                    .replace(/[^a-zA-Z0-9_-]/g, '_');
                var favIsExecuting = available && isJobActive(row.svc.name, row.method.name);
                var item = el('div', {
                    id: 'bowire-fav-' + safeKey,
                    draggable: 'true',
                    className: 'bowire-method-item bowire-fav-draggable'
                        + (isActive ? ' active' : '')
                        + (available ? '' : ' unavailable')
                        + (favIsExecuting ? ' executing' : ''),
                    title: available
                        ? (row.method.summary || row.method.description || row.method.name)
                        : 'This service is no longer in the discovered set — click × to remove',
                    onClick: function () {
                        if (!available) return;
                        openTab(row.svc, row.method);
                    }
                });

                // Drag & Drop reordering. The dataTransfer carries the
                // favorites index (ri) so the drop handler knows which
                // entry to move. The visual feedback is a translucent
                // clone and a top-border highlight on the drop target.
                (function (dragIdx) {
                    item.addEventListener('dragstart', function (e) {
                        e.dataTransfer.effectAllowed = 'move';
                        e.dataTransfer.setData('text/plain', String(dragIdx));
                        item.classList.add('bowire-fav-dragging');
                    });
                    item.addEventListener('dragend', function () {
                        item.classList.remove('bowire-fav-dragging');
                        var all = document.querySelectorAll('.bowire-fav-dragover');
                        for (var di = 0; di < all.length; di++) all[di].classList.remove('bowire-fav-dragover');
                    });
                    item.addEventListener('dragover', function (e) {
                        e.preventDefault();
                        e.dataTransfer.dropEffect = 'move';
                        item.classList.add('bowire-fav-dragover');
                    });
                    item.addEventListener('dragleave', function () {
                        item.classList.remove('bowire-fav-dragover');
                    });
                    item.addEventListener('drop', function (e) {
                        e.preventDefault();
                        item.classList.remove('bowire-fav-dragover');
                        var fromIdx = parseInt(e.dataTransfer.getData('text/plain'), 10);
                        if (isNaN(fromIdx) || fromIdx === dragIdx) return;
                        reorderFavorite(fromIdx, dragIdx);
                    });
                })(ri);

                // Layout: [proto-icon][method][service] ... [executing][badge][×]
                // Left side: identification. Right side (from executing):
                // status indicators + remove action. The spacer in between
                // pushes the right group to the far end of the row.

                // Protocol icon (leading)
                if (protocols.length > 1 && row.svc) {
                    var proto = protocols.find(function (p) { return p.id === row.svc.source; });
                    if (proto) {
                        item.appendChild(el('span', {
                            className: 'bowire-favorites-proto-badge',
                            title: proto.name,
                            innerHTML: proto.icon
                        }));
                    }
                }

                // Method name
                item.appendChild(el('span', {
                    className: 'bowire-method-name'
                        + (available && row.method.deprecated ? ' deprecated' : ''),
                    textContent: row.fav.method
                }));

                // Service name (secondary)
                item.appendChild(el('span', {
                    className: 'bowire-fav-service-label',
                    textContent: row.fav.service.split('.').pop()
                }));

                // Spacer pushes everything after this to the right
                item.appendChild(el('span', { style: 'flex:1' }));

                // #297 — cross-feature badges (C/R/B/P). null when no
                // artifacts exist for this method, keeping favorites
                // tidy for the common case.
                if (available) {
                    var favXfStrip = renderMethodCrossFeatureBadges(
                        favXfIndex, row.svc.name, row.method.name);
                    if (favXfStrip) item.appendChild(favXfStrip);
                }

                // Executing indicator (pulsing play icon, only when active)
                var favExecuting = available && isJobActive(row.svc.name, row.method.name);
                if (favExecuting) {
                    item.appendChild(el('span', {
                        className: 'bowire-method-executing',
                        innerHTML: svgIcon('play'),
                        title: 'Currently executing'
                    }));
                }

                // Method type badge (SS/U/CS/DX)
                if (available) {
                    item.appendChild(el('span', {
                        className: 'bowire-method-badge',
                        dataset: { type: methodBadgeType(row.method), direction: methodDirection(row.method) },
                        textContent: methodBadgeText(row.method)
                    }));
                }

                // Remove button (×)
                item.appendChild(el('button', {
                    className: 'bowire-favorites-remove',
                    title: 'Remove from favorites',
                    textContent: '\u00d7',
                    onClick: function (e) {
                        e.stopPropagation();
                        toggleFavorite(row.fav.service, row.fav.method);
                    }
                }));

                favGroup.appendChild(item);
            })(rows[ri]);
        }
        list.appendChild(favGroup);
    }

    // ---- Environments Sidebar View (Postman-style inline) ----
    // Shows the list of environments in the sidebar with an inline
    // variable editor below. No modal needed for everyday editing.
    // The active environment is highlighted; clicking another one
    // selects it for editing (but doesn't switch the active env —
    // that's what the topbar dropdown does).
    var envSidebarSelectedId = null;

    function renderEnvironmentsListInto(list) {
        // Self-contained workspaces: envs are the active workspace's
        // own set, no cross-workspace shared catalogue + inclusion
        // checkbox dance.
        var envs = getEnvironments();
        var activeId = getActiveEnvId();
        var globals = getGlobalVars();

        // Default selection: active env, or first env, or globals
        if (!envSidebarSelectedId) {
            envSidebarSelectedId = activeId || (envs.length > 0 ? envs[0].id : '__globals__');
        }

        // Unified sidebar toolbar — primary "+ New environment".
        // Drops the bespoke .bowire-env-sidebar-header markup that
        // had drifted out of sync with the other rails' headers.
        list.appendChild(renderSidebarToolbar({
            title: 'Environments',
            primary: {
                icon: 'plus',
                title: 'Create new environment',
                onClick: function () {
                    if (typeof openCreateEnvironmentDialog !== 'function') return;
                    openCreateEnvironmentDialog(function (env) {
                        envSidebarSelectedId = env.id;
                        render();
                    });
                }
            }
        }));

        // (Workspace include-all toggle + per-env inclusion checkboxes
        // retired with the self-contained workspaces refactor — envs
        // are workspace-owned, not subscribed-to.)

        // Globals first — always available, visually part of the
        // selectable list (not a footer/separator).
        var globalVarCount = Object.keys(globals).length;
        list.appendChild(el('div', {
            id: 'bowire-env-item-globals',
            className: 'bowire-env-sidebar-item'
                + (envSidebarSelectedId === '__globals__' ? ' selected' : ''),
            onClick: function () {
                envSidebarSelectedId = '__globals__';
                render();
            }
        },
            el('span', { className: 'bowire-env-sidebar-item-icon', innerHTML: svgIcon('globe') }),
            el('span', { className: 'bowire-env-sidebar-item-name', textContent: 'Globals' }),
            el('span', { style: 'flex:1' }),
            globalVarCount > 0
                ? el('span', { className: 'bowire-env-sidebar-item-count', textContent: String(globalVarCount) })
                : null
        ));

        // Workspace envs \u2014 just iterate. No inclusion gating, no
        // greyed-out non-included rows, because there's no catalogue
        // outside the workspace to gate against.
        for (var i = 0; i < envs.length; i++) {
            (function (env) {
                var isActive = env.id === activeId;
                var isSelected = env.id === envSidebarSelectedId;
                var varCount = Object.keys(env.vars || {}).length;
                var item = el('div', {
                    id: 'bowire-env-item-' + env.id,
                    className: 'bowire-env-sidebar-item'
                        + (isSelected ? ' selected' : '')
                        + (isActive ? ' active-env' : ''),
                    onClick: function () {
                        envSidebarSelectedId = env.id;
                        render();
                    }
                },
                    el('span', {
                        className: 'bowire-env-color-dot' + (isActive ? ' active' : ''),
                        style: 'background:' + (env.color || '#6366f1'),
                        title: isActive ? 'Active environment' : ''
                    }),
                    el('span', { className: 'bowire-env-sidebar-item-name', textContent: env.name }),
                    el('span', { style: 'flex:1' }),
                    varCount > 0
                        ? el('span', { className: 'bowire-env-sidebar-item-count', textContent: String(varCount) })
                        : null,
                    !isActive
                        ? el('button', {
                            className: 'bowire-env-sidebar-activate-btn',
                            title: 'Set as active environment',
                            textContent: '\u25B6',
                            onClick: function (e) {
                                e.stopPropagation();
                                setActiveEnvId(env.id);
                                render();
                            }
                        })
                        : null
                );
                list.appendChild(item);
            })(envs[i]);
        }

        // The variable editor lives in the main pane (renderMain)
        // — the sidebar only shows the selectable list.
    }

    // renderInlineVarEditor removed — replaced by renderEnvVariableTable
    // in render-main.js (full-width main pane with multi-line values).

    // #133 / #137 — Activity rail. Always-visible icon column on the
    // leftmost edge that switches workbench focus by use case. Single
    // source of truth for the rail mode catalogue: each entry declares
    // which sidebar template renders (kind). The render-env-auth body
    // assembly + the renderSidebar dispatcher both read from this so a
    // new mode only needs one entry to wire up its sidebar shape.
    //
    // sidebar.kind values:
    //   'none'         → no sidebar; main pane spans edge to edge
    //   'services'     → discover services tree (Discover, Security)
    //   'collections'  → Collections list (Collections mode)
    //   'environments' → Environments list (Environments mode)
    //   'recordings'   → Recordings list
    //   'mocks'        → Mocks list
    //   'workspaces'   → Workspaces list
    //   'sources'      → Sources list
    //   'benchmarks'   → Benchmarks list
    //   'flows'        → legacy flows sidebar via sidebarView='flows'
    //   'proxy'        → legacy proxy sidebar via sidebarView='proxy'
    var _railModes = [
        { id: 'home',         icon: 'house',     label: 'Home',              group: 'work',      sidebar: { kind: 'none' } },
        // 'sources' rail mode retired — URL + Schema-File management
        // moved into the Workspace-detail pane (workspaces are the
        // proper owner: each workspace's sources are an integral part
        // of "what I'm working on right now"). Boot migration in
        // prologue.js rewrites a stale railMode='sources' to
        // 'workspaces' so existing installs land on the new spot.
        { id: 'discover',     icon: 'discover',  label: 'Discover',          group: 'work',      sidebar: { kind: 'services' } },
        // Collections rail mode kept in the catalogue (so the existing
        // sidebar + main-pane render paths still work when the
        // Workspaces tree dispatches to it), but hideFromRail removes
        // the standalone button from the activity rail — collections
        // are managed inside their workspace.
        { id: 'collections',  icon: 'folder',    label: 'Collections',       group: 'work',      sidebar: { kind: 'collections' }, hideFromRail: true },
        // Environments retired from the rail too — owned by workspaces:
        // each workspace declares which envs are included, and the
        // editor opens inline when a workspace's Environments node
        // (or one of its env leaves) is clicked.
        { id: 'environments', icon: 'globe',     label: 'Environments',      group: 'work',      sidebar: { kind: 'environments' }, hideFromRail: true },
        // Recordings sits back on the rail — peer with Mocks + Flows
        // (every "scenarios"-group surface is a rail-level work mode).
        // The Workspaces tree's per-workspace Recordings sub-node stays
        // available for workspace-overview navigation; the rail is the
        // direct quick-access path for the active workspace.
        { id: 'recordings',   icon: 'recording', label: 'Recordings',        group: 'scenarios', sidebar: { kind: 'recordings' } },
        { id: 'mocks',        icon: 'mock',      label: 'Mocks',             group: 'scenarios', sidebar: { kind: 'mocks' } },
        { id: 'flows',        icon: 'flow',      label: 'Flows',             group: 'scenarios', sidebar: { kind: 'flows' } },
        { id: 'proxy',        icon: 'disconnect',label: 'Proxy / MITM',      group: 'quality',   sidebar: { kind: 'proxy' } },
        { id: 'benchmarks',   icon: 'chart',     label: 'Benchmarks',        group: 'quality',   sidebar: { kind: 'benchmarks' } },
        // Parallel sessions: launched directly from a recording or
        // collection toolbar (#132 minimal). No standalone rail mode —
        // the result lands inline under the source that started it.
        { id: 'security',     icon: 'shield',    label: 'Security',          group: 'hardening', sidebar: { kind: 'security' } },
        { id: 'workspaces',   icon: 'layers',    label: 'Workspaces',        group: 'hardening', sidebar: { kind: 'workspaces' } },
    ];
    function _railModeById(id) {
        for (var i = 0; i < _railModes.length; i++) {
            if (_railModes[i].id === id) return _railModes[i];
        }
        return null;
    }
    function currentRailSidebarSpec() {
        var m = _railModeById(railMode);
        return (m && m.sidebar) ? m.sidebar : { kind: 'none' };
    }

    // #163 — Per-rail-mode "what's in here" count. Reads from the live
    // global lists for each surface and returns the number that lights
    // up the rail-icon badge below. Rails that don't have a meaningful
    // count (home, parallel, security, workspaces' own rail) return
    // null so no badge renders.
    function _railModeCount(modeId) {
        switch (modeId) {
            case 'discover':
                // Match what the tree actually renders. Two filters
                // matter: getFilteredServices() applies source-mode /
                // protocol / URL filters; then the tree skips any
                // service whose methods array is empty (the OpenAPI
                // discovery sometimes emits a host-only service that
                // carries the connection but no operations). The
                // badge mirrors that second skip so it can't disagree
                // with the visible row count.
                if (typeof getFilteredServices === 'function') {
                    try {
                        return getFilteredServices()
                            .filter(function (s) {
                                return s && Array.isArray(s.methods) && s.methods.length > 0;
                            }).length;
                    }
                    catch { /* fall through */ }
                }
                return (typeof services !== 'undefined' && Array.isArray(services))
                    ? services.filter(function (s) {
                        return s && Array.isArray(s.methods) && s.methods.length > 0;
                    }).length
                    : 0;
            case 'collections':
                return (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList))
                    ? collectionsList.length : 0;
            case 'recordings':
                return (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList))
                    ? recordingsList.length : 0;
            case 'mocks':
                return (typeof mocksList !== 'undefined' && Array.isArray(mocksList))
                    ? mocksList.length : 0;
            case 'flows':
                return (typeof flowsList !== 'undefined' && Array.isArray(flowsList))
                    ? flowsList.length : 0;
            case 'benchmarks':
                return (typeof benchmarksList !== 'undefined' && Array.isArray(benchmarksList))
                    ? benchmarksList.length : 0;
            case 'environments':
                if (typeof getEnvironments === 'function') {
                    try { return getEnvironments().length; } catch { return 0; }
                }
                return 0;
            case 'workspaces':
                // Workspace count on the rail icon read like an unread-
                // message badge — visually loud for a number that just
                // says "you have N folders configured". No meaningful
                // signal to surface, so the badge is suppressed.
                return null;
            case 'proxy':
                return (typeof proxyFlows !== 'undefined' && Array.isArray(proxyFlows))
                    ? proxyFlows.length : 0;
            default:
                return null;
        }
    }

    function renderActivityRail() {
        // Modes marked hideFromRail (Collections, Environments) stay in
        // the catalogue so railMode='…' routing keeps working when the
        // workspace tree dispatches into them, but the standalone rail
        // button is suppressed — they live "inside" a workspace and
        // are reached via the Workspaces tree.
        var modes = _railModes.filter(function (m) { return !m.hideFromRail; });

        // Workspace identity cue — rail-tint variant. When the operator
        // turned the cue on AND picked "Rail tint" in Settings → General,
        // soften-blend the rail's surface with the active workspace's
        // colour. color-mix keeps the tint subtle (workspace hue at
        // ~18%) so the rail still reads as chrome rather than a full-
        // saturated colour block — readable in both themes without
        // per-colour calibration. The 'top-strip' variant is rendered
        // separately in render-env-auth.js's render().
        var _railTintStyle = '';
        try {
            var on = localStorage.getItem('bowire_workspace_identity_enabled') === 'true';
            var variant = localStorage.getItem('bowire_workspace_identity_variant') || 'top-strip';
            if (on && variant === 'rail-tint' && typeof activeWorkspace === 'function') {
                var aw = activeWorkspace();
                if (aw && aw.color) {
                    _railTintStyle = 'background: color-mix(in srgb, var(--bowire-sidebar-bg) 82%, ' + aw.color + ' 18%);';
                }
            }
        } catch { /* ignore */ }
        var rail = el('div', {
            id: 'bowire-activity-rail',
            className: 'bowire-activity-rail',
            style: _railTintStyle || undefined
        });

        // Cache the catalogue order so the post-mount layout pass can
        // build the overflow popover from the SAME list, in display
        // order, without having to re-derive labels / icons from DOM.
        _railOverflowCatalog = modes.slice();

        // Brand mark dropped — topbar already carries the Bowire
        // wordmark + logo as the workbench-identity anchor;
        // duplicating it on the rail is just visual repetition.
        // Discover stays the default mode and is reachable as the
        // first icon below.

        var lastGroup = null;
        // Build mode buttons first; sidebar-toggle goes at the
        // bottom once the loop completes (rendered separately so it
        // sits below the mode list with margin-top: auto via CSS).
        modes.forEach(function (m) {
            if (lastGroup !== null && m.group !== lastGroup) {
                rail.appendChild(el('div', {
                    className: 'bowire-rail-divider',
                    'data-rail-divider-after': m.id
                }));
            }
            lastGroup = m.group;
            var isActive = railMode === m.id;
            var modeHasSidebar = m.sidebar && m.sidebar.kind !== 'none';
            var count = _railModeCount(m.id);
            var hasCount = typeof count === 'number' && count > 0;
            rail.appendChild(el('button', {
                type: 'button',
                'data-rail-mode-id': m.id,
                className: 'bowire-rail-btn' + (isActive ? ' active' : ''),
                // Pattern B — double-click on the ACTIVE mode toggles
                // the sidebar. Tooltip-suffix only shows the hint on
                // the active button so non-active modes don't lie
                // ('Double-click to collapse' would do nothing on a
                // mode the user isn't in).
                title: m.label
                    + (hasCount ? ' (' + count + ')' : '')
                    + (isActive && modeHasSidebar
                        ? '\n(double-click to ' + (sidebarCollapsed ? 'expand' : 'collapse') + ' the sidebar)'
                        : ''),
                'aria-label': m.label + (hasCount ? ', ' + count + ' items' : ''),
                onClick: function () {
                    if (railMode === m.id) return;
                    railMode = m.id;
                    try { localStorage.setItem('bowire_rail_mode', m.id); } catch { /* ignore */ }
                    // Modes that piggyback on the legacy
                    // sidebarView state (Environments, Flows, Proxy,
                    // Discover) also flip sidebarView so the existing
                    // main-pane routing picks up the right editor.
                    // Leaving it untouched would keep the request/
                    // response layout while the rail shows the
                    // mode-specific surface active.
                    if (m.id === 'environments') {
                        sidebarView = 'environments';
                    } else if (m.id === 'flows') {
                        sidebarView = 'flows';
                    } else if (m.id === 'proxy') {
                        sidebarView = 'proxy';
                    } else if (m.id === 'discover') {
                        sidebarView = 'services';
                    }
                    render();
                },
                onDblClick: function () {
                    // Pattern B fires only when the user double-clicks
                    // the ALREADY-active mode. Double-clicking an
                    // inactive mode falls through onClick (which sets
                    // it active, sidebar opens at default) — that
                    // matches VS Code semantics.
                    if (railMode === m.id && modeHasSidebar) {
                        toggleSidebarCollapsed();
                    }
                }
            },
                el('span', { innerHTML: svgIcon(m.icon) }),
                hasCount ? el('span', {
                    className: 'bowire-rail-btn-badge',
                    textContent: count > 99 ? '99+' : String(count)
                }) : null
            ));
        });

        // #164 v3 — Overflow '…' button. Always rendered (hidden via
        // CSS unless _layoutActivityRail() decides at least one mode
        // doesn't fit). Sits between the last mode and Settings so
        // Settings stays anchored at the bottom whether or not any
        // modes overflow. Inner spans hold the icon + a count badge
        // for hidden-with-activity modes; the popover renders on click.
        var overflowBtn = el('button', {
            type: 'button',
            id: 'bowire-rail-overflow-btn',
            className: 'bowire-rail-btn bowire-rail-overflow-btn',
            title: 'More modes',
            'aria-label': 'More modes',
            onClick: function (e) {
                e.stopPropagation();
                railOverflowOpen = !railOverflowOpen;
                render();
            }
        },
            el('span', { innerHTML: svgIcon('grip') }),
            el('span', {
                id: 'bowire-rail-overflow-badge',
                className: 'bowire-rail-btn-badge',
                style: 'display:none'
            })
        );
        rail.appendChild(overflowBtn);

        // Popover with the hidden modes. Mounted as a sibling of the
        // overflow button (inside the rail's bounding box would let the
        // overflow: hidden rule clip it), so we anchor by absolute
        // position via CSS. Only rendered when railOverflowOpen is on
        // AND the layout pass actually flagged some modes as overflow.
        if (railOverflowOpen && _railOverflowHidden && _railOverflowHidden.length > 0) {
            var popover = el('div', {
                id: 'bowire-rail-overflow-popover',
                className: 'bowire-rail-overflow-popover',
                role: 'menu'
            });
            _railOverflowHidden.forEach(function (m) {
                var count = _railModeCount(m.id);
                var hasCount = typeof count === 'number' && count > 0;
                var isActive = railMode === m.id;
                popover.appendChild(el('button', {
                    type: 'button',
                    role: 'menuitem',
                    className: 'bowire-rail-overflow-popover-item' + (isActive ? ' active' : ''),
                    onClick: function () {
                        railOverflowOpen = false;
                        railMode = m.id;
                        try { localStorage.setItem('bowire_rail_mode', m.id); } catch { /* ignore */ }
                        if (m.id === 'environments') sidebarView = 'environments';
                        else if (m.id === 'flows') sidebarView = 'flows';
                        else if (m.id === 'proxy') sidebarView = 'proxy';
                        else if (m.id === 'discover') sidebarView = 'services';
                        render();
                    }
                },
                    el('span', {
                        className: 'bowire-rail-overflow-popover-icon',
                        innerHTML: svgIcon(m.icon)
                    }),
                    el('span', {
                        className: 'bowire-rail-overflow-popover-label',
                        textContent: m.label
                    }),
                    hasCount ? el('span', {
                        className: 'bowire-rail-overflow-popover-badge',
                        textContent: count > 99 ? '99+' : String(count)
                    }) : null
                ));
            });
            rail.appendChild(popover);
        }

        // Settings — anchored at the very bottom of the rail. Moved
        // out of the topbar ⋮ overflow into the rail per VS Code /
        // JetBrains convention; reachable from every mode without
        // going through a menu. The old sidebar-toggle that sat at
        // the rail bottom is gone — toggle now lives at the
        // wirkungsort (sidebar edge-chevron + header-chevron +
        // rail double-click + Ctrl+B).
        rail.appendChild(el('button', {
            type: 'button',
            className: 'bowire-rail-btn bowire-rail-settings',
            title: 'Settings',
            'aria-label': 'Settings',
            onClick: function () {
                if (typeof openSettings === 'function') openSettings();
            }
        },
            el('span', { innerHTML: svgIcon('settings') })
        ));

        return rail;
    }

    // #164 v3 — Activity rail overflow state. _railOverflowCatalog
    // mirrors the catalogue order so the layout pass can build the
    // popover from the same list the rail was rendered from.
    // _railOverflowHidden tracks the modes the layout pass decided
    // can't fit; cleared on every layout pass to avoid stale entries.
    var _railOverflowCatalog = [];
    var _railOverflowHidden = [];

    // Runs after every render() via the morphdom hook in render-env-auth.
    // Walks the mode buttons top-down, computes how many fit above the
    // Settings anchor (with room reserved for the overflow button when
    // anything would overflow), hides the rest via display:none, and
    // populates _railOverflowHidden so the next render with
    // railOverflowOpen=true can build the popover. Idempotent — running
    // it multiple times in a row is a no-op once layout is stable.
    function _layoutActivityRail() {
        var rail = document.getElementById('bowire-activity-rail');
        if (!rail) return;
        var settings = rail.querySelector('.bowire-rail-settings');
        var overflowBtn = rail.querySelector('#bowire-rail-overflow-btn');
        if (!settings || !overflowBtn) return;

        // First pass: unhide everything so we measure natural heights.
        var modeBtns = rail.querySelectorAll('.bowire-rail-btn[data-rail-mode-id]');
        for (var i = 0; i < modeBtns.length; i++) modeBtns[i].style.display = '';
        overflowBtn.classList.remove('has-overflow');

        var railHeight = rail.clientHeight;
        var settingsHeight = settings.getBoundingClientRect().height || 44;
        var overflowHeight = 44; // reserved when we actually need the button
        var dividers = rail.querySelectorAll('.bowire-rail-divider');
        var dividerHeight = dividers.length > 0 ? (dividers[0].getBoundingClientRect().height || 1) : 1;

        // Build an in-order list of [el, height, modeId|null (null=divider)].
        var slots = [];
        var children = rail.children;
        for (var c = 0; c < children.length; c++) {
            var ch = children[c];
            if (ch === settings || ch === overflowBtn) continue;
            if (ch.id === 'bowire-rail-overflow-popover') continue;
            if (ch.classList.contains('bowire-rail-divider')) {
                slots.push({ el: ch, h: dividerHeight, modeId: null });
            } else if (ch.dataset && ch.dataset.railModeId) {
                slots.push({ el: ch, h: ch.getBoundingClientRect().height || 44, modeId: ch.dataset.railModeId });
            }
        }

        var available = railHeight - settingsHeight;
        var natural = slots.reduce(function (a, s) { return a + s.h; }, 0);
        if (natural <= available) {
            _railOverflowHidden = [];
            overflowBtn.classList.remove('has-overflow');
            var badge = document.getElementById('bowire-rail-overflow-badge');
            if (badge) badge.style.display = 'none';
            return;
        }

        // Doesn't fit — reserve space for the overflow button itself,
        // then walk slots top-down until we exceed the budget.
        var budget = available - overflowHeight;
        var consumed = 0;
        var hiddenIds = [];
        var lastVisibleDivider = -1;
        var allHiddenAfter = false;
        for (var i2 = 0; i2 < slots.length; i2++) {
            var slot = slots[i2];
            if (allHiddenAfter) {
                slot.el.style.display = 'none';
                if (slot.modeId) hiddenIds.push(slot.modeId);
                continue;
            }
            if (consumed + slot.h > budget) {
                slot.el.style.display = 'none';
                if (slot.modeId) hiddenIds.push(slot.modeId);
                allHiddenAfter = true;
                continue;
            }
            consumed += slot.h;
            slot.el.style.display = '';
            if (slot.modeId == null) lastVisibleDivider = i2;
        }

        // Drop a trailing visible divider (its group's content is all
        // overflowed) so the rail doesn't end on a stray line.
        if (lastVisibleDivider >= 0) {
            var trailing = true;
            for (var j = lastVisibleDivider + 1; j < slots.length; j++) {
                if (slots[j].el.style.display !== 'none' && slots[j].modeId) {
                    trailing = false;
                    break;
                }
            }
            if (trailing) slots[lastVisibleDivider].el.style.display = 'none';
        }

        _railOverflowHidden = hiddenIds.map(function (id) {
            return _railOverflowCatalog.find(function (m) { return m.id === id; });
        }).filter(function (m) { return !!m; });

        overflowBtn.classList.add('has-overflow');

        // Aggregate badge — sum any per-mode counts on hidden modes so
        // the operator sees there's activity tucked behind the '…'.
        var aggregate = 0;
        for (var k = 0; k < _railOverflowHidden.length; k++) {
            var c2 = _railModeCount(_railOverflowHidden[k].id);
            if (typeof c2 === 'number' && c2 > 0) aggregate += c2;
        }
        var badge2 = document.getElementById('bowire-rail-overflow-badge');
        if (badge2) {
            if (aggregate > 0) {
                badge2.style.display = '';
                badge2.textContent = aggregate > 99 ? '99+' : String(aggregate);
            } else {
                badge2.style.display = 'none';
            }
        }
    }

    // #133 Phase 2 — Recordings rail mode sidebar. Lists every
    // recorded session as a clickable row; selecting one swaps the
    // main pane to its detail (steps + actions toolbar). Replaces
    // the manager-modal entry path for users on the recordings
    // mode. The legacy modal still works for users still on
    // Discover (#56 — kept for backward muscle memory; deprecated
    // in Phase 3).
    function renderRecordingsSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });

        // Per-row context menu = same actions the hover-reveal
        // exposes, in the same order. Both surfaces wired against
        // this single source so they can't drift apart.
        function _buildRecordingContextMenu(rec) {
            var hasSteps = (Array.isArray(rec.steps) && rec.steps.length > 0)
                || (Array.isArray(rec.stepsManifest) && rec.stepsManifest.length > 0)
                || (typeof rec.stepCount === 'number' && rec.stepCount > 0);
            var items = [];
            items.push({
                label: 'Replay',
                disabled: !hasSteps,
                onClick: function () {
                    if (typeof replayRecording === 'function') replayRecording(rec.id);
                }
            });
            items.push({
                label: rec.id === recordingActiveId ? 'Stop recording' : 'Continue recording',
                onClick: function () {
                    if (rec.id === recordingActiveId) {
                        if (typeof stopRecording === 'function') stopRecording();
                    } else {
                        if (typeof continueRecording === 'function') continueRecording(rec.id);
                    }
                }
            });
            items.push({ separator: true });
            items.push({
                label: 'Rename…',
                onClick: function () {
                    bowirePrompt('Recording name', {
                        title: 'Rename recording',
                        defaultValue: rec.name || '',
                        confirmText: 'Save'
                    }).then(function (newName) {
                        if (!newName) return;
                        if (typeof renameRecording === 'function') renameRecording(rec.id, newName);
                    });
                }
            });
            items.push({
                label: 'Use as mock',
                disabled: !hasSteps,
                onClick: function () {
                    if (typeof useRecordingAsMock === 'function') useRecordingAsMock(rec);
                }
            });
            items.push({
                label: 'Convert to collection',
                disabled: !hasSteps || typeof recordingToCollectionItems !== 'function',
                onClick: function () {
                    if (typeof recordingToCollectionItems !== 'function') return;
                    var items2 = recordingToCollectionItems(rec);
                    if (items2.length === 0) { toast('No unary steps to convert', 'error'); return; }
                    if (typeof createCollection !== 'function' || typeof addToCollection !== 'function') return;
                    var col = createCollection((rec.name || 'Recording') + ' (from recording)');
                    items2.forEach(function (it) { addToCollection(col.id, it); });
                    collectionsList = loadCollections();
                    collectionManagerSelectedId = col.id;
                    toast('Created collection (' + items2.length + ' items)', 'success');
                    render();
                }
            });
            items.push({ separator: true });
            items.push({
                label: 'Delete',
                danger: true,
                onClick: function () {
                    if (typeof deleteRecording === 'function') deleteRecording(rec.id);
                }
            });
            return items;
        }

        // Bulk handlers — same projection regardless of whether they
        // were triggered from the selection toolbar or the per-row
        // ⋮ menu later.
        function _bulkDelete(ids) {
            var removed = [];
            ids.forEach(function (rid) {
                var idx = recordingsList.findIndex(function (r) { return r.id === rid; });
                if (idx < 0) return;
                removed.push({ entry: recordingsList[idx], originalIdx: idx, deletedAt: Date.now() });
                recordingsList.splice(idx, 1);
                if (recordingManagerSelectedId === rid) recordingManagerSelectedId = null;
                if (recordingActiveId === rid) recordingActiveId = null;
            });
            for (var k = removed.length - 1; k >= 0; k--) recordingsTrash.unshift(removed[k]);
            recordingsSelected.clear();
            recordingsSelectionAnchor = null;
            persistRecordings();
            persistRecordingsTrash();
            toast(removed.length + ' recording' + (removed.length === 1 ? '' : 's') + ' moved to trash', 'success', {
                undo: function () {
                    for (var u = 0; u < removed.length; u++) {
                        var t = removed[u];
                        recordingsList.splice(Math.min(t.originalIdx, recordingsList.length), 0, t.entry);
                        var tIdx = recordingsTrash.findIndex(function (x) { return x.entry && x.entry.id === t.entry.id; });
                        if (tIdx >= 0) recordingsTrash.splice(tIdx, 1);
                    }
                    persistRecordings();
                    persistRecordingsTrash();
                    render();
                }
            });
            render();
        }
        function _bulkConvertToCollection(ids) {
            if (typeof createCollection !== 'function' || typeof addToCollection !== 'function') return;
            var picks = ids
                .map(function (id) { return recordingsList.find(function (r) { return r.id === id; }); })
                .filter(Boolean);
            var allItems = [];
            picks.forEach(function (rec) {
                allItems = allItems.concat(recordingToCollectionItems(rec));
            });
            if (allItems.length === 0) {
                toast('No unary steps to convert', 'error');
                return;
            }
            var name = picks.length === 1
                ? (picks[0].name + ' (from recording)')
                : (picks.length + ' recordings → collection');
            var col = createCollection(name);
            allItems.forEach(function (it) { addToCollection(col.id, it); });
            collectionsList = loadCollections();
            collectionManagerSelectedId = col.id;
            recordingsSelected.clear();
            recordingsSelectionAnchor = null;
            toast('Created collection "' + name + '" (' + allItems.length + ' items)', 'success');
            render();
        }

        // Unified sidebar toolbar (#143 / sidebar-consistency pass).
        // Default state offers Start/Stop primary + Refresh +
        // overflow; selection state swaps to bulk Delete / Convert
        // to collection + Clear.
        sidebar.appendChild(renderSidebarToolbar({
            title: 'Recordings',
            primary: {
                icon: isRecording() ? 'square' : 'record',
                title: isRecording() ? 'Stop the current recording' : 'Start a new recording',
                onClick: function () {
                    if (isRecording()) stopRecording();
                    else startRecording();
                    if (recordingActiveId) recordingManagerSelectedId = recordingActiveId;
                    render();
                }
            },
            overflow: recordingsList.length > 0 ? [
                {
                    label: 'Move all to trash',
                    danger: true,
                    onClick: function () {
                        var n = recordingsList.length;
                        bowireConfirm(
                            'Move all ' + n + ' recordings to trash?',
                            function () {
                                var removed = recordingsList.map(function (r, idx) {
                                    return { entry: r, originalIdx: idx, deletedAt: Date.now() };
                                });
                                recordingsList.length = 0;
                                recordingManagerSelectedId = null;
                                recordingActiveId = null;
                                for (var k = removed.length - 1; k >= 0; k--) recordingsTrash.unshift(removed[k]);
                                persistRecordings();
                                persistRecordingsTrash();
                                toast(removed.length + ' recordings moved to trash', 'success');
                                render();
                            },
                            { title: 'Move all to trash', confirmText: 'Move ' + n, danger: true }
                        );
                    }
                }
            ] : null,
            selection: recordingsSelected.size > 0 ? {
                count: recordingsSelected.size,
                actions: [
                    {
                        icon: 'folder',
                        title: 'Convert selected recordings into a new collection',
                        onClick: function () {
                            _bulkConvertToCollection(Array.from(recordingsSelected));
                        }
                    },
                    {
                        icon: 'trash',
                        danger: true,
                        title: 'Move selected to trash',
                        onClick: function () {
                            _bulkDelete(Array.from(recordingsSelected));
                        }
                    }
                ],
                onClear: function () {
                    recordingsSelected.clear();
                    recordingsSelectionAnchor = null;
                    render();
                }
            } : null
        }));

        // List
        if (recordingsList.length === 0) {
            // Sidebar shows the empty state as a single low-noise hint;
            // the full call-to-action lives in the main pane so the
            // operator doesn't read "No recordings yet · Start recording"
            // twice — once on the left, once in the middle.
            sidebar.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No recordings yet.'
            }));
        } else {
            var list = el('div', { id: 'bowire-recordings-list', className: 'bowire-env-list' });
            var recIds = recordingsList.map(function (r) { return r.id; });
            recordingsList.forEach(function (rec, idx) {
                // #144 Phase 1.8 — for manifest-only recordings the
                // steps[] array is empty until hydration; read step
                // count from stepCount (written by the backend) or
                // stepsManifest.length as a fallback.
                var stepN = 0;
                if (rec.steps && rec.steps.length) stepN = rec.steps.length;
                else if (typeof rec.stepCount === 'number') stepN = rec.stepCount;
                else if (Array.isArray(rec.stepsManifest)) stepN = rec.stepsManifest.length;

                list.appendChild(renderSidebarListItem({
                    id: 'bowire-rec-row-' + rec.id,
                    icon: 'filmstrip',
                    name: rec.name,
                    meta: stepN + ' step' + (stepN === 1 ? '' : 's')
                        + (rec.id === recordingActiveId ? ' · ● recording' : ''),
                    active: recordingManagerSelectedId === rec.id,
                    selected: recordingsSelected.has(rec.id),
                    onContextMenu: function (e) {
                        e.preventDefault();
                        e.stopPropagation();
                        if (typeof showContextMenu === 'function') {
                            showContextMenu(e.clientX, e.clientY, _buildRecordingContextMenu(rec));
                        }
                    },
                    onClick: function (e) {
                        // #143 Phase 3 — modifier-aware click handling.
                        var isMod = applyListSelectionClick(recordingsSelected, recordingsSelectionAnchor, recIds, idx, e);
                        if (isMod) {
                            recordingsSelectionAnchor = idx;
                        } else {
                            recordingsSelected.clear();
                            recordingsSelectionAnchor = idx;
                            recordingManagerSelectedId = rec.id;
                            if (typeof hydrateRecording === 'function'
                                && (!Array.isArray(rec.steps) || rec.steps.length === 0)
                                && Array.isArray(rec.stepsManifest)
                                && rec.stepsManifest.length > 0) {
                                hydrateRecording(rec).then(function () { render(); });
                            }
                        }
                        render();
                    },
                    deleteTitle: 'Delete recording',
                    onDelete: function () {
                        var dIdx = recordingsList.indexOf(rec);
                        if (dIdx < 0) return;
                        var backup = recordingsList[dIdx];
                        recordingsList.splice(dIdx, 1);
                        if (recordingManagerSelectedId === rec.id) recordingManagerSelectedId = null;
                        if (recordingActiveId === rec.id) recordingActiveId = null;
                        recordingsTrash.unshift({ entry: backup, deletedAt: Date.now(), originalIdx: dIdx });
                        persistRecordings();
                        persistRecordingsTrash();
                        toast('Recording moved to trash', 'success', {
                            undo: function () {
                                var t = recordingsTrash.shift();
                                if (t) {
                                    recordingsList.splice(Math.min(dIdx, recordingsList.length), 0, t.entry);
                                    persistRecordings();
                                    persistRecordingsTrash();
                                    render();
                                }
                            }
                        });
                        render();
                    }
                }));
            });
            sidebar.appendChild(list);
        }

        // #143 Phase 2 — Recently deleted recordings (30-day TTL).
        sidebar.appendChild(renderTrashSection({
            trashArray: recordingsTrash,
            isOpen: recordingsTrashOpen,
            setOpen: function (v) { recordingsTrashOpen = v; },
            restoreAt: function (t) {
                recordingsList.splice(Math.min(t.originalIdx || recordingsList.length, recordingsList.length), 0, t.entry);
                persistRecordings();
            },
            persist: function () { persistRecordingsTrash(); },
            nameOf: function (e) { return e && e.name ? e.name : '(unnamed recording)'; }
        }));

        return sidebar;
    }

    // #152 — Sources rail mode sidebar. Lists every configured
    // discovery URL as a clickable row; the right pane shows the
    // selected URL's discovery state + headers + schema imports.
    function renderSourcesSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });

        sidebar.appendChild(renderSidebarToolbar({
            title: 'Sources',
            primary: !config.lockServerUrl ? {
                icon: 'plus',
                title: 'Add a discovery URL',
                onClick: function () {
                    bowirePrompt('Add discovery URL', {
                        title: 'New source',
                        placeholder: 'rest@https://… / graphql@…  or plain https://…',
                        confirmText: 'Add',
                    }).then(function (raw) {
                        if (!raw) return;
                        if (typeof addServerUrl === 'function') addServerUrl(raw);
                        else if (typeof serverUrls !== 'undefined' && serverUrls.indexOf(raw) < 0) {
                            serverUrls.push(raw);
                            if (typeof persistServerUrls === 'function') persistServerUrls();
                        }
                        sourcesSelectedUrl = raw;
                        if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                        render();
                    });
                }
            } : null
        }));

        if (!serverUrls || serverUrls.length === 0) {
            // Empty-state copy + "Add URL" call-to-action live in the
            // main pane so the sidebar doesn't shout the same thing.
            sidebar.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No URLs yet.'
            }));
            return sidebar;
        }

        if (!sourcesSelectedUrl || serverUrls.indexOf(sourcesSelectedUrl) < 0) {
            sourcesSelectedUrl = serverUrls[0];
        }

        // #152 v3 — selection-mode header swap (same #143 shape as
        // recordings/collections). 'N selected' bar with bulk
        // delete + clear.
        if (sourcesSelected.size > 0) {
            var selHeader = el('div', { className: 'bowire-env-list-header bowire-selection-header' },
                el('span', { className: 'bowire-selection-count', textContent: sourcesSelected.size + ' selected' }),
                el('button', {
                    className: 'bowire-selection-action bowire-selection-action-danger',
                    title: 'Remove selected URLs',
                    onClick: function () {
                        var urls = Array.from(sourcesSelected);
                        urls.forEach(function (uu) {
                            if (typeof removeServerUrl === 'function') removeServerUrl(uu);
                            else {
                                var ri = serverUrls.indexOf(uu);
                                if (ri >= 0) serverUrls.splice(ri, 1);
                            }
                            removeUrlMeta(uu);
                        });
                        if (typeof persistServerUrls === 'function') persistServerUrls();
                        if (sourcesSelected.has(sourcesSelectedUrl)) {
                            sourcesSelectedUrl = serverUrls[0] || null;
                        }
                        sourcesSelected.clear();
                        sourcesSelectionAnchor = null;
                        if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                        toast(urls.length + ' URL' + (urls.length === 1 ? '' : 's') + ' removed', 'success');
                        render();
                    }
                }, el('span', { innerHTML: svgIcon('trash'), style: 'width:13px;height:13px;display:inline-flex' })),
                el('button', {
                    className: 'bowire-selection-action',
                    title: 'Clear selection (Esc)',
                    textContent: '×',
                    onClick: function () {
                        sourcesSelected.clear();
                        sourcesSelectionAnchor = null;
                        render();
                    }
                })
            );
            sidebar.replaceChild(selHeader, sidebar.firstChild);
        } else if (serverUrls.length > 0 && !config.lockServerUrl) {
            // ⋮ overflow on the normal header for 'Remove all'.
            var existingHeader = sidebar.firstChild;
            existingHeader.appendChild(el('button', {
                className: 'bowire-env-add-btn',
                title: 'More actions',
                'aria-label': 'More actions',
                textContent: '⋮',
                onClick: function () {
                    var n = serverUrls.length;
                    bowireConfirm(
                        'Remove all ' + n + ' URLs from this workspace?',
                        function () {
                            serverUrls.length = 0;
                            urlHeaders = {};
                            urlMeta = {};
                            if (typeof persistServerUrls === 'function') persistServerUrls();
                            persistUrlHeaders();
                            persistUrlMeta();
                            sourcesSelectedUrl = null;
                            sourcesSelected.clear();
                            if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                            toast(n + ' URLs removed', 'success');
                            render();
                        },
                        { title: 'Remove all URLs', confirmText: 'Remove ' + n, danger: true }
                    );
                }
            }));
        }

        var list = el('div', { id: 'bowire-sources-list', className: 'bowire-env-list' });
        var urlIds = serverUrls.slice();
        serverUrls.forEach(function (u, idx) {
            var isSelected = u === sourcesSelectedUrl;
            var isMultiSelected = sourcesSelected.has(u);
            var status = (typeof connectionStatuses === 'object' && connectionStatuses)
                ? (connectionStatuses[u] || 'disconnected') : 'disconnected';
            // Fixed #5 — count via urlMatchesService so prefixed +
            // resolved URL variants both match.
            var svcN = (typeof services !== 'undefined')
                ? services.filter(function (s) { return urlMatchesService(u, s); }).length : 0;
            var meta = (typeof getUrlMeta === 'function') ? getUrlMeta(u) : {};
            var displayName = meta.name || _stripUrlPrefix(u);
            var proto = (typeof getUrlProtocolHint === 'function') ? getUrlProtocolHint(u) : null;
            var dotColor = meta.color || null;
            var row = el('div', {
                id: 'bowire-src-row-' + idx,
                className: 'bowire-env-list-item bowire-sources-list-item'
                    + (isSelected ? ' selected' : '')
                    + (isMultiSelected ? ' bowire-multi-selected' : ''),
                title: u,
                onClick: function (e) {
                    var isMod = applyListSelectionClick(sourcesSelected, sourcesSelectionAnchor, urlIds, idx, e);
                    if (isMod) {
                        sourcesSelectionAnchor = idx;
                    } else {
                        sourcesSelected.clear();
                        sourcesSelectionAnchor = idx;
                        sourcesSelectedUrl = u;
                    }
                    render();
                }
            },
                // Protocol icon — #8 — show the protocol-glyph
                // when we can derive it from the URL prefix.
                el('span', {
                    className: 'bowire-sources-list-item-proto',
                    title: proto || 'unknown protocol',
                    innerHTML: proto ? svgIcon(_protoIconName(proto)) : ''
                }),
                el('span', {
                    className: 'bowire-conn-pill-dot bowire-conn-pill-dot-' + status,
                    style: dotColor ? ('background:' + dotColor + ';') : '',
                }),
                el('span', { className: 'bowire-env-list-item-name', textContent: displayName }),
                el('span', { style: 'flex:1' }),
                el('span', { className: 'bowire-env-list-item-meta', textContent: svcN + ' svc' + (svcN === 1 ? '' : 's') }),
                !config.lockServerUrl ? el('button', {
                    type: 'button',
                    className: 'bowire-list-row-delete',
                    title: 'Remove URL',
                    'aria-label': 'Remove URL',
                    innerHTML: svgIcon('trash'),
                    onClick: function (e) {
                        e.stopPropagation();
                        if (typeof removeServerUrl === 'function') removeServerUrl(u);
                        else {
                            var ri = serverUrls.indexOf(u);
                            if (ri >= 0) serverUrls.splice(ri, 1);
                            if (typeof persistServerUrls === 'function') persistServerUrls();
                        }
                        removeUrlMeta(u);
                        if (sourcesSelectedUrl === u) sourcesSelectedUrl = serverUrls[0] || null;
                        if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                        render();
                    }
                }) : null
            );
            list.appendChild(row);
        });
        sidebar.appendChild(list);
        return sidebar;
    }

    // Map an unprefixed protocol name to an SVG icon name. Falls
    // back to plug when unknown.
    function _protoIconName(proto) {
        var p = String(proto || '').toLowerCase();
        if (p === 'rest' || p === 'http') return 'globe';
        if (p === 'graphql') return 'beaker';
        if (p === 'grpc') return 'lightning';
        if (p === 'mqtt' || p === 'amqp') return 'flow';
        if (p === 'signalr' || p === 'websocket' || p === 'ws') return 'flow';
        if (p === 'mcp') return 'bot';
        return 'plug';
    }

    // Right-click context menu on a workspace tree row. Renders a
    // floating, pointer-anchored menu with the same shape as the topbar
    // chip dropdown — Switch / Rename / Edit / Delete / New — so the
    // operator gets the same actions from either entry point. Closes on
    // Esc, outside-click, or an action firing. Single global pointer:
    // a second right-click replaces the previous menu instead of
    // stacking N copies on top of each other.
    var _activeWorkspaceContextMenu = null;
    function _closeWorkspaceContextMenu() {
        if (!_activeWorkspaceContextMenu) return;
        var m = _activeWorkspaceContextMenu;
        _activeWorkspaceContextMenu = null;
        m.close();
    }
    // w === null is the "empty tree area" case — only the "+ New
    // workspace…" item is rendered. Used by the Workspaces rail
    // sidebar's right-click-on-empty-space hook so the operator can
    // create a new workspace without finding a row to click on first.
    // Generic floating context menu for tree sub-items in the Workspaces
    // rail (Sources / Environments / Variables / Collections / Recordings
    // / Settings rows). Same CSS chrome as openWorkspaceContextMenu so
    // the right-click experience reads consistent across every row. The
    // caller passes a flat item list; each item: { icon, label, danger?,
    // disabled?, onClick }. null entries are skipped so callers can use
    // inline conditional items.
    function openTreeSubContextMenu(e, items) {
        if (!e || !Array.isArray(items)) return;
        _closeWorkspaceContextMenu();
        var menu = document.createElement('div');
        menu.className = 'bowire-workspace-menu bowire-workspace-context-menu';
        menu.setAttribute('role', 'menu');
        menu.style.position = 'fixed';
        menu.style.left = (e.clientX || 0) + 'px';
        menu.style.top = (e.clientY || 0) + 'px';
        menu.style.zIndex = '10000';

        var sawAny = false;
        items.forEach(function (it) {
            if (!it) return;
            if (it === 'divider') {
                var d = document.createElement('div');
                d.className = 'bowire-workspace-menu-divider';
                menu.appendChild(d);
                return;
            }
            sawAny = true;
            var item = document.createElement('div');
            item.className = 'bowire-workspace-menu-item bowire-workspace-menu-item-action'
                + (it.danger ? ' bowire-workspace-menu-item-danger' : '')
                + (it.disabled ? ' is-disabled' : '');
            item.setAttribute('role', 'menuitem');
            var icon = document.createElement('span');
            icon.className = 'bowire-workspace-menu-item-icon';
            icon.textContent = it.icon || '';
            item.appendChild(icon);
            var label = document.createElement('span');
            label.textContent = it.label;
            item.appendChild(label);
            if (!it.disabled) {
                item.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    _closeWorkspaceContextMenu();
                    try { it.onClick(); } catch (err) { console.error('[bowire-tree-ctx] action threw', err); }
                });
            }
            menu.appendChild(item);
        });
        if (!sawAny) return;

        document.body.appendChild(menu);
        var vw = window.innerWidth, vh = window.innerHeight;
        var rect = menu.getBoundingClientRect();
        if (rect.right > vw) menu.style.left = Math.max(8, vw - rect.width - 8) + 'px';
        if (rect.bottom > vh) menu.style.top = Math.max(8, vh - rect.height - 8) + 'px';

        function onKeyDown(ev) {
            if (ev.key === 'Escape') { ev.preventDefault(); _closeWorkspaceContextMenu(); }
        }
        function onDocClick(ev) {
            if (!menu.contains(ev.target)) _closeWorkspaceContextMenu();
        }
        document.addEventListener('keydown', onKeyDown);
        setTimeout(function () { document.addEventListener('click', onDocClick); }, 0);

        _activeWorkspaceContextMenu = {
            element: menu,
            close: function () {
                document.removeEventListener('keydown', onKeyDown);
                document.removeEventListener('click', onDocClick);
                if (menu.parentNode) menu.parentNode.removeChild(menu);
            }
        };
    }

    function openWorkspaceContextMenu(w, e) {
        if (!e) return;
        _closeWorkspaceContextMenu();

        var isActive = w && w.id === activeWorkspaceId;
        var menu = document.createElement('div');
        menu.className = 'bowire-workspace-menu bowire-workspace-context-menu';
        menu.setAttribute('role', 'menu');
        menu.style.position = 'fixed';
        menu.style.left = (e.clientX || 0) + 'px';
        menu.style.top = (e.clientY || 0) + 'px';
        menu.style.zIndex = '10000';
        // The chip dropdown's positioning relies on the topbar group's
        // position:relative; the rail context-menu floats on document.body
        // so the topbar's offset doesn't matter. Pin top/left explicitly.
        menu.style.removeProperty('right');

        function addItem(opts) {
            var item = document.createElement('div');
            item.className = 'bowire-workspace-menu-item bowire-workspace-menu-item-action'
                + (opts.danger ? ' bowire-workspace-menu-item-danger' : '')
                + (opts.disabled ? ' is-disabled' : '');
            item.setAttribute('role', 'menuitem');
            var icon = document.createElement('span');
            icon.className = 'bowire-workspace-menu-item-icon';
            icon.textContent = opts.icon || '';
            item.appendChild(icon);
            var label = document.createElement('span');
            label.textContent = opts.label;
            item.appendChild(label);
            if (!opts.disabled) {
                item.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    _closeWorkspaceContextMenu();
                    try { opts.onClick(); } catch (err) { console.error('[bowire-workspace-ctx] action threw', err); }
                });
            }
            menu.appendChild(item);
        }
        function addDivider() {
            var d = document.createElement('div');
            d.className = 'bowire-workspace-menu-divider';
            menu.appendChild(d);
        }

        if (w) {
            addItem({
                icon: isActive ? '✓' : '→',
                label: isActive ? 'Active workspace' : 'Switch to this workspace',
                disabled: isActive,
                onClick: function () { switchWorkspace(w.id); }
            });
            addItem({
                icon: '✎',
                label: 'Rename…',
                onClick: function () {
                    var oldName = w.name;
                    bowirePrompt('Rename workspace', {
                        title: 'Rename',
                        defaultValue: oldName,
                        confirmText: 'Rename',
                        validator: function (val) {
                            var trimmed = String(val || '').trim();
                            if (!trimmed) return 'Name required';
                            if (trimmed.toLowerCase() === String(oldName || '').trim().toLowerCase()) return null;
                            if (typeof _isWorkspaceNameTaken === 'function'
                                && _isWorkspaceNameTaken(trimmed, w.id)) {
                                if (typeof toast === 'function') {
                                    toast('A workspace named "' + trimmed + '" already exists.', 'error');
                                }
                                return 'Duplicate';
                            }
                            return null;
                        }
                    }).then(function (renamed) {
                        if (renamed) {
                            renameWorkspace(w.id, renamed);
                            render();
                        }
                    });
                }
            });
            addItem({
                icon: '⚙',
                label: 'Edit settings',
                onClick: function () {
                    workspacesSelectedId = w.id;
                    workspaceTreeSelection = { wsId: w.id, kind: 'workspace' };
                    railMode = 'workspaces';
                    try { localStorage.setItem('bowire_rail_mode', 'workspaces'); }
                    catch { /* ignore */ }
                    render();
                }
            });
            addItem({
                icon: '🗑',
                label: 'Delete',
                danger: true,
                onClick: function () {
                    var isLast = workspaces.length === 1;
                    var msg = isLast
                        ? 'Delete the last workspace "' + w.name + '"? You will return to the empty no-workspace state — the underlying URLs / envs / recordings for this workspace are removed.'
                        : 'Delete workspace "' + w.name + '"? The underlying URLs / envs / recordings for this workspace are removed.';
                    bowireConfirm(
                        msg,
                        function () { deleteWorkspace(w.id); render(); },
                        { title: 'Delete workspace', confirmText: 'Delete', danger: true }
                    );
                }
            });
            addDivider();
        }
        addItem({
            icon: '+',
            label: 'New workspace…',
            onClick: function () {
                openCreateWorkspaceDialog(function (created) {
                    if (created) {
                        workspacesSelectedId = created.id;
                        workspaceTreeSelection = { wsId: created.id, kind: 'workspace' };
                        render();
                    }
                });
            }
        });

        document.body.appendChild(menu);

        // Viewport-clamp the menu so it doesn't paint past the right/bottom
        // edge. Mirrors the same shift the browser uses for its own menus.
        var vw = window.innerWidth, vh = window.innerHeight;
        var rect = menu.getBoundingClientRect();
        if (rect.right > vw) {
            menu.style.left = Math.max(8, vw - rect.width - 8) + 'px';
        }
        if (rect.bottom > vh) {
            menu.style.top = Math.max(8, vh - rect.height - 8) + 'px';
        }

        function onKeyDown(ev) {
            if (ev.key === 'Escape') {
                ev.preventDefault();
                _closeWorkspaceContextMenu();
            }
        }
        function onDocClick(ev) {
            if (!menu.contains(ev.target)) _closeWorkspaceContextMenu();
        }
        document.addEventListener('keydown', onKeyDown);
        // Defer the outside-click binding by one tick so the originating
        // contextmenu event doesn't close the menu on its own opening tick.
        setTimeout(function () { document.addEventListener('click', onDocClick); }, 0);

        _activeWorkspaceContextMenu = {
            element: menu,
            close: function () {
                document.removeEventListener('keydown', onKeyDown);
                document.removeEventListener('click', onDocClick);
                if (menu.parentNode) menu.parentNode.removeChild(menu);
            }
        };
    }

    // #116 — Workspaces rail mode sidebar. Lists every workspace as
    // a clickable row; clicking selects (right pane shows detail);
    // double-click switches. Header has the same '+' new-workspace
    // affordance as the topbar chip's menu.
    // #192 — Workspaces rail as a navigable tree (MudBlazor-inspired).
    // Each workspace is a top-level node; expanding it surfaces
    // Sources (with URL leaves), Environments, Collections, Recordings,
    // and Settings as child rows. Click a leaf → main pane jumps to
    // that node's editor. Expansion state is persisted per node so
    // collapse survives a reload.
    function renderWorkspacesSidebar() {
        var sidebar = el('div', {
            id: 'bowire-sidebar',
            className: 'bowire-sidebar bowire-sidebar-mode',
            // Right-click on the empty area of the rail (anywhere not
            // covered by a tree row) opens a minimal context menu with
            // just "+ New workspace…" — so the operator doesn't need to
            // find a row to click on before they can create one.
            onContextMenu: function (e) {
                if (e.target && e.target.closest && e.target.closest('.bowire-tree-row')) return;
                e.preventDefault();
                if (typeof openWorkspaceContextMenu === 'function') {
                    openWorkspaceContextMenu(null, e);
                }
            }
        });

        sidebar.appendChild(renderSidebarToolbar({
            title: 'Workspaces',
            onTitleClick: function () {
                workspaceTreeSelection = { kind: 'workspaces-overview' };
                render();
            },
            titleClickTitle: 'Open Workspaces overview',
            primary: {
                icon: 'plus',
                title: 'Create new workspace',
                onClick: function () {
                    openCreateWorkspaceDialog(function (ws) {
                        workspacesSelectedId = ws.id;
                        workspaceTreeSelection = { wsId: ws.id, kind: 'workspace' };
                    });
                }
            }
        }));

        var wsIds = workspaces.map(function (w) { return w.id; });
        if (!workspacesSelectedId || wsIds.indexOf(workspacesSelectedId) < 0) {
            workspacesSelectedId = activeWorkspaceId;
        }
        if (!workspaceTreeSelection || !workspaceTreeSelection.wsId) {
            workspaceTreeSelection = { wsId: workspacesSelectedId, kind: 'workspace' };
        }

        var nodes = workspaces.map(function (w) {
            return _buildWorkspaceTreeNode(w);
        });
        sidebar.appendChild(renderTree(nodes, { ariaLabel: 'Workspaces' }));

        return sidebar;
    }

    function _buildWorkspaceTreeNode(w) {
        var isActive = w.id === activeWorkspaceId;
        var sel = workspaceTreeSelection || {};
        var wsExpandKey = 'ws:' + w.id;
        // Default-open the active workspace so the operator sees its
        // contents without an extra click. Other workspaces stay
        // collapsed unless the operator opened them before.
        var wsExpanded = isWorkspaceTreeNodeExpanded(wsExpandKey, isActive);
        var wsSelected = sel.wsId === w.id && sel.kind === 'workspace';

        // No 'Settings' sub-node — clicking the workspace row itself
        // already renders _renderWorkspaceSettingsDetail (the default
        // case in renderWorkspaceDetailMain). A duplicate Settings
        // child would just confuse the operator (the workspace IS the
        // settings entry-point) without aiding discovery.
        var children = [];
        children.push(_buildSourcesTreeNode(w));
        children.push(_buildEnvironmentsTreeNode(w));
        // Variables-leaf retired — workspace-scope variables + secrets
        // moved into tabs inside the workspace settings pane (General
        // / Variables / Secrets). Single-table data fits a tab, not a
        // tree leaf (the leaves are for entity LISTS like
        // sources/envs/collections/recordings).
        children.push(_buildCollectionsTreeNode(w));
        children.push(_buildRecordingsTreeNode(w));

        // Same Lucide 'layers' glyph as the topbar chip + dropdown rows
        // — top "leaf" picks up the workspace's chosen colour, lower
        // two layers inherit currentColor. Reads as "this is a
        // workspace, identified by its colour" across all three
        // workspace-pick surfaces.
        var wsColor = w.color || 'var(--bowire-accent)';
        var wsGlyph = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="14" height="14">'
            + '<polygon points="12 2 2 7 12 12 22 7 12 2" fill="' + wsColor + '" stroke="' + wsColor + '"/>'
            + '<polyline points="2 17 12 22 22 17"/>'
            + '<polyline points="2 12 12 17 22 12"/>'
            + '</svg>';

        return {
            id: wsExpandKey,
            label: w.name,
            iconHtml: wsGlyph,
            accent: w.color || 'var(--bowire-accent)',
            active: isActive,
            selected: wsSelected,
            expandable: true,
            expanded: wsExpanded,
            title: isActive ? w.name + ' (active workspace)' : w.name,
            onClick: function () {
                workspacesSelectedId = w.id;
                workspaceTreeSelection = { wsId: w.id, kind: 'workspace' };
                toggleWorkspaceTreeNode(wsExpandKey, isActive);
                render();
            },
            onToggle: function () {
                toggleWorkspaceTreeNode(wsExpandKey, isActive);
                render();
            },
            onContext: function (e) {
                if (typeof openWorkspaceContextMenu === 'function') {
                    openWorkspaceContextMenu(w, e);
                }
            },
            onAdd: !isActive ? function () {
                switchWorkspace(w.id);
            } : null,
            addTitle: !isActive ? 'Switch to this workspace' : null,
            onDrop: function (dt) { _handleWorkspaceDrop(w, dt); },
            children: children
        };
    }

    function _buildSourcesTreeNode(w) {
        var sel = workspaceTreeSelection || {};
        var sourcesKey = 'ws:' + w.id + ':sources';
        var urls = readWorkspaceUrls(w.id);
        var sourcesSelected = sel.wsId === w.id && sel.kind === 'sources';
        var sourcesExpanded = isWorkspaceTreeNodeExpanded(sourcesKey, sel.wsId === w.id);

        var urlChildren = urls.map(function (u) {
            var urlSelected = sel.wsId === w.id && sel.kind === 'url' && sel.value === u;
            return {
                id: 'ws:' + w.id + ':url:' + u,
                label: u,
                icon: 'plug',
                selected: urlSelected,
                title: u,
                onClick: function () {
                    workspacesSelectedId = w.id;
                    workspaceTreeSelection = { wsId: w.id, kind: 'url', value: u };
                    render();
                },
                onContext: function (ev) {
                    openTreeSubContextMenu(ev, [
                        {
                            icon: '🗑',
                            label: 'Remove URL',
                            danger: true,
                            onClick: function () {
                                bowireConfirm(
                                    'Remove URL "' + u + '" from this workspace? Stored headers + cached schema for this source are dropped.',
                                    function () {
                                        if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                                        if (typeof removeServerUrl === 'function') removeServerUrl(u);
                                        render();
                                    },
                                    { title: 'Remove URL', confirmText: 'Remove', danger: true }
                                );
                            }
                        }
                    ]);
                }
            };
        });

        return {
            id: sourcesKey,
            label: 'Sources',
            // server (rack) glyph for the backend-endpoint side — sources
            // are "what backends does this workspace talk to". Used to be
            // `globe`, which collided with Environments (deployment
            // contexts / stages) in the tree.
            icon: 'server',
            badge: urls.length || null,
            selected: sourcesSelected,
            expandable: true,
            expanded: sourcesExpanded,
            onClick: function () {
                workspacesSelectedId = w.id;
                workspaceTreeSelection = { wsId: w.id, kind: 'sources' };
                toggleWorkspaceTreeNode(sourcesKey, sel.wsId === w.id);
                render();
            },
            onToggle: function () {
                toggleWorkspaceTreeNode(sourcesKey, sel.wsId === w.id);
                render();
            },
            onAdd: function () {
                _quickAddUrlToWorkspace(w);
            },
            addTitle: 'Add URL or schema',
            onContext: function (ev) {
                openTreeSubContextMenu(ev, [
                    {
                        icon: '+',
                        label: 'Add URL or schema',
                        onClick: function () { _quickAddUrlToWorkspace(w); }
                    },
                    {
                        icon: '⬆',
                        label: 'Upload schema files…',
                        onClick: function () { _uploadSchemaFilesForWorkspace(w); }
                    }
                ]);
            },
            onDrop: function (dt) { _handleWorkspaceDrop(w, dt); },
            children: urlChildren
        };
    }

    function _buildCollectionsTreeNode(w) {
        var sel = workspaceTreeSelection || {};
        var key = 'ws:' + w.id + ':collections';
        var isActive = w.id === activeWorkspaceId;
        // Read collections directly so non-active workspaces still get
        // their list surfaced under the tree without a workspace switch.
        var cols = isActive
            ? ((typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) ? collectionsList : [])
            : (typeof readWorkspaceJsonList === 'function' ? readWorkspaceJsonList(w.id, COLLECTIONS_KEY) : []);
        var selected = sel.wsId === w.id && sel.kind === 'collections';
        var expanded = isWorkspaceTreeNodeExpanded(key, sel.wsId === w.id);

        var leafChildren = cols.map(function (c) {
            var leafSelected = sel.wsId === w.id && sel.kind === 'collection' && sel.value === c.id;
            return {
                id: 'ws:' + w.id + ':collection:' + c.id,
                label: c.name || '(unnamed)',
                icon: 'folder',
                badge: Array.isArray(c.items) && c.items.length > 0 ? c.items.length : null,
                selected: leafSelected,
                title: c.name || c.id,
                onClick: function () {
                    if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                    workspacesSelectedId = w.id;
                    workspaceTreeSelection = { wsId: w.id, kind: 'collection', value: c.id };
                    if (typeof collectionManagerSelectedId !== 'undefined') {
                        collectionManagerSelectedId = c.id;
                    }
                    render();
                },
                onContext: function (ev) {
                    openTreeSubContextMenu(ev, [
                        {
                            icon: '🗑',
                            label: 'Delete collection',
                            danger: true,
                            onClick: function () {
                                bowireConfirm(
                                    'Delete collection "' + (c.name || c.id) + '"? Items stored in this collection are removed.',
                                    function () {
                                        if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                                        if (typeof deleteCollection === 'function') deleteCollection(c.id);
                                        render();
                                    },
                                    { title: 'Delete collection', confirmText: 'Delete', danger: true }
                                );
                            }
                        }
                    ]);
                }
            };
        });

        return {
            id: key,
            label: 'Collections',
            icon: 'folder',
            badge: cols.length || null,
            selected: selected,
            expandable: true,
            expanded: expanded,
            onClick: function () {
                workspacesSelectedId = w.id;
                workspaceTreeSelection = { wsId: w.id, kind: 'collections' };
                toggleWorkspaceTreeNode(key, sel.wsId === w.id);
                render();
            },
            onToggle: function () {
                toggleWorkspaceTreeNode(key, sel.wsId === w.id);
                render();
            },
            onAdd: function () {
                if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                if (typeof createCollection !== 'function') return;
                var col = createCollection();
                if (typeof collectionManagerSelectedId !== 'undefined') {
                    collectionManagerSelectedId = col.id;
                }
                workspaceTreeSelection = { wsId: w.id, kind: 'collection', value: col.id };
                workspaceTreeExpanded[key] = true;
                persistWorkspaceTreeExpanded();
                render();
            },
            addTitle: 'New collection',
            onContext: function (ev) {
                openTreeSubContextMenu(ev, [
                    {
                        icon: '+',
                        label: 'New collection',
                        onClick: function () {
                            if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                            if (typeof createCollection !== 'function') return;
                            var col = createCollection();
                            if (typeof collectionManagerSelectedId !== 'undefined') {
                                collectionManagerSelectedId = col.id;
                            }
                            workspaceTreeSelection = { wsId: w.id, kind: 'collection', value: col.id };
                            workspaceTreeExpanded[key] = true;
                            persistWorkspaceTreeExpanded();
                            render();
                        }
                    }
                ]);
            },
            children: leafChildren
        };
    }

    function _buildRecordingsTreeNode(w) {
        var sel = workspaceTreeSelection || {};
        var key = 'ws:' + w.id + ':recordings';
        var isActive = w.id === activeWorkspaceId;
        var recs = isActive
            ? ((typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) ? recordingsList : [])
            : (typeof readWorkspaceJsonList === 'function' ? readWorkspaceJsonList(w.id, RECORDINGS_KEY) : []);
        var selected = sel.wsId === w.id && sel.kind === 'recordings';
        var expanded = isWorkspaceTreeNodeExpanded(key, sel.wsId === w.id);

        var leafChildren = recs.map(function (r) {
            var leafSelected = sel.wsId === w.id && sel.kind === 'recording' && sel.value === r.id;
            var stepCount = Array.isArray(r.steps) ? r.steps.length : 0;
            return {
                id: 'ws:' + w.id + ':recording:' + r.id,
                label: r.name || '(unnamed)',
                icon: 'filmstrip',
                badge: stepCount > 0 ? stepCount : null,
                selected: leafSelected,
                title: r.name || r.id,
                onClick: function () {
                    if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                    workspacesSelectedId = w.id;
                    workspaceTreeSelection = { wsId: w.id, kind: 'recording', value: r.id };
                    if (typeof recordingManagerSelectedId !== 'undefined') {
                        recordingManagerSelectedId = r.id;
                    }
                    render();
                },
                onContext: function (ev) {
                    openTreeSubContextMenu(ev, [
                        {
                            icon: '🗑',
                            label: 'Delete recording',
                            danger: true,
                            onClick: function () {
                                bowireConfirm(
                                    'Delete recording "' + (r.name || r.id) + '"? Captured steps are removed.',
                                    function () {
                                        if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                                        if (typeof deleteRecording === 'function') deleteRecording(r.id);
                                        render();
                                    },
                                    { title: 'Delete recording', confirmText: 'Delete', danger: true }
                                );
                            }
                        }
                    ]);
                }
            };
        });

        return {
            id: key,
            label: 'Recordings',
            icon: 'recording',
            badge: recs.length || null,
            selected: selected,
            expandable: true,
            expanded: expanded,
            onClick: function () {
                workspacesSelectedId = w.id;
                workspaceTreeSelection = { wsId: w.id, kind: 'recordings' };
                toggleWorkspaceTreeNode(key, sel.wsId === w.id);
                render();
            },
            onToggle: function () {
                toggleWorkspaceTreeNode(key, sel.wsId === w.id);
                render();
            },
            onAdd: function () {
                if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                if (typeof startRecording === 'function') {
                    startRecording();
                    railMode = 'discover';
                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                    render();
                }
            },
            addTitle: 'Start recording',
            onContext: function (ev) {
                openTreeSubContextMenu(ev, [
                    {
                        icon: '●',
                        label: 'Start recording',
                        onClick: function () {
                            if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                            if (typeof startRecording === 'function') {
                                startRecording();
                                railMode = 'discover';
                                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                render();
                            }
                        }
                    }
                ]);
            },
            children: leafChildren
        };
    }

    function _buildEnvironmentsTreeNode(w) {
        var sel = workspaceTreeSelection || {};
        var key = 'ws:' + w.id + ':environments';
        var isActive = w.id === activeWorkspaceId;
        // Self-contained workspaces: read directly from the workspace's
        // own env bucket. For the active workspace getEnvironments() is
        // already keyed off it; for others readWorkspaceEnvironments
        // hits the wsKeyFor-prefixed key without switching active.
        var envs = isActive
            ? ((typeof getEnvironments === 'function') ? getEnvironments() : [])
            : ((typeof readWorkspaceEnvironments === 'function') ? readWorkspaceEnvironments(w.id) : []);

        var selected = sel.wsId === w.id && sel.kind === 'environments';
        var expanded = isWorkspaceTreeNodeExpanded(key, sel.wsId === w.id);

        var leafChildren = envs.map(function (e) {
            var leafSelected = sel.wsId === w.id && sel.kind === 'env' && sel.value === e.id;
            var varCount = e.vars ? Object.keys(e.vars).length : 0;
            // Filled-globe glyph in the env's chosen colour — matches
            // the topbar env-dropdown idiom so the env's identity reads
            // consistently in both pick surfaces.
            var envColor = e.color || 'var(--bowire-text-tertiary)';
            var envGlyph = '<svg viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" width="14" height="14">'
                + '<circle cx="12" cy="12" r="10" fill="' + envColor + '"/>'
                + '<line x1="2" y1="12" x2="22" y2="12" fill="none"/>'
                + '<path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z" fill="none"/>'
                + '</svg>';
            return {
                id: 'ws:' + w.id + ':env:' + e.id,
                label: e.name || '(unnamed)',
                iconHtml: envGlyph,
                badge: varCount > 0 ? varCount : null,
                selected: leafSelected,
                title: e.name || e.id,
                onClick: function () {
                    if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                    workspacesSelectedId = w.id;
                    workspaceTreeSelection = { wsId: w.id, kind: 'env', value: e.id };
                    if (typeof envSidebarSelectedId !== 'undefined') {
                        envSidebarSelectedId = e.id;
                    }
                    render();
                },
                onContext: function (ev) {
                    openTreeSubContextMenu(ev, [
                        {
                            icon: '🗑',
                            label: 'Delete environment',
                            danger: true,
                            onClick: function () {
                                bowireConfirm(
                                    'Delete environment "' + (e.name || e.id) + '"? Variables stored in this environment are removed.',
                                    function () {
                                        if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                                        if (typeof deleteEnvironment === 'function') deleteEnvironment(e.id);
                                        render();
                                    },
                                    { title: 'Delete environment', confirmText: 'Delete', danger: true }
                                );
                            }
                        }
                    ]);
                }
            };
        });

        return {
            id: key,
            label: 'Environments',
            icon: 'globe',
            badge: envs.length || null,
            selected: selected,
            expandable: true,
            expanded: expanded,
            onClick: function () {
                workspacesSelectedId = w.id;
                workspaceTreeSelection = { wsId: w.id, kind: 'environments' };
                toggleWorkspaceTreeNode(key, sel.wsId === w.id);
                render();
            },
            onToggle: function () {
                toggleWorkspaceTreeNode(key, sel.wsId === w.id);
                render();
            },
            onAdd: function () {
                if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                if (typeof openCreateEnvironmentDialog !== 'function') return;
                openCreateEnvironmentDialog(function (env) {
                    if (typeof envSidebarSelectedId !== 'undefined') {
                        envSidebarSelectedId = env.id;
                    }
                    workspaceTreeSelection = { wsId: w.id, kind: 'env', value: env.id };
                    workspaceTreeExpanded[key] = true;
                    persistWorkspaceTreeExpanded();
                    render();
                });
            },
            addTitle: 'New environment',
            onContext: function (ev) {
                openTreeSubContextMenu(ev, [
                    {
                        icon: '+',
                        label: 'New environment',
                        onClick: function () {
                            if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
                            if (typeof openCreateEnvironmentDialog !== 'function') return;
                            openCreateEnvironmentDialog(function (env) {
                                if (typeof envSidebarSelectedId !== 'undefined') {
                                    envSidebarSelectedId = env.id;
                                }
                                workspaceTreeSelection = { wsId: w.id, kind: 'env', value: env.id };
                                workspaceTreeExpanded[key] = true;
                                persistWorkspaceTreeExpanded();
                                render();
                            });
                        }
                    }
                ]);
            },
            children: leafChildren
        };
    }

    function _buildSimpleChildNode(w, kind, icon, label, badge) {
        var sel = workspaceTreeSelection || {};
        var selected = sel.wsId === w.id && sel.kind === kind;
        // Variables + Settings are navigation-only nodes — no add/delete
        // action applies at the category level, and clicking the row
        // already opens the view, so a context menu would only offer a
        // redundant "Open" item. Skip onContext entirely so the right-
        // click falls through to the rail's empty-area handler (which
        // surfaces "+ New workspace…").
        return {
            id: 'ws:' + w.id + ':' + kind,
            label: label,
            icon: icon,
            badge: badge,
            selected: selected,
            onClick: function () {
                workspacesSelectedId = w.id;
                workspaceTreeSelection = { wsId: w.id, kind: kind };
                render();
            }
        };
    }

    function _countEnvironments(w) {
        // Self-contained workspaces: env count = the workspace's own
        // env-store length.
        if (w.id === activeWorkspaceId) {
            return (typeof getEnvironments === 'function') ? getEnvironments().length : 0;
        }
        return (typeof readWorkspaceEnvironments === 'function')
            ? readWorkspaceEnvironments(w.id).length : 0;
    }

    function _countWorkspaceList(wsId, baseKey) {
        var list = readWorkspaceJsonList(wsId, baseKey);
        return list.length || null;
    }

    // #192 (B) — schema / URL drop on a workspace subtree row.
    // Accepts:
    //   - One or more files (.proto / .json / .graphql / .yaml / …):
    //     stages each as a `file://<filename>` entry on the target
    //     workspace's serverUrls. Schema-parse pipeline lands later;
    //     the drop establishes the source, the operator wires up the
    //     URL prefix from the Sources rail when they're ready.
    //   - Plain text (a URL pasted from another window): treated as
    //     a URL string and added as-is.
    // Switches to the target workspace first so the URL ends up in
    // the right per-workspace bucket regardless of which workspace
    // the operator was on when they dropped.
    function _handleWorkspaceDrop(w, dt) {
        if (!dt) return;
        if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
        var added = [];
        var files = dt.files;
        if (files && files.length > 0) {
            for (var i = 0; i < files.length; i++) {
                var entry = 'file://' + files[i].name;
                if (serverUrls.indexOf(entry) === -1) {
                    serverUrls.push(entry);
                    added.push(entry);
                }
            }
        } else {
            var text = '';
            try { text = dt.getData('text/plain') || dt.getData('text/uri-list') || ''; }
            catch { /* ignore */ }
            text = String(text || '').trim();
            if (text && serverUrls.indexOf(text) === -1) {
                serverUrls.push(text);
                added.push(text);
            }
        }
        if (added.length > 0) {
            if (typeof persistServerUrls === 'function') persistServerUrls();
            if (typeof toast === 'function') {
                toast('Added ' + added.length + ' source'
                    + (added.length === 1 ? '' : 's') + ' to ' + w.name, 'success');
            }
            workspaceTreeSelection = { wsId: w.id, kind: 'url', value: added[0] };
            workspaceTreeExpanded['ws:' + w.id] = true;
            workspaceTreeExpanded['ws:' + w.id + ':sources'] = true;
            persistWorkspaceTreeExpanded();
        }
        render();
    }

    function _quickAddUrlToWorkspace(w) {
        // If the operator is on a different workspace, switch first so
        // the URL lands in the right per-workspace bucket. Then prompt.
        if (w.id !== activeWorkspaceId) switchWorkspace(w.id);
        if (typeof bowirePrompt !== 'function') return;
        bowirePrompt('Add URL or schema reference', {
            title: 'Add to ' + w.name,
            placeholder: 'https://api.example.com or rest@https://… or grpc@…',
            confirmText: 'Add'
        }).then(function (raw) {
            if (!raw) return;
            raw = String(raw).trim();
            if (!raw) return;
            var wasNew = serverUrls.indexOf(raw) === -1;
            if (wasNew) {
                serverUrls.push(raw);
                if (typeof persistServerUrls === 'function') persistServerUrls();
            }
            workspaceTreeSelection = { wsId: w.id, kind: 'url', value: raw };
            sourcesSelectedUrl = raw;
            sidebarView = 'sources';
            render();
            // Kick off discovery immediately so the operator lands on
            // populated services instead of an empty Discover rail.
            // fetchServices fans out across every configured URL; the
            // newly-added one ends up in `services` once the discovery
            // round trip returns. Only fire when we actually added it
            // — re-clicking an existing URL shouldn't re-discover.
            if (wasNew && typeof fetchServices === 'function') {
                fetchServices();
            }
        });
    }

    // Shortcut to upload schema files (.proto / .json / .yaml / .yml)
    // straight from the Sources context menu without first navigating to
    // the Sources detail dropzone. POSTs to the same /api/proto/upload
    // and /api/openapi/upload endpoints the dropzone uses; switches to
    // the owning workspace first so the uploaded schemas land in the
    // right per-workspace store.
    function _uploadSchemaFilesForWorkspace(w) {
        if (w && w.id !== activeWorkspaceId) switchWorkspace(w.id);
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = '.proto,.json,.yaml,.yml';
        input.multiple = true;
        input.onchange = async function () {
            if (!input.files || input.files.length === 0) return;
            var protoCount = 0, openapiCount = 0;
            try {
                for (var file of input.files) {
                    var content = await file.text();
                    var lower = file.name.toLowerCase();
                    var endpoint;
                    if (lower.endsWith('.proto')) {
                        endpoint = '/api/proto/upload';
                        protoCount++;
                    } else {
                        endpoint = '/api/openapi/upload?name=' + encodeURIComponent(file.name);
                        openapiCount++;
                    }
                    await fetch(config.prefix + endpoint, { method: 'POST', body: content });
                }
                var parts = [];
                if (protoCount > 0) parts.push(protoCount + ' .proto');
                if (openapiCount > 0) parts.push(openapiCount + ' OpenAPI');
                if (typeof toast === 'function') toast(parts.join(' + ') + ' imported', 'success');
                if (typeof fetchServices === 'function') fetchServices();
            } catch (err) {
                console.error('[bowire-sources-ctx] schema upload failed', err);
                if (typeof toast === 'function') toast('Schema upload failed — see console', 'error');
            }
        };
        input.click();
    }

    // #133 Phase 2 — Environments rail mode sidebar. Reuses the
    // existing renderEnvironmentsListInto helper but wraps it in a
    // sidebar-mode container so the legacy Discover segmented-
    // control + protocol filter chrome doesn't render on top.
    function renderEnvironmentsSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        var list = el('div', { className: 'bowire-env-sidebar-list' });
        if (typeof renderEnvironmentsListInto === 'function') {
            renderEnvironmentsListInto(list);
        }
        sidebar.appendChild(list);
        return sidebar;
    }

    // #133 Phase 2 — Collections rail mode sidebar. Lists every
    // saved collection as a clickable row with the standard
    // active-state. Header carries a 'New collection' button and
    // a Postman-import affordance — same actions the legacy
    // collections-manager modal exposed.
    function renderCollectionsSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });

        function _bulkDeleteCollections(ids) {
            var removed = [];
            ids.forEach(function (cid) {
                var idx = collectionsList.findIndex(function (c) { return c.id === cid; });
                if (idx < 0) return;
                removed.push({ entry: collectionsList[idx], originalIdx: idx, deletedAt: Date.now() });
                collectionsList.splice(idx, 1);
                if (collectionManagerSelectedId === cid) collectionManagerSelectedId = null;
            });
            for (var k = removed.length - 1; k >= 0; k--) collectionsTrash.unshift(removed[k]);
            collectionsSelected.clear();
            collectionsSelectionAnchor = null;
            persistCollections();
            persistCollectionsTrash();
            toast(removed.length + ' collection' + (removed.length === 1 ? '' : 's') + ' moved to trash', 'success', {
                undo: function () {
                    for (var u = 0; u < removed.length; u++) {
                        var t = removed[u];
                        collectionsList.splice(Math.min(t.originalIdx, collectionsList.length), 0, t.entry);
                        var tIdx = collectionsTrash.findIndex(function (x) { return x.entry && x.entry.id === t.entry.id; });
                        if (tIdx >= 0) collectionsTrash.splice(tIdx, 1);
                    }
                    persistCollections();
                    persistCollectionsTrash();
                    render();
                }
            });
            render();
        }

        sidebar.appendChild(renderSidebarToolbar({
            title: 'Collections',
            primary: {
                icon: 'plus',
                title: 'Create new collection',
                onClick: function () {
                    var col = createCollection();
                    collectionManagerSelectedId = col.id;
                    render();
                }
            },
            overflow: (collectionsList && collectionsList.length > 0) ? [
                {
                    label: 'Move all to trash',
                    danger: true,
                    onClick: function () {
                        var n = collectionsList.length;
                        bowireConfirm(
                            'Move all ' + n + ' collections to trash?',
                            function () {
                                var removed = collectionsList.map(function (c, idx) {
                                    return { entry: c, originalIdx: idx, deletedAt: Date.now() };
                                });
                                collectionsList.length = 0;
                                collectionManagerSelectedId = null;
                                for (var k = removed.length - 1; k >= 0; k--) collectionsTrash.unshift(removed[k]);
                                persistCollections();
                                persistCollectionsTrash();
                                toast(removed.length + ' collections moved to trash', 'success');
                                render();
                            },
                            { title: 'Move all to trash', confirmText: 'Move ' + n, danger: true }
                        );
                    }
                }
            ] : null,
            selection: collectionsSelected.size > 0 ? {
                count: collectionsSelected.size,
                actions: [
                    {
                        icon: 'trash',
                        danger: true,
                        title: 'Move selected to trash',
                        onClick: function () {
                            _bulkDeleteCollections(Array.from(collectionsSelected));
                        }
                    }
                ],
                onClear: function () {
                    collectionsSelected.clear();
                    collectionsSelectionAnchor = null;
                    render();
                }
            } : null
        }));

        if (!collectionsList || collectionsList.length === 0) {
            sidebar.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No collections yet.'
            }));
        } else {
            var list = el('div', { id: 'bowire-collections-list', className: 'bowire-env-list' });
            var colIds = collectionsList.map(function (c) { return c.id; });
            collectionsList.forEach(function (col, idx) {
                var itemCount = (col.items ? col.items.length : 0);
                list.appendChild(renderSidebarListItem({
                    id: 'bowire-col-row-' + col.id,
                    name: col.name,
                    meta: itemCount + ' item' + (itemCount === 1 ? '' : 's'),
                    active: collectionManagerSelectedId === col.id,
                    selected: collectionsSelected.has(col.id),
                    onClick: function (e) {
                        var isMod = applyListSelectionClick(collectionsSelected, collectionsSelectionAnchor, colIds, idx, e);
                        if (isMod) {
                            collectionsSelectionAnchor = idx;
                        } else {
                            collectionsSelected.clear();
                            collectionsSelectionAnchor = idx;
                            collectionManagerSelectedId = col.id;
                        }
                        render();
                    },
                    deleteTitle: 'Delete collection',
                    onDelete: function () {
                        var dIdx = collectionsList.indexOf(col);
                        if (dIdx < 0) return;
                        var backup = collectionsList[dIdx];
                        collectionsList.splice(dIdx, 1);
                        if (collectionManagerSelectedId === col.id) collectionManagerSelectedId = null;
                        collectionsTrash.unshift({ entry: backup, deletedAt: Date.now(), originalIdx: dIdx });
                        persistCollections();
                        persistCollectionsTrash();
                        toast('Collection moved to trash', 'success', {
                            undo: function () {
                                var t = collectionsTrash.shift();
                                if (t) {
                                    collectionsList.splice(Math.min(dIdx, collectionsList.length), 0, t.entry);
                                    persistCollections();
                                    persistCollectionsTrash();
                                    render();
                                }
                            }
                        });
                        render();
                    }
                }));
            });
            sidebar.appendChild(list);
        }

        // #143 Phase 2 — Recently deleted collections (30-day TTL).
        sidebar.appendChild(renderTrashSection({
            trashArray: collectionsTrash,
            isOpen: collectionsTrashOpen,
            setOpen: function (v) { collectionsTrashOpen = v; },
            restoreAt: function (t) {
                collectionsList.splice(Math.min(t.originalIdx || collectionsList.length, collectionsList.length), 0, t.entry);
                persistCollections();
            },
            persist: function () { persistCollectionsTrash(); },
            nameOf: function (e) { return e && e.name ? e.name : '(unnamed collection)'; }
        }));

        return sidebar;
    }

    // #143 Phase 2 — Renders a collapsible 'Recently deleted'
    // section at the bottom of a list sidebar. Each entry can be
    // restored or permanently deleted. Auto-purge after 30 days
    // happens at startup so the section only shows what's actually
    // recoverable.
    //
    // opts: { trashArray, isOpen, setOpen, restoreAt(item), purgeAll, persist, labelOne, labelMany, nameOf }
    function renderTrashSection(opts) {
        var section = el('div', { className: 'bowire-trash-section' });
        var trash = opts.trashArray || [];
        if (trash.length === 0) return section;

        var header = el('div', {
            className: 'bowire-trash-header',
            onClick: function () { opts.setOpen(!opts.isOpen); render(); }
        },
            el('span', { textContent: '🗑 Recently deleted' }),
            el('span', { className: 'bowire-trash-count', textContent: String(trash.length) }),
            el('span', { className: 'bowire-trash-caret', textContent: opts.isOpen ? '▾' : '▸' })
        );
        section.appendChild(header);

        if (!opts.isOpen) return section;

        var list = el('div', { className: 'bowire-trash-list' });
        trash.forEach(function (t, i) {
            var row = el('div', { className: 'bowire-trash-row' },
                el('div', { className: 'bowire-trash-row-name', textContent: opts.nameOf(t.entry) }),
                el('div', { className: 'bowire-trash-row-meta', textContent: 'deleted ' + new Date(t.deletedAt).toLocaleString() }),
                el('button', {
                    type: 'button',
                    className: 'bowire-trash-row-action',
                    title: 'Restore',
                    textContent: '↩',
                    onClick: function () {
                        opts.restoreAt(t);
                        trash.splice(i, 1);
                        opts.persist();
                        render();
                    }
                }),
                el('button', {
                    type: 'button',
                    className: 'bowire-trash-row-action bowire-trash-row-action-danger',
                    title: 'Delete permanently',
                    textContent: '×',
                    onClick: function () {
                        trash.splice(i, 1);
                        opts.persist();
                        render();
                    }
                })
            );
            list.appendChild(row);
        });
        section.appendChild(list);

        if (trash.length > 0) {
            section.appendChild(el('button', {
                type: 'button',
                className: 'bowire-trash-empty-btn',
                textContent: 'Empty trash',
                onClick: function () {
                    var n = trash.length;
                    bowireConfirm(
                        'Permanently delete all ' + n + ' items in trash? This cannot be undone.',
                        function () {
                            trash.length = 0;
                            opts.persist();
                            render();
                        },
                        { title: 'Empty trash', confirmText: 'Delete ' + n, danger: true }
                    );
                }
            }));
        }

        return section;
    }

    // #133 Phase 2 — Mocks rail mode sidebar. Lists every running
    // mock host (from the BowireMockHostManager #94) as a clickable
    // row. Selecting a mock swaps the main pane to its detail view
    // (URL, log toggle, stop action). Refresh button at the top
    // re-fetches /api/mocks so externally-started mocks land in the
    // list without page reload (consolidated under #223).
    // Security rail sidebar — the Security pane lives entirely in the
    // main pane (threat-model + fuzz + nuclei). The sidebar carries
    // just the rail-mode label + a one-line orientation hint so the
    // operator sees what surface they're on. Used to fall through to
    // the legacy services tree, which then dispatched on sidebarView
    // and, if sidebarView happened to be 'proxy', rendered the
    // "Proxy not reachable" empty card — surprise content from a
    // sibling rail mode leaking into Security.
    function renderSecuritySidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        sidebar.appendChild(renderSidebarToolbar({ title: 'Security' }));
        sidebar.appendChild(el('div', {
            className: 'bowire-pane-empty',
            style: 'padding:12px 14px',
            textContent: 'Threat model, fuzz, and Nuclei templates sit in the main pane. Discovered endpoints are pulled automatically from the active workspace.'
        }));
        return sidebar;
    }

    function renderMocksSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });

        sidebar.appendChild(renderSidebarToolbar({
            title: 'Running mocks',
            actions: [
                {
                    icon: 'replay',
                    title: 'Refresh the list of running mocks',
                    onClick: function () {
                        if (typeof fetchMocks === 'function') {
                            fetchMocks().then(function () { render(); });
                        }
                    }
                }
            ],
            overflow: (mocksList && mocksList.length > 0 && typeof stopMock === 'function') ? [
                {
                    label: 'Stop all mocks',
                    danger: true,
                    onClick: function () {
                        var ids = mocksList.map(function (m) { return m.mockId; });
                        bowireConfirm(
                            'Stop all ' + ids.length + ' running mocks?',
                            function () {
                                ids.forEach(function (id) { stopMock(id); });
                                mockSelectedId = null;
                                render();
                            },
                            { title: 'Stop all mocks', confirmText: 'Stop ' + ids.length, danger: true }
                        );
                    }
                }
            ] : null
        }));

        if (!mocksList || mocksList.length === 0) {
            sidebar.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No mocks running.'
            }));
        } else {
            var list = el('div', { id: 'bowire-mocks-list', className: 'bowire-env-list' });
            mocksList.forEach(function (m) {
                list.appendChild(renderSidebarListItem({
                    id: 'bowire-mock-row-' + m.mockId,
                    name: m.recordingName || ('mock-' + m.port),
                    meta: 'port ' + m.port,
                    selected: mockSelectedId === m.mockId,
                    onClick: function () {
                        mockSelectedId = m.mockId;
                        render();
                    },
                    // Mocks delete = stop the host. No undo (the port +
                    // process are released; can't bring them back).
                    deleteTitle: 'Stop mock host',
                    onDelete: function () {
                        if (typeof stopMock === 'function') {
                            stopMock(m.mockId);
                            if (mockSelectedId === m.mockId) mockSelectedId = null;
                        }
                    }
                }));
            });
            sidebar.appendChild(list);
        }

        return sidebar;
    }

    // Flows + Proxy sidebars — thin wrappers around the existing
    // renderFlowsListInto / renderProxyListInto helpers in flows.js /
    // proxy-view.js. Both helpers already build their own unified
    // toolbar + list rows; the wrapper just supplies the sidebar
    // shell so the dispatcher below can route to them, instead of
    // falling through to the legacy Discover services-tree path
    // (which used to render search + protocol filters + new-request
    // dropdown ON TOP OF the flow/proxy entries — visually it read as
    // "Discover" with a list of flows tacked on).
    function renderFlowsSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        var list = el('div', {
            id: 'bowire-sidebar-list-flows',
            className: 'bowire-service-list'
        });
        if (typeof renderFlowsListInto === 'function') {
            renderFlowsListInto(list);
        }
        sidebar.appendChild(list);
        return sidebar;
    }
    function renderProxySidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        var list = el('div', {
            id: 'bowire-sidebar-list-proxy',
            className: 'bowire-service-list'
        });
        if (typeof renderProxyListInto === 'function') {
            renderProxyListInto(list);
        }
        sidebar.appendChild(list);
        return sidebar;
    }

    function renderSidebar() {
        // #137 — sidebar dispatch driven by the rail-mode catalogue.
        // Each mode declares its sidebar 'kind' in _railModes; this
        // dispatcher maps the kind to its renderer. New modes only
        // need to (a) add a catalogue entry with the right kind, and
        // (b) either reuse an existing renderer or extend this map.
        var spec = currentRailSidebarSpec();
        var sidebar = null;
        switch (spec.kind) {
            case 'collections':  sidebar = renderCollectionsSidebar(); break;
            case 'environments': sidebar = renderEnvironmentsSidebar(); break;
            case 'recordings':   sidebar = renderRecordingsSidebar(); break;
            case 'mocks':        sidebar = renderMocksSidebar(); break;
            case 'workspaces':   sidebar = renderWorkspacesSidebar(); break;
            case 'sources':      sidebar = renderSourcesSidebar(); break;
            case 'benchmarks':   sidebar = renderBenchmarksSidebar(); break;
            case 'security':     sidebar = renderSecuritySidebar(); break;
            case 'flows':        sidebar = renderFlowsSidebar(); break;
            case 'proxy':        sidebar = renderProxySidebar(); break;
        }
        if (sidebar) return sidebar;
        // Only 'services' falls through to the legacy Discover
        // sidebar below — that's the rail mode that actually wants
        // the search + protocol filter + service tree chrome.

        // Each top-level child of the sidebar gets a stable id so morphdom
        // matches them by key instead of by position. Without ids, morphdom
        // slides the old nodes rightward when a view switch changes the
        // number of children (e.g. favorites view drops protocol-tabs and
        // search) — the OLD click listeners come along for the ride and
        // end up firing for the wrong element. Stable ids per section make
        // morphdom drop removed sections and insert new ones cleanly, with
        // the listeners attached to the right nodes.
        //
        // The brand logo, the command-palette search, the env selector and
        // the theme toggle all live in the topbar now (see renderTopbar).
        // The sidebar focuses on navigation: source selector (when the
        // host isn't in pure embedded mode), view switch, protocol filter
        // row, service/favorites list, and a minimal footer with just the
        // count + tour button.
        sidebar = el('div', { className: `bowire-sidebar ${sidebarCollapsed ? 'collapsed' : ''}` });

        // #294 — Source selector (URL bar + schema-files tabs) retired
        // from the sidebar. Discover / Flows / Proxy / Security used to
        // mount it at the top of their sidebars, which duplicated the
        // canonical source surface that now lives in workspace-detail
        // (#155 / #290). Operators add a URL / upload a schema in
        // Workspaces → <ws> → Sources; the sidebar focuses on the
        // service tree it discovers.

        // (Environment selector moved to the topbar — see renderTopbar.)

        // View switch — two pills that toggle between the full service tree
        // and a flat favorites-only list. The split has two reasons:
        //   1. The favorites live in their own view instead of piggybacking
        //      on the services list, so they don't compete for vertical
        //      space or confuse the service grouping.
        //   2. In the favorites view, the entries get an explicit remove (×)
        //      button instead of the gold-star toggle — clearer affordance
        //      because the user is inside "my picks", not browsing the whole
        //      API surface. The star in the services view keeps its
        //      add/remove toggle behaviour.
        // Unified sidebar toolbar — caps-title "Discover" on the left,
        // favorites-only toggle + Plus new-request menu on the right.
        // Replaces the bespoke .bowire-view-switch card-look so Discover
        // sits in the same toolbar frame as every other rail. The Plus
        // dropdown keeps its bespoke per-protocol markup (icons read
        // faster than a plain text context menu) and anchors against
        // newBtnWrapper inside the new row.
        var viewSwitch = el('div', { id: 'bowire-sidebar-view-switch', className: 'bowire-sidebar-toolbar', role: 'toolbar' });
        viewSwitch.appendChild(el('span', {
            className: 'bowire-sidebar-toolbar-title',
            textContent: 'Discover'
        }));
        viewSwitch.appendChild(el('span', { className: 'bowire-sidebar-toolbar-spacer' }));

        // Favorites-only toggle retired from the toolbar row — it moved
        // into the protocol filter popup as the first option (state
        // marker + click toggle). One filter control on the strip
        // instead of two related-but-separate buttons (Memory: don't
        // multiply interaction patterns).

        // "+" new-request button — accent-primary in the toolbar so it
        // reads at the same weight as the "+" button on every other
        // rail. The dropdown still lives inside newBtnWrapper so it
        // can anchor `position:absolute` against the button.
        var newBtnWrapper = el('div', { className: 'bowire-new-btn-wrapper' });
        var newBtn = el('button', {
            id: 'bowire-new-btn',
            className: 'bowire-sidebar-toolbar-primary',
            title: 'New request, collection, or environment',
            'aria-label': 'New request, collection, or environment',
            onClick: function (e) {
                e.stopPropagation();
                var menu = newBtnWrapper.querySelector('.bowire-new-dropdown');
                if (menu) { menu.remove(); return; }
                var dropdown = el('div', { className: 'bowire-new-dropdown' });

                // Protocol-specific new request entries. Fallback uses
                // built-in glyphs so the icon column never reads blank
                // when the backend's IconSvg is empty for a protocol.
                function _protoFallbackIcon(id) {
                    switch (id) {
                        case 'grpc':      return svgIcon('connect');
                        case 'rest':      return svgIcon('plug');
                        case 'graphql':   return svgIcon('flow');
                        case 'mqtt':      return svgIcon('server');
                        case 'websocket': return svgIcon('repeat');
                        case 'socketio':  return svgIcon('repeat');
                        case 'sse':       return svgIcon('send');
                        case 'mcp':       return svgIcon('boxes');
                        case 'odata':     return svgIcon('chart');
                        case 'signalr':   return svgIcon('lightning');
                        case 'soap':      return svgIcon('briefcase');
                        case 'jsonrpc':   return svgIcon('flow');
                        case 'nats':      return svgIcon('server');
                        case 'pulsar':    return svgIcon('server');
                        default:          return svgIcon('plug');
                    }
                }
                var protoList = protocols.length > 0 ? protocols : [
                    { id: 'grpc', name: 'gRPC' }, { id: 'rest', name: 'REST' },
                    { id: 'graphql', name: 'GraphQL' }, { id: 'mqtt', name: 'MQTT' },
                    { id: 'websocket', name: 'WebSocket' }, { id: 'socketio', name: 'Socket.IO' }
                ];
                for (var pi = 0; pi < protoList.length; pi++) {
                    (function (p) {
                        dropdown.appendChild(el('div', {
                            className: 'bowire-new-dropdown-item',
                            onClick: function () {
                                // Default methodType to whatever shape
                                // the picked protocol can actually do
                                // (REST/OData = Unary, MQTT/WS = Duplex,
                                // SSE = ServerStreaming, …) so the
                                // builder doesn't open with an unsupported
                                // shape preselected.
                                var supported = (typeof getSupportedMethodTypes === 'function')
                                    ? getSupportedMethodTypes(p.id) : ['Unary'];
                                freeformRequest = {
                                    protocol: p.id,
                                    serverUrl: serverUrls.length > 0 ? serverUrls[0] : '',
                                    service: '', method: '', body: '{}', metadata: {},
                                    methodType: supported[0] || 'Unary',
                                    mockResponse: '', mockStatus: 'OK'
                                };
                                selectedMethod = null;
                                selectedService = null;
                                // The freeform-builder lives in the main
                                // pane reserved for the Services view —
                                // when the user fires "+ → MCP" while
                                // the sidebar is parked on Flows / Proxy
                                // / Environments, the renderMain branch
                                // for that view wins and the freeform
                                // pane never paints. Bounce back to
                                // Services so the builder actually shows.
                                if (sidebarView !== 'services' && sidebarView !== 'favorites') {
                                    setSidebarView('services');
                                }
                                dropdown.remove();
                                render();
                            }
                        },
                            el('span', { className: 'bowire-new-dropdown-icon', innerHTML: p.icon || _protoFallbackIcon(p.id) }),
                            el('span', { textContent: p.name })
                        ));
                    })(protoList[pi]);
                }

                // Divider
                dropdown.appendChild(el('div', { className: 'bowire-new-dropdown-divider' }));

                // Collection — switches to the Collections rail
                // mode and selects the freshly-created entry.
                dropdown.appendChild(el('div', {
                    className: 'bowire-new-dropdown-item',
                    onClick: function () {
                        var col = createCollection();
                        collectionManagerSelectedId = col.id;
                        railMode = 'collections';
                        try { localStorage.setItem('bowire_rail_mode', 'collections'); } catch { /* ignore */ }
                        dropdown.remove();
                        render();
                    }
                }, el('span', { textContent: 'Collection' })));

                // Environment
                dropdown.appendChild(el('div', {
                    className: 'bowire-new-dropdown-item',
                    onClick: function () {
                        dropdown.remove();
                        if (typeof openCreateEnvironmentDialog !== 'function') return;
                        openCreateEnvironmentDialog(function (env) {
                            envSidebarSelectedId = env.id;
                            setSidebarView('environments');
                            render();
                        });
                    }
                }, el('span', { textContent: 'Environment' })));

                // Flow — switch to flows sidebar view
                dropdown.appendChild(el('div', {
                    className: 'bowire-new-dropdown-item',
                    onClick: function () {
                        flowsList = loadFlows();
                        if (flowsList.length === 0) {
                            var flow = createFlow();
                            flowEditorSelectedId = flow.id;
                        } else {
                            flowEditorSelectedId = flowEditorSelectedId || flowsList[0].id;
                        }
                        setSidebarView('flows');
                        dropdown.remove();
                        render();
                    }
                }, el('span', { textContent: 'Flow' })));

                newBtnWrapper.appendChild(dropdown);
                // Close on outside click
                setTimeout(function () {
                    document.addEventListener('click', function handler() {
                        dropdown.remove();
                        document.removeEventListener('click', handler);
                    }, { once: true });
                }, 0);
            }
        },
            el('span', { innerHTML: svgIcon('plus'), style: 'width:14px;height:14px;display:flex' })
        );
        newBtnWrapper.appendChild(newBtn);
        viewSwitch.appendChild(newBtnWrapper);

        sidebar.appendChild(viewSwitch);

        // ---- Services Toolbar (filter + chips) ----
        // Only visible in the services view.
        if (sidebarView === 'services') {
            var svcToolbar = el('div', { id: 'bowire-services-toolbar', className: 'bowire-services-toolbar' });

        // #156 — favorites-only toggle moved to the viewSwitch row
        // above so it shares the same row as the '+' new-request
        // button. Keeping it here would have split related controls
        // across two rows for no reason.

        // Protocol filter button — always rendered on the view-switch
        // row when there are ≥2 protocols with services, so toggling
        // Services ↔ Favorites doesn't change the row width and the
        // pills don't jump. In the favorites view the button is
        // disabled (greyed out) and the popup is suppressed — the
        // filter is meaningless for the flat favorites list — but
        // the count badge still shows, so the user can see at a
        // glance that a filter is set and will apply again when they
        // switch back to Services.
        //
        // The popup uses `position: fixed` + viewport coordinates
        // computed from the button's bounding rect so it escapes the
        // sidebar overflow-clip and can sit anywhere on screen.
        var viewSwitchActiveProtocols = protocols.filter(function (p) {
            return services.some(function (s) { return s.source === p.id; });
        });
        // Show the filter button when there are multiple protocols OR
        // multiple method types OR multiple discovery URLs — every
        // dimension above 1 gives the user something useful to narrow
        // down. The URL dimension is new — distinguishes services
        // discovered from different origins (gateway vs. admin
        // backend vs. staging clone) when the same protocol shows up
        // at multiple URLs.
        var allMethodTypes = new Set();
        var allOriginUrls = new Set();
        for (var ats = 0; ats < services.length; ats++) {
            if (!services[ats].methods) continue;
            if (services[ats].originUrl) allOriginUrls.add(services[ats].originUrl);
            for (var atm = 0; atm < services[ats].methods.length; atm++) {
                allMethodTypes.add(services[ats].methods[atm].methodType || 'Unary');
            }
        }
        if (viewSwitchActiveProtocols.length > 1 || allMethodTypes.size > 1 || allOriginUrls.size > 1) {
            var filterDisabled = sidebarView !== 'services';
            var filterBtnWrapperTop = el('div', { className: 'bowire-protocol-filter-wrapper' });
            var totalFilterCount = protocolFilter.size + methodTypeFilter.size + urlFilter.size
                + (favoritesOnly ? 1 : 0);
            var filterBtnAttrs = {
                id: 'bowire-protocol-filter-btn',
                className: 'bowire-protocol-filter-btn'
                    + (totalFilterCount > 0 ? ' has-active' : '')
                    + (protocolFilterOpen && !filterDisabled ? ' open' : ''),
                title: filterDisabled
                    ? 'Protocol filter only applies in the Services view'
                    : 'Filter by protocol',
                'aria-label': 'Filter by protocol',
                onClick: function (e) {
                    e.stopPropagation();
                    if (filterDisabled) return;
                    protocolFilterOpen = !protocolFilterOpen;
                    render();
                }
            };
            if (filterDisabled) filterBtnAttrs.disabled = 'disabled';
            var filterBtnTop = el('button', filterBtnAttrs,
                el('span', { className: 'bowire-protocol-filter-btn-icon', innerHTML: svgIcon('filter') }),
                totalFilterCount > 0
                    ? el('span', { className: 'bowire-protocol-filter-btn-count', textContent: String(totalFilterCount) })
                    : null
            );
            filterBtnWrapperTop.appendChild(filterBtnTop);

            if (protocolFilterOpen && !filterDisabled) {
                    var popupTop = el('div', { className: 'bowire-protocol-filter-popup' });
                    // Favorites is a filter too — sits at the top of the
                    // popup as a single toggle row so the operator only
                    // has to learn one filter surface instead of one
                    // toolbar button + one popup.
                    popupTop.appendChild(el('div', {
                        className: 'bowire-protocol-filter-option' + (favoritesOnly ? ' selected' : ''),
                        role: 'menuitemcheckbox',
                        'aria-checked': favoritesOnly ? 'true' : 'false',
                        onClick: function (e) {
                            e.stopPropagation();
                            setFavoritesOnly(!favoritesOnly);
                            render();
                        }
                    },
                        el('span', {
                            className: 'bowire-protocol-filter-check',
                            textContent: favoritesOnly ? '✓' : ''
                        }),
                        el('span', {
                            className: 'bowire-protocol-filter-proto-icon',
                            innerHTML: svgIcon(favoritesOnly ? 'starFilled' : 'star')
                        }),
                        el('span', {
                            className: 'bowire-protocol-filter-proto-name',
                            textContent: 'Favorites only'
                        })
                    ));
                    popupTop.appendChild(el('div', { className: 'bowire-protocol-filter-popup-header', style: 'margin-top:6px' },
                        el('span', { textContent: 'Filter by protocol' }),
                        protocolFilter.size > 0
                            ? el('button', {
                                className: 'bowire-protocol-filter-clear',
                                textContent: 'Clear',
                                title: 'Remove all protocol filters',
                                onClick: function (e) {
                                    e.stopPropagation();
                                    protocolFilter.clear();
                                    persistProtocolFilter();
                                    refreshSelectedProtocolFromFilter();
                                    render();
                                }
                            })
                            : null
                    ));

                    for (var pit = 0; pit < viewSwitchActiveProtocols.length; pit++) {
                        (function (p) {
                            var methodCount = services
                                .filter(function (s) { return s.source === p.id; })
                                .reduce(function (acc, s) { return acc + (s.methods ? s.methods.length : 0); }, 0);
                            var isOn = protocolFilter.has(p.id);
                            popupTop.appendChild(el('div', {
                                className: 'bowire-protocol-filter-option' + (isOn ? ' selected' : ''),
                                role: 'menuitemcheckbox',
                                'aria-checked': isOn ? 'true' : 'false',
                                onClick: function (e) {
                                    e.stopPropagation();
                                    if (protocolFilter.has(p.id)) {
                                        protocolFilter.delete(p.id);
                                    } else {
                                        protocolFilter.add(p.id);
                                    }
                                    persistProtocolFilter();
                                    refreshSelectedProtocolFromFilter();
                                    render();
                                }
                            },
                                el('span', {
                                    className: 'bowire-protocol-filter-check',
                                    textContent: isOn ? '\u2713' : ''
                                }),
                                el('span', {
                                    className: 'bowire-protocol-filter-proto-icon',
                                    innerHTML: p.icon
                                }),
                                el('span', {
                                    className: 'bowire-protocol-filter-proto-name',
                                    textContent: p.name
                                }),
                                el('span', {
                                    className: 'bowire-protocol-filter-proto-count',
                                    textContent: methodCount + ' method' + (methodCount === 1 ? '' : 's')
                                })
                            ));
                        })(viewSwitchActiveProtocols[pit]);
                    }
                    // ---- Method-type section ----
                    // Collect the distinct method types present in the
                    // visible service set so only relevant types show.
                    var typeLabels = { Unary: 'U', ServerStreaming: 'SS', ClientStreaming: 'CS', Duplex: 'DX' };
                    var presentTypes = new Set();
                    for (var ts = 0; ts < services.length; ts++) {
                        if (!services[ts].methods) continue;
                        for (var tm = 0; tm < services[ts].methods.length; tm++) {
                            presentTypes.add(services[ts].methods[tm].methodType || 'Unary');
                        }
                    }
                    if (presentTypes.size > 1) {
                        popupTop.appendChild(el('div', { className: 'bowire-protocol-filter-popup-header', style: 'margin-top:6px' },
                            el('span', { textContent: 'Filter by type' }),
                            methodTypeFilter.size > 0
                                ? el('button', {
                                    className: 'bowire-protocol-filter-clear',
                                    textContent: 'Clear',
                                    onClick: function (e) {
                                        e.stopPropagation();
                                        methodTypeFilter.clear();
                                        persistMethodTypeFilter();
                                        render();
                                    }
                                })
                                : null
                        ));
                        var typeOrder = ['Unary', 'ServerStreaming', 'ClientStreaming', 'Duplex'];
                        for (var ti = 0; ti < typeOrder.length; ti++) {
                            (function (mtype) {
                                if (!presentTypes.has(mtype)) return;
                                var cnt = 0;
                                for (var cs = 0; cs < services.length; cs++) {
                                    if (!services[cs].methods) continue;
                                    for (var cm = 0; cm < services[cs].methods.length; cm++) {
                                        if ((services[cs].methods[cm].methodType || 'Unary') === mtype) cnt++;
                                    }
                                }
                                var isOn = methodTypeFilter.has(mtype);
                                popupTop.appendChild(el('div', {
                                    className: 'bowire-protocol-filter-option' + (isOn ? ' selected' : ''),
                                    role: 'menuitemcheckbox',
                                    onClick: function (e) {
                                        e.stopPropagation();
                                        if (methodTypeFilter.has(mtype)) {
                                            methodTypeFilter.delete(mtype);
                                        } else {
                                            methodTypeFilter.add(mtype);
                                        }
                                        persistMethodTypeFilter();
                                        render();
                                    }
                                },
                                    el('span', { className: 'bowire-protocol-filter-check', textContent: isOn ? '\u2713' : '' }),
                                    el('span', {
                                        className: 'bowire-method-badge',
                                        dataset: { type: mtype },
                                        textContent: typeLabels[mtype] || mtype,
                                        style: 'font-size:9px;margin-right:6px'
                                    }),
                                    el('span', { className: 'bowire-protocol-filter-proto-name', textContent: mtype }),
                                    el('span', { className: 'bowire-protocol-filter-proto-count', textContent: cnt + ' method' + (cnt === 1 ? '' : 's') })
                                ));
                            })(typeOrder[ti]);
                        }
                    }

                    // ---- URL section ----
                    // Only meaningful when ≥ 2 discovery URLs are loaded.
                    // Single-URL setups would offer a no-op toggle.
                    // Skipped in proto mode entirely — uploaded schemas
                    // have no per-URL grouping to filter on.
                    if (sourceMode !== 'proto' && typeof serverUrls !== 'undefined' && serverUrls.length > 1) {
                        var presentUrls = new Set();
                        for (var us = 0; us < services.length; us++) {
                            if (services[us].originUrl) presentUrls.add(services[us].originUrl);
                        }
                        if (presentUrls.size > 1) {
                            popupTop.appendChild(el('div', { className: 'bowire-protocol-filter-popup-header', style: 'margin-top:6px' },
                                el('span', { textContent: 'Filter by URL' }),
                                urlFilter.size > 0
                                    ? el('button', {
                                        className: 'bowire-protocol-filter-clear',
                                        textContent: 'Clear',
                                        title: 'Remove all URL filters',
                                        onClick: function (e) {
                                            e.stopPropagation();
                                            urlFilter.clear();
                                            persistUrlFilter();
                                            render();
                                        }
                                    })
                                    : null
                            ));
                            for (var ui2 = 0; ui2 < serverUrls.length; ui2++) {
                                (function (url) {
                                    if (!url || !presentUrls.has(url)) return;
                                    var urlMethodCount = services
                                        .filter(function (s) { return s.originUrl === url; })
                                        .reduce(function (acc, s) { return acc + (s.methods ? s.methods.length : 0); }, 0);
                                    var isOn = urlFilter.has(url);
                                    // Use the user's alias when one is set —
                                    // they picked it precisely because the
                                    // raw URL is too long to scan. Fall back
                                    // to a middle-truncated URL for entries
                                    // without an alias yet. Title carries
                                    // the full URL for hover either way.
                                    var aliasLabel = (typeof serverUrlAliases !== 'undefined' && serverUrlAliases[url])
                                        ? serverUrlAliases[url]
                                        : null;
                                    var shortUrl = aliasLabel
                                        ? aliasLabel
                                        : (url.length > 38
                                            ? url.slice(0, 14) + '…' + url.slice(-22)
                                            : url);
                                    popupTop.appendChild(el('div', {
                                        className: 'bowire-protocol-filter-option' + (isOn ? ' selected' : ''),
                                        role: 'menuitemcheckbox',
                                        'aria-checked': isOn ? 'true' : 'false',
                                        title: url,
                                        onClick: function (e) {
                                            e.stopPropagation();
                                            if (urlFilter.has(url)) {
                                                urlFilter.delete(url);
                                            } else {
                                                urlFilter.add(url);
                                            }
                                            persistUrlFilter();
                                            render();
                                        }
                                    },
                                        el('span', { className: 'bowire-protocol-filter-check', textContent: isOn ? '✓' : '' }),
                                        el('span', { className: 'bowire-protocol-filter-proto-name', textContent: shortUrl }),
                                        el('span', { className: 'bowire-protocol-filter-proto-count', textContent: urlMethodCount + ' method' + (urlMethodCount === 1 ? '' : 's') })
                                    ));
                                })(serverUrls[ui2]);
                            }
                        }
                    }

                    filterBtnWrapperTop.appendChild(popupTop);
                    // setTimeout(0) defers until after morphdom has
                    // merged the off-screen tree into the live DOM.
                    // rAF was too early (fired pre-layout → top:0).
                    setTimeout(function () {
                        var liveBtn = document.querySelector('.bowire-protocol-filter-btn');
                        var livePopup = document.querySelector('.bowire-protocol-filter-popup');
                        if (!liveBtn || !livePopup) return;
                        var btnRect = liveBtn.getBoundingClientRect();
                        if (!btnRect || btnRect.bottom === 0) return;
                        var topPos = Math.round(btnRect.bottom + 4);
                        var leftPos = Math.round(btnRect.left);
                        var popWidth = livePopup.offsetWidth || 220;
                        var popHeight = livePopup.offsetHeight || 200;
                        if (leftPos + popWidth > window.innerWidth - 8)
                            leftPos = window.innerWidth - popWidth - 8;
                        if (topPos + popHeight > window.innerHeight - 8)
                            topPos = Math.max(8, Math.round(btnRect.top - popHeight - 4));
                        livePopup.style.top = topPos + 'px';
                        livePopup.style.left = leftPos + 'px';
                        livePopup.style.right = 'auto';
                    }, 0);
            }
            // Filter button lives in the unified toolbar row alongside
            // the title + '+' button, NOT a step lower in svcToolbar.
            // One toolbar surface holds every state-changing control
            // for Discover; svcToolbar shrinks down to the name-filter
            // input + the (conditional) chip strip.
            viewSwitch.insertBefore(filterBtnWrapperTop, newBtnWrapper);
        }

        // Chip row inside the toolbar — shown when a filter is active
        // (protocol and/or name). The filter button that opens the
        // popup lives up in the view-switch row, so this row is purely
        // a "here's what's currently narrowing the list" display.
        // When nothing is active, the row is omitted entirely and the
        // sidebar doesn't pay vertical space for an empty container.
        if (sidebarView === 'services') {
            var anyFilterActive = protocolFilter.size > 0 || methodTypeFilter.size > 0 || urlFilter.size > 0 || !!nameFilter || favoritesOnly;
            if (anyFilterActive) {
                var filterRow = el('div', {
                    id: 'bowire-sidebar-filter-row',
                    className: 'bowire-sidebar-filter-row'
                });

                // #156 — favorites-only chip. Pinned first because
                // it changes the meaning of every other filter. Click
                // the × to drop the favorites filter; click the
                // chip body to keep it (no-op).
                if (favoritesOnly) {
                    filterRow.appendChild(el('div', { className: 'bowire-filter-chip bowire-filter-chip-favorites' },
                        el('span', { className: 'bowire-filter-chip-icon', innerHTML: svgIcon('starFilled') }),
                        el('span', { className: 'bowire-filter-chip-label', textContent: 'Favorites only' }),
                        el('button', {
                            className: 'bowire-filter-chip-remove',
                            title: 'Show everything again',
                            textContent: '×',
                            onClick: function (e) {
                                e.stopPropagation();
                                setFavoritesOnly(false);
                                render();
                            }
                        })
                    ));
                }

                // --- Protocol chips (one per active protocol filter) ---
                protocolFilter.forEach(function (protoId) {
                    var proto = protocols.find(function (p) { return p.id === protoId; });
                    if (!proto) return;
                    filterRow.appendChild(el('div', { className: 'bowire-filter-chip' },
                        el('span', {
                            className: 'bowire-filter-chip-icon',
                            innerHTML: proto.icon
                        }),
                        el('span', {
                            className: 'bowire-filter-chip-label',
                            textContent: proto.name
                        }),
                        el('button', {
                            className: 'bowire-filter-chip-remove',
                            title: 'Remove this filter',
                            textContent: '\u00d7',
                            onClick: function (e) {
                                e.stopPropagation();
                                protocolFilter.delete(protoId);
                                persistProtocolFilter();
                                refreshSelectedProtocolFromFilter();
                                render();
                            }
                        })
                    ));
                });

                // --- Method-type chips (Unary, SS, CS, DX) ---
                var typeChipLabels = { Unary: 'Unary', ServerStreaming: 'Server Streaming', ClientStreaming: 'Client Streaming', Duplex: 'Duplex' };
                var typeChipShort = { Unary: 'U', ServerStreaming: 'SS', ClientStreaming: 'CS', Duplex: 'DX' };
                methodTypeFilter.forEach(function (mtype) {
                    filterRow.appendChild(el('div', { className: 'bowire-filter-chip' },
                        el('span', {
                            className: 'bowire-method-badge',
                            dataset: { type: mtype },
                            textContent: typeChipShort[mtype] || mtype,
                            style: 'font-size:9px;margin-right:4px'
                        }),
                        el('span', {
                            className: 'bowire-filter-chip-label',
                            textContent: typeChipLabels[mtype] || mtype
                        }),
                        el('button', {
                            className: 'bowire-filter-chip-remove',
                            title: 'Remove this type filter',
                            textContent: '\u00d7',
                            onClick: function (e) {
                                e.stopPropagation();
                                methodTypeFilter.delete(mtype);
                                persistMethodTypeFilter();
                                render();
                            }
                        })
                    ));
                });

                // --- URL chips (one per active URL filter) ---
                urlFilter.forEach(function (url) {
                    // Prefer the user's alias for chip labels — the
                    // chip row is tight on horizontal space and the
                    // alias is exactly the short name the user picked
                    // to recognise this URL at a glance.
                    var aliasLabel = (typeof serverUrlAliases !== 'undefined' && serverUrlAliases[url])
                        ? serverUrlAliases[url]
                        : null;
                    var shortUrl = aliasLabel
                        ? aliasLabel
                        : (url.length > 30
                            ? url.slice(0, 10) + '…' + url.slice(-18)
                            : url);
                    filterRow.appendChild(el('div', { className: 'bowire-filter-chip', title: url },
                        el('span', {
                            className: 'bowire-filter-chip-icon',
                            innerHTML: svgIcon('connect')
                        }),
                        el('span', {
                            className: 'bowire-filter-chip-label',
                            textContent: shortUrl
                        }),
                        el('button', {
                            className: 'bowire-filter-chip-remove',
                            title: 'Remove this URL filter',
                            textContent: '×',
                            onClick: function (e) {
                                e.stopPropagation();
                                urlFilter.delete(url);
                                persistUrlFilter();
                                render();
                            }
                        })
                    ));
                });

                // --- Name filter chip (from command-palette "Apply as name filter") ---
                if (nameFilter) {
                    filterRow.appendChild(el('div', { className: 'bowire-filter-chip bowire-filter-chip-name' },
                        el('span', {
                            className: 'bowire-filter-chip-icon',
                            innerHTML: svgIcon('search')
                        }),
                        el('span', {
                            className: 'bowire-filter-chip-label',
                            textContent: '"' + nameFilter + '"'
                        }),
                        el('button', {
                            className: 'bowire-filter-chip-remove',
                            title: 'Remove name filter',
                            textContent: '\u00d7',
                            onClick: function (e) {
                                e.stopPropagation();
                                setNameFilter('');
                                render();
                            }
                        })
                    ));
                }

                // --- Clear-all — icon-only (trash) so it doesn't need
                //     translation. Hover state in CSS uses the error
                //     color for visual weight. ---
                filterRow.appendChild(el('button', {
                    id: 'bowire-filter-clear-all-btn',
                    className: 'bowire-filter-clear-all',
                    title: 'Remove all protocol and name filters',
                    innerHTML: svgIcon('trash'),
                    onClick: function (e) {
                        e.stopPropagation();
                        protocolFilter.clear();
                        persistProtocolFilter();
                        refreshSelectedProtocolFromFilter();
                        setNameFilter('');
                        render();
                    }
                }));

                svcToolbar.appendChild(filterRow);
            }
        }

            sidebar.appendChild(svcToolbar);
        } // end if (sidebarView === 'services')

        // Service list (or favorites list depending on view)
        // View-specific ID so morphdom treats a view-switch as a full
        // container replacement instead of trying to diff services-view
        // children against favorites-view children (which causes stale
        // service groups to bleed through into an empty favorites list).
        const list = el('div', {
            id: 'bowire-sidebar-list-' + sidebarView,
            className: 'bowire-service-list'
        });

        if (sidebarView === 'environments') {
            renderEnvironmentsListInto(list);
        } else if (sidebarView === 'flows') {
            renderFlowsListInto(list);
        } else if (sidebarView === 'proxy') {
            renderProxyListInto(list);
        } else if (sidebarView === 'favorites') {
            renderFavoritesListInto(list);
        } else if (services.length === 0 && (!Array.isArray(serverUrls) || serverUrls.length === 0)) {
            // Spinner while discovery is in flight; otherwise a one-line
            // hint that matches the other rails' sidebars (Mocks, Flows,
            // Recordings, Sources, Collections, Benchmarks all show a
            // short "No … yet." on the same .bowire-pane-empty shape).
            // The full empty-card call-to-action lives in the main pane.
            // We only fall into this empty branch when there are also
            // no serverUrls configured — when URLs exist (even with
            // every one disconnected) the panel path below still runs
            // so each URL keeps its panel + plug button visible.
            if (isLoadingServices) {
                list.appendChild(el('div', { className: 'bowire-loading' },
                    el('div', { className: 'bowire-spinner' }),
                    el('span', { className: 'bowire-loading-text', textContent: 'Loading services...' })
                ));
            } else {
                list.appendChild(el('div', {
                    className: 'bowire-pane-empty',
                    style: 'padding:12px 14px',
                    textContent: 'No services discovered yet.'
                }));
            }
        } else {
            // Effective query combines the transient topbar search with the
            // pinned name-filter chip. Both are AND-ed: a method must match
            // every token from both sources to stay visible. This lets the
            // user pin a base query as a chip and then type an additional
            // term in the topbar without losing the pinned filter.
            const effectiveQueryStr = (
                (nameFilter ? nameFilter + ' ' : '') + (searchQuery || '')
            ).trim();
            const query = effectiveQueryStr.toLowerCase();
            const visibleServices = getFilteredServices();
            // #297 — cross-feature index. One pass over collections /
            // recordings / benchmarks / presets; rows look up by key.
            const xfIndex = buildCrossFeatureIndex();

            // (The pinned "Favorites" group used to live here at the top of
            // the services list, but it now has its own view toggle — see
            // renderFavoritesListInto and the [Services | Favorites] pills
            // at the top of the sidebar.)

            // Multi-token AND search across name, fullName, description,
            // summary, httpPath, httpMethod, methodType, service name and
            // service description. Splitting on whitespace lets users type
            // "GET v1/books" or "deprecated user" to narrow down by
            // multiple criteria at once. Service-level matches make every
            // method in the service visible.
            const queryTokens = query ? query.split(/\s+/).filter(Boolean) : [];

            function methodMatchesAllTokens(svc, m) {
                if (queryTokens.length === 0) return true;
                var hay = (
                    (m.name || '') + ' ' +
                    (m.fullName || '') + ' ' +
                    (m.description || '') + ' ' +
                    (m.summary || '') + ' ' +
                    (m.httpPath || '') + ' ' +
                    (m.httpMethod || '') + ' ' +
                    (m.methodType || '') + ' ' +
                    (svc.name || '') + ' ' +
                    (svc.description || '')
                ).toLowerCase();
                for (var i = 0; i < queryTokens.length; i++) {
                    if (hay.indexOf(queryTokens[i]) === -1) return false;
                }
                return true;
            }

            // Pre-compute match counts so we can show a per-search tally in
            // the sidebar footer and an empty state at the right level.
            var totalMatches = 0;
            for (var psi = 0; psi < visibleServices.length; psi++) {
                for (var pmi = 0; pmi < visibleServices[psi].methods.length; pmi++) {
                    var pm = visibleServices[psi].methods[pmi];
                    if (methodTypeFilter.size > 0 && !methodTypeFilter.has(pm.methodType || 'Unary')) continue;
                    if (methodMatchesAllTokens(visibleServices[psi], pm)) totalMatches++;
                }
            }

            if (query && totalMatches === 0) {
                list.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 32px' },
                    el('div', { className: 'bowire-empty-title', textContent: 'No matches' }),
                    el('div', { className: 'bowire-empty-desc', textContent:
                        'Nothing matches "' + query + '". Try a different word or press Esc to clear the search.' })
                ));
            }

            // Group services by originUrl so a multi-source setup
            // reads as "services-from-this-host" sections rather than
            // a flat list where pet / store / user blend with services
            // from a second URL. Shallow-copy + sort by (originUrl,
            // name) keeps the deterministic order; a single-URL setup
            // still renders the source header once at the top so
            // operators see at a glance which host their tree comes
            // from. Empty originUrl (workspace-local schemas, mock
            // sources) buckets under "—".
            var orderedServices = visibleServices.slice().sort(function (a, b) {
                var au = a.originUrl || '';
                var bu = b.originUrl || '';
                if (au !== bu) return au < bu ? -1 : 1;
                return (a.name || '').localeCompare(b.name || '');
            });

            // Source-panel state — every discovery URL gets a panel,
            // even when its services list is empty (disconnected /
            // first-time / awaiting discovery). The user's plug
            // button on the panel header relies on the panel staying
            // visible so they can hit "connect" again. Panels are
            // built lazily by ensureSourcePanel() — once for each URL
            // — and the service loop drops its service groups into
            // the right panel by url.
            var _panelEntries = Object.create(null);

            function buildSourcePanel(originUrl) {
                var sourceLabel;
                if (originUrl) {
                    // Look up the friendly name through aliasForUrl()
                    // so the panel picks up whichever rename surface
                    // the user touched — the URL-bar alias input
                    // (serverUrlAliases) OR the Sources rail rename
                    // (urlMeta[url].name). The helper returns the raw
                    // URL when neither store has a hit, so fall
                    // through to the host+pathname formatter only
                    // when the alias really is empty.
                    var aliased = aliasForUrl(originUrl);
                    if (aliased && aliased !== originUrl) {
                        sourceLabel = aliased;
                    } else {
                        try {
                            var u = new URL(originUrl);
                            sourceLabel = u.host + (u.pathname && u.pathname !== '/' ? u.pathname : '');
                        } catch {
                            sourceLabel = originUrl;
                        }
                    }
                } else {
                    sourceLabel = '— no source';
                }

                var panelExpanded = isSourceExpanded(originUrl);
                var panelStatus = originUrl
                    ? (connectionStatuses[originUrl] || 'disconnected')
                    : '';
                var panelDomId = 'bowire-source-' + (originUrl || '_no_source_')
                    .replace(/[^a-zA-Z0-9_-]/g, '_').slice(0, 80);
                var panel = el('div', {
                    id: panelDomId,
                    className: 'bowire-source-panel'
                        + (panelExpanded ? ' expanded' : ' collapsed'),
                    'data-origin-url': originUrl
                });
                var panelCount = el('span', {
                    id: panelDomId + '-count',
                    className: 'bowire-source-panel-count',
                    textContent: ''
                });
                var panelHeader = el('div', {
                    id: panelDomId + '-header',
                    className: 'bowire-source-panel-header',
                    role: 'button',
                    tabIndex: 0,
                    title: originUrl || 'Services without an external source URL (e.g. workspace-local schemas)',
                    onClick: function () {
                        toggleSourceExpanded(originUrl);
                        render();
                    },
                    onKeydown: function (e) {
                        if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            toggleSourceExpanded(originUrl);
                            render();
                        }
                    }
                },
                    el('span', {
                        className: 'bowire-source-panel-chevron'
                            + (panelExpanded ? ' expanded' : ''),
                        innerHTML: svgIcon('chevron')
                    }),
                    el('span', {
                        id: panelDomId + '-label',
                        className: 'bowire-source-panel-label',
                        textContent: sourceLabel
                    }),
                    panelCount,
                    originUrl
                        ? el('button', {
                            type: 'button',
                            className: 'bowire-source-panel-refresh-btn',
                            title: 'Re-discover services from ' + originUrl,
                            'aria-label': 'Refresh source',
                            onClick: function (e) {
                                e.stopPropagation();
                                refreshSourceServices(originUrl);
                            },
                            innerHTML: svgIcon('repeat')
                        })
                        : null,
                    originUrl
                        ? el('button', {
                            type: 'button',
                            className: 'bowire-source-panel-conn-btn bowire-conn-pill-' + panelStatus,
                            title: (panelStatus === 'connected' || panelStatus === 'connecting')
                                ? 'Disconnect from ' + originUrl
                                : 'Re-connect to ' + originUrl,
                            'aria-label': 'Toggle connection',
                            onClick: function (e) {
                                e.stopPropagation();
                                toggleSourceConnection(originUrl);
                            },
                            // Always render the normal plug glyph;
                            // offline state gets a CSS-drawn diagonal
                            // strike-through overlay so the icon
                            // reads as "plug, but not plugged in".
                            innerHTML: svgIcon('plug')
                        })
                        : null
                );
                var panelBody = el('div', {
                    id: panelDomId + '-body',
                    className: 'bowire-source-panel-body'
                        + (panelExpanded ? '' : ' collapsed')
                });
                panel.appendChild(panelHeader);
                panel.appendChild(panelBody);
                list.appendChild(panel);
                return { body: panelBody, count: panelCount, services: 0 };
            }

            function ensureSourcePanel(originUrl) {
                var key = originUrl || '';
                if (!_panelEntries[key]) _panelEntries[key] = buildSourcePanel(key);
                return _panelEntries[key];
            }

            // Pre-create panels for every serverUrl so disconnected
            // URLs still appear in the tree (with an empty body) and
            // the user has a button to re-connect them. Order matches
            // the URL bar above. Services from other origins (e.g.
            // workspace-local schemas) get their panels lazily inside
            // the service loop below.
            for (var preU = 0; preU < serverUrls.length; preU++) {
                ensureSourcePanel(serverUrls[preU]);
            }

            for (const svc of orderedServices) {
                var baseMethods = svc.methods;
                // Method-type filter (Unary / SS / CS / DX). Applied
                // before the text search so the count reflects types
                // the user actually wants to see.
                if (methodTypeFilter.size > 0) {
                    baseMethods = baseMethods.filter(function (m) {
                        return methodTypeFilter.has(m.methodType || 'Unary');
                    });
                }
                // #156 — favorites-only narrows the per-service list
                // to the starred methods. Applied alongside the other
                // method-level filters so the service-counts in the
                // sidebar remain meaningful.
                if (favoritesOnly && typeof isFavorite === 'function') {
                    baseMethods = baseMethods.filter(function (m) {
                        return isFavorite(svc.name, m.name);
                    });
                }
                const filteredMethods = query
                    ? baseMethods.filter(function (m) { return methodMatchesAllTokens(svc, m); })
                    : baseMethods;

                if (filteredMethods.length === 0) continue;

                // Get or create the panel for this service's URL —
                // serverUrls panels were pre-created above; this
                // covers stray origins (workspace-local schemas, &c.)
                // not in the URL bar.
                var _thisOriginUrl = svc.originUrl || '';
                var _panelEntry = ensureSourcePanel(_thisOriginUrl);
                _panelEntry.services++;

                const isExpanded = expandedServices.has(svc.name) || (query && filteredMethods.length > 0);
                const shortName = svc.name.split('.').pop();

                // Stable id for morphdom keyed matching so the click
                // handlers survive re-renders without re-binding.
                var safeGroupId = 'bowire-svc-' + svc.name.replace(/[^a-zA-Z0-9_-]/g, '_');
                // #120 — stamp the protocol id on every service group so
                // CSS can paint a per-protocol left-edge stripe + tint
                // child affordances without per-protocol JS branching.
                const group = el('div', {
                    id: safeGroupId,
                    className: 'bowire-service-group',
                    'data-protocol': svc.source || 'default'
                });

                const headerEl = el('div', {
                    className: 'bowire-service-header',
                    onClick: function () {
                        if (expandedServices.has(svc.name)) expandedServices.delete(svc.name);
                        else expandedServices.add(svc.name);
                        persistExpandedServices();
                        render();
                    }
                });
                headerEl.appendChild(el('span', {
                    className: `bowire-service-chevron ${isExpanded ? 'expanded' : ''}`,
                    innerHTML: svgIcon('chevron')
                }));

                headerEl.appendChild(el('span', { className: 'bowire-service-name', textContent: shortName, title: svc.name }));

                // Protocol icon (trailing) — sits between the service
                // name and the method-count badge so the type label
                // reads as a row-end tag instead of a leading bullet.
                // Only emitted when more than one protocol is loaded;
                // with a single plugin the icon would just repeat for
                // every group and waste space.
                if (protocols.length > 1) {
                    var svcProto = protocols.find(function (p) { return p.id === svc.source; });
                    if (svcProto) {
                        headerEl.appendChild(el('span', {
                            className: 'bowire-service-proto-icon',
                            title: svcProto.name,
                            innerHTML: svcProto.icon
                        }));
                    }
                }

                headerEl.appendChild(el('span', {
                    className: 'bowire-method-count',
                    textContent: query
                        ? (filteredMethods.length + ' / ' + svc.methods.length)
                        : String(svc.methods.length)
                }));
                group.appendChild(headerEl);

                const methodList = el('div', { className: `bowire-method-list ${isExpanded ? 'expanded' : ''}` });
                for (const m of filteredMethods) {
                    const isActive = selectedMethod && selectedMethod.fullName === m.fullName;
                    const executing = isJobActive(svc.name, m.name);
                    const item = el('div', {
                        className: 'bowire-method-item'
                            + (isActive ? ' active' : '')
                            + (executing ? ' executing' : ''),
                        // #122 — direction encoded on the row itself
                        // so the right-edge rail picks up the warm /
                        // cool / duplex hue. Pairs with the
                        // protocol-color stripe on the group's left
                        // edge for the two-axis read.
                        'data-direction': methodDirection(m),
                        draggable: 'true',
                        onDragstart: function (e) {
                            try {
                                e.dataTransfer.setData('application/x-bowire-method',
                                    JSON.stringify({ service: svc.name, method: m.name }));
                                e.dataTransfer.effectAllowed = 'copy';
                            } catch { /* ignore */ }
                            methodDragPayload = { service: svc.name, method: m.name };
                            render();
                        },
                        onDragend: function () {
                            methodDragPayload = null;
                            render();
                        },
                        onContextMenu: function (e) {
                            // Right-click on a method-tree row opens an
                            // "Add to envelope…" picker (Phase 3 add-
                            // surfaces) — the operator can drop the
                            // method into an existing envelope or spawn
                            // a new one seeded with it. Body is the
                            // default-JSON for the method's inputType so
                            // the seeded envelope is runnable without
                            // any further editing.
                            if (typeof addTargetToEnvelopePicker !== 'function') return;
                            e.preventDefault();
                            e.stopPropagation();
                            var defaultBody = '{}';
                            if (typeof generateDefaultJson === 'function') {
                                try { defaultBody = generateDefaultJson(m.inputType, 0); }
                                catch { /* keep '{}' */ }
                            }
                            addTargetToEnvelopePicker(e.clientX, e.clientY, {
                                type: 'method',
                                service: svc.name,
                                method: m.name,
                                protocol: svc.source || null,
                                body: defaultBody, metadata: {}, serverUrl: null
                            }, { name: svc.name + '.' + m.name });
                        },
                        onClick: function (e) {
                            // Browser-style modifier: Ctrl/Cmd+click
                            // (or middle-click) opens in a new tab;
                            // plain click adopts the active tab.
                            var inNewTab = !!(e && (e.ctrlKey || e.metaKey || e.button === 1));
                            openTab(svc, m, { inNewTab: inNewTab });
                        },
                        // Wire middle-click via auxclick since DOM
                        // click events don't fire for button 1
                        // (middle mouse) by default.
                        onAuxClick: function (e) {
                            if (e && e.button === 1) {
                                e.preventDefault();
                                openTab(svc, m, { inNewTab: true });
                            }
                        },
                        // Right-click → context menu. Headline action
                        // is "Open in new tab" so users who don't know
                        // (or don't want) the Ctrl/middle-click
                        // modifier shortcut can still pin a method.
                        onContextMenu: function (e) {
                            e.preventDefault();
                            e.stopPropagation();
                            if (typeof showContextMenu !== 'function') return;
                            var items = [
                                { label: 'Open in new tab', onClick: function () {
                                    openTab(svc, m, { inNewTab: true });
                                } },
                                { separator: true },
                                {
                                    label: isFavorite(svc.name, m.name)
                                        ? 'Remove from favorites'
                                        : 'Add to favorites',
                                    onClick: function () {
                                        toggleFavorite(svc.name, m.name);
                                    }
                                }
                            ];
                            showContextMenu(e.clientX, e.clientY, items);
                        }
                    },
                        // Order: [name (flex:1)] [deprecated] [star] [badge]
                        // — method name leads the row, tags/indicators
                        // cluster on the right, type badge (SS/CS/DX/U)
                        // at the far right. User preference: the
                        // identifying word comes first, metadata after.
                        el('span', {
                            className: 'bowire-method-name' + (m.deprecated ? ' deprecated' : ''),
                            title: m.summary || m.description || m.name,
                            textContent: m.name
                        }),
                        m.deprecated ? el('span', { className: 'bowire-method-deprecated-tag', textContent: 'DEPR' }) : null,
                        (function (svcName, methodName) {
                            var fav = isFavorite(svcName, methodName);
                            return el('span', {
                                className: 'bowire-method-star' + (fav ? ' active' : ''),
                                innerHTML: svgIcon(fav ? 'starFilled' : 'star'),
                                title: fav ? 'Remove from favorites' : 'Add to favorites',
                                onClick: function (e) {
                                    e.stopPropagation();
                                    toggleFavorite(svcName, methodName);
                                }
                            });
                        })(svc.name, m.name),
                        renderMethodCrossFeatureBadges(xfIndex, svc.name, m.name),
                        executing ? el('span', {
                            className: 'bowire-method-executing',
                            innerHTML: svgIcon('play'),
                            title: 'Currently executing'
                        }) : null,
                        el('span', {
                            className: 'bowire-method-badge',
                            dataset: { type: methodBadgeType(m), direction: methodDirection(m) },
                            textContent: methodBadgeText(m)
                        })
                    );
                    methodList.appendChild(item);
                }
                group.appendChild(methodList);
                _panelEntry.body.appendChild(group);
            }
            // Stamp each panel's count badge with the running tally
            // we accumulated during the service loop. Disconnected /
            // empty panels print "0 services" — visible signal that
            // there's nothing to invoke from this URL right now.
            Object.keys(_panelEntries).forEach(function (key) {
                var entry = _panelEntries[key];
                entry.count.textContent = entry.services
                    + ' service'
                    + (entry.services === 1 ? '' : 's');
            });
        }

        sidebar.appendChild(list);

        // Footer — just the count + Take Tour button now. The theme
        // toggle lives in the topbar.
        var footerLabel;
        if (sidebarView === 'environments') {
            var envCountForFooter = getEnvironments().length;
            footerLabel = envCountForFooter + ' environment' + (envCountForFooter === 1 ? '' : 's');
        } else if (sidebarView === 'flows') {
            var flowCountForFooter = flowsList.length;
            footerLabel = flowCountForFooter + ' flow' + (flowCountForFooter === 1 ? '' : 's');
        } else if (sidebarView === 'favorites') {
            var favCountForFooter = getFavorites().length;
            footerLabel = favCountForFooter + ' favorite' + (favCountForFooter === 1 ? '' : 's');
        } else {
            var footerServiceCount = getFilteredServices().length;
            footerLabel = `${footerServiceCount} service${footerServiceCount !== 1 ? 's' : ''}`;
        }
        // When a search / name filter is active (services view only), show
        // the match tally so the user sees how many methods their query
        // hit at a glance.
        if (sidebarView === 'services' && (searchQuery || nameFilter)) {
            var effectiveQuery = (searchQuery || nameFilter || '').toLowerCase();
            var matchTally = 0;
            var qTokens = effectiveQuery.split(/\s+/).filter(Boolean);
            for (var fsi = 0; fsi < services.length; fsi++) {
                var fs = services[fsi];
                for (var fmi2 = 0; fmi2 < fs.methods.length; fmi2++) {
                    var fm2 = fs.methods[fmi2];
                    var hay2 = (
                        (fm2.name || '') + ' ' + (fm2.fullName || '') + ' ' +
                        (fm2.description || '') + ' ' + (fm2.summary || '') + ' ' +
                        (fm2.httpPath || '') + ' ' + (fm2.httpMethod || '') + ' ' +
                        (fm2.methodType || '') + ' ' + (fs.name || '') + ' ' +
                        (fs.description || '')
                    ).toLowerCase();
                    var ok = true;
                    for (var qti = 0; qti < qTokens.length; qti++) {
                        if (hay2.indexOf(qTokens[qti]) === -1) { ok = false; break; }
                    }
                    if (ok) matchTally++;
                }
            }
            footerLabel = matchTally + ' match' + (matchTally === 1 ? '' : 'es');
        }
        const footer = el('div', { id: 'bowire-sidebar-footer', className: 'bowire-sidebar-footer' },
            el('span', { className: 'bowire-service-count-label', textContent: footerLabel }),
            el('button', {
                id: 'bowire-tour-btn',
                className: 'bowire-tour-btn',
                textContent: 'Take Tour',
                onClick: startTour
            })
        );
        sidebar.appendChild(footer);
        return sidebar;
    }

