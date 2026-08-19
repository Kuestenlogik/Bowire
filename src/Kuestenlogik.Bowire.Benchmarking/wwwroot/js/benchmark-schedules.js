// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #232 — the scheduled-runs panel in the Benchmarks rail.
//
// Schedules live server-side (.bowire/benchmark-schedules), because a cron
// entry has to fire with no browser open — that is the whole point. This
// panel therefore READS them and can pause / resume one; authoring stays on
// the CLI, where an operator is explicit about a target the server will call
// unattended.
//
// Embedded in Kuestenlogik.Bowire.Benchmarking and spliced into the workbench
// bundle's shared IIFE, so core helpers (el / render) are in scope. Splice
// order across rail packages is not guaranteed — only `var`s and hoisted
// functions at top level.

var bowireBenchmarkSchedules = null;
var bowireBenchmarkSchedulesError = null;
var bowireBenchmarkSchedulesLoading = false;

function bowireSchedulesPrefix() {
    return (window.__BOWIRE_CONFIG__ && window.__BOWIRE_CONFIG__.prefix) || '';
}

async function bowireLoadBenchmarkSchedules() {
    bowireBenchmarkSchedulesLoading = true;
    bowireBenchmarkSchedulesError = null;
    render();
    try {
        var resp = await fetch(bowireSchedulesPrefix() + '/api/benchmarks/schedules', {
            headers: { 'Accept': 'application/json' }
        });
        if (!resp.ok) {
            // A host without the Benchmarking package mounted answers 404;
            // say so plainly rather than showing an empty list that reads
            // as "no schedules".
            bowireBenchmarkSchedulesError = resp.status === 404
                ? 'Scheduling is not available on this host.'
                : 'Could not load schedules (' + resp.status + ').';
            bowireBenchmarkSchedules = null;
        } else {
            bowireBenchmarkSchedules = await resp.json();
        }
    } catch (err) {
        bowireBenchmarkSchedulesError = 'Could not load schedules: ' + (err && err.message ? err.message : err);
        bowireBenchmarkSchedules = null;
    } finally {
        bowireBenchmarkSchedulesLoading = false;
        render();
    }
}

async function bowireToggleBenchmarkSchedule(id, enabled) {
    try {
        var resp = await fetch(bowireSchedulesPrefix() + '/api/benchmarks/schedules/' + encodeURIComponent(id) + '/enabled', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled: enabled })
        });
        if (!resp.ok) {
            bowireBenchmarkSchedulesError = 'Could not update the schedule (' + resp.status + ').';
            render();
            return;
        }
        // Re-read the list: the server recomputes the next firing time, and
        // guessing it here would drift from what actually fires.
        await bowireLoadBenchmarkSchedules();
    } catch (err) {
        bowireBenchmarkSchedulesError = 'Could not update the schedule: ' + (err && err.message ? err.message : err);
        render();
    }
}

// "in 4h 12m" is what an operator wants from a next-firing time; the exact
// timestamp goes in the title attribute for when they need it.
function bowireFormatUntil(iso) {
    if (!iso) return null;
    var when = new Date(iso);
    if (isNaN(when.getTime())) return null;
    var deltaMs = when.getTime() - Date.now();
    if (deltaMs <= 0) return 'due now';
    var minutes = Math.round(deltaMs / 60000);
    if (minutes < 60) return 'in ' + minutes + 'm';
    var hours = Math.floor(minutes / 60);
    var rest = minutes % 60;
    if (hours < 24) return 'in ' + hours + 'h' + (rest > 0 ? ' ' + rest + 'm' : '');
    var days = Math.floor(hours / 24);
    return 'in ' + days + 'd' + (hours % 24 > 0 ? ' ' + (hours % 24) + 'h' : '');
}

function bowireFormatScheduleMs(value) {
    if (typeof value !== 'number') return '—';
    return value >= 100 ? Math.round(value) + 'ms' : (Math.round(value * 100) / 100) + 'ms';
}

function bowireRenderScheduleRow(entry) {
    var nextText = !entry.enabled ? 'paused'
        : (bowireFormatUntil(entry.nextRun) || 'invalid cron');

    var head = el('div', { class: 'bowire-schedule-head' }, [
        el('span', { class: 'bowire-schedule-name' }, entry.name || entry.id),
        el('span', {
            class: 'bowire-schedule-next' + (entry.enabled ? '' : ' bowire-schedule-paused'),
            title: entry.nextRun ? 'Next run ' + entry.nextRun : ''
        }, nextText),
        el('button', {
            class: 'bowire-schedule-toggle',
            title: entry.enabled ? 'Pause this schedule' : 'Resume this schedule',
            'data-schedule-id': entry.id,
            onclick: function () {
                // Re-resolve the id from the DOM at click time: morphdom
                // preserves nodes across re-renders, so a captured entry
                // object can go stale.
                var id = this.getAttribute('data-schedule-id');
                var current = (bowireBenchmarkSchedules || []).filter(function (s) { return s.id === id; })[0];
                bowireToggleBenchmarkSchedule(id, current ? !current.enabled : true);
            }
        }, entry.enabled ? 'Pause' : 'Resume')
    ]);

    var meta = el('div', { class: 'bowire-schedule-meta' },
        entry.cron + ' [' + (entry.timezone || 'UTC') + ']  ·  ' + entry.target + '  ·  ' +
        entry.iterations + '× @ c' + entry.concurrency);

    var kids = [head, meta];

    if (entry.lastRun) {
        var last = entry.lastRun;
        kids.push(el('div', { class: 'bowire-schedule-last' }, [
            el('span', {
                class: 'bowire-schedule-verdict-' + (last.passed ? 'pass' : 'fail')
            }, last.passed ? 'PASS' : 'FAIL'),
            el('span', {}, ' p50 ' + bowireFormatScheduleMs(last.p50)
                + ' · p95 ' + bowireFormatScheduleMs(last.p95)
                + ' · ' + last.count + ' ok, ' + last.errors + ' failed'
                + ' (' + last.triggeredBy + ')')
        ]));
        (last.thresholds || []).forEach(function (t) {
            if (t.ok) return;
            kids.push(el('div', { class: 'bowire-schedule-breach' },
                t.spec + ' — actual ' + bowireFormatScheduleMs(t.actual)));
        });
    } else {
        kids.push(el('div', { class: 'bowire-schedule-last' }, 'never run'));
    }

    return el('li', { class: 'bowire-schedule-row' }, kids);
}

function renderBenchmarkSchedules() {
    var body = [
        el('div', { class: 'bowire-schedule-toolbar' }, [
            el('strong', {}, 'Scheduled runs'),
            el('button', {
                class: 'bowire-schedule-refresh',
                disabled: bowireBenchmarkSchedulesLoading ? 'disabled' : null,
                onclick: function () { bowireLoadBenchmarkSchedules(); }
            }, bowireBenchmarkSchedulesLoading ? 'Loading…' : 'Refresh')
        ])
    ];

    if (bowireBenchmarkSchedulesError) {
        body.push(el('div', { class: 'bowire-schedule-error' }, bowireBenchmarkSchedulesError));
    } else if (!bowireBenchmarkSchedules) {
        body.push(el('div', { class: 'bowire-schedule-empty' }, 'Press Refresh to load scheduled runs.'));
    } else if (bowireBenchmarkSchedules.length === 0) {
        body.push(el('div', { class: 'bowire-schedule-empty' },
            'No scheduled runs. Add one with: bowire bench schedule add <id> --cron "0 3 * * *" --target Svc/Method -url rest@http://…'));
    } else {
        body.push(el('ul', { class: 'bowire-schedule-list' },
            bowireBenchmarkSchedules.map(bowireRenderScheduleRow)));
    }

    return el('div', { class: 'bowire-schedule-panel' }, body);
}

window.__bowireRailRenderers = window.__bowireRailRenderers || {};
window.__bowireRailRenderers.benchmarkSchedules = renderBenchmarkSchedules;
