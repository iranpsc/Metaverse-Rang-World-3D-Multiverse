# Phase 33A — Mirror-Like Gameplay API Layer Test Checklist

## Current official phase

Phase 33A.1 is code-complete for the main Mirror-Like API surface.
The next official sub-work is compile validation, hotfixes, and real multi-client testing.

## Replacement order

1. Apply all Phase33A replacement packages already provided.
2. Apply the latest hotfix package for `MetaverseNetworkStateSyncMessageCodec.cs`.
3. Open Unity and wait for a full compile.
4. Do not enable all smoke tests at once for the first run.

## First compile check

Expected result:

```text
0 compile errors
```

If Unity reports a compile error, fix only the file mentioned by Unity.
Do not change unrelated files.

## Recommended first runtime flags

Enable only these first:

```text
EnableRuntimeSpawnTestPrefab = true
EnableSpawnRouteSmokeTest = true
EnableNetworkBehaviourLifecycleSmokeTest = true
EnableNetworkRpcSmokeTest = true
```

Keep these disabled for the very first run if compile is not yet stable:

```text
EnableNetworkStateSyncSmokeTest = false
EnableNetworkPlayerObjectSmokeTest = false
EnableNetworkPlayerMovementSmokeTest = false
```

## Mirror-like API routes to verify

```text
NetworkServer.SpawnPrefab
Cmd / SendCommand
Rpc / SendClientRpc
TargetRpc / SendTargetRpc
SyncVar / SetSyncVar
SyncTransform / SendNetworkTransform
AssignClientAuthority
RemoveClientAuthority
OwnerInput / CmdMove
NetworkServer.Despawn
NetworkServer.Destroy
```

## Expected log markers

```text
phase=33A
mirrorRoute=NetworkServer.SpawnPrefab
mirrorRoute=Cmd/Command
mirrorRoute=Rpc/ClientRpc
mirrorRoute=TargetRpc
mirrorRoute=SyncVar
mirrorRoute=SyncTransform
mirrorRoute=AssignClientAuthority
mirrorRoute=RemoveClientAuthority
mirrorRoute=OwnerInput/CmdMove
```

## Multi-client validation order

1. Start Windows Dedicated Server.
2. Connect User A.
3. Connect User B.
4. Connect User C.
5. Confirm all users join the same room.
6. Confirm every user receives spawn snapshot.
7. Confirm every client owns only its own player object.
8. Send `Cmd` from owner object.
9. Confirm server receives `OnCommand`.
10. Confirm `Rpc` reaches all clients in the room.
11. Confirm `TargetRpc` reaches only the selected connection.
12. Move the local player and verify `OwnerInput -> SyncTransform`.
13. Disconnect one client and verify despawn/cleanup on the other clients.

## Build timing

Do not create a Linux build before the Windows dedicated test passes.
Windows build first prevents repeating Linux deploy/debug work for compile-time and prefab-registry issues.

## Stop condition

If any of these appears, stop and fix that file only:

```text
CS0117 missing method
CS1501 overload mismatch
CS1061 missing property or method
NullReference during installer startup
Command rejected with wrong reason
Spawn snapshot missing for late client
Disconnect does not despawn player object
```
