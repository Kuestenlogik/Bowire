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

    function userMigrationDecide(path, onDone) {
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
                        'That did not go through. Your existing data is untouched — try again.';
                }
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
                    textContent: 'Bring your existing work across?',
                }),
                el('p', { className: 'bowire-migration-body' },
                    'This Bowire now keeps each person’s work separate. There ' +
                    'is ' + (count === 1 ? 'one file' : count + ' files') +
                    (size ? ' (' + size + ')' : '') +
                    ' from before that split, and it can be copied into your account.'),
                el('p', { className: 'bowire-migration-note' },
                    'Nothing is moved or deleted — the originals stay where they ' +
                    'are. If this was somebody else’s work, start fresh and it ' +
                    'will be offered to them instead.'),
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
                        textContent: 'Start fresh',
                        onClick: function () {
                            userMigrationDecide('decline', userMigrationDismiss);
                        },
                    }),
                    el('button', {
                        type: 'button',
                        className: 'bowire-btn bowire-migration-go',
                        textContent: 'Copy it to my account',
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

    if (typeof window !== 'undefined') {
        window.addEventListener('load', function () {
            // One tick, so `config` is populated by the bootstrap script.
            setTimeout(fetchUserMigrationOffer, 0);
        });
    }
