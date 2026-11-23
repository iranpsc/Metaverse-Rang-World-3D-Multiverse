# Meta_DayNightCycle — Script Reference

Concise reference for `Meta_DayNightCycle.cs` and its stores.
**The source of truth is [`README.md`](README.md)** — setup, ownership, tools,
verification results and troubleshooting live there.

## Requirements

- Unity `6000.2.6f2` (Unity 2022.3+ should work)
- URP (uses `RenderSettings`, `DynamicGI`, `Light`)
- One enabled directional light for the sun (the module can create `Meta_Sun_Light`)

## Scene setup

1. `Tools → Meta → DayNight → Setup` builds the rig, sun, visible disc and HUD card.
   Alt+click forces a rebuild.
2. Or add `Meta_DayNightCycle` manually, assign `Sun`, and wire `ToggleCycle(bool)`
   to a UI control.
3. Use `Tools → Meta → DayNight → Repair Lobby Scene Wiring` to repair the shipped
   Lobby scene without touching MiniMap, CameraForce, Voice or Network_A.

The scan `Assets/Script/Extentions/Day Night Cycle/**` contains no bundled unit or
PlayMode test assembly; the module is verified by the Play verifier and the server
E2E described below.

## Inspector fields

| Field | Default | Description |
|---|---|---|
| `Sun` | `null` | Enabled directional light; auto-finds `RenderSettings.sun`. |
| `DayNightSkybox` | `null` | Fixed fallback sky. |
| `ApplySkybox` | `true` | Let the module own `RenderSettings.skybox`. |
| `SkyDay` / `SkyDusk` / `SkyNight` | `null` | Assignable phase materials. |
| `DayStart01` / `DuskStart01` / `NightStart01` | `0.25` / `0.55` / `0.70` | Phase bands. |
| `SmoothSkyTransitions` | `true` | Tween `_Exposure` at real boundaries. |
| `SkyBlendSeconds` / `SkyBlendFloor` | `15` / `0.3` | Blend length / minimum exposure. |
| `RotationSpeed` | `2 °/s` | Scene pacing; 180 s per day. |
| `RotationAxis` | `(1,0,0)` | Sun orbit axis. |
| `MaxIntensity` / `MinIntensity` | `1.2` / `0` | Sun intensity range. |
| `AmbientDayIntensity` / `AmbientNightIntensity` | `1.0` / `0.2` | Ambient range. |
| `EnableShadows` | `true` | Applies soft shadows to the sun. |
| `VisibleSun` | auto | `Meta_Sun_Disc`; created on ON when missing. |
| `ShowDiscAtNight` | `false` | Keep a moon-like disc below the horizon. |
| `IsEnabled` | `false` | Serialized cycle state. |
| `AuthorityMode` | `ApplyOnly` | Owner = `LocalAuthoritySimulate`; visitor = `ApplyOnly`. |
| `EnvironmentId` | `lobby_1` | Place id; empty disables persistence. |
| `PersistEnvironment` | `true` | PlayerPrefs/HTTP persistence master switch. |
| `EnvironmentAutosaveInterval` | `10 s` | Autosave while running. |
| `UseHttpStore` / `HttpBaseUrl` | `false` / empty | Opt-in HTTP store. |
| `RemoteSnapThresholdDeg` / `RemoteSmoothingSharpness` | `25` / `4` | Snapshot smoothing. |
| `DiscUpdateInterval` | `0.08 s` | Disc/glow throttle; rotation and light run every frame. |

## Public API

```csharp
// Owner-only cycle control
void ToggleCycle(bool enable);
void ForceResetToDefault();                 // stop + restore cached lighting; Time01 unchanged
void RecacheInitialValues();

// State
EnvironmentDayNightState CaptureState();    // stamped: environmentId + updatedAt
void ApplyState(EnvironmentDayNightState state, bool snap = false);
float Time01 { get; }                       // 0 = midnight, 0.5 = noon
float SunAngle { get; }                     // Time01 * 360
bool IsEnvironmentOwner { get; set; }
EnvironmentAuthorityMode AuthorityMode { get; set; }
bool SimulatesLocally { get; }
bool HasPhaseSky { get; }
Material ResolvePhaseSky(float time01);
string DescribeSkyPhase(float time01);
string DescribeCycleState();

// Persistence
void SetEnvironmentId(string environmentId);
bool LoadEnvironmentState();                // alias: TryLoadEnvironmentState()
bool SaveEnvironmentState();                // owner-only
void SetStore(IEnvironmentDayNightStore store);

// Look / testing helpers
Quaternion DefaultSunRotation { get; }
float DefaultSunIntensity { get; }
float DefaultAmbientIntensity { get; }
Material DefaultSkybox { get; }
bool AimSunInFrontOfCamera(float elevation = 0.38f);
void Tick(float deltaTime);                 // public deterministic owner tick
```

## Behaviour that matters

### Authority
`LocalAuthoritySimulate` runs `Update() → Tick(deltaTime)` and writes every visual.
`ApplyOnly` returns immediately from `Update()` and renders only through
`ApplyState()`. The write gate (`ToggleCycle`, `ForceResetToDefault`,
`RecacheInitialValues`, `SaveEnvironmentState`) refuses visitors. The control card
never promotes a visitor.

### Persistence
Default store: `EnvironmentDayNightPlayerPrefsStore`, key
`env_daynight_<environmentId>`. The HTTP store is used only when `UseHttpStore` is on
**and** `HttpBaseUrl` is non-empty; otherwise the PlayerPrefs store is selected.

`LoadEnvironmentState()` is allowed for everyone. `SaveEnvironmentState()` is
owner-only. The HTTP read is asynchronous and guarded against a place change or a
user ON/OFF/Reset during the request.

### Restored speed policy
`ApplyState` accepts `RotationSpeed` only in **`[0.05, 6] °/s`**. Out-of-band stored
values are ignored and the scene value is kept; they are never clamped into a fake
value. Direct runtime setters remain unrestricted.

### `Time01` while OFF
`ApplyState` stores `Time01` and `BaseRotation` even when `CycleRunning` is false, so
the next ON resumes at the saved spot. The lighting itself still resets to the cached
scene look while OFF.

### Sky
Day band `[DayStart01, DuskStart01)`, dusk band `[DuskStart01, NightStart01)`, night
otherwise. Materials swap only on a band change; each shared asset gets a runtime copy
named `"<name> (Meta blend)"` so the user's material is never mutated. `_Exposure` is
tweened only when the shader exposes it. All copies are destroyed on reset.

### Visible disc
`UpdateVisibleSun` hides the disc below the horizon when `ShowDiscAtNight` is false;
when true it keeps a dimmed moon-like disc. OFF/Reset hides it. Only module-created
objects (`Meta_Sun_Light`, `Meta_Sun_Disc`, glow, materials) are destroyed.

## Persistence contract

```text
GET {base}/environments/{environmentId}/daynight.json
PUT {base}/environments/{environmentId}/daynight.json
    X-Is-Environment-Owner: true
```

Wire fields are camelCase: `version`, `environmentId`, `time01`, `cycleRunning`,
`rotationSpeed`, `baseRotation`, `updatedAt`, `updatedBy`. The server ignores a body
`environmentId` and uses the URL. Strict mode (`REQUIRE_OWNER=true`) returns `403`
without the owner header.

Bundled server:

```bash
cd "Assets/Script/Extentions/Day Night Cycle/Server"
npm install
npm start                 # http://127.0.0.1:8788
npm run test:e2e          # 10/10 PASS
```

Plain HTTP is blocked when Player Settings has `insecureHttpOption: 0`; the store
catches that, logs one warning, and PlayerPrefs remains effective. Use HTTPS or allow
HTTP downloads.

## Verification

- Batch compile of the project: **0 `error CS`** (final clean batch compile).
- Play verification: **28 PASS / 0 FAIL** in `Logs/daynight_play_result.txt`
  (ON advances time/sun/disc, day intensity 1.20, dusk exposure dip/rise, OFF restores
  sun/sky, status truthful, PlayerPrefs round-trip).
- Server E2E: **10/10 PASS**.

Menu entries:

| Menu | Purpose |
|---|---|
| `Tools → Meta → DayNight → Setup` | Build/repair the rig and HUD. |
| `Tools → Meta → DayNight → Repair Lobby Scene Wiring` | Non-destructive Lobby repair. |
| `Tools → Meta → DayNight → Clear Stored Day-Night For Scene Places` | Delete PlayerPrefs place keys. |
| `Tools → Meta → DayNight → Play Verify ON-OFF + Sky` | Full Play-mode evidence run. |

## Known limits

- No live network transport is bundled; feed `CaptureState()`/`ApplyState()` through
  the room RPC outside this module.
- The HTTP server has no day/night DELETE route; clear HTTP state on disk.
- HUD `Reset` does not clear the persisted key or rewind `Time01`; use the
  maintenance menu.
- Sky fading requires a shader with an `_Exposure` property; otherwise the material
  swap is instant.
