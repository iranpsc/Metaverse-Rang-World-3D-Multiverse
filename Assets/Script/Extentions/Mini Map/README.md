# Meta_MiniMap - Module Documentation

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
| `IsEnabled` | bool | Toggle minimap visibility |
| `MinZoom` | float | Minimum zoom level |
| `MaxZoom` | float | Maximum zoom level |

### Public Methods
```csharp
void ToggleMiniMap(bool enable)    // Enable/disable minimap UI
void ZoomInButton()                 // Zoom in by fixed step
void ZoomOutButton()                // Zoom out by fixed step
void SetZoomValue(float value)      // Set zoom directly (0-1 normalized)
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
- Automatically finds `NetworkClient.localPlayer` on Start
- Re-assigns target on respawn (detects localPlayer change)
- No infinite Invoke loops - uses timed check in LateUpdate
- Survives player destroy/spawn cycles

## Testing
Run unit tests: `Assets/Tests/MiniMap/Meta_MiniMap_UnitTests.cs`
Run integration tests: `Assets/Tests/Integration/MiniMap_IntegrationTests.cs`

### Test Checklist
- [ ] Spawn player → minimap assigns target and follows
- [ ] Respawn → minimap reconnects to new player
- [ ] Toggle ON/OFF → panels show/hide correctly
- [ ] Zoom buttons → orthographicSize stays in Min/Max range
- [ ] No localPlayer → no crash, no infinite invoke
- [ ] Pause/Resume → handles timeScale=0 gracefully
- [ ] Disable/enable GameObject → no state leak

## Known Limitations
- Requires Mirror Networking for auto-target assignment
- VR support requires XR plugin setup
- WebGL platform detection uses user-agent heuristics

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