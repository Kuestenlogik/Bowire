    // ---- Bowire CLI export (#538) ----
    //
    // Turns the current request into a runnable `bowire call …` line. This
    // is the one code export that is not a translation into somebody
    // else's tool, which makes it the one that can rot silently: rename a
    // flag in BowireCli.BuildCallCommand and every other exporter keeps
    // working while this one starts emitting a command the CLI rejects.
    //
    // The honesty mechanism is a two-sided golden fixture,
    // tests/Kuestenlogik.Bowire.Tests/Cli/cli-export-golden.json:
    //   * wwwroot-js/cli-export.test.mjs replays every ctx through the
    //     functions below and pins argv + the rendered POSIX string;
    //   * Cli/CliExportGrammarTests.cs feeds the SAME argv arrays through
    //     the real System.CommandLine `call` command and asserts zero
    //     parse errors.
    // Change either side alone and one of them goes red.
    //
    // Everything here is a PURE function: no DOM writes, no module-scope
    // assignment, no async, nothing that can throw on a missing global.
    // generateCliCommand is reachable from renderRequestPane (the Code
    // tab) and from two click handlers that call the generator map
    // directly, i.e. outside generateCodeSnippet's try/catch — and
    // render() has no try/catch at all.

    // Placeholder refs whose value must never be baked into a copied
    // command. substituteVars resolves {{secret.NAME}} and
    // {{keyring.svc/acct}} to the REAL credential; the curl/fetch/python
    // generators have always done that, and we are deliberately not
    // copying the behaviour into a string an operator is likely to paste
    // into a ticket or a shell history file.
    function cliSecretRefPattern() {
        return /\{\{\s*(?:secret|keyring)\.[^}]*\}\}|\$\{\s*(?:secret|keyring)\.[^}]*\}/g;
    }

    // Every {{name}} / ${name} ref in a string, minus the prefixes that
    // are not workbench variables (response chaining, runtime system vars,
    // and the secret refs handled separately above).
    function cliVarRefs(text) {
        var out = [];
        if (typeof text !== 'string' || !text) return out;
        var re = /\{\{\s*([^{}]+?)\s*\}\}|\$\{\s*([^}]+?)\s*\}/g;
        var m;
        while ((m = re.exec(text)) !== null) {
            var name = (m[1] || m[2] || '').trim();
            if (!name) continue;
            if (/^(secret|keyring|response|prev|runtime)\./.test(name)) continue;
            if (/^(now|nowMs|timestamp|uuid|random)$/.test(name)) continue;
            if (/^now[+-]\d+$/.test(name)) continue;
            if (out.indexOf(name) === -1) out.push(name);
        }
        return out;
    }

    function cliHasSecretRef(text) {
        return typeof text === 'string' && cliSecretRefPattern().test(text);
    }

    // substituteVars, with {{secret.*}} / {{keyring.*}} masked out and
    // restored verbatim afterwards. The sentinel is a token substituteVars
    // has no reason to touch; it never survives the round trip.
    function cliResolveVars(text) {
        if (typeof text !== 'string' || !text) return text;
        if (typeof substituteVars !== 'function') return text;
        var refs = [];
        var masked = text.replace(cliSecretRefPattern(), function (match) {
            refs.push(match);
            return '__BOWIRE_SECRET_' + (refs.length - 1) + '__';
        });
        var out = substituteVars(masked);
        for (var i = 0; i < refs.length; i++) {
            out = out.split('__BOWIRE_SECRET_' + i + '__').join(refs[i]);
        }
        return out;
    }

    // The plugin id the CLI needs. selectedService.source is already the
    // IBowireProtocol.Id, so this is a read with a fallback rather than a
    // mapping table.
    function cliProtocolId(ctx) {
        return (ctx && (ctx.protocolId || ctx.source)) || 'rest';
    }

    // Does `hint@url` parse back to (hint, url)? Mirrors the three rules in
    // BowireServerUrl.Parse so the generated line cannot compose something
    // the CLI would read as URI userinfo.
    function cliUrlCarriesHint(url) {
        if (typeof url !== 'string') return false;
        var at = url.indexOf('@');
        if (at <= 0) return false;
        var head = url.substring(0, at);
        var rest = url.substring(at + 1);
        if (!/^[A-Za-z0-9-]+$/.test(head)) return false;
        return rest.indexOf('://') !== -1;
    }

    // How to name the target: prefer the compact `protocol@url` form the
    // sidebar and /api/services already speak; fall back to an explicit
    // --protocol when the URL cannot carry a hint (no scheme, or the '@'
    // would collide with userinfo).
    //
    // Returns { url, protocolFlag, note }. protocolFlag is '' when the
    // hint rode along in the URL.
    function cliUrlSpec(ctx) {
        var bare = (ctx && ctx.serverUrl) || '';
        var id = cliProtocolId(ctx);
        if (!bare) {
            // Embedded hosts discover in-process and may have no origin URL
            // for a workspace-local schema. Emitting `--url ''` would
            // silently target the CLI's own https://localhost:5001 default.
            return {
                url: '<server-url>',
                protocolFlag: id,
                note: 'This workbench discovered the service in-process, so there is no server URL to '
                    + 'copy. Replace <server-url> with the address the CLI should reach it on.'
            };
        }
        if (cliUrlCarriesHint(bare)) return { url: bare, protocolFlag: '', note: '' };
        if (/^[A-Za-z0-9-]+$/.test(id) && bare.indexOf('://') !== -1) {
            return { url: id + '@' + bare, protocolFlag: '', note: '' };
        }
        return { url: bare, protocolFlag: id, note: '' };
    }

    // `service/method`. CliHandler.CallImplAsync splits on the FIRST '/'
    // only, so a method that is itself a path ('/chat' for WebSocket,
    // 'SSE/events') round-trips correctly. A service-less method still
    // needs the separator or `call` answers with its usage line.
    function cliTarget(ctx) {
        var service = (ctx && ctx.service) || '';
        var method = (ctx && ctx.method) || '';
        return service ? (service + '/' + method) : ('/' + method);
    }

    // What the CLI cannot reproduce. Emitted as leading '#' comments — a
    // comment in POSIX shells and in PowerShell alike — so the block can be
    // pasted whole.
    function cliExportNotes(spec, ctx) {
        var notes = [];
        if (spec.urlNote) notes.push(spec.urlNote);

        var auth = (ctx && ctx.authKind) || 'none';
        if (auth === 'session' || auth === 'oauth2_cc' || auth === 'oauth2_ac'
            || auth === 'custom_token' || auth === 'jwt') {
            notes.push('The active environment fetches its token at request time (' + auth + '); '
                + 'the workbench signs/exchanges it in the browser, so it is not in this command. '
                + "Export the token and add: -H 'Authorization: Bearer $TOKEN'");
        } else if (auth === 'aws_sigv4') {
            notes.push('AWS SigV4 signs each request over its own body and timestamp, so no static '
                + 'header can stand in for it. Use the AWS CLI or an SDK for a signed call.');
        } else if (auth === 'mtls') {
            notes.push('The active environment presents a client certificate; `bowire call` reads '
                + 'client certs from Bowire:Mtls configuration, not from a flag.');
        } else if (auth === 'apikey' && ctx && ctx.authLocation === 'query') {
            notes.push('The environment sends its API key as a QUERY parameter — append it to --url '
                + 'rather than passing it with -H.');
        }

        if (ctx && ctx.clientStreaming) {
            notes.push('Client-streaming: each -d is sent as one frame, then the send side closes. '
                + 'An interactive duplex session needs the workbench.');
        }
        if (spec.secretRefs) {
            notes.push('{{secret.*}} / {{keyring.*}} refs are left unresolved on purpose. '
                + 'The CLI resolves them from --env-file, or from the OS keyring with --keyring on '
                + '`bowire test`.');
        }
        if (typeof uiMode !== 'undefined' && uiMode === 'embedded') {
            notes.push('Needs the CLI: dotnet tool install -g Kuestenlogik.Bowire.Tool');
        }
        return notes;
    }

    // The shell-agnostic description of the command. Everything downstream
    // (argv, the rendered string, the golden fixture) derives from this.
    function buildCliCommandSpec(ctx, opts) {
        opts = opts || {};
        var keepVars = !!opts.keepVars;
        var urlSpec = cliUrlSpec(ctx);

        // Client-streaming sends one -d per frame; everything else sends
        // the single body. keepVars leaves the {{name}} refs in place so
        // the reader can see WHICH variable was used, and pairs them with
        // --var so the line still runs.
        var rawMessages = (ctx && ctx.clientStreaming && ctx.messages && ctx.messages.length)
            ? ctx.messages.slice()
            : [(ctx && (ctx.bodyJsonRaw || ctx.bodyJson)) || '{}'];

        var secretRefs = false;
        var varNames = [];
        var data = [];
        for (var i = 0; i < rawMessages.length; i++) {
            var raw = rawMessages[i];
            if (cliHasSecretRef(raw)) secretRefs = true;
            collectRefs(raw);
            var value = keepVars ? raw : cliResolveVars(raw);
            // An empty object carries no information — `call` defaults to
            // '{}' when -d is absent.
            if (String(value).trim() === '{}' && rawMessages.length === 1) continue;
            data.push(value);
        }

        var headers = [];
        // ALWAYS the raw map, never ctx.metadata — buildCodeExportContext
        // has already run substituteVars over that one, which resolves
        // {{secret.*}} to the live credential. Resolving here instead is
        // what keeps the mask-and-restore pass in cliResolveVars in the
        // loop. ctx.metadata is only a fallback for a context shape that
        // predates metadataRaw.
        var metaSource = (ctx && (ctx.metadataRaw || ctx.metadata)) || {};
        var metaKeys = Object.keys(metaSource);
        for (var h = 0; h < metaKeys.length; h++) {
            var hv = metaSource[metaKeys[h]];
            if (cliHasSecretRef(hv)) secretRefs = true;
            collectRefs(hv);
            headers.push([metaKeys[h], keepVars ? hv : cliResolveVars(hv)]);
        }

        var vars = [];
        if (keepVars) {
            var merged = (typeof getMergedVars === 'function') ? getMergedVars() : {};
            for (var v = 0; v < varNames.length; v++) {
                if (Object.prototype.hasOwnProperty.call(merged, varNames[v])) {
                    vars.push([varNames[v], String(merged[varNames[v]])]);
                }
            }
        }

        var spec = {
            protocol: urlSpec.protocolFlag,
            url: urlSpec.url,
            urlNote: urlSpec.note,
            target: cliTarget(ctx),
            data: data,
            headers: headers,
            // Server-streaming gRPC is auto-detected by the CLI, but every
            // other plugin needs to be told: only the caller knows whether
            // an SSE / WebSocket / broker target should be followed.
            stream: !!(ctx && ctx.serverStreaming),
            vars: vars,
            secretRefs: secretRefs
        };
        spec.notes = cliExportNotes(spec, ctx);
        return spec;

        function collectRefs(text) {
            var found = cliVarRefs(text);
            for (var f = 0; f < found.length; f++) {
                if (varNames.indexOf(found[f]) === -1) varNames.push(found[f]);
            }
        }
    }

    // The token array, in the order the CLI's grammar test replays it —
    // and in the same order cliCommandString renders, so the fixture's
    // `argv` and `rendered` describe one command rather than two.
    // `target` sits ahead of every repeatable option so none of them can
    // swallow the positional.
    function cliCommandArgv(spec) {
        var argv = ['bowire', 'call', '--url', spec.url];
        if (spec.protocol) argv.push('--protocol', spec.protocol);
        argv.push(spec.target);
        for (var i = 0; i < spec.data.length; i++) argv.push('-d', spec.data[i]);
        for (var h = 0; h < spec.headers.length; h++) {
            argv.push('-H', spec.headers[h][0] + ': ' + spec.headers[h][1]);
        }
        if (spec.stream) argv.push('--stream');
        for (var v = 0; v < spec.vars.length; v++) {
            argv.push('--var', spec.vars[v][0] + '=' + spec.vars[v][1]);
        }
        return argv;
    }

    // PowerShell single-quoted strings escape an embedded quote by
    // doubling it; there is no backslash escape.
    function escapeShellPowerShell(s) {
        return String(s).replace(/'/g, "''");
    }

    // Bare tokens stay bare — a line full of unnecessary quotes reads as
    // machine output rather than something a human would type.
    function cliTokenNeedsQuotes(token) {
        return !/^[A-Za-z0-9_@:/.,=+~-]+$/.test(String(token));
    }

    function cliQuoteToken(token, shell) {
        if (!cliTokenNeedsQuotes(token)) return String(token);
        if (shell === 'powershell') return "'" + escapeShellPowerShell(token) + "'";
        return "'" + escapeShellSingleQuote(token) + "'";
    }

    // One flag pair per line, continued with the flavour's line-break
    // character. Grouping keeps a long body from pushing the target off
    // the right edge.
    function cliCommandString(spec, shell) {
        var cont = (shell === 'powershell') ? ' `' : ' \\';
        var lines = ['bowire call'];
        lines.push('--url ' + cliQuoteToken(spec.url, shell));
        if (spec.protocol) lines.push('--protocol ' + cliQuoteToken(spec.protocol, shell));
        lines.push(cliQuoteToken(spec.target, shell));
        for (var i = 0; i < spec.data.length; i++) {
            lines.push('-d ' + cliQuoteToken(spec.data[i], shell));
        }
        for (var h = 0; h < spec.headers.length; h++) {
            lines.push('-H ' + cliQuoteToken(spec.headers[h][0] + ': ' + spec.headers[h][1], shell));
        }
        if (spec.stream) lines.push('--stream');
        for (var v = 0; v < spec.vars.length; v++) {
            lines.push('--var ' + cliQuoteToken(spec.vars[v][0] + '=' + spec.vars[v][1], shell));
        }
        if (lines.length === 1) return lines[0];
        var out = lines[0];
        for (var l = 1; l < lines.length; l++) {
            out += cont + '\n  ' + lines[l];
        }
        return out;
    }

    // Which shell flavour to render. Reads the module-scope preference and
    // falls back to a per-client default — WITHOUT writing it back, so this
    // stays safe to call from the render path.
    function cliExportShellFlavour() {
        if (typeof cliExportShell !== 'undefined' && cliExportShell) return cliExportShell;
        try {
            var nav = (typeof navigator !== 'undefined') ? navigator : null;
            var platform = (nav && nav.userAgentData && nav.userAgentData.platform)
                || (nav && nav.platform) || '';
            if (/win/i.test(String(platform))) return 'powershell';
        } catch (e) { /* headless / sandboxed: fall through to posix */ }
        return 'posix';
    }

    // The single entry point CODE_EXPORT_GENERATORS['bowire-cli'] calls.
    function generateCliCommand(ctx) {
        var keepVars = (typeof cliExportKeepVars !== 'undefined') && !!cliExportKeepVars;
        var spec = buildCliCommandSpec(ctx, { keepVars: keepVars });
        var lines = [];
        for (var i = 0; i < spec.notes.length; i++) lines.push('# ' + spec.notes[i]);
        if (lines.length) lines.push('');
        lines.push(cliCommandString(spec, cliExportShellFlavour()));
        return lines.join('\n');
    }
