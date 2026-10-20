    // ---- MCP tasks (#801): the pure part ----
    //
    // A tool call on an MCP server that runs it as a task streams `task`
    // frames ({ event: 'task', data: { taskId, status, ... } }) while the
    // task changes, then its `result` or `error` frame. Everything in this
    // fragment reads those frames, the elicitation schemas a task asks with,
    // and the per-workspace list of tasks still running — and touches no DOM,
    // no network and no storage, so the unit tests can drive it directly.
    // The UI around it lives in mcp-tasks.js.

    var MCP_FRAME_EVENTS = ['progress', 'log', 'result', 'error', 'task'];
    var MCP_TASK_STATUSES = ['working', 'input_required', 'completed', 'failed', 'cancelled'];
    var MCP_TASK_TERMINAL = ['completed', 'failed', 'cancelled'];

    /// The MCP frame a stream message carries, or null. The plugin wraps
    /// every frame as exactly { event, data }; anything with other keys, or
    /// an event outside the five it emits, is some other protocol's payload.
    function mcpParseFrame(msg) {
        if (!msg) return null;
        var raw = msg.data;
        var obj = raw;
        if (typeof raw === 'string') {
            var trimmed = raw.trim();
            if (trimmed.charAt(0) !== '{') return null;
            try { obj = JSON.parse(trimmed); } catch { return null; }
        }
        if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return null;
        if (MCP_FRAME_EVENTS.indexOf(obj.event) < 0 || !('data' in obj)) return null;
        var keys = Object.keys(obj);
        for (var i = 0; i < keys.length; i++) {
            if (keys[i] !== 'event' && keys[i] !== 'data') return null;
        }
        if (obj.event === 'task' && !mcpIsTaskData(obj.data)) return null;
        return { event: obj.event, data: obj.data };
    }

    function mcpIsTaskData(data) {
        return !!data && typeof data === 'object'
            && typeof data.taskId === 'string' && typeof data.status === 'string';
    }

    function mcpIsTerminalStatus(status) {
        return MCP_TASK_TERMINAL.indexOf(status) >= 0;
    }

    /// A status the CSS has a colour for; anything new from a later spec
    /// revision is shown under its own name in the neutral colour.
    function mcpStatusClass(status) {
        return MCP_TASK_STATUSES.indexOf(status) >= 0 ? status.replace('_', '-') : 'unknown';
    }

    /// Folds a stream's messages into the task they report on: the latest
    /// task frame wins, the result / error frame after it ends it. Null when
    /// the stream carries no task frame — an ordinary tool call.
    function mcpTaskSummary(messages) {
        var summary = null;
        if (!Array.isArray(messages)) return null;
        for (var i = 0; i < messages.length; i++) {
            var frame = mcpParseFrame(messages[i]);
            if (!frame) continue;
            if (frame.event === 'task') {
                summary = summary || { task: null, result: null, error: null, updates: 0, firstIndex: i };
                // A frame for a different task means a second task started
                // on the same stream — show the newest one.
                if (summary.task && summary.task.taskId !== frame.data.taskId) {
                    summary = { task: null, result: null, error: null, updates: 0, firstIndex: i };
                }
                summary.task = frame.data;
                summary.updates++;
                summary.lastIndex = i;
            } else if (summary && frame.event === 'result') {
                summary.result = frame.data;
                summary.resultIndex = i;
            } else if (summary && frame.event === 'error') {
                summary.error = frame.data || {};
                summary.errorIndex = i;
            }
        }
        if (!summary) return null;
        var status = summary.task.status;
        summary.terminal = mcpIsTerminalStatus(status) || summary.result !== null || summary.error !== null;
        // The stream's last word decides what the card says happened: a
        // result means completed, an error frame failed (or TTL elapsed),
        // even when no terminal task frame came before it.
        summary.outcome = summary.result !== null ? 'completed'
            : (summary.error !== null ? 'failed' : status);
        return summary;
    }

    // ---- time ----

    function mcpParseTime(iso) {
        if (!iso) return null;
        var ms = Date.parse(iso);
        return isNaN(ms) ? null : ms;
    }

    /// When the server may forget the task, or null for no TTL (unlimited)
    /// or no creation time to count from.
    function mcpExpiresAt(createdAt, ttlMs) {
        var created = mcpParseTime(createdAt);
        if (created === null || typeof ttlMs !== 'number') return null;
        return created + ttlMs;
    }

    /// A duration as the card shows it: '45s', '3m 05s', '2h 03m', '4d 2h'.
    function mcpFormatDuration(ms) {
        if (typeof ms !== 'number' || isNaN(ms)) return '';
        var total = Math.max(0, Math.floor(ms / 1000));
        var d = Math.floor(total / 86400);
        var h = Math.floor((total % 86400) / 3600);
        var m = Math.floor((total % 3600) / 60);
        var s = total % 60;
        var pad = function (n) { return n < 10 ? '0' + n : String(n); };
        if (d > 0) return d + 'd ' + h + 'h';
        if (h > 0) return h + 'h ' + pad(m) + 'm';
        if (m > 0) return m + 'm ' + pad(s) + 's';
        return s + 's';
    }

    // ---- input requests ----

    /// The outstanding requests of an input_required task, in a stable
    /// order, each marked answered when this client already sent a
    /// response for its key. The server re-sends outstanding requests on
    /// every poll, and a key is unique per task, so a key once answered is
    /// never offered again.
    function mcpInputRequestList(inputRequests, answeredKeys) {
        if (!inputRequests || typeof inputRequests !== 'object') return [];
        var answered = answeredKeys || {};
        return Object.keys(inputRequests).sort().map(function (key) {
            var req = inputRequests[key] || {};
            var params = req.params || {};
            var method = typeof req.method === 'string' ? req.method : '';
            var kind = method === 'elicitation/create' ? 'elicitation'
                : (method === 'sampling/createMessage' ? 'sampling' : 'other');
            return {
                key: key,
                method: method,
                kind: kind,
                mode: params.mode || (params.url ? 'url' : 'form'),
                message: typeof params.message === 'string' ? params.message : '',
                url: typeof params.url === 'string' ? params.url : null,
                schema: params.requestedSchema || null,
                params: params,
                answered: !!answered[key]
            };
        });
    }

    var MCP_STRING_FORMAT_INPUT = { email: 'email', uri: 'url', date: 'date', 'date-time': 'datetime-local' };

    function mcpEnumOptions(values, titles) {
        return values.map(function (v, i) {
            return { value: String(v), label: (titles && titles[i] != null) ? String(titles[i]) : String(v) };
        });
    }

    function mcpOneOfOptions(list) {
        return list.filter(function (o) { return o && o.const !== undefined; }).map(function (o) {
            return { value: String(o.const), label: o.title != null ? String(o.title) : String(o.const) };
        });
    }

    /// An elicitation's requestedSchema as the fields of a form. The spec
    /// restricts the schema to a flat object of primitives — string (with
    /// format, enum or oneOf), number / integer, boolean, and an array of
    /// enum values for a multi-select — so this is a table, not a general
    /// JSON-Schema renderer. A property it cannot map comes back as kind
    /// 'unsupported' and is shown, not silently dropped.
    function mcpElicitFields(schema) {
        if (!schema || typeof schema !== 'object' || !schema.properties || typeof schema.properties !== 'object') return [];
        var required = Array.isArray(schema.required) ? schema.required : [];
        return Object.keys(schema.properties).map(function (key) {
            var p = schema.properties[key] || {};
            var field = {
                key: key,
                label: typeof p.title === 'string' && p.title ? p.title : key,
                description: typeof p.description === 'string' ? p.description : '',
                required: required.indexOf(key) >= 0,
                kind: 'unsupported',
                options: null,
                min: null,
                max: null,
                inputType: 'text',
                default: p.default
            };
            if (p.type === 'boolean') {
                field.kind = 'checkbox';
            } else if (p.type === 'number' || p.type === 'integer') {
                field.kind = p.type;
                field.inputType = 'number';
                if (typeof p.minimum === 'number') field.min = p.minimum;
                if (typeof p.maximum === 'number') field.max = p.maximum;
            } else if (p.type === 'string') {
                if (Array.isArray(p.oneOf)) {
                    field.kind = 'select';
                    field.options = mcpOneOfOptions(p.oneOf);
                } else if (Array.isArray(p.enum)) {
                    field.kind = 'select';
                    field.options = mcpEnumOptions(p.enum, Array.isArray(p.enumNames) ? p.enumNames : null);
                } else {
                    field.kind = 'text';
                    field.inputType = MCP_STRING_FORMAT_INPUT[p.format] || 'text';
                    if (typeof p.minLength === 'number') field.min = p.minLength;
                    if (typeof p.maxLength === 'number') field.max = p.maxLength;
                }
            } else if (p.type === 'array' && p.items && typeof p.items === 'object') {
                var items = p.items;
                var opts = Array.isArray(items.anyOf) ? mcpOneOfOptions(items.anyOf)
                    : (Array.isArray(items.oneOf) ? mcpOneOfOptions(items.oneOf)
                        : (Array.isArray(items.enum) ? mcpEnumOptions(items.enum, null) : null));
                if (opts) {
                    field.kind = 'multiselect';
                    field.options = opts;
                    if (typeof p.minItems === 'number') field.min = p.minItems;
                    if (typeof p.maxItems === 'number') field.max = p.maxItems;
                }
            }
            return field;
        });
    }

    /// The value a field starts with: the draft the operator already typed,
    /// else the schema's default, else empty.
    function mcpFieldInitial(field, draft) {
        if (draft !== undefined) return draft;
        if (field.default !== undefined && field.default !== null) {
            if (field.kind === 'multiselect') return Array.isArray(field.default) ? field.default.map(String) : [];
            if (field.kind === 'checkbox') return field.default === true;
            return String(field.default);
        }
        if (field.kind === 'multiselect') return [];
        if (field.kind === 'checkbox') return false;
        return '';
    }

    /// Turns the raw form values (strings, a boolean for a checkbox, an
    /// array for a multi-select) into the elicitation's `content`, checking
    /// what the schema asks for. Empty optional fields are left out.
    /// Returns { content, errors } — errors keyed by field, each a reason
    /// code ('required', 'number', 'integer', 'min', 'max', 'option').
    function mcpBuildElicitContent(fields, values) {
        var content = {};
        var errors = {};
        var raw = values || {};
        fields.forEach(function (f) {
            var v = raw[f.key];
            if (f.kind === 'checkbox') {
                // A checkbox always has an answer; unticked is false.
                content[f.key] = v === true || v === 'true';
                return;
            }
            if (f.kind === 'multiselect') {
                var list = Array.isArray(v) ? v.slice() : [];
                if (list.length === 0) {
                    if (f.required) errors[f.key] = 'required';
                    return;
                }
                if (f.min !== null && list.length < f.min) { errors[f.key] = 'min'; return; }
                if (f.max !== null && list.length > f.max) { errors[f.key] = 'max'; return; }
                content[f.key] = list;
                return;
            }
            var text = v == null ? '' : String(v);
            if (text.trim() === '') {
                if (f.required) errors[f.key] = 'required';
                return;
            }
            if (f.kind === 'number' || f.kind === 'integer') {
                var n = Number(text);
                if (isNaN(n)) { errors[f.key] = 'number'; return; }
                if (f.kind === 'integer' && Math.floor(n) !== n) { errors[f.key] = 'integer'; return; }
                if (f.min !== null && n < f.min) { errors[f.key] = 'min'; return; }
                if (f.max !== null && n > f.max) { errors[f.key] = 'max'; return; }
                content[f.key] = n;
                return;
            }
            if (f.kind === 'select') {
                var ok = (f.options || []).some(function (o) { return o.value === text; });
                if (!ok) { errors[f.key] = 'option'; return; }
            }
            if (f.kind === 'text') {
                if (f.min !== null && text.length < f.min) { errors[f.key] = 'min'; return; }
                if (f.max !== null && text.length > f.max) { errors[f.key] = 'max'; return; }
            }
            content[f.key] = text;
        });
        return { content: content, errors: errors };
    }

    /// The Tasks/update body for one answered request. Decline and cancel
    /// carry no content, per the elicitation spec.
    function mcpBuildUpdateBody(taskId, key, action, content) {
        var response = { action: action };
        if (action === 'accept') response.content = content || {};
        var inputResponses = {};
        inputResponses[key] = response;
        return { taskId: taskId, inputResponses: inputResponses };
    }

    // ---- tasks still running, per workspace ----
    //
    // A reload drops the stream but not the task: the server keeps it until
    // its TTL runs out. These helpers keep the list the workbench persists so
    // it can offer to follow such a task again. All of them return a new
    // list and leave their input alone.

    var MCP_PENDING_LIMIT = 50;
    // An expired entry is shown as such for a day, then dropped.
    var MCP_EXPIRED_GRACE_MS = 24 * 60 * 60 * 1000;

    /// The entry a task frame turns into. `ctx` names where the task runs:
    /// the server URL and, when known, the tool that started it.
    function mcpPendingEntry(task, ctx, nowMs) {
        return {
            serverUrl: (ctx && ctx.serverUrl) || '',
            tool: (ctx && ctx.tool) || null,
            taskId: task.taskId,
            shape: task.shape || null,
            status: task.status,
            createdAt: task.createdAt || null,
            ttlMs: typeof task.ttlMs === 'number' ? task.ttlMs : null,
            savedAt: typeof nowMs === 'number' ? nowMs : Date.now()
        };
    }

    function mcpSamePending(a, b) {
        return a.serverUrl === b.serverUrl && a.taskId === b.taskId;
    }

    /// Records what a task frame says. A terminal task leaves the list —
    /// there is nothing left to follow. A frame from Tasks/follow knows no
    /// tool, so the tool the entry was saved with is kept.
    function mcpUpsertPending(list, entry) {
        var current = Array.isArray(list) ? list : [];
        var existing = null;
        var rest = current.filter(function (e) {
            if (e && mcpSamePending(e, entry)) { existing = e; return false; }
            return !!e;
        });
        if (mcpIsTerminalStatus(entry.status)) return rest;
        var merged = Object.assign({}, entry);
        if (!merged.tool && existing && existing.tool) merged.tool = existing.tool;
        if (!merged.createdAt && existing && existing.createdAt) merged.createdAt = existing.createdAt;
        rest.push(merged);
        return rest.length > MCP_PENDING_LIMIT ? rest.slice(rest.length - MCP_PENDING_LIMIT) : rest;
    }

    function mcpRemovePending(list, serverUrl, taskId) {
        return (Array.isArray(list) ? list : []).filter(function (e) {
            return e && !(e.serverUrl === serverUrl && e.taskId === taskId);
        });
    }

    /// Past its TTL: the server may have discarded it. An entry without a
    /// TTL (unlimited) or without a creation time never expires here.
    function mcpPendingExpired(entry, nowMs) {
        var at = mcpExpiresAt(entry.createdAt, entry.ttlMs);
        return at !== null && nowMs >= at;
    }

    /// Drops entries that expired more than a day ago, and anything that
    /// is not an entry at all (a hand-edited or older storage value).
    function mcpPrunePending(list, nowMs) {
        return (Array.isArray(list) ? list : []).filter(function (e) {
            if (!e || typeof e !== 'object' || typeof e.taskId !== 'string' || typeof e.serverUrl !== 'string') return false;
            var at = mcpExpiresAt(e.createdAt, e.ttlMs);
            return at === null || nowMs < at + MCP_EXPIRED_GRACE_MS;
        });
    }

    /// The entries to offer for one server — for one tool when `tool` is
    /// given, every one when it is null (the Tasks/follow method) — newest
    /// first, each marked expired or not.
    function mcpPendingFor(list, serverUrl, tool, nowMs) {
        return mcpPrunePending(list, nowMs)
            .filter(function (e) { return e.serverUrl === serverUrl && (tool == null || e.tool === tool); })
            .sort(function (a, b) { return (b.savedAt || 0) - (a.savedAt || 0); })
            .map(function (e) { return Object.assign({}, e, { expired: mcpPendingExpired(e, nowMs) }); });
    }
