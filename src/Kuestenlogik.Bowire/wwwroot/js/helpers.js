    // ---- Helpers ----
    function $(sel, parent) { return (parent || document).querySelector(sel); }
    function $$(sel, parent) { return [...(parent || document).querySelectorAll(sel)]; }

    function el(tag, attrs, ...children) {
        const e = document.createElement(tag);
        if (attrs) {
            for (const [k, v] of Object.entries(attrs)) {
                // Skip undefined/null so the caller can use conditional
                // expressions like `readOnly: locked ? 'readonly' : undefined`
                // without accidentally setting the attribute to the
                // literal string "undefined" (which makes inputs locked,
                // anchors disabled, &c).
                if (v === undefined || v === null) continue;
                if (k === 'className') e.className = v;
                else if (k === 'textContent') e.textContent = v;
                else if (k === 'innerHTML') e.innerHTML = v;
                else if (k.startsWith('on')) e.addEventListener(k.slice(2).toLowerCase(), v);
                else if (k === 'dataset') Object.assign(e.dataset, v);
                else e.setAttribute(k, v);
            }
        }
        // Children may be: a Node, a string, null/undefined (skip),
        // or an array of any of the above. Arrays come from
        // .map(...) callers — without the unwrap branch, appendChild
        // throws TypeError and the parent never renders. The same
        // shape works fine in JSX-style frameworks; matching it here
        // keeps el() idiomatic for callers that build conditional
        // children lists.
        function appendChildren(node, list) {
            for (const c of list) {
                if (Array.isArray(c)) appendChildren(node, c);
                else if (typeof c === 'string') node.appendChild(document.createTextNode(c));
                else if (c) node.appendChild(c);
            }
        }
        appendChildren(e, children);
        return e;
    }

    /**
     * Build the ?serverUrl=... query fragment. Pass an explicit url to target
     * a specific server, otherwise the primary (first in serverUrls) is used.
     * Returns "" when no URL is available — embedded mode falls through to
     * whatever the host resolved.
     */
    function serverUrlParam(prefix, url) {
        var u = url ?? getPrimaryServerUrl();
        if (!u) return '';
        // Substitute {{var}} placeholders so users can use {{baseUrl}} in the URL field
        var resolved = substituteVars(u);
        var sep = prefix ? '&' : '?';
        return sep + 'serverUrl=' + encodeURIComponent(resolved);
    }

    /**
     * Per-service variant: routes invocations to the URL the service was
     * discovered from. Multi-URL setups depend on this so a method from
     * "https://api-a.com" doesn't accidentally fire against "https://api-b.com".
     */
    function serverUrlParamForService(service, prefix) {
        var url = (service && service.originUrl) || getPrimaryServerUrl();
        return serverUrlParam(prefix, url);
    }

    function grpcStatusClass(status) {
        switch (status) {
            case 'OK': case 'Completed': return 'ok';
            case 'Streaming': case 'Connected': return 'streaming';
            case 'Cancelled': return 'warning';
            case 'NotFound': case 'Unimplemented': case 'InvalidArgument':
            case 'FailedPrecondition': case 'OutOfRange': case 'AlreadyExists':
                return 'warning';
            case 'DeadlineExceeded': case 'ResourceExhausted': case 'Unavailable':
            case 'Aborted':
                return 'caution';
            case 'Internal': case 'Unknown': case 'DataLoss': case 'Unauthenticated':
            case 'PermissionDenied': case 'Error': case 'NetworkError':
                return 'error';
            default: return 'unknown';
        }
    }

    function grpcStatusLabel(status) {
        switch (status) {
            case 'OK': return 'OK';
            case 'Cancelled': return 'CANCELLED';
            case 'InvalidArgument': return 'INVALID_ARGUMENT';
            case 'NotFound': return 'NOT_FOUND';
            case 'AlreadyExists': return 'ALREADY_EXISTS';
            case 'PermissionDenied': return 'PERMISSION_DENIED';
            case 'ResourceExhausted': return 'RESOURCE_EXHAUSTED';
            case 'FailedPrecondition': return 'FAILED_PRECONDITION';
            case 'OutOfRange': return 'OUT_OF_RANGE';
            case 'Unimplemented': return 'UNIMPLEMENTED';
            case 'DeadlineExceeded': return 'DEADLINE_EXCEEDED';
            case 'Unavailable': return 'UNAVAILABLE';
            case 'DataLoss': return 'DATA_LOSS';
            case 'Unauthenticated': return 'UNAUTHENTICATED';
            default: return status;
        }
    }

    function downloadResponse(format) {
        var methodName = selectedMethod ? selectedMethod.name : 'response';
        var text, filename, mimeType;

        if (format === 'proto') {
            // Proto binary: fetch raw bytes from server
            if (!responseData && streamMessages.length === 0) { toast('No response to download', 'info'); return; }
            // For proto binary, we'd need the raw bytes from the server.
            // Since we only have JSON, convert back (best effort).
            toast('Proto binary download requires raw bytes — use JSON for now', 'info');
            return;
        }

        // JSON format
        if (streamMessages.length > 0) {
            var msgs = streamMessages.map(function (m) {
                try { return JSON.parse(m.data || '{}'); } catch { return m.data; }
            });
            text = JSON.stringify(msgs, null, 2);
            filename = methodName + '-responses.json';
        } else if (responseData) {
            try { text = JSON.stringify(JSON.parse(responseData), null, 2); } catch { text = responseData; }
            filename = methodName + '.json';
        } else {
            toast('No response to download', 'info');
            return;
        }

        var blob = new Blob([text], { type: 'application/json' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        a.click();
        URL.revokeObjectURL(a.href);
        toast('Downloaded ' + filename, 'success');
    }

    function formatBytes(bytes) {
        if (bytes === 0) return '0 B';
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
        return (bytes / 1048576).toFixed(1) + ' MB';
    }

    function formatJson(json) {
        try {
            return JSON.stringify(JSON.parse(json), null, 2);
        } catch {
            return json;
        }
    }

    // Cheap inline JSON validation for the request editors. Empty input
    // is treated as valid (the user is mid-edit, not actively wrong).
    // Returns { ok: bool, error: string|null }. The status line under
    // the editor uses this and the .ok / .error class to flip colour
    // and pin a parser hint when something is broken.
    function validateJsonText(text) {
        var t = (text || '').trim();
        if (!t) return { ok: true, error: null, empty: true };
        try {
            JSON.parse(t);
            return { ok: true, error: null, empty: false };
        } catch (e) {
            return { ok: false, error: e.message || 'Invalid JSON', empty: false };
        }
    }

    // Pairs a textarea with its status element so the validation feedback
    // updates as the user types. Used for both the single-message and
    // multi-message JSON editors. The initial validation pass runs once
    // synchronously so the status reflects the textarea's starting value
    // (template / restored state) without waiting for an input event.
    function attachJsonValidator(textarea, statusEl) {
        function update() {
            var result = validateJsonText(textarea.value);
            statusEl.classList.remove('ok', 'error', 'empty');
            if (result.empty) {
                statusEl.classList.add('empty');
                statusEl.textContent = '';
            } else if (result.ok) {
                statusEl.classList.add('ok');
                statusEl.textContent = '\u2713 Valid JSON';
            } else {
                statusEl.classList.add('error');
                statusEl.textContent = '\u26A0 ' + result.error;
            }
        }
        textarea.addEventListener('input', update);
        update();
    }

    function highlightJson(json) {
        var formatted;
        try {
            formatted = JSON.stringify(JSON.parse(json), null, 2);
        } catch {
            return escapeHtml(json);
        }
        return formatted.replace(
            /("(?:\\.|[^"\\])*")\s*:|("(?:\\.|[^"\\])*")|([-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)|(\btrue\b|\bfalse\b)|(\bnull\b)/g,
            function (match, key, str, num, bool_, null_) {
                if (key) return '<span class="bowire-json-key">' + escapeHtml(key) + '</span>:';
                if (str) return '<span class="bowire-json-string">' + escapeHtml(str) + '</span>';
                if (num) return '<span class="bowire-json-number">' + escapeHtml(num) + '</span>';
                if (bool_) return '<span class="bowire-json-boolean">' + escapeHtml(bool_) + '</span>';
                if (null_) return '<span class="bowire-json-null">' + escapeHtml(null_) + '</span>';
                return escapeHtml(match);
            }
        );
    }

    // Walks a parsed JSON value and emits the same syntax-highlighted
    // markup as highlightJson(), but each key and leaf carries a
    // data-json-path attribute. The response-pane click handler copies
    // that path as a ${response.X} chaining variable so the user can
    // paste it straight into the next request body.
    //
    // Path format matches walkJsonPath() in prologue.js: dot-separated
    // keys for both objects and array indices (no brackets). That's the
    // syntax the chaining engine accepts, so what the user sees in the
    // toast is exactly what they can paste.
    function highlightJsonInteractive(rawText) {
        var parsed;
        try {
            parsed = typeof rawText === 'string' ? JSON.parse(rawText) : rawText;
        } catch {
            return escapeHtml(String(rawText));
        }
        return walkJsonForRender(parsed, '', 0);
    }

    function walkJsonForRender(value, path, indent) {
        var pad = '';
        for (var p = 0; p < indent; p++) pad += '  ';
        var nextPad = pad + '  ';
        var pathAttr = ' data-json-path="' + escapeHtml(path) + '"';

        if (value === null) {
            return '<span class="bowire-json-null bowire-json-pickable"' + pathAttr + '>null</span>';
        }
        if (typeof value === 'string') {
            return '<span class="bowire-json-string bowire-json-pickable"' + pathAttr + '>'
                + escapeHtml(JSON.stringify(value)) + '</span>';
        }
        if (typeof value === 'number') {
            return '<span class="bowire-json-number bowire-json-pickable"' + pathAttr + '>'
                + escapeHtml(String(value)) + '</span>';
        }
        if (typeof value === 'boolean') {
            return '<span class="bowire-json-boolean bowire-json-pickable"' + pathAttr + '>'
                + (value ? 'true' : 'false') + '</span>';
        }
        if (Array.isArray(value)) {
            if (value.length === 0) return '[]';
            var aparts = [];
            for (var i = 0; i < value.length; i++) {
                var childPath = path ? path + '.' + i : String(i);
                aparts.push(nextPad + walkJsonForRender(value[i], childPath, indent + 1));
            }
            return '[\n' + aparts.join(',\n') + '\n' + pad + ']';
        }
        if (typeof value === 'object') {
            var keys = Object.keys(value);
            if (keys.length === 0) return '{}';
            var oparts = [];
            for (var k = 0; k < keys.length; k++) {
                var ckey = keys[k];
                var ckpath = path ? path + '.' + ckey : ckey;
                oparts.push(nextPad
                    + '<span class="bowire-json-key bowire-json-pickable" data-json-path="'
                    + escapeHtml(ckpath) + '">'
                    + escapeHtml(JSON.stringify(ckey))
                    + '</span>: '
                    + walkJsonForRender(value[ckey], ckpath, indent + 1));
            }
            return '{\n' + oparts.join(',\n') + '\n' + pad + '}';
        }
        return escapeHtml(String(value));
    }

    function escapeHtml(s) {
        // Full attribute-safe escape — covers `"` and `'` in addition
        // to the three structural characters. Without those two an
        // attacker who controls a JSON path or value could break out
        // of a `data-json-path="…"` wrapper and inject extra
        // attributes / event handlers.
        return s
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // Collapsible tree view of a JSON value. Uses native <details> /
    // <summary> elements so the browser handles open/close state with
    // zero JS — only the leaf rendering and the data-json-path
    // attributes are ours. Each node carries the same path as
    // highlightJsonInteractive(), so the response-pane click handler
    // can copy ${response.X} chaining variables from either view.
    function renderJsonTree(rawText) {
        var parsed;
        try {
            parsed = typeof rawText === 'string' ? JSON.parse(rawText) : rawText;
        } catch {
            return '<pre class="bowire-json-tree-fallback">' + escapeHtml(String(rawText)) + '</pre>';
        }
        return '<div class="bowire-json-tree">' + treeNodeHtml(parsed, '', null, true) + '</div>';
    }

    function treeNodeHtml(value, path, label, openByDefault) {
        // Leaf values render as a single coloured row with the path
        // attached, just like the inline highlighter — clicking still
        // copies the chaining variable.
        function leafSpan(cls, text, p) {
            return '<span class="' + cls + ' bowire-json-pickable" data-json-path="'
                + escapeHtml(p) + '">' + escapeHtml(text) + '</span>';
        }
        function labelHtml(p) {
            if (label === null || label === undefined) return '';
            return '<span class="bowire-json-tree-label bowire-json-pickable" data-json-path="'
                + escapeHtml(p) + '">' + escapeHtml(JSON.stringify(label)) + '</span>'
                + '<span class="bowire-json-tree-colon">: </span>';
        }

        if (value === null) {
            return '<div class="bowire-json-tree-row">' + labelHtml(path)
                + leafSpan('bowire-json-null', 'null', path) + '</div>';
        }
        if (typeof value === 'string') {
            return '<div class="bowire-json-tree-row">' + labelHtml(path)
                + leafSpan('bowire-json-string', JSON.stringify(value), path) + '</div>';
        }
        if (typeof value === 'number') {
            return '<div class="bowire-json-tree-row">' + labelHtml(path)
                + leafSpan('bowire-json-number', String(value), path) + '</div>';
        }
        if (typeof value === 'boolean') {
            return '<div class="bowire-json-tree-row">' + labelHtml(path)
                + leafSpan('bowire-json-boolean', value ? 'true' : 'false', path) + '</div>';
        }
        // Arrays + objects render as IDE-style collapsible blocks:
        //   collapsed:  "label": [ … ]              (or { … })
        //   expanded:   "label": [
        //                 ...children...
        //               ]                          (closing bracket below)
        // The "3 items"/"3 keys" meta moves out of the inline text
        // into the summary's title attribute — visible on hover, not
        // competing with the JSON itself.
        function containerSummary(openBracket, closeBracket, metaText) {
            return '<summary class="bowire-json-tree-summary" title="' + escapeHtml(metaText) + '">'
                + labelHtml(path)
                + '<span class="bowire-json-tree-bracket">' + openBracket + '</span>'
                + '<span class="bowire-json-tree-ellipsis">' + ' … ' + '</span>'
                + '<span class="bowire-json-tree-bracket bowire-json-tree-bracket-closed">'
                + closeBracket + '</span>'
                + '</summary>';
        }
        function containerClose(closeBracket) {
            return '<div class="bowire-json-tree-row bowire-json-tree-close">'
                + '<span class="bowire-json-tree-bracket">' + closeBracket + '</span></div>';
        }

        if (Array.isArray(value)) {
            var openAttr = openByDefault ? ' open' : '';
            var len = value.length;
            var meta = len + (len === 1 ? ' item' : ' items');
            var summary = containerSummary('[', ']', meta);
            if (len === 0) {
                // Empty array — collapse marker doesn't apply, just
                // print "[]" inline. Stays consistent with the
                // expanded form where children list would be empty.
                return '<div class="bowire-json-tree-row">' + labelHtml(path)
                    + '<span class="bowire-json-tree-bracket">[]</span></div>';
            }
            var children = '';
            for (var i = 0; i < len; i++) {
                var childPath = path ? path + '.' + i : String(i);
                children += treeNodeHtml(value[i], childPath, i, false);
            }
            return '<details class="bowire-json-tree-node"' + openAttr + '>' + summary
                + '<div class="bowire-json-tree-children">' + children + '</div>'
                + containerClose(']')
                + '</details>';
        }
        if (typeof value === 'object') {
            var keys = Object.keys(value);
            var openAttr2 = openByDefault ? ' open' : '';
            var meta2 = keys.length + (keys.length === 1 ? ' key' : ' keys');
            var summary2 = containerSummary('{', '}', meta2);
            if (keys.length === 0) {
                return '<div class="bowire-json-tree-row">' + labelHtml(path)
                    + '<span class="bowire-json-tree-bracket">{}</span></div>';
            }
            var children2 = '';
            for (var k = 0; k < keys.length; k++) {
                var ckey = keys[k];
                var ckpath = path ? path + '.' + ckey : ckey;
                children2 += treeNodeHtml(value[ckey], ckpath, ckey, false);
            }
            return '<details class="bowire-json-tree-node"' + openAttr2 + '>' + summary2
                + '<div class="bowire-json-tree-children">' + children2 + '</div>'
                + containerClose('}')
                + '</details>';
        }
        return '<div class="bowire-json-tree-row">' + escapeHtml(String(value)) + '</div>';
    }

    function initResizer(divider, leftPane, rightPane) {
        var startX, startLeftWidth, startRightWidth;
        var isDragging = false;

        function onMouseDown(e) {
            isDragging = true;
            startX = e.clientX;
            startLeftWidth = leftPane.offsetWidth;
            startRightWidth = rightPane.offsetWidth;
            divider.classList.add('dragging');
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
            e.preventDefault();
        }

        function onMouseMove(e) {
            if (!isDragging) return;
            var dx = e.clientX - startX;
            var totalWidth = startLeftWidth + startRightWidth;
            var newLeftWidth = Math.max(200, Math.min(totalWidth - 200, startLeftWidth + dx));
            var newRightWidth = totalWidth - newLeftWidth;
            leftPane.style.flex = 'none';
            leftPane.style.width = newLeftWidth + 'px';
            rightPane.style.flex = 'none';
            rightPane.style.width = newRightWidth + 'px';
        }

        function onMouseUp() {
            isDragging = false;
            divider.classList.remove('dragging');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
        }

        divider.addEventListener('mousedown', onMouseDown);
    }

    function shortType(type) {
        switch (type) {
            case 'Unary': return 'U';
            case 'ServerStreaming': return 'SS';
            case 'ClientStreaming': return 'CS';
            case 'Duplex': return 'DX';
            // AsyncAPI operations carry a direction (`send` from the
            // application toward the broker, `receive` from the broker
            // toward the application). Arrows make the polarity readable
            // at a glance — leftward for "broker → us", rightward for
            // "us → broker".
            case 'asyncapi-send': return '→'; // →
            case 'asyncapi-receive': return '←'; // ←
            default: return type;
        }
    }

    /**
     * Returns the badge text for a method. REST methods (which carry an
     * httpMethod annotation) show the HTTP verb instead of "U" so the user
     * can scan a list of REST endpoints and tell GET from POST at a glance.
     */
    function methodBadgeText(method) {
        if (method && method.httpMethod) return method.httpMethod;
        return shortType(method && method.methodType);
    }

    /**
     * Returns a "data-type" value for the method badge. REST verbs get their
     * own bucket so the CSS can color them per-verb.
     */
    function methodBadgeType(method) {
        if (method && method.httpMethod) return 'HTTP_' + method.httpMethod;
        return method && method.methodType;
    }

    /**
     * #122 — direction axis: maps a method (or step descriptor) to one
     * of warm / cool / duplex / neutral. CSS picks up the value via
     * data-direction on the badge / row / step affordance and resolves
     * it to the corresponding --bowire-direction-* variable.
     *
     * Mapping intent (see issue body):
     *   warm    = client → server (mutation, send, publish, request kick-off)
     *   cool    = server → client (read, response, server-stream, subscribe-incoming)
     *   duplex  = bidirectional (duplex stream, websocket, hub method)
     *   neutral = metadata-only / unclassified / fallback
     */
    function methodDirection(method) {
        if (!method) return 'neutral';
        // REST verbs first — most explicit signal.
        var verb = (method.httpMethod || '').toUpperCase();
        if (verb === 'GET' || verb === 'HEAD' || verb === 'OPTIONS') return 'cool';
        if (verb === 'POST' || verb === 'PUT' || verb === 'PATCH' || verb === 'DELETE') return 'warm';

        // gRPC / streaming method types.
        switch (method.methodType) {
            case 'Unary':           return 'warm';      // client initiates
            case 'ServerStreaming': return 'cool';      // server emits
            case 'ClientStreaming': return 'warm';      // client emits
            case 'Duplex':          return 'duplex';
            case 'asyncapi-send':   return 'warm';
            case 'asyncapi-receive':return 'cool';
        }

        // Pub/Sub-shaped sources (MQTT publish vs subscribe, NATS pub vs sub).
        var op = (method.operation || method.kind || '').toLowerCase();
        if (op === 'publish' || op === 'send' || op === 'pub') return 'warm';
        if (op === 'subscribe' || op === 'receive' || op === 'sub') return 'cool';

        // GraphQL operation kinds.
        if (op === 'query') return 'cool';
        if (op === 'mutation') return 'warm';
        if (op === 'subscription') return 'cool';

        return 'neutral';
    }

    function methodBadgeLabel(type) {
        switch (type) {
            case 'Unary': return 'Unary';
            case 'ServerStreaming': return 'Server Stream';
            case 'ClientStreaming': return 'Client Stream';
            case 'Duplex': return 'Duplex';
            case 'asyncapi-send': return 'AsyncAPI Send';
            case 'asyncapi-receive': return 'AsyncAPI Receive';
            default: return type;
        }
    }

    /**
     * Normalise an error response body into the {title, detail, links,
     * extensions} shape every error renderer in the workbench wants
     * (#88). Accepts:
     *
     *   - RFC 7807 problem+json: { type, title, status, detail, instance, ... }
     *   - Plain string:          surfaced as the title
     *   - null/undefined:        a generic fallback
     *
     * Returns null when there's nothing error-shaped in the input.
     * Callers should use the result like:
     *
     *   var problem = normalizeProblem(body);
     *   if (problem) renderProblem(problem, container);
     */
    function normalizeProblem(body) {
        if (body === null || body === undefined) return null;
        if (typeof body === 'string') {
            return { title: body, detail: null, links: [], extensions: {} };
        }
        if (typeof body !== 'object') return null;

        if (typeof body.title !== 'string' || !body.title) return null;

        var extensions = {};
        for (var k in body) {
            if (!Object.prototype.hasOwnProperty.call(body, k)) continue;
            if (k === 'type' || k === 'title' || k === 'status' ||
                k === 'detail' || k === 'instance' || k === 'links') continue;
            extensions[k] = body[k];
        }
        return {
            type: body.type || null,
            title: body.title,
            detail: body.detail || null,
            status: body.status || null,
            instance: body.instance || null,
            links: Array.isArray(body.links) ? body.links : [],
            extensions: extensions,
        };
    }

    /**
     * One-line helper for toast / button-text / inline-text error
     * contexts (#91). Returns just the title of a problem+json body
     * (or a generic fallback) — no DOM, no card. Use for cases where a
     * single string is all that fits; use renderProblem when you have
     * room for the full structured card. Defensive: never throws.
     */
    function problemTitle(body, fallback) {
        if (typeof body === 'string' && body) return body;
        var p = normalizeProblem(body);
        if (p && p.title) return p.title;
        return fallback || 'Request failed';
    }

    /**
     * #243 — Rich error-detail formatter for console entries on failed
     * invocations. The plain {@link problemTitle} returns a single
     * line (usually just "Error" or the problem+json title), which is
     * useless when the actual signal — status code, response body,
     * exception message — lives further down in the result object.
     * Build a multi-line block that surfaces every available signal,
     * truncated to 4 KB so a giant response doesn't drown the console.
     *
     * Lines emitted (skipped when the field is empty):
     *   <title or fallback>
     *   Status: <httpStatus>  <statusReason>
     *   Detail: <problem+json detail>
     *   Body: <response body, truncated>
     *   Exception: <errorType>: <errorMessage>
     *   Stack: <first relevant frame>
     *
     * Each signal is keyed off the BowireInvokeResult shape returned
     * by /api/invoke. Defensive: a result that's just a plain string
     * falls through to a single-line block carrying the string.
     */
    function richErrorDetail(result, fallback) {
        var lines = [];
        if (typeof result === 'string' && result) {
            return result;
        }
        var title = problemTitle(result, fallback);
        if (title) lines.push(title);
        if (result && typeof result === 'object') {
            // Server's /api/invoke returns problem+json with a bare
            // `status` field carrying the HTTP code (see
            // BowireEndpointHelpers.Problem). Try that first, then fall
            // back to the *_code/*Status aliases for test doubles /
            // future endpoints.
            // problem+json's `status` is always present (server helper
            // requires it). Accept number OR numeric-string ("502")
            // since JSON.parse can deserialize either depending on
            // upstream serializer config. Fall through to the *_code
            // aliases for non-problem+json producers.
            var rawStatus = result.status;
            var statusCode = 0;
            if (typeof rawStatus === 'number' && rawStatus > 0) {
                statusCode = rawStatus;
            } else if (typeof rawStatus === 'string' && /^\d+$/.test(rawStatus)) {
                statusCode = parseInt(rawStatus, 10);
            } else {
                statusCode = result.status_code || result.statusCode
                    || result.httpStatus || result.http_status || 0;
            }
            var statusReason = result.status_reason || result.statusReason || '';
            if (statusCode) {
                lines.push('Status: ' + statusCode + (statusReason ? ' ' + statusReason : ''));
            }
            if (result.detail && result.detail !== result.title) {
                lines.push('Detail: ' + String(result.detail));
            }
            // problem+json carries a `type` URI and `instance` —
            // surface both inline so the operator can copy/click
            // without expanding the entry to inspect the raw body.
            if (result.type && result.type !== 'about:blank' && result.type !== result.title) {
                lines.push('Type: ' + String(result.type));
            }
            if (result.instance && result.instance !== '/api/invoke') {
                lines.push('At: ' + String(result.instance));
            }
            // Some endpoints return the response body even on failure
            // (4xx with a problem+json body, 5xx with a stack trace).
            // Show it so the operator can see the server's verbatim
            // message instead of guessing.
            if (result.response !== undefined && result.response !== null) {
                var bodyText = typeof result.response === 'string'
                    ? result.response
                    : JSON.stringify(result.response);
                if (bodyText && bodyText !== '{}') {
                    if (bodyText.length > 4096) {
                        bodyText = bodyText.substring(0, 4096) + '… (' + (bodyText.length - 4096) + ' more bytes truncated)';
                    }
                    lines.push('Body: ' + bodyText);
                }
            }
            var errType = result.errorType || result.error_type || result.exceptionType || result.exception_type;
            var errMsg = result.errorMessage || result.error_message || result.exception || result.error;
            if (errType || errMsg) {
                lines.push('Exception: ' + (errType ? errType + ': ' : '') + (errMsg || '(no message)'));
            }
            var stack = result.stackTrace || result.stack_trace;
            if (stack && typeof stack === 'string') {
                // Filter MS framework frames (high signal-to-noise:
                // Bowire / plugin frames first; framework internals
                // are noise unless we're debugging the framework
                // itself).
                var frames = stack.split('\n')
                    .map(function (s) { return s.trim(); })
                    .filter(function (s) {
                        if (!s) return false;
                        if (s.indexOf('at System.') === 0) return false;
                        if (s.indexOf('at Microsoft.') === 0) return false;
                        if (s.indexOf('at Internal.') === 0) return false;
                        return true;
                    });
                if (frames.length > 0) {
                    lines.push('Stack: ' + frames.slice(0, 3).join(' / '));
                }
            }
        }
        return lines.length > 0 ? lines.join('\n') : (fallback || 'Request failed');
    }

    /**
     * Render an empty-state card (#121). Reusable across every pane
     * that can be empty so the operator gets a consistent "what's
     * possible from here" shape instead of a literal "No X" string.
     *
     * opts:
     *   icon      — optional svg-icon id (Bowire icon catalog) for
     *               the large centred glyph above the headline.
     *               Falls back to a neutral dot.
     *   headline  — short string naming the gap ("No recordings yet").
     *   body      — optional longer text explaining the pane's purpose.
     *   actions   — array of { label, onClick, primary? } objects;
     *               renders as one button per entry. Pass [] for no
     *               buttons (rare — most empty states have at least
     *               one obvious next step).
     *   hintKey   — optional key for a permanently dismissible
     *               first-time hint. When set + not yet dismissed,
     *               the body text shows with a small "Got it" dismiss
     *               link below. Routed through the central hint-dismiss
     *               store (#169) so Settings → "Hints and warnings"
     *               can restore it.
     *   hintLabel — human-readable description for the Settings row.
     *               Defaults to opts.headline, then opts.hintKey.
     *
     * Returns a DOM node the caller appendChilds into the pane.
     * No state ownership — caller decides when the card mounts.
     */
    // ---- #115 Drawer-Primitive ----
    //
    // Shared widget for every right-side workbench drawer (Assistant,
    // Help, Tests, Activity, future Inspector, …). renderUnifiedRightDrawer
    // (render-env-auth.js) delegates here for the single-tab path; new
    // drawer surfaces are one renderDrawer({...}) call instead of a
    // copy-paste-and-rename of the chrome.
    //
    // Options:
    //   id          — root element id (required for morphdom keying)
    //   className   — optional extra CSS classes on the root
    //   title       — header label, plain string
    //   titleAccessory — optional DOM node placed right of the title
    //                    (e.g. AI's live status dot)
    //   closeTitle  — tooltip for the close button (defaults to 'Close')
    //   closeAriaLabel — aria-label for the close button
    //   ariaLabel   — aria-label for the drawer root
    //   onClose     — function called when the close button is clicked
    //   content     — DOM node OR a function returning one. Functions
    //                 let the caller defer content building until the
    //                 drawer is actually being rendered (the Assistant
    //                 panel is non-trivial to assemble).
    function renderDrawer(opts) {
        opts = opts || {};
        var drawer = el('div', {
            id: opts.id,
            className: 'bowire-drawer' + (opts.className ? ' ' + opts.className : ''),
            role: 'complementary',
            'aria-label': opts.ariaLabel || opts.title || 'Drawer'
        });

        var titleRow = el('div', { className: 'bowire-drawer-title-row' },
            el('span', { className: 'bowire-drawer-title', textContent: opts.title || '' }),
            opts.titleAccessory || null
        );

        var header = el('div', { className: 'bowire-drawer-header' },
            titleRow,
            el('button', {
                className: 'bowire-drawer-close',
                title: opts.closeTitle || 'Close',
                'aria-label': opts.closeAriaLabel || ('Close ' + (opts.title || 'drawer')),
                innerHTML: svgIcon('close'),
                onClick: function () {
                    if (typeof opts.onClose === 'function') opts.onClose();
                }
            })
        );
        drawer.appendChild(header);

        var contentWrap = el('div', { className: 'bowire-drawer-content' });
        var content;
        try {
            content = (typeof opts.content === 'function') ? opts.content() : opts.content;
        } catch (e) {
            console.warn('[drawer] content render failed', e);
            content = el('p', {
                className: 'bowire-drawer-empty',
                textContent: 'Drawer content failed to render.'
            });
        }
        if (content) contentWrap.appendChild(content);
        drawer.appendChild(contentWrap);

        return drawer;
    }

    // Generic alert bar — info / warning / error severity, optional
    // dismiss X, optional inline action button. Mounts as a full-width
    // strip; callers append it to the top of a pane / drawer body so it
    // sits like a status banner under the local header.
    //
    // opts:
    //   severity   — 'info' | 'warning' | 'error' (default 'info')
    //   text       — the message text
    //   actionLabel — optional button label (e.g. 'Enable in Settings')
    //   onAction   — onClick for the action button
    //   dismissKey — session-only dismiss; bar reappears next reload.
    //   permanentDismissKey — permanent dismiss; bar stays gone until
    //                the operator re-enables it from Settings →
    //                General → "Hints and warnings". When set the
    //                close affordance becomes a split control: X
    //                dismisses for the session, the chevron next to it
    //                offers "Don't show again".
    //   dismissLabel — human-readable description for the Settings
    //                row. Defaults to the key itself, which is ugly —
    //                callers should always provide this.
    function renderAlertBar(opts) {
        opts = opts || {};
        // Either dismiss key can suppress the bar at render-time. The
        // permanent key wins if both are set (legacy callers that pass
        // dismissKey only continue to behave as before via the same
        // store; the helper just reads + writes via isHintDismissed /
        // dismissHint instead of poking sessionStorage directly).
        var primaryKey = opts.permanentDismissKey || opts.dismissKey;
        if (primaryKey && typeof isHintDismissed === 'function'
            && isHintDismissed(primaryKey)) return null;
        var severity = (opts.severity === 'warning' || opts.severity === 'error') ? opts.severity : 'info';
        var bar = el('div', {
            className: 'bowire-alert-bar bowire-alert-bar-' + severity,
            role: severity === 'error' ? 'alert' : 'status'
        });
        bar.appendChild(el('span', { className: 'bowire-alert-bar-dot' }));
        bar.appendChild(el('span', {
            className: 'bowire-alert-bar-text',
            textContent: opts.text || ''
        }));
        if (opts.actionLabel && typeof opts.onAction === 'function') {
            bar.appendChild(el('button', {
                type: 'button',
                className: 'bowire-alert-bar-action',
                textContent: opts.actionLabel,
                onClick: opts.onAction
            }));
        }
        // Inline toggle support — same chrome as the Settings toggle
        // switch. Use when the alert's primary call-to-action is to
        // flip a single boolean (e.g. "AI is observe-only" → flip the
        // master Allow toggle without navigating to Settings).
        if (opts.inlineToggle && typeof opts.inlineToggle.onChange === 'function') {
            var iv = !!opts.inlineToggle.value;
            // Heuristic: an inline toggle on an alert bar usually
            // mirrors the condition the bar is warning about (e.g.
            // "AI is observe-only" with a toggle to allow invocation).
            // Flipping the toggle to its "resolve" state should fade
            // the bar out so the operator visibly perceives the
            // resolution instead of a snap-disappear. Set
            // dismissOnToggleTo=true to enable this; default off
            // means the bar stays put after the flip.
            var dismissOn = opts.inlineToggle.dismissOnToggleTo;
            var toggle = el('button', {
                type: 'button',
                className: 'bowire-settings-toggle bowire-alert-bar-toggle' + (iv ? ' on' : ''),
                'aria-pressed': iv ? 'true' : 'false',
                title: opts.inlineToggle.title || (iv ? 'On — click to turn off' : 'Off — click to turn on'),
                onClick: function () {
                    iv = !iv;
                    toggle.classList.toggle('on', iv);
                    toggle.setAttribute('aria-pressed', iv ? 'true' : 'false');
                    var dot = toggle.querySelector('.bowire-settings-toggle-dot');
                    if (dot) dot.style.transform = iv ? 'translateX(16px)' : 'translateX(0)';
                    opts.inlineToggle.onChange(iv);
                    if (dismissOn !== undefined && iv === !!dismissOn) {
                        // The bar text described the OLD state ("X is
                        // in mode Y"). Swap to the resolved-state
                        // wording so the operator can read what just
                        // happened during the fade. Brief delay before
                        // the fade starts so the new text registers
                        // before the banner disappears.
                        var textEl = bar.querySelector('.bowire-alert-bar-text');
                        if (textEl && opts.inlineToggle.dismissText) {
                            textEl.textContent = opts.inlineToggle.dismissText;
                        }
                        setTimeout(function () { _fadeOutAlertBar(bar); }, 520);
                    }
                }
            },
                el('span', {
                    className: 'bowire-settings-toggle-dot',
                    style: iv ? 'transform:translateX(16px)' : ''
                })
            );
            bar.appendChild(toggle);
        }
        // Close affordance. Three shapes:
        //  - dismissKey only → single X (session-only).
        //  - permanentDismissKey only → split control: X dismisses for
        //    the session, chevron opens a tiny menu with "Don't show
        //    again" which dismisses permanently and stamps the entry in
        //    the dismissed-hints store so Settings can restore it.
        //  - both set → permanent wins as the bar's identity. Both
        //    actions write under the SAME primaryKey (the permanent
        //    one), just with different scope, so the next render-time
        //    isHintDismissed(primaryKey) check actually sees the
        //    session dismiss too. Without this the X writes one key
        //    while the render-check reads another and the bar stays
        //    mounted across renders.
        if (opts.dismissKey || opts.permanentDismissKey) {
            var sessionKey = primaryKey;
            var permKey = opts.permanentDismissKey;
            var closeWrap = el('div', { className: 'bowire-alert-bar-close-wrap' });
            closeWrap.appendChild(el('button', {
                type: 'button',
                className: 'bowire-alert-bar-close',
                title: 'Hide for this session',
                'aria-label': 'Hide for this session',
                innerHTML: svgIcon('close'),
                onClick: function (e) {
                    // Stop the click from bubbling to any ancestor
                    // handler that would re-render the panel — a
                    // render() between the click and the CSS fade
                    // would tear the bar out via morphdom diff
                    // before the dismissing class transitions, and
                    // the dismiss would look like a snap.
                    if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
                    if (typeof dismissHint === 'function') {
                        dismissHint(sessionKey, 'session', opts.dismissLabel);
                    } else {
                        try { sessionStorage.setItem(sessionKey, '1'); } catch { /* ignore */ }
                    }
                    _fadeOutAlertBar(bar);
                }
            }));
            if (permKey) {
                var menuOpen = false;
                var menu = el('div', {
                    className: 'bowire-alert-bar-close-menu',
                    role: 'menu',
                    style: 'display:none'
                });
                menu.appendChild(el('button', {
                    type: 'button',
                    role: 'menuitem',
                    className: 'bowire-alert-bar-close-menu-item',
                    textContent: "Don't show again",
                    onClick: function (e) {
                        if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
                        if (typeof dismissHint === 'function') {
                            dismissHint(permKey, 'permanent', opts.dismissLabel);
                        } else {
                            try { localStorage.setItem(permKey, '1'); } catch { /* ignore */ }
                        }
                        _fadeOutAlertBar(bar);
                    }
                }));
                var chevron = el('button', {
                    type: 'button',
                    className: 'bowire-alert-bar-close-chevron',
                    title: 'More dismiss options',
                    'aria-label': 'More dismiss options',
                    'aria-haspopup': 'menu',
                    'aria-expanded': 'false',
                    innerHTML: svgIcon('chevronDown'),
                    onClick: function (e) {
                        if (e && typeof e.stopPropagation === 'function') e.stopPropagation();
                        menuOpen = !menuOpen;
                        menu.style.display = menuOpen ? '' : 'none';
                        chevron.setAttribute('aria-expanded', menuOpen ? 'true' : 'false');
                    }
                });
                closeWrap.appendChild(chevron);
                closeWrap.appendChild(menu);
            }
            bar.appendChild(closeWrap);
        }
        return bar;
    }

    // Smooth-fade helper for renderAlertBar. CSS handles the actual
    // opacity + max-height animation; this just sets the dismissing
    // class and removes the node when the transition ends, falling
    // back to a timeout so a missing transitionend (e.g. reduced-
    // motion users) doesn't leave the node stuck in the DOM.
    // #166 — single-source-of-truth list of keyboard shortcuts in the
    // workbench. Adding a new binding means adding it here AND to the
    // handler in init.js — the sheet is the public face, the handler
    // is the wiring. Grouped categories so the sheet reads as
    // "here's what you can do" instead of an alphabetised dump.
    var BOWIRE_KEYBINDINGS = [
        { group: 'Execution', binds: [
            { keys: ['Ctrl/Cmd', 'Enter'], action: 'Execute current method' },
            { keys: ['Ctrl/Cmd', 'S'], action: 'Save / flush all pending writes' },
            { keys: ['R'], action: 'Repeat the last call' },
        ]},
        { group: 'History', binds: [
            { keys: ['Ctrl/Cmd', 'Z'], action: 'Undo the most recent reversible action' },
            { keys: ['Ctrl/Cmd', 'Shift', 'Z'], action: 'Redo the last undone action' },
        ]},
        { group: 'Navigation', binds: [
            { keys: ['Ctrl/Cmd', 'K'], action: 'Open the command palette' },
            { keys: ['Ctrl/Cmd', 'T'], action: 'New freeform request tab' },
            { keys: ['Ctrl/Cmd', 'L'], action: 'Open Hoppscotch-style request bar (#289)' },
            { keys: ['Ctrl/Cmd', 'W'], action: 'Close the active request tab' },
            { keys: ['Ctrl/Cmd', '1-9'], action: 'Switch to tab N' },
            { keys: ['Ctrl', 'Tab'], action: 'Cycle to the next request tab' },
            { keys: ['Ctrl', 'Shift', 'Tab'], action: 'Cycle to the previous request tab' },
        ]},
        { group: 'Workspace', binds: [
            { keys: ['Ctrl/Cmd', 'Shift', 'U'], action: 'Add a URL or schema to the active workspace' },
        ]},
        { group: 'Drawers + layout', binds: [
            { keys: ['Ctrl/Cmd', 'B'], action: 'Toggle the sidebar' },
            { keys: ['F1'], action: 'Open the Help drawer (contextual)' },
            { keys: ['Ctrl/Cmd', 'Shift', 'A'], action: 'Toggle the Assistant drawer' },
            { keys: ['Ctrl/Cmd', 'Alt', '\\'], action: 'Toggle horizontal / vertical split' },
        ]},
        { group: 'System', binds: [
            { keys: ['Ctrl/Cmd', '/'], action: 'Show this shortcut sheet' },
            { keys: ['Esc'], action: 'Close any overlay; stop active streaming; disconnect channel' },
        ]},
    ];

    function renderShortcutSheet() {
        if (typeof shortcutSheetOpen === 'undefined' || !shortcutSheetOpen) return null;
        var modal = el('div', {
            className: 'bowire-shortcut-modal',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': 'Keyboard shortcuts'
        });
        var header = el('div', { className: 'bowire-shortcut-header' });
        header.appendChild(el('span', {
            className: 'bowire-shortcut-title',
            textContent: 'Keyboard shortcuts'
        }));
        header.appendChild(el('button', {
            type: 'button',
            className: 'bowire-drawer-close bowire-shortcut-close',
            title: 'Close (Esc or Ctrl/Cmd+/)',
            'aria-label': 'Close',
            innerHTML: svgIcon('close'),
            onClick: function () {
                shortcutSheetOpen = false;
                render();
            }
        }));
        modal.appendChild(header);

        var body = el('div', { className: 'bowire-shortcut-body' });
        BOWIRE_KEYBINDINGS.forEach(function (g) {
            var section = el('div', { className: 'bowire-shortcut-section' });
            section.appendChild(el('div', {
                className: 'bowire-shortcut-section-title',
                textContent: g.group
            }));
            var rows = el('div', { className: 'bowire-shortcut-rows' });
            g.binds.forEach(function (b) {
                var row = el('div', { className: 'bowire-shortcut-row' });
                var chord = el('span', { className: 'bowire-shortcut-chord' });
                b.keys.forEach(function (k, i) {
                    if (i > 0) chord.appendChild(el('span', {
                        className: 'bowire-shortcut-chord-plus', textContent: '+'
                    }));
                    chord.appendChild(el('kbd', {
                        className: 'bowire-shortcut-key', textContent: k
                    }));
                });
                row.appendChild(chord);
                row.appendChild(el('span', {
                    className: 'bowire-shortcut-action', textContent: b.action
                }));
                rows.appendChild(row);
            });
            section.appendChild(rows);
            body.appendChild(section);
        });
        modal.appendChild(body);

        var backdrop = el('div', {
            className: 'bowire-shortcut-backdrop',
            onClick: function (e) {
                if (e.target === e.currentTarget) {
                    shortcutSheetOpen = false;
                    render();
                }
            }
        });
        backdrop.appendChild(modal);
        return backdrop;
    }

    function _fadeOutAlertBar(bar) {
        if (!bar || bar.classList.contains('bowire-alert-bar-dismissing')) return;
        bar.classList.add('bowire-alert-bar-dismissing');
        var done = false;
        function finish() {
            if (done) return;
            done = true;
            if (bar.parentNode) bar.parentNode.removeChild(bar);
            // After the fade completes, re-render so badges and
            // counters that reflect dismissable state (topbar AI
            // hint badge, &c) pick up the new count. Done AFTER
            // the fade so the dismissing-class transition runs to
            // completion before morphdom touches the subtree.
            if (typeof render === 'function') {
                try { render(); } catch { /* ignore */ }
            }
        }
        bar.addEventListener('transitionend', finish);
        // Fallback timeout slightly longer than the CSS transition
        // (520 ms) so a missing transitionend (reduced-motion, page
        // backgrounded mid-fade, &c) still cleans up the node.
        setTimeout(finish, 900);
    }

    function renderEmptyCard(opts) {
        opts = opts || {};
        var card = el('div', { className: 'bowire-empty-card' });

        // Large centred icon. Neutral by default; callers can pass an
        // icon id that matches the pane's identity (recording, mock,
        // collection, etc.) for stronger semantic anchoring.
        var iconEl = el('div', { className: 'bowire-empty-card-icon' });
        if (opts.icon && typeof svgIcon === 'function') {
            iconEl.innerHTML = svgIcon(opts.icon);
        } else {
            iconEl.textContent = '•';
        }
        card.appendChild(iconEl);

        if (opts.headline) {
            card.appendChild(el('h3', {
                className: 'bowire-empty-card-headline',
                textContent: opts.headline,
            }));
        }

        // Hint visibility — show by default; suppress when the user
        // has previously dismissed it. Routed through the central
        // hint-dismiss store so Settings can restore it (#169).
        var hintDismissed = false;
        if (opts.hintKey) {
            if (typeof isHintDismissed === 'function') {
                hintDismissed = isHintDismissed(opts.hintKey);
            } else {
                try { hintDismissed = localStorage.getItem(opts.hintKey) === '1'; } catch { /* ignore */ }
            }
        }
        if (opts.body && !hintDismissed) {
            var bodyEl = el('p', { className: 'bowire-empty-card-body', textContent: opts.body });
            card.appendChild(bodyEl);
            if (opts.hintKey) {
                var hintLabel = opts.hintLabel || opts.headline || opts.hintKey;
                var dismiss = el('button', {
                    type: 'button',
                    className: 'bowire-empty-card-dismiss',
                    textContent: 'Got it — don’t show again',
                    onClick: function () {
                        if (typeof dismissHint === 'function') {
                            dismissHint(opts.hintKey, 'permanent', hintLabel);
                        } else {
                            try { localStorage.setItem(opts.hintKey, '1'); } catch { /* ignore */ }
                        }
                        bodyEl.remove();
                        dismiss.remove();
                    }
                });
                card.appendChild(dismiss);
            }
        }

        var actions = Array.isArray(opts.actions) ? opts.actions : [];
        if (actions.length > 0) {
            var actionsRow = el('div', { className: 'bowire-empty-card-actions' });
            actions.forEach(function (a) {
                if (!a) return;
                actionsRow.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-empty-card-action' + (a.primary ? ' bowire-empty-card-action-primary' : ''),
                    onClick: a.onClick,
                    textContent: a.label,
                }));
            });
            card.appendChild(actionsRow);
        }

        return card;
    }

    /**
     * Render a tree (#192) — MudBlazor TreeView / NavMenu shape. Used
     * for the Workspaces rail (and later Settings) so navigation reads
     * as "workspace → sources → URL" instead of "rail → flat list →
     * detail pane".
     *
     * Node shape:
     *   {
     *     id:        stable string (used as expansion key)
     *     icon:      optional svg-icon id from the catalog
     *     label:     row text
     *     badge:     optional small right-aligned count/label
     *     accent:    optional CSS color for the leading dot (workspace
     *                color, env tint, …). Mutually exclusive with icon.
     *     active:    bold + accent-tinted (workspace is currently active)
     *     selected:  highlighted (the currently focused node)
     *     expandable: boolean — chevron renders only if true
     *     expanded:  boolean — initial open state. Caller owns persistence.
     *     onClick:   primary action. Tree itself never re-renders; the
     *                caller decides what selection means.
     *     onToggle:  fires when the chevron is clicked. Use this to
     *                flip expanded state in the caller's store and call
     *                render(). If absent, onClick is also used to toggle.
     *     onAdd:     optional — renders a hover-visible '+' on the row.
     *     onContext: optional — right-click handler (rename / delete / …).
     *     children:  array of nested nodes
     *   }
     *
     * opts:
     *   ariaLabel: accessibility label for the tree role
     *
     * Returns a DOM node the caller appendChilds into the sidebar.
     */
    // Shared header row for the mode-sidebars (Mocks, Benchmarks,
    // Flows, etc.). One title on the left + N action buttons on the
    // right. Every action renders with the .bowire-env-add-btn shape
    // so the rail-mode sidebars all carry an identical control strip
    // instead of each surface hand-rolling its own header.
    //
    // opts.title: string shown left
    // opts.actions: [{ title, ariaLabel, icon, label, onClick }, …]
    //   icon → svg glyph, label → text (use either, not both). Null
    //   entries are skipped so callers can express conditional actions
    //   inline (`condition ? {…} : null`).
    // Unified sidebar toolbar — same shape across every rail's
    // sidebar so operators don't have to relearn the affordances per
    // mode. Two states:
    //   - default: title + primary action (accent button) +
    //     secondary icon actions + optional overflow ⋮ menu
    //   - selection: count + bulk-action icons + clear (×)
    // Selection-mode is offered when opts.selection is set with a
    // non-zero count; the caller still owns the selection store.
    //
    // opts.title          : section heading (string)
    // opts.primary        : { icon, label, title, onClick } — accent button
    // opts.actions        : [{ icon, title, onClick, danger?, disabled? }, ...]
    // opts.overflow       : [{ label, icon?, onClick, danger?, separator? }, ...]
    // opts.selection      : { count, actions:[{...}], onClear }
    function renderSidebarToolbar(opts) {
        opts = opts || {};
        var sel = opts.selection;
        if (sel && sel.count > 0) {
            var selRow = el('div', { className: 'bowire-sidebar-toolbar is-selection' });
            selRow.appendChild(el('span', {
                className: 'bowire-sidebar-toolbar-count',
                textContent: sel.count + ' selected'
            }));
            selRow.appendChild(el('span', { className: 'bowire-sidebar-toolbar-spacer' }));
            (sel.actions || []).forEach(function (a) {
                if (!a) return;
                selRow.appendChild(_renderToolbarIconBtn(a));
            });
            selRow.appendChild(_renderToolbarIconBtn({
                icon: 'close',
                title: 'Clear selection (Esc)',
                onClick: sel.onClear || function () {}
            }));
            return selRow;
        }

        var row = el('div', { className: 'bowire-sidebar-toolbar' });
        if (typeof opts.onTitleClick === 'function') {
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-sidebar-toolbar-title bowire-sidebar-toolbar-title-link',
                title: opts.titleClickTitle || 'Open ' + (opts.title || '') + ' overview',
                'aria-label': opts.titleClickTitle || 'Open ' + (opts.title || '') + ' overview',
                textContent: opts.title || '',
                onClick: opts.onTitleClick
            }));
        } else {
            row.appendChild(el('span', { className: 'bowire-sidebar-toolbar-title', textContent: opts.title || '' }));
        }
        row.appendChild(el('span', { className: 'bowire-sidebar-toolbar-spacer' }));
        if (opts.primary) {
            var p = opts.primary;
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-sidebar-toolbar-primary',
                title: p.title || p.label || 'Add',
                'aria-label': p.title || p.label || 'Add',
                disabled: p.disabled ? true : undefined,
                onClick: p.onClick
            },
                p.icon ? el('span', { innerHTML: svgIcon(p.icon) }) : null,
                p.label ? el('span', { textContent: p.label }) : null
            ));
        }
        (opts.actions || []).forEach(function (a) {
            if (!a) return;
            row.appendChild(_renderToolbarIconBtn(a));
        });
        if (Array.isArray(opts.overflow) && opts.overflow.length > 0) {
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-sidebar-toolbar-btn',
                title: 'More actions',
                'aria-label': 'More actions',
                'aria-haspopup': 'menu',
                onClick: function (e) {
                    e.stopPropagation();
                    showContextMenu(e.clientX, e.clientY, opts.overflow);
                }
            }, el('span', { innerHTML: svgIcon('dots') })));
        }

        // #276 — Optional second-row strip below the toolbar for
        // search + sort. When opts.search or opts.sort is set, the
        // function returns a wrapper containing the title row + the
        // filter row so callers can still append it as a single
        // sidebar child. opts.search: { value, placeholder?, onInput }.
        // opts.sort: { value, options: [{value,label}], onChange }.
        if (opts.search || opts.sort) {
            var wrap = el('div', { className: 'bowire-sidebar-toolbar-wrap' });
            wrap.appendChild(row);
            var filterRow = el('div', { className: 'bowire-sidebar-filterbar' });
            if (opts.search) {
                var searchHost = el('label', {
                    className: 'bowire-sidebar-filterbar-search'
                });
                searchHost.appendChild(el('span', {
                    className: 'bowire-sidebar-filterbar-search-icon',
                    innerHTML: svgIcon('search'),
                    'aria-hidden': 'true'
                }));
                searchHost.appendChild(el('input', {
                    type: 'search',
                    className: 'bowire-sidebar-filterbar-search-input',
                    placeholder: opts.search.placeholder || 'Search…',
                    value: opts.search.value || '',
                    autocomplete: 'off',
                    spellcheck: 'false',
                    // Search-palette: opt out of var-chip / autocomplete
                    // overlays so the field stays a plain filter.
                    'data-bowire-no-vars-chip': '1',
                    'data-bowire-no-vars-ac': '1',
                    onInput: function (e) {
                        if (typeof opts.search.onInput === 'function') {
                            opts.search.onInput(e.target.value);
                        }
                    }
                }));
                filterRow.appendChild(searchHost);
            }
            if (opts.sort && Array.isArray(opts.sort.options) && opts.sort.options.length > 0) {
                // Two render paths: cycling icon-button (when every
                // option carries an .icon) or the legacy native-select
                // fallback. The icon-button advances the mode on each
                // click — current-mode glyph + tooltip name the active
                // mode AND the next-on-click mode so the operator
                // doesn't have to memorise the cycle order.
                var allHaveIcons = opts.sort.options.every(function (o) { return o.icon; });
                if (allHaveIcons) {
                    var curIdx = 0;
                    for (var i = 0; i < opts.sort.options.length; i++) {
                        if (opts.sort.options[i].value === opts.sort.value) { curIdx = i; break; }
                    }
                    var curOpt = opts.sort.options[curIdx];
                    var nextOpt = opts.sort.options[(curIdx + 1) % opts.sort.options.length];
                    var sortBtn = el('button', {
                        type: 'button',
                        className: 'bowire-sidebar-filterbar-sort-btn',
                        title: (opts.sort.title || 'Sort') + ': ' + curOpt.label
                            + ' — click for ' + nextOpt.label,
                        'aria-label': (opts.sort.title || 'Sort') + ': ' + curOpt.label,
                        innerHTML: svgIcon(curOpt.icon),
                        onClick: function () {
                            if (typeof opts.sort.onChange === 'function') {
                                opts.sort.onChange(nextOpt.value);
                            }
                        }
                    });
                    filterRow.appendChild(sortBtn);
                } else {
                    var sortHost = el('label', {
                        className: 'bowire-sidebar-filterbar-sort',
                        title: opts.sort.title || 'Sort'
                    });
                    var sortSel = el('select', {
                        className: 'bowire-sidebar-filterbar-sort-select',
                        'aria-label': opts.sort.title || 'Sort',
                        onChange: function (e) {
                            if (typeof opts.sort.onChange === 'function') {
                                opts.sort.onChange(e.target.value);
                            }
                        }
                    });
                    opts.sort.options.forEach(function (o) {
                        var optEl = el('option', {
                            value: o.value,
                            textContent: o.label
                        });
                        if (o.value === opts.sort.value) optEl.selected = true;
                        sortSel.appendChild(optEl);
                    });
                    sortHost.appendChild(sortSel);
                    filterRow.appendChild(sortHost);
                }
            }
            wrap.appendChild(filterRow);
            return wrap;
        }

        return row;
    }
    function _renderToolbarIconBtn(a) {
        return el('button', {
            type: 'button',
            className: 'bowire-sidebar-toolbar-btn'
                + (a.danger ? ' is-danger' : '')
                + (a.active ? ' is-active' : ''),
            title: a.title,
            'aria-label': a.ariaLabel || a.title,
            disabled: a.disabled ? true : undefined,
            onClick: a.onClick
        }, a.icon ? el('span', { innerHTML: svgIcon(a.icon) }) : el('span', { textContent: a.label || '?' }));
    }

    function renderSidebarHeader(opts) {
        opts = opts || {};
        var row = el('div', { className: 'bowire-env-list-header' });
        if (typeof opts.onTitleClick === 'function') {
            // Title becomes a real button → operator can click the
            // section heading to navigate to that section's overview
            // (workspaces tree → Workspaces overview).
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-env-list-header-title-link',
                title: opts.titleClickTitle || ('Open ' + (opts.title || '') + ' overview'),
                'aria-label': opts.titleClickTitle || ('Open ' + (opts.title || '') + ' overview'),
                textContent: opts.title || '',
                onClick: opts.onTitleClick
            }));
        } else {
            row.appendChild(el('span', { textContent: opts.title || '' }));
        }
        (opts.actions || []).forEach(function (a) {
            if (!a) return;
            var attrs = {
                className: 'bowire-env-add-btn',
                title: a.title,
                'aria-label': a.ariaLabel || a.title,
                onClick: a.onClick
            };
            if (a.icon) attrs.innerHTML = svgIcon(a.icon);
            else if (a.label) attrs.textContent = a.label;
            row.appendChild(el('button', attrs));
        });
        return row;
    }

    // Shared list-item shape for the mode-sidebars. accent dot or icon
    // up front, name in the middle, optional meta on the right, optional
    // hover-delete affordance after that. Click selects the row;
    // onDelete (if given) fires without bubbling to the row click.
    //
    // opts.id: stable DOM id so morphdom keys by entity, not position
    // opts.icon: glyph for the lead slot (mutually exclusive with accent)
    // opts.accent: CSS color for the dot in the lead slot
    // opts.name: row label
    // opts.meta: short trailing meta string
    // opts.selected: boolean → adds the .selected modifier
    // opts.onClick: row-level click handler
    // opts.onDelete: optional delete handler — renders a trash button
    // opts.deleteTitle: tooltip + aria-label for the delete button
    // ---- Context menu ----
    // Floating panel triggered on right-click. Items: { label, onClick,
    // disabled?, danger?, separator? }. The first non-separator click
    // dismisses + dispatches; any outside-click dismisses without firing
    // anything. Multiple open menus replace each other so the
    // last-opened wins. Mounted directly on document.body so a sidebar
    // overflow-hidden parent doesn't clip it.
    var _bowireOpenContextMenu = null;
    function _closeContextMenu() {
        if (_bowireOpenContextMenu && _bowireOpenContextMenu.parentNode) {
            _bowireOpenContextMenu.parentNode.removeChild(_bowireOpenContextMenu);
        }
        _bowireOpenContextMenu = null;
        document.removeEventListener('click', _closeContextMenu, true);
        document.removeEventListener('contextmenu', _onDocContextMenu, true);
        document.removeEventListener('keydown', _onMenuKey, true);
    }
    function _onDocContextMenu(e) {
        // Don't close on right-clicks that would themselves spawn a
        // fresh menu — the new openContextMenu call already cleans up
        // the previous one before mounting.
        _closeContextMenu();
    }
    function _onMenuKey(e) {
        if (e.key === 'Escape') _closeContextMenu();
    }
    function showContextMenu(clientX, clientY, items) {
        if (!Array.isArray(items) || items.length === 0) return;
        _closeContextMenu();
        var menu = document.createElement('div');
        menu.className = 'bowire-context-menu';
        menu.style.position = 'fixed';
        // Pin off-screen first; we measure + reposition after mount so
        // the menu never clips the viewport.
        menu.style.left = '-9999px';
        menu.style.top = '-9999px';
        items.forEach(function (item) {
            if (item.separator) {
                var sep = document.createElement('div');
                sep.className = 'bowire-context-menu-separator';
                menu.appendChild(sep);
                return;
            }
            var row = document.createElement('button');
            row.type = 'button';
            row.className = 'bowire-context-menu-item'
                + (item.danger ? ' danger' : '')
                + (item.disabled ? ' disabled' : '')
                + (item.icon || item.meta || item.indicator ? ' bowire-context-menu-item-rich' : '');
            // Tooltip only on rich items (icon/meta/explicit title) so
            // the truncated label still surfaces the full text. Plain
            // text items skip the tooltip to avoid redundancy.
            if (item.title) row.title = item.title;
            else if (item.icon || item.meta || item.indicator) row.title = item.label || '';
            if (item.icon) {
                var iconEl = document.createElement('span');
                iconEl.className = 'bowire-context-menu-item-icon';
                iconEl.innerHTML = svgIcon(item.icon);
                row.appendChild(iconEl);
            }
            if (item.indicator) {
                var ind = document.createElement('span');
                ind.className = 'bowire-context-menu-item-indicator is-' + item.indicator;
                ind.setAttribute('aria-label', item.indicator);
                row.appendChild(ind);
            }
            var lbl = document.createElement('span');
            lbl.className = 'bowire-context-menu-item-label';
            lbl.textContent = item.label;
            row.appendChild(lbl);
            if (item.meta) {
                var meta = document.createElement('span');
                meta.className = 'bowire-context-menu-item-meta';
                meta.textContent = item.meta;
                row.appendChild(meta);
            }
            if (item.disabled) row.setAttribute('disabled', 'disabled');
            row.onclick = function (ev) {
                ev.stopPropagation();
                if (item.disabled) return;
                _closeContextMenu();
                try { item.onClick(ev); } catch (e) { console.warn('[bowire] context-menu handler failed', e); }
            };
            menu.appendChild(row);
        });
        document.body.appendChild(menu);
        // Reposition so the menu fits the viewport.
        var rect = menu.getBoundingClientRect();
        var vw = window.innerWidth;
        var vh = window.innerHeight;
        var left = Math.min(clientX, vw - rect.width - 4);
        var top = Math.min(clientY, vh - rect.height - 4);
        if (left < 4) left = 4;
        if (top < 4) top = 4;
        menu.style.left = left + 'px';
        menu.style.top = top + 'px';
        _bowireOpenContextMenu = menu;
        // Defer the dismiss-listener install by one tick so the very
        // click that opened the menu doesn't immediately close it.
        setTimeout(function () {
            document.addEventListener('click', _closeContextMenu, true);
            document.addEventListener('contextmenu', _onDocContextMenu, true);
            document.addEventListener('keydown', _onMenuKey, true);
        }, 0);
    }

    function renderSidebarListItem(opts) {
        opts = opts || {};
        var rowAttrs = {
            id: opts.id,
            className: 'bowire-env-list-item'
                + (opts.active ? ' active' : '')
                + (opts.selected ? ' selected' : ''),
            onClick: opts.onClick
        };
        if (typeof opts.onContextMenu === 'function') {
            rowAttrs.onContextMenu = opts.onContextMenu;
        }
        var row = el('div', rowAttrs);
        if (opts.icon) {
            row.appendChild(el('span', { className: 'bowire-env-sidebar-icon', innerHTML: svgIcon(opts.icon) }));
        } else if (opts.accent) {
            row.appendChild(el('span', {
                className: 'bowire-env-color-dot' + (opts.pulse ? ' is-pulsing' : ''),
                style: 'background:' + opts.accent
            }));
        }
        row.appendChild(el('span', { className: 'bowire-env-list-item-name', textContent: opts.name || '' }));
        row.appendChild(el('span', { style: 'flex:1' }));
        if (opts.meta) {
            row.appendChild(el('span', { className: 'bowire-env-list-item-meta', textContent: opts.meta }));
        }
        if (typeof opts.onDelete === 'function') {
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-list-row-delete',
                title: opts.deleteTitle || 'Delete',
                'aria-label': opts.deleteTitle || 'Delete',
                innerHTML: svgIcon('trash'),
                onClick: function (e) {
                    e.stopPropagation();
                    opts.onDelete(e);
                }
            }));
        }
        return row;
    }

    // Per-protocol method-type capability table. Used by the freeform
    // request builder + the "+" new-request menu so the type dropdown
    // only offers shapes the protocol actually supports — e.g. picking
    // REST doesn't surface ClientStreaming, picking MQTT only offers
    // the Duplex channel shape, etc. Order = sensible default first.
    function getSupportedMethodTypes(protocolId) {
        switch ((protocolId || '').toLowerCase()) {
            case 'grpc':       return ['Unary', 'ServerStreaming', 'ClientStreaming', 'Duplex'];
            case 'rest':       return ['Unary'];
            case 'graphql':    return ['Unary', 'ServerStreaming'];
            case 'signalr':    return ['Unary', 'ServerStreaming', 'Duplex'];
            case 'mqtt':       return ['Duplex'];
            case 'websocket':  return ['Duplex'];
            case 'sse':        return ['ServerStreaming'];
            case 'socketio':   return ['Duplex'];
            case 'mcp':        return ['Unary', 'ServerStreaming'];
            case 'odata':      return ['Unary'];
            case 'soap':       return ['Unary'];
            case 'jsonrpc':    return ['Unary', 'ServerStreaming'];
            case 'nats':       return ['Unary', 'Duplex'];
            case 'pulsar':     return ['Duplex'];
            default:           return ['Unary', 'ServerStreaming', 'ClientStreaming', 'Duplex'];
        }
    }

    function renderTree(nodes, opts) {
        opts = opts || {};
        var tree = el('div', {
            className: 'bowire-tree',
            role: 'tree',
            'aria-label': opts.ariaLabel || 'Tree'
        });
        (nodes || []).forEach(function (n) {
            tree.appendChild(_renderTreeNode(n, 0));
        });
        return tree;
    }

    function _renderTreeNode(node, depth) {
        if (!node) return document.createTextNode('');

        // Group-header node: non-clickable section label used to
        // visually segment a tree into named groups (e.g. the Settings
        // dialog's "My preferences" vs "This project" split, #193
        // Phase 2 item 4). Renders as a small uppercase chip; no
        // selection state, no drag/drop, no onClick.
        if (node.header) {
            return el('div', {
                className: 'bowire-tree-header',
                role: 'presentation',
                style: 'padding-left:' + (8 + depth * 14) + 'px',
                textContent: node.label || ''
            });
        }

        var hasChildren = Array.isArray(node.children) && node.children.length > 0;
        var isExpandable = !!node.expandable || hasChildren;
        var isExpanded = isExpandable && !!node.expanded;

        var wrap = el('div', {
            className: 'bowire-tree-node' + (isExpanded ? ' expanded' : ''),
            role: 'treeitem',
            'aria-expanded': isExpandable ? (isExpanded ? 'true' : 'false') : null
        });

        var row = el('div', {
            className: 'bowire-tree-row'
                + (node.selected ? ' selected' : '')
                + (node.active ? ' active' : ''),
            style: 'padding-left:' + (8 + depth * 14) + 'px',
            onClick: function (e) {
                if (typeof node.onClick === 'function') node.onClick(e);
            },
            onContextMenu: typeof node.onContext === 'function'
                ? function (e) { e.preventDefault(); node.onContext(e); }
                : null,
            // #192 (B) — drop support. Callers opt in by setting
            // node.onDrop(dataTransfer, event). Row flips to
            // .drop-target while a drag hovers so the operator gets
            // visual feedback before they let go.
            onDragOver: typeof node.onDrop === 'function'
                ? function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
                    row.classList.add('drop-target');
                }
                : null,
            onDragLeave: typeof node.onDrop === 'function'
                ? function () { row.classList.remove('drop-target'); }
                : null,
            onDrop: typeof node.onDrop === 'function'
                ? function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    row.classList.remove('drop-target');
                    node.onDrop(e.dataTransfer, e);
                }
                : null
        });

        // Chevron — only rendered when the row is expandable so leaves
        // align with the parent label, not the parent chevron.
        if (isExpandable) {
            var chevron = el('button', {
                type: 'button',
                className: 'bowire-tree-toggle' + (isExpanded ? ' expanded' : ''),
                'aria-label': isExpanded ? 'Collapse' : 'Expand',
                innerHTML: svgIcon('chevron'),
                onClick: function (e) {
                    e.stopPropagation();
                    if (typeof node.onToggle === 'function') node.onToggle(e);
                    else if (typeof node.onClick === 'function') node.onClick(e);
                }
            });
            row.appendChild(chevron);
        } else {
            row.appendChild(el('span', { className: 'bowire-tree-toggle bowire-tree-toggle-spacer' }));
        }

        // Leading glyph: an svg icon from the catalog (node.icon),
        // raw inline SVG markup (node.iconHtml — for plugin icons),
        // or an accent dot (node.accent). Lines up even when only
        // some siblings have one.
        var glyph = el('span', { className: 'bowire-tree-glyph' });
        if (node.iconHtml) {
            glyph.innerHTML = node.iconHtml;
            glyph.classList.add('bowire-tree-glyph-icon');
        } else if (node.icon) {
            glyph.innerHTML = svgIcon(node.icon);
            glyph.classList.add('bowire-tree-glyph-icon');
        } else if (node.accent) {
            var dot = el('span', { className: 'bowire-tree-glyph-dot' });
            dot.style.background = node.accent;
            glyph.appendChild(dot);
        }
        row.appendChild(glyph);

        row.appendChild(el('span', {
            className: 'bowire-tree-label',
            textContent: node.label || '',
            title: node.title || node.label || ''
        }));

        if (node.badge !== undefined && node.badge !== null && node.badge !== '') {
            row.appendChild(el('span', {
                className: 'bowire-tree-badge',
                textContent: String(node.badge)
            }));
        }

        // #276 — Per-row hover-revealed tools. Rendered before the
        // `+` add button so the "add to this group" affordance stays
        // anchored at the row's far right (operator's muscle memory).
        // Each tool: { icon, title, danger?, disabled?, onClick }.
        // null/undefined entries are silently dropped so callers can
        // use inline conditionals (e.g. save-as-template only on the
        // active workspace).
        if (Array.isArray(node.tools) && node.tools.length > 0) {
            node.tools.forEach(function (t) {
                if (!t) return;
                row.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-tree-tool'
                        + (t.danger ? ' bowire-tree-tool-danger' : ''),
                    title: t.title,
                    'aria-label': t.ariaLabel || t.title,
                    disabled: t.disabled ? true : undefined,
                    innerHTML: svgIcon(t.icon),
                    onClick: function (e) {
                        e.stopPropagation();
                        if (typeof t.onClick === 'function') t.onClick(e);
                    }
                }));
            });
        }

        // Rightmost slot: active indicator (always visible) OR add
        // button (hover-revealed). Same DOM position so the active
        // row's checkmark and the inactive row's activate-button
        // line up vertically across the list — operator feedback:
        // the slot must be at the same place whether active or not.
        //
        // node.isActive:                 always-visible filled indicator
        // node.onAdd (no isActive):      hover-revealed action button
        // both:                          renders only the indicator
        //                                (active rows don't need an
        //                                activate-button)
        if (node.isActive) {
            row.appendChild(el('span', {
                className: 'bowire-tree-active-indicator',
                title: node.activeTitle || 'Active',
                innerHTML: svgIcon(node.addIcon || 'check')
            }));
        } else if (typeof node.onAdd === 'function') {
            // node.addIcon lets the caller swap the default 'plus'
            // for a semantically-appropriate glyph. Workspaces use
            // 'check' for 'Switch to this workspace' (#277) — the
            // '+' there read as 'add what?' which confused the
            // operator.
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-tree-add'
                    + (node.addIcon ? ' bowire-tree-add-' + node.addIcon : ''),
                title: node.addTitle || 'Add',
                'aria-label': node.addTitle || 'Add',
                innerHTML: svgIcon(node.addIcon || 'plus'),
                onClick: function (e) {
                    e.stopPropagation();
                    node.onAdd(e);
                }
            }));
        }

        wrap.appendChild(row);

        if (hasChildren && isExpanded) {
            var children = el('div', { className: 'bowire-tree-children', role: 'group' });
            node.children.forEach(function (c) {
                children.appendChild(_renderTreeNode(c, depth + 1));
            });
            wrap.appendChild(children);
        }

        return wrap;
    }

    /**
     * Render a normalized problem object into a DOM container as a
     * structured error card (#88). Headline + collapsible detail + any
     * action links the backend exposed via `links` (e.g. "Configure"
     * pointing at Settings → AI).
     */
    function renderProblem(problem, container) {
        if (!problem || !container) return;
        var card = el('div', { className: 'bowire-problem-card' });

        card.appendChild(el('div', { className: 'bowire-problem-title', textContent: problem.title }));

        if (problem.detail) {
            var details = el('details', { className: 'bowire-problem-details' });
            details.appendChild(el('summary', { textContent: 'Details' }));
            details.appendChild(el('div', { className: 'bowire-problem-detail-body', textContent: problem.detail }));
            card.appendChild(details);
        }

        if (problem.links && problem.links.length > 0) {
            var actions = el('div', { className: 'bowire-problem-actions' });
            for (var i = 0; i < problem.links.length; i++) {
                var lnk = problem.links[i];
                if (!lnk || !lnk.href) continue;
                var label = lnk.rel === 'configure' ? 'Configure'
                          : lnk.rel === 'docs' ? 'Open docs'
                          : lnk.rel === 'retry' ? 'Retry'
                          : (lnk.rel || 'Open');
                actions.appendChild(el('a', {
                    className: 'bowire-problem-action',
                    href: lnk.href,
                    textContent: label
                }));
            }
            if (actions.childNodes.length > 0) card.appendChild(actions);
        }

        container.appendChild(card);
    }

    // Toast notification with optional undo callback.
    // Returns the toast element so callers can add custom content.
    // Options: { undo: function, duration: ms (0 = sticky),
    //           logAction: { kind, title, redo? } }
    //
    // #168 — When logAction is set alongside undo, the action also
    // joins the workbench-wide action log so Ctrl/Cmd+Z works after
    // the toast has expired and the Activity drawer surfaces it.
    function toast(message, type, options) {
        // Wire the action log first so the entry exists even if the
        // toast container creation fails for some reason.
        if (options && options.logAction && typeof options.undo === 'function'
            && typeof recordAction === 'function') {
            recordAction({
                kind: options.logAction.kind || 'unknown',
                title: options.logAction.title || message,
                undo: options.undo,
                redo: options.logAction.redo
            });
        }
        var opts = options || {};
        var container = $('.bowire-toast-container');
        if (!container) {
            container = el('div', {
                className: 'bowire-toast-container',
                role: 'status',
                'aria-live': 'polite'
            });
            document.body.appendChild(container);
        }
        var t = el('div', { className: 'bowire-toast ' + (type || 'info') });
        t.appendChild(el('span', { className: 'bowire-toast-message', textContent: message }));

        if (opts.undo && typeof opts.undo === 'function') {
            t.appendChild(el('button', {
                className: 'bowire-toast-undo',
                textContent: 'Undo',
                onClick: function (e) {
                    e.stopPropagation();
                    opts.undo();
                    dismissToast(t);
                }
            }));
        }
        // Generic action slot — pass {action: {label, onClick}} for a
        // toast that confirms a state change AND deep-links into the
        // relevant settings or surface ("Invocation enabled" → "Open
        // Settings").
        if (opts.action && typeof opts.action.onClick === 'function') {
            t.appendChild(el('button', {
                className: 'bowire-toast-undo',
                textContent: opts.action.label || 'Open',
                onClick: function (e) {
                    e.stopPropagation();
                    opts.action.onClick();
                    dismissToast(t);
                }
            }));
        }

        t.appendChild(el('button', {
            className: 'bowire-toast-close',
            innerHTML: svgIcon('close'),
            'aria-label': 'Dismiss',
            onClick: function (e) { e.stopPropagation(); dismissToast(t); }
        }));

        container.appendChild(t);
        var duration = opts.duration !== undefined ? opts.duration : 4000;
        if (duration > 0) {
            t._timeout = setTimeout(function () { dismissToast(t); }, duration);
        }
        return t;
    }

    function dismissToast(t) {
        if (t._dismissed) return;
        t._dismissed = true;
        if (t._timeout) clearTimeout(t._timeout);
        t.classList.add('bowire-toast-out');
        setTimeout(function () { t.remove(); }, 200);
    }

    // Custom confirm dialog — replaces native confirm().
    // message: string, onConfirm: function, options: { title, danger, confirmText, cancelText }
    function bowireConfirm(message, onConfirm, options) {
        var opts = options || {};
        var existing = document.querySelector('.bowire-confirm-overlay');
        if (existing) existing.remove();

        var confirmBtn = el('button', {
            className: 'bowire-confirm-btn' + (opts.danger ? ' danger' : ''),
            textContent: opts.confirmText || 'Confirm',
            'aria-label': opts.confirmText || 'Confirm',
            onClick: function () { overlay.remove(); if (onConfirm) onConfirm(); }
        });
        var cancelBtn = el('button', {
            className: 'bowire-confirm-btn cancel',
            textContent: opts.cancelText || 'Cancel',
            'aria-label': 'Cancel',
            onClick: function () { overlay.remove(); }
        });

        var dialog = el('div', {
            className: 'bowire-confirm-dialog',
            role: 'alertdialog',
            'aria-modal': 'true',
            'aria-labelledby': 'bowire-confirm-title'
        },
            opts.title ? el('div', { id: 'bowire-confirm-title', className: 'bowire-confirm-title', textContent: opts.title }) : null,
            el('div', { className: 'bowire-confirm-message', textContent: message }),
            el('div', { className: 'bowire-confirm-actions' }, cancelBtn, confirmBtn)
        );

        var overlay = el('div', {
            className: 'bowire-confirm-overlay',
            onClick: function (e) { if (e.target === overlay) overlay.remove(); }
        }, dialog);

        document.body.appendChild(overlay);
        // Focus the confirm button so Enter activates it
        confirmBtn.focus();
        // Esc to close
        overlay.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { overlay.remove(); }
        });
    }

    // In-app prompt dialog — mirrors bowireConfirm's shape + theme
    // so callers don't reach for browser-native window.prompt(),
    // which doesn't match Bowire's theme and isn't available at
    // all under some embedded hosts. Resolves the promise with the
    // entered string (trimmed) or null when the operator cancels.
    function bowirePrompt(message, options) {
        var opts = options || {};
        return new Promise(function (resolve) {
            var existing = document.querySelector('.bowire-confirm-overlay');
            if (existing) existing.remove();

            var input = el('input', {
                type: 'text',
                className: 'bowire-prompt-input',
                value: opts.defaultValue || '',
                placeholder: opts.placeholder || '',
                'aria-label': message,
                // #152 v3 — opt out of the vars-chip overlay + the
                // vars-autocomplete dropdown. bowirePrompt is meta-
                // UI (collects a name / URL / id); without this the
                // chip-overlay's color:transparent rule was hiding
                // the typed text and the operator saw only what the
                // overlay had rendered.
                'data-bowire-no-vars-chip': '1',
                'data-bowire-no-vars-ac': '1',
            });

            function commit() {
                var val = String(input.value || '').trim();
                // opts.validator(value) → falsy (null/undefined/empty) means
                // accept; a string return is treated as the rejection reason.
                // The dialog stays open, the input flashes red, and the
                // caller's toast (if any) tells the operator what went
                // wrong. Pattern mirrors the workspace create dialog's
                // duplicate-name handling.
                if (typeof opts.validator === 'function') {
                    var err = opts.validator(val);
                    if (err) {
                        input.focus();
                        input.classList.add('bowire-prompt-input-error');
                        setTimeout(function () {
                            input.classList.remove('bowire-prompt-input-error');
                        }, 600);
                        return;
                    }
                }
                overlay.remove();
                resolve(val || null);
            }

            var confirmBtn = el('button', {
                className: 'bowire-confirm-btn' + (opts.danger ? ' danger' : ''),
                textContent: opts.confirmText || 'OK',
                'aria-label': opts.confirmText || 'OK',
                onClick: commit,
            });
            var cancelBtn = el('button', {
                className: 'bowire-confirm-btn cancel',
                textContent: opts.cancelText || 'Cancel',
                'aria-label': 'Cancel',
                onClick: function () { overlay.remove(); resolve(null); },
            });

            input.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') { e.preventDefault(); commit(); }
                else if (e.key === 'Escape') { e.preventDefault(); overlay.remove(); resolve(null); }
            });

            var dialog = el('div', {
                className: 'bowire-confirm-dialog bowire-prompt-dialog',
                role: 'dialog',
                'aria-modal': 'true',
                'aria-labelledby': 'bowire-prompt-title'
            },
                opts.title ? el('div', { id: 'bowire-prompt-title', className: 'bowire-confirm-title', textContent: opts.title }) : null,
                el('div', { className: 'bowire-confirm-message', textContent: message }),
                input,
                el('div', { className: 'bowire-confirm-actions' }, cancelBtn, confirmBtn)
            );

            var overlay = el('div', {
                className: 'bowire-confirm-overlay',
                onClick: function (e) { if (e.target === overlay) { overlay.remove(); resolve(null); } }
            }, dialog);
            document.body.appendChild(overlay);
            // Focus the input + select existing text so the operator
            // can replace it with one keystroke.
            setTimeout(function () { input.focus(); input.select(); }, 0);
        });
    }

    function svgIcon(name) {
        const icons = {
            search: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="M21 21l-4.35-4.35"/></svg>',
            chevron: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M9 18l6-6-6-6"/></svg>',
            sidebar: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/></svg>',
            play: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>',
            stop: '<svg viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="6" width="12" height="12" rx="1"/></svg>',
            copy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>',
            // Pin glyph — used on the console auto-scroll toggle.
            // Active state ("pinned to tail") flips the colour via
            // the .is-on CSS class on the wrapping button.
            pin: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 17v5"/><path d="M9 10.76V6a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v4.76l2 4.24H7l2-4.24Z"/></svg>',
            sun: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="5"/><path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/></svg>',
            moon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12.79A9 9 0 1111.21 3 7 7 0 0021 12.79z"/></svg>',
            clock: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>',
            calendar: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="18" rx="2" ry="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/></svg>',
            sortAlpha: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M6 4v16M6 4l-3 3M6 4l3 3"/><path d="M13 6h7M13 12h5M13 18h3"/></svg>',
            connect: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M15 7h3a5 5 0 010 10h-3M9 17H6a5 5 0 010-10h3"/><line x1="8" y1="12" x2="16" y2="12"/></svg>',
            disconnect: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M15 7h3a5 5 0 010 10h-3M9 17H6a5 5 0 010-10h3"/><line x1="8" y1="12" x2="16" y2="12" stroke-dasharray="2 2"/></svg>',
            send: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>',
            replay: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M1 4v6h6"/><path d="M3.51 15a9 9 0 102.13-9.36L1 10"/></svg>',
            // Recording rail-mode icon — film camera (svgrepo 513400).
            // Used by the activity rail + any "go to Recordings" entry
            // point. A single recording row uses the filmstrip glyph
            // (below) so the rail-vs-individual-recording distinction
            // reads at a glance.
            recording: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17 9.50019L17.6584 9.17101C19.6042 8.19807 20.5772 7.7116 21.2886 8.15127C22 8.59094 22 9.67872 22 11.8543V12.1461C22 14.3217 22 15.4094 21.2886 15.8491C20.5772 16.2888 19.6042 15.8023 17.6584 14.8294L17 14.5002V9.50019Z"/><path d="M13.5607 7.43934C14.1464 8.02513 14.1464 8.97487 13.5607 9.56066C12.9749 10.1464 12.0251 10.1464 11.4393 9.56066C10.8536 8.97487 10.8536 8.02513 11.4393 7.43934C12.0251 6.85355 12.9749 6.85355 13.5607 7.43934Z"/><path d="M2 11.5C2 8.21252 2 6.56878 2.90796 5.46243C3.07418 5.25989 3.25989 5.07418 3.46243 4.90796C4.56878 4 6.21252 4 9.5 4C12.7875 4 14.4312 4 15.5376 4.90796C15.7401 5.07418 15.9258 5.25989 16.092 5.46243C17 6.56878 17 8.21252 17 11.5V12.5C17 15.7875 17 17.4312 16.092 18.5376C15.9258 18.7401 15.7401 18.9258 15.5376 19.092C14.4312 20 12.7875 20 9.5 20C6.21252 20 4.56878 20 3.46243 19.092C3.25989 18.9258 3.07418 18.7401 2.90796 18.5376C2 17.4312 2 15.7875 2 12.5V11.5Z"/></svg>',
            // Individual-recording glyph — rounded-square film reel
            // with the perforation strip across the top + a play-
            // triangle centred below. Reads as "a single playable
            // recording" so a row in the workspace tree / omnibox /
            // detail header doesn't repeat the rail-mode camera.
            // stroke recoloured to currentColor.
            filmstrip: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" xmlns="http://www.w3.org/2000/svg"><path d="M2 12C2 7.28595 2 4.92893 3.46447 3.46447C4.92893 2 7.28595 2 12 2C16.714 2 19.0711 2 20.5355 3.46447C22 4.92893 22 7.28595 22 12C22 16.714 22 19.0711 20.5355 20.5355C19.0711 22 16.714 22 12 22C7.28595 22 4.92893 22 3.46447 20.5355C2 19.0711 2 16.714 2 12Z"/><path d="M21.5 8H2.5"/><path d="M10.5 2.5L7 8"/><path d="M17 2.5L13.5 8"/><path d="M15 14.5C15 13.8666 14.338 13.4395 13.014 12.5852C11.6719 11.7193 11.0008 11.2863 10.5004 11.6042C10 11.9221 10 12.7814 10 14.5C10 16.2186 10 17.0779 10.5004 17.3958C11.0008 17.7137 11.6719 17.2807 13.014 16.4148C14.338 15.5605 15 15.1334 15 14.5Z"/></svg>',
            console: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/></svg>',
            // #133 — activity-rail icons. Lucide-style line work to
            // match the rest of the topbar / drawer toggles.
            compass: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><polygon points="16.24 7.76 14.12 14.12 7.76 16.24 9.88 9.88 16.24 7.76"/></svg>',
            house: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V21h14V9.5"/><path d="M10 21v-6h4v6"/></svg>',
            folder: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>',
            trash: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6"/><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/></svg>',
            briefcase: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="7" width="20" height="14" rx="2"/><path d="M16 21V5a2 2 0 0 0-2-2h-4a2 2 0 0 0-2 2v16"/></svg>',
            // Lucide-style "boxes" — three stacked / overlapping
            // cubes. Used as the Workspaces glyph (rail mode, palette
            // lane, first-run landing card). Reads as "multiple
            // project containers in one place", which is exactly what
            // the Workspaces rail manages.
            boxes: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2.97 12.92A2 2 0 0 0 2 14.63v3.24a2 2 0 0 0 .97 1.71l3 1.8a2 2 0 0 0 2.06 0L12 19v-5.5l-5-3-4.03 2.42z"/><polyline points="7 16.5 7 19.5"/><polyline points="2 14.5 7 17.5 12 14.5"/><path d="M12 5v8l4 2.5"/><path d="M16 9v8l4 2.5"/><path d="M12 13.5l4-2.5 4 2.5"/><path d="M16 11V6L12 3.5 8 6v5"/></svg>',
            plug: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 2v6M15 2v6M5 8h14v3a7 7 0 0 1-14 0V8zM12 18v4"/></svg>',
            // Broken / disconnected plug — two halves of the same plug
            // glyph drifting apart. Used on the per-URL connection
            // toggle in the discover tree when the URL is offline. Fill
            // (not stroke) shape with currentColor so the same colour
            // logic that drives the connected plug applies here.
            plugOff: '<svg viewBox="0 0 28 28" fill="currentColor"><path d="M22.7399 6.32717C24.3781 8.48282 24.2132 11.571 22.2453 13.5389L20.3007 15.4835C20.0078 15.7764 19.533 15.7764 19.2401 15.4835L12.5226 8.76595C12.2297 8.47306 12.2297 7.99818 12.5226 7.70529L14.4671 5.76075C16.4352 3.79268 19.5237 3.62792 21.6793 5.26646L24.7238 2.22166C25.0167 1.92875 25.4916 1.92873 25.7845 2.22161C26.0774 2.51449 26.0774 2.98936 25.7845 3.28227L22.7399 6.32717ZM19.7704 13.8925L21.1846 12.4783C22.7467 10.9162 22.7467 8.3835 21.1846 6.82141C19.6225 5.25931 17.0899 5.25931 15.5278 6.82141L14.1135 8.23562L19.7704 13.8925Z"/><path d="M12.7778 11.215C13.0707 11.5079 13.0707 11.9828 12.7778 12.2757L10.6514 14.402L13.5982 17.3489L15.7238 15.2234C16.0167 14.9305 16.4916 14.9305 16.7844 15.2234C17.0773 15.5163 17.0773 15.9912 16.7844 16.284L14.6589 18.4095L15.4858 19.2364C15.7787 19.5293 15.7787 20.0042 15.4858 20.2971L13.5412 22.2416C11.5732 24.2096 8.48484 24.3745 6.32918 22.7361L3.28475 25.7808C2.99187 26.0737 2.517 26.0737 2.22409 25.7808C1.93118 25.488 1.93116 25.0131 2.22404 24.7202L5.26853 21.6754C3.63025 19.5197 3.79509 16.4314 5.76306 14.4635L7.7076 12.5189C8.0005 12.226 8.47537 12.226 8.76826 12.5189L9.59072 13.3414L11.7172 11.215C12.0101 10.9221 12.485 10.9221 12.7778 11.215ZM6.83028 21.1875C8.3929 22.7431 10.9207 22.7409 12.4806 21.181L13.8948 19.7668L8.23793 14.1099L6.82372 15.5241C5.26383 17.084 5.26163 19.6117 6.81709 21.1743L6.82366 21.1808L6.83028 21.1875Z"/></svg>',
            history: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 3-6.7"/><polyline points="3 4 3 9 8 9"/><polyline points="12 7 12 12 15 14"/></svg>',
            // #135 split-mode toggle icons. Side-by-side pictogram for
            // horizontal mode, stacked pictogram for vertical mode.
            splitHorizontal: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="1"/><line x1="12" y1="4" x2="12" y2="20"/></svg>',
            splitVertical: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="1"/><line x1="3" y1="12" x2="21" y2="12"/></svg>',
            beaker: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3v6l-5 9a2 2 0 0 0 1.7 3h12.6a2 2 0 0 0 1.7-3l-5-9V3"/><line x1="8" y1="3" x2="16" y2="3"/></svg>',
            server: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>',
            chart: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="20" x2="18" y2="10"/><line x1="12" y1="20" x2="12" y2="4"/><line x1="6" y1="20" x2="6" y2="14"/></svg>',
            lightning: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/></svg>',
            // Lucide-style rocket — nose-cone + body + window + side
            // fins + exhaust flame at the bottom. Used by Home's
            // Start band to read as "launch / kick off" instead of
            // the generic '+' add icon.
            rocket: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z"/><path d="M12 15l-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z"/><path d="M9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0"/><path d="M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5"/></svg>',
            feather: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.24 12.24a6 6 0 0 0-8.49-8.49L5 10.5V19h8.5z"/><line x1="16" x2="2" y1="8" y2="22"/><line x1="17.5" x2="9" y1="15" y2="15"/></svg>',
            flow: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="5" cy="6" r="3"/><circle cx="19" cy="18" r="3"/><path d="M8 6h6a4 4 0 0 1 4 4v5"/></svg>',
            repeat: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="17 1 21 5 17 9"/><path d="M3 11V9a4 4 0 014-4h14"/><polyline points="7 23 3 19 7 15"/><path d="M21 13v2a4 4 0 01-4 4H3"/></svg>',
            lock: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0110 0v4"/></svg>',
            // Curly-braces `{ }` — used as the Variables-tab glyph.
            // Brace strokes read at small sizes (better than a generic
            // list icon, which would compete with the actual table
            // rows underneath).
            braces: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5c0 1.1.9 2 2 2h1"/><path d="M16 21h1a2 2 0 0 0 2-2v-5c0-1.1.9-2 2-2a2 2 0 0 1-2-2V5a2 2 0 0 0-2-2h-1"/></svg>',
            // Key — used as the Secrets-tab glyph. API keys / tokens
            // sit conceptually in the same family as the lock icon
            // (auth), but read as "the thing the lock takes" so the
            // two stay visually distinct in the tab strip.
            key: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="7.5" cy="15.5" r="3.5"/><path d="M10 13l8.5-8.5"/><path d="M16 7l3 3"/></svg>',
            // Two overlapping circles + connecting line — Compare tab
            // glyph. Reads as "two things contrasted" (Lucide's
            // git-compare idiom) without needing a git-specific shape.
            diff: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="5" cy="6" r="3"/><path d="M5 9v6a3 3 0 0 0 3 3h3"/><polyline points="10 14 7 17 10 20"/><circle cx="19" cy="18" r="3"/><path d="M19 15V9a3 3 0 0 0-3-3h-3"/><polyline points="14 10 17 7 14 4"/></svg>',
            star: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/></svg>',
            starFilled: '<svg viewBox="0 0 24 24" fill="currentColor" stroke="currentColor" stroke-width="1"><polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/></svg>',
            // #292 redux — outer circle dropped: the topbar's other
            // glyphs (search, settings, theme, bot, compass…) are all
            // open-shape Lucide-style strokes, so wrapping these in a
            // circle made them read as "button inside a button" and
            // collapsed contrast at 16 px. Now just the `i` and `?`
            // glyphs, scaled to fill the viewbox so they stay
            // recognisable at the same size as their neighbours.
            info: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><line x1="12" y1="22" x2="12" y2="10"/><line x1="12" y1="5" x2="12.01" y2="5"/></svg>',
            help: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M8 8a4 4 0 0 1 7.7 1.5c0 2.5-3.7 3.5-3.7 6"/><line x1="12" y1="20" x2="12.01" y2="20"/></svg>',
            upload: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>',
            globe: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z"/></svg>',
            settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 00.33 1.82l.06.06a2 2 0 01-2.83 2.83l-.06-.06a1.65 1.65 0 00-1.82-.33 1.65 1.65 0 00-1 1.51V21a2 2 0 01-4 0v-.09a1.65 1.65 0 00-1-1.51 1.65 1.65 0 00-1.82.33l-.06.06a2 2 0 01-2.83-2.83l.06-.06a1.65 1.65 0 00.33-1.82 1.65 1.65 0 00-1.51-1H3a2 2 0 010-4h.09a1.65 1.65 0 001.51-1 1.65 1.65 0 00-.33-1.82l-.06-.06a2 2 0 012.83-2.83l.06.06a1.65 1.65 0 001.82.33h0a1.65 1.65 0 001-1.51V3a2 2 0 014 0v.09a1.65 1.65 0 001 1.51h0a1.65 1.65 0 001.82-.33l.06-.06a2 2 0 012.83 2.83l-.06.06a1.65 1.65 0 00-.33 1.82v0a1.65 1.65 0 001.51 1H21a2 2 0 010 4h-.09a1.65 1.65 0 00-1.51 1z"/></svg>',
            plus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>',
            trash: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 01-2 2H8a2 2 0 01-2-2L5 6"/><path d="M10 11v6M14 11v6"/><path d="M9 6V4a1 1 0 011-1h4a1 1 0 011 1v2"/></svg>',
            pencil: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 3a2.85 2.85 0 0 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z"/></svg>',
            close: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>',
            // Distinct from `close` (the plain X) — depicts a dashed
            // selection rectangle around an interior X so the operator
            // sees 'clear the marked selection' rather than 'close this
            // pane'. Used by the console toolbar's Clear-selection
            // button to avoid the prior X duplication.
            selectionClear: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2" stroke-dasharray="3 3"/><line x1="9" y1="9" x2="15" y2="15"/><line x1="15" y1="9" x2="9" y2="15"/></svg>',
            // Floppy-disk save glyph — used by the workspace
            // overview's 'Save as template' row tool (#242). The
            // previous emoji fallback (💾) read as orphaned text
            // next to the SVG-rendered rename/delete pair.
            save: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/><polyline points="17 21 17 13 7 13 7 21"/><polyline points="7 3 7 8 15 8"/></svg>',
            // Plain checkmark glyph — used by the workspace
            // sidebar / dropdown's 'Switch to this workspace' button
            // (#277). The previous '+' read as 'add' and confused
            // the operator about what gets added.
            check: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>',
            // Bookmark glyph — used by 'Save as template' (#278) to
            // replace the floppy-disk save icon. Floppy reads as
            // 'force-flush current state' (operator already has a
            // 'Save now' button for that); bookmark reads as 'tag
            // this configuration for later reuse', which matches
            // the template semantic.
            bookmark: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z"/></svg>',
            record: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="12" r="6"/></svg>',
            download: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>',
            list: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/></svg>',
            layers: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>',
            // User-supplied "preset" glyph — clipboard with a saved
            // sheet and a corner fold. Reads as "captured config
            // sheet" so it pairs naturally with the Preset picker
            // without clashing with the workspace chip's `layers`
            // glyph. Custom viewBox; fill switched to currentColor so
            // it tints with the surrounding icon-button colour.
            preset: '<svg viewBox="-48 0 512 512" fill="currentColor"><path d="M256,512l-204,0c-28.719,0 -52,-23.281 -52,-52l0,-328c0,-28.719 23.281,-52 52,-52l44,0l0,-8c0,-39.765 32.236,-72 72,-72c39.764,0 72,32.236 72,72l0,8l124,0c28.719,0 52,23.281 52,52l0,220l-0.296,0c-1.259,11.755 -6.497,22.793 -14.934,31.23l-113.54,113.54c-8.437,8.437 -19.475,13.675 -31.23,14.934l0,0.296Zm-160,-400l-44,0c-11.071,0.025 -20,8.97 -20,20l0,328c0,11.046 8.954,20 20,20l172,0l0,-96c0,-35.346 28.654,-64 64,-64l96,0l0,-188c0,-11.03 -8.929,-19.975 -20,-20l-236,0l0,152c0,13.255 10.745,24 24,24c13.255,0 24,-10.745 24,-24l0,-120l32,0l0,120c0,30.928 -25.072,56 -56,56c-30.928,0 -56,-25.072 -56,-56l0,-152Zm160,367.218c3.222,-0.929 6.189,-2.662 8.603,-5.076l113.539,-113.539c2.414,-2.414 4.147,-5.381 5.076,-8.603l-95.218,0c-17.673,0 -32,14.327 -32,32l0,95.218Zm-48,-399.218l0,-8c0,-22.091 -17.909,-40 -40,-40c-22.091,0 -40,17.909 -40,40l0,8l80,0Z"/></svg>',
            // User-supplied "discover" glyph — horizontal bars joined
            // by a vertical spine that crosses the centre. Reads as a
            // sitemap / catalogue, which matches Discover's "show me
            // every endpoint on every service" intent better than the
            // compass needle it replaces. Custom viewBox 0 0 32 32;
            // fill switched to currentColor so it tints with the
            // surrounding icon-button colour.
            discover: '<svg viewBox="0 0 32 32" fill="currentColor"><path d="M31.783 5.814l-3.116-3.479c-0.189-0.214-0.462-0.336-0.748-0.336h-10.945v-1.062c0-0.517-0.448-0.938-1-0.938s-1 0.42-1 0.938v1.062h-7.994c-0.553 0-1 0.448-1 1v6.989c0 0.553 0.447 1 1 1h7.994v2.003h-10.893c-0.286 0-0.558 0.122-0.748 0.336l-3.115 3.541c-0.336 0.379-0.336 0.949 0 1.328l3.115 3.509c0.191 0.214 0.462 0.305 0.748 0.305h10.893v9.052c0 0.517 0.448 0.938 1 0.938s1-0.42 1-0.938v-9.053h8.015c0.552 0 1-0.448 1-1v-7.019c0-0.553-0.448-1-1-1h-8.014v-2.003h10.945c0.286 0 0.558-0.122 0.748-0.336l3.116-3.51c0.335-0.378 0.335-0.949 0-1.328v-0zM23.989 20.010h-19.459l-2.228-2.477 2.228-2.541h19.459v5.018zM27.47 8.989h-19.49v-4.989h19.49l2.227 2.479z"/></svg>',
            // User-supplied "mock" glyph — magnifying-glass head with
            // an open pacman target. Mirrored horizontally (the source
            // pointed right; mock semantics read better with the lens
            // pointing left). fill switched to currentColor so it
            // tints with the surrounding icon-button colour.
            // Crash-test-dummy head — Mocks rail's original glyph,
            // restored after the masks SVG migrated to Proxy.
            mock: '<svg viewBox="0 0 512 512" fill="currentColor"><g transform="translate(512,0) scale(-1,1)"><path d="M489.518,201.801C484.932,88.594,392.72,0,279.307,0C166.021,0,73.349,89.983,69.025,202.237 c-14.52,28.421-40.695,81.771-41.893,84.215c-6.784,13.536-7.038,29.476-0.587,43.479c6.427,13.965,18.677,24.129,33.61,27.865 l9.585,2.404c4.649,20.986,11.639,52.62,13.829,62.673c8.251,37.973,28.198,54.176,66.696,54.176 c5.332,0,10.759-0.318,16.043-0.834v3.285V512h32.499h190.645h32.499v-32.499v-66.719c0-21.74,6.712-42.822,17.948-56.389 C486.162,300.559,492.462,241.623,489.518,201.801z M414.872,335.66c-18.772,22.661-25.421,52.962-25.421,77.122v66.719H198.806 v-42.155c0,0-26.294,7.205-48.542,7.205c-22.232,0-29.833-5.094-34.943-28.596c-3.38-15.519-18.193-82.406-18.193-82.406 l-29.08-7.276c-5.292-1.325-9.703-4.975-11.988-9.942c-2.286-4.959-2.198-10.687,0.245-15.567c0,0,31.801-64.823,45.059-90.356 c0.016-98.258,79.677-177.911,177.943-177.911c95.997,0,174.19,76.034,177.76,171.158 C459.963,241.362,452.623,290.102,414.872,335.66z"/><path d="M279.307,93.093c-64.815,0-117.34,52.534-117.34,117.349c0,64.799,52.526,117.34,117.34,117.34 c64.8,0,117.34-52.541,117.34-117.34C396.647,145.626,344.107,93.093,279.307,93.093z M279.307,299.044v-88.602h-88.61 c0-48.938,39.671-88.611,88.61-88.611v88.611h88.602C367.909,259.372,328.245,299.044,279.307,299.044z"/></g></svg>',
            // Two-mask creature face — used by Proxy / MITM rail (the
            // intermediary that wears different faces to each side).
            // SVGRepo origin; cleaner silhouette than the prior 'disconnect'
            // chain icon.
            masks: '<svg viewBox="0 0 512 512" fill="currentColor"><path d="M293.686,333.324c5.976,52.529,58.497,59.684,74.014,59.684c15.521,0,68.042-7.155,74.01-59.684c-9.545,8.358-40.582,22.691-74.01,22.691C334.276,356.014,303.239,341.681,293.686,333.324z"/><path d="M312.681,272.069c12.31,1.33,23.671,5.338,33.591,11.37c0.394-17.695-12.824-33.041-30.747-34.969c-17.827-1.92-33.926,10.126-37.423,27.344C288.894,272.189,300.622,270.778,312.681,272.069z"/><path d="M389.129,283.439c9.919-6.032,21.285-10.039,33.587-11.37c12.067-1.291,23.786,0.12,34.579,3.745c-3.498-17.218-19.596-29.264-37.42-27.344C401.948,250.398,388.73,265.744,389.129,283.439z"/><path d="M511.724,171.919c0,0-0.02-0.216-0.036-0.327l-0.02-0.104l-0.02-0.144c-0.948-6.86-5.975-12.389-13.05-13.106c-5.765-0.59-11.586,2.35-17.119,3.673c-11.405,2.693-22.97,4.74-34.535,6.462c-30.085,4.478-60.56,6.549-90.964,5.832c-23.604-0.55-47.223-2.621-70.552-6.278c-8.7-36.101-23.006-95.259-27.058-112.023c-0.82-3.402-1.43-8.724-8.29-3.792c-4.96,3.593-54.665,28.874-118.249,44.243C68.247,111.724,12.475,111.916,6.42,111c-8.346-1.275-6.466,3.744-5.641,7.146C5.133,136.16,21.294,203.039,29.7,237.777c20.496,84.87,111.994,128.117,164.213,115.489c11.242-2.717,22.918-8.573,34.125-16.939c11.282,46.108,43.136,86.902,84.714,109.784c13.413,7.378,28.077,12.979,43.311,15.027c14.935,2.023,29.942-0.167,44.14-5.02c27.938-9.56,52.665-28.213,71.624-50.649c18.481-21.886,31.731-48.227,37.172-76.408c2.506-12.964,2.848-25.934,2.848-39.073V186.977C511.848,182.029,512.262,176.842,511.724,171.919z M61.156,185.351c11.358,0.972,23.082-0.398,34.487-4.493c11.656-4.176,21.776-10.733,29.99-18.932c4.534,17.098-4.705,35.129-21.679,41.2C87.086,209.182,68.606,201.262,61.156,185.351z M223.564,256.612c-13.808-2.852-33.539-3.991-52.84,0.685c-28.596,6.908-52.18,25.576-58.625,34.691c-5.744-46.156,37.694-63.134,50.98-66.354c10.202-2.47,40.001-5.832,60.485,12.629V256.612z M231.835,159.648c-1.932,3.928-4.581,7.466-7.844,10.406c-5.008,4.526-11.405,7.633-18.6,8.581c-17.875,2.327-34.324-9.489-38.108-26.794c11.07,3.554,23.046,4.764,35.328,3.171c12.019-1.57,23.082-5.705,32.734-11.76C235.564,149.083,234.289,154.677,231.835,159.648z M488.574,284.937c0,12.708,0.16,25.337-2.079,37.91c-1.933,10.867-5.247,21.448-9.72,31.519c-11.354,24.644-29.67,46.164-51.892,61.66c-12.302,8.589-26.109,15.593-40.813,18.939c-13.732,3.155-27.284,1.825-40.57-2.701c-27.826-9.506-52.032-29.814-69.01-53.526c-13.286-18.564-22.277-40.076-25.496-62.576c-0.88-6.103-1.326-12.302-1.326-18.516V184.149c14.274,3.513,28.83,6.102,43.438,8.07c9.314,1.275,18.676,2.295,27.986,3.14c31.193,2.805,62.548,2.964,93.757,0.43c25.66-2.079,51.514-5.497,76.551-11.656V284.937z"/></svg>',
            // Diagonal pencil — Compose rail. Distinct from the existing
            // small 'pencil' edit icon (used for inline rename buttons).
            // SVGRepo origin; reads as 'make/draw' at rail-strip size.
            pencilLarge: '<svg viewBox="0 0 508.089 508.089" fill="currentColor"><path d="M503.977,202.892l-198.6-198.7c-5.5-5.5-14.4-5.5-20,0c0,0-209.7,210.1-210.1,210.5c-1.2,1.5-74.8,275.9-74.8,275.9c-1.3,4.9,0.1,10.1,3.7,13.6c2.8,2.8,6.6,4.3,13.6,3.7l275.8-74.8c0.4-0.3,210.3-210.3,210.3-210.3C509.477,217.292,509.477,208.392,503.977,202.892z M295.477,34.192l29.6,29.7l-145.4,145.5h-59.3L295.477,34.192z M34.077,474.392l12.6-47.4c15.1,7.3,27.4,19.6,34.7,34.8L34.077,474.392z M270.577,410.092v1.2l-161.5,43.1c-10.4-24.8-30.2-44.6-55-55l43-161.7h1.2h73.1v85.2c0,7.8,6.3,14.1,14.1,14.1h85.1V410.092z M278.877,308.792h-79.3v-79.4l145.5-145.6l79.3,79.4L278.877,308.792z M298.877,388.092v-59.4l145.5-145.5l29.7,29.7L298.877,388.092z"/></svg>',
            // Cordless drill — SVGRepo. Compose rail icon: 'workshop / power
            // tool' metaphor. Reads more action-y than the pencil at rail-strip
            // size, and the chuck-on-the-right silhouette is distinctive.
            drill: '<svg viewBox="0 0 512 512" fill="currentColor"><path d="M495.313,139.162h-61.184v-16.687c0-7.657-5.212-14.332-12.64-16.188l-55.453-13.863c-6.207-30.422-33.173-53.381-65.4-53.381H50.338c-27.603,0-50.06,22.457-50.06,50.06v133.493c0,27.603,22.457,50.06,50.06,50.06h12.001L36.845,374.631C15.635,380.482,0,400.047,0,423.23c0,27.418,22.457,49.726,50.06,49.726h133.493c27.603,0,50.06-22.308,50.06-49.726c0-20.427-12.142-38.041-29.537-45.946l26.157-104.629h70.404c32.228,0,59.193-22.961,65.4-53.382l55.453-13.863c7.428-1.857,12.64-8.531,12.64-16.188v-16.687h61.184c9.215,0,16.687-7.471,16.687-16.687C511.999,146.633,504.529,139.162,495.313,139.162z M183.552,439.583H50.06c-9.357,0-16.687-7.183-16.687-16.353c0-9.386,7.486-17.02,16.687-17.02h133.493c9.201,0,16.687,7.635,16.687,17.02C200.239,432.4,192.909,439.583,183.552,439.583z M161.582,339.401h17.563l-8.359,33.435H71.694L96.74,272.655h99.093l-8.343,33.373h-25.908c-9.215,0-16.687,7.471-16.687,16.687C144.895,331.93,152.366,339.401,161.582,339.401z M334.01,205.909c0,18.402-14.971,33.373-33.373,33.373c-15.991,0-241.941,0-250.299,0c-9.201,0-16.687-7.486-16.687-16.687V89.102c0-9.201,7.486-16.687,16.687-16.687h250.299c18.402,0,33.373,14.971,33.373,33.373V205.909z M400.756,176.193l-33.373,8.343v-57.375l33.373,8.343V176.193z"/><path d="M217.204,105.789H83.711c-9.215,0-16.687,7.471-16.687,16.687s7.471,16.687,16.687,16.687h133.493c9.215,0,16.687-7.471,16.687-16.687S226.419,105.789,217.204,105.789z"/><path d="M217.204,172.535H83.711c-9.215,0-16.687,7.471-16.687,16.687s7.471,16.687,16.687,16.687h133.493c9.215,0,16.687-7.471,16.687-16.687S226.419,172.535,217.204,172.535z"/></svg>',
            filter: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3"/></svg>',
            grip: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="9" cy="5" r="1.5"/><circle cx="15" cy="5" r="1.5"/><circle cx="9" cy="12" r="1.5"/><circle cx="15" cy="12" r="1.5"/><circle cx="9" cy="19" r="1.5"/><circle cx="15" cy="19" r="1.5"/></svg>',
            // Three-dot overflow handle. Vertical orientation so the
            // glyph reads as "open this row's hidden actions" — the
            // same affordance Gmail / GitHub / VS Code use.
            dots: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="5" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="12" cy="19" r="1.6"/></svg>',
            chevronUp: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="18 15 12 9 6 15"/></svg>',
            chevronDown: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="6 9 12 15 18 9"/></svg>',
            // Half-filled circle = "follows system" — left half dark, right half light.
            themeAuto: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 3v18" stroke="none"/><path d="M12 3a9 9 0 010 18z" fill="currentColor" stroke="none"/></svg>',
            // Four-point sparkle — used for AI / inference surfaces. Mirrors
            // the universal "sparkle" affordance from Copilot / ChatGPT /
            // Gemini, so users recognize the AI tab without reading the label.
            spark: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l1.8 5.7L19 10l-5.2 1.3L12 17l-1.8-5.7L5 10l5.2-1.3L12 3z"/></svg>',
            // Robot-head used for the AI drawer toggle (#111 — paired
            // with a separate shield icon for the Security drawer so
            // the two surfaces are visually distinct in the topbar).
            // Lucide-style "bot" — rounded rect head + antenna + two
            // dot eyes + a mouth line. Reads as a friendly assistant
            // rather than the more abstract sparkle.
            bot: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v4"/><rect x="3" y="6" width="18" height="14" rx="3"/><path d="M2 14h2M20 14h2"/><circle cx="9" cy="13" r="1" fill="currentColor"/><circle cx="15" cy="13" r="1" fill="currentColor"/><path d="M9 17h6"/></svg>',
            // Shield — Security drawer toggle (#111). Classic outline
            // shield without internal decoration so it reads at 16 px
            // without competing with the bot icon's two-eye anchor.
            shield: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>',
            // Bowire brand mark — full horizontal knot + Circle-B
            // derived from images/bowire_logo.svg. Uses currentColor
            // so the surrounding CSS `color:` rule drives the fill
            // (landing page uses this for the welcome hero). viewBox
            // is 261.48×154.57 — wider than tall, ~1.69:1 ratio — so
            // the consuming CSS (bowire-landing-hero-logo etc.)
            // sizes by width and lets height auto-derive. Regenerate
            // by copying the paths out of images/bowire_logo.svg with
            // `fill:#000000` dropped so the currentColor inheritance
            // actually wins.
            bowireLogo: '<svg viewBox="0 0 261.48001 154.57416" fill="currentColor" aria-label="Bowire" xmlns="http://www.w3.org/2000/svg">'
                + '<g transform="translate(-17.759995,-27.712919)">'
                + '<g transform="translate(34.743257,-42.028188)">'
                + '<path d="m -16.83024,139.95929 c 1.84642,-18.46629 9.49416,-34.68302 22.48158,-47.671389 9.93944,-9.940176 22.10664,-16.854308 35.62523,-20.244412 6.47586,-1.62397 11.94542,-2.290645 18.88822,-2.30225 10.97822,-0.01835 20.30886,1.884138 30.73234,6.266232 2.94145,1.236609 8.55155,4.244648 11.36516,6.093811 6.77085,4.449944 24.72804,22.279808 41.45503,38.948248 l 11.80253,11.75565 -5.50011,5.50656 -5.5001,5.50655 -12.7968,-12.74895 C 115.87437,115.28014 97.22865,96.859687 91.3437,93.506247 82.64711,88.550643 74.18287,86.001008 64.05489,85.286197 50.32506,84.317176 35.49658,88.52034 24.44569,96.513514 2.4177,112.44647 -6.74323,139.62106 1.16888,165.56082 c 3.05114,10.00311 9.65326,20.28552 17.59422,27.40193 9.48791,8.5027 20.7322,13.6882 33.51565,15.4563 4.38843,0.60697 12.29155,0.54735 16.69984,-0.12598 11.3024,-1.72637 21.22073,-6.00905 29.92054,-12.91954 1.08433,-0.86132 6.82657,-6.40095 12.76053,-12.3103 l 9.20153,-9.15677 5.44327,5.44698 5.44328,5.44698 -0.26438,0.27598 c -9.33101,9.74042 -20.24688,20.15177 -21.56698,21.25635 -3.27335,2.73896 -6.00967,4.67833 -10.102,7.15981 -2.21752,1.34464 -6.51021,3.58751 -6.86624,3.58751 -0.10955,0 -0.70399,0.26874 -1.32099,0.59719 -3.99214,2.12518 -12.16167,4.56744 -19.2854,5.76533 -5.08078,0.85434 -14.10387,1.13358 -19.49205,0.6032 -11.62938,-1.14473 -24.11424,-5.4007 -33.7495,-11.50492 -9.1311,-5.7848 -17.10577,-13.51932 -23.05093,-22.35674 -6.68307,-9.93431 -10.8373,-20.9167 -12.54968,-33.17708 -0.43919,-3.14448 -0.64689,-13.87764 -0.3299,-17.04776 z m 130.67913,8.92978 c 0.0465,-0.19135 0.2116,-1.13071 0.36678,-2.08748 0.54517,-3.36124 2.09988,-7.01952 4.28222,-10.07621 0.55168,-0.77271 2.61322,-3.04039 4.58122,-5.03927 l 1.99065,-2.04684 5.48875,5.49231 5.48875,5.49233 c 0,0 -0.47021,0.3863 -1.67132,1.65725 -1.20111,1.27095 -3.553,3.76399 -3.91589,4.40446 -1.50283,2.65237 -1.57031,5.95731 -0.17882,8.75683 0.78678,1.58289 1.83691,2.68017 18.06828,18.87925 13.36236,13.33579 17.49342,17.328 18.39266,17.77444 3.38781,1.68195 7.43577,1.27015 10.23591,-1.04131 4.50529,-3.71902 4.9956,-9.50722 2.14917,-12.61831 -0.48752,-0.53285 -0.94004,-1.00567 -0.94004,-1.11421 0,-0.21688 10.53283,-10.84173 10.74783,-10.84173 0.24476,0 1.93015,1.72707 3.12492,3.18478 5.18111,6.32134 5.5618,16.01442 1.61736,24.55164 -2.39558,5.18491 -6.92271,9.71205 -12.10762,12.10762 -8.38394,3.87362 -18.40829,2.72945 -25.72882,-2.93667 -1.10954,-0.85879 -35.46056,-35.20756 -36.40976,-36.40739 -1.52016,-1.92152 -2.02839,-2.67654 -2.7585,-4.09796 -1.90704,-3.71274 -2.67369,-6.64338 -2.8112,-10.74634 -0.0535,-1.5946 -0.0591,-3.05584 -0.0125,-3.24719 z m 39.30483,-8.23395 c 15.53172,-15.55358 18.25173,-18.37528 19.37541,-20.11375 1.79389,-2.77535 2.69812,-4.84258 4.54323,-10.38665 3.16673,-9.51518 5.15695,-13.935259 8.28308,-18.395897 2.16286,-3.086157 6.16647,-7.320305 8.73678,-9.239872 9.70284,-7.246315 24.15069,-7.703981 35.06606,-1.110791 5.6433,3.408719 10.12699,8.493424 12.83584,14.556442 0.88227,1.974714 0.81053,2.767079 0.81053,3.393785 0,0.262198 -1.389,0.317213 -8.00889,0.317213 h -8.0089 l -0.77542,-1.212171 c -3.4428,-5.381971 -7.56084,-6.18557 -13.69259,-6.205119 -4.61072,-0.0147 -7.80152,1.27765 -10.90557,4.417009 -4.33511,4.384421 -6.1292,7.677041 -9.17724,16.842561 -3.42173,10.28921 -5.1411,13.82205 -9.08703,18.67135 -0.77853,0.95677 -9.45927,9.77611 -19.29054,19.59853 l -16.28753,16.27146 -5.44901,-5.45005 -5.449,-5.45006 z m -10.04827,-28.99278 c 4.37824,-4.23247 7.70304,-6.24053 12.5793,-7.59744 1.66535,-0.46342 2.42772,-0.50094 11.81176,-0.58139 5.51732,-0.0473 7.91483,-0.0138 7.91483,0.0744 0,0.62191 -3.84217,11.52045 -4.63563,13.14923 l -0.98867,2.0295 h -3.1575 c -8.12925,0 -8.89344,0.25287 -12.98916,4.29815 -1.48131,1.46306 -1.20742,1.07262 -1.33165,1.07262 -0.3024,0 -10.77241,-10.5134 -10.75136,-10.7959 0.009,-0.12175 -0.16742,0.009 1.54808,-1.64911 z m 27.40338,46.01798 c 10.04746,-10.02217 19.05069,-18.85125 20.00745,-19.62018 5.57304,-4.47902 10.84314,-7.00297 18.78732,-8.99764 7.10676,-1.7844 9.2615,-1.80883 13.29386,-5.61276 l 1.57928,-1.48982 7.28446,-0.0123 c 10.79197,-0.0182 10.18374,-0.0358 10.18374,0.29609 0,0.74686 -1.61901,4.02843 -3.17893,6.11847 -1.78423,2.3906 -5.30771,5.95185 -7.72422,7.80704 -4.77682,3.66723 -9.25459,5.70659 -16.79935,7.6511 -6.02109,1.55182 -7.89336,2.29671 -11.25348,4.47724 -2.66167,1.72727 -5.70725,4.63803 -23.53262,22.49083 -8.73554,8.74899 -14.35068,14.31975 -14.41836,14.31975 -0.0677,0 -2.54989,-2.42847 -5.51602,-5.39663 L 153.829,174.3149 Z m 25.46487,-44.27841 c 1.78139,-5.27994 2.61234,-7.38402 3.54946,-8.98776 l 0.57602,-0.98575 43.61506,-0.38944 c 1.17317,3.73468 1.06434,10.8818 -0.57886,15.91281 -31.50934,0 -48.87103,-0.29346 -48.86149,-0.38914 0.01,-0.0957 0.77445,-2.418 1.69981,-5.16072 z"/>'
                + '<path d="m 53.217234,151.69333 v 16.57418 h 7.67291 c 3.27394,0 5.83185,-0.75992 7.67344,-2.27997 1.87082,-1.52004 2.80603,-3.59565 2.80603,-6.22649 0,-2.51391 -0.92052,-4.48667 -2.76211,-5.91902 -1.81236,-1.43234 -4.35557,-2.1487 -7.62951,-2.1487 z"/>'
                + '<path d="m 53.217234,126.2618 v 14.90814 h 6.22597 c 2.92316,0 5.21814,-0.70167 6.88434,-2.10478 1.69543,-1.43235 2.54299,-3.39093 2.54299,-5.87561 0,-4.61859 -3.44955,-6.92775 -10.3482,-6.92775 z"/>'
                + '<path d="m 60.020434,99.707866 c -26.26504,-1.7e-4 -47.557183,21.291754 -47.557298,47.556794 -1.66e-4,26.26524 21.292058,47.55747 47.557298,47.5573 26.26504,-1.2e-4 47.556946,-21.29226 47.556776,-47.5573 -1.2e-4,-26.26484 -21.291936,-47.556674 -47.556776,-47.556794 z m -20.96616,16.118394 h 22.88852 c 7.01558,0 12.4085,1.28631 16.17937,3.85868 3.77088,2.57238 5.65651,6.19671 5.65651,10.87376 0,3.39086 -1.15458,6.35812 -3.46387,8.90126 -2.28007,2.54315 -5.20324,4.31175 -8.76949,5.30562 v 0.17519 c 4.47243,0.5554 8.03849,2.20696 10.69857,4.95473 2.6893,2.74777 4.03386,6.09499 4.03386,10.04125 0,5.75862 -2.06092,10.33336 -6.18257,13.72423 -4.12165,3.36163 -9.74863,5.04258 -16.88114,5.04258 h -24.15976 z"/>'
                + '</g></g></svg>',
        };
        return icons[name] || '';
    }

