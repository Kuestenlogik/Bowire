---
title: Agent hub
summary: 'One Bowire lists every other Bowire in a fleet. Agents push their name, tags, workbench link and services to the hub; the hub shows them, serves them as a catalogue, and offers the ones tagged parallel-executor as executors for parallel sessions.'
---

# Agent hub

A Bowire embedded in each service of a fleet is handy until someone has to find them all. The **hub** is one Bowire that knows the others: every **agent** registers with it and keeps the registration fresh, and the hub lists them with a link to each one's own workbench.

The hub does not proxy. Opening an agent opens that agent's workbench, with that agent's authentication — the hub never carries traffic to a service, so it never needs rights on one.

## Running a hub

Any Bowire becomes a hub with one setting:

```json
{
  "Bowire": {
    "Hub": {
      "Enabled": true,
      "Token": "…",
      "TrustedAgentPrefixes": [ "https://orders.internal/", "https://payments.internal/" ]
    }
  }
}
```

| Setting | Environment variable | Effect |
|---|---|---|
| `Bowire:Hub:Enabled` | `BOWIRE_HUB_ENABLED` | Maps the hub under `/hub` and lists the agents under **Sources** of every workspace, each with a link to its workbench. |
| `Bowire:Hub:Token` | `BOWIRE_HUB_TOKEN` | Every `/hub/*` call needs `Authorization: Bearer <token>`, otherwise **401**. Unset, the hub takes registrations only from loopback — anyone else could otherwise put links of their choosing into the list. |
| `Bowire:Hub:TrustedAgentPrefixes` | — | Set, an agent's workbench URL has to start with one of these, otherwise **400**. A list, or one string separated by `,` or `;`. |

In your own host, `app.MapBowireHub()` maps the same endpoints without the setting.

| Endpoint | |
|---|---|
| `POST /hub/agents` | Register or heartbeat. The same service name and instance id update one entry; the answer is the agent's id. |
| `GET /hub/agents` | Every agent, with `lastSeen` and `live`. |
| `GET /hub/agents/catalogue` | The live agents' services, in the shape the [agent catalogue provider](catalogue.md) reads. |
| `DELETE /hub/agents/{id}` | Deregister. |

An agent is **live** while its last heartbeat is younger than three of its heartbeat intervals. After that it stays listed, greyed out, and after thirty intervals of silence it is dropped. The list lives in memory: agents push, so a restarted hub is whole again after one heartbeat.

## Making a Bowire an agent

Point it at the hub:

```json
{
  "Bowire": {
    "Agent": {
      "HubUrl": "https://bowire-hub.internal",
      "Token": "…",
      "ServiceName": "orders",
      "Tags": [ "env:prod", "region:eu", "parallel-executor" ]
    }
  }
}
```

or in code, on the options of `MapBowire`:

```csharp
app.MapBowire(options =>
{
    options.Agent.HubUrl = "https://bowire-hub.internal";
    options.Agent.ServiceName = "orders";
    options.Agent.Tags.Add("env:prod");
});
```

| `Bowire:Agent:*` | Default | |
|---|---|---|
| `HubUrl` | — | The hub's base URL. Without it the Bowire is no agent. |
| `Token` | — | The hub's token. |
| `ServiceName` | the application name | The name the hub lists. |
| `InstanceId` | the machine name | Tells instances of one service apart. |
| `Version`, `Owner` | — | Shown in the hub. |
| `Tags` | — | Labels to filter on; `parallel-executor` offers this Bowire as an executor. |
| `CallbackUrl` | the listening address plus the Bowire route | Where the hub links to. Set it behind a reverse proxy. |
| `HeartbeatSeconds` | `30` | How often the registration is refreshed. |

Once the host listens, the agent registers, then heartbeats; on shutdown it deregisters. The services it announces are its configured server URLs, or, in embedded mode, the host itself. A hub that is down or refuses is logged once and retried at every heartbeat — the agent never fails its host over the hub.

## The hub as a catalogue

Another Bowire can take the fleet as its URL catalogue with the `agent` provider:

```json
{
  "Bowire": {
    "Discovery": {
      "Catalogue": {
        "Provider": "agent",
        "Agent": { "HubUrl": "https://bowire-hub.internal", "BootstrapToken": "…" }
      }
    }
  }
}
```

The provider ships in `Kuestenlogik.Bowire.Catalogue.Agent`: `bowire plugin install Kuestenlogik.Bowire.Catalogue.Agent` for the CLI, or the package plus `builder.Services.AddBowireAgentCatalogue(builder.Configuration)` in your own host.

Each entry carries the tags `agent:<id>` and `service:<name>` next to the agent's own, so the catalogue filter narrows it to one agent or one service.

## Executors for parallel sessions

In the hub's own workbench, the **Parallel sessions** dialog lists the live agents tagged `parallel-executor` under the Hosts field; a click adds one. The executor still decides what it runs — see [Running an executor safely](parallel-sessions.md#running-an-executor-safely).
