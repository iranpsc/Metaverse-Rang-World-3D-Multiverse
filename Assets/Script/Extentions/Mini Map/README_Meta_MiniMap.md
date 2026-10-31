# Meta_MiniMap

Client-only square radar for the Metaverse place: follows the local player in world space with a
top-down orthographic camera, and persists **per-citizen** preferences (radar on/off, zoom, camera
altitude) per **place** so a returning player drops back into the exact map state they left.

- MonoBehaviour only — no `NetworkBehaviour`, no forced `NetworkIdentity`.
- Local player resolve chain (no parenting, respawn-safe):
  `A0 G7ThreeDModeController.GetLocalPlayerTransform()` → `A1 Local_Player_Cylinder` →
  `A2 child of Dedicated_Players*` → `A3 Network_A RealtimePlayerMovementSender` →
  `A4 Mirror NetworkClient.localPlayer`. `Remote_Player*` clones are never used.
- Store contract shared with `Meta_DayNightCycle`'s environment JSON (Node.js files, see below):
  `environments/{environmentId}/minimap/{citizenId}.json`

## Requirements
- Unity 2022.3+ (URP 17.x)
- `Meta_PlatformDetector` in `Assets/Script/Platform/`
- Mirror **optional** (only the A4 fallback uses it)

## Scene Setup
1. Run `Tools → Meta → MiniMap → Setup` (or add `Meta_MiniMap` to a GameObject)
2. Assign / confirm:
   - `MiniMap` panel (non-VR), `VRMiniMap` panel (VR)
   - `MiniMapCamera` (auto-found in children if empty)
   - `MiniMapRenderImage` (RawImage for the RT; empty = 25% viewport fallback)
   - Optional `PlayerDirectionImage` / `VRPlayerDirectionImage`
3. Buttons connect to `ToggleMiniMap(bool)`, `ZoomInButton()`, `ZoomOutButton()`.

**Visual contract:** `SyncPanelToState()` is the single visual sync point — `MiniMap` visible iff `IsEnabled && !VR`, `VRMiniMap` iff `IsEnabled && VR`; `ToggleMiniMap` routes through it (same frame), and `MiniMapCamera.enabled` mirrors `IsEnabled` every `LateUpdate`. The restore attempt runs before the first sync in `Start`, so play-mode starts always honour the stored (or scene-serialized) radar state — the panel never disappears on its own.

## Lobby wiring (after login)
Both ids must be known before the restore runs (`Start`). From the login / room bootstrap:

```csharp
var miniMap = FindFirstObjectByType<Meta_MiniMap>();   // or a serialized reference

miniMap.SetEnvironmentId(roomId);      // the PLACE  -> environments/{room}/minimap/{citizen}.json
miniMap.SetCitizenId(citizenId);       // the PLAYER -> saves the outgoing citizen, loads this one
```
When the player leaves the room:
```csharp
miniMap.SaveCitizenSettings();         // flush immediately (OnDisable/Quit also flush)
```
`SetCitizenId` already saves the outgoing citizen when there are unsaved changes; both calls are
idempotent. Setting the same id twice is a no-op. An **empty citizen id disables persistence with a
single warning** (never spammed, never written).

## Inspector persistence fields
| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Citizen Id` `(_citizenId)` | string | "" | Owner of the preferences. Empty disables persistence. |
| `Environment Id` `(_environmentId)` | string | "" | The PLACE. Settings are keyed per place AND per citizen. |
| `Persist Citizen Settings` | bool | true | Master switch for persistence. |
| `Use Http Citizen Store` | bool | false | Use the Node.js store instead of PlayerPrefs (needs Base Url). |
| `Http Base Url` | string | "" | e.g. `http://127.0.0.1:8788` (no trailing slash). Empty = PlayerPrefs fallback. |
| `CitizenSaveDebounceSeconds` | float | 2 | Zoom changes are coalesced and written once. |
| `Target Resolve Retry Interval` | float | 0.2 s | Re-resolve cadence while `Target == null` (the A0..A4 chain walks the scene). |

Debug: `Environment Id` / `Citizen Id` / `EnableLog` are visible in the Inspector (`Debug` header).

## Public API
```csharp
void  SetCitizenId(string citizenId);            // login bootstrap
void  SetEnvironmentId(string environmentId);    // room bootstrap (before or after SetCitizenId)
bool  SaveCitizenSettings();                     // flush now (also SaveCitizenSettingsFor(env, citizen))
bool  TryLoadCitizenSettings();                  // load + ApplySettings for the current ids
void  ApplySettings(MiniMapCitizenSettings s);   // apply a snapshot (never writes back)
MiniMapCitizenSettings CaptureSettings();        // live snapshot
IMiniMapCitizenStore CitizenStore;               // swap the store without touching the module
```
Helpers: `TryReadCitizenSettings(env, citizen, out s)`, `TryLoadCitizenSettings(citizenId)`.

## Schema
JsonUtility-safe public **fields** (camelCase = the exact HTTP contract):

```json
{ "version": 1, "citizenId": "qa_user", "radarEnabled": true,
  "zoomOrOrthoSize": 25.0, "offsetY": 50.0, "updatedAt": "2026-10-19T03:10:08.1234567Z" }
```
`IsSupportedVersion` rejects payloads from newer builds; corrupt JSON is treated as "nothing
stored" (defaults kept, no crash); `citizenId` is never trusted from the payload — the caller's id
always wins.

## Persistence rules
- **Save:** `ToggleMiniMap` (immediate; suppressed while a restore is applying → load never writes
  back), zoom changes (debounced, flushed in `LateUpdate`), `SetCitizenId` / `SetEnvironmentId`
  (outgoing id), `OnDisable`, `OnApplicationQuit`, `OnDestroy`.
- **Load:** `Start` (after the rig is wired, before the first `TryResolveTarget`), then on
  `SetCitizenId` / `SetEnvironmentId` / `OnEnable`. Once per citizen+place
  (`_citizenSettingsRestored`); changing ids bumps the in-flight request id so a stale HTTP
  response can never apply another citizen's settings.

## Stores (`IMiniMapCitizenStore`)
| Store | Behaviour |
|-------|-----------|
| `PlayerPrefsMiniMapStore` | **Default.** Offline today. Key `citizen_minimap_{env}_{citizen}` (legacy `citizen_minimap_{citizen}` still read, never written). |
| `HttpMiniMapStore` | `GET/PUT {BaseUrl}/environments/{env}/minimap/{citizen}.json`. PUT is fire-and-forget; reads go through `TryLoadAsync` (a blocking read would stall the render thread). Wired automatically once `Use Http Citizen Store` + `Http Base Url` are set — `TryLoadCitizenSettings` fires the async read and applies the response on the main thread. |

Inject your own with one line, no module changes:
```csharp
miniMap.CitizenStore = new MyNodeBackedStore();
```

## Node.js service (BaseUrl)
The shared store lives in `tools/meta-environment-json-server`:
```powershell
cd tools\meta-environment-json-server
npm install
npm start                # http://127.0.0.1:8788
```
Then on the component: `Use Http Citizen Store = true`, `Http Base Url = http://127.0.0.1:8788`.
Every save/load now hits JSON files under `data/environments/{place}/minimap/`. Leaving the base
URL empty (or switching the toggle off) reverts to PlayerPrefs with no code change.

## Play-mode verification
1. Edit mode: set `Environment Id = lobby_1`, `Citizen Id = qa_citizen_1`.
2. Play → `MINIMAP → ON`, zoom out a few times → save happens by debounce (~2 s) or on stop.
3. **Stop Play, Play again** → log
   `[MiniMap] Loaded citizen settings for qa_citizen_1 (place 'lobby_1', store ...) -> ...`;
   the radar comes up ON at the stored ortho size without touching a button.
4. Set `Citizen Id = qa_citizen_2` → that citizen starts from scene defaults (no cross-leak).
5. Set `Environment Id = lobby_2` → the same citizen, a different place, separate settings.
6. With the Node service up + http enabled → the same flow reads/writes
   `data/environments/lobby_1/minimap/qa_citizen_1.json`.

## Key behaviours
| | |
|---|---|
| **Follow** | World-space `LateUpdate` lerp; never `SetParent`s to the player; survives respawn (`Target` cleared and re-resolved every 0.5 s). |
| **Zoom** | Ortho = `orthographicSize` only (camera altitude stays `Offset.y`); perspective = follow height. **Applied immediately on every zoom call — works even before the local player spawns (`Target == null`)** — plus the smooth lerp once follow is running. All writes clamped to `MinZoom/MaxZoom`; every camera access null-checked. |
| **Clip slice** | `ClipAboveRigPlane` near-clips ceilings above the rig (ground probe throttled, clamped so floor/body are never cut). |
| **Platform** | `Meta_PlatformDetector` in `Awake`; VR shows `VRMiniMap`, others `MiniMap`; panel fade via CanvasGroup else `SetActive`. |
| **Camera flag invariant** | `MiniMapCamera.enabled` mirrors `IsEnabled` every frame — no "on but invisible" after a room leave. |

## Testing
`Assets/Tests/PlayMode/` (Test Runner → PlayMode): `Meta_MiniMap_PlayModeTests.cs`,
`MiniMap_IntegrationTests.cs` — null camera zooms, no-player safety, follow convergence, respawn
re-resolve, toggle panels, zoom clamps, `timeScale=0`, disable/re-enable state leak.

## License
Part of Metaverse-Rang-World project.
