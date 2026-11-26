# CameraForceRenderByIndex

Forces a Camera to use a specific URP Renderer by index. Useful for minimaps, portals, or special
views with different renderer configurations.

## Requirements
- Unity 6 / URP 17.x (`com.unity.render-pipelines.universal`)
- A `Camera` on the same GameObject. The component does **not** auto-add a Camera
  (`RequireComponent` was removed so tools can never create a rogue camera by accident).

## Usage
1. Add `CameraForceRenderByIndex` to an existing Camera GameObject.
2. Set `RendererIndex` in the Inspector (0 = first renderer in the URP Asset).
3. Make sure the URP Asset has enough renderers in `rendererDataList`.
4. Enable the component to force the index. Disable it to restore the camera's original renderer.

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
| `RendererIndex` | int | 0 | Zero-based index into the URP Asset's `rendererDataList` |
| `EnableLog` | bool | false | Enable debug logs in the Console |

## Public API
| Member | Description |
|--------|-------------|
| `ValidateRendererIndex()` | Clamps/applies the index. While disabled it only clamps the serialized value and never touches the camera. |
| `IsApplied` | True while this component is actively overriding the camera renderer. |
| `CurrentRendererIndex` | Renderer index currently stored on the camera data (-1 = URP default). |
| `OriginalRendererIndex` | Renderer index captured before the current force was applied. |
| `AvailableRendererCount` | Number of renderers in the active URP asset (0 when URP is unavailable). |
| `EnableLog` | Runtime getter/setter for debug logging. |

## Behavior
- **Awake**: Resolves the Camera and its `UniversalAdditionalCameraData` (creating URP data only
  when URP itself has not added it yet). It does not apply the index while the component is off.
- **OnEnable**: Validates, clamps, caches the camera's current renderer, and applies the force.
- **OnDisable / OnDestroy**: Restores the renderer the camera had before the force was applied.
- **OnValidate (Editor only)**: Clamps negative values; re-applies in Play mode only while enabled.
- **ValidateRendererIndex()**: Public re-validation entry point for UI, tools, and tests.

## Edge Cases Handled
| Scenario | Behavior |
|----------|----------|
| No Camera on the GameObject | One warning, no crash, no auto-created Camera |
| No URP Asset in GraphicsSettings | One warning, no crash |
| Missing `UniversalAdditionalCameraData` | Requested through URP's own `GetUniversalAdditionalCameraData()` |
| `RendererIndex < 0` | Clamped to 0 |
| `RendererIndex >= rendererCount` | Warning once, clamped to the last valid index, then applied |
| Only 1 renderer in the URP Asset | Every index clamps to 0; the control card explains this |
| Component disabled | Original renderer restored; no per-frame work |

## Tools / Control Card
Menu path: `Tools → Meta → CameraForce`

- **Setup**: creates/repairs `Meta_CameraForce_Rig` and the Play-mode control card.
  - The helper camera is **Untagged**, **disabled**, depth `-100`, clear flags `Depth`, culling
    mask `0`, and has **no AudioListener**.
  - Never creates a Camera tagged `MainCamera` and never re-tags/strips the avatar/player
    `MainCamera`.
- **Show Control UI / Hide Control UI**: toggles the runner (`Meta_CameraForce_ControlRunner`) and
  card canvas (`Meta_CameraForce_ControlHUD`). The card ships hidden by default.
- **Alt + Setup**: force-recreates the rig and card.
- Buttons: `Enable`, `Disable`, `Default (0)`, `Alt (1)`. Persistent listeners are written by the
  Setup tool; at runtime the panel only adds a listener when one is missing, so buttons do not
  double-fire.
- The card targets the helper rig camera. A `CameraForceRenderByIndex` on a camera tagged
  `MainCamera` is ignored by the card and by the runtime tests.

## Limitations
- **Single Renderer URP Asset**: indices greater than 0 clamp to 0 with one warning.
- **Runtime URP changes**: if the URP Asset is swapped at runtime, call `ValidateRendererIndex()`
  to re-sync.
- **Private URP getter**: URP exposes `SetRenderer(int)` but no public getter. `CurrentRendererIndex`
  and the original-index cache read URP's serialized `m_RendererIndex` field by reflection, with a
  safe `-1` fallback.

## Verification
1. `Tools → Meta → CameraForce → Setup` in Edit mode.
2. Enter Play mode and use `Tools → Meta → CameraForce → Show Control UI` (or the Meta Test burger).
3. Checks:
   - avatar `MainCamera` remains active, tagged `MainCamera`, and drives the game view;
   - the helper rig camera is Untagged and disabled;
   - `Enable` applies index 0; `Disable` restores the original renderer;
   - `Alt (1)` on a one-renderer URP asset clamps to 0 without an exception;
   - the Console shows no duplicate `AudioListener` warnings.
