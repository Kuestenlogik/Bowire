    // ---- Schema Designer (#247) ----
    //
    // A graph view of a discovered schema's type relationships, for the
    // case a flat tree cannot serve: a .proto with fifty messages where
    // the question is "who actually uses this type".
    //
    // The graph is NOT derived from the discovered BowireMessageInfo
    // tree. That tree inlines each referenced message at its point of
    // use and de-duplicates with a visited-set spanning the whole tree,
    // so a type's SECOND appearance arrives as an empty stub — which is
    // exactly the cross-reference this rail exists to draw. Instead the
    // descriptor sets discovery already captured
    // (service.schemaDescriptor) are posted to /api/schema/graph, which
    // reads them flat and answers with nodes + edges.
    //
    // Rendering is hand-built SVG, deliberately. Rail fragments are
    // stitched into the core IIFE bundle for EVERY workbench load (see
    // BowireHtmlGenerator.CollectRailJsPayload), so a vendored graph
    // library would be paid for by operators who never switch this
    // default-off rail on. The only way to avoid that is the Map
    // package's dynamic-asset route, which needs its own core exclusion
    // — new plumbing #247 does not ask for.

    const SCHEMA_COL_W = 210;
    const SCHEMA_ROW_H = 46;
    const SCHEMA_NODE_W = 168;
    const SCHEMA_NODE_H = 28;

    let schemaGraph = null;          // { nodes, edges, files } from the endpoint
    let schemaStatus = 'idle';       // idle | loading | ready | error
    let schemaError = null;
    let schemaSearch = '';
    let schemaFileFilter = '';       // '' = every file
    let schemaMinUsage = 0;
    let schemaIncludeWellKnown = false;
    let schemaSelectedId = null;
    // The SVG host is kept across renders and handed back by reference.
    // morphdom short-circuits on isSameNode, so returning the very same
    // element keeps a 50-node graph out of the diff entirely — and keeps
    // pan / zoom from being reset by an unrelated re-render.
    let schemaHost = null;
    let schemaViewport = null;       // { x, y, w, h } — null until fitted to the graph

    function schemaResetView() {
        schemaViewport = null;
        schemaDrawGraph();
    }

    // ---- data ----

    function schemaDescriptorsFromServices() {
        var out = [];
        if (!Array.isArray(services)) return out;
        for (var i = 0; i < services.length; i++) {
            var d = services[i] && services[i].schemaDescriptor;
            // Only gRPC carries one today; every other protocol reports
            // null and simply contributes nothing to the graph.
            if (typeof d === 'string' && d.length > 0 && out.indexOf(d) < 0) out.push(d);
        }
        return out;
    }

    async function schemaLoadGraph() {
        var descriptors = schemaDescriptorsFromServices();
        if (descriptors.length === 0) {
            schemaGraph = null;
            schemaStatus = 'ready';
            schemaError = null;
            render();
            return;
        }
        schemaStatus = 'loading';
        schemaError = null;
        render();
        try {
            var prefix = (window.__BOWIRE_CONFIG__ && window.__BOWIRE_CONFIG__.prefix) || '';
            var resp = await fetch(prefix + '/api/schema/graph', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
                body: JSON.stringify({
                    descriptors: descriptors,
                    includeWellKnown: schemaIncludeWellKnown
                })
            });
            if (!resp.ok) {
                schemaStatus = 'error';
                schemaError = t('schema.loadFailed', { status: String(resp.status) });
            } else {
                schemaGraph = await resp.json();
                schemaStatus = 'ready';
                schemaViewport = null;
            }
        } catch (e) {
            schemaStatus = 'error';
            schemaError = (e && e.message) ? e.message : String(e);
        }
        render();
    }

    // ---- filtering ----

    function schemaVisibleNodes() {
        if (!schemaGraph || !Array.isArray(schemaGraph.nodes)) return [];
        var q = schemaSearch.trim().toLowerCase();
        var out = [];
        for (var i = 0; i < schemaGraph.nodes.length; i++) {
            var n = schemaGraph.nodes[i];
            if (schemaFileFilter && n.file !== schemaFileFilter) continue;
            // A method is a way IN to the graph, not a type with users, so
            // the usage threshold would always hide every method.
            if (schemaMinUsage > 0 && n.kind !== 'method' && (n.usageCount || 0) < schemaMinUsage) continue;
            if (q && !schemaMatches(n, q)) continue;
            out.push(n);
        }
        return out;
    }

    /// Search spans the node's own name, its fully-qualified id and the
    /// names of the fields that point at it — #247 asks for "type name,
    /// field name, method name", and a field name only exists on an edge.
    function schemaMatches(node, q) {
        if (node.name.toLowerCase().indexOf(q) >= 0) return true;
        if (node.id.toLowerCase().indexOf(q) >= 0) return true;
        var edges = schemaGraph.edges || [];
        for (var i = 0; i < edges.length; i++) {
            if (edges[i].to !== node.id && edges[i].from !== node.id) continue;
            if (edges[i].label && edges[i].label.toLowerCase().indexOf(q) >= 0) return true;
        }
        return false;
    }

    function schemaVisibleEdges(nodeIds) {
        if (!schemaGraph || !Array.isArray(schemaGraph.edges)) return [];
        var out = [];
        for (var i = 0; i < schemaGraph.edges.length; i++) {
            var e = schemaGraph.edges[i];
            if (nodeIds[e.from] && nodeIds[e.to]) out.push(e);
        }
        return out;
    }

    // ---- layout ----
    //
    // A layered left-to-right placement: methods and unreferenced types
    // sit on the left, and every reference moves its target one column
    // right. Cycles (a type that reaches itself) cannot be layered, so
    // back-edges are ignored while layering and drawn afterwards.

    function schemaLayout(nodes, edges) {
        var layer = Object.create(null);
        var indeg = Object.create(null);
        var outFrom = Object.create(null);
        var i;

        for (i = 0; i < nodes.length; i++) {
            layer[nodes[i].id] = 0;
            indeg[nodes[i].id] = 0;
            outFrom[nodes[i].id] = [];
        }
        for (i = 0; i < edges.length; i++) {
            if (edges[i].from === edges[i].to) continue;   // self-reference
            outFrom[edges[i].from].push(edges[i].to);
            indeg[edges[i].to]++;
        }

        // Longest-path layering, relaxed until stable. The iteration cap
        // is what makes a cycle terminate: a cyclic component simply
        // stops deepening instead of looping for ever.
        var changed = true;
        var guard = 0;
        while (changed && guard < nodes.length + 2) {
            changed = false;
            guard++;
            for (i = 0; i < edges.length; i++) {
                var e = edges[i];
                if (e.from === e.to) continue;
                if (layer[e.to] < layer[e.from] + 1) {
                    layer[e.to] = layer[e.from] + 1;
                    changed = true;
                }
            }
        }

        // Group by column, then order each column by the average row of
        // whatever points at it — a cheap barycentre pass that removes
        // most of the crossings a naive order produces.
        var columns = [];
        for (i = 0; i < nodes.length; i++) {
            var L = layer[nodes[i].id];
            (columns[L] = columns[L] || []).push(nodes[i]);
        }

        var rowOf = Object.create(null);
        for (var c = 0; c < columns.length; c++) {
            var col = columns[c] || [];
            if (c > 0) {
                col.sort(function (a, b) {
                    var ba = schemaBarycentre(a.id, edges, rowOf);
                    var bb = schemaBarycentre(b.id, edges, rowOf);
                    if (ba !== bb) return ba - bb;
                    return a.name.localeCompare(b.name);
                });
            } else {
                col.sort(function (a, b) {
                    // Methods first in the entry column: they are where an
                    // operator starts reading.
                    if ((a.kind === 'method') !== (b.kind === 'method')) return a.kind === 'method' ? -1 : 1;
                    return a.name.localeCompare(b.name);
                });
            }
            for (var r = 0; r < col.length; r++) rowOf[col[r].id] = r;
        }

        var placed = [];
        for (c = 0; c < columns.length; c++) {
            var column = columns[c] || [];
            for (r = 0; r < column.length; r++) {
                placed.push({
                    node: column[r],
                    x: c * SCHEMA_COL_W,
                    y: r * SCHEMA_ROW_H
                });
            }
        }
        return placed;
    }

    function schemaBarycentre(id, edges, rowOf) {
        var sum = 0, n = 0;
        for (var i = 0; i < edges.length; i++) {
            if (edges[i].to !== id) continue;
            var r = rowOf[edges[i].from];
            if (typeof r === 'number') { sum += r; n++; }
        }
        return n === 0 ? Number.MAX_SAFE_INTEGER : sum / n;
    }

    // ---- drawing ----

    const SCHEMA_SVG_NS = 'http://www.w3.org/2000/svg';

    function schemaSvgEl(tag, attrs) {
        var e = document.createElementNS(SCHEMA_SVG_NS, tag);
        if (attrs) {
            for (var k in attrs) {
                if (!Object.prototype.hasOwnProperty.call(attrs, k)) continue;
                if (attrs[k] === undefined || attrs[k] === null) continue;
                e.setAttribute(k, String(attrs[k]));
            }
        }
        return e;
    }

    function schemaDrawGraph() {
        if (!schemaHost) return;
        while (schemaHost.firstChild) schemaHost.removeChild(schemaHost.firstChild);

        var nodes = schemaVisibleNodes();
        if (nodes.length === 0) return;

        var ids = Object.create(null);
        for (var i = 0; i < nodes.length; i++) ids[nodes[i].id] = true;
        var edges = schemaVisibleEdges(ids);
        var placed = schemaLayout(nodes, edges);

        var pos = Object.create(null);
        var maxX = 0, maxY = 0;
        for (i = 0; i < placed.length; i++) {
            pos[placed[i].node.id] = placed[i];
            maxX = Math.max(maxX, placed[i].x + SCHEMA_NODE_W);
            maxY = Math.max(maxY, placed[i].y + SCHEMA_NODE_H);
        }

        if (!schemaViewport) {
            schemaViewport = { x: -24, y: -24, w: maxX + 48, h: maxY + 48 };
        }

        var svg = schemaSvgEl('svg', {
            width: '100%',
            height: '100%',
            viewBox: schemaViewport.x + ' ' + schemaViewport.y + ' ' + schemaViewport.w + ' ' + schemaViewport.h,
            style: 'display:block;cursor:grab;touch-action:none'
        });

        var edgeLayer = schemaSvgEl('g', { 'data-layer': 'edges' });
        var nodeLayer = schemaSvgEl('g', { 'data-layer': 'nodes' });
        svg.appendChild(edgeLayer);
        svg.appendChild(nodeLayer);

        for (i = 0; i < edges.length; i++) {
            var path = schemaDrawEdge(edges[i], pos);
            if (path) edgeLayer.appendChild(path);
        }
        for (i = 0; i < placed.length; i++) {
            nodeLayer.appendChild(schemaDrawNode(placed[i]));
        }

        schemaBindPanZoom(svg);
        schemaHost.appendChild(svg);
        schemaApplyHighlight(svg, schemaSelectedId);
    }

    function schemaDrawEdge(edge, pos) {
        var a = pos[edge.from], b = pos[edge.to];
        if (!a || !b) return null;

        var x1 = a.x + SCHEMA_NODE_W, y1 = a.y + SCHEMA_NODE_H / 2;
        var x2 = b.x, y2 = b.y + SCHEMA_NODE_H / 2;
        var d;
        if (edge.from === edge.to) {
            // Self-reference: a loop out of the right edge and back in.
            d = 'M' + x1 + ',' + y1 + ' c 34,-18 34,18 0,0';
        } else if (x2 < x1) {
            // A back-edge (the target sits left of its user). Bow it below
            // the rows so it cannot be mistaken for a forward reference.
            var dip = Math.max(y1, y2) + SCHEMA_ROW_H * 0.8;
            d = 'M' + x1 + ',' + y1 + ' C' + (x1 + 40) + ',' + dip + ' ' + (x2 - 40) + ',' + dip + ' ' + x2 + ',' + y2;
        } else {
            var mid = (x1 + x2) / 2;
            d = 'M' + x1 + ',' + y1 + ' C' + mid + ',' + y1 + ' ' + mid + ',' + y2 + ' ' + x2 + ',' + y2;
        }

        var stroke = edge.kind === 'field'
            ? 'var(--bowire-border-strong, #94a3b8)'
            : 'var(--bowire-accent, #6366f1)';

        var g = schemaSvgEl('g', {
            'data-from': edge.from,
            'data-to': edge.to,
            'data-kind': edge.kind
        });
        g.appendChild(schemaSvgEl('path', {
            d: d,
            fill: 'none',
            stroke: stroke,
            'stroke-width': edge.kind === 'field' ? 1.2 : 1.6,
            'stroke-dasharray': edge.kind === 'response' ? '4 3' : null,
            opacity: 0.55
        }));
        if (edge.label) {
            var title = schemaSvgEl('title');
            title.textContent = edge.label + (edge.map ? ' (map)' : edge.repeated ? ' (repeated)' : '');
            g.appendChild(title);
        }
        return g;
    }

    function schemaDrawNode(p) {
        var n = p.node;
        var g = schemaSvgEl('g', {
            'data-node': n.id,
            transform: 'translate(' + p.x + ',' + p.y + ')',
            style: 'cursor:pointer'
        });

        var fill = n.kind === 'method'
            ? 'var(--bowire-accent-subtle, #eef2ff)'
            : n.kind === 'enum'
                ? 'var(--bowire-bg-subtle, #f1f5f9)'
                : 'var(--bowire-bg-elevated, #ffffff)';

        g.appendChild(schemaSvgEl('rect', {
            width: SCHEMA_NODE_W,
            height: SCHEMA_NODE_H,
            rx: n.kind === 'method' ? 14 : 4,
            fill: fill,
            stroke: 'var(--bowire-border, #cbd5e1)',
            'stroke-width': 1,
            'stroke-dasharray': n.nested ? '3 2' : null
        }));

        var label = schemaSvgEl('text', {
            x: 10,
            y: SCHEMA_NODE_H / 2 + 4,
            'font-size': 11,
            'font-family': 'var(--bowire-mono, monospace)',
            fill: 'var(--bowire-text, #0f172a)'
        });
        label.textContent = schemaTruncate(n.name, 20);
        g.appendChild(label);

        if (n.kind !== 'method' && (n.usageCount || 0) > 0) {
            var badge = schemaSvgEl('text', {
                x: SCHEMA_NODE_W - 9,
                y: SCHEMA_NODE_H / 2 + 4,
                'text-anchor': 'end',
                'font-size': 10,
                fill: 'var(--bowire-text-tertiary, #94a3b8)'
            });
            badge.textContent = String(n.usageCount);
            g.appendChild(badge);
        }

        var tip = schemaSvgEl('title');
        tip.textContent = n.id + '\n' + n.file
            + (n.kind === 'message' ? '\n' + t('schema.tip.fields', { count: String(n.fieldCount || 0) }) : '');
        g.appendChild(tip);

        // Handlers resolve the node by id at event time rather than
        // capturing it: the SVG is rebuilt on every filter change, and a
        // captured node would go stale the moment it is.
        g.addEventListener('mouseenter', function () { schemaApplyHighlight(g.ownerSVGElement, n.id); });
        g.addEventListener('mouseleave', function () { schemaApplyHighlight(g.ownerSVGElement, schemaSelectedId); });
        g.addEventListener('click', function () { schemaSelectNode(n.id); });
        return g;
    }

    /// Dim everything that is not the focus node or one of its direct
    /// neighbours. Applied straight to the live SVG rather than through a
    /// re-render, so hovering fifty nodes costs fifty attribute writes.
    function schemaApplyHighlight(svg, focusId) {
        if (!svg) return;
        var neighbours = Object.create(null);
        var i;
        if (focusId) {
            neighbours[focusId] = true;
            var edges = (schemaGraph && schemaGraph.edges) || [];
            for (i = 0; i < edges.length; i++) {
                if (edges[i].from === focusId) neighbours[edges[i].to] = true;
                if (edges[i].to === focusId) neighbours[edges[i].from] = true;
            }
        }

        var nodeGroups = svg.querySelectorAll('g[data-node]');
        for (i = 0; i < nodeGroups.length; i++) {
            var id = nodeGroups[i].getAttribute('data-node');
            nodeGroups[i].setAttribute('opacity', !focusId || neighbours[id] ? '1' : '0.25');
        }
        var edgeGroups = svg.querySelectorAll('g[data-from]');
        for (i = 0; i < edgeGroups.length; i++) {
            var touches = !focusId
                || edgeGroups[i].getAttribute('data-from') === focusId
                || edgeGroups[i].getAttribute('data-to') === focusId;
            edgeGroups[i].setAttribute('opacity', touches ? '1' : '0.12');
        }
    }

    function schemaSelectNode(id) {
        schemaSelectedId = (schemaSelectedId === id) ? null : id;
        render();
    }

    function schemaBindPanZoom(svg) {
        var dragging = false, lastX = 0, lastY = 0;

        svg.addEventListener('wheel', function (ev) {
            ev.preventDefault();
            var factor = ev.deltaY > 0 ? 1.12 : 1 / 1.12;
            var rect = svg.getBoundingClientRect();
            // Zoom about the pointer so the thing under the cursor stays
            // under the cursor — the behaviour every map has taught.
            var px = schemaViewport.x + ((ev.clientX - rect.left) / rect.width) * schemaViewport.w;
            var py = schemaViewport.y + ((ev.clientY - rect.top) / rect.height) * schemaViewport.h;
            schemaViewport.x = px - (px - schemaViewport.x) * factor;
            schemaViewport.y = py - (py - schemaViewport.y) * factor;
            schemaViewport.w *= factor;
            schemaViewport.h *= factor;
            svg.setAttribute('viewBox', schemaViewport.x + ' ' + schemaViewport.y + ' ' + schemaViewport.w + ' ' + schemaViewport.h);
        }, { passive: false });

        svg.addEventListener('pointerdown', function (ev) {
            if (ev.target.closest && ev.target.closest('g[data-node]')) return;
            dragging = true; lastX = ev.clientX; lastY = ev.clientY;
            svg.style.cursor = 'grabbing';
            svg.setPointerCapture(ev.pointerId);
        });
        svg.addEventListener('pointermove', function (ev) {
            if (!dragging) return;
            var rect = svg.getBoundingClientRect();
            schemaViewport.x -= ((ev.clientX - lastX) / rect.width) * schemaViewport.w;
            schemaViewport.y -= ((ev.clientY - lastY) / rect.height) * schemaViewport.h;
            lastX = ev.clientX; lastY = ev.clientY;
            svg.setAttribute('viewBox', schemaViewport.x + ' ' + schemaViewport.y + ' ' + schemaViewport.w + ' ' + schemaViewport.h);
        });
        svg.addEventListener('pointerup', function () { dragging = false; svg.style.cursor = 'grab'; });
        svg.addEventListener('pointercancel', function () { dragging = false; svg.style.cursor = 'grab'; });
    }

    function schemaTruncate(text, max) {
        return text.length <= max ? text : text.slice(0, max - 1) + '…';
    }

    // ---- main pane ----

    function renderSchemaMain() {
        if (!schemaHost) {
            schemaHost = el('div', {
                style: 'flex:1;min-height:0;overflow:hidden;background:var(--bowire-bg);'
            });
        }

        // First paint of the rail is what triggers the fetch: the rail is
        // default-off, so loading eagerly at startup would make every
        // operator pay for a graph most never open.
        if (schemaStatus === 'idle') {
            schemaStatus = 'loading';
            setTimeout(schemaLoadGraph, 0);
        }

        var body;
        if (schemaStatus === 'loading') {
            body = schemaNotice(t('schema.loading'));
        } else if (schemaStatus === 'error') {
            body = schemaNotice(schemaError || t('schema.loadFailedShort'));
        } else if (!schemaGraph || !schemaGraph.nodes || schemaGraph.nodes.length === 0) {
            body = schemaNotice(t('schema.empty'));
        } else {
            schemaDrawGraph();
            body = schemaHost;
        }

        return el('div', { style: 'display:flex;flex-direction:column;height:100%;min-height:0' },
            schemaToolbar(),
            body
        );
    }

    function schemaNotice(text) {
        return el('div', {
            style: 'flex:1;display:flex;align-items:center;justify-content:center;'
                + 'color:var(--bowire-text-tertiary);font-size:13px;padding:24px;text-align:center'
        }, el('span', { textContent: text }));
    }

    function schemaToolbar() {
        var files = (schemaGraph && schemaGraph.files) || [];

        var search = el('input', {
            type: 'search',
            value: schemaSearch,
            placeholder: t('schema.searchPlaceholder'),
            style: 'flex:1;min-width:140px;max-width:280px',
            oninput: function (ev) { schemaSearch = ev.target.value; schemaDrawGraph(); }
        });

        var fileSelect = el('select', {
            style: 'max-width:200px',
            onchange: function (ev) { schemaFileFilter = ev.target.value; schemaResetView(); }
        }, el('option', { value: '', textContent: t('schema.allFiles') }));
        for (var i = 0; i < files.length; i++) {
            fileSelect.appendChild(el('option', {
                value: files[i],
                textContent: files[i],
                selected: files[i] === schemaFileFilter ? 'selected' : undefined
            }));
        }

        var usage = el('select', {
            onchange: function (ev) { schemaMinUsage = parseInt(ev.target.value, 10) || 0; schemaResetView(); }
        });
        [0, 2, 3, 5].forEach(function (n) {
            usage.appendChild(el('option', {
                value: String(n),
                textContent: n === 0 ? t('schema.anyUsage') : t('schema.minUsage', { count: String(n) }),
                selected: n === schemaMinUsage ? 'selected' : undefined
            }));
        });

        return el('div', {
            style: 'display:flex;gap:8px;align-items:center;flex-wrap:wrap;padding:8px 12px;'
                + 'border-bottom:1px solid var(--bowire-border);flex:0 0 auto'
        },
            search,
            fileSelect,
            usage,
            el('label', { style: 'display:flex;align-items:center;gap:4px;font-size:12px;white-space:nowrap' },
                el('input', {
                    type: 'checkbox',
                    checked: schemaIncludeWellKnown ? 'checked' : undefined,
                    onchange: function (ev) { schemaIncludeWellKnown = ev.target.checked; schemaLoadGraph(); }
                }),
                el('span', { textContent: t('schema.wellKnown') })
            ),
            el('button', {
                className: 'bowire-btn-ghost',
                textContent: t('schema.resetView'),
                onclick: function () { schemaResetView(); }
            })
        );
    }

    // ---- sidebar ----

    function renderSchemaSidebar() {
        var rows = [];
        var selected = schemaSelectedNode();

        if (selected) {
            rows.push(schemaDetailPanel(selected));
        } else if (schemaGraph && schemaGraph.nodes) {
            var nodes = schemaVisibleNodes().slice().sort(function (a, b) {
                return (b.usageCount || 0) - (a.usageCount || 0) || a.name.localeCompare(b.name);
            });
            for (var i = 0; i < Math.min(nodes.length, 200); i++) {
                rows.push(schemaListRow(nodes[i]));
            }
        }

        if (rows.length === 0) {
            rows.push(el('div', {
                style: 'padding:12px;color:var(--bowire-text-tertiary);font-size:12px',
                textContent: t('schema.empty')
            }));
        }

        return el('div', { style: 'display:flex;flex-direction:column;min-height:0;overflow:auto' }, ...rows);
    }

    function schemaSelectedNode() {
        if (!schemaSelectedId || !schemaGraph || !schemaGraph.nodes) return null;
        for (var i = 0; i < schemaGraph.nodes.length; i++) {
            if (schemaGraph.nodes[i].id === schemaSelectedId) return schemaGraph.nodes[i];
        }
        return null;
    }

    function schemaListRow(node) {
        return el('div', {
            style: 'display:flex;align-items:center;gap:6px;padding:5px 10px;cursor:pointer;font-size:12px',
            onclick: function () { schemaSelectNode(node.id); }
        },
            el('span', {
                style: 'flex:1;font-family:var(--bowire-mono);overflow:hidden;text-overflow:ellipsis;white-space:nowrap',
                textContent: node.name,
                title: node.id
            }),
            el('span', {
                style: 'color:var(--bowire-text-tertiary);font-size:11px',
                textContent: node.kind === 'method' ? t('schema.kind.method') : String(node.usageCount || 0)
            })
        );
    }

    function schemaDetailPanel(node) {
        var edges = (schemaGraph && schemaGraph.edges) || [];
        var usedBy = [], references = [];
        for (var i = 0; i < edges.length; i++) {
            if (edges[i].to === node.id) usedBy.push(edges[i]);
            if (edges[i].from === node.id) references.push(edges[i]);
        }

        var children = [
            el('div', { style: 'display:flex;align-items:center;gap:6px;padding:8px 10px;border-bottom:1px solid var(--bowire-border)' },
                el('button', {
                    className: 'bowire-btn-ghost',
                    textContent: '←',
                    title: t('schema.back'),
                    onclick: function () { schemaSelectedId = null; render(); }
                }),
                el('span', {
                    style: 'font-family:var(--bowire-mono);font-size:12px;overflow:hidden;text-overflow:ellipsis',
                    textContent: node.name,
                    title: node.id
                })
            ),
            el('div', {
                style: 'padding:6px 10px;color:var(--bowire-text-tertiary);font-size:11px',
                textContent: node.file
            })
        ];

        children.push(schemaSection(t('schema.usedBy', { count: String(usedBy.length) }), usedBy, 'from'));
        children.push(schemaSection(t('schema.references', { count: String(references.length) }), references, 'to'));

        return el('div', {}, ...children);
    }

    function schemaSection(title, edges, endKey) {
        var rows = [el('div', {
            style: 'padding:6px 10px;font-size:11px;text-transform:uppercase;letter-spacing:.04em;'
                + 'color:var(--bowire-text-tertiary)',
            textContent: title
        })];
        for (var i = 0; i < edges.length; i++) {
            rows.push(schemaEdgeRow(edges[i], endKey));
        }
        return el('div', {}, ...rows);
    }

    function schemaEdgeRow(edge, endKey) {
        var otherId = edge[endKey];
        var label = edge.label || (edge.kind === 'request' ? t('schema.kind.request') : t('schema.kind.response'));
        return el('div', {
            style: 'display:flex;gap:6px;padding:4px 10px;font-size:12px;cursor:pointer',
            onclick: function () { schemaSelectNode(otherId); }
        },
            el('span', {
                style: 'flex:1;font-family:var(--bowire-mono);overflow:hidden;text-overflow:ellipsis;white-space:nowrap',
                textContent: otherId,
                title: otherId
            }),
            el('span', { style: 'color:var(--bowire-text-tertiary);font-size:11px', textContent: label })
        );
    }

    // ---- registration ----

    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.schemaSidebar = renderSchemaSidebar;
        window.__bowireRailRenderers.schemaMain = renderSchemaMain;
        // Discovery finishing is what makes a graph possible, so the rail
        // reloads on it rather than on being opened — otherwise switching
        // to the rail after a re-discovery would show the previous schema.
        window.__bowireSchemaReload = function () {
            if (schemaStatus !== 'loading') schemaLoadGraph();
        };
    }
