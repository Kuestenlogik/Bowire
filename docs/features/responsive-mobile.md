---
summary: "Bowire's UI is fully responsive and works on mobile devices, tablets, and desktops."
---

# Responsive & Mobile

Bowire's UI is fully responsive and works on mobile devices, tablets, and desktops.

## Layout Behavior

On wide screens (desktop), the UI shows a three-panel layout: sidebar, request editor, and response viewer side by side. On narrower screens, panels stack vertically with the sidebar collapsible via a hamburger menu.

## Mobile Experience

On mobile devices, Bowire provides:

- **Collapsible sidebar** -- tap the menu icon to show/hide the service list
- **Full-width panels** -- request and response editors use the full screen width
- **Touch-friendly controls** -- buttons and inputs are sized for touch interaction
- **Swipe gestures** -- swipe to dismiss panels or navigate between views

## No Framework Dependency

Bowire's UI is built with pure HTML, CSS, and JavaScript. There is no Blazor, React, or Angular dependency. This keeps the bundle small and ensures fast loading on any device, including low-bandwidth mobile connections.

## Customization

The UI respects your system's dark/light mode preference by default. You can override this with the theme toggle (`t` shortcut) or via configuration:

```csharp
app.MapBowire(options =>
{
    options.Theme = BowireTheme.Light;
});
```

See also: [Keyboard Shortcuts](keyboard-shortcuts.md)
