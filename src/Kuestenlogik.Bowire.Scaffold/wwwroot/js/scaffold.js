    // ---- Service scaffolding (#177) ----
    //
    // A sentence becomes a spec, the spec becomes files, and the files —
    // edited or not — land in the workspace:
    //
    //   1. Describe  — a sentence and a protocol. "Propose" asks the AI
    //      assistant (/api/ai/scaffold: local model first, the configured
    //      provider as fallback, the parser last); "Read without AI" asks
    //      the parser alone (/api/scaffold/parse).
    //   2. Spec      — the spec as JSON, editable. Whatever the model got
    //      wrong is fixed here, before a single file exists.
    //   3. Files     — /api/scaffold/generate renders the checked-in
    //      templates; every file is editable before it is written.
    //
    // "Add to workspace" writes the files under the workspace's
    // scaffold/<Service>/ (/api/scaffold/write), uploads the schema the
    // way "Upload schema files" does, and adds the collection. The dialog
    // lives on document.body, outside morphdom's reach, like the catalogue
    // browser.

    var scaffoldState = null;

    function _scaffoldFresh() {
        return { step: 'describe', intent: '', protocol: '', busy: false, error: null,
            specText: '', source: null, model: null, notes: [], files: [], selected: 0 };
    }

    async function _scaffoldPost(path, body) {
        var resp = await fetch(config.prefix + path + workspaceParam(false), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        var data = await resp.json().catch(function () { return null; });
        return { ok: resp.ok, status: resp.status, data: data };
    }

    // "Propose" → the assistant; its route answers 404 when the AI package
    // is not installed, and then the parser is the honest fallback.
    async function _scaffoldPropose(useAi) {
        var s = scaffoldState;
        var body = { intent: s.intent, protocol: s.protocol || null };
        var r = useAi ? await _scaffoldPost('/api/ai/scaffold', body) : null;
        if (!r || r.status === 404) r = await _scaffoldPost('/api/scaffold/parse', body);
        if (!r.ok || !r.data || !r.data.spec) {
            throw new Error((r.data && r.data.errors && r.data.errors.join('; ')) || ('HTTP ' + r.status));
        }
        s.specText = JSON.stringify(r.data.spec, null, 2);
        s.source = r.data.source || 'parser';
        s.model = r.data.model || null;
        s.notes = Array.isArray(r.data.notes) ? r.data.notes : [];
        s.step = 'spec';
    }

    async function _scaffoldGenerate() {
        var s = scaffoldState;
        var spec;
        try { spec = JSON.parse(s.specText); }
        catch (e) { throw new Error(t('scaffold.specNotJson', { message: e.message })); }
        var r = await _scaffoldPost('/api/scaffold/generate', { spec: spec });
        if (!r.ok || !r.data || !Array.isArray(r.data.files)) {
            throw new Error((r.data && r.data.errors && r.data.errors.join('; ')) || ('HTTP ' + r.status));
        }
        s.specText = JSON.stringify(r.data.spec, null, 2);
        s.spec = r.data.spec;
        s.files = r.data.files;
        s.selected = Math.max(0, s.files.findIndex(function (f) { return f.kind === 'schema'; }));
        s.step = 'files';
    }

    // The collection the generator wrote, added through the same calls the
    // sidebar uses, so it persists and syncs like any other.
    function _scaffoldAddCollection(file) {
        var doc = JSON.parse(file.content);
        if (typeof createCollection !== 'function' || typeof addToCollection !== 'function') return null;
        var col = createCollection(doc.name);
        (doc.items || []).forEach(function (item) {
            var copy = Object.assign({}, item);
            delete copy.id;
            addToCollection(col.id, copy);
        });
        return col;
    }

    async function _scaffoldApply(overwrite) {
        var s = scaffoldState;
        // The spec file follows the spec as edited in step 2.
        var files = s.files.map(function (f) {
            return { path: f.path, kind: f.kind, language: f.language, content: f.content };
        });
        var w = await _scaffoldPost('/api/scaffold/write', { folder: s.spec.service, files: files, overwrite: !!overwrite });
        if (w.status === 409 && !overwrite) {
            var yes = typeof bowireConfirm === 'function'
                ? await bowireConfirm(t('scaffold.overwriteBody', { folder: 'scaffold/' + s.spec.service }), {
                    title: t('scaffold.overwriteTitle'), confirmText: t('scaffold.overwrite'), danger: true })
                : false;
            if (!yes) return null;
            return _scaffoldApply(true);
        }
        if (!w.ok) throw new Error((w.data && w.data.errors && w.data.errors.join('; ')) || ('HTTP ' + w.status));

        var schema = s.files.find(function (f) { return f.kind === 'schema'; });
        var uploaded = null;
        if (schema && typeof uploadSchemaFiles === 'function' && typeof File === 'function') {
            uploaded = await uploadSchemaFiles([new File([schema.content], schema.path.split('/').pop())]);
        }
        var colFile = s.files.find(function (f) { return f.kind === 'collection'; });
        var col = colFile ? _scaffoldAddCollection(colFile) : null;
        if (typeof fetchServices === 'function') fetchServices();
        return {
            folder: w.data.folder,
            written: w.data.written.length,
            collection: col ? col.name : null,
            schemaOk: !!(uploaded && uploaded[0] && uploaded[0].ok)
        };
    }

    function openScaffoldDialog() {
        if (typeof document === 'undefined' || !document.body) return null;
        var existing = document.querySelector('.bowire-scaffold-overlay');
        if (existing) existing.remove();
        scaffoldState = _scaffoldFresh();

        var overlay = null;
        var body = el('div', { className: 'bowire-scaffold-body' });
        var footer = el('div', { className: 'bowire-scaffold-footer' });

        function close() {
            document.removeEventListener('keydown', onKey, true);
            if (overlay && overlay.parentNode) overlay.remove();
            scaffoldState = null;
        }
        function onKey(e) {
            if (e.key === 'Escape' && !scaffoldState.busy) { e.preventDefault(); close(); }
        }

        async function run(action) {
            var s = scaffoldState;
            s.busy = true; s.error = null; paint();
            try { await action(); }
            catch (e) { s.error = e && e.message ? e.message : String(e); }
            s.busy = false;
            if (scaffoldState) paint();
        }

        function button(label, onClick, primary, disabled) {
            return el('button', {
                type: 'button',
                className: 'bowire-confirm-btn' + (primary ? '' : ' cancel'),
                textContent: label,
                disabled: !!disabled,
                onClick: onClick
            });
        }

        function paint() {
            var s = scaffoldState;
            body.textContent = '';
            footer.textContent = '';
            var steps = ['describe', 'spec', 'files'];
            body.appendChild(el('div', { className: 'bowire-scaffold-steps' },
                steps.map(function (id, i) {
                    return el('span', {
                        className: 'bowire-scaffold-step' + (id === s.step ? ' active' : '')
                            + (steps.indexOf(s.step) > i ? ' done' : ''),
                        textContent: (i + 1) + ' · ' + t('scaffold.step.' + id)
                    });
                })));

            if (s.step === 'describe') {
                var intent = el('textarea', {
                    className: 'bowire-scaffold-intent',
                    rows: 3,
                    placeholder: t('scaffold.intentPlaceholder'),
                    'aria-label': t('scaffold.intentLabel'),
                    onInput: function (e) { s.intent = e.target.value; refreshButtons(); }
                });
                intent.value = s.intent;
                var protocol = el('select', {
                    className: 'bowire-scaffold-protocol',
                    'aria-label': t('scaffold.protocolLabel'),
                    onChange: function (e) { s.protocol = e.target.value; }
                },
                    el('option', { value: '', textContent: t('scaffold.protocolAuto') }),
                    el('option', { value: 'rest', textContent: 'REST (OpenAPI)' }),  // i18n-exempt: protocol and format names
                    el('option', { value: 'grpc', textContent: 'gRPC (proto3)' }));  // i18n-exempt: protocol and format names
                protocol.value = s.protocol;
                body.appendChild(el('label', { className: 'bowire-scaffold-label', textContent: t('scaffold.intentLabel') }));
                body.appendChild(intent);
                body.appendChild(el('div', { className: 'bowire-scaffold-row' },
                    el('label', { className: 'bowire-scaffold-label', textContent: t('scaffold.protocolLabel') }), protocol));
                body.appendChild(el('p', { className: 'bowire-scaffold-hint', textContent: t('scaffold.describeHint') }));
                var proposeBtn = button(t('scaffold.propose'), function () { run(function () { return _scaffoldPropose(true); }); }, true);
                var parseBtn = button(t('scaffold.parseOnly'), function () { run(function () { return _scaffoldPropose(false); }); });
                var refreshButtons = function () {
                    proposeBtn.disabled = s.busy || !s.intent.trim();
                    parseBtn.disabled = s.busy || !s.intent.trim();
                };
                refreshButtons();
                footer.appendChild(button(t('common.cancel'), close));
                footer.appendChild(parseBtn);
                footer.appendChild(proposeBtn);
                setTimeout(function () { if (!s.busy) intent.focus(); }, 0);
            } else if (s.step === 'spec') {
                body.appendChild(el('p', {
                    className: 'bowire-scaffold-source',
                    textContent: s.source === 'ai'
                        ? t('scaffold.sourceAi', { model: s.model || '?' })
                        : t('scaffold.sourceParser')
                }));
                if (s.notes.length > 0) {
                    body.appendChild(el('ul', { className: 'bowire-scaffold-notes' },
                        s.notes.map(function (n) { return el('li', { textContent: n }); })));
                }
                var spec = el('textarea', {
                    className: 'bowire-scaffold-editor',
                    rows: 16,
                    spellcheck: 'false',
                    'aria-label': t('scaffold.step.spec'),
                    onInput: function (e) { s.specText = e.target.value; }
                });
                spec.value = s.specText;
                body.appendChild(spec);
                body.appendChild(el('p', { className: 'bowire-scaffold-hint', textContent: t('scaffold.specHint') }));
                footer.appendChild(button(t('scaffold.back'), function () { s.step = 'describe'; s.error = null; paint(); }));
                footer.appendChild(button(t('scaffold.generate'), function () { run(_scaffoldGenerate); }, true, s.busy));
            } else if (s.step === 'files') {
                var list = el('div', { className: 'bowire-scaffold-files', role: 'tablist' },
                    s.files.map(function (f, i) {
                        return el('button', {
                            type: 'button',
                            role: 'tab',
                            className: 'bowire-scaffold-file' + (i === s.selected ? ' active' : ''),
                            'aria-selected': i === s.selected ? 'true' : 'false',
                            title: t('scaffold.kind.' + f.kind),
                            textContent: f.path,
                            onClick: function () { s.selected = i; paint(); }
                        });
                    }));
                var current = s.files[s.selected];
                var editor = el('textarea', {
                    className: 'bowire-scaffold-editor',
                    rows: 20,
                    spellcheck: 'false',
                    'aria-label': current.path,
                    onInput: function (e) { current.content = e.target.value; }
                });
                editor.value = current.content;
                body.appendChild(el('div', { className: 'bowire-scaffold-split' }, list, editor));
                body.appendChild(el('p', { className: 'bowire-scaffold-hint',
                    textContent: t('scaffold.filesHint', { folder: 'scaffold/' + s.spec.service }) }));
                footer.appendChild(button(t('scaffold.back'), function () { s.step = 'spec'; s.error = null; paint(); }));
                footer.appendChild(button(t('scaffold.apply'), function () {
                    run(async function () {
                        var result = await _scaffoldApply(false);
                        if (!result) return;
                        close();
                        if (typeof toast === 'function') {
                            toast(t('scaffold.done', { count: result.written, folder: result.folder }), 'success');
                        }
                        if (typeof render === 'function') render();
                    });
                }, true, s.busy));
            }

            if (s.busy) body.appendChild(el('p', { className: 'bowire-scaffold-busy', textContent: t('scaffold.working') }));
            if (s.error) body.appendChild(el('p', { className: 'bowire-scaffold-error', role: 'alert', textContent: s.error }));
        }

        paint();
        var dialog = el('div', {
            className: 'bowire-confirm-dialog bowire-scaffold-dialog',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': t('scaffold.title')
        },
            el('div', { className: 'bowire-confirm-title', textContent: t('scaffold.title') }),
            body,
            footer
        );
        // Own overlay class, not .bowire-confirm-overlay: the overwrite
        // question is a bowireConfirm, which removes every element carrying
        // that class — this dialog included.
        overlay = el('div', { className: 'bowire-scaffold-overlay' }, dialog);
        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKey, true);
        return overlay;
    }

    (function registerScaffoldCommand() {
        function register() {
            if (typeof window === 'undefined' || typeof window.bowireRegisterPaletteCommand !== 'function') return false;
            window.bowireRegisterPaletteCommand({
                id: 'scaffold:open',
                // Getters: the language is resolved after this fragment runs.
                get label() { return t('scaffold.paletteLabel'); },
                get sublabel() { return t('scaffold.paletteSublabel'); },
                icon: 'plus',
                keywords: 'scaffold generate crud service openapi proto grpc rest stub new',
                run: function () { openScaffoldDialog(); }
            });
            return true;
        }
        // The palette API is set up in init.js; a fragment stitched in
        // before it waits for the first tick.
        if (!register() && typeof setTimeout === 'function') setTimeout(register, 0);
    })();
