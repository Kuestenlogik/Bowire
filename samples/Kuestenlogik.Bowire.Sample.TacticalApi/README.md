# Kuestenlogik.Bowire.Sample.TacticalApi

A minimal stand-alone gRPC server hosting Rheinmetall's TacticalAPI
`Situation` service. The point of the sample is to give an operator a
real endpoint to dial from Bowire's standalone Tool: invoke
`GetSituationObjects` and watch the seeded DACH situation objects
(airfields, harbours, stations) pin on the MapLibre viewer.

This sample is a **server only** — it does not host the Bowire workbench.
The Tool (standalone `bowire` process, typically at
`http://localhost:5180`) acts as the client.

## Run

```bash
dotnet run --project samples/Kuestenlogik.Bowire.Sample.TacticalApi --urls http://localhost:5182
```

Then open the Bowire Tool (`http://localhost:5180`) and add a gRPC
source pointed at `http://localhost:5182`. The
`Kuestenlogik.Bowire.Protocol.TacticalApi` plugin (shipped in
`Bundle.Workbench`, transitively via the Tool) matches the URL,
surfaces the `Situation` service in Discover, and lets you invoke
`GetSituationObjects`.

## What lands on the map

Each seeded `SituationObject` carries a `Symbol` whose `location.point`
holds a `GeoPoint` with `latitude_coordinate` + `longitude_coordinate`.
The `Wgs84CoordinateDetector` ships an anchored regex that matches both
the `lat` / `lon` short names **and** the upstream TacticalAPI
`*_coordinate` suffix (`lat(itude)?(coordinate)?`,
`(lon|lo?ng(itude)?)(coordinate)?`), so no field-mapping configuration
is required — the workbench's extension router resolves the
`coordinate.wgs84` semantic kind and mounts the MapLibre widget over
the response.

| UUID                | Display name              | Lat     | Lon    |
| ------------------- | ------------------------- | ------- | ------ |
| `frankfurt-airport` | Frankfurt Airport (EDDF)  | 50.0379 |  8.5622 |
| `munich-hbf`        | Munchen Hauptbahnhof      | 48.1402 | 11.5582 |
| `kiel-port`         | Hafen Kiel                | 54.3233 | 10.1396 |
| `hamburg-hbf`       | Hamburg Hauptbahnhof      | 53.5527 | 10.0067 |
| `berlin-tegel`      | Berlin Tegel (EDDT)       | 52.5597 | 13.2877 |
| `vienna-hbf`        | Wien Hauptbahnhof         | 48.1851 | 16.3754 |
| `zurich-hb`         | Zurich Hauptbahnhof       | 47.3779 |  8.5403 |
| `stuttgart-airport` | Stuttgart Airport (EDDS)  | 48.6898 |  9.2220 |

## Services

| RPC                              | Type       | Notes                                                  |
| -------------------------------- | ---------- | ------------------------------------------------------ |
| `GetSituationObjects`            | unary      | Returns the eight seeded points in one response.       |
| `SubscribeSituationObjectEvents` | server-stream | Emits the seeded list as a single frame, then closes. |

`AddOrUpdateSituationObjects` and `DeleteSituationObjects` are
deliberately left unimplemented — the sample is read-only so an operator
can demo the map flow without mutating state.
