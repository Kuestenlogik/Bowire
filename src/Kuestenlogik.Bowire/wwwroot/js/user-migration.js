    // ---- Bring the single-user data across (#97, #28 Phase E) ----
    // When an install that used to have one shared ~/.bowire/ starts
    // serving identities separately, everything on disk stops being
    // where the stores look. The workbench that greets the first person
    // to sign in is empty, and the conclusion they draw is that turning
    // on authentication cost them their work.
    //
    // So we ask, once, before they can draw it.
    //
    // Deliberately outside the morphdom render tree: this is a one-time
    // decision, not application state, and a node the diff never touches
    // cannot be reused for something else while it is on screen.

    var userMigrationOffer = null;
    // The last thing the server said, whatever it said. The dialog only cares
    // about 'Available', but Settings has to be able to show — and reverse —
    // a decision long after the dialog is gone.
    var userMigrationPlan = null;

    function userMigrationSize(bytes) {
        if (!bytes) return '';
        var units = ['bytes', 'KB', 'MB', 'GB'];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
        // One decimal below a gigabyte reads as precision nobody needs.
        return (unit === 0 ? value : value.toFixed(value < 10 ? 1 : 0)) + ' ' + units[unit];
    }

    function userMigrationDismiss() {
        if (userMigrationOffer && userMigrationOffer.parentNode) {
            userMigrationOffer.parentNode.removeChild(userMigrationOffer);
        }
        userMigrationOffer = null;
    }

    function userMigrationDecide(path, onDone, onFail) {
        var buttons = userMigrationOffer
            ? userMigrationOffer.querySelectorAll('button')
            : [];
        for (var i = 0; i < buttons.length; i++) buttons[i].disabled = true;

        fetch(config.prefix + '/api/migration/' + path, { method: 'POST' })
            .then(function (r) {
                if (!r.ok) throw new Error(String(r.status));
                return r.json();
            })
            .then(onDone)
            .catch(function () {
                // Leave the offer standing. A failed accept that quietly
                // disappeared would look exactly like a successful one,
                // and the difference is a year of collections.
                for (var j = 0; j < buttons.length; j++) buttons[j].disabled = false;
                var note = userMigrationOffer &&
                    userMigrationOffer.querySelector('.bowire-migration-error');
                if (note) {
                    note.textContent =
                        t('userMigration.failed');
                }
                // Called from Settings there is no dialog to write into, and
                // a failure nobody is told about is the one failure mode this
                // whole feature exists to avoid.
                if (typeof onFail === 'function') onFail();
            });
    }

    function renderUserMigrationOffer(plan) {
        var count = plan.files || 0;
        var size = userMigrationSize(plan.bytes);

        return el('div', {
            className: 'bowire-migration-offer',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-labelledby': 'bowire-migration-title',
        },
            el('div', { className: 'bowire-migration-card' },
                el('h2', {
                    id: 'bowire-migration-title',
                    className: 'bowire-migration-title',
                    textContent: t('migration.title'),
                }),
                el('p', { className: 'bowire-migration-body' },
                    // #688 - one message, two shapes. The bracketed size arrives
                    // ready-made or empty; brackets read the same in every language.
                    t(count === 1 ? 'migration.body.one' : 'migration.body.many', {
                        count: count, size: size ? ' (' + size + ')' : ''
                    })),
                el('p', { className: 'bowire-migration-note' }, t('migration.note')),
                el('p', {
                    className: 'bowire-migration-source',
                    title: plan.source,
                    textContent: plan.source,
                }),
                el('p', { className: 'bowire-migration-error' }),
                el('div', { className: 'bowire-migration-actions' },
                    el('button', {
                        type: 'button',
                        className: 'bowire-btn',
                        textContent: t('migration.startFresh'),
                        onClick: function () {
                            userMigrationDecide('decline', userMigrationDismiss);
                        },
                    }),
                    el('button', {
                        type: 'button',
                        className: 'bowire-btn bowire-migration-go',
                        textContent: t('migration.copy'),
                        onClick: function () {
                            userMigrationDecide('accept', function () {
                                // Every store read its (empty) slot while the
                                // page was loading. Re-reading them one by one
                                // is a list that goes stale; reloading is not.
                                window.location.reload();
                            });
                        },
                    }))));
    }

    function fetchUserMigrationOffer() {
        try {
            fetch(config.prefix + '/api/migration')
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (plan) {
                    userMigrationPlan = plan || null;
                    // Every other state is a state with nothing to ask about:
                    // the install is single-user, the person already decided,
                    // there is nothing on disk, or their slot has work in it.
                    if (!plan || plan.state !== 'Available') return;
                    userMigrationDismiss();
                    userMigrationOffer = renderUserMigrationOffer(plan);
                    document.body.appendChild(userMigrationOffer);
                })
                .catch(function () { /* single-user install, or offline */ });
        } catch { /* fetch threw synchronously */ }
    }

    // ---- Settings → Data ----
    // The dialog is a one-time question and is gone by the time anyone wants
    // to reverse the answer. This row is where that stays reachable.

    function userMigrationSettingsCopy(plan) {
        var when = plan.decidedUtc
            ? t('migration.settings.on', { date: String(plan.decidedUtc).slice(0, 10) })
            : '';

        if (plan.state === 'Available') {
            return {
                label: t('migration.settings.label'),
                description: t('migration.settings.available'),
                action: t('migration.settings.review'),
                run: function () { fetchUserMigrationOffer(); if (typeof closeSettings === 'function') closeSettings(); },
            };
        }
        if (plan.outcome === 'Migrated') {
            return {
                label: t('migration.settings.label'),
                description: t('migration.settings.migrated', { when: when }),
                action: t('common.undo'),
                run: function () { userMigrationDecide('undo', userMigrationReload, userMigrationComplain); },
            };
        }
        if (plan.outcome === 'Declined') {
            return {
                label: t('migration.settings.label'),
                description: t('migration.settings.declined', { when: when }),
                action: t('migration.settings.offerAgain'),
                run: function () { userMigrationDecide('undo', userMigrationReload, userMigrationComplain); },
            };
        }
        return null;
    }

    function userMigrationReload() { window.location.reload(); }

    function userMigrationComplain() {
        if (typeof toast === 'function') {
            toast(t('migration.failed'), 'error');
        }
    }

    function renderUserMigrationSettings() {
        // Nothing known, or nothing to say: no row rather than an empty one.
        // A single-user install must not carry a remnant of a feature it does
        // not have.
        if (!userMigrationPlan) { fetchUserMigrationOffer(); return null; }

        var copy = userMigrationSettingsCopy(userMigrationPlan);
        if (!copy || typeof renderSettingsRow !== 'function') return null;

        return renderSettingsRow(copy.label, copy.description, function () {
            return el('button', {
                type: 'button',
                className: 'bowire-btn',
                textContent: copy.action,
                onClick: copy.run,
            });
        });
    }

    if (typeof window !== 'undefined') {
        window.addEventListener('load', function () {
            // One tick, so `config` is populated by the bootstrap script.
            setTimeout(fetchUserMigrationOffer, 0);
        });
    }
