    // ---- MCP tasks (#801): the workbench surface ----
    //
    // A tool call the server runs as a task streams the task's status
    // instead of answering at once. This fragment draws that status as a
    // card above the frame list, answers the task's input requests through
    // Tasks/update, cancels it through Tasks/cancel, and keeps a list of
    // tasks still running so a reload can follow one again through
    // Tasks/follow. Every decision about frames, schemas and the list lives
    // in mcp-tasks-model.js; this file is the DOM, the calls and storage.
    //
    // Two things the card says out loud because they are easy to get wrong:
    // closing the stream (Stop) only stops following — the task keeps
    // running on the server — and cancelling is cooperative: the server is
    // asked to stop, and the task may still complete or fail.

    var MCP_PENDING_STORAGE_KEY = 'bowire_mcp_tasks';

    /// Per-task UI state that must survive a re-render: which input keys
    /// were answered, what the operator typed so far, field errors, the
    /// notes the server sent back, and which call is in flight.
    var mcpTaskUi = {};
    var mcpTaskTicker = null;

    function mcpTaskUiFor(taskId) {
        if (!mcpTaskUi[taskId]) {
            mcpTaskUi[taskId] = { answered: {}, drafts: {}, errors: {}, notices: [], busy: null };
        }
        return mcpTaskUi[taskId];
    }

    // ---- persistence ----

    function mcpLoadPending() {
        try {
            var raw = localStorage.getItem(wsKey(MCP_PENDING_STORAGE_KEY));
            var parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed : [];
        } catch { return []; }
    }

    function mcpSavePending(list) {
        try {
            if (list.length === 0) localStorage.removeItem(wsKey(MCP_PENDING_STORAGE_KEY));
            else localStorage.setItem(wsKey(MCP_PENDING_STORAGE_KEY), JSON.stringify(list));
        } catch { /* storage is a convenience here; the task itself lives on the server */ }
    }

    /// Called by invokeStreaming for every frame. Keeps the persisted list
    /// in step with the stream: a running task is saved, a finished one
    /// removed. `svcObj` is the service the stream was started on, captured
    /// at start, so a method switch mid-stream cannot misfile the task.
    function mcpTaskObserveStream(svcObj, methodName, messages) {
        if (!svcObj || svcObj.source !== 'mcp' || !Array.isArray(messages) || messages.length === 0) return;
        var last = mcpParseFrame(messages[messages.length - 1]);
        if (!last || (last.event !== 'task' && last.event !== 'result' && last.event !== 'error')) return;
        var summary = mcpTaskSummary(messages);
        if (!summary) return;
        var now = Date.now();
        var ctx = {
            serverUrl: svcObj.originUrl || '',
            tool: svcObj.name === 'Tasks' ? null : methodName
        };
        var entry = mcpPendingEntry(summary.task, ctx, now);
        if (summary.terminal) entry.status = summary.outcome === 'working' || summary.outcome === 'input_required' ? 'failed' : summary.outcome;
        mcpSavePending(mcpUpsertPending(mcpPrunePending(mcpLoadPending(), now), entry));
    }

    // ---- calls ----

    /// One unary call on the Tasks service of the server at `serverUrl`.
    /// Headless, like the compare runner: it touches no request state.
    async function mcpTaskCall(serverUrl, methodName, body) {
        try {
            var resp = await fetch(config.prefix + '/api/invoke' + serverUrlParamForService({ originUrl: serverUrl }, false), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: 'Tasks',
                    method: methodName,
                    messages: [JSON.stringify(body)],
                    metadata: null,
                    protocol: 'mcp'
                })
            });
            var r = await resp.json();
            if (!resp.ok || (r && r.title)) {
                return { ok: false, error: (r && (r.detail || r.title)) || ('HTTP ' + resp.status) };
            }
            var payload = r ? r.response : null;
            if (typeof payload === 'string') {
                try { payload = JSON.parse(payload); } catch { /* keep the text */ }
            }
            // A plugin that failed the call reports it on the status.
            if (r && r.status && r.status !== 'OK' && (!payload || typeof payload !== 'object')) {
                return { ok: false, error: String(r.status) };
            }
            return { ok: true, response: payload };
        } catch (e) {
            return { ok: false, error: (e && e.message) || 'network error' };  // i18n-exempt: transport error text, shown beside the server's own messages
        }
    }

    function mcpTaskNotice(taskId, kind, text) {
        var ui = mcpTaskUiFor(taskId);
        ui.notices.push({ kind: kind, text: text, at: Date.now() });
        if (ui.notices.length > 5) ui.notices = ui.notices.slice(ui.notices.length - 5);
    }

    async function mcpTaskCancel(serverUrl, taskId) {
        var ui = mcpTaskUiFor(taskId);
        if (ui.busy) return;
        ui.busy = 'cancel';
        render();
        var res = await mcpTaskCall(serverUrl, 'cancel', { taskId: taskId });
        ui.busy = null;
        if (res.ok) {
            var r = res.response || {};
            var text = r.note || t('mcpTask.cancel.sent');
            var status = r.task && r.task.status;
            if (status) text += ' ' + t('mcpTask.cancel.statusNow', { status: mcpStatusLabel(status) });
            mcpTaskNotice(taskId, 'info', text);
        } else {
            mcpTaskNotice(taskId, 'error', t('mcpTask.cancel.failed', { error: res.error }));
        }
        render();
    }

    async function mcpTaskAnswer(serverUrl, taskId, key, action, fields) {
        var ui = mcpTaskUiFor(taskId);
        if (ui.busy || ui.answered[key]) return;
        var content = null;
        if (action === 'accept') {
            var built = mcpBuildElicitContent(fields, ui.drafts[key] || {});
            ui.errors[key] = built.errors;
            if (Object.keys(built.errors).length > 0) { render(); return; }
            content = built.content;
        }
        ui.busy = 'answer:' + key;
        render();
        var res = await mcpTaskCall(serverUrl, 'update', mcpBuildUpdateBody(taskId, key, action, content));
        ui.busy = null;
        if (res.ok) {
            ui.answered[key] = action;
            mcpTaskNotice(taskId, 'info', (res.response && res.response.note) || t('mcpTask.input.sent'));
        } else {
            // A 2025-11-25 task cannot take input this way; the plugin says
            // so in its status message, which is what lands here.
            mcpTaskNotice(taskId, 'error', t('mcpTask.input.failed', { error: res.error }));
        }
        render();
    }

    /// Follow a task again: open the server's Tasks/follow method with the
    /// id filled in and start the stream. The card then draws there.
    function mcpTaskResume(serverUrl, taskId) {
        var found = null;
        for (var i = 0; i < services.length && !found; i++) {
            var svc = services[i];
            if (svc.name !== 'Tasks' || svc.source !== 'mcp' || (svc.originUrl || '') !== serverUrl) continue;
            for (var j = 0; j < svc.methods.length; j++) {
                if (svc.methods[j].name === 'follow') { found = { svc: svc, method: svc.methods[j] }; break; }
            }
        }
        if (!found) {
            toast(t('mcpTask.resume.noService', { server: serverUrl }), 'error');
            return;
        }
        openTab(found.svc, found.method);
        var S = activeState();
        var body = JSON.stringify({ taskId: taskId }, null, 2);
        S.requestMessages = [body];
        if (typeof syncJsonToForm === 'function') syncJsonToForm(S);
        invokeStreaming('Tasks', 'follow', [body], null);  // i18n-exempt: service and method names on the wire
    }

    function mcpTaskForget(serverUrl, taskId) {
        mcpSavePending(mcpRemovePending(mcpLoadPending(), serverUrl, taskId));
        render();
    }

    // ---- labels ----

    function mcpStatusLabel(status) {
        if (MCP_TASK_STATUSES.indexOf(status) < 0) return String(status);
        // Catalogue keys are camel-case; the wire status is snake-case.
        return t('mcpTask.status.' + (status === 'input_required' ? 'inputRequired' : status));
    }

    function mcpViaLabel(via) {
        if (via === 'create' || via === 'poll' || via === 'notification') return t('mcpTask.via.' + via);
        return via ? String(via) : '';
    }

    function mcpTimeOfDay(iso) {
        var ms = mcpParseTime(iso);
        if (ms === null) return '';
        try { return new Date(ms).toLocaleTimeString(); } catch { return iso; }
    }

    // ---- frame list badge ----

    /// The badge a stream row gets for an MCP frame: the frame kind, and
    /// for a task frame its status. Null for every other payload.
    function mcpFrameBadge(msg) {
        var frame = mcpParseFrame(msg);
        if (!frame) return null;
        if (frame.event === 'task') {
            return el('span', {
                className: 'bowire-mcp-frame-badge bowire-mcp-frame-task is-' + mcpStatusClass(frame.data.status),
                textContent: t('mcpTask.frame.task', { status: mcpStatusLabel(frame.data.status) }),
                title: t('mcpTask.frame.taskTitle', { taskId: frame.data.taskId, via: mcpViaLabel(frame.data.via) })
            });
        }
        return el('span', {
            className: 'bowire-mcp-frame-badge bowire-mcp-frame-' + frame.event,
            textContent: frame.event.toUpperCase(),
            title: t('mcpTask.frame.title', { kind: frame.event })
        });
    }

    /// Whether a new stream frame changes the task card, so the caller
    /// should re-render rather than only append the row.
    function mcpTaskFrameNeedsRender(S, msg) {
        var frame = mcpParseFrame(msg);
        if (!frame) return false;
        if (frame.event === 'task') return true;
        return (frame.event === 'result' || frame.event === 'error') && !!mcpTaskSummary(S.streamMessages);
    }

    // ---- the card ----

    function mcpFact(label, valueNode) {
        return el('div', { className: 'bowire-mcp-task-fact' },
            el('dt', { textContent: label }),
            el('dd', null, valueNode));
    }

    function mcpTickingSpan(kind, atMs, frozenText) {
        // The ticker rewrites these once a second while a task runs; a
        // finished task gets fixed text and no data attribute.
        if (frozenText !== undefined) return el('span', { textContent: frozenText });
        var span = el('span', { dataset: { mcpTick: kind, mcpAt: String(atMs) } });
        span.textContent = mcpTickText(kind, atMs, Date.now());
        return span;
    }

    function mcpTickText(kind, atMs, now) {
        if (kind === 'elapsed') return mcpFormatDuration(now - atMs);
        var left = atMs - now;
        return left <= 0 ? t('mcpTask.ttl.expired') : t('mcpTask.ttl.left', { duration: mcpFormatDuration(left) });
    }

    function mcpEnsureTicker() {
        if (mcpTaskTicker) return;
        mcpTaskTicker = setInterval(function () {
            var nodes = document.querySelectorAll('[data-mcp-tick]');
            if (nodes.length === 0) {
                clearInterval(mcpTaskTicker);
                mcpTaskTicker = null;
                return;
            }
            var now = Date.now();
            nodes.forEach(function (n) {
                n.textContent = mcpTickText(n.dataset.mcpTick, Number(n.dataset.mcpAt), now);
            });
        }, 1000);
    }

    function mcpCopyButton(text) {
        return el('button', {
            type: 'button',
            className: 'bowire-pane-btn bowire-mcp-task-copy',
            title: t('mcpTask.copyId'),
            textContent: t('mcpTask.copy'),
            dataset: { mcpCopy: text },
            onClick: function (e) {
                var value = e.currentTarget.dataset.mcpCopy;
                try {
                    navigator.clipboard.writeText(value).then(function () {
                        toast(t('mcpTask.copied'), 'success');
                    }, function () { /* clipboard refused; the id is selectable */ });
                } catch { /* no clipboard API: the id is selectable text */ }
            }
        });
    }

    /// The task card for a stream, or null when the stream carries no
    /// task. `svc` is the service the pane shows (Tools or Tasks); its
    /// originUrl is the server every call from the card goes to.
    function renderMcpTaskCard(S, svc, method, pid) {
        if (!svc || svc.source !== 'mcp' || !S || !Array.isArray(S.streamMessages)) return null;
        var summary = mcpTaskSummary(S.streamMessages);
        if (!summary) return null;
        var task = summary.task;
        var serverUrl = svc.originUrl || '';
        var ui = mcpTaskUiFor(task.taskId);
        var status = summary.terminal ? summary.outcome : task.status;
        var live = !summary.terminal;

        var card = el('section', {
            className: 'bowire-mcp-task-card is-' + mcpStatusClass(status),
            id: pid('bowire-mcp-task-card'),
            'aria-label': t('mcpTask.title'),
            dataset: { taskId: task.taskId, mcpStatus: status }
        });

        // Head: status, id, the one action.
        var head = el('div', { className: 'bowire-mcp-task-head' },
            el('span', {
                className: 'bowire-mcp-task-chip is-' + mcpStatusClass(status),
                textContent: mcpStatusLabel(status)
            }),
            el('span', { className: 'bowire-mcp-task-title', textContent: t('mcpTask.title') }),
            el('code', { className: 'bowire-mcp-task-id', textContent: task.taskId }),
            mcpCopyButton(task.taskId),
            el('span', { className: 'bowire-mcp-task-spacer' }));
        if (live) {
            if (!S.isExecuting) {
                head.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-pane-btn bowire-mcp-task-resume',
                    textContent: t('mcpTask.resume.button'),
                    dataset: { server: serverUrl, task: task.taskId },
                    onClick: function (e) {
                        var d = e.currentTarget.dataset;
                        mcpTaskResume(d.server, d.task);
                    }
                }));
            }
            head.appendChild(el('button', {
                type: 'button',
                className: 'bowire-pane-btn bowire-mcp-task-cancel',
                textContent: ui.busy === 'cancel' ? t('mcpTask.cancel.busy') : t('mcpTask.cancel.button'),
                title: t('mcpTask.cancel.title'),
                disabled: !!ui.busy,
                dataset: { server: serverUrl, task: task.taskId },
                onClick: function (e) {
                    var d = e.currentTarget.dataset;
                    mcpTaskCancel(d.server, d.task);
                }
            }));
        }
        card.appendChild(head);

        if (task.statusMessage) {
            card.appendChild(el('div', { className: 'bowire-mcp-task-message', textContent: task.statusMessage }));
        }

        // Facts.
        var facts = el('dl', { className: 'bowire-mcp-task-facts' });
        facts.appendChild(mcpFact(t('mcpTask.fact.spec'), el('span', {
            textContent: task.shape ? t('mcpTask.shape', { shape: task.shape }) : t('mcpTask.shapeUnknown')
        })));
        var created = mcpParseTime(task.createdAt);
        if (created !== null) {
            var ended = mcpParseTime(task.lastUpdatedAt);
            facts.appendChild(mcpFact(live ? t('mcpTask.fact.running') : t('mcpTask.fact.ran'),
                live ? mcpTickingSpan('elapsed', created)
                    : mcpTickingSpan('elapsed', created, ended !== null ? mcpFormatDuration(ended - created) : '')));
        }
        var expires = mcpExpiresAt(task.createdAt, task.ttlMs);
        facts.appendChild(mcpFact(t('mcpTask.fact.ttl'),
            task.ttlMs === null || task.ttlMs === undefined
                ? el('span', { textContent: t('mcpTask.ttl.unlimited') })
                : (expires !== null && live
                    ? mcpTickingSpan('ttl', expires)
                    : el('span', { textContent: mcpFormatDuration(task.ttlMs) }))));
        if (task.via) {
            var lastAt = mcpTimeOfDay(task.lastUpdatedAt);
            facts.appendChild(mcpFact(t('mcpTask.fact.via'), el('span', {
                textContent: lastAt ? t('mcpTask.viaAt', { via: mcpViaLabel(task.via), time: lastAt }) : mcpViaLabel(task.via)
            })));
        }
        if (typeof task.pollIntervalMs === 'number') {
            facts.appendChild(mcpFact(t('mcpTask.fact.poll'), el('span', {
                textContent: t('mcpTask.pollMs', { ms: task.pollIntervalMs })
            })));
        }
        card.appendChild(facts);

        // Input the server is waiting for.
        if (live && task.status === 'input_required') {
            var requests = mcpInputRequestList(task.inputRequests, ui.answered);
            if (requests.length > 0) {
                var inputBox = el('div', { className: 'bowire-mcp-task-inputs' },
                    el('div', { className: 'bowire-mcp-task-inputs-title', textContent: t('mcpTask.input.title') }));
                requests.forEach(function (req) { inputBox.appendChild(renderMcpInputRequest(serverUrl, task, req, ui)); });
                card.appendChild(inputBox);
            }
        }

        // What it ended in.
        if (summary.result !== null) {
            var text = mcpResultText(summary.result);
            // The task completed, but the tool inside it may have failed:
            // a CallToolResult says so with isError.
            var toolFailed = summary.result && summary.result.isError === true;
            card.appendChild(el('div', { className: 'bowire-mcp-task-outcome ' + (toolFailed ? 'is-failed' : 'is-completed') },
                el('div', {
                    className: 'bowire-mcp-task-outcome-title',
                    textContent: toolFailed ? t('mcpTask.outcome.toolError') : t('mcpTask.outcome.result')
                }),
                el('pre', { className: 'bowire-mcp-task-outcome-body', textContent: text })));
        } else if (summary.error !== null) {
            card.appendChild(el('div', { className: 'bowire-mcp-task-outcome is-failed' },
                el('div', { className: 'bowire-mcp-task-outcome-title', textContent: t('mcpTask.outcome.error') }),
                el('pre', {
                    className: 'bowire-mcp-task-outcome-body',
                    textContent: summary.error.message || JSON.stringify(summary.error, null, 2)
                })));
        }

        // Replies to what the card sent.
        ui.notices.forEach(function (n) {
            card.appendChild(el('div', {
                className: 'bowire-mcp-task-notice is-' + n.kind,
                role: n.kind === 'error' ? 'alert' : 'status',
                textContent: n.text
            }));
        });

        if (live) {
            card.appendChild(el('div', {
                className: 'bowire-mcp-task-hint',
                textContent: S.isExecuting ? t('mcpTask.hint.following') : t('mcpTask.hint.notFollowing')
            }));
            mcpEnsureTicker();
        }
        return card;
    }

    /// A CallToolResult's text parts, joined; anything else as JSON.
    function mcpResultText(result) {
        if (result && Array.isArray(result.content)) {
            var parts = result.content.map(function (c) {
                return c && c.type === 'text' && typeof c.text === 'string' ? c.text : JSON.stringify(c);
            });
            return parts.join('\n');
        }
        try { return JSON.stringify(result, null, 2); } catch { return String(result); }
    }

    function renderMcpInputRequest(serverUrl, task, req, ui) {
        var box = el('div', {
            className: 'bowire-mcp-task-request' + (req.answered ? ' is-answered' : ''),
            dataset: { key: req.key }
        });
        box.appendChild(el('div', { className: 'bowire-mcp-task-request-head' },
            el('code', { className: 'bowire-mcp-task-request-key', textContent: req.key }),
            el('span', { className: 'bowire-mcp-task-request-method', textContent: req.method })));
        if (req.message) {
            box.appendChild(el('div', { className: 'bowire-mcp-task-request-message', textContent: req.message }));
        }
        if (req.answered) {
            box.appendChild(el('div', {
                className: 'bowire-mcp-task-request-state',
                textContent: ui.answered[req.key] === 'decline' ? t('mcpTask.input.declined') : t('mcpTask.input.answered')
            }));
            return box;
        }
        if (req.kind === 'sampling') {
            box.appendChild(el('div', { className: 'bowire-mcp-task-request-state', textContent: t('mcpTask.input.sampling') }));
            box.appendChild(el('pre', { className: 'bowire-mcp-task-request-raw', textContent: JSON.stringify(req.params, null, 2) }));
            return box;
        }
        if (req.kind !== 'elicitation' || req.mode !== 'form' || !req.schema) {
            box.appendChild(el('div', { className: 'bowire-mcp-task-request-state', textContent: t('mcpTask.input.unsupported') }));
            box.appendChild(el('pre', { className: 'bowire-mcp-task-request-raw', textContent: JSON.stringify(req.params, null, 2) }));
            return box;
        }

        var fields = mcpElicitFields(req.schema);
        var drafts = ui.drafts[req.key] || (ui.drafts[req.key] = {});
        var errors = ui.errors[req.key] || {};
        var form = el('form', {
            className: 'bowire-mcp-task-form',
            onSubmit: function (e) { e.preventDefault(); }
        });
        fields.forEach(function (f) {
            if (drafts[f.key] === undefined) drafts[f.key] = mcpFieldInitial(f, undefined);
            form.appendChild(renderMcpElicitField(task.taskId, req.key, f, drafts[f.key], errors[f.key]));
        });
        var busy = ui.busy === 'answer:' + req.key;
        var actions = el('div', { className: 'bowire-mcp-task-form-actions' },
            el('button', {
                type: 'button',
                className: 'bowire-pane-btn bowire-mcp-task-accept is-primary',
                textContent: busy ? t('mcpTask.input.sending') : t('mcpTask.input.accept'),
                disabled: !!ui.busy,
                onClick: function () { mcpTaskAnswer(serverUrl, task.taskId, req.key, 'accept', fields); }
            }),
            el('button', {
                type: 'button',
                className: 'bowire-pane-btn bowire-mcp-task-decline',
                textContent: t('mcpTask.input.decline'),
                disabled: !!ui.busy,
                onClick: function () { mcpTaskAnswer(serverUrl, task.taskId, req.key, 'decline', fields); }
            }));
        form.appendChild(actions);
        box.appendChild(form);
        return box;
    }

    function mcpFieldErrorText(field, code) {
        switch (code) {
            case 'required': return t('mcpTask.field.required');
            case 'number': return t('mcpTask.field.number');
            case 'integer': return t('mcpTask.field.integer');
            case 'min': return t('mcpTask.field.min', { min: field.min });
            case 'max': return t('mcpTask.field.max', { max: field.max });
            case 'option': return t('mcpTask.field.option');
            default: return String(code);
        }
    }

    function renderMcpElicitField(taskId, key, field, value, errorCode) {
        var id = 'bowire-mcp-field-' + String(taskId).replace(/[^A-Za-z0-9_-]/g, '_') + '-'
            + String(key).replace(/[^A-Za-z0-9_-]/g, '_') + '-' + field.key.replace(/[^A-Za-z0-9_-]/g, '_');
        var store = function (v) {
            var ui = mcpTaskUiFor(taskId);
            (ui.drafts[key] || (ui.drafts[key] = {}))[field.key] = v;
            if (ui.errors[key]) delete ui.errors[key][field.key];
        };
        var row = el('div', {
            className: 'bowire-mcp-task-field is-' + field.kind + (errorCode ? ' has-error' : ''),
            dataset: { field: field.key }
        });
        var label = el('label', { className: 'bowire-mcp-task-field-label', for: id },
            field.label, field.required ? el('span', { className: 'bowire-mcp-task-field-req', textContent: ' *' }) : null);
        var input;
        if (field.kind === 'checkbox') {
            input = el('input', {
                type: 'checkbox', id: id, className: 'bowire-mcp-task-input', checked: value === true,
                onChange: function (e) { store(e.target.checked); }
            });
            row.appendChild(el('div', { className: 'bowire-mcp-task-check' }, input, label));
        } else {
            row.appendChild(label);
            if (field.kind === 'select' || field.kind === 'multiselect') {
                var multi = field.kind === 'multiselect';
                var chosen = multi ? (Array.isArray(value) ? value : []) : [String(value)];
                input = el('select', {
                    id: id, className: 'bowire-mcp-task-input', multiple: multi ? true : undefined,
                    onChange: function (e) {
                        if (multi) {
                            store(Array.prototype.filter.call(e.target.options, function (o) { return o.selected; })
                                .map(function (o) { return o.value; }));
                        } else {
                            store(e.target.value);
                        }
                    }
                });
                if (!multi && !field.required) input.appendChild(el('option', { value: '', textContent: '—' }));
                (field.options || []).forEach(function (o) {
                    input.appendChild(el('option', {
                        value: o.value, textContent: o.label,
                        selected: chosen.indexOf(o.value) >= 0 ? true : undefined
                    }));
                });
            } else if (field.kind === 'unsupported') {
                input = el('div', { className: 'bowire-mcp-task-field-unsupported', textContent: t('mcpTask.field.unsupported') });
            } else {
                input = el('input', {
                    type: field.inputType, id: id, className: 'bowire-mcp-task-input',
                    value: value == null ? '' : String(value),
                    min: field.kind !== 'text' && field.min !== null ? String(field.min) : undefined,
                    max: field.kind !== 'text' && field.max !== null ? String(field.max) : undefined,
                    step: field.kind === 'integer' ? '1' : (field.kind === 'number' ? 'any' : undefined),
                    onInput: function (e) { store(e.target.value); }
                });
            }
            row.appendChild(input);
        }
        if (field.description) {
            row.appendChild(el('div', { className: 'bowire-mcp-task-field-desc', textContent: field.description }));
        }
        if (errorCode) {
            row.appendChild(el('div', { className: 'bowire-mcp-task-field-error', role: 'alert', textContent: mcpFieldErrorText(field, errorCode) }));
        }
        return row;
    }

    // ---- resume after a reload ----

    /// The "still running" panel for a pane with no response yet: on a
    /// tool, the tasks that tool started on this server; on Tasks/follow,
    /// every task kept for the server. Null when there is nothing to offer.
    function renderMcpTaskResume(svc, method) {
        if (!svc || !method || svc.source !== 'mcp') return null;
        var tool;
        if (svc.name === 'Tasks') {
            if (method.name !== 'follow') return null;
            tool = null;
        } else if (svc.name === 'Tools') {
            tool = method.name;
        } else {
            return null;
        }
        var now = Date.now();
        var stored = mcpLoadPending();
        var pruned = mcpPrunePending(stored, now);
        if (pruned.length !== stored.length) mcpSavePending(pruned);
        var entries = mcpPendingFor(pruned, svc.originUrl || '', tool, now);
        if (entries.length === 0) return null;

        var box = el('section', { className: 'bowire-mcp-task-resume-list', 'aria-label': t('mcpTask.resume.title') },
            el('div', { className: 'bowire-mcp-task-inputs-title', textContent: t('mcpTask.resume.title') }));
        entries.forEach(function (e) {
            var expires = mcpExpiresAt(e.createdAt, e.ttlMs);
            var when = e.expired
                ? t('mcpTask.resume.expired')
                : (expires === null ? t('mcpTask.ttl.unlimited') : t('mcpTask.ttl.left', { duration: mcpFormatDuration(expires - now) }));
            var row = el('div', {
                className: 'bowire-mcp-task-resume-row' + (e.expired ? ' is-expired' : ''),
                dataset: { taskId: e.taskId }
            },
                el('span', {
                    className: 'bowire-mcp-task-chip is-' + (e.expired ? 'unknown' : mcpStatusClass(e.status)),
                    textContent: e.expired ? t('mcpTask.resume.expiredChip') : mcpStatusLabel(e.status)
                }),
                el('span', { className: 'bowire-mcp-task-resume-text' },
                    e.tool
                        ? t('mcpTask.resume.rowTool', { taskId: e.taskId, tool: e.tool })
                        : t('mcpTask.resume.row', { taskId: e.taskId })),
                el('span', { className: 'bowire-mcp-task-resume-when', textContent: when }),
                el('span', { className: 'bowire-mcp-task-spacer' }),
                el('button', {
                    type: 'button',
                    className: 'bowire-pane-btn bowire-mcp-task-resume' + (e.expired ? '' : ' is-primary'),
                    textContent: t('mcpTask.resume.button'),
                    dataset: { server: e.serverUrl, task: e.taskId },
                    onClick: function (ev) { var d = ev.currentTarget.dataset; mcpTaskResume(d.server, d.task); }
                }),
                el('button', {
                    type: 'button',
                    className: 'bowire-pane-btn bowire-mcp-task-forget',
                    textContent: t('mcpTask.resume.forget'),
                    dataset: { server: e.serverUrl, task: e.taskId },
                    onClick: function (ev) { var d = ev.currentTarget.dataset; mcpTaskForget(d.server, d.task); }
                }));
            box.appendChild(row);
        });
        box.appendChild(el('div', { className: 'bowire-mcp-task-hint', textContent: t('mcpTask.resume.hint') }));
        return box;
    }
