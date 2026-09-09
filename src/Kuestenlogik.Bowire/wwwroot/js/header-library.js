// #95 — Header Library: named, scoped, toggleable header sets.
//
// The problem this solves: `Accept: application/vnd.example+json`,
// `X-Api-Version: 2`, `User-Agent: my-tester/1.0` get retyped on every
// method, every environment, every workspace. The Metadata tab is
// per-request, and environments give variables that header values
// *reference* — neither gives a place to say "every call against
// api.example.com sends these three headers". Today the operator either
// pastes the same rows everywhere or writes a pre-request script.
//
// A library entry is a named set of header rows plus a scope that says
// where it applies. Sets whose scope matches the request auto-apply; the
// operator can flip any of them off (or a non-matching one on) for the
// current request from a chip strip above the header editor.
//
// Storage is workspace-scoped (wsKey), so a library travels with the
// workspace through .bww export / import like collections and
// environments do.
//
// Everything above the "---- state ----" line is pure: scope matching and
// composition take their inputs as arguments and touch no module state,
// so the unit tests exercise the actual precedence rules rather than a
// re-implementation of them.

// ---------------------------------------------------------------- pure

/**
 * Split a scope string into { kind, value }.
 *
 * Accepted forms (anything else is treated as 'global', because a set
 * with an unreadable scope should still be reachable by hand rather than
 * silently vanish from the chip strip):
 *
 *   global                      — applies everywhere
 *   url:api.example.com         — applies when the request host matches
 *   service:UserService         — applies to one discovered service
 *   method:UserService.GetUser  — applies to one method
 */
function parseHeaderScope(scope) {
    var raw = typeof scope === 'string' ? scope.trim() : '';
    if (!raw || raw === 'global') return { kind: 'global', value: '' };
    var i = raw.indexOf(':');
    if (i < 0) return { kind: 'global', value: '' };
    var kind = raw.slice(0, i).toLowerCase();
    var value = raw.slice(i + 1).trim();
    if (kind !== 'url' && kind !== 'service' && kind !== 'method') {
        return { kind: 'global', value: '' };
    }
    // A known kind with no value yet is INCOMPLETE, not global. The editor
    // is in exactly that state the moment you pick "URL host" and have not
    // typed the host, and silently turning that into "applies everywhere"
    // is the opposite of what was asked for. It matches nothing until the
    // value arrives -- see headerScopeMatches, where an empty value fails
    // every branch.
    return { kind: kind, value: value };
}

/**
 * Reduce anything URL-ish to a bare lowercase host, so `url:` scopes
 * match whether the operator wrote the host, the origin, or pasted a
 * whole URL — and whether the request URL carries a port, a path or a
 * `{{var}}` that has already been substituted.
 *
 * Returns '' when nothing host-like can be read, which never matches.
 */
function headerScopeHost(urlish) {
    var s = typeof urlish === 'string' ? urlish.trim() : '';
    if (!s) return '';
    // Strip a scheme if present, then everything from the first / ? #.
    s = s.replace(/^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//, '');
    s = s.split(/[/?#]/)[0];
    // Drop credentials and the port.
    var at = s.lastIndexOf('@');
    if (at >= 0) s = s.slice(at + 1);
    // IPv6 literal: keep the bracketed form, drop a trailing :port.
    if (s.charAt(0) === '[') {
        var close = s.indexOf(']');
        if (close > 0) return s.slice(0, close + 1).toLowerCase();
    }
    var colon = s.lastIndexOf(':');
    if (colon > 0) s = s.slice(0, colon);
    return s.toLowerCase();
}

/**
 * Does a scope apply to this request?
 *
 * `ctx` is { url, service, method } — any of them may be absent, and an
 * absent field simply fails the scopes that depend on it. A freeform
 * request with no discovered service still matches `global` and `url:`.
 */
function headerScopeMatches(scope, ctx) {
    var parsed = parseHeaderScope(scope);
    var c = ctx || {};
    switch (parsed.kind) {
        case 'global':
            return true;
        case 'url':
            var want = headerScopeHost(parsed.value);
            var have = headerScopeHost(c.url);
            return !!want && !!have && want === have;
        case 'service':
            return !!c.service && String(c.service).toLowerCase() === parsed.value.toLowerCase();
        case 'method':
            // `method:Service.Method` — the last dot separates them, so a
            // dotted service name (a proto package path) still parses.
            var dot = parsed.value.lastIndexOf('.');
            if (dot <= 0) return false;
            var svc = parsed.value.slice(0, dot).toLowerCase();
            var mth = parsed.value.slice(dot + 1).toLowerCase();
            return !!c.service && !!c.method
                && String(c.service).toLowerCase() === svc
                && String(c.method).toLowerCase() === mth;
        default:
            return false;
    }
}

/**
 * Which sets are on for this request?
 *
 * Scope decides the default; `overrides` (a { [setId]: boolean } map kept
 * on the request) is the operator's per-request answer and always wins —
 * that is what makes a chip a toggle rather than a read-out. A set the
 * scope does not match can be switched on by hand for one call.
 *
 * Order is library order, which is what the precedence rule below means
 * by "later wins".
 */
function activeHeaderSets(library, ctx, overrides) {
    var ov = overrides || {};
    return (Array.isArray(library) ? library : []).filter(function (set) {
        if (!set || !set.id) return false;
        if (Object.prototype.hasOwnProperty.call(ov, set.id)) return !!ov[set.id];
        return headerScopeMatches(set.scope, ctx);
    });
}

/**
 * Merge the active library sets under the request's own header rows.
 *
 * Precedence, lowest first:
 *   1. library sets, in library order — a later set overwrites an
 *      earlier one that names the same header;
 *   2. the request's own rows — a header typed into the Metadata tab
 *      always wins, so a library can never silently override what the
 *      operator is looking at.
 *
 * Header names are compared case-insensitively (HTTP says they are
 * case-insensitive, and `Accept` beside `accept` is a bug every time),
 * but the *first* spelling seen is the one that ships.
 *
 * Returns:
 *   rows       — [{ key, value, source, setId, setName }] in send order
 *   conflicts  — [{ key, winner, losers: [...] }] for the UI to mark
 *
 * Disabled rows (enabled === false) are dropped from both sides before
 * merging, so an unticked row neither ships nor shadows a library value.
 */
function composeHeaderRows(requestRows, sets, opts) {
    var o = opts || {};
    var identity = function (v) { return v; };
    var substitute = typeof o.substitute === 'function' ? o.substitute : identity;
    // A library value is authored once and reused, so a {{token}} in it
    // has to resolve wherever it lands. A request's own row is whatever
    // the surrounding execute path already does with it -- some of them
    // substitute, some do not, and #95 is not the ticket that changes
    // that. Hence two hooks rather than one.
    var substituteLibrary = typeof o.substituteLibrary === 'function'
        ? o.substituteLibrary : substitute;
    var order = [];               // lowercase key, in first-seen order
    var byKey = Object.create(null);
    var contributions = Object.create(null);

    function put(key, value, source, set) {
        var name = String(key == null ? '' : key).trim();
        if (!name) return;
        var lower = name.toLowerCase();
        if (!(lower in byKey)) {
            order.push(lower);
            contributions[lower] = [];
        }
        var entry = {
            key: (lower in byKey) ? byKey[lower].key : name,
            value: (source === 'library' ? substituteLibrary : substitute)(
                String(value == null ? '' : value)),
            source: source,
            setId: set ? set.id : null,
            setName: set ? set.name : null
        };
        contributions[lower].push(entry);
        byKey[lower] = entry;     // last writer wins
    }

    function rowsOf(container) {
        var rows = container && container.headers;
        return Array.isArray(rows) ? rows : [];
    }

    (Array.isArray(sets) ? sets : []).forEach(function (set) {
        rowsOf(set).forEach(function (row) {
            if (row && row.enabled === false) return;
            put(row && row.key, row && row.value, 'library', set);
        });
    });

    (Array.isArray(requestRows) ? requestRows : []).forEach(function (row) {
        if (row && row.enabled === false) return;
        put(row && row.key, row && row.value, 'request', null);
    });

    var rows = order.map(function (lower) { return byKey[lower]; });
    var conflicts = order.filter(function (lower) {
        return contributions[lower].length > 1;
    }).map(function (lower) {
        var all = contributions[lower];
        return {
            key: byKey[lower].key,
            winner: all[all.length - 1],
            losers: all.slice(0, all.length - 1)
        };
    });
    return { rows: rows, conflicts: conflicts };
}

/**
 * The send-ready object: what composeHeaderRows worked out, flattened.
 * Callers that already hold a plain { name: value } map (every execute
 * path does) use this and never see the provenance.
 */
function composeHeaderObject(requestRows, sets, opts) {
    var out = {};
    composeHeaderRows(requestRows, sets, opts).rows.forEach(function (r) {
        out[r.key] = r.value;
    });
    return out;
}

/**
 * A stable id for a new set. Not cryptographic — it only has to not
 * collide inside one workspace's library.
 */
function newHeaderSetId() {
    return 'hs_' + Date.now().toString(36) + '_' + Math.random().toString(36).slice(2, 8);
}

/**
 * Drop anything a hand-edited or imported library should not carry:
 * missing ids, non-array header rows, rows without a name. Returns a new
 * array — an import must never be able to wedge the chip strip.
 */
function sanitiseHeaderLibrary(raw) {
    if (!Array.isArray(raw)) return [];
    var seen = Object.create(null);
    var out = [];
    raw.forEach(function (set) {
        if (!set || typeof set !== 'object') return;
        var id = typeof set.id === 'string' && set.id ? set.id : newHeaderSetId();
        if (seen[id]) id = newHeaderSetId();
        seen[id] = true;
        var headers = Array.isArray(set.headers) ? set.headers : [];
        out.push({
            id: id,
            name: typeof set.name === 'string' ? set.name.trim() : '',
            scope: parseHeaderScopeToString(set.scope),
            headers: headers.filter(function (r) {
                return r && typeof r === 'object' && String(r.key == null ? '' : r.key).trim();
            }).map(function (r) {
                return {
                    key: String(r.key).trim(),
                    value: String(r.value == null ? '' : r.value),
                    description: typeof r.description === 'string' ? r.description : '',
                    enabled: r.enabled !== false
                };
            })
        });
    });
    return out;
}

/** Round-trip a scope through the parser so stored values stay canonical. */
function parseHeaderScopeToString(scope) {
    var p = parseHeaderScope(scope);
    return p.kind === 'global' ? 'global' : p.kind + ':' + p.value;
}

// ---------------------------------------------------------------- state

// Workspace-scoped, so the library travels with the workspace rather
// than following the browser profile. Registered in
// _WORKSPACE_DATA_KEYS / _V2_DATA_KEY_MAP so export / import carries it.
var HEADER_LIBRARY_KEY = 'bowire_header_library';
var headerLibrary = [];

function loadHeaderLibrary() {
    try {
        var raw = localStorage.getItem(wsKey(HEADER_LIBRARY_KEY));
        headerLibrary = sanitiseHeaderLibrary(raw ? JSON.parse(raw) : []);
    } catch { headerLibrary = []; }
    return headerLibrary;
}

function persistHeaderLibrary() {
    try {
        // The editor keeps one empty trailing row per set to type into.
        // That is an editing affordance, not data -- strip it on the way
        // out so an exported .bww carries the sets the operator wrote and
        // nothing else. The in-memory array keeps its trailing row.
        var clean = headerLibrary.map(function (set) {
            return {
                id: set.id,
                name: set.name,
                scope: set.scope,
                headers: (set.headers || []).filter(function (r) {
                    return r && String(r.key == null ? '' : r.key).trim();
                })
            };
        });
        localStorage.setItem(wsKey(HEADER_LIBRARY_KEY), JSON.stringify(clean));
        if (typeof markSaved === 'function') markSaved(t('headerLibrary.saved'));
    } catch { /* quota / private mode — the library stays in memory */ }
}

// ------------------------------------------------------------- request

// Which request are we composing for? Resolved fresh on every call —
// never captured in a closure. morphdom keeps DOM nodes alive across a
// tab or protocol switch, so a handler that closed over "the" request
// would go on writing into the request the operator has already left.
function headerRequestContext() {
    if (typeof freeformRequest === 'undefined' || !freeformRequest) return {};
    return {
        url: freeformRequest.serverUrl || '',
        service: freeformRequest.service
            || (typeof selectedService !== 'undefined' && selectedService ? selectedService.name : ''),
        method: freeformRequest.method
            || (typeof selectedMethod !== 'undefined' && selectedMethod ? selectedMethod.name : '')
    };
}

// The operator's per-request answer to "is this set on?". Lives on the
// request-builder state so it travels with the request tab, and is
// created lazily so an untouched request carries no noise.
function currentHeaderSetOverrides() {
    if (typeof freeformRequest === 'undefined' || !freeformRequest) return null;
    var rb = freeformRequest._requestBuilder;
    if (!rb || typeof rb !== 'object') return null;
    if (!rb.headerSets || typeof rb.headerSets !== 'object') rb.headerSets = {};
    return rb.headerSets;
}

/**
 * Flip one set for the current request. Setting the override back to the
 * scope's own answer removes it again, so a request that agrees with its
 * scope stores nothing and a later scope edit still reaches it.
 */
function toggleHeaderSet(setId) {
    var ov = currentHeaderSetOverrides();
    if (!ov) return;
    var set = headerLibrary.find(function (s) { return s.id === setId; });
    if (!set) return;
    var byScope = headerScopeMatches(set.scope, headerRequestContext());
    var current = Object.prototype.hasOwnProperty.call(ov, setId) ? !!ov[setId] : byScope;
    var next = !current;
    if (next === byScope) delete ov[setId];
    else ov[setId] = next;
}

/**
 * What this request will actually send, library included. Every execute
 * path calls this instead of reading its KV rows directly, so the
 * workbench and the preview can never disagree about what went out.
 */
function effectiveRequestHeaders(requestRows, opts) {
    var ctx = headerRequestContext();
    var sets = activeHeaderSets(headerLibrary, ctx, currentHeaderSetOverrides() || {});
    return composeHeaderRows(requestRows, sets, opts);
}

/** Send-ready map for an execute path that already holds a plain object. */
function effectiveRequestHeaderObject(requestRows, opts) {
    var out = {};
    effectiveRequestHeaders(requestRows, opts).rows.forEach(function (r) {
        out[r.key] = r.value;
    });
    return out;
}

// -------------------------------------------------------------- render

// Preview open/closed is a view affordance, not request data, so it does
// not belong on the request. One flag for the workbench is enough —
// only one header editor is on screen at a time.
var headerLibraryPreviewOpen = false;

/**
 * The chip strip above a header editor: one chip per library set, a count
 * of what will actually be sent, and a way into the editor.
 *
 * Returns null when the library is empty — an empty strip would cost
 * vertical space to say nothing, and an operator with no sets yet has to
 * go to Settings anyway.
 */
function renderHeaderLibraryStrip(requestRows) {
    if (!Array.isArray(headerLibrary) || headerLibrary.length === 0) return null;

    var ctx = headerRequestContext();
    var overrides = currentHeaderSetOverrides() || {};
    var active = activeHeaderSets(headerLibrary, ctx, overrides);
    var activeIds = {};
    active.forEach(function (s) { activeIds[s.id] = true; });
    var composed = composeHeaderRows(requestRows, active, {
        substitute: typeof substituteVars === 'function' ? substituteVars : null
    });

    // Which sets lost a header to someone else? Marked on the chip, so a
    // set that looks on but is being overridden says so instead of
    // quietly not mattering.
    var overridden = {};
    composed.conflicts.forEach(function (c) {
        c.losers.forEach(function (l) { if (l.setId) overridden[l.setId] = true; });
    });

    var strip = el('div', { className: 'bowire-header-library-strip' });

    strip.appendChild(el('span', {
        className: 'bowire-header-library-label',
        textContent: t('headerLibrary.strip.label')
    }));

    headerLibrary.forEach(function (set) {
        var on = !!activeIds[set.id];
        var byScope = headerScopeMatches(set.scope, ctx);
        var pinned = Object.prototype.hasOwnProperty.call(overrides, set.id);
        var count = (set.headers || []).filter(function (r) { return r.enabled !== false; }).length;

        // #117 - one whole sentence per state rather than four fragments
        // glued together. The old form was
        // `(on ? 'On' : 'Off') + ' - ' + why + '. Click to turn ' + ...`,
        // which no language that orders those parts differently can
        // reproduce. This is the concatenation rule in its clearest case.
        var reason = pinned
            ? (on ? t('headerLibrary.chip.pinnedOn') : t('headerLibrary.chip.pinnedOff'))
            : (byScope
                ? t('headerLibrary.chip.scopeMatch', { scope: set.scope })
                : t('headerLibrary.chip.scopeNoMatch', { scope: set.scope }));
        var invitation = on ? t('headerLibrary.chip.clickOff') : t('headerLibrary.chip.clickOn');

        strip.appendChild(el('button', {
            className: 'bowire-header-set-chip'
                + (on ? ' is-on' : '')
                + (pinned ? ' is-pinned' : '')
                + (on && overridden[set.id] ? ' is-overridden' : ''),
            'aria-pressed': on ? 'true' : 'false',
            title: reason + ' ' + invitation,
            dataset: { setId: set.id },
            onClick: function (e) {
                // Re-resolve from the DOM rather than from this closure:
                // morphdom may have carried the node over from another
                // request, and the closure would still point at that one.
                var target = e && e.currentTarget;
                var id = (target && target.dataset && target.dataset.setId) || set.id;
                toggleHeaderSet(id);
                render();
            }
        },
            el('span', {
                className: 'bowire-header-set-chip-name',
                textContent: set.name || t('headerLibrary.settings.untitled')
            }),
            el('span', { className: 'bowire-header-set-chip-meta', textContent: String(count) }),
            (on && overridden[set.id])
                ? el('span', {
                    className: 'bowire-header-set-chip-warn',
                    title: t('headerLibrary.chip.overridden'),
                    textContent: '⚠'
                })
                : null
        ));
    });

    strip.appendChild(el('span', { className: 'bowire-header-library-spacer' }));

    strip.appendChild(el('button', {
        className: 'bowire-header-library-toggle' + (headerLibraryPreviewOpen ? ' is-open' : ''),
        'aria-expanded': headerLibraryPreviewOpen ? 'true' : 'false',
        title: t('headerLibrary.strip.effectiveTitle'),
        onClick: function () { headerLibraryPreviewOpen = !headerLibraryPreviewOpen; render(); }
    },
        el('span', { textContent: t('headerLibrary.strip.effective', { count: composed.rows.length }) }),
        el('span', {
            className: 'bowire-header-library-caret',
            innerHTML: svgIcon(headerLibraryPreviewOpen ? 'chevronUp' : 'chevronDown'),
            style: 'width:12px;height:12px;display:flex'
        })
    ));

    strip.appendChild(el('button', {
        className: 'bowire-header-library-manage',
        title: t('headerLibrary.strip.manageTitle'),
        textContent: t('common.manage'),
        onClick: function () {
            if (typeof openHeaderLibraryEditor === 'function') openHeaderLibraryEditor();
        }
    }));

    if (!headerLibraryPreviewOpen) return strip;

    var wrap = el('div', { className: 'bowire-header-library-block' }, strip);
    wrap.appendChild(renderHeaderLibraryPreview(composed));
    return wrap;
}

/**
 * The merged result, in send order, saying where each header came from.
 * This answers "why is my request sending that?" — the question a
 * scoped, layered header set creates the moment it starts working.
 */
function renderHeaderLibraryPreview(composed) {
    var table = el('div', { className: 'bowire-header-library-preview' });

    if (composed.rows.length === 0) {
        table.appendChild(el('div', {
            className: 'bowire-header-library-preview-empty',
            textContent: t('headerLibrary.preview.empty')
        }));
        return table;
    }

    table.appendChild(el('div', { className: 'bowire-header-library-preview-head' },
        el('span', { textContent: t('headerLibrary.preview.header') }),
        el('span', { textContent: t('headerLibrary.preview.value') }),
        el('span', { textContent: t('headerLibrary.preview.from') })
    ));

    var beaten = {};
    composed.conflicts.forEach(function (c) {
        beaten[c.key.toLowerCase()] = c.losers.map(function (l) {
            return l.setName || t('headerLibrary.preview.thisRequest');
        });
    });

    composed.rows.forEach(function (r) {
        var lost = beaten[r.key.toLowerCase()];
        table.appendChild(el('div', { className: 'bowire-header-library-preview-row' },
            el('span', { className: 'bowire-header-library-preview-key', textContent: r.key }),
            el('span', { className: 'bowire-header-library-preview-val', textContent: r.value }),
            el('span', { className: 'bowire-header-library-preview-src' },
                el('span', {
                    className: 'bowire-header-library-preview-src-name',
                    textContent: r.source === 'request'
                        ? t('headerLibrary.preview.thisRequest')
                        : (r.setName || t('headerLibrary.preview.library'))
                }),
                lost
                    ? el('span', {
                        className: 'bowire-header-library-preview-beat',
                        title: t('headerLibrary.preview.alsoSetBy', { names: lost.join(', ') }),
                        textContent: t('headerLibrary.preview.winsOver', { count: lost.length })
                    })
                    : null
            )
        ));
    });
    return table;
}

// -------------------------------------------------------------- editor

/**
 * The chip strip's "Manage" affordance. The Settings dialog is already
 * the modal the ticket asks for, so the library gets a page in it rather
 * than a second overlay stacked on top of the first.
 */
function openHeaderLibraryEditor() {
    if (typeof openSettings === 'function') openSettings('workspace-headers');
}

var HEADER_SCOPE_KINDS = [
    { id: 'global',  label: 'headerLibrary.scope.global',  hint: '' },
    { id: 'url',     label: 'headerLibrary.scope.url',     hint: 'api.example.com' },
    { id: 'service', label: 'headerLibrary.scope.service', hint: 'UserService' },
    { id: 'method',  label: 'headerLibrary.scope.method',  hint: 'UserService.GetUser' }
];

/** Persist, then repaint the dialog. Used by every structural edit. */
function _headerLibraryChanged() {
    persistHeaderLibrary();
    if (typeof renderSettingsDialog === 'function') renderSettingsDialog();
}

/** Settings → Workspace → Header library. */
function renderSettingsHeaderLibrary() {
    var section = el('div', { className: 'bowire-settings-section' });
    var ws = (typeof _renderWorkspaceSubpageHeader === 'function')
        ? _renderWorkspaceSubpageHeader(section, t('headerLibrary.settings.title'),
            t('headerLibrary.settings.lede'))
        : true;
    if (!ws) return section;

    section.appendChild(el('div', { className: 'bowire-settings-row', style: 'margin-bottom:12px' },
        el('button', {
            className: 'bowire-settings-action-btn',
            onClick: function () {
                headerLibrary.push({
                    id: newHeaderSetId(),
                    // Empty rather than 'New set': a translated default would
                    // otherwise sit in the workspace for ever, in whichever
                    // language happened to be active when it was created.
                    name: '',
                    scope: 'global',
                    headers: []
                });
                _headerLibraryChanged();
            }
        },
            el('span', { innerHTML: svgIcon('plus'), style: 'width:13px;height:13px;display:flex' }),
            el('span', { textContent: t('headerLibrary.settings.newSet'), style: 'margin-left:5px' })
        )
    ));

    if (headerLibrary.length === 0) {
        section.appendChild(el('div', { className: 'bowire-header-library-empty' },
            el('p', { textContent: t('headerLibrary.settings.emptyTitle') }),
            el('p', {
                className: 'bowire-header-library-empty-hint',
                textContent: t('headerLibrary.settings.emptyHint')
            })
        ));
        return section;
    }

    headerLibrary.forEach(function (set) {
        section.appendChild(_renderHeaderSetCard(set));
    });
    return section;
}

function _renderHeaderSetCard(set) {
    var card = el('div', {
        className: 'bowire-header-set-card',
        dataset: { setId: set.id }
    });

    // ---- name + scope + delete -------------------------------------
    var head = el('div', { className: 'bowire-header-set-card-head' });

    head.appendChild(el('input', {
        className: 'bowire-settings-input bowire-header-set-name-input',
        type: 'text',
        value: set.name,
        'aria-label': t('headerLibrary.set.nameLabel'),
        placeholder: t('headerLibrary.set.nameLabel'),
        // Mutate in place while typing, repaint only on the way out --
        // a render per keystroke tears focus off the input (the lesson
        // the request-builder KV table records in its own comment).
        onInput: function (e) { _resolveHeaderSet(card, set).name = e.target.value; },
        onFocusOut: function () { persistHeaderLibrary(); }
    }));

    var parsed = parseHeaderScope(set.scope);

    var kindSelect = el('select', {
        className: 'bowire-settings-select bowire-header-set-scope-kind',
        'aria-label': t('headerLibrary.set.scopeLabel'),
        onChange: function (e) {
            var target = _resolveHeaderSet(card, set);
            var kind = e.target.value;
            var current = parseHeaderScope(target.scope);
            target.scope = kind === 'global' ? 'global' : kind + ':' + (current.value || '');
            _headerLibraryChanged();
        }
    });
    HEADER_SCOPE_KINDS.forEach(function (k) {
        // `selected` has to be set as a PROPERTY after construction. el()
        // routes unknown keys through setAttribute, and `selected="false"`
        // is still a present attribute -- every option would be marked and
        // the last one would win.
        var opt = el('option', { value: k.id, textContent: t(k.label) });
        if (k.id === parsed.kind) opt.selected = true;
        kindSelect.appendChild(opt);
    });
    head.appendChild(kindSelect);

    if (parsed.kind !== 'global') {
        var hint = (HEADER_SCOPE_KINDS.find(function (k) { return k.id === parsed.kind; }) || {}).hint;
        head.appendChild(el('input', {
            className: 'bowire-settings-input bowire-header-set-scope-value',
            type: 'text',
            value: parsed.value,
            placeholder: hint || '',
            'aria-label': t('headerLibrary.set.scopeValueLabel'),
            onInput: function (e) {
                var target = _resolveHeaderSet(card, set);
                target.scope = parseHeaderScope(target.scope).kind + ':' + e.target.value;
            },
            onFocusOut: function () { persistHeaderLibrary(); }
        }));
    }

    head.appendChild(el('span', { style: 'flex:1' }));

    head.appendChild(el('button', {
        className: 'bowire-settings-action-btn bowire-header-set-delete',
        title: t('headerLibrary.set.delete'),
        'aria-label': t('headerLibrary.set.deleteAria', {
            name: set.name || t('headerLibrary.settings.untitled')
        }),
        innerHTML: svgIcon('trash'),
        onClick: function () {
            var id = card.dataset.setId;
            var i = headerLibrary.findIndex(function (s) { return s.id === id; });
            if (i >= 0) headerLibrary.splice(i, 1);
            _headerLibraryChanged();
        }
    }));

    card.appendChild(head);

    // ---- header rows -----------------------------------------------
    var rows = set.headers;
    // One trailing empty row to type into, same model as the request
    // builder's KV table.
    if (rows.length === 0 || rows[rows.length - 1].key || rows[rows.length - 1].value) {
        rows.push({ key: '', value: '', description: '', enabled: true });
    }

    var table = el('div', { className: 'bowire-header-set-rows' });
    table.appendChild(el('div', { className: 'bowire-header-set-row bowire-header-set-row-head' },
        el('span', { textContent: t('headerLibrary.set.colHeader') }),
        el('span', { textContent: t('headerLibrary.set.colValue') }),
        el('span', { textContent: t('headerLibrary.set.colDescription') }),
        el('span', { textContent: '' }),
        el('span', { textContent: '' })
    ));

    rows.forEach(function (r, idx) {
        var row = el('div', { className: 'bowire-header-set-row' });
        ['key', 'value', 'description'].forEach(function (field) {
            row.appendChild(el('input', {
                className: 'bowire-settings-input',
                type: 'text',
                value: r[field] || '',
                'aria-label': t('headerLibrary.row.' + field + 'Aria'),
                placeholder: t('headerLibrary.row.' + field),
                onInput: function (e) {
                    var live = _resolveHeaderRow(card, set, idx);
                    if (live) live[field] = e.target.value;
                },
                onFocusOut: function (e) {
                    // Leaving the row entirely appends a fresh trailing
                    // row; moving between this row's own inputs does not.
                    persistHeaderLibrary();
                    if (!e.relatedTarget || !row.contains(e.relatedTarget)) {
                        if (typeof renderSettingsDialog === 'function') renderSettingsDialog();
                    }
                }
            }));
        });
        row.appendChild(el('button', {
            className: 'bowire-header-set-row-toggle' + (r.enabled === false ? '' : ' is-on'),
            title: r.enabled === false ? t('headerLibrary.row.disabled') : t('headerLibrary.row.enabled'),
            'aria-pressed': r.enabled === false ? 'false' : 'true',
            textContent: r.enabled === false ? '' : '✓',
            onClick: function () {
                var live = _resolveHeaderRow(card, set, idx);
                if (live) live.enabled = live.enabled === false;
                _headerLibraryChanged();
            }
        }));
        row.appendChild(el('button', {
            className: 'bowire-header-set-row-del',
            title: t('headerLibrary.row.remove'),
            'aria-label': t('headerLibrary.row.removeAria'),
            innerHTML: svgIcon('close'),
            onClick: function () {
                var live = _resolveHeaderSet(card, set);
                if (live && idx < live.headers.length) live.headers.splice(idx, 1);
                _headerLibraryChanged();
            }
        }));
        table.appendChild(row);
    });

    card.appendChild(table);
    return card;
}

// Re-resolve the set (and row) from the library at event time rather than
// trusting the object this closure captured. morphdom reuses DOM nodes
// across renders, so a handler bound while the card showed set A can fire
// after the same node has been repainted as set B; the id on the card is
// what the DOM actually shows.
function _resolveHeaderSet(card, fallback) {
    var id = card && card.dataset ? card.dataset.setId : null;
    return headerLibrary.find(function (s) { return s.id === id; }) || fallback;
}

function _resolveHeaderRow(card, fallbackSet, idx) {
    var set = _resolveHeaderSet(card, fallbackSet);
    if (!set || !Array.isArray(set.headers)) return null;
    return set.headers[idx] || null;
}
