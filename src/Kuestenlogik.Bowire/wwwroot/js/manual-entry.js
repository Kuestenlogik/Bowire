    // ---- Manual entry: the forms the protocol plugins describe (#995) ----
    //
    // A plugin that implements IBowireManualEntry describes how a call to it
    // is entered by hand: its call kinds (produce / consume, invoke / stream /
    // channel) with their method types, and the fields a call needs, each
    // going into the service and method templates or into the metadata. The
    // workbench reads the descriptions through the ManualEntry rpc
    // (GET /api/v1/manual-entry-forms) and
    //   * offers each protocol's method types in the freeform form;
    //   * registers one generic Compose layout for every protocol that has
    //     fields to fill and no layout of its own.
    // The plugin's knowledge stays with the plugin: nothing here names one.

    /** protocol id -> form, as the rpc transcodes it (camelCase). */
    var manualEntryForms = {};
    var _manualEntryLoadedFor = null;

    /** The method types when nothing is known about a protocol: all four. */
    var MANUAL_ENTRY_ALL_METHOD_TYPES = ['Unary', 'ServerStreaming', 'ClientStreaming', 'Duplex'];  // i18n-exempt: protocol method types, matched as identifiers

    /**
     * Load every page of the forms, once per set of loaded protocols. A host
     * without the ManualEntry package answers 404; the forms stay empty and
     * the freeform form offers all four method types, as before.
     */
    async function loadManualEntryForms() {
        var ids = (protocols || []).map(function (p) { return p.id; }).sort().join(',');
        if (_manualEntryLoadedFor === ids) return;
        _manualEntryLoadedFor = ids;
        var forms = {};
        var token = '';
        try {
            for (var page = 0; page < 50; page++) {
                var url = config.prefix + '/api/v1/manual-entry-forms?pageSize=200'
                    + (token ? '&pageToken=' + encodeURIComponent(token) : '');
                var resp = await fetch(url);
                if (!resp.ok) break;
                var body = await resp.json();
                (body.forms || []).forEach(function (f) { forms[f.protocol] = f; });
                token = body.nextPageToken || '';
                if (!token) break;
            }
        } catch (e) {
            console.warn('[manual-entry] forms did not load', e);
        }
        manualEntryForms = forms;
        registerManualEntryLayouts();
        render();
    }

    /** The method types a protocol offers by hand, in the order its call kinds name them. */
    function manualEntryMethodTypes(protocolId) {
        var form = manualEntryForms[protocolId];
        if (!form || !form.callKinds || form.callKinds.length === 0) return MANUAL_ENTRY_ALL_METHOD_TYPES.slice();
        var types = [];
        form.callKinds.forEach(function (k) {
            if (k.methodType && types.indexOf(k.methodType) < 0) types.push(k.methodType);
        });
        return types.length > 0 ? types : MANUAL_ENTRY_ALL_METHOD_TYPES.slice();
    }

    /** The fields of `form` that belong to call kind `kindId`. */
    function manualEntryFieldsFor(form, kindId) {
        return (form.fields || []).filter(function (f) {
            return !f.callKinds || f.callKinds.length === 0 || f.callKinds.indexOf(kindId) >= 0;
        });
    }

    /** Replace every `{key}` of `template` with the value of that field. */
    function expandManualEntryTemplate(template, values) {
        return String(template || '').replace(/\{([^{}]+)\}/g, function (whole, key) {
            var v = values && values[key];
            return v == null ? '' : String(v);
        });
    }

    /**
     * The call a filled-in form makes: service and method from the call
     * kind's templates, metadata from the metadata fields plus the extra
     * rows, and the required fields still empty.
     *
     * @param {object} form      A form from the rpc.
     * @param {string} kindId    The chosen call kind.
     * @param {object} values    field key -> value.
     * @param {object} extraMetadata  Further metadata (the KV rows), wins over nothing.
     * @returns {{service, method, methodType, metadata, missing: string[]}}
     */
    function assembleManualEntryCall(form, kindId, values, extraMetadata) {
        var kind = (form.callKinds || []).find(function (k) { return k.id === kindId; }) || form.callKinds[0];
        var fields = manualEntryFieldsFor(form, kind.id);
        var metadata = {};
        Object.keys(extraMetadata || {}).forEach(function (k) { metadata[k] = extraMetadata[k]; });
        var missing = [];
        fields.forEach(function (f) {
            var v = values ? values[f.key] : undefined;
            var empty = v == null || String(v).trim() === '';
            if (f.required && empty) missing.push(f.label || f.key);
            if (!empty && f.placement === 'PLACEMENT_METADATA') metadata[f.key] = String(v);
        });
        return {
            service: expandManualEntryTemplate(kind.service, values) || form.protocol,
            method: expandManualEntryTemplate(kind.method, values),
            methodType: kind.methodType || 'Unary',
            metadata: metadata,
            missing: missing
        };
    }

    /**
     * A generic Compose layout for every protocol whose form has fields and
     * which has no layout of its own. Re-run when the forms load; a layout
     * the workbench ships is never replaced.
     */
    function registerManualEntryLayouts() {
        Object.keys(manualEntryForms).forEach(function (pid) {
            var form = manualEntryForms[pid];
            if (!form.fields || form.fields.length === 0) return;
            if (rbLayouts[pid] && !rbLayouts[pid].manualEntry) return;
            registerRequestBuilderLayout(buildManualEntryLayout(pid, form));
        });
    }

    function _manualEntryProtocolName(pid) {
        var p = (protocols || []).find(function (x) { return x.id === pid; });
        return (p && p.name) || pid;
    }

    function _manualEntryKind(form, ps) {
        return form.callKinds.find(function (k) { return k.id === ps.kind; }) || form.callKinds[0];
    }

    /** The layout descriptor (see rbLayouts in request-builder.js) for one form. */
    function buildManualEntryLayout(pid, form) {
        return {
            id: pid,
            label: _manualEntryProtocolName(pid),
            manualEntry: true,
            urlPlaceholder: form.urlPlaceholder || '',
            defaults: function () {
                var values = {};
                (form.fields || []).forEach(function (f) { values[f.key] = f.defaultValue || ''; });
                var first = form.callKinds[0];
                return { kind: first.id, values: values, body: first.bodyExample || '', metadata: [] };
            },
            secondControl: function (fr) { return _renderManualEntryKindPicker(fr, form); },
            subTabs: function () {
                return [
                    { id: 'fields',   labelKey: 'rb.tab.fields' },
                    { id: 'body',     labelKey: 'rb.tab.body' },
                    { id: 'metadata', labelKey: 'rb.tab.metadata',
                      badge: function (f) { return _activeKvCount(rbProtoState(f).metadata); } }
                ];
            },
            renderTab: function (fr, tabId) {
                var ps = rbProtoState(fr);
                switch (tabId) {
                    case 'fields':   return _renderManualEntryFields(fr, form, ps);
                    case 'body':     return _renderManualEntryBody(ps);
                    case 'metadata': return _renderHoppKvTable(ps.metadata, {
                        headerLibrary: true,
                        keyPlaceholder: t('rb.kv.metadata'), valuePlaceholder: t('rb.kv.value'), descPlaceholder: t('rb.kv.description')
                    });
                    default:         return el('div');
                }
            },
            executeLabel: function (fr) {
                var kind = _manualEntryKind(form, rbProtoState(fr));
                return freeformExecuteLabel({ protocol: pid, methodType: kind.methodType });
            },
            execute: function (fr) { return _executeManualEntry(fr, pid, form); },
            renderResponse: function (fr) {
                var kind = _manualEntryKind(form, rbProtoState(fr));
                var mode = freeformCallMode(kind.methodType);
                return (mode !== 'unary' && freeformCall.mode === mode) ? renderFreeformFrames() : null;
            }
        };
    }

    function _renderManualEntryKindPicker(fr, form) {
        var ps = rbProtoState(fr);
        var select = el('select', {
            className: 'bowire-request-builder-manual-kind',
            'aria-label': t('rbManual.callKind'),
            onChange: function (e) {
                var previous = _manualEntryKind(form, ps);
                var next = form.callKinds.find(function (k) { return k.id === e.target.value; });
                if (!next || next.id === previous.id) return;
                stopFreeformCall();
                ps.kind = next.id;
                // Keep a payload the user wrote; swap only the example.
                if (!ps.body || ps.body === (previous.bodyExample || '')) ps.body = next.bodyExample || '';
                render();
            }
        });
        form.callKinds.forEach(function (k) {
            select.appendChild(el('option', { value: k.id, selected: k.id === ps.kind, textContent: k.label }));
        });
        return select;
    }

    function _renderManualEntryFields(fr, form, ps) {
        var kind = _manualEntryKind(form, ps);
        var wrap = el('div', { className: 'bowire-manual-entry-fields' });
        if (kind.description) {
            wrap.appendChild(el('div', { className: 'bowire-manual-entry-kind-description', textContent: kind.description }));
        }
        manualEntryFieldsFor(form, kind.id).forEach(function (f) {
            var row = el('label', { className: 'bowire-manual-entry-field' });
            row.appendChild(el('span', {
                className: 'bowire-manual-entry-field-label',
                textContent: f.label + (f.required ? ' *' : '')
            }));
            var value = ps.values[f.key] == null ? '' : String(ps.values[f.key]);
            var input;
            if (f.type === 'select') {
                input = el('select', {
                    className: 'bowire-freeform-input',
                    onChange: function (e) { ps.values[f.key] = e.target.value; }
                });
                input.appendChild(el('option', { value: '', selected: value === '', textContent: '—' }));
                (f.options || []).forEach(function (o) {
                    input.appendChild(el('option', { value: o.value, selected: o.value === value, textContent: o.label }));
                });
            } else if (f.type === 'bool') {
                input = el('input', {
                    type: 'checkbox',
                    checked: value === 'true',
                    onChange: function (e) { ps.values[f.key] = e.target.checked ? 'true' : ''; }
                });
            } else {
                input = el('input', {
                    type: f.type === 'number' ? 'number' : 'text',
                    className: 'bowire-freeform-input',
                    value: value,
                    placeholder: f.placeholder || '',
                    spellcheck: 'false',
                    onInput: function (e) { ps.values[f.key] = e.target.value; }
                });
            }
            row.appendChild(input);
            if (f.description) {
                row.appendChild(el('span', { className: 'bowire-manual-entry-field-description', textContent: f.description }));
            }
            wrap.appendChild(row);
        });
        return wrap;
    }

    function _renderManualEntryBody(ps) {
        var editor = el('textarea', {
            className: 'bowire-editor',
            spellcheck: 'false',
            style: 'min-height:200px',
            onInput: function (e) { ps.body = e.target.value; }
        });
        editor.value = ps.body || '';
        return editor;
    }

    /** The call the Compose bar of a generic layout makes. */
    function _manualEntryCallFor(fr, form) {
        var ps = rbProtoState(fr);
        var extra = (typeof effectiveRequestHeaderObject === 'function')
            ? effectiveRequestHeaderObject(ps.metadata || [], {
                substituteLibrary: typeof substituteVars === 'function' ? substituteVars : null
            })
            : _kvToObject(ps.metadata || []);
        return assembleManualEntryCall(form, ps.kind, ps.values, extra);
    }

    async function _executeManualEntry(fr, pid, form) {
        var S = activeState();
        if (!fr.serverUrl || !fr.serverUrl.trim()) {
            toast(t('main.needsUrl'), 'error');
            return;
        }
        var call = _manualEntryCallFor(fr, form);
        if (call.missing.length > 0) {
            toast(t('rbManual.needsField', { field: call.missing[0] }), 'error');
            return;
        }
        var ps = rbProtoState(fr);
        var request = {
            protocol: pid,
            service: call.service,
            method: call.method,
            methodType: call.methodType,
            serverUrl: typeof substituteVars === 'function' ? substituteVars(fr.serverUrl) : fr.serverUrl,
            body: ps.body || '',
            metadata: call.metadata
        };
        var mode = freeformCallMode(call.methodType);
        if (mode === 'stream') {
            if (freeformCallActive()) { stopFreeformCall(); render(); }
            else startFreeformStream(request);
            return;
        }
        if (mode === 'channel') {
            if (freeformCall.state === 'open') await sendFreeformChannel(request.body || '{}');
            else await openFreeformChannel(request);
            return;
        }

        stopFreeformCall();
        freeformCall.mode = null;
        S.isExecuting = true;
        S.responseData = null;
        S.responseError = null;
        render();
        var fullName = '[' + _manualEntryProtocolName(pid) + '] ' + call.service + '/' + call.method;
        addConsoleEntry({ type: 'request', method: fullName, body: request.body });
        try {
            var resp = await fetch(config.prefix + '/api/invoke?serverUrl=' + encodeURIComponent(request.serverUrl), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(buildFreeformInvokeBody(request, request.body || '', true))
            });
            var result = await resp.json();
            if (result.title) {
                S.responseError = result;
                addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: richErrorDetail(result, t('console.body.requestFailed')) });  // i18n-exempt: status token, and what the row's colour is decided on
            } else {
                S.responseData = result.response;
                S.statusInfo = { status: result.status, durationMs: result.duration_ms || 0 };
                addConsoleEntry({ type: 'response', method: fullName, status: result.status, body: result.response });
            }
        } catch (e) {
            S.responseError = String((e && e.message) || e);
        }
        S.isExecuting = false;
        render();
    }
