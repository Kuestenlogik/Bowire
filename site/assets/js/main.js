// Navbar background on scroll
const header = document.getElementById('site-header');
if (header) {
    window.addEventListener('scroll', () => {
        header.classList.toggle('scrolled', window.scrollY > 20);
    }, { passive: true });
}

// Mobile menu toggle
const toggle = document.getElementById('mobile-toggle');
const nav = document.querySelector('.header-nav');
if (toggle && nav) {
    toggle.addEventListener('click', () => nav.classList.toggle('open'));
    nav.querySelectorAll('a').forEach(link =>
        link.addEventListener('click', () => nav.classList.remove('open'))
    );
}

// ====================================================================
// Comparison table — wrap plain-text status values in a span so they can
// render as pill-shaped chips via CSS. Runs once on load; skips cells
// that are ✓/✗ markers, feature labels, or already wrapped.
// ====================================================================
(function () {
    const rows = document.querySelectorAll('.comparison-table tbody tr');
    rows.forEach(row => {
        row.querySelectorAll('td').forEach(td => {
            if (td.matches('.c-yes, .c-no, .c-partial, .comparison-feature')) return;
            const text = td.textContent.trim();
            if (!text || td.children.length > 0) return;
            td.textContent = '';
            const span = document.createElement('span');
            span.className = 'comparison-value';
            span.textContent = text;
            // Forward data-detail from the <td> onto the pill so the hover
            // tooltip sits on a small hover target instead of the whole cell.
            const detail = td.getAttribute('data-detail');
            if (detail) {
                span.setAttribute('data-detail', detail);
                td.removeAttribute('data-detail');
            }
            td.appendChild(span);
        });
    });
})();

// ====================================================================
// Comparison table — mobile tool-picker. Only two data columns fit on
// a phone: the feature names (sticky) and Bowire (sticky next to it).
// The third column rotates through the five competitors via a select
// above the table; the active value lands on <html data-comparison-
// tool="…"> and the CSS shows the matching column.
// ====================================================================
(function () {
    const wrap = document.querySelector('.comparison-table-wrap');
    if (!wrap) return;
    // [id, label] pairs match the markup column order in comparison.html
    // (Postman → 3rd column, Scalar → 4th, …). Keep these in lockstep
    // with the CSS [data-comparison-tool] selectors above.
    const tools = [
        ['postman',     'Postman'],
        ['scalar',      'Scalar'],
        ['swashbuckle', 'Swashbuckle'],
        ['insomnia',    'Insomnia'],
        ['bruno',       'Bruno'],
    ];
    const picker = document.createElement('div');
    picker.className = 'comparison-tool-picker';
    const labelEl = document.createElement('label');
    labelEl.htmlFor = 'comparison-tool-select';
    labelEl.textContent = 'Bowire vs.';
    const select = document.createElement('select');
    select.id = 'comparison-tool-select';
    for (const [id, label] of tools) {
        const opt = document.createElement('option');
        opt.value = id;
        opt.textContent = label;
        select.appendChild(opt);
    }
    picker.appendChild(labelEl);
    picker.appendChild(select);
    wrap.insertBefore(picker, wrap.firstChild);

    // Default to the first competitor; CSS hides the rest.
    const initial = tools[0][0];
    document.documentElement.setAttribute('data-comparison-tool', initial);
    select.value = initial;
    select.addEventListener('change', () => {
        document.documentElement.setAttribute('data-comparison-tool', select.value);
    });
})();

// ====================================================================
// Comparison table — expandable rows with per-item sub-rows. Click on
// the chevron in the feature column toggles visibility of all sub-rows
// sharing the same data-group. Sub-rows sit directly after the trigger
// in the DOM so the table stays semantically sound.
// ====================================================================
(function () {
    const toggles = document.querySelectorAll('.comparison-row-toggle');
    toggles.forEach(btn => {
        btn.addEventListener('click', () => {
            const tr = btn.closest('tr');
            if (!tr) return;
            const group = tr.getAttribute('data-group');
            if (!group) return;
            const subs = document.querySelectorAll(`.comparison-row-sub[data-group="${group}"]`);
            const expanded = btn.getAttribute('aria-expanded') === 'true';
            btn.setAttribute('aria-expanded', expanded ? 'false' : 'true');
            subs.forEach(sub => sub.classList.toggle('is-expanded', !expanded));
        });
    });
})();

// ====================================================================
// Hero badges — live values
//
// Pulls GitHub stars and NuGet package info (downloads + latest version)
// from public APIs so the hero badges stay in sync without a build step.
// Failures are silent — the em-dash placeholder remains so the badge still
// renders as a card, just without a number.
// ====================================================================
(function () {
    const badges = document.querySelectorAll('.hero-badge[data-badge]');
    if (badges.length === 0) return;

    function formatCount(n) {
        if (n == null || isNaN(n)) return '—';
        if (n >= 1_000_000) return (n / 1_000_000).toFixed(1).replace(/\.0$/, '') + 'M';
        if (n >= 1_000)     return (n / 1_000).toFixed(1).replace(/\.0$/, '') + 'k';
        return String(n);
    }

    function setValue(selector, value) {
        const el = document.querySelector(`.hero-badge[data-badge="${selector}"] [data-badge-value]`);
        if (el && value !== undefined && value !== null) {
            el.textContent = value;
            el.setAttribute('data-badge-loaded', 'true');
        }
    }

    // GitHub stargazers — unauthenticated endpoint, 60 requests/hour/IP.
    // Cached by the browser so casual traffic won't hit the limit.
    fetch('https://api.github.com/repos/Kuestenlogik/Bowire', { headers: { 'Accept': 'application/vnd.github+json' } })
        .then(r => r.ok ? r.json() : null)
        .then(data => { if (data) setValue('gh-stars', formatCount(data.stargazers_count)); })
        .catch(() => {});

    // NuGet — azuresearch returns totalDownloads + version in one call.
    // CORS-enabled, no auth required.
    fetch('https://azuresearch-usnc.nuget.org/query?q=packageid:Kuestenlogik.Bowire&prerelease=false&take=1')
        .then(r => r.ok ? r.json() : null)
        .then(data => {
            if (data && data.data && data.data[0]) {
                const pkg = data.data[0];
                setValue('nuget-downloads', formatCount(pkg.totalDownloads));
                setValue('nuget-version', 'v' + pkg.version);
            }
        })
        .catch(() => {});
})();

// ====================================================================
// Downloads quick-picker — auto-select the matching release asset
// based on the browser's platform, and wire the Download button to
// the selected option's URL. One click, no scrolling.
// ====================================================================
(function () {
    const btn = document.getElementById('quick-download-btn');
    const toggle = document.getElementById('quick-download-toggle');
    const label = document.getElementById('quick-download-label');
    const menu = document.getElementById('quick-download-list');
    if (!btn || !toggle || !label || !menu) return;
    const items = Array.from(menu.querySelectorAll('li[role="option"]'));
    if (items.length === 0) return;

    function detectPreferred() {
        const ua = (navigator.userAgent || '').toLowerCase();
        const platform = (navigator.platform || '').toLowerCase();
        const uaData = navigator.userAgentData;
        const p = uaData && uaData.platform ? uaData.platform.toLowerCase() : '';
        if (p.includes('windows') || ua.includes('win') || platform.includes('win')) return 'win-x64-msi';
        if (p.includes('mac') || ua.includes('mac') || platform.includes('mac')) return 'osx-arm64';
        if (p.includes('linux') || ua.includes('linux') || platform.includes('linux')) {
            if (ua.includes('aarch64') || ua.includes('arm64')) return 'linux-arm64';
            return 'linux-x64';
        }
        return 'win-x64-msi';
    }

    function select(li) {
        items.forEach(i => i.setAttribute('aria-selected', 'false'));
        li.setAttribute('aria-selected', 'true');
        label.textContent = li.textContent;
        btn.setAttribute('href', li.dataset.url || '#');
    }

    function openMenu() {
        menu.hidden = false;
        toggle.setAttribute('aria-expanded', 'true');
        const current = items.find(i => i.getAttribute('aria-selected') === 'true') || items[0];
        current.focus();
    }

    function closeMenu() {
        menu.hidden = true;
        toggle.setAttribute('aria-expanded', 'false');
    }

    function focusDelta(delta) {
        const idx = items.indexOf(document.activeElement);
        const next = items[(idx + delta + items.length) % items.length];
        next.focus();
    }

    toggle.addEventListener('click', (e) => {
        // Stop the bubble so the document-level "click outside" listener
        // doesn't immediately close the menu we just opened.
        e.stopPropagation();
        if (menu.hidden) openMenu();
        else closeMenu();
    });

    items.forEach(li => {
        li.addEventListener('click', (e) => {
            e.stopPropagation();
            select(li);
            closeMenu();
            toggle.focus();
        });
        li.addEventListener('keydown', e => {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                select(li);
                closeMenu();
                toggle.focus();
            } else if (e.key === 'ArrowDown') {
                e.preventDefault();
                focusDelta(1);
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                focusDelta(-1);
            } else if (e.key === 'Escape') {
                e.preventDefault();
                closeMenu();
                toggle.focus();
            }
        });
    });

    document.addEventListener('click', e => {
        if (!menu.hidden && !menu.contains(e.target) && e.target !== toggle && !toggle.contains(e.target)) {
            closeMenu();
        }
    });

    // Initial pre-selection based on browser platform.
    const preferred = detectPreferred();
    const match = items.find(i => i.dataset.value === preferred) || items[0];
    select(match);
})();

// Copy buttons
document.querySelectorAll('.copy-btn').forEach(btn => {
    btn.addEventListener('click', (e) => {
        // Copy-buttons often live inside an enclosing <a> (e.g. protocol
        // cards that link to the protocol docs). Stop the click from
        // triggering the outer link navigation.
        e.preventDefault();
        e.stopPropagation();
        const text = btn.dataset.copy;
        if (!text) return;
        navigator.clipboard.writeText(text).then(() => {
            btn.classList.add('copied');
            setTimeout(() => btn.classList.remove('copied'), 1500);
        });
    });
});

// Screenshot carousel — auto-drift, prev/next buttons, dot indicators,
// click-to-jump. Auto-drift is JS-driven (not CSS keyframe) so pause /
// resume is seamless: the current scroll position stays put, the drift
// just picks up from wherever the reader left off.
//
// Pause triggers:   hover, focus-within, pointer down (touch), dot click,
//                   prev/next button, arrow keys, manual scroll
// Resume behaviour: 5 s after the last interaction the drift starts again
(function () {
    const carousel = document.querySelector('.screenshot-carousel');
    const track    = document.querySelector('.screenshot-carousel-track');
    const dotsRow  = document.querySelector('.screenshot-dots');
    const prevBtn  = document.querySelector('.screenshot-nav-prev');
    const nextBtn  = document.querySelector('.screenshot-nav-next');
    if (!carousel || !track || !dotsRow) return;

    const cards = Array.from(track.querySelectorAll('.screenshot-card'));
    if (cards.length === 0) return;

    // ---- Infinite-loop: triple the card set so the reader always has
    //      cards to the left *and* the right of the focused one. Layout is
    //      [prefix clones] + [originals] + [suffix clones]. Auto-drift and
    //      manual scrolling stay inside the middle "safe zone"; once they
    //      wander into a clone set we invisibly subtract/add one set length
    //      so the reader stays on visually identical content. ----
    const originalCount = cards.length;

    function makeClone(card) {
        const clone = card.cloneNode(true);
        clone.setAttribute('aria-hidden', 'true');
        clone.setAttribute('tabindex', '-1');
        clone.classList.add('is-clone');
        return clone;
    }
    // Suffix clones — appended after originals.
    cards.forEach(card => track.appendChild(makeClone(card)));
    // Prefix clones — prepended in reverse so DOM order matches originals.
    for (let i = originalCount - 1; i >= 0; i--) {
        track.insertBefore(makeClone(cards[i]), track.firstChild);
    }

    // ---- Build the dot row (one dot per card) ----
    cards.forEach((card, idx) => {
        const dot = document.createElement('button');
        dot.type = 'button';
        dot.className = 'screenshot-dot';
        dot.role = 'tab';
        const label = card.querySelector('h4')?.textContent?.trim() || `Screenshot ${idx + 1}`;
        dot.setAttribute('aria-label', label);
        dot.dataset.idx = String(idx);
        // Inner span carries the card title as a hover tooltip — mirrors
        // the section-nav dots on the right edge of the page.
        const labelSpan = document.createElement('span');
        labelSpan.className = 'screenshot-dot-label';
        labelSpan.textContent = label;
        dot.appendChild(labelSpan);
        dot.addEventListener('click', () => { scrollToCard(idx); nudgePauseTimer(); });
        dotsRow.appendChild(dot);
    });
    const dots = Array.from(dotsRow.querySelectorAll('.screenshot-dot'));

    // ---- Auto-drift controller ----
    const DRIFT_PX_PER_SEC    = 28;    // leisurely film-strip cadence
    const DRIFT_INTERVAL_MS   = 30;
    const RESUME_AFTER_MS     = 5000;  // inactivity before drift restarts

    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let pausedUntil = 0;
    // Sub-pixel accumulator — scrollLeft is snapped to an integer, so fractional
    // per-tick deltas would silently stall the drift.
    let driftAccum = 0;
    let lastTickAt = performance.now();

    function nudgePauseTimer() {
        pausedUntil = Date.now() + RESUME_AFTER_MS;
    }

    // Only hover pauses auto-scroll "live" — keyboard focus used to pause too,
    // but it made the drift feel broken in browsers that gave the focusable
    // carousel region implicit focus on load. Explicit interactions (clicks,
    // arrow keys, manual scroll) drive the 5s resume timer via nudgePauseTimer.
    function isHovered() {
        return carousel.matches(':hover');
    }

    // Length of one complete card set (13 cards + gaps). Re-measured on
    // every resize. The "safe zone" for scrollLeft is [setLength, 2*setLength]
    // — that's where the originals live. If drift or manual scroll moves
    // beyond either edge, we subtract/add one setLength to snap back invisibly.
    let setLength = 0;
    function measureSetLength() {
        const firstOriginal = track.children[originalCount];            // idx N (after N prefix clones)
        const firstSuffix   = track.children[originalCount * 2];        // idx 2N
        if (firstOriginal && firstSuffix) {
            setLength = firstSuffix.offsetLeft - firstOriginal.offsetLeft;
        }
    }
    function initialScroll() {
        // Centre the SECOND original card — that way the first original card
        // sits on the left of the viewport (fully visible) and readers
        // immediately see two cards to the right, signalling "there's more".
        measureSetLength();
        const secondOriginal = track.children[originalCount + 1];
        if (!secondOriginal || setLength <= 0) return;
        const centreOffset = secondOriginal.offsetLeft + secondOriginal.offsetWidth / 2
                           - carousel.clientWidth / 2;
        markProgrammatic();
        carousel.scrollLeft = Math.max(0, centreOffset);
    }
    // Wait for the browser to finish layout before measuring / seeking —
    // offsetLeft can be stale or zero until after the first paint.
    requestAnimationFrame(() => {
        measureSetLength();
        initialScroll();
    });
    if (typeof ResizeObserver !== 'undefined') {
        new ResizeObserver(() => { measureSetLength(); }).observe(track);
    }
    window.addEventListener('load',   () => { measureSetLength(); initialScroll(); });
    window.addEventListener('resize', () => { measureSetLength(); });

    function driftTick() {
        const now = performance.now();
        const dtMs = now - lastTickAt;
        lastTickAt = now;

        if (reduceMotion) return;
        if (isHovered()) { nudgePauseTimer(); return; }
        if (Date.now() < pausedUntil) return;
        if (setLength <= 0) return;

        driftAccum += DRIFT_PX_PER_SEC * (dtMs / 1000);
        const whole = Math.floor(driftAccum);
        if (whole < 1) return;
        driftAccum -= whole;

        let next = carousel.scrollLeft + whole;
        // Infinite-loop wrap: keep scrollLeft inside the middle original set.
        // Viewport centre < setLength  → we're in the prefix zone  → +setLength
        // Viewport centre > 2*setLength → we're in the suffix zone → -setLength
        const viewportCentre = next + carousel.clientWidth / 2;
        if (viewportCentre >= 2 * setLength) next -= setLength;
        else if (viewportCentre <  setLength) next += setLength;

        markProgrammatic();
        carousel.scrollLeft = next;
    }
    setInterval(driftTick, DRIFT_INTERVAL_MS);

    // ---- Navigation helpers ----
    function scrollToCard(idx) {
        if (idx < 0) idx = 0;
        if (idx >= originalCount) idx = originalCount - 1;
        // Re-measure before every jump: the track width or set length
        // might have changed since the last resize/observer callback.
        measureSetLength();
        // Always centre the card from one of the three sets (prefix clone,
        // original, suffix clone). Pick whichever instance is closest to
        // the current scroll position so the smooth-scroll takes the shortest
        // path — critical with the 3-set infinite loop.
        const candidates = [
            track.children[idx],                     // prefix clone
            track.children[originalCount + idx],     // original
            track.children[originalCount * 2 + idx]  // suffix clone
        ].filter(Boolean);
        const carouselRect = carousel.getBoundingClientRect();
        let bestTarget = 0, bestDist = Infinity;
        candidates.forEach(card => {
            const cardRect = card.getBoundingClientRect();
            const delta = (cardRect.left - carouselRect.left)
                        - (carouselRect.width - cardRect.width) / 2;
            const t = Math.max(
                0,
                Math.min(carousel.scrollLeft + delta, track.scrollWidth - carousel.clientWidth)
            );
            const d = Math.abs(t - carousel.scrollLeft);
            if (d < bestDist) { bestDist = d; bestTarget = t; }
        });
        markProgrammatic();
        carousel.scrollTo({ left: bestTarget, behavior: 'smooth' });
    }

    function currentCardIndex() {
        // Active dot tracks the card whose centre is closest to the
        // carousel's centre. Walk *every* card slot (originals + clones)
        // so the dot stays in sync even when the reader has drifted into
        // cloned territory; then fold the result back into the 0..N-1
        // original range with a modulo.
        const carouselRect = carousel.getBoundingClientRect();
        const ref = carouselRect.left + carouselRect.width / 2;
        const allSlots = track.querySelectorAll('.screenshot-card');
        let bestSlot = 0;
        let bestDist = Infinity;
        allSlots.forEach((card, i) => {
            const r = card.getBoundingClientRect();
            const cardCentre = r.left + r.width / 2;
            const d = Math.abs(cardCentre - ref);
            if (d < bestDist) { bestDist = d; bestSlot = i; }
        });
        return bestSlot % originalCount;
    }

    function updateDots() {
        const idx = currentCardIndex();
        dots.forEach((d, i) => {
            const on = i === idx;
            d.classList.toggle('active', on);
            d.setAttribute('aria-selected', on ? 'true' : 'false');
        });
    }

    // ---- Scroll listener: keep dots in sync, detect manual scrolls ----
    // Smooth scrolling (scrollTo with behavior: 'smooth') fires many scroll
    // events over ~500ms; tracking "is this programmatic?" with a one-shot
    // flag resets on the first event and mis-classifies the rest as user
    // scrolling — which repeatedly nudged pausedUntil into the future and
    // left auto-drift stuck forever after any button/dot click. Switch to
    // a timestamp window: anything within 800ms of the last programmatic
    // scroll trigger is treated as programmatic.
    let programmaticUntil = 0;
    function markProgrammatic() { programmaticUntil = Date.now() + 800; }

    carousel.addEventListener('scroll', () => {
        updateDots();
        if (Date.now() >= programmaticUntil) {
            // Wheel / trackpad / touch-drag from the reader — give the auto
            // drift some breathing room before it picks up again.
            nudgePauseTimer();
        }
    }, { passive: true });

    prevBtn?.addEventListener('click', () => { scrollToCard(currentCardIndex() - 1); nudgePauseTimer(); });
    nextBtn?.addEventListener('click', () => { scrollToCard(currentCardIndex() + 1); nudgePauseTimer(); });

    carousel.addEventListener('keydown', (e) => {
        if (e.key === 'ArrowRight') { e.preventDefault(); scrollToCard(currentCardIndex() + 1); nudgePauseTimer(); }
        if (e.key === 'ArrowLeft')  { e.preventDefault(); scrollToCard(currentCardIndex() - 1); nudgePauseTimer(); }
    });

    updateDots();
})();

// Floating section navigation — scrollspy dots on the right edge. One dot
// per top-level <section>, the active one follows scroll position, hover
// reveals the section label, click jumps there. Replaces the earlier up/down
// arrow pair: readers see how deep the page runs AND where they are.
(function () {
    // One dot per section — ignore the sections that don't contain meaningful
    // top-level content (none right now, but keeps the door open).
    const sections = Array.from(document.querySelectorAll('main section, section'))
        .filter(s => s.offsetParent !== null || s.classList.contains('hero'));
    if (sections.length < 2) return;

    function sectionLabel(section) {
        // Prefer the <h2 class="section-title"> content; fall back to
        // any <h2>/<h1>, then any <h3>, then "Top" for the hero.
        // The h3 fallback covers /why-bowire.html where each section
        // wraps a single .features-block whose heading is an <h3>
        // (the page's only <h1> is in the hero, no <h2>s anywhere
        // else) — without it the outline modal showed six 'Section'
        // entries instead of the actual feature names.
        const title = section.querySelector('h2.section-title, h2, h1, h3');
        const text  = title ? title.textContent.trim() : '';
        if (text) return text;
        if (section.classList.contains('hero')) return 'Top';
        return 'Section';
    }

    const rail = document.createElement('aside');
    rail.className = 'section-rail';
    rail.setAttribute('aria-label', 'Page navigation rail');

    const nav = document.createElement('nav');
    nav.className = 'section-nav';
    nav.setAttribute('aria-label', 'Page sections');

    // Sample down to MAX_DOTS evenly across the page when there are more
    // sections than the pill can show without becoming a visual wall.
    // Always anchored at first + last so the strip still represents the
    // full document range; gaps between non-adjacent picks are flagged
    // with a small inline marker so the user can tell the strip is
    // sampled. Mirrors the docs site pattern.
    const MAX_DOTS = 8;
    let pickedIndexes;
    if (sections.length <= MAX_DOTS) {
        pickedIndexes = sections.map((_, i) => i);
    } else {
        const seen = new Set();
        pickedIndexes = [];
        for (let i = 0; i < MAX_DOTS; i++) {
            const idx = Math.round(i * (sections.length - 1) / (MAX_DOTS - 1));
            if (!seen.has(idx)) {
                seen.add(idx);
                pickedIndexes.push(idx);
            }
        }
    }

    const dots = [];
    pickedIndexes.forEach((sectionIdx, i) => {
        const section = sections[sectionIdx];
        const label = sectionLabel(section);
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'section-nav-dot';
        btn.setAttribute('aria-label', `Jump to ${label}`);
        // Build the inner span via DOM APIs rather than innerHTML so a
        // malicious section label (rare, but in theory anything that
        // ends up in sectionLabel()) cannot inject markup.
        const dotLabelSpan = document.createElement('span');
        dotLabelSpan.className = 'section-nav-dot-label';
        dotLabelSpan.textContent = label;
        btn.appendChild(dotLabelSpan);
        btn.addEventListener('click', () => jumpToSection(sectionIdx));
        nav.appendChild(btn);
        dots.push({ section, btn, label });

        if (i < pickedIndexes.length - 1
                && pickedIndexes[i + 1] > sectionIdx + 1) {
            const gap = document.createElement('span');
            gap.className = 'section-nav-gap';
            gap.setAttribute('aria-hidden', 'true');
            nav.appendChild(gap);
        }
    });
    rail.appendChild(nav);

    // Outline tools-pill — sits below the dot-nav on the same rail.
    // Mirrors the Outline pill on the docs site: clicking the trigger
    // opens a modal with the full (un-sampled) section list, which is
    // the way to reach a section that the dot-nav sampled out.
    const outlineNav = document.getElementById('page-outline');
    const overlay = document.getElementById('bowire-page-outline-overlay');
    let outlineLinks = [];
    if (outlineNav && overlay) {
        const tools = document.createElement('div');
        tools.className = 'section-tools';
        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'section-tools-trigger';
        trigger.setAttribute('aria-controls', 'bowire-page-outline-overlay');
        trigger.setAttribute('aria-expanded', 'false');
        trigger.setAttribute('aria-label', 'On this page');
        trigger.title = 'On this page (Esc closes)';
        trigger.innerHTML = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="3" y1="6" x2="21" y2="6"/><line x1="3" y1="12" x2="21" y2="12"/><line x1="3" y1="18" x2="21" y2="18"/></svg>';
        tools.appendChild(trigger);
        rail.appendChild(tools);

        // Populate the outline modal with one link per section — full
        // list, not sampled, so it complements the (potentially sampled)
        // dot-nav by always exposing every entry.
        sections.forEach((section, idx) => {
            const label = sectionLabel(section);
            const a = document.createElement('a');
            a.href = '#';
            a.dataset.sectionIdx = String(idx);
            a.textContent = label;
            outlineNav.appendChild(a);
        });
        outlineLinks = Array.from(outlineNav.querySelectorAll('a'));

        let highlighted = -1;
        function paintOutline() {
            outlineLinks.forEach((el, i) => {
                el.classList.toggle('is-highlighted', i === highlighted);
            });
            if (highlighted >= 0 && outlineLinks[highlighted]) {
                outlineLinks[highlighted].scrollIntoView({ block: 'nearest' });
            }
        }
        function openOutline() {
            overlay.classList.add('is-open');
            overlay.setAttribute('aria-hidden', 'false');
            overlay.removeAttribute('inert');
            trigger.setAttribute('aria-expanded', 'true');
            highlighted = activeIndexFull();
            paintOutline();
        }
        function closeOutline() {
            overlay.classList.remove('is-open');
            overlay.setAttribute('aria-hidden', 'true');
            overlay.setAttribute('inert', '');
            trigger.setAttribute('aria-expanded', 'false');
            highlighted = -1;
            paintOutline();
        }
        trigger.addEventListener('click', (e) => {
            e.preventDefault();
            if (overlay.classList.contains('is-open')) closeOutline();
            else openOutline();
        });
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) closeOutline();
            const link = e.target.closest('a[data-section-idx]');
            if (!link) return;
            e.preventDefault();
            jumpToSection(parseInt(link.dataset.sectionIdx, 10));
            // Defer close so the click event finishes propagating before
            // the modal vanishes — otherwise the next ArrowDown press
            // gets eaten by the input that briefly regains focus.
            setTimeout(closeOutline, 0);
        });
        outlineNav.addEventListener('mouseover', (e) => {
            const link = e.target.closest('a[data-section-idx]');
            if (!link) return;
            const idx = outlineLinks.indexOf(link);
            if (idx === -1 || idx === highlighted) return;
            highlighted = idx;
            paintOutline();
        });
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && overlay.classList.contains('is-open')) {
                closeOutline();
                return;
            }
            if (!overlay.classList.contains('is-open')) return;
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (outlineLinks.length === 0) return;
                highlighted = Math.min(highlighted + 1, outlineLinks.length - 1);
                paintOutline();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                if (outlineLinks.length === 0) return;
                highlighted = Math.max(highlighted - 1, 0);
                paintOutline();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (highlighted >= 0 && outlineLinks[highlighted]) {
                    const idx = parseInt(outlineLinks[highlighted].dataset.sectionIdx, 10);
                    jumpToSection(idx);
                    setTimeout(closeOutline, 0);
                }
            }
        });
    }

    document.body.appendChild(rail);

    // Account for the sticky header height so the chosen section's title
    // isn't hidden behind it after scrolling.
    function headerOffset() {
        // Keep this in sync with CSS scroll-padding-top on <html> = 0.
        // Section tops land at viewport top; each section's own padding
        // keeps headings below the translucent sticky header.
        return 0;
    }

    function activeIndexFull() {
        // Compute against the FULL sections array (not just picked) so
        // scrollspy stays precise even when the page is sampled.
        const threshold = window.scrollY + headerOffset() + 40;
        let activeSection = 0;
        for (let i = 0; i < sections.length; i++) {
            const top = sections[i].getBoundingClientRect().top + window.scrollY;
            if (top <= threshold) activeSection = i;
            else break;
        }
        const docBottomReached = (window.innerHeight + window.scrollY)
                              >= (document.documentElement.scrollHeight - 4);
        if (docBottomReached) activeSection = sections.length - 1;
        return activeSection;
    }

    function activeIndex() {
        // Map the full active section onto the largest picked dot whose
        // section index is <= activeSection. With a fully-listed strip
        // (no sampling), this is just the same index.
        const activeSection = activeIndexFull();
        let dotIdx = 0;
        for (let j = 0; j < pickedIndexes.length; j++) {
            if (pickedIndexes[j] <= activeSection) dotIdx = j;
            else break;
        }
        return dotIdx;
    }

    function updateActive() {
        const idx = activeIndex();
        dots.forEach((d, i) => d.btn.classList.toggle('is-active', i === idx));
        // Mirror the active state into the outline modal's list so the
        // current section reads as "you are here" when the modal opens.
        if (outlineLinks.length > 0) {
            const fullIdx = activeIndexFull();
            outlineLinks.forEach((el, i) => {
                el.classList.toggle('is-active', i === fullIdx);
            });
        }
    }

    function jumpToSection(sectionIdx) {
        const clamped = Math.max(0, Math.min(sections.length - 1, sectionIdx));
        const target  = sections[clamped].getBoundingClientRect().top + window.scrollY - headerOffset();
        window.scrollTo({ top: target, behavior: 'smooth' });
    }

    window.addEventListener('scroll', updateActive, { passive: true });
    window.addEventListener('resize', updateActive);
    updateActive();
})();

// ====================================================================
// Features-page screenshot lightbox — click any screenshot to enlarge it
// in a modal overlay. Click the backdrop, the close button, or press
// Escape to dismiss. Lightbox DOM is created lazily on first click.
// ====================================================================
(function () {
    const triggers = document.querySelectorAll('.features-block-screenshot img');
    if (!triggers.length) return;

    let lightbox = null;
    let lightboxImg = null;

    function ensureLightbox() {
        if (lightbox) return;
        lightbox = document.createElement('div');
        lightbox.className = 'lightbox';
        lightbox.setAttribute('role', 'dialog');
        lightbox.setAttribute('aria-modal', 'true');
        lightbox.setAttribute('aria-label', 'Enlarged screenshot');
        lightbox.innerHTML = `
            <button class="lightbox-close" type="button" aria-label="Close screenshot">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
            </button>
            <img class="lightbox-img" src="" alt="">
        `;
        document.body.appendChild(lightbox);
        lightboxImg = lightbox.querySelector('.lightbox-img');

        lightbox.addEventListener('click', (e) => {
            if (e.target === lightbox || e.target.closest('.lightbox-close')) {
                closeLightbox();
            }
        });
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && lightbox.classList.contains('is-open')) {
                closeLightbox();
            }
        });
    }

    function openLightbox(src, alt) {
        ensureLightbox();
        lightboxImg.src = src;
        lightboxImg.alt = alt || '';
        lightbox.classList.add('is-open');
        document.body.style.overflow = 'hidden';
    }

    function closeLightbox() {
        lightbox.classList.remove('is-open');
        document.body.style.overflow = '';
    }

    triggers.forEach(img => {
        img.addEventListener('click', () => openLightbox(img.src, img.alt));
    });
})();

// ====================================================================
// Demo-video fullscreen — click the hero video to request fullscreen on
// the video element. The wrapping <button> handles keyboard activation
// (Enter / Space); the native fullscreen API does the heavy lifting.
// ====================================================================
(function () {
    const wrapper = document.querySelector('.demo-video-expand');
    if (!wrapper) return;
    const video = wrapper.querySelector('video');
    if (!video) return;

    wrapper.addEventListener('click', () => {
        const target = video;
        const req = target.requestFullscreen
                 || target.webkitRequestFullscreen
                 || target.msRequestFullscreen;
        if (req) req.call(target);
    });
})();

// ====================================================================
// Comparison positioning-card click → reveal the "Why not X?" panel
// inline below the strip. Second click on the active card closes the
// panel; clicking a different card switches. Only one panel open at a
// time. Bowire's card is static (no data-target).
// ====================================================================
(function () {
    const cards = document.querySelectorAll('.comparison-bestfor-card[data-target]');
    const panels = document.querySelectorAll('.comparison-bestfor-panel');
    const placeholder = document.querySelector('.comparison-bestfor-placeholder');
    if (!cards.length) return;

    function closeAll() {
        cards.forEach(c => {
            c.classList.remove('active');
            c.setAttribute('aria-selected', 'false');
        });
        panels.forEach(p => {
            p.classList.remove('active');
            p.setAttribute('hidden', '');
        });
        // Back to default empty state → show the "pick a tool" CTA.
        if (placeholder) placeholder.hidden = false;
    }

    cards.forEach(card => {
        card.addEventListener('click', () => {
            const target = card.dataset.target;
            const wasActive = card.classList.contains('active');
            closeAll();
            if (wasActive) return;  // toggle off → placeholder shown via closeAll
            card.classList.add('active');
            card.setAttribute('aria-selected', 'true');
            const panel = document.querySelector(`.comparison-bestfor-panel[data-panel="${target}"]`);
            if (panel) {
                panel.classList.add('active');
                panel.removeAttribute('hidden');
            }
            if (placeholder) placeholder.hidden = true;
        });
    });
})();

// Install section tabs
(function () {
    const tabs = document.querySelectorAll('.install-tab');
    const panels = document.querySelectorAll('.install-panel');
    if (!tabs.length) return;

    tabs.forEach(tab => {
        tab.addEventListener('click', () => {
            const target = tab.dataset.tab;
            tabs.forEach(t => {
                const isActive = t === tab;
                t.classList.toggle('active', isActive);
                t.setAttribute('aria-selected', isActive ? 'true' : 'false');
            });
            panels.forEach(panel => {
                const isActive = panel.dataset.panel === target;
                panel.classList.toggle('active', isActive);
                if (isActive) panel.removeAttribute('hidden');
                else panel.setAttribute('hidden', '');
            });
        });
    });
})();

// ====================================================================
// Theme toggle (Light / Dark / Auto)
//
// Stores the choice in localStorage['theme'] — same key the docfx site
// reads at head time, so a click here is reflected over there after the
// next navigation. The button cycles light → dark → auto; the icon
// swaps in real-time. In auto mode the CSS @media query takes over.
// ====================================================================

(function () {
    const THEME_KEY = 'theme';
    const CYCLE = ['light', 'dark', 'auto'];

    const ICONS = {
        light: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>',
        dark:  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>',
        // Half-filled circle — universally understood "auto / system" theme
        // icon. Left half outline, right half filled with currentColor.
        auto:  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M12 3 a 9 9 0 0 1 0 18 z" fill="currentColor" stroke="none"/></svg>'
    };

    function getTheme() {
        return localStorage.getItem(THEME_KEY) || 'auto';
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        // Theme-aware <video> elements come in pairs (theme-video-dark
        // and theme-video-light). The CSS swap unhides one of them on
        // each toggle, but the browser may have paused it while hidden;
        // kick the now-visible video back into autoplay after the swap.
        try {
            requestAnimationFrame(() => {
                const resolved = (theme === 'auto')
                    ? (window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark')
                    : theme;
                document.querySelectorAll('.theme-video-' + resolved).forEach(v => {
                    if (v.tagName === 'VIDEO' && v.paused) v.play().catch(() => {});
                });
            });
        } catch { /* best-effort, no-throw */ }
    }

    function updateButton(btn, theme) {
        btn.innerHTML = ICONS[theme] || ICONS.auto;
        btn.title = 'Theme: ' + theme + ' (click to cycle)';
        btn.setAttribute('aria-label', btn.title);
    }

    // Apply the stored theme as soon as the script runs (the inline
    // <script> in the head would be cleaner but the marketing site uses
    // a single deferred main.js, so a brief flash on first load is OK).
    applyTheme(getTheme());

    const themeBtn = document.getElementById('theme-toggle');
    if (themeBtn) {
        updateButton(themeBtn, getTheme());
        themeBtn.addEventListener('click', (ev) => {
            const next = CYCLE[(CYCLE.indexOf(getTheme()) + 1) % CYCLE.length];
            const commit = () => {
                localStorage.setItem(THEME_KEY, next);
                applyTheme(next);
                updateButton(themeBtn, next);
            };
            // Circular-reveal theme transition — the same pattern
            // deepwiki.com uses: capture the DOM before/after via the
            // View Transitions API, then animate ::view-transition-new(root)
            // as an expanding clip-path circle from the click point.
            // Falls back to an instant swap in browsers without the API
            // (Firefox as of mid-2026).
            if (!document.startViewTransition || ev.isTrusted === false) {
                commit();
                return;
            }
            // Anchor the circle at the button centre, not the click
            // coordinate — keyboard (Enter/Space) activation reports
            // clientX/Y = 0 which would reveal from the top-left corner.
            const rect = themeBtn.getBoundingClientRect();
            const x = rect.left + rect.width / 2;
            const y = rect.top + rect.height / 2;
            document.documentElement.style.setProperty('--theme-transition-x', x + 'px');
            document.documentElement.style.setProperty('--theme-transition-y', y + 'px');
            document.startViewTransition(commit);
        });
    }
})();

// ============================================================
// Quickstart stepper — "Straight into the water"
// Three-step launch wizard: pick boat → install → run.
// Single-source-of-truth PROTOCOLS table feeds both the protocol
// search-combobox in step 2 and the per-protocol setup notes /
// doc links in step 3.
// ============================================================

// Every protocol Bowire ships under, plus the metadata each Stepper
// surface needs. `category` splits the list into two combobox modes:
// "first-party" plugins are pulled in via NuGet by the embedded
// backend boat; "third-party" plugins live in their own repos and the
// CLI installs them on demand via `bowire plugin install`.
var BOWIRE_PROTOCOLS = [
    {
        id: 'grpc', label: 'gRPC',
        hint: 'Schema discovery via reflection',
        packageId: 'Kuestenlogik.Bowire.Protocol.Grpc',
        urlPlaceholder: 'https://api.example.com:443',
        category: 'first-party',
        defaultBackend: true,
        setupNote: 'Bowire reads gRPC services via reflection &mdash; expose it on your host: <code>builder.Services.AddGrpcReflection();</code> + <code>app.MapGrpcReflectionService();</code>',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#grpc'
    },
    {
        id: 'rest', label: 'REST / OpenAPI',
        hint: 'Discover via the OpenAPI document',
        packageId: 'Kuestenlogik.Bowire.Protocol.Rest',
        urlPlaceholder: 'https://api.example.com/swagger.json',
        category: 'first-party',
        defaultBackend: true,
        setupNote: 'Make sure ASP.NET&rsquo;s OpenAPI document is reachable: <code>builder.Services.AddOpenApi();</code> + <code>app.MapOpenApi();</code>',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#rest'
    },
    {
        id: 'graphql', label: 'GraphQL',
        hint: 'Schema via standard introspection',
        packageId: 'Kuestenlogik.Bowire.Protocol.GraphQL',
        urlPlaceholder: 'https://api.example.com/graphql',
        category: 'first-party',
        setupNote: 'Bowire runs the standard <code>__schema</code> introspection query &mdash; nothing extra needed if your GraphQL endpoint allows it.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#graphql'
    },
    {
        id: 'signalr', label: 'SignalR',
        hint: 'Hub method discovery',
        packageId: 'Kuestenlogik.Bowire.Protocol.SignalR',
        urlPlaceholder: 'https://api.example.com/chathub',
        category: 'first-party',
        setupNote: 'Hubs are picked up automatically from the host&rsquo;s <code>EndpointDataSource</code> &mdash; no extra wiring beyond <code>app.MapHub&lt;…&gt;()</code>.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#signalr'
    },
    {
        id: 'sse', label: 'SSE',
        hint: 'Server-sent events',
        packageId: 'Kuestenlogik.Bowire.Protocol.Sse',
        urlPlaceholder: 'https://api.example.com/events',
        category: 'first-party',
        setupNote: 'Mark SSE endpoints with <code>[SseEndpoint]</code> or <code>Produces("text/event-stream")</code> so Bowire can find them.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#sse'
    },
    {
        id: 'websocket', label: 'WebSocket',
        hint: 'Raw bidirectional frame stream',
        packageId: 'Kuestenlogik.Bowire.Protocol.WebSocket',
        urlPlaceholder: 'wss://api.example.com/ws',
        category: 'first-party',
        setupNote: 'Annotate <code>app.Map("/ws/...")</code> handlers with <code>[WebSocketEndpoint]</code> so Bowire picks them up at discovery time.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#websocket'
    },
    {
        id: 'mcp', label: 'MCP',
        hint: 'Model Context Protocol',
        packageId: 'Kuestenlogik.Bowire.Protocol.Mcp',
        urlPlaceholder: 'http://localhost:5003/mcp',
        category: 'first-party',
        setupNote: 'Browse remote MCP servers (Bowire as MCP client) or expose your own services via <code>endpoints.WithMcpAdapter()</code>.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#mcp'
    },
    {
        id: 'mqtt', label: 'MQTT',
        hint: 'IoT pub/sub',
        packageId: 'Kuestenlogik.Bowire.Protocol.Mqtt',
        urlPlaceholder: 'mqtt://broker.example:1883',
        category: 'first-party',
        setupNote: 'Bowire talks to any MQTT 3.1.1 / 5.0 broker. No host changes needed &mdash; point at <code>mqtt://…</code>.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#mqtt'
    },
    {
        id: 'socketio', label: 'Socket.IO',
        hint: 'Engine.io + Socket.IO 4.x',
        packageId: 'Kuestenlogik.Bowire.Protocol.SocketIo',
        urlPlaceholder: 'http://localhost:3000',
        category: 'first-party',
        setupNote: 'Standalone-mode plugin &mdash; Bowire connects via the standard <code>/socket.io/</code> upgrade.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#socketio'
    },
    {
        id: 'odata', label: 'OData',
        hint: 'Discovery via $metadata',
        packageId: 'Kuestenlogik.Bowire.Protocol.OData',
        urlPlaceholder: 'https://api.example.com/odata',
        category: 'first-party',
        setupNote: 'Expose <code>$metadata</code> via <code>app.MapODataRoute(...)</code> so Bowire can read entity sets + actions.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire#odata'
    },
    // Third-party plugins — sibling repos, ship via NuGet on their own
    // release cadence. CLI installs on demand.
    {
        id: 'storm', label: 'Surgewave',
        hint: 'Kafka-compatible broker, native + Kafka-wire',
        packageId: 'Kuestenlogik.Bowire.Protocol.Storm',
        urlPlaceholder: 'storm://broker:9092',
        category: 'third-party',
        setupNote: 'Surgewave-native protocol via the KL.Storm.Client SDK. Use <code>?protocol=kafka</code> on the URL to switch to the Kafka-compat wire.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire.Protocol.Storm'
    },
    {
        id: 'kafka', label: 'Kafka',
        hint: 'Confluent + Schema Registry decode',
        packageId: 'Kuestenlogik.Bowire.Protocol.Kafka',
        urlPlaceholder: 'kafka://broker:9092',
        category: 'third-party',
        setupNote: 'Use <code>?schema-registry=http://sr:8081</code> on the URL to enable Avro decode of consumed messages.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire.Protocol.Kafka'
    },
    {
        id: 'dis', label: 'DIS',
        hint: 'IEEE 1278 simulation',
        packageId: 'Kuestenlogik.Bowire.Protocol.Dis',
        urlPlaceholder: 'udp://239.1.2.3:3000',
        category: 'third-party',
        setupNote: 'UDP multicast listener for DIS PDUs. Default group <code>239.1.2.3:3000</code> &mdash; override per-step in metadata.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire.Protocol.Dis'
    },
    {
        id: 'udp', label: 'UDP',
        hint: 'Raw datagrams',
        packageId: 'Kuestenlogik.Bowire.Protocol.Udp',
        urlPlaceholder: 'udp://localhost:9999',
        category: 'third-party',
        setupNote: 'Raw datagram listener &mdash; feed any UDP source into the Bowire streaming pane.',
        docUrl: 'https://github.com/Kuestenlogik/Bowire.Protocol.Udp'
    }
];

// Multi-select search combobox. Renders into `hostEl`, returns a
// small controller object the stepper uses to read selections + bind
// change events. Single-instance per host; vanilla DOM, no
// framework. Keyboard-friendly (arrow keys + enter).
function createBowireCombobox(hostEl, allItems, defaultSelectedIds, placeholder) {
    var wrap = document.createElement('div');
    wrap.className = 'launch-combobox';
    var input = document.createElement('input');
    input.type = 'text';
    input.className = 'launch-combobox-input';
    input.placeholder = placeholder || 'Search…';
    input.spellcheck = false;
    input.autocomplete = 'off';
    var suggestions = document.createElement('ul');
    suggestions.className = 'launch-combobox-suggestions';
    suggestions.hidden = true;

    hostEl.appendChild(wrap);
    hostEl.appendChild(suggestions);

    var selectedIds = (defaultSelectedIds || []).slice();
    var activeSuggestion = -1;
    var changeListeners = [];

    function findItem(id) {
        for (var i = 0; i < allItems.length; i++) {
            if (allItems[i].id === id) return allItems[i];
        }
        return null;
    }

    function renderTags() {
        // Wipe everything except the trailing input.
        while (wrap.firstChild) wrap.removeChild(wrap.firstChild);
        for (var i = 0; i < selectedIds.length; i++) {
            (function (id) {
                var item = findItem(id);
                if (!item) return;
                var tag = document.createElement('span');
                tag.className = 'launch-combobox-tag';
                tag.appendChild(document.createTextNode(item.label));
                var rm = document.createElement('button');
                rm.type = 'button';
                rm.className = 'launch-combobox-tag-remove';
                rm.setAttribute('aria-label', 'Remove ' + item.label);
                rm.textContent = '×';
                rm.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    removeSelection(id);
                });
                tag.appendChild(rm);
                wrap.appendChild(tag);
            })(selectedIds[i]);
        }
        wrap.appendChild(input);
    }

    function renderSuggestions() {
        var query = input.value.trim().toLowerCase();
        var matches = [];
        for (var i = 0; i < allItems.length; i++) {
            var item = allItems[i];
            if (selectedIds.indexOf(item.id) >= 0) continue;
            if (query.length === 0 ||
                item.label.toLowerCase().indexOf(query) >= 0 ||
                item.id.indexOf(query) >= 0 ||
                (item.hint && item.hint.toLowerCase().indexOf(query) >= 0)) {
                matches.push(item);
            }
        }

        suggestions.innerHTML = '';
        if (matches.length === 0) {
            var empty = document.createElement('li');
            empty.className = 'launch-combobox-empty';
            empty.textContent = query ? 'No protocols match "' + query + '"' : 'All protocols selected.';
            suggestions.appendChild(empty);
            activeSuggestion = -1;
            return;
        }

        for (var j = 0; j < matches.length; j++) {
            (function (item, idx) {
                var li = document.createElement('li');
                li.className = 'launch-combobox-suggestion' + (idx === activeSuggestion ? ' active' : '');
                li.dataset.itemId = item.id;
                var name = document.createElement('span');
                name.className = 'launch-combobox-suggestion-name';
                name.textContent = item.label;
                li.appendChild(name);
                if (item.hint) {
                    var hint = document.createElement('span');
                    hint.className = 'launch-combobox-suggestion-hint';
                    hint.textContent = item.hint;
                    li.appendChild(hint);
                }
                li.addEventListener('mousedown', function (ev) {
                    // mousedown not click — click would lose focus + close
                    // the dropdown before the selection registers.
                    ev.preventDefault();
                    addSelection(item.id);
                });
                suggestions.appendChild(li);
            })(matches[j], j);
        }
        if (activeSuggestion >= matches.length) activeSuggestion = matches.length - 1;
    }

    function addSelection(id) {
        if (selectedIds.indexOf(id) >= 0) return;
        selectedIds.push(id);
        input.value = '';
        renderTags();
        renderSuggestions();
        input.focus();
        notify();
    }

    function removeSelection(id) {
        var idx = selectedIds.indexOf(id);
        if (idx < 0) return;
        selectedIds.splice(idx, 1);
        renderTags();
        renderSuggestions();
        notify();
    }

    function notify() {
        for (var i = 0; i < changeListeners.length; i++) changeListeners[i]();
    }

    function openSuggestions() {
        suggestions.hidden = false;
        wrap.classList.add('focused');
        renderSuggestions();
    }

    function closeSuggestions() {
        suggestions.hidden = true;
        wrap.classList.remove('focused');
    }

    input.addEventListener('focus', openSuggestions);
    input.addEventListener('blur', function () {
        // Delay so a mousedown on a suggestion fires first.
        setTimeout(closeSuggestions, 120);
    });
    input.addEventListener('input', renderSuggestions);
    input.addEventListener('keydown', function (ev) {
        var visible = suggestions.querySelectorAll('.launch-combobox-suggestion');
        if (ev.key === 'ArrowDown') {
            ev.preventDefault();
            if (visible.length === 0) return;
            activeSuggestion = (activeSuggestion + 1) % visible.length;
            renderSuggestions();
        } else if (ev.key === 'ArrowUp') {
            ev.preventDefault();
            if (visible.length === 0) return;
            activeSuggestion = (activeSuggestion - 1 + visible.length) % visible.length;
            renderSuggestions();
        } else if (ev.key === 'Enter') {
            if (activeSuggestion >= 0 && visible[activeSuggestion]) {
                ev.preventDefault();
                addSelection(visible[activeSuggestion].dataset.itemId);
            }
        } else if (ev.key === 'Backspace' && input.value === '' && selectedIds.length > 0) {
            removeSelection(selectedIds[selectedIds.length - 1]);
        } else if (ev.key === 'Escape') {
            closeSuggestions();
        }
    });
    wrap.addEventListener('click', function () { input.focus(); });

    renderTags();
    renderSuggestions();

    return {
        getSelected: function () { return selectedIds.slice(); },
        onChange: function (cb) { changeListeners.push(cb); }
    };
}

(function () {
    var root = document.querySelector('[data-launch]');
    if (!root) return;

    // The four boats. Each carries the install + run snippets the
    // stepper renders on steps 2 + 3, plus prompt copy and a flag
    // for whether step 3 surfaces the URL input.
    var RECIPES = {
        backend: {
            installLang: 'bash',
            // The backend install snippet is rebuilt from the checked
            // NuGet protocol boxes on render — see buildInstall below.
            // The string here is just the fallback when nothing is
            // checked yet.
            install: 'dotnet add package Kuestenlogik.Bowire',
            protocolPicker: 'nuget',
            runLang: 'csharp',
            run:
                '// In your Program.cs, after WebApplication.CreateBuilder(...).Build():\n' +
                'app.MapBowire();\n' +
                '// app.Run();',
            then:
                'Then run your app and open <code>http://localhost:5000/bowire</code>.<br>Every gRPC, REST, SignalR, GraphQL endpoint your service exposes is browsable there.',
            urlInput: false,
            installPrompt: 'NuGet packages — core plus the protocols you ship. Tick whichever you need; the snippet updates live.',
            runPrompt: 'One line in your Program.cs.'
        },
        tester: {
            installLang: 'bash',
            install:
                '# Install the Bowire CLI as a global .NET tool\n' +
                'dotnet tool install -g Kuestenlogik.Bowire.Tool',
            protocolPicker: 'cli',
            runLang: 'bash',
            run: 'bowire --url {URL}',
            then:
                'Bowire opens in your browser, discovers the API, and shows every method in the sidebar. Click Record to capture a session, replay it later against any environment.',
            urlInput: true,
            urlPlaceholder: 'https://api.example.com/swagger.json',
            installPrompt: 'One <code>dotnet tool install</code>. The CLI bundles every first-party protocol; tick any third-party extras below.',
            runPrompt: 'Point Bowire at the API you want to browse.'
        },
        mock: {
            installLang: 'bash',
            install:
                '# Install the CLI (mock is a subcommand)\n' +
                'dotnet tool install -g Kuestenlogik.Bowire.Tool',
            protocolPicker: 'cli',
            runLang: 'bash',
            run:
                '# Replay a recording you captured earlier:\n' +
                'bowire mock --recording session.json --port 5050\n\n' +
                '# Or generate a mock from an OpenAPI / proto schema:\n' +
                'bowire mock --schema api.yaml --port 5050',
            then:
                'Mock server listens on the chosen port. Point your frontend or CI at <code>http://localhost:5050</code> and the recorded responses replay verbatim.',
            urlInput: false,
            installPrompt: 'Same CLI as the tester path. Add any third-party extras you want to mock against.',
            runPrompt: 'Two ways: replay a recording, or synthesize from a schema.'
        },
        ai: {
            installLang: 'bash',
            install:
                '# Pull the container image\n' +
                'docker pull ghcr.io/kuestenlogik/bowire:latest',
            runLang: 'bash',
            run:
                'docker run --rm -p 5080:5080 \\\n' +
                '  ghcr.io/kuestenlogik/bowire:latest \\\n' +
                '  --url {URL} \\\n' +
                '  --enable-mcp-adapter',
            then:
                'Add <code>http://localhost:5080/mcp</code> as an MCP server to Claude / Cursor / Copilot. Every discovered method shows up as an MCP tool with JSON Schema input.',
            urlInput: true,
            urlPlaceholder: 'https://my-internal-service:8443',
            installPrompt: 'Container image — runs anywhere Docker does.',
            runPrompt: 'Drop in your service URL, hand the MCP endpoint to your agent.'
        }
    };

    var indicators = root.querySelectorAll('[data-step-indicator]');
    var connectors = root.querySelectorAll('.launch-step-connector');
    var panels = root.querySelectorAll('[data-step-panel]');
    var boats = root.querySelectorAll('[data-boat]');
    var urlInput = root.querySelector('[data-url-input]');
    var nugetBox = root.querySelector('[data-protocols-host="nuget"]');
    var cliBox = root.querySelector('[data-protocols-host="cli"]');
    var setupNotesBox = root.querySelector('[data-setup-notes]');
    var setupNotesList = root.querySelector('[data-setup-notes-list]');

    // Build comboboxes once — defaults pre-select gRPC + REST for the
    // backend (covers 80% of ASP.NET workflows on first reach).
    // The NuGet picker offers every plugin (first- and third-party);
    // both ship as plain `dotnet add package` references in Embedded
    // Mode. The CLI picker only lists third-party extras because the
    // CLI bundle already includes every first-party protocol.
    var thirdParty = BOWIRE_PROTOCOLS.filter(function (p) { return p.category === 'third-party'; });
    var nugetDefaults = BOWIRE_PROTOCOLS.filter(function (p) { return p.defaultBackend; }).map(function (p) { return p.id; });

    var nugetCombo = createBowireCombobox(
        root.querySelector('[data-combobox-nuget]'), BOWIRE_PROTOCOLS, nugetDefaults,
        'Search protocols (gRPC, REST, Surgewave, Kafka, …)');
    var cliCombo = createBowireCombobox(
        root.querySelector('[data-combobox-cli]'), thirdParty, [],
        'Search third-party plugins (Surgewave, Kafka, DIS, UDP)');

    var currentStep = 1;
    var pickedBoat = null;
    var typedUrl = '';

    function selectedNugetIds() { return nugetCombo.getSelected(); }
    function selectedCliIds() { return cliCombo.getSelected(); }

    function findProtocol(id) {
        for (var i = 0; i < BOWIRE_PROTOCOLS.length; i++) {
            if (BOWIRE_PROTOCOLS[i].id === id) return BOWIRE_PROTOCOLS[i];
        }
        return null;
    }

    function buildNugetInstall() {
        var lines = [
            '# Add Bowire core',
            'dotnet add package Kuestenlogik.Bowire'
        ];
        var ids = selectedNugetIds();
        if (ids.length > 0) {
            lines.push('');
            lines.push('# ' + (ids.length === 1 ? 'Plus the protocol plugin you need' : 'Plus the protocol plugins you need'));
            ids.forEach(function (id) {
                var proto = findProtocol(id);
                if (proto) lines.push('dotnet add package ' + proto.packageId);
            });
        }
        return lines.join('\n');
    }

    function buildCliInstall(recipe) {
        var ids = selectedCliIds();
        if (ids.length === 0) return recipe.install;
        var lines = [
            recipe.install,
            '',
            '# ' + (ids.length === 1 ? 'Plus the third-party plugin you need' : 'Plus the third-party plugins you need')
        ];
        ids.forEach(function (id) {
            var proto = findProtocol(id);
            if (proto) lines.push('bowire plugin install ' + proto.packageId);
        });
        return lines.join('\n');
    }

    // Pick the URL placeholder for the run snippet from the first
    // selected protocol, so the user sees a wire-format example
    // matching what they're targeting (`mqtt://…` for MQTT,
    // `kafka://…` for Kafka, etc.). Falls back to the boat's static
    // placeholder when nothing is picked.
    function effectiveUrlPlaceholder(recipe) {
        var ids = recipe.protocolPicker === 'cli' ? selectedCliIds() : selectedNugetIds();
        if (ids.length > 0) {
            var first = findProtocol(ids[0]);
            if (first && first.urlPlaceholder) return first.urlPlaceholder;
        }
        return recipe.urlPlaceholder || '';
    }

    // Step 3 — render per-protocol setup notes + doc links. Only the
    // backend boat populates this; CLI boats already ship every plugin
    // wired internally so there's nothing for the user to set up.
    function renderSetupNotes(recipe) {
        if (!setupNotesBox || !setupNotesList) return;
        if (recipe.protocolPicker !== 'nuget') {
            setupNotesBox.hidden = true;
            return;
        }
        var ids = selectedNugetIds();
        if (ids.length === 0) { setupNotesBox.hidden = true; return; }
        setupNotesList.innerHTML = '';
        ids.forEach(function (id) {
            var p = findProtocol(id);
            if (!p) return;
            var li = document.createElement('li');
            var html = '<strong>' + p.label + '</strong> — ' + (p.setupNote || '');
            if (p.docUrl) {
                html += ' <a href="' + p.docUrl + '" target="_blank" rel="noopener">setup notes →</a>';
            }
            li.innerHTML = html;
            setupNotesList.appendChild(li);
        });
        setupNotesBox.hidden = false;
    }

    function renderRecipes() {
        var recipe = pickedBoat ? RECIPES[pickedBoat] : null;
        if (!recipe) return;

        // Show / hide the matching protocol picker.
        if (nugetBox) nugetBox.hidden = recipe.protocolPicker !== 'nuget';
        if (cliBox) cliBox.hidden = recipe.protocolPicker !== 'cli';

        // Build install snippet from the appropriate picker, or fall
        // back to the static recipe string when there's no picker.
        var installSnippet;
        if (recipe.protocolPicker === 'nuget') installSnippet = buildNugetInstall();
        else if (recipe.protocolPicker === 'cli') installSnippet = buildCliInstall(recipe);
        else installSnippet = recipe.install;

        renderSetupNotes(recipe);

        var installCode = root.querySelector('[data-recipe-content]');
        var installLang = root.querySelector('[data-recipe-lang]');
        var installCopy = root.querySelector('[data-recipe-copy]');
        var installPrompt = root.querySelector('[data-launch-prompt-2]');
        installCode.textContent = installSnippet;
        installLang.textContent = recipe.installLang;
        installCopy.dataset.copy = installSnippet;
        if (installPrompt) installPrompt.innerHTML = recipe.installPrompt;

        // Step 3 run snippet — substitute {URL} placeholder.
        var runCode = root.querySelector('[data-recipe-content-3]');
        var runLang = root.querySelector('[data-recipe-lang-3]');
        var runCopy = root.querySelector('[data-recipe-copy-3]');
        var runPrompt = root.querySelector('[data-launch-prompt-3]');
        var thenLine = root.querySelector('[data-launch-then]');
        var urlRow = root.querySelector('[data-url-row]');
        // URL placeholder adapts to the first selected protocol so
        // the run snippet's wire format matches what the user picked
        // (e.g. `mqtt://broker:1883` after MQTT, `kafka://broker:9092`
        // after Kafka, etc.). Boat-default kicks in when nothing is
        // selected.
        var placeholder = effectiveUrlPlaceholder(recipe);
        if (urlInput) urlInput.placeholder = placeholder;
        var url = (typedUrl || placeholder || '').trim() || '{URL}';
        var filled = recipe.run.replace(/\{URL\}/g, url);
        runCode.textContent = filled;
        runLang.textContent = recipe.runLang;
        runCopy.dataset.copy = filled;
        if (runPrompt) runPrompt.innerHTML = recipe.runPrompt;
        if (thenLine) thenLine.innerHTML = recipe.then;

        if (recipe.urlInput) {
            urlRow.hidden = false;
        } else {
            urlRow.hidden = true;
        }
    }

    function setStep(n) {
        currentStep = n;
        indicators.forEach(function (el) {
            var idx = Number(el.dataset.stepIndicator);
            el.classList.toggle('active', idx === n);
            el.classList.toggle('completed', idx < n);
            el.setAttribute('aria-selected', idx === n ? 'true' : 'false');
            el.setAttribute('aria-disabled', (!pickedBoat && idx > 1) ? 'true' : 'false');
        });
        connectors.forEach(function (el, i) {
            el.classList.toggle('completed', (i + 1) < n);
        });
        panels.forEach(function (p) {
            var idx = Number(p.dataset.stepPanel);
            p.classList.toggle('active', idx === n);
            p.setAttribute('aria-hidden', idx !== n ? 'true' : 'false');
        });
        if (n >= 2) renderRecipes();
    }

    boats.forEach(function (card) {
        card.addEventListener('click', function () {
            pickedBoat = card.dataset.boat;
            boats.forEach(function (b) { b.classList.toggle('selected', b === card); });
            // Auto-advance to step 2 — feels more direct than a
            // separate "Next" click on the boat-picker step.
            setTimeout(function () { setStep(2); }, 180);
        });
    });

    root.addEventListener('click', function (ev) {
        if (ev.target.closest('[data-step-back]')) {
            setStep(Math.max(1, currentStep - 1));
            return;
        }
        if (ev.target.closest('[data-step-next]')) {
            setStep(Math.min(3, currentStep + 1));
            return;
        }
        if (ev.target.closest('[data-step-restart]')) {
            pickedBoat = null;
            typedUrl = '';
            boats.forEach(function (b) { b.classList.remove('selected'); });
            if (urlInput) urlInput.value = '';
            setStep(1);
            return;
        }
        var indicator = ev.target.closest('[data-step-indicator]');
        if (indicator) {
            var idx = Number(indicator.dataset.stepIndicator);
            if (idx === 1 || pickedBoat) setStep(idx);
        }
    });

    if (urlInput) {
        urlInput.addEventListener('input', function (ev) {
            typedUrl = ev.target.value;
            renderRecipes();
        });
    }

    // Re-render install + run snippets whenever the user adds or
    // removes a protocol in either combobox. Visibility of the two
    // pickers is gated by recipe.protocolPicker up in renderRecipes
    // — at most one combobox is interactive at a time.
    nugetCombo.onChange(function () { if (pickedBoat) renderRecipes(); });
    cliCombo.onChange(function () { if (pickedBoat) renderRecipes(); });
})();


// ====================================================================
// Quickstart install tabs — pick a Windows / macOS / Linux / .NET
// tool / Container snippet on the /quickstart.html page. Auto-selects
// the visitor's platform on page load (UA sniffing, same approach as
// the downloads quick-picker), with click handlers + arrow-key
// navigation for keyboard / a11y users.
// ====================================================================
(function () {
    var containers = document.querySelectorAll('[data-qs-install-tabs]');
    if (containers.length === 0) return;

    function detectPlatform() {
        var ua = (navigator.userAgent || '').toLowerCase();
        var platform = (navigator.platform || '').toLowerCase();
        var uaData = navigator.userAgentData;
        var p = uaData && uaData.platform ? uaData.platform.toLowerCase() : '';
        if (p.indexOf('windows') >= 0 || ua.indexOf('win') >= 0 || platform.indexOf('win') >= 0) return 'windows';
        if (p.indexOf('mac') >= 0     || ua.indexOf('mac') >= 0 || platform.indexOf('mac') >= 0) return 'macos';
        if (p.indexOf('linux') >= 0   || ua.indexOf('linux') >= 0 || platform.indexOf('linux') >= 0) return 'linux';
        return null;
    }

    containers.forEach(function (container) {
        var tabs   = Array.from(container.querySelectorAll('[data-qs-install-tab]'));
        var panels = Array.from(container.querySelectorAll('[data-qs-install-panel]'));
        if (tabs.length === 0 || panels.length === 0) return;

        function activate(name) {
            var matched = false;
            tabs.forEach(function (tab) {
                var on = tab.dataset.qsInstallTab === name;
                if (on) matched = true;
                tab.setAttribute('aria-selected', on ? 'true' : 'false');
                tab.setAttribute('tabindex', on ? '0' : '-1');
            });
            panels.forEach(function (panel) {
                panel.hidden = panel.dataset.qsInstallPanel !== name;
            });
            return matched;
        }

        tabs.forEach(function (tab, i) {
            tab.addEventListener('click', function () { activate(tab.dataset.qsInstallTab); });
            tab.addEventListener('keydown', function (e) {
                // Arrow-key tab cycling — left/right/home/end, same
                // pattern as the WAI-ARIA tab pattern.
                var idx = i;
                if (e.key === 'ArrowRight') idx = (i + 1) % tabs.length;
                else if (e.key === 'ArrowLeft') idx = (i - 1 + tabs.length) % tabs.length;
                else if (e.key === 'Home') idx = 0;
                else if (e.key === 'End')  idx = tabs.length - 1;
                else return;
                e.preventDefault();
                tabs[idx].focus();
                activate(tabs[idx].dataset.qsInstallTab);
            });
        });

        var detected = detectPlatform();
        if (detected) activate(detected);
    });
})();

// ====================================================================
// Quickstart path picker — toggles between the Standalone CLI and
// Embedded ASP.NET steppers on /quickstart.html. The SVG fork above
// visually anchors the decision; this is the click handler that
// reveals the matching stepper below.
// ====================================================================
(function () {
    var picker = document.querySelector('[data-path-picker]');
    if (!picker) return;
    var buttons = Array.from(picker.querySelectorAll('[data-path]'));
    // Path-tagged steps live flat inside the host so the connector
    // rail flows top-down through both visible + hidden articles.
    // We toggle `hidden` per article instead of swapping wrappers.
    var pathSteps = Array.from(document.querySelectorAll('.quickstart-page-step[data-path]'));
    var decision  = document.querySelector('.quickstart-page-step-decision');
    var pending   = document.querySelector('[data-step-pending]');
    if (buttons.length === 0 || pathSteps.length === 0) return;

    function activate(name) {
        buttons.forEach(function (b) {
            var on = b.dataset.path === name;
            b.setAttribute('aria-selected', on ? 'true' : 'false');
            b.setAttribute('tabindex', on ? '0' : '-1');
        });
        pathSteps.forEach(function (s) {
            s.hidden = s.dataset.path !== name;
        });
        // Decision step transitions to "done" only after the user
        // actually picks a path. Default state has no path selected
        // and step 1 stays neutral (and steps 2+ stay hidden) so the
        // page reads as "make a choice" rather than "here's already
        // a default". The dashed-border "?" pending placeholder also
        // disappears here — its job was done at first paint.
        if (decision) decision.classList.add('is-done');
        if (pending) pending.hidden = true;
    }

    buttons.forEach(function (b, i) {
        b.addEventListener('click', function () { activate(b.dataset.path); });
        b.addEventListener('keydown', function (e) {
            var idx = i;
            if (e.key === 'ArrowRight') idx = (i + 1) % buttons.length;
            else if (e.key === 'ArrowLeft') idx = (i - 1 + buttons.length) % buttons.length;
            else return;
            e.preventDefault();
            buttons[idx].focus();
            activate(buttons[idx].dataset.path);
        });
    });

    // Default state: standalone is preselected via aria-selected="true"
    // in the markup. No platform-detection here — the choice between
    // CLI and embedded is about how the user wants to consume Bowire,
    // not about their host OS.
})();

// ====================================================================
// Quickstart code-copy buttons — wrap every <pre> on
// /quickstart.html in the same `.code-block` chrome the landing-page
// install snippets use: a header row with a language label on the
// left and a copy-button on the right, then the <pre> below. The
// header sits above the code (not on top of it), so single-line
// snippets don't compete with the button for horizontal space.
// .install-tab-panel pres already live inside their own bordered
// box and chip-row context — they get the same wrap treatment for
// consistency, and we just reset the install-tab-panel's outer
// padding so the wrap nests cleanly.
// ====================================================================
(function () {
    var preBlocks = Array.from(document.querySelectorAll('.quickstart-page-step pre'));
    if (preBlocks.length === 0) return;

    var copyIcon =
        '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
          '<rect x="9" y="9" width="13" height="13" rx="2"/>' +
          '<path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>' +
        '</svg>';

    preBlocks.forEach(function (pre) {
        if (pre.closest('.code-block')) return;
        var code = pre.querySelector('code');
        if (!code) return;

        // Detect language from the highlighted code class
        // (`lang-bash` / `lang-csharp` / …) and surface it in the
        // header row.
        var lang = '';
        var m = (code.className || '').match(/lang-(\S+)/);
        if (m) lang = m[1];
        var snippet = code.textContent.replace(/^\s+|\s+$/g, '');

        var wrapper = document.createElement('div');
        wrapper.className = 'code-block';

        var header = document.createElement('div');
        header.className = 'code-block-header';
        if (lang) {
            var langSpan = document.createElement('span');
            langSpan.className = 'code-block-lang';
            langSpan.textContent = lang;
            header.appendChild(langSpan);
        }

        var btn = document.createElement('button');
        btn.className = 'copy-btn';
        btn.type = 'button';
        btn.setAttribute('aria-label', 'Copy snippet to clipboard');
        btn.dataset.copy = snippet;
        btn.innerHTML = copyIcon;
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            if (!navigator.clipboard) return;
            navigator.clipboard.writeText(snippet).then(function () {
                btn.classList.add('copied');
                setTimeout(function () { btn.classList.remove('copied'); }, 1500);
            });
        });
        header.appendChild(btn);

        // Insert wrapper before the <pre>, then move the <pre> in.
        pre.parentNode.insertBefore(wrapper, pre);
        wrapper.appendChild(header);
        wrapper.appendChild(pre);
    });
})();
