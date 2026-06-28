    // ---- #154 Phase 3 — Help drawer + F1 + per-rail topic resolution ----
    //
    // Renders the in-app help on the renderDrawer primitive shipped in
    // #115. Two-pane layout: topic tree on the left (grouped by
    // category), search box at the top, rendered markdown on the
    // right. F1 anywhere opens the drawer; the topic that opens
    // depends on what the operator was looking at (per-rail mapping
    // below).
    //
    // The drawer is gated on helpAvailable (Phase 1 capability probe).
    // When the package isn't installed, F1 does nothing and the
    // topbar overflow shows the disabled hint that already exists.

    // Rail-mode → topic-id mapping. Each rail mode points at the
    // topic that's most likely to help an operator currently in that
    // surface. Missing entries fall back to 'index'.
    var _helpRailTopicMap = {
        home:         'index',
        sources:      'ui-guide/sidebar',
        discover:     'features/auto-discovery',
        compose:      'features/auto-discovery',
        collections:  'features/collections',
        environments: 'features/environments',
        recordings:   'features/recording',
        mocks:        'features/mock-server',
        flows:        'features/flows',
        proxy:        'features/proxy',
        benchmarks:   'features/performance',
        security:     'features/scan',
        workspaces:   'features/workspace'
    };

    function helpResolveContextualTopicId() {
        if (typeof railMode !== 'string' || !railMode) return 'index';
        return _helpRailTopicMap[railMode] || 'index';
    }

    function helpEnsureTopicsLoaded() {
        if (helpTopicsLoaded) return Promise.resolve();
        return fetch(config.prefix + '/api/help/topics')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                helpTopics = (data && Array.isArray(data.topics)) ? data.topics : [];
                helpTopicsLoaded = true;
            })
            .catch(function () { /* leave empty; UI handles 0-topics */ });
    }

    function helpLoadTopic(id) {
        if (!id) return Promise.resolve();
        return fetch(config.prefix + '/api/help/topic/' + encodeURIComponent(id))
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && data.id) {
                    helpSelectedTopic = data;
                    helpSelectedId = data.id;
                } else {
                    helpSelectedTopic = null;
                }
                render();
            })
            .catch(function () { /* leave whatever was loaded */ });
    }

    function helpOpenDrawer(targetId) {
        if (!helpAvailable) return;
        helpDrawerOpen = true;
        try { localStorage.setItem('bowire_help_drawer_open', '1'); } catch { /* ignore */ }
        // #299 — opening Help makes it the active tab in the unified
        // right-side drawer.
        rightDrawerActiveTab = 'help';
        try { localStorage.setItem('bowire_right_drawer_active_tab', 'help'); } catch { /* ignore */ }
        helpEnsureTopicsLoaded().then(function () {
            var wanted = targetId || helpSelectedId || helpResolveContextualTopicId();
            if (wanted !== helpSelectedId || !helpSelectedTopic) {
                helpLoadTopic(wanted);
            } else {
                render();
            }
        });
    }

    function helpCloseDrawer() {
        helpDrawerOpen = false;
        try { localStorage.setItem('bowire_help_drawer_open', '0'); } catch { /* ignore */ }
        render();
    }

    // Debounced search. The search endpoint is fast (in-memory inverted
    // index over ~40 topics) but coalescing keystrokes is still cheaper
    // than a fetch per character.
    var _helpSearchTimer = null;
    function helpRunSearch(q) {
        helpSearchQuery = q;
        if (_helpSearchTimer) clearTimeout(_helpSearchTimer);
        if (!q || !q.trim()) {
            helpSearchHits = [];
            render();
            return;
        }
        _helpSearchTimer = setTimeout(function () {
            fetch(config.prefix + '/api/help/search?q=' + encodeURIComponent(q.trim()))
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    helpSearchHits = (data && Array.isArray(data.hits)) ? data.hits : [];
                    render();
                })
                .catch(function () { helpSearchHits = []; render(); });
        }, 150);
    }

    // ---- Mini-markdown renderer ----
    //
    // The Help package ships raw markdown to keep itself small (no
    // server-side HTML pipeline). The workbench renders to HTML here.
    // Scope is deliberately tight — what the embedded docs/ actually
    // use: headings, paragraphs, lists, blockquotes, code blocks,
    // inline code, bold, italic, links, hr. Tables and images are
    // skipped (the embedded subset doesn't lean on them for content,
    // and a more complete renderer would mean vendoring 50 KB of
    // marked.js for marginal value).
    //
    // The output is a single string of HTML — innerHTML'd into the
    // content pane. Source is the trusted embedded markdown (our own
    // docs, embedded as manifest resources in the Help package), so
    // XSS is not the threat model — but we still escape HTML entities
    // inside text + code so a stray `<` in the source doesn't break
    // the rendered tree.
    function _helpEscapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function _helpRenderInline(text) {
        var s = _helpEscapeHtml(text);
        // Inline code first so its content isn't re-interpreted.
        s = s.replace(/`([^`\n]+)`/g, function (_, c) {
            return '<code class="bowire-help-md-code-inline">' + c + '</code>';
        });
        // Links [text](url) — URL is also escaped so a stray quote
        // doesn't break the href attribute. Only http(s) URLs land
        // as anchors; other schemes render as plain text.
        s = s.replace(/\[([^\]]+)\]\(([^)]+)\)/g, function (_, label, url) {
            var safeUrl = url.trim();
            if (!/^https?:\/\//i.test(safeUrl)) return label;
            return '<a href="' + safeUrl.replace(/"/g, '&quot;')
                + '" target="_blank" rel="noopener">' + label + '</a>';
        });
        // Bold + italic. Order matters: ** before _.
        s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        s = s.replace(/(^|[^_])_([^_]+)_/g, '$1<em>$2</em>');
        return s;
    }

    function helpRenderMarkdown(md) {
        if (!md) return '';
        var lines = String(md).split('\n');
        var out = [];
        var i = 0;
        while (i < lines.length) {
            var line = lines[i];

            // Fenced code block
            var fenceMatch = /^```(\w+)?\s*$/.exec(line);
            if (fenceMatch) {
                var lang = fenceMatch[1] || '';
                var codeLines = [];
                i++;
                while (i < lines.length && !/^```\s*$/.test(lines[i])) {
                    codeLines.push(lines[i]);
                    i++;
                }
                i++; // skip closing fence
                out.push('<pre class="bowire-help-md-pre" data-lang="' + _helpEscapeHtml(lang)
                    + '"><code>' + _helpEscapeHtml(codeLines.join('\n')) + '</code></pre>');
                continue;
            }

            // Heading (atx-style # … ######)
            var headingMatch = /^(#{1,6})\s+(.+?)\s*#*\s*$/.exec(line);
            if (headingMatch) {
                var level = headingMatch[1].length;
                out.push('<h' + level + ' class="bowire-help-md-h' + level + '">'
                    + _helpRenderInline(headingMatch[2]) + '</h' + level + '>');
                i++;
                continue;
            }

            // Horizontal rule
            if (/^---\s*$/.test(line)) {
                out.push('<hr class="bowire-help-md-hr">');
                i++;
                continue;
            }

            // Blockquote
            if (/^>\s?/.test(line)) {
                var quoteLines = [];
                while (i < lines.length && /^>\s?/.test(lines[i])) {
                    quoteLines.push(lines[i].replace(/^>\s?/, ''));
                    i++;
                }
                out.push('<blockquote class="bowire-help-md-quote">'
                    + _helpRenderInline(quoteLines.join(' ')) + '</blockquote>');
                continue;
            }

            // Unordered list
            if (/^[-*]\s+/.test(line)) {
                var ulItems = [];
                while (i < lines.length && /^[-*]\s+/.test(lines[i])) {
                    ulItems.push('<li>' + _helpRenderInline(lines[i].replace(/^[-*]\s+/, '')) + '</li>');
                    i++;
                }
                out.push('<ul class="bowire-help-md-ul">' + ulItems.join('') + '</ul>');
                continue;
            }

            // Ordered list
            if (/^\d+\.\s+/.test(line)) {
                var olItems = [];
                while (i < lines.length && /^\d+\.\s+/.test(lines[i])) {
                    olItems.push('<li>' + _helpRenderInline(lines[i].replace(/^\d+\.\s+/, '')) + '</li>');
                    i++;
                }
                out.push('<ol class="bowire-help-md-ol">' + olItems.join('') + '</ol>');
                continue;
            }

            // Blank line — paragraph separator
            if (line.trim() === '') {
                i++;
                continue;
            }

            // Paragraph (collect until blank line / structural line)
            var paraLines = [];
            while (i < lines.length && lines[i].trim() !== ''
                && !/^(#{1,6}\s+|>\s|[-*]\s+|\d+\.\s+|---\s*$|```)/.test(lines[i])) {
                paraLines.push(lines[i]);
                i++;
            }
            out.push('<p class="bowire-help-md-p">'
                + _helpRenderInline(paraLines.join(' ')) + '</p>');
        }
        return out.join('\n');
    }

    function _renderHelpDrawerContent() {
        var wrap = el('div', { className: 'bowire-help-body' });

        // Left column — search + topic list.
        var nav = el('div', { className: 'bowire-help-nav' });
        nav.appendChild(el('input', {
            type: 'search',
            className: 'bowire-help-search',
            placeholder: 'Search topics…',
            value: helpSearchQuery,
            'data-bowire-no-vars-chip': '1',
            'data-bowire-no-vars-ac': '1',
            onInput: function (e) { helpRunSearch(e.target.value); }
        }));

        if (helpSearchQuery && helpSearchQuery.trim().length > 0) {
            // Search-results view.
            if (!helpSearchHits || helpSearchHits.length === 0) {
                nav.appendChild(el('div', {
                    className: 'bowire-help-empty',
                    textContent: 'No matches'
                }));
            } else {
                var hitList = el('div', { className: 'bowire-help-hit-list' });
                helpSearchHits.forEach(function (h) {
                    var hitRow = el('button', {
                        className: 'bowire-help-hit'
                            + (h.id === helpSelectedId ? ' selected' : ''),
                        onClick: function () { helpLoadTopic(h.id); }
                    },
                        el('span', { className: 'bowire-help-hit-title', textContent: h.title }),
                        el('span', { className: 'bowire-help-hit-excerpt', textContent: h.excerpt || '' })
                    );
                    hitList.appendChild(hitRow);
                });
                nav.appendChild(hitList);
            }
        } else {
            // Default tree view — grouped by category. Each row shows
            // the short title (front-matter `title:` / first H1 / file
            // stem) and an optional one-line excerpt under it sourced
            // from front-matter `summary:` — same shape as the search
            // hit list (.bowire-help-hit-*) so the operator gets keyword
            // + context without scrolling. Excerpt collapses when the
            // topic has no summary.
            var grouped = _helpGroupTopics(helpTopics);
            grouped.forEach(function (group) {
                if (group.category) {
                    nav.appendChild(el('div', {
                        className: 'bowire-help-cat-header',
                        textContent: group.category
                    }));
                }
                group.topics.forEach(function (t) {
                    var row = el('button', {
                        className: 'bowire-help-topic-row'
                            + (t.id === helpSelectedId ? ' selected' : ''),
                        onClick: function () { helpLoadTopic(t.id); }
                    });
                    row.appendChild(el('span', {
                        className: 'bowire-help-topic-row-title',
                        textContent: t.title
                    }));
                    if (t.summary) {
                        row.appendChild(el('span', {
                            className: 'bowire-help-topic-row-excerpt',
                            textContent: t.summary
                        }));
                    }
                    nav.appendChild(row);
                });
            });
        }
        wrap.appendChild(nav);

        // Right column — rendered topic. Server-side Markdig pipeline
        // emits sanitised HTML in `bodyHtml` so the workbench can
        // innerHTML it directly. Old clients (or providers that
        // haven't rolled the new shape) fall back to the mini-renderer
        // over `markdown`. The mini-renderer escapes HTML entities,
        // which is wrong for DocFX-shaped topics carrying intentional
        // markup (picture, svg, dl) — the server path is the working
        // one going forward.
        var content = el('div', { className: 'bowire-help-content' });
        if (helpSelectedTopic) {
            if (helpSelectedTopic.bodyHtml) {
                content.innerHTML = helpSelectedTopic.bodyHtml;
            } else {
                content.innerHTML = helpRenderMarkdown(helpSelectedTopic.markdown);
            }
        } else if (!helpTopicsLoaded) {
            content.appendChild(el('div', {
                className: 'bowire-help-empty',
                textContent: 'Loading…'
            }));
        } else {
            content.appendChild(el('div', {
                className: 'bowire-help-empty',
                textContent: 'Pick a topic on the left.'
            }));
        }
        wrap.appendChild(content);

        return wrap;
    }

    function _helpGroupTopics(topics) {
        // Topic list comes pre-sorted (provider orders by category +
        // title) — group runs of same-category entries. Root-level
        // topics (categoryId=null) render first with no header.
        if (!topics || topics.length === 0) return [];
        var groups = [];
        var current = null;
        for (var i = 0; i < topics.length; i++) {
            var t = topics[i];
            var cat = t.categoryId || null;
            if (!current || current.category !== cat) {
                current = { category: cat, topics: [] };
                groups.push(current);
            }
            current.topics.push(t);
        }
        return groups;
    }
