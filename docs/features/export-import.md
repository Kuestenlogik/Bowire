---
summary: 'Bowire supports exporting requests as grpcurl commands and importing/downloading response data.'
---

# Export & Import

Bowire supports exporting requests as grpcurl commands and importing/downloading response data.

## Export as grpcurl

From the request editor, click the **Export** button to generate a grpcurl-compatible command for the current method and request body. The generated command includes:

- The fully qualified service and method name
- The request body as a `-d` JSON argument
- Any metadata headers as `-H` flags
- The server URL

Example output:

```bash
grpcurl -d '{"city":"Berlin"}' \
  -H "authorization: Bearer token123" \
  localhost:5001 \
  weather.WeatherService/GetCurrentWeather
```

This is useful for sharing exact invocations with team members or including them in documentation.

## JSON Response Download

Click the **Download** button in the response viewer to save the response body as a JSON file. For streaming responses, the download includes all received messages as a JSON array.

## Copy to Clipboard

Click the **Copy** button to copy the response body to your clipboard. For streaming responses, this copies all messages received so far.

## File-Based Input (CLI)

In CLI mode, use `@filename` to read the request body from a file:

```bash
bowire call --url https://server:443 \
  weather.WeatherService/GetCurrentWeather -d @request.json
```

This is useful for large or complex request bodies that are cumbersome to type inline.

See also: [CLI Mode](cli-mode.md), [UI Guide -- Response Pane](../ui-guide/response-pane.md)
