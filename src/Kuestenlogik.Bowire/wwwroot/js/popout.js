    // ---- Pop-out (#753): a tab in a browser window of its own ----
    //
    // The decisions recorded on #753, in code:
    //
    //   1. A running stream is handed over and restarted. The new window
    //      takes the tab with everything it holds, the frames so far
    //      included; the stream opens again there, and the tab says so. A
    //      unary call still in flight is not repeated (it may change
    //      something); the tab says that too.
    //   2. One owner per tab. A popped-out tab is written by its window
    //      only. The main window keeps a placeholder in the strip and
    //      persists the strip without the tab's state, as always; a pop-out
    //      persists nothing of the strip. Workspace, environments and the
    //      other shared stores stay in localStorage, read at use: a change
    //      in one window re-renders the others (last writer wins).
    //   3. Closing a pop-out brings its tab back to the main window. Closing
    //      the main window first promotes every pop-out to a full workbench,
    //      the strip the main window left behind restored around its tab.
    //   4. Focus and keyboard are per window. A pop-out holds one pane, so
    //      the pane shortcuts have nothing to switch there.
    //
    // The windows are same-origin, so they talk by calling each other
    // through window.opener and the handle window.open returned: every step
    // is a synchronous call, nothing waits on a timer. What crosses is a
    // JSON copy (no object of one window kept alive by the other), and
    // nothing of a tab's state touches localStorage, since request bodies
    // carry tokens.

    var POPOUT_PARAM = 'popout';
    var popoutWindows = {};      // main: tabId -> the pop-out's window
    var popoutTabId = (function () {
        try { return new URLSearchParams(window.location.search).get(POPOUT_PARAM); } catch { return null; }
    })();
    // True while this window is a pop-out; false from the moment it is promoted.
    var popoutMode = !!popoutTabId;
    var popoutAdopted = null;    // pop-out: the snapshot taken from the main window, until the tab is built

    /// Whether a tab of this window lives in another window right now.
    function tabIsPoppedOut(tab) {
        return !!(tab && tab.poppedOut);
    }

    // ---- the snapshot that travels ----

    function popoutSnapshot(tab) {
        var s = tab.state || newTabState(tab.method);
        var live = !!(s.sseSource || s.duplexConnected || s.duplexSseSource);
        return JSON.parse(JSON.stringify({
            id: tab.id,
            serviceKey: tab.serviceKey,
            methodKey: tab.methodKey,
            splitMode: tab.splitMode || null,
            freeform: tab.freeform || null,
            state: {
                formValues: s.formValues,
                requestMessages: s.requestMessages,
                requestInputMode: s.requestInputMode,
                responseData: s.responseData,
                responseError: s.responseError,
                streamMessages: s.streamMessages,
                statusInfo: s.statusInfo,
                sentMessages: s.sentMessages,
                sentCount: s.sentCount,
                receivedCount: s.receivedCount
            },
            // What was running, so the receiving window can say what it did about it.
            running: s.sseSource ? 'stream'
                : (s.duplexConnected || s.duplexSseSource) ? 'channel'
                : s.isExecuting ? 'call' : null,
            live: live
        }));
    }

    function popoutApplySnapshot(tab, snap) {
        var s = tabState(tab);
        var from = snap.state || {};
        ['formValues', 'requestMessages', 'requestInputMode', 'responseData', 'responseError',
            'streamMessages', 'statusInfo', 'sentMessages', 'sentCount', 'receivedCount'].forEach(function (k) {
            if (from[k] !== undefined && from[k] !== null) s[k] = from[k];
        });
        s.isExecuting = false;
        s.movedNotice = snap.running || null;
        tab.freeform = snap.freeform || tab.freeform || null;
    }

    /// After a move: run a stream again in the window that now has the tab.
    /// A channel and a unary call are left to the operator (sending again
    /// may change something); the notice says what happened.
    function popoutResume(snap) {
        if (snap.running === 'stream' && typeof handleExecute === 'function') {
            afterRender(function () { handleExecute(); });
        }
    }

    // ---- main window ----

    function popOutTab(tabId) {
        var tab = tabById(tabId);
        if (!tab || tab.empty || tabIsPoppedOut(tab) || popoutMode) return;
        var url = new URL(window.location.href);
        url.searchParams.set(POPOUT_PARAM, tabId);
        var win = window.open(url.toString(), 'bowire-popout-' + tabId, 'popup,width=1000,height=760');  // i18n-exempt: window features
        if (!win) {
            toast(t('popout.blocked'), 'error');
            return;
        }
        popoutWindows[tabId] = win;
        tab.poppedOut = { at: Date.now() };
        persistRequestTabs();
        render();
    }

    /// Called by a pop-out as it starts: hand over the tab and let go of it.
    /// Taken now, so frames that arrived while the window opened travel too.
    function popoutTake(tabId) {
        var tab = tabById(tabId);
        if (!tab || tab.empty) return null;
        var snap = popoutSnapshot(tab);
        releaseTabState(tab);
        tab.state = newTabState(tab.method);
        tab.poppedOut = { at: Date.now() };
        render();
        return JSON.stringify(snap);
    }

    /// Called by a pop-out that closes or is brought back: the tab returns.
    function popoutReturn(tabId, json) {
        var snap = null;
        try { snap = JSON.parse(json); } catch { return; }
        var tab = tabById(tabId);
        if (!tab) {
            // The main window was reloaded while the tab was away: rebuild it.
            var svc = (services || []).find(function (x) { return x.name === snap.serviceKey; });
            var meth = svc && (svc.methods || []).find(function (m) { return m.name === snap.methodKey; });
            if (!meth) return;
            tab = { id: tabId, serviceKey: snap.serviceKey, methodKey: snap.methodKey, service: svc, method: meth,
                splitMode: snap.splitMode || undefined, paneId: focusedPane().id };
            requestTabs.push(tab);
            bumpTabIdCounterPast([tab]);
        }
        delete tab.poppedOut;
        delete popoutWindows[tabId];
        tab.state = newTabState(tab.method);
        popoutApplySnapshot(tab, snap);
        switchTab(tab.id);
        persistRequestTabs();
        render();
        popoutResume(snap);
    }

    /// The main window's "Bring back": ask the pop-out to return the tab and
    /// close. A pop-out that is already gone left nothing to return; the tab
    /// comes back fresh.
    function bringBackTab(tabId) {
        var win = popoutWindows[tabId];
        if (win && !win.closed && typeof win.bowirePopoutRecall === 'function') {
            win.bowirePopoutRecall();
            return;
        }
        var tab = tabById(tabId);
        if (!tab) return;
        delete tab.poppedOut;
        delete popoutWindows[tabId];
        tab.state = newTabState(tab.method);
        tabState(tab).movedNotice = 'lost';
        switchTab(tab.id);
        render();
    }

    function focusPopout(tabId) {
        var win = popoutWindows[tabId];
        if (win && !win.closed) { try { win.focus(); } catch { /* ignore */ } }
    }

    // ---- pop-out window ----

    /// Build the tab from the snapshot once discovery has its method.
    /// Called from rehydrateRequestTabs, which render() runs until the
    /// services are in.
    function popoutTryAdopt() {
        if (!popoutAdopted || requestTabs.length > 0) return;
        var svc = (services || []).find(function (x) { return x.name === popoutAdopted.serviceKey; });
        var meth = svc && (svc.methods || []).find(function (m) { return m.name === popoutAdopted.methodKey; });
        if (!meth) return;
        var snap = popoutAdopted;
        popoutAdopted = null;
        var tab = { id: snap.id, serviceKey: snap.serviceKey, methodKey: snap.methodKey, service: svc, method: meth,
            splitMode: snap.splitMode || undefined, paneId: 'pane_1' };
        requestTabs = [tab];
        requestPanes = [{ id: 'pane_1', activeTab: tab.id, width: 100 }];
        bumpTabIdCounterPast(requestTabs);
        popoutApplySnapshot(tab, snap);
        focusPaneTab(requestPanes[0], tab.id);
        freeformRequest = tab.freeform || null;
        requestTabsRehydrated = true;
        document.title = snap.methodKey + ' — Bowire';  // i18n-exempt: a window title, method name and product
        popoutResume(snap);
    }

    function popoutReturnNow() {
        if (!popoutMode) return;
        var tab = tabById(popoutTabId);
        var opener = window.opener;
        if (!tab || !opener || opener.closed || typeof opener.bowirePopoutReturn !== 'function') return;
        try { opener.bowirePopoutReturn(popoutTabId, JSON.stringify(popoutSnapshot(tab))); } catch { /* the main window is going */ }
        releaseTabState(tab);
        popoutMode = false;  // returned: closing now must not return it twice
    }

    /// The main window closed first: become a full workbench, with the strip
    /// the main window left behind restored around this window's tab.
    function popoutPromote() {
        if (!popoutMode) return;
        popoutMode = false;
        try {
            var url = new URL(window.location.href);
            url.searchParams.delete(POPOUT_PARAM);
            window.history.replaceState(null, '', url.toString());
        } catch { /* the address bar keeps the parameter; harmless */ }
        document.documentElement.classList.remove('bowire-popout');
        var own = requestTabs.slice();
        requestTabs = [];
        requestTabsRehydrated = false;
        rehydrateRequestTabs();
        own.forEach(function (tab) {
            if (!tabById(tab.id)) { tab.paneId = focusedPane().id; requestTabs.push(tab); }
            else {
                // The stored strip has the tab (without state): keep this window's.
                var i = requestTabs.findIndex(function (x) { return x.id === tab.id; });
                tab.paneId = requestTabs[i].paneId;
                requestTabs[i] = tab;
            }
        });
        if (own[0]) switchTab(own[0].id);
        persistRequestTabs();
        toast(t('popout.promoted'), 'info');
        render();
    }

    // ---- wiring ----

    window.bowirePopoutTake = popoutTake;
    window.bowirePopoutReturn = popoutReturn;
    window.bowirePopoutRecall = function () {
        popoutReturnNow();
        window.close();
    };
    window.bowirePopoutPromote = popoutPromote;

    if (popoutMode) {
        document.documentElement.classList.add('bowire-popout');
        var openerWindow = window.opener;
        var taken = null;
        try {
            if (openerWindow && !openerWindow.closed && typeof openerWindow.bowirePopoutTake === 'function') {
                taken = openerWindow.bowirePopoutTake(popoutTabId);
            }
        } catch { taken = null; }
        if (taken) {
            try { popoutAdopted = JSON.parse(taken); } catch { popoutAdopted = null; }
        }
        // The tab shows where tabs are shown; not stored, the main window keeps its own rail.
        if (popoutAdopted) railMode = 'discover';
        if (!popoutAdopted) {
            // No main window to take a tab from (it closed, or this address
            // was opened by hand): this is a workbench window like any other.
            popoutMode = false;
            document.documentElement.classList.remove('bowire-popout');
        }
    }

    window.addEventListener('pagehide', function () {
        if (popoutMode) {
            popoutReturnNow();
            return;
        }
        // The main window goes first: every pop-out becomes a workbench.
        Object.keys(popoutWindows).forEach(function (tabId) {
            var win = popoutWindows[tabId];
            try { if (win && !win.closed && typeof win.bowirePopoutPromote === 'function') win.bowirePopoutPromote(); }
            catch { /* that window is going too */ }
        });
    });

    // Shared stores (environments, variables, workspace) are read at use;
    // a change another window made shows on the next render. The tab strip
    // is excluded: it has one writer per tab.
    window.addEventListener('storage', function (e) {
        if (!e.key || e.key.indexOf('bowire_') < 0) return;
        if (e.key.indexOf('bowire_request_tabs') >= 0 || e.key.indexOf('bowire_panes') >= 0) return;
        render();
    });

    // ---- what the main pane shows for a tab that is away ----

    function renderPoppedOutPlaceholder(tab) {
        return el('div', { className: 'bowire-popout-placeholder', role: 'status' },
            el('div', { className: 'bowire-popout-placeholder-title', textContent: t('popout.awayTitle', { method: tab.methodKey }) }),
            el('div', { className: 'bowire-popout-placeholder-text', textContent: t('popout.awayText') }),
            el('div', { className: 'bowire-popout-placeholder-actions' },
                el('button', { type: 'button', className: 'bowire-btn', textContent: t('popout.show'), onClick: function () { focusPopout(tab.id); } }),
                el('button', { type: 'button', className: 'bowire-btn bowire-btn-primary', textContent: t('popout.bringBack'), onClick: function () { bringBackTab(tab.id); } })));
    }

    /// The note a moved tab carries until the next run: what happened to
    /// what was running when it moved.
    function renderMovedNotice(state) {
        if (!state || !state.movedNotice) return null;
        var key = { stream: 'popout.noticeStream', channel: 'popout.noticeChannel', call: 'popout.noticeCall', lost: 'popout.noticeLost' }[state.movedNotice];
        if (!key) return null;
        return el('div', { className: 'bowire-popout-notice', role: 'status' },
            el('span', { textContent: t(key) }),
            el('button', {
                type: 'button', className: 'bowire-popout-notice-close', 'aria-label': t('common.close'), innerHTML: svgIcon('close'),
                onClick: function () { state.movedNotice = null; render(); }
            }));
    }

    /// The tab menu entries the pop-out adds: open in a window of its own,
    /// or (in a pop-out) bring it back.
    function popoutMenuEntries(tabId) {
        var tab = tabById(tabId);
        if (!tab || tab.empty) return [];
        if (popoutMode) {
            return [{ label: t('popout.bringBack'), icon: 'externalLink', onClick: function () { window.bowirePopoutRecall(); } }];
        }
        if (tabIsPoppedOut(tab)) {
            return [{ label: t('popout.bringBack'), onClick: function () { bringBackTab(tabId); } }];
        }
        return [{ label: t('popout.open'), icon: 'externalLink', onClick: function () { popOutTab(tabId); } }];
    }
