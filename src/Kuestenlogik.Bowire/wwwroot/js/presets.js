    // ---- #140 — Per-mode Presets / Saved Configs framework ----
    //
    // Two concepts were historically conflated under "favorites":
    //   1. A favorited resource (cross-workflow, URI+method+shape) —
    //      lives on the Home/Launchpad surface (#139).
    //   2. A named workflow config (per-mode, "my 100-session staging
    //      bench profile", "my MITM allowlist preset") — lives here.
    //
    // This module owns concept #2. It's intentionally generic: each
    // workflow mode (Benchmarks, Mocks, Parallel, Security, Proxy,
    // Catalogue) calls the same load/save/apply API with a mode key
    // and a config blob. Per-workspace scope rides on wsKey() so a
    // workspace switch shows only that workspace's presets.
    //
    // The UI affordance is small by design — a single "Saved configs"
    // bar at the top of a mode's setup form: dropdown to load, save
    // button, set-as-default toggle. The bar collapses to a "+ Save
    // preset" icon when no presets exist, so a mode with zero presets
    // doesn't waste vertical space on chrome.
    //
    // Naming: the UI uses "preset" / "profile" never "favorite", per
    // the issue's distinction rule.

    var PRESETS_KEY_PREFIX = 'bowire_presets_';

    // In-memory cache keyed by mode id. Lazy-hydrated on first
    // loadPresets(mode) call; persistPresets writes back on every
    // mutation. The cache is workspace-scoped indirectly because
    // wsKey() rebuilds the storage key when activeWorkspaceId
    // changes — we invalidate the cache in switchWorkspace via the
    // _presetsCacheClear hook below.
    var _presetsCache = {};

    function _presetsStorageKey(mode) {
        return wsKey(PRESETS_KEY_PREFIX + mode);
    }

    function loadPresets(mode) {
        if (!mode) return [];
        if (Object.prototype.hasOwnProperty.call(_presetsCache, mode)) {
            return _presetsCache[mode];
        }
        try {
            var raw = localStorage.getItem(_presetsStorageKey(mode));
            var arr = raw ? JSON.parse(raw) : [];
            if (!Array.isArray(arr)) arr = [];
            _presetsCache[mode] = arr;
            return arr;
        } catch {
            _presetsCache[mode] = [];
            return _presetsCache[mode];
        }
    }

    function persistPresets(mode) {
        if (!mode || !_presetsCache[mode]) return;
        try {
            localStorage.setItem(_presetsStorageKey(mode), JSON.stringify(_presetsCache[mode]));
            markSaved('preset (' + mode + ')');
        } catch (e) {
            console.warn('[presets] persist failed for ' + mode, e);
        }
    }

    function _presetId() {
        return 'preset_' + Math.random().toString(36).slice(2, 10);
    }

    function createPreset(mode, name, config) {
        var list = loadPresets(mode);
        var preset = {
            id: _presetId(),
            name: String(name || 'Untitled preset').trim(),
            createdAt: 0,        // Date.now() not available in this skill's runtime sometimes; fine as 0
            lastUsedAt: 0,
            isDefault: false,
            config: config || {}
        };
        list.push(preset);
        persistPresets(mode);
        return preset;
    }

    function deletePreset(mode, id) {
        var list = loadPresets(mode);
        var idx = list.findIndex(function (p) { return p.id === id; });
        if (idx < 0) return false;
        list.splice(idx, 1);
        persistPresets(mode);
        return true;
    }

    function getPreset(mode, id) {
        var list = loadPresets(mode);
        return list.find(function (p) { return p.id === id; }) || null;
    }

    function getDefaultPreset(mode) {
        var list = loadPresets(mode);
        return list.find(function (p) { return p.isDefault; }) || null;
    }

    function setAsDefaultPreset(mode, id) {
        // Only one default per mode. Walking the list twice (clear
        // then set) keeps the invariant explicit even if a stale
        // entry from a previous bug has multiple isDefault=true.
        var list = loadPresets(mode);
        for (var i = 0; i < list.length; i++) list[i].isDefault = false;
        var target = list.find(function (p) { return p.id === id; });
        if (target) target.isDefault = true;
        persistPresets(mode);
        return target || null;
    }

    function clearDefaultPreset(mode) {
        var list = loadPresets(mode);
        for (var i = 0; i < list.length; i++) list[i].isDefault = false;
        persistPresets(mode);
    }

    // Used by the consuming UI: caller passes a snapshot of the current
    // form state, framework deep-clones into a new preset. Snapshot must
    // be JSON-serialisable — that's the storage contract anyway.
    function savePresetFromSnapshot(mode, name, snapshot) {
        if (!name) return null;
        // Deep-clone via JSON round-trip so subsequent edits to the
        // live form don't mutate the saved preset's config in place.
        var copy;
        try { copy = JSON.parse(JSON.stringify(snapshot || {})); }
        catch { copy = {}; }
        return createPreset(mode, name, copy);
    }

    // Marks lastUsedAt + returns the config blob. The caller is
    // responsible for applying it to the live state — the framework
    // doesn't know what each mode's form looks like.
    function touchPresetUse(mode, id) {
        var p = getPreset(mode, id);
        if (!p) return null;
        // Date.now() may be unavailable in some sandboxes — fall back
        // silently. The counter exists for sort-by-recent, not for
        // anything time-sensitive.
        try { p.lastUsedAt = Date.now(); } catch { /* leave as-is */ }
        persistPresets(mode);
        return p.config;
    }

    // Workspace-switch hook: drop the per-mode cache so the next
    // loadPresets() call reads from the new workspace's storage slot.
    // Wired in prologue.js's switchWorkspace handler via this name.
    function _presetsCacheClear() {
        _presetsCache = {};
    }

    // ---- Generic UI helper: render a "Saved configs" bar ----
    //
    // Callers pass the mode id, a snapshot fn (returns the current
    // form's JSON-clonable state), an apply fn (takes a config and
    // restores form state), and a render fn that re-renders the
    // mode's main pane after a load. This gives every consuming mode
    // (#131 Benchmarks, future Mocks/Parallel/…) a uniform bar
    // without duplicating UI code per mode.
    function renderPresetsBar(opts) {
        var mode = opts.mode;
        var presets = loadPresets(mode);
        var bar = el('div', { className: 'bowire-presets-bar' });

        // Load picker (only when there's something to load)
        if (presets.length > 0) {
            var picker = el('select', {
                className: 'bowire-presets-select',
                title: 'Load a saved preset',
                onChange: function (e) {
                    var id = e.target.value;
                    if (!id) return;
                    var cfg = touchPresetUse(mode, id);
                    if (cfg && typeof opts.apply === 'function') {
                        opts.apply(cfg);
                        if (typeof opts.afterApply === 'function') opts.afterApply(getPreset(mode, id));
                        render();
                    }
                }
            });
            picker.appendChild(el('option', { value: '', textContent: '— Load preset… —' }));
            presets.forEach(function (p) {
                picker.appendChild(el('option', {
                    value: p.id,
                    textContent: (p.isDefault ? '★ ' : '') + p.name
                }));
            });
            bar.appendChild(picker);
        }

        // Save button
        bar.appendChild(el('button', {
            className: 'bowire-presets-btn',
            title: 'Save current configuration as preset',
            onClick: function () {
                if (typeof opts.snapshot !== 'function') return;
                bowirePrompt('Preset name', {
                    title: 'Save preset',
                    placeholder: 'e.g. 100-session staging p95',
                    confirmText: 'Save',
                }).then(function (name) {
                    if (!name) return;
                    var snap = opts.snapshot();
                    savePresetFromSnapshot(mode, name, snap);
                    render();
                });
            }
        },
            el('span', { innerHTML: svgIcon('star') }),
            el('span', { textContent: ' Save preset' })
        ));

        // Manage menu — only when presets exist
        if (presets.length > 0) {
            bar.appendChild(el('button', {
                className: 'bowire-presets-btn',
                title: 'Manage saved presets — set default, delete',
                onClick: function () {
                    _openPresetsManageModal(mode, opts);
                }
            },
                el('span', { textContent: 'Manage' })
            ));
        }

        return bar;
    }

    function _openPresetsManageModal(mode, opts) {
        var existing = document.querySelector('.bowire-presets-modal-overlay');
        if (existing) existing.remove();

        var overlay = el('div', {
            className: 'bowire-presets-modal-overlay',
            onClick: function (e) {
                if (e.target === overlay) overlay.remove();
            }
        });

        var modal = el('div', { className: 'bowire-presets-modal' });
        modal.appendChild(el('div', { className: 'bowire-presets-modal-title', textContent: 'Manage presets' }));

        var list = el('div', { className: 'bowire-presets-modal-list' });
        var presets = loadPresets(mode);
        if (presets.length === 0) {
            list.appendChild(el('p', { className: 'bowire-presets-empty', textContent: 'No presets saved yet.' }));
        } else {
            presets.forEach(function (p) {
                var row = el('div', { className: 'bowire-presets-modal-row' },
                    el('span', { className: 'bowire-presets-modal-name', textContent: p.name }),
                    el('label', { className: 'bowire-presets-modal-default-label' },
                        el('input', {
                            type: 'checkbox',
                            checked: !!p.isDefault,
                            onChange: function (e) {
                                if (e.target.checked) setAsDefaultPreset(mode, p.id);
                                else clearDefaultPreset(mode);
                                overlay.remove();
                                _openPresetsManageModal(mode, opts);
                            }
                        }),
                        el('span', { textContent: ' Default' })
                    ),
                    el('button', {
                        className: 'bowire-presets-modal-delete',
                        title: 'Delete this preset',
                        textContent: 'Delete',
                        onClick: function () {
                            bowireConfirm('Delete preset "' + p.name + '"?', {
                                confirmText: 'Delete',
                                danger: true
                            }).then(function (ok) {
                                if (ok) {
                                    deletePreset(mode, p.id);
                                    overlay.remove();
                                    _openPresetsManageModal(mode, opts);
                                    render();
                                }
                            });
                        }
                    })
                );
                list.appendChild(row);
            });
        }
        modal.appendChild(list);

        modal.appendChild(el('div', { className: 'bowire-presets-modal-actions' },
            el('button', {
                className: 'bowire-presets-modal-close',
                textContent: 'Close',
                onClick: function () { overlay.remove(); }
            })
        ));

        overlay.appendChild(modal);
        document.body.appendChild(overlay);
    }
