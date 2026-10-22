# CameraForceRenderByIndex

Forces a Camera to use a specific URP Renderer by index. Useful for rendering minimaps, portals, or special views with different renderer configurations.

## Requirements
- Unity 2022.3+
- URP 17.x (Universal Render Pipeline)
- Camera component (auto-added via RequireComponent)

## Installation
Place `CameraForceRenderByIndex.cs` in `Assets/Script/Extentions/Mini Map/` or any folder under `Assets`.

## Usage
1. Add `CameraForceRenderByIndex` component to any Camera GameObject
2. Set `RendererIndex` in Inspector (0 = first renderer in URP Asset)
3. Ensure your URP Asset has enough renderers in `rendererDataList`

### URP Configuration Example
In your **UniversalRenderPipelineAsset** (Render Pipeline Asset):
```
Renderer Data List:
  [0] ForwardRenderer (Default)
  [1] MinimapRenderer (Custom - e.g., with different LayerMask)
  [2] PortalRenderer (Custom)
```
Set `RendererIndex = 1` to use `MinimapRenderer` for this camera.

## Inspector Fields
| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `RendererIndex` | int | 0 | Zero-based index into URP Asset's `rendererDataList` |
| `EnableLog` | bool | false | Enable debug logs in Console |

## Behavior
- **Awake**: Validates and applies renderer index
- **OnValidate (Editor only)**: Clamps negative values, re-applies in Play mode
- **ValidateRendererIndex()**: Public method for manual re-validation

## Edge Cases Handled
| Scenario | Behavior |
|----------|----------|
| No URP Asset in GraphicsSettings | LogWarning, no crash |
| No UniversalAdditionalCameraData | LogWarning, uses default renderer |
| `RendererIndex < 0` | Clamped to 0 in Editor (OnValidate) |
| `RendererIndex >= rendererCount` | LogWarning, clamped to max valid index |
| Only 1 renderer in URP Asset | Only index 0 is valid; others clamp to 0 |

## Limitations
- **Single Renderer URP Asset**: If your URP Asset only has 1 renderer, all indices > 0 will clamp to 0 with a warning.
- **ReadOnlySpan**: URP 17+ uses `ReadOnlySpan<ScriptableRendererData>` for `rendererDataList`. This code reads `.Length` directly which is safe.
- **Runtime URP changes**: If you swap URP Asset at runtime, call `ValidateRendererIndex()` to re-sync.

## Testing
Run tests via Unity Test Runner:

### Edit Mode Tests
```
Window → General → Test Runner → Edit Mode → Run All
```
Tests in `Assets/Tests/EditMode/CameraForceRenderByIndex_Tests.cs`:
- Constructor defaults to RendererIndex = 0
- Valid index applies without warning
- Invalid index (99) logs warning, clamps, no crash
- RequireComponent enforces Camera

### Play Mode Tests
```
Window → General → Test Runner → Play Mode → Run All
```
Tests in `Assets/Tests/PlayMode/`:
- `CameraForceRenderByIndex_PlayModeTests.cs`: Basic valid/invalid index
- `CameraForceRender_IntegrationTests.cs`: Quality change, disable/enable, multiple cameras

## Example: Minimap Camera Setup
```csharp
// In your minimap initialization code
var minimapCamera = minimapGameObject.GetComponent<Camera>();
var forceRender = minimapCamera.GetComponent<CameraForceRenderByIndex>();
if (forceRender == null)
    forceRender = minimapCamera.gameObject.AddComponent<CameraForceRenderByIndex>();

// Use custom minimap renderer (index 1 in URP Asset)
forceRender.RendererIndex = 1;
forceRender.ValidateRendererIndex();
```

## Commit Message
```
fix: harden CameraForceRenderByIndex renderer index validation

- Add TryGetComponent for UniversalAdditionalCameraData with graceful fallback
- Validate RendererIndex against actual URP Asset rendererDataList.Length
- Clamp out-of-range indices with detailed warning (valid range + total count)
- Add ValidateRendererIndex() public API for tests/runtime re-validation
- OnValidate clamps negative values in Editor
- Comprehensive EditMode + PlayMode test coverage
```