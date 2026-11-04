---
summary: 'The detailed Bowire brand reference — logo construction, clear space, minimum sizes, colour tokens, co-branding, file formats, accessibility, and usage rights.'
---

# Brand guide

This is the **detailed** brand reference for Bowire: exact clear space, minimum
sizes, the full colour token set, co-branding spacing, file-format guidance,
accessibility minimums, and usage rights.

Just need the files and the gist? The [**Brand & logos**](https://bowire.io/brand.html)
page on bowire.io is the overview + download hub (logo SVGs, the ZIP kit, and a
one-page brand sheet). This page is the manual behind it.

> [!NOTE]
> The Bowire **software** is open source under Apache-2.0. The Bowire **name and
> logos** are trademarks of Küstenlogik — the licence covers the code, not the
> brand. Use the marks per this guide; when in doubt, ask: info@kuestenlogik.com.

## The logo system

Bowire has three forms. The **mark** is the logo: reach for it by default. Use
the **lockup** when you want the name spelled out beside the mark, and the
**emblem** where there is room for the whole story: the site's hero, a title
page, a cover. All three come from one drawing each, generated into every
format by the shared Küstenlogik branding tool.

### Emblem

The mark in its ring, with a bowline knot tied around it: Bowire is *bowline*
and *wire*. The most expressive form, for the places that have room for it.

<div style="display:flex;gap:16px;flex-wrap:wrap;margin:18px 0">
  <div style="background:#fafaff;border:1px solid #e1e1ee;border-radius:10px;padding:30px;flex:1;min-width:210px;text-align:center"><img src="images/brand/bowire_emblem_colored.svg" alt="Bowire emblem, navy, on a light background" style="max-height:74px"></div>
  <div style="background:#0f0f17;border:1px solid #2a2a3d;border-radius:10px;padding:30px;flex:1;min-width:210px;text-align:center"><img src="images/brand/bowire_emblem_white.svg" alt="Bowire emblem, white, on a dark background" style="max-height:74px"></div>
</div>

### Lockup — mark + wordmark

The mark with the "Bowire" wordmark set in Inter Bold (outlined, so no font is
required to render it). Use horizontally where the name should read alongside
the mark — headers, signatures, "powered by" strips.

<div style="display:flex;gap:16px;flex-wrap:wrap;margin:18px 0">
  <div style="background:#fafaff;border:1px solid #e1e1ee;border-radius:10px;padding:30px;flex:1;min-width:210px;text-align:center"><img src="images/brand/bowire_lockup_h_colored.svg" alt="Bowire lockup (dark) on a light background" style="max-height:54px"></div>
  <div style="background:#0f0f17;border:1px solid #2a2a3d;border-radius:10px;padding:30px;flex:1;min-width:210px;text-align:center"><img src="images/brand/bowire_lockup_h_white.svg" alt="Bowire lockup (light) on a dark background" style="max-height:54px"></div>
</div>

### Mark

The "bw" alone: the main logo. Use it wherever Bowire needs one logo, and as
an app icon, avatar, favicon, or any square slot.

<div style="display:flex;gap:16px;flex-wrap:wrap;margin:18px 0">
  <div style="background:#fafaff;border:1px solid #e1e1ee;border-radius:10px;padding:24px;flex:1;min-width:210px;text-align:center"><img src="images/brand/bowire_mark_colored.svg" alt="Bowire mark (dark) on a light background" style="max-height:66px"></div>
  <div style="background:#0f0f17;border:1px solid #2a2a3d;border-radius:10px;padding:24px;flex:1;min-width:210px;text-align:center"><img src="images/brand/bowire_mark_white.svg" alt="Bowire mark (light) on a dark background" style="max-height:66px"></div>
</div>

Each form ships in two colourways: a dark (ink `#0F0F17`) version for light
backgrounds and a light (white `#FFFFFF`) version for dark ones. Always pick the
version that gives the most contrast — see [Accessibility](#accessibility).

## Clear space

Keep a protected margin around the logo so nothing crowds it. The measurement
unit is **X = the height of the mark** (the "bw").

- **Minimum clear space: ½ X on every side** — top, bottom, left, right.
- For the **lockup**, X is the height of its mark; for the **emblem**, the height
  of its ring, not the whole artwork.
- Nothing — text, photo edges, page margins, or another logo — may enter that
  margin. When in doubt, give it more.

<figure style="margin:18px 0;background:#fafaff;border:1px solid #e1e1ee;border-radius:10px;padding:18px;display:flex;justify-content:center">
<svg width="340" height="190" viewBox="0 0 340 190" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="Clear-space diagram: keep half the mark height of free space around the logo on every side" font-family="Inter, system-ui, -apple-system, 'Segoe UI', sans-serif">
  <!-- logo bounding box -->
  <rect x="142" y="40" width="56" height="56" fill="none" stroke="#d0d0dc" stroke-width="1"/>
  <!-- the mark -->
  <image href="images/brand/bowire_mark_colored.svg" x="142" y="40" width="56" height="56"/>
  <!-- clear-space boundary: 28 = half of the 56 mark height (½ X) on every side -->
  <rect x="114" y="12" width="112" height="112" rx="3" fill="none" stroke="#6366f1" stroke-width="1.6" stroke-dasharray="6 4"/>
  <!-- X = mark height (left dimension) -->
  <g stroke="#5a5a72" stroke-width="1">
    <line x1="128" y1="40" x2="128" y2="96"/>
    <line x1="124" y1="40" x2="132" y2="40"/>
    <line x1="124" y1="96" x2="132" y2="96"/>
  </g>
  <text x="120" y="72" text-anchor="end" font-size="13" font-weight="600" fill="#1a1a2e">X</text>
  <!-- ½ X = clear space (top gap dimension) -->
  <g stroke="#5a5a72" stroke-width="1">
    <line x1="170" y1="12" x2="170" y2="40"/>
    <line x1="166" y1="12" x2="174" y2="12"/>
    <line x1="166" y1="40" x2="174" y2="40"/>
  </g>
  <text x="178" y="30" text-anchor="start" font-size="13" font-weight="600" fill="#1a1a2e">½ X</text>
  <!-- captions -->
  <text x="170" y="150" text-anchor="middle" font-size="12.5" fill="#5a5a72">Dashed margin = ½ X on every side</text>
  <text x="170" y="168" text-anchor="middle" font-size="12.5" fill="#5a5a72">X = the height of the mark</text>
</svg>
</figure>

## Minimum size

Below these sizes the bowline strands and the counters of the "bw" start to
fill in and the logo loses legibility.

| Form | Screen (min) | Print (min) |
| --- | --- | --- |
| Mark | 24 px tall | 8 mm tall |
| Emblem | 20 px tall (≈ 34 px wide) | 10 mm tall |
| Lockup | 24 px tall (≈ 130 px wide) | 10 mm tall |

Prefer the **mark** over a shrunk emblem or lockup whenever either would fall
below its minimum.

## Colour

The logo has two colours. **Navy** (`#003E60`) is the ink every Küstenlogik
product shares: the b of the mark, the emblem's disc and the "Bo" of the
wordmark. **Bowire Violet** (`#865BC4`) is Bowire's signature: the w of the mark,
the emblem's bowline frame and the "wire" of the wordmark. On dark backgrounds
the adaptive logo turns the navy **white** (`#FFFFFF`) and the violet lighter
(`#9369D4`); the `white` files are all white, for photos and coloured
backdrops. For single-colour print the whole logo is black. Never recolour the
logo otherwise.

| Colour | Hex | Use |
| --- | --- | --- |
| **Navy** | `#003E60` | The b, the disc and "Bo" on light backgrounds (11.3:1 on white) |
| **Bowire Violet** | `#865BC4` | Bowire's signature: the w, the bowline frame and "wire" on light backgrounds (4.88:1 on white) |
| Violet · on dark | `#9369D4` | The same parts on dark backgrounds (4.65:1 on `#121212`) |
| Bowire Indigo | `#6366F1` | The workbench's UI accent, not a logo colour |
| **Ink** | `#0F0F17` | Dark backgrounds |
| **White** | `#FFFFFF` | The logo on dark backgrounds |
| Off-white | `#FAFAFF` | Light backgrounds |

The product's wider UI palette (surfaces, borders, state colours) isn't part of
the logo, so it isn't needed to reference Bowire.

## Typography

| Typeface | Use | Weights | Notes |
| --- | --- | --- | --- |
| **Inter** | Headings, UI, the wordmark | 400 / 500 / 600 / 700 / 800 | The "Bowire" wordmark is Inter **Bold 700–ExtraBold 800**, tracking **−0.03em**. It's outlined in the lockup, so the font is never required to render the logo. |
| **JetBrains Mono** | Code, terminals, payloads | 400 / 500 | Anything monospaced. |

Both are open-source: [Inter](https://rsms.me/inter/) ·
[JetBrains Mono](https://www.jetbrains.com/lp/mono/).

## Co-branding

When the Bowire logo sits next to another logo (Küstenlogik, a partner, a
conference badge):

- **Separation: at least 2 X** between the logos (X = the mark's height). This
  is wider than the clear-space margin so neither mark crowds the other.
- Optionally place a **thin vertical divider** (1 px / 0.5 pt, in the border
  grey) centred in the gap.
- **Optically balance the sizes**: match the mark's height (or the wordmark
  cap-height) to the neighbouring logo's dominant element — not the bounding
  boxes, which read unevenly.
- Keep both logos in the same colourway family (all dark on light, or all light
  on dark).

## Backgrounds

Use the version that contrasts with the surface: the navy and violet logo on
light, the white logo on dark (or the adaptive one, white and light violet). On a coloured backdrop such as
Indigo, use the white logo.

<div style="display:flex;gap:14px;flex-wrap:wrap;margin:18px 0">
  <figure style="margin:0;flex:1;min-width:180px;border:1px solid #e1e1ee;border-radius:10px;overflow:hidden">
    <div style="background:#fafaff;padding:30px 20px;text-align:center"><img src="images/brand/bowire_lockup_h_colored.svg" alt="Bowire navy and violet logo on a light background" style="max-height:36px"></div>
    <figcaption style="padding:9px 12px;font-size:13px;color:#5a5a72;border-top:1px solid #e1e1ee">Light <code>#FAFAFF</code> &rarr; navy and violet logo</figcaption>
  </figure>
  <figure style="margin:0;flex:1;min-width:180px;border:1px solid #e1e1ee;border-radius:10px;overflow:hidden">
    <div style="background:#0f0f17;padding:30px 20px;text-align:center"><img src="images/brand/bowire_lockup_h_white.svg" alt="Bowire white logo on a dark background" style="max-height:36px"></div>
    <figcaption style="padding:9px 12px;font-size:13px;color:#5a5a72;border-top:1px solid #e1e1ee">Dark <code>#0F0F17</code> &rarr; white logo</figcaption>
  </figure>
  <figure style="margin:0;flex:1;min-width:180px;border:1px solid #e1e1ee;border-radius:10px;overflow:hidden">
    <div style="background:#6366f1;padding:30px 20px;text-align:center"><img src="images/brand/bowire_lockup_h_white.svg" alt="Bowire white logo on the brand indigo background" style="max-height:36px"></div>
    <figcaption style="padding:9px 12px;font-size:13px;color:#5a5a72;border-top:1px solid #e1e1ee">Indigo <code>#6366F1</code> &rarr; white logo</figcaption>
  </figure>
</div>

- On a photo or gradient, set the logo on a **solid or scrimmed panel** so it
  keeps its clear space and contrast — don't float it directly over busy imagery.
- Never place the navy and violet logo on a dark field, or the white logo on a light field.

## File formats

| Format | When | Notes |
| --- | --- | --- |
| **SVG** | Default, everywhere it's supported | Scales losslessly. `adaptive` follows the page's light or dark theme; `colored`, `white` and `black` are fixed. |
| **PNG / WebP** | Raster-only contexts (slides, raster email, app stores) | Supplied at 1× and 2× on a transparent background, every PNG also as a lossless WebP. Don't upscale. |
| **ICO / ICNS** | Browser-tab favicons, Windows and macOS app icons | Use the supplied `favicon.ico`, `bowire.ico` and `bowire.icns`. |

Every file is in the [brand kit](https://bowire.io/assets/brand/bowire_brand_kit.zip), named
`bowire_<form>_<colourway>`: `bowire_mark_colored.svg`, `bowire_lockup_h_white.png`, `bowire_emblem_adaptive.svg`, …

Never re-trace the logo from a screenshot or a raster export — always start from
the SVG.

## Accessibility

- **Contrast:** a logo is graphical content, so aim for at least **3:1** between
  the logo and its background (WCAG 1.4.11). The navy-on-white and
  white-on-ink pairings clear this comfortably; pick whichever variant maximises
  contrast on your surface.
- **Alt text:** use `alt="Bowire"` for the logo/lockup, or `alt="Bowire logo"`.
  Decorative repeats can use empty `alt=""`. Don't stuff the tagline into alt.
- **Don't rely on colour alone** to convey the brand — the mark's silhouette
  carries it in a single colour.

## Do & don't

**Do**

- Use the supplied SVGs at their native proportions.
- Pick the colourway that maximises contrast on your background.
- Keep the clear space and minimum sizes above.
- Drop to the mark alone when the emblem or the lockup won't fit.

**Don't**

- Recolour the logo (it is navy, white, or black), or add gradients, shadows, or outlines.
- Stretch, squash, rotate, skew, or otherwise distort it.
- Re-set the "Bowire" wordmark in a different typeface.
- Crop the mark, untie the bowline, or rebuild the artwork.
- Place it on a busy or low-contrast background.

## Writing "Bowire"

- **Bowire** — one word, capital B, lowercase rest. Always. Never *BoWire*,
  *bowire* (in prose), *BOWIRE*, or *Bow Wire*.
- The one-liner is "**The API workbench that embeds in your own service.**" Quote it verbatim everywhere Bowire gets one line —
  a wording that differs between surfaces reads as a different product (#668).
- Inside the product - the UI taglines, the default `BowireOptions.Description`
  an embedding host shows, the CLI help - Bowire describes itself as a
  "multi-protocol API workbench". That is a description for someone already
  using it, not the pitch, and it is shown inside other people's services.
- Built by **Küstenlogik**. Bowire is part of the Küstenlogik family.

The long form, for directories and articles that allow more than one line:

> The API workbench that embeds in your own service. Bowire mounts into your own ASP.NET service with one line, or runs standalone as a CLI. It discovers and calls gRPC, REST, GraphQL, MQTT, SignalR, WebSocket, MCP and more — and your own protocols through plugins. Open source, Apache-2.0. https://bowire.io

## Usage rights & permissions

The Bowire **software** is open source under Apache-2.0. The Bowire **name and
logos** are trademarks of Küstenlogik; the licence covers the code, not the
brand. These guidelines grant the everyday uses below — anything that implies
endorsement needs a quick email first.

**Fine without asking**

- Referring to Bowire in articles, talks, tutorials, and comparisons.
- Linking to bowire.io with the logo.
- Stating that your project "works with Bowire" or "supports Bowire."
- Unmodified use of the supplied assets.

**Ask first**

- Using the marks in your own product name, logo, domain, or app icon.
- Anything suggesting an official partnership, sponsorship, or endorsement.
- Merchandise.
- Modified or redrawn versions of the logo.

Questions or sign-off: info@kuestenlogik.com. For the parent identity, see the
[Küstenlogik brand](https://kuestenlogik.com).
