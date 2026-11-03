// Inline copies of the logo inside a product's pages and scripts, kept in step with the source drawing.
//
// A copy sits between two markers; everything between them is the generator's and is rewritten on every run:
//
//   <!-- brand:mark class="logo" aria-hidden="true" --> … <!-- /brand -->     HTML, Liquid, Razor, other templates:
//                                                                              the <svg> element itself
//   /* brand:lockup-h class="logo" */ … /* /brand */                           JavaScript, TypeScript: a string
//                                                                              literal that holds the <svg>
//
// The block-comment markers stand outside the literal, in the code: `const logo = /* brand:mark */ '…' /* /brand */;`.
// The generator writes a single-quoted string between them (' and \ escaped), so the markers stay comments of the
// script. (Inside a literal they would be part of the string and show on the page.)
//
// The kind after "brand:" is mark, emblem, lockup-h or lockup-v. The attributes after it are copied onto the <svg>;
// without aria-hidden, aria-label or role the copy gets role="img" and aria-label = the product. Every shape is
// painted currentColor, so the page's text colour paints the copy. The copy is one line.
export const inlineKinds = ['mark', 'emblem', 'lockup-h', 'lockup-v'];

const attrs = '((?:\\s+[\\w:.-]+="[^"]*")*)';
const markers = [
  { pattern: new RegExp(`(<!--\\s*brand:([\\w-]+)${attrs}\\s*-->)([\\s\\S]*?)(<!--\\s*/brand\\s*-->)`, 'g'), quote: false },
  { pattern: new RegExp(`(/\\*\\s*brand:([\\w-]+)${attrs}\\s*\\*/)([\\s\\S]*?)(/\\*\\s*/brand\\s*\\*/)`, 'g'), quote: true },
];

// A single-quoted script string holding the text.
export const quoted = (text) => `'${text.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;

// The text with every marked copy rewritten; `render(kind)` gives { viewBox, body } of a kind, null when the
// product has none. `label` is the product's name.
export function syncInline(text, file, label, render) {
  let result = text;
  for (const { pattern, quote } of markers) {
    result = result.replace(pattern, (_, open, kind, attributes, _old, close) => {
      if (!inlineKinds.includes(kind)) throw new Error(`${file}: brand:${kind} is no kind (${inlineKinds.join(', ')})`);
      const drawing = render(kind);
      if (!drawing) throw new Error(`${file}: brand:${kind}, but the configuration has no ${kind}`);
      const own = attributes.trim();
      const aria = /\b(aria-hidden|aria-label|role)=/.test(own) ? '' : ` role="img" aria-label="${label}"`;
      const body = drawing.body.replace(/\n\s*/g, '');
      const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${drawing.viewBox}"${own ? ' ' + own : ''}${aria}>${body}</svg>`;
      return quote ? `${open} ${quoted(svg)} ${close}` : `${open}${svg}${close}`;
    });
  }
  return result;
}

// Whether a text holds a marker at all (a file named by the configuration that holds none is a mistake).
export const hasMarker = (text) => markers.some(({ pattern }) => {
  pattern.lastIndex = 0;
  const hit = pattern.test(text);
  pattern.lastIndex = 0;
  return hit;
});
