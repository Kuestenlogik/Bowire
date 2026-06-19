    // ---- Response Diff ----
    // Push each successful response into a per-method FIFO ring of up to
    // MAX_RESPONSE_SNAPSHOTS entries. The diff view reads from this ring
    // and lets the user pick any two snapshots to compare side by side.
    function captureResponseForDiff(serviceName, methodName, body, status, durationMs) {
        if (!serviceName || !methodName || !body) return;
        var key = serviceName + '::' + methodName;
        if (!responseSnapshots[key]) responseSnapshots[key] = [];
        responseSnapshots[key].push({
            timestamp: Date.now(),
            status: status || 'OK',
            durationMs: typeof durationMs === 'number' ? durationMs : 0,
            body: body
        });
        if (responseSnapshots[key].length > MAX_RESPONSE_SNAPSHOTS) {
            responseSnapshots[key].shift();
        }
    }

    function getResponseSnapshots() {
        if (!selectedService || !selectedMethod) return [];
        var key = selectedService.name + '::' + selectedMethod.name;
        return responseSnapshots[key] || [];
    }

    // Build a line-oriented diff between two strings using a small LCS
    // implementation. Returns an array of { type: 'eq'|'add'|'del', text }
    // entries the renderer can colour. Good enough for JSON-as-text diffs;
    // not as fancy as a deep semantic JSON diff, but it handles the common
    // "field changed value" case well after the bodies are pretty-printed.
    function computeLineDiff(oldText, newText) {
        var oldLines = String(oldText || '').split('\n');
        var newLines = String(newText || '').split('\n');
        var n = oldLines.length, m = newLines.length;

        // LCS table
        var lcs = new Array(n + 1);
        for (var i = 0; i <= n; i++) {
            lcs[i] = new Array(m + 1).fill(0);
        }
        for (var i2 = n - 1; i2 >= 0; i2--) {
            for (var j = m - 1; j >= 0; j--) {
                if (oldLines[i2] === newLines[j]) {
                    lcs[i2][j] = lcs[i2 + 1][j + 1] + 1;
                } else {
                    lcs[i2][j] = Math.max(lcs[i2 + 1][j], lcs[i2][j + 1]);
                }
            }
        }

        // Walk the table to emit edits
        var out = [];
        var i3 = 0, j3 = 0;
        while (i3 < n && j3 < m) {
            if (oldLines[i3] === newLines[j3]) {
                out.push({ type: 'eq', text: oldLines[i3] });
                i3++; j3++;
            } else if (lcs[i3 + 1][j3] >= lcs[i3][j3 + 1]) {
                out.push({ type: 'del', text: oldLines[i3] });
                i3++;
            } else {
                out.push({ type: 'add', text: newLines[j3] });
                j3++;
            }
        }
        while (i3 < n) { out.push({ type: 'del', text: oldLines[i3++] }); }
        while (j3 < m) { out.push({ type: 'add', text: newLines[j3++] }); }
        return out;
    }

    // Pretty-print JSON for diffs so structurally identical bodies that
    // differ only by whitespace or key order don't look different. Falls
    // back to the raw text when the body isn't valid JSON.
    function prettyJson(text) {
        try {
            return JSON.stringify(JSON.parse(text), null, 2);
        } catch (e) {
            return text;
        }
    }

    // ---- Performance Tab ----

    function renderPerformanceTab() {
        var body = el('div', { className: 'bowire-pane-body bowire-perf-body' });

        // Config row
        var configRow = el('div', { className: 'bowire-perf-config' });

        var nLabel = el('label', { className: 'bowire-perf-label', textContent: 'Calls' });
        var nInput = el('input', {
            id: 'bowire-perf-calls-input',
            className: 'bowire-perf-input',
            type: 'number',
            min: '1',
            max: '10000',
            value: String(benchmark.config.n),
            disabled: benchmark.running ? 'disabled' : undefined,
            onChange: function (e) {
                var v = parseInt(e.target.value, 10);
                if (!Number.isNaN(v) && v > 0) benchmark.config.n = v;
            }
        });

        var cLabel = el('label', { className: 'bowire-perf-label', textContent: 'Concurrency' });
        var cInput = el('input', {
            id: 'bowire-perf-concurrency-input',
            className: 'bowire-perf-input',
            type: 'number',
            min: '1',
            max: '20',
            value: String(benchmark.config.concurrency),
            disabled: benchmark.running ? 'disabled' : undefined,
            onChange: function (e) {
                var v = parseInt(e.target.value, 10);
                if (!Number.isNaN(v) && v > 0 && v <= 20) benchmark.config.concurrency = v;
            }
        });

        var runBtn;
        if (benchmark.running) {
            runBtn = el('button', {
                id: 'bowire-perf-stop-btn',
                className: 'bowire-perf-stop',
                textContent: 'Stop',
                onClick: stopBenchmark
            });
        } else {
            runBtn = el('button', {
                id: 'bowire-perf-run-btn',
                className: 'bowire-perf-run',
                textContent: 'Run benchmark',
                onClick: function () {
                    var nVal = parseInt(nInput.value, 10) || 100;
                    var cVal = parseInt(cInput.value, 10) || 1;
                    runBenchmark(nVal, cVal);
                }
            });
        }

        configRow.appendChild(el('div', { className: 'bowire-perf-field' }, nLabel, nInput));
        configRow.appendChild(el('div', { className: 'bowire-perf-field' }, cLabel, cInput));
        configRow.appendChild(runBtn);
        body.appendChild(configRow);

        body.appendChild(el('div', { className: 'bowire-perf-hint',
            textContent: 'Repeats the current request body and metadata using the active environment + auth helper. {{runtime.now}}, {{runtime.uuid}} and other system variables regenerate per call.' }));

        // Progress bar
        if (benchmark.running || benchmark.completed > 0) {
            var pct = benchmark.total > 0 ? Math.round((benchmark.completed / benchmark.total) * 100) : 0;
            var progressWrap = el('div', { className: 'bowire-perf-progress-wrap' });
            var progressLabel = el('div', { className: 'bowire-perf-progress-label' },
                el('span', { textContent: benchmark.completed + ' / ' + benchmark.total + ' calls' }),
                el('span', { textContent: pct + '%' })
            );
            var progressBar = el('div', { className: 'bowire-perf-progress' });
            var progressFill = el('div', {
                className: 'bowire-perf-progress-fill' + (benchmark.running ? ' running' : ''),
                style: 'width: ' + pct + '%'
            });
            progressBar.appendChild(progressFill);
            progressWrap.appendChild(progressLabel);
            progressWrap.appendChild(progressBar);
            body.appendChild(progressWrap);
        }

        // Stats grid (after at least one successful call)
        var stats = computeBenchmarkStats();
        if (stats) {
            var grid = el('div', { className: 'bowire-perf-stats' });
            var statCells = [
                ['min',        formatMs(stats.min)],
                ['avg',        formatMs(stats.avg)],
                ['p50',        formatMs(stats.p50)],
                ['p90',        formatMs(stats.p90)],
                ['p95',        formatMs(stats.p95)],
                ['p99',        formatMs(stats.p99)],
                ['max',        formatMs(stats.max)],
                ['throughput', stats.throughput.toFixed(1) + ' req/s'],
                ['success',    String(benchmark.success)],
                ['failed',     String(benchmark.failure)],
                ['total',      stats.totalSeconds.toFixed(2) + ' s'],
                ['count',      String(stats.count)]
            ];
            for (var s = 0; s < statCells.length; s++) {
                grid.appendChild(el('div', { className: 'bowire-perf-stat' },
                    el('div', { className: 'bowire-perf-stat-label', textContent: statCells[s][0] }),
                    el('div', { className: 'bowire-perf-stat-value', textContent: statCells[s][1] })
                ));
            }
            body.appendChild(grid);

            // Status code distribution
            if (Object.keys(benchmark.statusCounts).length > 0) {
                var dist = el('div', { className: 'bowire-perf-statuses' });
                dist.appendChild(el('div', { className: 'bowire-perf-section-title', textContent: 'Status distribution' }));
                var distGrid = el('div', { className: 'bowire-perf-status-grid' });
                for (var status in benchmark.statusCounts) {
                    if (Object.prototype.hasOwnProperty.call(benchmark.statusCounts, status)) {
                        distGrid.appendChild(el('div', { className: 'bowire-perf-status-row' },
                            el('span', { className: 'bowire-perf-status-name ' + grpcStatusClass(status), textContent: status }),
                            el('span', { className: 'bowire-perf-status-count', textContent: String(benchmark.statusCounts[status]) })
                        ));
                    }
                }
                dist.appendChild(distGrid);
                body.appendChild(dist);
            }

            // Charts
            body.appendChild(el('div', { className: 'bowire-perf-section-title', textContent: 'Latency histogram' }));
            body.appendChild(renderHistogram(benchmark.durations, stats));

            body.appendChild(el('div', { className: 'bowire-perf-section-title', textContent: 'Latency over time' }));
            body.appendChild(renderTimeline(benchmark.durations, stats));
        } else if (!benchmark.running && benchmark.completed === 0) {
            body.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                el('div', { className: 'bowire-empty-icon', innerHTML: svgIcon('clock') }),
                el('div', { className: 'bowire-empty-title', textContent: 'No benchmark yet' }),
                el('div', { className: 'bowire-empty-desc', textContent: 'Configure the request body in the Body tab, then click Run benchmark to measure latency.' })
            ));
        }

        return body;
    }

    function formatMs(ms) {
        if (ms < 10)   return ms.toFixed(2) + ' ms';
        if (ms < 100)  return ms.toFixed(1) + ' ms';
        if (ms < 1000) return Math.round(ms) + ' ms';
        return (ms / 1000).toFixed(2) + ' s';
    }

    function renderHistogram(durations, stats) {
        var W = 600, H = 180, PAD_L = 40, PAD_R = 12, PAD_T = 12, PAD_B = 28;
        var chartW = W - PAD_L - PAD_R;
        var chartH = H - PAD_T - PAD_B;
        var BUCKETS = 24;

        if (durations.length === 0) {
            return el('div', { className: 'bowire-perf-empty', textContent: 'No data' });
        }

        // Bucket the durations linearly between min and max
        var min = stats.min, max = stats.max;
        if (min === max) max = min + 1; // avoid div by zero
        var bucketWidth = (max - min) / BUCKETS;
        var buckets = new Array(BUCKETS).fill(0);
        for (var i = 0; i < durations.length; i++) {
            var idx = Math.min(BUCKETS - 1, Math.floor((durations[i] - min) / bucketWidth));
            buckets[idx]++;
        }
        var maxCount = Math.max.apply(null, buckets);

        var svg = '<svg viewBox="0 0 ' + W + ' ' + H + '" class="bowire-perf-svg" preserveAspectRatio="xMidYMid meet">';
        // Axes
        svg += '<line x1="' + PAD_L + '" y1="' + (H - PAD_B) + '" x2="' + (W - PAD_R) + '" y2="' + (H - PAD_B) + '" class="bowire-perf-axis"/>';
        svg += '<line x1="' + PAD_L + '" y1="' + PAD_T + '" x2="' + PAD_L + '" y2="' + (H - PAD_B) + '" class="bowire-perf-axis"/>';

        // Bars
        var barW = chartW / BUCKETS;
        for (var b = 0; b < BUCKETS; b++) {
            var h = maxCount > 0 ? (buckets[b] / maxCount) * chartH : 0;
            var x = PAD_L + b * barW;
            var y = (H - PAD_B) - h;
            svg += '<rect x="' + (x + 1) + '" y="' + y + '" width="' + (barW - 2) + '" height="' + h + '" class="bowire-perf-bar"><title>' +
                   formatMs(min + b * bucketWidth) + ' – ' + formatMs(min + (b + 1) * bucketWidth) + ': ' + buckets[b] + ' calls</title></rect>';
        }

        // Percentile markers (p50, p95)
        function markerLine(value, label, cls) {
            if (value < min || value > max) return '';
            var x = PAD_L + ((value - min) / (max - min)) * chartW;
            return '<line x1="' + x + '" y1="' + PAD_T + '" x2="' + x + '" y2="' + (H - PAD_B) + '" class="bowire-perf-marker ' + cls + '"/>' +
                   '<text x="' + (x + 3) + '" y="' + (PAD_T + 10) + '" class="bowire-perf-marker-label ' + cls + '">' + label + '</text>';
        }
        svg += markerLine(stats.p50, 'p50', 'p50');
        svg += markerLine(stats.p95, 'p95', 'p95');

        // X-axis labels (min, max)
        svg += '<text x="' + PAD_L + '" y="' + (H - 8) + '" class="bowire-perf-axis-label">' + formatMs(min) + '</text>';
        svg += '<text x="' + (W - PAD_R) + '" y="' + (H - 8) + '" class="bowire-perf-axis-label" text-anchor="end">' + formatMs(max) + '</text>';
        // Y-axis label (max count)
        svg += '<text x="' + (PAD_L - 4) + '" y="' + (PAD_T + 10) + '" class="bowire-perf-axis-label" text-anchor="end">' + maxCount + '</text>';
        svg += '<text x="' + (PAD_L - 4) + '" y="' + (H - PAD_B - 2) + '" class="bowire-perf-axis-label" text-anchor="end">0</text>';

        svg += '</svg>';

        var wrap = el('div', { className: 'bowire-perf-chart' });
        wrap.innerHTML = svg;
        return wrap;
    }

    function renderTimeline(durations, stats) {
        var W = 600, H = 160, PAD_L = 40, PAD_R = 12, PAD_T = 12, PAD_B = 28;
        var chartW = W - PAD_L - PAD_R;
        var chartH = H - PAD_T - PAD_B;

        if (durations.length === 0) {
            return el('div', { className: 'bowire-perf-empty', textContent: 'No data' });
        }

        var n = durations.length;
        var max = stats.max;
        if (max === 0) max = 1;

        var svg = '<svg viewBox="0 0 ' + W + ' ' + H + '" class="bowire-perf-svg" preserveAspectRatio="xMidYMid meet">';
        svg += '<line x1="' + PAD_L + '" y1="' + (H - PAD_B) + '" x2="' + (W - PAD_R) + '" y2="' + (H - PAD_B) + '" class="bowire-perf-axis"/>';
        svg += '<line x1="' + PAD_L + '" y1="' + PAD_T + '" x2="' + PAD_L + '" y2="' + (H - PAD_B) + '" class="bowire-perf-axis"/>';

        // Polyline of latencies
        var points = '';
        for (var i = 0; i < n; i++) {
            var x = PAD_L + (n > 1 ? (i / (n - 1)) * chartW : chartW / 2);
            var y = (H - PAD_B) - (durations[i] / max) * chartH;
            points += x.toFixed(1) + ',' + y.toFixed(1) + ' ';
        }
        svg += '<polyline points="' + points + '" class="bowire-perf-line"/>';

        // p50 / p95 horizontal lines
        function hline(value, cls, label) {
            var y = (H - PAD_B) - (value / max) * chartH;
            return '<line x1="' + PAD_L + '" y1="' + y + '" x2="' + (W - PAD_R) + '" y2="' + y + '" class="bowire-perf-marker ' + cls + '"/>' +
                   '<text x="' + (W - PAD_R - 2) + '" y="' + (y - 3) + '" class="bowire-perf-marker-label ' + cls + '" text-anchor="end">' + label + ' ' + formatMs(value) + '</text>';
        }
        svg += hline(stats.p50, 'p50', 'p50');
        svg += hline(stats.p95, 'p95', 'p95');

        svg += '<text x="' + PAD_L + '" y="' + (H - 8) + '" class="bowire-perf-axis-label">#1</text>';
        svg += '<text x="' + (W - PAD_R) + '" y="' + (H - 8) + '" class="bowire-perf-axis-label" text-anchor="end">#' + n + '</text>';
        svg += '<text x="' + (PAD_L - 4) + '" y="' + (PAD_T + 10) + '" class="bowire-perf-axis-label" text-anchor="end">' + formatMs(max) + '</text>';
        svg += '<text x="' + (PAD_L - 4) + '" y="' + (H - PAD_B - 2) + '" class="bowire-perf-axis-label" text-anchor="end">0</text>';

        svg += '</svg>';

        var wrap = el('div', { className: 'bowire-perf-chart' });
        wrap.innerHTML = svg;
        return wrap;
    }

    function renderActionBar() {
        // ID encodes the method identity + channel-mode so morphdom fully
        // replaces the bar when switching between channel and standard
        // methods instead of reusing stale DOM with wrong closures.
        var actionBarKey = (selectedService ? selectedService.name : '') + '-'
            + (selectedMethod ? selectedMethod.name : '') + '-'
            + (isChannelMethod() ? 'channel' : 'standard');
        const bar = el('div', { id: 'bowire-action-bar-' + actionBarKey, className: 'bowire-action-bar' });

        if (isChannelMethod()) {
            // ---- Channel Action Bar (Duplex / Client Streaming) ----
            if (!duplexConnected) {
                // Not connected: show Connect button
                var connectBtn = el('button', {
                    id: 'bowire-channel-connect-btn',
                    className: 'bowire-execute-btn bowire-connect-btn',
                    title: 'Connect (Ctrl+Enter)',
                    onClick: channelConnect
                },
                    el('span', { innerHTML: svgIcon('connect'), style: 'width:14px;height:14px;display:flex' }),
                    el('span', { textContent: 'Connect' })
                );
                bar.appendChild(connectBtn);
            } else {
                // Connected: show Send button + Close/Disconnect
                var sendBtn = el('button', {
                    id: 'bowire-channel-send-btn',
                    className: 'bowire-execute-btn bowire-send-btn',
                    title: 'Send (Ctrl+Enter)',
                    onClick: channelSend
                },
                    el('span', { innerHTML: svgIcon('send'), style: 'width:14px;height:14px;display:flex' }),
                    el('span', { textContent: 'Send' })
                );
                bar.appendChild(sendBtn);

                if (selectedMethod.methodType === 'ClientStreaming') {
                    // Client streaming: "Close & Get Response" button
                    var closeBtn = el('button', {
                        id: 'bowire-channel-close-btn',
                        className: 'bowire-execute-btn bowire-disconnect-btn',
                        onClick: channelClose,
                        style: 'margin-left: 8px'
                    },
                        el('span', { innerHTML: svgIcon('stop'), style: 'width:14px;height:14px;display:flex' }),
                        el('span', { textContent: 'Close & Get Response' })
                    );
                    bar.appendChild(closeBtn);
                } else {
                    // Duplex: "Disconnect" button
                    var disconnectBtn = el('button', {
                        id: 'bowire-channel-disconnect-btn',
                        className: 'bowire-execute-btn bowire-disconnect-btn',
                        onClick: channelDisconnect,
                        style: 'margin-left: 8px'
                    },
                        el('span', { innerHTML: svgIcon('disconnect'), style: 'width:14px;height:14px;display:flex' }),
                        el('span', { textContent: 'Disconnect' })
                    );
                    bar.appendChild(disconnectBtn);
                }
            }

            // Status
            var statusBar = el('div', { className: 'bowire-status-bar' });

            if (statusInfo) {
                if (statusInfo.status === 'Connected') {
                    statusBar.appendChild(el('div', { className: 'bowire-status-item bowire-streaming-indicator' },
                        el('span', { className: 'bowire-pulse-dot' }),
                        el('span', { textContent: 'Connected' })
                    ));
                } else {
                    var statusDot = grpcStatusClass(statusInfo.status);
                    statusBar.appendChild(el('div', { className: 'bowire-status-item' },
                        el('span', { className: 'bowire-status-dot ' + statusDot }),
                        el('span', { textContent: grpcStatusLabel(statusInfo.status) })
                    ));
                }
                if (statusInfo.durationMs > 0) {
                    statusBar.appendChild(el('div', { className: 'bowire-status-item' },
                        el('span', { innerHTML: svgIcon('clock'), style: 'width:12px;height:12px;display:flex;opacity:0.6' }),
                        el('span', { textContent: statusInfo.durationMs + 'ms' })
                    ));
                }
                if (statusInfo.requestSize > 0 || statusInfo.responseSize > 0) {
                    var sizeText = '';
                    if (statusInfo.requestSize > 0) sizeText += '\u2191 ' + formatBytes(statusInfo.requestSize);
                    if (statusInfo.requestSize > 0 && statusInfo.responseSize > 0) sizeText += '  ';
                    if (statusInfo.responseSize > 0) sizeText += '\u2193 ' + formatBytes(statusInfo.responseSize);
                    statusBar.appendChild(el('div', { className: 'bowire-status-item', style: 'font-family:var(--bowire-font-mono);font-size:11px' },
                        el('span', { textContent: sizeText })
                    ));
                }
            }

            if (channelError) {
                statusBar.appendChild(el('div', { className: 'bowire-status-item' },
                    el('span', { className: 'bowire-status-dot error' }),
                    el('span', { textContent: channelError })
                ));
            }

            bar.appendChild(statusBar);

            bar.appendChild(el('div', {
                className: 'bowire-status-item',
                style: 'opacity: 0.5',
                textContent: duplexConnected ? 'Ctrl+Enter to send' : 'Ctrl+Enter to connect'
            }));

            // Console button was here — retired, the statusbar console
            // button at the bottom-right already owns this affordance.
            return bar;
        }

        // ---- Standard Action Bar (Unary / Server Streaming) ----
        const isStreaming = selectedMethod && selectedMethod.serverStreaming;
        const btnText = isExecuting && isStreaming ? 'Stop' : 'Execute';
        const btnIcon = isExecuting && isStreaming ? svgIcon('stop') : svgIcon('play');
        const btnClass = isExecuting && isStreaming ? 'bowire-execute-btn streaming-active' : 'bowire-execute-btn';

        // Execute as a split button: primary = Execute (or Stop
        // when a stream is mid-flight); caret = a small menu with
        // alternate run modes (Repeat last, Run as benchmark, Run
        // with preset). Repeat, Save-to-collection and the inline
        // collections-manager icon were retired from the bar —
        // they duplicated the "+ Add to…" menu in the method
        // header and the History tab.
        const lastCall = getHistory().find(function (h) {
            return h.service === selectedService?.name && h.method === selectedMethod?.name;
        });
        const splitWrap = el('div', { className: 'bowire-split-btn-wrap bowire-action-execute-split' });
        const btn = el('button', {
            id: isExecuting && isStreaming ? 'bowire-action-stop-btn' : 'bowire-action-execute-btn',
            className: btnClass + ' bowire-split-btn-main',
            title: btnText + ' (Ctrl+Enter)',
            onClick: handleExecute
        },
            el('span', { innerHTML: btnIcon, style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: btnText })
        );
        if (isExecuting && !isStreaming) btn.disabled = true;
        splitWrap.appendChild(btn);
        const caret = el('button', {
            id: 'bowire-action-execute-caret-btn',
            className: 'bowire-execute-btn bowire-split-btn-caret',
            title: 'More run options',
            'aria-haspopup': 'menu',
            onClick: function (e) {
                e.stopPropagation();
                var prev = document.querySelector('.bowire-action-execute-menu');
                if (prev) { prev.remove(); return; }
                var menu = el('div', { className: 'bowire-action-execute-menu', role: 'menu' });
                // "Repeat last call" was retired — primary Execute
                // re-uses the active form state (which already holds
                // the last sent body) and the History tab's per-row
                // Replay button restores any older entry verbatim.
                var lastOkCall = lastCall && lastCall.status && lastCall.status !== 'Error';
                var canBench = lastOkCall && typeof createBenchmarkSpec === 'function';
                menu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-action-execute-menu-item' + (canBench ? '' : ' is-disabled'),
                    disabled: canBench ? undefined : true,
                    title: canBench
                        ? 'Create a benchmark spec from this request and open it'
                        : 'Execute a successful call first — benchmarks need a known-good request',
                    onClick: function () {
                        menu.remove();
                        if (!canBench) return;
                        var addBtn = document.querySelector('#bowire-header-addto-btn');
                        if (addBtn) { try { addBtn.click(); } catch {} }
                        railMode = 'benchmarks';
                        try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); } catch { /* ignore */ }
                        render();
                    }
                },
                    el('span', { className: 'bowire-action-execute-menu-icon', innerHTML: svgIcon('lightning') }),
                    el('span', { textContent: 'Run as benchmark…' })
                ));
                var presetList = (typeof loadPresets === 'function')
                    ? loadPresets('discover').filter(function (p) {
                        return p && p.config
                            && p.config.service === selectedService.name
                            && p.config.method === selectedMethod.name;
                    })
                    : [];
                var hasPresets = presetList.length > 0;
                menu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-action-execute-menu-item' + (hasPresets ? '' : ' is-disabled'),
                    disabled: hasPresets ? undefined : true,
                    title: hasPresets
                        ? 'Pick a saved preset, apply it, then execute'
                        : 'No presets defined for this method',
                    onClick: function () {
                        menu.remove();
                        if (!hasPresets) return;
                        // Open a small chooser modal — picking an
                        // entry applies the preset and immediately
                        // executes. No detour through the header
                        // picker dropdown.
                        _openRunWithPresetDialog(presetList);
                    }
                },
                    el('span', { className: 'bowire-action-execute-menu-icon', innerHTML: svgIcon('preset') }),
                    el('span', { textContent: 'Run with preset…' })
                ));
                document.body.appendChild(menu);
                var rect = caret.getBoundingClientRect();
                menu.style.position = 'fixed';
                menu.style.left = Math.max(8, rect.left) + 'px';
                // Auto-flip vertically — the action bar lives at the
                // bottom of the workbench, so a downward popover
                // gets clipped. Measure after mount and place above
                // the caret when there isn't enough room below.
                var menuH = menu.offsetHeight;
                var vpH = window.innerHeight;
                var spaceBelow = vpH - rect.bottom;
                var spaceAbove = rect.top;
                if (spaceBelow >= menuH + 12 || spaceBelow >= spaceAbove) {
                    menu.style.top = (rect.bottom + 6) + 'px';
                } else {
                    menu.style.top = Math.max(8, rect.top - menuH - 6) + 'px';
                }
                setTimeout(function () {
                    function onOutside(ev) {
                        if (!menu.contains(ev.target) && !caret.contains(ev.target)) {
                            menu.remove();
                            document.removeEventListener('click', onOutside, true);
                        }
                    }
                    document.addEventListener('click', onOutside, true);
                }, 0);
            }
        }, el('span', { innerHTML: svgIcon('chevronDown') }));
        splitWrap.appendChild(caret);
        bar.appendChild(splitWrap);

        // Transcoding mode toggle — only when the method has a google.api.http
        // annotation AND the REST plugin is loaded (the "via HTTP" path needs
        // it as the IInlineHttpInvoker provider). When REST isn't installed,
        // we still show the HTTP verb + path as a read-only info badge so the
        // user knows the transcoding endpoint exists.
        if (methodSupportsTranscoding(selectedMethod)) {
            if (isHttpInvocationAvailable()) {
                var currentMode = getTranscodingMode(selectedService.name, selectedMethod.name);
                bar.appendChild(el('div', { className: 'bowire-toggle-group', style: 'margin-left:8px' },
                    el('button', {
                        id: 'bowire-transcoding-grpc-btn',
                        className: 'bowire-toggle-btn' + (currentMode === 'grpc' ? ' is-active' : ''),
                        title: 'Invoke via gRPC (native binary protocol)',
                        onClick: function () {
                            setTranscodingMode(selectedService.name, selectedMethod.name, 'grpc');
                            render();
                        },
                        textContent: 'gRPC'
                    }),
                    el('button', {
                        id: 'bowire-transcoding-http-btn',
                        className: 'bowire-toggle-btn' + (currentMode === 'http' ? ' is-active' : ''),
                        title: 'Invoke via HTTP transcoding (' + selectedMethod.httpMethod + ' ' + selectedMethod.httpPath + ')',
                        onClick: function () {
                            setTranscodingMode(selectedService.name, selectedMethod.name, 'http');
                            render();
                        },
                        textContent: 'HTTP'
                    })
                ));
            } else {
                // REST plugin not installed — read-only badge with the HTTP info
                bar.appendChild(el('div', {
                    className: 'bowire-transcoding-info',
                    title: 'This method has a google.api.http transcoding annotation. '
                        + 'Install the REST plugin (Kuestenlogik.Bowire.Protocol.Rest) to invoke it via HTTP.'
                },
                    el('span', { className: 'bowire-transcoding-info-verb', textContent: selectedMethod.httpMethod }),
                    el('span', { className: 'bowire-transcoding-info-path', textContent: selectedMethod.httpPath })
                ));
            }
        }

        // Status
        var statusBar = el('div', { className: 'bowire-status-bar' });

        if (statusInfo) {
            if (statusInfo.status === 'Streaming') {
                statusBar.appendChild(el('div', { className: 'bowire-status-item bowire-streaming-indicator' },
                    el('span', { className: 'bowire-pulse-dot' }),
                    el('span', { textContent: 'Streaming' })
                ));
            } else {
                const statusDot = grpcStatusClass(statusInfo.status);
                statusBar.appendChild(el('div', { className: 'bowire-status-item' },
                    el('span', { className: `bowire-status-dot ${statusDot}` }),
                    el('span', { textContent: grpcStatusLabel(statusInfo.status) })
                ));
            }
            if (statusInfo.durationMs > 0) {
                statusBar.appendChild(el('div', { className: 'bowire-status-item' },
                    el('span', { innerHTML: svgIcon('clock'), style: 'width:12px;height:12px;display:flex;opacity:0.6' }),
                    el('span', { textContent: `${statusInfo.durationMs}ms` })
                ));
            }
            if (streamMessages.length > 0) {
                statusBar.appendChild(el('div', { className: 'bowire-status-item' },
                    el('span', { className: 'bowire-stream-badge', textContent: String(streamMessages.length) }),
                    el('span', { textContent: `message${streamMessages.length !== 1 ? 's' : ''}` })
                ));
            }
        }

        bar.appendChild(statusBar);

        // Ctrl+Enter hint retired — the Execute button's title
        // attribute already exposes the shortcut on hover.

        bar.appendChild(renderRecordingToggleButton());
        // Console toggle retired from the action bar — same affordance
        // lives in the statusbar at the bottom-right.
        return bar;
    }

    // Modal chooser for "Run with preset…". Lists every preset that
    // matches the active (service, method); click applies + executes.
    // Pattern mirrors bowireConfirm — same overlay + dialog classes
    // so theming + Esc/backdrop close come for free.
    function _openRunWithPresetDialog(presets) {
        var existing = document.querySelector('.bowire-confirm-overlay');
        if (existing) existing.remove();
        var list = el('div', { className: 'bowire-run-preset-list' });
        presets.forEach(function (preset) {
            list.appendChild(el('button', {
                type: 'button',
                className: 'bowire-run-preset-item' + (preset.isDefault ? ' is-default' : ''),
                title: preset.isDefault ? 'Default preset for this method' : preset.name,
                onClick: function () {
                    overlay.remove();
                    if (typeof applyPresetToCurrentMethod === 'function'
                        && applyPresetToCurrentMethod(preset)) {
                        // Give the editor + metadata-row patches a
                        // frame to settle before execute reads the
                        // DOM. applyPresetToCurrentMethod already
                        // re-renders, so we wait one rAF.
                        requestAnimationFrame(function () {
                            if (typeof handleExecute === 'function') handleExecute();
                        });
                    }
                }
            },
                el('span', { className: 'bowire-run-preset-item-name', textContent: preset.name || 'Untitled' }),
                preset.isDefault
                    ? el('span', { className: 'bowire-run-preset-item-tag', textContent: 'default' })
                    : null
            ));
        });
        var cancelBtn = el('button', {
            className: 'bowire-confirm-btn cancel',
            textContent: 'Cancel',
            onClick: function () { overlay.remove(); }
        });
        var dialog = el('div', {
            className: 'bowire-confirm-dialog bowire-run-preset-dialog',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-labelledby': 'bowire-run-preset-title'
        },
            el('div', { id: 'bowire-run-preset-title', className: 'bowire-confirm-title', textContent: 'Run with preset' }),
            el('div', { className: 'bowire-confirm-message', textContent: 'Pick a saved preset for ' + selectedMethod.name + '. The body and headers are applied, then the request is sent.' }),
            list,
            el('div', { className: 'bowire-confirm-actions' }, cancelBtn)
        );
        var overlay = el('div', {
            className: 'bowire-confirm-overlay',
            onClick: function (e) { if (e.target === overlay) overlay.remove(); }
        }, dialog);
        document.body.appendChild(overlay);
        // Esc closes — focus the first preset button so keyboard
        // navigation works out of the box.
        document.addEventListener('keydown', function escHandler(e) {
            if (e.key === 'Escape') {
                overlay.remove();
                document.removeEventListener('keydown', escHandler);
            }
        });
        var first = list.querySelector('button');
        if (first) { try { first.focus(); } catch {} }
    }

    function renderConsoleToggleButton() {
        return el('button', {
            id: 'bowire-console-toggle-btn',
            className: 'bowire-console-toggle' + (consoleOpen ? ' active' : ''),
            title: 'Toggle Console (chronological log of every request and response)',
            onClick: toggleConsole
        },
            el('span', { innerHTML: svgIcon('clock'), className: 'bowire-console-toggle-icon' }),
            el('span', { textContent: 'Console' }),
            consoleLog.length > 0
                ? el('span', { className: 'bowire-console-toggle-count', textContent: String(consoleLog.length) })
                : null
        );
    }

    /**
     * Recording control in the action bar. Single button that does three things:
     *
     *  - Click while idle → start a new recording, switch to "armed" state
     *    (red dot pulses, label changes to "Stop")
     *  - Click while armed → stop the current recording (label flips back to
     *    "Record"), the saved recording can be replayed/exported via the modal
     *  - Right-click (context menu) or shift+click → open the manager modal
     *    without toggling the recording state
     *
     * The little badge to the right of the label shows the live step count of
     * the active recording so users can see captures landing as they happen.
     */
    function renderRecordingToggleButton() {
        var active = isRecording();
        var activeRec = active ? recordingsList.find(function (r) { return r.id === recordingActiveId; }) : null;
        var stepCount = activeRec ? activeRec.steps.length : 0;

        return el('button', {
            id: active ? 'bowire-recording-stop-btn' : 'bowire-recording-start-btn',
            className: 'bowire-console-toggle bowire-recording-toggle' + (active ? ' active' : ''),
            title: active
                ? 'Recording — click to stop. Shift-click to switch to Recordings mode.'
                : 'Start recording a sequence of calls. Shift-click to switch to Recordings mode.',
            onClick: function (e) {
                if (e.shiftKey) {
                    // #133 Phase 3 — switch to the rail mode instead
                    // of opening the legacy modal.
                    recordingManagerSelectedId = recordingActiveId
                        || (recordingsList.length > 0 ? recordingsList[recordingsList.length - 1].id : null);
                    railMode = 'recordings';
                    try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                    render();
                    return;
                }
                if (active) {
                    stopRecording();
                } else {
                    startRecording();
                }
            },
            onContextMenu: function (e) {
                e.preventDefault();
                recordingManagerSelectedId = recordingActiveId
                    || (recordingsList.length > 0 ? recordingsList[recordingsList.length - 1].id : null);
                railMode = 'recordings';
                try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                render();
            }
        },
            el('span', {
                innerHTML: svgIcon(active ? 'square' : 'record'),
                className: 'bowire-console-toggle-icon bowire-recording-toggle-icon'
            }),
            // Step-count badge in the corner when recording is live —
            // small accent pill so the operator can glance at it
            // without the verbose "Record · 4 steps" wording the pill
            // used to carry.
            active && stepCount > 0
                ? el('span', { className: 'bowire-console-toggle-count', textContent: String(stepCount) })
                : null
        );
    }

