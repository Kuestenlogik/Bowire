---
title: Outbound connections
summary: 'Every connection Bowire makes on its own initiative, and the switch that turns it on. Out of the box it talks to the services you point it at and to nothing else — no telemetry, no account, no update check.'
---

# Outbound connections

Bowire talks to the services you point it at. Everything else it could reach is **off until you switch it on** — that is a house rule, and this page is the list to hold it to. There is no telemetry sent to Küstenlogik, no account, and no background call to anything you did not configure.

| Connection | When | Switch |
|---|---|---|
| The URLs you add, the schemas you upload | Always — that is the job | You add them |
| A catalogue (HTTP, Consul, Kubernetes, an agent hub) | Only when a catalogue provider is configured | `Bowire:Discovery:Catalogue:Provider` — [Service catalogue](../features/catalogue.md) |
| nuget.org for plugins | When you run `bowire plugin install` | The command itself |
| nuget.org, once a day, for newer plugin versions | Only when the update check is on | `--update-check` / `Bowire:PluginUpdateCheck:Enabled` |
| An AI provider | Only the provider you choose; the default is a local Ollama on loopback | Settings → AI, `Bowire:Ai:*` — [AI assistant](../features/ai-assistant.md) |
| Map tiles | Only when a basemap is set; the built-in one is local | `Bowire:MapBasemap` |
| OpenTelemetry export (self-observability) | Only with the `Kuestenlogik.Bowire.Telemetry` package enabled, and to your own collector | `Bowire:Telemetry:*` |
| Alerts from scheduled probes (PagerDuty, Slack) | Only when an alert channel is configured | Monitoring settings |
| A Bowire agent hub | Only when this Bowire is an agent | `Bowire:Agent:HubUrl` — [Agent hub](../features/agent-hub.md) |
| Parallel-session executors | Only the hosts you list in a distributed run | The run's Hosts — [Parallel sessions](../features/parallel-sessions.md) |
| A workspace template's example service (e.g. the public Petstore) | Only when you create a workspace from that template | Your choice of template |
| The VS Code extension fetching a CLI | Only when no `bowire` is found, and only after it asks | `bowire.autoDownload` (`prompt` by default) |

Nothing on this list is on in a fresh install except the first row. If you find Bowire contacting something that is not here, that is a bug — please [report it](https://github.com/Kuestenlogik/Bowire/issues/new).

The source is Apache 2.0, so each row can be checked against the code that makes the connection.

Behind a corporate proxy, every row on this list goes through the proxy you configure, with the same bypass list and trusted CAs — see [Proxy and certificates](proxy-and-certificates.md).
