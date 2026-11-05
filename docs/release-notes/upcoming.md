---
# The release's `#` heading. A sentence about this delivery, not the version.
title: <fill in before the tag>
version: 2.11.0
---

<One-sentence frame for what 2.11 is about. Replaces this placeholder
before the tag.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern —
note the example heading is indented so the placeholder detector in
release.yml (which counts lines matching `^### `) does not mistake this
template for a filled-in body:

  ### <headline> (#issue)
  <2-4 sentences>
-->

### Streams and channels typed in by hand (#995)

The freeform form offered a method type for every protocol but always made a unary call. A server-streaming call typed in by hand now follows its stream frame by frame, and a client-streaming or duplex call opens a channel: connect, send each message, disconnect. Neither needs the method to have been discovered first.

Compose gains a form for every protocol plugin that describes one. SOAP, SignalR, NATS, Pulsar and Socket.IO do so far. You pick a call kind (produce or subscribe, invoke, stream or channel, publish, request or JetStream consume) and fill in the fields that kind needs, such as topic, hub method, subject, SOAPAction or namespace. Bowire assembles the call from them. The plugin describes its form on the server through the new `IBowireManualEntry` contract, so a plugin in its own repository adds a Compose form without workbench code. The freeform form now offers each protocol the method types its plugin names, instead of a list kept in the workbench.

## Fixes along the way

### A compiled sidecar ships one manifest for every platform (#990, #986)

On Windows a manifest's `executable` is now also looked up in the plugin directory with each `PATHEXT` extension, so `"executable": "echo"` starts the shipped `echo.exe` instead of the first `echo.exe` on `PATH`. A Go or Rust sidecar names its binary once and runs on Linux, macOS and Windows.

The .NET plugin loader also leaves sidecar folders to the sidecar loader, so a start no longer logs `ManifestMissing` for every installed sidecar (#986).

### A discovered SOAP call carries its SOAPAction and namespace (#1000)

Discovery read an operation's SOAPAction only into its description and did not read the target namespace at all, so a call to a discovered operation went out with an empty SOAPAction and an unqualified body element. The SOAP plugin now takes both from the WSDL. For a document-style operation the namespace is that of the input element, for an rpc-style one it is that of `soap:body`. A call made without discovering first, from the CLI, a flow or a recording, loads the WSDL once itself. Metadata the user enters still wins.

### A discovered NATS request waits for its reply (#1001)

NATS discovery gave the subscribe, publish and request of a subject one name, the subject itself, and a bare subject runs as a publish. A discovered request therefore never waited for a reply. Each operation now has its own name, such as `orders.created/request`, so the sidebar also tells the three apart. JetStream and Services methods reach their stream or service route through any caller, including MCP tools and benchmarks. A request saved with the bare subject runs as it did.

### A SOAP call entered by hand reaches the service (#996)

The freeform form sent its Metadata tab for ad-hoc REST only, so a SOAP call typed in by hand, against a service with no WSDL, went out without its `soap_action` and `target_namespace`. The metadata now reaches every protocol's invoke.

### An embedded workbench no longer keeps a disposed host alive (#989)

The plugin update check kept the `HttpClient` that `AddHttpClient` registers for the default name, and with it the host's root service provider, so a host that embeds the workbench was never collected after it was disposed. The check now owns its client, which also recycles its connections every two minutes so a DNS change reaches it. A test holds that no singleton Bowire registers takes an `HttpClient` from DI.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
