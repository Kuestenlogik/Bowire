# Socket.IO — Chat sample

Node.js Socket.IO chat server. The Bowire Socket.IO plugin can connect
and emit / receive events on the default namespace.

## Run

```pwsh
cd examples/SocketIo/Chat
npm install
npm start
```

Listens on `http://localhost:5189`.

## Connect from Bowire

Server URL: `http://localhost:5189`. The plugin lists events under the
`/` namespace; `chat:send` broadcasts and `echo` (with ack) round-trips
to the caller.
