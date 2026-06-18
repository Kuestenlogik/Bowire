    // ---- Tests ----
    //
    // Tests are now split across the two halves of the workbench:
    //   - Definition lives on the REQUEST side (renderTestDefinitionsTab,
    //     mounted as a Tests sub-tab in the request pane). Assertions
    //     are part of the method's configuration — they ride along
    //     with collections / recordings / export the same way the
    //     body and headers do.
    //   - Results live on the RESPONSE side (renderTestResultsTab,
    //     mounted as the "Test results" tab in the response pane).
    //     Read-only pass/fail per assertion against the last response,
    //     plus a re-run-against-last-response button.

    function renderTestDefinitionsTab() {
        var body = el('div', { className: 'bowire-pane-body bowire-tests-body' });

        body.appendChild(el('div', { className: 'bowire-tests-hint',
            textContent: 'Assertions that run automatically after every successful response. Use {{var}} substitution in expected values if you need them resolved at run time. Results show up in the Response pane under "Test results".' }));

        var tests = getTestsFor(selectedService.name, selectedMethod.name);

        // Add Assertion button
        body.appendChild(el('button', {
            className: 'bowire-tests-add',
            textContent: '+ Add Assertion',
            onClick: function () {
                var fresh = getTestsFor(selectedService.name, selectedMethod.name);
                fresh.push({ id: nextTestId(), path: 'response.', op: 'eq', expected: '' });
                setTestsFor(selectedService.name, selectedMethod.name, fresh);
                render();
            }
        }));

        if (tests.length === 0) {
            body.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 20px' },
                el('div', { className: 'bowire-empty-desc', textContent: 'No assertions yet. Add one and it will run after the next response.' })
            ));
            return body;
        }

        var list = el('div', { className: 'bowire-tests-list' });
        for (var i = 0; i < tests.length; i++) {
            (function (test, idx) {
                list.appendChild(renderAssertionEditorRow(test, idx));
            })(tests[i], i);
        }
        body.appendChild(list);

        // Export + Import + Clear buttons. Re-run lives in the
        // response pane's Test results tab — running is a verb that
        // operates on a response, not a definition.
        var actions = el('div', { className: 'bowire-tests-actions' },
            el('button', {
                className: 'bowire-tests-export',
                textContent: 'Export collection',
                title: 'Download a portable test collection JSON file (use with `bowire test`)',
                onClick: function () {
                    var collection = buildTestCollection(selectedService, selectedMethod, tests);
                    var json = JSON.stringify(collection, null, 2);
                    var blob = new Blob([json], { type: 'application/json' });
                    var a = document.createElement('a');
                    a.href = URL.createObjectURL(blob);
                    a.download = (selectedMethod.name || 'tests') + '.bwt';
                    a.click();
                    URL.revokeObjectURL(a.href);
                }
            }),
            el('button', {
                className: 'bowire-tests-import',
                textContent: 'Import collection',
                title: 'Load assertions from a previously exported test collection',
                onClick: function () {
                    var input = document.createElement('input');
                    input.type = 'file';
                    input.accept = '.json,application/json';
                    input.onchange = async function () {
                        if (!input.files || !input.files[0]) return;
                        try {
                            var text = await input.files[0].text();
                            var data = JSON.parse(text);
                            importTestCollection(data, selectedService, selectedMethod);
                            toast('Collection imported', 'success');
                            render();
                        } catch (e) {
                            toast('Import failed: ' + e.message, 'error');
                        }
                    };
                    input.click();
                }
            }),
            el('button', {
                className: 'bowire-tests-clear',
                textContent: 'Remove all',
                onClick: function () {
                    bowireConfirm('Remove all ' + tests.length + ' assertions for this method?', function () {
                        var backup = tests.slice();
                        setTestsFor(selectedService.name, selectedMethod.name, []);
                        lastAssertionResults = {};
                        render();
                        toast('Assertions removed', 'success', { undo: function () { setTestsFor(selectedService.name, selectedMethod.name, backup); render(); } });
                    }, { title: 'Remove Assertions', danger: true, confirmText: 'Remove All' });
                }
            })
        );
        body.appendChild(actions);

        return body;
    }

    // Response-side, read-only results view. Pass/fail icons +
    // failure details per assertion; an empty state nudges the user
    // back to the Request → Tests tab to define one. A re-run
    // button lets the operator replay the assertions against the
    // last response after they've edited a definition.
    function renderTestResultsTab() {
        var body = el('div', { className: 'bowire-pane-body bowire-tests-body bowire-tests-results-body' });
        var tests = getTestsFor(selectedService.name, selectedMethod.name);

        if (tests.length === 0) {
            body.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                el('div', { className: 'bowire-empty-desc', textContent: 'No assertions defined for this method.' }),
                el('button', {
                    className: 'bowire-empty-action',
                    textContent: 'Add assertions in Request → Tests',
                    onClick: function () {
                        try { activeRequestTab = 'tests'; } catch { /* let-shadow */ }
                        render();
                    }
                })
            ));
            return body;
        }

        if (lastResponseJson == null) {
            body.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                el('div', { className: 'bowire-empty-desc', textContent: 'Execute the request to see assertion results.' })
            ));
            return body;
        }

        // Summary line (passed / failed / untested).
        var summary = (typeof getAssertionSummary === 'function')
            ? getAssertionSummary(selectedService.name, selectedMethod.name)
            : null;
        if (summary) {
            var summaryText = summary.passed + ' / ' + summary.total + ' passed';
            if (summary.failed > 0) summaryText += ' · ' + summary.failed + ' failed';
            body.appendChild(el('div', {
                className: 'bowire-tests-results-summary'
                    + (summary.failed > 0 ? ' has-failure' : '')
                    + (summary.failed === 0 && summary.passed === summary.total ? ' all-pass' : ''),
                textContent: summaryText
            }));
        }

        var list = el('div', { className: 'bowire-tests-list bowire-tests-results-list' });
        for (var i = 0; i < tests.length; i++) {
            list.appendChild(renderAssertionResultRow(tests[i]));
        }
        body.appendChild(list);

        body.appendChild(el('div', { className: 'bowire-tests-actions' },
            el('button', {
                className: 'bowire-tests-run',
                textContent: 'Re-run against last response',
                title: 'Re-run all assertions against the most recent response',
                onClick: function () {
                    runAssertions(selectedService.name, selectedMethod.name,
                        statusInfo ? statusInfo.status : 'OK',
                        lastResponseJson);
                    render();
                }
            })
        ));

        return body;
    }

    // Old name retained as an alias — anything still importing it
    // (older mode handlers / external callers) keeps working. We
    // route it to the read-only results view since that's the closer
    // analogue to what the tab used to show when called from the
    // response pane.
    function renderTestsTab() { return renderTestResultsTab(); }

    // Read-only result row for the response-side Test results tab.
    // No inputs, no remove button — the operator edits assertions in
    // the request-pane Tests sub-tab; this view just shows what the
    // last response did to each one.
    function renderAssertionResultRow(test) {
        var result = lastAssertionResults[test.id];
        var statusClass = result === undefined ? 'untested' : (result.pass ? 'pass' : 'fail');
        var row = el('div', { className: 'bowire-test-row bowire-test-row-result ' + statusClass });
        var icon = '?';
        if (result !== undefined) icon = result.pass ? '✓' : '✗';
        row.appendChild(el('div', { className: 'bowire-test-status', textContent: icon }));
        row.appendChild(el('div', { className: 'bowire-test-path bowire-test-path-readonly', textContent: test.path || '' }));
        row.appendChild(el('div', { className: 'bowire-test-op bowire-test-op-readonly', textContent: test.op || 'eq' }));
        var op = test.op || 'eq';
        var needsExpected = !(op === 'exists' || op === 'notexists');
        row.appendChild(el('div', {
            className: 'bowire-test-expected bowire-test-expected-readonly',
            textContent: needsExpected ? (test.expected == null ? '' : String(test.expected)) : ''
        }));
        if (result && !result.pass) {
            var detail = el('div', { className: 'bowire-test-detail' });
            if (result.message) {
                detail.appendChild(el('span', { className: 'bowire-test-detail-error', textContent: result.message }));
            } else {
                detail.appendChild(el('span', { textContent: 'actual: ' + formatActual(result.actual) }));
            }
            row.appendChild(detail);
        }
        return row;
    }

    function renderAssertionEditorRow(test, idx) {
        var result = lastAssertionResults[test.id];
        var statusClass = result === undefined ? 'untested' : (result.pass ? 'pass' : 'fail');

        var row = el('div', { className: 'bowire-test-row ' + statusClass });

        // Status icon
        var icon = '?';
        if (result !== undefined) icon = result.pass ? '\u2713' : '\u2717';
        row.appendChild(el('div', { className: 'bowire-test-status', textContent: icon }));

        // Path input
        row.appendChild(el('input', {
            className: 'bowire-test-path',
            type: 'text',
            value: test.path || '',
            placeholder: 'response.path.to.field  or  status',
            title: 'JSON path against the response body, or "status" for the gRPC/HTTP status name',
            onChange: function (e) {
                var fresh = getTestsFor(selectedService.name, selectedMethod.name);
                if (fresh[idx]) { fresh[idx].path = e.target.value; setTestsFor(selectedService.name, selectedMethod.name, fresh); }
            }
        }));

        // Operator dropdown
        var opSelect = el('select', {
            className: 'bowire-test-op',
            title: 'Comparison operator',
            onChange: function (e) {
                var fresh = getTestsFor(selectedService.name, selectedMethod.name);
                if (fresh[idx]) { fresh[idx].op = e.target.value; setTestsFor(selectedService.name, selectedMethod.name, fresh); render(); }
            }
        });
        for (var i = 0; i < ASSERT_OPERATORS.length; i++) {
            var o = el('option', { value: ASSERT_OPERATORS[i], textContent: ASSERT_OPERATORS[i] });
            if ((test.op || 'eq') === ASSERT_OPERATORS[i]) o.setAttribute('selected', 'selected');
            opSelect.appendChild(o);
        }
        row.appendChild(opSelect);

        // Expected value input — hidden for exists/notexists
        var op = test.op || 'eq';
        var needsExpected = !(op === 'exists' || op === 'notexists');
        if (needsExpected) {
            row.appendChild(el('input', {
                className: 'bowire-test-expected',
                type: 'text',
                value: test.expected == null ? '' : String(test.expected),
                placeholder: op === 'matches' ? 'regex' : (op === 'type' ? 'string|number|boolean|object|array|null' : 'expected'),
                onChange: function (e) {
                    var fresh = getTestsFor(selectedService.name, selectedMethod.name);
                    if (fresh[idx]) { fresh[idx].expected = e.target.value; setTestsFor(selectedService.name, selectedMethod.name, fresh); }
                }
            }));
        } else {
            row.appendChild(el('div', { className: 'bowire-test-expected-placeholder' }));
        }

        // Remove button
        row.appendChild(el('button', {
            className: 'bowire-test-remove',
            innerHTML: svgIcon('close'),
            title: 'Remove assertion',
            onClick: function () {
                var fresh = getTestsFor(selectedService.name, selectedMethod.name);
                fresh.splice(idx, 1);
                setTestsFor(selectedService.name, selectedMethod.name, fresh);
                delete lastAssertionResults[test.id];
                render();
            }
        }));

        // Failure detail row
        if (result && !result.pass) {
            var detail = el('div', { className: 'bowire-test-detail' });
            if (result.message) {
                detail.appendChild(el('span', { className: 'bowire-test-detail-error', textContent: result.message }));
            } else {
                detail.appendChild(el('span', { textContent: 'actual: ' + formatActual(result.actual) }));
            }
            row.appendChild(detail);
        }

        return row;
    }

    function formatActual(v) {
        if (v === undefined) return '<undefined>';
        if (v === null) return 'null';
        if (typeof v === 'object') {
            try {
                var s = JSON.stringify(v);
                return s.length > 80 ? s.substring(0, 80) + '…' : s;
            } catch { return String(v); }
        }
        return String(v);
    }

