    // #125 Phase 2 v2 — color-chip rendering for {{...}} references.
    //
    // <input> and <textarea> are atomic — you can't put markup inside
    // them. The classic workaround: overlay a div on top of the field
    // that mirrors the text but renders {{...}} matches as colored
    // spans. The real field has color: transparent + caret-color
    // set to the original text color so the user still sees their
    // caret and selection.
    //
    // Sync points: every input event (re-tokenize), every scroll
    // (mirror scrollLeft/Top), every focus (attach if not yet).
    // Detach if the field never gets a focus event in the lifetime
    // of the host element — cheaper than instrumenting everything
    // up front.

    function isChipsEligibleTarget(t) {
        if (!t) return false;
        if (t.dataset && t.dataset.bowireNoVarsChip === '1') return false;
        var tag = t.tagName;
        if (tag === 'TEXTAREA') return true;
        if (tag === 'INPUT') {
            var type = (t.type || 'text').toLowerCase();
            return type === 'text' || type === 'search' || type === 'url' || type === 'email' || type === '';
        }
        return false;
    }

    // Source-kind classifier — duplicates the prefix detection in
    // history-env's resolveKey at a high level. Used by the chip
    // renderer to pick the right tint per match without having to
    // resolve the value.
    function classifyVarKind(refName) {
        var s = String(refName || '').trim();
        if (s.indexOf('env.') === 0) return 'env';
        if (s.indexOf('prev.') === 0 || s === 'prev'
            || s.indexOf('response.') === 0 || s === 'response') return 'prev';
        if (s.indexOf('runtime.') === 0) return 'runtime';
        if (/^step\d+\./.test(s)) return 'step';
        if (s.indexOf('secret.') === 0) return 'secret';
        if (s.indexOf('ai.') === 0) return 'ai';
        return 'env';   // bare {{NAME}} resolves through env
    }

    function escapeHtml(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function attachChipOverlay(field) {
        if (!isChipsEligibleTarget(field)) return;
        if (field.__bowireChipOverlay) {
            renderChipsIntoOverlay(field, field.__bowireChipOverlay);
            return;
        }

        var host = document.createElement('span');
        host.className = 'bowire-vars-chip-host';
        var parent = field.parentNode;
        if (!parent) return;
        parent.insertBefore(host, field);
        host.appendChild(field);

        var overlay = document.createElement('div');
        overlay.className = 'bowire-vars-chip-overlay';
        host.appendChild(overlay);

        // Mirror the field's typography + box so chip positions
        // match the underlying text exactly. Re-read every render in
        // case the user's theme switch updated computed styles.
        function syncStyles() {
            var cs = window.getComputedStyle(field);
            var props = ['font', 'letterSpacing', 'textTransform',
                'padding', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft',
                'lineHeight', 'whiteSpace', 'wordSpacing', 'tabSize',
                'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth',
                'boxSizing', 'textIndent'];
            for (var i = 0; i < props.length; i++) {
                overlay.style[props[i]] = cs[props[i]];
            }
            overlay.style.borderStyle = 'solid';
            overlay.style.borderColor = 'transparent';
            overlay.style.width = field.offsetWidth + 'px';
            overlay.style.height = field.offsetHeight + 'px';
            if (field.tagName === 'INPUT') {
                overlay.style.whiteSpace = 'pre';
                overlay.style.overflowX = 'hidden';
            } else {
                overlay.style.whiteSpace = 'pre-wrap';
                overlay.style.wordWrap = 'break-word';
                overlay.style.overflowY = 'auto';
            }
        }

        function onSync() {
            renderChipsIntoOverlay(field, overlay);
            overlay.scrollTop = field.scrollTop;
            overlay.scrollLeft = field.scrollLeft;
        }
        function onScroll() {
            overlay.scrollTop = field.scrollTop;
            overlay.scrollLeft = field.scrollLeft;
        }

        field.addEventListener('input', onSync);
        field.addEventListener('scroll', onScroll);
        field.addEventListener('focus', syncStyles);
        // Observe size changes so the overlay stays in the same box
        // even after a window resize or sidebar collapse.
        if (typeof ResizeObserver === 'function') {
            try {
                var ro = new ResizeObserver(function () { syncStyles(); onSync(); });
                ro.observe(field);
                field.__bowireChipResizeObs = ro;
            } catch { /* old browser — sync on window resize instead */ }
        }
        window.addEventListener('resize', function () { syncStyles(); onSync(); });

        field.classList.add('bowire-vars-chip-field');
        field.__bowireChipOverlay = overlay;
        field.__bowireChipHost = host;

        syncStyles();
        onSync();
    }

    function renderChipsIntoOverlay(field, overlay) {
        var value = field.value != null ? String(field.value) : '';
        if (value.indexOf('{{') === -1) {
            // Nothing to render; clear the overlay so the field's
            // text becomes plain again (no chip backgrounds bleeding
            // through).
            overlay.textContent = value + '\n';
            return;
        }
        var html = '';
        var lastIdx = 0;
        // Same pattern as substituteVars's curly regex.
        var re = /\{\{([^{}]+)\}\}/g;
        var m;
        while ((m = re.exec(value)) !== null) {
            html += escapeHtml(value.substring(lastIdx, m.index));
            var kind = classifyVarKind(m[1]);
            html += '<span class="bowire-vars-chip bowire-vars-chip-' + kind + '">'
                + escapeHtml(m[0]) + '</span>';
            lastIdx = m.index + m[0].length;
        }
        html += escapeHtml(value.substring(lastIdx));
        // Trailing newline guard for textareas — keeps the overlay
        // height in sync when the user's input ends with a newline.
        html += '\n';
        overlay.innerHTML = html;
    }

    // Document-level focus listener: attach the overlay lazily on
    // first focus. Skips fields explicitly opted out
    // (data-bowire-no-vars-chip="1") so search palettes / debug
    // boxes / passwords don't get the chip treatment.
    function installVarsChips() {
        if (window.__bowireVarsChipsInstalled) return;
        window.__bowireVarsChipsInstalled = true;
        document.addEventListener('focusin', function (e) {
            attachChipOverlay(e.target);
        }, true);
    }

    window.__bowireVarsChips = {
        install: installVarsChips,
        attach: attachChipOverlay,
        classify: classifyVarKind,
    };
