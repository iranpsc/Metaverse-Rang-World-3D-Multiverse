# Realtime T1 Unity WebSocket Test

## Goal

This test validates the Unity WebSocket realtime path after U1 to U7.

## Flow

Connect
Auth
Ping / Pong
Join Room
Player Action
Player State
Leave Room
Disconnect

## Unity Setup

1. Add `RealtimeWebSocketT1TestController` to an empty GameObject.
2. Set `Server Url` to `ws://127.0.0.1:8080` for local server tests.
3. Set `Transport Kind` to `WebSocket`.
4. Paste a fresh access token into `Access Token Override`, or enable stored token usage if your Auth flow has already saved a token.
5. Press `RunFullWebSocketT1TestButton` from Inspector context/UI button, or enable `Run On Start`.

## Expected Result

The Unity console should show:

Connect result: True
Authenticated
system/pong result: True
join_room ack result: True
player_action ack result: True
Player state sent: True
leave_room ack result: True
T1 test completed successfully

## Notes

WebGL build still needs a JavaScript WebSocket adapter.
Editor, Windows, Android, and Quest can test this WebSocket path with the current ClientWebSocket transport.
