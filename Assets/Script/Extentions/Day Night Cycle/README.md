# Meta_DayNightCycle — Module Documentation

Day/night cycle for Unity URP: a directional **sun rotates for real**, its intensity,
colour, shadow strength and ambient light follow the sun elevation, a **visible sun disc**
tracks the light, and the skybox blends through **Day / Dusk / Night** materials.

State belongs to the **PLACE** (`environmentId`), is driven by one owner, and is restored
on the next visit. The module ships its own JSON server and its own verification tools.

> Unity `6000.2.6f2` / URP. Files live in `Assets/Script/Extentions/Day Night Cycle/`.

## Quick start

| | |
|---|---|
| Build / repair controls | `Tools → Meta → DayNight → Setup` (Alt+click = force rebuild) |
| Repair the shipped Lobby | `Tools → Meta → DayNight → Repair Lobby Scene Wiring` |
| Clear stored place time | `Tools → Meta → DayNight → Clear Stored Day-Night For Scene Places` |
| Play verification | `Tools → Meta → DayNight → Play Verify ON-OFF + Sky` |
| Controls | HUD card `DAY / NIGHT` → `ON` / `OFF` / `Reset` |
| Code | `cycle.ToggleCycle(true / false)` |

Press **ON**: `Time01` advances, the sun rotates at the scene-authored `RotationSpeed`
(the Lobby rig uses `2 °/s` → 180 s per full day), shadows move, the sky transitions,
and the disc follows. Press **OFF**: the cycle stops and the cached scene lighting,
skybox, ambient and fog are restored.

## Authority model (owner vs visitor)

`Authority` is a single serialized enum, so write permission and simulation role can
never disagree:

| Mode | Who | Behaviour |
|---|---|---|
| `LocalAuthoritySimulate` | owner / host | `Update()` calls `Tick(dt)`, advances `Time01`, writes every visual, may save. |
| `ApplyOnly` | visitor | Never advances the clock. Visuals come only from `ApplyState()` snapshots. |

The **code default is `ApplyOnly`**. `Setup` creates a new Lobby rig as owner; repairing
an existing rig keeps its authored authority, so a visitor scene is never silently
promoted. The HUD card is strict: on a visitor, `ON` / `OFF` / `Reset` log one message
and do nothing. `ToggleCycle`, `ForceResetToDefault`, `RecacheInitialValues` and
`SaveEnvironmentState` all no-op for a visitor.

```csharp
// Owner (host / lobby bootstrap)
var cycle = FindFirstObjectByType<Meta_DayNightCycle>();
cycle.AuthorityMode = EnvironmentAuthorityMode.LocalAuthoritySimulate;
cycle.SetEnvironmentId(roomId);          // flush old place (owner only), then load new place
cycle.ToggleCycle(true);

// Visitor (joining client)
cycle.AuthorityMode = EnvironmentAuthorityMode.ApplyOnly;   // required
cycle.SetEnvironmentId(roomId);          // loads the place snapshot; ApplyState renders it
cycle.ApplyState(hostSnapshot, snap: true);
```

`Time01` is the single source of truth (`0` = midnight, `0.5` = noon, `1` = next
midnight). The sun rotation is derived as
`_cycleBaseRotation * AngleAxis(Time01 * 360, RotationAxis)`, so every peer placed at
the same `Time01` shows the same sun even if its local scene light differs.

**Remote snap policy:** corrections smaller than `RemoteSnapThresholdDeg` (25°) are
chased with a short slerp (`RemoteSmoothingSharpness`, 4 ≈ 0.25 s). Larger jumps snap,
so a joiner never watches the sun fly across the sky. `ApplyState(state, snap: true)`
forces an instant jump.

## State restoration and the speed band

`ApplyState` restores `Time01` and `BaseRotation` **even when the stored
`CycleRunning` is false**, so the next ON resumes at the saved spot instead of jumping
back to the serialized value. If the stored state is OFF, the scene lighting is also
restored to its cached look.

A speed arriving through `ApplyState` (store load or owner snapshot) is accepted only
inside **`0.05 … 6 °/s`**. Values outside the band are **ignored**, and the component
keeps its scene/prefab value; they are never clamped into a fake value. This is what
stops a stale `12` or `200 °/s` demo speed from hijacking scene pacing. Direct runtime
setters (Inspector, slider) are not restricted by the band.

The Lobby scene ships `RotationSpeed: 2` (180 s day). A restored `2` is accepted; a
stale `12` or `200` is rejected and `2` stays in force.

## Sky phases and smooth transitions

| Field | Default | Meaning |
|---|---|---|
| `ApplySkybox` | `true` | Master switch for sky ownership. |
| `DayNightSkybox` | `null` | Fixed fallback used when a phase slot is empty. |
| `SkyDay` / `SkyDusk` / `SkyNight` | `null` | Assignable phase materials. The Lobby scene assigns `Sky_Anime_01_Day_a`, `Sky_Anime_01_dusk_b`, `Sky_Anime_01_Night_c`. |
| `DayStart01` / `DuskStart01` / `NightStart01` | `0.25` / `0.55` / `0.70` | Clock bands: DAY `[0.25, 0.55)`, DUSK `[0.55, 0.70)`, NIGHT otherwise. |
| `SmoothSkyTransitions` | `true` | Tween `_Exposure` around a real material boundary. |
| `SkyBlendSeconds` | `15` | Target transition length; converted to clock units from the current day length. |
| `SkyBlendFloor` | `0.3` | Minimum `_Exposure` at the boundary; the sky never fades to black. |

Implementation notes:

- Thresholds are clamped in order, so unordered Inspector values still produce clean
  bands. An empty slot falls back to `DayNightSkybox`; both empty keep the current sky.
- Materials are applied only when the band changes. Each shared material is copied
  once into a runtime instance named `"<name> (Meta blend)"`; only that copy's
  `_Exposure` is tweened, so user assets are never mutated. Copies are destroyed on
  every reset path.
- The blend is a **stateless function of `Time01`**. Owner ticks and visitor network
  jumps land in the same window; there is no accumulated blend state.
- Shaders without `_Exposure` silently keep the hard material swap.
- `HasPhaseSky` is true only when `ApplySkybox` and at least one phase slot is set.
  `ResolvePhaseSky(time01)` and `DescribeSkyPhase(time01)` expose the same logic to
  UI and tools.

## Visible sun disc

A `Directional Light` is invisible, so the module drives a `Meta_Sun_Disc` sphere.

| Field | Default | Meaning |
|---|---|---|
| `VisibleSun` | auto | Assign a sphere/quad, or let the module create/find `Meta_Sun_Disc`. |
| `CreateVisibleSunIfMissing` | `true` | Create the disc on ON if it is absent. |
| `SunDiscDistance` / `SunDiscScale` | `300` / `25` | Orbit distance, auto-clamped inside the camera far clip. |
| `SunFocusPoint` | `Camera.main` | What the disc orbits. |
| `ShowDiscAtNight` | `false` | `false` hides the disc below the horizon; `true` keeps a moon-like disc visible. |
| `DiscGlow` / `DiscPulse` | `true` / `0.02` | Additive halo and subtle breathing. |

The disc is hidden when OFF (`ResetToDefault`), so a restored night scene never keeps a
sun hanging in the sky. Only objects the module created are destroyed.

## Persistence (per place)

The default store is **PlayerPrefs**, key `env_daynight_<environmentId>`. Persistence
survives Play/reload on the same machine, not a reinstall. HTTP is **opt-in**.

| Member | Default | Notes |
|---|---|---|
| `Environment Id` (`_environmentId`) | `lobby_1` | The PLACE id. Empty disables persistence. |
| `Persist Environment` | `true` | Master switch. |
| `Environment Autosave Interval` | `10 s` | Autosave while the cycle runs. |
| `Use Http Store` | `false` | Route through the bundled Node service instead of PlayerPrefs. |
| `Http Base Url` | `""` | e.g. `http://127.0.0.1:8788`. Empty = PlayerPrefs fallback. |
| `Store` / `SetStore(...)` | PlayerPrefs | Custom store injection from the lobby bootstrap. |

Rules:

- **Only the owner saves.** `SaveEnvironmentState()` returns early for `ApplyOnly`.
  The HTTP store also refuses a snapshot whose `IsOwner` is false.
- **Everyone may load.** `Awake()` and `SetEnvironmentId()` call
  `LoadEnvironmentState()`, so a visitor also sees the place's last time.
- Saves fire on toggle, `Reset`, meaningful autosave changes, `OnDisable`, `OnDestroy`,
  and `SetEnvironmentId` for the outgoing place.
- Applying a loaded state re-baselines the change detector, so loading does not
  immediately re-save itself.
- The async HTTP restore is abandoned if the user pressed ON/OFF/Reset while the GET
  was in flight, or if the place id changed. Explicit user action always wins.
- Store payloads newer than this build, corrupt JSON, and foreign `environmentId`
  values are ignored with a warning.

The module does **not** bundle a live network transport. A host broadcasts by calling
`CaptureState()` and sending the struct with whatever room RPC exists; a visitor calls
`ApplyState()`. Network_A is intentionally untouched.

## HTTP store (opt-in)

The server bundle is inside this module:

```bash
cd "Assets/Script/Extentions/Day Night Cycle/Server"
npm install
npm start
# http://127.0.0.1:8788/health
```

Enable `Use Http Store` and set `Http Base Url = http://127.0.0.1:8788`.
Contract:

```text
GET {base}/environments/{environmentId}/daynight.json
PUT {base}/environments/{environmentId}/daynight.json
    Header: X-Is-Environment-Owner: true
```

The wire document is camelCase (`version`, `environmentId`, `time01`,
`cycleRunning`, `rotationSpeed`, `baseRotation`, `updatedAt`, `updatedBy`); the store
translates to/from the PascalCase Unity struct. `environmentId` always comes from the
URL on the server.

Server options: `PORT` (default `8788`), `DATA_ROOT` (default `./data`), and
`REQUIRE_OWNER=true` to reject day/night PUTs without the owner header (`403`).
The server has **no day/night DELETE route**; see “Clearing stored state”.

**Insecure-HTTP caveat:** if Player Settings disallows HTTP downloads
(`insecureHttpOption: 0` in this project), Unity rejects the request synchronously.
The store catches that, logs one clear warning, and leaves PlayerPrefs as the
effective store. Use an `https://` base URL, or enable “Allow downloads over HTTP”
for the build. The Lobby scene therefore ships with `Use Http Store` **off**.

## Clearing stored state

- **PlayerPrefs (default):** `Tools → Meta → DayNight → Clear Stored Day-Night For
  Scene Places` deletes `env_daynight_<place>` for every place id in the open scene.
  The next Play starts from the scene values. Manually:
  `PlayerPrefs.DeleteKey("env_daynight_lobby_1"); PlayerPrefs.Save();`
- **HTTP store:** stop the service and delete
  `Server/data/environments/<placeId>/daynight.json` (or PUT a scene-authored
  snapshot). The HTTP store has no DELETE client route.
- **HUD `Reset`** stops the cycle and restores the cached scene lighting. It does not
  clear the persisted key and does not rewind `Time01`; use the menu above to forget
  the place's time.

## Tooling and verification

### `Tools → Meta → DayNight → Play Verify ON-OFF + Sky`

Editor side prepares the Lobby scene in memory (owner, `Time01 = 0`, PlayerPrefs/HTTP
off), enters Play, and the runtime probe `DayNight_PlayVerifierProbe` drives the real
`DayNight_ControlPanel` through ON → day → dusk → OFF. It checks time advance, sun
rotation, sky material and `_Exposure` blending, disc visibility, status truthfulness,
OFF restoration, and a real PlayerPrefs save/load/cleanup round-trip.

- Result: `Logs/daynight_play_result.txt`
- Batch: `Unity.exe -batchmode -nographics -projectPath <project> -executeMethod Meta.EditorTools.DayNight_PlayVerifier.Run -logFile <log>`
  **Do not add `-quit`** — the verifier exits itself after writing the result.

Current result: **28 PASS / 0 FAIL**. Evidence includes `Time01 0.0000 → 0.0142` in
2.5 s, sun rotation delta `5.11°`, day intensity `1.20`, dusk `_Exposure` dipping
`0.488 → 0.261` then rising, OFF sun delta `0.000°`, restored sky
`Sky_Anime_01_Day_a`, and PlayerPrefs round-trip restoring `Time01 = 0.3210`,
`Speed = 2.000`, `IsEnabled = false`.

### `Tools → Meta → DayNight → Setup`

Non-destructive repair when the rig exists: keeps authored values, points the cycle at
an enabled directional light, re-wires the visible disc, and fixes the HUD card refs.
Alt+click forces a full rebuild and removes the old rig, HUD and orphan disc.

### `Tools → Meta → DayNight → Repair Lobby Scene Wiring`

Opens `Assets/Scenes/Grpc/Lobby 1 WebGL/Lobby 1 WebGL.unity`, runs the same repair,
removes ghost/duplicate cycle and panel objects, saves the scene.
Batch: `-executeMethod Meta.EditorTools.DayNight_SceneRepair.RepairLobbySceneBatch`.

### Server E2E

```bash
cd "Assets/Script/Extentions/Day Night Cycle/Server"
npm run test:e2e
```

Current result: **10/10 PASS** (`A1` health, `B1`–`B7c`: minimap PUT/GET, 404,
traversal rejection, daynight owner PUT/GET, strict `REQUIRE_OWNER` 403/200).
The Unity HTTP client itself is not e2e-tested in this project because
`insecureHttpOption` currently blocks plain HTTP.

## Main API reference

```csharp
// Cycle control (owner only; visitor no-op)
void ToggleCycle(bool enable);
void ForceResetToDefault();          // stop + restore cached lighting (Time01 unchanged)
void RecacheInitialValues();

// State
EnvironmentDayNightState CaptureState();                  // owner snapshot, stamped
void ApplyState(EnvironmentDayNightState state, bool snap = false);   // everyone
float Time01 { get; }                 // 0..1
float SunAngle { get; }               // Time01 * 360
EnvironmentAuthorityMode AuthorityMode { get; set; }
bool IsEnvironmentOwner { get; set; }
bool SimulatesLocally { get; }
bool HasPhaseSky { get; }
Material ResolvePhaseSky(float time01);
string DescribeSkyPhase(float time01);
string DescribeCycleState();

// Persistence
void SetEnvironmentId(string environmentId);
bool LoadEnvironmentState();          // alias: TryLoadEnvironmentState()
bool SaveEnvironmentState();          // owner only
void SetStore(IEnvironmentDayNightStore store);
```

`EnvironmentDayNightState` fields: `Time01`, `SunAngle`, `CycleRunning`,
`RotationSpeed`, `IsOwner`, `BaseRotation`, `Version`, plus the wire fields
`environmentId`, `updatedAt`, `updatedBy`. `STATE_VERSION = 1`.

## Inspector defaults

| Field | Default |
|---|---|
| `RotationSpeed` / `RotationAxis` | `2 °/s` / `(1,0,0)` |
| `MaxIntensity` / `MinIntensity` | `1.2` / `0` |
| `AmbientDayIntensity` / `AmbientNightIntensity` | `1.0` / `0.2` |
| `DayElevationStart` / `DayElevationFull` | `-0.15` / `0.35` |
| `EnableShadows` / `ShadowType` | `true` / `Soft` |
| `DayShadowStrength` / `NightShadowStrength` | `0.9` / `0.15` |
| `EnableFog` / `FogDensity` | `false` / `0.004` |
| `CreateGradientSky` | `false` |
| `IsEnabled` | `false` |
| `AuthorityMode` | `ApplyOnly` |
| `EnvironmentId` / `PersistEnvironment` | `lobby_1` / `true` |
| `UseHttpStore` / `HttpBaseUrl` | `false` / empty |
| `RemoteSnapThresholdDeg` / `RemoteSmoothingSharpness` | `25` / `4` |
| `DiscUpdateInterval` | `0.08 s` |

## Troubleshooting

**“ON but nothing moves.”** The rig is probably `ApplyOnly` (visitor) or has no usable
enabled directional sun. The status line shows `visitor: apply-only`; the cycle fails
closed and reports OFF if it cannot resolve a sun. Run Setup/Repair and confirm
`owner: simulate`.

**“The sun light moves but the sky is static.”** Assign `SkyDay`/`SkyDusk`/`SkyNight`
and keep `ApplySkybox` on. The status line reports `slots empty` otherwise. If the
shader has no `_Exposure`, the material still swaps, just without a fade.

**“The sun disc is invisible.”** A disc is only visible above the horizon unless
`ShowDiscAtNight` is on, and it is placed relative to the focus camera. Confirm
`Disc: Meta_Sun_Disc` in the status line and run Setup with a camera present.

**“The cycle starts at the wrong time.”** That is the stored place state. Clear it
with the menu above, or change `EnvironmentId` for a new place.

**“HTTP warning about insecure connections.”** The project blocks plain HTTP. Use
HTTPS or enable HTTP downloads; PlayerPrefs remains active meanwhile.

## License

Part of the Metaverse-Rang-World project.
