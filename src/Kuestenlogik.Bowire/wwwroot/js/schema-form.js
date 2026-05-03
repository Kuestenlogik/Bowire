    // ---- Schema Generation ----
    function generateDefaultJson(messageInfo, depth) {
        if (!messageInfo || !messageInfo.fields || depth > 4) return '{}';
        const d = depth || 0;
        const obj = {};
        for (const f of messageInfo.fields) {
            const key = fieldJsonKey(f);

            // Prefer an OpenAPI example/default if the field carries one
            if (f.example) {
                try { obj[key] = JSON.parse(f.example); continue; }
                catch { /* fall through to type-based default */ }
            }

            if (f.isMap) {
                obj[key] = {};
            } else if (f.isRepeated) {
                obj[key] = [];
            } else if (f.type === 'message' && f.messageType) {
                if (d < 2) {
                    try {
                        obj[key] = JSON.parse(generateDefaultJson(f.messageType, d + 1));
                    } catch {
                        obj[key] = {};
                    }
                } else {
                    obj[key] = {};
                }
            } else if (f.type === 'string') {
                obj[key] = '';
            } else if (f.type === 'bool') {
                obj[key] = false;
            } else if (f.type === 'bytes') {
                obj[key] = '';
            } else if (f.type === 'enum') {
                obj[key] = (f.enumValues && f.enumValues.length > 0) ? f.enumValues[0].name : 0;
            } else {
                // numeric types
                obj[key] = 0;
            }
        }
        return JSON.stringify(obj, null, 2);
    }

    function toCamelCase(s) {
        return s.replace(/_([a-z])/g, (_, c) => c.toUpperCase());
    }

    /**
     * Returns the JSON key to use for a field. REST/OpenAPI fields preserve
     * their original name (the OpenAPI parameter name) so the server-side
     * RestInvoker can find them. Proto fields go through camelCase to match
     * the protobuf JSON wire format.
     */
    function fieldJsonKey(field) {
        if (field && field.source) return field.name;
        return toCamelCase(field.name);
    }

    // ---- Form Input Helpers ----

    function hasDeepNesting(messageInfo, depth) {
        if (!messageInfo || !messageInfo.fields) return false;
        if (depth > 2) return true;
        for (var f of messageInfo.fields) {
            if (f.type === 'message' && f.messageType && f.messageType.fields) {
                if (hasDeepNesting(f.messageType, depth + 1)) return true;
            }
        }
        return false;
    }

    function isIntegerType(type) {
        return ['int32', 'int64', 'uint32', 'uint64', 'sint32', 'sint64',
                'fixed32', 'fixed64', 'sfixed32', 'sfixed64'].indexOf(type) !== -1;
    }

    function isFloatType(type) {
        return type === 'double' || type === 'float';
    }

    function isNumericType(type) {
        return isIntegerType(type) || isFloatType(type);
    }

    /**
     * Walk a discovered messageInfo and return a map of dotted-form-key →
     * error message for every field that violates its declared shape:
     *   - required field with empty / missing value
     *   - numeric field whose current value isn't a parseable number
     *   - integer field with a fractional value
     * Recurses into message types and repeated entries so nested forms get
     * the same checks. Map / bytes fields are skipped (no clean validation
     * rule yet — they pass through to the server).
     */
    function validateForm(messageInfo, prefix) {
        var errors = {};
        if (!messageInfo || !messageInfo.fields) return errors;
        prefix = prefix || '';

        for (var i = 0; i < messageInfo.fields.length; i++) {
            var f = messageInfo.fields[i];
            var camelName = fieldJsonKey(f);
            var key = prefix ? prefix + '.' + camelName : camelName;
            var value = formValues[key];

            // Required check — supports both REST f.required and the legacy
            // proto label === 'required' convention.
            var isRequired = f.required || (f.label === 'required' && !f.isRepeated && !f.isMap && f.type !== 'bool');

            // Repeated fields: validate each entry, but the "required" flag
            // means "at least one element" rather than "every element non-empty".
            if (f.isRepeated && !f.isMap) {
                var arr = Array.isArray(value) ? value : [];
                if (isRequired && arr.length === 0) {
                    errors[key] = f.name + ' must have at least one entry';
                    continue;
                }
                for (var ri = 0; ri < arr.length; ri++) {
                    var itemKey = key + '.' + ri;
                    if (f.type === 'message' && f.messageType) {
                        var nested = validateForm(f.messageType, itemKey);
                        Object.assign(errors, nested);
                    } else if (isNumericType(f.type)) {
                        var ne = numericFieldError(f, arr[ri]);
                        if (ne) errors[itemKey] = ne;
                    }
                }
                continue;
            }

            // Map fields: pass through, no validation rule yet
            if (f.isMap) continue;

            // Nested message — recurse
            if (f.type === 'message' && f.messageType) {
                var nestedErrors = validateForm(f.messageType, key);
                Object.assign(errors, nestedErrors);
                if (isRequired && Object.keys(nestedErrors).length === 0 && !hasAnyNestedValue(f.messageType, key)) {
                    errors[key] = f.name + ' is required';
                }
                continue;
            }

            // Required string / number / etc.
            if (isRequired && (value === undefined || value === null || value === '')) {
                errors[key] = f.name + ' is required';
                continue;
            }

            // Type check for numerics — only when there's a value to check.
            // Empty optional numbers stay empty (server gets a missing field).
            if (isNumericType(f.type) && value !== undefined && value !== null && value !== '') {
                var err = numericFieldError(f, value);
                if (err) errors[key] = err;
            }
        }

        return errors;
    }

    function numericFieldError(field, value) {
        var num = Number(value);
        if (Number.isNaN(num) || !Number.isFinite(num)) {
            return field.name + ' must be a number';
        }
        if (isIntegerType(field.type) && !Number.isInteger(num)) {
            return field.name + ' must be an integer';
        }
        return null;
    }

    // True if any descendant of a nested message has a non-empty form value.
    // Used so that "required nested message" errors don't fire when the user
    // has filled in at least one inner field.
    function hasAnyNestedValue(messageInfo, prefix) {
        if (!messageInfo || !messageInfo.fields) return false;
        for (var i = 0; i < messageInfo.fields.length; i++) {
            var f = messageInfo.fields[i];
            var key = prefix + '.' + fieldJsonKey(f);
            var value = formValues[key];
            if (value !== undefined && value !== null && value !== '') return true;
            if (f.type === 'message' && f.messageType && hasAnyNestedValue(f.messageType, key)) return true;
        }
        return false;
    }

    function getFormValue(prefix, fieldName) {
        var key = prefix ? prefix + '.' + fieldName : fieldName;
        return formValues[key];
    }

    function setFormValue(prefix, fieldName, value) {
        var key = prefix ? prefix + '.' + fieldName : fieldName;
        formValues[key] = value;
    }

    function syncFormToJson() {
        if (!selectedMethod || !selectedMethod.inputType) return;
        // Flush live DOM input values into formValues before
        // serialising. morphdom can replace input nodes between
        // renders, and the new node's onInput listener may not
        // have fired yet for the last keystroke. Reading the DOM
        // directly ensures we never miss user input.
        var liveInputs = document.querySelectorAll('.bowire-form-input');
        for (var li = 0; li < liveInputs.length; li++) {
            var inp = liveInputs[li];
            var fieldKey = inp.dataset && inp.dataset.fieldKey;
            if (fieldKey) {
                if (inp.type === 'checkbox') {
                    formValues[fieldKey] = inp.checked;
                } else {
                    formValues[fieldKey] = inp.value;
                }
            }
        }
        var json = collectFormValuesFromState(selectedMethod.inputType, '');
        requestMessages[0] = JSON.stringify(json, null, 2);
    }

    function syncJsonToForm() {
        if (!selectedMethod || !selectedMethod.inputType) return;
        try {
            var obj = JSON.parse(requestMessages[0] || '{}');
            formValues = {};
            populateFormValuesFromJson(selectedMethod.inputType, '', obj);
        } catch (e) {
            // If JSON is invalid, keep current form values
        }
    }

    function populateFormValuesFromJson(messageInfo, prefix, obj) {
        if (!messageInfo || !messageInfo.fields || !obj) return;
        for (var f of messageInfo.fields) {
            var camelName = fieldJsonKey(f);
            var val = obj[camelName];
            if (val === undefined || val === null) continue;

            if (f.isMap) {
                setFormValue(prefix, camelName, val);
            } else if (f.isRepeated) {
                setFormValue(prefix, camelName, val);
            } else if (f.type === 'message' && f.messageType) {
                populateFormValuesFromJson(f.messageType, prefix ? prefix + '.' + camelName : camelName, val);
            } else {
                setFormValue(prefix, camelName, val);
            }
        }
    }

    function collectFormValuesFromState(messageInfo, prefix) {
        if (!messageInfo || !messageInfo.fields) return {};
        var obj = {};
        for (var f of messageInfo.fields) {
            var camelName = fieldJsonKey(f);
            var key = prefix ? prefix + '.' + camelName : camelName;

            if (f.isMap) {
                var mapVal = formValues[key];
                if (mapVal && typeof mapVal === 'object' && Object.keys(mapVal).length > 0) {
                    obj[camelName] = mapVal;
                }
            } else if (f.isRepeated) {
                var arrVal = formValues[key];
                if (Array.isArray(arrVal) && arrVal.length > 0) {
                    obj[camelName] = arrVal;
                }
            } else if (f.type === 'message' && f.messageType) {
                var nested = collectFormValuesFromState(f.messageType, key);
                if (Object.keys(nested).length > 0) {
                    obj[camelName] = nested;
                }
            } else if (f.type === 'bool') {
                var bVal = formValues[key];
                if (bVal === true) obj[camelName] = true;
                // Omit false (proto3 default)
            } else if (isNumericType(f.type)) {
                var nVal = formValues[key];
                if (nVal !== undefined && nVal !== '' && nVal !== null) {
                    var num = Number(nVal);
                    if (!isNaN(num) && num !== 0) obj[camelName] = num;
                }
            } else if (f.type === 'enum') {
                var eVal = formValues[key];
                if (eVal !== undefined && eVal !== '' && eVal !== null) {
                    // Omit the first enum value (proto3 default)
                    var firstEnum = (f.enumValues && f.enumValues.length > 0) ? f.enumValues[0].name : '';
                    if (eVal !== firstEnum) obj[camelName] = eVal;
                }
            } else if (f.type === 'bytes') {
                var bytesVal = formValues[key];
                if (bytesVal && String(bytesVal).trim()) obj[camelName] = String(bytesVal);
            } else if (f.isBinary) {
                // Multipart binary: pass the structured { filename, data }
                // object straight through so RestInvoker decodes it back
                // into a multipart StreamContent with the original filename.
                var fileVal = formValues[key];
                if (fileVal && typeof fileVal === 'object' && fileVal.data) {
                    obj[camelName] = { filename: fileVal.filename || '', data: fileVal.data };
                }
            } else {
                // string
                var sVal = formValues[key];
                if (sVal !== undefined && sVal !== null && String(sVal) !== '') {
                    obj[camelName] = String(sVal);
                }
            }
        }
        return obj;
    }

    function renderFormFields(messageInfo, prefix, depth) {
        if (!messageInfo || !messageInfo.fields) return el('div');
        var d = depth || 0;
        var container = el('div', { className: d > 0 ? 'bowire-form-nested' : 'bowire-form' });

        for (var fi = 0; fi < messageInfo.fields.length; fi++) {
            (function (f) {
                var camelName = fieldJsonKey(f);
                var key = prefix ? prefix + '.' + camelName : camelName;

                var field = el('div', { className: 'bowire-form-field' });

                // Label row
                var typeLabel = f.type;
                if (f.isRepeated) typeLabel = 'repeated ' + typeLabel;
                if (f.isMap) typeLabel = 'map';
                // REST: explicit Required wins; Proto: legacy "label" check.
                var isRequired = f.required || (f.label === 'required' && !f.isRepeated && !f.isMap && f.type !== 'bool');
                var label = el('div', { className: 'bowire-form-label' },
                    el('span', {
                        className: 'bowire-form-label-name' + (isRequired ? ' required' : ''),
                        title: f.description || f.name,
                        textContent: f.name
                    }),
                    isRequired
                        ? el('span', { className: 'bowire-form-label-required', textContent: 'required' })
                        : el('span', { className: 'bowire-form-label-optional', textContent: 'optional' }),
                    f.source ? el('span', { className: 'bowire-form-label-source bowire-source-' + f.source, textContent: f.source }) : null,
                    el('span', { className: 'bowire-form-label-type', textContent: typeLabel }),
                    f.source ? null : el('span', { className: 'bowire-form-label-number', textContent: '#' + f.number })
                );
                field.appendChild(label);
                // Description tooltip below label for REST fields with non-trivial text
                if (f.description && f.description.length > 0 && f.source) {
                    field.appendChild(el('div', { className: 'bowire-form-field-desc', textContent: f.description }));
                }

                // Handle repeated fields
                if (f.isRepeated && !f.isMap) {
                    var arr = formValues[key];
                    if (!Array.isArray(arr)) { arr = []; formValues[key] = arr; }

                    var listContainer = el('div', { className: 'bowire-form-repeated-list' });

                    for (var ri = 0; ri < arr.length; ri++) {
                        (function (idx) {
                            var itemRow = el('div', { style: 'display:flex;gap:6px;align-items:center;margin-bottom:4px' });

                            if (f.type === 'message' && f.messageType && d < 3) {
                                // Nested repeated message — render as mini fieldset
                                var nestedContainer = el('div', { style: 'flex:1' });
                                var itemLabel = el('div', { className: 'bowire-form-nested-toggle', textContent: '[' + idx + ']' });
                                nestedContainer.appendChild(itemLabel);
                                // For repeated messages, store each as sub-object in array
                                // We use a separate key pattern: key.{idx}.subfield
                                var subPrefix = key + '.' + idx;
                                if (typeof arr[idx] !== 'object' || arr[idx] === null) arr[idx] = {};
                                var subFields = renderFormFieldsForRepeatedMessage(f.messageType, subPrefix, d + 1, arr, idx);
                                nestedContainer.appendChild(subFields);
                                itemRow.appendChild(nestedContainer);
                            } else {
                                var input = createInputForType(f.type, arr[idx], function (val) {
                                    arr[idx] = val;
                                });
                                input.style.flex = '1';
                                itemRow.appendChild(input);
                            }

                            var removeBtn = el('button', {
                                className: 'bowire-metadata-remove',
                                textContent: '\u00d7',
                                title: 'Remove item',
                                onClick: function () { arr.splice(idx, 1); render(); }
                            });
                            itemRow.appendChild(removeBtn);
                            listContainer.appendChild(itemRow);
                        })(ri);
                    }

                    var addBtn = el('button', {
                        className: 'bowire-form-repeated-add',
                        textContent: '+ Add item',
                        onClick: function () {
                            if (f.type === 'message') arr.push({});
                            else if (f.type === 'bool') arr.push(false);
                            else if (isNumericType(f.type)) arr.push(0);
                            else arr.push('');
                            render();
                        }
                    });
                    listContainer.appendChild(addBtn);
                    field.appendChild(listContainer);
                }
                // Handle map fields
                else if (f.isMap) {
                    var mapVal = formValues[key];
                    if (!mapVal || typeof mapVal !== 'object') { mapVal = {}; formValues[key] = mapVal; }

                    var mapEntries = Object.entries(mapVal);
                    var mapContainer = el('div', { className: 'bowire-form-repeated-list' });

                    for (var mi = 0; mi < mapEntries.length; mi++) {
                        (function (mapKey, mapValue, midx) {
                            var entryRow = el('div', { style: 'display:flex;gap:6px;align-items:center;margin-bottom:4px' });
                            var keyInput = el('input', {
                                className: 'bowire-form-input',
                                type: 'text',
                                placeholder: 'key',
                                value: mapKey,
                                style: 'flex:1',
                                onInput: function () {
                                    var entries = Object.entries(formValues[key]);
                                    var newMap = {};
                                    for (var ei = 0; ei < entries.length; ei++) {
                                        if (ei === midx) newMap[this.value] = entries[ei][1];
                                        else newMap[entries[ei][0]] = entries[ei][1];
                                    }
                                    formValues[key] = newMap;
                                }
                            });
                            var valInput = el('input', {
                                className: 'bowire-form-input',
                                type: 'text',
                                placeholder: 'value',
                                value: String(mapValue),
                                style: 'flex:1',
                                onInput: function () {
                                    formValues[key][mapKey] = this.value;
                                }
                            });
                            var removeBtn = el('button', {
                                className: 'bowire-metadata-remove',
                                textContent: '\u00d7',
                                title: 'Remove entry',
                                onClick: function () {
                                    delete formValues[key][mapKey];
                                    render();
                                }
                            });
                            entryRow.appendChild(keyInput);
                            entryRow.appendChild(valInput);
                            entryRow.appendChild(removeBtn);
                            mapContainer.appendChild(entryRow);
                        })(mapEntries[mi][0], mapEntries[mi][1], mi);
                    }

                    var addEntryBtn = el('button', {
                        className: 'bowire-form-repeated-add',
                        textContent: '+ Add entry',
                        onClick: function () {
                            formValues[key][''] = '';
                            render();
                        }
                    });
                    mapContainer.appendChild(addEntryBtn);
                    field.appendChild(mapContainer);
                }
                // Handle nested message
                else if (f.type === 'message' && f.messageType && d < 3) {
                    var nestedExpanded = formValues['__expanded__' + key] !== false;
                    var toggleEl = el('div', {
                        className: 'bowire-form-nested-toggle',
                        onClick: function () {
                            formValues['__expanded__' + key] = !nestedExpanded;
                            render();
                        }
                    },
                        el('span', { textContent: nestedExpanded ? '\u25BE' : '\u25B8' }),
                        el('span', { textContent: f.messageType.name || 'object' })
                    );
                    field.appendChild(toggleEl);
                    if (nestedExpanded) {
                        field.appendChild(renderFormFields(f.messageType, key, d + 1));
                    }
                }
                // Handle enum
                else if (f.type === 'enum' && f.enumValues && f.enumValues.length > 0) {
                    var currentEnumVal = formValues[key] !== undefined ? formValues[key] : f.enumValues[0].name;
                    var select = el('select', {
                        className: 'bowire-form-select',
                        dataset: { fieldKey: key },
                        onChange: function () { formValues[key] = this.value; }
                    });
                    for (var ei = 0; ei < f.enumValues.length; ei++) {
                        var opt = el('option', { value: f.enumValues[ei].name, textContent: f.enumValues[ei].name + ' (' + f.enumValues[ei].number + ')' });
                        if (f.enumValues[ei].name === currentEnumVal) opt.selected = true;
                        select.appendChild(opt);
                    }
                    field.appendChild(select);
                }
                // Handle bool
                else if (f.type === 'bool') {
                    var boolVal = formValues[key] === true;
                    var toggle = el('div', { className: 'bowire-form-toggle' },
                        el('input', {
                            className: 'bowire-form-checkbox',
                            type: 'checkbox',
                            onChange: function () { formValues[key] = this.checked; }
                        }),
                        el('span', { textContent: boolVal ? 'true' : 'false', style: 'font-size:12px;color:var(--bowire-text-secondary)' })
                    );
                    // Set checked state after creation
                    var cb = toggle.querySelector('input');
                    if (cb) cb.checked = boolVal;
                    // Update label on change
                    cb.addEventListener('change', function () {
                        var labelSpan = this.parentElement.querySelector('span');
                        if (labelSpan) labelSpan.textContent = this.checked ? 'true' : 'false';
                    });
                    field.appendChild(toggle);
                }
                // Handle bytes
                else if (f.type === 'bytes') {
                    var bytesVal = formValues[key] !== undefined ? String(formValues[key]) : '';
                    var ta = el('textarea', {
                        className: 'bowire-form-input',
                        dataset: { fieldKey: key },
                        placeholder: 'Base64-encoded bytes...',
                        style: 'height:60px;resize:vertical;padding:6px 10px',
                        onInput: function () { formValues[key] = this.value; }
                    });
                    ta.value = bytesVal;
                    field.appendChild(ta);
                }
                // Handle number types
                else if (isNumericType(f.type)) {
                    var numVal = formValues[key] !== undefined ? formValues[key] : '';
                    var numInvalid = formValidationErrors[key] ? ' invalid' : '';
                    var numAttrs = {
                        className: 'bowire-form-input' + numInvalid,
                        dataset: { fieldKey: key },
                        type: 'number',
                        placeholder: f.type,
                        onInput: function () {
                            formValues[key] = this.value;
                            if (formValidationErrors[key]) {
                                delete formValidationErrors[key];
                                this.classList.remove('invalid');
                                var sib = this.parentElement && this.parentElement.querySelector('.bowire-form-field-error');
                                if (sib) sib.remove();
                            }
                        }
                    };
                    if (isFloatType(f.type)) numAttrs.step = 'any';
                    var numInput = el('input', numAttrs);
                    numInput.value = numVal;
                    field.appendChild(numInput);
                }
                // Handle multipart/form-data binary fields — file picker
                // uploads the file as base64, stored in formValues as a
                // structured { filename, data } object so the server-side
                // RestInvoker can decode it back into a multipart part with
                // a real filename. Discovery flags REST fields with
                // isBinary=true when the OpenAPI schema declared format=binary.
                else if (f.isBinary) {
                    var binVal = formValues[key];
                    var binWrap = el('div', { className: 'bowire-form-file-wrap' });
                    var fileInput = el('input', {
                        className: 'bowire-form-file-input',
                        type: 'file',
                        dataset: { fieldKey: key },
                        onChange: function () {
                            var file = this.files && this.files[0];
                            if (!file) {
                                formValues[key] = null;
                                render();
                                return;
                            }
                            var reader = new FileReader();
                            var fname = file.name;
                            reader.onload = function (e) {
                                // FileReader.readAsDataURL returns "data:<mime>;base64,<payload>"
                                // — strip the prefix so RestInvoker gets pure base64.
                                var raw = String(e.target.result || '');
                                var commaIdx = raw.indexOf(',');
                                var base64 = commaIdx >= 0 ? raw.substring(commaIdx + 1) : raw;
                                formValues[key] = { filename: fname, data: base64, size: file.size };
                                render();
                            };
                            reader.readAsDataURL(file);
                        }
                    });
                    binWrap.appendChild(fileInput);
                    if (binVal && typeof binVal === 'object' && binVal.filename) {
                        var sizeKb = binVal.size ? (binVal.size / 1024).toFixed(1) + ' KB' : '';
                        binWrap.appendChild(el('div', {
                            className: 'bowire-form-file-loaded',
                            textContent: '✓ ' + binVal.filename + (sizeKb ? ' (' + sizeKb + ')' : '')
                        }));
                    }
                    field.appendChild(binWrap);
                }
                // Handle string (default)
                else {
                    var strVal = formValues[key] !== undefined ? String(formValues[key]) : '';
                    var strInvalid = formValidationErrors[key] ? ' invalid' : '';
                    var strInput = el('input', {
                        className: 'bowire-form-input' + strInvalid,
                        type: 'text',
                        placeholder: f.type,
                        dataset: { fieldKey: key },
                        onInput: function () {
                            formValues[key] = this.value;
                            if (formValidationErrors[key]) {
                                delete formValidationErrors[key];
                                this.classList.remove('invalid');
                                var sib2 = this.parentElement && this.parentElement.querySelector('.bowire-form-field-error');
                                if (sib2) sib2.remove();
                            }
                        }
                    });
                    strInput.value = strVal;
                    field.appendChild(strInput);
                }

                // Per-field validation error message — populated by handleExecute
                // when the user tries to submit an invalid form. Cleared
                // automatically as soon as the user edits the field.
                if (formValidationErrors[key]) {
                    field.appendChild(el('div', {
                        className: 'bowire-form-field-error',
                        textContent: formValidationErrors[key]
                    }));
                }

                container.appendChild(field);
            })(messageInfo.fields[fi]);
        }

        return container;
    }

    function renderFormFieldsForRepeatedMessage(messageInfo, prefix, depth, arr, idx) {
        // For repeated messages stored in array, we read/write from arr[idx] directly
        if (!messageInfo || !messageInfo.fields) return el('div');
        var container = el('div', { className: 'bowire-form-nested' });

        for (var fi = 0; fi < messageInfo.fields.length; fi++) {
            (function (f) {
                var camelName = fieldJsonKey(f);
                var field = el('div', { className: 'bowire-form-field' });

                var typeLabel = f.type;
                var isReq = f.required || (f.label === 'required' && f.type !== 'bool');
                var label = el('div', { className: 'bowire-form-label' },
                    el('span', {
                        className: 'bowire-form-label-name' + (isReq ? ' required' : ''),
                        title: f.description || f.name,
                        textContent: f.name
                    }),
                    isReq
                        ? el('span', { className: 'bowire-form-label-required', textContent: 'required' })
                        : el('span', { className: 'bowire-form-label-optional', textContent: 'optional' }),
                    el('span', { className: 'bowire-form-label-type', textContent: typeLabel }),
                    el('span', { className: 'bowire-form-label-number', textContent: '#' + f.number })
                );
                field.appendChild(label);

                if (f.type === 'bool') {
                    var bVal = arr[idx][camelName] === true;
                    var toggle = el('div', { className: 'bowire-form-toggle' },
                        el('input', {
                            className: 'bowire-form-checkbox',
                            type: 'checkbox',
                            onChange: function () { arr[idx][camelName] = this.checked; }
                        }),
                        el('span', { textContent: bVal ? 'true' : 'false', style: 'font-size:12px;color:var(--bowire-text-secondary)' })
                    );
                    var cb = toggle.querySelector('input');
                    if (cb) cb.checked = bVal;
                    field.appendChild(toggle);
                } else if (isNumericType(f.type)) {
                    var nAttrs = {
                        className: 'bowire-form-input',
                        type: 'number',
                        placeholder: f.type,
                        onInput: function () { arr[idx][camelName] = this.value; }
                    };
                    if (isFloatType(f.type)) nAttrs.step = 'any';
                    var nInput = el('input', nAttrs);
                    nInput.value = arr[idx][camelName] !== undefined ? arr[idx][camelName] : '';
                    field.appendChild(nInput);
                } else {
                    var sInput = el('input', {
                        className: 'bowire-form-input',
                        type: 'text',
                        placeholder: f.type,
                        onInput: function () { arr[idx][camelName] = this.value; }
                    });
                    sInput.value = arr[idx][camelName] !== undefined ? String(arr[idx][camelName]) : '';
                    field.appendChild(sInput);
                }

                container.appendChild(field);
            })(messageInfo.fields[fi]);
        }

        return container;
    }

    function createInputForType(type, currentVal, onChange) {
        if (type === 'bool') {
            var cb = el('input', {
                className: 'bowire-form-checkbox',
                type: 'checkbox',
                onChange: function () { onChange(this.checked); }
            });
            cb.checked = currentVal === true;
            return cb;
        } else if (isNumericType(type)) {
            var attrs = {
                className: 'bowire-form-input',
                type: 'number',
                placeholder: type,
                onInput: function () { onChange(this.value); }
            };
            if (isFloatType(type)) attrs.step = 'any';
            var inp = el('input', attrs);
            inp.value = currentVal !== undefined ? currentVal : '';
            return inp;
        } else {
            var textInp = el('input', {
                className: 'bowire-form-input',
                type: 'text',
                placeholder: type,
                onInput: function () { onChange(this.value); }
            });
            textInp.value = currentVal !== undefined ? String(currentVal) : '';
            return textInp;
        }
    }

    // ---- Schema Rendering ----
    function renderSchemaFields(fields, depth) {
        if (!fields || !fields.length) return null;
        const d = depth || 0;
        const container = el('div', { className: d > 0 ? 'bowire-schema-nested' : '' });
        for (const f of fields) {
            let typeLabel = f.type;
            if (f.type === 'message' && f.messageType) typeLabel = f.messageType.name;
            if (f.type === 'enum' && f.enumValues) typeLabel = 'enum';
            if (f.isMap) typeLabel = 'map<...>';
            if (f.isRepeated) typeLabel = `repeated ${typeLabel}`;

            const row = el('div', { className: 'bowire-schema-field' },
                el('span', { className: 'bowire-schema-field-name', textContent: f.name }),
                el('span', { className: 'bowire-schema-field-type', textContent: typeLabel }),
                el('span', { className: 'bowire-schema-field-label', textContent: f.label !== 'optional' ? f.label : '' }),
                el('span', { className: 'bowire-schema-field-number', textContent: `#${f.number}` })
            );
            container.appendChild(row);

            if (f.type === 'message' && f.messageType && f.messageType.fields && f.messageType.fields.length > 0 && d < 4) {
                const nested = renderSchemaFields(f.messageType.fields, d + 1);
                if (nested) container.appendChild(nested);
            }

            if (f.type === 'enum' && f.enumValues && f.enumValues.length > 0) {
                const enumContainer = el('div', { className: 'bowire-schema-nested' });
                for (const ev of f.enumValues) {
                    enumContainer.appendChild(el('div', { className: 'bowire-schema-field' },
                        el('span', { className: 'bowire-schema-field-name', textContent: ev.name }),
                        el('span', { className: 'bowire-schema-field-number', textContent: `= ${ev.number}` })
                    ));
                }
                container.appendChild(enumContainer);
            }
        }
        return container;
    }

