# What an automated browser pass cannot reach

The workbench is driven under test by a browser automation that works on a
**hidden** tab — `document.visibilityState === "hidden"`, and
`document.hasFocus()` false. Three classes of behaviour do not happen there,
and all three have been mistaken for product bugs at least once.

Read this before concluding that a workbench feature is broken because the
automated pass could not exercise it.

## 1. Frame callbacks do not fire

Browsers do not service `requestAnimationFrame` while the tab is hidden.
Measured in a running workbench: **7 callbacks scheduled, 0 run** over a full
second.

This used to be a product bug as well (#696): post-mount wiring was scheduled
on a frame, so a workbench restored into a background tab mounted with its
wiring absent, and anything that latched a flag before scheduling stayed
latched for the rest of the session — a streaming method left running while the
operator was in another browser tab froze, and stayed frozen after they came
back.

**Use `afterRender(fn)` for post-mount wiring** — resolving nodes by id and
attaching behaviour to them. It takes a frame when there will be one and a task
when there will not.

**Keep `requestAnimationFrame` for measurement and animation** — `offsetWidth`,
`getBoundingClientRect`, scroll positions, CSS transitions. A hidden tab reports
zero-sized boxes, so running those early computes a *wrong* layout rather than
no layout. Every such site carries a one-line note saying why it stays.

## 2. Focus events do not fire

With `document.hasFocus()` false, calling `element.focus()` still moves
`document.activeElement` — but **no `focus` / `focusin` event is dispatched**.

A feature that learns about the focused field only by listening is therefore
untestable, and fragile in production too: the listener only knows about fields
focused *after* it was attached, so a field focused by autofocus, or before
init ran, is invisible to it. The shelf (#251) hit exactly this; it now captures
the target on `mousedown`, before the click moves focus.

## 3. Real pointer gestures are not available

HTML5 drag-and-drop needs a genuine pointer sequence. Synthesised
`dragstart` / `drop` events do not carry a working `dataTransfer` in the way
the real ones do, so drag paths cannot be verified by the automated pass at
all — regardless of visibility.

Verify those by hand, and say so plainly in the ticket rather than ticking the
box.

## Practical consequences

- If a behaviour "does not happen" under automation, **measure before
  diagnosing**: count scheduled versus run callbacks, check
  `document.visibilityState`, check whether the event you depend on actually
  fired.
- Prefer wiring that does not depend on a frame, a focus event, or a gesture —
  it is more robust in production for the same reasons it is more testable.
- When something genuinely cannot be verified here, write that in the ticket
  next to the unticked box. An unticked box with a reason is worth more than a
  ticked one without evidence.
