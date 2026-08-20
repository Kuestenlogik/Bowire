// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #587 — the Rollup rail: one row per service, folded from whatever Bowire
// reports the operator points it at (lint findings, contract results,
// benchmark runs, scan SARIF, test JUnit).
//
// The heavy lifting is server-side; this reads POST /api/report/rollup and
// renders. Everything it shows is traceable: expanding a row lists the files
// that fed it, so a surprising number never has to be taken on faith.

var bowireRollup = null;
var bowireRollupError = null;
var bowireRollupLoading = false;
var bowireRollupPath = '.bowire';
var bowireRollupExpanded = null;

async function bowireLoadRollup() {
    bowireRollupLoading = true;
    bowireRollupError = null;
    render();
    try {
        var prefix = (window.__BOWIRE_CONFIG__ && window.__BOWIRE_CONFIG__.prefix) || '';
        var from = (bowireRollupPath || '').split(',').map(function (p) { return p.trim(); })
            .filter(function (p) { return p.length > 0; });
        var resp = await fetch(prefix + '/api/report/rollup', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ from: from })
        });
        if (!resp.ok) {
            bowireRollupError = 'Rollup failed (' + resp.status + ').';
            bowireRollup = null;
        } else {
            bowireRollup = await resp.json();
        }
    } catch (err) {
        bowireRollupError = 'Rollup failed: ' + (err && err.message ? err.message : err);
        bowireRollup = null;
    } finally {
        bowireRollupLoading = false;
        render();
    }
}

function bowireRollupCell(pair) {
    // null means "no such report", which is not the same as 0 — show an
    // em dash so a missing report cannot read as a clean result.
    if (!pair) return '—';
    return pair.passed + '/' + pair.total;
}

function bowireRollupMs(value) {
    if (value === null || value === undefined) return '—';
    return value >= 100 ? Math.round(value) + 'ms' : (Math.round(value * 100) / 100) + 'ms';
}

function bowireRollupRow(service) {
    var worst = service.worst || 'ok';
    var lint = service.lint
        ? service.lint.high + '/' + service.lint.medium + '/' + service.lint.low
        : '—';

    var cells = [
        el('td', { class: 'bowire-rollup-service' }, service.service),
        el('td', { class: 'bowire-rollup-worst bowire-rollup-worst-' + worst }, worst.toUpperCase()),
        el('td', {}, lint),
        el('td', {}, bowireRollupCell(service.contracts)),
        el('td', {}, bowireRollupCell(service.tests)),
        el('td', {}, service.benchmark ? bowireRollupMs(service.benchmark.p95Ms) : '—'),
        el('td', {}, service.scanErrors === null || service.scanErrors === undefined
            ? '—' : String(service.scanErrors)),
        el('td', {}, service.lastReportAt ? String(service.lastReportAt).slice(0, 10) : '—'),
    ];

    return el('tr', {
        class: 'bowire-rollup-row',
        role: 'button',
        tabindex: '0',
        title: 'Show the reports behind this row',
        'data-service': service.service,
        onclick: function () {
            // Resolve by name at click time — morphdom keeps nodes across
            // re-renders, so a captured object would go stale.
            var name = this.getAttribute('data-service');
            bowireRollupExpanded = bowireRollupExpanded === name ? null : name;
            render();
        }
    }, cells);
}

function bowireRollupSources(service) {
    var items = (service.sources || []).map(function (src) {
        return el('li', { class: 'bowire-rollup-source' }, src.kind + '  ·  ' + src.path);
    });
    if (items.length === 0) {
        items = [el('li', { class: 'bowire-rollup-source' }, 'no source files recorded')];
    }
    return el('tr', { class: 'bowire-rollup-sources-row' },
        el('td', { colspan: '8' }, el('ul', { class: 'bowire-rollup-sources' }, items)));
}

function renderRollupMain() {
    var body = [];

    body.push(el('div', { class: 'bowire-rollup-toolbar' }, [
        el('input', {
            class: 'bowire-rollup-path',
            type: 'text',
            value: bowireRollupPath,
            placeholder: 'paths to read, comma-separated',
            title: 'Files or directories holding Bowire reports. Directories are walked recursively.',
            oninput: function () { bowireRollupPath = this.value; }
        }),
        el('button', {
            class: 'bowire-rollup-run',
            disabled: bowireRollupLoading ? 'disabled' : null,
            onclick: function () { bowireLoadRollup(); }
        }, bowireRollupLoading ? 'Reading…' : 'Roll up')
    ]));

    if (bowireRollupError) {
        body.push(el('div', { class: 'bowire-rollup-error' }, bowireRollupError));
    } else if (!bowireRollup) {
        body.push(el('div', { class: 'bowire-rollup-empty' },
            'Point this at a folder of Bowire reports — lint findings, contract results, benchmark runs, scan SARIF, test JUnit — and press Roll up.'));
    } else if (!bowireRollup.services || bowireRollup.services.length === 0) {
        body.push(el('div', { class: 'bowire-rollup-empty' },
            'No Bowire reports found under those paths'
            + (bowireRollup.summary && bowireRollup.summary.skipped
                ? ' (' + bowireRollup.summary.skipped + ' file(s) read but not recognised).'
                : '.')));
    } else {
        var head = el('tr', {}, [
            el('th', {}, 'Service'), el('th', {}, 'Worst'), el('th', {}, 'Lint H/M/L'),
            el('th', {}, 'Contracts'), el('th', {}, 'Tests'), el('th', {}, 'p95'),
            el('th', {}, 'Scan'), el('th', {}, 'Last'),
        ]);

        var rows = [head];
        bowireRollup.services.forEach(function (service) {
            rows.push(bowireRollupRow(service));
            if (bowireRollupExpanded === service.service) rows.push(bowireRollupSources(service));
        });

        var s = bowireRollup.summary || {};
        body.push(el('div', { class: 'bowire-rollup-summary' },
            s.services + ' service(s) · ' + s.atHigh + ' at high · ' + s.clean + ' clean'
            + (s.skipped ? ' · ' + s.skipped + ' file(s) skipped' : '')));
        body.push(el('table', { class: 'bowire-rollup-table' }, rows));
    }

    return el('div', { class: 'bowire-rollup-main' }, body);
}

window.__bowireRailRenderers = window.__bowireRailRenderers || {};
window.__bowireRailRenderers.rollupMain = renderRollupMain;
