    // ---- Helpers ----
    function $(sel, parent) { return (parent || document).querySelector(sel); }
    function $$(sel, parent) { return [...(parent || document).querySelectorAll(sel)]; }

    function el(tag, attrs, ...children) {
        const e = document.createElement(tag);
        if (attrs) {
            for (const [k, v] of Object.entries(attrs)) {
                if (k === 'className') e.className = v;
                else if (k === 'textContent') e.textContent = v;
                else if (k === 'innerHTML') e.innerHTML = v;
                else if (k.startsWith('on')) e.addEventListener(k.slice(2).toLowerCase(), v);
                else if (k === 'dataset') Object.assign(e.dataset, v);
                else e.setAttribute(k, v);
            }
        }
        for (const c of children) {
            if (typeof c === 'string') e.appendChild(document.createTextNode(c));
            else if (c) e.appendChild(c);
        }
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
        // Substitute ${var} placeholders so users can use ${baseUrl} in the URL field
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
        if (Array.isArray(value)) {
            var openAttr = openByDefault ? ' open' : '';
            var len = value.length;
            var summary = '<summary class="bowire-json-tree-summary">'
                + labelHtml(path)
                + '<span class="bowire-json-tree-bracket">[</span>'
                + '<span class="bowire-json-tree-meta">' + len + (len === 1 ? ' item' : ' items') + '</span>'
                + '<span class="bowire-json-tree-bracket">]</span>'
                + '</summary>';
            var children = '';
            for (var i = 0; i < len; i++) {
                var childPath = path ? path + '.' + i : String(i);
                children += treeNodeHtml(value[i], childPath, i, false);
            }
            return '<details class="bowire-json-tree-node"' + openAttr + '>' + summary
                + '<div class="bowire-json-tree-children">' + children + '</div></details>';
        }
        if (typeof value === 'object') {
            var keys = Object.keys(value);
            var openAttr2 = openByDefault ? ' open' : '';
            var summary2 = '<summary class="bowire-json-tree-summary">'
                + labelHtml(path)
                + '<span class="bowire-json-tree-bracket">{</span>'
                + '<span class="bowire-json-tree-meta">' + keys.length
                + (keys.length === 1 ? ' key' : ' keys') + '</span>'
                + '<span class="bowire-json-tree-bracket">}</span>'
                + '</summary>';
            var children2 = '';
            for (var k = 0; k < keys.length; k++) {
                var ckey = keys[k];
                var ckpath = path ? path + '.' + ckey : ckey;
                children2 += treeNodeHtml(value[ckey], ckpath, ckey, false);
            }
            return '<details class="bowire-json-tree-node"' + openAttr2 + '>' + summary2
                + '<div class="bowire-json-tree-children">' + children2 + '</div></details>';
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

    function methodBadgeLabel(type) {
        switch (type) {
            case 'Unary': return 'Unary';
            case 'ServerStreaming': return 'Server Stream';
            case 'ClientStreaming': return 'Client Stream';
            case 'Duplex': return 'Duplex';
            default: return type;
        }
    }

    // Toast notification with optional undo callback.
    // Returns the toast element so callers can add custom content.
    // Options: { undo: function, duration: ms (0 = sticky) }
    function toast(message, type, options) {
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

    function svgIcon(name) {
        const icons = {
            search: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><path d="M21 21l-4.35-4.35"/></svg>',
            chevron: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M9 18l6-6-6-6"/></svg>',
            sidebar: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/></svg>',
            play: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>',
            stop: '<svg viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="6" width="12" height="12" rx="1"/></svg>',
            copy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 01-2-2V4a2 2 0 012-2h9a2 2 0 012 2v1"/></svg>',
            sun: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="5"/><path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/></svg>',
            moon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 12.79A9 9 0 1111.21 3 7 7 0 0021 12.79z"/></svg>',
            clock: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>',
            connect: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M15 7h3a5 5 0 010 10h-3M9 17H6a5 5 0 010-10h3"/><line x1="8" y1="12" x2="16" y2="12"/></svg>',
            disconnect: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M15 7h3a5 5 0 010 10h-3M9 17H6a5 5 0 010-10h3"/><line x1="8" y1="12" x2="16" y2="12" stroke-dasharray="2 2"/></svg>',
            send: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M2.01 21L23 12 2.01 3 2 10l15 2-15 2z"/></svg>',
            replay: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M1 4v6h6"/><path d="M3.51 15a9 9 0 102.13-9.36L1 10"/></svg>',
            repeat: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="17 1 21 5 17 9"/><path d="M3 11V9a4 4 0 014-4h14"/><polyline points="7 23 3 19 7 15"/><path d="M21 13v2a4 4 0 01-4 4H3"/></svg>',
            lock: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0110 0v4"/></svg>',
            star: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/></svg>',
            starFilled: '<svg viewBox="0 0 24 24" fill="currentColor" stroke="currentColor" stroke-width="1"><polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/></svg>',
            info: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/></svg>',
            help: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><path d="M9.09 9a3 3 0 015.83 1c0 2-3 3-3 3"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
            upload: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>',
            globe: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 014 10 15.3 15.3 0 01-4 10 15.3 15.3 0 01-4-10 15.3 15.3 0 014-10z"/></svg>',
            settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 00.33 1.82l.06.06a2 2 0 01-2.83 2.83l-.06-.06a1.65 1.65 0 00-1.82-.33 1.65 1.65 0 00-1 1.51V21a2 2 0 01-4 0v-.09a1.65 1.65 0 00-1-1.51 1.65 1.65 0 00-1.82.33l-.06.06a2 2 0 01-2.83-2.83l.06-.06a1.65 1.65 0 00.33-1.82 1.65 1.65 0 00-1.51-1H3a2 2 0 010-4h.09a1.65 1.65 0 001.51-1 1.65 1.65 0 00-.33-1.82l-.06-.06a2 2 0 012.83-2.83l.06.06a1.65 1.65 0 001.82.33h0a1.65 1.65 0 001-1.51V3a2 2 0 014 0v.09a1.65 1.65 0 001 1.51h0a1.65 1.65 0 001.82-.33l.06-.06a2 2 0 012.83 2.83l-.06.06a1.65 1.65 0 00-.33 1.82v0a1.65 1.65 0 001.51 1H21a2 2 0 010 4h-.09a1.65 1.65 0 00-1.51 1z"/></svg>',
            plus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>',
            trash: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 01-2 2H8a2 2 0 01-2-2L5 6"/><path d="M10 11v6M14 11v6"/><path d="M9 6V4a1 1 0 011-1h4a1 1 0 011 1v2"/></svg>',
            close: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>',
            record: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="12" r="6"/></svg>',
            download: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>',
            list: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/></svg>',
            layers: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>',
            filter: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3"/></svg>',
            grip: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="9" cy="5" r="1.5"/><circle cx="15" cy="5" r="1.5"/><circle cx="9" cy="12" r="1.5"/><circle cx="15" cy="12" r="1.5"/><circle cx="9" cy="19" r="1.5"/><circle cx="15" cy="19" r="1.5"/></svg>',
            chevronUp: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="18 15 12 9 6 15"/></svg>',
            chevronDown: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="6 9 12 15 18 9"/></svg>',
            // Half-filled circle = "follows system" — left half dark, right half light.
            themeAuto: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 3v18" stroke="none"/><path d="M12 3a9 9 0 010 18z" fill="currentColor" stroke="none"/></svg>',
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

