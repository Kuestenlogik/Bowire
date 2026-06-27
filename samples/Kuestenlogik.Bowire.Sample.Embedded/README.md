# Kuestenlogik.Bowire.Sample.Embedded

A minimal ASP.NET Core host that mounts the Bowire workbench at `/bowire`
alongside a handful of its own REST endpoints. The point of the sample
is to let an operator see whether Bowire — embedded in someone else's
app — actually auto-discovers that app's own routes, intercepts its
own traffic, and (since v1.3) auto-mounts semantic-kind extensions
against shape-matched responses.

## Run

```bash
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Embedded --urls http://localhost:5181
```

Then open `http://localhost:5181/bowire` and add a REST source pointed at
`http://localhost:5181`.

## Endpoints

| Method | Path                     | Notes                                                                 |
| ------ | ------------------------ | --------------------------------------------------------------------- |
| GET    | `/api/users`             | Collection of sample users.                                           |
| GET    | `/api/users/{id}`        | Single user, 404 when unknown.                                        |
| POST   | `/api/users`             | Echoes a created user.                                                |
| GET    | `/api/products`          | Collection of sample products.                                        |
| GET    | `/api/health`            | Liveness probe.                                                       |
| GET    | `/api/locations`         | Geographic points (DACH cluster) — drives the MapLibre map widget.    |
| GET    | `/api/locations/{id}`    | Single point — non-collection response, useful for invoke flow tests. |

## Seeing the map widget

The csproj references `Kuestenlogik.Bowire.Map`, so the
`MapLibreExtension` ships with the host and registers itself on assembly
load (via `[BowireExtension]`). Each location's `coordinate` object has
the shape `{ "lat": ..., "lon": ... }` — exactly what Bowire's built-in
`Wgs84CoordinateDetector` looks for. No OpenAPI extension, no
`x-bowire-semantic-kind` annotation, no per-field configuration. The
detector tags both fields the first time a frame is observed (REST GET
runs through `FrameProbingMiddleware.Observe` on the unary invoke path),
and the workbench's extension router mounts the `coordinate.wgs84`
viewer against the resolved parent path.

To try it from the workbench:

1. Pick the REST source pointing at this host.
2. Open `GET /api/locations/{id}` and invoke with `id=fra-airport`.
   The response panel renders the JSON **and** a MapLibre map pinned on
   Frankfurt Airport's `(50.0379, 8.5622)`.
3. Open `GET /api/locations` (collection) and invoke. The map shows
   every point as a marker — one map widget per detected
   `coordinate.wgs84` parent.

## Basemap

The map widget defaults to MapLibre's own `demotiles` style — fully
hosted by MapLibre, zero local config needed. The standalone tool
accepts `--map-basemap demotiles` (default), `--map-basemap osm`, and
`--map-basemap satellite`; in embedded mode the same options are
available via the `Bowire:MapTileUrl` configuration entry. When no
tile URL is configured at all the widget falls back to MapLibre's
no-source style so the no-network guarantee still holds — markers
render against a blank canvas, the geometry is still readable.
