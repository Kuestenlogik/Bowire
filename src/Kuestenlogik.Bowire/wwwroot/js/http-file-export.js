    // ---- .http export (#673) ----
    //
    // The current request as a .http block that Rider, IntelliJ, Visual
    // Studio 2022 and VS Code's REST Client run. Collections go the long way
    // through the Interop service's ExportHttp rpc; one request in the Code
    // tab is rendered here so the tab stays synchronous.
    //
    // The two must agree, and a golden fixture holds them to it:
    // tests/Kuestenlogik.Bowire.Interop.Tests/Fixtures/http-snippet-golden.json.
    // wwwroot-js/http-file-export.test.mjs replays every ctx through the
    // functions below; HttpSnippetGoldenTests.cs parses every expected block
    // with the importer's own .http parser.
    //
    // Pure functions, like cli-export.js: generateCodeSnippet's try/catch
    // does not cover every caller, and render() has none of its own.
    //
    // Secrets follow the CLI export's rule: {{secret.x}} and
    // {{keyring.svc/acct}} are never resolved into the text. They become
    // plain {{x}} names the user defines in http-client.private.env.json.

    // keyring.svc/acct → svc_acct: a name every .http client accepts.
    function httpFilePlainName(name) {
        return String(name).replace(/[^A-Za-z0-9_-]/g, '_');
    }

    // Bowire references → the .http dialect's. Collects secret names and
    // references with no .http counterpart into `notes`.
    function httpFileRefs(text, notes) {
        if (typeof text !== 'string' || !text) return text;
        var curly = text.replace(/\$\{([A-Za-z_][\w.\-]*)\}/g, function (_, name) { return '{{' + name + '}}'; });
        return curly.replace(/\{\{([^{}]+)\}\}/g, function (match, raw) {
            var name = raw.trim();
            switch (name) {
                case 'uuid': return '{{$uuid}}';
                case 'now': return '{{$timestamp}}';
                case 'timestamp': return '{{$isoTimestamp}}';
                case 'random': return '{{$randomInt}}';
            }
            var secret = /^(?:secret|keyring)\.(.+)$/.exec(name);
            if (secret) {
                var plain = httpFilePlainName(secret[1]);
                if (notes.privateNames.indexOf(plain) === -1) notes.privateNames.push(plain);
                return '{{' + plain + '}}';
            }
            if (name.indexOf('env.') === 0) return '{{' + name.substring(4) + '}}';
            if (/^(response|prev|runtime|ai)\./.test(name) || /^step\d+\./.test(name)) {
                if (notes.unmapped.indexOf(name) === -1) notes.unmapped.push(name);
            }
            return match;
        });
    }

    // Resolve variables but leave secrets as references. cliResolveVars
    // (cli-export.js) does exactly that; without it, keep everything.
    function httpFileResolve(text) {
        return (typeof cliResolveVars === 'function') ? cliResolveVars(text) : text;
    }

    // `# @name` is one token: how a response handler refers to the request.
    function httpFileRequestName(label) {
        var slug = String(label || '').replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '');
        return slug || 'request';
    }

    function httpFileVerbAndUrl(ctx, keepVars) {
        var source = ctx.source || 'rest';
        if (source === 'graphql' || source === 'mcp') return { verb: 'POST', url: ctx.serverUrl || '', pathKeys: [] };
        var verb = String(ctx.httpMethod || 'GET').toUpperCase();
        var url = ctx.serverUrl || '';
        var pathKeys = [];
        if (ctx.httpPath) {
            // Path placeholders take their value from the body, as Execute does.
            var body = keepVars ? ctx.bodyJsonRaw : httpFileResolve(ctx.bodyJsonRaw);
            var values = null;
            try { values = JSON.parse(body || '{}'); } catch (e) { values = null; }
            var path = String(ctx.httpPath).replace(/\{(\w+)\}/g, function (whole, key) {
                pathKeys.push(key);
                if (!values || values[key] === undefined || values[key] === null) return whole;
                // A reference stays a reference; the IDE resolves it.
                var value = String(values[key]);
                return /\{\{[^{}]+\}\}/.test(value) ? value : encodeURIComponent(value);
            });
            url = url.replace(/\/$/, '') + path;
        }
        return { verb: verb, url: url, pathKeys: pathKeys };
    }

    /**
     * The request as a .http block. `opts.keepVars` keeps {{variables}} as
     * references (they resolve from http-client.env.json); otherwise their
     * current values are written. Secrets are references either way.
     */
    function generateHttpFileSnippet(ctx, opts) {
        opts = opts || {};
        var keepVars = !!opts.keepVars;
        var notes = { privateNames: [], unmapped: [] };
        var target = httpFileVerbAndUrl(ctx, keepVars);
        var lines = [];
        var title = ctx.method ? lastSegmentOf(ctx.method) : target.verb;

        lines.push('### ' + title);
        lines.push('# @name ' + httpFileRequestName(title));
        var url = keepVars ? target.url : httpFileResolve(target.url);
        lines.push(target.verb + ' ' + httpFileRefs(url, notes));

        var raw = ctx.metadataRaw || {};
        var keys = Object.keys(raw).sort(function (a, b) { return a.toLowerCase() < b.toLowerCase() ? -1 : a.toLowerCase() > b.toLowerCase() ? 1 : 0; });
        var hasContentType = false;
        for (var i = 0; i < keys.length; i++) {
            if (/^__bowire/i.test(keys[i])) continue;
            if (keys[i].toLowerCase() === 'content-type') hasContentType = true;
            var value = keepVars ? raw[keys[i]] : httpFileResolve(raw[keys[i]]);
            lines.push(keys[i] + ': ' + httpFileRefs(value, notes));
        }

        var bodyRaw = String(ctx.bodyJsonRaw == null ? '' : ctx.bodyJsonRaw);
        var body = (keepVars ? bodyRaw : httpFileResolve(bodyRaw)).replace(/\s+$/, '');
        var trimmed = body.trim();
        var noBodyVerb = /^(GET|HEAD|DELETE|OPTIONS)$/.test(target.verb);
        if (trimmed && !(noBodyVerb && httpFileOnlyPathFields(trimmed, target.pathKeys))) {
            if (!hasContentType && /^[{[]/.test(trimmed)) lines.push('Content-Type: application/json');
            lines.push('');
            lines.push(httpFileRefs(body, notes));
        }

        var head = [];
        if (ctx.authKind && ctx.authKind !== 'none') {
            head.push('# Auth (' + ctx.authKind + ') comes from the Bowire environment; add an Authorization header here.');
        }
        if (notes.privateNames.length > 0) {
            head.push('# Secrets are not exported. Define these in http-client.private.env.json: '
                + notes.privateNames.slice().sort().join(', '));
        }
        for (var u = 0; u < notes.unmapped.length; u++) {
            head.push('# {{' + notes.unmapped[u] + '}} has no .http counterpart without a response-handler script.');
        }
        return (head.length > 0 ? head.join('\n') + '\n' : '') + lines.join('\n') + '\n';
    }

    // A body that only carries the values the path already took (or none
    // at all) is not sent with a GET: Bowire's form holds path parameters
    // in the body, a .http request line holds them in the URL.
    function httpFileOnlyPathFields(body, pathKeys) {
        if (body === '{}') return true;
        var parsed;
        try { parsed = JSON.parse(body); } catch (e) { return false; }
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return false;
        return Object.keys(parsed).every(function (k) { return pathKeys.indexOf(k) !== -1; });
    }

    function lastSegmentOf(name) {
        var parts = String(name || '').split(/[./]/);
        return parts[parts.length - 1] || String(name || '');
    }
