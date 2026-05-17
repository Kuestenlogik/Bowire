    // ---- Flows ----
    // Visual node-based editor for multi-step API interactions.
    // Node types:
    //   - Request: invoke a method (protocol, service, method, body)
    //   - Condition: branch on response — trueBranch[] / falseBranch[]
    //   - Delay: wait N seconds
    //   - Variable: set ${var} from response
    //   - Loop: repeat body[] N times or until condition
    //
    // Phase 2: branching conditions, loop nodes, response viewer,
    // import/export, recursive runner.

    const FLOWS_KEY = 'bowire_flows';
    let flowsList = [];
    let flowEditorSelectedId = null;
    // Results keyed by node.id: { [nodeId]: { pass, status, durationMs, response, error, iteration?, assertions? } }
    let flowRunResults = {};
    let flowRunActiveNodeId = null;
    let flowRunStatus = null; // null | 'running' | 'done' | 'stopped'
    let flowRunFlowId = null;
    let flowExpandedNodeId = null;
    let flowResultExpandedNodeId = null; // which node's response viewer is open
    let flowDragState = null;
    // Per-run variable store. Variable + Foreach + Request-extract nodes
    // write here; substituteVars (in history-env.js) reads it so ${name}
    // placeholders in any later node's body resolve at run-time. Cleared
    // at run start and pinned to the Watch-Panel in the canvas footer.
    let flowVars = {};

    function loadFlows() {
        try {
            var raw = localStorage.getItem(FLOWS_KEY);
            var list = raw ? JSON.parse(raw) : [];
            // Migrate Phase 1 flows: ensure condition nodes have branch arrays
            for (var fi = 0; fi < list.length; fi++) {
                migrateNodes(list[fi].nodes);
            }
            return list;
        } catch { return []; }
    }

    function migrateNodes(nodes) {
        if (!nodes) return;
        for (var i = 0; i < nodes.length; i++) {
            var n = nodes[i];
            if (n.type === 'condition') {
                if (!n.trueBranch) n.trueBranch = [];
                if (!n.falseBranch) n.falseBranch = [];
                migrateNodes(n.trueBranch);
                migrateNodes(n.falseBranch);
            }
            if (n.type === 'loop') {
                if (!n.body) n.body = [];
                migrateNodes(n.body);
            }
        }
    }

    function persistFlows() {
        try { localStorage.setItem(FLOWS_KEY, JSON.stringify(flowsList)); } catch {}
    }

    function createFlow(name) {
        var flow = {
            id: 'flow_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6),
            name: name || 'New Flow',
            nodes: [],
            createdAt: Date.now()
        };
        flowsList.push(flow);
        persistFlows();
        return flow;
    }

    function duplicateFlow(id) {
        var src = flowsList.find(function (f) { return f.id === id; });
        if (!src) return null;
        var copy = JSON.parse(JSON.stringify(src));
        copy.id = 'flow_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
        copy.name = src.name + ' (Copy)';
        copy.createdAt = Date.now();
        reassignNodeIds(copy.nodes);
        flowsList.push(copy);
        persistFlows();
        return copy;
    }

    function reassignNodeIds(nodes) {
        if (!nodes) return;
        for (var i = 0; i < nodes.length; i++) {
            nodes[i].id = 'node_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6) + i;
            if (nodes[i].trueBranch) reassignNodeIds(nodes[i].trueBranch);
            if (nodes[i].falseBranch) reassignNodeIds(nodes[i].falseBranch);
            if (nodes[i].body) reassignNodeIds(nodes[i].body);
        }
    }

    function deleteFlow(id) {
        flowsList = flowsList.filter(function (f) { return f.id !== id; });
        if (flowEditorSelectedId === id) flowEditorSelectedId = null;
        persistFlows();
    }

    function addNodeToFlow(flowId, node, targetArray) {
        var arr = targetArray;
        if (!arr) {
            var flow = flowsList.find(function (f) { return f.id === flowId; });
            if (!flow) return;
            arr = flow.nodes;
        }
        node.id = 'node_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
        if (node.type === 'condition') {
            if (!node.trueBranch) node.trueBranch = [];
            if (!node.falseBranch) node.falseBranch = [];
        }
        if (node.type === 'loop') {
            if (!node.body) node.body = [];
        }
        arr.push(node);
        persistFlows();
    }

    function findAndRemoveNode(nodes, nodeId) {
        for (var i = 0; i < nodes.length; i++) {
            if (nodes[i].id === nodeId) {
                nodes.splice(i, 1);
                return true;
            }
            if (nodes[i].trueBranch && findAndRemoveNode(nodes[i].trueBranch, nodeId)) return true;
            if (nodes[i].falseBranch && findAndRemoveNode(nodes[i].falseBranch, nodeId)) return true;
            if (nodes[i].body && findAndRemoveNode(nodes[i].body, nodeId)) return true;
        }
        return false;
    }

    function removeNodeFromFlow(flowId, nodeId) {
        var flow = flowsList.find(function (f) { return f.id === flowId; });
        if (!flow) return;
        findAndRemoveNode(flow.nodes, nodeId);
        if (flowExpandedNodeId === nodeId) flowExpandedNodeId = null;
        if (flowResultExpandedNodeId === nodeId) flowResultExpandedNodeId = null;
        persistFlows();
    }

    function moveNodeInArray(arr, fromIdx, toIdx) {
        if (!arr || fromIdx < 0 || toIdx < 0 || fromIdx >= arr.length || toIdx >= arr.length) return;
        var node = arr.splice(fromIdx, 1)[0];
        arr.splice(toIdx, 0, node);
    }

    function moveNodeInFlow(flowId, fromIdx, toIdx) {
        var flow = flowsList.find(function (f) { return f.id === flowId; });
        if (!flow) return;
        moveNodeInArray(flow.nodes, fromIdx, toIdx);
        persistFlows();
    }

    function duplicateNode(flowId, nodeId) {
        var flow = flowsList.find(function (f) { return f.id === flowId; });
        if (!flow) return;
        duplicateNodeInArray(flow.nodes, nodeId);
        persistFlows();
    }

    function duplicateNodeInArray(arr, nodeId) {
        for (var i = 0; i < arr.length; i++) {
            if (arr[i].id === nodeId) {
                var copy = JSON.parse(JSON.stringify(arr[i]));
                reassignNodeIds([copy]);
                arr.splice(i + 1, 0, copy);
                return true;
            }
            if (arr[i].trueBranch && duplicateNodeInArray(arr[i].trueBranch, nodeId)) return true;
            if (arr[i].falseBranch && duplicateNodeInArray(arr[i].falseBranch, nodeId)) return true;
            if (arr[i].body && duplicateNodeInArray(arr[i].body, nodeId)) return true;
        }
        return false;
    }

    // Count all nodes recursively (for sidebar badge)
    function countNodes(nodes) {
        var c = 0;
        if (!nodes) return 0;
        for (var i = 0; i < nodes.length; i++) {
            c++;
            if (nodes[i].trueBranch) c += countNodes(nodes[i].trueBranch);
            if (nodes[i].falseBranch) c += countNodes(nodes[i].falseBranch);
            if (nodes[i].body) c += countNodes(nodes[i].body);
        }
        return c;
    }

    // ---- Import / Export ----
    function exportFlow(id) {
        var flow = flowsList.find(function (f) { return f.id === id; });
        if (!flow) return;
        var json = JSON.stringify(flow, null, 2);
        var blob = new Blob([json], { type: 'application/json' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = (flow.name || 'flow').replace(/[^a-zA-Z0-9_-]/g, '_') + '.bwf';
        a.click();
        URL.revokeObjectURL(a.href);
    }

    function importFlow() {
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = '.bwf,.json';
        input.onchange = function () {
            if (!input.files || !input.files[0]) return;
            var reader = new FileReader();
            reader.onload = function () {
                try {
                    var data = JSON.parse(reader.result);
                    if (!data.nodes || !Array.isArray(data.nodes)) {
                        alert('Invalid flow file.');
                        return;
                    }
                    data.id = 'flow_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
                    data.name = (data.name || 'Imported Flow') + ' (imported)';
                    data.createdAt = Date.now();
                    reassignNodeIds(data.nodes);
                    migrateNodes(data.nodes);
                    flowsList.push(data);
                    persistFlows();
                    flowEditorSelectedId = data.id;
                    render();
                } catch (e) {
                    alert('Failed to import flow: ' + e.message);
                }
            };
            reader.readAsText(input.files[0]);
        };
        input.click();
    }

    // ---- Flow Runner (Phase 2 — recursive, branch-aware) ----
    async function runFlow(id) {
        var flow = flowsList.find(function (f) { return f.id === id; });
        if (!flow || flow.nodes.length === 0) return;

        flowRunResults = {};
        flowRunActiveNodeId = null;
        flowRunStatus = 'running';
        flowRunFlowId = id;
        flowVars = {};        // reset per-run vars; the Watch-Panel reflects this
        render();

        try {
            await runNodeList(flow.nodes);
        } catch (e) {
            // Fatal runner error
        }

        flowRunActiveNodeId = null;
        flowRunStatus = 'done';
        render();
    }

    async function runNodeList(nodes) {
        for (var i = 0; i < nodes.length; i++) {
            if (flowRunStatus !== 'running') return;
            await runSingleNode(nodes[i]);
        }
    }

    async function runSingleNode(node) {
        if (flowRunStatus !== 'running') return;
        flowRunActiveNodeId = node.id;
        render();

        try {
            if (node.type === 'request') {
                var t0 = performance.now();
                var result = await executeFlowRequest(node);
                result.durationMs = Math.round(performance.now() - t0);
                flowRunResults[node.id] = result;
                if (result.response) captureResponse(result.response);

            } else if (node.type === 'delay') {
                await new Promise(function (resolve) {
                    setTimeout(resolve, (node.delayMs || 1000));
                });
                flowRunResults[node.id] = { pass: true, status: 'Delayed ' + (node.delayMs || 1000) + 'ms' };

            } else if (node.type === 'condition') {
                var condResult = evaluateCondition(node);
                flowRunResults[node.id] = {
                    pass: condResult,
                    status: condResult ? 'True branch' : 'False branch',
                    branch: condResult ? 'true' : 'false'
                };
                // Execute the matching branch
                var branch = condResult ? (node.trueBranch || []) : (node.falseBranch || []);
                if (branch.length > 0) {
                    await runNodeList(branch);
                }

            } else if (node.type === 'variable') {
                var val = resolveResponseVar(node.path || '');
                if (val !== null && node.varName) {
                    // Write into the per-run var store so substituteVars
                    // can resolve ${varName} from any later node.
                    flowVars[node.varName] = val;
                    flowRunResults[node.id] = { pass: true, status: 'Set ' + node.varName + ' = ' + truncateForDisplay(val) };
                } else if (!node.varName) {
                    flowRunResults[node.id] = { pass: false, status: 'Variable name is empty' };
                } else {
                    flowRunResults[node.id] = { pass: false, status: 'Path not found: ' + node.path };
                }

            } else if (node.type === 'loop') {
                var loopType = node.loopType || 'count';
                if (loopType === 'foreach') {
                    // Foreach: walk the source JSONPath against the last
                    // response (or a previously-stored object var), iterate
                    // each entry as ${itemVar}. Bulk-API / batch-import
                    // pattern — "for each user in users[] do this request".
                    var sourceArr = resolveForeachSource(node.loopSource || '');
                    if (!Array.isArray(sourceArr)) {
                        flowRunResults[node.id] = { pass: false, status: 'Foreach source is not an array: ' + (node.loopSource || '(empty)') };
                    } else {
                        var itemVar = (node.loopItemVar || 'item').trim();
                        var iterations = 0;
                        for (var fi = 0; fi < sourceArr.length; fi++) {
                            if (flowRunStatus !== 'running') break;
                            iterations++;
                            // Bind the item AND a parallel ${itemVar_index}
                            // so loops over [a,b,c] can also reference position.
                            flowVars[itemVar] = typeof sourceArr[fi] === 'object'
                                ? JSON.stringify(sourceArr[fi])
                                : String(sourceArr[fi]);
                            flowVars[itemVar + '_index'] = String(fi);
                            flowRunResults[node.id] = { pass: true, status: 'Iteration ' + iterations + '/' + sourceArr.length + ' — ${' + itemVar + '} = ' + truncateForDisplay(flowVars[itemVar]), iteration: iterations };
                            render();
                            if (node.body && node.body.length > 0) {
                                await runNodeList(node.body);
                            }
                        }
                        // Clean up the per-iteration bindings so a downstream
                        // node doesn't accidentally pick up the last value.
                        delete flowVars[itemVar];
                        delete flowVars[itemVar + '_index'];
                        flowRunResults[node.id] = { pass: true, status: iterations + ' iteration' + (iterations !== 1 ? 's' : '') + ' completed (foreach)', iteration: iterations };
                    }
                } else {
                    var maxIter = loopType === 'count' ? (node.loopCount || 1) : 100;
                    var iterations = 0;
                    for (var li = 0; li < maxIter; li++) {
                        if (flowRunStatus !== 'running') break;
                        iterations++;
                        flowRunResults[node.id] = { pass: true, status: 'Iteration ' + iterations + '/' + maxIter, iteration: iterations };
                        render();
                        if (node.body && node.body.length > 0) {
                            await runNodeList(node.body);
                        }
                        // For while-loops, check condition after each iteration
                        if (loopType === 'while') {
                            var loopCond = evaluateCondition(node);
                            if (!loopCond) break;
                        }
                    }
                    flowRunResults[node.id] = { pass: true, status: iterations + ' iteration' + (iterations !== 1 ? 's' : '') + ' completed', iteration: iterations };
                }
            }
        } catch (e) {
            flowRunResults[node.id] = { pass: false, status: 'Error', error: e.message };
        }
        render();
    }

    function executeFlowRequest(node) {
        var messages = [substituteVars(node.body || '{}')];
        var url = config.prefix + '/api/invoke'
            + (node.serverUrl ? '?serverUrl=' + encodeURIComponent(node.serverUrl) : '');
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                service: node.service,
                method: node.method,
                messages: messages,
                protocol: node.protocol || null
            })
        }).then(function (resp) {
            return resp.json().then(function (json) {
                var baseStatus = json.status || (resp.ok ? 'OK' : 'Error');
                var baseOk = resp.ok && !json.error;
                // Evaluate inline assertions against an envelope that
                // carries both the response body AND the call metadata
                // (status / durationMs / error), so a request-node can
                // assert on either without an extra Condition-node.
                var envelope = {
                    status: baseStatus,
                    durationMs: json.duration_ms || 0,
                    error: json.error || null,
                    response: tryParseAsJson(json.response),
                };
                var assertionResults = evaluateAssertions(node.assertions || [], envelope);
                var allAssertionsPassed = assertionResults.every(function (a) { return a.pass; });
                return {
                    pass: baseOk && allAssertionsPassed,
                    status: assertionResults.length === 0
                        ? baseStatus
                        : baseStatus + ' · ' + assertionResults.filter(function (a) { return a.pass; }).length + '/' + assertionResults.length + ' assertions',
                    durationMs: json.duration_ms || 0,
                    response: json.response,
                    error: json.error || null,
                    assertions: assertionResults,
                };
            });
        });
    }

    /**
     * Resolve an assertion path against the request-envelope
     * { status, durationMs, error, response }. Three forms:
     *   `$.field` or `$` — alias for `response.field` / response root.
     *                       90% of assertions target the body, so this
     *                       is the ergonomic shortcut.
     *   `response.foo` / `status` / `durationMs` — explicit envelope key.
     *   `field` — plain dotted path against the body (no $-anchor).
     */
    function resolveAssertionPath(envelope, path) {
        if (envelope == null) return undefined;
        var trimmed = path == null ? '' : String(path).trim();
        if (trimmed === '' || trimmed === '$') return envelope.response;
        if (trimmed.indexOf('$.') === 0) {
            return walkJsonPath(envelope.response, trimmed.substring(2));
        }
        if (trimmed === 'response') return envelope.response;
        // Top-level envelope keys go through directly; everything
        // else is treated as a body-relative path.
        if (trimmed.indexOf('.') === -1 && Object.prototype.hasOwnProperty.call(envelope, trimmed)) {
            return envelope[trimmed];
        }
        if (trimmed.indexOf('response.') === 0) {
            return walkJsonPath(envelope.response, trimmed.substring('response.'.length));
        }
        return walkJsonPath(envelope.response, trimmed);
    }

    /**
     * Drive an array of {path, op, value} assertions against a
     * parsed response object. Returns one row per assertion with
     * pass/fail + a human-readable detail string for the result
     * viewer. The op vocabulary matches Condition-nodes so users
     * don't have to learn a second predicate language.
     */
    function evaluateAssertions(assertions, envelope) {
        if (!Array.isArray(assertions) || assertions.length === 0) return [];
        return assertions.map(function (a) {
            var actual = resolveAssertionPath(envelope, a.path || '');
            var pass = false;
            var op = a.op || 'eq';
            var expected = a.value;
            if (op === 'eq') pass = String(actual) === String(expected);
            else if (op === 'neq') pass = String(actual) !== String(expected);
            else if (op === 'gt') pass = Number(actual) > Number(expected);
            else if (op === 'lt') pass = Number(actual) < Number(expected);
            else if (op === 'contains') pass = String(actual).indexOf(String(expected)) !== -1;
            else if (op === 'exists') pass = actual !== undefined && actual !== null;
            else if (op === 'missing') pass = actual === undefined || actual === null;
            return {
                path: a.path || '$',
                op: op,
                expected: expected,
                actual: actual === undefined ? null : (typeof actual === 'object' ? JSON.stringify(actual) : String(actual)),
                pass: pass,
            };
        });
    }

    /**
     * Resolve the Foreach loop's source — a JSONPath into the last
     * response (or a previously-bound array variable). Accepts:
     *   $.users       — array nested in the captured response
     *   $             — whole response (when it IS an array)
     *   myArray       — a ${myArray} bound via Variable / Foreach
     */
    function resolveForeachSource(path) {
        var trimmed = (path || '').trim();
        if (!trimmed) return null;
        if (trimmed === '$' || trimmed.indexOf('$.') === 0 || trimmed.charAt(0) === '$') {
            var inner = trimmed === '$' ? '' : (trimmed.indexOf('$.') === 0 ? trimmed.substring(2) : trimmed.substring(1));
            return walkJsonPath(lastResponseJson, inner);
        }
        // Try as a flow-var name — Variable-node may have captured
        // an array into ${name} via JSON.stringify; parse it back.
        if (Object.prototype.hasOwnProperty.call(flowVars, trimmed)) {
            var stored = flowVars[trimmed];
            try { return JSON.parse(stored); } catch { return null; }
        }
        return null;
    }

    /**
     * Project a recording (recordingsList entry) into a fresh flow with
     * one Request node per step. The Tier-3 + Recording-tab synergy:
     * captured traffic flows into the workbench's recording store via
     * the proxy's "Send to recording" action, then one click turns it
     * into an editable + replayable flow with inline assertions on the
     * recorded status. Returns the new flow's id.
     */
    function convertRecordingToFlow(recordingId) {
        if (typeof recordingsList === 'undefined' || !Array.isArray(recordingsList)) return null;
        var rec = recordingsList.find(function (r) { return r.id === recordingId; });
        if (!rec || !Array.isArray(rec.steps) || rec.steps.length === 0) return null;

        var flow = createFlow(rec.name + ' (flow)');
        for (var i = 0; i < rec.steps.length; i++) {
            var step = rec.steps[i];
            // The recorded `status` becomes the default assertion so
            // replaying the flow surfaces drift from the captured shape.
            var node = {
                type: 'request',
                protocol: step.protocol || (protocols.length > 0 ? protocols[0].id : 'grpc'),
                service: step.service || '',
                method: step.method || '',
                serverUrl: step.serverUrl || '',
                body: step.body || '{}',
                serviceMethodMode: 'custom',  // recordings often carry services the discovery tree doesn't have yet
                assertions: step.status
                    ? [{ path: 'status', op: 'eq', value: String(step.status) }]
                    : [],
            };
            addNodeToFlow(flow.id, node);
        }
        return flow.id;
    }

    function tryParseAsJson(value) {
        if (value == null) return null;
        if (typeof value !== 'string') return value;
        try { return JSON.parse(value); } catch { return value; }
    }

    function truncateForDisplay(s) {
        if (s == null) return '';
        var str = String(s);
        return str.length <= 60 ? str : str.slice(0, 57) + '…';
    }

    function evaluateCondition(node) {
        if (!lastResponseJson) return false;
        if (node.conditionPath) {
            var val = walkJsonPath(lastResponseJson, node.conditionPath);
            if (node.conditionOp === 'eq') return String(val) === String(node.conditionValue);
            if (node.conditionOp === 'neq') return String(val) !== String(node.conditionValue);
            if (node.conditionOp === 'gt') return Number(val) > Number(node.conditionValue);
            if (node.conditionOp === 'lt') return Number(val) < Number(node.conditionValue);
            if (node.conditionOp === 'contains') return String(val).indexOf(String(node.conditionValue)) !== -1;
            if (node.conditionOp === 'exists') return val !== undefined && val !== null;
            return !!val;
        }
        return true;
    }

    // ---- Flow Sidebar List ----
    function renderFlowsListInto(container) {
        if (flowsList.length === 0) {
            container.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding:32px' },
                el('div', { className: 'bowire-empty-title', textContent: 'No flows yet' }),
                el('div', { className: 'bowire-empty-desc', textContent: 'Create a flow to chain multiple API calls together.' }),
                el('button', {
                    id: 'bowire-flow-create-first-btn',
                    className: 'bowire-recording-action-btn',
                    style: 'margin-top:12px',
                    onClick: function () {
                        var flow = createFlow();
                        flowEditorSelectedId = flow.id;
                        render();
                    }
                }, el('span', { innerHTML: svgIcon('plus') }), el('span', { textContent: 'Create Flow' }))
            ));
            return;
        }

        for (var i = 0; i < flowsList.length; i++) {
            (function (flow) {
                var isActive = flowEditorSelectedId === flow.id;
                var isRunning = flowRunStatus === 'running' && flowRunFlowId === flow.id;
                var nodeCount = countNodes(flow.nodes);

                container.appendChild(el('div', {
                    id: 'bowire-flow-item-' + flow.id,
                    className: 'bowire-env-sidebar-item' + (isActive ? ' selected' : ''),
                    onClick: function () {
                        flowEditorSelectedId = flow.id;
                        render();
                    }
                },
                    isRunning
                        ? el('span', { className: 'bowire-flow-sidebar-running', innerHTML: svgIcon('play') })
                        : el('span', { className: 'bowire-flow-sidebar-icon', innerHTML: svgIcon('play') }),
                    el('span', { className: 'bowire-env-sidebar-item-name', textContent: flow.name }),
                    el('span', { className: 'bowire-env-sidebar-item-count', textContent: String(nodeCount) })
                ));
            })(flowsList[i]);
        }

        container.appendChild(el('div', { className: 'bowire-env-sidebar-add', style: 'padding:8px' },
            el('button', {
                id: 'bowire-flow-create-btn',
                className: 'bowire-recording-action-btn',
                style: 'width:100%',
                onClick: function () {
                    var flow = createFlow();
                    flowEditorSelectedId = flow.id;
                    render();
                }
            }, el('span', { innerHTML: svgIcon('plus') }), el('span', { textContent: 'New Flow' }))
        ));
    }

    // ---- Flow Canvas (Main Pane) ----
    function renderFlowCanvas() {
        var flow = flowsList.find(function (f) { return f.id === flowEditorSelectedId; });

        if (!flow) {
            return el('div', { id: 'bowire-flow-canvas-empty', className: 'bowire-flow-canvas' },
                el('div', { className: 'bowire-empty-state', style: 'padding:64px' },
                    el('div', { className: 'bowire-empty-title', textContent: 'Select a flow' }),
                    el('div', { className: 'bowire-empty-desc', textContent: 'Choose a flow from the sidebar or create a new one.' })
                )
            );
        }

        var canvas = el('div', { id: 'bowire-flow-canvas-' + flow.id, className: 'bowire-flow-canvas' });

        // ---- Header ----
        var header = el('div', { className: 'bowire-flow-canvas-header' });
        header.appendChild(el('input', {
            id: 'bowire-flow-name-input',
            type: 'text',
            className: 'bowire-flow-canvas-name',
            value: flow.name,
            spellcheck: 'false',
            onChange: function (e) { flow.name = e.target.value; persistFlows(); render(); }
        }));

        var isRunning = flowRunStatus === 'running' && flowRunFlowId === flow.id;

        // Run
        header.appendChild(el('button', {
            id: 'bowire-flow-run-btn',
            className: 'bowire-flow-canvas-run-btn' + (isRunning ? ' running' : ''),
            disabled: isRunning || countNodes(flow.nodes) === 0,
            title: 'Run flow',
            onClick: function () { runFlow(flow.id); }
        },
            el('span', { innerHTML: svgIcon('play') }),
            el('span', { textContent: isRunning ? 'Running\u2026' : 'Run' })
        ));

        // Export
        header.appendChild(el('button', {
            id: 'bowire-flow-export-btn',
            className: 'bowire-flow-canvas-action-btn',
            title: 'Export as .bwf',
            'aria-label': 'Export as .bwf',
            onClick: function () { exportFlow(flow.id); }
        }, el('span', { innerHTML: svgIcon('download') })));

        // Duplicate flow
        header.appendChild(el('button', {
            id: 'bowire-flow-dup-btn',
            className: 'bowire-flow-canvas-action-btn',
            title: 'Duplicate flow',
            'aria-label': 'Duplicate flow',
            onClick: function () {
                var dup = duplicateFlow(flow.id);
                if (dup) { flowEditorSelectedId = dup.id; render(); }
            }
        }, el('span', { innerHTML: svgIcon('copy') })));

        // Import
        header.appendChild(el('button', {
            id: 'bowire-flow-import-btn',
            className: 'bowire-flow-canvas-action-btn',
            title: 'Import .bwf flow',
            'aria-label': 'Import .bwf flow',
            onClick: importFlow
        }, el('span', { innerHTML: svgIcon('upload') })));

        // Delete
        header.appendChild(el('button', {
            id: 'bowire-flow-delete-btn',
            className: 'bowire-flow-canvas-delete-btn',
            title: 'Delete flow',
            'aria-label': 'Delete flow',
            onClick: function () {
                bowireConfirm('Delete flow "' + flow.name + '"?', function () {
                    var backup = JSON.parse(JSON.stringify(flow));
                    deleteFlow(flow.id);
                    render();
                    toast('Flow deleted', 'success', { undo: function () { flowsList.push(backup); persistFlows(); flowEditorSelectedId = backup.id; render(); } });
                }, { title: 'Delete Flow', danger: true, confirmText: 'Delete' });
            }
        }, el('span', { innerHTML: svgIcon('trash') })));

        canvas.appendChild(header);

        // ---- Node Pipeline ----
        var pipeline = el('div', { className: 'bowire-flow-pipeline' });

        pipeline.appendChild(el('div', { className: 'bowire-flow-card bowire-flow-card-start' },
            el('span', { className: 'bowire-flow-card-badge start', textContent: 'START' })
        ));

        renderNodeList(pipeline, flow.nodes, flow, 0);

        // Final add-zone
        pipeline.appendChild(el('div', { className: 'bowire-flow-connector-line' }, createSvgConnector()));
        pipeline.appendChild(createAddZone(flow, flow.nodes));

        pipeline.appendChild(el('div', { className: 'bowire-flow-connector-line' }, createSvgConnector()));
        pipeline.appendChild(el('div', { className: 'bowire-flow-card bowire-flow-card-end' },
            el('span', { className: 'bowire-flow-card-badge end', textContent: 'END' })
        ));

        canvas.appendChild(pipeline);

        // Variable-Watch panel pinned to the canvas footer. Shows the
        // live flowVars bindings (Variable + Foreach + extracted-from-
        // response) so the user can see what ${name} resolves to during
        // / after a run. Empty bindings show a hint instead.
        canvas.appendChild(renderFlowWatchPanel());

        return canvas;
    }

    /**
     * Watch-panel — one row per live flow-var binding. Updated by render()
     * after each runSingleNode tick, so iterations of a Foreach loop
     * visibly cycle the bound ${item}.
     */
    function renderFlowWatchPanel() {
        var panel = el('div', { id: 'bowire-flow-watch', className: 'bowire-flow-watch' });
        panel.appendChild(el('div', { className: 'bowire-flow-watch-header' },
            el('span', { className: 'bowire-flow-watch-title', textContent: 'Variables' }),
            el('span', { className: 'bowire-flow-watch-state',
                textContent: flowRunStatus === 'running'
                    ? 'running…'
                    : flowRunStatus === 'done'
                        ? 'last run complete'
                        : 'idle' })
        ));

        var entries = Object.keys(flowVars).sort();
        if (entries.length === 0) {
            panel.appendChild(el('div', { className: 'bowire-flow-watch-empty',
                textContent: flowRunStatus === null
                    ? 'No variables yet — Variable / Foreach nodes show their live bindings here during the run.'
                    : 'No flow-var bindings produced by this run.'
            }));
            return panel;
        }

        var list = el('div', { className: 'bowire-flow-watch-list' });
        for (var i = 0; i < entries.length; i++) {
            var name = entries[i];
            list.appendChild(el('div', { className: 'bowire-flow-watch-row' },
                el('span', { className: 'bowire-flow-watch-name', textContent: '${' + name + '}' }),
                el('span', { className: 'bowire-flow-watch-value', textContent: truncateForDisplay(flowVars[name]),
                    title: String(flowVars[name]) })
            ));
        }
        panel.appendChild(list);
        return panel;
    }

    /**
     * Suggestion-set for the ${var}-autocomplete dropdown. Combines:
     *   - per-run flow-vars (current run snapshot)
     *   - environment vars (merged global + active env)
     *   - system vars (now, uuid, …)
     *   - response.foo paths (top-level keys from lastResponseJson)
     * Returned as { name, hint } so the dropdown can show a contextual
     * second column without re-fetching each one.
     */
    function flowVariableSuggestions() {
        var seen = new Set();
        var out = [];
        function add(name, hint) {
            if (seen.has(name)) return;
            seen.add(name);
            out.push({ name: name, hint: hint });
        }
        Object.keys(flowVars || {}).forEach(function (k) { add(k, 'flow-var · ' + truncateForDisplay(flowVars[k])); });
        try {
            var merged = (typeof getMergedVars === 'function') ? getMergedVars() : {};
            Object.keys(merged).forEach(function (k) { add(k, 'env · ' + truncateForDisplay(merged[k])); });
        } catch { /* env machinery not ready */ }
        // System vars from history-env.js — hard-coded names since the
        // resolver doesn't expose an enumeration.
        ['now', 'nowMs', 'timestamp', 'uuid', 'random'].forEach(function (k) { add(k, 'system'); });
        // Response chaining
        add('response', 'last response (whole body)');
        if (lastResponseJson && typeof lastResponseJson === 'object') {
            var keys = Array.isArray(lastResponseJson)
                ? lastResponseJson.slice(0, 5).map(function (_, i) { return String(i); })
                : Object.keys(lastResponseJson).slice(0, 8);
            keys.forEach(function (k) { add('response.' + k, 'last response field'); });
        }
        return out;
    }

    var NODE_COLORS = {
        request: 'blue',
        condition: 'orange',
        delay: 'gray',
        variable: 'purple',
        loop: 'teal'
    };

    function renderNodeList(container, nodes, flow, depth) {
        for (var ni = 0; ni < nodes.length; ni++) {
            (function (node, idx, arr) {
                var runResult = flowRunResults[node.id] || null;
                var isActive = flowRunActiveNodeId === node.id;
                var colorClass = NODE_COLORS[node.type] || 'gray';
                var isExpanded = flowExpandedNodeId === node.id;
                var isResultOpen = flowResultExpandedNodeId === node.id;

                // Connector
                container.appendChild(el('div', { className: 'bowire-flow-connector-line' }, createSvgConnector()));

                // Node card
                var card = el('div', {
                    id: 'bowire-flow-node-' + node.id,
                    className: 'bowire-flow-card bowire-flow-card-' + colorClass
                        + (isActive ? ' executing' : '')
                        + (isExpanded ? ' expanded' : '')
                        + (runResult ? (runResult.pass ? ' pass' : ' fail') : '')
                        + (depth > 0 ? ' nested' : ''),
                    draggable: depth === 0 ? 'true' : 'false',
                    onClick: function (e) {
                        if (e.target.closest('.bowire-flow-card-actions') ||
                            e.target.closest('.bowire-flow-card-editor') ||
                            e.target.closest('.bowire-flow-result-viewer')) return;
                        flowExpandedNodeId = isExpanded ? null : node.id;
                        render();
                    }
                });

                // Drag events (top-level only)
                if (depth === 0) {
                    card.addEventListener('dragstart', function (e) {
                        flowDragState = { flowId: flow.id, nodeId: node.id, fromIndex: idx };
                        e.dataTransfer.effectAllowed = 'move';
                        e.dataTransfer.setData('text/plain', node.id);
                        card.classList.add('dragging');
                    });
                    card.addEventListener('dragend', function () {
                        flowDragState = null;
                        card.classList.remove('dragging');
                        var indicators = document.querySelectorAll('.bowire-flow-drop-above,.bowire-flow-drop-below');
                        for (var di = 0; di < indicators.length; di++) indicators[di].classList.remove('bowire-flow-drop-above', 'bowire-flow-drop-below');
                    });
                    card.addEventListener('dragover', function (e) {
                        if (!flowDragState || flowDragState.flowId !== flow.id) return;
                        e.preventDefault();
                        e.dataTransfer.dropEffect = 'move';
                        var rect = card.getBoundingClientRect();
                        var midY = rect.top + rect.height / 2;
                        card.classList.toggle('bowire-flow-drop-above', e.clientY < midY);
                        card.classList.toggle('bowire-flow-drop-below', e.clientY >= midY);
                    });
                    card.addEventListener('dragleave', function () {
                        card.classList.remove('bowire-flow-drop-above', 'bowire-flow-drop-below');
                    });
                    card.addEventListener('drop', function (e) {
                        e.preventDefault();
                        card.classList.remove('bowire-flow-drop-above', 'bowire-flow-drop-below');
                        if (!flowDragState || flowDragState.flowId !== flow.id) return;
                        var rect = card.getBoundingClientRect();
                        var toIdx = e.clientY < rect.top + rect.height / 2 ? idx : idx + 1;
                        if (flowDragState.fromIndex < toIdx) toIdx--;
                        if (flowDragState.fromIndex !== toIdx) {
                            moveNodeInFlow(flow.id, flowDragState.fromIndex, toIdx);
                        }
                        flowDragState = null;
                        render();
                    });
                }

                // Drag handle
                if (depth === 0) {
                    card.appendChild(el('div', {
                        className: 'bowire-flow-card-drag',
                        title: 'Drag to reorder',
                        innerHTML: svgIcon('grip')
                    }));
                }

                // Badge
                var badgeText = node.type === 'request' ? 'REQ'
                    : node.type === 'condition' ? 'IF'
                    : node.type === 'delay' ? 'WAIT'
                    : node.type === 'variable' ? 'VAR'
                    : node.type === 'loop' ? 'LOOP' : '?';
                card.appendChild(el('div', { className: 'bowire-flow-card-badge ' + colorClass, textContent: badgeText }));

                // Content summary
                var content = el('div', { className: 'bowire-flow-card-content' });
                if (node.type === 'request') {
                    content.appendChild(el('div', { className: 'bowire-flow-card-title', textContent: (node.method || 'Untitled') }));
                    var subtitle = (node.service || '') + (node.protocol ? ' \u00B7 ' + node.protocol : '');
                    if (Array.isArray(node.assertions) && node.assertions.length > 0) {
                        subtitle += ' \u00B7 ' + node.assertions.length + ' assert' + (node.assertions.length === 1 ? '' : 's');
                    }
                    content.appendChild(el('div', { className: 'bowire-flow-card-subtitle', textContent: subtitle }));
                } else if (node.type === 'delay') {
                    content.appendChild(el('div', { className: 'bowire-flow-card-title', textContent: 'Delay ' + (node.delayMs || 1000) + 'ms' }));
                } else if (node.type === 'condition') {
                    content.appendChild(el('div', { className: 'bowire-flow-card-title', textContent: 'Condition' }));
                    content.appendChild(el('div', { className: 'bowire-flow-card-subtitle',
                        textContent: (node.conditionPath || '') + ' ' + opLabel(node.conditionOp) + ' ' + (node.conditionValue || '') }));
                } else if (node.type === 'variable') {
                    content.appendChild(el('div', { className: 'bowire-flow-card-title', textContent: 'Set Variable' }));
                    content.appendChild(el('div', { className: 'bowire-flow-card-subtitle',
                        textContent: (node.varName || '') + ' = ${response.' + (node.path || '') + '}' }));
                } else if (node.type === 'loop') {
                    var loopTypeForLabel = node.loopType || 'count';
                    var loopLabel = loopTypeForLabel === 'while'
                        ? 'While ' + (node.conditionPath || '') + ' ' + opLabel(node.conditionOp) + ' ' + (node.conditionValue || '')
                        : loopTypeForLabel === 'foreach'
                            ? 'Foreach over ' + (node.loopSource || '?') + ' as ${' + (node.loopItemVar || 'item') + '}'
                            : 'Repeat ' + (node.loopCount || 1) + '\u00D7';
                    content.appendChild(el('div', { className: 'bowire-flow-card-title', textContent: 'Loop' }));
                    content.appendChild(el('div', { className: 'bowire-flow-card-subtitle', textContent: loopLabel }));
                }
                card.appendChild(content);

                // Run result indicator + response viewer toggle
                if (runResult) {
                    card.appendChild(el('span', {
                        className: 'bowire-flow-card-status ' + (runResult.pass ? 'pass' : 'fail'),
                        textContent: runResult.pass ? '\u2713' : '\u2717'
                    }));
                    if (runResult.durationMs) {
                        card.appendChild(el('span', { className: 'bowire-flow-card-timing', textContent: runResult.durationMs + 'ms' }));
                    }
                    // Response viewer toggle button
                    if (runResult.response || runResult.error) {
                        card.appendChild(el('button', {
                            className: 'bowire-flow-card-action-btn' + (isResultOpen ? ' active' : ''),
                            title: isResultOpen ? 'Hide response' : 'Show response',
                            'aria-label': isResultOpen ? 'Hide response' : 'Show response',
                            innerHTML: svgIcon('info'),
                            onClick: function (e) {
                                e.stopPropagation();
                                flowResultExpandedNodeId = isResultOpen ? null : node.id;
                                render();
                            }
                        }));
                    }
                }

                // Actions toolbar
                var actions = el('div', { className: 'bowire-flow-card-actions' });
                if (depth === 0 && idx > 0) {
                    actions.appendChild(el('button', {
                        className: 'bowire-flow-card-action-btn', title: 'Move up',
                        'aria-label': 'Move up',
                        innerHTML: svgIcon('chevronUp'),
                        onClick: function (e) { e.stopPropagation(); moveNodeInArray(arr, idx, idx - 1); persistFlows(); render(); }
                    }));
                }
                if (depth === 0 && idx < arr.length - 1) {
                    actions.appendChild(el('button', {
                        className: 'bowire-flow-card-action-btn', title: 'Move down',
                        'aria-label': 'Move down',
                        innerHTML: svgIcon('chevronDown'),
                        onClick: function (e) { e.stopPropagation(); moveNodeInArray(arr, idx, idx + 1); persistFlows(); render(); }
                    }));
                }
                actions.appendChild(el('button', {
                    className: 'bowire-flow-card-action-btn', title: 'Duplicate',
                    'aria-label': 'Duplicate',
                    innerHTML: svgIcon('copy'),
                    onClick: function (e) { e.stopPropagation(); duplicateNode(flow.id, node.id); render(); }
                }));
                actions.appendChild(el('button', {
                    className: 'bowire-flow-card-action-btn danger', title: 'Remove',
                    'aria-label': 'Remove',
                    innerHTML: svgIcon('trash'),
                    onClick: function (e) { e.stopPropagation(); removeNodeFromFlow(flow.id, node.id); render(); }
                }));
                card.appendChild(actions);

                // ---- Expanded Inline Editor ----
                if (isExpanded) {
                    var editor = el('div', { className: 'bowire-flow-card-editor' });
                    if (node.type === 'request') {
                        editor.appendChild(flowField('Protocol', 'select', node.protocol || '', function (v) { node.protocol = v; node.method = ''; persistFlows(); render(); },
                            protocols.map(function (p) { return { value: p.id, label: p.name }; })));
                        // Schema-aware picker: when the workbench has
                        // already discovered services, present
                        // Service + Method as dropdowns scoped to the
                        // chosen protocol. Operators can drop back to
                        // free-text via the "Custom" toggle for ad-hoc
                        // services that aren't part of the discovery
                        // tree yet (recordings from a different server,
                        // schema-less HTTP endpoints, ...).
                        editor.appendChild(renderServiceMethodPicker(node));
                        editor.appendChild(flowField('Server URL', 'text', node.serverUrl || '', function (v) { node.serverUrl = v; persistFlows(); }));
                        editor.appendChild(flowField('Body', 'textarea', node.body || '{}', function (v) { node.body = v; persistFlows(); }));
                        // Inline assertions: turn the request-node into
                        // a fully-validated probe without an extra chain
                        // of downstream Condition-nodes.
                        editor.appendChild(renderAssertionsEditor(node));
                    } else if (node.type === 'delay') {
                        editor.appendChild(flowField('Delay (ms)', 'number', String(node.delayMs || 1000), function (v) { node.delayMs = parseInt(v, 10) || 1000; persistFlows(); }));
                    } else if (node.type === 'condition') {
                        editor.appendChild(flowField('Path', 'text', node.conditionPath || '', function (v) { node.conditionPath = v; persistFlows(); }));
                        editor.appendChild(flowField('Operator', 'select', node.conditionOp || 'eq', function (v) { node.conditionOp = v; persistFlows(); },
                            [{ value: 'eq', label: '==' }, { value: 'neq', label: '!=' }, { value: 'gt', label: '>' }, { value: 'lt', label: '<' },
                             { value: 'contains', label: 'contains' }, { value: 'exists', label: 'exists' }]));
                        editor.appendChild(flowField('Value', 'text', node.conditionValue || '', function (v) { node.conditionValue = v; persistFlows(); }));
                    } else if (node.type === 'variable') {
                        editor.appendChild(flowField('Variable Name', 'text', node.varName || '', function (v) { node.varName = v; persistFlows(); }));
                        editor.appendChild(flowField('Response Path', 'text', node.path || '', function (v) { node.path = v; persistFlows(); }));
                    } else if (node.type === 'loop') {
                        var loopTypeNow = node.loopType || 'count';
                        editor.appendChild(flowField('Loop Type', 'select', loopTypeNow, function (v) { node.loopType = v; persistFlows(); render(); },
                            [
                                { value: 'count', label: 'Count (N times)' },
                                { value: 'while', label: 'While (condition)' },
                                { value: 'foreach', label: 'Foreach (over array)' },
                            ]));
                        if (loopTypeNow === 'count') {
                            editor.appendChild(flowField('Iterations', 'number', String(node.loopCount || 1), function (v) { node.loopCount = parseInt(v, 10) || 1; persistFlows(); }));
                        } else if (loopTypeNow === 'foreach') {
                            editor.appendChild(flowField('Source ($.users / $)', 'text', node.loopSource || '', function (v) { node.loopSource = v; persistFlows(); }));
                            editor.appendChild(flowField('Item Variable', 'text', node.loopItemVar || 'item', function (v) { node.loopItemVar = v; persistFlows(); }));
                        } else {
                            editor.appendChild(flowField('Path', 'text', node.conditionPath || '', function (v) { node.conditionPath = v; persistFlows(); }));
                            editor.appendChild(flowField('Operator', 'select', node.conditionOp || 'eq', function (v) { node.conditionOp = v; persistFlows(); },
                                [{ value: 'eq', label: '==' }, { value: 'neq', label: '!=' }, { value: 'gt', label: '>' }, { value: 'lt', label: '<' },
                                 { value: 'contains', label: 'contains' }, { value: 'exists', label: 'exists' }]));
                            editor.appendChild(flowField('Value', 'text', node.conditionValue || '', function (v) { node.conditionValue = v; persistFlows(); }));
                        }
                    }
                    card.appendChild(editor);
                }

                // ---- Response Viewer ----
                if (isResultOpen && runResult) {
                    var viewer = el('div', { className: 'bowire-flow-result-viewer' });
                    viewer.appendChild(el('div', { className: 'bowire-flow-result-header' },
                        el('span', { className: 'bowire-flow-result-status ' + (runResult.pass ? 'pass' : 'fail'),
                            textContent: runResult.status }),
                        runResult.durationMs ? el('span', { className: 'bowire-flow-result-time', textContent: runResult.durationMs + 'ms' }) : null
                    ));
                    // Assertion outcomes — one row per evaluated tuple.
                    if (Array.isArray(runResult.assertions) && runResult.assertions.length > 0) {
                        var assertList = el('div', { className: 'bowire-flow-assertion-results' });
                        for (var ari = 0; ari < runResult.assertions.length; ari++) {
                            var a = runResult.assertions[ari];
                            var opLbl = opLabel(a.op);
                            var line = a.op === 'exists' || a.op === 'missing'
                                ? a.path + ' ' + opLbl
                                : a.path + ' ' + opLbl + ' ' + a.expected;
                            var detail = a.pass
                                ? '✓ ' + line
                                : '✗ ' + line + ' — got: ' + truncateForDisplay(a.actual);
                            assertList.appendChild(el('div', {
                                className: 'bowire-flow-assertion-result ' + (a.pass ? 'pass' : 'fail'),
                                textContent: detail,
                            }));
                        }
                        viewer.appendChild(assertList);
                    }
                    if (runResult.error) {
                        viewer.appendChild(el('pre', { className: 'bowire-flow-result-body error', textContent: runResult.error }));
                    }
                    if (runResult.response) {
                        var json;
                        try { json = typeof runResult.response === 'string' ? runResult.response : JSON.stringify(runResult.response, null, 2); } catch { json = String(runResult.response); }
                        viewer.appendChild(el('pre', { className: 'bowire-flow-result-body', textContent: json }));
                    }
                    card.appendChild(viewer);
                }

                container.appendChild(card);

                // ---- Render branches for condition nodes ----
                if (node.type === 'condition') {
                    var branchWrapper = el('div', { className: 'bowire-flow-branches' });

                    // True branch
                    var trueBranchEl = el('div', { className: 'bowire-flow-branch bowire-flow-branch-true' });
                    trueBranchEl.appendChild(el('div', { className: 'bowire-flow-branch-label true' },
                        el('span', { textContent: '\u2713 True' })
                    ));
                    if (node.trueBranch && node.trueBranch.length > 0) {
                        renderNodeList(trueBranchEl, node.trueBranch, flow, depth + 1);
                    }
                    trueBranchEl.appendChild(el('div', { className: 'bowire-flow-branch-add' }));
                    trueBranchEl.lastChild.appendChild(createAddZone(flow, node.trueBranch || [], true));
                    branchWrapper.appendChild(trueBranchEl);

                    // False branch
                    var falseBranchEl = el('div', { className: 'bowire-flow-branch bowire-flow-branch-false' });
                    falseBranchEl.appendChild(el('div', { className: 'bowire-flow-branch-label false' },
                        el('span', { textContent: '\u2717 False' })
                    ));
                    if (node.falseBranch && node.falseBranch.length > 0) {
                        renderNodeList(falseBranchEl, node.falseBranch, flow, depth + 1);
                    }
                    falseBranchEl.appendChild(el('div', { className: 'bowire-flow-branch-add' }));
                    falseBranchEl.lastChild.appendChild(createAddZone(flow, node.falseBranch || [], true));
                    branchWrapper.appendChild(falseBranchEl);

                    container.appendChild(branchWrapper);

                    // Merge indicator
                    container.appendChild(el('div', { className: 'bowire-flow-connector-line' }, createSvgConnector()));
                    container.appendChild(el('div', { className: 'bowire-flow-merge-indicator' },
                        el('span', { textContent: '\u2014 merge \u2014' })
                    ));
                }

                // ---- Render body for loop nodes ----
                if (node.type === 'loop') {
                    var loopBody = el('div', { className: 'bowire-flow-loop-body' });
                    loopBody.appendChild(el('div', { className: 'bowire-flow-branch-label loop' },
                        el('span', { textContent: '\u21BA Loop Body' })
                    ));
                    if (node.body && node.body.length > 0) {
                        renderNodeList(loopBody, node.body, flow, depth + 1);
                    }
                    loopBody.appendChild(el('div', { className: 'bowire-flow-branch-add' }));
                    loopBody.lastChild.appendChild(createAddZone(flow, node.body || [], true));
                    container.appendChild(loopBody);

                    container.appendChild(el('div', { className: 'bowire-flow-connector-line' }, createSvgConnector()));
                    container.appendChild(el('div', { className: 'bowire-flow-merge-indicator' },
                        el('span', { textContent: '\u2014 end loop \u2014' })
                    ));
                }

            })(nodes[ni], ni, nodes);
        }
    }

    function opLabel(op) {
        var labels = { eq: '==', neq: '!=', gt: '>', lt: '<', contains: '\u2283', exists: '\u2203', missing: '\u2204' };
        return labels[op] || op || '==';
    }

    function createAddZone(flow, targetArray, compact) {
        var zone = el('div', { className: 'bowire-flow-add-zone' + (compact ? ' compact' : '') });
        var types = [
            { type: 'request', label: '+ Request', color: 'blue' },
            { type: 'delay', label: '+ Delay', color: 'gray' },
            { type: 'condition', label: '+ Condition', color: 'orange' },
            { type: 'variable', label: '+ Variable', color: 'purple' },
            { type: 'loop', label: '+ Loop', color: 'teal' }
        ];
        for (var ti = 0; ti < types.length; ti++) {
            (function (t) {
                zone.appendChild(el('button', {
                    className: 'bowire-flow-add-btn bowire-flow-add-btn-' + t.color,
                    onClick: function () {
                        var defaults = {
                            request: { type: 'request', protocol: protocols.length > 0 ? protocols[0].id : 'grpc', service: '', method: '', body: '{}', assertions: [] },
                            delay: { type: 'delay', delayMs: 1000 },
                            condition: { type: 'condition', conditionPath: 'status', conditionOp: 'eq', conditionValue: 'OK', trueBranch: [], falseBranch: [] },
                            variable: { type: 'variable', varName: 'myVar', path: '' },
                            loop: { type: 'loop', loopType: 'count', loopCount: 3, body: [] }
                        };
                        addNodeToFlow(flow.id, defaults[t.type], targetArray);
                        render();
                    }
                }, el('span', { textContent: t.label })));
            })(types[ti]);
        }
        return zone;
    }

    // Helper: creates a labeled form field for the inline node editor.
    function flowField(label, type, value, onChange, options) {
        var row = el('div', { className: 'bowire-flow-field' });
        row.appendChild(el('label', { className: 'bowire-flow-field-label', textContent: label }));
        if (type === 'select' && options) {
            var sel = el('select', {
                className: 'bowire-flow-field-input',
                onChange: function (e) { onChange(e.target.value); }
            });
            for (var oi = 0; oi < options.length; oi++) {
                var opt = el('option', { value: options[oi].value, textContent: options[oi].label });
                if (options[oi].value === value) opt.selected = true;
                sel.appendChild(opt);
            }
            row.appendChild(sel);
        } else if (type === 'textarea') {
            var ta = el('textarea', {
                className: 'bowire-flow-field-input bowire-flow-field-textarea',
                value: value,
                spellcheck: 'false',
                rows: '4',
                onInput: function (e) { onChange(e.target.value); }
            });
            attachFlowVarAutocomplete(ta);
            row.appendChild(ta);
        } else {
            var input = el('input', {
                type: type || 'text',
                className: 'bowire-flow-field-input',
                value: value,
                spellcheck: 'false',
                onInput: function (e) { onChange(e.target.value); }
            });
            // Numeric inputs (delay, count) don't need autocomplete.
            if (type !== 'number') attachFlowVarAutocomplete(input);
            row.appendChild(input);
        }
        return row;
    }

    /**
     * Attach a ${var}-autocomplete dropdown to an input / textarea.
     * The dropdown appears as the user types `${`, filtered by what's
     * after the cursor's open-brace, and inserts the chosen variable
     * including the closing brace. Lightweight — no virtualisation,
     * the suggestion set rarely exceeds 10 entries.
     */
    function attachFlowVarAutocomplete(field) {
        var dropdown = null;
        var activeIndex = 0;
        var lastSuggestions = [];

        function close() {
            if (dropdown && dropdown.parentNode) dropdown.parentNode.removeChild(dropdown);
            dropdown = null;
            activeIndex = 0;
        }

        function position() {
            if (!dropdown) return;
            var rect = field.getBoundingClientRect();
            dropdown.style.left = rect.left + 'px';
            dropdown.style.top = (rect.bottom + 2) + 'px';
            dropdown.style.minWidth = rect.width + 'px';
        }

        function detectOpenBracePrefix() {
            var val = field.value;
            var pos = field.selectionStart;
            if (typeof pos !== 'number') return null;
            // Walk back from cursor until we find `${` without a `}`
            // between cursor and the brace pair start.
            for (var i = pos - 1; i >= 0; i--) {
                var ch = val.charAt(i);
                if (ch === '}') return null;
                if (ch === '{' && i > 0 && val.charAt(i - 1) === '$') {
                    return { start: i - 1, query: val.substring(i + 1, pos) };
                }
            }
            return null;
        }

        function render() {
            var ctx = detectOpenBracePrefix();
            if (!ctx) { close(); return; }
            var suggestions = flowVariableSuggestions();
            var q = ctx.query.toLowerCase();
            var filtered = suggestions.filter(function (s) {
                return q === '' || s.name.toLowerCase().indexOf(q) !== -1;
            });
            if (filtered.length === 0) { close(); return; }
            lastSuggestions = filtered;
            if (!dropdown) {
                dropdown = el('div', { className: 'bowire-flow-var-autocomplete' });
                document.body.appendChild(dropdown);
            }
            dropdown.innerHTML = '';
            activeIndex = Math.min(activeIndex, filtered.length - 1);
            for (var i = 0; i < filtered.length; i++) {
                (function (entry, idx) {
                    var row = el('div', {
                        className: 'bowire-flow-var-autocomplete-row' + (idx === activeIndex ? ' active' : ''),
                        onMouseDown: function (e) { e.preventDefault(); choose(idx); },
                    },
                        el('span', { className: 'bowire-flow-var-autocomplete-name', textContent: '${' + entry.name + '}' }),
                        el('span', { className: 'bowire-flow-var-autocomplete-hint', textContent: entry.hint || '' })
                    );
                    dropdown.appendChild(row);
                })(filtered[i], i);
            }
            position();
        }

        function choose(idx) {
            var ctx = detectOpenBracePrefix();
            if (!ctx || !lastSuggestions[idx]) { close(); return; }
            var entry = lastSuggestions[idx];
            var val = field.value;
            var before = val.substring(0, ctx.start);
            var after = val.substring(field.selectionStart);
            // Trim a trailing `}` from `after` so we don't double-close.
            var insertion = '${' + entry.name + '}';
            field.value = before + insertion + after;
            var caret = before.length + insertion.length;
            field.setSelectionRange(caret, caret);
            field.dispatchEvent(new Event('input', { bubbles: true }));
            close();
        }

        field.addEventListener('keydown', function (e) {
            if (!dropdown) return;
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                activeIndex = (activeIndex + 1) % lastSuggestions.length;
                render();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                activeIndex = (activeIndex - 1 + lastSuggestions.length) % lastSuggestions.length;
                render();
            } else if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                choose(activeIndex);
            } else if (e.key === 'Escape') {
                close();
            }
        });
        field.addEventListener('input', render);
        field.addEventListener('blur', function () { setTimeout(close, 100); });
    }

    /**
     * Schema-aware Service / Method picker. When the workbench has
     * discovered services (`services` global state), the request-node
     * binds to a (service, method) pair via dropdowns scoped to the
     * chosen protocol. Falling back to free-text via the "Custom" toggle
     * keeps the editor usable for ad-hoc services that aren't in the
     * discovery tree (different server, schema-less HTTP, …).
     *
     * Selecting a method pre-fills:
     *   • node.method (display name)
     *   • node.service (display name)
     *   • node.body — empty `{}` for now; the schema-form-driven sample
     *     payload is a follow-up since BowireFieldInfo isn't shipped
     *     into the flow runner's HTTP path yet.
     */
    function renderServiceMethodPicker(node) {
        var wrap = el('div', { className: 'bowire-flow-service-picker' });
        var protoId = node.protocol || (protocols.length > 0 ? protocols[0].id : null);
        var protoServices = (services || []).filter(function (s) {
            return !protoId || s.source === protoId;
        });
        var useCustom = node.serviceMethodMode === 'custom' || protoServices.length === 0;

        // Toggle row — only meaningful when discovery DOES have results.
        if (protoServices.length > 0) {
            wrap.appendChild(el('div', { className: 'bowire-flow-service-picker-toggle' },
                el('label', { className: 'bowire-flow-field-label' }, el('span', { textContent: 'Service' })),
                el('button', {
                    type: 'button',
                    className: 'bowire-flow-service-picker-mode' + (useCustom ? '' : ' active'),
                    onClick: function (e) {
                        e.stopPropagation();
                        node.serviceMethodMode = 'discovered';
                        persistFlows();
                        render();
                    },
                    textContent: 'From discovery (' + protoServices.length + ')',
                }),
                el('button', {
                    type: 'button',
                    className: 'bowire-flow-service-picker-mode' + (useCustom ? ' active' : ''),
                    onClick: function (e) {
                        e.stopPropagation();
                        node.serviceMethodMode = 'custom';
                        persistFlows();
                        render();
                    },
                    textContent: 'Custom',
                })
            ));
        }

        if (useCustom) {
            wrap.appendChild(flowField('Service', 'text', node.service || '', function (v) { node.service = v; persistFlows(); }));
            wrap.appendChild(flowField('Method', 'text', node.method || '', function (v) { node.method = v; persistFlows(); }));
            return wrap;
        }

        // Discovered-mode: dropdowns.
        var serviceOptions = [{ value: '', label: '(pick a service)' }].concat(
            protoServices.map(function (s) { return { value: s.name, label: s.name }; })
        );
        wrap.appendChild(flowField('Service', 'select', node.service || '', function (v) {
            node.service = v;
            // Clear method when service changes — the previous selection
            // probably doesn't exist on the new service.
            node.method = '';
            persistFlows();
            render();
        }, serviceOptions));

        var chosenService = protoServices.find(function (s) { return s.name === node.service; });
        var methodOptions = chosenService && Array.isArray(chosenService.methods)
            ? [{ value: '', label: '(pick a method)' }].concat(
                chosenService.methods.map(function (m) {
                    var label = m.name + (m.methodType && m.methodType !== 'Unary' ? '  · ' + m.methodType : '');
                    return { value: m.name, label: label };
                }))
            : [{ value: '', label: '(select a service first)' }];
        wrap.appendChild(flowField('Method', 'select', node.method || '', function (v) {
            node.method = v;
            persistFlows();
        }, methodOptions));

        return wrap;
    }

    /**
     * Inline-assertion editor — rendered inside the expanded
     * Request-Node editor. Each row is a (path, op, value) tuple
     * exercising the same predicate vocabulary as Condition-nodes.
     * The Add-row button appends a fresh empty entry; per-row × removes.
     */
    function renderAssertionsEditor(node) {
        if (!Array.isArray(node.assertions)) node.assertions = [];
        var wrap = el('div', { className: 'bowire-flow-assertions' });

        var header = el('div', { className: 'bowire-flow-assertions-header' },
            el('span', { className: 'bowire-flow-assertions-title', textContent: 'Assertions' }),
            el('button', {
                className: 'bowire-flow-card-action-btn',
                title: 'Add assertion',
                'aria-label': 'Add assertion',
                innerHTML: svgIcon('plus'),
                onClick: function (e) {
                    e.stopPropagation();
                    node.assertions.push({ path: 'status', op: 'eq', value: 'OK' });
                    persistFlows();
                    render();
                },
            })
        );
        wrap.appendChild(header);

        if (node.assertions.length === 0) {
            wrap.appendChild(el('div', { className: 'bowire-flow-assertions-empty',
                textContent: 'No assertions — request passes whenever the call returns OK.' }));
            return wrap;
        }

        for (var ai = 0; ai < node.assertions.length; ai++) {
            (function (assertion, idx) {
                var row = el('div', { className: 'bowire-flow-assertion-row' });
                row.appendChild(el('input', {
                    type: 'text', className: 'bowire-flow-field-input bowire-flow-assertion-path',
                    placeholder: '$.id',
                    value: assertion.path || '',
                    spellcheck: 'false',
                    onInput: function (e) { node.assertions[idx].path = e.target.value; persistFlows(); },
                }));
                var opSel = el('select', {
                    className: 'bowire-flow-field-input bowire-flow-assertion-op',
                    onChange: function (e) { node.assertions[idx].op = e.target.value; persistFlows(); render(); },
                });
                var opEntries = [
                    { value: 'eq', label: '==' },
                    { value: 'neq', label: '!=' },
                    { value: 'gt', label: '>' },
                    { value: 'lt', label: '<' },
                    { value: 'contains', label: 'contains' },
                    { value: 'exists', label: 'exists' },
                    { value: 'missing', label: 'missing' },
                ];
                for (var oi = 0; oi < opEntries.length; oi++) {
                    var opt = el('option', { value: opEntries[oi].value, textContent: opEntries[oi].label });
                    if (opEntries[oi].value === (assertion.op || 'eq')) opt.selected = true;
                    opSel.appendChild(opt);
                }
                row.appendChild(opSel);
                // exists/missing operators don't need a value field.
                if (assertion.op !== 'exists' && assertion.op !== 'missing') {
                    row.appendChild(el('input', {
                        type: 'text', className: 'bowire-flow-field-input bowire-flow-assertion-value',
                        placeholder: 'expected',
                        value: assertion.value == null ? '' : String(assertion.value),
                        spellcheck: 'false',
                        onInput: function (e) { node.assertions[idx].value = e.target.value; persistFlows(); },
                    }));
                }
                row.appendChild(el('button', {
                    className: 'bowire-flow-card-action-btn danger',
                    title: 'Remove assertion',
                    'aria-label': 'Remove assertion',
                    innerHTML: svgIcon('trash'),
                    onClick: function (e) {
                        e.stopPropagation();
                        node.assertions.splice(idx, 1);
                        persistFlows();
                        render();
                    },
                }));
                wrap.appendChild(row);
            })(node.assertions[ai], ai);
        }
        return wrap;
    }

    function createSvgConnector() {
        var ns = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(ns, 'svg');
        svg.setAttribute('width', '2');
        svg.setAttribute('height', '24');
        svg.setAttribute('viewBox', '0 0 2 24');
        svg.setAttribute('class', 'bowire-flow-svg-connector');
        var line = document.createElementNS(ns, 'line');
        line.setAttribute('x1', '1'); line.setAttribute('y1', '0');
        line.setAttribute('x2', '1'); line.setAttribute('y2', '24');
        line.setAttribute('stroke', 'var(--bowire-border)');
        line.setAttribute('stroke-width', '2');
        line.setAttribute('stroke-dasharray', '4 3');
        svg.appendChild(line);
        return svg;
    }
