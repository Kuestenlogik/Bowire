---
title: Tabs and panes
summary: 'Open methods live in tabs above the request pane; a tab can be split to the right so two methods are on screen at once, each with its own live request, response and stream.'
---

# Tabs and Panes

Every method you open in Discover gets a **tab** in the strip above the request pane. Each tab owns its request (body, form values, input mode) and its response (result, stream frames, open channel). Switching tabs changes which one is in front; nothing is lost in the one you leave.

## Tabs

- **Click** a method in the sidebar to open it in the active tab. The tab is reused, browser-style — unless it has something live (an open channel, a running stream, a call in flight), in which case the method opens in a new tab beside it. The stream's tab is its only home, so it is never taken away from under it.
- **Ctrl/Cmd+click**, **middle-click**, or the row's context menu **Open in new tab** pins the method into a fresh tab and leaves the active one alone.
- **+** on the strip pins the current method into a new tab; **Shift+click** on it opens an empty tab.
- **×** closes a tab. Closing a tab closes whatever it held open — a stream, a channel, an in-flight request.
- A tab's context menu offers **Close**, **Close others**, **Close tabs to the right**, and **Split right**.

## Panes

The main area holds one or two **panes** side by side. Each pane has its own tab strip and its own active tab, so two methods can be live at once: a stream in the left pane keeps filling while you edit and send in the right one.

### Splitting

Three ways to open the second pane, all carrying the tab over:

- the **Split right** button on the tab strip (the two-column icon beside **+**),
- **Split right** in the tab's context menu,
- **Ctrl/Cmd+\\**.

With two panes open the same actions read **Move to other pane** and move the tab across. Two panes is the limit.

### Focus

One pane is the **focused** pane — marked by an accent line along its tab strip. Whatever you click in a pane focuses it. The focused pane is where a sidebar click lands, what **Ctrl+Enter** runs, and what **Ctrl+W**, **Ctrl+Tab** and **Ctrl+1–9** address; the other pane's strip is its own ring.

### Resizing and folding

Drag the divider between the panes to change their widths; double-click it for an even split. The widths are remembered per workspace, as is the split itself: a reload brings both panes back with their tabs.

Closing the last tab of a pane folds the split — the remaining pane takes the whole width. Moving a pane's only tab over does the same.

## Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl/Cmd+W` | Close the active tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab in the focused pane |
| `Ctrl/Cmd+1`…`9` | Jump to the Nth tab of the focused pane |
| `Ctrl/Cmd+\` | Split the active tab to the right, or move it to the other pane |
| `Ctrl/Cmd+Alt+\` | Cycle the request/response split of the active tab (vertical / horizontal) |

See also: [Request pane](request-pane.md), [Response pane](response-pane.md), [Multi-channel](../features/multi-channel.md)
