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
    var userPickerOpen = false;
    var userPickerCandidates = null;
    var userPickerError = '';

    /** True once the server has said this install serves several people. */
    function isMultiTenant() {
        return !!(bowireIdentity && bowireIdentity.multiTenant);
    }

    // ---- whose work is this ----
    // A person looking at a list of recordings on a shared instance is asking
    // one question: are these mine, or everyone's? The answer belongs in the
    // label they are already reading, not in a footnote somewhere else on the
    // page. In a single-user install the question does not arise, and the
    // wording stays as it was.

    /** "Your recordings" where several people share an instance, else "Recordings". */
    function ownedLabel(noun) {
        return isMultiTenant() ? 'Your ' + noun.toLowerCase() : noun;
    }

    /**
     * "You have no recordings yet" rather than "No recordings yet" — the
     * difference between an empty account and an empty server, which is the
     * first thing somebody wonders when a shared instance greets them with
     * nothing.
     */
    function ownedEmpty(noun) {
        return isMultiTenant()
            ? 'You have no ' + noun.toLowerCase() + ' yet'
            : 'No ' + noun.toLowerCase() + ' yet';
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

        // Only an administrator, and only while they are themselves. The
        // banner is how somebody already in a session gets back out; offering
        // a second hop from inside one is a way to lose track of whose
        // workbench you are looking at.
        if (bowireIdentity.isAdmin && !bowireIdentity.actingAs) {
            rows.push(el('button', {
                type: 'button',
                className: 'bowire-user-chip-switch',
                textContent: 'View as another user…',
                onClick: function (e) {
                    if (e && e.stopPropagation) e.stopPropagation();
                    userChipOpen = false;
                    openUserPicker();
                },
            }));
        }

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

    // ---- acting on somebody else's behalf ----

    function renderImpersonationBanner() {
        var acting = bowireIdentity && bowireIdentity.actingAs;
        if (!acting) return null;

        var who = acting.displayName || acting.email || acting.subject;
        return el('div', {
            className: 'bowire-impersonation-banner',
            role: 'status',
        },
            el('span', { className: 'bowire-impersonation-text' },
                'Viewing as ',
                el('strong', { textContent: who }),
                (acting.email && acting.email !== who)
                    ? el('span', {
                        className: 'bowire-impersonation-email',
                        textContent: ' (' + acting.email + ')',
                    })
                    : null,
                '. Anything you change is recorded against your own account.'),
            el('button', {
                type: 'button',
                className: 'bowire-impersonation-end',
                textContent: 'Return to my workbench',
                onClick: endImpersonation,
            }));
    }

    function endImpersonation() {
        // Reload either way. Every store read the other person's slot while
        // this page was up, and re-reading them one by one is a list that goes
        // stale; a failed end that left the page as it was would also leave
        // somebody believing they had returned.
        fetch(config.prefix + '/api/impersonation', { method: 'DELETE' })
            .then(function () { window.location.reload(); })
            .catch(function () { window.location.reload(); });
    }

    function beginImpersonation(subject) {
        userPickerError = '';
        fetch(config.prefix + '/api/impersonation', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ subject: subject }),
        })
            .then(function (r) {
                if (!r.ok) throw new Error(String(r.status));
                window.location.reload();
            })
            .catch(function () {
                // Stay open with the reason visible: a picker that closed on
                // failure looks exactly like one that worked.
                userPickerError = 'That did not go through. Nothing changed.';
                renderUserPicker();
            });
    }

    // ---- the picker ----

    function openUserPicker() {
        userPickerOpen = true;
        userPickerCandidates = null;
        userPickerError = '';
        renderUserPicker();
        searchUsers('');
        if (typeof render === 'function') render();
    }

    function closeUserPicker() {
        userPickerOpen = false;
        var existing = document.querySelector('.bowire-user-picker');
        if (existing && existing.parentNode) existing.parentNode.removeChild(existing);
    }

    function searchUsers(term) {
        fetch(config.prefix + '/api/users?limit=20&q=' + encodeURIComponent(term || ''))
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (found) {
                userPickerCandidates = Array.isArray(found) ? found : [];
                renderUserPicker();
            })
            .catch(function () {
                userPickerCandidates = [];
                userPickerError = 'Could not read the user list.';
                renderUserPicker();
            });
    }

    function renderUserPickerRows() {
        if (userPickerCandidates === null) {
            return [el('p', { className: 'bowire-user-picker-empty', textContent: 'Looking…' })];
        }

        if (userPickerCandidates.length === 0) {
            // Not "no results": an install with no directory that lists other
            // identities has nobody it *can* name, and saying so is the
            // difference between a search that found nothing and a feature
            // that cannot work here.
            return [el('p', { className: 'bowire-user-picker-empty' },
                'Nobody to show. This instance has no directory listing other identities.')];
        }

        return userPickerCandidates.map(function (candidate) {
            return el('button', {
                type: 'button',
                className: 'bowire-user-picker-row',
                onClick: function () { beginImpersonation(candidate.subject); },
            },
                el('span', {
                    className: 'bowire-user-picker-name',
                    textContent: candidate.displayName || candidate.email || candidate.subject,
                }),
                candidate.email
                    ? el('span', { className: 'bowire-user-picker-email', textContent: candidate.email })
                    : null);
        });
    }

    function renderUserPicker() {
        var existing = document.querySelector('.bowire-user-picker');
        if (existing && existing.parentNode) existing.parentNode.removeChild(existing);
        if (!userPickerOpen) return;

        var dialog = el('div', {
            className: 'bowire-user-picker',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': 'View as another user',
        },
            el('div', { className: 'bowire-user-picker-card' },
                el('h2', {
                    className: 'bowire-user-picker-title',
                    textContent: 'View as another user',
                }),
                el('p', { className: 'bowire-user-picker-note' },
                    'You will see their recordings, environments and collections. '
                    + 'Anything you change is recorded against your own account, '
                    + 'with theirs named alongside it.'),
                el('input', {
                    type: 'search',
                    className: 'bowire-user-picker-search',
                    placeholder: 'Search by name or address',
                    'aria-label': 'Search users',
                    onInput: function (e) { searchUsers(e && e.target ? e.target.value : ''); },
                }),
                el('div', { className: 'bowire-user-picker-list' }, renderUserPickerRows()),
                el('p', { className: 'bowire-user-picker-error', textContent: userPickerError }),
                el('div', { className: 'bowire-user-picker-actions' },
                    el('button', {
                        type: 'button',
                        className: 'bowire-btn',
                        textContent: 'Cancel',
                        onClick: closeUserPicker,
                    }))));

        document.body.appendChild(dialog);
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
