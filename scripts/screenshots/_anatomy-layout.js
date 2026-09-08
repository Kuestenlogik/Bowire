/**
 * Layout half of generate-ui-anatomy.js — kept separate so the geometry can
 * be reasoned about (and unit-checked) without launching a browser.
 *
 * The diagram sits on a canvas larger than the screenshot: a margin around
 * the image gives the narrow regions somewhere to put their label. The
 * topbar is 47px tall and the rail strip 48px wide, so a plate inside
 * either one covers the very thing it is labelling. Those get a plate in
 * the margin and a leader line with an arrowhead pointing at the region;
 * the big panes keep their label inside, where a leader would be noise.
 */

const MARGIN = { top: 46, right: 168, bottom: 52, left: 168 };

/** Canvas size for a given screenshot size. */
function canvas(width, height) {
    return {
        w: width + MARGIN.left + MARGIN.right,
        h: height + MARGIN.top + MARGIN.bottom,
        imageX: MARGIN.left,
        imageY: MARGIN.top,
    };
}

/** Approximate the rendered label width; SVG offers no metrics pre-layout. */
function plateSize(label) {
    return { w: 30 + Math.round(label.length * 7.4) + 14, h: 26 };
}

/**
 * Where a region's callout plate goes, and whether it needs a leader.
 *
 * `placement` is the region's own declaration:
 *   inside  — the region is big enough to host the plate
 *   above / below / left / right — put it in that margin and draw a leader
 */
function place(region, box, screen) {
    const c = canvas(screen.width, screen.height);
    const b = {
        x: box.x + c.imageX, y: box.y + c.imageY,
        w: box.w, h: box.h,
    };
    const p = plateSize(region.label);

    if (region.placement === 'inside') {
        return {
            plate: {
                x: clamp(b.x + 10, 6, c.w - p.w - 6),
                y: clamp(b.y + 10, 6, c.h - p.h - 6),
                ...p,
            },
            leader: null,
            box: b,
            canvas: c,
        };
    }

    let plate;
    let from;   // point on the plate the leader starts at
    let to;     // point on the region the arrow lands on

    if (region.placement === 'above') {
        plate = { x: clamp(b.x + 24, 6, c.w - p.w - 6), y: c.imageY - p.h - 12, ...p };
        from = { x: plate.x + p.w / 2, y: plate.y + p.h };
        to = { x: plate.x + p.w / 2, y: b.y + Math.min(b.h / 2, 18) };
    } else if (region.placement === 'below') {
        plate = { x: clamp(b.x + 24, 6, c.w - p.w - 6), y: c.imageY + screen.height + 12, ...p };
        from = { x: plate.x + p.w / 2, y: plate.y };
        to = { x: plate.x + p.w / 2, y: b.y + b.h - Math.min(b.h / 2, 18) };
    } else if (region.placement === 'left') {
        const cy = clamp(b.y + b.h * 0.28, 6 + p.h / 2, c.h - p.h / 2 - 6);
        plate = { x: MARGIN.left - p.w - 16, y: cy - p.h / 2, ...p };
        from = { x: plate.x + p.w, y: cy };
        to = { x: b.x + b.w / 2, y: cy };
    } else { // right
        const cy = clamp(b.y + b.h * 0.28, 6 + p.h / 2, c.h - p.h / 2 - 6);
        plate = { x: b.x + b.w + 16, y: cy - p.h / 2, ...p };
        from = { x: plate.x, y: cy };
        to = { x: b.x + b.w / 2, y: cy };
    }

    return { plate, leader: { from, to }, box: b, canvas: c };
}

function clamp(v, lo, hi) { return Math.min(Math.max(v, lo), hi); }

module.exports = { MARGIN, canvas, plateSize, place, clamp };
