---
title: Settings
summary: "The Settings dialog provides a centralized place to configure Bowire's behavior, review keyboard shortcuts, manage stored data, and adjust plugin-specific options."
---

# Settings Dialog

The Settings dialog provides a centralized place to configure Bowire's behavior, review keyboard shortcuts, manage stored data, and adjust plugin-specific options.

## Opening settings

Click the **gear icon** in the topbar to open the Settings dialog. Press **Esc** or click outside the modal to close it.

The dialog has a category sidebar on the left and a settings panel on the right. Select a category to see its options.

## General

The General tab controls core UI behavior.

### Theme

Choose between three options:

| Option | Behavior |
|--------|----------|
| **Auto (follow OS)** | Matches your operating system's light/dark preference |
| **Dark** | Always use the dark color scheme |
| **Light** | Always use the light color scheme |

The theme preference is saved in `localStorage` and applies immediately.

### Auto-interpret JSON

When enabled (the default), Bowire parses JSON payloads in WebSocket, MQTT, and SSE responses for pretty-printed display with syntax highlighting. Disable this if you work with non-JSON text protocols and want raw output.

### Schema Watch interval

Time to wait after one re-discovery **finishes** before starting the next. In seconds, minimum 5, maximum 300, default 15; a value outside those bounds is clamped rather than rejected.

It is a gap, not a fixed cadence, and the distinction matters: a discovery fans out over every loaded plugin and takes seconds against a real server (5-8 s is typical). The watch waits for a poll to settle before re-arming, so two discoveries are never in flight at once. Setting 5 s does not mean "every 5 s" -- it means "5 s of quiet between polls".

Schema Watch is useful during active development -- your server's service definitions are polled at this interval, and each poll is compared against the previous one. What moved is shown in the Discover sidebar:

| Mark | Where | Meaning |
|---|---|---|
| `+` | on the method row | The method appeared since the last poll. |
| `~` | on the method row | The name stayed put but the shape moved -- a parameter, a required flag, an HTTP verb or path, a streaming direction, the `deprecated` flag. A saved request still opens and still sends, and fails at the far end. |
| `+n −n ~n` | on the service header | Per-service tally, so a change is findable without expanding every group. |
| summary banner | above the tree | One line for the whole delta, plus the removals -- a method that is gone has no row left to mark, so this is the only place it appears. Dismiss with the `×`. |

Prose-only edits (a changed summary or description) are deliberately **not** toasted or marked: descriptions move constantly while a schema is under development, and alerting on them would train you to ignore the alert. They ARE recorded in the change log below, as the quiet `±` annotation type.

#### The change log ([#185](https://github.com/Kuestenlogik/Bowire/issues/185))

Every detected change is also appended to a per-workspace **change log**, kept for **7 days** on the server (`workspaces/<id>/schema-changes/log.json`, capped at 500 entries, oldest out first; entries are stamped with the server clock, and two clients observing the same transition within two minutes count as one change), so "what changed while I was at lunch" survives a reload and reaches every client of the workspace:

- A **statusbar pill** next to the watch toggle reads "3 changes since 14:30" while there are unread changes, and decays to a quiet "12 changes · 7d" once read. Click it for the chronological log; opening the log marks it read.
- The **Discover rail icon** carries a gently pulsing dot while unread changes exist.
- Each logged change is classified: `+` added (green), `−` removed (red), `~` signature change (yellow, with the facet that moved -- "route GET /pets → POST /pets", "request shape changed"), `!` deprecation (yellow), `±` annotation (muted).
- **Clicking a change navigates** to the affected method in Discover. Removed entries are inert -- there is nothing left to open.

#### Per-workspace interval

The interval above is the global default. A workspace can override it in **Workspaces → (workspace) → General → Schema watch**; the override is read the next time the watch starts, same as the global. Leave the field empty to inherit.

## Shortcuts

The Shortcuts tab shows a read-only keyboard reference for all available shortcuts.

| Shortcut | Action |
|----------|--------|
| `Ctrl+Enter` | Execute request / Send message |
| `?` | Show/hide shortcuts overlay |
| `Esc` | Close dialog / Stop streaming / Disconnect |
| `/` | Focus command palette |
| `t` | Toggle theme (Auto / Dark / Light) |
| `f` | Toggle Form/JSON mode |
| `r` | Repeat last call |
| `j` | Next method (sidebar) |
| `k` | Previous method (sidebar) |

This is the same set shown by pressing `?` from the main UI, collected here for reference.

## Data

The Data tab provides destructive actions for managing Bowire's stored state. Every action prompts for confirmation before proceeding.

### Clear call history

Removes all request history entries. This clears the history panel in the sidebar. Call history is stored in `localStorage`.

### Clear favorites

Removes all starred methods. Favorites can be re-added by clicking the star icon next to any method.

### Reset all settings

Clears **all** `localStorage` data -- history, favorites, environments, collections, flows, theme preference, and plugin settings -- then reloads the page. This is irreversible and returns Bowire to its initial state.

## Plugin settings

Protocol plugins can define their own settings by including a `settings` array in their `IBowireProtocol` registration. When a plugin provides settings, a new category appears in the Settings sidebar using the plugin's name and icon.

Bowire supports four setting types:

| Type | Rendered as |
|------|-------------|
| `bool` | Toggle switch |
| `number` | Numeric input |
| `select` | Dropdown with predefined options |
| `string` (default) | Text input |

Each setting has a `key`, `label`, optional `description`, and optional `defaultValue`. Values are stored in `localStorage` under the key `bowire_plugin_<pluginId>_<settingKey>`.

### Example: plugin settings definition

A protocol plugin might expose settings like this:

```csharp
public IReadOnlyList<PluginSetting> Settings => new[]
{
    new PluginSetting
    {
        Key = "timeout",
        Label = "Request timeout",
        Description = "Maximum seconds to wait for a response",
        Type = "number",
        DefaultValue = 30
    },
    new PluginSetting
    {
        Key = "verboseLogging",
        Label = "Verbose logging",
        Description = "Log raw protocol frames to the console",
        Type = "bool",
        DefaultValue = false
    }
};
```

These settings appear automatically in the Settings dialog under the plugin's name. Plugin code reads current values at runtime via `getPluginSetting(pluginId, key, defaultValue)`.

## About

The About tab displays:

| Field | Content |
|-------|---------|
| **Version** | Current Bowire version |
| **Mode** | UI mode (standalone, embedded, etc.) |
| **Protocols** | Comma-separated list of loaded protocol plugins |
| **Services** | Total number of discovered services |
| **Methods** | Total number of discovered methods |

Links to the GitHub repository and online documentation are provided at the bottom.

## Tips

- The Settings dialog is **non-blocking** -- you can open it, check a shortcut, and close it without interrupting your current request.
- Plugin authors: define settings for anything that changes behavior at runtime (timeouts, log levels, display preferences). Avoid settings for things that should be environment variables.
- Use **Reset all** when troubleshooting -- it guarantees a clean slate without reinstalling.

See also: [Keyboard Shortcuts](keyboard-shortcuts.md), [Plugin System](plugin-system.md)
