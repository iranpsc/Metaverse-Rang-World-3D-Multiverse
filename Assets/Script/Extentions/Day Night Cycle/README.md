# Meta_DayNightCycle - Module Documentation

## Run JSON server (local)
The module ships its own server bundle for QA — no repo-root paths needed:

```bash
cd "Assets/Script/Extentions/Day Night Cycle/Server"
npm install
npm start
# http://127.0.0.1:8788/health
```

Unity `BaseUrl` = `http://127.0.0.1:8788`. Wiring: `SetEnvironmentId(place)` +
`IsEnvironmentOwner` from the API gate (owner = `LocalAuthoritySimulate`,
visitor = `ApplyOnly`); only the owner runs `SaveEnvironmentState`, visitors
only receive `ApplyState`. Module-specific curl examples (owner header
`X-Is-Environment-Owner: true`, strict `403` case): see `Server/README.md`.
Optional one-click Editor check: `Tools → Meta → Run Full Module E2E Test`
(shared launcher lives on the minimap branch — `Meta_Modules_RuntimeTest`).

## Overview
Day/night cycle system with sun rotation, intensity interpolation, skybox blending, and ambient lighting control. Fully resettable to initial state.

A `Directional Light` renders **nothing** in the sky, so the module also drives a **visible sun disc**
(`Meta_Sun_Disc`) that is parked opposite the light direction and tinted by the same elevation curve.

## Quick Start
| | |
|---|---|
| **Purpose** | Rotating sun + real shadows + a visible sun disc, persisted per place |
| **Setup menu** | `Tools → Meta → DayNight → Setup` (Alt + click = force recreate) |
| **How to ON/OFF** | HUD card `DAY / NIGHT` → `ON` / `OFF` / `Reset`, or code `ToggleCycle(bool)` |

### Owner vs visitor setup
The **owner** drives the environment and is the only one that persists it. Ownership is SET FROM
OUTSIDE (from the API / lobby bootstrap) — the module never invents it:
```csharp
// OWNER (host / lobby rig) — after entering the room
var cycle = FindFirstObjectByType<Meta_DayNightCycle>();   // or a serialized reference

cycle.SetEnvironmentId(roomId);              // the PLACE -> {base}/environments/{room}/daynight.json
cycle.IsEnvironmentOwner = ownerFromApi;     // true = simulate + persist, false = apply only
cycle.TryLoadEnvironmentState();             // load the place's stored time (+ ApplyState for everyone)
```
`SetEnvironmentId` flushes the outgoing place first (owner only) and immediately loads the new one,
so the explicit `TryLoadEnvironmentState()` is belt-and-braces. Visitors keep everything as
`ApplyOnly` and get the same load for free:
```csharp
// VISITOR (joining client)
cycle.SetEnvironmentId(roomId);              // -> LoadEnvironmentState applies the stored snapshot
cycle.IsEnvironmentOwner = false;
cycle.ApplyState(hostSnapshot);              // then the host's live snapshot overwrites the stored one
```
Visitors may **read** the place and **apply** state, but every write action (`ToggleCycle`,
`ForceResetToDefault`, `RecacheInitialValues`) is refused with one warning, and `SaveEnvironmentState()`
returns early - a visitor can never overwrite the place's time of day.

### How to test "the place remembers its time"
1. Edit mode: run `Tools → Meta → DayNight → Setup` (Alt to rebuild). Confirm **Environment Id** = `lobby_1`
   and the HUD status line shows `owner: simulate`.
2. Play. Press `ON`. Let the sun move for ~10 s (or watch the `Time: 0.xxx` row).
3. Press `OFF` or just **stop Play** - either path saves (`ToggleCycle` saves immediately,
   `OnDisable`/`OnDestroy` flush).
4. **Play again.**
5. Expected: the log `[Meta_DayNightCycle] Restored 'lobby_1' from store -> v1 t=0.xxx ...` and the sun
   starts back at the stored time of day instead of midnight.
6. To simulate a **visitor** without a second machine: in the Inspector set **Authority Mode** to
   `ApplyOnly`, start Play, and confirm the time still loads (read-only), the `ON` button is ignored
   with the "not the environment owner" warning, and the status line shows `visitor: apply-only`.
7. Different place, separate entry: change **Environment Id** to `lobby_2` and reload - it starts at
   its own stored time.
8. With the **Node service** up + **Use Http Store** + **Http Base Url** = `http://127.0.0.1:8788`:
   owner saves / loads hit `tools/meta-environment-json-server/data/environments/{place}/daynight.json`
   on disk; a visitor peer (second window, `Use Http Store` on, owner flag off) loads the same file
   read-only. Turning `REQUIRE_OWNER=true` on the server keeps working - the client already sends
   `X-Is-Environment-Owner: true` on owner writes, and a visitor cannot PUT at all (client gate).

## Persistence per PLACE (part 3/6)
The state belongs to the **place**, not the citizen: the same lobby keeps its time of day for whoever
walks in next.

```csharp
public interface IEnvironmentDayNightStore {
    void Save(string environmentId, EnvironmentDayNightState state);
    bool TryLoad(string environmentId, out EnvironmentDayNightState state);
    string Describe { get; }
}
```

| Member | Default | Notes |
|--------|---------|-------|
| `_environmentId` | `"lobby_1"` | The PLACE id. Empty disables persistence. |
| `_persistEnvironment` | `true` | Master switch. |
| `EnvironmentAutosaveInterval` | `10 s` | Timer autosave while the cycle runs. |
| `Store` | shared `EnvironmentDayNightPlayerPrefsStore` | Key = `"env_daynight_" + environmentId`. |
| `Use Http Store` | `false` | On the component: route through the Node.js JSON service instead of PlayerPrefs. |
| `Http Base Url` | `""` | e.g. `http://127.0.0.1:8788` (no trailing slash). Empty = PlayerPrefs fallback. |
| `SetStore(store)` | - | Custom store injection (one line, outside this module). |

### HTTP store (Node.js JSON files)
`EnvironmentDayNightHttpStore` (same "Day Night Cycle" folder) speaks the exact contract of the
shared `tools/meta-environment-json-server` service:

```
GET  {base}/environments/{environmentId}/daynight.json
PUT  {base}/environments/{environmentId}/daynight.json    + header X-Is-Environment-Owner: true
```

It becomes active as soon as `Use Http Store` **and** `Http Base Url` are set (PlayerPrefs is used
otherwise, and a `SetStore(...)` injection always wins). Notes:
- PUTs are fire-and-forget; only the owner ever saves, and the owner header keeps the server's
  strict mode (`REQUIRE_OWNER=true`) working with no client change.
- The wire document is camelCase (`time01`, `cycleRunning`, `updatedAt`, `updatedBy`, plus the
  optional passthrough `rotationSpeed` and `baseRotation`); the store translates both directions,
  and `environmentId` is always taken from the URL on the server.
- Reads go through `TryLoadAsync` (a blocking read would stall the render thread);
  `LoadEnvironmentState()` fires the read automatically and `ApplyState`s the result on the
  main thread. Corrupt or foreign payloads are ignored (defaults kept, no crash).

Write / read rules:
- **Only the owner saves.** `SaveEnvironmentState()` returns early for `ApplyOnly` peers, so a visitor
  can never overwrite the place's time.
- **Everyone may load.** `Awake()` / `SetEnvironmentId()` call `LoadEnvironmentState()` ->
  `ApplyState(stored, snap: true)`, so a returning player drops into the time the place was left at;
  the owner's live snapshot overwrites it as soon as the first packet arrives.
- Saves fire on: cycle toggle (meaningful change), the autosave timer, `OnDisable`, `OnDestroy`,
  and `SetEnvironmentId` for the outgoing place.
- `ApplyState` re-baselines the change detector, so loading does not immediately re-save itself.
- The store rejects payloads whose `Version` is newer than this build and tolerates corrupt JSON.

**Server hook (Network_A untouched).** Implement `IEnvironmentDayNightStore` over the existing room RPC
and inject it where the lobby is bootstrapped - one line, outside this module:
```csharp
dayNight.SetStore(new ServerEnvironmentDayNightStore(rpcClient));
```
The module only ever calls `Save` / `TryLoad`, so nothing else changes.

## Authority Modes (part 2/6)
| Mode | Who | Behaviour |
|------|-----|-----------|
| `LocalAuthoritySimulate` | owner / host | `Update()` -> `Tick(dt)` advances `time01` and writes every visual. Snap rotation, zero drift. |
| `ApplyOnly` | visitor | `Update()` returns immediately - **never** simulates. Visuals come only from `ApplyState()`. |

`IsEnvironmentOwner` is a convenience alias over the same field
(`IsEnvironmentOwner = true` -> `LocalAuthoritySimulate`, `false` -> `ApplyOnly`), so write permission
and simulation role can never disagree.

**One apply path.** Both sides funnel through `ApplyEnvironment(time01, deltaTime, snap)`:
rotation -> light intensity/colour -> ambient -> disc. Owner passes `snap: true`, visitors pass `false`.

**Snap policy (documented choice).** A remote correction smaller than `RemoteSnapThresholdDeg`
(25°) is chased with an exponential slerp (`RemoteSmoothingSharpness`, 4 = settles in ~0.25 s), which
absorbs packet jitter. A larger jump **snaps** - a join, a resync or a host fast-forward must not show
the sun flying across the sky. `ApplyState(state, snap: true)` forces an instant jump.

**Budget.** No `GetComponent` and no `FindObjects` in `Update`: sun/disc/glow renderers and the focus
camera are cached. The sun rotation, light and ambient update **every frame**; only the disc + glow are
throttled to `DiscUpdateInterval` (0.08 s ≈ 12 Hz), and they update immediately when the disc appears
or disappears.

`BaseRotation` is part of the snapshot: it is the canonical sun orientation at `time01 == 0`, so every
client derives an identical rotation from the same `Time01` even if its local scene sun differs.

## Environment State Model (owner vs visitor)
The environment has a single source of truth: `_time01` (0 = midnight, 1 = next midnight). The sun
rotation is **derived** from it (`_cycleBaseRotation * AngleAxis(time01 * 360, axis)`), so any peer can
be placed at the exact same moment - a late joiner looks identical to everyone else.

```csharp
// Owner (host): read the state to broadcast
EnvironmentDayNightState s = cycle.CaptureState();     // Time01, SunAngle, CycleRunning, RotationSpeed, IsOwner, BaseRotation,
                                                       // environmentId + fresh updatedAt (via Stamp), Version

// Visitor (joiner): consume it - ALWAYS allowed, no ownership needed
cycle.ApplyState(s);                                  // sun + shadows + ambient + disc, no UI involved

// Ownership gate: these are ignored unless IsEnvironmentOwner == true
cycle.ToggleCycle(true);   cycle.ForceResetToDefault();   cycle.RecacheInitialValues();
// -> logs one warning ("not the environment owner") and returns without touching anything
```

| Member | Meaning |
|--------|---------|
| `IsEnvironmentOwner` | **default `false`**. `true` = this peer drives the environment. The Setup tool sets it to `true` for the lobby rig. |
| `Time01` / `SunAngle` | Read-only cycle position (`SunAngle = Time01 * 360`). |
| `CaptureState()` | Owner-side snapshot. |
| `ApplyState(state, applyVisual = true)` | Visitor-side apply; works with or without ownership. |
| `STATE_VERSION` / `state.Version` | Wire format version, currently `1`. Bump only on breaking changes. |
| `EnvironmentDayNightState.Default` | `v1`, midnight, frozen, 2°/s, non-owner. |

No network wire exists yet - this is the data model + the write gate only.

## Visible Sun Disc
| Field | Default | Description |
|-------|---------|-------------|
| `VisibleSun` | `Meta_Sun_Disc` | Assign your own sphere/quad, or leave empty for auto-create |
| `CreateVisibleSunIfMissing` | `true` | Creates `Meta_Sun_Disc` on `ToggleCycle(true)` |
| `SunDiscDistance` | `300` | Distance from the focus point; auto-clamped inside the camera far clip |
| `SunDiscScale` | `25` | Apparent size, rescaled with distance so on-screen size stays constant |
| `SunFocusPoint` | `Camera.main` | What the disc orbits around |
| `DiscBaseColor` | `#FFD27A` | Gold albedo - keeps the hue when emission would clip |
| `DiscEmissionIntensity` | `2.2` | HDR emission strength (clamped so it never turns white) |
| `DiscGlow` / `DiscGlowScale` / `DiscGlowAlpha` | `true` / `1.45` / `0.45` | Additive halo - the "bloom" even without post-processing |
| `DiscPulse` / `DiscPulseSpeed` | `0.02` / `0.7` | Subtle breathing so it never looks like a static ball |
| `SunDiscDayColor` / `SunDiscHorizonColor` | white → orange | Noon tint and dawn/dusk tint |
| `ShowDiscAtNight` | `false` | Keep the disc visible below the horizon (moon-like) |

- **Sphere, not a billboard quad:** a quad pops and clips when the camera rolls, a sphere self-orients.
- Material: `Universal Render Pipeline/Lit` with `_EMISSION` enabled over a gold albedo, falling back to
  `Unlit`. A user-supplied material is **never** overwritten; only Unity's `Default-*` material is replaced.
- Emission is clamped to a peak of **1.35** so the sun stays gold on pipelines without HDR/bloom
  instead of clipping to a white debug ball.
- The halo is a larger additive sphere (`_SrcBlend = SrcAlpha`, `_DstBlend = One`, `ZWrite = 0`) parented
  to the disc, so it follows for free.
- `MakeSunFriendly`: no shadow casting, no shadow receiving, no light/reflection probes,
  `allowOcclusionWhenDynamic = false` - walls must never hide the sun.
- Position each frame: `disc.position = focus - sunLight.forward * distance`, rotated to face the focus.
- OFF / Reset / OnDisable / OnDestroy → disc renderer disabled and the exact cached lighting restored.
- Only objects the module created (`Meta_Sun_Light`, `Meta_Sun_Disc`, its glow/material/texture) are ever destroyed.

### Seeing the sun
A `Directional Light` is invisible, and a sun in the sky is only visible when you **face** it.
`AimSunInFrontOfCamera(elevation = 0.38f)` aims the light so the disc lands in front of the current view
(`Camera.main`, falling back to `Camera.current`); the Setup tool calls it automatically and parks the
disc along the sun direction so the Scene view shows it before Play. If no camera is available it falls
back to Euler `(155, 0, 0)`.

## Light Colour + Atmosphere
| Field | Default | Description |
|-------|---------|-------------|
| `SunDayColor` | `#FFF4E0` | Directional light at noon |
| `SunDuskColor` | `#FFB48A` | Near the horizon (dawn / dusk) |
| `SunNightColor` | cool blue | Twilight, below the horizon |
| `AmbientDayColor` / `AmbientNightColor` | warm / soft blue | Ambient tint along the curve |
| `EnableFog` / `FogDensity` | `false` / `0.004` | Optional distance fog tinted with the time of day |
| `CreateGradientSky` | `false` | Optional `Skybox/Procedural` gradient, only when `DayNightSkybox` is empty |

Both extras are **off by default** and fully cached: `Sun.color`, fog state, the original skybox and any
material the module created are restored byte-for-byte on OFF / Reset / OnDisable / OnDestroy.
If `Skybox/Procedural` was stripped from a WebGL build, the module logs one warning and keeps the scene skybox.

## Installation
1. Place `Meta_DayNightCycle.cs` in `Assets/Script/Extentions/Day Night Cycle/`
2. Requires: Unity 2022.3+, URP 17.x

## API Reference

### Public Fields
| Field | Type | Description |
|-------|------|-------------|
| `Sun` | Light | Directional light representing sun (auto-finds RenderSettings.sun) |
| `DayNightSkybox` | Material | Custom skybox for cycle (optional) |
| `RotationSpeed` | float | Degrees per second (X-axis rotation) |
| `MaxIntensity` | float | Maximum sun intensity (day) |
| `MinIntensity` | float | Minimum sun intensity (night) |
| `IsEnabled` | bool | Toggle cycle on/off |

### Public Methods
```csharp
void ToggleCycle(bool enable)        // Start/stop cycle
void ForceResetToDefault()           // Hard reset to initial state
void RecacheInitialValues()          // Re-cache initial values at runtime
```

### Internal Behavior
- **Awake**: Caches initial sun rotation, intensity, skybox, ambient intensity
- **OnEnable**: Starts cycle if `IsEnabled == true`
- **OnDisable**: Stops cycle and resets to defaults if was enabled
- **OnDestroy**: Stops cycle and resets to defaults if was enabled
- **ResetToDefault**: Restores **exact initial values** (not MaxIntensity)

## Sun Intensity Curve
```
Dot Product (Sun.forward · Vector3.down)
  1.0 (directly overhead)  → MaxIntensity
  0.4 (horizon)            → Lerp threshold
 -0.1 (below horizon)      → MinIntensity
```

Ambient intensity follows similar curve: 0.2 (night) → 1.0 (day)

## Setup Instructions
1. `Tools → Meta → DayNight → Setup` (creates the rig, the directional sun, the visible disc and the control card)
2. Or manually: add `Meta_DayNightCycle` to a GameObject (e.g., "DayNightController")
3. Assign `Sun` (directional light) - or leave null to auto-find / create `Meta_Sun_Light`
4. (Optional) Assign `VisibleSun` - or leave null to auto-create `Meta_Sun_Disc`
5. (Optional) Assign `DayNightSkybox` material
6. Configure `RotationSpeed`, `MinIntensity`, `MaxIntensity`
7. Create UI toggle button connected to `ToggleCycle(bool)`

## Testing
Run unit tests: `Assets/Tests/DayNightCycle/Meta_DayNightCycle_UnitTests.cs`
Run integration tests: `Assets/Tests/Integration/DayNightCycle_IntegrationTests.cs`

### Test Checklist
- [ ] Toggle ON → sun rotates, intensity changes
- [ ] **Toggle ON → a bright sun disc is visible in the sky and moves with the light**
- [ ] Shadow on floor/capsule slides with the sun within 5–10 s
- [ ] Night → disc hidden / dimmed, morning → brighter
- [ ] Toggle OFF → sun rotation, intensity, ambient, skybox **fully restored**, disc hidden
- [ ] Sun = null → no NRE, only logs warning if EnableLog
- [ ] OnDisable while running → stops + resets
- [ ] OnDestroy while running → stops + resets
- [ ] Pause (timeScale=0) → cycle pauses
- [ ] Quality Level change → continues working
- [ ] Rapid toggle ON/OFF → stable, no leaks
- [ ] Custom skybox → applied on enable, restored on disable

## Critical Fixes (v2.0)
### C4: Initial Values Caching
```csharp
// Awake now caches EXACT initial values
_defaultSunIntensity = Sun.intensity;      // e.g., 1.5f (scene value)
_defaultAmbientIntensity = RenderSettings.ambientIntensity; // e.g., 1.0f
```

### C4: Correct Reset
```csharp
// ResetToDefault restores ORIGINAL values, not MaxIntensity
Sun.intensity = _defaultSunIntensity;  // 1.5f, NOT 1f (MaxIntensity)
RenderSettings.ambientIntensity = _defaultAmbientIntensity;
```

### C5: Cleanup on Disable/Destroy
```csharp
void OnDisable() { if (IsEnabled) { StopCycle(); ResetToDefault(); } }
void OnDestroy() { if (IsEnabled) { StopCycle(); ResetToDefault(); } }
```

## Common Issues

### "Sun doesn't reset to original brightness"
Ensure you're not setting `Sun.intensity` in scene after Awake. The component caches values in Awake.

### "Skybox not restoring"
Make sure `DayNightSkybox` is only assigned if you want a custom skybox. Leave null to use scene default.

### "Cycle runs in Edit Mode"
Component only runs in Play Mode (uses Coroutines).

## Performance
- One Coroutine per cycle
- Minimal allocations (structs only)
- No Update() - uses coroutine yield

## Changelog
### v2.0 (Current)
- **C4**: Cache initial ambientIntensity and Sun.intensity in Awake
- **C4**: ResetToDefault restores exact initial values
- **C5**: OnDisable/OnDestroy call StopCycle + ResetToDefault

## License
Part of Metaverse-Rang-World project.