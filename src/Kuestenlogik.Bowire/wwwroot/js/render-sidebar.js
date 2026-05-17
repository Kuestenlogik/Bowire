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
                        } else if (newUrl !== url) {
                            serverUrls[idx] = newUrl;
                            delete connectionStatuses[url];
                            connectionStatuses[newUrl] = 'connecting';
                        }
                        persistServerUrls();
                        render();
                        fetchServices();
                    }, function () {
                        // Remove button
                        serverUrls.splice(idx, 1);
                        delete connectionStatuses[url];
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
     * Renders a single editable URL row with status dot, text input, and
     * (optional) remove button. Calls onCommit with the trimmed value when
     * the user presses Enter or blurs the input.
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

        var input = el('input', {
            className: 'bowire-url-input' + (locked ? ' locked' : ''),
            type: 'text',
            value: url,
            placeholder: 'https://api.example.com/openapi.json',
            readOnly: locked ? 'readonly' : undefined,
            title: locked ? 'URL is fixed via --url parameter' : 'Discovery URL — gRPC server, OpenAPI doc, SignalR hub, ...',
            onKeydown: locked ? undefined : function (e) {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    onCommit(e.target.value.trim());
                }
            },
            onBlur: locked ? undefined : function (e) {
                var v = e.target.value.trim();
                if (v !== url) onCommit(v);
            }
        });
        row.appendChild(input);

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

    function renderSourceSelector() {
        // Locked mode → no tabs, no upload, just the locked URL row.
        if (config.lockServerUrl) {
            return renderUrlBarRow(true);
        }

        var wrap = el('div', { id: 'bowire-sidebar-source-selector', className: 'bowire-source-selector' });

        // Tab pills
        var tabs = el('div', { className: 'bowire-source-tabs', role: 'tablist' });
        var urlTab = el('button', {
            id: 'bowire-source-tab-url',
            className: 'bowire-source-tab' + (sourceMode === 'url' ? ' active' : ''),
            role: 'tab',
            onClick: function () {
                if (sourceMode === 'url') return;
                setSourceMode('url');
                selectedMethod = null;
                selectedService = null;
                requestTabs = [];
                activeTabId = null;
                render();
            }
        },
            el('span', { className: 'bowire-source-tab-icon', innerHTML: svgIcon('connect') }),
            el('span', { textContent: 'Server URL' })
        );
        var protoTab = el('button', {
            id: 'bowire-source-tab-proto',
            className: 'bowire-source-tab' + (sourceMode === 'proto' ? ' active' : ''),
            role: 'tab',
            onClick: function () {
                if (sourceMode === 'proto') return;
                setSourceMode('proto');
                selectedMethod = null;
                selectedService = null;
                requestTabs = [];
                activeTabId = null;
                render();
            }
        },
            el('span', { className: 'bowire-source-tab-icon', innerHTML: svgIcon('upload') }),
            el('span', { textContent: 'Schema Files' })
        );
        tabs.appendChild(urlTab);
        tabs.appendChild(protoTab);
        wrap.appendChild(tabs);

        // Tab content — ID includes sourceMode so morphdom replaces the
        // content when switching between URL and proto-upload views.
        var content = el('div', { id: 'bowire-source-content-' + sourceMode, className: 'bowire-source-content' });
        if (sourceMode === 'proto') {
            content.appendChild(renderProtoUploadPanel());
        } else {
            content.appendChild(renderUrlBarRow(false));
        }
        wrap.appendChild(content);

        return wrap;
    }

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
                        dataset: { type: methodBadgeType(row.method) },
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
        var envs = getEnvironments();
        var activeId = getActiveEnvId();
        var globals = getGlobalVars();

        // Default selection: active env, or first env, or globals
        if (!envSidebarSelectedId) {
            envSidebarSelectedId = activeId || (envs.length > 0 ? envs[0].id : '__globals__');
        }

        // Header with + button
        var header = el('div', { className: 'bowire-env-sidebar-header' },
            el('span', { className: 'bowire-env-sidebar-title', textContent: 'Environments' }),
            el('div', { className: 'bowire-env-sidebar-actions' },
                el('button', {
                    id: 'bowire-env-sidebar-create-btn',
                    className: 'bowire-env-sidebar-action-btn',
                    title: 'Create new environment',
                    'aria-label': 'Create new environment',
                    innerHTML: svgIcon('plus'),
                    onClick: function () {
                        var env = createEnvironment('New Environment');
                        envSidebarSelectedId = env.id;
                        render();
                    }
                })
            )
        );
        list.appendChild(header);

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

        // Environment list
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

    function renderSidebar() {
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
        const sidebar = el('div', { className: `bowire-sidebar ${sidebarCollapsed ? 'collapsed' : ''}` });

        // Source selector — URL input (with reflection) or schema file upload.
        // Hidden in pure embedded mode because neither control is useful
        // there: the host has already exposed its services via the in-process
        // EndpointDataSource, there's no remote URL to reflect against, and
        // an uploaded schema wouldn't route anywhere.
        //
        //   pure embedded = not locked
        //                 AND no URLs configured / added
        //                 AND initial discovery already succeeded with ≥1 service
        //
        // The initial-loading guard matters: during the very first fetch the
        // service list is empty, so without it the selector would briefly
        // flash in before being hidden once the embedded discovery returns.
        //
        // Fallthrough cases that still show the selector:
        //   - Locked mode (--url): renderSourceSelector() returns just the
        //     locked URL row, no tabs — the user sees what's pinned.
        //   - Standalone tool without --url: embedded discovery turns up
        //     nothing, so services.length stays 0 and the user gets the URL
        //     input to add their first target.
        //   - Multi-URL setups: serverUrls.length > 0, selector stays.
        // Source selector visibility is determined by the static uiMode
        // (set once at boot from the host config) — no runtime heuristic,
        // no flash of URL-bar-then-hide. In embedded mode the URL bar
        // stays hidden because discovery runs in-process.
        if (uiMode !== 'embedded') {
            sidebar.appendChild(renderSourceSelector());
        }

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
        var favCountForSwitch = getFavorites().length;
        var servicesCountForSwitch = getFilteredServices().reduce(
            function (acc, s) { return acc + (s.methods ? s.methods.length : 0); }, 0);
        var viewSwitch = el('div', { id: 'bowire-sidebar-view-switch', className: 'bowire-view-switch', role: 'tablist' });
        viewSwitch.appendChild(el('button', {
            id: 'bowire-view-pill-services',
            className: 'bowire-view-pill' + (sidebarView === 'services' ? ' active' : ''),
            role: 'tab',
            'aria-label': 'Services view',
            'aria-selected': sidebarView === 'services' ? 'true' : 'false',
            onClick: function () {
                if (sidebarView === 'services') return;
                setSidebarView('services');
                render();
            }
        },
            el('span', { className: 'bowire-view-pill-icon', innerHTML: svgIcon('list') }),
            el('span', { textContent: 'Services' }),
            servicesCountForSwitch > 0
                ? el('span', { className: 'bowire-view-pill-badge', textContent: String(servicesCountForSwitch) })
                : null
        ));
        viewSwitch.appendChild(el('button', {
            id: 'bowire-view-pill-favorites',
            className: 'bowire-view-pill' + (sidebarView === 'favorites' ? ' active' : ''),
            role: 'tab',
            'aria-label': 'Favorites view',
            'aria-selected': sidebarView === 'favorites' ? 'true' : 'false',
            onClick: function () {
                if (sidebarView === 'favorites') return;
                setSidebarView('favorites');
                render();
            }
        },
            el('span', { className: 'bowire-view-pill-icon', innerHTML: svgIcon('starFilled') }),
            el('span', { textContent: 'Favorites' }),
            favCountForSwitch > 0
                ? el('span', { className: 'bowire-view-pill-badge', textContent: String(favCountForSwitch) })
                : null
        ));

        var envCountForSwitch = getEnvironments().length;
        viewSwitch.appendChild(el('button', {
            id: 'bowire-view-pill-environments',
            className: 'bowire-view-pill' + (sidebarView === 'environments' ? ' active' : ''),
            role: 'tab',
            'aria-label': 'Environments view',
            'aria-selected': sidebarView === 'environments' ? 'true' : 'false',
            onClick: function () {
                if (sidebarView === 'environments') return;
                setSidebarView('environments');
                render();
            }
        },
            el('span', { className: 'bowire-view-pill-icon', innerHTML: svgIcon('globe') }),
            el('span', { textContent: 'Environments' }),
            envCountForSwitch > 0
                ? el('span', { className: 'bowire-view-pill-badge', textContent: String(envCountForSwitch) })
                : null
        ));

        var flowCountForSwitch = flowsList.length;
        viewSwitch.appendChild(el('button', {
            id: 'bowire-view-pill-flows',
            className: 'bowire-view-pill' + (sidebarView === 'flows' ? ' active' : ''),
            role: 'tab',
            'aria-label': 'Flows view',
            'aria-selected': sidebarView === 'flows' ? 'true' : 'false',
            onClick: function () {
                if (sidebarView === 'flows') return;
                setSidebarView('flows');
                render();
            }
        },
            el('span', { className: 'bowire-view-pill-icon', innerHTML: svgIcon('play') }),
            el('span', { textContent: 'Flows' }),
            flowCountForSwitch > 0
                ? el('span', { className: 'bowire-view-pill-badge', textContent: String(flowCountForSwitch) })
                : null
        ));

        // Tier-3 Stage C — captured-flows view (sidecar `bowire proxy` API).
        // Badge reflects the live snapshot count; the SSE subscriber
        // updates it as flows land.
        var proxyCountForSwitch = (typeof proxyFlows !== 'undefined' && proxyFlows) ? proxyFlows.length : 0;
        viewSwitch.appendChild(el('button', {
            id: 'bowire-view-pill-proxy',
            className: 'bowire-view-pill' + (sidebarView === 'proxy' ? ' active' : ''),
            role: 'tab',
            'aria-label': 'Proxy view',
            'aria-selected': sidebarView === 'proxy' ? 'true' : 'false',
            title: 'Captured HTTP/HTTPS traffic from `bowire proxy`',
            onClick: function () {
                if (sidebarView === 'proxy') return;
                setSidebarView('proxy');
                if (typeof bowireProxyConnect === 'function') bowireProxyConnect();
                render();
            }
        },
            el('span', { className: 'bowire-view-pill-icon', innerHTML: svgIcon('connect') }),
            el('span', { textContent: 'Proxy' }),
            proxyCountForSwitch > 0
                ? el('span', { className: 'bowire-view-pill-badge', textContent: String(proxyCountForSwitch) })
                : null
        ));

        // "+" button — always visible in the tab row, all views
        viewSwitch.appendChild(el('span', { style: 'flex:1' }));
        var newBtnWrapper = el('div', { className: 'bowire-new-btn-wrapper' });
        var newBtn = el('button', {
            id: 'bowire-new-btn',
            className: 'bowire-new-btn' + (freeformRequest ? ' active' : ''),
            title: 'New request, collection, or environment',
            'aria-label': 'New request, collection, or environment',
            onClick: function (e) {
                e.stopPropagation();
                var menu = newBtnWrapper.querySelector('.bowire-new-dropdown');
                if (menu) { menu.remove(); return; }
                var dropdown = el('div', { className: 'bowire-new-dropdown' });

                // Protocol-specific new request entries
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
                                freeformRequest = {
                                    protocol: p.id,
                                    serverUrl: serverUrls.length > 0 ? serverUrls[0] : '',
                                    service: '', method: '', body: '{}', metadata: {},
                                    methodType: 'Unary',
                                    mockResponse: '', mockStatus: 'OK'
                                };
                                selectedMethod = null;
                                selectedService = null;
                                dropdown.remove();
                                render();
                            }
                        },
                            p.icon ? el('span', { className: 'bowire-new-dropdown-icon', innerHTML: p.icon }) : null,
                            el('span', { textContent: p.name })
                        ));
                    })(protoList[pi]);
                }

                // Divider
                dropdown.appendChild(el('div', { className: 'bowire-new-dropdown-divider' }));

                // Collection
                dropdown.appendChild(el('div', {
                    className: 'bowire-new-dropdown-item',
                    onClick: function () {
                        var col = createCollection();
                        collectionManagerOpen = true;
                        collectionManagerSelectedId = col.id;
                        dropdown.remove();
                        renderCollectionManager();
                    }
                }, el('span', { textContent: 'Collection' })));

                // Environment
                dropdown.appendChild(el('div', {
                    className: 'bowire-new-dropdown-item',
                    onClick: function () {
                        var env = createEnvironment('New Environment');
                        envSidebarSelectedId = env.id;
                        setSidebarView('environments');
                        dropdown.remove();
                        render();
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
        // multiple method types — either dimension gives the user
        // something useful to narrow down.
        var allMethodTypes = new Set();
        for (var ats = 0; ats < services.length; ats++) {
            if (!services[ats].methods) continue;
            for (var atm = 0; atm < services[ats].methods.length; atm++) {
                allMethodTypes.add(services[ats].methods[atm].methodType || 'Unary');
            }
        }
        if (viewSwitchActiveProtocols.length > 1 || allMethodTypes.size > 1) {
            var filterDisabled = sidebarView !== 'services';
            var filterBtnWrapperTop = el('div', { className: 'bowire-protocol-filter-wrapper' });
            var totalFilterCount = protocolFilter.size + methodTypeFilter.size;
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
                    popupTop.appendChild(el('div', { className: 'bowire-protocol-filter-popup-header' },
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
            svcToolbar.appendChild(filterBtnWrapperTop);
        }

        // Chip row inside the toolbar — shown when a filter is active
        // (protocol and/or name). The filter button that opens the
        // popup lives up in the view-switch row, so this row is purely
        // a "here's what's currently narrowing the list" display.
        // When nothing is active, the row is omitted entirely and the
        // sidebar doesn't pay vertical space for an empty container.
        if (sidebarView === 'services') {
            var anyFilterActive = protocolFilter.size > 0 || methodTypeFilter.size > 0 || !!nameFilter;
            if (anyFilterActive) {
                var filterRow = el('div', {
                    id: 'bowire-sidebar-filter-row',
                    className: 'bowire-sidebar-filter-row'
                });

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
        } else if (services.length === 0) {
            // Spinner only while discovery is in flight; once
            // discovery has resolved, an empty service list means the
            // landing card on the right is showing the proper empty
            // state and the sidebar should match it (no spinner).
            if (isLoadingServices) {
                list.appendChild(el('div', { className: 'bowire-loading' },
                    el('div', { className: 'bowire-spinner' }),
                    el('span', { className: 'bowire-loading-text', textContent: 'Loading services...' })
                ));
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

            for (const svc of visibleServices) {
                var baseMethods = svc.methods;
                // Method-type filter (Unary / SS / CS / DX). Applied
                // before the text search so the count reflects types
                // the user actually wants to see.
                if (methodTypeFilter.size > 0) {
                    baseMethods = baseMethods.filter(function (m) {
                        return methodTypeFilter.has(m.methodType || 'Unary');
                    });
                }
                const filteredMethods = query
                    ? baseMethods.filter(function (m) { return methodMatchesAllTokens(svc, m); })
                    : baseMethods;

                if (filteredMethods.length === 0) continue;

                const isExpanded = expandedServices.has(svc.name) || (query && filteredMethods.length > 0);
                const shortName = svc.name.split('.').pop();

                // Stable id for morphdom keyed matching so the click
                // handlers survive re-renders without re-binding.
                var safeGroupId = 'bowire-svc-' + svc.name.replace(/[^a-zA-Z0-9_-]/g, '_');
                const group = el('div', { id: safeGroupId, className: 'bowire-service-group' });

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

                // Protocol icon (leading) — only when more than one
                // protocol is loaded; with a single plugin the icon
                // would just repeat for every group and waste space.
                // Helps the user tell mixed gRPC / REST / Kafka /
                // DIS / Akka sidebars apart at a glance.
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

                headerEl.appendChild(el('span', { className: 'bowire-service-name', textContent: shortName, title: svc.name }));
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
                        onClick: function () {
                            openTab(svc, m);
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
                        executing ? el('span', {
                            className: 'bowire-method-executing',
                            innerHTML: svgIcon('play'),
                            title: 'Currently executing'
                        }) : null,
                        el('span', {
                            className: 'bowire-method-badge',
                            dataset: { type: methodBadgeType(m) },
                            textContent: methodBadgeText(m)
                        })
                    );
                    methodList.appendChild(item);
                }
                group.appendChild(methodList);
                list.appendChild(group);
            }
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

