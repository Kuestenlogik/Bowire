// The lockups' geometry and the wordmark's paint, kept apart from the generator so the tests can hold them without a
// browser. See README.md (Outputs) beside the tool.

// The family's lockup rules. Horizontal: 200 tall, the mark 180 at the margin, the wordmark 100 px (cap height centred
// on the mark's centre line) a gap after it. Vertical: the mark 240 at the top, the wordmark 70 px centred below it
// on the baseline 340, 380 tall. A wordmark whose ink would be wider than maxWidth is set smaller. The canvas is as
// wide as its ink needs and no wider: margin, mark, gap, the wordmark's ink, margin (vertical: margin, the wider of
// mark and wordmark, margin), rounded up to a whole pixel so the PNGs keep the SVG's proportions. Before 1.2.0 every
// lockup was 1100 or 480 wide whatever the product's name, which left a short name in empty space.
export const family = {
  h: { h: 200, margin: 10, mark: 180, gap: 30, size: 100, maxWidth: 870 },
  v: { h: 380, margin: 10, top: 20, mark: 240, size: 70, maxWidth: 460, baseline: 340 },
};

// The wordmark's ink from its outline: the bounding box in font units, scaled to the lockup's size.
function scaled(wordmark, L) {
  const [x0, , x1] = wordmark.bbox;
  const s = Math.min(L.size / wordmark.unitsPerEm, L.maxWidth / (x1 - x0));
  return { s, x0, inkWidth: (x1 - x0) * s };
}

// Where a lockup's mark and wordmark go, and how wide the canvas is.
export function lockupGeometry(kind, wordmark) {
  const L = family[kind];
  if (!L) throw new Error(`no lockup "${kind}"`);
  const { s, x0, inkWidth } = scaled(wordmark, L);
  if (kind === 'h') {
    const textX = L.margin + L.mark + L.gap;
    return {
      w: Math.ceil(textX + inkWidth + L.margin), h: L.h,
      mark: { x: L.margin, y: (L.h - L.mark) / 2, size: L.mark },
      text: { s, inkWidth, tx: textX - x0 * s, baseline: L.h / 2 + (wordmark.capHeight * s) / 2 },
    };
  }
  const w = Math.ceil(Math.max(L.mark, inkWidth) + 2 * L.margin);
  return {
    w, h: L.h,
    mark: { x: (w - L.mark) / 2, y: L.top, size: L.mark },
    text: { s, inkWidth, tx: w / 2 - inkWidth / 2 - x0 * s, baseline: L.baseline },
  };
}

// The paint of one run of the wordmark. Only the colored and adaptive variants paint a run by its role; black, white
// and current paint every run the one flat colour, so a mono lockup stays one colour.
export const flat = { black: '#000000', white: '#ffffff', current: 'currentColor' };
export function partPaint(variant, role, roles, prefix) {
  if (variant === 'adaptive') return `class="${prefix}-${role}"`;
  if (flat[variant]) return `fill="${flat[variant]}"`;
  if (variant !== 'colored') throw new Error(`no variant "${variant}"`);
  return `fill="${roles[role].light}"`;
}
