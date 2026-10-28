    // ---- Data sets for data-driven collection runs (#675) ----
    //
    // The workbench face of the DataSets gRPC service: a data file attached
    // to a collection is read through its JSON transcoding
    // (POST {prefix}/api/v1/datasets/parse) — the same rpc `bowire test
    // --data` calls — so a file reads and fails the same way in both places.
    //
    // Core reaches this fragment only behind `typeof` guards
    // (bowireCollectionDataSet, bowireDataSetToolbarItems): without the
    // DataSets package a collection runs once, as before.
    //
    // The attached file lives for this session only. Data sets often hold
    // test accounts and are re-exported per run; they are not written into
    // the workspace.

    var _bowireDataSets = {};   // collectionId → { fileName, format, rows, columns, failFast }

    /** The data set attached to a collection, or null. */
    function bowireCollectionDataSet(collectionId) {
        return _bowireDataSets[collectionId] || null;
    }

    async function bowireDataSetParse(fileName, content) {
        var res = await fetch(config.prefix + '/api/v1/datasets/parse', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ fileName: fileName, content: content })
        });
        var text = await res.text();
        var json = null;
        try { json = text ? JSON.parse(text) : null; } catch (e) { json = null; }
        // A refused file arrives as google.rpc.Status JSON naming the line.
        if (!res.ok) throw new Error((json && json.message) || text || ('HTTP ' + res.status));
        return json || {};
    }

    function bowireDataSetPick(col) {
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = '.csv,.tsv,.json,.txt';
        input.onchange = async function () {
            if (!input.files || !input.files[0]) return;
            var file = input.files[0];
            try {
                var parsed = await bowireDataSetParse(file.name, await file.text());
                _bowireDataSets[col.id] = {
                    fileName: file.name,
                    format: parsed.format || 'csv',
                    columns: parsed.columns || [],
                    rows: parsed.rows || [],
                    failFast: false
                };
                toast(t('datasets.attached', { name: file.name, rows: (parsed.rows || []).length }), 'success');
                render();
            } catch (e) {
                toast(t('datasets.parseFailed', { reason: e.message }), 'error');
            }
        };
        input.click();
    }

    /** Toolbar entries for a collection: "Data file…", or the attached file's chip. */
    function bowireDataSetToolbarItems(col, isRunning) {
        var ds = _bowireDataSets[col.id];
        if (!ds) {
            return [el('button', {
                className: 'bowire-recording-action-btn',
                disabled: isRunning || !col.items || col.items.length === 0,
                title: t('datasets.pickTitle'),
                onClick: function () { bowireDataSetPick(col); }
            },
                el('span', { innerHTML: svgIcon('upload') }),
                el('span', { textContent: t('datasets.pick') }))];
        }
        var chip = el('span', {
            className: 'bowire-dataset-chip',
            title: t('datasets.chipTitle', { columns: ds.columns.join(', ') })
        },
            el('span', { textContent: t('datasets.chip', { name: ds.fileName, rows: ds.rows.length }) }),
            el('button', {
                className: 'bowire-dataset-chip-remove',
                disabled: isRunning,
                title: t('datasets.remove'),
                'aria-label': t('datasets.remove'),
                innerHTML: svgIcon('close'),
                onClick: function () { delete _bowireDataSets[col.id]; render(); }
            }));
        var failFast = el('label', { className: 'bowire-dataset-failfast', title: t('datasets.failFastTitle') },
            el('input', {
                type: 'checkbox',
                checked: ds.failFast,
                disabled: isRunning,
                onChange: function (e) { ds.failFast = !!e.target.checked; }
            }),
            el('span', { textContent: t('datasets.failFast') }));
        return [chip, failFast];
    }
