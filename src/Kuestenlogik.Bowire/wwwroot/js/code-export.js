    // ---- Code Export ----
    // Per-protocol list of available export languages. The order matters:
    // the first entry is the default. Each entry's `id` keys into the
    // CODE_EXPORT_GENERATORS map below.
    const CODE_EXPORT_LANGUAGES = {
        rest:      [{ id: 'curl', label: 'curl' }, { id: 'fetch', label: 'JS fetch' }, { id: 'python', label: 'Python (requests)' }, { id: 'csharp-http', label: 'C# (HttpClient)' }],
        graphql:   [{ id: 'curl', label: 'curl' }, { id: 'fetch', label: 'JS fetch' }, { id: 'python', label: 'Python (requests)' }],
        mcp:       [{ id: 'curl', label: 'curl' }, { id: 'python', label: 'Python (requests)' }, { id: 'fetch', label: 'JS fetch' }],
        sse:       [{ id: 'curl-sse', label: 'curl' }, { id: 'js-eventsource', label: 'JS EventSource' }],
        grpc:      [{ id: 'grpcurl', label: 'grpcurl' }, { id: 'csharp-grpc', label: 'C# (Grpc.Net.Client)' }],
        websocket: [{ id: 'wscat', label: 'wscat' }, { id: 'js-ws', label: 'JS WebSocket' }],
        signalr:   [{ id: 'csharp-signalr', label: 'C# (HubConnection)' }, { id: 'js-signalr', label: 'JS (@microsoft/signalr)' }]
    };

    // Returns the list of language entries available for the currently
    // selected method, or an empty array when no method is selected. Falls
    // back to the REST set when the source is unknown — that gives users at
    // least curl/fetch/python/csharp for any HTTP-shaped plugin.
    function getCodeExportLanguages() {
        if (!selectedService) return [];
        var src = selectedService.source || 'rest';
        return CODE_EXPORT_LANGUAGES[src] || CODE_EXPORT_LANGUAGES.rest;
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
    // snippet matches what Execute would actually send.
    function buildCodeExportContext() {
        var bodyJson = '{}';
        if (selectedMethod && selectedMethod.inputType && requestInputMode === 'form') {
            try { syncFormToJson(); } catch (e) { /* ignore */ }
            bodyJson = requestMessages[0] || '{}';
        } else {
            var ed = $('.bowire-editor') || $('.bowire-message-editor');
            bodyJson = ed ? ed.value || '{}' : '{}';
        }
        bodyJson = substituteVars(bodyJson);

        var meta = {};
        var rows = $$('.bowire-metadata-row');
        for (var i = 0; i < rows.length; i++) {
            var inputs = rows[i].querySelectorAll('.bowire-metadata-input');
            if (inputs.length === 2 && inputs[0].value.trim()) {
                meta[inputs[0].value.trim()] = substituteVars(inputs[1].value);
            }
        }

        var serverUrl = serverUrlForService(selectedService) || '';

        return {
            serverUrl: serverUrl,
            service: selectedService.name,
            method: selectedMethod.name,
            httpMethod: selectedMethod.httpMethod || 'POST',
            httpPath: selectedMethod.httpPath || '',
            bodyJson: bodyJson,
            metadata: meta,
            source: selectedService.source || 'rest'
        };
    }

    // Look up the origin URL the service was discovered from. Falls back to
    // the first connected URL when the service has no origin tag.
    function serverUrlForService(svc) {
        if (svc && svc.originUrl) return svc.originUrl;
        if (typeof serverUrls !== 'undefined' && serverUrls && serverUrls.length > 0) return serverUrls[0];
        return '';
    }

    function generateCodeSnippet(langId) {
        var ctx = buildCodeExportContext();
        var gen = CODE_EXPORT_GENERATORS[langId];
        if (!gen) return '// language not supported for this protocol';
        try {
            return gen(ctx);
        } catch (e) {
            return '// codegen error: ' + e.message;
        }
    }

    // Helpers for the language templates
    function escapeShellSingleQuote(s) {
        return String(s).replace(/'/g, "'\\''");
    }
    function indentLines(s, prefix) {
        return String(s).split('\n').map(function (line) { return prefix + line; }).join('\n');
    }
    function formatHeadersForLang(headers, fn) {
        var keys = Object.keys(headers);
        if (keys.length === 0) return '';
        return keys.map(function (k) { return fn(k, headers[k]); }).join('\n');
    }

    // ---- Codegen functions ----
    const CODE_EXPORT_GENERATORS = {
        // ---- HTTP-shaped plugins (REST / GraphQL / MCP) ----
        'curl': function (ctx) {
            var url = buildHttpUrl(ctx);
            var verb = (ctx.source === 'graphql' || ctx.source === 'mcp') ? 'POST' : ctx.httpMethod;
            var lines = ['curl -X ' + verb + " '" + escapeShellSingleQuote(url) + "' \\"];
            lines.push("  -H 'Content-Type: application/json' \\");
            var hdrKeys = Object.keys(ctx.metadata);
            for (var i = 0; i < hdrKeys.length; i++) {
                var k = hdrKeys[i];
                lines.push("  -H '" + escapeShellSingleQuote(k + ': ' + ctx.metadata[k]) + "' \\");
            }
            if (verb !== 'GET' && verb !== 'DELETE' && verb !== 'HEAD') {
                lines.push("  -d '" + escapeShellSingleQuote(buildHttpBody(ctx)) + "'");
            } else {
                // Trim the trailing backslash on the last header line
                lines[lines.length - 1] = lines[lines.length - 1].replace(/ \\$/, '');
            }
            return lines.join('\n');
        },

        'fetch': function (ctx) {
            var url = buildHttpUrl(ctx);
            var verb = (ctx.source === 'graphql' || ctx.source === 'mcp') ? 'POST' : ctx.httpMethod;
            var headersObj = Object.assign({ 'Content-Type': 'application/json' }, ctx.metadata);
            var headersJs = JSON.stringify(headersObj, null, 2);
            var bodyArg = (verb !== 'GET' && verb !== 'DELETE' && verb !== 'HEAD')
                ? ',\n  body: ' + JSON.stringify(buildHttpBody(ctx))
                : '';
            return 'const response = await fetch(' + JSON.stringify(url) + ', {\n' +
                '  method: ' + JSON.stringify(verb) + ',\n' +
                '  headers: ' + indentLines(headersJs, '  ').trimStart() + bodyArg + '\n' +
                '});\n' +
                'const data = await response.json();\n' +
                'console.log(data);';
        },

        'python': function (ctx) {
            var url = buildHttpUrl(ctx);
            var verb = ((ctx.source === 'graphql' || ctx.source === 'mcp') ? 'POST' : ctx.httpMethod).toLowerCase();
            var headersObj = Object.assign({ 'Content-Type': 'application/json' }, ctx.metadata);
            var headersPy = '{\n' + Object.keys(headersObj).map(function (k) {
                return '    ' + JSON.stringify(k) + ': ' + JSON.stringify(headersObj[k]);
            }).join(',\n') + '\n}';
            var lines = ['import requests', ''];
            lines.push('headers = ' + headersPy);
            if (verb !== 'get' && verb !== 'delete' && verb !== 'head') {
                lines.push('body = ' + buildHttpBody(ctx));
                lines.push('response = requests.' + verb + '(' + JSON.stringify(url) + ', headers=headers, json=body)');
            } else {
                lines.push('response = requests.' + verb + '(' + JSON.stringify(url) + ', headers=headers)');
            }
            lines.push('print(response.json())');
            return lines.join('\n');
        },

        'csharp-http': function (ctx) {
            var url = buildHttpUrl(ctx);
            var verb = ctx.httpMethod;
            var lines = [
                'using var client = new HttpClient();',
                'using var request = new HttpRequestMessage(HttpMethod.' + (verb.charAt(0) + verb.slice(1).toLowerCase()) + ', ' + JSON.stringify(url) + ');'
            ];
            var hdrKeys = Object.keys(ctx.metadata);
            for (var i = 0; i < hdrKeys.length; i++) {
                lines.push('request.Headers.TryAddWithoutValidation(' + JSON.stringify(hdrKeys[i]) + ', ' + JSON.stringify(ctx.metadata[hdrKeys[i]]) + ');');
            }
            if (verb !== 'GET' && verb !== 'DELETE' && verb !== 'HEAD') {
                lines.push('request.Content = new StringContent(' + JSON.stringify(buildHttpBody(ctx)) + ', Encoding.UTF8, "application/json");');
            }
            lines.push('var response = await client.SendAsync(request);');
            lines.push('var json = await response.Content.ReadAsStringAsync();');
            lines.push('Console.WriteLine(json);');
            return lines.join('\n');
        },

        // ---- SSE ----
        'curl-sse': function (ctx) {
            var url = buildHttpUrl(ctx);
            var lines = ["curl -N '" + escapeShellSingleQuote(url) + "' \\"];
            lines.push("  -H 'Accept: text/event-stream'");
            return lines.join('\n');
        },

        'js-eventsource': function (ctx) {
            var url = buildHttpUrl(ctx);
            return 'const source = new EventSource(' + JSON.stringify(url) + ');\n' +
                'source.onmessage = (event) => {\n' +
                '  console.log(event.data);\n' +
                '};\n' +
                'source.onerror = (err) => {\n' +
                '  console.error(err);\n' +
                '  source.close();\n' +
                '};';
        },

        // ---- gRPC ----
        'grpcurl': function (ctx) {
            var host = stripScheme(ctx.serverUrl);
            var lines = [];
            var hdrKeys = Object.keys(ctx.metadata);
            var bodyArg = ctx.bodyJson && ctx.bodyJson.trim() !== '{}' ? "  -d '" + escapeShellSingleQuote(ctx.bodyJson) + "' \\" : '';
            lines.push('grpcurl \\');
            for (var i = 0; i < hdrKeys.length; i++) {
                lines.push("  -H '" + escapeShellSingleQuote(hdrKeys[i] + ': ' + ctx.metadata[hdrKeys[i]]) + "' \\");
            }
            if (bodyArg) lines.push(bodyArg);
            lines.push('  ' + host + ' \\');
            lines.push('  ' + ctx.service + '/' + ctx.method);
            return lines.join('\n');
        },

        'csharp-grpc': function (ctx) {
            return [
                'using var channel = GrpcChannel.ForAddress(' + JSON.stringify(ctx.serverUrl) + ');',
                '// Generated client from your .proto:',
                'var client = new ' + lastSegment(ctx.service) + '.' + lastSegment(ctx.service) + 'Client(channel);',
                '',
                'var request = new ' + ctx.method + 'Request',
                '{',
                '    // populate from: ' + ctx.bodyJson,
                '};',
                '',
                'var response = await client.' + ctx.method + 'Async(request);',
                'Console.WriteLine(response);'
            ].join('\n');
        },

        // ---- WebSocket ----
        'wscat': function (ctx) {
            var url = wsUrl(ctx);
            return [
                '# Install once: npm install -g wscat',
                'wscat -c ' + "'" + escapeShellSingleQuote(url) + "'",
                '# Then type your text frame and press Enter to send.',
                '# Example payload:',
                '# > ' + (ctx.bodyJson === '{}' ? 'hello' : ctx.bodyJson)
            ].join('\n');
        },

        'js-ws': function (ctx) {
            var url = wsUrl(ctx);
            return [
                'const ws = new WebSocket(' + JSON.stringify(url) + ');',
                '',
                "ws.addEventListener('open', () => {",
                '  ws.send(' + JSON.stringify(ctx.bodyJson === '{}' ? 'hello' : ctx.bodyJson) + ');',
                '});',
                '',
                "ws.addEventListener('message', (event) => {",
                '  console.log(event.data);',
                '});',
                '',
                "ws.addEventListener('close', () => console.log('disconnected'));"
            ].join('\n');
        },

        // ---- SignalR ----
        'csharp-signalr': function (ctx) {
            return [
                'using Microsoft.AspNetCore.SignalR.Client;',
                '',
                'var connection = new HubConnectionBuilder()',
                '    .WithUrl(' + JSON.stringify(ctx.serverUrl) + ')',
                '    .Build();',
                '',
                'await connection.StartAsync();',
                '',
                'var result = await connection.InvokeAsync<object>(' + JSON.stringify(ctx.method) + ');',
                '// payload from: ' + ctx.bodyJson,
                'Console.WriteLine(result);'
            ].join('\n');
        },

        'js-signalr': function (ctx) {
            return [
                "import * as signalR from '@microsoft/signalr';",
                '',
                'const connection = new signalR.HubConnectionBuilder()',
                '  .withUrl(' + JSON.stringify(ctx.serverUrl) + ')',
                '  .build();',
                '',
                'await connection.start();',
                '',
                'const result = await connection.invoke(' + JSON.stringify(ctx.method) + ');',
                '// payload from: ' + ctx.bodyJson,
                'console.log(result);'
            ].join('\n');
        }
    };

    // For HTTP-shaped plugins: build the actual request URL. REST methods
    // with httpPath get the path appended (and any {placeholders}
    // substituted from the body); GraphQL/MCP go straight to the server URL.
    function buildHttpUrl(ctx) {
        if (ctx.source === 'graphql' || ctx.source === 'mcp') {
            return ctx.serverUrl;
        }
        if (ctx.source === 'sse') {
            // Method full name format: "SSE/path"
            var p = ctx.method.startsWith('SSE') ? ctx.method.substring(3) : ctx.method;
            return ctx.serverUrl.replace(/\/$/, '') + p;
        }
        if (ctx.httpPath) {
            var path = ctx.httpPath;
            try {
                var bodyObj = JSON.parse(ctx.bodyJson);
                path = path.replace(/\{(\w+)\}/g, function (_, key) {
                    return bodyObj && bodyObj[key] !== undefined ? encodeURIComponent(bodyObj[key]) : '{' + key + '}';
                });
            } catch (e) { /* ignore */ }
            return ctx.serverUrl.replace(/\/$/, '') + path;
        }
        return ctx.serverUrl;
    }

    // For HTTP plugins: build the actual JSON body. REST methods strip
    // path-bucket fields from the body so the snippet doesn't double-send
    // them. GraphQL is already wrapped as { query, variables } from the
    // editor pane. MCP / others pass through.
    function buildHttpBody(ctx) {
        if (ctx.source === 'graphql' || ctx.source === 'mcp') return ctx.bodyJson;
        return ctx.bodyJson;
    }

    function stripScheme(url) {
        return String(url || '').replace(/^https?:\/\//, '');
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

    function lastSegment(name) {
        var parts = String(name || '').split('.');
        return parts[parts.length - 1] || name;
    }

