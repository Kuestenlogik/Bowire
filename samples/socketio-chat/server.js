// Socket.IO chat sample for the Bowire Socket.IO plugin demo.
// Uses Node + the canonical socket.io npm package because there is no
// first-class .NET Socket.IO server library — so, unlike the other
// per-protocol samples in this folder, this one is a Node project (not a
// .csproj) and is not part of the dotnet solution build.

import { createServer } from "node:http";
import { Server } from "socket.io";

const port = 5189;
const httpServer = createServer((_, res) => {
  res.writeHead(200, { "content-type": "text/plain" });
  res.end("Bowire Socket.IO chat sample. Connect via Socket.IO at /\n");
});

const io = new Server(httpServer, { cors: { origin: "*" } });

io.on("connection", (socket) => {
  console.log(`[chat] connected ${socket.id}`);

  socket.on("chat:send", ({ user, text }) => {
    io.emit("chat:message", { user, text, at: new Date().toISOString() });
  });

  socket.on("echo", (payload, ack) => {
    if (typeof ack === "function") ack({ echoed: payload });
  });

  socket.on("disconnect", () => console.log(`[chat] disconnected ${socket.id}`));
});

httpServer.listen(port, () => console.log(`[chat] listening on http://localhost:${port}`));
