# Protocol brand assets

Logos and marks for protocols Bowire integrates with, kept here so the
marketing site can reference them by stable path without bundling them
into the JS package.

## AsyncAPI

Source: [asyncapi/brand](https://github.com/asyncapi/brand/tree/master/logos/asyncapi/mark)
&middot; Licence: Apache-2.0 (same as the AsyncAPI initiative)

| File | Source path | When to use |
|---|---|---|
| `asyncapi-mark--primary.svg` | `logos/asyncapi/mark/primary/SVG/asyncapi-logo-mark--primary.svg` | Brand-coloured glyph (cyan&rarr;purple&rarr;magenta gradient). Works on both light + dark backgrounds; use as the default mark wherever AsyncAPI is identified by its brand. |
| `asyncapi-mark--outline-dark.svg` | `logos/asyncapi/mark/outline/dark/SVG/` | Single-colour `#1b1130` outline. Use in light-theme contexts where the page background already carries colour and the gradient mark would compete. |
| `asyncapi-mark--outline-light.svg` | `logos/asyncapi/mark/outline/light/SVG/` | Single-colour `#fff` outline. Dark-theme counterpart of the above. |

The Bowire **app** (sidebar plugin icon in `BowireAsyncApiProtocol.IconSvg`)
uses the primary mark inline — it carries its own colours and reads in
both themes without a swap. The outline variants are reserved for the
marketing site, where a CSS `prefers-color-scheme` / theme-class switch
picks the right one against the current background.

When adding marks for other protocols, follow the same pattern: stable
file name, license + source path documented here, primary variant inline
in the plugin, outline variants on disk for the site.
