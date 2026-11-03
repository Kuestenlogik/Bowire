// Inline copies of the logo inside a product's pages and scripts, kept in step with the source drawing.
//
// A copy sits between two markers; everything between them is the generator's and is rewritten on every run:
//
//   <!-- brand:mark class="logo" aria-hidden="true" --> … <!-- /brand -->     HTML, Liquid, Razor, other templates
//   /* brand:lockup-h class="logo" */ … /* /brand */                           JavaScript, CSS
//
// The kind after "brand:" is mark, emblem, lockup-h or lockup-v. The attributes after it are copied onto the <svg>;
// without aria-hidden, aria-label or role the copy gets role="img" and aria-label = the product. Every shape is
// painted currentColor, so the page's text colour paints the copy. The copy is one line, so it also fits a
// single-quoted or template string in a script.
export const inlineKinds = ['mark', 'emblem', 'lockup-h', 'lockup-v'];

const attrs = '((?:\\s+[\\w:.-]+="[^"]*")*)';
const markers = [
  new RegExp(`(<!--\\s*brand:([\\w-]+)${attrs}\\s*-->)([\\s\\S]*?)(<!--\\s*/brand\\s*-->)`, 'g'),
  new RegExp(`(/\\*\\s*brand:([\\w-]+)${attrs}\\s*\\*/)([\\s\\S]*?)(/\\*\\s*/brand\\s*\\*/)`, 'g'),
];

// The text with every marked copy rewritten; `render(kind)` gives { viewBox, body } of a kind, null when the
// product has none. `label` is the product's name.
export function syncInline(text, file, label, render) {
  let result = text;
  for (const marker of markers) {
    result = result.replace(marker, (_, open, kind, attributes, _old, close) => {
      if (!inlineKinds.includes(kind)) throw new Error(`${file}: brand:${kind} is no kind (${inlineKinds.join(', ')})`);
      const drawing = render(kind);
      if (!drawing) throw new Error(`${file}: brand:${kind}, but the configuration has no ${kind}`);
      const own = attributes.trim();
      const aria = /\b(aria-hidden|aria-label|role)=/.test(own) ? '' : ` role="img" aria-label="${label}"`;
      const body = drawing.body.replace(/\n\s*/g, '');
      return `${open}<svg xmlns="http://www.w3.org/2000/svg" viewBox="${drawing.viewBox}"${own ? ' ' + own : ''}${aria}>${body}</svg>${close}`;
    });
  }
  return result;
}

// Whether a text holds a marker at all (a file named by the configuration that holds none is a mistake).
export const hasMarker = (text) => markers.some((m) => { m.lastIndex = 0; const hit = m.test(text); m.lastIndex = 0; return hit; });
