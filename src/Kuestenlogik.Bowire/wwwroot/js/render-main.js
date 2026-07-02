    // #279 — Workspaces overview drag-drop state. Drives the manual-
    // sort reordering in _renderWorkspacesOverview. Single slot
    // because at most one row drag is in flight at a time. Lives
    // outside any render function so the drag's lifecycle (start →
    // move → drop) survives the re-renders fired by morphdom while
    // the operator is mid-drag.
    let _workspaceDragState = null;

    // ---- Freeform Request Builder ----
    //
    // Built deliberately against the discovered-method pane's chrome
    // (.bowire-main → .bowire-header → .bowire-content[data-split] →
    // .bowire-action-bar) so a freeform request reads like a discovered
    // method's pane with every field made editable. Sections, in
    // top-to-bottom order:
    //
    //   1. Header (.bowire-header)
    //      - Info column: editable Method name (big) + editable Service
    //        name (small, below). Mirrors .bowire-header-method /
    //        .bowire-header-summary-row from the discovered path.
    //      - Right cluster: protocol icon-popover, Type segmented bar,
    //        '+ Add to…' menu, Cancel.
    //   2. URL row — outside the tabs because URL is identity, not a
    //      payload part (the URL determines which tabs make sense).
    //      Carries the Inline / From-Source mode toggle from #252.
    //   3. Content split (.bowire-content data-split) with the standard
    //      pane chrome + draggable divider.
    //      - Request pane: tab strip Payload / Metadata / Mock + tab
    //        content (Body editor for Payload; key/value rows for
    //        Metadata; status + response body for Mock).
    //      - Response pane: last response output or empty hint.
    //   4. Action bar at the bottom with Execute (primary) + Save as
    //      Mock Step + Save to Collection.
    // Append the freeform builder's children directly into the
    // caller's main pane, so they sit at the same DOM depth as the
    // discovered-method's header / content / action-bar instead of
    // being wrapped in an extra .bowire-main container.
    function _appendFreeformInto(pane) {
        var fr = freeformRequest;

        // ----- Protocol options + icon helpers (used by header) -----
        var protoOptions = protocols.length > 0
            ? protocols
            : [{ id: 'grpc', name: 'gRPC' }, { id: 'rest', name: 'REST' }, { id: 'graphql', name: 'GraphQL' },
               { id: 'signalr', name: 'SignalR' }, { id: 'mqtt', name: 'MQTT' }, { id: 'websocket', name: 'WebSocket' }];
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
        function _protoIconHtml(p) { return p.icon || _protoFallbackIcon(p.id); }
        var currentProto = protoOptions.find(function (p) { return p.id === fr.protocol; })
            || protoOptions[0];

        // ----- 1. Header (.bowire-header for chrome parity) -----
        var header = el('div', { className: 'bowire-header bowire-freeform-header' });

        // Info column: editable method name (big) + editable service
        // name (small, below). Same .bowire-header-info /
        // .bowire-header-method classes as discovered so spacing,
        // truncation, font weights all inherit.
        //
        // #256 — REST-specific Postman-style layout: when protocol is
        // 'rest' the service field is hidden (no RPC-method identity
        // for HTTP) and the method input becomes a verb-segmented bar
        // alongside the URL (rendered in the URL-bar section below).
        // The fr.method field is reused: it stores the HTTP verb
        // ('GET' / 'POST' / …) which the server-side ad-hoc routing
        // (BowireRestProtocol.InvokeAsync) picks up.
        var isRest = fr.protocol === 'rest';
        var info = el('div', { className: 'bowire-header-info' });
        if (isRest) {
            // Compact REST identity: just the verb chip + URL preview
            // so the title strip reads as 'POST https://api/foo'
            // without the service/method input fields. Verb picker
            // lives in the URL bar below to keep the header clean.
            var verbForTitle = (fr.method && fr.method.trim()) ? fr.method.trim().toUpperCase() : 'GET';
            info.appendChild(el('div', {
                className: 'bowire-header-method bowire-freeform-rest-title',
                title: 'Ad-hoc REST — pick verb + URL below'
            },
                el('span', { className: 'bowire-freeform-rest-verb-chip', textContent: verbForTitle }),
                el('span', { className: 'bowire-freeform-rest-url-preview',
                    textContent: fr.serverUrl || 'enter URL below' })
            ));
        } else {
            info.appendChild(el('input', {
                id: 'bowire-freeform-method-input',
                type: 'text',
                className: 'bowire-header-method bowire-freeform-header-method-input',
                value: fr.method || '',
                placeholder: 'methodName',
                spellcheck: 'false',
                onInput: function (e) { fr.method = e.target.value; }
            }));
            var serviceRow = el('div', { className: 'bowire-header-summary-row' });
            serviceRow.appendChild(el('input', {
                id: 'bowire-freeform-service-input',
                type: 'text',
                className: 'bowire-header-summary bowire-freeform-header-service-input',
                value: fr.service || '',
                placeholder: 'service (optional)',
                spellcheck: 'false',
                title: (fr._lineageHint && fr._lineageHint.sourceMethod)
                    ? 'Cloned from ' + fr._lineageHint.sourceMethod
                    : '',
                onInput: function (e) { fr.service = e.target.value; }
            }));
            info.appendChild(serviceRow);
        }
        header.appendChild(info);

        // Right cluster: protocol picker (icon + popover) + method-type
        // segmented bar + '+ Add to…' menu + Cancel.
        var rightCluster = el('div', { className: 'bowire-freeform-header-right' });

        // Protocol picker — icon button + popover with all protocols
        // (icon + name per row).
        var protoPickerWrap = el('div', { className: 'bowire-freeform-proto-picker-wrap' });
        var protoBtn = el('button', {
            type: 'button',
            id: 'bowire-freeform-protocol-btn',
            className: 'bowire-freeform-proto-btn' + (freeformProtocolPickerOpen ? ' is-open' : ''),
            title: currentProto.name + ' — click to switch protocol',
            'aria-haspopup': 'listbox',
            'aria-expanded': freeformProtocolPickerOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                freeformProtocolPickerOpen = !freeformProtocolPickerOpen;
                render();
            }
        },
            el('span', { className: 'bowire-freeform-proto-btn-icon', innerHTML: _protoIconHtml(currentProto) }),
            el('span', { className: 'bowire-freeform-proto-btn-caret', innerHTML: svgIcon('chevronDown') })
        );
        protoPickerWrap.appendChild(protoBtn);
        if (freeformProtocolPickerOpen) {
            var protoMenu = el('div', {
                className: 'bowire-freeform-proto-menu',
                role: 'listbox',
                onClick: function (e) { e.stopPropagation(); }
            });
            protoOptions.forEach(function (p) {
                protoMenu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-freeform-proto-menu-item'
                        + (p.id === fr.protocol ? ' is-selected' : ''),
                    role: 'option',
                    'aria-selected': p.id === fr.protocol ? 'true' : 'false',
                    onClick: function () {
                        fr.protocol = p.id;
                        var supported = getSupportedMethodTypes(fr.protocol);
                        if (supported.indexOf(fr.methodType) < 0) fr.methodType = supported[0];
                        freeformProtocolPickerOpen = false;
                        render();
                    }
                },
                    el('span', { className: 'bowire-freeform-proto-menu-icon', innerHTML: _protoIconHtml(p) }),
                    el('span', { className: 'bowire-freeform-proto-menu-label', textContent: p.name })
                ));
            });
            protoPickerWrap.appendChild(protoMenu);
        }
        rightCluster.appendChild(protoPickerWrap);

        // Method-type segmented bar — compact in the header cluster.
        // Suppressed for REST since REST is always Unary; the
        // verb-picker in the URL bar carries the meaningful choice.
        var supportedTypes = getSupportedMethodTypes(fr.protocol);
        if (isRest) {
            // skip
        } else if (supportedTypes.length > 1) {
            var typeSeg = el('div', { className: 'bowire-freeform-type-seg bowire-freeform-type-seg-compact' });
            supportedTypes.forEach(function (t) {
                typeSeg.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-freeform-type-seg-btn'
                        + (t === fr.methodType ? ' is-active' : ''),
                    title: t,
                    textContent: t,
                    onClick: function () {
                        if (fr.methodType === t) return;
                        fr.methodType = t;
                        render();
                    }
                }));
            });
            rightCluster.appendChild(typeSeg);
        } else {
            rightCluster.appendChild(el('span', {
                className: 'bowire-freeform-type-chip',
                textContent: supportedTypes[0]
            }));
        }

        // '+ Add to…' collection picker (self-contained snapshot per
        // #246). Same dropdown UX as the discovered-method header.
        var freeformAddToWrap = el('div', { className: 'bowire-header-addto-wrap' });
        freeformAddToWrap.appendChild(el('button', {
            id: 'bowire-freeform-addto-btn',
            className: 'bowire-header-addto-btn' + (freeformAddToMenuOpen ? ' active' : ''),
            title: 'Add this request to a collection',
            'aria-label': 'Add to collection',
            'aria-haspopup': 'menu',
            'aria-expanded': freeformAddToMenuOpen ? 'true' : 'false',
            innerHTML: svgIcon('plus'),
            onClick: function (e) {
                e.stopPropagation();
                freeformAddToMenuOpen = !freeformAddToMenuOpen;
                render();
            }
        }));
        if (freeformAddToMenuOpen) {
            var addToMenu = el('div', {
                className: 'bowire-header-addto-menu',
                role: 'menu',
                onClick: function (e) { e.stopPropagation(); }
            });
            function _snapshotFreeform() {
                var urlMode = fr.urlMode === 'source' ? 'source' : 'inline';
                return {
                    service: fr.service || '',
                    method: fr.method || '',
                    methodType: fr.methodType || 'Unary',
                    protocol: fr.protocol || 'rest',
                    body: fr.body || '{}',
                    messages: [fr.body || '{}'],
                    metadata: (fr.metadata && Object.keys(fr.metadata).length > 0) ? fr.metadata : null,
                    serverUrl: fr.serverUrl || null,
                    urlMode: urlMode,
                    kind: 'ad-hoc',
                    lineage: fr._lineageHint || { kind: 'compose' }
                };
            }
            addToMenu.appendChild(el('div', { className: 'bowire-header-addto-section', textContent: 'Collection' }));
            var freefCols = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList))
                ? collectionsList : [];
            if (freefCols.length > 0) {
                freefCols.slice(0, 6).forEach(function (col) {
                    addToMenu.appendChild(el('button', {
                        className: 'bowire-header-addto-item',
                        role: 'menuitem',
                        onClick: function () {
                            var snap = _snapshotFreeform();
                            if (typeof addToCollection === 'function') {
                                addToCollection(col.id, snap);
                                if (typeof toast === 'function') {
                                    toast('Added to "' + col.name + '"', 'success');
                                }
                            }
                            freeformAddToMenuOpen = false;
                            render();
                        }
                    },
                        el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('folder') }),
                        el('span', { textContent: col.name }),
                        el('span', { className: 'bowire-header-addto-item-meta', textContent: (col.items || []).length + ((col.items || []).length === 1 ? ' entry' : ' entries') })
                    ));
                });
                if (freefCols.length > 6) {
                    addToMenu.appendChild(el('div', {
                        className: 'bowire-header-addto-item-meta',
                        style: 'padding:4px 12px 0',
                        textContent: '+ ' + (freefCols.length - 6) + ' more in Collections'
                    }));
                }
            }
            addToMenu.appendChild(el('button', {
                className: 'bowire-header-addto-item bowire-header-addto-item-create',
                role: 'menuitem',
                onClick: function () {
                    if (typeof bowirePrompt !== 'function') return;
                    bowirePrompt('Collection name', {
                        title: 'New collection',
                        placeholder: 'e.g. Smoke tests',
                        confirmText: 'Create'
                    }).then(function (name) {
                        if (name === null) return;
                        var trimmed = String(name || '').trim();
                        if (typeof createCollection !== 'function'
                            || typeof addToCollection !== 'function') return;
                        var col = createCollection(trimmed || undefined);
                        var snap = _snapshotFreeform();
                        addToCollection(col.id, snap);
                        if (typeof toast === 'function') {
                            toast('Saved to "' + col.name + '"', 'success');
                        }
                        freeformAddToMenuOpen = false;
                        render();
                    });
                }
            },
                el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('plus') }),
                el('span', { textContent: 'New collection…' })
            ));
            freeformAddToWrap.appendChild(addToMenu);
        }
        rightCluster.appendChild(freeformAddToWrap);

        // Cancel button — discards the freeform draft.
        rightCluster.appendChild(el('button', {
            id: 'bowire-freeform-cancel-btn',
            className: 'bowire-header-action-btn',
            textContent: 'Cancel',
            title: (fr._lineageHint && fr._lineageHint.sourceMethod)
                ? 'Cancel — cloned from ' + fr._lineageHint.sourceMethod
                : 'Discard this draft',
            onClick: cancelFreeformRequest
        }));

        header.appendChild(rightCluster);
        pane.appendChild(header);

        // ----- 2. URL row (identity-level, outside the tabs) -----
        // URL is part of "what the call hits" — it determines which
        // tabs make sense. Inline / From-Source toggle from #252.
        //
        // #256 — REST mode prepends a verb-segmented bar (GET / POST /
        // PUT / DELETE / PATCH / HEAD / OPTIONS) so the operator
        // composes a single identity row 'POST https://api/foo' in
        // the canonical Postman shape. fr.method holds the verb; the
        // server-side BowireRestProtocol.InvokeAsync detects the
        // ad-hoc shape (empty service + verb method) and routes to
        // RestInvoker.InvokeAdHocAsync.
        var urlBar = el('div', { className: 'bowire-freeform-url-bar' });
        if (isRest) {
            // Default the verb when entering REST mode without one set.
            var REST_VERBS = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD', 'OPTIONS'];
            if (!fr.method || REST_VERBS.indexOf((fr.method || '').toUpperCase()) < 0) {
                fr.method = 'GET';
            }
            var verbSeg = el('div', { className: 'bowire-freeform-verb-seg' });
            REST_VERBS.forEach(function (v) {
                verbSeg.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-freeform-verb-seg-btn bowire-freeform-verb-' + v.toLowerCase()
                        + (fr.method.toUpperCase() === v ? ' is-active' : ''),
                    title: v,
                    textContent: v,
                    onClick: function () {
                        if (fr.method.toUpperCase() === v) return;
                        fr.method = v;
                        // Clear the service field — ad-hoc REST has no
                        // RPC service identity, only the verb + URL.
                        fr.service = '';
                        render();
                    }
                }));
            });
            urlBar.appendChild(verbSeg);
        } else {
            urlBar.appendChild(el('span', {
                className: 'bowire-freeform-url-bar-label',
                textContent: 'URL'
            }));
        }
        if (fr.urlMode !== 'source') fr.urlMode = 'inline';
        var hasAnySources = (typeof serverUrls !== 'undefined'
            && Array.isArray(serverUrls) && serverUrls.length > 0);
        var modeWrap = el('div', { className: 'bowire-freeform-url-mode-wrap' });
        function _setUrlMode(next) {
            if (fr.urlMode === next) return;
            if (fr.urlMode === 'inline' && next === 'source'
                && fr.serverUrl
                && hasAnySources
                && serverUrls.indexOf(fr.serverUrl) < 0) {
                if (typeof bowireConfirm === 'function') {
                    bowireConfirm(
                        'Save "' + fr.serverUrl + '" as a workspace Source URL first?',
                        function () {
                            try {
                                if (serverUrls.indexOf(fr.serverUrl) < 0) {
                                    serverUrls.push(fr.serverUrl);
                                    if (typeof persistServerUrls === 'function') persistServerUrls();
                                }
                            } catch { /* ignore */ }
                            fr.urlMode = 'source';
                            render();
                        },
                        {
                            title: 'Switch to Source mode',
                            confirmText: 'Save & switch',
                            cancelText: 'Discard & switch',
                            onCancel: function () {
                                fr.serverUrl = hasAnySources ? serverUrls[0] : '';
                                fr.urlMode = 'source';
                                render();
                            }
                        }
                    );
                    return;
                }
            }
            fr.urlMode = next;
            if (next === 'source') {
                if (!hasAnySources) { fr.serverUrl = ''; }
                else if (serverUrls.indexOf(fr.serverUrl) < 0) { fr.serverUrl = serverUrls[0]; }
            }
            render();
        }
        // #256 — Inline/From-Source toggle suppressed for REST. Ad-hoc
        // REST URLs are always full request URLs; there's no central
        // 'source' concept to bind against (operators often don't even
        // have an OpenAPI spec — the whole point of ad-hoc REST is
        // typing the call URL directly). For other protocols (gRPC,
        // GraphQL, MQTT, …) the toggle stays since those bind to
        // workspace-managed base URLs.
        if (!isRest) {
            modeWrap.appendChild(el('button', {
                type: 'button',
                className: 'bowire-freeform-url-mode-btn' + (fr.urlMode === 'inline' ? ' is-active' : ''),
                title: 'Self-contained URL — lives inline on this request',
                textContent: 'Inline',
                onClick: function () { _setUrlMode('inline'); }
            }));
            modeWrap.appendChild(el('button', {
                type: 'button',
                className: 'bowire-freeform-url-mode-btn' + (fr.urlMode === 'source' ? ' is-active' : ''),
                title: hasAnySources
                    ? 'Bind URL to a workspace-managed Source'
                    : 'No Source URLs in this workspace yet',
                disabled: !hasAnySources,
                textContent: 'From Source',
                onClick: function () { _setUrlMode('source'); }
            }));
            urlBar.appendChild(modeWrap);
        }
        if (!isRest && fr.urlMode === 'source' && hasAnySources) {
            var sourceSelect = el('select', {
                id: 'bowire-freeform-url-select',
                className: 'bowire-freeform-url-source-select bowire-freeform-url-bar-input',
                onChange: function (e) { fr.serverUrl = e.target.value; }
            });
            serverUrls.forEach(function (url) {
                var opt = el('option', { value: url, textContent: url });
                if (url === fr.serverUrl) opt.selected = true;
                sourceSelect.appendChild(opt);
            });
            urlBar.appendChild(sourceSelect);
        } else {
            urlBar.appendChild(el('input', {
                id: 'bowire-freeform-url-input',
                type: 'text',
                className: 'bowire-freeform-url-bar-input',
                value: fr.serverUrl,
                placeholder: isRest
                    ? 'https://api.example.com/users/123 — full request URL'
                    : 'https://api.example.com or mqtt://broker:1883',
                spellcheck: 'false',
                onInput: function (e) { fr.serverUrl = e.target.value; }
            }));
        }
        pane.appendChild(urlBar);

        // ----- 3. Content split (.bowire-content data-split) -----
        var splitContent = el('div', {
            className: 'bowire-content bowire-content-enter bowire-freeform-content',
            'data-split': (typeof resolveSplitMode === 'function')
                ? resolveSplitMode(splitMode) : (splitMode || 'horizontal')
        });
        var reqPane = el('div', { className: 'bowire-pane bowire-freeform-req-pane' });
        reqPane.appendChild(el('div', { className: 'bowire-pane-heading', textContent: 'Request' }));
        // Tab strip — Payload / Metadata / Mock, same classes as
        // discovered-method's request-pane tabs.
        var reqTabs = el('div', { id: 'bowire-freeform-request-tabs', className: 'bowire-tabs' });
        function _ffTab(id, label) {
            return el('div', {
                id: 'bowire-freeform-request-tab-' + id,
                className: 'bowire-tab' + (freeformActiveRequestTab === id ? ' active' : ''),
                textContent: label,
                onClick: function () { freeformActiveRequestTab = id; render(); }
            });
        }
        reqTabs.appendChild(_ffTab('body', 'Payload'));
        reqTabs.appendChild(_ffTab('metadata', 'Metadata'));
        reqTabs.appendChild(_ffTab('mock', 'Mock'));
        reqPane.appendChild(reqTabs);
        // Overflow popover — three tabs rarely overflow today, but the
        // request pane gets narrowed via the splitter and we want the
        // same affordance everywhere a horizontal tab strip lives.
        requestAnimationFrame(function () {
            var live = document.getElementById('bowire-freeform-request-tabs');
            if (live && typeof bowireWireTabOverflow === 'function') {
                bowireWireTabOverflow(live, { tabSelector: '.bowire-tab', label: 'More tabs' });
            }
        });

        // Payload tab — body editor.
        var bodyTabContent = el('div', {
            id: 'bowire-freeform-request-tab-content-body',
            className: 'bowire-tab-content' + (freeformActiveRequestTab === 'body' ? ' active' : '')
        });
        var bodyEditor = el('textarea', {
            className: 'bowire-editor',
            placeholder: 'Enter JSON request body…',
            spellcheck: 'false',
            style: 'min-height:200px'
        });
        bodyEditor.value = fr.body;
        bodyEditor.addEventListener('input', function () { fr.body = this.value; });
        bodyTabContent.appendChild(bodyEditor);
        var jsonStatus = el('div', { className: 'bowire-json-status empty' });
        bodyTabContent.appendChild(jsonStatus);
        attachJsonValidator(bodyEditor, jsonStatus);
        reqPane.appendChild(bodyTabContent);

        // Metadata tab — key/value rows, one trailing empty row so
        // the operator can add a new pair without clicking '+' first.
        var metaTabContent = el('div', {
            id: 'bowire-freeform-request-tab-content-metadata',
            className: 'bowire-tab-content' + (freeformActiveRequestTab === 'metadata' ? ' active' : '')
        });
        var metaRowsWrap = el('div', { className: 'bowire-freeform-meta-rows' });
        var metaPairs = [];
        if (fr.metadata && typeof fr.metadata === 'object') {
            Object.keys(fr.metadata).forEach(function (k) {
                metaPairs.push({ key: k, value: String(fr.metadata[k]) });
            });
        }
        metaPairs.push({ key: '', value: '' });
        function _writeMeta() {
            var next = {};
            metaPairs.forEach(function (p) {
                if (p.key && p.key.trim()) next[p.key.trim()] = p.value;
            });
            fr.metadata = next;
        }
        metaPairs.forEach(function (p, idx) {
            var row = el('div', { className: 'bowire-freeform-meta-row' });
            row.appendChild(el('input', {
                type: 'text',
                className: 'bowire-freeform-input',
                value: p.key,
                placeholder: 'Header name',
                spellcheck: 'false',
                onInput: function (e) { metaPairs[idx].key = e.target.value; _writeMeta(); }
            }));
            row.appendChild(el('input', {
                type: 'text',
                className: 'bowire-freeform-input',
                value: p.value,
                placeholder: 'Value',
                spellcheck: 'false',
                onInput: function (e) { metaPairs[idx].value = e.target.value; _writeMeta(); }
            }));
            metaRowsWrap.appendChild(row);
        });
        metaTabContent.appendChild(metaRowsWrap);
        reqPane.appendChild(metaTabContent);

        // Mock tab — status + response body editor.
        var mockTabContent = el('div', {
            id: 'bowire-freeform-request-tab-content-mock',
            className: 'bowire-tab-content' + (freeformActiveRequestTab === 'mock' ? ' active' : '')
        });
        var mockStatusRow = el('div', { className: 'bowire-freeform-row' });
        mockStatusRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Status' }));
        mockStatusRow.appendChild(el('input', {
            id: 'bowire-freeform-mock-status-input',
            type: 'text',
            className: 'bowire-freeform-input',
            value: fr.mockStatus,
            placeholder: 'OK',
            spellcheck: 'false',
            onInput: function (e) { fr.mockStatus = e.target.value; }
        }));
        mockTabContent.appendChild(mockStatusRow);
        var mockBodyRow = el('div', { className: 'bowire-freeform-row bowire-freeform-body-row' });
        mockBodyRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Response' }));
        var mockEditor = el('textarea', {
            id: 'bowire-freeform-mock-body',
            className: 'bowire-editor',
            placeholder: 'JSON response body the mock server will return…',
            spellcheck: 'false',
            style: 'min-height:160px'
        });
        mockEditor.value = fr.mockResponse;
        mockEditor.addEventListener('input', function () { fr.mockResponse = this.value; });
        mockBodyRow.appendChild(mockEditor);
        var mockJsonStatus = el('div', { className: 'bowire-json-status empty' });
        mockBodyRow.appendChild(mockJsonStatus);
        attachJsonValidator(mockEditor, mockJsonStatus);
        mockTabContent.appendChild(mockBodyRow);
        reqPane.appendChild(mockTabContent);

        // Response pane — last response output, problem+json, or
        // empty hint.
        var resPane = el('div', { className: 'bowire-pane bowire-freeform-res-pane' });
        resPane.appendChild(el('div', { className: 'bowire-pane-heading', textContent: 'Response' }));
        if (responseError || responseData) {
            if (responseError) {
                var errOut = el('div', { className: 'bowire-response-output error' });
                var prob = (typeof responseError === 'object') ? normalizeProblem(responseError) : null;
                if (prob) renderProblem(prob, errOut);
                else errOut.textContent = (typeof responseError === 'string')
                    ? responseError
                    : problemTitle(responseError, 'Request failed');
                resPane.appendChild(errOut);
            } else if (responseData) {
                var output = el('div', { className: 'bowire-response-output is-interactive' });
                output.innerHTML = highlightJsonInteractive(responseData);
                resPane.appendChild(output);
            }
        } else {
            resPane.appendChild(el('div', {
                className: 'bowire-response-empty',
                textContent: 'Execute the request to see the response here.'
            }));
        }
        // Stable id so the rAF callback below can re-resolve the live
        // divider after morphdom merges the new tree in (the local
        // `divider` reference can become detached if morphdom preserved
        // an older sibling). initResizer is idempotent — re-runs on the
        // already-initialised live node are no-ops.
        reqPane.id = 'bowire-freeform-req-pane';
        resPane.id = 'bowire-freeform-res-pane';
        var divider = el('div', {
            id: 'bowire-freeform-pane-divider',
            className: 'bowire-pane-divider'
        });
        splitContent.appendChild(reqPane);
        splitContent.appendChild(divider);
        splitContent.appendChild(resPane);
        pane.appendChild(splitContent);
        requestAnimationFrame(function () {
            var d = document.getElementById('bowire-freeform-pane-divider');
            var l = document.getElementById('bowire-freeform-req-pane');
            var r = document.getElementById('bowire-freeform-res-pane');
            if (d && l && r) initResizer(d, l, r);
        });

        // ----- 4. Action bar (.bowire-action-bar) at the bottom -----
        var actionBar = el('div', { className: 'bowire-action-bar bowire-freeform-action-bar' });
        actionBar.appendChild(el('button', {
            id: 'bowire-freeform-execute-btn',
            className: 'bowire-execute-btn',
            onClick: function () { executeFreeformRequest(); }
        },
            el('span', { innerHTML: svgIcon('play'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Execute' })
        ));
        actionBar.appendChild(el('button', {
            id: 'bowire-freeform-save-mock-btn',
            className: 'bowire-repeat-btn',
            title: 'Save this request + response as a mock step in the active recording',
            onClick: function () { saveFreeformAsMockStep(); }
        },
            el('span', { innerHTML: svgIcon('record'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Save as Mock Step' })
        ));
        actionBar.appendChild(el('button', {
            id: 'bowire-freeform-save-btn',
            className: 'bowire-repeat-btn',
            title: 'Save this request to a collection',
            onClick: function () {
                if (!fr.service || !fr.method) { toast('Enter a service and method name', 'error'); return; }
                if (collectionsList.length === 0) {
                    var col = createCollection();
                    addToCollection(col.id, {
                        protocol: fr.protocol,
                        service: fr.service,
                        method: fr.method,
                        methodType: fr.methodType,
                        body: fr.body,
                        messages: [fr.body],
                        metadata: null,
                        serverUrl: fr.serverUrl,
                        urlMode: fr.urlMode || 'inline'
                    });
                } else {
                    addToCollection(collectionsList[0].id, {
                        protocol: fr.protocol,
                        service: fr.service,
                        method: fr.method,
                        methodType: fr.methodType,
                        body: fr.body,
                        messages: [fr.body],
                        metadata: null,
                        serverUrl: fr.serverUrl,
                        urlMode: fr.urlMode || 'inline'
                    });
                }
                toast('Saved to collection', 'success');
            }
        },
            el('span', { innerHTML: svgIcon('folder'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Save to Collection' })
        ));
        pane.appendChild(actionBar);
    }


    async function executeFreeformRequest() {
        if (!freeformRequest) return;
        var fr = freeformRequest;
        // #256 — REST ad-hoc only needs URL + verb (no service / RPC
        // identity). Other protocols still require service + method.
        var isRestAdHoc = fr.protocol === 'rest';
        if (isRestAdHoc) {
            if (!fr.serverUrl || !fr.serverUrl.trim()) {
                toast('Enter a request URL', 'error');
                return;
            }
            if (!fr.method) {
                toast('Pick an HTTP verb', 'error');
                return;
            }
        } else if (!fr.service || !fr.method) {
            toast('Enter a service and method name', 'error');
            return;
        }

        isExecuting = true;
        responseData = null;
        responseError = null;
        markJobActive(fr.service || 'adhoc', fr.method);
        render();

        // Console label: REST ad-hoc reads as 'POST https://…' since
        // there's no RPC service/method to identify; other protocols
        // keep the 'service/method' shape.
        var fullName = isRestAdHoc
            ? (fr.method + ' ' + (fr.serverUrl || ''))
            : (fr.service + '/' + fr.method);
        // Body sent on the wire: REST ad-hoc skips the {} placeholder
        // for GET/HEAD/OPTIONS so the request log doesn't show a
        // spurious empty-object payload.
        var bodyToSend = fr.body || '{}';
        var verbHasBody = !isRestAdHoc
            || ['POST', 'PUT', 'PATCH', 'DELETE'].indexOf((fr.method || '').toUpperCase()) >= 0;
        addConsoleEntry({ type: 'request', method: fullName, body: verbHasBody ? bodyToSend : '' });

        var statusInfo = null;
        try {
            var url = config.prefix + '/api/invoke'
                + (fr.serverUrl ? '?serverUrl=' + encodeURIComponent(fr.serverUrl) : '');
            // Metadata (headers): pass fr.metadata through for ad-hoc
            // REST so custom Authorization / Accept / X-* headers
            // reach RestInvoker.InvokeAdHocAsync. Other protocols
            // keep null until they grow custom header support.
            var metaToSend = (isRestAdHoc && fr.metadata && Object.keys(fr.metadata).length > 0)
                ? fr.metadata : null;
            var resp = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: fr.service,
                    method: fr.method,
                    messages: [verbHasBody ? bodyToSend : '{}'],
                    metadata: metaToSend,
                    protocol: fr.protocol
                })
            });
            var result = await resp.json();
            if (result.title) {
                responseError = result;
                statusInfo = { status: 'Error', durationMs: result.duration_ms || 0 };
                addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: richErrorDetail(result, 'Request failed') });
            } else {
                responseData = result.response;
                statusInfo = { status: result.status, durationMs: result.duration_ms || 0 };
                if (result.response) captureResponse(result.response);
                addConsoleEntry({ type: 'response', method: fullName, status: result.status, body: result.response });

                // Auto-populate the Mock Response textarea from the real
                // response so the user can save-as-mock with one extra
                // click. Non-destructive: we only fill if empty, so a
                // user who already authored a mock response isn't
                // overwritten by a subsequent Execute.
                if (!fr.mockResponse && typeof result.response === 'string') {
                    fr.mockResponse = result.response;
                    fr.mockStatus = result.status || 'OK';
                }

                // Recorder hook — parity with the discovery-first path
                // in api.js. Silent no-op when no recording is active.
                captureFreeformRecordingStep(fr, {
                    response: result.response,
                    responseBinary: result.response_binary || null,
                    status: statusInfo.status,
                    durationMs: statusInfo.durationMs
                });
            }
        } catch (e) {
            responseError = e.message;
            addConsoleEntry({ type: 'error', method: fullName, status: 'NetworkError', body: e.message });
        }

        isExecuting = false;
        markJobDone(fr.service, fr.method);
        render();
    }

    // Freeze the freeform request + its (real or authored) response
    // into a recording step. When no recording is active, spin up a
    // fresh "Manual mocks" recording and mark it active — the user's
    // next click is enough to build up a whole mock surface, no
    // record-start ceremony first.
    function saveFreeformAsMockStep() {
        if (!freeformRequest) return;
        var fr = freeformRequest;
        if (!fr.service || !fr.method) {
            toast('Enter a service and method name', 'error');
            return;
        }
        var responseText = fr.mockResponse || responseData || '';
        if (!responseText) {
            toast('Enter a Mock Response or click Execute first', 'error');
            freeformMockExpanded = true;
            render();
            return;
        }

        if (!isRecording()) {
            // startRecording already push+persist+set-active; it also
            // renders, but the trailing render() below still runs so
            // the freeform pane re-shows with the captured step list.
            startRecording('Manual mocks');
        }

        captureFreeformRecordingStep(fr, {
            response: responseText,
            responseBinary: null,
            status: fr.mockStatus || 'OK',
            durationMs: 0
        });
        toast('Saved as mock step', 'success');
        render();
    }

    // Shared capture helper for both paths — autopopulated httpPath /
    // httpVerb for REST so Phase-1 mock-server wire matching can pair
    // an incoming request against the recorded step. For non-REST
    // protocols the mock matcher keys on service+method, so leaving
    // these null is the right thing.
    function captureFreeformRecordingStep(fr, extras) {
        var httpPath = null;
        var httpVerb = null;
        if (fr.protocol === 'rest') {
            // Method field in REST freeform is either "GET /path"
            // (Postman-style) or just "/path" with the verb elsewhere.
            // Parse both shapes; fall back to the method text.
            var m = String(fr.method || '').trim().match(/^([A-Z]+)\s+(.+)$/);
            if (m) { httpVerb = m[1]; httpPath = m[2]; }
            else if (fr.method && fr.method.startsWith('/')) {
                httpVerb = 'GET';
                httpPath = fr.method;
            }
        }
        captureRecordingStep({
            protocol: fr.protocol,
            service: fr.service,
            method: fr.method,
            methodType: fr.methodType || 'Unary',
            serverUrl: fr.serverUrl || null,
            body: fr.body || '{}',
            messages: [fr.body || '{}'],
            metadata: null,
            status: extras.status,
            durationMs: extras.durationMs,
            response: extras.response,
            responseBinary: extras.responseBinary,
            httpPath: httpPath,
            httpVerb: httpVerb
        });
    }

    // ---- Environment Editor (full-width main pane) ----
    function renderEnvironmentEditor() {
        var envs = getEnvironments();
        var activeId = getActiveEnvId();
        var isGlobals = envSidebarSelectedId === '__globals__';
        // Resolve from the SHARED env store (not the workspace-
        // Self-contained workspaces: envs are the workspace's own set —
        // no separate "shared store" to consult for fallback lookup.
        var selectedEnv = isGlobals
            ? null
            : envs.find(function (e) { return e.id === envSidebarSelectedId; });
        // ID includes the selected env AND the active tab so morphdom
        // fully replaces the editor when switching between environments
        // or tabs instead of reusing the old DOM with stale closures.
        var envTabKey = isGlobals ? 'variables' : envEditorTab;
        var pane = el('div', {
            id: 'bowire-env-editor-' + (envSidebarSelectedId || 'none') + '-' + envTabKey,
            className: 'bowire-env-editor-main'
        });

        if (!isGlobals && !selectedEnv) {
            pane.appendChild(el('div', { className: 'bowire-env-editor-empty' },
                el('div', { className: 'bowire-env-editor-empty-icon', innerHTML: svgIcon('globe') }),
                el('div', { textContent: 'Select an environment on the left or create a new one' }),
                el('button', {
                    id: 'bowire-env-create-btn',
                    className: 'bowire-env-editor-create-btn',
                    textContent: '+ Create Environment',
                    onClick: function () {
                        if (typeof openCreateEnvironmentDialog !== 'function') return;
                        openCreateEnvironmentDialog(function (env) {
                            envSidebarSelectedId = env.id;
                            render();
                        });
                    }
                })
            ));
            return pane;
        }

        // ---- Header: name + action buttons ----
        var headerRow = el('div', { className: 'bowire-env-editor-header' });

        var envColor = !isGlobals ? (selectedEnv.color || '#6366f1') : null;
        if (isGlobals) {
            headerRow.appendChild(el('span', { className: 'bowire-env-editor-icon', innerHTML: svgIcon('globe') }));
            headerRow.appendChild(el('h2', { className: 'bowire-env-editor-title', textContent: 'Global Variables' }));
        } else {
            // Header mirrors the workspace settings detail: small colour
            // dot leads, name input as the in-place editable title, then
            // action buttons. The colour picker itself moved out of the
            // header into its own labelled section below — same IA the
            // workspace settings surface uses, so the two pick surfaces
            // read as variants of one pattern instead of two.
            headerRow.appendChild(el('span', {
                className: 'bowire-env-editor-glyph',
                // Same filled-globe glyph as the topbar env dropdown +
                // workspace tree env leaf. Matches the workspace
                // settings header's layers glyph in idiom (top "feature"
                // of the icon picked up in the colour, lower lines stay
                // currentColor), so the two settings surfaces read as
                // variants of one pattern.
                innerHTML: '<svg viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" width="24" height="24">'
                    + '<circle cx="12" cy="12" r="10" fill="' + envColor + '"/>'
                    + '<line x1="2" y1="12" x2="22" y2="12" fill="none"/>'
                    + '<path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z" fill="none"/>'
                    + '</svg>'
            }));
            // Title stack: editable env name on top, small navigable
            // trail underneath as the "where am I" subtitle. Matches
            // the workspace settings + env overview header pattern.
            var envOwnerWs = (typeof workspaces !== 'undefined' && Array.isArray(workspaces))
                ? (workspaces.find(function (w) { return w.id === activeWorkspaceId; }) || null)
                : null;
            var envOwnerWsId = envOwnerWs ? envOwnerWs.id : activeWorkspaceId;
            var envOwnerWsName = envOwnerWs ? (envOwnerWs.name || '(unnamed)') : 'Workspace';
            headerRow.appendChild(el('div', { className: 'bowire-ws-detail-title-stack' },
                el('input', {
                    id: 'bowire-env-name-input',
                    type: 'text',
                    // Reuse the workspace settings name input class so the
                    // hover/focus border + max-width behaviour matches
                    // across both surfaces.
                    className: 'bowire-ws-detail-name',
                    value: selectedEnv.name,
                    'aria-label': 'Environment name',
                    'data-bowire-no-vars-chip': '1',
                    'data-bowire-no-vars-ac': '1',
                    onChange: function (e) { updateEnvironment(envSidebarSelectedId, { name: e.target.value }); }
                }),
                _renderHeaderTrail([
                    { label: 'Workspaces', onClick: _goToWorkspacesOverview },
                    { label: envOwnerWsName, onClick: function () { _goToWorkspaceSettings(envOwnerWsId); } },
                    { label: 'Environments', onClick: function () { _goToEnvironmentsOverview(envOwnerWsId); } }
                ])
            ));
        }
        headerRow.appendChild(el('span', { style: 'flex:1' }));

        // Import / Export
        headerRow.appendChild(el('button', {
            id: 'bowire-env-export-btn',
            className: 'bowire-env-editor-action-btn',
            title: 'Export all environments as JSON',
            textContent: 'Export',
            onClick: function () {
                var json = exportEnvironments();
                var blob = new Blob([json], { type: 'application/json' });
                var a = document.createElement('a');
                a.href = URL.createObjectURL(blob);
                a.download = 'bowire-environments.bwe';
                a.click();
                URL.revokeObjectURL(a.href);
            }
        }));
        headerRow.appendChild(el('button', {
            id: 'bowire-env-import-btn',
            className: 'bowire-env-editor-action-btn',
            title: 'Import environments from JSON file',
            textContent: 'Import',
            onClick: function () {
                var input = document.createElement('input');
                input.type = 'file';
                input.accept = '.json,application/json';
                input.onchange = async function () {
                    if (!input.files || !input.files[0]) return;
                    try {
                        var text = await input.files[0].text();
                        importEnvironments(text);
                        toast('Environments imported', 'success');
                        render();
                    } catch (e) {
                        toast('Import failed: ' + e.message, 'error');
                    }
                };
                input.click();
            }
        }));

        if (!isGlobals) {
            if (envSidebarSelectedId !== activeId) {
                headerRow.appendChild(el('button', {
                    id: 'bowire-env-set-active-btn',
                    className: 'bowire-env-editor-action-btn',
                    textContent: 'Set Active',
                    onClick: function () { setActiveEnvId(envSidebarSelectedId); render(); }
                }));
            } else {
                headerRow.appendChild(el('span', {
                    className: 'bowire-env-editor-active-badge',
                    textContent: 'Active'
                }));
            }
            headerRow.appendChild(el('button', {
                id: 'bowire-env-delete-btn',
                className: 'bowire-env-editor-action-btn bowire-env-editor-danger',
                title: 'Delete this environment',
                'aria-label': 'Delete this environment',
                innerHTML: svgIcon('trash'),
                onClick: function () {
                    bowireConfirm('Delete environment "' + selectedEnv.name + '"?', function () {
                        var backup = JSON.parse(JSON.stringify(selectedEnv));
                        deleteEnvironment(envSidebarSelectedId);
                        envSidebarSelectedId = activeId || '__globals__';
                        render();
                        toast('Deleted environment "' + (backup.name || 'unnamed') + '"', 'info', {
                            undo: function () { restoreEnvironment(backup); envSidebarSelectedId = backup.id; render(); },
                            logAction: { kind: 'env-delete',
                                title: 'Deleted environment "' + (backup.name || 'unnamed') + '"',
                                undoSpec: { env: backup } }
                        });
                    }, { title: 'Delete Environment', danger: true, confirmText: 'Delete' });
                }
            }));
        }
        pane.appendChild(headerRow);

        // ---- Color section — mirrors the workspace settings detail
        // layout (palette swatches + native colour picker in a labelled
        // section below the header instead of inline next to the name).
        // Same swatch palette as the env editor used to inline. ----
        if (!isGlobals) {
            var envSwatchColors = [
                { color: '#6366f1', label: 'Default' },
                { color: '#10b981', label: 'Green' },
                { color: '#f59e0b', label: 'Yellow' },
                { color: '#ef4444', label: 'Red' },
                { color: '#3b82f6', label: 'Blue' },
                { color: '#8b5cf6', label: 'Purple' },
                { color: '#78716c', label: 'Brown' },
                { color: '#000000', label: 'Black' }
            ];
            var envColorRowInner = el('div', { className: 'bowire-env-color-row' });
            envSwatchColors.forEach(function (sw) {
                envColorRowInner.appendChild(el('button', {
                    id: 'bowire-env-swatch-' + sw.label.toLowerCase(),
                    className: 'bowire-env-color-swatch' + (envColor === sw.color ? ' active' : ''),
                    style: 'background:' + sw.color,
                    title: sw.label,
                    onClick: function () {
                        updateEnvironment(envSidebarSelectedId, { color: sw.color });
                        render();
                    }
                }));
            });
            envColorRowInner.appendChild(el('input', {
                id: 'bowire-env-color-picker',
                type: 'color',
                className: 'bowire-env-color-picker',
                value: envColor,
                title: 'Custom colour',
                'aria-label': 'Custom environment colour',
                onInput: function (e) { updateEnvironment(envSidebarSelectedId, { color: e.target.value }); render(); }
            }));
            pane.appendChild(el('div', { className: 'bowire-env-editor-color-section' },
                el('div', { className: 'bowire-env-editor-section-label', textContent: 'Color' }),
                envColorRowInner
            ));
        }

        // ---- Tabs: Variables | Secrets | Auth | Compare ----
        // Globals only have Variables; named envs get all four. Each
        // tab carries a glyph in front of the label so the operator
        // scans by shape, not just by text. Icons stay consistent with
        // the rest of the surface: braces for Variables (data type),
        // key for Secrets (the "thing the lock takes"), lock for Auth
        // (same convention as the workspace's Auth section + the
        // sidebar lock-server-url affordance), diff for Compare.
        if (!isGlobals) {
            var tabs = el('div', { className: 'bowire-env-editor-tabs' });
            var tabDefs = [
                { id: 'variables', label: 'Variables', icon: 'braces' },
                { id: 'secrets', label: 'Secrets', icon: 'key' },
                { id: 'auth', label: 'Auth', icon: 'lock' },
                { id: 'compare', label: 'Compare', icon: 'diff' }
            ];
            for (var ti = 0; ti < tabDefs.length; ti++) {
                (function (td) {
                    var btn = el('button', {
                        id: 'bowire-env-tab-' + td.id,
                        className: 'bowire-env-editor-tab' + (envEditorTab === td.id ? ' active' : ''),
                        onClick: function () { envEditorTab = td.id; render(); }
                    });
                    btn.appendChild(el('span', {
                        className: 'bowire-env-editor-tab-icon',
                        innerHTML: svgIcon(td.icon),
                        'aria-hidden': 'true'
                    }));
                    btn.appendChild(el('span', {
                        className: 'bowire-env-editor-tab-label',
                        textContent: td.label
                    }));
                    tabs.appendChild(btn);
                })(tabDefs[ti]);
            }
            pane.appendChild(tabs);
        }

        // ---- Tab content ----
        var activeTab = isGlobals ? 'variables' : envEditorTab;

        if (activeTab === 'variables') {
            pane.appendChild(renderEnvVariableTable(
                isGlobals ? getGlobalVars() : (selectedEnv.vars || {}),
                isGlobals
                    ? function (v) { saveGlobalVars(v); }
                    : function (v) { updateEnvironment(envSidebarSelectedId, { vars: v }); },
                isGlobals
                    ? 'Available in every environment. Override per-environment with the same key name.'
                    : 'Use {{name}} in request body, metadata or server URL. Workspace defaults under Workspace › Variables apply when this environment has no override.'
            ));
        } else if (activeTab === 'secrets' && selectedEnv) {
            var envSecretsMap = (typeof getEnvSecrets === 'function') ? getEnvSecrets(selectedEnv.id) : {};
            var secWrap = el('div', { className: 'bowire-env-editor-vars' });
            secWrap.appendChild(el('div', {
                className: 'bowire-env-editor-subtitle',
                textContent: 'Reference as {{secret.NAME}} in any input. Session-only — cleared on reload. Workspace defaults under Workspace › Variables apply when this environment has no override.'
            }));
            secWrap.appendChild(_renderKvSection('Secrets', envSecretsMap, function (next) {
                var oldNames = Object.keys(envSecretsMap);
                var newNames = Object.keys(next);
                oldNames.forEach(function (n) {
                    if (newNames.indexOf(n) < 0 && typeof setEnvSecret === 'function') {
                        setEnvSecret(selectedEnv.id, n, null);
                    }
                });
                newNames.forEach(function (n) {
                    if (next[n] !== envSecretsMap[n] && typeof setEnvSecret === 'function') {
                        setEnvSecret(selectedEnv.id, n, next[n]);
                    }
                });
            }, true));
            pane.appendChild(secWrap);
        } else if (activeTab === 'auth' && selectedEnv) {
            var freshAuth = function () {
                var e = getEnvironments().find(function (e) { return e.id === envSidebarSelectedId; });
                return (e && e.auth) || { type: 'none' };
            };
            pane.appendChild(renderAuthSection(
                freshAuth,
                function (a) { updateEnvironment(envSidebarSelectedId, { auth: a }); }
            ));
        } else if (activeTab === 'compare' && selectedEnv) {
            pane.appendChild(renderEnvDiffPane(selectedEnv,
                envManagerDiffTargetId
                    ? envs.find(function (e) { return e.id === envManagerDiffTargetId; })
                    : null,
                envs));
        }

        return pane;
    }

    // Variable table builder — shared by env editor
    function renderEnvVariableTable(vars, setVars, subtitle) {
        var frag = el('div', { className: 'bowire-env-editor-vars' });
        if (subtitle) {
            frag.appendChild(el('div', { className: 'bowire-env-editor-subtitle', textContent: subtitle }));
        }

        var keys = Object.keys(vars);
        var table = el('div', { className: 'bowire-env-editor-table' });

        table.appendChild(el('div', { className: 'bowire-env-editor-row bowire-env-editor-col-header' },
            el('span', { textContent: 'Variable' }),
            el('span', { textContent: 'Value' }),
            el('span')
        ));

        function commitAll(eventOrEl) {
            // Resolve the live table at event time instead of relying on
            // the `table` captured at render-time. After a save+render,
            // morphdom preserves the original table DOM node and APPENDS
            // the new render's freshly-built rows into it — physically
            // moving them out of the next render's temporary table div.
            // The listeners attached to those appended rows still close
            // over the now-empty next-render table, so a closure-based
            // querySelectorAll returns zero rows → newVars = {} → every
            // variable in the env gets wiped on the next change.
            //
            // Accept either a DOM Event or an Element so the Remove
            // button (which calls us after detaching the row) can pass
            // the table directly. Walking up from the source via
            // closest() always lands on whichever table actually
            // contains the row in the live DOM.
            var anchor = eventOrEl && (eventOrEl.target || eventOrEl);
            var liveTable = (anchor && anchor.closest)
                ? anchor.closest('.bowire-env-editor-table')
                : null;
            if (!liveTable) liveTable = table;
            if (!liveTable) { setVars({}); return; }
            var newVars = {};
            var rows = liveTable.querySelectorAll('.bowire-env-editor-row:not(.bowire-env-editor-col-header)');
            for (var ri = 0; ri < rows.length; ri++) {
                var k = rows[ri].querySelector('.bowire-env-editor-key');
                var v = rows[ri].querySelector('.bowire-env-editor-val');
                if (k && v && k.value.trim()) {
                    newVars[k.value.trim()] = v.value;
                }
            }
            setVars(newVars);
        }

        function addVarRow(key, value, intoTable) {
            var targetTable = intoTable || table;
            var row = el('div', { className: 'bowire-env-editor-row' });
            var keyInput = el('input', {
                type: 'text',
                className: 'bowire-env-editor-key',
                value: key,
                placeholder: 'variable name',
                spellcheck: 'false',
                'data-bowire-no-vars-chip': '1',
                'data-bowire-no-vars-ac': '1'
            });
            var valInput = el('textarea', {
                className: 'bowire-env-editor-val',
                placeholder: 'value (Ctrl+Enter for multi-line)',
                spellcheck: 'false',
                rows: '1'
            });
            valInput.value = value;
            function autoResize() {
                valInput.style.height = 'auto';
                valInput.style.height = Math.max(28, valInput.scrollHeight) + 'px';
            }
            // Tab / Enter on the value field: commit + advance to the
            // next row's key. When the operator is already on the last
            // row, instead append a fresh empty row and focus its key.
            // Shift+Tab and Tab on non-last rows fall through to the
            // browser's default nav (val → next row's key, with the
            // tabIndex=-1 remove button skipped) so back-navigation
            // and mid-table moves don't need any custom handling.
            function advanceFromVal(e) {
                e.preventDefault();
                commitAll(e);
                var liveTable = e.target && e.target.closest
                    ? e.target.closest('.bowire-env-editor-table')
                    : null;
                if (!liveTable) return;
                var thisRow = e.target.closest('.bowire-env-editor-row');
                var rows = liveTable.querySelectorAll('.bowire-env-editor-row:not(.bowire-env-editor-col-header)');
                var idx = -1;
                for (var ri = 0; ri < rows.length; ri++) {
                    if (rows[ri] === thisRow) { idx = ri; break; }
                }
                if (idx >= 0 && idx + 1 < rows.length) {
                    var nextKey = rows[idx + 1].querySelector('.bowire-env-editor-key');
                    if (nextKey) nextKey.focus();
                    return;
                }
                addVarRow('', '', liveTable);
                var freshRows = liveTable.querySelectorAll('.bowire-env-editor-row:not(.bowire-env-editor-col-header)');
                var newRow = freshRows[freshRows.length - 1];
                if (newRow) {
                    var newKey = newRow.querySelector('.bowire-env-editor-key');
                    if (newKey) newKey.focus();
                }
            }
            keyInput.addEventListener('change', commitAll);
            valInput.addEventListener('change', commitAll);
            valInput.addEventListener('input', autoResize);
            // Enter on the key field jumps to the same row's value
            // field — keyboard-only operators don't need the mouse
            // to commit the name.
            keyInput.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    valInput.focus();
                }
            });
            valInput.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
                    advanceFromVal(e);
                    return;
                }
                if (e.key === 'Tab' && !e.shiftKey) {
                    // Only intercept when at the last row so a fresh row
                    // gets appended; mid-table Tabs fall through to the
                    // browser's default cell-to-cell navigation.
                    var liveTable = e.target && e.target.closest
                        ? e.target.closest('.bowire-env-editor-table')
                        : null;
                    var thisRow = e.target.closest('.bowire-env-editor-row');
                    if (!liveTable || !thisRow) return;
                    var rows = liveTable.querySelectorAll('.bowire-env-editor-row:not(.bowire-env-editor-col-header)');
                    if (rows[rows.length - 1] === thisRow) {
                        advanceFromVal(e);
                    }
                }
            });
            row.appendChild(keyInput);
            row.appendChild(valInput);
            row.appendChild(el('button', {
                className: 'bowire-env-editor-remove',
                title: 'Remove variable',
                textContent: '\u00d7',
                // Skip the Remove button in tab order \u2014 the operator's
                // Tab walks key \u2192 value \u2192 next row's key \u2192 next row's
                // value seamlessly, without a detour through the remove
                // affordance. Click stays available via mouse.
                tabIndex: '-1',
                onClick: function () {
                    // Capture the live table before detaching the row \u2014
                    // after row.remove() the row has no parent, so we
                    // can't walk back up to it to find the table.
                    var liveTable = row.closest('.bowire-env-editor-table');
                    row.remove();
                    commitAll(liveTable);
                }
            }));
            targetTable.appendChild(row);
            requestAnimationFrame(autoResize);
        }

        for (var ki = 0; ki < keys.length; ki++) {
            addVarRow(keys[ki], String(vars[keys[ki]]));
        }
        // Show ONE empty row for initial discoverability when the env
        // has no vars yet — gives the operator somewhere to type into
        // without having to find the + Add button first. For non-empty
        // envs the keyboard nav (Tab / Enter on the last row's value)
        // adds a new row on demand, so we don't pre-render a trailing
        // empty row that the operator would otherwise tab through.
        if (keys.length === 0) {
            addVarRow('', '');
        }
        frag.appendChild(table);

        frag.appendChild(el('button', {
            id: 'bowire-env-add-variable-btn',
            className: 'bowire-ws-detail-action',
            style: 'margin-top:8px',
            textContent: '+ Add variable',
            onClick: function (e) {
                // Walk up to the live table from the clicked button so
                // morphdom-preserved-table reuse stays correct (the
                // closure 'table' may be the next-render's discarded
                // table after a commit-driven re-render).
                var host = e && e.target && e.target.closest
                    ? e.target.closest('.bowire-env-editor-vars')
                    : null;
                var liveTable = host ? host.querySelector('.bowire-env-editor-table') : null;
                addVarRow('', '', liveTable || undefined);
                // Drop focus into the new row's key field — mouse-driven
                // operators get the same "edit mode of the new cell"
                // contract as the keyboard-driven Tab/Enter path.
                var finalTable = liveTable || table;
                var rows = finalTable.querySelectorAll('.bowire-env-editor-row:not(.bowire-env-editor-col-header)');
                var newRow = rows[rows.length - 1];
                if (newRow) {
                    var newKey = newRow.querySelector('.bowire-env-editor-key');
                    if (newKey) newKey.focus();
                }
            }
        }));

        return frag;
    }

    // #116 — Workspaces rail-mode main pane. Right-hand detail view:
    // color picker, inline rename, description, stats (env / recording
    // / collection counts), switch + delete actions. Bundles every
    // workspace config touchpoint into one surface.
    // Three-way import dialog for workspace JSON. bowireConfirm only
    // supports two buttons (confirm / cancel); the import flow needs
    // three distinct strategies (new / merge / replace) so we build a
    // small purpose-built modal that reuses bowire-confirm CSS classes
    // for visual consistency.
    function _showWorkspaceImportDialog(payload, targetWs) {
        var existing = document.querySelector('.bowire-confirm-overlay');
        if (existing) existing.remove();

        var meta = (payload && payload.workspace) || {};
        var data = (payload && payload.data) || {};
        var counts = {
            urls: Array.isArray(data.bowire_server_urls) ? data.bowire_server_urls.length : 0,
            collections: Array.isArray(data.bowire_collections) ? data.bowire_collections.length : 0,
            recordings: Array.isArray(data.bowire_recordings) ? data.bowire_recordings.length : 0,
            favorites: Array.isArray(data.bowire_favorites) ? data.bowire_favorites.length : 0,
            benchmarks: Array.isArray(data.bowire_benchmarks) ? data.bowire_benchmarks.length : 0,
            flows: Array.isArray(data.bowire_flows) ? data.bowire_flows.length : 0
        };
        var summary = (meta.name || 'Imported') + ' — '
            + counts.urls + ' URLs, '
            + counts.collections + ' collections, '
            + counts.recordings + ' recordings, '
            + counts.favorites + ' favorites, '
            + counts.benchmarks + ' benchmarks, '
            + counts.flows + ' flows';

        function close() { overlay.remove(); }
        function runImport(mode) {
            try {
                if (mode === 'new') {
                    var newId = importWorkspaceJson(payload, { mode: 'new' });
                    workspacesSelectedId = newId;
                    close();
                    toast('Workspace imported', 'success');
                    if (typeof switchWorkspace === 'function' && newId) {
                        switchWorkspace(newId);
                    } else {
                        render();
                    }
                } else {
                    importWorkspaceJson(payload, { mode: mode, target: targetWs.id });
                    close();
                    toast(mode === 'replace'
                        ? 'Workspace "' + targetWs.name + '" replaced'
                        : 'Merged into "' + targetWs.name + '"', 'success');
                    try { window.location.reload(); } catch { /* ignore */ }
                }
            } catch (e) {
                toast(e.message || 'Import failed', 'error');
            }
        }

        var dialog = el('div', {
            className: 'bowire-confirm-dialog',
            role: 'alertdialog',
            'aria-modal': 'true',
            style: 'min-width:380px'
        },
            el('div', { className: 'bowire-confirm-title', textContent: 'Import workspace' }),
            el('div', { className: 'bowire-confirm-message', textContent: summary }),
            el('div', { className: 'bowire-confirm-actions', style: 'flex-wrap:wrap;gap:6px' },
                el('button', {
                    className: 'bowire-confirm-btn cancel',
                    textContent: 'Cancel',
                    onClick: close
                }),
                el('button', {
                    className: 'bowire-confirm-btn danger',
                    textContent: 'Replace "' + targetWs.name + '"',
                    title: 'Wipe the target workspace and load the file into it',
                    onClick: function () { runImport('replace'); }
                }),
                el('button', {
                    className: 'bowire-confirm-btn',
                    textContent: 'Merge into "' + targetWs.name + '"',
                    title: 'Concat lists and assign maps into the current workspace',
                    onClick: function () { runImport('merge-into'); }
                }),
                el('button', {
                    className: 'bowire-confirm-btn',
                    textContent: 'New workspace',
                    title: 'Create a new workspace and load the file into it (recommended)',
                    onClick: function () { runImport('new'); }
                })
            )
        );
        var overlay = el('div', {
            className: 'bowire-confirm-overlay',
            onClick: function (e) { if (e.target === overlay) close(); }
        }, dialog);
        document.body.appendChild(overlay);
        overlay.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') close();
        });
    }

    // #192 — Dispatcher: when the sidebar tree picks a workspace
    // sub-node (Sources / a URL leaf / Environments / …), the main
    // pane swaps to the matching editor instead of always showing the
    // workspace settings card. Keeping the dispatch here means the
    // operator stays in the Workspaces rail while drilling in — the
    // rail is the workspace navigator, the main pane is the editor.
    // Breadcrumb for the workspace sub-views (Sources / Collections /
    // Environments / Recordings, and their per-item detail panes). The
    // tree on the left names the same thing, but operators routinely
    // collapse it; without a breadcrumb here the only thing on screen
    // was the workspace name (rendered like a page title even though
    // we're on a sub-view), and the current section showed only as a
    // small grey "› Environments" hint that didn't read as "you are
    // here". The first crumb is the workspace itself — clickable back
    // to its settings card; intermediate crumbs are clickable back to
    // the parent section; the final crumb is the active page.
    // Small inline trail that sits underneath the editable title in
    // every workspace-tree settings header (workspace settings, env
    // overview, env detail). Pattern is Notion-style: the trail shows
    // only the PARENT path; the leaf is the page title (and editable
    // where appropriate). Every crumb is navigable unless an explicit
    // `current: true` flag marks it as the current page (rendered as
    // plain text). Pass items as { label, onClick } or { label, current }.
    function _renderHeaderTrail(crumbs) {
        var nav = el('nav', {
            className: 'bowire-ws-detail-trail',
            'aria-label': 'Breadcrumb'
        });
        crumbs.forEach(function (c, idx) {
            if (idx > 0) {
                nav.appendChild(el('span', {
                    className: 'bowire-ws-detail-trail-sep',
                    'aria-hidden': 'true',
                    textContent: '›'
                }));
            }
            if (c.current || typeof c.onClick !== 'function') {
                nav.appendChild(el('span', {
                    className: 'bowire-ws-detail-trail-current',
                    'aria-current': c.current ? 'page' : null,
                    textContent: c.label
                }));
            } else {
                nav.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-ws-detail-trail-link',
                    textContent: c.label,
                    onClick: c.onClick
                }));
            }
        });
        return nav;
    }

    // Navigation helpers used by every header trail. Centralised so the
    // workspace settings / env overview / env detail crumbs all route
    // through one place and stay consistent.
    function _goToWorkspacesOverview() {
        // Belt-and-suspenders: set railMode too so the route works
        // even when triggered from a non-Workspaces rail (welcome-card
        // 'Manage workspaces' button, topbar dropdown 'Show all',
        // command palette, etc.). Without the railMode flip, renderMain
        // dispatches to whichever rail the operator was last on and
        // never reaches renderWorkspaceDetailMain → the overview
        // never paints + the click appears to do nothing.
        railMode = 'workspaces';
        try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
        workspaceTreeSelection = { kind: 'workspaces-overview' };
        render();
    }
    function _goToWorkspaceSettings(wsId) {
        // Flip railMode like _goToWorkspacesOverview above. Without
        // this, calling the function from the topbar workspace
        // dropdown's gear button (or anywhere outside the Workspaces
        // rail) updates the tree selection but renderMain dispatches
        // to the current rail and never reaches
        // renderWorkspaceDetailMain — the click appears to do
        // nothing. Operator feedback: 'der hover settings button für
        // den workspace dropdown funktioniert nicht (settings seite
        // für workspace öffnet sich nicht). rename und löschen bei
        // hover im workspace dropdown gehen aber.' (Rename + delete
        // open in-place dialogs, so they didn't need the rail flip.)
        railMode = 'workspaces';
        try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
        workspacesSelectedId = wsId;
        workspaceTreeSelection = { wsId: wsId, kind: 'workspace' };
        render();
    }
    function _goToEnvironmentsOverview(wsId) {
        // Same rail-flip rationale as _goToWorkspaceSettings — env
        // overview lives under the Workspaces rail dispatch arm.
        railMode = 'workspaces';
        try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
        workspacesSelectedId = wsId;
        workspaceTreeSelection = { wsId: wsId, kind: 'environments' };
        render();
    }

    function _renderWorkspaceBreadcrumb(ws, segments) {
        var nav = el('nav', {
            className: 'bowire-ws-breadcrumb',
            'aria-label': 'Workspace navigation'
        });
        var wsCrumb = el('button', {
            type: 'button',
            className: 'bowire-ws-breadcrumb-crumb bowire-ws-breadcrumb-workspace',
            title: 'Open ' + ws.name + ' settings',
            onClick: function () {
                workspaceTreeSelection = { wsId: ws.id, kind: 'workspace' };
                render();
            }
        },
            el('span', {
                className: 'bowire-ws-breadcrumb-dot',
                style: 'background:' + (ws.color || 'var(--bowire-accent)')
            }),
            el('span', { textContent: ws.name })
        );
        nav.appendChild(wsCrumb);
        segments.forEach(function (seg, idx) {
            nav.appendChild(el('span', {
                className: 'bowire-ws-breadcrumb-sep',
                'aria-hidden': 'true',
                textContent: '›'
            }));
            var isLast = idx === segments.length - 1;
            if (isLast || typeof seg.onClick !== 'function') {
                nav.appendChild(el('span', {
                    className: 'bowire-ws-breadcrumb-current',
                    'aria-current': isLast ? 'page' : null,
                    textContent: seg.label
                }));
            } else {
                nav.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-ws-breadcrumb-crumb',
                    textContent: seg.label,
                    onClick: seg.onClick
                }));
            }
        });
        return nav;
    }

    function renderWorkspaceDetailMain() {
        var sel0 = (typeof workspaceTreeSelection !== 'undefined' && workspaceTreeSelection) || {};
        // Workspaces overview is workspace-agnostic — render it before
        // any single-workspace lookup so it works even when the active
        // workspace is null (e.g. right after the last workspace was
        // deleted).
        if (sel0.kind === 'workspaces-overview') return _renderWorkspacesOverview();
        var ws = workspaces.find(function (w) { return w.id === workspacesSelectedId; })
                 || activeWorkspace();
        if (!ws) {
            // Was: dead-end 'No workspace selected. Pick one in the
            // sidebar or create a new one.' — but the sidebar might be
            // collapsed AND no 'create' affordance was shown, leaving
            // the operator stuck on a screen they couldn't act on.
            // Route to the overview list instead; it already has its
            // own empty / non-empty states with proper CTAs.
            return _renderWorkspacesOverview();
        }
        var sel = sel0;
        var kind = (sel.wsId === ws.id) ? sel.kind : 'workspace';
        if (kind === 'url' && sel.value) {
            // URL leaf no longer routes to the bare-bones "Open in Sources
            // rail" stub — that screen was a dead-end (Sources rail itself
            // was retired). Point the rich URL editor (renderSourcesDetailMain)
            // at this URL and reuse it: the operator gets Refresh discovery,
            // the URL/name/color editor, headers, status — everything the
            // legacy path eventually surfaced — without an extra hop.
            sourcesSelectedUrl = sel.value;
            return renderSourcesDetailMain();
        }
        if (kind === 'sources') return _renderWorkspaceSourcesDetail(ws);
        if (kind === 'collections') return _renderWorkspaceCollectionsOverview(ws);
        if (kind === 'collection' && sel.value) return _renderWorkspaceCollectionDetail(ws, sel.value);
        if (kind === 'environments') return _renderWorkspaceEnvironmentsOverview(ws);
        if (kind === 'env' && sel.value) return _renderWorkspaceEnvironmentDetail(ws, sel.value);
        // Variables leaf retired in favour of a Variables tab inside
        // workspace settings. The route stays as a back-compat alias:
        // any persisted tree-selection from before the change opens
        // the settings pane with the Variables tab already active.
        if (kind === 'variables') {
            workspaceSettingsTab = 'variables';
            return _renderWorkspaceSettingsDetail(ws);
        }
        if (kind === 'recordings') return _renderWorkspaceRecordingsOverview(ws);
        if (kind === 'recording' && sel.value) return _renderWorkspaceRecordingDetail(ws, sel.value);
        return _renderWorkspaceSettingsDetail(ws);
    }

    // Workspaces overview — the root of the workspaces-tree settings
    // surface. Mirrors the env overview pattern (header glyph + title
    // + hover-revealed row tools) so the two listing surfaces read as
    // variants of one shape. Sits one level above workspace settings;
    // the trail in every nested header links back here.
    function _renderWorkspacesOverview() {
        // #301 — Empty Workspaces overview now uses the canonical
        // rail-empty-card pattern (icon + headline + body + primary
        // CTA inside a bowire-main-pad wrap) so it reads identically
        // to Recordings / Collections / Mocks / Flows / Benchmarks
        // when no workspace exists. The custom bowire-ws-detail-header
        // + section + paragraph hint is only kept for the populated
        // branch where the title carries the workspace count and the
        // list rows sit under it.
        //
        // #301 followup — empty branch builds its main WITHOUT the
        // bowire-main-workspaces class because that class ships its own
        // var(--bowire-main-gutter) padding, which would double up
        // against the inner bowire-main-pad's matching padding and
        // shift the welcome card 24px farther from the left edge than
        // every other rail's empty card. Populated branch keeps the
        // workspaces class (its padding + flex-column + gap layout
        // are still right for the list view below).
        if (workspaces.length === 0) {
            var emptyMain = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main' });
            var emptyWrap = el('div', { className: 'bowire-main-pad' });
            emptyWrap.appendChild(renderEmptyCard({
                icon: 'layers',
                headline: 'No workspaces yet',
                body: 'A workspace is your project folder — it holds the URLs you discover, the environments + variables + secrets you reference, and the collections / recordings / benchmarks you build. Create one to start organising sources, environments and recordings.',
                actions: [{
                    label: 'New workspace…',
                    primary: true,
                    onClick: function () {
                        if (typeof openCreateWorkspaceDialog === 'function') {
                            openCreateWorkspaceDialog(function (created) {
                                if (created && created.id) {
                                    _goToWorkspaceSettings(created.id);
                                } else {
                                    render();
                                }
                            });
                        }
                    }
                }]
            }));
            emptyMain.appendChild(emptyWrap);
            return emptyMain;
        }

        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });

        var headerGlyph = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="24" height="24">'
            + '<polygon points="12 2 2 7 12 12 22 7 12 2"/>'
            + '<polyline points="2 17 12 22 22 17"/>'
            + '<polyline points="2 12 12 17 22 12"/>'
            + '</svg>';
        // #279 — header carries the current sort label + a Reset
        // action to the right of the title so the operator always has
        // a way back to the canonical order without hunting for the
        // sidebar overflow menu. The Reset button only renders when
        // the current sort isn't already alphabetical / there's a
        // non-empty manual order to clear — otherwise it'd be a no-op
        // visual nudge.
        var sortLabelMap = {
            alphabetical: 'Alphabetical',
            created: 'Created date',
            lastUsed: 'Last used',
            manual: 'Manual'
        };
        var curSortLabel = sortLabelMap[workspacesSortBy] || 'Alphabetical';
        var canReset = workspacesSortBy !== 'alphabetical'
            || (Array.isArray(workspacesManualOrder) && workspacesManualOrder.length > 0);
        var headerRight = el('div', { className: 'bowire-ws-detail-header-actions' });
        headerRight.appendChild(el('span', {
            className: 'bowire-ws-detail-sort-label',
            textContent: 'Sort: ' + curSortLabel
        }));
        if (canReset && typeof resetWorkspacesSort === 'function') {
            headerRight.appendChild(el('button', {
                type: 'button',
                className: 'bowire-ws-detail-sort-reset',
                title: 'Reset workspace sort to alphabetical',
                'aria-label': 'Reset workspace sort to alphabetical',
                textContent: 'Reset sort',
                onClick: function () {
                    resetWorkspacesSort();
                    if (typeof toast === 'function') {
                        toast('Workspace sort reset to alphabetical.', 'info');
                    }
                    render();
                }
            }));
        }
        main.appendChild(el('div', { className: 'bowire-ws-detail-header' },
            el('span', { className: 'bowire-ws-detail-glyph', innerHTML: headerGlyph }),
            el('div', { className: 'bowire-ws-detail-title-stack' },
                el('div', { className: 'bowire-ws-detail-title-static', textContent: 'Workspaces (' + workspaces.length + ')' })
            ),
            headerRight
        ));

        var section = el('div', { className: 'bowire-ws-detail-section' });
        // #279 — manual-mode hint sits above the list so the operator
        // sees WHY rows have a drag handle. Skipped in other modes
        // where the rows have no drag affordance.
        if (workspacesSortBy === 'manual') {
            section.appendChild(el('div', {
                className: 'bowire-ws-detail-sort-hint',
                textContent: 'Manual sort — drag the grip handle on a row to reorder. The order is shared across the sidebar, the topbar dropdown, and this overview.'
            }));
        }
        var list = el('div', { className: 'bowire-env-overview-list' });
        // Cross-cutting workspace sort applies in the overview
        // list too (same workspacesSortBy state as sidebar +
        // dropdown). The overview will gain its own dedicated
        // search bar separately; today no filter is applied here.
        var overviewWorkspaces = (typeof getSortedWorkspaces === 'function')
            ? getSortedWorkspaces() : workspaces;
        var manualMode = workspacesSortBy === 'manual';
        overviewWorkspaces.forEach(function (w, rowIdx) {
            var isActive = w.id === activeWorkspaceId;
            var wsColor = w.color || 'var(--bowire-text-tertiary)';
            // Same layers glyph idiom as the workspace settings
            // header — top "feature" picks up the workspace colour,
            // lower lines stay currentColor. Smaller (14px) to fit
            // the row.
            var rowGlyph = '<svg viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round" width="14" height="14">'
                + '<polygon points="12 2 2 7 12 12 22 7 12 2" fill="' + wsColor + '" stroke="' + wsColor + '"/>'
                + '<polyline points="2 17 12 22 22 17"/>'
                + '<polyline points="2 12 12 17 22 12"/>'
                + '</svg>';
            var envN = (typeof readWorkspaceEnvironments === 'function')
                ? readWorkspaceEnvironments(w.id).length
                : 0;
            var meta = envN + (envN === 1 ? ' env' : ' envs');
            var row = el('div', {
                className: 'bowire-env-overview-row'
                    + (manualMode ? ' is-draggable' : ''),
                draggable: manualMode ? 'true' : 'false',
                'data-ws-id': w.id
            });
            // #279 — drag handle, only in manual mode. Sits to the
            // left of the layer glyph as a dedicated affordance so
            // operators don't accidentally try to drag the glyph or
            // the row body. We bind drag listeners to the whole row
            // (`draggable="true"` above) so the operator can pick the
            // row up by the handle OR by clicking-and-holding the row
            // surface — the handle is the visible cue, not the only
            // pick-up spot.
            if (manualMode) {
                row.appendChild(el('span', {
                    className: 'bowire-env-overview-grip',
                    title: 'Drag to reorder',
                    'aria-hidden': 'true',
                    innerHTML: svgIcon('grip')
                }));
                row.addEventListener('dragstart', function (e) {
                    _workspaceDragState = { id: w.id, fromIndex: rowIdx };
                    try {
                        e.dataTransfer.effectAllowed = 'move';
                        e.dataTransfer.setData('text/plain', w.id);
                    } catch { /* ignore */ }
                    row.classList.add('dragging');
                });
                row.addEventListener('dragend', function () {
                    _workspaceDragState = null;
                    row.classList.remove('dragging');
                    var indicators = document.querySelectorAll(
                        '.bowire-env-overview-row.bowire-drop-above,'
                        + '.bowire-env-overview-row.bowire-drop-below'
                    );
                    for (var di = 0; di < indicators.length; di++) {
                        indicators[di].classList.remove('bowire-drop-above', 'bowire-drop-below');
                    }
                });
                row.addEventListener('dragover', function (e) {
                    if (!_workspaceDragState) return;
                    e.preventDefault();
                    try { e.dataTransfer.dropEffect = 'move'; } catch { /* ignore */ }
                    var rect = row.getBoundingClientRect();
                    var midY = rect.top + rect.height / 2;
                    row.classList.toggle('bowire-drop-above', e.clientY < midY);
                    row.classList.toggle('bowire-drop-below', e.clientY >= midY);
                });
                row.addEventListener('dragleave', function () {
                    row.classList.remove('bowire-drop-above', 'bowire-drop-below');
                });
                row.addEventListener('drop', function (e) {
                    e.preventDefault();
                    row.classList.remove('bowire-drop-above', 'bowire-drop-below');
                    if (!_workspaceDragState) return;
                    var rect = row.getBoundingClientRect();
                    var toIdx = e.clientY < rect.top + rect.height / 2 ? rowIdx : rowIdx + 1;
                    var draggedId = _workspaceDragState.id;
                    _workspaceDragState = null;
                    if (typeof moveWorkspaceManualOrder === 'function') {
                        moveWorkspaceManualOrder(draggedId, toIdx);
                    }
                    render();
                });
            }
            row.appendChild(el('span', {
                className: 'bowire-env-overview-glyph',
                innerHTML: rowGlyph
            }));
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-env-overview-name',
                textContent: w.name || '(unnamed)',
                title: 'Open workspace settings',
                onClick: function () { _goToWorkspaceSettings(w.id); }
            }));
            row.appendChild(el('span', {
                className: 'bowire-env-overview-meta',
                textContent: meta
            }));
            // Click-to-activate checkmark — solid on the active
            // workspace, ghosted-but-clickable on every other.
            // Same idiom as the env overview's active toggle so
            // the two listings share the affordance.
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-env-overview-check' + (isActive ? ' is-active' : ''),
                title: isActive ? 'Active workspace' : 'Switch to this workspace',
                'aria-label': isActive ? 'Active workspace' : 'Switch to this workspace',
                'aria-pressed': isActive ? 'true' : 'false',
                textContent: '✓',
                onClick: function (ev) {
                    ev.stopPropagation();
                    if (typeof switchWorkspace === 'function') switchWorkspace(w.id);
                    render();
                }
            }));
            // #276 — Per-row tools sourced from the same
            // _workspaceRowActionDefs the sidebar tree uses, so
            // both surfaces share the actions, the labels, the
            // dialog wording, and the confirmation copy. The
            // overview wraps each definition in its own
            // `bowire-env-overview-tool` button shape (different
            // visual rhythm from the sidebar's compact tree-tool
            // icons, but same outcome).
            var tools = el('div', { className: 'bowire-env-overview-tools' });
            var rowDefs = (typeof _workspaceRowActionDefs === 'function')
                ? _workspaceRowActionDefs(w) : [];
            rowDefs.forEach(function (def) {
                tools.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-env-overview-tool'
                        + (def.danger ? ' bowire-env-overview-tool-danger' : ''),
                    title: def.title || def.label,
                    'aria-label': def.title || def.label,
                    innerHTML: svgIcon(def.icon),
                    onClick: def.onClick
                }));
            });
            row.appendChild(tools);
            list.appendChild(row);
        });
        section.appendChild(list);

        // Create-workspace button at the bottom of the list. Primary-
        // button convention: just the verb-phrase 'New workspace…',
        // no leading '+ ' (the + prefix belongs on inline list items
        // / icon-only buttons, not on primary actions where the
        // button's prominence carries the affordance). Ellipsis
        // because the click opens a further dialog.
        section.appendChild(el('button', {
            className: 'bowire-ws-detail-action bowire-ws-detail-action-primary',
            style: 'margin-top:12px',
            textContent: 'New workspace…',
            onClick: function () {
                if (typeof openCreateWorkspaceDialog === 'function') {
                    openCreateWorkspaceDialog(function (created) {
                        if (created && created.id) {
                            _goToWorkspaceSettings(created.id);
                        } else {
                            render();
                        }
                    });
                }
            }
        }));
        main.appendChild(section);
        return main;
    }

    // The current workspace settings card — name, color, includeAllEnvironments,
    // includedEnvironmentIds list, danger zone. Reached by selecting either
    // the workspace row itself or the "Settings" child in the tree.
    function _renderWorkspaceSettingsDetail(ws) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        if (!ws) {
            // Defensive: was a dead-end empty hint. Route through the
            // overview so the operator gets the New-workspace CTA
            // instead of staring at instructional text they can't
            // act on.
            return _renderWorkspacesOverview();
        }

        // Live-lookup helper — every mutating handler in this pane reads
        // the currently-selected workspace at click time instead of the
        // `ws` reference captured at render time. Reason: morphdom
        // preserves DOM nodes (buttons / inputs) across renders when the
        // structure matches, and the old node keeps its old listener.
        // Switching from workspace A's detail to workspace B's detail
        // (via the topbar dropdown's cogwheel or the sidebar tree) would
        // otherwise route B's clicks back into A's stale closures and
        // mutate the wrong workspace. workspacesSelectedId is module-
        // scoped and always current at call time, so even a reused button
        // resolves the right target.
        function _liveWs() {
            return workspaces.find(function (x) { return x.id === workspacesSelectedId; });
        }

        var isActive = ws.id === activeWorkspaceId;
        // Self-contained workspaces: env count = number of envs in
        // this workspace's own bucket. Active workspace can use the
        // live getEnvironments; non-active ones read from the wsKeyFor-
        // prefixed key via readWorkspaceEnvironments.
        var envCount = (ws.id === activeWorkspaceId)
            ? ((typeof getEnvironments === 'function') ? getEnvironments().length : 0)
            : ((typeof readWorkspaceEnvironments === 'function') ? readWorkspaceEnvironments(ws.id).length : 0);

        var wsColor = ws.color || 'var(--bowire-accent)';
        var wsGlyph = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="24" height="24">'
            + '<polygon points="12 2 2 7 12 12 22 7 12 2" fill="' + wsColor + '" stroke="' + wsColor + '"/>'
            + '<polyline points="2 17 12 22 22 17"/>'
            + '<polyline points="2 12 12 17 22 12"/>'
            + '</svg>';
        var header = el('div', { className: 'bowire-ws-detail-header' },
            el('span', {
                className: 'bowire-ws-detail-glyph',
                innerHTML: wsGlyph
            }),
            // Title stack: editable name on top, small breadcrumb-style
            // path below as the page's "where am I" subtitle. Pattern
            // is reused on env detail + env overview headers so every
            // workspaces-tree settings surface shares one shape.
            el('div', { className: 'bowire-ws-detail-title-stack' },
                el('input', {
                    type: 'text',
                    className: 'bowire-ws-detail-name',
                    value: ws.name,
                    'aria-label': 'Workspace name',
                    'data-bowire-no-vars-chip': '1',
                    'data-bowire-no-vars-ac': '1',
                    onChange: function (e) {
                        var v = String(e.target.value || '').trim();
                        var t = _liveWs();
                        if (v && t && v !== t.name) {
                            var prevName = t.name;
                            var targetId = t.id;
                            if (renameWorkspace(targetId, v)) {
                                // #194 — log so the rename is reversible
                                // via Ctrl/Cmd+Z + the Activity drawer.
                                if (typeof recordAction === 'function') {
                                    recordAction({
                                        kind: 'workspace-rename',
                                        rail: 'workspaces',
                                        title: 'Renamed workspace "' + prevName + '" → "' + v + '"',
                                        undoSpec: { workspaceId: targetId, prevName: prevName, nextName: v },
                                        undo: function () { renameWorkspace(targetId, prevName); render(); },
                                        redo: function () { renameWorkspace(targetId, v); render(); }
                                    });
                                }
                            }
                            render();
                        }
                    }
                }),
                _renderHeaderTrail([
                    { label: 'Workspaces', onClick: _goToWorkspacesOverview }
                ])
            ),
            isActive
                ? el('span', { className: 'bowire-ws-detail-badge', textContent: 'Active' })
                : el('button', {
                    className: 'bowire-ws-detail-switch-btn',
                    textContent: 'Switch to this workspace',
                    onClick: function () {
                        var t = _liveWs();
                        if (t) switchWorkspace(t.id);
                    }
                })
        );
        // #127 follow-up — manual control cluster. v2.0's auto-save
        // covers the path-of-least-resistance case, but operators
        // also want explicit affordances for:
        //   * Save now — force-flush every persist slot in one click,
        //     same as Cmd+S; useful before closing the tab or sharing
        //     a screenshot of a known-saved state.
        //   * Duplicate — "Save As" / fork: clone this workspace's
        //     entire data set into a fresh workspace, restoring the
        //     v1.9 Save-As path the auto-save model dropped.
        var headerActions = el('div', { className: 'bowire-ws-detail-header-actions' });
        // 'All workspaces' — explicit nav back to the overview. The
        // breadcrumb 'Workspaces' link at the top of the header does
        // the same thing but reads as decoration; a real action
        // button next to Save-now / Duplicate gives the operator a
        // visible affordance to leave a single workspace's detail
        // pane and see the listing.
        headerActions.appendChild(el('button', {
            className: 'bowire-ws-detail-action-btn',
            textContent: 'Manage workspaces',
            title: 'Show the list of every workspace',
            onClick: _goToWorkspacesOverview
        }));
        // R3a — Workspaces detail → Settings transition. Deep-link
        // into the Settings modal's per-workspace overrides page.
        // openSettings('workspace-overrides') routes through the
        // legacy-tab migration and lands on the right tab; the
        // Settings tree node ('Workspace…' → 'Per-Workspace overrides')
        // highlights automatically because settingsTab is the only
        // selection source.
        headerActions.appendChild(el('button', {
            className: 'bowire-ws-detail-action-btn',
            textContent: 'Per-workspace overrides…',
            title: 'Edit per-workspace setting overrides in the Settings panel',
            onClick: function () {
                if (typeof openSettings === 'function') {
                    openSettings('workspace-overrides');
                }
            }
        }));
        if (isActive) {
            // Save-now button greys out when nothing has been
            // autosaved since the last force-flush — pressing it then
            // would be a no-op the operator can't see. hasUnflushedChanges
            // tracks autosaves outside the flush sweep itself.
            var _dirty = (typeof hasUnflushedChanges === 'function') && hasUnflushedChanges();
            var saveBtn = el('button', {
                className: 'bowire-ws-detail-action-btn',
                textContent: _dirty ? 'Save now' : 'Saved',
                title: _dirty
                    ? 'Force-flush every persist slot. Same as Cmd/Ctrl+S.'
                    : 'Nothing pending — every autosave slot is already on disk.',
                onClick: function () {
                    if (typeof flushAllPersists === 'function') flushAllPersists();
                }
            });
            if (!_dirty) saveBtn.setAttribute('disabled', 'disabled');
            headerActions.appendChild(saveBtn);
        }
        // 'Save as…' split-style action — one slot in the header that
        // groups every 'persist this workspace in another shape' verb
        // behind a caret menu. Save-as-template + Duplicate + Export
        // are individually low-frequency but conceptually related; a
        // single 'Save as' entry-point makes the header tighter and
        // matches the method-execute split-button pattern operators
        // already know.
        //
        // Direct click on the primary button opens the menu (no
        // implicit default action — operator picks which 'save as'
        // variant they want).
        var saveAsWrap = el('div', { className: 'bowire-ws-detail-saveas-wrap' });
        var saveAsBtn = el('button', {
            id: 'bowire-ws-saveas-btn',
            className: 'bowire-ws-detail-action-btn',
            title: 'Save this workspace as a copy, template, or export file',
            onClick: function (e) {
                e.stopPropagation();
                var prev = document.querySelector('.bowire-ws-saveas-menu');
                if (prev) { prev.remove(); return; }
                var menu = el('div', { className: 'bowire-ws-saveas-menu', role: 'menu' });
                menu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-ws-saveas-menu-item',
                    title: 'Create a copy of this workspace under a new name (URLs, envs, collections, recordings, flows, pins).',
                    onClick: function () {
                        menu.remove();
                        var t = _liveWs();
                        if (!t || typeof duplicateWorkspace !== 'function') return;
                        bowirePrompt('Name for the duplicate', {
                            title: 'Duplicate workspace',
                            defaultValue: t.name + ' (copy)',
                            okLabel: 'Duplicate'
                        }, function (newName) {
                            if (newName === null) return;
                            var fresh = duplicateWorkspace(t.id, newName);
                            if (fresh) {
                                workspacesSelectedId = fresh.id;
                                render();
                            }
                        });
                    }
                },
                    el('span', { className: 'bowire-ws-saveas-menu-icon', innerHTML: svgIcon('copy') }),
                    el('span', { className: 'bowire-ws-saveas-menu-label', textContent: 'Duplicate as new workspace…' })
                ));
                if (typeof saveWorkspaceAsTemplate === 'function') {
                    menu.appendChild(el('button', {
                        type: 'button',
                        className: 'bowire-ws-saveas-menu-item',
                        title: 'Snapshot this workspace as a reusable template — appears in "Your templates" on the next create-workspace dialog.',
                        onClick: function () {
                            menu.remove();
                            var t = _liveWs();
                            if (!t) return;
                            bowirePrompt('Template name', {
                                title: 'Save as template',
                                defaultValue: t.name + ' template',
                                confirmText: 'Save',
                                validator: function (val) {
                                    return String(val || '').trim() ? null : 'Name required';
                                }
                            }).then(function (name) {
                                if (!name) return;
                                try {
                                    saveWorkspaceAsTemplate(t.id, name, '', 'layers');
                                    if (typeof toast === 'function') {
                                        toast('Saved "' + name + '" — available in the next create-workspace dialog.', 'success');
                                    }
                                } catch (e) {
                                    if (typeof toast === 'function') {
                                        toast('Save failed: ' + (e && e.message ? e.message : 'unknown error'), 'error');
                                    }
                                }
                            });
                        }
                    },
                        el('span', { className: 'bowire-ws-saveas-menu-icon', innerHTML: svgIcon('bookmark') }),
                        el('span', { className: 'bowire-ws-saveas-menu-label', textContent: 'Save as template…' })
                    ));
                }
                if (typeof downloadWorkspaceExport === 'function') {
                    menu.appendChild(el('button', {
                        type: 'button',
                        className: 'bowire-ws-saveas-menu-item',
                        title: 'Download the workspace as a single .bww file (URLs, collections, recordings, favorites, benchmarks, flows, presets — NO secrets).',
                        onClick: function () {
                            menu.remove();
                            var t = _liveWs();
                            if (!t) return;
                            if (downloadWorkspaceExport(t.id)) {
                                if (typeof toast === 'function') toast('Workspace exported', 'success');
                            } else {
                                if (typeof toast === 'function') toast('Export failed — see console', 'error');
                            }
                        }
                    },
                        el('span', { className: 'bowire-ws-saveas-menu-icon', innerHTML: svgIcon('download') }),
                        el('span', { className: 'bowire-ws-saveas-menu-label', textContent: 'Export to .bww file' })
                    ));
                }
                document.body.appendChild(menu);
                var rect = saveAsBtn.getBoundingClientRect();
                menu.style.position = 'fixed';
                menu.style.left = Math.max(8, rect.left) + 'px';
                var menuH = menu.offsetHeight;
                var spaceBelow = window.innerHeight - rect.bottom;
                menu.style.top = (spaceBelow >= menuH + 12
                    ? rect.bottom + 6
                    : Math.max(8, rect.top - menuH - 6)) + 'px';
                setTimeout(function () {
                    function onOutside(ev) {
                        if (!menu.contains(ev.target) && !saveAsBtn.contains(ev.target)) {
                            menu.remove();
                            document.removeEventListener('click', onOutside, true);
                        }
                    }
                    document.addEventListener('click', onOutside, true);
                }, 0);
            }
        },
            el('span', { textContent: 'Save as' }),
            el('span', { innerHTML: svgIcon('chevronDown'), style: 'display:inline-flex;align-items:center;margin-left:4px' })
        );
        saveAsWrap.appendChild(saveAsBtn);
        headerActions.appendChild(saveAsWrap);
        header.appendChild(headerActions);
        main.appendChild(header);

        // ---- Tabs: General | Variables | Secrets ----
        // Mirrors the env editor's tab pattern + icon convention so
        // both settings surfaces read as one shape. Single-table data
        // (workspace vars + workspace secrets) lives behind tabs here
        // instead of as separate tree leaves — the tree is for entity
        // LISTS (sources / envs / collections / recordings), not for
        // settings of the workspace itself.
        var wsTabsBar = el('div', { className: 'bowire-env-editor-tabs' });
        // Variables + Secrets are data tabs of the same shape (KV
        // tables); they belong next to each other in tab order.
        // Auth — a different mental model (per-call credentials) —
        // lands AFTER, not between the two related ones.
        var wsTabDefs = [
            { id: 'general',   label: 'General',   icon: 'settings' },
            { id: 'variables', label: 'Variables', icon: 'braces' },
            { id: 'secrets',   label: 'Secrets',   icon: 'key' },
            { id: 'auth',      label: 'Auth',      icon: 'lock' }
        ];
        wsTabDefs.forEach(function (td) {
            var btn = el('button', {
                className: 'bowire-env-editor-tab' + (workspaceSettingsTab === td.id ? ' active' : ''),
                onClick: function () { workspaceSettingsTab = td.id; render(); }
            });
            btn.appendChild(el('span', {
                className: 'bowire-env-editor-tab-icon',
                innerHTML: svgIcon(td.icon),
                'aria-hidden': 'true'
            }));
            btn.appendChild(el('span', {
                className: 'bowire-env-editor-tab-label',
                textContent: td.label
            }));
            wsTabsBar.appendChild(btn);
        });
        main.appendChild(wsTabsBar);

        // ---- Variables tab ----
        if (workspaceSettingsTab === 'variables') {
            main.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                style: 'margin:8px 0',
                textContent: 'Workspace defaults that every Environment in this workspace inherits. An Environment can add its own or override these. Reference as {{NAME}}.'
            }));
            var liveWsForVars = _liveWs() || ws;
            var varsMap = liveWsForVars.vars || {};
            main.appendChild(_renderKvSection('Variables', varsMap, function (next) {
                var t = _liveWs();
                if (t) {
                    t.vars = next;
                    if (typeof persistWorkspaces === 'function') persistWorkspaces();
                }
            }, false));
            return main;
        }

        // ---- Auth tab ----
        // Workspace-level auth config — applied as the default when
        // the active environment has no auth (or `auth.type === 'none'`).
        // Environment auth wins on collision, mirroring the variables
        // pattern (env vars override workspace defaults). Reuses
        // renderAuthSection() so the editor reads identical to the
        // env auth editor.
        if (workspaceSettingsTab === 'auth') {
            main.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                style: 'margin:8px 0',
                textContent: 'Workspace-level default auth — applied to every request unless the active environment defines its own. Use this for "every env in this workspace hits the same API gateway with the same token" setups.'
            }));
            if (typeof renderAuthSection === 'function') {
                main.appendChild(renderAuthSection(
                    function () {
                        var t = _liveWs() || ws;
                        return t.auth || { type: 'none' };
                    },
                    function (nextAuth) {
                        var t = _liveWs();
                        if (!t) return;
                        if (!nextAuth || nextAuth.type === 'none') {
                            delete t.auth;
                        } else {
                            t.auth = nextAuth;
                        }
                        if (typeof persistWorkspaces === 'function') persistWorkspaces();
                    }
                ));
            }
            return main;
        }

        // ---- Secrets tab ----
        if (workspaceSettingsTab === 'secrets') {
            main.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                style: 'margin:8px 0',
                textContent: 'Session-only secrets — pass-through values for {{secret.NAME}} resolutions. Never written to disk, never exported.'
            }));
            var secretsMap = (typeof getWorkspaceSecrets === 'function') ? getWorkspaceSecrets(ws.id) : {};
            main.appendChild(_renderKvSection('Secrets', secretsMap, function (next) {
                var oldNames = Object.keys(secretsMap);
                var newNames = Object.keys(next);
                oldNames.forEach(function (n) {
                    if (newNames.indexOf(n) < 0 && typeof setWorkspaceSecret === 'function') {
                        setWorkspaceSecret(n, null, ws.id);
                    }
                });
                newNames.forEach(function (n) {
                    if (next[n] !== secretsMap[n] && typeof setWorkspaceSecret === 'function') {
                        setWorkspaceSecret(n, next[n], ws.id);
                    }
                });
            }, true));
            return main;
        }

        // ---- General tab (default) ----

        // AI-Override hint (#193 item 4 follow-up) — when the active
        // workspace has its own AI override file (override > global
        // resolution), surface a banner + Quick-Link to Settings →
        // Assistant where the operator can clear or edit it. Only
        // rendered for the ACTIVE workspace since refreshAiStatus
        // fetches against activeWorkspaceId; other workspaces in the
        // tree are read-only previews and don't surface the override
        // chip.
        if (isActive && typeof window.__bowireAi === 'object' && window.__bowireAi
                     && typeof window.__bowireAi.getStatus === 'function') {
            var _aiSt = window.__bowireAi.getStatus();
            if (_aiSt && _aiSt.hasOverride) {
                var aiBanner = el('div', { className: 'bowire-ws-detail-ai-override' });
                aiBanner.appendChild(el('span', {
                    className: 'bowire-ws-detail-ai-override-tag',
                    textContent: 'AI override'
                }));
                aiBanner.appendChild(el('span', {
                    className: 'bowire-ws-detail-ai-override-desc',
                    textContent: 'This workspace overrides the global AI default — currently using '
                        + (_aiSt.providerId || 'unknown') + ' / ' + (_aiSt.model || 'no model') + '.'
                }));
                aiBanner.appendChild(el('button', {
                    className: 'bowire-ws-detail-ai-override-link',
                    textContent: 'Open Assistant Settings',
                    onClick: function () {
                        if (typeof openSettings === 'function') openSettings('ai');
                    }
                }));
                main.appendChild(aiBanner);
            }
        }

        // Color picker — predefined palette + native colour input for
        // custom picks, mirrors the env editor's pattern so the two
        // surfaces share the same affordance.
        var palette = ['#6366f1', '#22c55e', '#f59e0b', '#ec4899', '#06b6d4', '#a855f7', '#ef4444', '#64748b'];
        var colorRowInner = el('div', { className: 'bowire-ws-detail-color-row' },
            palette.map(function (c) {
                return el('button', {
                    className: 'bowire-ws-detail-color-swatch' + (ws.color === c ? ' selected' : ''),
                    style: 'background:' + c,
                    title: c,
                    onClick: function () {
                        var t = _liveWs();
                        if (t && t.color !== c) {
                            var prevColor = t.color;
                            var targetId = t.id;
                            t.color = c;
                            persistWorkspaces();
                            // #194 — colour picks are reversible.
                            if (typeof recordAction === 'function') {
                                recordAction({
                                    kind: 'workspace-color',
                                    rail: 'workspaces',
                                    title: 'Changed workspace colour',
                                    undoSpec: { workspaceId: targetId, prevColor: prevColor, nextColor: c },
                                    undo: function () {
                                        var w = workspaces.find(function (x) { return x.id === targetId; });
                                        if (w) { w.color = prevColor; persistWorkspaces(); render(); }
                                    },
                                    redo: function () {
                                        var w = workspaces.find(function (x) { return x.id === targetId; });
                                        if (w) { w.color = c; persistWorkspaces(); render(); }
                                    }
                                });
                            }
                            render();
                        }
                    }
                });
            })
        );
        // Native colour input — onInput fires continuously as the user
        // drags the picker so the workspace colour previews live across
        // every surface that paints with it (chip glyph, identity band,
        // rail tint). Skip render() per input event because the inline
        // background style already updates; commit happens on dialog
        // close (which fires no further events).
        colorRowInner.appendChild(el('input', {
            type: 'color',
            className: 'bowire-ws-detail-color-picker',
            value: ws.color || '#6366f1',
            title: 'Custom colour',
            'aria-label': 'Custom workspace colour',
            onInput: function (e) {
                var t = _liveWs();
                if (t) {
                    t.color = e.target.value;
                    persistWorkspaces();
                    render();
                }
            }
        }));
        var colorRow = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Color' }),
            colorRowInner
        );
        main.appendChild(colorRow);

        var descRow = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Description' }),
            el('textarea', {
                className: 'bowire-ws-detail-desc',
                value: ws.description || '',
                placeholder: 'Notes — what this workspace is for, who shares it, …',
                rows: '3',
                onChange: function (e) {
                    var t = _liveWs();
                    if (t) {
                        t.description = String(e.target.value || '').trim();
                        persistWorkspaces();
                    }
                }
            })
        );
        main.appendChild(descRow);

        // Stats grid — counts of what's in this workspace. Each tile is
        // clickable for the ACTIVE workspace: jumps to the matching rail
        // mode so the operator can drill in. Non-active tiles render as
        // inert hints (the count is a stored snapshot, not live).
        function statTile(label, value, hint, navTo) {
            var Tag = navTo ? 'button' : 'div';
            var node = el(Tag, {
                className: 'bowire-ws-detail-stat' + (navTo ? ' bowire-ws-detail-stat-clickable' : ''),
                type: navTo ? 'button' : undefined,
                onClick: navTo ? function () {
                    railMode = navTo;
                    try { localStorage.setItem('bowire_rail_mode', navTo); } catch { /* ignore */ }
                    render();
                } : undefined
            },
                el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(value) }),
                el('div', { className: 'bowire-ws-detail-stat-label', textContent: label }),
                hint ? el('div', { className: 'bowire-ws-detail-stat-hint', textContent: hint }) : null
            );
            return node;
        }
        // Only the ACTIVE workspace's state is currently in memory.
        // For non-active workspaces the counts are stored hints from
        // the persisted workspace meta (added in future as needed); for
        // now show '—' so it's clear those reflect 'currently loaded'.
        var urlCount = isActive
            ? ((typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) ? serverUrls.length : 0)
            : '—';
        var connectedCount = 0;
        if (isActive && typeof connectionStatuses === 'object' && connectionStatuses
            && typeof serverUrls !== 'undefined') {
            for (var ui = 0; ui < serverUrls.length; ui++) {
                if (connectionStatuses[serverUrls[ui]] === 'connected') connectedCount++;
            }
        }
        var recordingCount = isActive ? (Array.isArray(recordingsList) ? recordingsList.length : 0) : '—';
        var collectionCount = isActive ? (Array.isArray(collectionsList) ? collectionsList.length : 0) : '—';
        var favCount = '—';
        if (isActive && typeof getFavorites === 'function') {
            try { favCount = getFavorites().length; } catch { /* ignore */ }
        }
        var mockCount = isActive && Array.isArray(mocksList) ? mocksList.length : '—';
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Contents' }),
            el('div', { className: 'bowire-ws-detail-stats' },
                statTile('URLs', urlCount,
                    isActive
                        ? (urlCount === 0 ? 'add one below' : connectedCount + ' connected')
                        : 'switch to load',
                    isActive ? 'discover' : null),
                statTile('Environments', envCount,
                    envCount === 0 ? 'add one in the tree' : 'in this workspace',
                    isActive ? 'environments' : null),
                statTile('Recordings', recordingCount,
                    isActive ? 'in this workspace' : 'switch to load',
                    isActive ? 'recordings' : null),
                statTile('Collections', collectionCount,
                    isActive ? 'in this workspace' : 'switch to load',
                    isActive ? 'collections' : null),
                statTile('Favorites', favCount,
                    isActive ? 'starred methods' : 'switch to load',
                    isActive ? 'home' : null),
                statTile('Mocks', mockCount,
                    isActive ? 'running now' : 'switch to load',
                    isActive ? 'mocks' : null)
            )
        ));

        // #212 Phase 0 — workspace-level storage decision. Single
        // toggle (Browser-only opt-out) drives every entity store
        // (recordings, env-sync, future collections / flows). The
        // dedicated "Recording storage" radio (both / disk-only /
        // browser-only) retired — workspace.storage answers the
        // question once. When #196 adds the per-entity git layout,
        // the third value 'git' slots in here without changing the
        // shape.
        var storageMode = (typeof getWorkspaceStorageMode === 'function')
            ? getWorkspaceStorageMode(ws) : 'disk';
        var browserOnly = storageMode === 'browser-only';
        var storageSection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Storage' }),
            el('label', { className: 'bowire-ws-detail-storage-toggle' },
                el('input', {
                    type: 'checkbox',
                    checked: browserOnly ? 'checked' : null,
                    onChange: function (e) {
                        var t = _liveWs();
                        if (t && typeof setWorkspaceStorageMode === 'function') {
                            setWorkspaceStorageMode(t.id, e.target.checked ? 'browser-only' : 'disk');
                            render();
                        }
                    }
                }),
                el('span', { className: 'bowire-ws-detail-storage-toggle-label',
                    textContent: 'Browser-only — skip disk writes' }),
                el('span', { className: 'bowire-ws-detail-storage-toggle-hint',
                    textContent: browserOnly
                        ? 'localStorage is the only store. Browser clear = data loss; ~5-10 MB quota.'
                        : 'Default: ~/.bowire/workspaces/' + ws.id + '/. Browser cache as best-effort, survives quota errors.' })
            )
        );

        // #196 Phase 2.3 — per-workspace storage-root override. Only
        // meaningful when the workspace stores on disk; the field stays
        // hidden for browser-only because storageRoot is a filesystem
        // pointer that has no analogue in localStorage. Trim on blur so
        // a trailing space the operator pasted doesn't smuggle into the
        // path resolver. Empty string clears the override → backend
        // falls back to ~/.bowire/workspaces/<id>/.
        if (!browserOnly) {
            var currentRoot = (typeof getWorkspaceStorageRoot === 'function')
                ? getWorkspaceStorageRoot(ws) : null;
            storageSection.appendChild(el('div', { className: 'bowire-ws-detail-storage-root' },
                el('label', { className: 'bowire-ws-detail-storage-root-label',
                    textContent: 'Storage root' }),
                el('input', {
                    type: 'text',
                    className: 'bowire-ws-detail-storage-root-input',
                    value: currentRoot || '',
                    placeholder: 'Default: ~/.bowire/workspaces/' + ws.id + '/',
                    onBlur: function (e) {
                        var t = _liveWs();
                        if (!t || typeof setWorkspaceStorageRoot !== 'function') return;
                        var raw = String(e.target.value == null ? '' : e.target.value);
                        var trimmed = raw.trim();
                        var prior = (typeof getWorkspaceStorageRoot === 'function')
                            ? (getWorkspaceStorageRoot(t) || '') : '';
                        if (trimmed === prior) return;
                        setWorkspaceStorageRoot(t.id, trimmed);
                        render();
                    }
                }),
                el('span', { className: 'bowire-ws-detail-storage-toggle-hint',
                    textContent: 'Absolute path to a workspace folder on disk (e.g. a checked-out git repo). Leave empty for the default per-user location.' })
            ));
            // #155 follow-up — disk-mode workspaces carry recordings
            // and collections on disk; the .bww download / Import
            // surface only round-trips localStorage state. For a full
            // disk-mode round-trip, point the operator at the new
            // 'bowire workspace export' / 'import' CLI (#149).
            storageSection.appendChild(el('div', {
                className: 'bowire-ws-detail-storage-cli-hint'
            },
                el('span', {
                    className: 'bowire-ws-detail-storage-cli-hint-label',
                    textContent: 'CLI round-trip:'
                }),
                el('code', {
                    className: 'bowire-ws-detail-storage-cli-hint-code',
                    textContent: 'bowire workspace export <file.json>'
                }),
                el('span', {
                    className: 'bowire-ws-detail-storage-cli-hint-tail',
                    textContent: ' captures every per-entity file on disk; ' +
                        '\'workspace import\' materialises the export into the per-entity layout. ' +
                        'Use this for sharing or archiving disk-mode workspaces.'
                })
            ));
        }

        main.appendChild(storageSection);

        // #299 — External proxy endpoint (per-workspace). Optional URL
        // of an externally-running `bowire proxy` instance. Required
        // for embedded hosts that can't run the proxy in-process and
        // useful for teams that share a central capture host. Empty ⇒
        // standalone falls back to loopback; embedded shows the
        // "Proxy runs outside this host" empty state in the Proxy
        // rail.
        var currentProxyEndpoint = (typeof getWorkspaceProxyEndpoint === 'function')
            ? getWorkspaceProxyEndpoint(ws.id) : '';
        var proxySection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Proxy' }),
            el('div', { className: 'bowire-ws-detail-storage-root' },
                el('label', { className: 'bowire-ws-detail-storage-root-label',
                    textContent: 'External proxy endpoint' }),
                el('input', {
                    type: 'text',
                    className: 'bowire-ws-detail-storage-root-input',
                    value: currentProxyEndpoint,
                    placeholder: 'http://proxy.example.internal:8889',
                    'aria-label': 'External proxy endpoint URL',
                    onBlur: function (e) {
                        var t = _liveWs();
                        if (!t || typeof setWorkspaceProxyEndpoint !== 'function') return;
                        var raw = String(e.target.value == null ? '' : e.target.value).trim();
                        var prior = getWorkspaceProxyEndpoint(t.id) || '';
                        if (raw === prior) return;
                        setWorkspaceProxyEndpoint(t.id, raw);
                        // Reset the rail state so the next visit
                        // re-connects against the new endpoint.
                        if (typeof proxyConnectionState !== 'undefined') {
                            proxyConnectionState = 'idle';
                        }
                        render();
                    }
                }),
                el('span', { className: 'bowire-ws-detail-storage-toggle-hint',
                    textContent: 'URL of an externally-running `bowire proxy` the Proxy rail should talk to. Leave empty for standalone CLI default (loopback). Embedded hosts (MapBowire) need this set to use the rail.' })
            )
        );
        main.appendChild(proxySection);

        // #193 Phase 2 item 3 — Required plugins (pluginPins) editor.
        // Lets the operator declare which protocols this workspace
        // expects so a team member opening it gets the pin-check
        // banner instead of cryptic "no such protocol" errors at
        // first request. Lives between Storage and Metadata because
        // it's project-scoped configuration like Storage — both
        // travel with the workspace, not the user.
        if (typeof ensurePluginCatalog === 'function') ensurePluginCatalog();
        var catalogInfo = (typeof getCachedPluginCatalog === 'function')
            ? getCachedPluginCatalog() : { loaded: [], catalog: {} };
        var currentPins = (typeof getWorkspacePluginPins === 'function')
            ? getWorkspacePluginPins(ws.id) : {};
        var pinIds = Object.keys(currentPins).sort();

        var pinsSection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Required plugins' }));
        pinsSection.appendChild(el('p', {
            className: 'bowire-ws-detail-stat-hint',
            style: 'margin-bottom:10px',
            textContent: 'Pins the protocols this workspace expects. A team member opening it gets a banner if any are missing; use \'*\' for any version, or a semver like \'1.5.0\' / \'>=1.4.0\'.'
        }));

        // Current pins table — minimal, no header row when empty so
        // the empty state reads as a single line of guidance rather
        // than an "empty table" UI smell.
        if (pinIds.length === 0) {
            pinsSection.appendChild(el('p', {
                className: 'bowire-pane-empty',
                style: 'margin:6px 0 12px;',
                textContent: 'No pins declared — this workspace accepts any protocol set.'
            }));
        } else {
            var list = el('div', { className: 'bowire-ws-detail-pins-list' });
            pinIds.forEach(function (pid) {
                var row = el('div', { className: 'bowire-ws-detail-pins-row' });
                row.appendChild(el('span', { className: 'bowire-ws-detail-pins-id', textContent: pid }));
                row.appendChild(el('input', {
                    type: 'text',
                    className: 'bowire-ws-detail-pins-version',
                    value: currentPins[pid],
                    'aria-label': 'Version constraint for ' + pid,
                    onBlur: function (e) {
                        var t = _liveWs();
                        if (!t || typeof setWorkspacePluginPins !== 'function') return;
                        var v = String(e.target.value || '').trim();
                        var pins = getWorkspacePluginPins(t.id);
                        if (v.length === 0) { delete pins[pid]; }
                        else { pins[pid] = v; }
                        setWorkspacePluginPins(t.id, pins);
                        if (typeof triggerWorkspacePinCheck === 'function') triggerWorkspacePinCheck(t.id);
                        render();
                    }
                }));
                row.appendChild(el('button', {
                    className: 'bowire-ws-detail-pins-remove',
                    'aria-label': 'Remove pin for ' + pid,
                    textContent: '✕',
                    onClick: function () {
                        var t = _liveWs();
                        if (!t || typeof setWorkspacePluginPins !== 'function') return;
                        var pins = getWorkspacePluginPins(t.id);
                        delete pins[pid];
                        setWorkspacePluginPins(t.id, pins);
                        if (typeof triggerWorkspacePinCheck === 'function') triggerWorkspacePinCheck(t.id);
                        render();
                    }
                }));
                list.appendChild(row);
            });
            pinsSection.appendChild(list);
        }

        // Add row — dropdown fed by the catalog minus the already-
        // pinned set. Version input defaults to '*' so the operator
        // can hit Add without thinking about semver if they just
        // want "this protocol must be loaded, any version is fine".
        var catalogIds = Object.keys(catalogInfo.catalog || {})
            .filter(function (id) { return !currentPins[id]; })
            .sort();
        if (catalogIds.length > 0) {
            var addRow = el('div', { className: 'bowire-ws-detail-pins-addrow' });
            var addSelect = el('select', { className: 'bowire-ws-detail-pins-addselect',
                'aria-label': 'Protocol to pin' });
            catalogIds.forEach(function (id) {
                addSelect.appendChild(el('option', { value: id, textContent: id }));
            });
            var addVersion = el('input', {
                type: 'text',
                className: 'bowire-ws-detail-pins-addversion',
                value: '*',
                'aria-label': 'Version constraint'
            });
            var addBtn = el('button', {
                className: 'bowire-presets-btn',
                textContent: 'Add pin',
                onClick: function () {
                    var t = _liveWs();
                    if (!t || typeof setWorkspacePluginPins !== 'function') return;
                    var id = addSelect.value;
                    var v = String(addVersion.value || '').trim() || '*';
                    if (!id) return;
                    var pins = getWorkspacePluginPins(t.id);
                    pins[id] = v;
                    setWorkspacePluginPins(t.id, pins);
                    if (typeof triggerWorkspacePinCheck === 'function') triggerWorkspacePinCheck(t.id);
                    render();
                }
            });
            addRow.appendChild(addSelect);
            addRow.appendChild(addVersion);
            addRow.appendChild(addBtn);
            pinsSection.appendChild(addRow);
        }

        // Convenience — pin every currently-loaded protocol with the
        // catalog's known packageId. Saves a lot of clicks when an
        // operator wants to lock the workspace's expected surface to
        // "exactly what's loaded right now". Version is '*' because
        // /api/plugins/protocols doesn't expose semver per loaded
        // assembly today; refine when the registry surfaces it.
        var loadedNotPinned = (catalogInfo.loaded || []).filter(function (id) {
            return !currentPins[String(id).toLowerCase()];
        });
        if (loadedNotPinned.length > 0) {
            pinsSection.appendChild(el('button', {
                className: 'bowire-presets-btn',
                style: 'margin-top:10px;',
                textContent: 'Pin all currently loaded protocols (' + loadedNotPinned.length + ')',
                onClick: function () {
                    var t = _liveWs();
                    if (!t || typeof setWorkspacePluginPins !== 'function') return;
                    var pins = getWorkspacePluginPins(t.id);
                    loadedNotPinned.forEach(function (id) {
                        pins[String(id).toLowerCase()] = '*';
                    });
                    setWorkspacePluginPins(t.id, pins);
                    if (typeof triggerWorkspacePinCheck === 'function') triggerWorkspacePinCheck(t.id);
                    render();
                }
            }));
        }

        main.appendChild(pinsSection);

        // Metadata strip — IDs / timestamps so the operator can see
        // creation / last-opened for audit / debugging.
        var createdStr = ws.createdAt ? new Date(ws.createdAt).toLocaleString() : '—';
        var openedStr = ws.lastOpenedAt ? new Date(ws.lastOpenedAt).toLocaleString() : '—';
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Metadata' }),
            el('div', { className: 'bowire-ws-detail-meta-grid' },
                el('div', { textContent: 'ID' }),
                el('div', { className: 'bowire-ws-detail-meta-value', textContent: ws.id }),
                el('div', { textContent: 'Created' }),
                el('div', { className: 'bowire-ws-detail-meta-value', textContent: createdStr }),
                el('div', { textContent: 'Last opened' }),
                el('div', { className: 'bowire-ws-detail-meta-value', textContent: openedStr })
            )
        ));

        // Sources, Schema-Files and Secrets sections used to live here.
        // They were moved out: Sources + Schema-Files now belong to the
        // dedicated Workspace › Sources tree node; Variables + Secrets
        // were promoted to a workspace-globals editor at Workspace ›
        // Variables, where they also serve as the fallback layer
        // beneath each Environment's own variables + secrets.

        // Share / portability — Export this workspace as a .bowire JSON
        // bundle, or import another. Secrets are intentionally excluded
        // from the export (session-only, would leak credentials).
        var shareSection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Share' })
        );
        shareSection.appendChild(el('p', {
            className: 'bowire-ws-detail-stat-hint',
            style: 'margin-bottom:10px',
            textContent: 'Export packages URLs, collections, recordings, favorites, benchmarks, flows and presets into a single JSON file. Secrets are NOT included.'
        }));
        var shareRow = el('div', { style: 'display:flex;gap:8px;flex-wrap:wrap' });
        shareRow.appendChild(el('button', {
            className: 'bowire-presets-btn',
            textContent: 'Export workspace…',
            onClick: function () {
                var t = _liveWs();
                if (!t) return;
                if (!downloadWorkspaceExport(t.id)) {
                    toast('Export failed — see console', 'error');
                    return;
                }
                toast('Workspace exported', 'success');
            }
        }));
        shareRow.appendChild(el('button', {
            className: 'bowire-presets-btn',
            textContent: 'Import workspace…',
            onClick: function () {
                var input = document.createElement('input');
                input.type = 'file';
                input.accept = '.json,.bowire,application/json';
                input.onchange = function () {
                    if (!input.files || input.files.length === 0) return;
                    var file = input.files[0];
                    var reader = new FileReader();
                    reader.onload = function () {
                        var payload;
                        try { payload = JSON.parse(reader.result); }
                        catch (e) {
                            toast('Could not parse the file as JSON', 'error');
                            return;
                        }
                        var t = _liveWs();
                        if (t) _showWorkspaceImportDialog(payload, t);
                    };
                    reader.readAsText(file);
                };
                input.click();
            }
        }));
        shareSection.appendChild(shareRow);
        main.appendChild(shareSection);

        // Danger zone — delete button. Allowed even on the last workspace
        // (deleteWorkspace falls back to the empty no-workspace state); the
        // confirm copy spells out the consequence when this is the last one.
        main.appendChild(el('div', { className: 'bowire-ws-detail-section bowire-ws-detail-danger' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Danger zone' }),
            el('button', {
                className: 'bowire-settings-action-btn bowire-ws-detail-delete-btn',
                textContent: 'Delete this workspace',
                onClick: function () {
                    var t = _liveWs();
                    if (!t) return;
                    var wsName = t.name;
                    var wsId = t.id;
                    var isLast = workspaces.length === 1;
                    // v2.2 W2 — Hard-delete branch gets its own warning
                    // copy; soft-delete keeps the v2.1 copy verbatim.
                    var mode = (typeof getWorkspaceDeleteMode === 'function')
                        ? getWorkspaceDeleteMode() : 'soft';
                    var hardMsg = 'This workspace will be deleted IMMEDIATELY. Undo will work for the next ~200 actions, but it won’t be in the Trash. Continue?';
                    var softMsg = isLast
                        ? 'Delete the last workspace "' + wsName + '"? You will return to the empty no-workspace state — URLs / envs / recordings scoped to this workspace are removed.'
                        : 'Delete workspace "' + wsName + '"? URLs / envs / recordings scoped to this workspace are removed.';
                    var msg = mode === 'hard' ? hardMsg : softMsg;
                    bowireConfirm(
                        msg,
                        function () {
                            var snapshotName = wsName;
                            deleteWorkspace(wsId);
                            workspacesSelectedId = activeWorkspaceId;
                            render();
                            // #194 — soft-delete: route through the
                            // action log so the operator can recover
                            // via Ctrl/Cmd+Z, the toast Undo button,
                            // or the global trash drawer's Workspaces
                            // bucket.
                            // v2.2 W2a — pass the snapshot inline so
                            // the resolver doesn't have to consult
                            // workspacesTrash (which is empty in hard
                            // mode anyway).
                            if (typeof toast === 'function') {
                                var sidechannel = (typeof _lastWorkspaceDeleteSnapshot === 'object'
                                    && _lastWorkspaceDeleteSnapshot)
                                    ? _lastWorkspaceDeleteSnapshot[wsId] : null;
                                if (sidechannel) {
                                    try { delete _lastWorkspaceDeleteSnapshot[wsId]; } catch { /* ignore */ }
                                }
                                toast('Deleted workspace "' + snapshotName + '"', 'info', {
                                    undo: function () {
                                        var t = (typeof workspacesTrash !== 'undefined'
                                            && Array.isArray(workspacesTrash))
                                            ? workspacesTrash.find(function (x) { return x && x.workspace && x.workspace.id === wsId; })
                                            : null;
                                        if (t && typeof restoreWorkspaceFromTrash === 'function') {
                                            restoreWorkspaceFromTrash(t);
                                            workspacesSelectedId = wsId;
                                            render();
                                            return;
                                        }
                                        if (sidechannel
                                            && typeof restoreWorkspaceFromSnapshot === 'function') {
                                            restoreWorkspaceFromSnapshot(sidechannel);
                                            workspacesSelectedId = wsId;
                                            render();
                                        }
                                    },
                                    logAction: {
                                        kind: 'workspace-delete',
                                        rail: 'workspaces',
                                        title: 'Deleted workspace "' + snapshotName + '"',
                                        undoSpec: {
                                            workspaceId: wsId,
                                            workspace: sidechannel ? sidechannel.workspace : null,
                                            data: sidechannel ? sidechannel.data : null,
                                            originalIdx: sidechannel ? sidechannel.originalIdx : null,
                                            mode: sidechannel ? sidechannel.mode : 'soft'
                                        }
                                    }
                                });
                            }
                        },
                        {
                            title: mode === 'hard' ? 'Hard-delete workspace' : 'Delete workspace',
                            confirmText: mode === 'hard' ? 'Delete forever' : 'Delete',
                            danger: true
                        }
                    );
                }
            })
        ));

        return main;
    }

    // #192 — Tree sub-views. URL leaf opens a focused URL editor;
    // Sources node shows the URL list with the same chrome as the
    // Sources rail but rooted in the picked workspace; Environments /
    // Collections / Recordings show a "switch rail" jump card because
    // the full editors already live in their dedicated rails.
    // _renderWorkspaceUrlDetail retired — its dead-end "Open in Sources
    // rail" / "Remove URL" view was replaced by routing URL leaf clicks
    // directly into renderSourcesDetailMain (the rich URL editor) in
    // renderWorkspaceDetailMain. Remove URL still works via the URL-leaf
    // right-click context menu added in the workspace-polish pass.

    function _renderWorkspaceSourcesDetail(ws) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        var isActive = ws.id === activeWorkspaceId;

        main.appendChild(_renderWorkspaceBreadcrumb(ws, [
            { label: 'Sources' }
        ]));

        var section = el('div', { className: 'bowire-ws-detail-section' });
        section.appendChild(el('div', { className: 'bowire-ws-detail-section-label', textContent: 'URLs' }));

        if (!isActive) {
            section.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                textContent: 'Switch to this workspace to manage its URLs and schema files.'
            }));
            main.appendChild(section);
            return main;
        }

        // Live URL list with discovery status, service count, and
        // Discover / Remove actions. Mirrors the source-of-truth on the
        // active workspace — non-active workspaces fell through above.
        var urls = (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) ? serverUrls : [];
        if (urls.length === 0) {
            section.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                style: 'margin-bottom:8px',
                textContent: 'No URLs yet. Add one below to start discovery.'
            }));
        } else {
            var srcList = el('div', { style: 'display:flex;flex-direction:column;gap:4px;margin-bottom:8px' });
            urls.forEach(function (u) {
                var st = (typeof connectionStatuses === 'object' && connectionStatuses)
                    ? (connectionStatuses[u] || 'disconnected') : 'disconnected';
                var meta = (typeof getUrlMeta === 'function') ? getUrlMeta(u) : {};
                var name = meta.name || (typeof _stripUrlPrefix === 'function' ? _stripUrlPrefix(u) : u);
                var svcN = (typeof services !== 'undefined')
                    ? services.filter(function (s) {
                        return typeof urlMatchesService === 'function'
                            ? urlMatchesService(u, s) : s.originUrl === u;
                    }).length : 0;
                srcList.appendChild(el('div', {
                    style: 'display:flex;align-items:center;gap:8px;padding:6px 8px;background:var(--bowire-surface);border:1px solid var(--bowire-border-subtle);border-radius:var(--bowire-radius-sm)'
                },
                    el('span', {
                        className: 'bowire-conn-pill-dot bowire-conn-pill-dot-' + st,
                        style: 'width:10px;height:10px;border-radius:50%;flex-shrink:0;background:'
                            + (st === 'connected' ? 'var(--bowire-success)'
                                : st === 'error' ? 'var(--bowire-danger)'
                                : st === 'discovering' ? 'var(--bowire-warning)'
                                : 'var(--bowire-text-tertiary)')
                    }),
                    el('span', {
                        style: 'flex:1;font-size:12px;font-family:var(--bowire-mono);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer',
                        textContent: name,
                        title: u,
                        onClick: function () {
                            workspaceTreeSelection = { wsId: ws.id, kind: 'url', value: u };
                            render();
                        }
                    }),
                    el('span', { style: 'font-size:11px;color:var(--bowire-text-tertiary);flex-shrink:0', textContent: svcN + ' svc' + (svcN === 1 ? '' : 's') }),
                    el('button', {
                        className: 'bowire-ws-detail-action',
                        title: 'Open this URL in Discover',
                        textContent: 'Discover',
                        onClick: function () {
                            railMode = 'discover';
                            sidebarView = 'services';
                            try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                            render();
                        }
                    }),
                    el('button', {
                        className: 'bowire-ws-detail-action bowire-ws-detail-danger',
                        title: 'Remove this URL from the workspace',
                        textContent: 'Remove',
                        onClick: function () {
                            bowireConfirm('Remove URL "' + name + '"?', {
                                confirmText: 'Remove',
                                danger: true
                            }).then(function (ok) {
                                if (!ok) return;
                                var idx = serverUrls.indexOf(u);
                                if (idx >= 0) serverUrls.splice(idx, 1);
                                if (typeof persistServerUrls === 'function') persistServerUrls();
                                render();
                            });
                        }
                    })
                ));
            });
            section.appendChild(srcList);
        }
        section.appendChild(el('button', {
            className: 'bowire-ws-detail-action',
            style: 'margin-top:4px',
            textContent: '+ Add URL',
            onClick: function () {
                bowirePrompt('Server URL', {
                    title: 'Add URL',
                    placeholder: 'e.g. https://petstore3.swagger.io/api/v3/openapi.json',
                    confirmText: 'Add'
                }).then(function (raw) {
                    if (!raw) return;
                    var trimmed = String(raw).trim();
                    if (!trimmed) return;
                    if (serverUrls.indexOf(trimmed) < 0) {
                        serverUrls.push(trimmed);
                        if (typeof persistServerUrls === 'function') persistServerUrls();
                        if (typeof fetchServices === 'function') fetchServices();
                    }
                    render();
                });
            }
        }));

        // Schema files — drop-zone for .proto / .openapi.json / .yaml.
        // Used to live in Workspace > Settings; moved here so Sources
        // is the single mount site for everything that introduces a
        // service catalogue into the workspace (URLs + schema files).
        section.appendChild(el('div', {
            className: 'bowire-ws-detail-section-label',
            style: 'margin-top:14px',
            textContent: 'Schema files'
        }));
        if (typeof renderProtoUploadPanel === 'function') {
            section.appendChild(renderProtoUploadPanel());
        }
        main.appendChild(section);
        return main;
    }

    // _renderWorkspaceVariablesDetail retired — workspace-scope
    // variables + secrets moved into the Workspace settings pane as
    // the Variables + Secrets tabs (see _renderWorkspaceSettingsDetail).
    // Tree leaf removed alongside; the kind === 'variables' route
    // keeps working as a back-compat alias.

    // Shared key/value table renderer for Variables + Secrets sections.
    // `masked` switches the value input to type=password and changes
    // the empty-state + placeholder copy. `onChange(nextMap)` is the
    // persist callback: it gets the new map after add / edit / remove.
    function _renderKvSection(title, map, onChange, masked) {
        var section = el('div', { className: 'bowire-ws-detail-section' });
        section.appendChild(el('div', { className: 'bowire-ws-detail-section-label', textContent: title }));

        var keys = Object.keys(map).sort();
        if (keys.length > 0) {
            var list = el('div', { style: 'display:flex;flex-direction:column;gap:4px;margin-bottom:8px' });
            keys.forEach(function (k) {
                list.appendChild(el('div', {
                    style: 'display:flex;align-items:center;gap:8px;padding:6px 8px;background:var(--bowire-surface);border:1px solid var(--bowire-border-subtle);border-radius:var(--bowire-radius-sm)'
                },
                    el('span', { style: 'flex:0 0 30%;font-family:var(--bowire-mono);font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap', textContent: k, title: k }),
                    el('input', {
                        type: masked ? 'password' : 'text',
                        value: map[k] || '',
                        placeholder: masked ? '(secret)' : 'value',
                        style: 'flex:1;min-width:0;font-family:var(--bowire-mono);font-size:12px;background:transparent;border:1px solid var(--bowire-border);border-radius:var(--bowire-radius-sm);padding:4px 6px;color:var(--bowire-text)',
                        onChange: function (e) {
                            var next = Object.assign({}, map);
                            next[k] = e.target.value;
                            onChange(next);
                        }
                    }),
                    el('button', {
                        className: 'bowire-ws-detail-action bowire-ws-detail-danger',
                        title: 'Remove ' + k,
                        textContent: 'Remove',
                        onClick: function () {
                            var next = Object.assign({}, map);
                            delete next[k];
                            onChange(next);
                            render();
                        }
                    })
                ));
            });
            section.appendChild(list);
        } else {
            section.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                style: 'margin-bottom:8px',
                textContent: masked
                    ? 'No secrets yet. Secrets are session-only — Phase 5 wraps an OS keyring.'
                    : 'No variables yet.'
            }));
        }

        section.appendChild(el('button', {
            className: 'bowire-ws-detail-action',
            textContent: '+ Add ' + (masked ? 'secret' : 'variable'),
            onClick: function () {
                bowirePrompt(masked ? 'Secret name' : 'Variable name', {
                    title: masked ? 'Add secret' : 'Add variable',
                    placeholder: masked ? 'e.g. GH_TOKEN' : 'e.g. baseUrl',
                    confirmText: 'Next'
                }).then(function (name) {
                    if (!name) return;
                    var trimmed = String(name).trim();
                    if (!trimmed) return;
                    bowirePrompt('Value for ' + trimmed, {
                        title: masked ? 'Set secret value' : 'Set variable value',
                        placeholder: masked ? '(value is not stored on disk)' : 'value',
                        confirmText: 'Save'
                    }).then(function (value) {
                        if (value == null) return;
                        var next = Object.assign({}, map);
                        next[trimmed] = String(value);
                        onChange(next);
                        render();
                    });
                });
            }
        }));

        return section;
    }

    // #164 v4 — Collections + Environments now render INSIDE the
    // workspace tree main pane instead of redirecting to a separate
    // rail. Overview = "pick one or create" with the list; Detail =
    // the existing collection / environment editor with a header
    // breadcrumb so the workspace context stays visible.

    function _renderWorkspaceCollectionsOverview(ws) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        var isActive = ws.id === activeWorkspaceId;
        var cols = isActive
            ? ((typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) ? collectionsList : [])
            : (typeof readWorkspaceJsonList === 'function' ? readWorkspaceJsonList(ws.id, COLLECTIONS_KEY) : []);

        main.appendChild(_renderWorkspaceBreadcrumb(ws, [
            { label: 'Collections' }
        ]));

        var section = el('div', { className: 'bowire-ws-detail-section' });
        section.appendChild(el('div', { className: 'bowire-ws-detail-section-label',
            textContent: cols.length === 0 ? 'No collections yet' : 'Collections (' + cols.length + ')' }));
        if (cols.length === 0) {
            section.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                textContent: 'Collections bundle saved requests so you can replay them as a set. Create one below to start.'
            }));
        } else {
            var list = el('div', { style: 'display:flex;flex-direction:column;gap:4px;margin-top:6px' });
            cols.forEach(function (c) {
                var itemCount = Array.isArray(c.items) ? c.items.length : 0;
                list.appendChild(el('div', {
                    className: 'bowire-ws-detail-stat-hint',
                    style: 'cursor:pointer;padding:6px 8px;border-radius:var(--bowire-radius-sm);display:flex;align-items:center;gap:8px',
                    onClick: function () {
                        if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                        workspaceTreeSelection = { wsId: ws.id, kind: 'collection', value: c.id };
                        if (typeof collectionManagerSelectedId !== 'undefined') {
                            collectionManagerSelectedId = c.id;
                        }
                        render();
                    }
                },
                    el('span', { innerHTML: svgIcon('folder'), style: 'width:14px;height:14px;display:inline-flex;flex-shrink:0' }),
                    el('span', { style: 'flex:1', textContent: c.name || '(unnamed)' }),
                    el('span', { style: 'opacity:0.6;font-size:11px', textContent: itemCount + (itemCount === 1 ? ' item' : ' items') })
                ));
            });
            section.appendChild(list);
        }
        section.appendChild(el('button', {
            className: 'bowire-ws-detail-action',
            style: 'margin-top:8px',
            textContent: '+ New collection',
            onClick: function () {
                if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                if (typeof createCollection !== 'function') return;
                var col = createCollection();
                if (typeof collectionManagerSelectedId !== 'undefined') {
                    collectionManagerSelectedId = col.id;
                }
                workspaceTreeSelection = { wsId: ws.id, kind: 'collection', value: col.id };
                render();
            }
        }));
        main.appendChild(section);
        return main;
    }

    function _renderWorkspaceCollectionDetail(ws, collectionId) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        var isActive = ws.id === activeWorkspaceId;
        if (!isActive) {
            // Editor needs the active workspace's in-memory state to
            // write back via persistCollections, so switching is
            // implicit — the click handler upstream already promoted
            // the workspace, but a direct route-in via tree selection
            // could land here without that. Show a quick hint instead
            // of silently editing the wrong workspace's data.
            main.appendChild(el('p', {
                className: 'bowire-pane-empty bowire-main-pad',
                textContent: 'Switch to ' + ws.name + ' to edit this collection.'
            }));
            main.appendChild(el('button', {
                className: 'bowire-ws-detail-action',
                style: 'margin:0 var(--bowire-main-gutter)',
                textContent: 'Switch to ' + ws.name,
                onClick: function () { switchWorkspace(ws.id); }
            }));
            return main;
        }
        var col = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList))
            ? collectionsList.find(function (c) { return c.id === collectionId; })
            : null;
        if (!col) {
            workspaceTreeSelection = { wsId: ws.id, kind: 'collections' };
            return _renderWorkspaceCollectionsOverview(ws);
        }
        main.appendChild(_renderWorkspaceBreadcrumb(ws, [
            { label: 'Collections', onClick: function () {
                workspaceTreeSelection = { wsId: ws.id, kind: 'collections' };
                render();
            } },
            { label: col.name || '(unnamed)' }
        ]));
        if (typeof collectionManagerSelectedId !== 'undefined') {
            collectionManagerSelectedId = col.id;
        }
        if (typeof renderCollectionDetail === 'function') {
            main.appendChild(renderCollectionDetail(col));
        }
        return main;
    }

    function _renderWorkspaceRecordingsOverview(ws) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        var isActive = ws.id === activeWorkspaceId;
        var recs = isActive
            ? ((typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) ? recordingsList : [])
            : (typeof readWorkspaceJsonList === 'function' ? readWorkspaceJsonList(ws.id, RECORDINGS_KEY) : []);

        main.appendChild(_renderWorkspaceBreadcrumb(ws, [
            { label: 'Recordings' }
        ]));

        var section = el('div', { className: 'bowire-ws-detail-section' });
        section.appendChild(el('div', { className: 'bowire-ws-detail-section-label',
            textContent: recs.length === 0 ? 'No recordings yet' : 'Recordings (' + recs.length + ')' }));
        if (recs.length === 0) {
            section.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                textContent: 'Recordings capture a sequence of live calls so you can replay them, build mocks, or run them as benchmarks. Start one from Discover, or use the + below.'
            }));
        } else {
            var list = el('div', { style: 'display:flex;flex-direction:column;gap:4px;margin-top:6px' });
            recs.forEach(function (r) {
                var stepCount = Array.isArray(r.steps) ? r.steps.length : 0;
                list.appendChild(el('div', {
                    className: 'bowire-ws-detail-stat-hint',
                    style: 'cursor:pointer;padding:6px 8px;border-radius:var(--bowire-radius-sm);display:flex;align-items:center;gap:8px',
                    onClick: function () {
                        if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                        workspaceTreeSelection = { wsId: ws.id, kind: 'recording', value: r.id };
                        if (typeof recordingManagerSelectedId !== 'undefined') {
                            recordingManagerSelectedId = r.id;
                        }
                        render();
                    }
                },
                    el('span', { innerHTML: svgIcon('filmstrip'), style: 'width:14px;height:14px;display:inline-flex;flex-shrink:0' }),
                    el('span', { style: 'flex:1', textContent: r.name || '(unnamed)' }),
                    el('span', { style: 'opacity:0.6;font-size:11px', textContent: stepCount + (stepCount === 1 ? ' step' : ' steps') })
                ));
            });
            section.appendChild(list);
        }
        section.appendChild(el('button', {
            className: 'bowire-ws-detail-action',
            style: 'margin-top:8px',
            textContent: '+ Start recording',
            onClick: function () {
                if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                if (typeof startRecording === 'function') {
                    startRecording();
                    railMode = 'discover';
                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                    render();
                }
            }
        }));
        main.appendChild(section);
        return main;
    }

    function _renderWorkspaceRecordingDetail(ws, recordingId) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        var isActive = ws.id === activeWorkspaceId;
        if (!isActive) {
            main.appendChild(el('p', {
                className: 'bowire-pane-empty bowire-main-pad',
                textContent: 'Switch to ' + ws.name + ' to open this recording.'
            }));
            main.appendChild(el('button', {
                className: 'bowire-ws-detail-action',
                style: 'margin:0 var(--bowire-main-gutter)',
                textContent: 'Switch to ' + ws.name,
                onClick: function () { switchWorkspace(ws.id); }
            }));
            return main;
        }
        var rec = (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList))
            ? recordingsList.find(function (r) { return r.id === recordingId; })
            : null;
        if (!rec) {
            workspaceTreeSelection = { wsId: ws.id, kind: 'recordings' };
            return _renderWorkspaceRecordingsOverview(ws);
        }
        main.appendChild(_renderWorkspaceBreadcrumb(ws, [
            { label: 'Recordings', onClick: function () {
                workspaceTreeSelection = { wsId: ws.id, kind: 'recordings' };
                render();
            } },
            { label: rec.name || '(unnamed)' }
        ]));
        if (typeof recordingManagerSelectedId !== 'undefined') {
            recordingManagerSelectedId = rec.id;
        }
        // Chunked recording storage (#144 Phase 1) — rec.steps is
        // populated lazily from the manifest. Trigger hydration the
        // moment the detail pane mounts via the Workspace-Tree path.
        // Gate ONLY on `rec.steps === undefined` (never tried) — the
        // empty array case means hydrateRecording already ran and the
        // manifest was empty; re-triggering would render-loop forever
        // because hydrateRecording resolves synchronously when there's
        // nothing to fetch.
        if (typeof hydrateRecording === 'function' && rec.steps === undefined) {
            hydrateRecording(rec).then(function () { render(); });
        }
        if (typeof renderRecordingDetail === 'function') {
            main.appendChild(renderRecordingDetail(rec));
        }
        return main;
    }

    function _renderWorkspaceEnvironmentsOverview(ws) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        // Self-contained workspaces: envs are read from the workspace's
        // own bucket. The previous "all shared envs" + inclusion-list
        // model is retired — this overview is just "the envs in this
        // workspace", not "the envs the workspace happens to subscribe
        // to from a shared pool".
        var envs = (ws.id === activeWorkspaceId)
            ? ((typeof getEnvironments === 'function') ? getEnvironments() : [])
            : ((typeof readWorkspaceEnvironments === 'function') ? readWorkspaceEnvironments(ws.id) : []);

        // Header pattern shared with workspace settings + env detail:
        // glyph + title-stack [page name on top, trail subtitle below]
        // + right-side action slot. Standalone breadcrumb above the
        // section retired — the trail under the title is the single
        // "where am I" hint.
        var envOverviewGlyph = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" width="24" height="24">'
            + '<circle cx="12" cy="12" r="10"/>'
            + '<line x1="2" y1="12" x2="22" y2="12"/>'
            + '<path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z"/>'
            + '</svg>';
        main.appendChild(el('div', { className: 'bowire-ws-detail-header' },
            el('span', { className: 'bowire-env-editor-glyph', innerHTML: envOverviewGlyph }),
            el('div', { className: 'bowire-ws-detail-title-stack' },
                el('div', { className: 'bowire-ws-detail-title-static', textContent: 'Environments (' + envs.length + ')' }),
                _renderHeaderTrail([
                    { label: 'Workspaces', onClick: _goToWorkspacesOverview },
                    { label: ws.name || '(unnamed)', onClick: function () { _goToWorkspaceSettings(ws.id); } }
                ])
            )
        ));

        var section = el('div', { className: 'bowire-ws-detail-section' });
        if (envs.length === 0) {
            // #303 — empty state uses the canonical empty-card pattern
            // so it can host a per-rail "Take a tour" CTA alongside the
            // create affordance. Without this the section was a single
            // hint sentence + the section's own "+ New environment"
            // button at the bottom, which left no place to anchor the
            // tour entry point.
            var envEmptyWrap = el('div', { style: 'margin-top:8px' });
            envEmptyWrap.appendChild(renderEmptyCard({
                icon: 'globe',
                headline: 'No environments yet',
                body: 'Environments scope variables, auth tokens, and secrets per deployment stage. Create one for "staging", another for "prod", and toggle the active env from the topbar to re-point your requests.',
                actions: [
                    {
                        label: 'New environment',
                        primary: true,
                        onClick: function () {
                            if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                            if (typeof openCreateEnvironmentDialog !== 'function') return;
                            openCreateEnvironmentDialog(function (env) {
                                if (typeof envSidebarSelectedId !== 'undefined') {
                                    envSidebarSelectedId = env.id;
                                }
                                workspaceTreeSelection = { wsId: ws.id, kind: 'env', value: env.id };
                                render();
                                if (typeof window !== 'undefined'
                                    && typeof window.bowireFireTourEvent === 'function') {
                                    window.bowireFireTourEvent('environment-created');
                                }
                            });
                        }
                    },
                    {
                        // #303 — secondary tour: create env → add vars →
                        // reference {{var}} in a request. Force-mode so
                        // the affordance re-runs after dismissal.
                        id: 'bowire-envs-empty-tour-btn',
                        label: 'Take a tour',
                        onClick: function () {
                            if (typeof window !== 'undefined'
                                && typeof window.bowireStartSetupEnvironmentsTour === 'function') {
                                window.bowireStartSetupEnvironmentsTour({ force: true });
                            }
                        }
                    }
                ]
            }));
            section.appendChild(envEmptyWrap);
        } else {
            var activeEnvIdNow = (typeof getActiveEnvId === 'function') ? getActiveEnvId() : null;
            var list = el('div', { className: 'bowire-env-overview-list' });
            envs.forEach(function (e) {
                var varCount = e.vars ? Object.keys(e.vars).length : 0;
                var isActive = e.id === activeEnvIdNow;
                var envColor = e.color || 'var(--bowire-text-tertiary)';
                // Same filled-globe glyph as the topbar env dropdown +
                // the workspace tree env leaf — env identity reads
                // consistently across every pick surface.
                var envGlyph = '<svg viewBox="0 0 24 24" stroke="currentColor" stroke-width="2" width="14" height="14">'
                    + '<circle cx="12" cy="12" r="10" fill="' + envColor + '"/>'
                    + '<line x1="2" y1="12" x2="22" y2="12" fill="none"/>'
                    + '<path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z" fill="none"/>'
                    + '</svg>';
                var row = el('div', { className: 'bowire-env-overview-row' });
                row.appendChild(el('span', {
                    className: 'bowire-env-overview-glyph',
                    innerHTML: envGlyph
                }));
                row.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-env-overview-name',
                    textContent: e.name || '(unnamed)',
                    title: 'Open env editor',
                    onClick: function () {
                        if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                        workspaceTreeSelection = { wsId: ws.id, kind: 'env', value: e.id };
                        if (typeof envSidebarSelectedId !== 'undefined') {
                            envSidebarSelectedId = e.id;
                        }
                        render();
                    }
                }));
                // Hover tooltip on the vars-count chip: lists up to 10
                // name=value pairs (values capped at 40 chars so a
                // multi-line cert / long secret doesn't blow the
                // tooltip up). Past the cap, append '…' so the operator
                // sees there's more. Empty envs get no tooltip.
                var varsTitle = '';
                if (varCount > 0 && e.vars) {
                    var entries = Object.keys(e.vars);
                    var capN = 10;
                    var shown = entries.slice(0, capN).map(function (k) {
                        var raw = String(e.vars[k] == null ? '' : e.vars[k]);
                        if (raw.length > 40) raw = raw.slice(0, 39) + '…';
                        // Newlines in the original value would split the
                        // line in the tooltip — collapse them to a glyph
                        // so the entry stays one row.
                        raw = raw.replace(/\r?\n/g, ' ⏎ ');
                        return k + ' = ' + raw;
                    }).join('\n');
                    varsTitle = entries.length > capN
                        ? shown + '\n… (' + (entries.length - capN) + ' more)'
                        : shown;
                }
                row.appendChild(el('span', {
                    className: 'bowire-env-overview-meta',
                    title: varsTitle || undefined,
                    textContent: varCount + (varCount === 1 ? ' var' : ' vars')
                }));
                // Click-to-activate checkmark. Same idiom as the topbar
                // env dropdown's row check — solid when this env is the
                // workbench's active one, ghosted-but-clickable when it
                // isn't. Replaces the separate "Active" badge + "Set
                // active" button. Stops propagation so clicking the
                // mark doesn't ALSO open the env editor (the row's
                // name button is the open-affordance).
                row.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-env-overview-check' + (isActive ? ' is-active' : ''),
                    title: isActive ? 'Active environment' : 'Set as active environment',
                    'aria-label': isActive ? 'Active environment' : 'Set as active environment',
                    'aria-pressed': isActive ? 'true' : 'false',
                    textContent: '✓',
                    onClick: function (ev) {
                        ev.stopPropagation();
                        if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                        if (typeof setActiveEnvId === 'function') {
                            setActiveEnvId(isActive ? '' : e.id);
                        }
                        render();
                    }
                }));
                // Hover-revealed tools cluster: Rename + Delete. Active
                // toggle moved out into its own always-rendered slot to
                // the left so the row's "this is the active one" state
                // doesn't depend on hover.
                var tools = el('div', { className: 'bowire-env-overview-tools' });
                tools.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-env-overview-tool',
                    title: 'Rename environment',
                    'aria-label': 'Rename environment',
                    innerHTML: svgIcon('pencil'),
                    onClick: function () {
                        var envId = e.id;
                        var oldName = e.name || '';
                        bowirePrompt('Rename environment', {
                            title: 'Rename',
                            defaultValue: oldName,
                            confirmText: 'Rename',
                            validator: function (val) {
                                var trimmed = String(val || '').trim();
                                if (!trimmed) return 'Name required';
                                if (trimmed.toLowerCase() === String(oldName || '').trim().toLowerCase()) return null;
                                if (typeof _isEnvironmentNameTaken === 'function'
                                    && _isEnvironmentNameTaken(trimmed, envId)) {
                                    if (typeof toast === 'function') {
                                        toast('An environment named "' + trimmed + '" already exists.', 'error');
                                    }
                                    return 'Duplicate';
                                }
                                return null;
                            }
                        }).then(function (renamed) {
                            if (renamed && typeof updateEnvironment === 'function') {
                                updateEnvironment(envId, { name: renamed });
                                render();
                            }
                        });
                    }
                }));
                tools.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-env-overview-tool bowire-env-overview-tool-danger',
                    title: 'Delete environment',
                    'aria-label': 'Delete environment',
                    innerHTML: svgIcon('trash'),
                    onClick: function () {
                        var envId = e.id;
                        var envName = e.name || '(unnamed)';
                        bowireConfirm(
                            'Delete environment "' + envName + '"? Variables stored in this environment are removed.',
                            function () {
                                if (typeof deleteEnvironment === 'function') deleteEnvironment(envId);
                                render();
                            },
                            { title: 'Delete environment', confirmText: 'Delete', danger: true }
                        );
                    }
                }));
                row.appendChild(tools);
                list.appendChild(row);
            });
            section.appendChild(list);
        }
        // #303 — stable id so the set-up-environments tour can spotlight
        // the canonical "+ New environment" entry point on populated
        // overviews (the empty-card variant carries its own create
        // action with the same fire-event hook).
        section.appendChild(el('button', {
            id: 'bowire-workspace-env-new-btn',
            className: 'bowire-ws-detail-action',
            style: 'margin-top:8px',
            textContent: '+ New environment',
            onClick: function () {
                if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                if (typeof openCreateEnvironmentDialog !== 'function') return;
                openCreateEnvironmentDialog(function (env) {
                    if (typeof envSidebarSelectedId !== 'undefined') {
                        envSidebarSelectedId = env.id;
                    }
                    workspaceTreeSelection = { wsId: ws.id, kind: 'env', value: env.id };
                    render();
                    if (typeof window !== 'undefined'
                        && typeof window.bowireFireTourEvent === 'function') {
                        window.bowireFireTourEvent('environment-created');
                    }
                });
            }
        }));
        main.appendChild(section);
        return main;
    }

    function _renderWorkspaceEnvironmentDetail(ws, envId) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        var isActive = ws.id === activeWorkspaceId;
        if (!isActive) {
            main.appendChild(el('p', {
                className: 'bowire-pane-empty bowire-main-pad',
                textContent: 'Switch to ' + ws.name + ' to edit this environment.'
            }));
            main.appendChild(el('button', {
                className: 'bowire-ws-detail-action',
                style: 'margin:0 var(--bowire-main-gutter)',
                textContent: 'Switch to ' + ws.name,
                onClick: function () { switchWorkspace(ws.id); }
            }));
            return main;
        }
        var envs = (typeof getEnvironments === 'function') ? getEnvironments() : [];
        var env = envs.find(function (e) { return e.id === envId; });
        if (!env) {
            workspaceTreeSelection = { wsId: ws.id, kind: 'environments' };
            return _renderWorkspaceEnvironmentsOverview(ws);
        }
        // No breadcrumb on env detail — the env editor's header carries
        // the editable name input, and the workspace tree sidebar
        // already shows the full path (workspace › Environments ›
        // env-name) visually selected. A separate breadcrumb above the
        // header read as a second place naming the env without being
        // editable. Matches the workspace settings detail pattern,
        // which also runs without a breadcrumb.
        if (typeof envSidebarSelectedId !== 'undefined') {
            envSidebarSelectedId = env.id;
        }
        if (typeof renderEnvironmentEditor === 'function') {
            main.appendChild(renderEnvironmentEditor());
        }
        return main;
    }

    function _renderWorkspaceJumpDetail(ws, kind, icon, label, jumpLabel, body) {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });
        main.appendChild(_renderWorkspaceBreadcrumb(ws, [
            { label: label }
        ]));
        var section = el('div', { className: 'bowire-ws-detail-section' });
        section.appendChild(el('div', { className: 'bowire-ws-detail-section-label', textContent: label }));
        section.appendChild(el('p', { className: 'bowire-ws-detail-stat-hint', textContent: body }));
        section.appendChild(el('button', {
            className: 'bowire-ws-detail-action',
            style: 'margin-top:8px',
            textContent: jumpLabel,
            onClick: function () {
                if (ws.id !== activeWorkspaceId) switchWorkspace(ws.id);
                railMode = kind;
                try { localStorage.setItem('bowire_rail_mode', kind); } catch { /* ignore */ }
                render();
            }
        }));
        main.appendChild(section);
        return main;
    }

    // #152 follow-up — schema upload helper shared by the Sources-
    // detail drop-zone (drag-drop + click→file-picker). Mirrors the
    // Discover sidebar's renderProtoUploadPanel upload loop so
    // /api/proto/upload + /api/openapi/upload stay the single backend
    // seam for every schema source — the difference is which surface
    // the operator triggers it from.
    async function _uploadSourcesSchemaFiles(files) {
        var protoCount = 0, openapiCount = 0;
        for (var file of files) {
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
        var msg = [];
        if (protoCount > 0) msg.push(protoCount + ' .proto');
        if (openapiCount > 0) msg.push(openapiCount + ' OpenAPI');
        toast(msg.join(' + ') + ' imported', 'success');
        if (typeof fetchServices === 'function') fetchServices();
    }

    // #152 — Sources rail-mode main pane. Right-hand detail view
    // for the selected URL: status header + discovery summary +
    // headers + schema upload + delete action.
    function renderSourcesDetailMain() {
        var main = el('div', { id: 'bowire-main-sources', className: 'bowire-main bowire-main-workspaces' });

        if (!serverUrls || serverUrls.length === 0) {
            main.appendChild(el('p', {
                className: 'bowire-pane-empty bowire-main-pad',
                textContent: 'No URLs configured yet. Add one via the + button in the sidebar to start discovery.'
            }));
            return main;
        }
        if (!sourcesSelectedUrl || serverUrls.indexOf(sourcesSelectedUrl) < 0) {
            sourcesSelectedUrl = serverUrls[0];
        }
        var u = sourcesSelectedUrl;
        var status = (typeof connectionStatuses === 'object' && connectionStatuses)
            ? (connectionStatuses[u] || 'disconnected') : 'disconnected';
        var statusLabel = status === 'connected' ? 'Connected'
                        : status === 'disconnected' ? 'Disconnected'
                        : status === 'discovering' ? 'Discovering' : status;
        var svcList = (typeof services !== 'undefined')
            ? services.filter(function (s) {
                // Only count services that actually carry methods —
                // the JSON-RPC plugin probes every REST URL and emits
                // a methodless "service" when no JSON-RPC endpoint is
                // there, which would inflate the count past what the
                // operator sees in Discover. Same gate the Discover
                // rail-badge + tree use.
                return s.originUrl === u
                    && Array.isArray(s.methods)
                    && s.methods.length > 0;
            })
            : [];
        var protoCounts = {};
        for (var i = 0; i < svcList.length; i++) {
            var proto = svcList[i].source || 'unknown';
            protoCounts[proto] = (protoCounts[proto] || 0) + 1;
        }
        var methodN = svcList.reduce(function (n, s) { return n + ((s.methods && s.methods.length) || 0); }, 0);

        var meta = (typeof getUrlMeta === 'function') ? getUrlMeta(u) : {};
        var dotColor = meta.color || null;
        var header = el('div', { className: 'bowire-ws-detail-header' },
            el('span', {
                className: 'bowire-conn-pill-dot bowire-conn-pill-dot-' + status,
                style: 'width:20px;height:20px;border-radius:50%;flex-shrink:0;'
                    + (dotColor ? ('background:' + dotColor + ';') : '')
            }),
            el('input', {
                type: 'text',
                className: 'bowire-ws-detail-name',
                value: meta.name || _stripUrlPrefix(u),
                placeholder: 'Optional display name (defaults to host)',
                'aria-label': 'Display name',
                'data-bowire-no-vars-chip': '1',
                'data-bowire-no-vars-ac': '1',
                onChange: function (e) {
                    var v = String(e.target.value || '').trim();
                    setUrlMeta(u, { name: v || null });
                    render();
                }
            }),
            el('span', { className: 'bowire-ws-detail-badge', textContent: statusLabel })
        );
        main.appendChild(header);

        // Editable URL field below the header — the URL is what
        // discovery actually hits; the input above is the operator-
        // visible name.
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'URL' }),
            el('input', {
                type: 'text',
                className: 'bowire-url-header-value',
                value: u,
                readonly: !!config.lockServerUrl,
                onChange: function (e) {
                    var v = String(e.target.value || '').trim();
                    if (!v || v === u) return;
                    var idx = serverUrls.indexOf(u);
                    if (idx < 0) return;
                    // Re-key the meta + headers bags so the operator
                    // doesn't lose them on a URL edit.
                    var oldMeta = urlMeta[u];
                    var oldHeaders = urlHeaders[u];
                    serverUrls[idx] = v;
                    if (oldMeta) { delete urlMeta[u]; urlMeta[v] = oldMeta; persistUrlMeta(); }
                    if (oldHeaders) { delete urlHeaders[u]; urlHeaders[v] = oldHeaders; persistUrlHeaders(); }
                    sourcesSelectedUrl = v;
                    if (typeof persistServerUrls === 'function') persistServerUrls();
                    if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                    render();
                }
            })
        ));

        // Color picker — same palette as the Workspaces detail
        // pane so the operator picks from a curated set instead of
        // a color wheel.
        var palette = ['#6366f1', '#22c55e', '#f59e0b', '#ec4899', '#06b6d4', '#a855f7', '#ef4444', '#64748b'];
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Color' }),
            el('div', { className: 'bowire-ws-detail-color-row' },
                palette.map(function (c) {
                    return el('button', {
                        className: 'bowire-ws-detail-color-swatch' + ((meta.color === c) ? ' selected' : ''),
                        style: 'background:' + c,
                        title: c,
                        onClick: function () { setUrlMeta(u, { color: c }); render(); }
                    });
                }),
                meta.color ? el('button', {
                    className: 'bowire-ws-detail-color-swatch',
                    style: 'background:transparent; border:1px dashed var(--bowire-border); color:var(--bowire-text-tertiary); font-size:14px;',
                    title: 'Clear color',
                    textContent: '×',
                    onClick: function () { setUrlMeta(u, { color: null }); render(); }
                }) : null
            )
        ));

        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Discovery' }),
            el('div', { className: 'bowire-ws-detail-stats' },
                el('div', { className: 'bowire-ws-detail-stat' },
                    el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(svcList.length) }),
                    el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'Services' })
                ),
                el('div', { className: 'bowire-ws-detail-stat' },
                    el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(methodN) }),
                    el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'Methods' })
                ),
                el('div', { className: 'bowire-ws-detail-stat' },
                    el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(Object.keys(protoCounts).length || '—') }),
                    el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'Protocols' }),
                    el('div', { className: 'bowire-ws-detail-stat-hint', textContent: Object.keys(protoCounts).join(', ') || 'no protocols detected' })
                )
            )
        ));

        var actions = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Actions' }),
            el('div', { style: 'display:flex; gap:8px; flex-wrap:wrap;' },
                el('button', {
                    className: 'bowire-settings-action-btn',
                    textContent: 'Refresh discovery',
                    onClick: function () {
                        // refreshServices() / onServerUrlChanged() never
                        // existed (legacy hook names). fetchServices() is
                        // the real entry point — fans out across every
                        // configured URL, repopulates `services`, and
                        // renders. Triggered the same way the schema-
                        // watch loop drives re-discovery.
                        if (typeof fetchServices === 'function') fetchServices();
                    }
                }),
                el('button', {
                    className: 'bowire-settings-action-btn',
                    textContent: 'Open in Discover',
                    onClick: function () {
                        railMode = 'discover';
                        sidebarView = 'services';
                        try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                        render();
                    }
                })
            )
        );
        main.appendChild(actions);

        // #152 follow-up — schema-file drop zone per URL. Same upload
        // path the Discover sidebar uses (/api/proto/upload + /api/openapi/upload)
        // so the operator can manage schemas next to the URL they
        // belong to instead of context-switching to Discover.
        var schemaSection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Schema files' }));

        // List schemas associated with this URL (matched by OriginUrl).
        // Per-URL filter keeps the section relevant — global schemas
        // still surface in the Discover sidebar.
        var matchingSchemas = (typeof services === 'object' && Array.isArray(services))
            ? services.filter(function (s) {
                return (s.source === 'proto' || s.source === 'rest')
                    && s.originUrl === u;
            })
            : [];

        var schemaDrop = el('div', {
            className: 'bowire-sources-dropzone',
            onDragOver: function (e) { e.preventDefault(); this.classList.add('drag'); },
            onDragLeave: function () { this.classList.remove('drag'); },
            onDrop: async function (e) {
                e.preventDefault();
                this.classList.remove('drag');
                var files = e.dataTransfer && e.dataTransfer.files;
                if (!files || files.length === 0) return;
                await _uploadSourcesSchemaFiles(files);
            },
            onClick: function () {
                var input = document.createElement('input');
                input.type = 'file';
                input.accept = '.proto,.json,.yaml,.yml';
                input.multiple = true;
                input.onchange = async function () {
                    if (!input.files || input.files.length === 0) return;
                    await _uploadSourcesSchemaFiles(input.files);
                };
                input.click();
            }
        },
            el('div', { className: 'bowire-sources-dropzone-icon', innerHTML: svgIcon('upload') }),
            el('div', { className: 'bowire-sources-dropzone-title',
                textContent: matchingSchemas.length === 0
                    ? 'Upload schema files'
                    : 'Add more schema files' }),
            el('div', { className: 'bowire-sources-dropzone-hint',
                textContent: '.proto for gRPC  ·  .json / .yaml for OpenAPI / Swagger  ·  drop or click' })
        );
        schemaSection.appendChild(schemaDrop);

        if (matchingSchemas.length > 0) {
            var schemaList = el('div', { className: 'bowire-sources-schema-list' });
            matchingSchemas.forEach(function (svc) {
                schemaList.appendChild(el('div', { className: 'bowire-sources-schema-row' },
                    el('span', {
                        className: 'bowire-sources-schema-name',
                        textContent: svc.Name
                    }),
                    el('span', {
                        className: 'bowire-sources-schema-kind',
                        textContent: svc.source === 'proto' ? 'gRPC (.proto)' : 'OpenAPI'
                    })
                ));
            });
            schemaSection.appendChild(schemaList);
        }
        main.appendChild(schemaSection);
        // #152 v2 — per-URL headers editor. Applied to every
        // request invoked against this URL by the api.js request-
        // header builder.
        var headers = (typeof getUrlHeaders === 'function') ? getUrlHeaders(u) : {};
        var headerRows = el('div', { className: 'bowire-url-headers-list' });
        var headerKeys = Object.keys(headers).sort();
        headerKeys.forEach(function (k) {
            headerRows.appendChild(el('div', { className: 'bowire-url-header-row' },
                el('input', {
                    type: 'text',
                    className: 'bowire-url-header-key',
                    value: k,
                    placeholder: 'Header name',
                    onChange: function (e) {
                        var newKey = String(e.target.value || '').trim();
                        if (!newKey || newKey === k) return;
                        var v = headers[k];
                        deleteUrlHeader(u, k);
                        setUrlHeader(u, newKey, v);
                        render();
                    }
                }),
                el('input', {
                    type: 'text',
                    className: 'bowire-url-header-value',
                    value: headers[k],
                    placeholder: 'Value (supports {{vars}})',
                    onChange: function (e) {
                        setUrlHeader(u, k, String(e.target.value || ''));
                    }
                }),
                el('button', {
                    type: 'button',
                    className: 'bowire-list-row-delete',
                    title: 'Remove header',
                    innerHTML: svgIcon('trash'),
                    style: 'opacity:1;',
                    onClick: function () {
                        deleteUrlHeader(u, k);
                        render();
                    }
                })
            ));
        });
        if (headerKeys.length === 0) {
            headerRows.appendChild(el('p', {
                className: 'bowire-sources-hint',
                style: 'color:var(--bowire-text-tertiary); font-size:12px; margin:0;',
                textContent: 'No headers configured. Add one to send it with every request to this URL.'
            }));
        }
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Per-URL headers' }),
            headerRows,
            el('button', {
                className: 'bowire-settings-action-btn',
                style: 'align-self:flex-start; margin-top:4px;',
                textContent: '+ Add header',
                onClick: function () {
                    bowirePrompt('Header name', {
                        title: 'Add header',
                        placeholder: 'Authorization',
                        confirmText: 'Add',
                    }).then(function (name) {
                        if (!name) return;
                        if (headers[name] !== undefined) { toast('Header already exists', 'info'); return; }
                        setUrlHeader(u, name, '');
                        render();
                    });
                }
            })
        ));

        if (!config.lockServerUrl && serverUrls.length > 1) {
            main.appendChild(el('div', { className: 'bowire-ws-detail-section bowire-ws-detail-danger' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Danger zone' }),
                el('button', {
                    className: 'bowire-settings-action-btn bowire-ws-detail-delete-btn',
                    textContent: 'Remove this URL',
                    onClick: function () {
                        var snapshot = u;
                        bowireConfirm(
                            'Remove "' + snapshot + '" from the workspace? Discovered services for this URL are dropped from the in-memory cache.',
                            function () {
                                if (typeof removeServerUrl === 'function') removeServerUrl(snapshot);
                                else {
                                    var ri = serverUrls.indexOf(snapshot);
                                    if (ri >= 0) serverUrls.splice(ri, 1);
                                    if (typeof persistServerUrls === 'function') persistServerUrls();
                                }
                                if (sourcesSelectedUrl === snapshot) sourcesSelectedUrl = serverUrls[0] || null;
                                if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                                render();
                            },
                            { title: 'Remove URL', confirmText: 'Remove', danger: true }
                        );
                    }
                })
            ));
        }

        return main;
    }

    // Portal home — band title strip. Icon + label + small sub-label.
    function _renderHomeBandTitle(iconName, title, subtitle) {
        return el('div', { className: 'bowire-home-band-title' },
            el('span', { className: 'bowire-home-band-title-icon', innerHTML: svgIcon(iconName) }),
            el('span', { className: 'bowire-home-band-title-label', textContent: title }),
            subtitle ? el('span', { className: 'bowire-home-band-title-sub', textContent: subtitle }) : null
        );
    }

    // One Continue-band tile — wide, accent-bordered, single-click
    // resume target. icon + section-label up top, primary text in the
    // middle, optional meta at the bottom.
    function _renderContinueTile(iconName, label, primary, meta, onClick) {
        return el('button', {
            className: 'bowire-home-continue-tile',
            type: 'button',
            onClick: onClick
        },
            el('div', { className: 'bowire-home-continue-tile-head' },
                el('span', { className: 'bowire-home-continue-tile-icon', innerHTML: svgIcon(iconName) }),
                el('span', { className: 'bowire-home-continue-tile-label', textContent: label })
            ),
            el('div', { className: 'bowire-home-continue-tile-primary', textContent: primary, title: primary }),
            meta ? el('div', { className: 'bowire-home-continue-tile-meta', textContent: meta }) : null
        );
    }

    // Six canonical Start actions. firstRun=true labels the same set
    // slightly differently (the operator hasn't seen the workspace
    // yet) but the grid layout and hit-areas are identical.
    function _renderHomeStartGrid(firstRun) {
        var grid = el('div', { className: 'bowire-home-start-grid' });
        function card(iconName, label, sub, onClick) {
            grid.appendChild(el('button', {
                className: 'bowire-home-start-card',
                type: 'button',
                onClick: onClick
            },
                el('span', { className: 'bowire-home-start-card-icon', innerHTML: svgIcon(iconName) }),
                el('span', { className: 'bowire-home-start-card-label', textContent: label }),
                el('span', { className: 'bowire-home-start-card-sub', textContent: sub })
            ));
        }
        // Capability gates — skip cards whose action wouldn't work in
        // the current uiMode / configuration. Showing a button you
        // can't press is worse than not showing it: the operator
        // either thinks they hit a bug or trains themselves to ignore
        // the panel.
        var locked = !!(typeof config !== 'undefined' && config && config.lockServerUrl);
        var embedded = typeof uiMode !== 'undefined' && uiMode === 'embedded';
        var hasServices = typeof services !== 'undefined' && Array.isArray(services) && services.length > 0;

        // #252 — Two compose entry-points with different URL semantics.
        // 'Compose new request' (self-contained — URL lives inline on
        // the saved item, no central reference) is the canonical
        // freeform path. 'New from source…' binds the request to one
        // of the workspace's centrally-managed Source URLs so an env
        // rename / URL retirement propagates to the saved item. Both
        // open the same freeform builder; the difference is the URL
        // row's mode + the persisted shape.
        card('plus', 'Compose new request', 'Self-contained — URL inline', function () {
            if (typeof startFreeformRequest === 'function') startFreeformRequest({ urlMode: 'inline' });
            else {
                railMode = 'discover';
                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                render();
            }
        });
        // #289 — Hoppscotch-style single-line bar. Lower-friction
        // entry for the 'I just want to fire a request' case: drops
        // the operator straight into a method + URL + Execute bar
        // with the 7 sub-tabs (Parameter / Body / Header / Auth /
        // Pre-script / Post-script / Variables) underneath. No
        // workspace setup needed; binds to whatever env is active.
        // #293 — Routes to the Compose rail + spawns a fresh request-
        // builder tab. Pre-#293 this called startHoppRequest() which
        // injected the builder into Discover; now the builder has its
        // own rail with a per-tab strip, so the operator's discover
        // view stays untouched.
        if (typeof gotoComposeAndSpawn === 'function') {
            card('send', 'Just fire a request', 'Single-line bar (Ctrl+L)', function () {
                gotoComposeAndSpawn();
                requestAnimationFrame(function () {
                    var inp = document.querySelector('.bowire-request-builder-url-input');
                    if (inp) inp.focus();
                });
            });
        }
        var hasSourceUrls = (typeof serverUrls !== 'undefined'
            && Array.isArray(serverUrls) && serverUrls.length > 0);
        if (hasSourceUrls && !locked && !embedded) {
            card('connect', 'New from source…', 'URL bound to a managed Source', function () {
                if (typeof openNewFromSourceDialog === 'function') {
                    openNewFromSourceDialog();
                } else if (typeof startFreeformRequest === 'function') {
                    startFreeformRequest({ urlMode: 'source', sourceUrl: serverUrls[0] });
                }
            });
        }
        // 'Add URL' only makes sense when URL editing is allowed.
        // Locked mode pins the host-provided URL; embedded mode pins
        // the in-process service catalogue. Either way there's nothing
        // for the operator to add — hide the card so the panel
        // doesn't advertise an action that can't run.
        if (!locked && !embedded) {
            card('connect', firstRun ? 'Add a URL' : 'Add URL', 'Configure a source', function () {
                railMode = 'workspaces';
                try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                if (typeof workspacesSelectedId !== 'undefined') workspacesSelectedId = activeWorkspaceId;
                render();
            });
        }
        card('list', 'Import collection', 'Postman / OpenAPI', function () {
            // Collections rail retired (v2.1) — route to Compose;
            // the operator opens the side panel's Collections section
            // and uses 'Import Postman' on a fresh row, or runs the
            // import via the renderCollectionDetail toolbar from the
            // Workspaces-rail collection-detail leaf.
            railMode = 'compose';
            try { localStorage.setItem('bowire_rail_mode', 'compose'); } catch { /* ignore */ }
            if (typeof window !== 'undefined' && typeof window.focusComposeOnCollection === 'function') {
                window.focusComposeOnCollection(null);
            }
            render();
        });
        // 'Record session' captures live traffic against discovered
        // services. With nothing discovered there's no target, so
        // recording would silently do nothing — hide the card until
        // discovery yields at least one service.
        if (hasServices) {
            card('record', 'Record session', 'Capture live calls', function () {
                if (typeof startRecording === 'function') {
                    startRecording();
                    railMode = 'discover';
                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                    render();
                }
            });
        }
        card('server', 'Build a mock', 'Replay recordings', function () {
            // v2.2 — Mocks rail folded into Intercept -> Mock servers sub-tab.
            railMode = 'intercept';
            try { localStorage.setItem('bowire_rail_mode', 'intercept'); } catch { /* ignore */ }
            try { localStorage.setItem('bowire_intercept_sub_tab', 'mock-servers'); } catch { /* ignore */ }
            if (typeof sidebarView !== 'undefined') sidebarView = 'intercept';
            try { localStorage.setItem('bowire_sidebar_view', 'intercept'); } catch { /* ignore */ }
            if (typeof interceptSubView !== 'undefined') interceptSubView = 'mock-servers';
            render();
        });
        card('compass', 'Browse Discover', 'Explore services', function () {
            railMode = 'discover';
            try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
            sidebarView = 'services';
            render();
        });
        return grid;
    }

    // #139 — Home tile (favorite or recent entry). Resolves the
    // service + method from the live discovery list so the tile
    // shows method type + protocol icon when available, and
    // gracefully degrades to plain text when the method isn't in
    // the current discovery (stale favorite).
    function renderHomeTile(serviceName, methodName, isFavorite, metaText) {
        var svc = services.find(function (s) { return s.name === serviceName; });
        var meth = svc && (svc.methods || []).find(function (m) { return m.name === methodName; });
        var available = !!(svc && meth);
        var proto = svc ? svc.source : null;
        var dir = meth ? (typeof methodDirection === 'function' ? methodDirection(meth) : 'neutral') : 'neutral';

        var tile = el('div', {
            className: 'bowire-home-tile' + (available ? '' : ' bowire-home-tile-stale'),
            'data-protocol': proto || 'default',
            'data-direction': dir,
            title: available
                ? 'Open in Discover · right-click for actions'
                : 'Method no longer in discovery — connect to its server to use it',
            onClick: function () {
                if (!available) return;
                railMode = 'discover';
                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                sidebarView = 'services';
                openTab(svc, meth);
            },
            onContextMenu: function (e) {
                e.preventDefault();
                e.stopPropagation();
                if (typeof showContextMenu !== 'function') return;
                var items = [];
                if (available) {
                    items.push({
                        label: 'Open in Discover',
                        onClick: function () {
                            railMode = 'discover';
                            try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                            sidebarView = 'services';
                            openTab(svc, meth);
                        }
                    });
                    items.push({ separator: true });
                }
                if (typeof isFavorite === 'function' && typeof toggleFavorite === 'function') {
                    var fav = false;
                    try { fav = isFavorite(serviceName, methodName); } catch { /* ignore */ }
                    items.push({
                        label: fav ? 'Remove from favorites' : 'Add to favorites',
                        onClick: function () {
                            toggleFavorite(serviceName, methodName);
                            render();
                        }
                    });
                }
                if (available && typeof addTargetToEnvelopePicker === 'function') {
                    items.push({ separator: true });
                    items.push({
                        label: 'Add to envelope…',
                        onClick: function (ev) {
                            // Default-JSON body from the method's input
                            // type so the seeded envelope is runnable
                            // without extra editing.
                            var defaultBody = '{}';
                            if (meth && typeof generateDefaultJson === 'function') {
                                try { defaultBody = generateDefaultJson(meth.inputType, 0); }
                                catch { /* keep '{}' */ }
                            }
                            addTargetToEnvelopePicker(ev.clientX, ev.clientY, {
                                type: 'method',
                                service: serviceName,
                                method: methodName,
                                protocol: proto || null,
                                body: defaultBody, metadata: {}, serverUrl: null
                            }, { name: serviceName + '.' + methodName });
                        }
                    });
                }
                showContextMenu(e.clientX, e.clientY, items);
            }
        });

        // Header row: method name + verb badge on the same line.
        // Previously the badge sat on its own line above the method
        // name, which used 3 rows for what could be 2 and made the
        // tile feel oversized for the information it carries.
        var headerRow = el('div', { className: 'bowire-home-tile-header' },
            el('span', {
                className: 'bowire-home-tile-method',
                title: methodName,
                textContent: methodName
            })
        );
        if (meth) {
            headerRow.appendChild(el('span', {
                className: 'bowire-home-tile-badge bowire-method-badge',
                dataset: { type: methodBadgeType(meth), direction: dir },
                textContent: methodBadgeText(meth)
            }));
        }
        // Star toggle — shown when the tile is rendered from the
        // Favorites section (isFavorite=true). Click toggles the
        // entry off; the surrounding tile click stays bound to
        // openTab via stopPropagation here.
        if (isFavorite && typeof toggleFavorite === 'function') {
            headerRow.appendChild(el('button', {
                type: 'button',
                className: 'bowire-home-tile-star',
                title: 'Remove from favorites',
                'aria-label': 'Remove ' + serviceName + '.' + methodName + ' from favorites',
                innerHTML: svgIcon('starFilled'),
                onClick: function (ev) {
                    ev.stopPropagation();
                    toggleFavorite(serviceName, methodName);
                    render();
                }
            }));
        }
        tile.appendChild(headerRow);
        var subRow = el('div', { className: 'bowire-home-tile-sub' });
        subRow.appendChild(el('span', {
            className: 'bowire-home-tile-service',
            title: serviceName,
            textContent: serviceName
        }));
        if (metaText) {
            subRow.appendChild(el('span', {
                className: 'bowire-home-tile-meta',
                textContent: metaText
            }));
        }
        tile.appendChild(subRow);

        return tile;
    }

    // #160 — Thin breadcrumb strip rendered at the top of every main
    // pane: `[color-dot] WorkspaceName / RailLabel`. Click on the
    // chip opens the workspace switcher (same target as the topbar
    // chip — single source of truth). Hides when only one workspace
    // exists (no value adding chrome the operator can't act on),
    // hides in embedded mode (the host owns workspace context).
    // renderWorkspaceBreadcrumb (top-of-main page-header version)
    // retired in favour of the per-sub-view _renderWorkspaceBreadcrumb
    // below. The topbar workspace chip plus the sub-view breadcrumb
    // together cover the "which workspace + which section + which
    // item am I on" question without doubling the chrome.

    function renderMain() {
        // #314 — give rail-owned renderers first crack at the main
        // pane. When a rail descriptor sets mainPaneRendererKey and
        // the rail's JS fragment has registered the function on
        // window.__bowireRailRenderers, it takes precedence over the
        // hardcoded `if (railMode === '…')` arms below. Falls through
        // silently when no key is set or the function hasn't loaded —
        // same incremental cut-over shape as the sidebar dispatch.
        var ownedMain = (typeof _currentRailRenderer === 'function')
            ? _currentRailRenderer('main') : null;
        if (ownedMain) {
            try {
                var ownedRoot = ownedMain();
                if (ownedRoot) return ownedRoot;
            } catch (e) {
                // Defensive: don't take down the workbench because a
                // rail's main renderer threw — fall through to the
                // core arms so the user still sees something.
                try { console.error('Rail main renderer threw', e); } catch { /* ignore */ }
            }
        }

        // Honest empty state when there's no active workspace (first
        // run, or last workspace just deleted). Every other rail
        // would either render against stale in-memory state or against
        // the orphan namespace — both confuse the operator into
        // thinking the workspace they just deleted is half-alive. Force
        // the Home-rail CTA so the only path forward is "create one".
        // Force-home rule retired — see top of render() in
        // render-env-auth.js for rationale. Pinning every non-home
        // click back to Home trapped the operator: rails read as
        // clickable (hover highlight + transition) but the click
        // was undone before the next paint. Each rail's own empty
        // state handles the no-workspace case.

        // #139 — Home rail mode. Default landing for first-time
        // users; cross-workflow launchpad. Phase 1 shows recent
        // activity + favorites as two grids; click opens the method
        // in Discover. Launch-picker ('what do you want to do with
        // this favorite?') lands in Phase 2 once more modes are
        // wired.
        if (railMode === 'home') {
            var homeMain = el('div', { id: 'bowire-main-home', className: 'bowire-main bowire-main-home' });
            var homeWrap = el('div', { className: 'bowire-home-wrap' });

            // No-workspace state. v2.0 default ships the workspace list
            // empty (Settings → General → "Auto-create initial workspace"
            // brings the old seed-on-first-run behaviour back). Home
            // shows a single guided CTA explaining what a workspace is
            // and letting the operator name + colour their first one.
            // Once they create one, the regular home bands take over on
            // the next render.
            if (typeof workspaces !== 'undefined' && Array.isArray(workspaces) && workspaces.length === 0) {
                // #301 followup — no-workspace welcome uses the canonical
                // rail-empty shape (bowire-main-pad > renderEmptyCard as
                // sole child) so the :has() centring rule kicks in. The
                // previous bowire-home-wrap + bowire-home-band wrapping
                // left the card pinned top-left with the home-wrap's own
                // padding, which read inconsistent next to Mocks /
                // Recordings / Flows / Compose (all centred). Operator
                // feedback: every rail's welcome should sit in the same
                // spot regardless of which rail is active.
                var noWsPad = el('div', { className: 'bowire-main-pad' });
                noWsPad.appendChild(renderEmptyCard({
                    // Match the rail-strip glyph: this empty card is
                    // the Home rail's no-workspace state, so it should
                    // show the rail's house icon — NOT the Workspaces
                    // rail's layers icon. Operator feedback: 'im home
                    // welcome sehe ich das workspace icon, ich müsste
                    // aber doch das haus des rails sehen.'
                    icon: 'house',
                    headline: 'Create your first workspace',
                    body: 'A workspace is your project folder — it holds the URLs you discover, the environments + variables + secrets you reference, and the collections / recordings / benchmarks you build. Most operators name them after the project ("Petstore Staging", "Internal CMS"). You can switch + add more from the workspace chip in the topbar later.',
                    actions: [
                        {
                            // #281 — stable id used as the
                            // 'create-first-workspace' tour step
                            // target. Don't rename without also
                            // updating tour.js's getting-started
                            // steps.
                            id: 'bowire-welcome-create-btn',
                            label: 'New workspace…',
                            primary: true,
                            onClick: function () {
                                if (typeof openCreateWorkspaceDialog === 'function') {
                                    openCreateWorkspaceDialog(function (ws) {
                                        if (ws) activeWorkspaceId = ws.id;
                                        render();
                                        // #281 — wake the tour engine's
                                        // 'workspace-created' advance
                                        // signal so the Getting-started
                                        // tour steps past step 1 without
                                        // the operator having to click
                                        // Next.
                                        if (typeof window !== 'undefined'
                                            && typeof window.bowireFireTourEvent === 'function') {
                                            window.bowireFireTourEvent('workspace-created');
                                        }
                                    });
                                }
                            }
                        },
                        // Secondary — jump into the (empty) Workspaces
                        // overview where the operator can do heavier
                        // management later. The empty overview itself
                        // surfaces a New-workspace CTA, so this entry
                        // funnels through the list rather than dead-
                        // ends back here.
                        {
                            label: 'Manage workspaces',
                            onClick: function () {
                                railMode = 'workspaces';
                                try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                                workspaceTreeSelection = { kind: 'workspaces-overview' };
                                render();
                            }
                        },
                        // #281 — Take-a-tour CTA on the welcome card.
                        // Same engine the sidebar 'Take Tour' footer
                        // launches; force-mode so the operator can
                        // re-run it after dismissing once.
                        {
                            id: 'bowire-welcome-tour-btn',
                            label: 'Take a tour',
                            onClick: function () {
                                if (typeof window !== 'undefined'
                                    && typeof window.bowireStartGettingStartedTour === 'function') {
                                    window.bowireStartGettingStartedTour({ force: true });
                                }
                            }
                        }
                    ]
                }));
                // Skip homeWrap (bowire-home-wrap) entirely so the
                // bowire-main-pad sits directly under bowire-main-home —
                // its :has(> .bowire-empty-card:only-child) rule then
                // centres the welcome card both axes, identical to
                // Recordings / Mocks / Flows / Compose / Collections.
                homeMain.appendChild(noWsPad);
                return homeMain;
            }

            // Portal home (3 bands, left-aligned, consistent gutter):
            //   1. Continue — last method / collection / recording (3 tiles)
            //   2. Start    — action cards for the canonical entry points
            //   3. Favorites + Recent activity (the muscle-memory layer)
            //
            // Same horizontal padding + max-width across every band so
            // the page reads as one column from top to bottom instead of
            // a centered hero floating above a wider grid (which was
            // the prior layout's restless feel — user feedback).
            //
            // first-run state replaces the Continue band with a short
            // tagline + two onboarding CTAs so the layout stays
            // visually aligned but the content matches the new operator.
            var isFirstRun = typeof window.bowireDetectLandingState === 'function'
                && window.bowireDetectLandingState() === 'first-run';

            var recent = (typeof getRecentMethods === 'function') ? getRecentMethods() : [];
            var collections = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) ? collectionsList : [];
            var recordings = (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) ? recordingsList : [];

            // ---- Band 1: Continue (or first-run onboarding) ----
            if (isFirstRun) {
                var firstRunBand = el('div', { className: 'bowire-home-band bowire-home-band-firstrun' });
                // Same Start title as the returning-operator branch
                // below so every Home band carries a consistent header
                // — without it the cards row floats labelless above
                // Favorites / Recent, which the operator reads as a
                // missing section title.
                firstRunBand.appendChild(_renderHomeBandTitle('rocket', 'Start'));
                firstRunBand.appendChild(_renderHomeStartGrid(true));
                homeWrap.appendChild(firstRunBand);
            } else {
                var hasContinueItems = recent.length > 0 || collections.length > 0 || recordings.length > 0;
                if (hasContinueItems) {
                    var continueBand = el('div', { className: 'bowire-home-band' });
                    continueBand.appendChild(_renderHomeBandTitle('replay', 'Continue'));
                    var continueGrid = el('div', { className: 'bowire-home-continue-grid' });
                    if (recent.length > 0) {
                        continueGrid.appendChild(_renderContinueTile(
                            'compass', 'Last method', recent[0].service + ' / ' + recent[0].method,
                            'Open in Discover',
                            function () {
                                var r = recent[0];
                                var svc = (services || []).find(function (s) { return s.name === r.service; });
                                var mth = svc && (svc.methods || []).find(function (m) { return m.name === r.method; });
                                if (svc && mth && typeof openTab === 'function') {
                                    railMode = 'discover';
                                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                    openTab(svc, mth);
                                } else {
                                    railMode = 'discover';
                                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                    render();
                                }
                            }));
                    }
                    if (collections.length > 0) {
                        var col = collections[0];
                        continueGrid.appendChild(_renderContinueTile(
                            'list', 'Last collection', col.name,
                            (col.items && col.items.length) + ' item' + (col.items && col.items.length === 1 ? '' : 's'),
                            function () {
                                // Collections rail retired (v2.1) — open Compose
                                // with the Collections side panel focused on
                                // this collection (#295).
                                if (typeof collectionManagerSelectedId !== 'undefined') collectionManagerSelectedId = col.id;
                                railMode = 'compose';
                                try { localStorage.setItem('bowire_rail_mode', 'compose'); } catch { /* ignore */ }
                                if (typeof window !== 'undefined' && typeof window.focusComposeOnCollection === 'function') {
                                    window.focusComposeOnCollection(col.id);
                                }
                                render();
                            }));
                    }
                    if (recordings.length > 0) {
                        var rec = recordings[0];
                        continueGrid.appendChild(_renderContinueTile(
                            'recording', 'Last recording', rec.name || ('Recording ' + rec.id),
                            (rec.steps && rec.steps.length) + ' step' + (rec.steps && rec.steps.length === 1 ? '' : 's'),
                            function () {
                                if (typeof recordingManagerSelectedId !== 'undefined') recordingManagerSelectedId = rec.id;
                                railMode = 'recordings';
                                try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                                render();
                            }));
                    }
                    continueBand.appendChild(continueGrid);
                    homeWrap.appendChild(continueBand);
                }

                // ---- Band 2: Start ----
                var startBand = el('div', { className: 'bowire-home-band' });
                startBand.appendChild(_renderHomeBandTitle('rocket', 'Start'));
                startBand.appendChild(_renderHomeStartGrid(false));
                homeWrap.appendChild(startBand);
            }

            // ---- Band 3: Favorites + Recent activity ----
            var sectionsBand = el('div', { className: 'bowire-home-band' });
            var sections = el('div', { className: 'bowire-home-sections' });

            var favs = (typeof getFavorites === 'function') ? getFavorites() : [];
            var favSection = el('div', { className: 'bowire-home-section bowire-home-section-favs' });
            var favCappedCount = Math.min(favs.length, 9);
            var favCountText = favs.length > favCappedCount
                ? favCappedCount + ' of ' + favs.length
                : favs.length + (favs.length === 1 ? ' entry' : ' entries');
            // Title click opens the right-side overlay drawer with the
            // full list. The grid in Home stays capped at 9 so Home
            // itself never needs to scroll; the drawer is where the
            // operator browses the long tail.
            favSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-home-section-title bowire-home-section-title-toggle',
                title: 'Show full favorites list',
                onClick: function () {
                    homeListDrawer = 'favorites';
                    render();
                }
            },
                el('span', { innerHTML: svgIcon('starFilled'), style: 'color:var(--bowire-accent)' }),
                el('span', { textContent: 'Favorites' }),
                el('span', { className: 'bowire-home-section-count', textContent: favCountText }),
                el('span', {
                    className: 'bowire-home-section-toggle-caret',
                    innerHTML: svgIcon('chevron')
                })
            ));
            if (favs.length === 0) {
                favSection.appendChild(renderEmptyCard({
                    icon: 'star',
                    headline: 'No favorites yet',
                    body: 'Star a method from the sidebar or any recent entry below — it lands here for one-click access across every workflow.'
                }));
            } else {
                var favGrid = el('div', { className: 'bowire-home-grid' });
                favs.slice(0, 9).forEach(function (fav) {
                    favGrid.appendChild(renderHomeTile(fav.service, fav.method, true));
                });
                favSection.appendChild(favGrid);
            }
            sections.appendChild(favSection);

            var recentSection = el('div', { className: 'bowire-home-section bowire-home-section-recent' });
            var recentCappedCount = Math.min(recent.length, 9);
            var recentCountText = recent.length > recentCappedCount
                ? recentCappedCount + ' of ' + recent.length
                : recent.length + (recent.length === 1 ? ' entry' : ' entries');
            // Title click opens the right-side overlay drawer with
            // the full activity history.
            recentSection.appendChild(el('button', {
                type: 'button',
                className: 'bowire-home-section-title bowire-home-section-title-toggle',
                title: 'Show full activity list',
                onClick: function () {
                    homeListDrawer = 'recent';
                    render();
                }
            },
                el('span', { innerHTML: svgIcon('history') }),
                el('span', { textContent: 'Recent activity' }),
                el('span', { className: 'bowire-home-section-count', textContent: recentCountText }),
                el('span', {
                    className: 'bowire-home-section-toggle-caret',
                    innerHTML: svgIcon('chevron')
                })
            ));
            if (recent.length === 0) {
                recentSection.appendChild(renderEmptyCard({
                    icon: 'console',
                    headline: 'No recent activity',
                    body: 'Open methods in Discover and they land here in MRU order.'
                }));
            } else {
                var recentGrid = el('div', { className: 'bowire-home-grid' });
                // Cap the visible recents at 9 — the Home grid is a
                // 3-column layout, so 9 lands as a clean 3x3 block
                // without a stranded trailing row. Full list lives
                // in the overlay drawer (click the section title).
                recent.slice(0, 9).forEach(function (r) {
                    var when = (typeof getRelativeTime === 'function' && r.ts)
                        ? getRelativeTime(r.ts) : '';
                    recentGrid.appendChild(renderHomeTile(r.service, r.method, false, when));
                });
                recentSection.appendChild(recentGrid);
            }
            sections.appendChild(recentSection);
            sectionsBand.appendChild(sections);
            homeWrap.appendChild(sectionsBand);

            // Right-side drawer — opened by clicking either section
            // title. Uses the shared renderDrawer chrome (same shape
            // as the Assistant / Help / Tests drawers) so the Home
            // overlay reads as part of the existing workbench drawer
            // family instead of a one-off.
            if (homeListDrawer && typeof renderDrawer === 'function') {
                var drawerKind = homeListDrawer;
                var drawerTitle = drawerKind === 'favorites' ? 'Favorites' : 'Recent activity';
                var drawerItems = drawerKind === 'favorites' ? favs : recent;
                var closeDrawer = function () { homeListDrawer = null; render(); };
                homeMain.appendChild(renderDrawer({
                    id: 'bowire-home-list-drawer',
                    className: 'bowire-home-list-drawer',
                    title: drawerTitle,
                    titleAccessory: el('span', {
                        className: 'bowire-drawer-title-count',
                        textContent: String(drawerItems.length)
                    }),
                    onClose: closeDrawer,
                    content: function () {
                        var list = el('div', { className: 'bowire-home-list-drawer-list' });
                        drawerItems.forEach(function (item) {
                            var when = (drawerKind === 'recent'
                                    && typeof getRelativeTime === 'function' && item.ts)
                                ? getRelativeTime(item.ts) : '';
                            list.appendChild(renderHomeTile(item.service, item.method,
                                drawerKind === 'favorites', when));
                        });
                        return list;
                    }
                }));
            }

            // Tour + Docs footer at the very bottom of Home. Used to
            // sit under every Discover empty state — once per session
            // is enough, and Home is the canonical welcome surface.
            if (typeof window.bowireRenderLandingHelpFooter === 'function') {
                var helpFooterSlot = el('div', { className: 'bowire-home-footer-slot' });
                window.bowireRenderLandingHelpFooter(helpFooterSlot);
                homeWrap.appendChild(helpFooterSlot);
            }

            homeMain.appendChild(homeWrap);
            return homeMain;
        }

        // #293 — Compose rail. Home for the ad-hoc request-builder
        // (previously hosted inside Discover's per-method tabs, which
        // overwrote the schema-driven view). The renderer lives in
        // compose-rail.js — owns its own tab strip + pinned '+ New
        // Request' tab + per-tab builder instance.
        if (railMode === 'compose' && typeof renderComposeMain === 'function') {
            return renderComposeMain();
        }

        // #131 Phase 1 — Benchmarks ships the single-method shape;
        // collection / recording / random / scheduled probes land in
        // later phases. Renderer lives in benchmarks.js.
        if (railMode === 'benchmarks') {
            return renderBenchmarksDetailMain();
        }

        // #132 — Parallel sessions are launched directly from the
        // Recording / Collection detail toolbars; the result lands
        // inline under the source. No standalone rail mode any more.

        // Collections rail retired (v2.1) — the standalone main-pane
        // dispatch is gone; the Compose rail (#295) is the canonical
        // surface for Collections + Presets. The Workspaces rail's
        // collection-detail leaf still renders renderCollectionDetail
        // (defined in collections.js, in core).

        // v2.2 — the standalone Mocks rail descriptor is gone. The
        // Mock-servers content surface now lives inside the Intercept
        // rail's "Mock servers" sub-tab; the renderer lives in
        // Bowire.Mock's mocks.js fragment as window.__bowireMocks.
        // renderRailMain. The boot migration rewrites a stored
        // railMode='mocks' to 'intercept' on first paint, so this
        // dispatcher no longer needs to dispatch on 'mocks'.

        // #133 Phase 2 — Recordings rail mode owns the main pane.
        // Sidebar shows the recordings list; main pane shows the
        // selected recording's detail (steps + actions). When no
        // recording is selected (fresh entry, or after a delete),
        // an empty card guides the operator toward the next step.
        if (railMode === 'recordings') {
            var recMain = el('div', { id: 'bowire-main-recordings', className: 'bowire-main bowire-main-recordings' });
            // Pattern B: workspace prereq comes before the
            // selected-recording lookup; without a workspace there's
            // no scoped recording list to pick from anyway.
            if (!activeWorkspaceId && typeof renderWorkspacePrereqEmpty === 'function') {
                recMain.appendChild(renderWorkspacePrereqEmpty({
                    icon: 'recording',
                    railLabel: 'Recordings',
                    railBody: 'Recordings capture a sequence of live calls you can replay, build mocks from, or run as benchmarks.'
                }));
                return recMain;
            }
            var selectedRec = recordingsList.find(function (r) { return r.id === recordingManagerSelectedId; });
            if (selectedRec && typeof renderRecordingDetail === 'function') {
                // Chunked recording storage (#144 Phase 1) — hydrate
                // before the toolbar reads rec.steps.length so the
                // "Use as mock" / "Convert to Tests" actions enable
                // correctly even when the rail-mode entry path was a
                // jump rather than a sidebar click. Gate ONLY on
                // `steps === undefined` — empty array means hydration
                // already ran and re-triggering would render-loop.
                if (typeof hydrateRecording === 'function' && selectedRec.steps === undefined) {
                    hydrateRecording(selectedRec).then(function () { render(); });
                }
                recMain.appendChild(renderRecordingDetail(selectedRec));
            } else {
                var emptyWrap = el('div', { className: 'bowire-main-pad' });
                var noRecs = recordingsList.length === 0;
                emptyWrap.appendChild(renderEmptyCard({
                    icon: 'recording',
                    headline: noRecs ? 'No recordings yet' : 'Pick a recording',
                    body: noRecs
                        ? 'Recordings capture a sequence of live calls so you can replay them, build mocks, or run them as benchmarks. Start one, then invoke methods from Discover.'
                        : 'Pick a recording from the sidebar list to see its steps and actions.',
                    actions: noRecs ? [
                        {
                            label: 'Start recording',
                            primary: true,
                            onClick: function () {
                                startRecording();
                                railMode = 'discover';
                                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                render();
                            }
                        },
                        {
                            label: 'Browse Discover',
                            onClick: function () {
                                railMode = 'discover';
                                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                render();
                            }
                        },
                        // #39 — bootstrap a recording straight from a HAR
                        // (Chrome DevTools / Playwright / Charles) without
                        // capturing anything first.
                        {
                            label: 'Import HAR',
                            onClick: function () {
                                if (typeof importHarFromFile === 'function') importHarFromFile();
                            }
                        },
                        // Per-rail welcome tour: explains the
                        // capture loop (arm → invoke → stop → save).
                        // Force-mode so the operator can re-trigger
                        // from the same empty card after dismissal.
                        {
                            id: 'bowire-recordings-empty-tour-btn',
                            label: 'Take a tour',
                            onClick: function () {
                                if (typeof window !== 'undefined'
                                    && typeof window.bowireStartCaptureRecordingTour === 'function') {
                                    window.bowireStartCaptureRecordingTour({ force: true });
                                }
                            }
                        }
                    ] : []
                }));
                recMain.appendChild(emptyWrap);
            }
            return recMain;
        }

        // #133 Phase 2 — Security rail mode owns the main pane.
        // When the operator picks the 🛡️ Security icon on the rail,
        // the main pane swaps to the Security surface (threat-model,
        // tier toggle, ranked endpoints, template generator) that
        // used to live in the right-side drawer. Sidebar still shows
        // the services tree so the operator can drill into a method
        // from the same screen without losing the security view.
        if (railMode === 'workspaces') {
            return renderWorkspaceDetailMain();
        }

        if (railMode === 'sources') {
            return renderSourcesDetailMain();
        }

        // #324 — Help rail owns the main pane. Renders the topic body
        // full-width (no drawer chrome competing for horizontal real
        // estate) with the standard rail-pad gutter so the prose
        // aligns with every other rail's left/right inset.
        if (railMode === 'help') {
            return renderHelpMain();
        }

        if (railMode === 'security') {
            var secMain = el('div', { id: 'bowire-main-security', className: 'bowire-main bowire-main-security' });
            if (window.__bowireAi && typeof window.__bowireAi.renderSecurityPanel === 'function') {
                // Wrap in the shared main-pad gutter so the security
                // surface aligns with every other rail's left/right
                // inset instead of sitting at the pane edge.
                var secWrap = el('div', { className: 'bowire-main-pad' });
                secWrap.appendChild(window.__bowireAi.renderSecurityPanel());
                secMain.appendChild(secWrap);
            } else {
                secMain.appendChild(el('p', {
                    className: 'bowire-pane-empty bowire-main-pad',
                    textContent: 'Security tools need Kuestenlogik.Bowire.Ai installed in the workbench process. Install the package + restart, or switch back to Discover via the rail.'
                }));
            }
            return secMain;
        }

        // #92 — Sources management view. When the operator clicks
        // the topbar connection pill (or otherwise sets sidebarView
        // to 'sources'), the main pane swaps to the full-width URL
        // + schema editor. Reuses the existing renderUrlBarRow
        // helper at unlocked / editable width; lockServerUrl still
        // forces readonly.
        if (sidebarView === 'sources') {
            var srcMain = el('div', { id: 'bowire-main-sources', className: 'bowire-main bowire-main-sources' });
            var srcWrap = el('div', { className: 'bowire-sources-wrap' });
            srcWrap.appendChild(el('h2', { className: 'bowire-sources-title', textContent: 'Sources' }));
            srcWrap.appendChild(el('p', {
                className: 'bowire-sources-subtitle',
                textContent: config.lockServerUrl
                    ? 'URLs configured by the host — read-only.'
                    : 'Discovery URLs and schema files. Drop a schema below to import; type a URL to add it.'
            }));
            srcWrap.appendChild(el('h3', { className: 'bowire-sources-section', textContent: 'Discovery URLs' }));
            // Reuse the sidebar's URL bar at full main-pane width.
            // The renderUrlBarRow already understands the locked vs.
            // editable cases.
            srcWrap.appendChild(renderUrlBarRow(!!config.lockServerUrl));
            srcMain.appendChild(srcWrap);
            return srcMain;
        }

        // ID encodes the current view mode so morphdom fully replaces
        // the main pane when switching between environments editor,
        // freeform builder, landing page, and the normal request/response
        // layout — preventing stale DOM and wrong closures.
        var mainViewKey = sidebarView === 'environments'
            ? 'env'
            : sidebarView === 'flows'
                ? 'flows-' + (flowEditorSelectedId || 'none')
                : sidebarView === 'proxy'
                    ? 'proxy-' + (proxyFlowSelectedId || 'none')
                    : sidebarView === 'intercepted'
                        ? 'intercepted-' + (interceptedFlowSelectedId || 'none')
                        : sidebarView === 'intercept'
                            ? 'intercept-' + (typeof interceptSubView !== 'undefined' ? interceptSubView : 'captured')
                                + '-' + (typeof interceptedFlowSelectedId !== 'undefined' ? (interceptedFlowSelectedId || 'none') : 'none')
                            : freeformRequest
                                ? 'freeform'
                                : selectedMethod
                                    ? (selectedService ? selectedService.name : '') + '-' + selectedMethod.name
                                    : 'landing';
        const main = el('div', { id: 'bowire-main-' + mainViewKey, className: 'bowire-main' });

        // When the sidebar is in Environments view, the main pane
        // shows the full-width variable editor instead of the
        // request/response layout. This gives enough room for
        // multi-line values (JSON, certs, tokens).
        if (sidebarView === 'environments') {
            main.appendChild(renderEnvironmentEditor());
            return main;
        }

        // Flow canvas — visual node editor
        if (sidebarView === 'flows') {
            main.appendChild(renderFlowCanvas());
            return main;
        }

        // Proxy view — captured-flow detail pane
        if (sidebarView === 'proxy') {
            main.appendChild(renderProxyMainPane());
            return main;
        }

        // #153 — Intercepted view — same detail-pane idiom, but the
        // source is the in-process interceptor middleware rather than
        // the standalone CLI proxy. Renderer lives in intercepted-view.js.
        if (sidebarView === 'intercepted') {
            main.appendChild(renderInterceptedMainPane());
            return main;
        }

        // v2.2 — Unified Intercept view. Four sub-tabs (Captured / Live
        // overrides / Mock servers / Settings) — sub-tab state is owned
        // by the Interceptor package's intercept-view.js fragment.
        if (sidebarView === 'intercept') {
            if (typeof renderInterceptMainPane === 'function') {
                main.appendChild(renderInterceptMainPane());
            }
            return main;
        }

        // Header bar — the legacy Toggle-sidebar button used to live
        // here; retired in favour of the splitter chevron + Cmd/Ctrl+B
        // (#289). Two affordances for the same thing was clutter.
        const header = el('div', { className: 'bowire-header' });

        if (selectedMethod) {
            // Breadcrumb: Protocol > Service. The method name is rendered
            // separately as headerName below (at a much larger size), so
            // leaving it out of the breadcrumb avoids the visual stutter
            // that used to put two `listen`s next to each other.
            var breadcrumb = el('div', { className: 'bowire-breadcrumb' });
            if (selectedService && selectedService.source) {
                var proto = protocols.find(function (p) { return p.id === selectedService.source; });
                if (proto) {
                    breadcrumb.appendChild(el('span', {
                        className: 'bowire-breadcrumb-item clickable',
                        innerHTML: proto.icon ? '<span class="bowire-breadcrumb-icon">' + proto.icon + '</span>' : '',
                        onClick: function () {
                            // Close the active tab — goes back to landing
                            // if no tabs remain, or switches to an adjacent tab.
                            if (activeTabId) {
                                closeTab(activeTabId);
                            } else {
                                selectedMethod = null;
                                selectedService = null;
                                render();
                            }
                        }
                    }));
                    // BUG fix: this used to use 'bowire-breadcrumb-icon'
                    // (the 12×12 icon slot) for a multi-character text label,
                    // which let the protocol name overflow its container and
                    // collide with whatever sat to the right of the breadcrumb.
                    breadcrumb.appendChild(el('span', { className: 'bowire-breadcrumb-item', textContent: proto.name }));
                    breadcrumb.appendChild(el('span', { className: 'bowire-breadcrumb-sep', textContent: '\u203A' }));
                }
            }
            if (selectedService) {
                // Service is the breadcrumb's terminal segment now \u2014 the
                // method name is rendered separately as headerName below
                // (and at a much larger size), so duplicating it inside
                // the breadcrumb just produced a `\u2026 \u203A listen   listen`
                // visual stutter at narrow widths.
                // Trim the namespace prefix only when it looks like a
                // proto-style FQN (e.g. `weather.WeatherService` → `WeatherService`).
                // Service names that *end* with a dotted token (e.g. `Socket.IO`,
                // `Akka.Actor.Tap`) keep their full name — splitting them would
                // produce nonsense like `Socket.IO` → `IO`.
                var svcDisplayName = selectedService.name;
                if (svcDisplayName.includes('.')) {
                    var lastToken = svcDisplayName.split('.').pop();
                    if (lastToken.length >= 4) svcDisplayName = lastToken;
                }

                // When the service name matches the plugin's display name
                // exactly (e.g. Socket.IO plugin exposes one service literally
                // named 'Socket.IO'), drop this segment so the breadcrumb
                // doesn't read 'Socket.IO > Socket.IO'. Multi-service
                // protocols like gRPC have per-call distinct names so this
                // only kicks in for single-service plugins.
                var protoForCheck = selectedService.source
                    ? protocols.find(function (p) { return p.id === selectedService.source; })
                    : null;
                var skipServiceSegment = protoForCheck && protoForCheck.name === svcDisplayName;

                if (!skipServiceSegment) breadcrumb.appendChild(el('span', {
                    className: 'bowire-breadcrumb-item current',
                    textContent: svcDisplayName,
                    title: selectedService.name,
                    onClick: function () {
                        // Expand this service in the sidebar, close active tab
                        expandedServices.add(selectedService.name);
                        persistExpandedServices();
                        if (activeTabId) {
                            closeTab(activeTabId);
                        } else {
                            selectedMethod = null;
                            render();
                        }
                    }
                }));
            }

            var headerName = el('div', { className: 'bowire-header-method' + (selectedMethod.deprecated ? ' deprecated' : '') });
            headerName.appendChild(document.createTextNode(selectedMethod.name));
            if (selectedMethod.deprecated) {
                headerName.appendChild(el('span', { className: 'bowire-header-deprecated', textContent: 'DEPRECATED' }));
            }

            // Stack breadcrumb + method-name + path + summary vertically
            // inside the same column-flex container. Previously breadcrumb
            // sat in a sibling row to the right of the toggle button, so a
            // short Protocol > Service trail and a short method name landed
            // on the same horizontal axis and overlapped at narrower widths.
            // Path label — for REST the badge already shows the verb,
            // so we only print the path (httpPath) to avoid a redundant
            // "PUT /pet" + "PUT" pair. For other protocols
            // (gRPC, GraphQL, …) httpPath isn't set; fall back to
            // fullName so the operator still gets a service/method
            // identifier here.
            //
            // The Protocol > Service breadcrumb line that used to lead
            // the info column is gone — it read as filler ("REST › pet"
            // duplicated the protocol chip + the service segment the
            // user just clicked in the sidebar). The protocol icon
            // moves to the right cluster as a small glyph next to the
            // verb chip so the operator still sees which protocol
            // they're invoking, just out of the info column.
            // Path moved out of the info column and next to the
            // verb chip on the right — REST verb + path read as
            // one address ("PUT /pet"), and the info column shrinks
            // to two lines (name + description) instead of three.
            var pathLabel = selectedMethod.httpPath || selectedMethod.fullName || '';
            const info = el('div', { className: 'bowire-header-info' },
                headerName
            );
            // Summary line — first line of the method's summary or
            // description. When that line is too long for the
            // available width it ellipsis-truncates via CSS; the
            // full text is mirrored into the title attribute (native
            // tooltip on hover) AND a "…" expand button next to the
            // line opens a popup with the unabridged content so the
            // operator can read multi-paragraph descriptions
            // without leaving the header.
            var summary = selectedMethod.summary || selectedMethod.description;
            if (summary) {
                var fullText = String(summary).trim();
                var firstLine = fullText.split('\n')[0].trim();
                if (firstLine.length > 0) {
                    var summaryRow = el('div', { className: 'bowire-header-summary-row' });
                    summaryRow.appendChild(el('span', {
                        className: 'bowire-header-summary',
                        textContent: firstLine,
                        title: fullText
                    }));
                    // Show the expand button only when the full text
                    // is meaningfully longer than the first line
                    // (multi-paragraph, or a long single line). 80
                    // chars is a coarse-but-reasonable cutoff that
                    // matches the row width we hover-truncate at.
                    var needsExpand = fullText.length > firstLine.length
                        || firstLine.length > 80;
                    if (needsExpand) {
                        summaryRow.appendChild(el('button', {
                            type: 'button',
                            className: 'bowire-header-summary-expand',
                            title: fullText,
                            'aria-label': 'Show full description',
                            textContent: '…',
                            onClick: function (e) {
                                e.stopPropagation();
                                // Drop a previously-open popup if
                                // any, then mount a fresh one
                                // anchored to body so it isn't
                                // clipped by the header's overflow.
                                var prev = document.querySelector('.bowire-header-summary-popup');
                                if (prev) prev.remove();
                                var popup = el('div', { className: 'bowire-header-summary-popup', role: 'dialog' },
                                    el('div', { className: 'bowire-header-summary-popup-title',
                                        textContent: selectedMethod.name + ' — description' }),
                                    el('div', { className: 'bowire-header-summary-popup-body', textContent: fullText }),
                                    el('button', {
                                        type: 'button',
                                        className: 'bowire-header-summary-popup-close',
                                        textContent: 'Close',
                                        onClick: function () { popup.remove(); }
                                    })
                                );
                                document.body.appendChild(popup);
                                // Outside-click closer.
                                setTimeout(function () {
                                    function onOutside(ev) {
                                        if (!popup.contains(ev.target)) {
                                            popup.remove();
                                            document.removeEventListener('click', onOutside, true);
                                        }
                                    }
                                    document.addEventListener('click', onOutside, true);
                                }, 0);
                            }
                        }));
                    }
                    info.appendChild(summaryRow);
                }
            }
            // Silence the unused-var lint — breadcrumb is still
            // assembled above for the moment in case we want to
            // reinstate it behind a toggle.
            void breadcrumb;
            header.appendChild(info);
            // Protocol icon — small glyph immediately left of the
            // verb chip. Reuses the proto icon already supplied by
            // the protocol plugin (same svg the sidebar's service
            // header uses) so the chip cluster carries the full
            // "protocol + verb" identity in one line.
            if (selectedService && selectedService.source) {
                var headerProto = protocols.find(function (p) { return p.id === selectedService.source; });
                if (headerProto && headerProto.icon) {
                    var protoSource = selectedService.source;
                    var protoBtn = el('button', {
                        type: 'button',
                        className: 'bowire-header-proto-icon bowire-header-proto-icon-clickable',
                        title: headerProto.name + ' — click for recent ' + headerProto.name + ' calls',
                        'aria-label': 'Recent ' + headerProto.name + ' calls',
                        innerHTML: headerProto.icon,
                        onClick: function (e) {
                            e.stopPropagation();
                            // Close any previously-open popup before
                            // mounting a fresh one. The list is filtered
                            // to history entries whose service shares
                            // the current method's protocol (REST →
                            // REST calls, gRPC → gRPC calls, &c.).
                            var prev = document.querySelector('.bowire-header-recent-popup');
                            if (prev) prev.remove();
                            var entries = (typeof getHistory === 'function' ? getHistory() : [])
                                .filter(function (h) {
                                    var svc = services && services.find(function (s) { return s.name === h.service; });
                                    return svc && svc.source === protoSource;
                                })
                                .slice(0, 20);
                            var popup = el('div', {
                                className: 'bowire-header-recent-popup',
                                role: 'dialog'
                            });
                            popup.appendChild(el('div', {
                                className: 'bowire-header-recent-popup-title',
                                textContent: 'Recent ' + headerProto.name + ' calls'
                            }));
                            if (entries.length === 0) {
                                popup.appendChild(el('div', {
                                    className: 'bowire-header-recent-popup-empty',
                                    textContent: 'No ' + headerProto.name + ' calls yet — fire one to see it here.'
                                }));
                            } else {
                                var list = el('div', { className: 'bowire-header-recent-popup-list' });
                                entries.forEach(function (h) {
                                    var row = el('button', {
                                        type: 'button',
                                        className: 'bowire-header-recent-popup-row'
                                            + (isHistoryEntryOk(h) ? '' : ' is-error'),
                                        title: 'Replay this call',
                                        onClick: function () {
                                            popup.remove();
                                            if (typeof replayHistoryEntry === 'function') replayHistoryEntry(h);
                                        }
                                    });
                                    row.appendChild(el('span', {
                                        className: 'bowire-header-recent-popup-method',
                                        textContent: (h.service ? h.service + '.' : '') + h.method
                                    }));
                                    var when = h.timestamp
                                        ? new Date(h.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
                                        : '';
                                    row.appendChild(el('span', {
                                        className: 'bowire-header-recent-popup-meta',
                                        textContent: (h.status || '') + (when ? ' · ' + when : '')
                                    }));
                                    list.appendChild(row);
                                });
                                popup.appendChild(list);
                            }
                            popup.appendChild(el('button', {
                                type: 'button',
                                className: 'bowire-header-recent-popup-close',
                                textContent: 'Close',
                                onClick: function () { popup.remove(); }
                            }));
                            document.body.appendChild(popup);
                            // Anchor below the trigger.
                            var rect = protoBtn.getBoundingClientRect();
                            popup.style.position = 'fixed';
                            var rightEdge = Math.min(window.innerWidth - 8, rect.right);
                            popup.style.left = Math.max(8, rightEdge - popup.offsetWidth) + 'px';
                            popup.style.top = (rect.bottom + 6) + 'px';
                            // Outside-click closer.
                            setTimeout(function () {
                                function onOutside(ev) {
                                    if (!popup.contains(ev.target) && !protoBtn.contains(ev.target)) {
                                        popup.remove();
                                        document.removeEventListener('click', onOutside, true);
                                    }
                                }
                                document.addEventListener('click', onOutside, true);
                            }, 0);
                        }
                    });
                    header.appendChild(protoBtn);
                    // R3a — Discover method meta → Help transition. The
                    // protocol id always maps to a topic in the Help
                    // package's docs/protocols/<id>.md set, so render a
                    // small "?" alongside the protocol glyph. helpAvailable
                    // gates the click in openHelpRail so a host without
                    // the Help package referenced gets a no-op (and we
                    // hide the icon altogether) instead of a dead link.
                    // No icon for method-meta labels that don't have a
                    // dedicated topic (HTTP status / content-type) — the
                    // brief explicitly forbids fake-wired CTAs.
                    if (typeof helpAvailable !== 'undefined' && helpAvailable
                        && typeof openHelpRail === 'function') {
                        var helpProtoSlug = 'protocols/' + protoSource;
                        header.appendChild(el('button', {
                            type: 'button',
                            className: 'bowire-header-meta-help',
                            title: 'Open the ' + headerProto.name + ' protocol docs in the Help rail',
                            'aria-label': 'Help on ' + headerProto.name,
                            textContent: '?',
                            onClick: function (e) {
                                e.stopPropagation();
                                openHelpRail(helpProtoSlug);
                            }
                        }));
                    }
                }
            }
            header.appendChild(el('span', {
                className: 'bowire-header-badge',
                dataset: { type: methodBadgeType(selectedMethod) },
                textContent: selectedMethod.httpMethod || methodBadgeLabel(selectedMethod.methodType)
            }));
            // R3a — Discover method meta → Help transition (method-type).
            // Streaming method types map to features/streaming; Duplex
            // maps to features/duplex-channels. Unary has no special
            // topic, so the icon stays hidden — that's the intentional
            // "no topic, no icon" path from the brief.
            if (typeof helpAvailable !== 'undefined' && helpAvailable
                && typeof openHelpRail === 'function') {
                var mt = (selectedMethod && selectedMethod.methodType) || '';
                var typeTopic = null;
                if (mt === 'ServerStreaming' || mt === 'ClientStreaming') typeTopic = 'features/streaming';
                else if (mt === 'Duplex') typeTopic = 'features/duplex-channels';
                if (typeTopic) {
                    var typeTopicSlug = typeTopic;
                    header.appendChild(el('button', {
                        type: 'button',
                        className: 'bowire-header-meta-help',
                        title: 'Open ' + mt + ' docs in the Help rail',
                        'aria-label': 'Help on ' + mt,
                        textContent: '?',
                        onClick: function (e) {
                            e.stopPropagation();
                            openHelpRail(typeTopicSlug);
                        }
                    }));
                }
            }
            // Path right next to the verb chip — "PUT /pet" reads
            // as one address. The title tooltip + double-click copy
            // use the FULLY qualified URL (origin + path) so the
            // operator can grab the real callable URL without
            // hunting through code-export.
            if (pathLabel) {
                var fullMethodUrl = (function () {
                    var base = (selectedService && selectedService.originUrl)
                        || (typeof serverUrls !== 'undefined' && serverUrls[0])
                        || '';
                    var path = selectedMethod.httpPath || '';
                    if (!path) return base || pathLabel;
                    // Discovery URLs that point at an OpenAPI / AsyncAPI
                    // doc need their last segment stripped — otherwise we'd
                    // append the method path to .../openapi.json.
                    if (/\.(json|yaml|yml)(\?.*)?$/i.test(base)) {
                        base = base.replace(/\/[^\/]+(\?.*)?$/, '');
                    }
                    return base.replace(/\/$/, '') + path;
                })();
                header.appendChild(el('span', {
                    className: 'bowire-header-path bowire-header-path-inline',
                    textContent: pathLabel,
                    title: fullMethodUrl,
                    onDblClick: function (e) {
                        e.preventDefault();
                        navigator.clipboard.writeText(fullMethodUrl).then(function () {
                            if (typeof toast === 'function') toast('URL copied', 'success');
                        });
                    }
                }));
            }

            // #156 — favorite star in the method header. Toggle the
            // current method's favorite state from where the operator
            // is looking at it instead of having to navigate to Home.
            // Filled glyph = favorited, outline = not. Reads + writes
            // the same workspace-scoped store the Home page consumes.
            if (typeof isFavorite === 'function' && typeof toggleFavorite === 'function') {
                try {
                    var svcName = selectedService.name;
                    var mthName = selectedMethod.name;
                    var isStar = isFavorite(svcName, mthName);
                    header.appendChild(el('button', {
                        className: 'bowire-header-fav-btn' + (isStar ? ' active' : ''),
                        title: isStar ? 'Remove from favorites' : 'Add to favorites',
                        'aria-label': isStar ? 'Remove from favorites' : 'Add to favorites',
                        onClick: function () {
                            toggleFavorite(svcName, mthName);
                            render();
                        },
                        innerHTML: svgIcon(isStar ? 'starFilled' : 'star')
                    }));
                } catch (e) { console.warn('[favorites] header-star failed', e); }
            }

            // Preset picker — lists the presets saved against this
            // method (filtered by service + method on the preset's
            // snapshot). Default click on a preset applies it
            // (body → editor); per-row star toggles
            // "load this preset automatically when this method is
            // opened"; per-row trash deletes; the bottom-of-menu
            // "Add to collection ▸" routes the selected preset into
            // a Collection via the same per-item shape Collections
            // already store. Trigger sits next to the favorite star
            // so the cluster reads as "this method's saved state".
            try {
                var presetSvc = selectedService.name;
                var presetMth = selectedMethod.name;
                var presetAll = (typeof loadPresets === 'function')
                    ? loadPresets('discover') : [];
                var presetList = presetAll.filter(function (p) {
                    return p && p.config
                        && p.config.service === presetSvc
                        && p.config.method === presetMth;
                });
                // Picker is per-method — showing it for unrelated
                // presets just clutters the header. Workspace-wide
                // management stays reachable via the bottom of this
                // dropdown when at least one preset matches.
                if (presetList.length > 0) {
                    var presetWrap = el('div', { className: 'bowire-header-presets-wrap' });
                    var presetBtn = el('button', {
                        type: 'button',
                        id: 'bowire-header-presets-btn',
                        className: 'bowire-header-presets-btn',
                        title: presetList.length + ' preset' + (presetList.length === 1 ? '' : 's') + ' for this method',
                        'aria-label': 'Saved presets',
                        'aria-haspopup': 'menu',
                        onClick: function (e) {
                            e.stopPropagation();
                            var prev = document.querySelector('.bowire-header-presets-menu');
                            if (prev) { prev.remove(); return; }
                            // Re-resolve the preset list AT CLICK TIME
                            // — morphdom keeps the old DOM button across
                            // re-renders, so the closure variable
                            // captured at first paint is stale by the
                            // time the next preset is saved. Reading
                            // loadPresets() here picks up the live list,
                            // including entries added by Save-as-preset
                            // since the last render.
                            var liveSvc = selectedService ? selectedService.name : presetSvc;
                            var liveMth = selectedMethod ? selectedMethod.name : presetMth;
                            var presetList = (typeof loadPresets === 'function'
                                    ? loadPresets('discover') : []).filter(function (p) {
                                return p && p.config
                                    && p.config.service === liveSvc
                                    && p.config.method === liveMth;
                            });
                            var menu = el('div', { className: 'bowire-header-presets-menu', role: 'menu' });
                            // Each row follows the same shape as the workspace
                            // dropdown: whole row clicks to apply, tools cluster
                            // on the right carries the state marker (default
                            // star — analog of the workspace ✓) + secondary
                            // actions that hover-reveal.
                            //
                            //   name                    [⭐ state] [🗀] [🗑]
                            //    ↑                          ↑         ↑
                            //  row-click=apply          state marker hover-reveal
                            presetList.forEach(function (preset) {
                                var row = el('div', {
                                    className: 'bowire-header-presets-row'
                                        + (preset.isDefault ? ' is-default' : ''),
                                    role: 'menuitem',
                                    title: 'Apply preset',
                                    onClick: function (ev) {
                                        ev.stopPropagation();
                                        if (typeof applyPresetToCurrentMethod === 'function'
                                            && applyPresetToCurrentMethod(preset)) {
                                            menu.remove();
                                        }
                                    }
                                });
                                row.appendChild(el('span', {
                                    className: 'bowire-header-presets-name',
                                    textContent: preset.name || 'Untitled'
                                }));
                                // Tools cluster — sits on the right, carries
                                // the state marker (always-visible slot;
                                // greyed-out when not default) plus the
                                // hover-revealed secondary actions.
                                var tools = el('div', { className: 'bowire-header-presets-tools' });
                                // Star = state marker + toggle. Reserved-width
                                // slot so the row geometry doesn't jump when
                                // the default flips. Stays visible when
                                // default ("the marker"); when not default it
                                // hover-reveals along with the other tools.
                                tools.appendChild(el('button', {
                                    type: 'button',
                                    className: 'bowire-header-presets-default'
                                        + (preset.isDefault ? ' is-on' : ''),
                                    title: preset.isDefault
                                        ? 'Default preset — click to clear'
                                        : 'Set as default for this method',
                                    'aria-label': 'Toggle default preset',
                                    innerHTML: svgIcon(preset.isDefault ? 'starFilled' : 'star'),
                                    onClick: function (ev) {
                                        ev.stopPropagation();
                                        if (preset.isDefault) {
                                            if (typeof clearDefaultPreset === 'function') clearDefaultPreset('discover');
                                        } else {
                                            if (typeof setAsDefaultPreset === 'function') setAsDefaultPreset('discover', preset.id);
                                        }
                                        menu.remove();
                                        render();
                                    }
                                }));
                                tools.appendChild(el('button', {
                                    type: 'button',
                                    className: 'bowire-header-presets-tool',
                                    title: 'Add to collection…',
                                    'aria-label': 'Add to collection',
                                    innerHTML: svgIcon('folder'),
                                    onClick: function (ev) {
                                        ev.stopPropagation();
                                        var existing = row.querySelector('.bowire-header-presets-collist');
                                        if (existing) { existing.remove(); return; }
                                        var collist = el('div', { className: 'bowire-header-presets-collist' });
                                        var cols = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList))
                                            ? collectionsList : [];
                                        if (cols.length === 0) {
                                            collist.appendChild(el('div', {
                                                className: 'bowire-header-presets-collist-empty',
                                                textContent: 'No collections yet'
                                            }));
                                        }
                                        cols.forEach(function (col) {
                                            collist.appendChild(el('button', {
                                                type: 'button',
                                                className: 'bowire-header-presets-collist-row',
                                                textContent: col.name,
                                                onClick: function (ev2) {
                                                    ev2.stopPropagation();
                                                    if (typeof addToCollection === 'function') {
                                                        addToCollection(col.id, preset.config);
                                                        toast('Added "' + (preset.name || 'preset') + '" to ' + col.name, 'success');
                                                    }
                                                    menu.remove();
                                                    render();
                                                }
                                            }));
                                        });
                                        row.appendChild(collist);
                                    }
                                }));
                                tools.appendChild(el('button', {
                                    type: 'button',
                                    className: 'bowire-header-presets-tool bowire-header-presets-tool-danger',
                                    title: 'Delete preset',
                                    'aria-label': 'Delete preset',
                                    innerHTML: svgIcon('trash'),
                                    onClick: function (ev) {
                                        ev.stopPropagation();
                                        if (typeof deletePreset === 'function') deletePreset('discover', preset.id);
                                        menu.remove();
                                        render();
                                    }
                                }));
                                row.appendChild(tools);
                                menu.appendChild(row);
                            });
                            // Separator + "Save as preset" action — mirrors
                            // the workspace dropdown's "New workspace…" entry
                            // at the bottom. Keeps the create-flow discoverable
                            // from the same surface where the operator browses
                            // existing presets, without forcing them through
                            // the unrelated "+ Add to…" menu.
                            menu.appendChild(el('div', { className: 'bowire-header-presets-divider' }));
                            menu.appendChild(el('div', {
                                className: 'bowire-header-presets-row bowire-header-presets-row-action',
                                role: 'menuitem',
                                title: 'Save current request as a new preset',
                                onClick: function (ev) {
                                    ev.stopPropagation();
                                    // Inline snapshot — _snapshotRequest lives
                                    // in the +Add-to closure and isn't reachable
                                    // here. Same shape as that path.
                                    if (!selectedService || !selectedMethod) return;
                                    try {
                                        if (typeof syncFormToJson === 'function'
                                                && typeof requestInputMode !== 'undefined'
                                                && requestInputMode === 'form') {
                                            syncFormToJson();
                                        }
                                    } catch { /* schema-form not loaded */ }
                                    var body = (Array.isArray(requestMessages) && requestMessages[0]) || '{}';
                                    var meta = {};
                                    var metaRows = document.querySelectorAll('.bowire-metadata-row');
                                    for (var mi = 0; mi < metaRows.length; mi++) {
                                        var inputs = metaRows[mi].querySelectorAll('.bowire-metadata-input');
                                        if (inputs.length === 2 && inputs[0].value.trim()) {
                                            meta[inputs[0].value.trim()] = inputs[1].value;
                                        }
                                    }
                                    var snap = {
                                        service: selectedService.name,
                                        method: selectedMethod.name,
                                        methodType: selectedMethod.methodType || 'Unary',
                                        protocol: selectedService.source || selectedProtocol || 'grpc',
                                        body: body,
                                        messages: Array.isArray(requestMessages) ? requestMessages.slice() : [body],
                                        metadata: Object.keys(meta).length > 0 ? meta : null,
                                        serverUrl: selectedService.originUrl || (Array.isArray(serverUrls) && serverUrls[0]) || null
                                    };
                                    menu.remove();
                                    bowirePrompt('Preset name', {
                                        title: 'Save as preset',
                                        placeholder: selectedMethod.name + ' preset',
                                        confirmText: 'Save'
                                    }).then(function (name) {
                                        if (!name) return;
                                        if (typeof savePresetFromSnapshot === 'function') {
                                            savePresetFromSnapshot('discover', String(name).trim(), snap);
                                            toast('Preset saved', 'success');
                                        }
                                        render();
                                    });
                                }
                            },
                                el('span', { className: 'bowire-header-presets-action-icon', innerHTML: svgIcon('plus') }),
                                el('span', { textContent: 'Save as preset…' })
                            ));
                            document.body.appendChild(menu);
                            // Anchor to the trigger button.
                            var rect = presetBtn.getBoundingClientRect();
                            var rightEdge = Math.min(window.innerWidth - 8, rect.right);
                            menu.style.position = 'fixed';
                            menu.style.left = Math.max(8, rightEdge - menu.offsetWidth) + 'px';
                            menu.style.top = (rect.bottom + 6) + 'px';
                            setTimeout(function () {
                                function onOutside(ev) {
                                    if (!menu.contains(ev.target) && !presetBtn.contains(ev.target)) {
                                        menu.remove();
                                        document.removeEventListener('click', onOutside, true);
                                    }
                                }
                                document.addEventListener('click', onOutside, true);
                            }, 0);
                        }
                    },
                        el('span', { innerHTML: svgIcon('preset') }),
                        el('span', {
                            className: 'bowire-header-presets-count',
                            textContent: String(presetList.length)
                        })
                    );
                    presetWrap.appendChild(presetBtn);
                    header.appendChild(presetWrap);
                }
            } catch (e) { console.warn('[presets] header picker failed', e); }

            // #296 — "Add to…" quick-action menu. Sits between the
            // favorite star and the hint chip. The trigger is a
            // single icon-only button; the popup lists the canonical
            // cross-feature destinations (Collection, Preset,
            // Benchmark, Recording). Each action snapshots the LIVE
            // request state at click-time (not render-time) — morphdom
            // preserves the dropdown node across renders so any captured
            // closure values would get stale.
            try {
                var svcNameForMenu = selectedService.name;
                var mthNameForMenu = selectedMethod.name;
                var addToWrap = el('div', { className: 'bowire-header-addto-wrap' });
                addToWrap.appendChild(el('button', {
                    id: 'bowire-header-addto-btn',
                    className: 'bowire-header-addto-btn' + (methodAddToMenuOpen ? ' active' : ''),
                    title: 'Add this method to…',
                    'aria-label': 'Add this method to…',
                    'aria-haspopup': 'menu',
                    'aria-expanded': methodAddToMenuOpen ? 'true' : 'false',
                    onClick: function (e) {
                        e.stopPropagation();
                        methodAddToMenuOpen = !methodAddToMenuOpen;
                        render();
                    },
                    innerHTML: svgIcon('plus')
                }));

                if (methodAddToMenuOpen) {
                    var menu = el('div', {
                        className: 'bowire-header-addto-menu',
                        role: 'menu',
                        onClick: function (e) { e.stopPropagation(); }
                    });

                    function _closeAddToMenu() {
                        methodAddToMenuOpen = false;
                    }
                    function _snapshotRequest() {
                        var liveSvc = selectedService;
                        var liveMth = selectedMethod;
                        if (!liveSvc || !liveMth) return null;
                        // If the user was editing on the Form sub-tab,
                        // their changes live in formValues — flush them
                        // into requestMessages[0] before snapshotting so
                        // the saved preset / collection / benchmark
                        // carries the real edited body, not the empty
                        // {} that the editor started with.
                        try {
                            if (typeof syncFormToJson === 'function'
                                    && typeof requestInputMode !== 'undefined'
                                    && requestInputMode === 'form') {
                                syncFormToJson();
                            }
                        } catch { /* schema-form not loaded */ }
                        var body = (Array.isArray(requestMessages) && requestMessages[0]) || '{}';
                        var meta = {};
                        var metaRows = document.querySelectorAll('.bowire-metadata-row');
                        for (var mi = 0; mi < metaRows.length; mi++) {
                            var inputs = metaRows[mi].querySelectorAll('.bowire-metadata-input');
                            if (inputs.length === 2 && inputs[0].value.trim()) {
                                meta[inputs[0].value.trim()] = inputs[1].value;
                            }
                        }
                        return {
                            service: liveSvc.name,
                            method: liveMth.name,
                            methodType: liveMth.methodType || 'Unary',
                            protocol: liveSvc.source || selectedProtocol || 'grpc',
                            body: body,
                            messages: Array.isArray(requestMessages) ? requestMessages.slice() : [body],
                            metadata: Object.keys(meta).length > 0 ? meta : null,
                            serverUrl: liveSvc.originUrl || (Array.isArray(serverUrls) && serverUrls[0]) || null
                        };
                    }

                    // ---- Collection section ----
                    menu.appendChild(el('div', { className: 'bowire-header-addto-section', textContent: 'Collection' }));
                    var existingCols = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) ? collectionsList : [];
                    if (existingCols.length > 0) {
                        existingCols.slice(0, 6).forEach(function (col) {
                            menu.appendChild(el('button', {
                                className: 'bowire-header-addto-item',
                                role: 'menuitem',
                                onClick: function () {
                                    var snap = _snapshotRequest();
                                    if (!snap) return;
                                    addToCollection(col.id, snap);
                                    toast('Added to "' + col.name + '"', 'success');
                                    _closeAddToMenu();
                                    render();
                                }
                            },
                                el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('folder') }),
                                el('span', { textContent: col.name }),
                                el('span', { className: 'bowire-header-addto-item-meta', textContent: col.items.length + (col.items.length === 1 ? ' entry' : ' entries') })
                            ));
                        });
                        if (existingCols.length > 6) {
                            menu.appendChild(el('div', {
                                className: 'bowire-header-addto-item-meta',
                                style: 'padding:4px 12px 0',
                                textContent: '+ ' + (existingCols.length - 6) + ' more in Collections'
                            }));
                        }
                    }
                    menu.appendChild(el('button', {
                        className: 'bowire-header-addto-item bowire-header-addto-item-create',
                        role: 'menuitem',
                        onClick: function () {
                            bowirePrompt('Collection name', {
                                title: 'New collection',
                                placeholder: 'e.g. Smoke tests',
                                confirmText: 'Create'
                            }).then(function (name) {
                                if (name === null) return;
                                var trimmed = String(name || '').trim();
                                var col = createCollection(trimmed || undefined);
                                var snap = _snapshotRequest();
                                if (snap) addToCollection(col.id, snap);
                                toast('Saved to "' + col.name + '"', 'success');
                                _closeAddToMenu();
                                render();
                            });
                        }
                    },
                        el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('plus') }),
                        el('span', { textContent: 'New collection…' })
                    ));

                    // ---- Other destinations ----
                    menu.appendChild(el('div', { className: 'bowire-header-addto-section', textContent: 'Send to' }));

                    menu.appendChild(el('button', {
                        className: 'bowire-header-addto-item',
                        role: 'menuitem',
                        onClick: function () {
                            var snap = _snapshotRequest();
                            if (!snap) return;
                            bowirePrompt('Preset name', {
                                title: 'Save as preset',
                                placeholder: snap.method + ' preset',
                                confirmText: 'Save'
                            }).then(function (name) {
                                if (!name) return;
                                if (typeof savePresetFromSnapshot === 'function') {
                                    savePresetFromSnapshot('discover', String(name).trim(), snap);
                                    toast('Preset saved', 'success');
                                }
                                _closeAddToMenu();
                                render();
                            });
                        }
                    },
                        el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('preset') }),
                        el('span', { textContent: 'Save as preset' })
                    ));

                    // #295 Phase E — "Open in Compose" promotes the
                    // discovered method into a live Compose tab so the
                    // operator can iterate freely without affecting the
                    // schema-driven Discover view. The new tab carries
                    // an origin:'discover' badge.
                    if (typeof spawnDesignTabFromItem === 'function') {
                        menu.appendChild(el('button', {
                            className: 'bowire-header-addto-item',
                            role: 'menuitem',
                            onClick: function () {
                                var snap = _snapshotRequest();
                                if (!snap) return;
                                _closeAddToMenu();
                                if (typeof railMode !== 'undefined') {
                                    railMode = 'compose';
                                    try { localStorage.setItem('bowire_rail_mode', 'compose'); } catch { /* ignore */ }
                                }
                                spawnDesignTabFromItem(snap, {
                                    kind: 'discover',
                                    service: snap.service,
                                    method: snap.method
                                });
                                if (typeof toast === 'function') {
                                    toast('Opened in Compose', 'success');
                                }
                                render();
                            }
                        },
                            el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('compose') }),
                            el('span', { textContent: 'Open in Compose' })
                        ));
                    }

                    if (typeof addTargetToEnvelopePicker === 'function') {
                        // Benchmarking is only meaningful for a request
                        // shape the server actually accepts — otherwise
                        // the user persists a wire that they already
                        // know is broken. Require a successful last
                        // call (statusInfo present, no responseError)
                        // before letting the item open the picker.
                        var lastCallOk = (typeof statusInfo !== 'undefined' && statusInfo)
                            && (typeof responseError === 'undefined' || !responseError)
                            && statusInfo.status !== 'Error';
                        menu.appendChild(el('button', {
                            className: 'bowire-header-addto-item'
                                + (lastCallOk ? '' : ' bowire-header-addto-item-disabled'),
                            role: 'menuitem',
                            disabled: lastCallOk ? undefined : true,
                            title: lastCallOk
                                ? 'Add this method to a benchmark envelope'
                                : 'Execute a successful call first — envelopes need a known-good request',
                            onClick: function (ev) {
                                if (!lastCallOk) return;
                                var snap = _snapshotRequest();
                                if (!snap) return;
                                _closeAddToMenu();
                                addTargetToEnvelopePicker(ev.clientX, ev.clientY, {
                                    type: 'method',
                                    service: snap.service,
                                    method: snap.method,
                                    protocol: snap.protocol,
                                    body: snap.body,
                                    metadata: snap.metadata || {},
                                    serverUrl: null
                                }, { name: snap.service + '.' + snap.method });
                            }
                        },
                            el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('lightning') }),
                            el('span', { textContent: 'Add to envelope…' })
                        ));
                    }

                    if (typeof startRecording === 'function') {
                        var recActive = (typeof recordingActive === 'boolean' && recordingActive)
                            || (typeof activeRecordingId !== 'undefined' && activeRecordingId);
                        menu.appendChild(el('button', {
                            className: 'bowire-header-addto-item' + (recActive ? ' bowire-header-addto-item-disabled' : ''),
                            role: 'menuitem',
                            disabled: recActive ? true : undefined,
                            title: recActive ? 'Already recording — new calls land in the active recording' : 'Start a new recording; subsequent calls land there',
                            onClick: function () {
                                if (recActive) return;
                                startRecording();
                                toast('Recording started — invoke the method to capture', 'success');
                                _closeAddToMenu();
                                render();
                            }
                        },
                            el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('record') }),
                            el('span', { textContent: recActive ? 'Recording in progress' : 'Start recording' })
                        ));
                    }

                    addToWrap.appendChild(menu);
                }
                header.appendChild(addToWrap);
                // svcNameForMenu / mthNameForMenu kept in case future
                // items need the labels at construction time.
                void svcNameForMenu; void mthNameForMenu;
            } catch (e) { console.warn('[addto] header-menu failed', e); }

            // #114 — inline hint chip at the method header. Surfaces the
            // deterministic hint engine's results at the place where the
            // operator looks for help (the method they're about to
            // invoke) instead of burying them in the Assistant drawer's
            // static list. Click opens the drawer to read the full
            // text; the chip itself is a one-glance affordance.
            // Phase 2 — only hints WITH surface='header' (or no surface
            // → defaults to header) appear here. Response/auth-targeted
            // hints render inline at their own surface; the drawer
            // still shows the full unfiltered list as the history view.
            if (typeof evaluateHintsForSurface === 'function') {
                try {
                    var hints = evaluateHintsForSurface('header');
                    if (hints && hints.length > 0) {
                        header.appendChild(el('button', {
                            className: 'bowire-header-hint-chip',
                            title: hints.length + ' hint' + (hints.length === 1 ? '' : 's') + ' for this method — click to open the Assistant',
                            'aria-label': 'Open Assistant hints (' + hints.length + ')',
                            onClick: function () {
                                aiDrawerOpen = true;
                                try { localStorage.setItem('bowire_ai_drawer_open', '1'); } catch { /* ignore */ }
                                render();
                            }
                        },
                            el('span', { className: 'bowire-header-hint-chip-icon', innerHTML: svgIcon('spark') }),
                            el('span', { className: 'bowire-header-hint-chip-count', textContent: String(hints.length) })
                        ));
                    }
                } catch { /* hint engine may throw on partial state — fail silent */ }
            }

            // Copy invoke URL — useful in embedded mode where there's no
            // visible URL bar. Builds a curl-friendly URL from the method.
            header.appendChild(el('button', {
                id: 'bowire-header-copy-url-btn',
                className: 'bowire-header-copy-url',
                title: 'Copy invoke URL',
                'aria-label': 'Copy invoke URL',
                innerHTML: svgIcon('copy'),
                onClick: function () {
                    var base = (selectedService && selectedService.originUrl)
                        || window.location.origin;
                    var invokeUrl = base + '/' + config.prefix + '/api/invoke'
                        + '?service=' + encodeURIComponent(selectedService ? selectedService.name : '')
                        + '&method=' + encodeURIComponent(selectedMethod.name);
                    navigator.clipboard.writeText(invokeUrl).then(function () {
                        toast('Copied invoke URL', 'success');
                    });
                }
            }));
        }
        // Stack order: tab strip → method header → content panes.
        // The tab strip is the top-level navigation (which method are
        // we looking at), the header is the identity card for the
        // currently-active tab, and the panes are the work surface.
        // Putting tabs first lets the user switch contexts without
        // their eye crossing the entire header column.
        //
        // Request tab bar — always visible whenever there's a
        // selected method context so the "+" button stays reachable
        // even after the operator closes the last tab. Operator:
        // 'beim schließen eines method tabs in discovery verschwindet
        // der tab button [+] zum erzeugen eines neuen tabs.' The
        // strip used to gate on requestTabs.length >= 1 which hid
        // the "+" together with the tabs.
        // Render the strip whenever Discover is showing a work surface
        // (method view OR the landing card), NOT just when a method is
        // selected — so the "+" stays reachable to spawn empty tabs /
        // the new-tab picker even before anything is selected (Discover
        // '+' cases a + b). The freeform builder owns its own chrome, so
        // skip the strip while it's active.
        if (!freeformRequest) {
            var tabBar = el('div', { id: 'bowire-request-tabs', className: 'bowire-request-tabs' });
            var tabScroll = el('div', { className: 'bowire-request-tabs-scroll' });
            for (var ti = 0; ti < requestTabs.length; ti++) {
                (function (tab) {
                    var isActive = tab.id === activeTabId;
                    // Empty placeholder tab (case a) — no method to badge;
                    // render a lightweight "New tab" chip. Selecting a
                    // method while it's active fills it in place.
                    if (tab.empty || !tab.method) {
                        tabScroll.appendChild(el('div', {
                            id: 'bowire-request-tab-' + tab.id,
                            className: 'bowire-request-tab bowire-request-tab-empty' + (isActive ? ' active' : ''),
                            title: 'New tab — pick a method to fill it',
                            'data-tab-id': tab.id,
                            onClick: function (e) {
                                var id = e.currentTarget.dataset.tabId;
                                if (id) switchTab(id);
                            }
                        },
                            el('span', { className: 'bowire-request-tab-name', textContent: 'New tab' }),
                            requestTabs.length > 1 ? el('button', {
                                className: 'bowire-request-tab-close',
                                innerHTML: svgIcon('close'),
                                title: 'Close tab (Ctrl+W)',
                                onClick: function (e) {
                                    e.stopPropagation();
                                    var parent = e.currentTarget.closest('.bowire-request-tab');
                                    var id = parent && parent.dataset.tabId;
                                    if (id) closeTab(id);
                                }
                            }) : null
                        ));
                        return;
                    }
                    var dir = methodDirection(tab.method);
                    var proto = (tab.service && tab.service.source) || 'default';
                    // Stable id + data-tab-id — morphdom matches the
                    // node across re-renders by id (so it keeps the
                    // same DOM element for the same tab) AND every
                    // click handler re-reads the id from data-tab-id
                    // at click time instead of closing over `tab.id`.
                    // Without this, morphdom keeps the prior listener
                    // attached to the recycled node — the close button
                    // on tab C would fire `closeTab(B.id)` because the
                    // listener was attached when this slot rendered B.
                    var tabEl = el('div', {
                        id: 'bowire-request-tab-' + tab.id,
                        className: 'bowire-request-tab' + (isActive ? ' active' : ''),
                        title: tab.serviceKey + ' / ' + tab.methodKey,
                        'data-tab-id': tab.id,
                        'data-protocol': proto,
                        'data-direction': dir,
                        onClick: function (e) {
                            var id = e.currentTarget.dataset.tabId;
                            if (id) switchTab(id);
                        },
                        onContextMenu: function (e) {
                            e.preventDefault();
                            e.stopPropagation();
                            if (typeof showContextMenu !== 'function') return;
                            var id = e.currentTarget.dataset.tabId;
                            if (!id) return;
                            var idx = requestTabs.findIndex(function (t) { return t.id === id; });
                            var hasOthers = requestTabs.length > 1;
                            var hasRight = idx >= 0 && idx < requestTabs.length - 1;
                            showContextMenu(e.clientX, e.clientY, [
                                {
                                    label: 'Close',
                                    icon: 'close',
                                    disabled: !hasOthers,
                                    onClick: function () { closeTab(id); }
                                },
                                {
                                    label: 'Close others',
                                    disabled: !hasOthers,
                                    onClick: function () {
                                        // Close every other tab, then make sure
                                        // the right-clicked one is the active +
                                        // sole remaining tab.
                                        var keepId = id;
                                        requestTabs.slice().forEach(function (t) {
                                            if (t.id !== keepId) closeTab(t.id);
                                        });
                                        switchTab(keepId);
                                    }
                                },
                                {
                                    label: 'Close tabs to the right',
                                    disabled: !hasRight,
                                    onClick: function () {
                                        var currentIdx = requestTabs.findIndex(function (t) { return t.id === id; });
                                        if (currentIdx < 0) return;
                                        requestTabs.slice(currentIdx + 1).forEach(function (t) {
                                            closeTab(t.id);
                                        });
                                    }
                                }
                            ]);
                        }
                    },
                        el('span', {
                            className: 'bowire-request-tab-name',
                            textContent: tab.methodKey
                        }),
                        el('span', {
                            className: 'bowire-request-tab-proto bowire-method-badge',
                            dataset: { type: methodBadgeType(tab.method), direction: dir },
                            textContent: methodBadgeText(tab.method)
                        }),
                        // Hide the × on the last remaining tab — closeTab()
                        // refuses to drop below one tab anyway, so leaving
                        // the affordance visible just invited dead clicks.
                        // Closure-free handler — reads the tab id at click
                        // time from data-tab-id on the parent tab.
                        requestTabs.length > 1 ? el('button', {
                            className: 'bowire-request-tab-close',
                            innerHTML: svgIcon('close'),
                            title: 'Close tab (Ctrl+W)',
                            onClick: function (e) {
                                e.stopPropagation();
                                var parent = e.currentTarget.closest('.bowire-request-tab');
                                var id = parent && parent.dataset.tabId;
                                if (id) closeTab(id);
                            }
                        }) : null
                    );
                    tabScroll.appendChild(tabEl);
                })(requestTabs[ti]);
            }
            // "+" tab — pins the currently-selected method into a
            // fresh tab so the active tab stays free for ad-hoc
            // browsing. Without an active method (just started, or
            // landing page open) the button falls back to the
            // freeform request builder. Shift-click forces freeform
            // even when a method is selected, for users who want
            // the empty-tab path on demand.
            var hasUrl = typeof serverUrls !== 'undefined' && Array.isArray(serverUrls) && serverUrls.length > 0;
            tabScroll.appendChild(el('button', {
                className: 'bowire-request-tab-new',
                title: selectedMethod && selectedService
                    ? 'Pin current method into a new tab (Shift-click for empty tab)'
                    : (hasUrl ? 'New tab — pick a method' : 'New tab'),
                onClick: function (e) {
                    var forceEmpty = e && e.shiftKey;
                    // Primary: copy the active tab's method into a fresh
                    // tab (previous tab stays put). Shift forces an empty
                    // tab on demand even with a method selected.
                    if (!forceEmpty && selectedMethod && selectedService) {
                        openTab(selectedService, selectedMethod, { inNewTab: true });
                    } else if (!forceEmpty && hasUrl) {
                        // Case b — URL configured but nothing selected:
                        // offer a URL → service → method picker so a tab
                        // can be filled without the sidebar.
                        openNewTabPicker(e.currentTarget);
                    } else {
                        // Case a — no discovery URL (or shift-forced):
                        // spawn an empty tab that shows the landing hint.
                        openEmptyTab();
                    }
                },
                innerHTML: svgIcon('plus')
            }));
            tabBar.appendChild(tabScroll);
            main.appendChild(tabBar);
            // Overflow popover — replaces the horizontal scrollbar on the
            // open-methods strip. The trailing "+" button stays visible at
            // the right edge as a fixed sibling, the chevron sits just
            // BEFORE it so the spawn-new-tab affordance is never tucked
            // away.
            // Only wire overflow when there are real tabs to overflow.
            // With zero tabs the strip holds just the '+' over the landing
            // card; running the relayout there needlessly hid the '+'
            // (collapsing the strip to 0 height). requestTabs.length gates
            // it so the '+' stays visible in the empty Discover state.
            if (requestTabs.length > 0) {
                requestAnimationFrame(function () {
                    var live = document.querySelector('#bowire-request-tabs .bowire-request-tabs-scroll');
                    if (live && typeof bowireWireTabOverflow === 'function') {
                        bowireWireTabOverflow(live, {
                            tabSelector: '.bowire-request-tab',
                            fixedSelector: '.bowire-request-tab-new',
                            label: 'More tabs'
                        });
                    }
                });
            }
        }

        // Header lives BELOW the tab strip now — see the comment
        // before the tab bar above for the rationale. Empty header
        // (no method selected) stays unattached so the landing card
        // doesn't sit under a gray strip with no content. Skip when
        // the freeform builder is active — that surface renders its
        // OWN editable header below (selectedMethod is preserved so
        // peripheral handlers like the [+] tab still work, but the
        // discovered-method header would visually duplicate the
        // freeform builder's title strip).
        if (header.firstChild && !freeformRequest) main.appendChild(header);

        // Proto-only warning banner
        if (selectedService && selectedService.source === 'proto') {
            var protoBanner = el('div', { className: 'bowire-proto-banner' },
                el('span', { innerHTML: svgIcon('info'), style: 'width:14px;height:14px;display:flex;flex-shrink:0' }),
                el('span', { textContent: 'Schema loaded from .proto file. Invocation requires gRPC Server Reflection enabled on the target server.' })
            );
            main.appendChild(protoBanner);
        }

        // Freeform request builder — manual request without discovery.
        // The builder used to return its own `.bowire-main` div which
        // we appended INSIDE the outer main element — double-nested,
        // and the inner pane's box-model interacted poorly with the
        // outer container's padding/flex. Now we let the builder
        // populate the SHARED main directly via _appendFreeformInto;
        // its children land as direct siblings of header / banner /
        // content / action-bar exactly like a discovered method's
        // render order.
        if (freeformRequest) {
            // #293 — The Hoppscotch-style request-builder no longer
            // piggy-backs on Discover. A freeformRequest carrying the
            // `_requestBuilder` marker belongs in the Compose rail's tab
            // strip; if one shows up while Discover is active that's a
            // stray (Ctrl+L mid-render, &c.) — punt back to Compose and
            // adopt it as a fresh tab there. Classic non-hopp freeform
            // requests still render inline here via _appendFreeformInto.
            if (typeof isHoppRequest === 'function' && isHoppRequest(freeformRequest)) {
                if (typeof composeTabs !== 'undefined'
                    && typeof activeDesignTabId !== 'undefined') {
                    // Adopt the stray request into a Compose tab so the
                    // operator's in-flight edits aren't dropped.
                    var alreadyOwned = composeTabs.some(function (t) { return t.request === freeformRequest; });
                    if (!alreadyOwned) {
                        var adoptedId = 'design_' + (++_designTabIdCounter);
                        composeTabs.push({ id: adoptedId, request: freeformRequest, origin: { kind: 'fresh' } });
                        activeDesignTabId = adoptedId;
                        if (typeof persistDesignTabs === 'function') persistDesignTabs();
                    }
                    railMode = 'compose';
                    try { localStorage.setItem('bowire_rail_mode', 'compose'); } catch { /* ignore */ }
                    return renderComposeMain();
                }
                // Defensive fallback — Compose module not loaded for some
                // reason; render the builder inline so the operator still
                // sees something rather than a blank pane.
                _appendRequestBuilderInto(main);
                return main;
            }
            _appendFreeformInto(main);
            return main;
        }

        if (!selectedMethod) {
            // Context-sensitive empty-state landing page — see landing.js for
            // the seven states (first-run / loading / discovery-failed /
            // editable-no-services / wrong-protocol-tab / multi-url-partial /
            // ready) and their detection rules. The renderer reads global
            // state (serverUrls, services, isLoadingServices, ...) and
            // dispatches to the matching renderState* function.
            renderLandingPage(main);
            return main;
        }

        // Content area: request + response panes. #135 split-mode
        // attribute drives whether the two panes sit side-by-side
        // (horizontal) or stacked (vertical) — CSS rules below the
        // attribute selector flip the flex-direction. Resolve 'auto'
        // here so both the CSS attribute selector and the resizer
        // read the same literal axis ('horizontal' | 'vertical').
        var resolvedSplit = (typeof resolveSplitMode === 'function')
            ? resolveSplitMode(splitMode) : (splitMode || 'horizontal');
        const content = el('div', {
            id: 'bowire-content',
            className: 'bowire-content bowire-content-enter',
            'data-split': resolvedSplit,
        });
        var reqPane = renderRequestPane();
        var resPane = renderResponsePane();
        // Divider id includes the method key so morphdom fully
        // replaces the bar when switching methods — that drops the
        // stale initResizer closure (which captured the OLD reqPane /
        // resPane refs) and the rAF below installs a fresh one against
        // the new panes.
        var dividerMethodKey = (selectedService ? selectedService.name : '')
            + '-' + (selectedMethod ? selectedMethod.name : '');
        var dividerId = 'bowire-pane-divider-' + dividerMethodKey;
        var divider = el('div', {
            id: dividerId,
            className: 'bowire-pane-divider'
        });
        content.appendChild(reqPane);
        content.appendChild(divider);
        content.appendChild(resPane);
        main.appendChild(content);

        // Initialize resizable pane divider after DOM attachment. Re-
        // resolve via getElementById so morphdom node-preservation
        // doesn't leave us holding a detached reference.
        var reqPaneId = reqPane.id;
        var resPaneId = resPane.id;
        requestAnimationFrame(function () {
            var d = document.getElementById(dividerId);
            var l = document.getElementById(reqPaneId);
            var r = document.getElementById(resPaneId);
            if (d && l && r) initResizer(d, l, r);
        });

        // Action bar
        main.appendChild(renderActionBar());

        return main;
    }

    function saveMessageEditors() {
        var editors = $$('.bowire-message-editor');
        if (editors.length > 0) {
            requestMessages = editors.map(function (e) { return e.value; });
        } else {
            var single = $('.bowire-editor');
            if (single && single.value.trim()) {
                requestMessages = [single.value];
            }
        }
    }

    function renderRequestPane() {
        // Save current editor content before re-render
        saveMessageEditors();

        // ID includes the selected method so morphdom fully replaces the
        // pane when switching methods instead of reusing stale DOM with
        // wrong closures/editors from the previous method.
        var reqMethodKey = (selectedService ? selectedService.name : '')
            + '-' + (selectedMethod ? selectedMethod.name : '');
        const pane = el('div', { id: 'bowire-request-pane-' + reqMethodKey, className: 'bowire-pane' });

        // Channel methods use single-message mode (one message at a time)
        const isMultiMessage = selectedMethod && (selectedMethod.clientStreaming === true) && !isChannelMethod();

        // Tabs — every tab button and its paired content pane get a
        // stable id so morphdom uses keyed matching across renders
        // instead of matching by position. Without ids, switching
        // tabs (which swaps the content panes' `.active` class AND
        // rebuilds the body/metadata/schema sub-trees) would make
        // morphdom slide nodes by index and drag listeners onto the
        // wrong targets — the reason a second click on "Request
        // Body" after visiting Metadata couldn't re-activate the
        // body tab (old listener was attached to a node that now
        // represented metadata).
        // Pane heading — anchors the left half visually so the
        // operator doesn't rely on the verbose "Request Body" tab
        // label to know which side they're on. Tabs below can stay
        // terse (Body / Metadata / Schema / Tests / …).
        pane.appendChild(el('div', { className: 'bowire-pane-heading', textContent: 'Request' }));
        const tabs = el('div', { id: 'bowire-request-tabs', className: 'bowire-tabs' });
        // Top-tab label — "Payload" instead of "Body" so the REST/OData
        // sub-tab strip below ("Form / Body") doesn't read as the
        // nonsensical "Body > Body". Multi-message methods keep their
        // count-aware "Messages (N)" label.
        const bodyTabLabel = isMultiMessage ? 'Messages (' + requestMessages.length + ')' : 'Payload';
        const bodyTab = el('div', {
            id: 'bowire-request-tab-body',
            className: `bowire-tab ${activeRequestTab === 'body' ? 'active' : ''}`,
            textContent: bodyTabLabel,
            onClick: function () { activeRequestTab = 'body'; render(); }
        });
        const metaTab = el('div', {
            id: 'bowire-request-tab-metadata',
            className: `bowire-tab ${activeRequestTab === 'metadata' ? 'active' : ''}`,
            textContent: 'Metadata',
            onClick: function () { activeRequestTab = 'metadata'; render(); }
        });
        const schemaTab = el('div', {
            id: 'bowire-request-tab-schema',
            className: `bowire-tab ${activeRequestTab === 'schema' ? 'active' : ''}`,
            textContent: 'Schema',
            onClick: function () { activeRequestTab = 'schema'; render(); }
        });

        // Form/JSON input-mode toggle used to live here as a pair of
        // pill buttons in the request-pane tab strip — always visible,
        // even when the user was on Metadata/Schema/History/Code where
        // it had no effect. Now it's part of the Body sub-tab strip
        // (#85), so the chrome only appears where it's actionable and
        // matches the GraphQL [Query/Variables/Selection set] pattern.
        var hasInputFields = selectedMethod && selectedMethod.inputType && selectedMethod.inputType.fields && selectedMethod.inputType.fields.length > 0;
        const historyTab = el('div', {
            id: 'bowire-request-tab-history',
            className: `bowire-tab ${activeRequestTab === 'history' ? 'active' : ''}`,
            textContent: 'History',
            onClick: function () { activeRequestTab = 'history'; render(); }
        });
        const codeTab = el('div', {
            id: 'bowire-request-tab-code',
            className: `bowire-tab ${activeRequestTab === 'code' ? 'active' : ''}`,
            textContent: 'Code',
            onClick: function () { activeRequestTab = 'code'; render(); }
        });
        const scriptsTab = el('div', {
            id: 'bowire-request-tab-scripts',
            className: `bowire-tab ${activeRequestTab === 'scripts' ? 'active' : ''}`,
            textContent: 'Scripts',
            onClick: function () { activeRequestTab = 'scripts'; render(); }
        });
        // Tests-DEFINITION tab. Lives request-side because assertions
        // are part of the method's configuration (they ride along
        // with collections / recordings / exports). Pass/fail results
        // surface in the response pane's "Test results" tab.
        const testsTab = el('div', {
            id: 'bowire-request-tab-tests',
            className: `bowire-tab ${activeRequestTab === 'tests' ? 'active' : ''}`,
            textContent: 'Tests',
            onClick: function () { activeRequestTab = 'tests'; render(); }
        });
        tabs.appendChild(bodyTab);
        tabs.appendChild(metaTab);
        tabs.appendChild(schemaTab);
        tabs.appendChild(codeTab);
        tabs.appendChild(scriptsTab);
        tabs.appendChild(testsTab);
        tabs.appendChild(historyTab);
        pane.appendChild(tabs);
        // Overflow-popover wiring — when the request pane is narrowed
        // and the seven tabs no longer fit, trailing tabs collapse into
        // a "▾ N" chevron rather than getting clipped off. Deferred so
        // morphdom has mounted the strip into the live tree.
        requestAnimationFrame(function () {
            var live = document.getElementById('bowire-request-tabs');
            if (live && typeof bowireWireTabOverflow === 'function') {
                bowireWireTabOverflow(live, { tabSelector: '.bowire-tab', label: 'More tabs' });
            }
        });

        // Channel status bar (Duplex / Client Streaming)
        if (isChannelMethod()) {
            var statusClass = duplexConnected ? 'bowire-channel-connected' : 'bowire-channel-disconnected';
            var dotClass = duplexConnected ? 'bowire-pulse-dot' : 'bowire-channel-dot-grey';
            var statusText = duplexConnected ? 'Channel Open' : (statusInfo && statusInfo.status === 'Completed' ? 'Completed' : 'Disconnected');
            var channelStatus = el('div', { className: 'bowire-channel-status ' + statusClass },
                el('span', { className: dotClass }),
                el('span', { textContent: statusText })
            );
            if (duplexConnected || sentCount > 0 || receivedCount > 0) {
                var counters = el('div', { className: 'bowire-channel-counters' },
                    el('div', { className: 'bowire-channel-counter' },
                        el('span', { textContent: 'Sent: ' }),
                        el('span', { className: 'bowire-counter-sent', textContent: String(sentCount) })
                    ),
                    el('div', { className: 'bowire-channel-counter' },
                        el('span', { textContent: 'Received: ' }),
                        el('span', { className: 'bowire-counter-received', textContent: String(receivedCount) })
                    )
                );
                channelStatus.appendChild(counters);
            }
            pane.appendChild(channelStatus);
        }

        // Body tab — stable id paired with #bowire-request-tab-body
        // so morphdom keyed-matches this pane and never swaps it with
        // another tab-content pane by position accidentally.
        const bodyContent = el('div', { id: 'bowire-request-tab-body-content', className: `bowire-tab-content ${activeRequestTab === 'body' ? 'active' : ''}` });

        // Sub-tabs within the Body pane (#85). For GraphQL methods the
        // historical layout stacked Selection set + Query + Variables
        // form vertically; that eats screen real-estate when users
        // typically work in one surface at a time. We compute which
        // sub-tabs apply for the current method below, render a strip
        // when ≥ 2 apply (so single-surface protocols don't get
        // useless chrome), and gate each surface's render block on
        // the active sub-tab id.
        var bodySubTabs = [];
        var isGqlMethod = isGraphQLMethod();
        var hasGqlSelectionSet = isGqlMethod && selectedMethod && selectedMethod.outputType
            && selectedMethod.outputType.fields && selectedMethod.outputType.fields.length > 0;
        var hasGqlQuery = isGqlMethod && selectedMethod;
        var hasInputFormFields = selectedMethod && selectedMethod.inputType
            && selectedMethod.inputType.fields && selectedMethod.inputType.fields.length > 0;

        if (isGqlMethod) {
            // Order matters: Query first as the default sub-tab (it's
            // what gets POSTed); Variables next (most-edited secondary);
            // Selection set last (high-level structure).
            if (hasGqlQuery) bodySubTabs.push({ id: 'query', label: 'Query' });
            if (hasInputFormFields) bodySubTabs.push({ id: 'form', label: 'Variables' });
            if (hasGqlSelectionSet) bodySubTabs.push({ id: 'selection', label: 'Selection set' });
        } else if (hasInputFormFields && !isMultiMessage && !isChannelMethod()) {
            // REST / gRPC unary / JSON-RPC / OData / SOAP — the existing
            // Form vs. JSON toggle that lived in the top-pane tab strip
            // (mode-buttons next to Body/Metadata/Schema). Surface it as
            // Body sub-tabs instead so the chrome is consistent with the
            // GraphQL case and the toggle only appears when Body is the
            // active tab. We mirror the click into requestInputMode so
            // every existing code path that branches on it (sync helpers,
            // render gates further down) keeps working.
            // Protocol-specific labels match the conventions of each
            // ecosystem so the sub-tab strip reads native (#85): gRPC
            // calls a request a "Message" (single proto envelope);
            // JSON-RPC calls them "Params" (per the JSON-RPC 2.0 spec);
            // REST / SignalR / WebSocket / OData / SOAP fall back to
            // the generic "Form" / "Body" pair. JSON stays "JSON"
            // everywhere because it's the wire format, not a protocol
            // term.
            var src = selectedService && selectedService.source;
            var formLabel, jsonLabel;
            if (src === 'grpc') {
                formLabel = 'Message';
                jsonLabel = 'JSON';
            } else if (src === 'jsonrpc') {
                formLabel = 'Params';
                jsonLabel = 'JSON';
            } else if (src === 'rest' || src === 'odata') {
                formLabel = 'Form';
                jsonLabel = 'Body';
            } else {
                formLabel = 'Form';
                jsonLabel = 'JSON';
            }
            bodySubTabs.push({ id: 'form', label: formLabel });
            bodySubTabs.push({ id: 'json', label: jsonLabel });
            // Mirror activeBodySubTab → requestInputMode so the lower
            // render gates pick the right surface. The reverse mirror
            // happens in the legacy buttons' onClick (kept for now in
            // case anything still reads them); when those buttons are
            // dropped the mirror becomes one-way.
            if (activeBodySubTab === 'form' || activeBodySubTab === 'json') {
                requestInputMode = activeBodySubTab;
            }
        }

        // If the active sub-tab isn't applicable for this method
        // (switched from a method with a Selection set to one without,
        // for example), snap back to the first applicable tab.
        if (bodySubTabs.length >= 2 && !bodySubTabs.some(function (t) { return t.id === activeBodySubTab; })) {
            activeBodySubTab = bodySubTabs[0].id;
        }

        // Render the sub-tab strip when ≥ 2 sub-tabs apply.
        if (bodySubTabs.length >= 2) {
            var subTabBar = el('div', { id: 'bowire-body-subtabs', className: 'bowire-sub-tabs', role: 'tablist' });
            for (var sti = 0; sti < bodySubTabs.length; sti++) {
                (function (t) {
                    subTabBar.appendChild(el('button', {
                        id: 'bowire-body-subtab-' + t.id,
                        className: 'bowire-sub-tab' + (activeBodySubTab === t.id ? ' active' : ''),
                        role: 'tab',
                        textContent: t.label,
                        onClick: function () {
                            // Form ↔ JSON swap needs the existing sync
                            // helpers so values stay in step. The legacy
                            // top-strip toggle did the same; preserving
                            // it here so the sub-tab is a drop-in.
                            if (activeBodySubTab === 'form' && t.id === 'json') {
                                syncFormToJson();
                            } else if (activeBodySubTab === 'json' && t.id === 'form') {
                                syncJsonToForm();
                            }
                            activeBodySubTab = t.id;
                            if (t.id === 'form' || t.id === 'json') {
                                requestInputMode = t.id;
                            }
                            render();
                        }
                    }));
                })(bodySubTabs[sti]);
            }
            bodyContent.appendChild(subTabBar);
            // Overflow popover — body sub-tab strip (Form / JSON /
            // Selection set / Query / Variables / Headers / …). Mounted
            // on each render; helper is idempotent.
            requestAnimationFrame(function () {
                var live = document.getElementById('bowire-body-subtabs');
                if (live && typeof bowireWireTabOverflow === 'function') {
                    bowireWireTabOverflow(live, { tabSelector: '.bowire-sub-tab', label: 'More' });
                }
            });
        }

        // showSurface(name) is the gate for every surface block below.
        // Returns true when (a) there's no sub-tab strip (single
        // surface — render unconditionally), or (b) the active sub-tab
        // matches the requested surface. Keeps the existing per-protocol
        // render blocks intact — we only wrap them in the gate.
        function showSurface(name) {
            return bodySubTabs.length < 2 || activeBodySubTab === name;
        }

        // GraphQL: Selection-set picker pane above the query editor.
        // Renders a checkbox tree of the discovered output type. Toggling
        // a field updates graphqlSelections and re-renders so the query
        // editor below picks up the new selection automatically (provided
        // the user hasn't manually overridden the query — in that case the
        // override wins, which is a deliberate "manual edit > picker"
        // ordering).
        if (hasGqlSelectionSet && showSurface('selection')) {
            var selKey = graphqlMethodKey();
            var sels = getGraphQLSelections();

            var selHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Selection set' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-graphql-select-all-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Select all (top-level)',
                        onClick: function () {
                            for (var i = 0; i < selectedMethod.outputType.fields.length; i++) {
                                var f = selectedMethod.outputType.fields[i];
                                setGraphQLSelection(f.name, true);
                            }
                            // Drop any user query override so the new
                            // selection takes effect on the next render.
                            delete graphqlQueryOverrides[selKey];
                            render();
                        }
                    }),
                    el('button', {
                        id: 'bowire-graphql-clear-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Clear',
                        onClick: function () {
                            graphqlSelections[selKey] = {};
                            delete graphqlQueryOverrides[selKey];
                            render();
                        }
                    })
                )
            );

            var selTree = renderGraphQLSelectionTree(selectedMethod.outputType, '', sels, 0);
            var selBox = el('div', { className: 'bowire-graphql-selection-pane' }, selHeader, selTree);
            bodyContent.appendChild(selBox);
        }

        // GraphQL: Query editor pane above the variables form/JSON. Lets the
        // user override the auto-generated operation string with their own
        // selection set. Stored per-method in graphqlQueryOverrides — when
        // the user clicks "Reset to default" we drop the override and the
        // pane regenerates from the discovered method shape.
        if (hasGqlQuery && showSurface('query')) {
            var queryKey = graphqlMethodKey();
            var queryHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'GraphQL Query' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-graphql-reset-query-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Reset to default',
                        onClick: function () {
                            delete graphqlQueryOverrides[queryKey];
                            render();
                        }
                    })
                )
            );
            var queryEditor = el('textarea', {
                className: 'bowire-graphql-query-editor',
                spellcheck: 'false',
                placeholder: 'GraphQL operation — edit to change the selection set'
            });
            queryEditor.value = getGraphQLQuery();
            queryEditor.addEventListener('input', function () {
                graphqlQueryOverrides[queryKey] = this.value;
            });
            queryEditor.addEventListener('keydown', function (e) {
                if (e.key === 'Tab') {
                    e.preventDefault();
                    var s = this.selectionStart, en = this.selectionEnd;
                    this.value = this.value.substring(0, s) + '  ' + this.value.substring(en);
                    this.selectionStart = this.selectionEnd = s + 2;
                    graphqlQueryOverrides[queryKey] = this.value;
                }
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    handleExecute();
                }
            });
            var queryBox = el('div', { className: 'bowire-graphql-query-pane' }, queryHeader, queryEditor);
            bodyContent.appendChild(queryBox);
        }

        // WebSocket: outgoing frame type toggle + binary file upload. Sits
        // above the message editor so the user can pick text vs binary and
        // optionally drop a file in before clicking Send.
        if (isWebSocketMethod() && isChannelMethod()) {
            var wsHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Frame type' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('div', { className: 'bowire-toggle-group' },
                        el('button', {
                            id: 'bowire-ws-frame-text-btn',
                            className: 'bowire-toggle-btn' + (websocketFrameType === 'text' ? ' is-active' : ''),
                            textContent: 'Text',
                            onClick: function () { websocketFrameType = 'text'; websocketPendingBinary = null; render(); }
                        }),
                        el('button', {
                            id: 'bowire-ws-frame-binary-btn',
                            className: 'bowire-toggle-btn' + (websocketFrameType === 'binary' ? ' is-active' : ''),
                            textContent: 'Binary',
                            onClick: function () { websocketFrameType = 'binary'; render(); }
                        })
                    )
                )
            );
            var wsBox = el('div', { className: 'bowire-ws-frame-pane' }, wsHeader);

            if (websocketFrameType === 'binary') {
                var fileInput = el('input', { type: 'file', className: 'bowire-ws-file-input' });
                fileInput.addEventListener('change', function () {
                    var file = this.files && this.files[0];
                    if (!file) return;
                    var reader = new FileReader();
                    reader.onload = function () {
                        // reader.result is a data URL: "data:...;base64,XXXX"
                        var url = reader.result || '';
                        var commaIdx = url.indexOf(',');
                        websocketPendingBinary = commaIdx >= 0 ? url.substring(commaIdx + 1) : '';
                        toast('Loaded ' + file.name + ' (' + file.size + ' bytes) — click Send to transmit', 'success');
                        render();
                    };
                    reader.readAsDataURL(file);
                });
                var fileRow = el('div', { className: 'bowire-ws-file-row' },
                    el('label', { className: 'bowire-ws-file-label', textContent: 'Pick a file → Send sends it as a single binary frame' }),
                    fileInput
                );
                wsBox.appendChild(fileRow);

                if (websocketPendingBinary) {
                    wsBox.appendChild(el('div', { className: 'bowire-ws-pending', textContent:
                        '\u2713 Binary payload ready (' + websocketPendingBinary.length + ' base64 chars)' }));
                }
            }

            bodyContent.appendChild(wsBox);
        }

        // For GraphQL methods the form/JSON editor surface lives behind
        // the 'form' sub-tab. Skip the whole block (multi-message,
        // form, JSON variants) when GraphQL is active and the user is
        // on a different sub-tab — that surface re-renders when they
        // switch back. Non-GraphQL methods fall through unchanged so
        // REST/gRPC/JSON-RPC keep their existing single-surface layout.
        var skipFormSurface = isGqlMethod && bodySubTabs.length >= 2 && activeBodySubTab !== 'form';
        if (skipFormSurface) {
            // Intentional no-op — sub-tab gate excludes this surface.
        } else if (isMultiMessage) {
            // Multi-message mode: header with Format All + Template All
            const paneHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'JSON Messages' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-multi-msg-format-all-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Format All',
                        onClick: function () {
                            var editors = $$('.bowire-message-editor');
                            for (var i = 0; i < editors.length; i++) {
                                editors[i].value = formatJson(editors[i].value);
                                requestMessages[i] = editors[i].value;
                            }
                        }
                    }),
                    el('button', {
                        id: 'bowire-multi-msg-template-all-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Template All',
                        onClick: function () {
                            var editors = $$('.bowire-message-editor');
                            var tmpl = selectedMethod ? generateDefaultJson(selectedMethod.inputType, 0) : '{}';
                            for (var i = 0; i < editors.length; i++) {
                                editors[i].value = tmpl;
                                requestMessages[i] = tmpl;
                            }
                        }
                    })
                )
            );
            bodyContent.appendChild(paneHeader);

            // Message list container
            var messageList = el('div', { className: 'bowire-message-list' });

            for (var idx = 0; idx < requestMessages.length; idx++) {
                (function (i) {
                    var msgItem = el('div', { className: 'bowire-message-item' });

                    // Message header with number and remove button
                    var msgHeader = el('div', { className: 'bowire-message-header' },
                        el('span', { textContent: 'Message #' + (i + 1) })
                    );
                    if (requestMessages.length > 1) {
                        msgHeader.appendChild(el('button', {
                            id: 'bowire-msg-remove-' + i,
                            className: 'bowire-message-remove',
                            textContent: '\u00d7',
                            title: 'Remove message',
                            onClick: function () {
                                saveMessageEditors();
                                requestMessages.splice(i, 1);
                                render();
                            }
                        }));
                    }
                    msgItem.appendChild(msgHeader);

                    // Message editor textarea
                    var msgEditor = el('textarea', {
                        className: 'bowire-message-editor',
                        placeholder: 'Enter JSON message...',
                        spellcheck: 'false'
                    });
                    msgEditor.value = requestMessages[i] || '{}';
                    msgEditor.addEventListener('input', function () {
                        requestMessages[i] = this.value;
                    });
                    msgEditor.addEventListener('keydown', function (e) {
                        if (e.key === 'Tab') {
                            e.preventDefault();
                            var start = this.selectionStart;
                            var end = this.selectionEnd;
                            this.value = this.value.substring(0, start) + '  ' + this.value.substring(end);
                            this.selectionStart = this.selectionEnd = start + 2;
                            requestMessages[i] = this.value;
                        }
                        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                            e.preventDefault();
                            handleExecute();
                        }
                    });
                    msgItem.appendChild(msgEditor);
                    // Schema-based autocomplete for streaming message editors
                    if (selectedMethod && selectedMethod.inputType) {
                        attachBodyAutocomplete(msgEditor, selectedMethod.inputType);
                    }
                    // Per-message validation status — same component as
                    // the single-editor variant so a multi-message
                    // streaming send pinpoints which message is broken
                    // instead of failing the whole batch silently.
                    var msgJsonStatus = el('div', { className: 'bowire-json-status empty' });
                    msgItem.appendChild(msgJsonStatus);
                    attachJsonValidator(msgEditor, msgJsonStatus);
                    messageList.appendChild(msgItem);
                })(idx);
            }

            // Add Message button
            messageList.appendChild(el('button', {
                id: 'bowire-add-message-btn',
                className: 'bowire-add-message',
                onClick: function () {
                    saveMessageEditors();
                    var tmpl = selectedMethod ? generateDefaultJson(selectedMethod.inputType, 0) : '{}';
                    requestMessages.push(tmpl);
                    render();
                }
            },
                el('span', { textContent: '+' }),
                el('span', { textContent: 'Add Message' })
            ));

            bodyContent.appendChild(messageList);
        } else if (requestInputMode === 'form' && hasInputFields) {
            // Single-message mode: Form view
            const paneHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Form' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-form-reset-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Reset',
                        onClick: function () {
                            // Drop both the live form values and the
                            // cached per-method state so a return to
                            // this method starts from a clean default.
                            formValues = {};
                            if (selectedService && selectedMethod) {
                                clearMethodState(selectedService.name, selectedMethod.name);
                            }
                            render();
                        }
                    })
                )
            );
            bodyContent.appendChild(paneHeader);
            const body = el('div', { className: 'bowire-pane-body' });
            body.appendChild(renderFormFields(selectedMethod.inputType, '', 0));
            bodyContent.appendChild(body);
        } else {
            // Single-message mode: JSON editor (Unary / Server Streaming)
            // JSON validation pill — used to live UNDER the editor where
            // a long payload pushed it below the fold. Now sits in the
            // pane header so the status is always visible while editing.
            // Operator: 'Valid JSON müsste eigentlich eine pill weiter
            // oben in der payload toolbar sein, nicht unten nach dem
            // json, wo man durch scroll das ergebnis eventuell nicht
            // sieht.'
            const jsonStatus = el('div', { className: 'bowire-json-status bowire-json-status-pill empty' });
            const paneHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'JSON' }),
                jsonStatus,
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-json-format-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Format',
                        onClick: function () {
                            var ed = $('.bowire-editor');
                            if (ed) { ed.value = formatJson(ed.value); requestMessages[0] = ed.value; }
                        }
                    }),
                    el('button', {
                        id: 'bowire-json-template-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Template',
                        onClick: function () {
                            var ed = $('.bowire-editor');
                            if (ed && selectedMethod) {
                                ed.value = generateDefaultJson(selectedMethod.inputType, 0);
                                requestMessages[0] = ed.value;
                            }
                        }
                    }),
                    el('button', {
                        id: 'bowire-json-import-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Import',
                        title: 'Import JSON from file (or drag & drop)',
                        onClick: function () {
                            var input = document.createElement('input');
                            input.type = 'file';
                            input.accept = '.json,application/json';
                            input.onchange = function () {
                                var file = input.files[0];
                                if (!file) return;
                                var reader = new FileReader();
                                reader.onload = function (ev) {
                                    var text = ev.target.result;
                                    try { text = JSON.stringify(JSON.parse(text), null, 2); } catch {}
                                    var ed = $('.bowire-editor');
                                    if (ed) { ed.value = text; requestMessages[0] = text; }
                                    toast('Imported ' + file.name, 'success');
                                };
                                reader.readAsText(file);
                            };
                            input.click();
                        }
                    }),
                    el('button', {
                        id: 'bowire-json-grpcurl-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'grpcurl',
                        title: 'Copy request as grpcurl command',
                        onClick: function () {
                            if (!selectedService || !selectedMethod) return;
                            var body = requestMessages[0] || '{}';
                            var target = (selectedService && selectedService.originUrl) || getPrimaryServerUrl() || 'localhost:5001';
                            var plaintext = target.startsWith('http://');
                            var host = target.replace(/^https?:\/\//, '');
                            var cmd = 'grpcurl';
                            if (plaintext) cmd += ' -plaintext';
                            try { cmd += " -d '" + JSON.stringify(JSON.parse(body)).replace(/'/g, "'\\''") + "'"; }
                            catch { cmd += " -d '" + body.replace(/\n/g, ' ').replace(/'/g, "'\\''") + "'"; }
                            cmd += ' ' + host + ' ' + selectedService.name + '/' + selectedMethod.name;
                            navigator.clipboard.writeText(cmd).then(function () { toast('Copied grpcurl command', 'success'); });
                        }
                    })
                )
            );
            bodyContent.appendChild(paneHeader);
            const body = el('div', { className: 'bowire-pane-body' });
            const defaultJson = selectedMethod ? generateDefaultJson(selectedMethod.inputType, 0) : '{}';
            const editor = el('textarea', {
                className: 'bowire-editor',
                placeholder: 'Enter JSON request body...',
                spellcheck: 'false'
            });
            editor.value = (requestMessages[0] && requestMessages[0].trim()) ? requestMessages[0] : defaultJson;
            editor.addEventListener('input', function () {
                requestMessages[0] = this.value;
            });
            editor.addEventListener('keydown', function (e) {
                if (e.key === 'Tab') {
                    e.preventDefault();
                    const start = this.selectionStart;
                    const end = this.selectionEnd;
                    this.value = this.value.substring(0, start) + '  ' + this.value.substring(end);
                    this.selectionStart = this.selectionEnd = start + 2;
                    requestMessages[0] = this.value;
                }
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    handleExecute();
                }
            });
            // Drag & Drop JSON file onto editor
            editor.addEventListener('dragover', function (e) {
                e.preventDefault();
                e.stopPropagation();
                editor.classList.add('bowire-editor-dragover');
            });
            editor.addEventListener('dragleave', function (e) {
                e.preventDefault();
                editor.classList.remove('bowire-editor-dragover');
            });
            editor.addEventListener('drop', function (e) {
                e.preventDefault();
                e.stopPropagation();
                editor.classList.remove('bowire-editor-dragover');
                var file = e.dataTransfer.files[0];
                if (file) {
                    var reader = new FileReader();
                    reader.onload = function (ev) {
                        var text = ev.target.result;
                        try { text = JSON.stringify(JSON.parse(text), null, 2); } catch {}
                        editor.value = text;
                        requestMessages[0] = text;
                        toast('Imported ' + file.name, 'success');
                    };
                    reader.readAsText(file);
                }
            });

            body.appendChild(editor);
            // Schema-based autocomplete for field names in the JSON editor
            if (selectedMethod && selectedMethod.inputType) {
                attachBodyAutocomplete(editor, selectedMethod.inputType);
            }
            // jsonStatus pill already mounted in the pane header above —
            // attach the validator now that the editor exists. Keeping
            // it in the header means a tall payload doesn't push the
            // status off-screen.
            attachJsonValidator(editor, jsonStatus);
            bodyContent.appendChild(body);
        }
        pane.appendChild(bodyContent);

        // Metadata tab
        const metaContent = el('div', { id: 'bowire-request-tab-metadata-content', className: `bowire-tab-content ${activeRequestTab === 'metadata' ? 'active' : ''}` });
        const metaEditor = el('div', { className: 'bowire-metadata-editor' });

        // Preserve existing metadata rows
        const existingRows = $$('.bowire-metadata-row');
        if (existingRows.length > 0) {
            for (const row of existingRows) {
                const inputs = row.querySelectorAll('.bowire-metadata-input');
                if (inputs.length === 2) {
                    metaEditor.appendChild(createMetadataRow(inputs[0].value, inputs[1].value));
                }
            }
        } else {
            metaEditor.appendChild(createMetadataRow('', ''));
        }

        metaEditor.appendChild(el('button', {
            id: 'bowire-metadata-add-btn',
            className: 'bowire-metadata-add',
            textContent: '+ Add header',
            onClick: function () {
                this.parentElement.insertBefore(createMetadataRow('', ''), this);
            }
        }));
        metaContent.appendChild(metaEditor);
        pane.appendChild(metaContent);

        // Schema tab
        const schemaContent = el('div', { id: 'bowire-request-tab-schema-content', className: `bowire-tab-content ${activeRequestTab === 'schema' ? 'active' : ''}` });
        const schemaDiv = el('div', { className: 'bowire-schema' });

        if (selectedMethod) {
            // Input type
            const inputSection = el('div', { className: 'bowire-schema-section' });
            inputSection.appendChild(el('div', { className: 'bowire-schema-title' },
                el('span', { textContent: 'Input' }),
                el('span', { className: 'bowire-schema-type-name', textContent: selectedMethod.inputType.fullName })
            ));
            const inputFields = renderSchemaFields(selectedMethod.inputType.fields, 0);
            if (inputFields) inputSection.appendChild(inputFields);
            else inputSection.appendChild(el('div', { className: 'bowire-schema-field', textContent: 'No fields (empty message)' }));
            schemaDiv.appendChild(inputSection);

            // Output type
            const outputSection = el('div', { className: 'bowire-schema-section' });
            outputSection.appendChild(el('div', { className: 'bowire-schema-title' },
                el('span', { textContent: 'Output' }),
                el('span', { className: 'bowire-schema-type-name', textContent: selectedMethod.outputType.fullName })
            ));
            const outputFields = renderSchemaFields(selectedMethod.outputType.fields, 0);
            if (outputFields) outputSection.appendChild(outputFields);
            else outputSection.appendChild(el('div', { className: 'bowire-schema-field', textContent: 'No fields (empty message)' }));
            schemaDiv.appendChild(outputSection);
        }

        schemaContent.appendChild(schemaDiv);
        pane.appendChild(schemaContent);

        // History tab
        const historyContent = el('div', { id: 'bowire-request-tab-history-content', className: `bowire-tab-content ${activeRequestTab === 'history' ? 'active' : ''}` });
        const allHistory = getHistory();
        const methodFiltered = (selectedMethod && !showAllHistory)
            ? allHistory.filter(function (h) { return h.service === selectedService.name && h.method === selectedMethod.name; })
            : allHistory;
        const filtered = filterHistoryEntries(methodFiltered);

        if (allHistory.length === 0) {
            historyContent.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                el('div', { className: 'bowire-empty-desc', textContent: 'No request history yet. Execute a request to see it here.' })
            ));
        } else {
            // Search + status filter bar — shown whenever there is any
            // history at all, so users can scan failures or grep across
            // every recorded request.
            const searchBar = el('div', { className: 'bowire-history-search-bar' });

            const searchInput = el('input', {
                className: 'bowire-history-search-input',
                type: 'search',
                placeholder: 'Search history (service, method, body, status)…'
            });
            searchInput.value = historySearchQuery;
            searchInput.addEventListener('input', function () {
                historySearchQuery = this.value;
                // Don't full-render on every keystroke — only re-render the
                // history list to keep typing snappy. Easiest path: render()
                // recomputes the whole pane, which is acceptable here because
                // the input has its own DOM identity preserved by Bowire's
                // diff-free rerender. We re-focus to prevent the cursor jump.
                var pos = this.selectionStart;
                render();
                requestAnimationFrame(function () {
                    var fresh = document.querySelector('.bowire-history-search-input');
                    if (fresh) {
                        fresh.focus();
                        try { fresh.setSelectionRange(pos, pos); } catch (e) { /* ignore */ }
                    }
                });
            });
            searchBar.appendChild(searchInput);

            const filterPills = el('div', { className: 'bowire-history-filter-pills' });
            const buckets = [
                { id: 'all', label: 'All' },
                { id: 'ok', label: 'OK' },
                { id: 'error', label: 'Errors' }
            ];
            for (var bi = 0; bi < buckets.length; bi++) {
                (function (bucket) {
                    filterPills.appendChild(el('button', {
                        id: 'bowire-history-filter-' + bucket.id,
                        className: 'bowire-history-filter-pill' + (historyStatusFilter === bucket.id ? ' active' : ''),
                        textContent: bucket.label,
                        onClick: function () {
                            historyStatusFilter = bucket.id;
                            render();
                        }
                    }));
                })(buckets[bi]);
            }
            searchBar.appendChild(filterPills);

            historyContent.appendChild(searchBar);

            // Filter info bar
            if (selectedMethod && !showAllHistory) {
                const filterInfo = el('div', { className: 'bowire-history-filter-info' },
                    el('span', { textContent: filtered.length + ' call' + (filtered.length !== 1 ? 's' : '') + ' for ' + selectedMethod.name + ' (' + allHistory.length + ' total)' }),
                    el('button', {
                        id: 'bowire-history-show-all-btn',
                        className: 'bowire-history-show-all',
                        textContent: 'Show all',
                        onClick: function () { showAllHistory = true; render(); }
                    })
                );
                historyContent.appendChild(filterInfo);
            } else if (showAllHistory && selectedMethod) {
                const filterInfo = el('div', { className: 'bowire-history-filter-info' },
                    el('span', { textContent: allHistory.length + ' total calls' }),
                    el('button', {
                        id: 'bowire-history-filter-method-btn',
                        className: 'bowire-history-show-all',
                        textContent: 'Filter to ' + selectedMethod.name,
                        onClick: function () { showAllHistory = false; render(); }
                    })
                );
                historyContent.appendChild(filterInfo);
            }

            if (filtered.length === 0) {
                var emptyText = (historySearchQuery || historyStatusFilter !== 'all')
                    ? 'No history matches the current search / filter.'
                    : 'No history for this method yet.';
                historyContent.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                    el('div', { className: 'bowire-empty-desc', textContent: emptyText })
                ));
            } else {
                const historyList = el('div', { className: 'bowire-history-list bowire-history-timeline' });
                var prevBody = null;
                for (const h of filtered) {
                    const methodName = h.method || 'Unknown';
                    const statusColor = grpcStatusClass(h.status);
                    const badgeType = h.methodType === 'Unary' ? 'unary' : h.methodType === 'ServerStreaming' ? 'server' : h.methodType === 'ClientStreaming' ? 'client' : 'duplex';
                    const ago = h.timestamp ? timeAgo(h.timestamp) : '';

                    // Build body preview
                    var bodyPreview = '';
                    if (h.body && !h.body.startsWith('(channel:')) {
                        try {
                            const parsed = JSON.parse(h.body);
                            bodyPreview = JSON.stringify(parsed);
                            if (bodyPreview.length > 80) bodyPreview = bodyPreview.substring(0, 77) + '...';
                        } catch {
                            bodyPreview = h.body.substring(0, 80);
                        }
                    } else if (h.body) {
                        bodyPreview = h.body;
                    }

                    // Resolve the entry FRESH at click time via the
                    // (timestamp, service, method) tuple stored on the
                    // node — closure capture of `h` is unsafe because
                    // morphdom preserves the previous DOM node across
                    // re-renders (the row is recreated, but the diff
                    // keeps the old element with its stale handler).
                    // See feedback_morphdom_stale_handler_pitfall.
                    const replayBtn = el('button', {
                        className: 'bowire-history-replay',
                        title: 'Replay this request',
                        'aria-label': 'Replay this request',
                        innerHTML: svgIcon('play'),
                        'data-replay-ts': String(h.timestamp || 0),
                        'data-replay-svc': h.service || '',
                        'data-replay-mth': h.method || '',
                        onClick: function (e) {
                            e.stopPropagation();
                            var btn = e.currentTarget;
                            var ts = parseInt(btn.getAttribute('data-replay-ts'), 10) || 0;
                            var svc = btn.getAttribute('data-replay-svc');
                            var mth = btn.getAttribute('data-replay-mth');
                            var fresh = (typeof getHistory === 'function')
                                ? getHistory().find(function (x) {
                                    return x && x.timestamp === ts
                                        && x.service === svc
                                        && x.method === mth;
                                })
                                : null;
                            if (fresh) replayHistoryEntry(fresh);
                        }
                    });

                    const itemContent = el('div', { className: 'bowire-history-item-content' },
                        el('div', { className: 'bowire-history-item-row' },
                            el('span', {
                                className: 'bowire-history-badge',
                                style: 'background: var(--bowire-badge-' + badgeType + ')',
                                textContent: shortType(h.methodType || 'Unary')
                            }),
                            el('span', { className: 'bowire-history-method', textContent: h.service?.split('.').pop() + '.' + methodName }),
                            el('span', { className: `bowire-history-status bowire-status-dot ${statusColor}` }),
                            el('span', { className: 'bowire-history-time', textContent: (h.durationMs || 0) + 'ms' }),
                            el('span', { className: 'bowire-history-ago', textContent: ago }),
                            replayBtn
                        )
                    );

                    if (bodyPreview && bodyPreview !== '{}') {
                        itemContent.appendChild(el('div', { className: 'bowire-history-body-preview', textContent: bodyPreview }));
                    }

                    // Diff badge: show when body changed from previous entry
                    var bodyChanged = prevBody !== null && bodyPreview !== prevBody;
                    if (bodyChanged) {
                        itemContent.appendChild(el('span', {
                            className: 'bowire-history-diff-badge',
                            title: 'Request body changed from previous call',
                            textContent: '\u0394 body changed'
                        }));
                    }
                    prevBody = bodyPreview;

                    const item = el('div', {
                        className: 'bowire-history-item',
                        'data-entry-ts': String(h.timestamp || 0),
                        'data-entry-svc': h.service || '',
                        'data-entry-mth': h.method || '',
                        onClick: function (clickEvt) {
                            // Same stale-closure caveat as the replay
                            // button — resolve via dom attributes so a
                            // morphdom-preserved row picks up the
                            // current entry, not the one captured when
                            // this node was first inserted.
                            var node = clickEvt.currentTarget;
                            var ts = parseInt(node.getAttribute('data-entry-ts'), 10) || 0;
                            var fSvc = node.getAttribute('data-entry-svc');
                            var fMth = node.getAttribute('data-entry-mth');
                            var fresh = (typeof getHistory === 'function')
                                ? getHistory().find(function (x) {
                                    return x && x.timestamp === ts
                                        && x.service === fSvc
                                        && x.method === fMth;
                                })
                                : null;
                            if (!fresh) return;
                            for (const svc of services) {
                                for (const m of svc.methods) {
                                    if (svc.name === fresh.service && m.name === fresh.method) {
                                        openTab(svc, m);
                                        activeRequestTab = 'body';
                                        if (fresh.messages && fresh.messages.length > 0) {
                                            requestMessages = fresh.messages.slice();
                                        } else if (fresh.body && !fresh.body.startsWith('(channel:')) {
                                            requestMessages = [fresh.body];
                                        }
                                        requestInputMode = 'json';
                                        render();
                                        return;
                                    }
                                }
                            }
                        }
                    }, itemContent);
                    historyList.appendChild(item);
                }
                historyContent.appendChild(historyList);
            }

            historyContent.appendChild(el('button', {
                id: 'bowire-history-clear-btn',
                className: 'bowire-history-clear',
                textContent: 'Clear history',
                onClick: clearHistory
            }));
        }
        pane.appendChild(historyContent);

        // Code tab — generate request snippets in C# / Python / curl /
        // grpcurl / wscat / fetch based on the current method's protocol.
        const codeContent = el('div', { id: 'bowire-request-tab-code-content', className: `bowire-tab-content ${activeRequestTab === 'code' ? 'active' : ''}` });
        if (selectedMethod) {
            var availLangs = getCodeExportLanguages();
            var activeLang = resolveCodeExportLang();
            if (activeLang) codeExportLang = activeLang;

            var langPills = el('div', { className: 'bowire-code-lang-pills' });
            for (var li = 0; li < availLangs.length; li++) {
                (function (lang) {
                    langPills.appendChild(el('button', {
                        id: 'bowire-code-lang-' + lang.id,
                        className: 'bowire-code-lang-pill' + (codeExportLang === lang.id ? ' active' : ''),
                        textContent: lang.label,
                        onClick: function () { codeExportLang = lang.id; render(); }
                    }));
                })(availLangs[li]);
            }

            var snippet = generateCodeSnippet(codeExportLang);
            var codeBox = el('pre', { className: 'bowire-code-snippet' });
            codeBox.textContent = snippet;

            var copyBtn = el('button', {
                className: 'bowire-pane-btn',
                textContent: 'Copy',
                onClick: function () {
                    navigator.clipboard.writeText(snippet).then(function () {
                        toast('Snippet copied to clipboard', 'success');
                    });
                }
            });

            var codeHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Generated snippet' }),
                el('div', { className: 'bowire-pane-actions' }, copyBtn)
            );

            codeContent.appendChild(codeHeader);
            codeContent.appendChild(langPills);
            codeContent.appendChild(codeBox);
            codeContent.appendChild(el('div', {
                className: 'bowire-code-hint',
                textContent: 'The snippet uses the current request body, metadata, and the active environment\u2019s {{var}} substitutions. Re-generates whenever you switch language or edit the form.'
            }));
        }
        pane.appendChild(codeContent);

        // Scripts tab — per-method pre-request / post-response scripts
        // #126 — typed `ctx.*` sandbox. Editors are collapsible so the
        // tab stays scan-able when only one phase is populated;
        // protocol-aware placeholders + inline lint warnings + a
        // per-phase Console panel + a "Generate from intent" AI
        // button round out the surface.
        const scriptsContent = el('div', { id: 'bowire-request-tab-scripts-content', className: `bowire-tab-content ${activeRequestTab === 'scripts' ? 'active' : ''}` });
        if (selectedMethod && selectedService) {
            scriptsContent.appendChild(renderScriptsTab());
        }
        pane.appendChild(scriptsContent);

        // Tests-definition tab content. The editor (renderTestDefinitionsTab)
        // lets the operator manage the assertion list; results show
        // up in the response pane's "Test results" tab.
        const testsContent = el('div', {
            id: 'bowire-request-tab-tests-content',
            className: `bowire-tab-content ${activeRequestTab === 'tests' ? 'active' : ''}`
        });
        if (selectedMethod) {
            testsContent.appendChild(typeof renderTestDefinitionsTab === 'function'
                ? renderTestDefinitionsTab()
                : el('div', { className: 'bowire-empty-state', textContent: 'Tests not available.' }));
        }
        pane.appendChild(testsContent);

        return pane;
    }

    // #126 — Scripts tab UI. Two collapsible editors (Pre-request /
    // Post-response) plus a shared Console panel that surfaces
    // ctx.log() output and the last script error. Each editor has
    // an "AI generate" button that routes through the existing
    // schema-grounded chat endpoint (Phase C — falls back to a
    // friendly hint when the AI host isn't configured).
    //
    // Collapse state lives in module scope (not localStorage) —
    // it's a per-session preference, not project content.
    var _scriptEditorCollapsed = { pre: false, post: false, console: false };

    function renderScriptsTab() {
        var wrap = el('div', { className: 'bowire-scripts-wrap' });
        var svc = selectedService.name;
        var mth = selectedMethod.name;
        var scriptsState = getMethodScripts(svc, mth);
        var shape = (typeof detectScriptProtocolShape === 'function')
            ? detectScriptProtocolShape(selectedService.source, selectedProtocol)
            : 'rest';

        // Shape-aware placeholder + hint so the operator immediately
        // sees the protocol's typed surface for this method.
        var preExample = _scriptExampleFor(shape, 'pre');
        var postExample = _scriptExampleFor(shape, 'post');
        var preHintText = _scriptHintFor(shape, 'pre');
        var postHintText = _scriptHintFor(shape, 'post');

        // Protocol pill — the workbench has a million ways to surface
        // protocol, but for the Scripts tab a small inline pill keeps
        // the connection visible while editing.
        var protoPill = el('div', { className: 'bowire-script-proto-pill', textContent: shape.toUpperCase() + ' surface' });
        var header = el('div', { className: 'bowire-script-tab-header' },
            el('span', { className: 'bowire-script-tab-title', textContent: 'Scripts' }),
            protoPill
        );
        wrap.appendChild(header);

        wrap.appendChild(_renderScriptEditorBlock({
            phase: 'pre',
            label: 'Pre-request',
            hint: preHintText,
            placeholder: preExample,
            value: scriptsState.preScript,
            shape: shape,
            svc: svc,
            mth: mth
        }));
        wrap.appendChild(_renderScriptEditorBlock({
            phase: 'post',
            label: 'Post-response',
            hint: postHintText,
            placeholder: postExample,
            value: scriptsState.postScript,
            shape: shape,
            svc: svc,
            mth: mth
        }));
        wrap.appendChild(_renderScriptConsoleBlock(svc, mth));
        return wrap;
    }

    function _renderScriptEditorBlock(opts) {
        var collapsed = !!_scriptEditorCollapsed[opts.phase];
        var block = el('div', { className: 'bowire-script-block bowire-script-block-' + opts.phase });
        var caret = el('span', {
            className: 'bowire-script-caret' + (collapsed ? ' collapsed' : ''),
            textContent: collapsed ? '▸' : '▾'
        });
        var labelEl = el('span', { className: 'bowire-script-block-label', textContent: opts.label });
        var indicator = el('span', { className: 'bowire-script-block-state' });
        var hasContent = !!(opts.value && opts.value.trim());
        if (hasContent) {
            indicator.classList.add('active');
            indicator.textContent = 'on';
        } else {
            indicator.textContent = 'empty';
        }
        var head = el('div', {
            className: 'bowire-script-block-head',
            onClick: function () {
                _scriptEditorCollapsed[opts.phase] = !_scriptEditorCollapsed[opts.phase];
                render();
            }
        }, caret, labelEl, indicator);
        block.appendChild(head);
        if (collapsed) return block;

        var hint = el('div', { className: 'bowire-script-hint', textContent: opts.hint });
        var area = el('textarea', {
            id: 'bowire-script-' + opts.phase,
            className: 'bowire-script-editor',
            placeholder: opts.placeholder,
            value: opts.value || '',
            spellcheck: false
        });
        area.addEventListener('input', function () {
            setMethodScript(opts.svc, opts.mth, opts.phase === 'pre' ? 'preScript' : 'postScript', area.value);
            // Re-lint on every keystroke. Bucket-debounce would be
            // nicer but the source is short enough that the regex
            // scan is sub-millisecond.
            _updateLintWarnings(block, area.value, opts.shape);
        });
        block.appendChild(hint);
        block.appendChild(area);

        // Lint warnings — protocol-typed surface check.
        var warnHost = el('div', { className: 'bowire-script-warnings' });
        block.appendChild(warnHost);
        _updateLintWarnings(block, opts.value, opts.shape);

        // Tools row — Generate from intent + Clear + Format.
        var toolsRow = el('div', { className: 'bowire-script-tools' });
        var genBtn = el('button', {
            className: 'bowire-script-gen-btn',
            type: 'button',
            textContent: 'Generate from intent',
            title: 'Describe what the script should do; the assistant emits a runnable script for this protocol shape.',
            onClick: function () { _openScriptGenPrompt(opts.phase, opts.shape, opts.svc, opts.mth, area); }
        });
        var clearBtn = el('button', {
            className: 'bowire-script-tool-btn',
            type: 'button',
            textContent: 'Clear',
            onClick: function () {
                if (!area.value) return;
                area.value = '';
                setMethodScript(opts.svc, opts.mth, opts.phase === 'pre' ? 'preScript' : 'postScript', '');
                render();
            }
        });
        toolsRow.appendChild(genBtn);
        toolsRow.appendChild(clearBtn);
        block.appendChild(toolsRow);
        return block;
    }

    function _updateLintWarnings(blockEl, source, shape) {
        var host = blockEl.querySelector('.bowire-script-warnings');
        if (!host) return;
        host.innerHTML = '';
        if (typeof lintScriptForShape !== 'function') return;
        var warnings = lintScriptForShape(source, shape);
        for (var i = 0; i < warnings.length; i++) {
            host.appendChild(el('div', {
                className: 'bowire-script-warning',
                textContent: warnings[i].member + ' — ' + warnings[i].hint
            }));
        }
    }

    function _renderScriptConsoleBlock(svc, mth) {
        var entries = (typeof getScriptConsole === 'function')
            ? getScriptConsole(svc, mth) : [];
        var collapsed = !!_scriptEditorCollapsed.console;
        var block = el('div', { className: 'bowire-script-block bowire-script-block-console' });
        var caret = el('span', {
            className: 'bowire-script-caret' + (collapsed ? ' collapsed' : ''),
            textContent: collapsed ? '▸' : '▾'
        });
        var labelEl = el('span', { className: 'bowire-script-block-label', textContent: 'Console' });
        var count = el('span', { className: 'bowire-script-block-state', textContent: String(entries.length) });
        var head = el('div', {
            className: 'bowire-script-block-head',
            onClick: function () {
                _scriptEditorCollapsed.console = !_scriptEditorCollapsed.console;
                render();
            }
        }, caret, labelEl, count);
        block.appendChild(head);
        if (collapsed) return block;

        if (entries.length === 0) {
            block.appendChild(el('div', {
                className: 'bowire-script-console-empty',
                textContent: 'No script output yet. ctx.log() writes land here after Send.'
            }));
        } else {
            var list = el('div', { className: 'bowire-script-console-list' });
            for (var i = 0; i < entries.length; i++) {
                var e = entries[i];
                list.appendChild(el('div', { className: 'bowire-script-console-row bowire-script-console-' + e.level },
                    el('span', { className: 'bowire-script-console-phase', textContent: e.phase }),
                    el('span', { className: 'bowire-script-console-level', textContent: e.level }),
                    el('span', { className: 'bowire-script-console-msg', textContent: e.message })
                ));
            }
            block.appendChild(list);
            block.appendChild(el('button', {
                className: 'bowire-script-tool-btn',
                type: 'button',
                textContent: 'Clear console',
                onClick: function () {
                    if (typeof clearScriptConsole === 'function') clearScriptConsole(svc, mth);
                    render();
                }
            }));
        }
        return block;
    }

    function _scriptExampleFor(shape, phase) {
        if (phase === 'pre') {
            switch (shape) {
                case 'grpc':
                    return [
                        '// gRPC pre-request — set authorization metadata',
                        '// + a 5s deadline.',
                        'ctx.metadata.set("authorization", "Bearer " + ctx.env.get("TOKEN"));',
                        'ctx.deadline.setSeconds(5);'
                    ].join('\n');
                case 'mqtt':
                    return [
                        '// MQTT pre-request — exactly-once delivery, no retain.',
                        'ctx.publish.qos = 1;',
                        'ctx.publish.retain = false;'
                    ].join('\n');
                case 'websocket':
                    return [
                        '// WebSocket pre-request — adjust the outgoing frame.',
                        '// ctx.request.body is the JSON frame string.',
                        'ctx.log("sending frame", ctx.request.body);'
                    ].join('\n');
                default:
                    return [
                        '// REST pre-request — sign the body, stamp the URL.',
                        'ctx.request.headers.set("X-Sent-At", String(Date.now()));',
                        'ctx.request.query.set("trace", ctx.env.get("TRACE_ID") || "anon");'
                    ].join('\n');
            }
        }
        // post
        switch (shape) {
            case 'grpc':
                return [
                    '// gRPC post-response — capture a token, assert OK.',
                    'ctx.assert.equal(ctx.response.status, "OK");',
                    'var body = ctx.response.json();',
                    'if (body && body.accessToken) ctx.vars.captured.token = body.accessToken;'
                ].join('\n');
            case 'mqtt':
                return [
                    '// MQTT post-response — log the broker ack and assert.',
                    'ctx.log("ack", ctx.response.body);',
                    'ctx.assert.ok(ctx.response.status, "broker did not ack");'
                ].join('\n');
            case 'websocket':
                return [
                    '// WebSocket post-response — read the final frame.',
                    'ctx.log("final frame", ctx.response.body);'
                ].join('\n');
            default:
                return [
                    '// REST post-response — capture + assert.',
                    'ctx.assert.equal(ctx.response.status, "OK");',
                    'var body = ctx.response.json();',
                    'if (body && body.id) ctx.vars.captured.lastId = body.id;'
                ].join('\n');
        }
    }

    function _scriptHintFor(shape, phase) {
        var always = 'ctx.env.get/set, ctx.vars.captured, ctx.assert.equal/ok/deepEqual, ctx.log() are available in every script.';
        if (phase === 'pre') {
            switch (shape) {
                case 'grpc':     return 'Runs before the gRPC call ships. ' + always + ' Protocol-typed: ctx.metadata.set, ctx.deadline.setSeconds.';
                case 'mqtt':     return 'Runs before the MQTT publish. ' + always + ' Protocol-typed: ctx.publish.qos, ctx.publish.retain.';
                case 'websocket':return 'Runs before the WebSocket frame is sent. ' + always + ' Protocol-typed: ctx.request.body.';
                default:         return 'Runs before the request is sent. ' + always + ' Protocol-typed: ctx.request.headers.set, ctx.request.query.set, ctx.request.body.';
            }
        }
        switch (shape) {
            case 'grpc':     return 'Runs after the gRPC response lands. ' + always + ' Protocol-typed: ctx.response.json(), ctx.metadata (request-side, frozen).';
            case 'mqtt':     return 'Runs after the MQTT broker ack. ' + always + ' Protocol-typed: ctx.response.body, ctx.publish (request-side, frozen).';
            case 'websocket':return 'Runs after the WebSocket stream completes. ' + always + ' Protocol-typed: ctx.response.body.';
            default:         return 'Runs after the response lands. ' + always + ' Protocol-typed: ctx.response.json(), ctx.response.headers, ctx.request (frozen).';
        }
    }

    // #126 Phase C — "Generate from intent". Opens a tiny inline
    // prompt; on submit, sends the operator's one-liner plus
    // the protocol shape + method schema to the existing AI chat
    // endpoint and writes the returned script body straight into
    // the editor. The fallback path (AI host not configured)
    // surfaces a friendly hint rather than silently doing nothing.
    function _openScriptGenPrompt(phase, shape, svc, mth, areaEl) {
        var existing = document.querySelector('.bowire-script-gen-modal');
        if (existing) existing.remove();
        var prompt = el('div', { className: 'bowire-script-gen-modal' });
        var heading = el('div', { className: 'bowire-script-gen-heading', textContent: 'Generate ' + (phase === 'pre' ? 'pre-request' : 'post-response') + ' script' });
        var sub = el('div', { className: 'bowire-script-gen-sub', textContent: 'Describe what the script should do for this ' + shape.toUpperCase() + ' method. The assistant sees the protocol shape + the available ctx.* surface and emits a runnable script.' });
        var input = el('textarea', {
            className: 'bowire-script-gen-input',
            placeholder: phase === 'pre'
                ? 'sign the body with my HMAC secret using sha256'
                : 'capture the response token and assert status is OK',
            rows: 2
        });
        var status = el('div', { className: 'bowire-script-gen-status' });
        var actions = el('div', { className: 'bowire-script-gen-actions' });
        var cancelBtn = el('button', {
            className: 'bowire-script-tool-btn',
            type: 'button',
            textContent: 'Cancel',
            onClick: function () { prompt.remove(); }
        });
        var submitBtn = el('button', {
            className: 'bowire-script-gen-btn',
            type: 'button',
            textContent: 'Generate'
        });
        submitBtn.addEventListener('click', function () {
            var intent = (input.value || '').trim();
            if (!intent) { status.textContent = 'Type a one-line intent first.'; return; }
            submitBtn.disabled = true;
            status.textContent = 'Generating…';
            _generateScriptFromIntent(intent, phase, shape, svc, mth)
                .then(function (script) {
                    submitBtn.disabled = false;
                    if (!script) {
                        status.textContent = 'No script returned. Try a more specific intent or check the AI configuration.';
                        return;
                    }
                    areaEl.value = script;
                    setMethodScript(svc, mth, phase === 'pre' ? 'preScript' : 'postScript', script);
                    prompt.remove();
                    render();
                })
                .catch(function (err) {
                    submitBtn.disabled = false;
                    status.textContent = 'Generation failed: ' + (err && err.message ? err.message : String(err));
                });
        });
        actions.appendChild(cancelBtn);
        actions.appendChild(submitBtn);
        prompt.appendChild(heading);
        prompt.appendChild(sub);
        prompt.appendChild(input);
        prompt.appendChild(status);
        prompt.appendChild(actions);
        var overlay = el('div', {
            className: 'bowire-script-gen-overlay',
            onClick: function (e) { if (e.target === overlay) overlay.remove(); }
        }, prompt);
        document.body.appendChild(overlay);
        setTimeout(function () { try { input.focus(); } catch (_) {} }, 0);
    }

    function _generateScriptFromIntent(intent, phase, shape, svc, mth) {
        // Compose a tight system prompt + user-intent payload and
        // POST it to /api/ai/chat. The schema-grounded backend
        // applies the tool-call loop on the request body the same
        // way it does for the Assistant drawer; we just feed it a
        // narrower system prompt so the response is a runnable
        // script body and not a chat reply.
        var prefix = (typeof config !== 'undefined' && config.prefix) ? config.prefix : '';
        var surface = _ctxSurfaceFor(shape);
        var methodSchema = null;
        if (selectedMethod && selectedMethod.inputType) {
            try { methodSchema = JSON.stringify({
                input: selectedMethod.inputType && selectedMethod.inputType.name,
                output: selectedMethod.outputType && selectedMethod.outputType.name,
                httpVerb: selectedMethod.httpMethod || null,
                httpPath: selectedMethod.httpPath || null
            }); } catch (_) {}
        }
        var system = [
            'You are a script-generator for the Bowire workbench.',
            'Emit a single JavaScript snippet that runs inside the typed ctx.* sandbox for a ' + shape.toUpperCase() + ' ' + (phase === 'pre' ? 'pre-request' : 'post-response') + ' phase.',
            'The available ctx.* surface for this phase is:',
            surface,
            'Constraints:',
            '- Output ONLY the script body — no Markdown fences, no commentary.',
            '- Use only members the surface above lists.',
            '- Prefer ctx.env.get(...) over hard-coded secrets.',
            '- Keep the snippet idempotent where possible.',
            methodSchema ? ('Method schema: ' + methodSchema) : ''
        ].filter(Boolean).join('\n');
        var body = {
            messages: [
                { role: 'system', content: system },
                { role: 'user', content: intent }
            ]
        };
        return fetch(prefix + '/api/ai/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        }).then(function (r) {
            if (!r.ok) {
                if (r.status === 404 || r.status === 503) {
                    throw new Error('Assistant not configured. Install the Bowire.Ai package or pick a provider in Settings → AI.');
                }
                throw new Error('Assistant request failed (HTTP ' + r.status + ')');
            }
            return r.json();
        }).then(function (resp) {
            if (!resp || typeof resp.content !== 'string') return '';
            return _stripCodeFences(resp.content);
        });
    }

    function _stripCodeFences(text) {
        if (!text) return '';
        var s = String(text).trim();
        // Strip ```js / ```javascript / ``` fences if the model
        // wraps the script body despite the system prompt asking
        // it not to.
        s = s.replace(/^```[a-z]*\n?/i, '').replace(/\n?```$/i, '');
        return s.trim();
    }

    function _ctxSurfaceFor(shape) {
        var always = [
            'ctx.env.get(key) / ctx.env.set(key, value) — workspace env vars',
            'ctx.vars.captured.NAME = value — value future {{captured.NAME}} resolves to',
            'ctx.assert.equal(actual, expected) / ctx.assert.ok(value) / ctx.assert.deepEqual(a, b)',
            'ctx.log(...args) — surfaces in the Console panel'
        ];
        switch (shape) {
            case 'grpc':
                return always.concat([
                    'ctx.metadata.set(key, value) / ctx.metadata.get(key)',
                    'ctx.deadline.setSeconds(n)',
                    'ctx.request.body (pre) | ctx.response.body, ctx.response.json(), ctx.response.metadata (post)'
                ]).join('\n  - ');
            case 'mqtt':
                return always.concat([
                    'ctx.publish.qos = 0|1|2',
                    'ctx.publish.retain = true|false',
                    'ctx.request.body (pre) | ctx.response.body (post)'
                ]).join('\n  - ');
            case 'websocket':
                return always.concat([
                    'ctx.request.body (pre) | ctx.response.body (post)'
                ]).join('\n  - ');
            default:
                return always.concat([
                    'ctx.request.headers.set/get/delete(key[, value])',
                    'ctx.request.query.set/get/delete(key[, value])',
                    'ctx.request.body / ctx.request.method / ctx.request.url',
                    'ctx.response.json(), ctx.response.body, ctx.response.status, ctx.response.headers (post)'
                ]).join('\n  - ');
        }
    }

    function createMetadataRow(key, value) {
        const row = el('div', { className: 'bowire-metadata-row' },
            el('input', {
                className: 'bowire-metadata-input',
                placeholder: 'Header name',
                value: key || ''
            }),
            el('input', {
                className: 'bowire-metadata-input',
                placeholder: 'Value',
                value: value || ''
            }),
            el('button', {
                className: 'bowire-metadata-remove',
                textContent: '\u00d7',
                onClick: function () { this.parentElement.remove(); }
            })
        );
        return row;
    }

    // ---- Streaming Output (Wireshark-style: list + detail pane) ----
    //
    // Layout:
    //   [toolbar]                     ← live indicator, count, auto-scroll, maximize
    //   [list-pane (scrollable)]      ← append-only chronological list of messages
    //   [splitter]                    ← drag-to-resize between list and detail
    //   [detail-pane]                 ← formatted body of the currently selected message
    //
    // Key invariant: while a stream is live, NEW messages append a single DOM node
    // to the list and (when auto-scroll is on) replace the detail pane content.
    // The full render() path is only used to build the structure on the first
    // message of a stream and on terminal state changes (done / error / cancel).

    function streamMessageRaw(msg) {
        if (!msg) return '';
        if (typeof msg.data === 'string') return msg.data;
        if (msg.data != null) {
            try { return JSON.stringify(msg.data); } catch { return String(msg.data); }
        }
        return '';
    }

    function streamMessageBytes(msg) {
        try { return new Blob([streamMessageRaw(msg)]).size; } catch { return streamMessageRaw(msg).length; }
    }

    function streamMessagePreview(msg, maxLen) {
        var raw = streamMessageRaw(msg);

        // WebSocket frames arrive wrapped as { type: 'text'|'binary', text|base64, bytes }.
        // The JSON envelope is plumbing — show the caller the *payload* so the
        // preview is useful at a glance (text, base64, or the JSON if the payload
        // is itself JSON).
        var trimmed = raw.trim();
        if (trimmed.startsWith('{')) {
            try {
                var parsed = JSON.parse(trimmed);
                if (parsed && typeof parsed === 'object') {
                    if (parsed.type === 'text' && typeof parsed.text === 'string') {
                        var inner = parsed.text.trim();
                        // If the text itself is JSON, pretty-compact it.
                        if (inner.startsWith('{') || inner.startsWith('[')) {
                            try { inner = JSON.stringify(JSON.parse(inner)); } catch { /* keep as-is */ }
                        }
                        return clamp(inner, maxLen);
                    }
                    if (parsed.type === 'binary' && typeof parsed.base64 === 'string') {
                        return clamp('[binary] ' + parsed.base64, maxLen);
                    }
                }
            } catch { /* fall through to raw preview */ }
        }

        var oneLine = raw.replace(/\s+/g, ' ').trim();
        return clamp(oneLine, maxLen);
    }

    function clamp(s, maxLen) {
        return s.length > maxLen ? s.substring(0, maxLen) + '\u2026' : s;
    }

    // Filter predicate for the stream list. Two modes:
    //   1. Substring  — "abc"            matches anywhere in the payload.
    //   2. Key-scoped — "topic:abc"      matches only when a JSON property
    //      named `topic` (case-insensitive, at any nesting depth) has a
    //      stringified value that contains `abc`.
    // Falls back to substring when the message isn't valid JSON so the
    // shorthand stays useful for raw text frames too.
    function streamMessageMatchesFilter(msg) {
        var q = (streamFilterQuery || '').trim();
        if (!q) return true;
        var raw = streamMessageRaw(msg);
        var keyMatch = q.match(/^([\w.\-]+)\s*:\s*(.+)$/);
        if (keyMatch) {
            var key = keyMatch[1].toLowerCase();
            var want = keyMatch[2].toLowerCase();
            try {
                var data = JSON.parse(raw);
                return streamJsonHasKeyValue(data, key, want);
            } catch {
                // Not JSON — keep the prefix-aware string searchable as
                // a substring so users aren't stuck typing escapes.
                return raw.toLowerCase().indexOf(q.toLowerCase()) !== -1;
            }
        }
        return raw.toLowerCase().indexOf(q.toLowerCase()) !== -1;
    }

    function streamJsonHasKeyValue(node, key, want) {
        if (node === null || typeof node !== 'object') return false;
        if (Array.isArray(node)) {
            for (var i = 0; i < node.length; i++) {
                if (streamJsonHasKeyValue(node[i], key, want)) return true;
            }
            return false;
        }
        for (var k in node) {
            if (!Object.prototype.hasOwnProperty.call(node, k)) continue;
            var v = node[k];
            if (k.toLowerCase() === key) {
                var s = (typeof v === 'string') ? v : JSON.stringify(v);
                if (s !== undefined && s.toLowerCase().indexOf(want) !== -1) return true;
            }
            if (streamJsonHasKeyValue(v, key, want)) return true;
        }
        return false;
    }

    function streamEffectiveIndex() {
        // null/-1 means "follow latest"; resolve to the last message index.
        if (streamSelectedIndex == null) return streamMessages.length - 1;
        if (streamSelectedIndex < 0 || streamSelectedIndex >= streamMessages.length) {
            return streamMessages.length - 1;
        }
        return streamSelectedIndex;
    }

    function buildStreamListItem(msg, idx) {
        var raw = streamMessageRaw(msg);
        var wsType = detectWebSocketFrameType(raw);
        var disPdu = detectDisPdu(raw);
        var udp = disPdu ? null : detectUdpDatagram(raw);
        var surgewaveTap = (disPdu || udp) ? null : detectSurgewaveTapEvent(raw);
        // Phase 3.1 — multi-select click handler. Ctrl/Cmd toggles
        // membership; Shift extends a range from the last anchor;
        // plain click replaces with a single-member selection. The
        // helper centralises the snapshot dispatch so the call site
        // here stays narrow.
        var item = el('div', {
            className: 'bowire-stream-list-item'
                + (msg && msg.id && streamSelectedIds.has(msg.id) ? ' multi-selected' : ''),
            dataset: { idx: String(idx), frameId: (msg && msg.id) || '' },
            onClick: function (e) {
                handleStreamFrameClick(idx, e);
            }
        });
        item.appendChild(el('span', {
            className: 'bowire-stream-list-idx',
            textContent: '#' + (idx + 1)
        }));
        if (wsType) {
            item.appendChild(el('span', {
                className: 'bowire-ws-frame-badge bowire-ws-frame-' + wsType,
                textContent: wsType.toUpperCase()
            }));
        }
        if (surgewaveTap) {
            item.appendChild(el('span', {
                className: 'bowire-surgewave-tap-badge bowire-surgewave-tap-' + surgewaveTap.event,
                textContent: surgewaveTap.event.toUpperCase(),
                title: formatSurgewaveTapTooltip(surgewaveTap)
            }));
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: formatSurgewaveTapPreview(surgewaveTap)
            }));
        } else if (disPdu) {
            item.appendChild(el('span', {
                className: 'bowire-dis-pdu-badge bowire-dis-pdu-' + disPdu.pduType.toLowerCase(),
                textContent: formatDisPduTypeLabel(disPdu.pduType),
                title: 'PDU type ' + disPdu.pduTypeId + ' (' + disPdu.pduType + ')'
            }));
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: formatDisPreview(disPdu)
            }));
        } else if (udp) {
            item.appendChild(el('span', {
                className: 'bowire-udp-badge' + (udp.text !== null ? ' bowire-udp-text' : ' bowire-udp-binary'),
                textContent: udp.text !== null ? 'UDP' : 'UDP·BIN',
                title: udp.text !== null
                    ? 'UTF-8 text datagram from ' + udp.source
                    : 'Binary datagram from ' + udp.source
            }));
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: formatUdpPreview(udp)
            }));
        } else {
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: streamMessagePreview(msg, 200)
            }));
        }
        item.appendChild(el('span', {
            className: 'bowire-stream-list-size',
            textContent: streamMessageBytes(msg) + ' B'
        }));
        return item;
    }

    // One-line summary of a Surgewave tap event for the list preview:
    //   produced:   'orders/p0@42 · alice · "…payload preview…"'
    //   consumed:   'orders/p0@42 → group-a, group-b'
    //   rejected:   'orders · bob · acl-deny'
    //   rebalanced: 'orders · group-a, group-b'
    function formatSurgewaveTapPreview(tap) {
        var parts = [];
        if (tap.topic) {
            var loc = tap.topic;
            if (tap.partition != null && tap.partition >= 0) loc += '/p' + tap.partition;
            if (tap.offset != null) loc += '@' + tap.offset;
            parts.push(loc);
        }
        if (tap.event === 'consumed' || tap.event === 'rebalanced') {
            if (tap.consumers && tap.consumers.length > 0) {
                parts.push((tap.event === 'consumed' ? '→ ' : '') + tap.consumers.join(', '));
            }
        } else {
            if (tap.principal) parts.push(tap.principal);
            if (tap.event === 'rejected' && tap.reason) {
                parts.push(tap.reason);
            } else if (tap.value && tap.value.length > 0) {
                var compact = tap.value.replace(/\s+/g, ' ').trim();
                if (compact.length > 120) compact = compact.slice(0, 120) + '…';
                parts.push('"' + compact + '"');
            }
        }
        return parts.length > 0 ? parts.join(' · ') : tap.event;
    }

    function formatSurgewaveTapTooltip(tap) {
        switch (tap.event) {
            case 'produced': return 'Broker accepted a produce' +
                (tap.principal ? ' from ' + tap.principal : '');
            case 'consumed': return 'Broker dispatched to consumer group(s)' +
                (tap.consumers ? ': ' + tap.consumers.join(', ') : '');
            case 'rejected': return 'Broker rejected a produce' +
                (tap.reason ? ' — ' + tap.reason : '');
            case 'rebalanced': return 'Consumer group finished rebalancing';
            default: return tap.event;
        }
    }

    // One-line summary of a UDP datagram:
    //   text:   '10.0.0.5:53812 · "GET /api HTTP/1.1"'
    //   binary: '10.0.0.5:53812 · 144 bytes binary'
    function formatUdpPreview(udp) {
        if (udp.text !== null && udp.text.length > 0) {
            var compact = udp.text.replace(/\s+/g, ' ').trim();
            if (compact.length > 160) compact = compact.slice(0, 160) + '…';
            return udp.source + ' · "' + compact + '"';
        }
        return udp.source + ' · ' + udp.bytes + ' bytes binary';
    }

    // Turn "EntityState" into "ENTITY STATE" — readable in the badge
    // without wasting width on CamelCase runs.
    function formatDisPduTypeLabel(pduType) {
        return pduType
            .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
            .replace(/([A-Z])([A-Z][a-z])/g, '$1 $2')
            .toUpperCase();
    }

    // One-line summary of a DIS PDU for the list preview:
    //   EntityState: "1:1:500 MERKAVA · Friendly · 1.1.225.1.1.0.0"
    //   other types: "exercise 1 · N bytes"
    function formatDisPreview(disPdu) {
        if (disPdu.pduType === 'EntityState') {
            var parts = [];
            if (disPdu.entityId) parts.push(disPdu.entityId);
            if (disPdu.marking) parts.push('"' + disPdu.marking + '"');
            if (disPdu.force) parts.push(disPdu.force);
            if (disPdu.entityType) parts.push(disPdu.entityType);
            if (parts.length > 0) return parts.join(' · ');
        }
        var meta = [];
        if (typeof disPdu.exerciseId === 'number') meta.push('exercise ' + disPdu.exerciseId);
        if (typeof disPdu.length === 'number') meta.push(disPdu.length + ' wire bytes');
        return meta.length > 0 ? meta.join(' · ') : disPdu.pduType;
    }

    function buildStreamDetailContent(msg, idx) {
        var raw = streamMessageRaw(msg);
        var wsType = detectWebSocketFrameType(raw);
        var bytes = streamMessageBytes(msg);

        var header = el('div', {
            className: 'bowire-stream-detail-header',
            id: 'bowire-stream-detail-header'
        });
        header.appendChild(el('span', {
            className: 'bowire-stream-detail-title',
            textContent: 'Message #' + (idx + 1)
        }));
        header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '\u2022' }));
        header.appendChild(el('span', {
            className: 'bowire-stream-detail-meta',
            textContent: bytes + ' bytes'
        }));
        if (wsType) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '\u2022' }));
            header.appendChild(el('span', {
                className: 'bowire-ws-frame-badge bowire-ws-frame-' + wsType,
                textContent: wsType.toUpperCase()
            }));
        }

        var disPdu = detectDisPdu(raw);
        var udp = disPdu ? null : detectUdpDatagram(raw);
        var surgewaveTap = (disPdu || udp) ? null : detectSurgewaveTapEvent(raw);
        if (surgewaveTap) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-surgewave-tap-badge bowire-surgewave-tap-' + surgewaveTap.event,
                textContent: surgewaveTap.event.toUpperCase(),
                title: formatSurgewaveTapTooltip(surgewaveTap)
            }));
            if (surgewaveTap.topic) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: surgewaveTap.topic + (surgewaveTap.partition != null && surgewaveTap.partition >= 0 ? ' · p' + surgewaveTap.partition : '')
                }));
            }
            if (surgewaveTap.principal) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: '@' + surgewaveTap.principal
                }));
            }
            if (surgewaveTap.event === 'rejected' && surgewaveTap.reason) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: surgewaveTap.reason
                }));
            }
        }
        if (disPdu) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-dis-pdu-badge bowire-dis-pdu-' + disPdu.pduType.toLowerCase(),
                textContent: formatDisPduTypeLabel(disPdu.pduType),
                title: 'PDU type ' + disPdu.pduTypeId + ' (' + disPdu.pduType + ')'
            }));
            if (disPdu.entityId) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: disPdu.entityId
                }));
            }
            if (disPdu.marking) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: '"' + disPdu.marking + '"'
                }));
            }
            if (disPdu.force) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-dis-force-badge bowire-dis-force-' + disPdu.force.toLowerCase(),
                    textContent: disPdu.force
                }));
            }
        }

        if (udp) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-udp-badge' + (udp.text !== null ? ' bowire-udp-text' : ' bowire-udp-binary'),
                textContent: udp.text !== null ? 'UDP' : 'UDP·BIN'
            }));
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-stream-detail-meta',
                textContent: udp.source
            }));
        }

        // Spacer pushes the toggle to the far right of the header
        header.appendChild(el('span', { style: 'flex:1' }));

        // "Show list / Hide list" — lives in the detail header
        // because it controls the preview size of this message:
        // the user expands the detail to see the full payload,
        // then collapses back when done inspecting.
        var maxBtn = el('button', {
            className: 'bowire-stream-toolbar-btn' + (streamDetailMaximized ? ' is-on' : ''),
            id: 'bowire-stream-maximize-btn',
            title: streamDetailMaximized
                ? 'Show the message list'
                : 'Hide the message list — full detail view',
            onClick: function () {
                streamDetailMaximized = !streamDetailMaximized;
                var out = document.getElementById('bowire-stream-output');
                if (out) out.classList.toggle('is-maximized', streamDetailMaximized);
                var btn = document.getElementById('bowire-stream-maximize-btn');
                if (btn) {
                    btn.classList.toggle('is-on', streamDetailMaximized);
                    btn.title = streamDetailMaximized
                        ? 'Show the message list'
                        : 'Hide the message list — full detail view';
                    var label = btn.querySelector('span');
                    if (label) label.textContent = streamDetailMaximized ? '\u25A3 Show list' : '\u25A1 Hide list';
                }
            }
        });
        maxBtn.appendChild(el('span', {
            textContent: streamDetailMaximized ? '\u25A3 Show list' : '\u25A1 Hide list'
        }));
        header.appendChild(maxBtn);

        // DIS and UDP envelopes both carry the raw on-wire bytes as
        // base64. Show a hex dump underneath the JSON view so users
        // can inspect the wire format directly; for UDP text payloads
        // we also show the decoded string between envelope and hex so
        // the eye lands there first. Non-DIS / non-UDP messages get
        // the original single-<pre> highlighted JSON body.
        if (disPdu || udp) {
            var body = el('div', {
                className: 'bowire-stream-detail-body bowire-stream-detail-body-bytes',
                id: 'bowire-stream-detail-body'
            });
            var envelopePre = el('pre', { className: 'bowire-stream-detail-envelope' });
            envelopePre.innerHTML = highlightJson(raw);
            body.appendChild(envelopePre);

            if (udp && udp.text !== null) {
                body.appendChild(el('div', {
                    className: 'bowire-bytes-section-label',
                    textContent: 'UTF-8 payload (' + udp.bytes + ' bytes):'
                }));
                var textPre = el('pre', { className: 'bowire-udp-text-pane' });
                textPre.textContent = udp.text;
                body.appendChild(textPre);
            }

            var byteCount = disPdu ? (disPdu.bytes || 0) : udp.bytes;
            var rawB64 = disPdu ? disPdu.raw : udp.raw;
            body.appendChild(el('div', {
                className: 'bowire-bytes-section-label',
                textContent: 'On-wire bytes (' + byteCount + '):'
            }));
            var hexPre = el('pre', { className: 'bowire-dis-hex-dump' });
            hexPre.textContent = formatDisHexDump(rawB64);
            body.appendChild(hexPre);

            return { header: header, body: body };
        }

        // renderJsonViewer (Hoppscotch-style line-numbered viewer with
        // gutter chevron) is the consistent body shape across response
        // panes — gives the streaming detail the same line numbers +
        // gutter toggle column as the unary response output. Carries
        // the same [data-json-path] markers the semantics decorator
        // anchors badges onto. Operator feedback: 'nach dem refactor
        // funktioniert das aufklappen noch per click, aber die
        // zeilennummern fehlen und die symbole zum auf/zuklappen sind
        // nicht in einer extra reihe.'
        var body = el('div', {
            className: 'bowire-stream-detail-body',
            id: 'bowire-stream-detail-body'
        });
        // JSON viewer + per-pane toolbar (Expand-all / Collapse-all /
        // Wrap / Search / Copy / Download). Toolbar is part of the
        // JSON pane so it hides naturally when tab mode switches to
        // the widget tab (the widget tab body never contains a JSON
        // viewer), and stays put in split mode where the JSON pane
        // is always visible. Same wrapper shape goes onto the unary
        // path below.
        var streamViewer = renderJsonViewer(raw, { wrap: false });
        body.appendChild(bowireRenderJsonViewerWithToolbar(streamViewer, {
            raw: raw,
            downloadName: 'stream-message',
            contentType: 'application/json'
        }));
        // Same gesture wiring the unary response output gets — click
        // toggles via native <details>, dblclick copies the JSONPath,
        // right-click opens the unified context menu.
        bowireWireResponseTreeGestures(body);
        if (typeof bowireDecorateResponseTreeForSemantics === 'function'
            && selectedService && selectedMethod) {
            try {
                bowireDecorateResponseTreeForSemantics(
                    body, selectedService.name, selectedMethod.name);
            } catch (e) { console.error('[bowire-semantics] decorate stream-detail', e); }
        }
        if (selectedService && selectedMethod) {
            try {
                // Per-frame body so any extension that resolves
                // values from the JSON (e.g. the map widget's
                // "Center on map" entry) sees THIS message's
                // payload, not whatever the last unary response
                // left behind in `responseData`.
                var parsedFrame = null;
                try { parsedFrame = JSON.parse(raw); } catch { parsedFrame = null; }
                bowireDecorateResponseTreeViaExtensions(
                    body, selectedService.name, selectedMethod.name, parsedFrame);
            } catch (e) { console.error('[bowire-resp-tree] decorate stream-detail', e); }
        }

        return { header: header, body: body };
    }

    // Decode base64 into a canonical hex dump:
    //   00000000  06 01 01 01 00 00 00 00  00 90 00 00 00 01 00 01  |................|
    // Returns a plain-text multi-line string, ready for a <pre> block.
    function formatDisHexDump(base64) {
        var bytes;
        try {
            var bin = atob(base64);
            bytes = new Uint8Array(bin.length);
            for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        } catch (e) {
            return '(base64 decode failed)';
        }
        var out = [];
        for (var offset = 0; offset < bytes.length; offset += 16) {
            var hex = [];
            var ascii = [];
            for (var j = 0; j < 16; j++) {
                if (offset + j < bytes.length) {
                    var b = bytes[offset + j];
                    hex.push(('0' + b.toString(16)).slice(-2));
                    ascii.push(b >= 0x20 && b < 0x7F ? String.fromCharCode(b) : '.');
                } else {
                    hex.push('  ');
                    ascii.push(' ');
                }
                if (j === 7) hex.push('');
            }
            out.push(
                ('0000000' + offset.toString(16)).slice(-8) + '  ' +
                hex.join(' ') + '  |' + ascii.join('') + '|'
            );
        }
        return out.join('\n');
    }

    // ---- Subscription state badge ----
    // Reads the active subscription registry + the in-memory frame count
    // (the registry's receivedCount lags slightly because frames push
    // before notify) and renders a pill with one of the five states:
    //   ● Subscribed  — connection up, no frame yet
    //   ● Receiving   — connection up, frame within idle window
    //   ○ Idle        — connection up, no frame in last N seconds
    //   ○ Closed      — connection ended (stream completed or stopped)
    //   × Error       — channel reported an error
    function renderSubscriptionBadge(svcName, methodName, frameCount) {
        var entry = (svcName && methodName)
            ? findSubscription(svcName, methodName) : null;
        var state;
        var label;
        var count = (typeof frameCount === 'number')
            ? frameCount
            : (entry ? entry.receivedCount : 0);
        if (entry) {
            state = subscriptionState(entry);
            switch (state) {
                case 'subscribed': label = 'Subscribed'; break;
                case 'receiving': label = 'Receiving'; break;
                case 'idle':      label = 'Idle'; break;
                case 'error':     label = 'Error'; break;
                default:          label = 'Closed';
            }
        } else if (frameCount > 0) {
            state = 'closed';
            label = 'Closed';
        } else if (isExecuting) {
            // Active unary streaming (no registry entry — should be
            // rare now) or pre-registry init.
            state = 'subscribed';
            label = 'Subscribed';
        } else {
            state = 'idle-empty';
            label = 'Stream ended';
        }
        var pill = el('div', {
            className: 'bowire-stream-status-pill bowire-stream-state-' + state,
            id: 'bowire-stream-state-badge',
            'data-bowire-state': state,
            title: entry && entry.channelError
                ? ('Error: ' + entry.channelError)
                : (label + ' · ' + count + ' message' + (count === 1 ? '' : 's'))
        });
        pill.appendChild(el('span', {
            className: 'bowire-stream-status-dot' + ((state === 'receiving' || state === 'subscribed') ? ' live' : '')
                + ((state === 'error') ? ' error' : '')
        }));
        pill.appendChild(el('span', {
            className: 'bowire-stream-status-text',
            textContent: label
        }));
        pill.appendChild(el('span', {
            className: 'bowire-stream-status-count',
            textContent: '· ' + count + ' msg' + (count === 1 ? '' : 's')
        }));
        return pill;
    }

    function renderStreamingOutput() {
        // Outer container — referenced by appendStreamMessage / selectStreamMessage
        // / updateStreamDetail to find the live DOM nodes.
        var output = el('div', {
            className: 'bowire-response-output streaming' + (streamDetailMaximized ? ' is-maximized' : ''),
            id: 'bowire-stream-output'
        });
        output.style.setProperty('--bowire-stream-list-pct', streamListSizePct + '%');

        // ---- Toolbar ----
        var toolbar = el('div', { className: 'bowire-stream-toolbar' });
        // Compose the rich state badge. Pulls the live entry from the
        // subscription registry so a subscription that's open on the
        // active method but not yet emitting a frame surfaces as
        // "Subscribed" instead of the old binary "Streaming" / "Stream
        // ended" pair. The pill is built by the shared helper so the
        // statusbar dropdown can re-use the same layout primitives.
        var badge = renderSubscriptionBadge(
            selectedService && selectedService.name,
            selectedMethod && selectedMethod.name,
            streamMessages.length);
        toolbar.appendChild(badge);
        var hasFilter = (streamFilterQuery || '').trim().length > 0;

        toolbar.appendChild(el('span', { className: 'bowire-stream-toolbar-spacer' }));

        // Filter toggle button. The actual input lives in a panel
        // below the toolbar (see renderStreamFilterPanel) so its
        // variable width + the growing digit count can't shift the
        // right-hand buttons around as messages roll in. Stays
        // highlighted while a filter is active even when the panel
        // is closed, so users don't lose track of an applied
        // predicate.
        var filterToggle = el('button', {
            className: 'bowire-stream-toolbar-btn' + ((streamFilterPanelOpen || hasFilter) ? ' is-on' : ''),
            id: 'bowire-stream-filter-toggle',
            title: streamFilterPanelOpen
                ? 'Hide filter panel'
                : (hasFilter ? 'Filter is active \u2014 click to edit' : 'Open the filter panel'),
            onClick: function () {
                streamFilterPanelOpen = !streamFilterPanelOpen;
                render();
                if (streamFilterPanelOpen) {
                    var inp = document.getElementById('bowire-stream-filter-input');
                    if (inp) inp.focus();
                }
            }
        });
        filterToggle.appendChild(el('span', {
            textContent: hasFilter
                ? '\u25bc Filter \u00b7 on'
                : '\u25bc Filter'
        }));
        toolbar.appendChild(filterToggle);

        // "Follow latest" — when on, every new arrival auto-selects
        // and scrolls to the newest message. When off, the view stays
        // pinned to whatever the user last clicked. The old label
        // "Auto-scroll" was unclear because it sounded like a list
        // feature, but it also controls the detail pane.
        var autoBtn = el('button', {
            className: 'bowire-stream-toolbar-btn' + (streamAutoScroll ? ' is-on' : ''),
            id: 'bowire-stream-autoscroll-btn',
            title: streamAutoScroll
                ? 'Following the latest message \u2014 click to pin the current selection'
                : 'Pinned to current selection \u2014 click to follow the latest',
            onClick: function () { setStreamAutoScroll(!streamAutoScroll); }
        });
        autoBtn.appendChild(el('span', {
            textContent: streamAutoScroll ? '\u25C9 Follow latest' : '\u25CB Pinned'
        }));
        toolbar.appendChild(autoBtn);

        // Phase 3.1 \u2014 layout-mode toggle. Visible only when the active
        // method has a split-eligible widget AND the user has opted into
        // tab mode (the default for those methods is split, so the
        // toggle on the widget pane header is enough \u2014 but once the
        // user has switched to tab there's no widget pane and we need
        // an alternate escape hatch).
        var toolbarToggle = renderStreamingToolbarLayoutToggle();
        if (toolbarToggle) toolbar.appendChild(toolbarToggle);

        output.appendChild(toolbar);

        // ---- Filter panel (collapsible) ----
        // Structured as rows so future "advanced" filter modes (e.g.
        // pick from known message keys, multiple key:value rules) can
        // append additional `.bowire-stream-filter-row` children without
        // reshaping the layout. Only mounted when the toggle is open so
        // closed state contributes zero height.
        if (streamFilterPanelOpen) {
            var panel = el('div', {
                className: 'bowire-stream-filter-panel',
                id: 'bowire-stream-filter-panel'
            });
            var basicRow = el('div', { className: 'bowire-stream-filter-row' });
            var filterInput = el('input', {
                type: 'text',
                className: 'bowire-stream-filter',
                id: 'bowire-stream-filter-input',
                placeholder: 'Filter messages…  (e.g. topic:foo, status:200, or any substring)',
                value: streamFilterQuery,
                spellcheck: 'false',
                onInput: function (e) {
                    streamFilterQuery = e.target.value;
                    render();
                    var inp = document.getElementById('bowire-stream-filter-input');
                    if (inp) {
                        inp.focus();
                        inp.selectionStart = inp.selectionEnd = inp.value.length;
                    }
                }
            });
            basicRow.appendChild(filterInput);
            if (hasFilter) {
                var clearBtn = el('button', {
                    className: 'bowire-stream-filter-clear',
                    type: 'button',
                    title: 'Clear filter',
                    onClick: function () {
                        streamFilterQuery = '';
                        render();
                    }
                });
                clearBtn.appendChild(el('span', { textContent: '✕' }));
                basicRow.appendChild(clearBtn);
            }
            panel.appendChild(basicRow);
            output.appendChild(panel);
        }

        // ---- Count line ----
        // Sits directly above the list, on its own row, so the
        // monotonic growth of the message count never reflows the
        // toolbar buttons. Shows "X / Y messages" while a filter
        // hides some rows.
        var visibleCount = streamMessages.filter(streamMessageMatchesFilter).length;
        var countText;
        if (hasFilter) {
            countText = visibleCount + ' / ' + streamMessages.length + ' messages';
        } else {
            countText = streamMessages.length + (streamMessages.length === 1 ? ' message' : ' messages');
        }
        output.appendChild(el('div', {
            className: 'bowire-stream-count-row',
            id: 'bowire-stream-count',
            textContent: countText
        }));

        // ---- List pane ----
        var listPane = el('div', {
            className: 'bowire-stream-list-pane',
            id: 'bowire-stream-list-pane'
        });
        var list = el('div', {
            className: 'bowire-stream-list',
            id: 'bowire-stream-list'
        });
        for (var i = 0; i < streamMessages.length; i++) {
            // Skip messages that don't match the filter — the indices
            // stay absolute (used by selectStreamMessage), so the
            // detail pane and existing selection still work.
            if (!streamMessageMatchesFilter(streamMessages[i])) continue;
            list.appendChild(buildStreamListItem(streamMessages[i], i));
        }
        listPane.appendChild(list);
        output.appendChild(listPane);

        // ---- Splitter ----
        var splitter = el('div', {
            className: 'bowire-stream-splitter',
            id: 'bowire-stream-splitter',
            title: 'Drag to resize'
        });
        output.appendChild(splitter);

        // ---- Detail pane ----
        var detailPane = el('div', {
            className: 'bowire-stream-detail-pane',
            id: 'bowire-stream-detail-pane'
        });
        var effIdx = streamEffectiveIndex();
        if (effIdx >= 0) {
            var built = buildStreamDetailContent(streamMessages[effIdx], effIdx);
            detailPane.appendChild(built.header);
            detailPane.appendChild(built.body);
        }
        output.appendChild(detailPane);

        // ---- Post-mount setup ----
        // Selection class on the active item; auto-scroll to bottom; splitter
        // drag handler; auto-scroll abandon detection. All run after the DOM
        // is attached so getElementById finds the freshly inserted nodes.
        requestAnimationFrame(function () {
            updateStreamSelection();
            if (streamAutoScroll) scrollStreamListToBottom();
            attachStreamScrollListener();
            attachStreamSplitterDrag();
        });

        return output;
    }

    // ---- Phase 3.1: streaming pane composed with split-mode widgets ----
    //
    // When the active method has annotations that match a widget whose
    // default layout is `split-horizontal` (resolved by the framework's
    // preferredSplitExtensionForMethod helper — extensions own which
    // kinds default to split via layout.js → defaultLayoutForKind),
    // wrap the streaming pane and the widget pane in a split-pane
    // primitive so the user can multi-select frames AND watch the
    // widget react in real time.
    //
    // Falls back to the plain streaming output when:
    //   - no extension is registered
    //   - the extension framework hasn't loaded yet
    //   - no method is selected (would happen on a freeform request)
    //   - the user has overridden the layout to `tab` (the widget
    //     stays mountable via Phase-4's right-click menu, but the
    //     streaming pane goes back to its original shape).
    //
    // The widget(s) mount asynchronously via `mountWidgetsForMethod`
    // (fetches `/api/semantics/effective`), so the widget slot starts
    // empty and fills in once the annotation lookup resolves. The
    // streaming pane is fully rendered + interactive while that's
    // pending — the split layout is the only thing that depends on a
    // synchronous "is this method map-eligible?" answer.
    //
    // The cleanup function returned by `mountWidgetsForMethod` is
    // tracked on the wrapper so the next render() can tear down
    // viewers cleanly. Without that, every render() would leave the
    // previous mount's frames$ subscription dangling.
    var bowireWidgetUnmounts = [];
    function disposeWidgetMounts() {
        for (var i = 0; i < bowireWidgetUnmounts.length; i++) {
            try { bowireWidgetUnmounts[i](); } catch (e) { console.error('[bowire-widget]', e); }
        }
        bowireWidgetUnmounts = [];
    }

    function renderStreamingPaneWithWidgets() {
        var fw = window.__bowireExtFramework;
        var layout = window.__bowireLayout;

        // The straightforward fallbacks — no extension framework, no
        // selected method, etc. — return the plain streaming output
        // unchanged. Anything that wants the split layout is on the
        // happy path below.
        if (!fw || !layout || !selectedService || !selectedMethod) {
            disposeWidgetMounts();
            return renderStreamingOutput();
        }

        // Synchronously decide whether the active method is split-
        // eligible. Core delegates the lookup to the extension
        // framework — `preferredSplitExtensionForMethod` walks the
        // cached effective annotations for (service, method),
        // matches each present kind against registered viewers,
        // and returns the first whose default layout is `split-*`
        // (per layout.js → defaultLayoutForKind). When the cache
        // is empty (first render before /api/semantics/effective
        // resolves) it falls back to "any registered split-default
        // viewer" so the wrapper doesn't flicker from tab to split
        // when the cache lands.
        //
        // Core hardcodes no kind here — extensions own the split
        // decision via their kind registration + the framework
        // owns the lookup. Adding a future image / chart / audio
        // viewer with a split default is purely additive.
        var splitKindExt = (typeof fw.preferredSplitExtensionForMethod === 'function')
            ? fw.preferredSplitExtensionForMethod(
                selectedService.name, selectedMethod.name)
            : null;
        var saved = splitKindExt
            ? layout.loadWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, splitKindExt.kind)
            : null;
        var splitActive = !!(splitKindExt
            && saved
            && (saved.mode === 'split-horizontal' || saved.mode === 'split-vertical'));

        if (!splitActive) {
            disposeWidgetMounts();
            var streamingOut = renderStreamingOutput();
            // No registered viewer for a split-default kind → keep
            // the legacy "stream + placeholder card for Phase 3-R
            // install-discovery" wrapper. The placeholder slot folds
            // away if there's no suggested package.
            if (!splitKindExt) {
                var wrapper = el('div', { className: 'bowire-streaming-with-placeholders' });
                wrapper.appendChild(streamingOut);
                var placeholderSlot = el('div', { className: 'bowire-placeholder-slot' });
                wrapper.appendChild(placeholderSlot);
                var cleanup = fw.mountWidgetsForMethod(
                    selectedService.name, selectedMethod.name, placeholderSlot);
                if (typeof cleanup === 'function') {
                    bowireWidgetUnmounts.push(cleanup);
                }
                return wrapper;
            }
            // Registered viewer + tab mode → Stream / Map tab-strip.
            // Without this branch the Toggle (Split → Tab) used to
            // strand the map widget without a slot, so the user lost
            // the map entirely (Issue 1).
            return renderResponseTabbedWithWidget(
                streamingOut, splitKindExt, saved);
        }

        // Build the split-pane host. We re-create the wrapper on every
        // render — morphdom diffs it against the previous wrapper at
        // the same position — but the split-pane primitive owns the
        // divider drag listeners only for the lifetime of THIS wrapper.
        // dispose() runs on every cleanup pass via disposeWidgetMounts.
        disposeWidgetMounts();

        // Stable id so morphdom matches by key when re-rendering. The
        // tab-mode counterpart uses `bowire-response-widget-host`;
        // distinct ids force morphdom to replace the wrapper subtree
        // wholesale on a Tab ↔ Split transition rather than trying to
        // merge a leftSlot div with a tab strip and stranding live
        // onClick listeners on the wrong nodes (Bug 2).
        var host = el('div', {
            id: 'bowire-response-widget-host-split',
            className: 'bowire-widget-split-host'
        });
        var pane = layout.createSplitPane(host, {
            orientation: saved.mode === 'split-vertical' ? 'vertical' : 'horizontal',
            initialRatio: typeof saved.ratio === 'number' ? saved.ratio : 0.5,
            storageKey: 'bowire_widget_split_ratio:' + splitKindExt.id
        });
        bowireWidgetUnmounts.push(function () { pane.dispose(); });

        // Left slot: the streaming-frames pane (Wireshark list + detail).
        pane.firstSlot.appendChild(renderStreamingOutput());

        // Right slot: the widget pane with its own header (title +
        // layout toggle). The actual viewer DOM is attached
        // asynchronously by mountWidgetsForMethod, which fetches the
        // effective annotations first.
        var widgetPane = el('div', {
            className: 'bowire-widget-pane' + (widgetPaneMaximized ? ' is-maximized' : '')
        });
        var widgetHeader = el('div', { className: 'bowire-widget-pane-header' },
            el('span', {
                className: 'bowire-widget-pane-header-title',
                textContent: (splitKindExt.viewer && splitKindExt.viewer.label) || splitKindExt.kind
            }),
            el('button', {
                className: 'bowire-widget-pane-maximize',
                title: widgetPaneMaximized
                    ? 'Restore widget to its slot (Esc)'
                    : 'Maximize widget to fill the window',
                onClick: function () {
                    // Toggle the CSS class directly without calling
                    // render(). Every render() pass tears the widget
                    // pane down via disposeWidgetMounts() and re-runs
                    // mountWidgetsForMethod, which for a MapLibre
                    // viewer means losing the WebGL canvas + the
                    // basemap tiles + the camera state — defeats the
                    // purpose of maximizing. Direct DOM manipulation
                    // preserves the live MapLibre instance; its
                    // internal ResizeObserver picks up the new
                    // viewport-filling geometry and calls
                    // map.resize() automatically.
                    widgetPaneMaximized = !widgetPaneMaximized;
                    var pane = document.querySelector('.bowire-widget-pane');
                    if (pane) pane.classList.toggle('is-maximized', widgetPaneMaximized);
                    this.innerHTML = bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize');
                    this.title = widgetPaneMaximized
                        ? 'Restore widget to its slot (Esc)'
                        : 'Maximize widget to fill the window';
                },
                innerHTML: bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize')
            }),
            el('button', {
                className: 'bowire-widget-layout-toggle',
                title: 'Toggle layout (Tab ↔ Split)',
                onClick: function () {
                    var current = saved.mode;
                    var nextMode = layout.cycleLayoutMode(current, splitKindExt.kind);
                    layout.saveWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, {
                        mode: nextMode,
                        ratio: pane.getRatio()
                    });
                    render();
                },
                innerHTML: bowireLayoutIcon('layout-split')
            })
        );
        var widgetBody = el('div', { className: 'bowire-widget-pane-body' });
        widgetPane.appendChild(widgetHeader);
        widgetPane.appendChild(widgetBody);
        pane.secondSlot.appendChild(widgetPane);

        // Kick off the asynchronous mount. mountWidgetsForMethod
        // resolves /api/semantics/effective, finds pairing matches,
        // and calls each viewer's mount() on its own slot. We return
        // the cleanup so the next render() can dispose this mount.
        var widgetCleanup = fw.mountWidgetsForMethod(
            selectedService.name, selectedMethod.name, widgetBody);
        if (typeof widgetCleanup === 'function') {
            bowireWidgetUnmounts.push(widgetCleanup);
        }

        return host;
    }

    /**
     * Unary counterpart to renderStreamingPaneWithWidgets. The unary
     * response pane defaults to "JSON tree only" (no widgets), but
     * when the response shape carries annotations a registered viewer
     * cares about — TacticalAPI's GetSituationObjects returns lat/lon
     * pairs, REST GETs against geo APIs do the same — we want the
     * same split-pane affordance the streaming path already gives
     * users: JSON tree on the left, the widget (map) on the right.
     *
     * Caller passes the already-built response output element so we
     * don't reproduce its GraphQL-errors banner / MCP-content
     * banner logic here — only the wrapping changes. Returns the
     * `outputElement` unchanged when:
     *   • the extension framework / layout primitive isn't available
     *   • no registered extension claims the active method's kind
     *   • the user has the per-method layout set to `tab` (no split)
     *
     * The unary response was already dispatched as a single synthetic
     * frame from api.js, so the framework's replay-cache covers the
     * mount race (widget attaches after the dispatch fired — same
     * trick the streaming path uses).
     */
    function renderResponseWithWidgets(outputElement) {
        var fw = window.__bowireExtFramework;
        var layout = window.__bowireLayout;
        if (!fw || !layout || !selectedService || !selectedMethod) {
            disposeWidgetMounts();
            return outputElement;
        }

        // Extension-driven lookup (no hardcoded kind in core) —
        // see renderStreamingPaneWithWidgets above for the
        // framework helper's contract.
        var splitKindExt = (typeof fw.preferredSplitExtensionForMethod === 'function')
            ? fw.preferredSplitExtensionForMethod(
                selectedService.name, selectedMethod.name)
            : null;
        var saved = splitKindExt
            ? layout.loadWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, splitKindExt.kind)
            : null;
        var splitActive = !!(splitKindExt
            && saved
            && (saved.mode === 'split-horizontal' || saved.mode === 'split-vertical'));

        if (!splitActive) {
            // Tab mode — TWO sub-tabs inside the response pane: a
            // JSON tab (default) and one Map tab per registered
            // split-eligible viewer. The viewer mounts INSIDE its tab
            // body, not into a placeholder slot — without this wiring
            // the toggle "Split → Tab" would make the map vanish
            // entirely (Issue 1).
            //
            // When no registered viewer claims the kind we still need
            // the placeholder-card path for the Phase 3-R discovery
            // hint, so the absence of a `splitKindExt` falls through
            // to the legacy single-pane wrapper.
            disposeWidgetMounts();

            if (!splitKindExt) {
                var wrapper = el('div', { className: 'bowire-unary-with-placeholders' });
                wrapper.appendChild(outputElement);
                var placeholderSlot = el('div', { className: 'bowire-placeholder-slot' });
                wrapper.appendChild(placeholderSlot);
                var cleanup = fw.mountWidgetsForMethod(
                    selectedService.name, selectedMethod.name, placeholderSlot);
                if (typeof cleanup === 'function') {
                    bowireWidgetUnmounts.push(cleanup);
                }
                return wrapper;
            }

            return renderResponseTabbedWithWidget(
                outputElement, splitKindExt, saved);
        }

        // Split mode — same construction as the streaming pane: JSON
        // output on the left, widget pane on the right, sharing a
        // resizable divider. The widget pane keeps its own header so
        // the maximize toggle and layout cycle work the same way
        // they do in streaming mode.
        disposeWidgetMounts();
        // Stable id so morphdom matches by key when re-rendering. The
        // tab-mode counterpart uses `bowire-response-widget-host`;
        // distinct ids force morphdom to replace the wrapper subtree
        // wholesale on a Tab ↔ Split transition rather than trying to
        // merge a leftSlot div with a tab strip and stranding live
        // onClick listeners on the wrong nodes (Bug 2).
        var host = el('div', {
            id: 'bowire-response-widget-host-split',
            className: 'bowire-widget-split-host'
        });
        var pane = layout.createSplitPane(host, {
            orientation: saved.mode === 'split-vertical' ? 'vertical' : 'horizontal',
            initialRatio: typeof saved.ratio === 'number' ? saved.ratio : 0.5,
            storageKey: 'bowire_widget_split_ratio:' + splitKindExt.id
        });
        bowireWidgetUnmounts.push(function () { pane.dispose(); });

        pane.firstSlot.appendChild(outputElement);

        var widgetPane = el('div', {
            className: 'bowire-widget-pane' + (widgetPaneMaximized ? ' is-maximized' : '')
        });
        var widgetHeader = el('div', { className: 'bowire-widget-pane-header' },
            el('span', {
                className: 'bowire-widget-pane-header-title',
                textContent: (splitKindExt.viewer && splitKindExt.viewer.label) || splitKindExt.kind
            }),
            el('button', {
                className: 'bowire-widget-pane-maximize',
                title: widgetPaneMaximized
                    ? 'Restore widget to its slot (Esc)'
                    : 'Maximize widget to fill the window',
                onClick: function () {
                    widgetPaneMaximized = !widgetPaneMaximized;
                    var p = document.querySelector('.bowire-widget-pane');
                    if (p) p.classList.toggle('is-maximized', widgetPaneMaximized);
                    this.innerHTML = bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize');
                    this.title = widgetPaneMaximized
                        ? 'Restore widget to its slot (Esc)'
                        : 'Maximize widget to fill the window';
                },
                innerHTML: bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize')
            }),
            el('button', {
                className: 'bowire-widget-layout-toggle',
                title: 'Toggle layout (Tab ↔ Split)',
                onClick: function () {
                    var nextMode = layout.cycleLayoutMode(saved.mode, splitKindExt.kind);
                    layout.saveWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, {
                        mode: nextMode,
                        ratio: pane.getRatio()
                    });
                    render();
                },
                innerHTML: bowireLayoutIcon('layout-split')
            })
        );
        var widgetBody = el('div', { className: 'bowire-widget-pane-body' });
        widgetPane.appendChild(widgetHeader);
        widgetPane.appendChild(widgetBody);
        pane.secondSlot.appendChild(widgetPane);

        var widgetCleanup = fw.mountWidgetsForMethod(
            selectedService.name, selectedMethod.name, widgetBody);
        if (typeof widgetCleanup === 'function') {
            bowireWidgetUnmounts.push(widgetCleanup);
        }

        return host;
    }

    /**
     * Tab mode for the response pane. Builds a strip of sub-tabs
     * (JSON / Map) on top of the response body and switches between
     * them via the `widgetActiveTab` module state. The Map tab
     * mounts the registered viewer via `mountWidgetsForMethod` so
     * the same code path that produces map pins in split mode runs
     * here too — the only difference is the tab strip + body
     * swap.
     *
     * Both tab bodies stay mounted simultaneously, with the inactive
     * one hidden via `display: none`. That preserves the MapLibre
     * WebGL canvas + camera state across tab switches (re-mounting
     * the viewer on every switch would tear down + rebuild MapLibre
     * — defeats the point of a live widget).
     *
     * `outputElement` is the primary content (JSON tree for unary,
     * Wireshark-style streaming pane for streams). It always lands
     * in the first tab. The widget viewer mounts into the second
     * tab's body.
     */
    function renderResponseTabbedWithWidget(outputElement, splitKindExt, saved) {
        var fw = window.__bowireExtFramework;
        var layout = window.__bowireLayout;
        var widgetLabel = (splitKindExt.viewer && splitKindExt.viewer.label)
            || splitKindExt.kind;
        // Bug 2 — defensive guard against a corrupted `widgetActiveTab`.
        // If the operator round-trips Tab → Split → Tab the module
        // state survives the render(s), but the tab-body display
        // flips below silently strand the user when the value is
        // neither 'json' nor 'widget' (both bodies inline-styled to
        // `display: none`, no clickable tab strip — operator reports
        // "JSON disappears AND can't switch back"). Coerce to the
        // canonical default before any DOM is built.
        if (widgetActiveTab !== 'json' && widgetActiveTab !== 'widget') {
            widgetActiveTab = 'json';
        }

        // Stable id on the host so morphdom replaces the split-pane
        // wrapper instead of trying to merge its slot subtree into
        // the tab strip. Without this the operator's Split → Unsplit
        // round-trip occasionally left dead onClick closures attached
        // to position-matched nodes (split's leftSlot div repurposed
        // as the tab strip), and tab clicks went nowhere.
        var host = el('div', {
            id: 'bowire-response-widget-host',
            className: 'bowire-widget-tabbed'
        });

        // ---- Tab strip ----
        var strip = el('div', { className: 'bowire-widget-tab-strip' });

        function makeTabBtn(name, label) {
            return el('button', {
                type: 'button',
                className: 'bowire-widget-tab'
                    + (widgetActiveTab === name ? ' is-active' : ''),
                'data-widget-tab': name,
                onClick: function () {
                    if (widgetActiveTab === name) return;
                    widgetActiveTab = name;
                    // Direct DOM swap rather than render() so the
                    // MapLibre canvas keeps its WebGL context and
                    // camera state across tab switches. A full
                    // render() would tear down + re-mount the
                    // viewer, defeating the whole point of holding
                    // both bodies in the DOM at once.
                    var allBodies = host.querySelectorAll('.bowire-widget-tab-body');
                    var allTabs = host.querySelectorAll('.bowire-widget-tab');
                    for (var i = 0; i < allBodies.length; i++) {
                        var bn = allBodies[i].getAttribute('data-widget-tab') || '';
                        allBodies[i].style.display = bn === name ? '' : 'none';
                    }
                    for (var j = 0; j < allTabs.length; j++) {
                        var tn = allTabs[j].getAttribute('data-widget-tab') || '';
                        allTabs[j].classList.toggle('is-active', tn === name);
                    }
                    // The map widget's ResizeObserver fires on the
                    // first visibility flip (display:none → '') and
                    // calls map.resize() automatically — same path
                    // the split-pane drag relies on. No explicit
                    // resize call needed here.
                }
            }, label);
        }
        strip.appendChild(makeTabBtn('json', 'JSON'));
        strip.appendChild(makeTabBtn('widget', widgetLabel));

        // Spacer pushes the layout-toggle to the far right of the
        // strip — visual parity with the split-pane header.
        strip.appendChild(el('span', { className: 'bowire-widget-tab-strip-spacer' }));

        // Layout-toggle button — same shape as the one on the
        // split-pane header. Without this affordance the user has
        // no way back to split mode once they're on the tab layout
        // (Issue 1 + Issue 3 — the toggle stayed reachable through
        // the streaming-toolbar shortcut, but the unary path had no
        // toolbar so the user was stuck).
        strip.appendChild(el('button', {
            className: 'bowire-widget-layout-toggle',
            title: 'Toggle layout (Tab ↔ Split)',
            onClick: function () {
                var nextMode = layout.cycleLayoutMode(
                    saved.mode, splitKindExt.kind);
                layout.saveWidgetLayout(
                    selectedService.name, selectedMethod.name,
                    splitKindExt.id, {
                        mode: nextMode,
                        ratio: typeof saved.ratio === 'number' ? saved.ratio : 0.5
                    });
                render();
            },
            innerHTML: bowireLayoutIcon('layout-split')
        }));

        host.appendChild(strip);

        // ---- Tab bodies ----
        // JSON body — the caller's outputElement (JSON tree for
        // unary, streaming pane for streams) goes here verbatim.
        var jsonBody = el('div', {
            className: 'bowire-widget-tab-body',
            'data-widget-tab': 'json'
        });
        jsonBody.style.display = widgetActiveTab === 'json' ? '' : 'none';
        jsonBody.appendChild(outputElement);
        host.appendChild(jsonBody);

        // Widget body — the viewer mounts here. We always create the
        // slot (even on the JSON tab) so the widget starts streaming
        // frames immediately and is hot the moment the user clicks
        // the Map tab. Otherwise the operator would get a 200ms
        // basemap-load delay every time they switch tabs.
        var widgetBody = el('div', {
            className: 'bowire-widget-tab-body bowire-widget-pane-body',
            'data-widget-tab': 'widget'
        });
        widgetBody.style.display = widgetActiveTab === 'widget' ? '' : 'none';
        host.appendChild(widgetBody);

        var widgetCleanup = fw.mountWidgetsForMethod(
            selectedService.name, selectedMethod.name, widgetBody);
        if (typeof widgetCleanup === 'function') {
            bowireWidgetUnmounts.push(widgetCleanup);
        }

        return host;
    }

    /**
     * Render the small toolbar button that appears in the streaming
     * pane's toolbar when the active method has a split-eligible
     * widget AND the user has currently opted into `tab` mode (no
     * widget pane → no header → toolbar is the only place left to
     * surface the toggle). Returns null in every other case so the
     * caller can skip appending.
     */
    function renderStreamingToolbarLayoutToggle() {
        var fw = window.__bowireExtFramework;
        var layout = window.__bowireLayout;
        if (!fw || !layout || !selectedService || !selectedMethod) return null;
        var splitKindExt = (typeof fw.preferredSplitExtensionForMethod === 'function')
            ? fw.preferredSplitExtensionForMethod(
                selectedService.name, selectedMethod.name)
            : null;
        if (!splitKindExt) return null;
        var saved = layout.loadWidgetLayout(
            selectedService.name, selectedMethod.name, splitKindExt.id, splitKindExt.kind);
        if (saved.mode !== 'tab') return null;
        return el('button', {
            className: 'bowire-widget-layout-toggle',
            title: 'Switch to split layout (' + (splitKindExt.viewer && splitKindExt.viewer.label || splitKindExt.kind) + ')',
            onClick: function () {
                layout.saveWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, {
                    mode: 'split-horizontal',
                    ratio: typeof saved.ratio === 'number' ? saved.ratio : 0.5
                });
                render();
            },
            innerHTML: bowireLayoutIcon('layout-split')
        });
    }

    /**
     * Inline-SVG layout icons. Two states: tab (stacked rectangles)
     * and split (side-by-side rectangles). Matches the visual style
     * of svgIcon() in helpers.js (16x16 viewBox, currentColor,
     * 2px stroke) so the workbench's icon family stays consistent.
     */
    function bowireLayoutIcon(name) {
        switch (name) {
            case 'layout-tab':
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">'
                    + '<rect x="3" y="5" width="18" height="14" rx="2"/>'
                    + '<line x1="3" y1="9" x2="21" y2="9"/>'
                    + '</svg>';
            case 'maximize':
                // Four outward-pointing brackets — universal "expand" glyph.
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
                    + '<polyline points="4 9 4 4 9 4"/>'
                    + '<polyline points="20 9 20 4 15 4"/>'
                    + '<polyline points="20 15 20 20 15 20"/>'
                    + '<polyline points="4 15 4 20 9 20"/>'
                    + '</svg>';
            case 'minimize':
                // Four inward-pointing brackets — the inverse of maximize.
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
                    + '<polyline points="9 4 9 9 4 9"/>'
                    + '<polyline points="15 4 15 9 20 9"/>'
                    + '<polyline points="15 20 15 15 20 15"/>'
                    + '<polyline points="9 20 9 15 4 15"/>'
                    + '</svg>';
            case 'layout-split':
            default:
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">'
                    + '<rect x="3" y="5" width="18" height="14" rx="2"/>'
                    + '<line x1="12" y1="5" x2="12" y2="19"/>'
                    + '</svg>';
        }
    }

    // -------------------------------------------------------------------
    // Response-tree extension hooks (Phase 4.1)
    //
    // The core workbench owns the JSON-tree DOM but defers per-kind
    // decoration + context-menu items to extensions. The map widget
    // registers a decorator that stamps data-bowire-coord-path on
    // lat/lon spans + wires hover sync, and a menu contributor that
    // returns a "Center on map" entry when the right-clicked span
    // belongs to a resolved coord pair. Image/chart/audio viewers
    // would plug in the same way without core ever importing
    // kind-specific code.
    //
    // What this fragment owns:
    //   - bowireWireResponseTreeGestures(treeRoot)
    //       click toggle via native <details>, dblclick = Copy path,
    //       right-click = unified menu
    //   - bowireOpenResponseTreeContextMenu(x, y, ctx)
    //       builds Copy ${response.X} + Copy path (the kind-agnostic
    //       essentials) then appends each entry the extension hooks
    //       return
    //   - bowireDecorateResponseTreeViaExtensions(treeRoot, service,
    //       method, explicitRoot)
    //       fans the decorator hook out for every registered extension
    //
    // What lives in extensions (Kuestenlogik.Bowire.Map et al.):
    //   - per-kind data-attribute stamping
    //   - per-kind hover sync
    //   - per-kind menu items (label + action + meta)
    // -------------------------------------------------------------------

    /**
     * Fan out the response-tree decorator hook to every extension
     * that registered one. Each registered callback runs against the
     * same `(treeRoot, service, method, explicitRoot)` context and
     * is free to query `/api/semantics/effective` (cached or fetched)
     * to find the kinds it cares about. Failures are isolated by
     * the framework's try/catch so one misbehaving extension
     * doesn't break the rest of the response tree.
     */
    function bowireDecorateResponseTreeViaExtensions(treeRoot, service, method, explicitRoot) {
        if (!treeRoot) return;
        var fw = window.__bowireExtFramework;
        if (!fw || typeof fw.runResponseTreeDecorators !== 'function') return;
        fw.runResponseTreeDecorators({
            treeRoot: treeRoot,
            service: service,
            method: method,
            explicitRoot: explicitRoot
        });
    }

    /**
     * One-shot install — listen for `bowire:map-coord-click` (the
     * map widget dispatches this on pin click), find the matching
     * row in whichever JSON viewer is currently visible, auto-expand
     * collapsed ancestors, and scroll the row into view.
     *
     * Lives in core (not the map bundle) so the same listener
     * services future image/chart viewers that might want the same
     * "click → centre JSON" gesture without each bundle re-implementing
     * the path walk + scroll. The event payload only carries chain-form
     * paths — the map widget converts JSONPath → chain form before
     * dispatching, see `bowireJsonPathToChainPath`.
     */
    (function bowireInstallMapCoordClickListener() {
        if (window.__bowireMapCoordClickInstalled) return;
        window.__bowireMapCoordClickInstalled = true;
        document.addEventListener('bowire:map-coord-click', function (e) {
            var detail = e && e.detail;
            if (!detail) return;
            // Prefer the parent container path so the viewer scrolls
            // to the coordinate object opener line (not just lat OR
            // lon). Falls back to lat path when parent is missing.
            var target = detail.parentPath
                || detail.latPath
                || detail.lonPath
                || '';
            if (!target) return;
            // Find every JSON viewer currently mounted — there might
            // be more than one when split mode shows both streaming
            // detail + an extension pane on the same page. Try each
            // until one of them resolves the path.
            var viewers = document.querySelectorAll('.bowire-json-viewer');
            for (var i = 0; i < viewers.length; i++) {
                // Only consider viewers that are actually visible —
                // tab-mode hides the inactive body via display:none.
                var v = viewers[i];
                if (v.offsetParent === null) continue;
                if (bowireScrollJsonViewerToPath(v, target)) return;
            }
        });
    })();

    // -------------------------------------------------------------------
    // Unified response-tree gestures (click / dblclick / contextmenu)
    //
    // Replaces the legacy "click = copy ${response.X}, dblclick on a
    // key = copy path" overlay with the operator-expected shape:
    //   - Click on a `<details>` summary toggles the node (native
    //     <details> contract). No JS overlay needed.
    //   - Double-click on any `[data-json-path]` span copies the
    //     bare JSONPath of that node.
    //   - Right-click opens a unified menu:
    //         Copy ${response.X}
    //         Copy path
    //         ── Center on map  (only when the map plugin is loaded
    //                            AND the span participates in a
    //                            resolved coord pair)
    //
    // The semantics-menu (Reinterpret as ▸ / Suppress / Fuzz / etc.)
    // attaches a per-label contextmenu listener via
    // bowireMountSemanticsContextMenu — its handler calls
    // e.stopPropagation() so the unified menu doesn't double-fire
    // on label spans. Operator gets the semantics menu on keys, the
    // unified menu on values.
    // -------------------------------------------------------------------

    /**
     * Attach the universal dblclick + contextmenu handlers to a
     * response-tree root. Idempotent: re-running on the same node
     * is a no-op so a render() pass that re-uses the DOM doesn't
     * stack listeners.
     *
     * The handlers read the active service/method off the module
     * state at event-time — that's correct because morphdom can
     * preserve a tree node across method switches; binding the
     * service/method at attach-time would let stale closures
     * resolve against the wrong method's annotation set. Same
     * shape extension decorators use for hover delegation.
     */
    function bowireWireResponseTreeGestures(treeRoot) {
        if (!treeRoot || treeRoot.__bowireRespTreeGesturesMounted) return;
        treeRoot.__bowireRespTreeGesturesMounted = true;

        // Double-click → Copy path.
        treeRoot.addEventListener('dblclick', function (e) {
            var target = e.target && e.target.closest
                ? e.target.closest('[data-json-path]')
                : null;
            if (!target) return;
            e.preventDefault();
            e.stopPropagation();
            var raw = target.getAttribute('data-json-path') || '';
            if (!raw) return;
            try {
                navigator.clipboard.writeText(raw).then(
                    function () { toast('Copied path: ' + raw, 'success'); },
                    function () { toast('Copy failed', 'error'); }
                );
            } catch { /* clipboard API unavailable — silent */ }
        });

        // Right-click → unified menu.
        treeRoot.addEventListener('contextmenu', function (e) {
            var target = e.target && e.target.closest
                ? e.target.closest('[data-json-path]')
                : null;
            if (!target) return;
            // Skip when the right-click landed on a label span —
            // semantics-menu attaches its own contextmenu listener
            // per label and calls stopPropagation, so we wouldn't
            // reach this branch normally. The guard is belt-and-
            // braces for the case where semantics-menu hasn't
            // mounted yet (decorate-async race).
            if (target.classList
                && target.classList.contains('bowire-json-tree-label')) {
                return;
            }
            // Reading service/method off module state at event time
            // (rather than capturing in the closure) keeps the menu
            // honest across method switches — see comment on
            // bowireWireResponseTreeGestures.
            var svc = (typeof selectedService !== 'undefined' && selectedService)
                ? selectedService.name : null;
            var mth = (typeof selectedMethod !== 'undefined' && selectedMethod)
                ? selectedMethod.name : null;
            e.preventDefault();
            e.stopPropagation();
            bowireOpenResponseTreeContextMenu(e.clientX, e.clientY, {
                target: target,
                treeRoot: treeRoot,
                jsonPath: target.getAttribute('data-json-path') || '',
                service: svc,
                method: mth
            });
        });
    }

    /**
     * Build and position the unified context menu. Same DOM shape
     * as semantics-menu so the workbench's existing CSS rules
     * cover it without bespoke styling — only the menu-meta line
     * (under "Center on map") adds a small footer.
     */
    function bowireOpenResponseTreeContextMenu(x, y, ctx) {
        // Tear down any previous instance so rapid right-clicks
        // don't pile up popups.
        var existing = document.querySelector('.bowire-response-tree-menu');
        if (existing && existing.parentNode) existing.parentNode.removeChild(existing);

        var menu = document.createElement('div');
        menu.className = 'bowire-semantics-menu bowire-response-tree-menu';
        menu.setAttribute('role', 'menu');
        menu.style.position = 'fixed';
        menu.style.left = x + 'px';
        menu.style.top = y + 'px';
        menu.style.zIndex = '10000';

        function close() {
            document.removeEventListener('keydown', onKey);
            document.removeEventListener('click', onClick);
            if (menu.parentNode) menu.parentNode.removeChild(menu);
        }
        function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); close(); } }
        function onClick(e) { if (!menu.contains(e.target)) close(); }

        function addItem(label, onActivate) {
            var item = document.createElement('button');
            item.type = 'button';
            item.className = 'bowire-semantics-menu-item';
            item.setAttribute('role', 'menuitem');
            item.textContent = label;
            item.addEventListener('click', function () {
                try { onActivate(); } catch (err) { console.error('[bowire-resp-menu]', err); }
                close();
            });
            menu.appendChild(item);
            return item;
        }

        // Item 1 — Copy ${response.X}
        var chainVar = ctx.jsonPath
            ? '${response.' + ctx.jsonPath + '}'
            : '${response}';
        addItem('Copy ' + chainVar, function () {
            navigator.clipboard.writeText(chainVar).then(
                function () { toast('Copied: ' + chainVar, 'success'); },
                function () { toast('Copy failed', 'error'); }
            );
        });

        // Item 2 — Copy path
        if (ctx.jsonPath) {
            addItem('Copy path (' + ctx.jsonPath + ')', function () {
                navigator.clipboard.writeText(ctx.jsonPath).then(
                    function () { toast('Copied path: ' + ctx.jsonPath, 'success'); },
                    function () { toast('Copy failed', 'error'); }
                );
            });
        }

        // Extension-contributed items — every registered
        // contributor returns an array of `{ label, action, meta?,
        // divider? }` entries. A `divider: true` entry inserts a
        // visual rule between groups. `meta` (a short footer line
        // like "50.0123, 8.5621") renders directly under the most
        // recently appended item. The core never names a specific
        // extension here — Kuestenlogik.Bowire.Map registers the
        // "Center on map" entry from inside its own bundle.
        var fw = window.__bowireExtFramework;
        var contributed = (fw && typeof fw.collectResponseTreeMenuItems === 'function')
            ? fw.collectResponseTreeMenuItems({
                target: ctx.target,
                treeRoot: ctx.treeRoot,
                jsonPath: ctx.jsonPath,
                service: ctx.service,
                method: ctx.method
            })
            : [];
        // Only emit a leading divider when the contributed batch
        // actually has at least one renderable item — otherwise the
        // menu ends with an orphan rule.
        var rendered = 0;
        for (var i = 0; i < contributed.length; i++) {
            var entry = contributed[i];
            if (!entry) continue;
            if (entry.divider) {
                var hr = document.createElement('div');
                hr.className = 'bowire-semantics-menu-divider';
                menu.appendChild(hr);
                continue;
            }
            if (!entry.label || typeof entry.action !== 'function') continue;
            if (rendered === 0) {
                var leadHr = document.createElement('div');
                leadHr.className = 'bowire-semantics-menu-divider';
                menu.appendChild(leadHr);
            }
            addItem(entry.label, entry.action);
            if (entry.meta) {
                var metaEl = document.createElement('div');
                metaEl.className = 'bowire-semantics-menu-meta';
                metaEl.textContent = String(entry.meta);
                menu.appendChild(metaEl);
            }
            rendered++;
        }

        document.body.appendChild(menu);

        // Viewport clamp — same math as semantics-menu.
        var vw = window.innerWidth, vh = window.innerHeight;
        var rect = menu.getBoundingClientRect();
        if (rect.right > vw) menu.style.left = Math.max(8, vw - rect.width - 8) + 'px';
        if (rect.bottom > vh) menu.style.top = Math.max(8, vh - rect.height - 8) + 'px';

        document.addEventListener('keydown', onKey);
        // Defer the click-out listener by one tick so the same
        // mouse-up that triggered the menu's items doesn't close
        // it immediately.
        setTimeout(function () { document.addEventListener('click', onClick); }, 0);
    }

    function appendStreamMessage(parsed) {
        // Fast-path called from sseSource.onmessage / channel onmessage AFTER
        // the message has been pushed onto streamMessages. Returns true when
        // the structure exists and the append succeeded; false means the
        // caller should fall back to a full render() (typically the very
        // first message of a stream).
        var output = document.getElementById('bowire-stream-output');
        if (!output) return false;
        var list = document.getElementById('bowire-stream-list');
        if (!list) return false;

        var idx = streamMessages.length - 1;
        var msg = streamMessages[idx];
        // Filtered out: skip the DOM append but still update the count
        // and (if auto-scroll is on) the detail pane — the user should
        // still see "X / Y" climb so they know the stream is alive.
        var matches = streamMessageMatchesFilter(msg);
        if (matches) {
            var item = buildStreamListItem(msg, idx);
            list.appendChild(item);
        }

        // Update count badge — when a filter is active show "X / Y".
        var count = document.getElementById('bowire-stream-count');
        if (count) {
            var hasFilter = (streamFilterQuery || '').trim().length > 0;
            if (hasFilter) {
                var visible = streamMessages.filter(streamMessageMatchesFilter).length;
                count.textContent = visible + ' / ' + streamMessages.length + ' messages';
            } else {
                count.textContent = streamMessages.length + (streamMessages.length === 1 ? ' message' : ' messages');
            }
        }
        // Refresh the state badge so the operator sees "Receiving" /
        // "N msgs" climb without waiting for the 1 s ticker. Surgical
        // replace keeps the streaming pane DOM otherwise intact.
        var badge = document.getElementById('bowire-stream-state-badge');
        if (badge && selectedService && selectedMethod) {
            var fresh = renderSubscriptionBadge(
                selectedService.name, selectedMethod.name, streamMessages.length);
            badge.replaceWith(fresh);
        }

        if (streamAutoScroll) {
            // Follow latest: shift selection forward and refresh the detail pane.
            streamSelectedIndex = null;
            updateStreamDetail();
            updateStreamSelection();
            scrollStreamListToBottom();
        }
        return true;
    }

    function selectStreamMessage(idx, fromUserClick) {
        if (idx < 0 || idx >= streamMessages.length) return;
        streamSelectedIndex = idx;
        if (fromUserClick && streamAutoScroll) {
            // The user took control — stop following the latest.
            setStreamAutoScroll(false);
        }
        updateStreamDetail();
        updateStreamSelection();
    }

    // ---- Phase 3.1: multi-select handler + snapshot dispatch ----
    //
    // Three click modes, matching what every Wireshark-style UI does:
    //   plain click       → replace selection with this one frame
    //   Ctrl/Cmd click    → toggle membership
    //   Shift click       → extend a contiguous range from the anchor
    //                       (the last single-clicked row)
    // The detail pane keeps tracking the most recently clicked row —
    // that's `streamSelectedIndex`, set by selectStreamMessage. The
    // multi-select set lives in `streamSelectedIds` and is read by the
    // map widget through the `ctx.selection$` async iterable. We emit
    // ONE `bowire:frames-selection-changed` event per logical change
    // (carrying the full N-snapshot) so widgets don't have to
    // accumulate state — see selection-stream wiring in extensions.js.
    function handleStreamFrameClick(idx, e) {
        if (idx < 0 || idx >= streamMessages.length) return;
        var msg = streamMessages[idx];
        var id = msg && msg.id;

        if (e && (e.ctrlKey || e.metaKey) && id != null) {
            // Toggle this frame in/out of the selected set. The detail
            // pane still snaps to whatever the user clicked so the
            // single-frame inspection flow keeps working.
            if (streamSelectedIds.has(id)) streamSelectedIds.delete(id);
            else streamSelectedIds.add(id);
            streamSelectionAnchorIdx = idx;
            selectStreamMessage(idx, true);
            dispatchFramesSelectionSnapshot();
            refreshMultiSelectClasses();
            return;
        }

        if (e && e.shiftKey && streamSelectionAnchorIdx != null) {
            // Range select between anchor and clicked index. Replaces
            // the current set rather than additive-extending — most
            // users expect "Shift defines the new range from anchor".
            var lo = Math.min(streamSelectionAnchorIdx, idx);
            var hi = Math.max(streamSelectionAnchorIdx, idx);
            streamSelectedIds = new Set();
            for (var i = lo; i <= hi; i++) {
                var rid = streamMessages[i] && streamMessages[i].id;
                if (rid != null) streamSelectedIds.add(rid);
            }
            selectStreamMessage(idx, true);
            dispatchFramesSelectionSnapshot();
            refreshMultiSelectClasses();
            return;
        }

        // Plain click — replace selection with just this frame.
        if (id != null) {
            streamSelectedIds = new Set([id]);
        } else {
            streamSelectedIds = new Set();
        }
        streamSelectionAnchorIdx = idx;
        selectStreamMessage(idx, true);
        dispatchFramesSelectionSnapshot();
        refreshMultiSelectClasses();
    }

    /**
     * Send the current snapshot to the extension framework. The
     * framework re-broadcasts it as a `bowire:frames-selection-changed`
     * DOM event, which every active widget's `ctx.selection$` iterable
     * pulls from. ONE event per logical change — never one per delta.
     */
    function dispatchFramesSelectionSnapshot() {
        if (!window.__bowireExtFramework
            || typeof window.__bowireExtFramework.recordSelectionSnapshot !== 'function') {
            return;
        }
        var ids = [];
        streamSelectedIds.forEach(function (v) { ids.push(v); });
        window.__bowireExtFramework.recordSelectionSnapshot(ids);
    }

    /**
     * Surgically toggle the .multi-selected class on each list item.
     * We don't go through the full render() path because the
     * Streaming-Frames pane is on the hot path (one row per frame, up
     * to thousands per second); the existing append fast-path skips
     * render() entirely. Class-toggling matches that performance
     * envelope and keeps morphdom out of the loop.
     */
    function refreshMultiSelectClasses() {
        var list = document.getElementById('bowire-stream-list');
        if (!list) return;
        var children = list.children;
        for (var i = 0; i < children.length; i++) {
            var c = children[i];
            var fid = c && c.dataset ? c.dataset.frameId : '';
            if (fid && streamSelectedIds.has(fid)) c.classList.add('multi-selected');
            else c.classList.remove('multi-selected');
        }
    }

    function updateStreamDetail() {
        var pane = document.getElementById('bowire-stream-detail-pane');
        if (!pane) return;
        var idx = streamEffectiveIndex();
        if (idx < 0) return;
        var built = buildStreamDetailContent(streamMessages[idx], idx);
        pane.replaceChildren(built.header, built.body);
    }

    function updateStreamSelection() {
        var list = document.getElementById('bowire-stream-list');
        if (!list) return;
        var sel = streamEffectiveIndex();
        // Single-point remove + single-point add. Avoids walking the entire
        // list on every message (O(N) → O(1) for long streams) and
        // guarantees only one node ever carries .selected at a time — no
        // in-between state where multiple siblings look highlighted.
        var currentSelected = list.querySelector('.bowire-stream-list-item.selected');
        if (currentSelected) currentSelected.classList.remove('selected');
        if (sel >= 0) {
            var target = list.children[sel];
            if (target && target.classList) target.classList.add('selected');
        }
    }

    function scrollStreamListToBottom() {
        var pane = document.getElementById('bowire-stream-list-pane');
        if (!pane) return;
        // Mark the next scroll event as programmatic so the abandon-detection
        // listener doesn't switch off auto-scroll.
        streamProgrammaticScroll = true;
        pane.scrollTop = pane.scrollHeight;
    }

    function setStreamAutoScroll(on) {
        streamAutoScroll = !!on;
        var btn = document.getElementById('bowire-stream-autoscroll-btn');
        if (btn) {
            btn.classList.toggle('is-on', streamAutoScroll);
            btn.title = streamAutoScroll
                ? 'Following the latest message \u2014 click to pin the current selection'
                : 'Pinned to current selection \u2014 click to follow the latest';
            var label = btn.querySelector('span');
            if (label) label.textContent = streamAutoScroll ? '\u25C9 Follow latest' : '\u25CB Pinned';
        }
        if (streamAutoScroll) {
            streamSelectedIndex = null;
            updateStreamDetail();
            updateStreamSelection();
            scrollStreamListToBottom();
        }
    }

    var streamProgrammaticScroll = false;
    function attachStreamScrollListener() {
        var pane = document.getElementById('bowire-stream-list-pane');
        if (!pane || pane.dataset.scrollHooked === '1') return;
        pane.dataset.scrollHooked = '1';
        pane.addEventListener('scroll', function () {
            if (streamProgrammaticScroll) {
                streamProgrammaticScroll = false;
                return;
            }
            // Slack/Discord-style: scrolling away from the bottom abandons
            // auto-scroll; scrolling back to the bottom re-enables it.
            var atBottom = (pane.scrollHeight - pane.scrollTop - pane.clientHeight) < 6;
            if (!atBottom && streamAutoScroll) {
                setStreamAutoScroll(false);
            } else if (atBottom && !streamAutoScroll) {
                setStreamAutoScroll(true);
            }
        });
    }

    function attachStreamSplitterDrag() {
        var splitter = document.getElementById('bowire-stream-splitter');
        var output = document.getElementById('bowire-stream-output');
        if (!splitter || !output || splitter.dataset.dragHooked === '1') return;
        splitter.dataset.dragHooked = '1';
        splitter.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();
            document.body.style.cursor = 'ns-resize';
            document.body.style.userSelect = 'none';

            // Freeze auto-scroll for the duration of the drag. Without this,
            // new messages arriving mid-drag would shift the list under the
            // cursor and move the selection highlight — the user perceives
            // this as "the splitter jumped one entry down". Restore the
            // original state on mouseup so auto-scroll picks up right where
            // it was (and catches up with any messages queued during drag).
            var autoScrollBefore = streamAutoScroll;
            streamAutoScroll = false;

            var rect = output.getBoundingClientRect();
            function onMove(ev) {
                var rel = (ev.clientY - rect.top) / rect.height;
                var pct = Math.max(10, Math.min(90, Math.round(rel * 100)));
                streamListSizePct = pct;
                output.style.setProperty('--bowire-stream-list-pct', pct + '%');
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                // Restore auto-scroll. If it was on before, catch up to the
                // latest message that arrived while we were dragging so the
                // user doesn't end up with a stale detail pane.
                if (autoScrollBefore) {
                    streamAutoScroll = true;
                    var btn = document.getElementById('bowire-stream-autoscroll-btn');
                    if (btn) btn.classList.add('is-on');
                    streamSelectedIndex = null;
                    updateStreamDetail();
                    updateStreamSelection();
                    scrollStreamListToBottom();
                }
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    }

    // Expose the append fast-path so api.js / protocols.js can call it from
    // their respective onmessage handlers without going through render().
    window.bowireAppendStreamMessage = appendStreamMessage;

    // Idle-tick re-render: every second the subscription registry
    // notifies listeners so the state badge can flip from Receiving →
    // Idle without waiting on a frame. We only touch the badge in
    // place — the rest of the streaming pane is left alone.
    onSubscriptionsChanged(function () {
        if (!selectedService || !selectedMethod) return;
        var badge = document.getElementById('bowire-stream-state-badge');
        if (!badge) return;
        var fresh = renderSubscriptionBadge(
            selectedService.name, selectedMethod.name, streamMessages.length);
        badge.replaceWith(fresh);
    });
    ensureSubscriptionTicker();

    function renderResponsePane() {
        // ID includes the selected method so morphdom fully replaces the
        // pane when switching methods instead of reusing stale DOM with
        // wrong closures from the previous method.
        var resMethodKey = (selectedService ? selectedService.name : '')
            + '-' + (selectedMethod ? selectedMethod.name : '');
        const pane = el('div', { id: 'bowire-response-pane-' + resMethodKey, className: 'bowire-pane' });

        // Pane heading — mirrors the "Request" label on the left so
        // the seam between the two halves reads at a glance.
        pane.appendChild(el('div', { className: 'bowire-pane-heading', textContent: 'Response' }));

        // Tabs
        const tabs = el('div', { id: 'bowire-response-tabs', className: 'bowire-tabs' });
        tabs.appendChild(el('div', {
            id: 'bowire-response-tab-response',
            className: `bowire-tab ${activeResponseTab === 'response' ? 'active' : ''}`,
            textContent: 'Response',
            onClick: function () { activeResponseTab = 'response'; render(); }
        }));
        tabs.appendChild(el('div', {
            id: 'bowire-response-tab-headers',
            className: `bowire-tab ${activeResponseTab === 'headers' ? 'active' : ''}`,
            textContent: 'Response Metadata',
            onClick: function () { activeResponseTab = 'headers'; render(); }
        }));
        // Tests tab — only meaningful for unary methods. The
        // Performance tab was retired here: load runs are an action
        // ("repeat this request N times under concurrency C") rather
        // than a response property, so they belong in the Benchmarks
        // rail. Use "+ Add to… → Create benchmark" in the method
        // header — that path is gated on a successful last call so
        // only known-good requests become benchmark specs.
        if (selectedMethod && selectedMethod.methodType === 'Unary') {
            // Test results tab \u2014 read-only pass/fail per assertion
            // against the most recent response. Definitions are
            // managed in the request pane's Tests sub-tab.
            var testsLabel = 'Test results';
            var summary = selectedService && selectedMethod
                ? getAssertionSummary(selectedService.name, selectedMethod.name)
                : null;
            if (summary && summary.total > 0) {
                if (summary.untested === summary.total) {
                    testsLabel = 'Test results (' + summary.total + ')';
                } else if (summary.failed > 0) {
                    testsLabel = 'Test results (' + summary.passed + '/' + summary.total + ' \u2717)';
                } else {
                    testsLabel = 'Test results (' + summary.passed + '/' + summary.total + ' \u2713)';
                }
            }
            tabs.appendChild(el('div', {
                id: 'bowire-response-tab-tests',
                className: `bowire-tab bowire-tab-tests ${activeResponseTab === 'tests' ? 'active' : ''}`
                    + (summary && summary.failed > 0 ? ' has-failure' : '')
                    + (summary && summary.failed === 0 && summary.passed === summary.total && summary.total > 0 ? ' all-pass' : ''),
                textContent: testsLabel,
                onClick: function () { activeResponseTab = 'tests'; render(); }
            }));
        }
        // AI moved out of the response-pane tab strip into a workbench-
        // wide drawer (#90). The drawer renders alongside the workbench
        // body, not under the response tabs — see renderUnifiedRightDrawer
        // in render-env-auth.js. Toggle button lives in the topbar.
        pane.appendChild(tabs);
        // Overflow popover — Response / Response Metadata / Test results
        // crowd a narrow pane. Same affordance as the request side.
        requestAnimationFrame(function () {
            var live = document.getElementById('bowire-response-tabs');
            if (live && typeof bowireWireTabOverflow === 'function') {
                bowireWireTabOverflow(live, { tabSelector: '.bowire-tab', label: 'More tabs' });
            }
        });

        // #114 Phase 2 — inline hint banners at the response surface.
        // Hints flagged with surface='response' (slow-response,
        // large-response, response-status-mismatch, …) land here so
        // the operator reads them at the place where the signal
        // originated, instead of having to flip to the Assistant
        // drawer. The drawer's list keeps the full unfiltered view.
        if (typeof renderInlineHintBanners === 'function') {
            try {
                var rBanners = renderInlineHintBanners('response');
                if (rBanners) pane.appendChild(rBanners);
            } catch (e) { console.warn('[hints] response banners failed', e); }
        }

        // Response tab
        const respContent = el('div', { className: `bowire-tab-content ${activeResponseTab === 'response' ? 'active' : ''}` });
        const respHeader = el('div', { className: 'bowire-pane-header' },
            el('span', { className: 'bowire-pane-title', textContent: 'Result' }),
            el('div', { className: 'bowire-pane-actions' },
                // Expand all / Collapse all — operate on the
                // <details> nodes the JSON tree renders. Hidden when
                // there is no response body or we're in streaming
                // mode (the stream pane has its own controls).
                (function () {
                    if (!responseData || streamMessages.length > 0) return el('span');
                    return el('button', {
                        id: 'bowire-response-tree-expand-btn',
                        className: 'bowire-pane-btn',
                        title: 'Expand all nodes',
                        textContent: 'Expand all',
                        onClick: function () {
                            var nodes = document.querySelectorAll('.bowire-response-output .bowire-json-tree-node');
                            nodes.forEach(function (n) { n.open = true; });
                        }
                    });
                })(),
                (function () {
                    if (!responseData || streamMessages.length > 0) return el('span');
                    return el('button', {
                        id: 'bowire-response-tree-collapse-btn',
                        className: 'bowire-pane-btn',
                        title: 'Collapse all nodes',
                        textContent: 'Collapse all',
                        onClick: function () {
                            var nodes = document.querySelectorAll('.bowire-response-output .bowire-json-tree-node');
                            nodes.forEach(function (n) {
                                // Leave the topmost root open so the
                                // tree still hints at its shape.
                                if (n.parentElement && n.parentElement.classList.contains('bowire-json-tree')) return;
                                n.open = false;
                            });
                        }
                    });
                })(),
                // Copy split-button. Primary click copies the response
                // body as-is; the caret opens a protocol-aware code-
                // export dropdown (curl / grpcurl / fetch / wscat / …)
                // sourced from code-export.js. Streaming responses also
                // get "selected / all messages" raw entries up top.
                (function () {
                    var wrapper = el('div', { id: 'bowire-response-copy-split', className: 'bowire-split-btn-wrap' });

                    function copyRawResponse() {
                        if (streamMessages.length > 0) {
                            var idx = streamEffectiveIndex();
                            var text = idx >= 0 ? streamMessageRaw(streamMessages[idx]) : '';
                            navigator.clipboard.writeText(text).then(function () {
                                toast(idx >= 0 ? ('Copied message #' + (idx + 1)) : 'Copied response', 'success');
                            });
                            return;
                        }
                        var t = responseData || '';
                        navigator.clipboard.writeText(t).then(function () {
                            toast('Copied to clipboard', 'success');
                        });
                    }

                    var mainBtn = el('button', {
                        id: 'bowire-response-copy-main-btn',
                        className: 'bowire-pane-btn bowire-split-btn-main',
                        title: 'Copy response body as-is',
                        textContent: 'Copy',
                        onClick: function () { copyRawResponse(); }
                    });
                    var caretBtn = el('button', {
                        id: 'bowire-response-copy-caret-btn',
                        className: 'bowire-pane-btn bowire-split-btn-caret',
                        title: 'Copy as code / command',
                        'aria-haspopup': 'menu',
                        textContent: '\u25BE',
                        onClick: function (e) {
                            e.stopPropagation();
                            var menu = wrapper.querySelector('.bowire-dropdown-menu');
                            if (menu) menu.classList.toggle('visible');
                        }
                    });

                    var menu = el('div', { className: 'bowire-dropdown-menu', role: 'menu' });

                    if (streamMessages.length > 0) {
                        menu.appendChild(el('div', {
                            className: 'bowire-dropdown-item',
                            textContent: 'Copy selected message',
                            onClick: function () {
                                var idx = streamEffectiveIndex();
                                var text = idx >= 0 ? streamMessageRaw(streamMessages[idx]) : '';
                                navigator.clipboard.writeText(text).then(function () {
                                    toast('Copied message #' + (idx + 1), 'success');
                                });
                                menu.classList.remove('visible');
                            }
                        }));
                        menu.appendChild(el('div', {
                            className: 'bowire-dropdown-item',
                            textContent: 'Copy all messages',
                            onClick: function () {
                                var text = streamMessages.map(function (m) {
                                    return streamMessageRaw(m);
                                }).join('\n');
                                navigator.clipboard.writeText(text).then(function () {
                                    toast('Copied ' + streamMessages.length + ' messages', 'success');
                                });
                                menu.classList.remove('visible');
                            }
                        }));
                        menu.appendChild(el('div', { className: 'bowire-dropdown-separator' }));
                    }

                    // Protocol-specific request-codegen snippets sourced
                    // from code-export.js: REST \u2192 curl/fetch/python,
                    // gRPC \u2192 grpcurl, websocket \u2192 wscat, \u2026 .
                    var langs = (typeof getCodeExportLanguages === 'function') ? getCodeExportLanguages() : [];
                    if (langs.length === 0) {
                        menu.appendChild(el('div', {
                            className: 'bowire-dropdown-item bowire-dropdown-item-disabled',
                            textContent: 'No code-export options for this protocol'
                        }));
                    } else {
                        for (var li = 0; li < langs.length; li++) {
                            (function (lang) {
                                menu.appendChild(el('div', {
                                    className: 'bowire-dropdown-item',
                                    textContent: 'Copy as ' + lang.label,
                                    onClick: function () {
                                        var snippet = (typeof generateCodeSnippet === 'function')
                                            ? generateCodeSnippet(lang.id)
                                            : '';
                                        navigator.clipboard.writeText(snippet).then(function () {
                                            toast('Copied ' + lang.label + ' snippet', 'success');
                                        });
                                        menu.classList.remove('visible');
                                    }
                                }));
                            })(langs[li]);
                        }
                    }

                    wrapper.appendChild(mainBtn);
                    wrapper.appendChild(caretBtn);
                    wrapper.appendChild(menu);
                    document.addEventListener('click', function (e) {
                        if (!wrapper.contains(e.target)) menu.classList.remove('visible');
                    });
                    return wrapper;
                })(),
                (function () {
                    // Download menu \u2014 protocol-aware so only formats
                    // that make sense for the current response shape
                    // are offered. JSON always available (every
                    // protocol's response is JSON-encoded in the
                    // workbench); Proto Binary only for gRPC where the
                    // wire bytes are meaningful; plain text fallback
                    // when the response isn't JSON. Items get unique
                    // id-prefixes so morphdom doesn't reuse a JSON-only
                    // protocol's items as Proto-Binary ones across a
                    // method swap.
                    var src = (selectedService && selectedService.source) || 'rest';
                    var formats = [];
                    formats.push({ id: 'json', label: 'JSON' });
                    if (src === 'grpc' || src === 'proto') {
                        formats.push({ id: 'proto', label: 'Proto Binary' });
                    }
                    var wrapper = el('div', { id: 'bowire-response-download-wrap', className: 'bowire-dropdown-wrapper' });
                    var btn = el('button', {
                        id: 'bowire-response-download-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Download \u25BE',
                        onClick: function (e) {
                            e.stopPropagation();
                            var menu = wrapper.querySelector('.bowire-dropdown-menu');
                            if (menu) menu.classList.toggle('visible');
                        }
                    });
                    var menu = el('div', { className: 'bowire-dropdown-menu' });
                    for (var di = 0; di < formats.length; di++) {
                        (function (fmt) {
                            menu.appendChild(el('div', {
                                className: 'bowire-dropdown-item',
                                textContent: fmt.label,
                                onClick: function () {
                                    downloadResponse(fmt.id);
                                    menu.classList.remove('visible');
                                }
                            }));
                        })(formats[di]);
                    }
                    wrapper.appendChild(btn);
                    wrapper.appendChild(menu);
                    document.addEventListener('click', function (e) {
                        if (!wrapper.contains(e.target)) menu.classList.remove('visible');
                    });
                    return wrapper;
                })(),
                (function () {
                    // Compare button — opens the multi-snapshot diff view
                    // when at least two responses have been captured for this
                    // method. Hidden otherwise so the header stays clean.
                    var snaps = getResponseSnapshots();
                    if (snaps.length < 2) return el('span');
                    return el('button', {
                        className: 'bowire-pane-btn' + (diffViewOpen ? ' active' : ''),
                        textContent: diffViewOpen ? 'Close Diff' : 'Compare',
                        title: 'Compare responses for this method (' + snaps.length + ' snapshots)',
                        onClick: function () {
                            diffViewOpen = !diffViewOpen;
                            if (!diffViewOpen) {
                                diffSnapshotA = null;
                                diffSnapshotB = null;
                            }
                            render();
                        }
                    });
                })(),
                // The standalone "grpcurl" button used to live here.
                // It's now an entry in the Copy split-button's
                // dropdown (see above) — protocol-aware so REST gets
                // curl, gRPC gets grpcurl, WebSocket gets wscat, &c.
            )
        );
        respContent.appendChild(respHeader);

        const respBody = el('div', { className: 'bowire-pane-body' });

        if (isExecuting && streamMessages.length === 0) {
            // Server-streaming methods that haven't yet emitted a frame
            // get the subscription-shaped loader so the operator sees
            // "Subscribed — 0 msgs" instead of the generic "Executing…"
            // spinner. The badge re-uses the same state pill the
            // streaming pane uses once frames start arriving.
            var isStreamingMethodLoading = selectedMethod && selectedMethod.serverStreaming;
            if (isStreamingMethodLoading) {
                var loadingPane = el('div', { className: 'bowire-loading bowire-loading-subscribed' });
                loadingPane.appendChild(renderSubscriptionBadge(
                    selectedService && selectedService.name,
                    selectedMethod && selectedMethod.name,
                    0));
                loadingPane.appendChild(el('span', {
                    className: 'bowire-loading-text',
                    textContent: 'Waiting for first message…'
                }));
                respBody.appendChild(loadingPane);
            } else {
                respBody.appendChild(el('div', { className: 'bowire-loading' },
                    el('div', { className: 'bowire-spinner' }),
                    el('span', { className: 'bowire-loading-text', textContent: 'Executing...' })
                ));
            }
        } else if (responseError) {
            // #91 — render structured problem+json when the upstream
            // returned one; fall back to plain text for legacy
            // strings and exception messages.
            const output = el('div', { className: 'bowire-response-output error' });
            var prob = (typeof responseError === 'object')
                ? normalizeProblem(responseError)
                : null;
            if (prob) {
                renderProblem(prob, output);
            } else {
                output.textContent = (typeof responseError === 'string')
                    ? responseError
                    : (problemTitle(responseError, 'Request failed'));
            }
            respBody.appendChild(output);
        } else if (streamMessages.length > 0) {
            // Wireshark-style: append-only list + selectable detail pane.
            // The full DOM is built once per render() (start / done / error);
            // new in-flight messages take the appendStreamMessage() fast path
            // and never trigger a sidebar/main re-render.
            //
            // Phase 3.1: when the active method carries annotations
            // that mount a widget whose default layout is split-*
            // (resolved through the framework — core hardcodes no
            // kind here), the streaming list and the widget share
            // the pane via the split-pane primitive instead of
            // competing for the same tab slot. Falls through to
            // the original
            // single-pane render when no such widget is mountable —
            // identical behaviour for every other method.
            respBody.appendChild(renderStreamingPaneWithWidgets());
        } else if (diffViewOpen && getResponseSnapshots().length >= 2) {
            // Multi-snapshot diff view — replaces the normal response body
            // with snapshot selectors and a line-by-line comparison.
            respBody.appendChild(renderResponseDiff());
        } else if (responseData) {
            // GraphQL: surface a non-empty `errors` array as a banner above
            // the body so the user doesn't have to scan the JSON. The body
            // itself still renders verbatim — we don't drop the data field.
            var gqlErrors = detectGraphQLErrors(responseData);
            if (gqlErrors) {
                var banner = el('div', { className: 'bowire-graphql-errors-banner' },
                    el('div', { className: 'bowire-graphql-errors-title', textContent:
                        '\u26A0 GraphQL errors (' + gqlErrors.length + ')' })
                );
                for (var ei = 0; ei < gqlErrors.length; ei++) {
                    var errMsg = (gqlErrors[ei] && gqlErrors[ei].message) || JSON.stringify(gqlErrors[ei]);
                    banner.appendChild(el('div', { className: 'bowire-graphql-errors-item', textContent: errMsg }));
                }
                respBody.appendChild(banner);
            }

            // MCP: tool responses are wrapped as { content: [{type, text}, ...] }.
            // Show the concatenated text payload by default with a toggle to
            // see the raw envelope. Resources / prompts and any non-content
            // shape fall through to the raw view automatically.
            var mcpContent = detectMcpContent(responseData);
            if (mcpContent && !mcpRawEnvelope) {
                var header = el('div', { className: 'bowire-mcp-content-header' },
                    el('span', { className: 'bowire-mcp-content-title', textContent:
                        mcpContent.isError ? '\u26A0 Tool error' : 'Tool result' }),
                    el('span', { className: 'bowire-mcp-content-meta', textContent:
                        mcpContent.count + (mcpContent.count === 1 ? ' item' : ' items') }),
                    el('button', {
                        className: 'bowire-pane-btn',
                        textContent: 'Show raw envelope',
                        onClick: function () { mcpRawEnvelope = true; render(); }
                    })
                );
                var textBox = el('pre', {
                    className: 'bowire-mcp-content-body' + (mcpContent.isError ? ' error' : ''),
                    textContent: mcpContent.text
                });
                respBody.appendChild(el('div', { className: 'bowire-mcp-content' }, header, textBox));
            } else {
                if (mcpContent && mcpRawEnvelope) {
                    var rawHeader = el('div', { className: 'bowire-mcp-content-header' },
                        el('span', { className: 'bowire-mcp-content-title', textContent: 'Raw envelope' }),
                        el('button', {
                            className: 'bowire-pane-btn',
                            textContent: 'Show tool result',
                            onClick: function () { mcpRawEnvelope = false; render(); }
                        })
                    );
                    respBody.appendChild(el('div', { className: 'bowire-mcp-content' }, rawHeader));
                }
                // Interactive collapsible JSON tree. Click on a
                // container's summary toggles via the native
                // <details>/<summary> contract — no JS overlay
                // needed. Double-click copies the JSONPath of the
                // clicked node ("position.lat"); right-click opens
                // a unified context menu with Copy ${response.X} /
                // Copy path / Center on map (when a map plugin is
                // mounted on the coord pair). The old click-to-
                // copy-${} action was demoted to right-click +
                // dblclick because primary-click on a JSON node is
                // operator-expected to do the obvious "open/close"
                // — same shape as <details>, the IDE tree views
                // and every Hoppscotch-style viewer ship.
                const output = el('div', {
                    className: 'bowire-response-output is-interactive is-tree',
                    title: 'Click to expand/collapse — double-click to copy path — right-click for more actions'
                });
                bowireWireResponseTreeGestures(output);
                // JSON viewer + per-pane toolbar (Expand-all /
                // Collapse-all / Wrap / Search / Copy / Download).
                // Wrapping the viewer inside the output keeps the
                // gesture wiring + extension decorators (semantics
                // badges, map coord-path) anchored on the same node;
                // the toolbar lives at the top of the output and
                // moves with it through the split/tab/single-pane
                // wrapping `renderResponseWithWidgets` does below.
                var unaryViewer = renderJsonViewer(responseData, { wrap: false });
                var ctMeta = (selectedService && selectedMethod && responseData)
                    ? 'application/json'
                    : '';
                var methodName = (selectedMethod && selectedMethod.name)
                    ? selectedMethod.name.replace(/[^A-Za-z0-9_-]+/g, '-')
                    : 'response';
                output.appendChild(bowireRenderJsonViewerWithToolbar(unaryViewer, {
                    raw: responseData,
                    downloadName: methodName,
                    contentType: ctMeta
                }));
                // Wrap the response output in a split-pane host when a
                // registered viewer claims the active method's kind
                // (e.g. MapLibre on coordinate.wgs84). For unary RPCs
                // whose response carries lat/lon — TacticalAPI's
                // GetSituationObjects, REST GETs against geo APIs —
                // this surfaces the same map widget the streaming
                // path already mounted, without forcing the user to
                // switch to a streaming method. Falls through to the
                // single-pane `output` element on the standard path
                // (no registered widget / per-method layout = tab),
                // so non-geo responses look exactly as before.
                respBody.appendChild(renderResponseWithWidgets(output));

                // Phase 4 — mount per-leaf semantic badges + right-click
                // handlers. The decoration runs asynchronously (it
                // depends on /api/semantics/effective which the
                // extensions framework already fetches for the active
                // method). Failure is silent — the response tree is
                // perfectly usable without the badges.
                if (typeof bowireDecorateResponseTreeForSemantics === 'function'
                    && selectedService && selectedMethod) {
                    try {
                        bowireDecorateResponseTreeForSemantics(
                            output, selectedService.name, selectedMethod.name);
                    } catch (e) { console.error('[bowire-semantics] decorate', e); }
                }
                if (selectedService && selectedMethod) {
                    try {
                        // Fan out the per-kind decoration hooks. The
                        // map widget registers a decorator that
                        // stamps data-bowire-coord-path + wires
                        // hover sync; other extensions plug in the
                        // same way without touching core.
                        bowireDecorateResponseTreeViaExtensions(
                            output, selectedService.name, selectedMethod.name);
                    } catch (e) { console.error('[bowire-resp-tree] decorate', e); }
                }
            }
        } else {
            respBody.appendChild(el('div', { className: 'bowire-no-response' },
                el('div', { className: 'bowire-no-response-icon', innerHTML: svgIcon('play') }),
                el('div', { textContent: 'Send a request to see the response' }),
                el('div', { style: 'font-size: 11px; opacity: 0.6', textContent: 'Press Ctrl+Enter to execute' })
            ));
        }

        respContent.appendChild(respBody);
        pane.appendChild(respContent);

        // Response metadata tab
        const headersContent = el('div', { className: `bowire-tab-content ${activeResponseTab === 'headers' ? 'active' : ''}` });
        const headersBody = el('div', { className: 'bowire-pane-body' });

        if (statusInfo && statusInfo.metadata && Object.keys(statusInfo.metadata).length > 0) {
            var metaEntries = Object.entries(statusInfo.metadata).sort(function (a, b) {
                return String(a[0]).toLowerCase().localeCompare(String(b[0]).toLowerCase());
            });
            // Raw-text serialisation (`Header: Value\n…`) is still
            // useful for the bulk copy action — drops cleanly into a
            // curl / script — but it's not its own view any more.
            var rawText = metaEntries.map(function (p) {
                var v = (p[1] === null || p[1] === undefined) ? '' : String(p[1]);
                return p[0] + ': ' + v;
            }).join('\n');
            var toolbar = el('div', { className: 'bowire-response-metadata-toolbar' });
            toolbar.appendChild(el('div', { className: 'bowire-response-metadata-toolbar-spacer' }));
            toolbar.appendChild(el('button', {
                type: 'button',
                className: 'bowire-response-metadata-tool-btn',
                title: 'Copy as raw text',
                'aria-label': 'Copy as raw text',
                innerHTML: svgIcon('copy'),
                onClick: function () {
                    navigator.clipboard.writeText(rawText).then(function () {
                        if (typeof toast === 'function') toast('Copied headers', 'success');
                    });
                }
            }));
            headersBody.appendChild(toolbar);

            // Two-column table — header name (mono, narrow) + value
            // (mono, wraps). Hover-revealed copy button on each row
            // dumps that single value to the clipboard.
            var table = el('table', { className: 'bowire-response-metadata-table' });
            table.appendChild(el('thead', null,
                el('tr', null,
                    el('th', { textContent: 'Header' }),
                    el('th', { textContent: 'Value' }),
                    el('th', { className: 'bowire-response-metadata-actions-col', 'aria-label': 'Actions' })
                )
            ));
            var tbody = el('tbody');
            metaEntries.forEach(function (pair) {
                var k = pair[0], v = pair[1];
                var valueStr = (v === null || v === undefined) ? '' : String(v);
                tbody.appendChild(el('tr', null,
                    el('td', { className: 'bowire-response-metadata-key', textContent: k }),
                    el('td', { className: 'bowire-response-metadata-value', textContent: valueStr }),
                    el('td', { className: 'bowire-response-metadata-actions' },
                        el('button', {
                            type: 'button',
                            className: 'bowire-response-metadata-copy',
                            title: 'Copy value',
                            'aria-label': 'Copy ' + k,
                            innerHTML: svgIcon('copy'),
                            onClick: function () {
                                navigator.clipboard.writeText(valueStr).then(function () {
                                    if (typeof toast === 'function') toast('Copied ' + k, 'success');
                                });
                            }
                        })
                    )
                ));
            });
            table.appendChild(tbody);
            headersBody.appendChild(table);
        } else {
            headersBody.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                el('div', { className: 'bowire-empty-desc', textContent: 'No response metadata available.' })
            ));
        }

        headersContent.appendChild(headersBody);
        pane.appendChild(headersContent);

        // Test results tab — only for unary methods. The editor
        // lives in the request pane's Tests sub-tab; this view is
        // read-only pass/fail against the last response. Performance
        // moved to the Benchmarks rail (see the response-tab list
        // above).
        if (selectedMethod && selectedMethod.methodType === 'Unary') {
            var testsContent = el('div', { className: 'bowire-tab-content ' + (activeResponseTab === 'tests' ? 'active' : '') });
            testsContent.appendChild(typeof renderTestResultsTab === 'function'
                ? renderTestResultsTab()
                : renderTestsTab());
            pane.appendChild(testsContent);
        }
        // Migrate stale tab state forward — if someone had the
        // Performance tab active when this build shipped, snap them
        // to Response so they're not stuck on a tab that no longer
        // exists.
        if (activeResponseTab === 'performance') {
            activeResponseTab = 'response';
        }

        return pane;
    }

