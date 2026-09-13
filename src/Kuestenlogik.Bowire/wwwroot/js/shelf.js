    // ---- Shelf (#251) ----
    //
    // A visible holding area for fragments the operator wants to carry
    // between surfaces. Parallel to the OS clipboard, never a replacement:
    // Ctrl+C / Ctrl+V keep doing exactly what they did. What the clipboard
    // cannot do is hold three things at once, or still hold them ten
    // minutes and four screens later — that is what this is for.
    //
    // Two lifetimes, deliberately:
    //   - unpinned: this session, in memory. Grabbed for the next paste.
    //   - pinned:   persisted per workspace. "I will be using this all day."
    // An unpinned item that survived a reload would be a surprise; a pinned
    // one that did not would be a broken promise.

    const SHELF_KEY = 'bowire_shelf';
    const SHELF_MAX = 50;

    // Every item, both lifetimes. Pinned ones are mirrored to storage on
    // change; unpinned ones live only here.
    let shelfItems = [];
    let shelfDrawerOpen = false;
    try { shelfDrawerOpen = localStorage.getItem('bowire_shelf_drawer_open') === '1'; }
    catch { /* ignore */ }

    // The item being dragged out of the shelf, if any. Module-scope rather
    // than captured, because the row is rebuilt on every render and a
    // captured item would go stale mid-drag.
    let shelfDragItemId = null;

    // The last text field the operator was in. Clicking a shelf row moves
    // focus to the shelf, so "insert at the cursor" has to remember where
    // the cursor WAS — reading document.activeElement at click time would
    // always find the shelf row itself.
    let shelfLastFocusedField = null;

    /// Wire the document-level listeners the shelf needs: remembering the
    /// last text field, and accepting drops onto one. Called once from
    /// init; idempotent so a re-entry cannot stack listeners.
    let shelfWired = false;
    function initShelf() {
        if (shelfWired) return;
        shelfWired = true;
        loadShelf();

        document.addEventListener('focusin', function (e) {
            var el2 = e.target;
            if (el2 && (el2.tagName === 'INPUT' || el2.tagName === 'TEXTAREA')) {
                shelfLastFocusedField = el2;
            }
        });

        // Drop onto a field. Delegated rather than per-input: the workbench
        // rebuilds its forms constantly, and a listener per field would be
        // re-attached on every render.
        document.addEventListener('dragover', function (e) {
            if (!shelfDragItemId) return;
            var target = e.target;
            if (!target || (target.tagName !== 'INPUT' && target.tagName !== 'TEXTAREA')) return;
            var item = shelfItemById(shelfDragItemId);
            var ok = shelfAccepts(item, target);
            // Refusal is shown, not swallowed: a drop that quietly does
            // nothing reads as a broken feature.
            target.classList.toggle('bowire-shelf-drop-ok', ok);
            target.classList.toggle('bowire-shelf-drop-no', !ok);
            if (ok) {
                e.preventDefault();
                try { e.dataTransfer.dropEffect = 'copy'; } catch { /* ignore */ }
            }
        });

        document.addEventListener('dragleave', function (e) {
            var target = e.target;
            if (!target || !target.classList) return;
            target.classList.remove('bowire-shelf-drop-ok', 'bowire-shelf-drop-no');
        });

        // "Send to shelf" on a response JSON node. Delegated, because the
        // response tree is rebuilt on every invocation and the nodes are
        // built as HTML strings rather than elements — there is nowhere to
        // hang a per-node handler even if we wanted one.
        //
        // This is the access path that matters most: Ctrl/Cmd+Shift+C only
        // reaches request fields, and the whole point of the shelf is
        // carrying a value OUT of a response and into the next request.
        document.addEventListener('contextmenu', function (e) {
            var node = e.target && e.target.closest
                ? e.target.closest('.bowire-json-pickable')
                : null;
            if (!node) return;
            var path = node.getAttribute('data-json-path') || '';
            var text = (node.textContent || '').replace(/^"|"$/g, '');
            if (!text) return;
            e.preventDefault();
            showContextMenu(e.clientX, e.clientY, [{
                label: t('shelf.sendTo'),
                icon: svgIcon('shelf'),
                onClick: function () {
                    shelfAdd('value', path || t('shelf.untitled'), text, path ? 'response:' + path : '');
                    toast(t('shelf.added', { label: shelfTruncate(path || text, 30) }), 'success');
                    render();
                }
            }]);
        }, true);

        document.addEventListener('drop', function (e) {
            if (!shelfDragItemId) return;
            var target = e.target;
            var item = shelfItemById(shelfDragItemId);
            if (!shelfAccepts(item, target)) return;
            e.preventDefault();
            target.classList.remove('bowire-shelf-drop-ok', 'bowire-shelf-drop-no');
            shelfInsertInto(target, shelfPayloadText(item));
            // The item stays. Holding Alt used to mean "keep" in the sketch;
            // keeping by default is the kinder way round, because losing a
            // fragment you still needed costs another hunt for its source.
            if (!item.pinned && !e.altKey) { /* kept — see above */ }
        });
    }

    /// Ctrl/Cmd+Shift+C — one chord away from the copy that still goes to
    /// the OS clipboard, and deliberately not overriding it.
    function shelfAddFromFocus() {
        var field = shelfFocusedField() || shelfLastFocusedField;
        if (!field || !document.contains(field)) { toast(t('shelf.nothingFocused'), 'info'); return; }
        var selected = '';
        try {
            if (typeof field.selectionStart === 'number' && field.selectionStart !== field.selectionEnd) {
                selected = field.value.slice(field.selectionStart, field.selectionEnd);
            }
        } catch { /* ignore */ }
        var value = selected || field.value;
        if (!value) { toast(t('shelf.nothingToAdd'), 'info'); return; }
        var label = field.getAttribute('data-field-key')
            || field.getAttribute('name')
            || field.getAttribute('placeholder')
            || t('shelf.untitled');
        shelfAdd('value', label, value, field.id ? '#' + field.id : '');
        toast(t('shelf.added', { label: shelfTruncate(label, 30) }), 'success');
        render();
    }

    function loadShelf() {
        try {
            var raw = localStorage.getItem(wsKey(SHELF_KEY));
            var list = raw ? JSON.parse(raw) : [];
            if (!Array.isArray(list)) list = [];
            // Everything in storage was pinned when it was written. Stamp it
            // again on read so a hand-edited file cannot smuggle in an
            // unpinned item that then outlives its lifetime.
            shelfItems = list.map(function (it) {
                return {
                    id: it.id, type: it.type || 'value', label: it.label || '',
                    payload: it.payload, pinned: true,
                    createdAt: it.createdAt || Date.now(), sourceHint: it.sourceHint || ''
                };
            });
        } catch { shelfItems = []; }
    }

    function persistShelf() {
        try {
            var pinned = shelfItems.filter(function (it) { return it.pinned; });
            localStorage.setItem(wsKey(SHELF_KEY), JSON.stringify(pinned));
        } catch (e) { markSaveFailed(t('save.shelf'), e); }
    }

    /// Add a fragment. Returns the item so a caller can report what it did.
    /// `type` is 'value' (a string), 'json' (a parsed fragment) or 'request'
    /// (a whole method call) — it decides which targets will accept it.
    function shelfAdd(type, label, payload, sourceHint) {
        var item = {
            id: 'shf_' + Math.random().toString(36).slice(2, 10),
            type: type || 'value',
            label: label || '',
            payload: payload,
            pinned: false,
            createdAt: Date.now(),
            sourceHint: sourceHint || ''
        };
        shelfItems.unshift(item);
        // Oldest UNPINNED item goes first when the shelf is full: a pin is
        // the operator saying "not this one", and a cap should never be the
        // thing that breaks that promise.
        while (shelfItems.length > SHELF_MAX) {
            var victim = -1;
            for (var i = shelfItems.length - 1; i >= 0; i--) {
                if (!shelfItems[i].pinned) { victim = i; break; }
            }
            if (victim < 0) break;      // all pinned — keep them, stop trimming
            shelfItems.splice(victim, 1);
        }
        return item;
    }

    function shelfRemove(id) {
        for (var i = 0; i < shelfItems.length; i++) {
            if (shelfItems[i].id === id) { shelfItems.splice(i, 1); break; }
        }
        persistShelf();
    }

    function shelfTogglePin(id) {
        var it = shelfItemById(id);
        if (!it) return;
        it.pinned = !it.pinned;
        persistShelf();
    }

    function shelfItemById(id) {
        for (var i = 0; i < shelfItems.length; i++) {
            if (shelfItems[i].id === id) return shelfItems[i];
        }
        return null;
    }

    /// What an item reads as when it lands in a text field. A JSON fragment
    /// travels as compact JSON — pretty-printing it would drop a block of
    /// whitespace into a single-line header field.
    function shelfPayloadText(item) {
        if (!item) return '';
        if (item.type === 'json' || item.type === 'request') {
            try { return JSON.stringify(item.payload); } catch { return String(item.payload); }
        }
        return String(item.payload == null ? '' : item.payload);
    }

    /// Whether an item may be dropped on a target. A JSON fragment refuses a
    /// single-line input: dropping an object into a header is not a thing the
    /// operator meant, and silently stringifying it would hide the mistake.
    function shelfAccepts(item, target) {
        if (!item || !target) return false;
        var tag = target.tagName;
        if (tag !== 'INPUT' && tag !== 'TEXTAREA') return false;
        if (target.readOnly || target.disabled) return false;
        if (item.type === 'json' || item.type === 'request') return tag === 'TEXTAREA';
        return true;
    }

    /// Insert at the cursor of a text field, or replace the selection.
    function shelfInsertInto(target, text) {
        if (!target) return false;
        var start = typeof target.selectionStart === 'number' ? target.selectionStart : target.value.length;
        var end = typeof target.selectionEnd === 'number' ? target.selectionEnd : start;
        var before = target.value.slice(0, start);
        var after = target.value.slice(end);
        target.value = before + text + after;
        var caret = start + text.length;
        try { target.setSelectionRange(caret, caret); } catch { /* ignore */ }
        // Let whatever owns the field hear about it — the schema form and the
        // header editor both persist on input, and a value written straight
        // to .value fires nothing on its own.
        target.dispatchEvent(new Event('input', { bubbles: true }));
        target.focus();
        return true;
    }

    /// The shelf's own view of "what is focused and writable". Used by both
    /// the keyboard add and click-to-insert.
    function shelfFocusedField() {
        var a = document.activeElement;
        if (!a) return null;
        if (a.tagName !== 'INPUT' && a.tagName !== 'TEXTAREA') return null;
        return a;
    }

    // ---- drawer panel ----

    function renderShelfPanel() {
        if (shelfItems.length === 0) {
            return el('div', { className: 'bowire-shelf-empty bowire-main-pad' },
                el('p', { className: 'bowire-drawer-empty', textContent: t('shelf.empty') }),
                el('p', { className: 'bowire-drawer-empty-hint', textContent: t('shelf.emptyHint') })
            );
        }

        var list = el('div', { className: 'bowire-shelf-list' });
        shelfItems.forEach(function (item) {
            list.appendChild(shelfRow(item));
        });
        return list;
    }

    function shelfRow(item) {
        var preview = shelfPayloadText(item);
        var row = el('div', {
            className: 'bowire-shelf-row' + (item.pinned ? ' pinned' : ''),
            'data-shelf-id': item.id,
            draggable: 'true',
            title: item.sourceHint || undefined,
            onDragstart: function (ev) {
                // Resolve by id at drop time, not by closure: the row is
                // rebuilt on every render and morphdom may hand the drop a
                // node from a different render than the one that captured.
                shelfDragItemId = item.id;
                try {
                    ev.dataTransfer.effectAllowed = 'copy';
                    ev.dataTransfer.setData('text/plain', preview);
                } catch { /* ignore */ }
                document.body.classList.add('bowire-shelf-dragging');
            },
            onDragend: function () {
                shelfDragItemId = null;
                document.body.classList.remove('bowire-shelf-dragging');
                document.querySelectorAll('.bowire-shelf-drop-ok, .bowire-shelf-drop-no')
                    .forEach(function (n) {
                        n.classList.remove('bowire-shelf-drop-ok', 'bowire-shelf-drop-no');
                    });
            },
            onMousedown: function () {
                // Read the focused field HERE, before the click moves focus
                // away from it. Relying on the focusin listener alone is
                // fragile: it only knows about fields focused after the
                // shelf was wired, so a field focused by autofocus or before
                // init would leave the shelf with no target on its first
                // use. Not preventing the default — that would cancel the
                // native drag this same row starts.
                var active = shelfFocusedField();
                if (active) shelfLastFocusedField = active;
            },
            onClick: function () {
                // Click inserts at the cursor of whatever text field the
                // operator last had focus in. Without one there is nowhere
                // sensible to put it, and silently doing nothing would read
                // as a broken row — so say so.
                var target = shelfLastFocusedField && document.contains(shelfLastFocusedField)
                    ? shelfLastFocusedField : null;
                if (!target) { toast(t('shelf.noTarget'), 'info'); return; }
                if (!shelfAccepts(item, target)) { toast(t('shelf.refused'), 'error'); return; }
                shelfInsertInto(target, shelfPayloadText(item));
            }
        });

        row.appendChild(el('span', {
            className: 'bowire-shelf-row-glyph',
            innerHTML: svgIcon(item.type === 'json' ? 'braces'
                : item.type === 'request' ? 'lightning' : 'key')
        }));

        row.appendChild(el('div', { className: 'bowire-shelf-row-body' },
            el('div', { className: 'bowire-shelf-row-label', textContent: item.label || t('shelf.untitled') }),
            el('div', { className: 'bowire-shelf-row-preview', textContent: shelfTruncate(preview, 60) })
        ));

        var tools = el('div', { className: 'bowire-shelf-row-tools' });
        tools.appendChild(el('button', {
            className: 'bowire-shelf-row-pin' + (item.pinned ? ' active' : ''),
            title: item.pinned ? t('shelf.unpin') : t('shelf.pin'),
            'aria-label': item.pinned ? t('shelf.unpin') : t('shelf.pin'),
            innerHTML: svgIcon(item.pinned ? 'starFilled' : 'star'),
            onClick: function (ev) {
                ev.stopPropagation();
                shelfTogglePin(item.id);
                render();
            }
        }));
        tools.appendChild(el('button', {
            className: 'bowire-shelf-row-remove',
            title: t('shelf.remove'),
            'aria-label': t('shelf.remove'),
            innerHTML: svgIcon('close'),
            onClick: function (ev) {
                ev.stopPropagation();
                shelfRemove(item.id);
                render();
            }
        }));
        row.appendChild(tools);
        return row;
    }

    function shelfTruncate(text, max) {
        var flat = String(text).replace(/\s+/g, ' ').trim();
        return flat.length <= max ? flat : flat.slice(0, max - 1) + '…';
    }
