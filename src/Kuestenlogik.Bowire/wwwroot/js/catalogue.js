    // #136 — URL / service catalogue providers (local / http / consul / k8s / agent).
    //
    // On boot the workbench asks the host whether a catalogue provider
    // is configured and, if so, fetches its current snapshot. Catalogue-
    // sourced URLs get merged into `serverUrls` so the existing
    // discovery + sidebar surface picks them up; each merged URL is
    // tagged in `catalogueOriginByUrl` so a later phase can render an
    // origin chip distinguishing it from user-entered URLs.
    //
    // No-op for hosts that haven't called AddBowireCatalogue() — the
    // info endpoint returns { available: false } and the rest of the
    // workbench keeps its current "manual URL entry only" behaviour.

    // Module-scope state: which URLs came from the catalogue + the
    // current catalogue snapshot for surfaces that want richer metadata
    // (Settings → Sources tab, filter popup, &c).
    let catalogueInfo = { available: false };
    let catalogueEntries = [];
    let catalogueOriginByUrl = Object.create(null);

    // #537 — which of the URLs currently sitting in `serverUrls` got
    // there because the catalogue merged them in, as opposed to the
    // operator typing / adopting them. persistServerUrls (prologue.js)
    // filters these out so a provider row never freezes into
    // localStorage and outlives its removal upstream.
    //
    // Deliberately `var`, not `let`: prologue.js is concatenated long
    // BEFORE this fragment, and its persistServerUrls calls
    // isCatalogueUrl. A `let` would sit in the temporal dead zone until
    // this line executes, and a save fired from module scope during boot
    // would throw a ReferenceError that render() has no catch for. `var`
    // hoists to the shared IIFE as `undefined`, which isCatalogueUrl
    // treats as "nothing adopted yet" — the honest answer at that point.
    var catalogueAdoptedUrls = Object.create(null);

    // Browser filter state. Module-scope so a full re-render (workspace
    // switch, discovery finishing) doesn't reset what the operator typed.
    // `var` for the same TDZ reason as above — these are read by
    // renderers that live in earlier fragments.
    var catalogueSearchQuery = '';
    var catalogueTagFilter = null;

    // ---- Read-only accessors -----------------------------------------
    //
    // Every one of these is side-effect free: they are called straight
    // from the render path (render() has no try/catch, and a mutator
    // there would fight the caller that just set the state).

    function catalogueIsAvailable() {
        return !!(catalogueInfo && catalogueInfo.available);
    }

    function catalogueEntryCount() {
        return Array.isArray(catalogueEntries) ? catalogueEntries.length : 0;
    }

    function catalogueHasEntries() {
        return catalogueEntryCount() > 0;
    }

    function catalogueProviderLabel() {
        if (!catalogueInfo) return null;
        return catalogueInfo.providerName || catalogueInfo.providerId || null;
    }

    // 'editable' (default) | 'readonly' | 'hidden'. Mirrors
    // BowireCatalogueVisibility: readonly keeps the list but drops every
    // add affordance, hidden suppresses URL management entirely.
    function catalogueVisibility() {
        return (catalogueInfo && catalogueInfo.visibility) || 'editable';
    }

    // Provider ids the HOST actually has loaded, from /api/catalogue/info's
    // `providers` array (#537). Empty when the host predates that field —
    // callers must treat empty as "can't tell" and not grey anything out.
    function catalogueProviderIds() {
        if (!catalogueInfo || !Array.isArray(catalogueInfo.providers)) return [];
        return catalogueInfo.providers
            .map(function (p) { return p && p.id; })
            .filter(function (id) { return !!id; });
    }

    function catalogueOriginFor(url) {
        if (!url || !catalogueOriginByUrl) return null;
        return catalogueOriginByUrl[url] || null;
    }

    function isCatalogueUrl(url) {
        // Defends against its own binding: see the `var` note above.
        if (!url || !catalogueAdoptedUrls) return false;
        return !!catalogueAdoptedUrls[url];
    }

    // The URL Bowire actually discovers against. A catalogue entry that
    // declares protocols gets the first one as a `protocol@` hint —
    // without it an entry whose surface lives under a path (…/graphql,
    // …/openapi.json) is probed as a bare host and finds nothing. This
    // is the same composition scripts/ci/smoke-samples.mjs performs,
    // which is why CI kept passing while the UI stayed empty.
    function catalogueEntryUrl(entry) {
        if (!entry || !entry.url) return '';
        var raw = String(entry.url);
        // An entry may already carry its own hint ("grpcweb@http://…");
        // prefixing again yields "grpc@grpcweb@…", which no plugin claims.
        if (/^[a-z][a-z0-9]*@/i.test(raw)) return raw;
        var protos = Array.isArray(entry.protocols) ? entry.protocols : null;
        var first = (protos && protos.length > 0) ? String(protos[0] || '').trim() : '';
        return first ? (first + '@' + raw) : raw;
    }

    // Pure filter over the current snapshot. `query` matches name / URL /
    // tag as a case-insensitive substring; `tag` is exact membership.
    function catalogueFilterEntries(query, tag) {
        var all = Array.isArray(catalogueEntries) ? catalogueEntries : [];
        var q = String(query || '').trim().toLowerCase();
        var t = tag ? String(tag) : null;
        return all.filter(function (e) {
            if (!e || !e.url) return false;
            if (t) {
                if (!Array.isArray(e.tags) || e.tags.indexOf(t) < 0) return false;
            }
            if (!q) return true;
            var hay = [
                e.name || '',
                e.url || '',
                Array.isArray(e.tags) ? e.tags.join(' ') : '',
                Array.isArray(e.protocols) ? e.protocols.join(' ') : ''
            ].join(' ').toLowerCase();
            return hay.indexOf(q) >= 0;
        });
    }

    // Deduped, sorted union of every entry's tags.
    function catalogueAllTags() {
        var seen = Object.create(null);
        var out = [];
        var all = Array.isArray(catalogueEntries) ? catalogueEntries : [];
        for (var i = 0; i < all.length; i++) {
            var tags = all[i] && all[i].tags;
            if (!Array.isArray(tags)) continue;
            for (var j = 0; j < tags.length; j++) {
                var t = String(tags[j] || '').trim();
                if (!t || seen[t]) continue;
                seen[t] = true;
                out.push(t);
            }
        }
        return out.sort();
    }

    // Look an entry back up by its composed URL. Click handlers must use
    // this instead of closing over the entry object: morphdom preserves
    // the inline browser's rows across re-renders, and every refresh
    // replaces `catalogueEntries` wholesale, so a captured object is
    // stale the moment the catalogue moves.
    function catalogueEntryByUrl(composedUrl) {
        var all = Array.isArray(catalogueEntries) ? catalogueEntries : [];
        for (var i = 0; i < all.length; i++) {
            if (all[i] && catalogueEntryUrl(all[i]) === composedUrl) return all[i];
        }
        return null;
    }

    async function fetchCatalogueInfo() {
        try {
            const resp = await fetch(`${config.prefix}/api/catalogue/info`);
            if (!resp.ok) return null;
            return await resp.json();
        } catch (_) {
            return null;
        }
    }

    async function fetchCatalogueEntries() {
        try {
            const resp = await fetch(`${config.prefix}/api/catalogue/entries`);
            if (!resp.ok) return null;
            return await resp.json();
        } catch (_) {
            return null;
        }
    }

    async function refreshCatalogueEntries() {
        try {
            const resp = await fetch(`${config.prefix}/api/catalogue/refresh`, { method: 'POST' });
            if (!resp.ok) return null;
            return await resp.json();
        } catch (_) {
            return null;
        }
    }

    // Merge catalogue URLs into serverUrls + tag their origin. The
    // existing prologue.js loader has already merged config + local-
    // storage URLs; this runs after that and adds whatever the
    // catalogue provider returned. URLs already present (entered by
    // the user or shipped by config) keep their existing origin
    // marker — catalogue entries don't overwrite local choices.
    function applyCatalogueToServerUrls(payload) {
        if (!payload || !Array.isArray(payload.entries)) return 0;
        catalogueEntries = payload.entries;
        var added = 0;
        for (var i = 0; i < payload.entries.length; i++) {
            var entry = payload.entries[i];
            if (!entry || !entry.url) continue;
            // #537 — key everything by the COMPOSED url (protocol hint
            // folded in) so the origin chip, the "Added ✓" state and the
            // discovery fan-out all agree on one string.
            var composed = catalogueEntryUrl(entry);
            if (!composed) continue;
            if (catalogueOriginByUrl[composed]) continue;
            catalogueOriginByUrl[composed] = {
                providerId: payload.providerId || null,
                providerName: payload.providerName || null,
                name: entry.name || null,
                protocols: entry.protocols || null,
                tags: entry.tags || null,
                schema: entry.schema || null,
            };
            if (serverUrls.indexOf(composed) === -1) {
                serverUrls.push(composed);
                if (typeof connectionStatuses !== 'undefined') {
                    connectionStatuses[composed] = 'disconnected';
                }
                // Merged, not adopted — persistServerUrls skips it. A URL
                // that was ALREADY in serverUrls (hand-typed, or adopted
                // on an earlier visit) is deliberately not marked, so its
                // localStorage row survives.
                if (catalogueAdoptedUrls) catalogueAdoptedUrls[composed] = true;
                added++;
            }
        }
        return added;
    }

    // Boot-time loader called from init.js after the workbench
    // mounts. Fire-and-forget — failure to reach the catalogue
    // endpoint doesn't block the rest of the boot.
    async function initialCatalogueLoad() {
        var info = await fetchCatalogueInfo();
        if (!info || !info.available) {
            catalogueInfo = info || { available: false };
            return;
        }
        catalogueInfo = info;
        var payload = await fetchCatalogueEntries();
        if (payload) {
            applyCatalogueToServerUrls(payload);
            // Re-render so the sidebar picks up the new URLs.
            if (typeof render === 'function') render();
        }
        // #537 — deliberately does NOT kick discovery itself. init.js
        // sequences the first fetchServices() AFTER this promise settles,
        // so the merged URLs are in serverUrls before the one and only
        // boot fan-out. Firing a second fetchServices() from here instead
        // was measurably worse: two overlapping runs both write the
        // isLoadingServices flag and the workbench wedged on
        // "Discovering services…" forever.
        return catalogueEntryCount();
    }

    // ---- #537: the browsable catalogue surface ------------------------
    //
    // Everything below is the "pick a service" affordance the catalogue
    // never had. It has two mount sites — inline inside the Workspace →
    // Sources detail pane (lives INSIDE the morphdom tree) and a modal
    // dialog appended to document.body (outside it, like bowirePrompt).
    // The shared renderer means the two can't drift.

    // Mutator — only ever called from a click handler. Adopting an entry
    // promotes it from "merged by the provider" to a normal workspace URL:
    // it is cleared from catalogueAdoptedUrls so persistServerUrls writes
    // it, and it survives the entry disappearing upstream.
    function addCatalogueEntryToWorkspace(entryUrl) {
        if (!entryUrl) return false;
        var wasNew = false;
        if (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) {
            if (serverUrls.indexOf(entryUrl) < 0) {
                serverUrls.push(entryUrl);
                wasNew = true;
            }
        }
        if (typeof connectionStatuses !== 'undefined' && connectionStatuses
            && !connectionStatuses[entryUrl]) {
            connectionStatuses[entryUrl] = 'disconnected';
        }
        if (catalogueAdoptedUrls) catalogueAdoptedUrls[entryUrl] = false;
        if (typeof persistServerUrls === 'function') persistServerUrls();
        if (typeof toast === 'function') {
            toast(wasNew ? ('Added ' + entryUrl) : (entryUrl + ' is already in this workspace'),
                wasNew ? 'success' : 'info');
        }
        // Only probe when we actually introduced a new target — clicking
        // Add on an already-present row shouldn't re-fan-out discovery.
        if (wasNew && typeof fetchServices === 'function') fetchServices();
        if (typeof render === 'function') render();
        return wasNew;
    }

    // Mutator — refresh the snapshot from the provider and re-merge.
    async function refreshCatalogueNow() {
        var payload = await refreshCatalogueEntries();
        if (!payload) {
            if (typeof toast === 'function') {
                toast('Could not refresh the catalogue — the provider did not answer.', 'error');
            }
            return false;
        }
        // A refresh replaces the snapshot wholesale, so re-key the origin
        // map: rows that vanished upstream must stop claiming an origin.
        catalogueOriginByUrl = Object.create(null);
        var added = applyCatalogueToServerUrls(payload);
        if (typeof toast === 'function') {
            toast(catalogueEntryCount() + ' catalogue entr'
                + (catalogueEntryCount() === 1 ? 'y' : 'ies')
                + (added > 0 ? (' · ' + added + ' new') : ''), 'success');
        }
        if (typeof render === 'function') render();
        // Same reasoning as initialCatalogueLoad: a row the refresh just
        // introduced has to be probed or it reads as broken.
        if (added > 0 && typeof fetchServices === 'function') fetchServices();
        return true;
    }

    // Render the browsable list. `opts.inline` picks the pane-embedded
    // layout (no dialog chrome); `opts.onAdded(url)` lets the mount site
    // repaint itself after an adoption.
    function renderCatalogueBrowser(opts) {
        opts = opts || {};
        var inline = !!opts.inline;
        var readOnly = catalogueVisibility() === 'readonly';
        var wrap = el('div', {
            className: 'bowire-catalogue-browser' + (inline ? ' is-inline' : '')
        });

        var header = el('div', { className: 'bowire-catalogue-header' });
        header.appendChild(el('span', {
            className: 'bowire-catalogue-provider',
            textContent: catalogueProviderLabel() || 'Catalogue'
        }));
        header.appendChild(el('span', {
            className: 'bowire-catalogue-count',
            textContent: catalogueEntryCount() + ' entr' + (catalogueEntryCount() === 1 ? 'y' : 'ies')
        }));
        if (!readOnly && catalogueHasEntries()) {
            // Bulk adoption is deliberately a tertiary text button, not a
            // primary one: fanning discovery across a 200-service Consul
            // catalogue is a foot-gun, so anything sizeable asks first.
            header.appendChild(el('button', {
                type: 'button',
                className: 'bowire-catalogue-addall',
                textContent: 'Add all',
                title: 'Add every catalogue entry to this workspace',
                onClick: function () {
                    var pending = catalogueEntries.filter(function (e) {
                        var u = catalogueEntryUrl(e);
                        return u && (typeof serverUrls === 'undefined' || serverUrls.indexOf(u) < 0);
                    });
                    if (pending.length === 0) {
                        if (typeof toast === 'function') toast('Every entry is already in this workspace', 'info');
                        return;
                    }
                    var run = function () {
                        pending.forEach(function (e) {
                            var u = catalogueEntryUrl(e);
                            if (!u) return;
                            if (serverUrls.indexOf(u) < 0) serverUrls.push(u);
                            if (typeof connectionStatuses !== 'undefined' && connectionStatuses) {
                                connectionStatuses[u] = 'disconnected';
                            }
                            if (catalogueAdoptedUrls) catalogueAdoptedUrls[u] = false;
                        });
                        if (typeof persistServerUrls === 'function') persistServerUrls();
                        if (typeof toast === 'function') toast('Added ' + pending.length + ' URLs', 'success');
                        if (typeof fetchServices === 'function') fetchServices();
                        if (typeof opts.onAdded === 'function') opts.onAdded(null);
                        if (typeof render === 'function') render();
                    };
                    if (pending.length > 25 && typeof bowireConfirm === 'function') {
                        bowireConfirm(
                            'Add all ' + pending.length + ' catalogue entries to this workspace? '
                            + 'Discovery probes every one of them.',
                            run,
                            { title: 'Add all entries', confirmText: 'Add ' + pending.length }
                        );
                    } else {
                        run();
                    }
                }
            }));
        }
        if (catalogueIsAvailable()) {
            var refreshBtn = el('button', {
                type: 'button',
                className: 'bowire-catalogue-refresh',
                title: 'Re-fetch the catalogue from the provider',
                'aria-label': 'Refresh catalogue',
                onClick: function () { refreshCatalogueNow(); }
            });
            if (typeof svgIcon === 'function') refreshBtn.innerHTML = svgIcon('replay');
            else refreshBtn.textContent = '↻';
            header.appendChild(refreshBtn);
        }
        wrap.appendChild(header);

        // The list + tag bar are repainted locally on filter changes so
        // typing in the search box never triggers a full render() — that
        // would fight the operator's cursor and re-run every rail.
        var tagbar = el('div', { className: 'bowire-catalogue-tagbar' });
        var list = el('div', { className: 'bowire-catalogue-list' });

        var search = el('input', {
            type: 'search',
            className: 'bowire-catalogue-search',
            placeholder: 'Filter by name, URL, protocol or tag…',
            'aria-label': 'Filter catalogue entries',
            value: catalogueSearchQuery || '',
            // Meta-UI, same as bowirePrompt's input: opt out of the
            // vars-chip overlay + the {{var}} autocomplete.
            'data-bowire-no-vars-chip': '1',
            'data-bowire-no-vars-ac': '1'
        });
        search.oninput = function () {
            catalogueSearchQuery = search.value || '';
            paintList();
        };
        wrap.appendChild(search);
        wrap.appendChild(tagbar);
        wrap.appendChild(list);

        function paintTags() {
            tagbar.textContent = '';
            var tags = catalogueAllTags();
            if (tags.length === 0) {
                tagbar.style.display = 'none';
                return;
            }
            tagbar.style.display = '';
            tags.forEach(function (t) {
                tagbar.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-catalogue-tag' + (catalogueTagFilter === t ? ' is-active' : ''),
                    textContent: t,
                    // The tag travels on the node, not in the closure. In
                    // the inline mount these chips sit inside the morphdom
                    // tree and are keyless, so a refresh that changes the
                    // tag set recycles a preserved chip onto a different
                    // label — a closed-over `t` would then filter by the
                    // tag the chip USED to show.
                    dataset: { catalogueTag: t },
                    onClick: function (e) {
                        var tag = (e && e.currentTarget && e.currentTarget.dataset)
                            ? e.currentTarget.dataset.catalogueTag
                            : t;
                        catalogueTagFilter = (catalogueTagFilter === tag) ? null : tag;
                        paintTags();
                        paintList();
                    }
                }));
            });
        }

        function paintList() {
            list.textContent = '';
            var matches = catalogueFilterEntries(catalogueSearchQuery, catalogueTagFilter);
            if (matches.length === 0) {
                list.appendChild(el('p', {
                    className: 'bowire-catalogue-empty',
                    textContent: catalogueHasEntries()
                        ? 'No entry matches this filter.'
                        : (catalogueProviderLabel() || 'The catalogue') + ' returned no entries.'
                }));
                return;
            }
            matches.forEach(function (entry) {
                list.appendChild(renderCatalogueRow(entry, readOnly, opts, paintList));
            });
        }

        paintTags();
        paintList();
        return wrap;
    }

    // One catalogue row. Split out so both mount sites and the repaint
    // path build an identical node.
    function renderCatalogueRow(entry, readOnly, opts, repaint) {
        // Capture the composed URL STRING only. The entry object itself
        // must never be closed over — see catalogueEntryByUrl.
        var entryUrl = catalogueEntryUrl(entry);
        var already = (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls))
            && serverUrls.indexOf(entryUrl) >= 0;

        var row = el('div', {
            className: 'bowire-catalogue-row',
            'data-bowire-catalogue-url': entryUrl
        });

        var protoWrap = el('span', { className: 'bowire-catalogue-row-protos' });
        var protos = Array.isArray(entry.protocols) ? entry.protocols.slice(0, 3) : [];
        protos.forEach(function (p) {
            var glyph = el('span', { className: 'bowire-catalogue-row-proto', title: String(p) });
            if (typeof svgIcon === 'function' && typeof _protoIconName === 'function') {
                glyph.innerHTML = svgIcon(_protoIconName(p));
            } else {
                glyph.textContent = String(p).slice(0, 2);
            }
            protoWrap.appendChild(glyph);
        });
        row.appendChild(protoWrap);

        var text = el('div', { className: 'bowire-catalogue-row-text' });
        text.appendChild(el('div', {
            className: 'bowire-catalogue-row-name',
            textContent: entry.name || entryUrl,
            title: entry.name || entryUrl
        }));
        text.appendChild(el('div', {
            className: 'bowire-catalogue-row-url',
            textContent: entryUrl,
            title: entryUrl
        }));
        if (Array.isArray(entry.tags) && entry.tags.length > 0) {
            var tagRow = el('div', { className: 'bowire-catalogue-row-meta' });
            entry.tags.slice(0, 4).forEach(function (t) {
                tagRow.appendChild(el('span', { className: 'bowire-catalogue-row-tag', textContent: String(t) }));
            });
            text.appendChild(tagRow);
        }
        row.appendChild(text);

        row.appendChild(el('button', {
            type: 'button',
            className: 'bowire-catalogue-row-add' + (already ? ' is-added' : ''),
            textContent: already ? 'Added' : 'Add',
            // undefined, not false: el() writes attrs via setAttribute and
            // the DOM disables on the attribute's mere PRESENCE, so
            // `disabled="false"` would grey out every Add button.
            disabled: (already || readOnly) ? 'disabled' : undefined,
            title: readOnly
                ? 'This catalogue is read-only — URL management is disabled by the host.'
                : (already ? 'Already in this workspace' : 'Add ' + entryUrl + ' to this workspace'),
            onClick: function (e) {
                // Re-resolve at CLICK time from the DOM, not from the
                // render-time closure. Both halves matter: morphdom keeps
                // this button alive across renders AND the rows are
                // keyless, so a refresh that reorders or shortens the list
                // recycles this node onto a DIFFERENT entry. The captured
                // `entryUrl` would then add the previous row's URL. The
                // row's data-bowire-catalogue-url attribute is the one
                // thing morphdom patches to match the node it kept.
                var rowEl = e && e.currentTarget && e.currentTarget.closest
                    ? e.currentTarget.closest('.bowire-catalogue-row')
                    : null;
                var liveUrl = rowEl ? rowEl.getAttribute('data-bowire-catalogue-url') : entryUrl;
                var live = catalogueEntryByUrl(liveUrl);
                if (!live) {
                    if (typeof toast === 'function') toast('That entry is no longer in the catalogue', 'error');
                    if (typeof repaint === 'function') repaint();
                    return;
                }
                addCatalogueEntryToWorkspace(catalogueEntryUrl(live));
                if (typeof opts.onAdded === 'function') opts.onAdded(liveUrl);
                else if (typeof repaint === 'function') repaint();
            }
        }));
        return row;
    }

    // Modal variant. Appended to document.body — the same out-of-tree
    // pattern bowirePrompt uses — so morphdom never walks it and the
    // search input keeps its focus + value across the render() that
    // adopting an entry triggers.
    function openCatalogueBrowserDialog(opts) {
        opts = opts || {};
        if (typeof document === 'undefined' || !document.body) return null;
        var existing = document.querySelector('.bowire-catalogue-overlay');
        if (existing) existing.remove();

        var overlay = null;
        function close() {
            document.removeEventListener('keydown', onKey, true);
            if (overlay && overlay.parentNode) overlay.remove();
        }
        function onKey(e) {
            if (e.key === 'Escape') { e.preventDefault(); close(); }
        }

        var mount = el('div', { className: 'bowire-catalogue-mount' });
        function paint() {
            mount.textContent = '';
            mount.appendChild(renderCatalogueBrowser({
                inline: false,
                onAdded: function () { paint(); }
            }));
        }
        paint();

        var title = opts.workspace && opts.workspace.name
            ? 'Add to ' + opts.workspace.name
            : 'Browse catalogue';

        var footer = el('div', { className: 'bowire-catalogue-footer' });
        if (typeof opts.onManual === 'function' && catalogueVisibility() === 'editable') {
            footer.appendChild(el('button', {
                type: 'button',
                className: 'bowire-catalogue-footer-link',
                textContent: 'Enter a URL manually…',
                onClick: function () { close(); opts.onManual(); }
            }));
        }
        footer.appendChild(el('button', {
            type: 'button',
            className: 'bowire-confirm-btn cancel',
            textContent: 'Close',
            onClick: close
        }));

        var dialog = el('div', {
            className: 'bowire-confirm-dialog bowire-catalogue-dialog',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': title
        },
            el('div', { className: 'bowire-confirm-title', textContent: title }),
            mount,
            footer
        );

        // NOT `.bowire-confirm-overlay`: bowireConfirm / bowirePrompt both
        // start by removing any element carrying that class, so sharing it
        // would make the "Add all" confirmation delete the dialog that
        // raised it. Own class, own styles (bowire.css mirrors the confirm
        // overlay's geometry).
        overlay = el('div', {
            className: 'bowire-catalogue-overlay',
            onClick: function (e) { if (e.target === overlay) close(); }
        }, dialog);
        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKey, true);
        setTimeout(function () {
            var input = overlay.querySelector('.bowire-catalogue-search');
            if (input) input.focus();
        }, 0);
        return overlay;
    }
