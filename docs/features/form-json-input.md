---
summary: 'Bowire provides two input modes for composing requests: a structured form view and a raw JSON editor.'
---

# Form & JSON Input

Bowire provides two input modes for composing requests: a structured form view and a raw JSON editor.

## Form Mode

When you select a method, Bowire auto-generates a form from the method's input schema. Each field appears as a labeled input with its type indicated (string, int32, bool, etc.). Nested messages render as collapsible groups.

Form mode is ideal for:

- Quick edits to individual fields
- Exploring unfamiliar schemas where field names and types guide you
- Avoiding JSON syntax errors

## JSON Mode

Switch to JSON mode for full control over the request body. Bowire generates a JSON template from the schema with placeholder values, so you start with a valid structure.

JSON mode is ideal for:

- Pasting request bodies from other tools
- Complex nested structures with repeated fields
- Copying requests for documentation or sharing

## Auto-Generated Templates

When you first select a method, Bowire creates a template based on the schema:

```json
{
  "name": "string",
  "age": 0,
  "active": false,
  "tags": []
}
```

Placeholder values match the field type: `"string"` for strings, `0` for numbers, `false` for booleans, and `[]` for repeated fields. Nested messages include their own fields recursively.

## Switching Between Modes

Toggle between form and JSON input using the mode switch above the request editor. Changes in one mode are preserved when switching to the other -- the underlying JSON is kept in sync.

## Configuration

Input mode is a per-session preference stored in the browser's localStorage. There is no server-side configuration for input mode.
