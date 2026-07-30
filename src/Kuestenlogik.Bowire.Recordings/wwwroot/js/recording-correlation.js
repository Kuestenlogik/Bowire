    // ---- Correlated timeline (#539) ----
    // Second tab of the Recordings detail pane. A recording that fanned
    // one logical transaction out across gRPC, REST, OData, GraphQL,
    // WebSocket, SignalR, SSE and MQTT reads, in the Steps list, as
    // eight unrelated rows. This renders it as one protocol lane per
    // protocol on a shared time axis, with each step verdicted against
    // a correlation key.
    //
    // The analysis is NOT done here — it lives once, in C#, in
    // Kuestenlogik.Bowire.Recordings.Correlation.RecordingCorrelationAnalyzer,
    // and is reached over POST {prefix}/api/recordings/correlate. The
    // same analyzer backs `bowire recording correlate`, which is what
    // keeps the terminal and the workbench from disagreeing.
    //
    // Fragment contract (this file is spliced into core's IIFE):
    //   * nothing executes at load time beyond the `var` initialisers,
    //   * renderRecordingTimeline() is PURE — it reads the cache and
    //     returns a node, never fetches, never persists, never renders,
    //   * every mutator (ensureRecordingCorrelation /
    //     setRecordingCorrelationKey / importRecordingFromFile) is
    //     reachable only from a click handler,
    //   * the lanes carry exactly ONE delegated listener which
    //     re-resolves its target from data-attributes at click time —
    //     morphdom preserves these nodes across re-renders, so a
    //     closure captured at render time would go stale.

    // Which detail tab is open, and for which recording. Scoped by id
    // (rather than a bare global) so selecting a different recording
    // opens on Steps instead of inheriting a Timeline tab whose model
    // has not been computed yet.
    var recordingDetailTab = 'steps';
    var recordingDetailTabRecId = null;
    // cacheKey -> timeline model, or { error: '…' } for a failed run.
    var recordingCorrelationCache = {};
    // cacheKey -> true while a POST is in flight. Keyed the same way so
    // a re-render during the round-trip cannot start a second one.
    var recordingCorrelationPending = {};
    var recordingCorrelationKeyMenuOpen = false;
    // Step / frame the inspect strip is pinned to. Ids, not objects —
    // re-resolved from the live model on every render.
    var recordingCorrelationSelectedStepId = null;
    var recordingCorrelationSelectedFrame = null;

    function recordingActiveDetailTab(recId) {
        return (recId && recId === recordingDetailTabRecId) ? recordingDetailTab : 'steps';
    }

    // Every click handler in this fragment resolves its recording
    // THROUGH this, at click time — never from a `rec` captured when the
    // pane was built. morphdom keeps these nodes alive across renders,
    // so the handler that fires may well have been created while a
    // different recording was selected.
    function currentCorrelationRecording() {
        return recordingsList.find(function (r) { return r.id === recordingManagerSelectedId; }) || null;
    }

    // MUTATOR — click path only.
    function setRecordingDetailTab(recId, tab) {
        recordingDetailTabRecId = recId || null;
        recordingDetailTab = tab || 'steps';
        if (tab !== 'timeline') {
            recordingCorrelationKeyMenuOpen = false;
        }
    }

    function _correlationStepCount(rec) {
        if (!rec) return 0;
        if (Array.isArray(rec.steps)) return rec.steps.length;
        if (Array.isArray(rec.stepsManifest)) return rec.stepsManifest.length;
        if (typeof rec.stepCount === 'number') return rec.stepCount;
        return 0;
    }

    // The cache identity: which recording, how many steps it currently
    // has (so a hydration or a fresh capture invalidates), and which key
    // it was analysed under.
    function correlationCacheKey(rec, name, value) {
        if (!rec) return '';
        return String(rec.id) + '|' + _correlationStepCount(rec)
            + '|' + (name || '') + '=' + (value || '');
    }

    // The key the next analysis should run under: whatever the recording
    // persisted, else nothing (which lets the server auto-pick).
    function correlationKeyOf(rec) {
        if (rec && rec.correlation && rec.correlation.name && rec.correlation.value) {
            return { name: String(rec.correlation.name), value: String(rec.correlation.value) };
        }
        return null;
    }

    function correlationModelFor(rec) {
        var k = correlationKeyOf(rec);
        return recordingCorrelationCache[correlationCacheKey(rec, k && k.name, k && k.value)] || null;
    }

    function correlationIsPending(rec) {
        var k = correlationKeyOf(rec);
        return !!recordingCorrelationPending[correlationCacheKey(rec, k && k.name, k && k.value)];
    }

    // MUTATOR — click path only (tab click, context menu, key picker,
    // the explicit Build button). Never called from a render function.
    function ensureRecordingCorrelation(recId, name, value) {
        var rec = recordingsList.find(function (r) { return r.id === recId; });
        if (!rec) return;
        // Manifest-only recordings hydrate lazily; posting an empty step
        // array would cache a bogus "no signal" verdict. Hydrate first,
        // then come back.
        if (!Array.isArray(rec.steps)) {
            if (typeof hydrateRecording === 'function') {
                hydrateRecording(rec).then(function () {
                    ensureRecordingCorrelation(recId, name, value);
                    render();
                });
            }
            return;
        }
        var cacheKey = correlationCacheKey(rec, name, value);
        if (recordingCorrelationCache[cacheKey] || recordingCorrelationPending[cacheKey]) return;
        recordingCorrelationPending[cacheKey] = true;

        var payload = { recording: _correlationRecordingPayload(rec) };
        if (name && value) payload.key = { name: String(name), value: String(value) };

        fetch(config.prefix + '/api/recordings/correlate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }).then(function (resp) {
            if (!resp.ok) {
                return resp.json().catch(function () { return null; }).then(function (body) {
                    return {
                        error: resp.status === 404
                            ? 'This server does not expose /api/recordings/correlate — it is older than the workbench, or was built without the Recordings package.'
                            : ((body && (body.detail || body.title)) || ('Correlation failed (HTTP ' + resp.status + ').'))
                    };
                });
            }
            return resp.json();
        }).then(function (model) {
            recordingCorrelationCache[cacheKey] = model || { error: 'Empty response from /api/recordings/correlate.' };
            delete recordingCorrelationPending[cacheKey];
            render();
        }).catch(function (err) {
            recordingCorrelationCache[cacheKey] = {
                error: 'Could not reach /api/recordings/correlate: ' + (err && err.message ? err.message : 'network error')
            };
            delete recordingCorrelationPending[cacheKey];
            render();
        });
    }

    // Only the fields the analyzer reads. Keeps a big recording's
    // manifest / UI bookkeeping off the wire, and — via the shared
    // sanitiser — keeps resolved secrets out of the POST body.
    function _correlationRecordingPayload(rec) {
        var slim = {
            id: rec.id,
            name: rec.name,
            recordingFormatVersion: rec.recordingFormatVersion,
            correlation: rec.correlation || null,
            steps: Array.isArray(rec.steps) ? rec.steps : []
        };
        return (typeof sanitiseForExport === 'function') ? sanitiseForExport(slim) : slim;
    }

    // MUTATOR — click path only. Persists the chosen key onto the
    // recording so the CLI and a later session read the same one.
    function setRecordingCorrelationKey(recId, name, value) {
        var rec = recordingsList.find(function (r) { return r.id === recId; });
        if (!rec) return;
        if (name && value) {
            rec.correlation = { name: String(name), value: String(value) };
        } else {
            delete rec.correlation;
        }
        recordingCorrelationKeyMenuOpen = false;
        recordingCorrelationSelectedStepId = null;
        recordingCorrelationSelectedFrame = null;
        if (typeof persistRecordings === 'function') persistRecordings();
        var k = correlationKeyOf(rec);
        ensureRecordingCorrelation(recId, k && k.name, k && k.value);
        render();
    }

    // ---- Rendering (pure) ----

    function renderRecordingTimeline(rec) {
        var wrap = el('div', { className: 'bowire-recording-timeline' });
        if (!rec) return wrap;

        var model = correlationModelFor(rec);
        var pending = correlationIsPending(rec);

        if (pending) {
            wrap.appendChild(el('div', {
                className: 'bowire-recording-timeline-note',
                textContent: 'Correlating ' + _correlationStepCount(rec) + ' steps…'
            }));
            return wrap;
        }
        if (!model) {
            // Cold cache — the tab click primes it, so this is only
            // reached after a hard refresh or a step-count change. An
            // explicit button rather than a fetch fired from the render
            // path, which would be a state write during render.
            wrap.appendChild(_correlationNotice(
                'Timeline not built yet',
                'Correlating walks every payload in this recording, so it runs on demand rather than on every render.',
                'Build timeline',
                function () { _correlationRebuild(false); }));
            return wrap;
        }
        if (model.error) {
            wrap.appendChild(_correlationNotice('Correlation unavailable', model.error, 'Retry',
                function () { _correlationRebuild(true); }));
            return wrap;
        }

        wrap.appendChild(renderCorrelationKeyBar(model));

        var warnings = Array.isArray(model.warnings) ? model.warnings : [];
        if (!model.key) {
            wrap.appendChild(el('div', { className: 'bowire-recording-timeline-banner' },
                el('strong', { textContent: 'No correlation signal found. ' }),
                el('span', {
                    textContent: 'No correlation header and no id-shaped value shared by two or more steps. '
                        + 'The whole recording is treated as one transaction — the lanes below are still a '
                        + 'faithful time chart, they just carry no per-step verdict.'
                })
            ));
        }
        warnings.forEach(function (w) {
            wrap.appendChild(el('div', {
                className: 'bowire-recording-timeline-banner bowire-recording-timeline-banner-warn',
                textContent: w
            }));
        });

        // The id is load-bearing, not decoration. This node carries the ONE
        // delegated listener for every bar and tick in the pane, and a
        // listener is a property — morphdom copies attributes, never
        // properties. Without a stable id morphdom matches this div by
        // sibling position, keeps whichever live node already sat there,
        // and discards the freshly built one along with its listener; the
        // timeline then renders correctly but nothing in it responds to a
        // click. With the id, morphdom key-matches and patches the node
        // that already has the listener bound.
        var lanes = el('div', {
            id: 'bowire-recording-timeline-lanes',
            className: 'bowire-recording-timeline-lanes'
        });
        lanes.addEventListener('click', _correlationLaneClick);
        var laneList = Array.isArray(model.lanes) ? model.lanes : [];
        if (laneList.length === 0) {
            lanes.appendChild(el('div', {
                className: 'bowire-recording-timeline-note',
                textContent: 'This recording has no steps to place on a timeline.'
            }));
        }
        laneList.forEach(function (lane) {
            lanes.appendChild(renderCorrelationLane(lane, model));
        });
        wrap.appendChild(lanes);

        wrap.appendChild(_renderCorrelationAxis(model));

        var inspect = _renderCorrelationInspect(model);
        if (inspect) wrap.appendChild(inspect);

        wrap.appendChild(_renderCorrelationLegend());
        return wrap;
    }

    // MUTATOR — click path only. Re-resolves the recording so the
    // Build / Retry buttons cannot act on a pane that has since been
    // replaced under them.
    function _correlationRebuild(discardCached) {
        var rec = currentCorrelationRecording();
        if (!rec) return;
        var k = correlationKeyOf(rec);
        if (discardCached) {
            delete recordingCorrelationCache[correlationCacheKey(rec, k && k.name, k && k.value)];
        }
        ensureRecordingCorrelation(rec.id, k && k.name, k && k.value);
        render();
    }

    function _correlationNotice(headline, body, actionLabel, onAction) {
        var card = el('div', { className: 'bowire-recording-timeline-notice' },
            el('div', { className: 'bowire-recording-timeline-notice-title', textContent: headline }),
            el('div', { className: 'bowire-recording-timeline-notice-body', textContent: body })
        );
        if (actionLabel) {
            card.appendChild(el('button', {
                type: 'button',
                className: 'bowire-recording-action-btn',
                onClick: onAction
            }, el('span', { textContent: actionLabel })));
        }
        return card;
    }

    function renderCorrelationKeyBar(model) {
        var bar = el('div', { className: 'bowire-recording-timeline-keybar' });

        var keyLabel = model.key
            ? (model.key.name + ' = ' + model.key.value)
            : 'no signal';
        var chipWrap = el('div', { className: 'bowire-recording-timeline-key-wrap' });
        chipWrap.appendChild(el('button', {
            type: 'button',
            className: 'bowire-recording-timeline-key-chip' + (model.key ? '' : ' is-empty'),
            title: model.key
                ? ('Correlating on the ' + (model.key.source === 'header' ? 'correlation header' : 'payload field')
                    + ' "' + model.key.name + '". Click to pick a different key.')
                : 'No correlation key resolved. Click to pick one manually.',
            onClick: function (e) {
                e.stopPropagation();
                recordingCorrelationKeyMenuOpen = !recordingCorrelationKeyMenuOpen;
                render();
            }
        },
            el('span', { className: 'bowire-recording-timeline-key-source',
                textContent: model.key ? (model.key.source || 'field') : 'none' }),
            el('span', { className: 'bowire-recording-timeline-key-text', textContent: keyLabel }),
            el('span', { className: 'bowire-recording-timeline-key-caret', innerHTML: svgIcon('chevron') })
        ));

        if (recordingCorrelationKeyMenuOpen) {
            var menu = el('div', { className: 'bowire-recording-timeline-key-menu', role: 'menu' });
            // One delegated listener, same reason as the lanes: the menu
            // survives a re-render, so which key an item stands for has
            // to come off the DOM at click time, not out of a closure.
            menu.addEventListener('click', _correlationKeyMenuClick);
            var suggestions = Array.isArray(model.suggestions) ? model.suggestions : [];
            if (suggestions.length === 0) {
                menu.appendChild(el('div', {
                    className: 'bowire-recording-timeline-key-menu-empty',
                    textContent: 'No candidate key — no value is shared by two or more steps.'
                }));
            }
            suggestions.forEach(function (s) {
                var active = !!(model.key
                    && model.key.name === s.name && model.key.value === s.value);
                menu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-recording-timeline-key-menu-item' + (active ? ' active' : ''),
                    title: (s.source === 'header' ? 'Correlation header' : 'Shared payload field')
                        + ' · ' + s.stepCount + ' step' + (s.stepCount === 1 ? '' : 's')
                        + ' · ' + (Array.isArray(s.protocols) ? s.protocols.join(', ') : ''),
                    dataset: { corrName: String(s.name), corrValue: String(s.value) }
                },
                    el('span', { className: 'bowire-recording-timeline-key-menu-mark',
                        textContent: active ? '✓' : '' }),
                    el('span', { className: 'bowire-recording-timeline-key-menu-label',
                        textContent: s.name + ' = ' + s.value }),
                    el('span', { className: 'bowire-recording-timeline-key-menu-meta',
                        textContent: (Array.isArray(s.protocols) ? s.protocols.length : 0) + ' proto · '
                            + s.stepCount + ' steps' })
                ));
            });
            menu.appendChild(el('button', {
                type: 'button',
                className: 'bowire-recording-timeline-key-menu-item',
                title: 'Forget the pinned key and go back to the best automatic guess',
                dataset: { corrAuto: '1' }
            },
                el('span', { className: 'bowire-recording-timeline-key-menu-mark', textContent: '' }),
                el('span', { className: 'bowire-recording-timeline-key-menu-label',
                    textContent: 'Auto — best guess' }),
                el('span', { className: 'bowire-recording-timeline-key-menu-meta', textContent: '' })
            ));
            chipWrap.appendChild(menu);
        }
        bar.appendChild(chipWrap);

        var protoTotal = Array.isArray(model.lanes) ? model.lanes.length : 0;
        var stepTotal = Array.isArray(model.events) ? model.events.length : 0;
        bar.appendChild(el('div', { className: 'bowire-recording-timeline-stats' },
            el('span', { textContent: 'matched ' + (model.matchedStepCount || 0) + '/' + stepTotal + ' steps' }),
            el('span', { textContent: (model.matchedProtocolCount || 0) + '/' + protoTotal + ' protocols' }),
            el('span', { textContent: _fmtDuration(model.spanMs) + ' span' }),
            el('span', {
                title: model.timebase === 'absolute'
                    ? 'capturedAt carries wall-clock timestamps'
                    : 'capturedAt carries offsets from an arbitrary zero — no wall clock to show',
                textContent: model.timebase === 'absolute'
                    ? _fmtClock(model.originMs)
                    : 'relative timebase'
            })
        ));

        bar.appendChild(el('button', {
            type: 'button',
            className: 'bowire-recording-timeline-copy',
            title: 'Copy the raw correlation model as JSON (same shape as `bowire recording correlate --json`)',
            onClick: _correlationCopyJson
        }, el('span', { textContent: 'Copy JSON' })));

        return bar;
    }

    // MUTATOR-ish (clipboard) — click path only, and it re-reads the
    // live model rather than the one captured at render time.
    function _correlationCopyJson(e) {
        e.stopPropagation();
        var rec = currentCorrelationRecording();
        var live = rec && correlationModelFor(rec);
        if (!live || live.error) return;
        if (!navigator.clipboard || !navigator.clipboard.writeText) return;
        navigator.clipboard.writeText(JSON.stringify(live, null, 2)).then(function () {
            if (typeof toast === 'function') toast('Correlation model copied', 'success');
        }).catch(function () {
            if (typeof toast === 'function') toast('Clipboard blocked by the browser', 'error');
        });
    }

    function _correlationKeyMenuClick(e) {
        var item = e.target && e.target.closest
            ? e.target.closest('.bowire-recording-timeline-key-menu-item')
            : null;
        if (!item) return;
        e.stopPropagation();
        var rec = currentCorrelationRecording();
        if (!rec) return;
        if (item.dataset.corrAuto === '1') {
            setRecordingCorrelationKey(rec.id, null, null);
            return;
        }
        setRecordingCorrelationKey(rec.id, item.dataset.corrName, item.dataset.corrValue);
    }

    function renderCorrelationLane(lane, model) {
        var span = Math.max(1, model.spanMs || 1);
        var row = el('div', { className: 'bowire-recording-lane' });
        row.appendChild(el('div', { className: 'bowire-recording-lane-label' },
            el('span', { className: 'bowire-recording-step-protocol', textContent: lane.protocol }),
            el('span', {
                className: 'bowire-recording-lane-count',
                textContent: lane.matchedCount + '/' + lane.stepCount
            })
        ));

        var track = el('div', { className: 'bowire-recording-lane-track' });
        var events = (Array.isArray(model.events) ? model.events : []).filter(function (e) {
            return e.protocol === lane.protocol;
        });
        events.forEach(function (ev) {
            var left = (ev.offsetMs / span) * 100;
            var width = (Math.max(1, ev.durationMs) / span) * 100;
            if (left + width > 100) width = Math.max(0.4, 100 - left);
            var failed = _correlationEventFailed(ev);
            var bar = el('div', {
                className: 'bowire-recording-bar is-' + (ev.match || 'none')
                    + (failed ? ' is-fail' : '')
                    + (ev.stepId === recordingCorrelationSelectedStepId ? ' is-selected' : ''),
                style: 'left:' + left.toFixed(3) + '%;width:' + width.toFixed(3) + '%',
                title: _correlationEventTooltip(ev, model),
                dataset: { stepId: ev.stepId }
            });
            track.appendChild(bar);

            var frames = Array.isArray(ev.frames) ? ev.frames : [];
            // Thin the ticks on very chatty streams — past a couple of
            // hundred they stop being readable and start being a
            // rendering cost. The model still carries every frame; the
            // server pushes a warning when this kicks in.
            var stride = frames.length > 200 ? Math.ceil(frames.length / 200) : 1;
            for (var i = 0; i < frames.length; i += stride) {
                var f = frames[i];
                var fLeft = (f.offsetMs / span) * 100;
                track.appendChild(el('div', {
                    className: 'bowire-recording-frame-tick is-' + (f.match || 'none'),
                    style: 'left:' + Math.min(100, fLeft).toFixed(3) + '%',
                    title: 'frame ' + f.index + (f.label ? ' · ' + f.label : '')
                        + ' · +' + _fmtOffset(f.offsetMs)
                        + ' · ' + _matchWord(f.match),
                    dataset: { stepId: ev.stepId, frameIndex: String(f.index) }
                }));
            }
        });
        row.appendChild(track);
        return row;
    }

    function _correlationEventFailed(ev) {
        var s = String(ev.status || '').toUpperCase();
        if (!s) return false;
        if (s === 'OK' || s === '200' || s === '201' || s === '202' || s === '204') return false;
        // Numeric HTTP status: 4xx / 5xx are failures, everything else
        // (1xx / 2xx / 3xx) is not.
        var n = parseInt(s, 10);
        if (!isNaN(n)) return n >= 400;
        return true;
    }

    function _correlationEventTooltip(ev, model) {
        var parts = [ev.protocol + ' · ' + (ev.service || '') + ' / ' + (ev.method || '')];
        parts.push('+' + _fmtOffset(ev.offsetMs) + ' · ' + _fmtDuration(ev.durationMs));
        parts.push('status ' + (ev.status || '?'));
        parts.push(model.key
            ? (_matchWord(ev.match) + ' match on ' + model.key.name)
            : 'no correlation key');
        return parts.join('\n');
    }

    function _matchWord(match) {
        if (match === 'strong') return 'strong';
        if (match === 'weak') return 'weak';
        return 'no';
    }

    function _renderCorrelationAxis(model) {
        var axis = el('div', { className: 'bowire-recording-timeline-axis' });
        var span = Math.max(1, model.spanMs || 1);
        for (var i = 0; i <= 4; i++) {
            var pct = i * 25;
            axis.appendChild(el('div', {
                className: 'bowire-recording-timeline-axis-tick',
                style: 'left:' + pct + '%',
                textContent: '+' + _fmtOffset(Math.round((span * pct) / 100))
            }));
        }
        return axis;
    }

    function _renderCorrelationLegend() {
        function swatch(cls, label, hint) {
            return el('span', { className: 'bowire-recording-timeline-legend-item', title: hint },
                el('span', { className: 'bowire-recording-timeline-legend-swatch ' + cls }),
                el('span', { textContent: label })
            );
        }
        return el('div', { className: 'bowire-recording-timeline-legend' },
            swatch('is-strong', 'strong',
                'The key’s own name and value were both found in this step’s payload.'),
            swatch('is-weak', 'weak',
                'The value turned up on some other id-shaped field. Low-cardinality ids collide, so this tier stays visibly separate.'),
            swatch('is-none', 'unmatched',
                'The key does not appear in this step at all.')
        );
    }

    function _renderCorrelationInspect(model) {
        if (!recordingCorrelationSelectedStepId) return null;
        var events = Array.isArray(model.events) ? model.events : [];
        var ev = events.find(function (e) { return e.stepId === recordingCorrelationSelectedStepId; });
        if (!ev) return null;

        var frame = null;
        if (recordingCorrelationSelectedFrame !== null && Array.isArray(ev.frames)) {
            frame = ev.frames.find(function (f) {
                return String(f.index) === String(recordingCorrelationSelectedFrame);
            }) || null;
        }

        function field(label, value) {
            return el('div', { className: 'bowire-recording-timeline-inspect-field' },
                el('span', { className: 'bowire-recording-timeline-inspect-label', textContent: label }),
                el('span', { className: 'bowire-recording-timeline-inspect-value', textContent: value })
            );
        }

        var panel = el('div', { className: 'bowire-recording-timeline-inspect' },
            el('div', { className: 'bowire-recording-timeline-inspect-head' },
                el('span', { className: 'bowire-recording-step-protocol', textContent: ev.protocol }),
                el('span', {
                    className: 'bowire-recording-timeline-inspect-title',
                    textContent: 'Step ' + (ev.stepIndex + 1) + ' — ' + (ev.service || '') + ' / ' + (ev.method || '')
                }),
                el('button', {
                    type: 'button',
                    className: 'bowire-recording-timeline-inspect-close',
                    title: 'Close',
                    'aria-label': 'Close step details',
                    innerHTML: svgIcon('close'),
                    onClick: function (e) {
                        e.stopPropagation();
                        recordingCorrelationSelectedStepId = null;
                        recordingCorrelationSelectedFrame = null;
                        render();
                    }
                })
            ),
            field('offset', '+' + _fmtOffset(ev.offsetMs)),
            field('duration', _fmtDuration(ev.durationMs)),
            field('type', ev.methodType || 'Unary'),
            field('status', ev.status || '?'),
            field('match', model.key
                ? (_matchWord(ev.match) + ' on ' + model.key.name + ' = ' + model.key.value)
                : 'no correlation key resolved')
        );
        if (Array.isArray(ev.frames) && ev.frames.length > 0) {
            panel.appendChild(field('frames', String(ev.frames.length)));
        }
        if (frame) {
            panel.appendChild(field('frame ' + frame.index,
                '+' + _fmtOffset(frame.offsetMs) + ' · ' + _matchWord(frame.match) + ' match'
                + (frame.label ? ' · ' + frame.label : '')));
        }
        return panel;
    }

    // The single delegated handler for every bar + tick. Resolves its
    // target from data-attributes at CLICK time, and the recording from
    // module-scope state — morphdom preserves these nodes across
    // re-renders, so anything captured in a closure here would be stale
    // the moment the operator switched recordings.
    function _correlationLaneClick(e) {
        // Any click in the lanes also dismisses the key picker. The
        // listener lives here rather than on the timeline root because
        // the root node is shared with the "not built yet" / "Retry"
        // branches — morphdom would preserve whichever version rendered
        // first and silently drop the other's listener.
        var menuWasOpen = recordingCorrelationKeyMenuOpen;
        recordingCorrelationKeyMenuOpen = false;

        var node = e.target && e.target.closest ? e.target.closest('[data-step-id]') : null;
        var stepId = node && node.dataset.stepId;
        if (!stepId) {
            if (menuWasOpen) render();
            return;
        }
        var frameIndex = node.dataset.frameIndex;
        if (recordingCorrelationSelectedStepId === stepId
            && String(recordingCorrelationSelectedFrame) === String(frameIndex === undefined ? null : frameIndex)) {
            recordingCorrelationSelectedStepId = null;
            recordingCorrelationSelectedFrame = null;
        } else {
            recordingCorrelationSelectedStepId = stepId;
            recordingCorrelationSelectedFrame = (frameIndex === undefined) ? null : frameIndex;
        }
        render();
    }

    function _fmtOffset(ms) {
        var n = Number(ms) || 0;
        if (n < 1000) return Math.round(n) + ' ms';
        if (n < 60000) return (n / 1000).toFixed(n < 10000 ? 2 : 1) + ' s';
        return Math.floor(n / 60000) + ' min ' + Math.round((n % 60000) / 1000) + ' s';
    }

    function _fmtDuration(ms) {
        var n = Number(ms) || 0;
        return n < 1000 ? (Math.round(n) + ' ms') : ((n / 1000).toFixed(n < 10000 ? 2 : 1) + ' s');
    }

    function _fmtClock(originMs) {
        try { return new Date(Number(originMs)).toLocaleTimeString(); }
        catch { return 'absolute timebase'; }
    }

    // ---- Import a .bwr from disk ----
    // The Recordings rail could only ever import HAR, which meant a
    // shared .bwr (the format the CLI, the mock server and every
    // "Export → JSON" produce) was openable everywhere except the
    // workbench that writes it.
    // MUTATOR — click path only.
    function importRecordingFromFile() {
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = '.bwr,.json,application/json';
        input.onchange = function () {
            var file = input.files && input.files[0];
            if (!file) return;
            var reader = new FileReader();
            reader.onload = function () {
                var imported;
                try { imported = _parseRecordingEnvelope(String(reader.result)); }
                catch (err) {
                    if (typeof toast === 'function') {
                        toast('Could not read "' + file.name + '": ' + (err && err.message ? err.message : 'invalid JSON'), 'error');
                    }
                    return;
                }
                if (imported.length === 0) {
                    if (typeof toast === 'function') toast('No recordings found in "' + file.name + '"', 'error');
                    return;
                }
                var firstId = null;
                imported.forEach(function (rec) {
                    rec.id = nextRecordingId();
                    if (!rec.name) rec.name = file.name.replace(/\.(bwr|json)$/i, '');
                    if (!rec.createdAt) rec.createdAt = Date.now();
                    recordingsList.push(rec);
                    if (!firstId) firstId = rec.id;
                });
                persistRecordings();
                recordingManagerSelectedId = firstId;
                if (typeof toast === 'function') {
                    toast('Imported ' + imported.length + ' recording'
                        + (imported.length === 1 ? '' : 's') + ' from "' + file.name + '"', 'success');
                }
                render();
            };
            reader.readAsText(file);
        };
        input.click();
    }

    // Both envelopes the .bwr loader accepts: store-wrapped
    // ({recordings:[…]}) and single-recording-at-root ({id,name,steps}).
    // Same detection rule as RecordingLoader on the C# side.
    function _parseRecordingEnvelope(text) {
        var parsed = JSON.parse(text);
        var list = [];
        if (parsed && Array.isArray(parsed.recordings)) list = parsed.recordings;
        else if (parsed && Array.isArray(parsed.steps)) list = [parsed];
        return list.filter(function (r) { return r && Array.isArray(r.steps); });
    }
