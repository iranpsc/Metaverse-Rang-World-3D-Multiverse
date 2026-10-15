# Meta_MiniMap

Minimap system for multiplayer games using Mirror networking. Follows local player with zoom, rotation, and platform-specific UI support.

## Requirements
- Unity 2022.3+
- URP 17.x
- Mirror Networking (for `NetworkClient.localPlayer` auto-assignment)
- `Meta_PlatformDetector` in `Assets/Script/Platform/`

## Installation
Place `Meta_MiniMap.cs` in `Assets/Script/Extentions/Mini Map/`.

## Scene Setup
1. Add `Meta_MiniMap` component to a GameObject in your scene (e.g., "MiniMap")
2. Ensure the GameObject has a **NetworkIdentity** component (required since Meta_MiniMap is a NetworkBehaviour)
3. Create a child Camera for minimap rendering:
   - Set `Camera.orthographic = true`
   - Set `Camera.orthographicSize = 50` (adjust as needed)
   - Assign to `MiniMapCamera` field
4. Create UI panels for minimap display:
   - **MiniMap** panel (for desktop/mobile/WebGL)
   - **VRMiniMap** panel (for VR)
5. (Optional) Add direction indicator Images:
   - `PlayerDirectionImage` on MiniMap panel
   - `VRPlayerDirectionImage` on VRMiniMap panel
6. Create UI buttons and connect:
   - `ToggleMiniMap(bool)` for on/off toggle
   - `ZoomInButton()` for zoom in
   - `ZoomOutButton()` for zoom out

## Inspector Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Target` | Transform | null | Auto-assigned to local player if null |
| `MiniMap` | GameObject | null | UI panel for non-VR platforms |
| `VRMiniMap` | GameObject | null | UI panel for VR |
| `MiniMapCamera` | Camera | null | Minimap camera (auto-found in children) |
| `PlayerDirectionImage` | Image | null | Direction indicator (non-VR) |
| `VRPlayerDirectionImage` | Image | null | Direction indicator (VR) |
| `Offset` | Vector3 | (0, 100, 0) | Camera offset from target |
| `FollowSpeed` | float | 10f | Lerp speed for following |
| `RotateWithPlayer` | bool | true | Whether minimap rotates with player |
| `IsEnabled` | bool | false | Toggle minimap visibility |
| `ZoomSpeed` | float | 50f | Continuous zoom speed (scroll wheel) |
| `MinZoom` | float | 5f | Minimum zoom (orthographicSize or Offset.y) |
| `MaxZoom` | float | 100f | Maximum zoom |
| `EnableLog` | bool | false | Debug logging |

## Public API

```csharp
// Toggle minimap visibility
void ToggleMiniMap(bool enable)

// Button-based zoom (fixed step)
void ZoomInButton()
void ZoomOutButton()

// Continuous zoom (scroll wheel - pass Time.deltaTime)
void ZoomIn(float deltaTime)
void ZoomOut(float deltaTime)

// Direct zoom value (0-1 normalized or raw)
void SetZoomValue(float value)
```

## Key Behaviors

### C1: World-Space Follow (No Parenting)
Minimap follows target in **world space** via `LateUpdate`. It never parents to the player, so it survives player destroy/respawn.

### C2: Null-Safe Zoom
All zoom methods check `MiniMapCamera != null` before access. `TargetZoomValue` clamped between `_runtimeMinZoom` and `_runtimeMaxZoom`.

### C3: Respawn Handling
- Polls `NetworkClient.localPlayer` every 1 second in `LateUpdate`
- If local player changes (respawn), re-resolves `Target` and `_cameraTransform`
- No infinite `Invoke` - single retry if no player at start

### M1: Platform Detection
Platform auto-detected in `Awake` via `Meta_PlatformDetector.GetPlatformType()`.

### M2: Multi-Platform UI
| Platform | Panel Shown |
|----------|-------------|
| Windows | MiniMap |
| Android | MiniMap |
| iOS | MiniMap |
| WebGL (Windows/Android) | MiniMap |
| VR | VRMiniMap |

### M3: Button Zoom
`ZoomInButton()` / `ZoomOutButton()` use fixed step (2 for ortho, 10 for perspective). No `Time.deltaTime` dependency.

### M4: Zoom Range Preservation
`MinZoom`/`MaxZoom` from Inspector are copied to `_runtimeMinZoom`/`_runtimeMaxZoom` in `Awake`. Never modified in `Start`.

### M5: Direction Image Logic
| RotateWithPlayer | Behavior |
|------------------|----------|
| `true` | Minimap rotates with player. Direction image stays fixed (identity). |
| `false` | Minimap is north-up. Direction image rotates to show player heading. |

## Respawn Notes
- Before destroying old player: `miniMap.IsEnabled = false`
- After spawning new player: minimap auto-detects new `localPlayer` within 1 second
- No manual re-assignment needed

## Testing
Run via Unity Test Runner:

### PlayMode Tests
```
Window → General → Test Runner → PlayMode → Run All
```
Tests in `Assets/Tests/PlayMode/`:
- `Meta_MiniMap_PlayModeTests.cs`
- `MiniMap_IntegrationTests.cs`

### Test Coverage
- `MiniMapCamera=null` → Zoom buttons safe (no NRE)
- No localPlayer → No crash, no infinite Invoke
- AssignTarget + Follow → Position converges to target
- Respawn simulation → Target re-resolves after Destroy+Spawn
- ToggleMiniMap true/false → Correct panel activated
- Zoom clicks → Stays within Min/Max range
- timeScale=0 → No crash on Resume
- Disable GO → No state leak on re-enable

## Example: Button Setup
```csharp
// On your UI Manager or Button script
[SerializeField] private Meta_MiniMap _miniMap;

public void OnToggleMiniMap() => _miniMap.ToggleMiniMap(!_miniMap.IsEnabled);
public void OnZoomIn() => _miniMap.ZoomInButton();
public void OnZoomOut() => _miniMap.ZoomOutButton();
```

## Limitations
- Requires Mirror for auto-target assignment
- VR support requires XR plugin setup
- WebGL platform detection uses user-agent heuristics

## Commit
```
fix: stabilise Meta_MiniMap follow, zoom and respawn target resolve

- C1: Remove transform.SetParent - follow in world space via LateUpdate
- C2: Null-check MiniMapCamera in all zoom methods + clamp TargetZoomValue
- C3: Respawn detection via LateUpdate poll (1s interval), no infinite Invoke
- M1: Platform from Meta_PlatformDetector.GetPlatformType()
- M2: MiniMap UI enabled for Windows/Android/iOS/WebGL
- M3: Zoom buttons use fixed step (no deltaTime)
- M4: MinZoom/MaxZoom preserved - runtime copies created in Awake
- M5: UpdateDirectionImage respects RotateWithPlayer flag
- Tests: PlayMode with NetworkIdentity in SetUp
```