    // ---- MCP Apps (#810): the host side ----
    //
    // A tool whose _meta.ui.resourceUri names a ui:// resource comes with an
    // interactive view. After the call, this fragment hosts it:
    //
    //   1. reads the resource through /api/invoke, as any resource is read
    //      (same rights, audit and credentials as the call itself);
    //   2. hands its HTML and _meta.ui.csp to /api/ui/mcp-apps/views, which
    //      serves it from a URL of its own under exactly that policy, in a
    //      sandbox (a document the page built itself would inherit the
    //      workbench's policy, which refuses the app's inline script);
    //   3. frames that URL in an iframe sandboxed to allow-scripts, and talks
    //      to it with the AppBridge from @modelcontextprotocol/ext-apps
    //      (vendored, loaded on first use): ui/initialize, the tool's input
    //      and result, and the app's own tools/call and resources/read,
    //      forwarded through /api/invoke;
    //   4. logs every postMessage in both directions beside the app — the
    //      reason to debug an MCP App here.
    //
    // The response pane asks renderMcpAppPane(S, svc, method, pid) for its
    // section; a tool without a UI gets null and is shown as before.

    var MCP_APP_DEFAULT_HEIGHT = 360;
    // The MCP plugin's service names and the sandbox tokens: identifiers, not prose.
    var MCP_TOOLS = 'Tools';  // i18n-exempt: the MCP plugin's service name
    var MCP_RESOURCES = 'Resources';  // i18n-exempt: the MCP plugin's service name
    var MCP_APP_SANDBOX = 'allow-scripts allow-forms';  // i18n-exempt: iframe sandbox tokens
    var MCP_APP_LOG_CAP = 500;
    var _mcpAppRuns = new WeakMap();   // the result frame -> its run
    var _mcpAppRunCounter = 0;
    var _mcpAppBridgeLoad = null;

    // The tool's result frame, the newest one in the stream.
    function mcpAppResultMessage(S) {
        var msgs = (S && S.streamMessages) || [];
        for (var i = msgs.length - 1; i >= 0; i--) {
            var frame = (typeof mcpParseFrame === 'function') ? mcpParseFrame(msgs[i]) : null;
            if (frame && frame.event === 'result') return { message: msgs[i], result: frame.data };
        }
        return null;
    }

    // The plugin writes the SDK's objects with every unset field as null
    // ("_meta": null). MCP's schemas make such fields optional, never
    // nullable, and an app that validates (the ext-apps App does, with zod)
    // drops the whole message. So what goes to an app leaves the nulls out.
    function mcpAppWithoutNulls(value) {
        if (Array.isArray(value)) return value.map(mcpAppWithoutNulls);
        if (!value || typeof value !== 'object') return value;
        var out = {};
        Object.keys(value).forEach(function (k) {
            if (value[k] !== null) out[k] = mcpAppWithoutNulls(value[k]);
        });
        return out;
    }

    function mcpAppArguments(S) {
        var raw = (S && S.requestMessages && S.requestMessages[0]) || '{}';
        try {
            var parsed = JSON.parse(raw);
            return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
        } catch { return {}; }
    }

    function ensureMcpAppBridge() {
        if (window.BowireMcpAppBridge) return Promise.resolve(window.BowireMcpAppBridge);
        if (_mcpAppBridgeLoad) return _mcpAppBridgeLoad;
        _mcpAppBridgeLoad = new Promise(function (resolve, reject) {
            var tag = document.createElement('script');
            tag.src = config.prefix + '/api/ui/mcp-apps/app-bridge.js?v=' + encodeURIComponent(config.version || '');
            tag.onload = function () { resolve(window.BowireMcpAppBridge); };
            tag.onerror = function () { _mcpAppBridgeLoad = null; reject(new Error('app-bridge.js did not load')); };  // i18n-exempt: the reason inside mcpApp.failed, names a file
            document.head.appendChild(tag);
        });
        return _mcpAppBridgeLoad;
    }

    // One call through /api/invoke, the way the workbench makes any: the
    // environment's credentials, the server's allowlist and audit.
    async function mcpAppInvoke(serverUrl, service, method, args) {
        var metadata = (typeof applyAuth === 'function') ? await applyAuth({}) : {};
        var resp = await fetch(config.prefix + '/api/invoke?serverUrl=' + encodeURIComponent(serverUrl), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ service: service, method: method, messages: [JSON.stringify(args || {})], metadata: metadata, protocol: 'mcp' })
        });
        var json = await resp.json().catch(function () { return {}; });
        if (!resp.ok || json.title || json.response == null) {
            throw new Error(json.title || (json.metadata && json.metadata.error) || json.status || ('HTTP ' + resp.status));
        }
        return mcpAppWithoutNulls(JSON.parse(json.response));
    }

    function mcpAppLog(run, direction, message) {
        var text = '';
        try { text = JSON.stringify(message); } catch { text = String(message); }
        run.log.push({
            at: Math.round(performance.now() - run.startedAt),
            direction: direction,
            method: (message && message.method) || (message && message.error ? 'error' : 'response'),
            id: message && message.id !== undefined ? message.id : null,
            payload: text
        });
        if (run.log.length > MCP_APP_LOG_CAP) run.log.splice(0, run.log.length - MCP_APP_LOG_CAP);
        render();
    }

    async function mcpAppStart(run, mount) {
        try {
            var B = await ensureMcpAppBridge();
            var read = await mcpAppInvoke(run.serverUrl, MCP_RESOURCES, run.resourceUri, {});
            var content = (read && read.contents && read.contents[0]) || {};
            var html = typeof content.text === 'string' ? content.text
                : (typeof content.blob === 'string' ? atob(content.blob) : '');
            if (!html) throw new Error(t('mcpApp.noHtml', { uri: run.resourceUri }));
            var ui = (content._meta && content._meta.ui) || {};
            run.csp = ui.csp || null;

            var viewResp = await fetch(config.prefix + '/api/ui/mcp-apps/views', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ html: html, csp: ui.csp || null })
            });
            var view = await viewResp.json();
            if (!viewResp.ok || !view.url) throw new Error((view.errors && view.errors.join('; ')) || ('HTTP ' + viewResp.status));

            var frame = document.createElement('iframe');
            frame.className = 'bowire-mcp-app-frame';
            frame.setAttribute('sandbox', MCP_APP_SANDBOX);
            frame.setAttribute('title', t('mcpApp.frameTitle', { uri: run.resourceUri }));
            var allow = B.buildAllowAttribute(ui.permissions);
            if (allow) frame.setAttribute('allow', allow);
            frame.style.height = MCP_APP_DEFAULT_HEIGHT + 'px';
            mount.appendChild(frame);
            run.frame = frame;

            // The log reads the wire itself: what the app sends arrives as a
            // message event from its window; what the host sends goes
            // through the transport.
            run.onWindowMessage = function (event) {
                if (event.source === frame.contentWindow && event.data && event.data.jsonrpc) mcpAppLog(run, 'in', event.data);
            };
            window.addEventListener('message', run.onWindowMessage);

            var bridge = new B.AppBridge(null, { name: 'Bowire', version: config.version || '' }, {
                serverTools: {},
                serverResources: {},
                logging: {},
                sandbox: { csp: ui.csp || undefined, permissions: ui.permissions || undefined }
            });
            bridge.oncalltool = function (params) {
                return mcpAppInvoke(run.serverUrl, MCP_TOOLS, params.name, params.arguments || {});
            };
            bridge.onreadresource = function (params) {
                return mcpAppInvoke(run.serverUrl, MCP_RESOURCES, params.uri, {});
            };
            bridge.onsizechange = function (params) {
                if (params && typeof params.height === 'number' && params.height > 0) frame.style.height = Math.ceil(params.height) + 'px';
            };
            bridge.onopenlink = function (params) {
                if (params && /^https?:\/\//i.test(params.url)) window.open(params.url, '_blank', 'noopener');  // i18n-exempt: window target and features
                return Promise.resolve({});
            };
            bridge.oninitialized = function () {
                run.status = 'running';
                render();
                bridge.sendToolInput({ arguments: run.args });
                bridge.sendToolResult(run.result);
            };

            var transport = new B.PostMessageTransport(frame.contentWindow, frame.contentWindow);
            var send = transport.send.bind(transport);
            transport.send = function (message, options) {
                mcpAppLog(run, 'out', message);
                return send(message, options);
            };
            await bridge.connect(transport);
            run.bridge = bridge;
            // The bridge listens before the app loads, so its ui/initialize is not missed.
            frame.src = view.url;
            run.status = 'loading';
        } catch (err) {
            run.status = 'failed';
            run.error = (err && err.message) || String(err);
        }
        render();
    }

    function mcpAppRunFor(S, svc, method) {
        var found = mcpAppResultMessage(S);
        if (!found) return null;
        var run = _mcpAppRuns.get(found.message);
        if (!run) {
            run = {
                id: 'mcp-app-' + (++_mcpAppRunCounter),
                serverUrl: (svc && svc.originUrl) || '',
                resourceUri: method.uiResourceUri,
                args: mcpAppArguments(S),
                result: mcpAppWithoutNulls(found.result),
                status: 'starting',
                error: null,
                log: [],
                startedAt: performance.now(),
                started: false
            };
            _mcpAppRuns.set(found.message, run);
        }
        return run;
    }

    function renderMcpAppTraffic(run) {
        var list = el('ol', { className: 'bowire-mcp-app-traffic-list' });
        if (run.log.length === 0) {
            list.appendChild(el('li', { className: 'bowire-mcp-app-traffic-empty', textContent: t('mcpApp.trafficEmpty') }));
        }
        run.log.forEach(function (entry) {
            list.appendChild(el('li', { className: 'bowire-mcp-app-traffic-entry is-' + entry.direction },
                el('details', {},
                    el('summary', {},
                        el('span', { className: 'bowire-mcp-app-traffic-time', textContent: entry.at + ' ms' }),  // i18n-exempt: a unit
                        el('span', { className: 'bowire-mcp-app-traffic-dir',
                            textContent: entry.direction === 'out' ? t('mcpApp.toApp') : t('mcpApp.toHost') }),
                        el('code', { className: 'bowire-mcp-app-traffic-method', textContent: entry.method }),
                        entry.id !== null ? el('span', { className: 'bowire-mcp-app-traffic-id', textContent: '#' + entry.id }) : null),
                    el('pre', { className: 'bowire-mcp-app-traffic-payload', textContent: entry.payload }))));
        });
        return el('section', { className: 'bowire-mcp-app-traffic', 'aria-label': t('mcpApp.traffic') },
            el('div', { className: 'bowire-mcp-app-traffic-head', textContent: t('mcpApp.traffic') + ' (' + run.log.length + ')' }),
            list);
    }

    function renderMcpAppPane(S, svc, method, pid) {
        if (!svc || svc.source !== 'mcp' || !method || !method.uiResourceUri) return null;
        var run = mcpAppRunFor(S, svc, method);
        if (!run) return null;

        // Owned by mcpAppStart once filled: morphdom leaves a live mount alone,
        // so the app is not reloaded by the next render.
        var mount = el('div', { className: 'bowire-mcp-app-mount bowire-live-mount', id: pid('bowire-' + run.id) });
        if (!run.started) {
            run.started = true;
            afterRender(function () {
                var live = document.getElementById(mount.id);
                if (live) mcpAppStart(run, live);
            });
        }

        var status = run.status === 'failed'
            ? el('div', { className: 'bowire-mcp-app-error', role: 'alert', textContent: t('mcpApp.failed', { reason: run.error }) })
            : el('span', { className: 'bowire-mcp-app-status is-' + run.status, textContent: t('mcpApp.status.' + run.status) });
        return el('section', { className: 'bowire-mcp-app', 'aria-label': t('mcpApp.title') },
            el('div', { className: 'bowire-mcp-app-head' },
                el('span', { className: 'bowire-mcp-app-title', textContent: t('mcpApp.title') }),
                el('code', { className: 'bowire-mcp-app-uri', textContent: run.resourceUri }),
                status),
            el('div', { className: 'bowire-mcp-app-body' }, mount, renderMcpAppTraffic(run)));
    }
