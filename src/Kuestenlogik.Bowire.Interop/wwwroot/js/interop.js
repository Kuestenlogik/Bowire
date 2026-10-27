    // ---- Collections from other tools, and back out as .http (#673, #674) ----
    //
    // The workbench face of the Interop gRPC service. Both calls go to its
    // JSON transcoding, the same rpcs `bowire import` / `bowire export http`
    // and the MCP tools call, so every surface reads the same formats and
    // reports the same skips:
    //
    //   POST {prefix}/api/v1/interop/import       ImportCollection
    //   POST {prefix}/api/v1/interop/export-http  ExportHttp
    //
    // Core reaches this fragment only behind `typeof` guards
    // (bowireInteropToolbarButtons, bowireInteropContextItems), so a host
    // without the Interop package renders the workbench unchanged.

    async function interopCall(path, body) {
        var res = await fetch(config.prefix + '/api/v1/interop/' + path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        var text = await res.text();
        var json = null;
        try { json = text ? JSON.parse(text) : null; } catch (e) { json = null; }
        if (!res.ok) {
            // A transcoded RpcException arrives as google.rpc.Status JSON.
            throw new Error((json && json.message) || text || ('HTTP ' + res.status));
        }
        return json || {};
    }

    // ---- import ----

    async function interopReadFiles(fileList) {
        var files = [];
        for (var i = 0; i < fileList.length; i++) {
            var f = fileList[i];
            // A picked folder reports paths below it; the importer wants
            // them relative to the collection root, so drop the folder name.
            var rel = f.webkitRelativePath || f.name;
            var slash = rel.indexOf('/');
            if (f.webkitRelativePath && slash > 0) rel = rel.substring(slash + 1);
            if (/(^|\/)(\.git|node_modules)\//.test(rel)) continue;
            files.push({ path: rel, content: await f.text() });
        }
        return files;
    }

    function interopPick(directory) {
        var input = document.createElement('input');
        input.type = 'file';
        if (directory) {
            input.webkitdirectory = true;
        } else {
            input.multiple = true;
            input.accept = '.http,.rest,.json';
        }
        input.onchange = async function () {
            if (!input.files || input.files.length === 0) return;
            try {
                await interopImportFiles(await interopReadFiles(input.files));
            } catch (e) {
                toast(t('interop.importFailed', { reason: e.message }), 'error');
            }
        };
        input.click();
    }

    async function interopImportFiles(files) {
        var result = await interopCall('import', { files: files });
        var collections = result.collections || [];
        var created = [];
        _suppressCollectionCreateLog = true;
        try {
            for (var i = 0; i < collections.length; i++) {
                var col = createCollection(collections[i].name || t('interop.unnamed'));
                var items = collections[i].items || [];
                for (var j = 0; j < items.length; j++) col.items.push(interopToWorkbenchItem(items[j]));
                created.push(col);
            }
        } finally {
            _suppressCollectionCreateLog = false;
        }
        if (created.length > 0) persistCollections();

        var envs = result.environments || [];
        if (envs.length > 0) {
            var list = getEnvironments();
            for (var e = 0; e < envs.length; e++) {
                var env = { id: genEnvId(), name: envs[e].name || t('interop.unnamed'), vars: envs[e].vars || {} };
                if (envs[e].auth && Object.keys(envs[e].auth).length > 0) env.auth = envs[e].auth;
                list.push(env);
            }
            saveEnvironments(list);
        }

        if (created.length > 0 && typeof collectionManagerSelectedId !== 'undefined') {
            collectionManagerSelectedId = created[0].id;
        }
        render();

        var requests = created.reduce(function (n, c) { return n + c.items.length; }, 0);
        var summary = t('interop.imported', {
            format: interopFormatLabel(result.format),
            requests: requests,
            collections: created.length,
            environments: envs.length
        });
        if (interopHasFindings(result.report)) interopShowReport(t('interop.importReportTitle'), summary, result.report);
        else toast(summary, 'success');
    }

    // An Interop CollectionItem → the shape collections.js stores.
    function interopToWorkbenchItem(item) {
        var messages = (item.messages && item.messages.length > 0) ? item.messages.slice() : ['{}'];
        var metadata = item.metadata && Object.keys(item.metadata).length > 0 ? item.metadata : null;
        var name = (item.folders || []).concat(item.name ? [item.name] : []).join(' / ');
        return {
            id: nextCollectionItemId(),
            name: name || undefined,
            protocol: item.protocol || 'rest',
            service: item.service || '',
            method: item.method || 'GET',
            methodType: item.methodType || 'Unary',
            body: messages[0],
            messages: messages,
            metadata: metadata,
            serverUrl: item.serverUrl || item.service || ''
        };
    }

    function interopFormatLabel(format) {
        switch (format) {
            case 'insomnia': return 'Insomnia';  // i18n-exempt: product name
            case 'bruno': return 'Bruno';  // i18n-exempt: product name
            case 'opencollection': return 'OpenCollection';  // i18n-exempt: format name
            case 'http': return '.http';
            default: return format || '';
        }
    }

    // ---- export ----

    // A stored item → an Interop CollectionItem. Discovered methods carry
    // no verb or path of their own; the schema has them, so they are filled
    // in here and the exporter can write a request line for them.
    function interopFromWorkbenchItem(item) {
        var out = {
            name: item.name || '',
            protocol: item.protocol || 'rest',
            service: item.service || '',
            method: item.method || '',
            methodType: item.methodType || 'Unary',
            messages: (item.messages && item.messages.length > 0) ? item.messages : [item.body || '{}'],
            metadata: {},
            serverUrl: item.serverUrl || ''
        };
        var meta = item.metadata || {};
        for (var k in meta) {
            if (Object.prototype.hasOwnProperty.call(meta, k) && typeof meta[k] === 'string') out.metadata[k] = meta[k];
        }
        if (typeof services !== 'undefined' && Array.isArray(services)) {
            for (var s = 0; s < services.length; s++) {
                if (services[s].name !== item.service) continue;
                var methods = services[s].methods || [];
                for (var m = 0; m < methods.length; m++) {
                    if (methods[m].name !== item.method) continue;
                    if (methods[m].httpMethod) out.httpMethod = methods[m].httpMethod;
                    if (methods[m].httpPath) out.httpPath = methods[m].httpPath;
                    if (!out.serverUrl && services[s].originUrl) out.serverUrl = services[s].originUrl;
                }
            }
        }
        return out;
    }

    function interopEnvironments() {
        return getEnvironments().map(function (env) {
            var vars = {};
            var auth = {};
            var src = env.vars || {};
            for (var k in src) {
                if (Object.prototype.hasOwnProperty.call(src, k)) vars[k] = interopScalar(src[k]);
            }
            var a = env.auth || {};
            for (var key in a) {
                if (Object.prototype.hasOwnProperty.call(a, key) && a[key] != null && typeof a[key] !== 'object') auth[key] = String(a[key]);
            }
            return { name: env.name || '', vars: vars, auth: auth };
        });
    }

    function interopScalar(value) {
        if (value == null) return '';
        return typeof value === 'string' ? value : JSON.stringify(value);
    }

    async function interopExportHttp(col) {
        try {
            var result = await interopCall('export-http', {
                collections: [{ name: col.name || '', items: (col.items || []).map(interopFromWorkbenchItem) }],
                environments: interopEnvironments()
            });
            var base = (col.name || 'collection').replace(/[^a-zA-Z0-9._-]/g, '_');
            interopDownload(base + '.http', result.http || '', 'text/plain');
            if (result.envJson) interopDownload('http-client.env.json', result.envJson, 'application/json');
            var summary = t('interop.exported', { name: base + '.http' });
            if (interopHasFindings(result.report)) interopShowReport(t('interop.exportReportTitle'), summary, result.report);
            else toast(summary, 'success');
        } catch (e) {
            toast(t('interop.exportFailed', { reason: e.message }), 'error');
        }
    }

    function interopDownload(fileName, text, type) {
        var blob = new Blob([text], { type: type });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = fileName;
        a.click();
        URL.revokeObjectURL(a.href);
    }

    // ---- report ----

    function interopHasFindings(report) {
        if (!report) return false;
        var entries = report.entries || [];
        for (var i = 0; i < entries.length; i++) {
            var kind = interopKind(entries[i]);
            if (kind === 'APPROXIMATED' || kind === 'SKIPPED') return true;
        }
        return (report.credentials || []).length > 0;
    }

    // ReportEntry.kind as protobuf JSON writes it: "KIND_SKIPPED".
    function interopKind(entry) {
        return String((entry && entry.kind) || '').replace(/^KIND_/, '');
    }

    // What changed, what was left out, and which credentials sit in clear
    // text — the parts of an import the operator has to look at.
    function interopShowReport(title, summary, report) {
        var existing = document.querySelector('.bowire-interop-report-overlay');
        if (existing) existing.remove();

        var groups = [
            { kind: 'SKIPPED', heading: t('interop.reportSkipped') },
            { kind: 'APPROXIMATED', heading: t('interop.reportApproximated') }
        ];
        var body = el('div', { className: 'bowire-interop-report-body' });
        for (var g = 0; g < groups.length; g++) {
            var rows = (report.entries || []).filter(function (e) { return interopKind(e) === groups[g].kind; });
            if (rows.length === 0) continue;
            var list = el('ul', { className: 'bowire-interop-report-list' });
            for (var r = 0; r < rows.length; r++) {
                list.appendChild(el('li', {},
                    el('strong', { textContent: rows[r].subject || '' }),
                    el('span', { textContent: ' — ' + (rows[r].detail || '') })));
            }
            body.appendChild(el('div', { className: 'bowire-interop-report-heading', textContent: groups[g].heading + ' (' + rows.length + ')' }));
            body.appendChild(list);
        }
        var creds = report.credentials || [];
        if (creds.length > 0) {
            var credList = el('ul', { className: 'bowire-interop-report-list' });
            for (var c = 0; c < creds.length; c++) {
                credList.appendChild(el('li', {},
                    el('strong', { textContent: creds[c].subject || '' }),
                    el('span', { textContent: ' — ' + (creds[c].field || '') })));
            }
            body.appendChild(el('div', { className: 'bowire-interop-report-heading', textContent: t('interop.reportCredentials') + ' (' + creds.length + ')' }));
            body.appendChild(el('div', { className: 'bowire-interop-report-hint', textContent: t('interop.reportCredentialsHint') }));
            body.appendChild(credList);
        }

        function close() { overlay.remove(); }
        var okBtn = el('button', { className: 'bowire-confirm-btn', textContent: t('common.close'), onClick: close });
        var dialog = el('div', {
            className: 'bowire-confirm-dialog bowire-interop-report',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-labelledby': 'bowire-interop-report-title'
        },
            el('div', { id: 'bowire-interop-report-title', className: 'bowire-confirm-title', textContent: title }),
            el('div', { className: 'bowire-confirm-message', textContent: summary }),
            body,
            el('div', { className: 'bowire-confirm-actions' }, okBtn));
        var overlay = el('div', {
            className: 'bowire-confirm-overlay bowire-interop-report-overlay',
            onClick: function (e) { if (e.target === overlay) close(); }
        }, dialog);
        overlay.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
        document.body.appendChild(overlay);
        okBtn.focus();
    }

    // ---- entry points core renders behind typeof guards ----

    // Collection detail toolbar: import next to "Import Postman", and the
    // collection itself out as .http.
    function bowireInteropToolbarButtons(col) {
        return [
            el('button', {
                className: 'bowire-recording-action-btn',
                title: t('interop.importTitle'),
                onClick: function (ev) { interopOpenImportMenu(ev.currentTarget); }
            },
                el('span', { innerHTML: svgIcon('upload') }),
                el('span', { textContent: t('interop.import') })),
            el('button', {
                className: 'bowire-recording-action-btn',
                disabled: !col.items || col.items.length === 0,
                title: t('interop.exportHttpTitle'),
                onClick: function () { interopExportHttp(col); }
            },
                el('span', { innerHTML: svgIcon('download') }),
                el('span', { textContent: t('interop.exportHttp') }))
        ];
    }

    // The Collections node's context menu: import without a collection open.
    function bowireInteropContextItems() {
        return [
            { icon: '↥', label: t('interop.importFiles'), onClick: function () { interopPick(false); } },
            { icon: '↥', label: t('interop.importFolder'), onClick: function () { interopPick(true); } }
        ];
    }

    // Files or a folder: a Bruno collection is a folder, everything else a file.
    function interopOpenImportMenu(anchor) {
        var existing = document.querySelector('.bowire-interop-import-menu');
        if (existing) { existing.remove(); return; }
        var menu = el('div', { className: 'bowire-interop-import-menu bowire-dropdown-menu visible' },
            el('div', {
                className: 'bowire-dropdown-item',
                textContent: t('interop.importFiles'),
                title: t('interop.importFilesTitle'),
                onClick: function () { menu.remove(); interopPick(false); }
            }),
            el('div', {
                className: 'bowire-dropdown-item',
                textContent: t('interop.importFolder'),
                title: t('interop.importFolderTitle'),
                onClick: function () { menu.remove(); interopPick(true); }
            }));
        // Fixed at the button: the toolbar is no positioning context, and
        // it wraps, so the menu opens under whichever row the button is on.
        var rect = anchor.getBoundingClientRect();
        menu.style.position = 'fixed';
        menu.style.top = rect.bottom + 'px';
        menu.style.left = rect.left + 'px';
        menu.style.right = 'auto';
        document.body.appendChild(menu);
        setTimeout(function () {
            document.addEventListener('click', function () { menu.remove(); }, { once: true });
        }, 0);
    }
