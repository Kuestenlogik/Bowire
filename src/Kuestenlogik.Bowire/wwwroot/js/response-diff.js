    // ---- Response Comparison / Diff View ----
    // Renders a side-by-side diff of two user-selected response snapshots.
    // Called from renderResponsePane when diffViewOpen is true. The snapshot
    // selector dropdowns let the user pick any two of the last
    // MAX_RESPONSE_SNAPSHOTS captures for the current method.

    function formatSnapshotLabel(snap, idx, total) {
        var d = new Date(snap.timestamp);
        var pad = function (n) { return n < 10 ? '0' + n : String(n); };
        var time = pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
        var label = '#' + (idx + 1) + '  ' + time;
        if (snap.status) label += '  ' + snap.status;
        if (snap.durationMs) label += '  ' + snap.durationMs + 'ms';
        if (idx === total - 1) label += '  (latest)';
        return label;
    }

    function renderResponseDiff() {
        var snaps = getResponseSnapshots();
        var container = el('div', { className: 'bowire-diff-view' });

        // Auto-select sensible defaults when the view opens
        if (diffSnapshotA === null || diffSnapshotA >= snaps.length) {
            diffSnapshotA = snaps.length >= 2 ? snaps.length - 2 : 0;
        }
        if (diffSnapshotB === null || diffSnapshotB >= snaps.length) {
            diffSnapshotB = snaps.length - 1;
        }

        // ---- Header: snapshot selectors + close button ----
        var header = el('div', { className: 'bowire-diff-header' });

        // Left selector (snapshot A)
        var selectA = el('select', {
            className: 'bowire-diff-select',
            onChange: function (e) {
                diffSnapshotA = parseInt(e.target.value, 10);
                render();
            }
        });
        for (var i = 0; i < snaps.length; i++) {
            var optA = el('option', {
                value: String(i),
                textContent: formatSnapshotLabel(snaps[i], i, snaps.length)
            });
            if (i === diffSnapshotA) optA.selected = true;
            selectA.appendChild(optA);
        }

        // Right selector (snapshot B)
        var selectB = el('select', {
            className: 'bowire-diff-select',
            onChange: function (e) {
                diffSnapshotB = parseInt(e.target.value, 10);
                render();
            }
        });
        for (var j = 0; j < snaps.length; j++) {
            var optB = el('option', {
                value: String(j),
                textContent: formatSnapshotLabel(snaps[j], j, snaps.length)
            });
            if (j === diffSnapshotB) optB.selected = true;
            selectB.appendChild(optB);
        }

        header.appendChild(el('span', { className: 'bowire-diff-title', textContent: 'Compare responses' }));
        header.appendChild(selectA);
        header.appendChild(el('span', { className: 'bowire-diff-vs', textContent: 'vs' }));
        header.appendChild(selectB);

        // Stats
        var snapA = snaps[diffSnapshotA];
        var snapB = snaps[diffSnapshotB];
        var prevText = prettyJson(snapA ? snapA.body : '');
        var currText = prettyJson(snapB ? snapB.body : '');
        var diff = computeLineDiff(prevText, currText);

        var added = 0, removed = 0;
        for (var di = 0; di < diff.length; di++) {
            if (diff[di].type === 'add') added++;
            else if (diff[di].type === 'del') removed++;
        }

        header.appendChild(el('span', { className: 'bowire-diff-stat add', textContent: '+' + added }));
        header.appendChild(el('span', { className: 'bowire-diff-stat del', textContent: '\u2212' + removed }));

        header.appendChild(el('button', {
            className: 'bowire-pane-btn',
            textContent: 'Close Diff',
            onClick: function () {
                diffViewOpen = false;
                diffSnapshotA = null;
                diffSnapshotB = null;
                render();
            }
        }));

        container.appendChild(header);

        // ---- Diff body ----
        if (diffSnapshotA === diffSnapshotB) {
            container.appendChild(el('div', { className: 'bowire-diff-empty',
                textContent: 'Select two different snapshots to compare.' }));
        } else if (added === 0 && removed === 0) {
            container.appendChild(el('div', { className: 'bowire-diff-empty',
                textContent: 'Both responses are identical.' }));
        } else {
            var diffBox = el('pre', { className: 'bowire-diff-body' });
            for (var dj = 0; dj < diff.length; dj++) {
                var line = diff[dj];
                var prefix = line.type === 'add' ? '+ ' : line.type === 'del' ? '- ' : '  ';
                diffBox.appendChild(el('div', {
                    className: 'bowire-diff-line bowire-diff-' + line.type,
                    textContent: prefix + line.text
                }));
            }
            container.appendChild(diffBox);
        }

        return container;
    }
