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
    var _varsRefPopover = null;   // resolved-value preview when caret is INSIDE a closed {{ref}}
    var _varsACMirror = null;     // hidden div used to measure caret pixel coords

    // #125 Phase 2 v3 — measure where the caret sits in pixel
    // coordinates inside an <input> or <textarea>. Classic
    // mirror-div technique: clone the field's typography into a
    // hidden div, copy the text up to the caret, insert a marker
    // span, read its rect. The marker is zero-width so it doesn't
    // shift the layout it's about to measure.
    function getCaretCoords(field) {
        if (!field) return null;
        if (!_varsACMirror) {
            _varsACMirror = document.createElement('div');
            _varsACMirror.className = 'bowire-vars-ac-mirror';
            _varsACMirror.setAttribute('aria-hidden', 'true');
            document.body.appendChild(_varsACMirror);
        }
        var cs = window.getComputedStyle(field);
        var props = ['font', 'fontFamily', 'fontSize', 'fontWeight', 'fontStyle',
            'letterSpacing', 'textTransform', 'wordSpacing', 'lineHeight', 'tabSize',
            'padding', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft',
            'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth',
            'boxSizing', 'textIndent'];
        for (var i = 0; i < props.length; i++) {
            _varsACMirror.style[props[i]] = cs[props[i]];
        }
        _varsACMirror.style.position = 'absolute';
        _varsACMirror.style.visibility = 'hidden';
        _varsACMirror.style.pointerEvents = 'none';
        _varsACMirror.style.left = '0';
        _varsACMirror.style.top = '0';
        _varsACMirror.style.borderStyle = 'solid';
        _varsACMirror.style.borderColor = 'transparent';
        _varsACMirror.style.width = field.offsetWidth + 'px';
        _varsACMirror.style.height = 'auto';
        // Word wrap: textareas wrap, inputs don't.
        if (field.tagName === 'INPUT') {
            _varsACMirror.style.whiteSpace = 'pre';
            _varsACMirror.style.overflow = 'hidden';
        } else {
            _varsACMirror.style.whiteSpace = 'pre-wrap';
            _varsACMirror.style.wordWrap = 'break-word';
        }

        var caret = typeof field.selectionStart === 'number' ? field.selectionStart : (field.value || '').length;
        var value = field.value != null ? String(field.value) : '';
        _varsACMirror.textContent = '';
        _varsACMirror.appendChild(document.createTextNode(value.substring(0, caret)));
        var marker = document.createElement('span');
        marker.textContent = '​'; // zero-width space — doesn't take up layout but has a rect
        _varsACMirror.appendChild(marker);
        _varsACMirror.appendChild(document.createTextNode(value.substring(caret) || ' '));

        var fieldRect = field.getBoundingClientRect();
        var markerRect = marker.getBoundingClientRect();
        var mirrorRect = _varsACMirror.getBoundingClientRect();

        return {
            left: fieldRect.left + (markerRect.left - mirrorRect.left) - (field.scrollLeft || 0),
            top: fieldRect.top + (markerRect.top - mirrorRect.top) - (field.scrollTop || 0),
            height: markerRect.height || parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) || 16,
        };
    }

    function installVarsAutocomplete() {
        if (window.__bowireVarsACInstalled) return;
        window.__bowireVarsACInstalled = true;

        document.addEventListener('input', onVarsACInput, true);
        document.addEventListener('keydown', onVarsACKeydown, true);
        document.addEventListener('mousedown', onVarsACMousedown, true);
        document.addEventListener('blur', onVarsACBlur, true);
        // #125 Phase 2 v3 — selectionchange fires when the caret
        // moves without a value change (arrow-keys, mouse-click
        // inside the field). Pull the ref-preview path off the
        // input event so it surfaces even when the operator is
        // just navigating through an existing template.
        document.addEventListener('selectionchange', onVarsACSelectionChange, true);
        document.addEventListener('focusin', onVarsACFocusIn, true);
        window.addEventListener('scroll', function () {
            closeVarsAC();
            closeVarsRefPreview();
        }, true);
        window.addEventListener('resize', function () {
            closeVarsAC();
            closeVarsRefPreview();
        });
    }

    function onVarsACSelectionChange() {
        var target = document.activeElement;
        if (!isVarsACEligibleTarget(target)) {
            closeVarsRefPreview();
            return;
        }
        // Skip when the autocomplete dropdown is open — the
        // dropdown owns the user's attention; the preview would
        // crowd the screen.
        if (varsACState) return;
        var caret = getTargetCaret(target);
        var value = getTargetValue(target);
        var enclosed = findEnclosingRef(value, caret);
        if (enclosed) showVarsRefPreview(target, enclosed);
        else closeVarsRefPreview();
    }

    function onVarsACFocusIn(e) {
        onVarsACSelectionChange();
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
            // #125 Phase 2 v3 — if the caret sits inside a CLOSED
            // {{ref}}, show the resolved-value preview instead of
            // the autocomplete dropdown. Two distinct surfaces, two
            // distinct moments.
            var enclosed = findEnclosingRef(value, caret);
            if (enclosed) showVarsRefPreview(target, enclosed);
            else closeVarsRefPreview();
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
        // setTimeout so a click inside the popover lands before we
        // tear the state down.
        setTimeout(function () {
            var ae = document.activeElement;
            if (varsACState) {
                if (ae !== varsACState.target && (!_varsACPopover || !_varsACPopover.contains(ae))) {
                    closeVarsAC();
                }
            }
            // Preview popover also folds when focus leaves the
            // template field — no point dangling a resolved value
            // for a field the operator isn't looking at.
            if (!isVarsACEligibleTarget(ae)) closeVarsRefPreview();
        }, 0);
    }

    function isVarsACEligibleTarget(t) {
        if (!t) return false;
        // Opt-out: fields that aren't template-bearing (search
        // palette, workspace rename prompt, etc.) set
        // data-bowire-no-vars-ac="1" so the dropdown leaves them
        // alone.
        if (t.dataset && t.dataset.bowireNoVarsAc === '1') return false;
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

    // #125 Phase 2 v3 — find the closed {{...}} that encloses the
    // caret position, if any. Used to decide whether to show the
    // resolved-value preview popover.
    function findEnclosingRef(value, caret) {
        if (!value) return null;
        var re = /\{\{([^{}]+)\}\}/g;
        var m;
        while ((m = re.exec(value)) !== null) {
            var start = m.index;
            var end = m.index + m[0].length;
            if (caret >= start && caret <= end) {
                return { start: start, end: end, ref: m[1].trim(), match: m[0] };
            }
        }
        return null;
    }

    function showVarsRefPreview(target, enclosed) {
        if (!_varsRefPopover) {
            _varsRefPopover = document.createElement('div');
            _varsRefPopover.className = 'bowire-vars-ref-preview';
            document.body.appendChild(_varsRefPopover);
        }
        var ref = enclosed.ref;
        var kind = (typeof classifyVarKind === 'function')
            ? classifyVarKind(ref)
            : 'env';

        // Resolve via the same substituteVars seam every send-path
        // routes through. Wrap in a single curly so the resolver
        // treats it as a {{...}} reference.
        var resolved, error = null;
        try {
            resolved = (typeof substituteVars === 'function')
                ? substituteVars('{{' + ref + '}}')
                : '{{' + ref + '}}';
            // Detect cycle marker emitted by the resolver.
            if (typeof resolved === 'string' && /<<cycle:[^>]+>>/.test(resolved)) {
                error = 'Circular reference detected';
            } else if (resolved === '{{' + ref + '}}') {
                // The resolver left the placeholder intact → unresolved.
                // #125 Phase 4: ai.* needs prefetch before send (no
                // value yet → "will be resolved at send time"); secret.*
                // means the named secret isn't set on the workspace.
                if (kind === 'ai') {
                    error = 'Resolved at send time via prefetch — not cached yet';
                } else if (kind === 'secret') {
                    error = 'No secret named "' + ref.replace(/^secret\./, '') + '" in this workspace';
                } else {
                    error = 'Not resolved — check the source / name';
                }
            } else if (kind === 'secret') {
                // Successfully resolved secret — mask in the preview
                // so the UI never displays the bearer token.
                resolved = '••••••••';
            }
        } catch (e) {
            error = e.message || 'Resolver threw';
        }

        var preview = error
            ? '<span class="bowire-vars-ref-preview-error">' + escapeHtmlVA(error) + '</span>'
            : '<span class="bowire-vars-ref-preview-value">' + escapeHtmlVA(String(resolved)) + '</span>';
        var label = '<span class="bowire-vars-ref-preview-kind bowire-vars-ref-preview-kind-' + kind + '">'
            + escapeHtmlVA(kind) + '</span>'
            + '<span class="bowire-vars-ref-preview-ref">' + escapeHtmlVA(enclosed.match) + '</span>';
        // #208 Phase 5 — ai.* values are regenerable; offer a re-roll
        // button right in the preview so the operator can cycle to a
        // fresh suggestion without re-typing the reference.
        var reroll = (kind === 'ai' && typeof window.bowireRerollAiVar === 'function')
            ? '<button type="button" class="bowire-vars-ref-reroll" title="Re-roll this AI value" aria-label="Re-roll">↻</button>'
            : '';
        _varsRefPopover.innerHTML = label + '<span class="bowire-vars-ref-preview-arrow">→</span>' + preview + reroll;

        if (reroll) wireRerollButton(ref);

        // Position above the caret. Mirror-div coords from the
        // autocomplete plumbing.
        var coords = getCaretCoords(target);
        if (coords) {
            // Show, measure, then place. Need to set display: block
            // before reading offsetHeight.
            _varsRefPopover.style.display = 'block';
            _varsRefPopover.style.left = '0';
            _varsRefPopover.style.top = '0';
            var ph = _varsRefPopover.offsetHeight || 30;
            var pw = _varsRefPopover.offsetWidth || 240;
            var topPx = coords.top - ph - 4;
            if (topPx < 8) topPx = coords.top + coords.height + 4; // flip below if no room above
            var leftPx = coords.left;
            if (leftPx + pw > window.innerWidth - 8) leftPx = window.innerWidth - pw - 8;
            leftPx = Math.max(8, leftPx);
            _varsRefPopover.style.left = leftPx + 'px';
            _varsRefPopover.style.top = topPx + 'px';
        } else {
            var rect = target.getBoundingClientRect();
            _varsRefPopover.style.left = Math.max(8, rect.left) + 'px';
            _varsRefPopover.style.top = (rect.top - 30) + 'px';
            _varsRefPopover.style.display = 'block';
        }
    }

    // #208 Phase 5 — wire the re-roll button inside the ref preview.
    // mousedown/preventDefault keeps the textarea focused so the
    // selectionchange + blur handlers don't tear the popover down before
    // the click lands; the click then regenerates the ai.* value and
    // updates the value span in place.
    function wireRerollButton(ref) {
        if (!_varsRefPopover) return;
        var btn = _varsRefPopover.querySelector('.bowire-vars-ref-reroll');
        if (!btn) return;
        var aiName = ref.replace(/^ai\./, '');
        btn.addEventListener('mousedown', function (e) { e.preventDefault(); });
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            var valSpan = _varsRefPopover.querySelector(
                '.bowire-vars-ref-preview-value, .bowire-vars-ref-preview-error');
            btn.disabled = true;
            btn.classList.add('bowire-vars-ref-reroll-spin');
            if (valSpan) {
                valSpan.className = 'bowire-vars-ref-preview-value';
                valSpan.textContent = 'rerolling…';
            }
            window.bowireRerollAiVar(aiName).then(function (v) {
                if (!valSpan) return;
                if (v === null || v === undefined || v === '') {
                    valSpan.className = 'bowire-vars-ref-preview-error';
                    valSpan.textContent = 'Re-roll returned no value';
                } else {
                    valSpan.className = 'bowire-vars-ref-preview-value';
                    valSpan.textContent = String(v);
                }
            }).catch(function () {
                if (valSpan) {
                    valSpan.className = 'bowire-vars-ref-preview-error';
                    valSpan.textContent = 'Re-roll failed';
                }
            }).finally(function () {
                btn.disabled = false;
                btn.classList.remove('bowire-vars-ref-reroll-spin');
            });
        });
    }

    function closeVarsRefPreview() {
        if (_varsRefPopover) _varsRefPopover.style.display = 'none';
    }

    function escapeHtmlVA(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
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
        // Anchor at the exact caret column (mirror-div technique).
        // Clamp into viewport so the popover stays visible even when
        // the caret is at the far right edge of the screen.
        var coords = getCaretCoords(varsACState.target);
        if (!coords) {
            var rect = varsACState.target.getBoundingClientRect();
            _varsACPopover.style.left = Math.max(8, rect.left) + 'px';
            _varsACPopover.style.top = (rect.bottom + 2) + 'px';
        } else {
            var popoverW = 280;
            var popoverH = 320;
            var leftPx = coords.left;
            var topPx = coords.top + coords.height + 4;
            // Flip above the caret if the popover would clip the
            // bottom of the viewport.
            if (topPx + popoverH > window.innerHeight - 8) {
                topPx = coords.top - popoverH - 4;
            }
            // Clamp left so popover stays on-screen.
            if (leftPx + popoverW > window.innerWidth - 8) {
                leftPx = window.innerWidth - popoverW - 8;
            }
            leftPx = Math.max(8, leftPx);
            _varsACPopover.style.left = leftPx + 'px';
            _varsACPopover.style.top = topPx + 'px';
        }
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
