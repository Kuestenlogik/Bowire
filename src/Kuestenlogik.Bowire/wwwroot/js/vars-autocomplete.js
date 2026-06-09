    // #125 Phase 2 — vars autocomplete dropdown.
    //
    // Listens to `input` events on every text-bearing field (input,
    // textarea, contenteditable) and opens a floating popover when the
    // operator types `{{`. Suggestions cover the same source vocabulary
    // the resolver knows about: env / prev / runtime / step / secret /
    // ai prefixes, plus every concrete env-var name and runtime built-
    // in. Filters as the operator keeps typing. Tab / Enter inserts,
    // arrow keys navigate, Esc closes.
    //
    // Trade-off vs the chip-rendering shape in the issue body: this
    // ships the autocomplete leg of Phase 2 without forcing every
    // input to become contenteditable. Chip-rendering remains a
    // follow-up; the dropdown is the higher-value half because it
    // tells the operator what's *available* up front.

    var _varsACPopover = null;

    function installVarsAutocomplete() {
        if (window.__bowireVarsACInstalled) return;
        window.__bowireVarsACInstalled = true;

        document.addEventListener('input', onVarsACInput, true);
        document.addEventListener('keydown', onVarsACKeydown, true);
        document.addEventListener('mousedown', onVarsACMousedown, true);
        document.addEventListener('blur', onVarsACBlur, true);
        window.addEventListener('scroll', closeVarsAC, true);
        window.addEventListener('resize', closeVarsAC, true);
    }

    function onVarsACInput(e) {
        var target = e.target;
        if (!isVarsACEligibleTarget(target)) return;
        var caret = getTargetCaret(target);
        var value = getTargetValue(target);
        // Look back from the caret for an unclosed '{{'. The popover
        // activates the moment two opening braces precede the caret
        // without a closing pair in between.
        var openIdx = findOpenCurlyBefore(value, caret);
        if (openIdx < 0) {
            closeVarsAC();
            return;
        }
        var query = value.substring(openIdx + 2, caret);
        var suggestions = buildVarsACSuggestions(query);
        if (suggestions.length === 0) {
            closeVarsAC();
            return;
        }
        varsACState = {
            target: target,
            openIdx: openIdx,
            caret: caret,
            query: query,
            suggestions: suggestions,
            selectedIdx: 0,
        };
        showVarsACPopover();
    }

    function onVarsACKeydown(e) {
        if (!varsACState) return;
        // Esc closes. Arrow up/down navigate. Tab/Enter insert.
        if (e.key === 'Escape') {
            e.preventDefault();
            closeVarsAC();
            return;
        }
        if (e.key === 'ArrowDown') {
            e.preventDefault();
            varsACState.selectedIdx = (varsACState.selectedIdx + 1) % varsACState.suggestions.length;
            showVarsACPopover();
            return;
        }
        if (e.key === 'ArrowUp') {
            e.preventDefault();
            varsACState.selectedIdx = (varsACState.selectedIdx - 1 + varsACState.suggestions.length) % varsACState.suggestions.length;
            showVarsACPopover();
            return;
        }
        if (e.key === 'Tab' || e.key === 'Enter') {
            e.preventDefault();
            insertVarsACSelection();
            return;
        }
    }

    function onVarsACMousedown(e) {
        if (!varsACState) return;
        if (_varsACPopover && _varsACPopover.contains(e.target)) return;
        // Click outside the popover closes it; click on the original
        // target (typing more) is fine and the next input event will
        // re-open with the new query.
        if (e.target !== varsACState.target) {
            closeVarsAC();
        }
    }

    function onVarsACBlur(e) {
        if (!varsACState) return;
        // setTimeout so a click inside the popover lands before we
        // tear the state down.
        setTimeout(function () {
            if (!varsACState) return;
            var ae = document.activeElement;
            if (ae !== varsACState.target && (!_varsACPopover || !_varsACPopover.contains(ae))) {
                closeVarsAC();
            }
        }, 0);
    }

    function isVarsACEligibleTarget(t) {
        if (!t) return false;
        var tag = t.tagName;
        if (tag === 'TEXTAREA') return true;
        if (tag === 'INPUT') {
            var type = (t.type || 'text').toLowerCase();
            return type === 'text' || type === 'search' || type === 'url' || type === 'email' || type === '';
        }
        // contenteditable not handled by the dropdown (selection
        // tracking is more complex) — chip-rendering shape will
        // pick that up.
        return false;
    }

    function getTargetCaret(t) {
        return typeof t.selectionStart === 'number' ? t.selectionStart : (t.value || '').length;
    }
    function getTargetValue(t) {
        return t.value != null ? String(t.value) : '';
    }

    function findOpenCurlyBefore(value, caret) {
        // Walk back from caret-2 for '{{' that hasn't been closed by
        // '}}' yet. We stop early on a newline or '}' to avoid the
        // popover triggering on already-closed references on the
        // same line.
        for (var i = caret - 2; i >= 0; i--) {
            var two = value.charAt(i) + value.charAt(i + 1);
            if (two === '}}') return -1;
            if (two === '{{') return i;
            // Allow word chars / dots / digits in the query body —
            // anything else means we crossed out of an open ref.
            var c = value.charAt(i + 1);
            if (c === '\n' || c === '\r') return -1;
        }
        return -1;
    }

    function buildVarsACSuggestions(query) {
        var q = (query || '').toLowerCase();
        var out = [];
        // Source prefixes — typed when query is empty or matches the
        // prefix prefix.
        var sources = [
            { label: 'env.', kind: 'source', desc: 'Environment variable', insert: 'env.' },
            { label: 'prev.', kind: 'source', desc: 'Previous response field', insert: 'prev.' },
            { label: 'runtime.', kind: 'source', desc: 'Runtime built-in (now, uuid, …)', insert: 'runtime.' },
            { label: 'step1.', kind: 'source', desc: 'Recording step N response', insert: 'step1.' },
            { label: 'secret.', kind: 'source', desc: 'OS keyring (Phase 2 — masked today)', insert: 'secret.' },
            { label: 'ai.', kind: 'source', desc: 'AI-suggested value (Phase 2 stub)', insert: 'ai.' },
        ];
        sources.forEach(function (s) {
            if (q === '' || s.label.toLowerCase().indexOf(q) === 0) {
                out.push(s);
            }
        });

        // Runtime built-ins under the `runtime.` namespace.
        var runtimes = ['now', 'nowMs', 'timestamp', 'uuid', 'random'];
        runtimes.forEach(function (r) {
            var full = 'runtime.' + r;
            if (q === '' || full.toLowerCase().indexOf(q) !== -1) {
                out.push({ label: full, kind: 'runtime', desc: 'Runtime ' + r, insert: full });
            }
        });

        // Env vars from the merged set.
        try {
            if (typeof getMergedVars === 'function') {
                var merged = getMergedVars();
                Object.keys(merged).sort().forEach(function (k) {
                    if (q === '' || k.toLowerCase().indexOf(q) !== -1 || ('env.' + k).toLowerCase().indexOf(q) === 0) {
                        var preview = String(merged[k]);
                        if (preview.length > 40) preview = preview.substring(0, 40) + '…';
                        out.push({ label: k, kind: 'env', desc: preview, insert: k });
                    }
                });
            }
        } catch { /* getMergedVars may not exist yet at very early bootstrap */ }

        // Recording-step hint — if a recording is selected, surface
        // the step1.../step2... shortcuts ranked above the generic
        // step1. prefix.
        try {
            if (typeof recordingsList !== 'undefined' && recordingManagerSelectedId) {
                var rec = recordingsList.find(function (r) { return r.id === recordingManagerSelectedId; });
                if (rec && rec.steps && rec.steps.length > 0) {
                    var max = Math.min(rec.steps.length, 5);
                    for (var i = 1; i <= max; i++) {
                        var label = 'step' + i + '.';
                        var stepDesc = (rec.steps[i - 1].method || rec.steps[i - 1].service || 'step ' + i);
                        if (q === '' || label.toLowerCase().indexOf(q) === 0) {
                            out.push({ label: label, kind: 'step', desc: stepDesc, insert: label });
                        }
                    }
                }
            }
        } catch { /* recordingsList may not exist on bare load */ }

        // Cap result count so the popover stays readable.
        return out.slice(0, 30);
    }

    function showVarsACPopover() {
        if (!varsACState) return;
        if (!_varsACPopover) {
            _varsACPopover = document.createElement('div');
            _varsACPopover.className = 'bowire-vars-ac-popover';
            document.body.appendChild(_varsACPopover);
        }
        _varsACPopover.innerHTML = '';
        varsACState.suggestions.forEach(function (s, idx) {
            var row = document.createElement('div');
            row.className = 'bowire-vars-ac-row bowire-vars-ac-row-' + s.kind
                + (idx === varsACState.selectedIdx ? ' selected' : '');
            row.addEventListener('mousedown', function (e) {
                // mousedown (not click) so the input doesn't blur
                // before we read the selection.
                e.preventDefault();
                varsACState.selectedIdx = idx;
                insertVarsACSelection();
            });
            row.addEventListener('mouseenter', function () {
                if (!varsACState) return;
                varsACState.selectedIdx = idx;
                Array.prototype.forEach.call(_varsACPopover.children, function (c, ci) {
                    if (ci === idx) c.classList.add('selected');
                    else c.classList.remove('selected');
                });
            });
            var label = document.createElement('span');
            label.className = 'bowire-vars-ac-row-label';
            label.textContent = s.label;
            var kindBadge = document.createElement('span');
            kindBadge.className = 'bowire-vars-ac-row-kind';
            kindBadge.textContent = s.kind;
            var desc = document.createElement('span');
            desc.className = 'bowire-vars-ac-row-desc';
            desc.textContent = s.desc || '';
            row.appendChild(kindBadge);
            row.appendChild(label);
            row.appendChild(desc);
            _varsACPopover.appendChild(row);
        });
        // Position below-left of the target. Crude but functional;
        // refining to caret-tracking is a follow-up.
        var rect = varsACState.target.getBoundingClientRect();
        _varsACPopover.style.left = Math.max(8, rect.left) + 'px';
        _varsACPopover.style.top = (rect.bottom + 2) + 'px';
        _varsACPopover.style.display = 'block';
    }

    function insertVarsACSelection() {
        if (!varsACState) return;
        var s = varsACState.suggestions[varsACState.selectedIdx];
        if (!s) { closeVarsAC(); return; }
        var t = varsACState.target;
        var value = getTargetValue(t);
        var before = value.substring(0, varsACState.openIdx);
        var afterCaret = value.substring(varsACState.caret);
        // If the suggestion is a source-prefix (ends with '.'), keep
        // the popover open after insertion so the operator can keep
        // typing the field name. If it's a concrete value, close the
        // ref with }}.
        var insert;
        var newCaret;
        var isPrefix = s.kind === 'source';
        if (isPrefix) {
            insert = '{{' + s.insert;
            newCaret = before.length + insert.length;
        } else {
            insert = '{{' + s.insert + '}}';
            newCaret = before.length + insert.length;
        }
        t.value = before + insert + afterCaret;
        t.setSelectionRange(newCaret, newCaret);
        // Fire input so listeners (incl. this autocomplete) update.
        t.dispatchEvent(new Event('input', { bubbles: true }));
        if (isPrefix) {
            // Re-trigger the dropdown so the operator sees the
            // matches for the just-inserted prefix.
            onVarsACInput({ target: t });
        } else {
            closeVarsAC();
        }
    }

    function closeVarsAC() {
        if (_varsACPopover) _varsACPopover.style.display = 'none';
        varsACState = null;
    }

    window.__bowireVarsAutocomplete = {
        install: installVarsAutocomplete,
        close: closeVarsAC,
    };
