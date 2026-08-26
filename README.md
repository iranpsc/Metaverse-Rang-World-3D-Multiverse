# Metaverse Server

Modular server infrastructure for metaverse gameplay, identity management, realtime communication, Room management, and Dedicated Game Server allocation.

## Overview

The server starts from `src/index.js`. It validates the environment, connects to MongoDB, prepares the public Lobby Room, creates the HTTP server, attaches GameServerControl, starts the shared Realtime core, and finally registers the gRPC services and realtime streaming service.

The API core is implemented with gRPC. WebGL uses Envoy and gRPC-Web over HTTP/1.1 because browser networking imposes different transport constraints. Native clients such as Windows and Quest use gRPC directly. Realtime uses two transport adapters: WebSocket and bidirectional gRPC streaming. Both adapters use the same Realtime Core, Router, RoomManager, Registry, and Game Services.

## Startup Flow

```text
src/index.js
  -> validateConfig()
  -> connectDatabase()
  -> ensurePublicLobbyRoomReady()
  -> createHttpServer()
  -> attachGameServerControl()
  -> attachRealtime()                         # WebSocket
  -> registerPublicLobbyInRoomManager()
  -> startGrpcServer()
       -> register AuthService
       -> register HealthService
       -> attachGrpcStreamingRealtimeCore()
  -> setupGracefulShutdown()
```

## Repository Structure

```text
metaverse-server/
├── src/index.js
├── src/config/
├── src/core/auth/
├── src/domain/auth/
├── src/infra/mongo/
├── src/transport/grpc/
├── src/realTime/
├── src/gameServerControl/
├── src/integrations/microservice/
├── src/health/
├── src/http/
├── protos/
└── envoy/
```

## Application Entry Point

### `src/index.js`

### `main()`

The main startup function. It validates configuration, connects to MongoDB, prepares the public Lobby Room, creates HTTP and realtime runtimes, starts gRPC, and registers graceful shutdown.

### `validateConfig()`

Validates the required gRPC, MongoDB, JWT, TLS, and internal-service configuration before any runtime is created.

### `connectDatabase()`

Creates the Mongoose connection used by the User, Room, RefreshToken, and profile repositories.

### `ensurePublicLobbyRoomReady()`

Creates or restores the public Lobby Room in MongoDB. The resulting Room definition is later registered in memory with `RoomManager.registerPermanentRoom()`.

### `createHttpServer()`

Creates the main HTTP server and connects the JWKS, Health, and Game Server Control handlers. Unknown routes return HTTP 404.

### `setupGracefulShutdown()`

Registers controlled shutdown for HTTP, gRPC, Realtime, and GameServerControl so connections, streams, sessions, and timers are released in order.

## Environment Configuration

### Location

`src/config/env.js`, `src/config/validation.js`, `src/config/tls.js`

### `loadConfig()`

Reads the gRPC, WebSocket, Envoy, MongoDB, JWT, TLS, and microservice settings from the environment and produces the internal `cfg` object.

### `validateConfig()`

Validates ports, durations, file paths, security options, and required values before the server connects its runtimes.

| Service | Default | Purpose |
|---|---:|---|
| gRPC | `50051` | API and native streaming |
| WebSocket | `8080` | Realtime WebSocket transport |
| Envoy TLS | `8443` | WebGL and gRPC-Web gateway |

## Protobuf Contracts

### Location

`protos/auth/auth.proto`, `protos/health.proto`, `protos/realtime/realtime.proto`, and `protos/realtime/realtime_stream.proto`

### `AuthService`

Defines Register, Login, LoginWithMicroservice, Refresh, GetUserData, GetMicroserviceUserData, Logout, and LogoutAllDevices.

### `HealthService.Check()`

Provides the base gRPC health check operation.

### `RealtimeStreamService.Open()`

Creates a bidirectional stream. Each `RealtimeRawJson` message carries the realtime JSON envelope in its `rawJson` field.

## gRPC Server

### Location

`src/transport/grpc/server.js`

### `loadBaseGrpcProto()`

Loads the protobuf contracts through `@grpc/proto-loader` and applies the project options for field names, enums, long values, and oneof messages.

### `registerBaseGrpcServices()`

Registers Auth and Health on `grpc.Server`. Auth calls are wrapped by authentication and logging interceptors.

### `startGrpcServer()`

Creates the gRPC server, registers the base services, invokes the pre-bind hook for realtime streaming, and binds the server to the configured gRPC endpoint.

## Authentication

### Location

`src/transport/grpc/handlers/auth.handler.js`, `src/core/auth/auth.service.js`, and `src/domain/auth/`

### `AuthService.register()`

Normalizes the email and username, checks duplicates, hashes the password, creates the user through the repository, and issues access and refresh tokens.

### `AuthService.login()`

Finds the user by email, verifies the password, updates the last-login value, and issues new access and refresh tokens.

### `AuthService.refresh()`

Validates the refresh token and its session, rotates the token pair, and returns the new authentication response.

### `AuthService.logoutCurrentSession()`

Revokes only the refresh token belonging to the current session.

### `AuthService.logoutAllDevices()`

Revokes every refresh token belonging to the user.

### `getAuthenticatedUserId()`

Uses the user injected by the interceptor when available. Otherwise, it reads the Bearer token from gRPC metadata, verifies it, and extracts the user identifier from the `sub` claim.

## MongoDB and Repositories

### Location

`src/infra/mongo/connection.js`, `src/infra/mongo/models/`, and `src/infra/mongo/repositories/`

### `connectDatabase()`

Creates the Mongoose connection and propagates connection failures to startup.

### `UserRepository`

Handles user creation, lookup by email or username, and last-login updates.

### `RefreshTokenRepository`

Stores, finds, rotates, and revokes refresh tokens.

### `RoomRepository`

Stores Room capacity, type, public status, and Lobby metadata.

## Realtime Core

### Location

`src/realTime/core/realtimeServer.js`

### `RealtimeServer.start()`

Connects transport callbacks to the core handlers for connection, message, close, and error events.

### `RealtimeServer.handleTransportConnection()`

Creates a `RealtimeContext` and a `RealtimeConnection` for each transport connection and registers the connection in `ClientsRegistry`.

### `RealtimeServer.handleTransportMessage()`

Receives the raw message, executes flood protection, parses the envelope, and forwards the valid envelope to the Router.

### `RealtimeServer.dispatchEnvelope()`

Passes the validated envelope to `realtimeRouter`.

### `RealtimeServer.handleTransportClose()`

Executes the close lifecycle, removes the connection from the Registry, and marks its context as closed.

## WebSocket Realtime

### Location

`src/realTime/index.js` and `src/realTime/transport/websocket/`

### `attachRealtime()`

Creates the Registry, RoomManager, Router, flood protection, acknowledgement tracker, and Game Services, then attaches `WebSocketRealtimeTransport` to the shared Realtime Core.

### `WebSocketRealtimeTransport`

Converts the raw WebSocket connection into the internal transport contract and forwards incoming messages and outgoing envelopes.

### `attachRealtimeHeartbeatToContext()`

Starts a heartbeat for each WebSocket connection and closes the connection when the heartbeat timeout is reached.

## gRPC Streaming Realtime

### Location

`src/realTime/transport/grpcStreaming/`

### `attachGrpcStreamingRealtimeCore()`

Creates the gRPC streaming transport, reuses shared Registry and Room dependencies when provided, and registers `RealtimeStreamService` on the main gRPC server.

### `GrpcStreamingRealtimeTransport.handleStream()`

Converts each gRPC RPC stream into a realtime connection and connects its data, end, close, and error events to the core.

### `handleIncomingFrame()`

Reads the `rawJson` field from the incoming frame and passes it to `RealtimeServer.handleTransportMessage()`.

### `sendRaw()`

Writes the outgoing envelope to the same stream inside the `rawJson` field.

## Envelope and Router

### Location

`src/realTime/protocol/` and `src/realTime/router/`

### `parseEnvelope()`

Parses the JSON message and validates fields such as `id`, `type`, `channel`, `room`, and `payload`.

### `serializeEnvelope()`

Converts the internal envelope into JSON that can be sent through WebSocket or gRPC streaming.

### `realtimeRouter()`

Routes messages to Lobby, Chat, Game, Presence, NPC, System, or World handlers according to the envelope type and channel.

### `makeErrorEnvelope()`

Converts parsing, authorization, and internal failures into a standard error envelope and links the error to the original message through `replyTo`.

## Connection Registry and Rooms

### Location

`src/realTime/clientsRegistry.js` and `src/realTime/roomManager.js`

### `ClientsRegistry.addConnection()`

Stores an active connection so Presence and broadcast services can resolve its user and transport state.

### `ClientsRegistry.removeConnectionById()`

Removes a closed connection and prevents stale connections from remaining visible to the runtime.

### `RoomManager.registerPermanentRoom()`

Registers the public Lobby Room and prevents normal automatic cleanup from removing it.

### `RoomManager.join()` and `RoomManager.leave()`

Update Room membership, capacity, and online counters.

## Game Services and Lobby

### `createGameServices()`

Creates Presence and Room State services using the shared Registry and RoomManager.

### `RoomDirectoryService.markUserLeft()`

Updates the Room online count and status after a user leaves.

### `broadcastRoomUpdatedToRealtimeRoom()`

Broadcasts Room changes to the members of that same Room.

## Dedicated Game Server

### Location

`src/gameServerControl/`

### `attachGameServerControl()`

Validates the configuration, creates the runtime, and stores it on the application or startup context.

### `createGameServerControl()`

Combines Ticket Store, Registry, Health Store, Allocator, Session Registry, and Client/Dedicated handlers into one runtime.

### `gameServerAllocator.allocateServer()`

Selects an instance with suitable capacity, health, and Room state.

### `gameSessionRegistry`

Maintains the relationship between the user, Room, Dedicated Server, and active session.

## Tickets and Warm Pool

### `clientGameServerHandler`

Normalizes and rate-limits the client request, selects a server, and issues a connection ticket.

### `gameTicketService.create()`

Creates a short-lived ticket containing `ticketId`, `userId`, `roomId`, `serverId`, and `expiresAt`, then signs it with the internal service secret.

### `gameTicketService.validate()`

Validates expiration, the stored ticket record, the signature, and the user, Room, and server association.

### `gameTicketService.consume()`

Consumes the ticket so the same authorization cannot be replayed.

### Warm Pool

Keeps ready Dedicated instances available before a client request so server allocation and entry latency are reduced.

## Health, JWKS, and Security

### `publicHealthWrapper`

Collects User, Room, Connection, Session, Game Server, CPU, and memory metrics and exposes JSON or HTML health output.

### JWKS Handler

Provides the public key required by consumers to validate JWT signatures.

### TLS Configuration

Loads the TLS certificate and key for secure gateway and endpoint operation. WebGL enters gRPC-Web through Envoy and HTTPS while the internal service contract remains gRPC.

## Realtime Message Flow

```text
WebSocket or gRPC Stream
  -> Transport
  -> RealtimeServer
  -> Flood Protection
  -> parseEnvelope()
  -> Ack Tracker
  -> realtimeRouter()
  -> Room / Presence / Game Service
  -> serializeEnvelope()
  -> Transport Response
```

## Login to Dedicated Flow

```text
Register/Login
  -> Auth Handler
  -> AuthService
  -> UserRepository
  -> MongoDB
  -> TokenService
  -> Access/Refresh Token

Room Join
  -> GameServerControl Handler
  -> GameServerAllocator
  -> Warm Pool or Dedicated Registry
  -> GameTicketService
  -> Dedicated Ticket Validation
  -> Game Session
```

## Summary

The server starts from one central entry point, while responsibilities remain separated across Configuration, Authentication, Persistence, Transport, Realtime, Room, and Dedicated Server layers. API and Realtime logic are shared; platform differences remain inside the transport and gateway boundaries.

For the complete chapter-by-chapter technical explanation, see the [server technical PDF](Metaverse_Server_Technical_Documentation.pdf).
