    // ---- Code Export ----
    // #677 — one registry of snippet targets. A target names the protocols
    // it serves, its label, the comment syntax of its language (for the notes
    // a snippet carries) and a generate(ctx) function. Core registers its own
    // targets in the code-export-*.js fragments; a plugin registers more
    // through window.bowireCodeExport.register from its UI-extension bundle,
    // without touching core.
    //
    // Rules every target keeps:
    // - Secrets stay references. ctx.bodyJson and ctx.metadata are resolved
    //   with {{secret.*}} / {{keyring.*}} held back (cliResolveVars), and a
    //   note says so; a resolved credential never lands in a snippet.
    // - A token the environment fetches at request time (OAuth, session,
    //   JWT, …) is a note, never a made-up header.
    // - What a target cannot express is stated in a note, not dropped.
    // - A target is offered only for the protocols it names: a broker is
    //   never offered a curl command. A protocol no target names gets the
    //   Bowire CLI, which speaks every protocol.

    var _codeExportTargets = [];

    // target: { id, label, protocols: [...] | '*', comment: '#' | '//' | null,
    //           rank?: number, ownNotes?: bool, rawContext?: bool, generate(ctx) }
    // ownNotes: the target writes its own notes. rawContext: it gets the
    // server URL as written ({{vars}} and all) and resolves it itself.
    // Lower rank sorts first; the first target of a protocol is the default
    // and labels the request pane's "copy as" button. A later registration
    // with the same id replaces the earlier one.
    function registerCodeExportTarget(target) {
        if (!target || typeof target.id !== 'string' || typeof target.generate !== 'function') {
            throw new Error('a code export target needs an id and a generate function');
        }
        var entry = {
            id: target.id,
            label: String(target.label || target.id),
            protocols: target.protocols === '*' ? '*' : (target.protocols || []).slice(),
            comment: target.comment === undefined ? '//' : target.comment,
            rank: typeof target.rank === 'number' ? target.rank : 100,
            ownNotes: !!target.ownNotes,
            rawContext: !!target.rawContext,
            generate: target.generate,
            order: _codeExportTargets.length
        };
        _codeExportTargets = _codeExportTargets.filter(function (t) { return t.id !== entry.id; });
        _codeExportTargets.push(entry);
        return entry;
    }

    function codeExportTarget(id) {
        for (var i = 0; i < _codeExportTargets.length; i++) {
            if (_codeExportTargets[i].id === id) return _codeExportTargets[i];
        }
        return null;
    }

    // The targets for one protocol id, default first. Targets registered for
    // every protocol ('*', the Bowire CLI) come with the rest by rank.
    function codeExportTargetsFor(protocolId) {
        var src = protocolId || 'rest';
        return _codeExportTargets
            .filter(function (t) { return t.protocols === '*' || t.protocols.indexOf(src) >= 0; })
            .sort(function (a, b) { return (a.rank - b.rank) || (a.order - b.order); })
            .map(function (t) { return { id: t.id, label: t.label }; });
    }

    try {
        if (typeof window !== 'undefined') {
            window.bowireCodeExport = {
                register: registerCodeExportTarget,
                targetsFor: codeExportTargetsFor
            };
        }
    } catch (_) { /* no window in the Node tests */ }

    // Returns the list of language entries available for the currently
    // selected method, or an empty array when no method is selected.
    function getCodeExportLanguages() {
        if (!selectedService) return [];
        return codeExportTargetsFor(selectedService.source || 'rest');
    }

    // Pick a snippet language: explicit user choice if it's still valid for
    // the current protocol, otherwise the first available default.
    function resolveCodeExportLang() {
        var available = getCodeExportLanguages();
        if (available.length === 0) return null;
        if (codeExportLang && available.some(function (l) { return l.id === codeExportLang; })) {
            return codeExportLang;
        }
        return available[0].id;
    }

    // Build a snapshot of the current request for codegen — body / metadata
    // / server URL / etc. — substituting environment vars so the generated
    // snippet matches what Execute would actually send, secrets excepted.
    //
    // CRITICAL: this runs INSIDE renderRequestPane, which means it executes
    // BEFORE morphdom syncs the new form values into the live DOM. Calling
    // syncFormToJson() here would read the STALE (pre-merge) inputs and
    // overwrite formValues + requestMessages with empty strings — every
    // render cycle. That destroyed preset apply state mid-render. We
    // assemble bodyJson read-only without mutating any global.
    function buildCodeExportContext() {
        var S = activeState();
        var bodyJson = '{}';
        if (selectedMethod && selectedMethod.inputType && S.requestInputMode === 'form') {
            // Read-only snapshot: prefer formValues for the current schema,
            // fall back to requestMessages[0] if formValues isn't initialised
            // yet. NEVER call syncFormToJson here — see note above.
            try {
                var snap = (typeof collectFormValuesFromState === 'function')
                    ? collectFormValuesFromState(activeState(), selectedMethod.inputType, '')
                    : null;
                if (snap != null) {
                    bodyJson = JSON.stringify(snap, null, 2);
                } else {
                    bodyJson = (S.requestMessages && S.requestMessages[0]) || '{}';
                }
            } catch (e) {
                bodyJson = (S.requestMessages && S.requestMessages[0]) || '{}';
            }
        } else {
            var ed = $('.bowire-editor', focusedSurfaceEl()) || $('.bowire-message-editor', focusedSurfaceEl());
            bodyJson = ed ? ed.value || '{}' : '{}';
        }

        var metaRaw = {};
        var rows = $$('.bowire-metadata-row', focusedSurfaceEl());
        for (var i = 0; i < rows.length; i++) {
            var inputs = rows[i].querySelectorAll('.bowire-metadata-input');
            if (inputs.length === 2 && inputs[0].value.trim()) {
                metaRaw[inputs[0].value.trim()] = inputs[1].value;
            }
        }

        var env = (typeof getActiveEnv === 'function') ? getActiveEnv() : null;
        return codeExportContextFrom({
            serverUrl: serverUrlForService(selectedService) || '',
            service: selectedService.name,
            method: selectedMethod.name,
            methodFullName: selectedMethod.fullName || '',
            httpMethod: selectedMethod.httpMethod || 'POST',
            httpPath: selectedMethod.httpPath || '',
            bodyJsonRaw: bodyJson,
            metadataRaw: metaRaw,
            // selectedService.source IS the IBowireProtocol.Id — api.js
            // sends it verbatim as /api/invoke's `protocol` field.
            source: selectedService.source || 'rest',
            serverStreaming: !!selectedMethod.serverStreaming,
            clientStreaming: !!selectedMethod.clientStreaming,
            inputTypeName: (selectedMethod.inputType && selectedMethod.inputType.name) || '',
            inputFieldNames: ((selectedMethod.inputType && selectedMethod.inputType.fields) || [])
                .map(function (f) { return f.name; }),
            outputTypeName: (selectedMethod.outputType && selectedMethod.outputType.name) || '',
            messages: (typeof S.requestMessages !== 'undefined' && S.requestMessages)
                ? S.requestMessages.slice() : [],
            // The auth TYPE only. applyAuth is async and performs fetches
            // (session login, OAuth token endpoints, JWT signing), so no
            // exporter can call it from the render path.
            authKind: (env && env.auth && env.auth.type) || 'none',
            authLocation: (env && env.auth && env.auth.location) || ''
        });
    }

    // The context every target reads, from the raw request. Pure, so the
    // tests build it the same way. bodyJson / metadata carry variables
    // resolved and secrets held back; bodyJsonRaw / metadataRaw keep the
    // template for the Bowire CLI and .http targets, which resolve it
    // themselves.
    function codeExportContextFrom(raw) {
        var resolve = (typeof cliResolveVars === 'function') ? cliResolveVars : function (s) { return s; };
        var metadata = {};
        var metaRaw = raw.metadataRaw || {};
        Object.keys(metaRaw).forEach(function (k) { metadata[k] = resolve(String(metaRaw[k])); });
        var bodyRaw = raw.bodyJsonRaw == null ? '{}' : String(raw.bodyJsonRaw);
        var ctx = Object.assign({}, raw, {
            bodyJsonRaw: bodyRaw,
            metadataRaw: metaRaw,
            bodyJson: resolve(bodyRaw),
            metadata: metadata,
            serverUrlRaw: raw.serverUrl || '',
            serverUrlResolved: resolve(raw.serverUrl || ''),
            protocolId: raw.protocolId || raw.source || 'rest',
            source: raw.source || 'rest',
            httpMethod: raw.httpMethod || 'POST',
            httpPath: raw.httpPath || '',
            methodFullName: raw.methodFullName || '',
            messages: raw.messages || [],
            authKind: raw.authKind || 'none',
            authLocation: raw.authLocation || ''
        });
        var secretRef = /\{\{\s*(?:secret|keyring)\.[^}]*\}\}|\$\{\s*(?:secret|keyring)\.[^}]*\}/;
        ctx.secretRefs = secretRef.test(bodyRaw)
            || Object.keys(metaRaw).some(function (k) { return secretRef.test(String(metaRaw[k])); });
        return ctx;
    }

    // Look up the origin URL the service was discovered from. Falls back to
    // the first connected URL when the service has no origin tag.
    function serverUrlForService(svc) {
        if (svc && svc.originUrl) return svc.originUrl;
        if (typeof serverUrls !== 'undefined' && serverUrls && serverUrls.length > 0) return serverUrls[0];
        return '';
    }

    // What a snippet cannot carry, as plain sentences. A target's own notes
    // (ctx.notes, pushed by generate) come after these.
    function codeExportNotes(ctx) {
        var notes = [];
        var auth = ctx.authKind || 'none';
        if (auth === 'session' || auth === 'oauth2_cc' || auth === 'oauth2_ac'
            || auth === 'custom_token' || auth === 'jwt') {
            notes.push('The active environment fetches its token at request time (' + auth + '); '
                + 'it is not in this snippet. Add it as an Authorization: Bearer header.');
        } else if (auth === 'aws_sigv4') {
            notes.push('AWS SigV4 signs each request over its body and time; no fixed header stands in for it. '
                + 'Sign the call with an AWS SDK.');
        } else if (auth === 'mtls') {
            notes.push('The active environment presents a client certificate; configure it in the client.');
        } else if (auth !== 'none' && auth) {
            notes.push('The active environment adds ' + auth + ' credentials that are not in this snippet.');
        }
        if (ctx.secretRefs) {
            notes.push('{{secret.*}} / {{keyring.*}} references are left as they are; put the values in before running.');
        }
        return notes;
    }

    function generateCodeSnippetFor(langId, ctx) {
        var target = codeExportTarget(langId);
        if (!target) return '// language not supported for this protocol';
        try {
            ctx = target.rawContext ? ctx : Object.assign({}, ctx, { serverUrl: ctx.serverUrlResolved });
            ctx.notes = [];
            var body = target.generate(ctx);
            if (target.ownNotes || !target.comment) return body;
            var notes = codeExportNotes(ctx).concat(ctx.notes);
            if (notes.length === 0) return body;
            return notes.map(function (n) { return target.comment + ' ' + n; }).join('\n') + '\n\n' + body;
        } catch (e) {
            return (target.comment || '//') + ' codegen error: ' + e.message;
        }
    }

    function generateCodeSnippet(langId) {
        return generateCodeSnippetFor(langId, buildCodeExportContext());
    }

    // ---- Helpers for the language templates ----
    function escapeShellSingleQuote(s) {
        return String(s).replace(/'/g, "'\\''");
    }
    function indentLines(s, prefix) {
        return String(s).split('\n').map(function (line) { return prefix + line; }).join('\n');
    }

    // A string literal in the target language. JSON's escapes are valid in
    // JS, Python, C#, Java and Go; Kotlin also interpolates `$`; Swift and
    // Rust spell a code point \u{…}; PHP and Ruby single quotes and the
    // shells take their own quoting.
    function codeLiteral(lang, s) {
        s = String(s == null ? '' : s);
        switch (lang) {
            case 'kotlin': return JSON.stringify(s).replace(/\$/g, '\\$');
            case 'swift':
            case 'rust': return '"' + s.replace(/[\\"\x00-\x1f]/g, function (c) {
                if (c === '\\') return '\\\\';
                if (c === '"') return '\\"';
                if (c === '\n') return '\\n';
                if (c === '\r') return '\\r';
                if (c === '\t') return '\\t';
                return '\\u{' + c.charCodeAt(0).toString(16) + '}';
            }) + '"';
            case 'php':
            case 'ruby': return "'" + s.replace(/\\/g, '\\\\').replace(/'/g, "\\'") + "'";
            case 'sh': return "'" + escapeShellSingleQuote(s) + "'";
            case 'ps': return "'" + s.replace(/'/g, "''") + "'";
            default: return JSON.stringify(s);
        }
    }

    // A JSON body on one line, so a literal stays readable in every
    // language; anything that is not JSON passes through as written.
    function compactJson(text) {
        try { return JSON.stringify(JSON.parse(text)); }
        catch (_) { return String(text == null ? '' : text); }
    }

    function parsedBody(ctx) {
        try { return JSON.parse(ctx.bodyJson); } catch (_) { return undefined; }
    }

    function isEmptyBody(text) {
        var t = String(text == null ? '' : text).trim();
        return t === '' || t === '{}';
    }

    function lastSegment(name) {
        var parts = String(name || '').split('.');
        return parts[parts.length - 1] || name;
    }

    function stripScheme(url) {
        return String(url || '').replace(/^[a-z][a-z0-9+.-]*:\/\//i, '').replace(/\/$/, '');
    }

    // ---- The HTTP request a protocol of the HTTP family sends ----
    // REST and OData send the editor body to the method's path; GraphQL
    // sends {query, variables} as edited; JSON-RPC and MCP wrap the body in
    // the envelope their plugin builds server-side, so the snippet sends
    // what the workbench sends.
    var HTTP_FAMILY = ['rest', 'odata', 'graphql', 'jsonrpc', 'mcp'];

    function buildHttpUrl(ctx) {
        if (ctx.source === 'graphql' || ctx.source === 'mcp' || ctx.source === 'jsonrpc') {
            return ctx.serverUrl;
        }
        if (ctx.source === 'sse') {
            // Method full name format: "SSE/path"
            var p = ctx.method.startsWith('SSE') ? ctx.method.substring(3) : ctx.method;
            return ctx.serverUrl.replace(/\/$/, '') + p;
        }
        if (ctx.httpPath) {
            var path = ctx.httpPath;
            var bodyObj = parsedBody(ctx);
            path = path.replace(/\{(\w+)\}/g, function (_, key) {
                return bodyObj && bodyObj[key] !== undefined ? encodeURIComponent(bodyObj[key]) : '{' + key + '}';
            });
            return ctx.serverUrl.replace(/\/$/, '') + path;
        }
        return ctx.serverUrl;
    }

    function mcpEnvelope(ctx) {
        var args = parsedBody(ctx);
        var service = String(ctx.service || '');
        if (service === 'Resources') return { jsonrpc: '2.0', id: 1, method: 'resources/read', params: { uri: ctx.method } };
        if (service === 'Prompts') return { jsonrpc: '2.0', id: 1, method: 'prompts/get', params: { name: ctx.method, arguments: args || {} } };
        return { jsonrpc: '2.0', id: 1, method: 'tools/call', params: { name: ctx.method, arguments: args || {} } };
    }

    // { verb, url, headers, body } — body null when the verb sends none.
    function httpRequestOf(ctx) {
        var verb = (ctx.source === 'graphql' || ctx.source === 'mcp' || ctx.source === 'jsonrpc')
            ? 'POST' : String(ctx.httpMethod || 'POST').toUpperCase();
        var body = null;
        if (verb !== 'GET' && verb !== 'HEAD' && verb !== 'DELETE') {
            if (ctx.source === 'mcp') {
                body = JSON.stringify(mcpEnvelope(ctx));
            } else if (ctx.source === 'jsonrpc') {
                var envelope = { jsonrpc: '2.0', id: 1, method: ctx.method };
                if (!isEmptyBody(ctx.bodyJson)) {
                    var params = parsedBody(ctx);
                    envelope.params = params === undefined ? ctx.bodyJson : params;
                }
                body = JSON.stringify(envelope);
            } else {
                body = compactJson(ctx.bodyJson);
            }
        }
        var headers = {};
        if (body !== null) headers['Content-Type'] = 'application/json';
        if (ctx.source === 'mcp') {
            headers.Accept = 'application/json, text/event-stream';
            ctx.notes.push('An MCP server expects an initialize request first and the Mcp-Session-Id it answers with on every '
                + 'later request; this snippet shows the call itself.');
        }
        Object.keys(ctx.metadata || {}).forEach(function (k) { headers[k] = ctx.metadata[k]; });
        return { verb: verb, url: buildHttpUrl(ctx), headers: headers, body: body };
    }

    function wsUrl(ctx) {
        var url = ctx.serverUrl || '';
        if (url.startsWith('http://')) url = 'ws://' + url.substring('http://'.length);
        else if (url.startsWith('https://')) url = 'wss://' + url.substring('https://'.length);
        // Method name is the path discovered for the WebSocket endpoint
        if (ctx.method && ctx.method.startsWith('/')) {
            try {
                var u = new URL(url);
                u.pathname = ctx.method;
                return u.toString();
            } catch (e) {
                return url;
            }
        }
        return url;
    }

    // host and port out of a broker URL (mqtt://, nats://, tcp://, …),
    // with the protocol's default port when the URL has none.
    function brokerHostPort(url, defaultPort) {
        var m = /^(?:[a-z][a-z0-9+.-]*:\/\/)?([^/:?#]+)(?::(\d+))?/i.exec(String(url || '').trim());
        return { host: (m && m[1]) || 'localhost', port: m && m[2] ? Number(m[2]) : defaultPort };
    }
