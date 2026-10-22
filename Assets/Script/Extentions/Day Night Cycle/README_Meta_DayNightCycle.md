# Meta_DayNightCycle

Day/night cycle system for Unity URP. Rotates a directional light (Sun), adjusts intensity and ambient lighting, and optionally swaps skybox material. Fully resettable to initial state.

## Requirements
- Unity 2022.3+
- URP 17.x (uses `RenderSettings`, `DynamicGI`)
- Directional Light for Sun

## Installation
Place `Meta_DayNightCycle.cs` in `Assets/Script/Extentions/Day Night Cycle/`.

## Scene Setup
1. Add `Meta_DayNightCycle` component to a GameObject (e.g., "DayNightController")
2. Assign **Sun** (Directional Light) — or leave null to auto-find from `RenderSettings.sun`
3. (Optional) Assign **DayNightSkybox** material for custom skybox during cycle
4. Configure settings:
   - `RotationSpeed`: degrees/second (X-axis rotation)
   - `MinIntensity` / `MaxIntensity`: sun intensity range
5. Create UI toggle connected to `ToggleCycle(bool)`

## Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Sun` | Light | null | Directional light (auto-finds `RenderSettings.sun`) |
| `DayNightSkybox` | Material | null | Custom skybox for cycle |
| `RotationSpeed` | float | 0.01 | Degrees per second (X-axis) |
| `MaxIntensity` | float | 1f | Day intensity |
| `MinIntensity` | float | 0f | Night intensity |
| `IsEnabled` | bool | false | Toggle cycle on/off |
| `EnableLog` | bool | false | Debug logging |

## Public API

```csharp
// Start/stop cycle
void ToggleCycle(bool enable)

// Hard reset to initial cached values
void ForceResetToDefault()

// Re-cache initial values at runtime (e.g., after scene load)
void RecacheInitialValues()

// Read-only access to cached defaults (for testing/inspection)
Quaternion DefaultSunRotation { get; }
float DefaultSunIntensity { get; }
float DefaultAmbientIntensity { get; }
Material DefaultSkybox { get; }
```

### Environment persistence API (per PLACE)
```csharp
void  SetEnvironmentId(string environmentId);   // lobby bootstrap: flush outgoing place, load new one
bool  IsEnvironmentOwner { get; set; }          // ownership flag, SET FROM OUTSIDE (API/lobby)
bool  LoadEnvironmentState();                   // load + ApplyState (one-shots per place)
bool  TryLoadEnvironmentState();                // alias, same behaviour
EnvironmentDayNightState CaptureState();        // snapshot (stamped: environmentId + updatedAt)
void  ApplyState(EnvironmentDayNightState s, bool snap = false);   // visitors: apply only
bool  SaveEnvironmentState();                   // owner-only persist (gated by SimulatesLocally)
```
Snapshot struct (`EnvironmentDayNightState`, public JsonUtility fields):
`Time01`, `SunAngle`, `CycleRunning`, `RotationSpeed`, `IsOwner`, `BaseRotation`, `Version`,
plus the Node contract fields `environmentId`, `updatedAt` (ISO-8601 UTC, stamped by
`Stamp(environmentId)`), `updatedBy` (optional).

### Node.js HTTP store
`EnvironmentDayNightHttpStore` talks to `tools/meta-environment-json-server`:

```
GET {base}/environments/{environmentId}/daynight.json
PUT {base}/environments/{environmentId}/daynight.json   + X-Is-Environment-Owner: true
```

Enable on the component: `Use Http Store = true`, `Http Base Url = http://127.0.0.1:8788`
(empty base URL = offline `EnvironmentDayNightPlayerPrefsStore`, key `env_daynight_{env}`).

## Key Behaviors

### C4: Initial Value Caching (Awake)
In `Awake`, the component caches **exact initial values**:
- `_defaultSunRotation` = Sun.transform.rotation
- `_defaultSunIntensity` = Sun.intensity
- `_defaultAmbientIntensity` = RenderSettings.ambientIntensity
- `_defaultSkybox` = RenderSettings.skybox

### C4: Correct Reset
`ResetToDefault()` restores **exact cached values** (not `MaxIntensity`):
```csharp
Sun.transform.rotation = _defaultSunRotation;
Sun.intensity = _defaultSunIntensity;  // original, not MaxIntensity
RenderSettings.ambientIntensity = _defaultAmbientIntensity;
RenderSettings.skybox = _defaultSkybox;
DynamicGI.UpdateEnvironment();
```

### C5: Cleanup on Disable/Destroy
```csharp
OnDisable: if (IsEnabled) { StopCycle(); ResetToDefault(); }
OnDestroy: if (IsEnabled) { StopCycle(); ResetToDefault(); }
```
Ensures sun/ambient/skybox don't stay in "midnight" state after scene unload or GameObject destruction.

### Sun Null Safety
- If `Sun == null` when cycle starts: logs warning (if `EnableLog`), no NRE
- `RotateSun()` checks `if (Sun != null)` each frame

### Skybox Handling
- `DayNightSkybox` applied when cycle starts
- Original skybox restored on stop/reset
- If `DayNightSkybox` is null, uses scene default

### Ambient Intensity Curve
Follows sun angle (dot = `Sun.forward · Vector3.down`):
| Dot | Sun Position | Intensity | Ambient |
|-----|--------------|-----------|---------|
| 1.0 | Overhead | MaxIntensity | 1.0 |
| 0.4 | Horizon | Lerp | Lerp |
| -0.1 | Below | MinIntensity | 0.2 |

## Testing
Run via Unity Test Runner:

### PlayMode Tests
```
Window → General → Test Runner → PlayMode → Run All
```
Tests in `Assets/Tests/PlayMode/`:
- `Meta_DayNightCycle_PlayModeTests.cs` (8 tests)
- `DayNightCycle_IntegrationTests.cs` (7 tests)

### Test Coverage
- `ToggleCycle(true)` → `false` → rotation/intensity/ambient/skybox restored (`Quaternion.Angle < 0.1f`)
- `Sun=null` → no NRE, only log
- `Destroy GO` while running → capture expected values BEFORE destroy; assert on live Light
- Rapid toggle 20x → stable, coroutine null
- `timeScale=0` → rotation pauses (deltaTime=0)
- Disable/Enable GO → clean restart
- Custom skybox → applied on enable, restored on disable

## GI Notes
- `DynamicGI.UpdateEnvironment()` called after `ResetToDefault()` to update light probes/reflection probes
- For baked lighting: ensure Sun is not marked "Baked" if using realtime cycle
- For realtime GI: works automatically with `DynamicGI.UpdateEnvironment()`

## Example: UI Toggle
```csharp
[SerializeField] private Meta_DayNightCycle _dayNight;

public void OnDayNightToggle(bool isOn)
{
    _dayNight.ToggleCycle(isOn);
}
```

## Limitations
- Requires URP (uses `RenderSettings`, `DynamicGI`)
- Skybox swap requires `DayNightSkybox` material assigned
- WebGL: test `timeScale=0` behavior (may not pause in all browsers)

## Commit
```
fix: cache and restore DayNight defaults; cleanup on disable

- C4: Cache _defaultSunRotation, _defaultSunIntensity, _defaultAmbientIntensity, _defaultSkybox in Awake
- C4: ResetToDefault restores exact cached values (not MaxIntensity)
- C5: OnDisable/OnDestroy call StopCycle + ResetToDefault
- Sun null-safe in RotateSun (log only, no NRE)
- Coroutine always stopped and nulled
- Public read-only properties for cached defaults (testing)
- EnableLog public get/set
- DynamicGI.UpdateEnvironment after reset
- Tests: PlayMode + Integration with Quaternion.Angle tolerance
```