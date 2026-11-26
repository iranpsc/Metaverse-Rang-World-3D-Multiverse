# Meta_MiniMap

Client-only square radar for the Metaverse place: follows the local player in world space with a
top-down orthographic camera, and persists **per-citizen** preferences (radar on/off, zoom, camera
altitude) per **place** so a returning player drops back into the exact map state they left.

- MonoBehaviour only — no `NetworkBehaviour`, no forced `NetworkIdentity`.
- Local player resolve chain (no parenting, respawn-safe):
  `A0 G7ThreeDModeController.GetLocalPlayerTransform()` → `A1 Local_Player_Cylinder` (or any
  `Local_Player*` / `LocalPlayer*`) → `A2 child of Dedicated_Players*` → `A3 Network_A
  RealtimePlayerMovementSender` → `A4 Mirror NetworkClient.localPlayer`. `Remote_Player*` clones
  are never used.
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
   - Optional `MiniMapRenderTexture`; if empty, the RawImage's existing RT is reused and only a
     RenderTexture created by the component itself is released on destroy (borrowed RTs are
     detached, never destroyed)
   - Optional `PlayerDirectionImage` / `VRPlayerDirectionImage`
3. Buttons connect to `ToggleMiniMap(bool)`, `ZoomInButton()`, `ZoomOutButton()`.

**Visual contract:** `SyncPanelToState()` is the single visual sync point — `MiniMap` visible iff `IsEnabled && !VR`, `VRMiniMap` iff `IsEnabled && VR`; `ToggleMiniMap` routes through it (same frame), and `MiniMapCamera.enabled` mirrors `IsEnabled` every `LateUpdate`. **First-run default is OFF/hidden**: `Start` enforces default OFF before any restore (also while an HTTP restore is pending), then a stored `radarEnabled=true` may turn it on. A scene-serialized `IsEnabled=1` alone never shows the radar.

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
bool  PersistCitizenSettings { get; set; }       // master switch
bool  UseHttpCitizenStore  { get; set; }         // choose HTTP store at runtime
string HttpBaseUrl         { get; set; }         // e.g. http://127.0.0.1:8788
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
| `HttpMiniMapStore` | `GET/PUT {BaseUrl}/environments/{env}/minimap/{citizen}.json`. PUT is fire-and-forget; reads go through `TryLoadAsync` (a blocking read would stall the render thread). Wired automatically once `Use Http Citizen Store` + `Http Base Url` are set — `TryLoadCitizenSettings` fires the async read and applies the response on the main thread. An explicit ON/OFF pressed while that read is in flight wins: the late response is dropped and the local radar state is saved. |

Inject your own with one line, no module changes:
```csharp
miniMap.CitizenStore = new MyNodeBackedStore();
```

## Node.js service (BaseUrl)
The module ships its own bundle in `Assets/Script/Extentions/Mini Map/Server`:
```powershell
cd "Assets\Script\Extentions\Mini Map\Server"
npm install
npm start                # http://127.0.0.1:8788
```
Then on the component: `Use Http Citizen Store = true`, `Http Base Url = http://127.0.0.1:8788`.
Every save/load now hits JSON files under `Server/data/environments/{place}/minimap/`. A 404
means "no document yet" and does not warn; an unparseable body is treated as a miss (DEFAULT OFF).
Leaving the base URL empty (or switching the toggle off) reverts to PlayerPrefs with no code change.

## Play-mode verification
1. Edit mode: set `Environment Id = lobby_1`, `Citizen Id = qa_citizen_1`, clear stored settings
   (`Tools → Meta → MiniMap → Clear Stored MiniMap Citizen Settings`).
2. Play → radar is **OFF/hidden** on the first run (scene `IsEnabled` is ignored).
3. `MINIMAP → ON`, zoom out a few times → save happens by debounce (~2 s) or on stop.
4. **Stop Play, Play again** → log
   `[MiniMap] restored radar=on citizen='qa_citizen_1' (place 'lobby_1', store ...) -> ...`;
   the radar comes up ON at the stored ortho size without touching a button.
5. Set `Citizen Id = qa_citizen_2` → that citizen starts OFF/hidden (no cross-leak).
6. Set `Environment Id = lobby_2` → the same citizen, a different place, starts OFF/hidden.
7. With the Node service up + http enabled → the same flow reads/writes
   `Server/data/environments/lobby_1/minimap/qa_citizen_1.json`.

## Key behaviours
| | |
|---|---|
| **Follow** | World-space `LateUpdate` lerp; never `SetParent`s to the player; survives respawn (`Target` cleared and re-resolved every 0.5 s). |
| **Zoom** | Ortho = `orthographicSize` only (camera altitude stays `Offset.y`); perspective = follow height. **Applied immediately on every zoom call — works even before the local player spawns (`Target == null`)** — plus the smooth lerp once follow is running. All writes clamped to `MinZoom/MaxZoom`; every camera access null-checked. |
| **Clip slice** | `ClipAboveRigPlane` near-clips ceilings above the rig; near = camera altitude − head height − margin, clamped so the floor and the player body stay visible. |
| **Platform** | `Meta_PlatformDetector` in `Awake`; VR shows `VRMiniMap`, others `MiniMap`; show uses a CanvasGroup fade, hide is immediate `SetActive(false)` (no ghost frame). |
| **Camera flag invariant** | `MiniMapCamera.enabled` mirrors `IsEnabled` every frame — no "on but invisible" after a room leave. |

## Testing
No Unity Test Framework suites are committed for this module (`Assets/Tests/**` has no MiniMap
tests). Use the built-in tools:
- `Tools → Meta → MiniMap → Setup` — repairs the rig, sets first-run `IsEnabled=false`.
- `Tools → Meta → MiniMap → Diagnose Play` — Play-mode ON → OFF → ON verification, report in
  `%TEMP%/MetaMiniMapDiagnoseReport.txt`.
- `Server/e2e-smoke.ps1` (`npm run test:e2e`) — HTTP minimap/daynight contract.
- Manual: fresh store → hidden/OFF; stored ON → visible; zoom with no target; HTTP save/load.

## License
Part of Metaverse-Rang-World project.
