    // ---- Response Handoff ("and now what?") ----
    // #536 — every follow-up the workbench offers for a response already
    // existed as a callable function; what was missing was a path FROM
    // the response. This fragment owns exactly one affordance — a
    // "Use this…" button in the response action cluster — and the
    // context menu behind it, with four handoffs:
    //
    //   Save as mock          → Recordings (capture) + Mock (boot a host)
    //   Add to flow…          → Flows      (addRequestToFlowPicker)
    //   Keep as test          → core        (setTestsFor)
    //   Add to benchmark…     → Benchmarking (addTargetToEnvelopePicker)
    //
    // Three of those four live in OPTIONAL packages, so every symbol
    // crossing that boundary is probed with `typeof` and never
    // referenced bare — an unguarded ReferenceError inside render()
    // blanks the whole workbench (render() has no try/catch).
    //
    // Nothing here declares fragment-scope `let`/`const`: cross-fragment
    // bindings do not hoist across the concat, and this design needs no
    // shared state because every value is re-resolved at click time.
    // That is also the morphdom contract — the trigger button survives
    // re-render, so a closure captured at render time would act on a
    // stale method after a context switch.

    /**
     * True when the last call on `surface` produced something worth
     * handing off. Pure — no mutation, safe to call from the render
     * path (bowireRenderHandoffButton does exactly that).
     *
     * surface: 'discover' (the schema-driven response pane) or
     * 'builder' (the Compose request-builder's response viewer).
     */
    function bowireLastCallSucceeded(surface) {
        if (typeof isExecuting !== 'undefined' && isExecuting) return false;
        if (typeof responseError !== 'undefined' && responseError) return false;
        var hasBody = (typeof responseData !== 'undefined' && responseData !== null && responseData !== '')
            || (typeof streamMessages !== 'undefined'
                && Array.isArray(streamMessages) && streamMessages.length > 0);
        if (!hasBody) return false;
        if (surface === 'builder') {
            // Both builder execute paths (executeFreeformRequest and
            // executeHoppRequest) declare a LOCAL `statusInfo` that
            // shadows the module-scope one, so the module binding is
            // stale on this surface and must not be consulted. A
            // non-null responseData with no responseError IS the
            // builder's success signal — it is the same condition
            // _renderHoppResponse uses to render the viewer at all.
            return true;
        }
        if (typeof statusInfo === 'undefined' || !statusInfo) return false;
        var st = String(statusInfo.status);
        return st !== 'Error' && st !== 'NetworkError';
    }

    /**
     * Which handoffs the current host can actually perform. Only
     * `typeof` probes — never a bare reference to an optional package's
     * symbol. Pure; called from the click path only, but safe anywhere.
     */
    function bowireHandoffOffers() {
        return {
            // Recordings package — same probe saveFreeformAsMockStep uses.
            mock: typeof isRecording === 'function' && typeof startRecording === 'function',
            // Mock package — the window shim installed by mocks.js.
            mockHost: typeof window !== 'undefined'
                && !!window.__bowireMocks
                && typeof window.__bowireMocks.startFromRecording === 'function',
            // Flows package.
            flow: typeof addRequestToFlowPicker === 'function',
            // Core (test-assertions.js) — probed anyway so the item
            // shape stays uniform with its optional neighbours.
            test: typeof setTestsFor === 'function' && typeof getTestsFor === 'function',
            // Benchmarking package.
            benchmark: typeof addTargetToEnvelopePicker === 'function'
        };
    }

    /**
     * Snapshot the LIVE Discover request state.
     *
     * MUTATOR — calls syncFormToJson(), which writes requestMessages.
     * Call it ONLY from a click handler; calling it on the render path
     * would read stale form values and overwrite state the caller just
     * set. Lifted out of the "+ Add to…" header menu closure (#296) so
     * the response side can reuse the identical shape instead of
     * growing a second, divergent snapshot.
     */
    function bowireSnapshotDiscoverRequest() {
        var liveSvc = (typeof selectedService !== 'undefined') ? selectedService : null;
        var liveMth = (typeof selectedMethod !== 'undefined') ? selectedMethod : null;
        if (!liveSvc || !liveMth) return null;
        // If the user was editing on the Form sub-tab, their changes
        // live in formValues — flush them into requestMessages[0]
        // before snapshotting so the saved preset / collection /
        // benchmark carries the real edited body, not the empty {}
        // that the editor started with.
        try {
            if (typeof syncFormToJson === 'function'
                    && typeof requestInputMode !== 'undefined'
                    && requestInputMode === 'form') {
                syncFormToJson();
            }
        } catch { /* schema-form not loaded */ }
        var body = (Array.isArray(requestMessages) && requestMessages[0]) || '{}';
        var meta = {};
        var metaRows = document.querySelectorAll('.bowire-metadata-row');
        for (var mi = 0; mi < metaRows.length; mi++) {
            var inputs = metaRows[mi].querySelectorAll('.bowire-metadata-input');
            if (inputs.length === 2 && inputs[0].value.trim()) {
                meta[inputs[0].value.trim()] = inputs[1].value;
            }
        }
        return {
            service: liveSvc.name,
            method: liveMth.name,
            methodType: liveMth.methodType || 'Unary',
            protocol: liveSvc.source || selectedProtocol || 'grpc',
            body: body,
            messages: Array.isArray(requestMessages) ? requestMessages.slice() : [body],
            metadata: Object.keys(meta).length > 0 ? meta : null,
            serverUrl: liveSvc.originUrl || (Array.isArray(serverUrls) && serverUrls[0]) || null
        };
    }

    /**
     * Compose-builder requests carry no RPC identity — /api/invoke is
     * called with service:'' and method:'<HTTP verb>'. Assertions and
     * mock steps still need a stable (service, method) key, so fall
     * back to the request URL's host+path. When the operator bound the
     * tab to a discovered method ("Save as method tab"), that canonical
     * identity wins.
     */
    function bowireBuilderIdentity(fr) {
        var svc = fr.discoveredService || fr.service || '';
        var mth = fr.discoveredMethod || fr.method || '';
        if (!svc) {
            var raw = String(fr.serverUrl || '').trim();
            if (raw) {
                try {
                    var parsed = new URL(raw, window.location.href);
                    svc = parsed.host + parsed.pathname;
                } catch { svc = raw; }
            }
        }
        return { service: svc, method: mth };
    }

    /**
     * The one snapshot every handoff consumes. MUTATOR on the discover
     * surface (see bowireSnapshotDiscoverRequest) — click path only.
     * Returns null when there is nothing to hand off.
     */
    function bowireHandoffSnapshot(surface) {
        if (surface === 'builder') {
            var fr = (typeof freeformRequest !== 'undefined') ? freeformRequest : null;
            if (!fr) return null;
            var ident = bowireBuilderIdentity(fr);
            var rbMeta = (fr._requestBuilder && fr._requestBuilder.lastResponseMeta) || null;
            return {
                surface: 'builder',
                service: ident.service,
                method: ident.method,
                methodType: fr.methodType || 'Unary',
                protocol: fr.protocol || 'rest',
                body: fr.body || '{}',
                messages: [fr.body || '{}'],
                metadata: (fr.metadata && Object.keys(fr.metadata).length > 0) ? fr.metadata : null,
                serverUrl: fr.serverUrl || null,
                response: (typeof responseData !== 'undefined') ? responseData : null,
                status: rbMeta && rbMeta.status != null
                    ? rbMeta.status
                    : ((typeof statusInfo !== 'undefined' && statusInfo) ? statusInfo.status : 'OK'),
                durationMs: rbMeta && rbMeta.durationMs != null ? rbMeta.durationMs : 0,
                httpPath: null,
                httpVerb: null,
                schemaDescriptor: null
            };
        }
        var snap = bowireSnapshotDiscoverRequest();
        if (!snap) return null;
        snap.surface = 'discover';
        // Response-side fields mirror what api.js already feeds
        // bowireCaptureStep after a successful invoke, field for field.
        snap.response = (typeof responseData !== 'undefined') ? responseData : null;
        snap.status = (typeof statusInfo !== 'undefined' && statusInfo) ? statusInfo.status : 'OK';
        snap.durationMs = (typeof statusInfo !== 'undefined' && statusInfo) ? statusInfo.durationMs : 0;
        snap.httpPath = (typeof selectedMethod !== 'undefined' && selectedMethod)
            ? (selectedMethod.httpPath || null) : null;
        snap.httpVerb = (typeof selectedMethod !== 'undefined' && selectedMethod)
            ? (selectedMethod.httpMethod || null) : null;
        snap.schemaDescriptor = (typeof selectedService !== 'undefined' && selectedService)
            ? (selectedService.schemaDescriptor || null) : null;
        return snap;
    }

    /**
     * The single owner of the "is Recordings present → start one if
     * needed → capture" rule. Both the freeform pane's "Save as Mock
     * Step" button and the response handoff route through here, so the
     * two can't drift.
     *
     * Returns true only when the step actually landed. startRecording
     * bails (with its own toast) when no workspace is active, so the
     * post-start re-check is load-bearing: without it the caller would
     * report a mock that was never captured.
     */
    function bowireEnsureRecordingAndCapture(step, recordingName) {
        if (typeof isRecording !== 'function' || typeof startRecording !== 'function') {
            toast('Capturing mocks needs the Recordings package (Kuestenlogik.Bowire.Recordings).', 'error');
            return false;
        }
        if (!isRecording()) {
            // startRecording already push+persist+set-active; it also
            // renders, but callers still render afterwards so the
            // captured step list shows up.
            startRecording(recordingName || 'Manual mocks');
        }
        if (!isRecording()) return false;
        bowireCaptureStep(step);
        return true;
    }

    /**
     * Build the recording-step payload for a snapshot. The builder
     * surface delegates to bowireFreeformStepPayload so the REST
     * "GET /path" parsing lives in exactly one place.
     */
    function bowireHandoffStepFromSnapshot(snap) {
        if (snap.surface === 'builder'
                && typeof bowireFreeformStepPayload === 'function'
                && typeof freeformRequest !== 'undefined' && freeformRequest) {
            return bowireFreeformStepPayload(freeformRequest, {
                response: snap.response,
                responseBinary: null,
                status: snap.status || 'OK',
                durationMs: snap.durationMs || 0
            });
        }
        return {
            protocol: snap.protocol,
            service: snap.service,
            method: snap.method,
            methodType: snap.methodType || 'Unary',
            serverUrl: snap.serverUrl || null,
            body: snap.body || '{}',
            messages: Array.isArray(snap.messages) ? snap.messages.slice() : [snap.body || '{}'],
            metadata: snap.metadata || null,
            status: snap.status || 'OK',
            durationMs: snap.durationMs || 0,
            response: snap.response,
            responseBinary: null,
            schemaDescriptor: snap.schemaDescriptor || null,
            httpPath: snap.httpPath || null,
            httpVerb: snap.httpVerb || null
        };
    }

    /** Freeze the response into a recording step, then boot a mock host if one is available. */
    function bowireHandoffToMock(snap) {
        var step = bowireHandoffStepFromSnapshot(snap);
        var label = snap.method || snap.service || 'response';
        if (!bowireEnsureRecordingAndCapture(step, 'Mock: ' + label)) return;
        var mocks = (typeof window !== 'undefined') ? window.__bowireMocks : null;
        if (!mocks || typeof mocks.startFromRecording !== 'function') {
            toast('Captured as a mock step — add Kuestenlogik.Bowire.Mock to boot it as a host', 'info');
            render();
            return;
        }
        var rec = (Array.isArray(recordingsList))
            ? recordingsList.find(function (r) { return r.id === recordingActiveId; })
            : null;
        if (!rec) {
            toast('Captured as a mock step', 'success');
            render();
            return;
        }
        mocks.startFromRecording(rec, 0).then(function () {
            if (typeof mocks.open === 'function') mocks.open();
        }).catch(function () { /* startFromRecording already toasted */ });
    }

    /** Hand the request to the Flows package's picker (new flow / append to an existing one). */
    function bowireHandoffToFlow(snap, clientX, clientY) {
        if (typeof addRequestToFlowPicker !== 'function') return;
        // Node shape mirrors convertCollectionToFlow's — note flows use
        // `value` on assertions where test-assertions.js uses `expected`.
        addRequestToFlowPicker(clientX, clientY, {
            type: 'request',
            protocol: snap.protocol || 'rest',
            service: snap.service || '',
            method: snap.method || '',
            methodType: snap.methodType || 'Unary',
            serverUrl: snap.serverUrl || '',
            body: snap.body || '{}',
            metadata: snap.metadata || {},
            serviceMethodMode: 'custom',
            assertions: snap.status
                ? [{ path: 'status', op: 'eq', value: String(snap.status) }]
                : []
        }, { name: (snap.service ? snap.service + '.' : '') + (snap.method || 'request') });
    }

    /**
     * Derive the assertion set for a snapshot: the status, plus the
     * response body's top-level scalar fields.
     *
     * NOT a `{ path: 'response', op: 'eq', expected: <whole body> }`
     * pair (the shape convertRecordingToTests uses): the assertion
     * engine hands `evaluateAssertion` the PARSED body for the
     * `response` path, and deepEqualsLoose compares an object against a
     * string via String(a) === String(b), i.e. "[object Object]" —
     * which fails against the very response it was derived from.
     * Verified in the browser before this shape was chosen.
     *
     * Field-level assertions also say more when they break: "response
     * .code drifted" instead of "the body changed somewhere".
     */
    function bowireHandoffTestsForSnapshot(snap) {
        var tests = [{
            id: nextTestId(),
            path: 'status',
            op: 'eq',
            expected: String(snap.status || 'OK')
        }];
        var body = snap.response;
        if (body === undefined || body === null || body === '') return tests;
        if (typeof body === 'string') {
            try { body = JSON.parse(body); } catch { return tests; }
        }
        if (!body || typeof body !== 'object' || Array.isArray(body)) {
            // Arrays and scalars have no stable field set to pin, and a
            // whole-body equality on a live endpoint is noise. Assert
            // that a body arrives at all and let the operator refine it.
            tests.push({ id: nextTestId(), path: 'response', op: 'exists' });
            return tests;
        }
        // Cap the fan-out: six assertions is already a full screen in
        // the Tests editor, and the operator can add more by hand.
        var keys = Object.keys(body);
        for (var i = 0; i < keys.length && tests.length < 6; i++) {
            var v = body[keys[i]];
            if (v === null || typeof v === 'object') continue;
            tests.push({
                id: nextTestId(),
                path: 'response.' + keys[i],
                op: 'eq',
                expected: String(v)
            });
        }
        if (tests.length === 1) tests.push({ id: nextTestId(), path: 'response', op: 'exists' });
        return tests;
    }

    /** Freeze status (+ body shape) into assertions for this (service, method). */
    function bowireHandoffToTest(snap) {
        if (typeof getTestsFor !== 'function' || typeof setTestsFor !== 'function') return;
        if (!snap.service || !snap.method) return;
        var fresh = bowireHandoffTestsForSnapshot(snap);
        var added = fresh.length;
        var existing = getTestsFor(snap.service, snap.method).concat(fresh);
        setTestsFor(snap.service, snap.method, existing);
        if (snap.surface === 'builder') {
            // The Compose viewer's Tests tab is a pre/post-script
            // placeholder, not an assertion-results surface — name
            // where the assertions landed instead of pretending.
            toast(added + ' assertion' + (added === 1 ? '' : 's') + ' saved for '
                + snap.service + ' · ' + snap.method
                + ' — results show on the Discover response pane', 'success');
        } else {
            if (typeof runAssertions === 'function') {
                runAssertions(snap.service, snap.method, snap.status,
                    (typeof lastResponseJson !== 'undefined') ? lastResponseJson : null);
            }
            if (typeof activeResponseTab !== 'undefined') activeResponseTab = 'tests';
            toast(added + ' assertion' + (added === 1 ? '' : 's') + ' saved', 'success');
        }
        render();
    }

    /** Hand the request to Benchmarking's envelope picker. */
    function bowireHandoffToBenchmark(snap, clientX, clientY) {
        if (typeof addTargetToEnvelopePicker !== 'function') return;
        addTargetToEnvelopePicker(clientX, clientY, {
            type: 'method',
            service: snap.service,
            method: snap.method,
            protocol: snap.protocol,
            body: snap.body,
            metadata: snap.metadata || {},
            serverUrl: null
        }, { name: (snap.service ? snap.service + '.' : '') + (snap.method || 'request') });
    }

    /**
     * Open the handoff menu. EVERYTHING is re-resolved here, at click
     * time — the trigger button is preserved across renders by
     * morphdom, so a closure captured at render time would hand off a
     * stale method after a context switch.
     *
     * Unavailable handoffs stay VISIBLE and disabled with a title
     * naming the missing package (same convention as the disabled
     * envelope item in the "+ Add to…" header menu) — a host on
     * Bundle.Minimal should learn what it is missing, not see a
     * silently shorter menu.
     */
    function bowireShowHandoffMenu(surface, clientX, clientY) {
        if (typeof showContextMenu !== 'function') return;
        var snap = bowireHandoffSnapshot(surface);
        if (!snap) return;
        var offers = bowireHandoffOffers();
        var items = [];

        items.push({
            label: 'Save as mock',
            icon: 'server',
            disabled: !offers.mock,
            title: offers.mock
                ? (offers.mockHost
                    ? 'Freeze this response into a recording step and boot a mock host from it'
                    : 'Freeze this response into a recording step (add Kuestenlogik.Bowire.Mock to boot a host)')
                : 'Needs Kuestenlogik.Bowire.Recordings',
            onClick: function () {
                var s = bowireHandoffSnapshot(surface);
                if (s) bowireHandoffToMock(s);
            }
        });

        items.push({
            label: 'Add to flow…',
            icon: 'flow',
            disabled: !offers.flow,
            title: offers.flow
                ? 'Append this request as a step in a new or existing flow'
                : 'Needs Kuestenlogik.Bowire.Flows',
            onClick: function (ev) {
                var s = bowireHandoffSnapshot(surface);
                if (s) bowireHandoffToFlow(s, ev.clientX, ev.clientY);
            }
        });

        var canTest = offers.test && !!snap.service && !!snap.method;
        items.push({
            label: 'Keep as test',
            icon: 'check',
            disabled: !canTest,
            title: canTest
                ? 'Save this status (and body) as assertions for ' + snap.service + ' · ' + snap.method
                : (offers.test
                    ? 'This request has no service/method identity to key assertions on'
                    : 'Assertions are unavailable in this build'),
            onClick: function () {
                var s = bowireHandoffSnapshot(surface);
                if (s) bowireHandoffToTest(s);
            }
        });

        items.push({ separator: true });

        items.push({
            label: 'Add to benchmark envelope…',
            icon: 'lightning',
            disabled: !offers.benchmark,
            title: offers.benchmark
                ? 'Add this known-good request to a benchmark envelope'
                : 'Needs Kuestenlogik.Bowire.Benchmarking',
            onClick: function (ev) {
                var s = bowireHandoffSnapshot(surface);
                if (s) bowireHandoffToBenchmark(s, ev.clientX, ev.clientY);
            }
        });

        showContextMenu(clientX, clientY, items);
    }

    /**
     * The affordance itself. Returns an empty <span> when the last call
     * did not succeed — the same do-nothing shape the neighbouring
     * Compare button uses when it has fewer than two snapshots, so the
     * action cluster keeps its layout without a visibility mechanism of
     * its own.
     *
     * The click handler captures only the `surface` discriminator (a
     * constant for each mount site) — never entity state. That is the
     * morphdom contract.
     */
    function bowireRenderHandoffButton(surface) {
        if (!bowireLastCallSucceeded(surface)) return el('span');
        var isBuilder = surface === 'builder';
        var btn = el('button', {
            type: 'button',
            id: isBuilder ? 'bowire-builder-handoff-btn' : 'bowire-response-handoff-btn',
            className: (isBuilder ? 'bowire-response-meta-btn' : 'bowire-pane-btn') + ' bowire-handoff-btn',
            title: 'Turn this response into a mock, flow, test or benchmark',
            'aria-haspopup': 'menu',
            onClick: function (e) {
                e.stopPropagation();
                bowireShowHandoffMenu(surface, e.clientX, e.clientY);
            }
        });
        btn.appendChild(el('span', { innerHTML: svgIcon('plus'), className: 'bowire-handoff-btn-icon' }));
        btn.appendChild(el('span', { textContent: 'Use this…' }));
        return btn;
    }
