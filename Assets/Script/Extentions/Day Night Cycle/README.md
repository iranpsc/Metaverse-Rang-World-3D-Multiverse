# Meta_DayNightCycle - Module Documentation

## Overview
Day/night cycle system with sun rotation, intensity interpolation, skybox blending, and ambient lighting control. Fully resettable to initial state.

A `Directional Light` renders **nothing** in the sky, so the module also drives a **visible sun disc**
(`Meta_Sun_Disc`) that is parked opposite the light direction and tinted by the same elevation curve.

## Quick Start
| | |
|---|---|
| **Purpose** | Rotating sun + real shadows + a visible sun disc in the sky |
| **Setup menu** | `Tools → Meta → DayNight → Setup` (Alt + click = force recreate) |
| **How to ON/OFF** | HUD card `DAY / NIGHT` → `ON` / `OFF` / `Reset`, or code `ToggleCycle(bool)` |

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