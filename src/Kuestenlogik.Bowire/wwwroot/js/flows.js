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
    // Results keyed by node.id: { [nodeId]: { pass, status, durationMs, response, error, iteration? } }
    let flowRunResults = {};
    let flowRunActiveNodeId = null;
    let flowRunStatus = null; // null | 'running' | 'done' | 'stopped'
    let flowRunFlowId = null;
    let flowExpandedNodeId = null;
    let flowResultExpandedNodeId = null; // which node's response viewer is open
    let flowDragState = null;

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
                if (val !== null) {
                    flowRunResults[node.id] = { pass: true, status: 'Set ' + node.varName + ' = ' + val };
                } else {
                    flowRunResults[node.id] = { pass: false, status: 'Path not found: ' + node.path };
                }

            } else if (node.type === 'loop') {
                var maxIter = node.loopType === 'count' ? (node.loopCount || 1) : 100;
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
                    if (node.loopType === 'while') {
                        var loopCond = evaluateCondition(node);
                        if (!loopCond) break;
                    }
                }
                flowRunResults[node.id] = { pass: true, status: iterations + ' iteration' + (iterations !== 1 ? 's' : '') + ' completed', iteration: iterations };
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
                return {
                    pass: resp.ok && !json.error,
                    status: json.status || (resp.ok ? 'OK' : 'Error'),
                    durationMs: json.duration_ms || 0,
                    response: json.response,
                    error: json.error || null
                };
            });
        });
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
        return canvas;
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
                    content.appendChild(el('div', { className: 'bowire-flow-card-subtitle', textContent: (node.service || '') + (node.protocol ? ' \u00B7 ' + node.protocol : '') }));
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
                    var loopLabel = node.loopType === 'while'
                        ? 'While ' + (node.conditionPath || '') + ' ' + opLabel(node.conditionOp) + ' ' + (node.conditionValue || '')
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
                        editor.appendChild(flowField('Protocol', 'select', node.protocol || '', function (v) { node.protocol = v; persistFlows(); },
                            protocols.map(function (p) { return { value: p.id, label: p.name }; })));
                        editor.appendChild(flowField('Service', 'text', node.service || '', function (v) { node.service = v; persistFlows(); }));
                        editor.appendChild(flowField('Method', 'text', node.method || '', function (v) { node.method = v; persistFlows(); }));
                        editor.appendChild(flowField('Server URL', 'text', node.serverUrl || '', function (v) { node.serverUrl = v; persistFlows(); }));
                        editor.appendChild(flowField('Body', 'textarea', node.body || '{}', function (v) { node.body = v; persistFlows(); }));
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
                        editor.appendChild(flowField('Loop Type', 'select', node.loopType || 'count', function (v) { node.loopType = v; persistFlows(); render(); },
                            [{ value: 'count', label: 'Count (N times)' }, { value: 'while', label: 'While (condition)' }]));
                        if ((node.loopType || 'count') === 'count') {
                            editor.appendChild(flowField('Iterations', 'number', String(node.loopCount || 1), function (v) { node.loopCount = parseInt(v, 10) || 1; persistFlows(); }));
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
        var labels = { eq: '==', neq: '!=', gt: '>', lt: '<', contains: '\u2283', exists: '\u2203' };
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
                            request: { type: 'request', protocol: protocols.length > 0 ? protocols[0].id : 'grpc', service: '', method: '', body: '{}' },
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
            row.appendChild(el('textarea', {
                className: 'bowire-flow-field-input bowire-flow-field-textarea',
                value: value,
                spellcheck: 'false',
                rows: '4',
                onInput: function (e) { onChange(e.target.value); }
            }));
        } else {
            row.appendChild(el('input', {
                type: type || 'text',
                className: 'bowire-flow-field-input',
                value: value,
                spellcheck: 'false',
                onInput: function (e) { onChange(e.target.value); }
            }));
        }
        return row;
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
