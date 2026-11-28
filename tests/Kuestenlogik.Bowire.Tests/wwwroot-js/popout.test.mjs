// #753 — popout.js: a tab in a browser window of its own.
//
// The decisions on #753 as tests: what travels with a tab (its state, the
// frames so far, what was running), that the main window lets go of it when
// the pop-out takes it, that it comes back with its state, and what the tab
// menu offers in each kind of window. The DOM, the tab model and the other
// window are fakes; the browser check drives two real windows.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const HOST = ['window', 'document', 'requestTabs', 'tabById', 'tabState', 'newTabState', 'releaseTabState',
    'render', 'afterRender', 'persistRequestTabs', 'switchTab', 'focusedPane', 'services', 'bumpTabIdCounterPast',
    'handleExecute', 'toast', 'el', 'svgIcon', 'railMode', 'requestPanes', 'focusPaneTab', 'freeformRequest',
    'requestTabsRehydrated', 'rehydrateRequestTabs'];
const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/popout.js',
    HOST,
    `return { popoutSnapshot, popoutApplySnapshot, popOutTab, popoutTake, popoutReturn, popoutMenuEntries,
              tabIsPoppedOut, isPopout: () => popoutMode, adopted: () => popoutAdopted };`,
);

function setup({ search = '', opener = null, tabs = [] } = {}) {
    const calls = { released: [], persisted: 0, renders: 0, executed: 0, opened: [], toasts: [], switched: [] };
    const listeners = {};
    const requestTabs = tabs;
    const newTabState = () => ({ formValues: {}, requestMessages: [''], requestInputMode: 'form', isExecuting: false,
        sseSource: null, responseData: null, responseError: null, streamMessages: [], statusInfo: null,
        duplexConnected: false, duplexSseSource: null, sentMessages: [], sentCount: 0, receivedCount: 0 });
    const window = {
        location: { search, href: 'http://h/bowire' + search },
        opener,
        open: (url, name) => { calls.opened.push({ url, name }); return { closed: false, focus() {} }; },
        addEventListener: (type, fn) => { listeners[type] = fn; },
        history: { replaceState() {} },
    };
    const host = {
        window,
        document: { documentElement: { classList: { add() {}, remove() {} } }, title: '' },
        requestTabs,
        tabById: (id) => requestTabs.find((t) => t.id === id) || null,
        tabState: (tab) => { if (!tab.state) tab.state = newTabState(); return tab.state; },
        newTabState,
        releaseTabState: (tab) => { calls.released.push(tab.id); if (tab.state) tab.state.sseSource = null; },
        render: () => { calls.renders++; },
        afterRender: (fn) => fn(),
        persistRequestTabs: () => { calls.persisted++; },
        switchTab: (id) => { calls.switched.push(id); },
        focusedPane: () => ({ id: 'pane_1' }),
        services: [{ name: 'Svc', methods: [{ name: 'Watch' }] }],
        bumpTabIdCounterPast: () => {},
        handleExecute: () => { calls.executed++; },
        toast: (m) => { calls.toasts.push(m); },
        el: (tag, props, ...children) => ({ tag, props, children }),
        svgIcon: () => '',
        railMode: 'home',
        requestPanes: [{ id: 'pane_1' }],
        focusPaneTab: () => {},
        freeformRequest: null,
        requestTabsRehydrated: false,
        rehydrateRequestTabs: () => {},
    };
    // URL is a real global in node; the fragment uses it for the address.
    return { m: load(host), calls, listeners, window, requestTabs };
}

const streamingTab = () => ({
    id: 'tab_3', serviceKey: 'Svc', methodKey: 'Watch', method: { name: 'Watch' },
    state: {
        formValues: { city: 'Kiel' }, requestMessages: ['{"city":"Kiel"}'], requestInputMode: 'json',
        isExecuting: true, sseSource: { close() {} }, responseData: null, responseError: null,
        streamMessages: [{ index: 0, data: '{"t":1}' }, { index: 1, data: '{"t":2}' }], statusInfo: { status: 'OK' },
        duplexConnected: false, duplexSseSource: null, sentMessages: [], sentCount: 0, receivedCount: 2,
    },
});

test('a tab travels with what it holds, the frames so far, and what was running', () => {
    const { m } = setup();
    const snap = m.popoutSnapshot(streamingTab());

    assert.equal(snap.running, 'stream');
    assert.deepEqual(snap.state.streamMessages.map((f) => f.data), ['{"t":1}', '{"t":2}']);
    assert.deepEqual(snap.state.formValues, { city: 'Kiel' });
    assert.equal('sseSource' in snap.state, false, 'the live source stays behind');
});

test('what was running is named: a stream, a channel, a call, or nothing', () => {
    const { m } = setup();
    const tab = streamingTab();
    tab.state.sseSource = null;
    tab.state.duplexConnected = true;
    assert.equal(m.popoutSnapshot(tab).running, 'channel');
    tab.state.duplexConnected = false;
    assert.equal(m.popoutSnapshot(tab).running, 'call');
    tab.state.isExecuting = false;
    assert.equal(m.popoutSnapshot(tab).running, null);
});

test('the main window opens the pop-out at its own address and marks the tab as away', () => {
    const tab = streamingTab();
    const { m, calls } = setup({ tabs: [tab] });

    m.popOutTab('tab_3');

    assert.equal(calls.opened.length, 1);
    assert.match(calls.opened[0].url, /[?&]popout=tab_3/);
    assert.ok(m.tabIsPoppedOut(tab));
    assert.ok(calls.persisted > 0, 'the strip is stored without the tab');
    assert.deepEqual(calls.released, [], 'it keeps running until the pop-out takes it');
});

test('when the pop-out takes the tab, the main window lets go of it', () => {
    const tab = streamingTab();
    const { m, calls } = setup({ tabs: [tab] });

    const taken = JSON.parse(m.popoutTake('tab_3'));

    assert.equal(taken.running, 'stream');
    assert.equal(taken.state.streamMessages.length, 2);
    assert.deepEqual(calls.released, ['tab_3']);
    assert.ok(tab.poppedOut);
    assert.equal(tab.state.streamMessages.length, 0, 'what it held went with it');
});

test('a returned tab gets its state back, comes to the front, and its stream starts again', () => {
    const tab = streamingTab();
    const { m, calls } = setup({ tabs: [tab] });
    const snap = m.popoutTake('tab_3');

    m.popoutReturn('tab_3', snap);

    assert.equal(m.tabIsPoppedOut(tab), false);
    assert.equal(tab.state.streamMessages.length, 2);
    assert.equal(tab.state.movedNotice, 'stream');
    assert.deepEqual(calls.switched, ['tab_3']);
    assert.equal(calls.executed, 1);
});

test('a returned call is not run again: it may change something', () => {
    const tab = streamingTab();
    tab.state.sseSource = null;  // a unary call in flight
    const { m, calls } = setup({ tabs: [tab] });

    m.popoutReturn('tab_3', m.popoutTake('tab_3'));

    assert.equal(tab.state.movedNotice, 'call');
    assert.equal(calls.executed, 0);
});

test('a tab returned to a main window that no longer has it is rebuilt', () => {
    const { m, requestTabs } = setup({ tabs: [] });
    const snap = JSON.stringify({ id: 'tab_9', serviceKey: 'Svc', methodKey: 'Watch', state: { formValues: { a: 1 } }, running: null });

    m.popoutReturn('tab_9', snap);

    assert.equal(requestTabs.length, 1);
    assert.equal(requestTabs[0].id, 'tab_9');
    assert.deepEqual(requestTabs[0].state.formValues, { a: 1 });
});

test('the tab menu offers a window of its own, or the way back', () => {
    const tab = streamingTab();
    const { m } = setup({ tabs: [tab] });
    assert.deepEqual(m.popoutMenuEntries('tab_3').map((e) => e.label), ['Open in new window']);
    tab.poppedOut = {};
    assert.deepEqual(m.popoutMenuEntries('tab_3').map((e) => e.label), ['Bring back to the main window']);
    assert.deepEqual(m.popoutMenuEntries('nope'), []);
});

test('a pop-out takes its tab from the main window as it starts', () => {
    const snap = JSON.stringify({ id: 'tab_3', serviceKey: 'Svc', methodKey: 'Watch', state: {}, running: null });
    const opener = { closed: false, bowirePopoutTake: (id) => (id === 'tab_3' ? snap : null) };

    const { m } = setup({ search: '?popout=tab_3', opener });

    assert.equal(m.isPopout(), true);
    assert.equal(m.adopted().id, 'tab_3');
});

test('a pop-out address with no main window to take from is an ordinary workbench', () => {
    const { m } = setup({ search: '?popout=tab_3', opener: null });

    assert.equal(m.isPopout(), false);
});
