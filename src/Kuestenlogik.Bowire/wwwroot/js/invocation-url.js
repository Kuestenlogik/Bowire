    // ---- #253 — Invocation URL override (discovery url ≠ invocation url) ----
    //
    // A discovered method carries a "schema" URL — service.originUrl, where
    // its spec / proto / OpenAPI came from — that today doubles as the
    // invocation target. Real deployments often host the docs / spec apart
    // from the live API (docs.example.com vs api.example.com; a gateway in
    // front of a reflection-enabled gRPC server). This slice lets the
    // operator point the CALL somewhere else without touching discovery:
    //   * schema  — same URL as the schema (default; today's behaviour)
    //   * source  — a workspace Source URL, resolved live (rename-tolerant)
    //   * inline  — a custom one-off URL typed freely, for a quick call
    //
    // resolveInvocationUrl is the cross-cutting seam: serverUrlParamForService
    // (helpers.js), the collection-item replay (collections.js) and the
    // recorder all resolve through it, so an override applies wherever a
    // single call is actually made. Discovery is untouched — it stamps and
    // reads service.originUrl directly, never this resolver.

    var METHOD_INVOCATION_URL_KEY = 'bowire_method_invocation_url';

    // Per-method override store, workspace-scoped, keyed by
    // methodStateKey(svc, method) = 'svc::method' (mirrors the method
    // scripts store — an override is config, not a secret, so it persists).
    function _loadAllInvocationOverrides() {
        try {
            var raw = localStorage.getItem(wsKey(METHOD_INVOCATION_URL_KEY));
            var m = raw ? JSON.parse(raw) : {};
            return (m && typeof m === 'object') ? m : {};
        } catch (_) { return {}; }
    }

    function _saveAllInvocationOverrides(map) {
        try {
            localStorage.setItem(wsKey(METHOD_INVOCATION_URL_KEY), JSON.stringify(map));
            if (typeof markSaved === 'function') markSaved('invocation URL');
        } catch (e) {
            if (typeof console !== 'undefined') console.warn('[#253] invocation override persist failed', e);
        }
    }

    // The stored override for a method, or null when it uses the schema URL.
    function getInvocationOverride(svcName, methodName) {
        if (!svcName || !methodName) return null;
        var m = _loadAllInvocationOverrides();
        return m[methodStateKey(svcName, methodName)] || null;
    }

    // Persist (or clear) a method's override. mode 'schema' or a null
    // override deletes the entry — the default costs no storage.
    function setInvocationOverride(svcName, methodName, override) {
        if (!svcName || !methodName) return;
        var m = _loadAllInvocationOverrides();
        var key = methodStateKey(svcName, methodName);
        if (!override || override.mode === 'schema') {
            delete m[key];
        } else {
            m[key] = { mode: override.mode, url: override.url || '' };
        }
        _saveAllInvocationOverrides(m);
    }

    // Resolve a raw Source-URL reference against the live workspace list,
    // mirroring #252's drift behaviour: a friendly-name RENAME keeps the raw
    // URL so it still matches; a RETIRED url falls back to the first Source
    // with a console.warn (not a toast — too noisy for batch replay). Shared
    // by the per-method override and #252's collection replay.
    function resolveSourceUrl(ref, label) {
        if (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls) && serverUrls.length > 0) {
            if (ref && serverUrls.indexOf(ref) >= 0) return ref;
            var fallback = serverUrls[0];
            if (ref && ref !== fallback && typeof console !== 'undefined') {
                console.warn('[#253] ' + (label || 'invocation') + ' bound to source URL "'
                    + ref + '" no longer in workspace — using "' + fallback + '"');
            }
            return fallback;
        }
        // No sources at all — best-effort reuse the ref so the call still
        // has something to hit.
        return ref || '';
    }

    // THE resolver: the raw invocation URL for a (service, method) pair,
    // honouring the per-method override. Returns the schema URL
    // (service.originUrl) for the default / unknown mode, so an override-
    // free call behaves exactly as before. Callers that pass no method (the
    // service-compare / benchmark-of-arbitrary-service paths) get the plain
    // schema URL — overrides are a per-selected-method concept.
    function resolveInvocationUrl(service, method) {
        var schemaUrl = (service && service.originUrl) || null;
        if (!service || !method) return schemaUrl;
        var ov = getInvocationOverride(service.name, method.name);
        if (!ov || ov.mode === 'schema') return schemaUrl;
        if (ov.mode === 'source') return resolveSourceUrl(ov.url, 'method invocation') || schemaUrl;
        if (ov.mode === 'inline') {
            var u = (ov.url || '').trim();
            return u || schemaUrl;
        }
        return schemaUrl;
    }

    // The invocation URL with {{vars}} substituted — the URL the request
    // will ACTUALLY be sent to. resolveInvocationUrl returns the raw stored
    // ref (an inline override may be a template like https://{{env}}.api/…);
    // the live send substitutes it inside serverUrlParam, but the satellites
    // that read the URL directly — the pre-request script's ctx.request.url,
    // the per-URL header lookup, the recorder step — must substitute too, or
    // a script signs / a header keys / a recording labels a template string
    // that never matches the host actually hit.
    function invocationUrlFor(service, method) {
        var u = resolveInvocationUrl(service, method);
        return (u && typeof substituteVars === 'function') ? substituteVars(u) : u;
    }

    // ---- override UI (discovered-method request pane) ----

    // Collapsed by default — the disclosure header signals "by default we
    // call the schema URL", and only power users who override need the body.
    var _invocationOverrideExpanded = false;

    function _alwaysShowInvocationUrl() {
        try { return localStorage.getItem('bowire_always_show_invocation_url') === 'true'; }
        catch (_) { return false; }
    }

    // A collapsed "Invocation URL" disclosure for the discovered-method
    // request pane. Three modes: schema (default, calls the schema URL),
    // source (a workspace Source), inline (a custom one-off URL). Mirrors
    // the script-editor collapse pattern + the #252 URL-row toggle look.
    // Every interactive handler below writes through _ovTarget() rather
    // than the render-time service/method args. The block is appended to a
    // statically-id'd container, so on a method switch morphdom would keep
    // the old input/select/button NODES (with their original closures) —
    // the documented stale-handler trap. Reading the live selection at
    // click time makes an edit always land on the method on screen. The
    // per-method id on the block additionally lets morphdom swap the whole
    // block when the selection changes.
    function _ovTarget() {
        var s = (typeof selectedService !== 'undefined') ? selectedService : null;
        var m = (typeof selectedMethod !== 'undefined') ? selectedMethod : null;
        return (s && m) ? { svc: s.name, method: m.name } : null;
    }
    function _ovSet(mode, url) {
        var t = _ovTarget();
        if (t) setInvocationOverride(t.svc, t.method, { mode: mode, url: url });
    }

    function renderInvocationUrlOverride(service, method) {
        if (!service || !method || typeof el !== 'function') return null;
        var ov = getInvocationOverride(service.name, method.name) || { mode: 'schema', url: '' };
        var schemaUrl = service.originUrl || '';
        var expanded = _invocationOverrideExpanded || _alwaysShowInvocationUrl();

        var block = el('div', {
            id: 'bowire-invoke-url-' + String(service.name + '::' + method.name).replace(/[^A-Za-z0-9]+/g, '_'),
            className: 'bowire-invoke-url-block'
        });
        var stateText = ov.mode === 'source' ? 'source override'
            : (ov.mode === 'inline' ? 'custom override' : 'schema URL');
        var head = el('div', {
            className: 'bowire-invoke-url-head',
            role: 'button',
            title: 'Choose where Execute sends this call',
            onClick: function () { _invocationOverrideExpanded = !_invocationOverrideExpanded; render(); }
        },
            el('span', { className: 'bowire-invoke-url-caret' + (expanded ? '' : ' collapsed'),
                textContent: expanded ? '▾' : '▸' }),
            el('span', { className: 'bowire-invoke-url-label', textContent: 'Invocation URL' }),
            el('span', {
                className: 'bowire-invoke-url-state' + (ov.mode !== 'schema' ? ' is-override' : ''),
                textContent: stateText
            })
        );
        block.appendChild(head);
        if (!expanded) return block;

        // Mode switch — three segments. Writes through _ovSet (live target).
        function setMode(mode) {
            var url = '';
            if (mode === 'source') {
                url = (ov.mode === 'source' && ov.url) ? ov.url
                    : (typeof serverUrls !== 'undefined' && serverUrls.length ? serverUrls[0] : '');
            } else if (mode === 'inline') {
                url = (ov.mode === 'inline' && ov.url) ? ov.url : schemaUrl;
            }
            _ovSet(mode, url);
            render();
        }
        var hasSources = typeof serverUrls !== 'undefined' && Array.isArray(serverUrls) && serverUrls.length > 0;
        var seg = el('div', { className: 'bowire-invoke-url-modes', role: 'group', 'aria-label': 'Invocation URL mode' });
        [['schema', 'Same as schema', false],
         ['source', 'From Source', !hasSources],
         ['inline', 'Custom', false]].forEach(function (m) {
            seg.appendChild(el('button', {
                type: 'button',
                className: 'bowire-invoke-url-mode-btn' + (ov.mode === m[0] ? ' is-active' : ''),
                disabled: m[2] ? 'disabled' : null,
                title: m[2] ? 'No workspace Source URLs to pick from' : null,
                onClick: m[2] ? null : function () { setMode(m[0]); }
            }, el('span', { textContent: m[1] })));
        });
        block.appendChild(seg);

        // Mode-specific control.
        var body = el('div', { className: 'bowire-invoke-url-body' });
        if (ov.mode === 'source') {
            var sel = el('select', { className: 'bowire-invoke-url-select', 'aria-label': 'Source URL',
                onChange: function (e) { _ovSet('source', e.target.value); render(); }
            });
            serverUrls.forEach(function (u) {
                var label = (typeof aliasForUrl === 'function') ? aliasForUrl(u) : u;
                sel.appendChild(el('option', { value: u, selected: ov.url === u, textContent: label }));
            });
            body.appendChild(sel);
        } else if (ov.mode === 'inline') {
            body.appendChild(el('input', {
                type: 'text',
                className: 'bowire-invoke-url-input',
                value: ov.url || '',
                placeholder: 'https://api.example.com',
                'aria-label': 'Custom invocation URL',
                'data-bowire-no-vars-chip': '1',
                onChange: function (e) { _ovSet('inline', e.target.value); }
            }));
            body.appendChild(el('span', { className: 'bowire-invoke-url-hint',
                textContent: '{{vars}} are substituted at send time.' }));
        } else {
            body.appendChild(el('span', { className: 'bowire-invoke-url-schema',
                textContent: schemaUrl || '(embedded host)' }));
            body.appendChild(el('span', { className: 'bowire-invoke-url-hint',
                textContent: 'The call goes to the same URL the schema came from.' }));
        }
        block.appendChild(body);
        return block;
    }

    // The invocation URL a SAVED collection item should hit. Extends #252's
    // urlMode resolution with the invocation split: an item may carry
    // invocationUrlMode ('same-as-schema' | 'source' | 'inline') +
    // invocationUrl. 'same-as-schema' (the migration default) reproduces the
    // pre-#253 behaviour of calling the schema/source URL the item was saved
    // against. A saved item has no live service.originUrl, so 'same-as-schema'
    // resolves to the item's own schema url (schemaResolvedUrl passed in).
    function resolveItemInvocationUrl(item, schemaResolvedUrl) {
        var mode = item && item.invocationUrlMode;
        if (mode === 'inline') {
            var u = (item.invocationUrl || '').trim();
            return u || schemaResolvedUrl;
        }
        if (mode === 'source') {
            return resolveSourceUrl(item.invocationUrl, 'collection item invocation') || schemaResolvedUrl;
        }
        // 'same-as-schema' or absent (pre-#253) → the schema URL.
        return schemaResolvedUrl;
    }
