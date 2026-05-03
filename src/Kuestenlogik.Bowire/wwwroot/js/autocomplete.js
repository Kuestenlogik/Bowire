    // ---- JSON Body Autocomplete ----
    // Shows a dropdown of schema field names when the user types `"`
    // inside the JSON body textarea. Only top-level fields are suggested.

    /**
     * Attach schema-based autocomplete to a JSON body textarea.
     * @param {HTMLTextAreaElement} textarea  The editor textarea element.
     * @param {object|null} schema           The inputType schema ({ fields: [...] }).
     */
    function attachBodyAutocomplete(textarea, schema) {
        if (!textarea || !schema || !schema.fields || schema.fields.length === 0) return;

        var popup = null;       // the dropdown element, created lazily
        var items = [];         // current filtered suggestions
        var activeIndex = -1;   // keyboard-highlighted index
        var triggerStart = -1;  // caret position of the opening `"`

        // Build the flat suggestion list once from top-level fields.
        var allFields = [];
        for (var i = 0; i < schema.fields.length; i++) {
            var f = schema.fields[i];
            allFields.push({
                name: fieldJsonKey(f),
                type: f.type || '',
                description: f.description || ''
            });
        }

        function createPopup() {
            if (popup) return;
            popup = document.createElement('div');
            popup.className = 'bowire-autocomplete-popup';
            document.body.appendChild(popup);
        }

        function destroyPopup() {
            if (popup) {
                popup.remove();
                popup = null;
            }
            items = [];
            activeIndex = -1;
            triggerStart = -1;
        }

        function positionPopup() {
            if (!popup) return;

            // Measure cursor position inside the textarea by mirroring
            // the text up to the caret into a hidden span.
            var rect = textarea.getBoundingClientRect();
            var coords = getCaretCoordinates(textarea);

            var top = rect.top + coords.top + coords.lineHeight + window.scrollY;
            var left = rect.left + coords.left + window.scrollX;

            // Keep popup within the viewport
            var vpWidth = window.innerWidth;
            var vpHeight = window.innerHeight;
            var popupWidth = 320;

            if (left + popupWidth > vpWidth - 8) {
                left = vpWidth - popupWidth - 8;
            }
            if (left < 8) left = 8;

            popup.style.top = top + 'px';
            popup.style.left = left + 'px';
            popup.style.width = popupWidth + 'px';

            // If popup would overflow below viewport, show it above the cursor
            var popupRect = popup.getBoundingClientRect();
            if (popupRect.bottom > vpHeight - 8) {
                popup.style.top = (rect.top + coords.top - popupRect.height + window.scrollY) + 'px';
            }
        }

        /**
         * Approximate caret pixel coordinates relative to the textarea's
         * top-left corner. Uses a mirror div technique.
         */
        function getCaretCoordinates(ta) {
            var mirror = document.createElement('div');
            var style = window.getComputedStyle(ta);
            var props = [
                'fontFamily', 'fontSize', 'fontWeight', 'fontStyle',
                'letterSpacing', 'textTransform', 'wordSpacing',
                'lineHeight', 'paddingTop', 'paddingRight', 'paddingBottom',
                'paddingLeft', 'borderTopWidth', 'borderRightWidth',
                'borderBottomWidth', 'borderLeftWidth', 'tabSize',
                'whiteSpace', 'wordWrap', 'overflowWrap'
            ];
            mirror.style.position = 'absolute';
            mirror.style.visibility = 'hidden';
            mirror.style.whiteSpace = 'pre-wrap';
            mirror.style.wordWrap = 'break-word';
            mirror.style.width = ta.offsetWidth + 'px';
            for (var p = 0; p < props.length; p++) {
                mirror.style[props[p]] = style[props[p]];
            }

            var text = ta.value.substring(0, ta.selectionStart);
            mirror.textContent = text;

            // Add a marker span at the caret position
            var marker = document.createElement('span');
            marker.textContent = '|';
            mirror.appendChild(marker);

            document.body.appendChild(mirror);
            var markerRect = marker.getBoundingClientRect();
            var mirrorRect = mirror.getBoundingClientRect();
            var coords = {
                top: markerRect.top - mirrorRect.top - ta.scrollTop,
                left: markerRect.left - mirrorRect.left - ta.scrollLeft,
                lineHeight: parseInt(style.lineHeight, 10) || parseInt(style.fontSize, 10) * 1.2
            };
            document.body.removeChild(mirror);
            return coords;
        }

        function renderItems() {
            if (!popup) return;
            popup.innerHTML = '';
            if (items.length === 0) {
                destroyPopup();
                return;
            }
            for (var i = 0; i < items.length; i++) {
                (function (idx) {
                    var it = items[idx];
                    var row = document.createElement('div');
                    row.className = 'bowire-autocomplete-item' + (idx === activeIndex ? ' selected' : '');

                    var nameSpan = document.createElement('span');
                    nameSpan.className = 'bowire-autocomplete-item-name';
                    nameSpan.textContent = it.name;
                    row.appendChild(nameSpan);

                    var typeSpan = document.createElement('span');
                    typeSpan.className = 'bowire-autocomplete-item-type';
                    typeSpan.textContent = it.type;
                    row.appendChild(typeSpan);

                    if (it.description) {
                        var descSpan = document.createElement('span');
                        descSpan.className = 'bowire-autocomplete-item-desc';
                        descSpan.textContent = it.description.length > 60
                            ? it.description.substring(0, 57) + '...'
                            : it.description;
                        row.appendChild(descSpan);
                    }

                    row.addEventListener('mousedown', function (e) {
                        e.preventDefault(); // prevent textarea blur
                        acceptSuggestion(idx);
                    });
                    row.addEventListener('mouseenter', function () {
                        activeIndex = idx;
                        renderItems();
                    });

                    popup.appendChild(row);
                })(i);
            }
        }

        function filterAndShow() {
            if (triggerStart < 0) return;

            var caret = textarea.selectionStart;
            // The prefix is everything between the opening `"` and the caret
            var prefix = textarea.value.substring(triggerStart + 1, caret).toLowerCase();

            items = [];
            for (var i = 0; i < allFields.length; i++) {
                if (allFields[i].name.toLowerCase().indexOf(prefix) === 0) {
                    items.push(allFields[i]);
                }
            }
            // Also include fuzzy-contains matches after prefix matches
            for (var j = 0; j < allFields.length; j++) {
                var n = allFields[j].name.toLowerCase();
                if (n.indexOf(prefix) > 0) {
                    // Only add if not already in items
                    var dup = false;
                    for (var k = 0; k < items.length; k++) {
                        if (items[k].name === allFields[j].name) { dup = true; break; }
                    }
                    if (!dup) items.push(allFields[j]);
                }
            }

            if (items.length === 0) {
                destroyPopup();
                return;
            }

            activeIndex = 0;
            createPopup();
            renderItems();
            positionPopup();
        }

        function acceptSuggestion(idx) {
            if (idx < 0 || idx >= items.length) return;
            var chosen = items[idx].name;
            var caret = textarea.selectionStart;
            var before = textarea.value.substring(0, triggerStart + 1); // includes the opening `"`
            var after = textarea.value.substring(caret);
            // Insert: fieldName" + ": "
            textarea.value = before + chosen + '": ' + after;
            var newCaret = triggerStart + 1 + chosen.length + 3; // after `: `
            textarea.selectionStart = textarea.selectionEnd = newCaret;
            requestMessages[0] = textarea.value;
            destroyPopup();
            // Fire input event so other listeners (e.g. JSON validator) stay in sync
            textarea.dispatchEvent(new Event('input', { bubbles: true }));
        }

        // ---- Event listeners ----

        textarea.addEventListener('input', function () {
            var caret = textarea.selectionStart;
            if (caret === 0) { destroyPopup(); return; }

            // Determine if we're inside a key position: look backwards for
            // an unmatched `"` that appears after a `{`, `,`, or newline
            // (i.e. a JSON key context, not a value context).
            var textBefore = textarea.value.substring(0, caret);

            // Find the last unmatched double-quote
            var lastQuote = textBefore.lastIndexOf('"');
            if (lastQuote < 0) { destroyPopup(); return; }

            // Check if we're between the opening quote and a closing quote
            // The text between lastQuote and caret should have no unescaped quote
            var between = textBefore.substring(lastQuote + 1);
            if (between.indexOf('"') >= 0) { destroyPopup(); return; }

            // Check if this quote is in a key position: the character before
            // the quote (ignoring whitespace) should be `{`, `,`, or start of string
            var beforeQuote = textBefore.substring(0, lastQuote).trimEnd();
            if (beforeQuote.length === 0) { destroyPopup(); return; }
            var lastChar = beforeQuote[beforeQuote.length - 1];
            if (lastChar !== '{' && lastChar !== ',') { destroyPopup(); return; }

            triggerStart = lastQuote;
            filterAndShow();
        });

        textarea.addEventListener('keydown', function (e) {
            if (!popup) return;

            if (e.key === 'ArrowDown') {
                e.preventDefault();
                activeIndex = (activeIndex + 1) % items.length;
                renderItems();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                activeIndex = (activeIndex - 1 + items.length) % items.length;
                renderItems();
            } else if (e.key === 'Enter' || e.key === 'Tab') {
                if (items.length > 0 && activeIndex >= 0) {
                    e.preventDefault();
                    e.stopPropagation();
                    acceptSuggestion(activeIndex);
                }
            } else if (e.key === 'Escape') {
                e.preventDefault();
                destroyPopup();
            }
        });

        textarea.addEventListener('blur', function () {
            // Delay so mousedown on suggestion fires first
            setTimeout(destroyPopup, 150);
        });

        textarea.addEventListener('scroll', function () {
            if (popup) positionPopup();
        });
    }
