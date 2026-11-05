    // ---- Freeform request: streaming and channel calls (#995) ----
    // The classic freeform form offered a method type for every protocol but
    // always called /api/invoke, so a server-streaming or duplex call typed
    // in by hand ran as unary. A server-streaming call now reads
    // /api/invoke/stream; a client-streaming or duplex call opens a channel.
    // Neither endpoint needs the method to have been discovered.
    //
    // State lives here, not on the per-method state the discovered path
    // uses: a freeform call has no service or method object behind it.

    var freeformCall = {
        mode: null,        // null | 'stream' | 'channel'
        state: 'idle',     // 'idle' | 'running' | 'open' | 'done' | 'error'
        frames: [],        // [{ dir: 'recv'|'send', data, index }]
        error: null,
        source: null,      // EventSource of the stream or of the channel's responses
        channelId: null
    };

    /** How a method type is called: 'unary', 'stream' or 'channel'. */
    function freeformCallMode(methodType) {
        switch (methodType) {
            case 'ServerStreaming': return 'stream';
            case 'ClientStreaming':
            case 'Duplex':          return 'channel';
            default:                return 'unary';
        }
    }

    /** Metadata of the freeform request, or null when there is none. */
    function _freeformMetadata(fr) {
        return (fr.metadata && typeof fr.metadata === 'object' && Object.keys(fr.metadata).length > 0)
            ? fr.metadata : null;
    }

    /** The /api/invoke/stream URL for a freeform server-streaming call. */
    function buildFreeformStreamUrl(prefix, fr) {
        var metadata = _freeformMetadata(fr);
        return prefix + '/api/invoke/stream'
            + '?service=' + encodeURIComponent(fr.service || '')
            + '&method=' + encodeURIComponent(fr.method || '')
            + '&messages=' + encodeURIComponent(JSON.stringify([fr.body || '{}']))
            + (fr.protocol ? '&protocol=' + encodeURIComponent(fr.protocol) : '')
            + (metadata ? '&metadata=' + encodeURIComponent(JSON.stringify(metadata)) : '')
            + (fr.serverUrl ? '&serverUrl=' + encodeURIComponent(fr.serverUrl) : '');
    }

    /** The /api/channel/open body for a freeform client-streaming or duplex call. */
    function buildFreeformChannelOpenBody(fr) {
        return {
            service: fr.service,
            method: fr.method,
            protocol: fr.protocol || undefined,
            metadata: _freeformMetadata(fr) || undefined,
            // #1002 - the plugin cannot always look the method up.
            methodType: fr.methodType || undefined
        };
    }

    /** A received SSE frame as the response pane lists it. */
    function _freeformFrame(raw) {
        try {
            var parsed = JSON.parse(raw);
            if (parsed && parsed.error && parsed.error.message) freeformCall.error = parsed.error.message;
            return {
                dir: 'recv',
                index: typeof parsed.index === 'number' ? parsed.index : freeformCall.frames.length,
                data: parsed && parsed.data !== undefined ? parsed.data : raw
            };
        } catch {
            return { dir: 'recv', index: freeformCall.frames.length, data: raw };
        }
    }

    function _resetFreeformCall(mode) {
        freeformCall.mode = mode;
        freeformCall.state = mode ? 'running' : 'idle';
        freeformCall.frames = [];
        freeformCall.error = null;
        freeformCall.source = null;
        freeformCall.channelId = null;
    }

    /**
     * Follow the frames of `source` until its `done` event. `onDone`
     * runs once, whether the stream ended or failed.
     */
    function _followFreeformSource(source, onDone) {
        freeformCall.source = source;
        source.onmessage = function (event) {
            freeformCall.frames.push(_freeformFrame(event.data));
            render();
        };
        source.addEventListener('done', function () {
            source.close();
            if (freeformCall.source === source) freeformCall.source = null;
            onDone(freeformCall.error ? 'error' : 'done');
        });
        // Both a dropped connection and the server's `event: error` frame
        // arrive here; only the frame carries data. EventSource would
        // reconnect on its own, and a call that has ended must not be
        // opened again, so either ends the call.
        source.onerror = function (event) {
            source.close();
            if (freeformCall.source !== source) return;
            freeformCall.source = null;
            if (event && event.data) {
                try { freeformCall.error = JSON.parse(event.data).error || event.data; }
                catch { freeformCall.error = event.data; }
            }
            if (!freeformCall.error) freeformCall.error = t('rb.response.failed');
            onDone('error');
        };
    }

    /** Start a server-streaming freeform call. */
    function startFreeformStream(fr) {
        stopFreeformCall();
        _resetFreeformCall('stream');
        var source = new EventSource(buildFreeformStreamUrl(config.prefix, fr));
        _followFreeformSource(source, function (state) {
            freeformCall.state = state;
            render();
        });
        render();
    }

    /** Open a client-streaming or duplex channel for a freeform call. */
    async function openFreeformChannel(fr) {
        stopFreeformCall();
        _resetFreeformCall('channel');
        render();
        try {
            var url = config.prefix + '/api/channel/open'
                + (fr.serverUrl ? '?serverUrl=' + encodeURIComponent(fr.serverUrl) : '');
            var resp = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(buildFreeformChannelOpenBody(fr))
            });
            var result = await resp.json();
            if (!resp.ok || !result.channelId) {
                freeformCall.state = 'error';
                freeformCall.error = (result && (result.detail || result.title)) || t('rb.response.failed');
                toast(t('channel.openFailed', { reason: freeformCall.error }), 'error');
                render();
                return;
            }
            freeformCall.channelId = result.channelId;
            freeformCall.state = 'open';
            _followFreeformSource(
                new EventSource(config.prefix + '/api/channel/' + encodeURIComponent(result.channelId) + '/responses'),
                function (state) {
                    freeformCall.state = state;
                    freeformCall.channelId = null;
                    render();
                });
            render();
        } catch (e) {
            freeformCall.state = 'error';
            freeformCall.error = String((e && e.message) || e);
            toast(t('channel.openFailed', { reason: freeformCall.error }), 'error');
            render();
        }
    }

    /** Send one message on the open freeform channel. */
    async function sendFreeformChannel(message) {
        if (!freeformCall.channelId) return;
        freeformCall.frames.push({ dir: 'send', index: freeformCall.frames.length, data: message });
        render();
        try {
            var resp = await fetch(config.prefix + '/api/channel/' + encodeURIComponent(freeformCall.channelId) + '/send', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: message })
            });
            if (!resp.ok) toast(t('channel.sendFailed', { reason: resp.status }), 'error');
        } catch (e) {
            toast(t('channel.sendFailed', { reason: String((e && e.message) || e) }), 'error');
        }
    }

    /** End the running freeform call, if any: close the stream or the channel. */
    function stopFreeformCall() {
        if (freeformCall.source) {
            freeformCall.source.close();
            freeformCall.source = null;
        }
        if (freeformCall.channelId) {
            var id = freeformCall.channelId;
            freeformCall.channelId = null;
            fetch(config.prefix + '/api/channel/' + encodeURIComponent(id) + '/close', { method: 'POST' })
                .catch(function () { /* closing the response stream above already ends the channel on the server */ });
        }
        if (freeformCall.state === 'running' || freeformCall.state === 'open') freeformCall.state = 'done';
    }

    /** True while a freeform stream runs or a freeform channel is open. */
    function freeformCallActive() {
        return freeformCall.state === 'running' || freeformCall.state === 'open';
    }

    /** The execute button's label for the freeform request's call mode and state. */
    function freeformExecuteLabel(fr) {
        var mode = (fr && fr.protocol !== 'rest') ? freeformCallMode(fr.methodType) : 'unary';
        if (mode === 'stream' && freeformCall.mode === 'stream' && freeformCallActive()) return t('freeform.stop');
        if (mode === 'channel') {
            return (freeformCall.mode === 'channel' && freeformCall.state === 'open')
                ? t('channel.send') : t('channel.connect');
        }
        return t('main.execute');
    }

    /** The response pane of a freeform stream or channel: state, error, frames in arrival order. */
    function renderFreeformFrames() {
        var wrap = el('div', { className: 'bowire-freeform-frames', id: 'bowire-freeform-frames' });
        var stateKey = {
            running: 'stream.state.receiving',
            open: 'channel.connected',
            done: 'stream.state.ended',
            error: 'stream.state.error'
        }[freeformCall.state] || 'stream.state.idle';
        wrap.appendChild(el('div', { className: 'bowire-freeform-frames-state', textContent: t(stateKey) }));
        if (freeformCall.error) {
            wrap.appendChild(el('div', { className: 'bowire-response-output error', textContent: String(freeformCall.error) }));
        }
        freeformCall.frames.forEach(function (frame) {
            var row = el('div', { className: 'bowire-freeform-frame is-' + frame.dir });
            row.appendChild(el('span', { className: 'bowire-freeform-frame-dir', textContent: frame.dir === 'send' ? '↑' : '↓' }));
            var body = el('div', { className: 'bowire-response-output is-interactive' });
            var text = typeof frame.data === 'string' ? frame.data : JSON.stringify(frame.data, null, 2);
            body.innerHTML = highlightJsonInteractive(text);
            row.appendChild(body);
            wrap.appendChild(row);
        });
        return wrap;
    }
