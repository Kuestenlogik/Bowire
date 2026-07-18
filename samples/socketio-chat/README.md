# socketio-chat

A Node.js Socket.IO chat server for the Bowire Socket.IO plugin demo.

**The one non-.NET sample in this folder.** Socket.IO has no first-class
.NET server library, so this sample is a Node project (`server.js` +
`package.json`), not a `.csproj` — it is *not* part of the dotnet
solution build. It lives here because the Socket.IO plugin
(`Kuestenlogik.Bowire.Protocol.SocketIo`) ships from this repo, so its
sample belongs next to it.

Because the server is Node, it can't embed the Bowire workbench the way
the .NET samples do — run it standalone and point Bowire at it.

## Run

```pwsh
cd samples/socketio-chat
npm install
npm start
```

Listens on `http://localhost:5189`.

## Connect from Bowire

Point the workbench (or the CLI) at the server:

```pwsh
bowire --url socketio@http://localhost:5189
```

The plugin lists events under the `/` namespace; `chat:send` broadcasts
and `echo` (with ack) round-trips to the caller.
