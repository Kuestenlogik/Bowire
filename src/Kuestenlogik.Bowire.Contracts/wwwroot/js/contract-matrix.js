// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #364 — Contracts rail: the consumer x provider verification matrix.
//
// This fragment is embedded in Kuestenlogik.Bowire.Contracts and spliced
// into the workbench bundle's shared IIFE by the core HTML generator, so
// it sees core helpers (el / render / apiFetch) directly. Splice order
// relative to other rail packages is not guaranteed — declare only `var`s
// and hoisted functions at top level.

// Latest matrix payload from /api/contracts/matrix, plus which cell the
// operator drilled into. Module-scope rather than closure-captured: the
// drill-in handler re-resolves its cell by key at click time, because
// morphdom preserves nodes across re-renders and a captured cell object
// would go stale (the same pitfall the recordings rail documents).
var bowireContractMatrix = null;
var bowireContractMatrixError = null;
var bowireContractMatrixLoading = false;
var bowireContractDrillKey = null;

function bowireContractCellKey(consumer, provider) {
  return consumer + '::' + provider;
}

function bowireContractFindCell(key) {
  if (!bowireContractMatrix || !key) return null;
  var cells = bowireContractMatrix.cells || [];
  for (var i = 0; i < cells.length; i++) {
    if (bowireContractCellKey(cells[i].consumer, cells[i].provider) === key) return cells[i];
  }
  return null;
}

async function bowireLoadContractMatrix() {
  bowireContractMatrixLoading = true;
  bowireContractMatrixError = null;
  render();
  try {
    var prefix = (window.__BOWIRE_CONFIG__ && window.__BOWIRE_CONFIG__.prefix) || '';
    var resp = await fetch(prefix + '/api/contracts/matrix', {
      method: 'GET',
      headers: { 'Accept': 'application/json' },
    });
    if (!resp.ok) {
      bowireContractMatrixError = 'Matrix request failed (' + resp.status + ')';
      bowireContractMatrix = null;
    } else {
      bowireContractMatrix = await resp.json();
    }
  } catch (err) {
    bowireContractMatrixError = 'Matrix request failed: ' + (err && err.message ? err.message : err);
    bowireContractMatrix = null;
  } finally {
    bowireContractMatrixLoading = false;
    render();
  }
}

function bowireContractStatusLabel(status) {
  if (status === 'pass') return 'PASS';
  if (status === 'fail') return 'FAIL';
  return '—';
}

// One matrix cell: verdict + pass/total + last run, clickable when there
// is a report to drill into.
function bowireRenderContractCell(cell) {
  var status = (cell.status || 'notRun').toLowerCase();
  var isRun = status === 'pass' || status === 'fail';
  var attrs = {
    className: 'bowire-contract-cell bowire-contract-cell-' + status,
    title: cell.consumer + ' → ' + cell.provider,
  };
  if (isRun) {
    attrs.role = 'button';
    attrs.tabindex = '0';
    attrs['data-cell-key'] = bowireContractCellKey(cell.consumer, cell.provider);
    attrs.onClick = function () {
      // Re-resolve by key at click time — never trust a captured object.
      bowireContractDrillKey = this.getAttribute('data-cell-key');
      render();
    };
  }
  var children = [el('span', { className: 'bowire-contract-verdict' }, bowireContractStatusLabel(status))];
  if (isRun) {
    children.push(el('span', { className: 'bowire-contract-counts' },
      cell.passedInteractions + '/' + cell.totalInteractions));
  }
  return el('td', attrs, children);
}

function bowireRenderContractGrid() {
  var m = bowireContractMatrix;
  var head = [el('th', { className: 'bowire-contract-corner' }, 'consumer \\ provider')];
  m.providers.forEach(function (p) {
    head.push(el('th', { className: 'bowire-contract-col' }, p));
  });

  var rows = [el('tr', {}, head)];
  m.consumers.forEach(function (consumer) {
    var cells = [el('th', { className: 'bowire-contract-row' }, consumer)];
    m.providers.forEach(function (provider) {
      var key = bowireContractCellKey(consumer, provider);
      var cell = bowireContractFindCell(key)
        || { consumer: consumer, provider: provider, status: 'notRun' };
      cells.push(bowireRenderContractCell(cell));
    });
    rows.push(el('tr', {}, cells));
  });

  return el('table', { className: 'bowire-contract-matrix' }, rows);
}

// Drill-in: every interaction of the selected cell, failures expanded
// with the shape diff the verifier produced.
function bowireRenderContractDrillIn() {
  var cell = bowireContractFindCell(bowireContractDrillKey);
  if (!cell || !cell.report) return null;

  var items = (cell.report.interactions || []).map(function (i) {
    var kids = [
      el('div', { className: 'bowire-contract-interaction-head' }, [
        el('span', { className: 'bowire-contract-verdict-' + (i.passed ? 'pass' : 'fail') },
          i.passed ? 'PASS' : 'FAIL'),
        el('span', { className: 'bowire-contract-method' }, i.method || ''),
        el('span', { className: 'bowire-contract-desc' }, i.description || ''),
        el('span', { className: 'bowire-contract-duration' }, (i.durationMs || 0) + 'ms'),
      ]),
    ];
    if (i.error) {
      kids.push(el('div', { className: 'bowire-contract-error' }, i.error));
    }
    (i.assertions || []).forEach(function (a) {
      if (a.passed) return;
      kids.push(el('div', { className: 'bowire-contract-assertion' },
        a.path + ' ' + a.op + ' — ' + (a.error || ('expected ' + a.expected + ', got ' + a.actualText))));
    });
    return el('li', { className: 'bowire-contract-interaction' }, kids);
  });

  return el('div', { className: 'bowire-contract-drillin' }, [
    el('div', { className: 'bowire-contract-drillin-head' }, [
      el('strong', {}, cell.consumer + ' → ' + cell.provider),
      // #645 — the house close affordance (icon, `bowire-drawer-close`),
      // as used by every other dismissible panel. `bowire-contract-close`
      // was likewise never styled.
      el('button', {
        type: 'button',
        className: 'bowire-drawer-close',
        title: t('common.close'),
        'aria-label': t('common.close'),
        innerHTML: typeof svgIcon === 'function' ? svgIcon('close') : '×',
        onClick: function () { bowireContractDrillKey = null; render(); },
      }),
    ]),
    el('ul', { className: 'bowire-contract-interactions' }, items),
  ]);
}

function renderContractsMain() {
  var body = [];

  // #645 — the toolbar row keeps its own class (it only lays out), but the
  // button inside it takes the shared one. `bowire-contract-run` had no CSS
  // anywhere, so this rendered as a raw browser button next to rails full of
  // styled ones — which is most of what "the style doesn't fit" was.
  body.push(el('div', { className: 'bowire-contract-toolbar' }, [
    el('button', {
      type: 'button',
      className: 'bowire-settings-action-btn',
      disabled: bowireContractMatrixLoading ? 'disabled' : null,
      onClick: function () { bowireLoadContractMatrix(); },
    }, bowireContractMatrixLoading ? 'Loading…' : 'Refresh matrix'),
  ]));

  if (bowireContractMatrixError) {
    body.push(el('div', { className: 'bowire-contract-error' }, bowireContractMatrixError));
  } else if (!bowireContractMatrix) {
    // #645 — the same empty card every other rail uses. A bare sentence in a
    // div is why this pane read as unfinished beside its neighbours, and it
    // left the obvious next step unsaid. The icon is the rail's own
    // (`IconKey => "certificate"`), so the card echoes the strip.
    body.push(renderEmptyCard({
      icon: 'certificate',
      headline: 'No contract results yet',
      body: 'Run `bowire contract verify` against a provider, then refresh. '
        + 'The matrix folds whatever results it finds into one row per consumer.',
    }));
  } else if (!bowireContractMatrix.consumers || bowireContractMatrix.consumers.length === 0) {
    body.push(renderEmptyCard({
      icon: 'certificate',
      headline: 'No verification results found',
      body: 'Results were read but none carried a consumer and provider pair. '
        + 'Check that the verification wrote where this rail reads.',
    }));
  } else {
    body.push(el('div', { className: 'bowire-contract-summary' },
      bowireContractMatrix.passedCells + ' passing · ' + bowireContractMatrix.failedCells + ' failing'));
    body.push(bowireRenderContractGrid());
    var drill = bowireRenderContractDrillIn();
    if (drill) body.push(drill);
  }

  return el('div', { className: 'bowire-contract-main' }, body);
}

window.__bowireRailRenderers = window.__bowireRailRenderers || {};
window.__bowireRailRenderers.contractsMain = renderContractsMain;
