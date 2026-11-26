# Meta_MiniMap - Module Documentation

## Run JSON server (local)
The module ships its own server bundle for QA — no repo-root paths needed:

```bash
cd "Assets/Script/Extentions/Mini Map/Server"
npm install
npm start
# http://127.0.0.1:8788/health
```

Unity `BaseUrl` = `http://127.0.0.1:8788` (the module-local bundle defaults to that port). Wiring:
`SetEnvironmentId(place)` + `SetCitizenId(player)` from the lobby bootstrap (never swapped), play,
the `Save` fires on leave and on explicit toggles via the debounced store.
Module-specific curl examples: see `Server/README.md` (minimap endpoints only).
Optional one-click Editor check: `Tools → Meta → Run Full Module E2E Test`
(shared launcher is part of this branch under `Meta_Modules_RuntimeTest`).

## First-run default (IMPORTANT)
A fresh citizen / wiped store always boots **radar OFF and HUD hidden**, even when the scene was
saved with `IsEnabled = 1`. The default-OFF decision is applied **before** any restore, including
while an asynchronous HTTP restore is still in flight, so a stale scene value can never flash the
map on. Only a citizen document with `"radarEnabled": true` turns it back on. This is enforced in
`Meta_MiniMap.Start`, `OnEnable`, `SetCitizenId`, `SetEnvironmentId` and `Setup` repair.

## Quick Start (citizen preferences)
| | |
|---|---|
| **Purpose** | Client-only square radar (top-left) + per-citizen preferences that survive a re-enter |
| **Setup menu** | `Tools → Meta → MiniMap → Setup` (Alt + click = force recreate) |
| **How to ON/OFF** | HUD card `MINIMAP` → `ON` / `OFF` / `Zoom +` / `Zoom −`, or `ToggleMiniMap(bool)` |
| **Set the citizen** | `SetCitizenId(id)` before Play, or fill `Citizen Id` in the Inspector |

### Wiring it from the lobby after login
Both ids must be known **before the rig starts ticking**, because the restore runs in `Start`.
Call this from the login / room bootstrap (outside this module), in this order:

```csharp
// after the citizen is authenticated, before/while the lobby loads
var miniMap = FindFirstObjectByType<Meta_MiniMap>();   // or keep a serialized reference

miniMap.SetEnvironmentId(roomId);      // "the PLACE"  -> environments/{env}/minimap/{citizen}.json
miniMap.SetCitizenId(citizenId);       // "the PLAYER" -> saves the outgoing one, loads this one

// leave / log out: flush whatever is still pending
miniMap.SaveCitizenSettings();
```
`SetCitizenId` already saves the outgoing citizen when there are unsaved changes, so the explicit
`SaveCitizenSettings()` on leave is belt-and-braces. Both calls are safe to make again at any time —
setting the same id twice is a no-op, and setting an empty id only disables persistence.

### Storage contract
| Piece | Value |
|-------|-------|
| Path shape | `environments/{environmentId}/minimap/{citizenId}.json` |
| Offline key | `citizen_minimap_{environmentId}_{citizenId}` (legacy: `citizen_minimap_{citizenId}`, still read) |
| Payload | `{ version, citizenId, radarEnabled, zoomOrOrthoSize, offsetY, updatedAt }` |

```json
{ "version": 1, "citizenId": "qa_user", "radarEnabled": true,
  "zoomOrOrthoSize": 25.0, "offsetY": 50.0, "updatedAt": "2026-10-18T03:10:08.1234567Z" }
```

### Stores (`IMiniMapCitizenStore`)
| Store | When it is used |
|-------|-----------------|
| `PlayerPrefsMiniMapStore` | **Default**, works offline today, same JSON schema. |
| `HttpMiniMapStore` | Set `Use Http Citizen Store` + `Http Base Url` on the component. Stub: `PUT` is fire-and-forget, reads go through `TryLoadAsync` (a synchronous read would stall the render thread). |

Pluggable by design — inject your own without touching `Meta_MiniMap`:
```csharp
miniMap.CitizenStore = new MyNodeBackedStore();     // GET / PUT the JSON above
```

### Node service (HTTP BaseUrl)
The module ships its own bundle in `Assets/Script/Extentions/Mini Map/Server`
(`npm install && npm start`, default `http://127.0.0.1:8788`). Enable it on the component:

| Field | Value |
|-------|-------|
| `Use Http Citizen Store` | `true` |
| `Http Base Url` | `http://127.0.0.1:8788` (no trailing slash) |

It can also be configured from code before/after the rig starts:
```csharp
miniMap.UseHttpCitizenStore = true;
miniMap.HttpBaseUrl = "http://127.0.0.1:8788";
miniMap.PersistCitizenSettings = true;
```

With that set, every save / load flow hits the JSON tree on disk
(`Server/data/environments/{place}/minimap/{citizen}.json`). `TryLoadCitizenSettings()` fires the
async read automatically and applies the response on the main thread; a stale response after an id
change is dropped, and an explicit ON/OFF pressed while the read is in flight wins over the
stored snapshot (the late response is discarded and the local choice is saved). A 404 means
"no document yet" (normal first run, no warning); an unparseable body is treated as a miss, so it
keeps DEFAULT OFF instead of pretending the citizen chose OFF.
Leaving the fields defaults to the offline `PlayerPrefsMiniMapStore` — no code change either way.

### API (stable names)
```csharp
void SetCitizenId(string citizenId);
void SetEnvironmentId(string environmentId);
void SaveCitizenSettings();                  // flush now
bool TryLoadCitizenSettings();               // load + ApplySettings for the current ids
void ApplySettings(MiniMapCitizenSettings s);
MiniMapCitizenSettings CaptureSettings();
```
Helpers: `CitizenStore`, `SaveCitizenSettingsFor(env, citizen)`, `TryReadCitizenSettings(env, citizen, out s)`,
`TryReadCitizenSettings(citizen, out s)`, `CitizenId`, `EnvironmentId`.

## Citizen Settings (per player)
Radar preferences belong to the **citizen**, not the shared environment (that is
`EnvironmentDayNightState` in the DayNight module).

```csharp
// set once, by the login / citizen bootstrap
minimap.SetCitizenId(citizenId);                       // "" disables persistence

MiniMapCitizenSettings s = minimap.CaptureSettings();  // radarEnabled / zoomOrOrthoSize / offsetY / version
bool ok = minimap.SaveCitizenSettings(citizenId);      // PlayerPrefs key "citizen_minimap_" + citizenId
```

| Member | Notes |
|--------|-------|
| `MiniMapCitizenSettings` | `[Serializable]` struct matching the JSON contract: `version`, `citizenId`, `radarEnabled`, `zoomOrOrthoSize`, `offsetY`, `updatedAt`. |
| `CaptureSettings()` | Reads live state: `radarEnabled = IsEnabled`, `zoomOrOrthoSize` = ortho size (ortho camera) or follow height (perspective), `offsetY = Offset.y`. |
| `ApplySettings(s)` | Applies a snapshot (clamps zoom, restores `Offset.y` only when `offsetY > 0`, re-toggles the radar). Called for you by `TryLoadCitizenSettings`. |
| `SaveCitizenSettings()` | Writes + flushes through the active store. Returns `false` when persistence is off or the id is empty. |
| `TryLoadCitizenSettings()` | Reads **and applies** the current citizen's settings. Returns `false` when the id is empty or nothing valid is stored. |
| `CitizenSaveDebounceSeconds` | `2 s`. Zoom changes mark the settings dirty and are written once instead of every held-button frame. |
| `TargetResolveRetryInterval` | `0.2 s`. Re-resolve cadence while `Target` is null, so the A0..A4 chain (which walks the scene) is not run every frame. First attempt, `Start` and `ToggleMiniMap` are never throttled. |

**When it saves:** `ToggleMiniMap` (immediately — explicit user action, and skipped while a restore is
being applied so a load never writes back what it just read), zoom changes (debounced, flushed from
`LateUpdate`), `SetCitizenId` / `SetEnvironmentId` (outgoing id), `OnDisable`, `OnApplicationQuit`,
`OnDestroy`.

**Empty citizen id:** nothing is written and a single warning is logged, no matter how many times a
save is attempted.

**Restoring on enter (5/6).** The restore runs in `Start` **after** the rig is wired (camera found,
render output set up, zoom range initialised) and **before** the first `TryResolveTarget()`:

```
Awake -> Start: camera + RT + zoom -> RestoreCitizenSettings() -> TryResolveTarget()
```

`SetCitizenId(id)` from the login bootstrap triggers the restore immediately when the rig is already
wired, or at the next `OnEnable` if it was not. It **saves the outgoing citizen first**, so one player's
zoom can never land under another player's key.

Restoring **does not touch Target resolution**: the radar state is applied through the normal
`ToggleMiniMap` path, which enables the camera and shows the HUD; if the player has not spawned yet,
`ToggleMiniMap(true)` leaves the camera enabled and `LateUpdate` keeps resolving G7 -> `Local_Player_Cylinder`
-> ... exactly as before, and the map starts following the moment the target appears.

The restore happens **once per citizen** (`_citizenSettingsRestored`), so re-enabling the component
during a session never reverts the player's in-session choices.

**When it saves:** `ToggleMiniMap` (immediately - explicit user action), zoom changes (debounced, flushed
from `LateUpdate`), `OnDisable`, `OnApplicationQuit`, `OnDestroy`, and `SetCitizenId` (for the outgoing
citizen).

### Wiring it (before Play)
The id must be known **before the component starts**, because the restore runs in `Start`:
```csharp
// from the login / citizen bootstrap, BEFORE the rig starts ticking:
minimap.SetCitizenId(citizenId);      // "" = persistence disabled, nothing is written
```
For a quick manual test just type an id into **Citizen Id** on the `Meta_MiniMap` component
(or leave it and use the Inspector field). Changing the id at runtime works too: the outgoing citizen
is saved first, then the new one's preferences are applied immediately.

### How to test "same settings after re-enter"
1. Edit mode: set **Environment Id** = `lobby_1` and **Citizen Id** = `qa_citizen_1`.
2. Play. `MINIMAP → ON`. Press `Zoom −` a few times (watch the map shrink).
3. Wait ~2 s (debounce) or stop Play — either way it is written to
   `citizen_minimap_lobby_1_qa_citizen_1`.
4. **Stop Play, then Play again.**
5. Expected: `[MiniMap] Loaded citizen settings for qa_citizen_1 (place 'lobby_1', store PlayerPrefs) -> ...`
   and the radar is already ON at the same ortho size, without touching a button.
6. Change the citizen id to `qa_citizen_2` and repeat → that citizen starts **OFF/hidden** (each
   citizen has its own key, and each place has its own key).
7. Change **Environment Id** to `lobby_2` → same citizen, different place, starts OFF/hidden until
   that place has its own saved document.
8. With **Use Http Citizen Store** + the module Node service up: the log reads `store Http` and the
   same flow writes/reads `Assets/Script/Extentions/Mini Map/Server/data/environments/lobby_1/minimap/qa_citizen_1.json`.

## Overview
Minimap system for multiplayer games using Mirror networking. Follows local player with zoom, rotation, and platform-specific UI support.

## Installation
1. Place `Meta_MiniMap.cs` in `Assets/Script/Extentions/Mini Map/`
2. Ensure `Meta_PlatformDetector.cs` is in `Assets/Script/Platform/`
3. Requires: Unity 2022.3+, URP 17.x, Mirror Networking

## API Reference

### Public Fields
| Field | Type | Description |
|-------|------|-------------|
| `Target` | Transform | Target to follow (auto-assigned to local player) |
| `MiniMap` | GameObject | UI panel for desktop/minimap |
| `VRMiniMap` | GameObject | UI panel for VR |
| `MiniMapCamera` | Camera | Minimap camera reference |
| `PlayerDirectionImage` | Image | Direction indicator (desktop) |
| `VRPlayerDirectionImage` | Image | Direction indicator (VR) |
| `Offset` | Vector3 | Camera offset from target |
| `FollowSpeed` | float | Lerp speed for position following |
| `RotateWithPlayer` | bool | Whether minimap rotates with player |
| `IsEnabled` | bool | Toggle minimap visibility (first run defaults to OFF) |
| `MinZoom` | float | Minimum zoom level |
| `MaxZoom` | float | Maximum zoom level |
| `PersistCitizenSettings` | bool | Load/save this citizen's preferences |
| `UseHttpCitizenStore` | bool | Use the HTTP store instead of PlayerPrefs |
| `HttpBaseUrl` | string | Settings service root, e.g. `http://127.0.0.1:8788` |

### Public Methods
```csharp
void ToggleMiniMap(bool enable)    // Enable/disable minimap UI
void ZoomInButton()                 // Zoom in by fixed step
void ZoomOutButton()                // Zoom out by fixed step
void SetZoomValue(float value)      // Set zoom directly in world units (clamped to Min/Max)
void ZoomIn(float deltaTime)        // Continuous zoom in (scroll wheel)
void ZoomOut(float deltaTime)       // Continuous zoom out (scroll wheel)
```

### Events
None - uses direct method calls for UI integration.

## Platform Support
| Platform | Minimap Panel | VR Panel |
|----------|---------------|----------|
| Windows | ✅ | ❌ |
| Android | ✅ | ❌ |
| iOS | ✅ | ❌ |
| WebGL (Windows) | ✅ | ❌ |
| WebGL (Android) | ✅ | ❌ |
| VR | ❌ | ✅ |

## Setup Instructions
1. Add `Meta_MiniMap` component to a GameObject in scene
2. Assign `MiniMapCamera` (child camera with orthographic projection)
3. Assign `MiniMap` panel UI GameObject
4. (Optional) Assign `VRMiniMap` for VR builds
5. (Optional) Assign direction indicator images
6. Create UI buttons and connect to:
   - `ToggleMiniMap(bool)` for on/off toggle
   - `ZoomInButton()` for zoom in
   - `ZoomOutButton()` for zoom out

## Network Behavior
- Resolves the local player through A0 G7 controller → A1 `Local_Player*` → A2 `Dedicated_Players*`
  → A3 Network_A sender → A4 `NetworkClient.localPlayer` (only when Mirror is active)
- Re-assigns target on respawn (detects localPlayer change)
- Follows in world space; never parents the rig to the player
- No infinite Invoke loops - uses timed check in LateUpdate
- Survives player destroy/spawn cycles
- `MonoBehaviour` only: no `NetworkBehaviour`, no required `NetworkIdentity`

## Testing
This module currently has **no committed Unity Test Framework suites** under `Assets/Tests/**`.
Use the built-in Editor tooling instead:

1. `Tools → Meta → MiniMap → Setup` repairs the rig and writes `IsEnabled=false` when nothing is stored.
2. `Tools → Meta → MiniMap → Show Control UI` opens the Play-mode control card.
3. `Tools → Meta → MiniMap → Diagnose Play` enters Play, samples the rig, forces ON → OFF → ON, writes
   `%TEMP%/MetaMiniMapDiagnoseReport.txt`, and exits Play automatically.
4. `Assets/Script/Extentions/Mini Map/Server/e2e-smoke.ps1` (`npm run test:e2e`) checks the HTTP contract.

### Manual Play checklist
- [ ] Fresh citizen/store → radar hidden, control card reads OFF
- [ ] Stored `radarEnabled=true` → radar visible after restore
- [ ] Spawn player → minimap assigns target and follows in world space
- [ ] Respawn → minimap reconnects to the new player
- [ ] Toggle OFF → panel disappears immediately (no fade-out ghost frame)
- [ ] Zoom +/- with no local player → `orthographicSize` changes immediately
- [ ] No localPlayer → no crash, no infinite invoke
- [ ] Pause/Resume → handles timeScale=0 gracefully
- [ ] Disable/enable GameObject → no state leak

## Known Limitations
- Mirror is only the A4 fallback; other resolve paths work without an active NetworkClient
- VR support requires XR plugin setup
- WebGL platform detection uses user-agent heuristics
- HTTP reads are async: the radar stays OFF until the response arrives (by design)

## Changelog
### v2.0 (Current)
- **C1**: Removed `transform.SetParent(Target)` - follows in world space
- **C2**: Null-checks on all camera accesses, clamp zoom values
- **C3**: Respawn handling via LateUpdate check, removed infinite Invoke
- **M1**: Platform from `Meta_PlatformDetector.GetPlatformType()`
- **M2**: MiniMap UI enabled for all non-VR platforms
- **M3**: Zoom buttons use fixed step (no deltaTime)
- **M4**: MinZoom/MaxZoom preserved, runtime copies used
- **M5**: Direction image logic clarified

## License
Part of Metaverse-Rang-World project.