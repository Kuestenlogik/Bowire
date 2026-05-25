    // ---- Plugin update-check badge ----
    // Renders a count badge over the Settings gear when the cached
    // update-check snapshot reports one or more plugins with a newer
    // version on nuget.org. Opt-in: when
    // Bowire:PluginUpdateCheck:Enabled is false, the status endpoint
    // returns enabled=false and the badge stays hidden so air-gapped
    // installs see no visual remnant of a feature they didn't ask for.

    var pluginUpdateCheckStatus = { enabled: false, cached: null };

    function pluginUpdateBadgeCount() {
        var c = pluginUpdateCheckStatus.cached;
        if (!c || !Array.isArray(c.Results)) return 0;
        var n = 0;
        for (var i = 0; i < c.Results.length; i++) {
            if (c.Results[i] && c.Results[i].UpdateAvailable) n++;
        }
        return n;
    }

    function renderPluginUpdateBadge() {
        var count = pluginUpdateBadgeCount();
        if (!pluginUpdateCheckStatus.enabled || count <= 0) return null;
        return el('span', {
            className: 'bowire-plugin-update-badge',
            title: count + ' plugin update(s) available — open Settings → Plugins',
            'aria-label': count + ' plugin updates available',
            textContent: count > 9 ? '9+' : String(count),
        });
    }

    function fetchPluginUpdateStatus() {
        try {
            fetch(config.prefix + '/api/plugins/check-updates/status')
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (body) {
                    if (!body) return;
                    pluginUpdateCheckStatus = body;
                    // Re-render the topbar so the badge appears /
                    // disappears without a full reload. morphdom in
                    // render() is a no-op when nothing changed, so
                    // this is cheap.
                    if (typeof render === 'function') render();
                })
                .catch(function () { /* offline / disabled / first start */ });
        } catch { /* fetch threw synchronously */ }
    }

    // Kick off one fetch on page load; the daily background job (when
    // opted in) refreshes the cache, and an explicit "Check now" click
    // inside the Plugins panel will re-fetch via this same function.
    if (typeof window !== 'undefined') {
        window.addEventListener('load', function () {
            // Defer one tick so the initial render has run and `config`
            // is already populated by the bootstrap script.
            setTimeout(fetchPluginUpdateStatus, 0);
        });
    }
