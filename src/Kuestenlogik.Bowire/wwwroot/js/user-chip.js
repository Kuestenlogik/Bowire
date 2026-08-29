    // ---- Who is signed in (#98, #28 Phase F) ----
    // Everything below the surface already knows: the tenancy scope, the
    // storage slot, the SCIM record. The workbench does not — so signing in
    // changes nothing anybody can see, and an operator demoing to their
    // compliance team cannot point at the screen and say "that is me, and
    // these recordings are mine".
    //
    // In single-user mode there is nobody to identify, and the chip stays
    // away entirely rather than rendering an empty circle.

    var bowireIdentity = null;
    var userChipOpen = false;

    /** True once the server has said this install serves several people. */
    function isMultiTenant() {
        return !!(bowireIdentity && bowireIdentity.multiTenant);
    }

    /** The name to show — never the raw subject, which is usually a GUID. */
    function userChipName() {
        if (!bowireIdentity) return '';
        return bowireIdentity.displayName
            || bowireIdentity.email
            || bowireIdentity.subject
            || '';
    }

    function renderUserChipAvatar() {
        if (bowireIdentity.picture) {
            return el('img', {
                className: 'bowire-user-chip-avatar',
                src: bowireIdentity.picture,
                alt: '',
                // A provider that stops serving the picture must not leave a
                // broken-image glyph where a face was.
                onError: function (e) {
                    if (e && e.target) e.target.style.display = 'none';
                },
            });
        }
        return el('span', {
            className: 'bowire-user-chip-avatar bowire-user-chip-initials',
            textContent: bowireIdentity.initials || '?',
            'aria-hidden': 'true',
        });
    }

    function renderUserChipPopover() {
        var rows = [
            el('div', { className: 'bowire-user-chip-name', textContent: userChipName() }),
        ];

        if (bowireIdentity.email && bowireIdentity.email !== userChipName()) {
            rows.push(el('div', {
                className: 'bowire-user-chip-email',
                textContent: bowireIdentity.email,
            }));
        }

        rows.push(el('div', {
            className: 'bowire-user-chip-role',
            textContent: bowireIdentity.isAdmin ? 'Administrator' : 'Member',
        }));

        // The whole point of the chip for a person looking at their own
        // recordings: these are yours, and they are stored apart.
        rows.push(el('div', {
            className: 'bowire-user-chip-slot',
            textContent: 'Your work is stored separately from everyone else’s.',
        }));

        if (bowireIdentity.signOutUrl) {
            rows.push(el('a', {
                className: 'bowire-user-chip-signout',
                href: bowireIdentity.signOutUrl,
                textContent: 'Sign out',
            }));
        }

        return el('div', {
            className: 'bowire-user-chip-popover',
            role: 'dialog',
            'aria-label': 'Signed in as ' + userChipName(),
        }, rows);
    }

    function renderUserChip() {
        // Nothing known yet, or a single-user install. Either way there is
        // nobody to name, and a placeholder would be a lie about the shape of
        // the deployment.
        if (!isMultiTenant()) return null;

        var chip = el('button', {
            type: 'button',
            id: 'bowire-user-chip',
            className: 'bowire-theme-toggle-btn bowire-user-chip' + (userChipOpen ? ' active' : ''),
            title: 'Signed in as ' + userChipName(),
            'aria-label': 'Signed in as ' + userChipName(),
            'aria-expanded': userChipOpen ? 'true' : 'false',
            'data-topbar-priority': '2',
            'data-topbar-label': 'Account',
            'data-topbar-group': 'account',
            onClick: function (e) {
                if (e && e.stopPropagation) e.stopPropagation();
                userChipOpen = !userChipOpen;
                if (typeof render === 'function') render();
            },
        },
            renderUserChipAvatar(),
            el('span', { className: 'bowire-user-chip-label', textContent: userChipName() }));

        if (!userChipOpen) return chip;

        return el('div', { className: 'bowire-user-chip-wrapper' },
            chip, renderUserChipPopover());
    }

    function fetchBowireIdentity() {
        try {
            fetch(config.prefix + '/api/me')
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (me) {
                    if (!me) return;
                    bowireIdentity = me;
                    // Only worth a re-render when there is something new to
                    // show; morphdom would no-op anyway, but a single-user
                    // install should not pay for a render it does not need.
                    if (me.multiTenant && typeof render === 'function') render();
                })
                .catch(function () { /* single-user, or offline */ });
        } catch { /* fetch threw synchronously */ }
    }

    if (typeof window !== 'undefined') {
        window.addEventListener('load', function () {
            // One tick, so `config` is populated by the bootstrap script.
            setTimeout(fetchBowireIdentity, 0);
        });
        // Clicking anywhere else closes the popover, the way every other
        // popover in the workbench behaves.
        window.addEventListener('click', function () {
            if (!userChipOpen) return;
            userChipOpen = false;
            if (typeof render === 'function') render();
        });
    }
