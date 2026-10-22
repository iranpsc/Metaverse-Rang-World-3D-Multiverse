# Meta HUD Setup Tools

Editor menu items under **Tools > Meta** that build the three Meta HUDs directly in a scene.
These are **production HUD setups**, not throwaway test rigs.

## Menu Structure (current)
```
Tools
 └─ Meta
     ├─ Setup Test Scene              (existing, unrelated)
     ├─ Generate Test Report...       (existing, unrelated)
     ├─ MiniMap                 / Setup
     ├─ DayNight                / Setup
     ├─ CameraForce             / Setup
     ├─ Setup Runtime Module Tester          (QA only)
     └─ Force Recreate Runtime Module Tester (QA only)
```

**Exactly one `Setup` per module.** Plain click re-uses an existing rig;
**Alt + click = force recreate** (old rig, old sun disc and old control card are destroyed first).
All three run in **Edit mode only** and re-use the scene's single `EventSystem` - never a second one.

## Control cards (one per module, bottom-left stack)
| Slot | Card | Position | Size |
|------|------|----------|------|
| 0 | `MINIMAP` - ON / OFF, Zoom + / Zoom - | (20, 20) | 220x130 |
| 1 | `DAY / NIGHT` - ON / OFF / Reset + status | (20, 158) | 220x176 |
| 2 | `CAMERA RENDERER` - Enable / Disable, Default (0) / Alt (1) | (20, 342) | 220x166 |

The radar square stays **TOP-LEFT** (0,1)/(0,1)/(0,1) @ (24, -24), 280x280, square `RectMask2D` clip.

## Shared visual language

All three HUDs are built from `Assets/Script/Extentions/Mini Map/Tools/Editor/Meta_HudStyle.cs`,
so they look like one game UI instead of three separate prototypes.

| Token | Value | Used for |
|-------|-------|----------|
| Panel BG | `#0E1218` @ 0.92 | dark glass cards |
| Bezel | `#12151A` | radar bezel |
| Accent | `#7EE0FF` (cyan) | rims, active chips, slider fill |
| Text primary | `#E8ECF1` | titles |
| Text muted | `#8B93A1` | labels, readouts |
| Button normal | `#1A2030` | chips |
| Button hover | `#243044` | chip hover |
| Player blip | `#7CFF6B` + `#0B0D10` outline | radar centre blip |
| Danger | `#FF5A5A` | invalid states |

Type scale: Title 14 / Body 12 / Micro 10 (`LegacyRuntime.ttf`, no TMP dependency).
Every button uses **`UnityEventTools.AddVoidPersistentListener`** so the serialized
listener survives Edit -> Play (edit-mode lambdas are dropped and the button dies).

---

## 1) MiniMap - Tools/Meta/MiniMap/Setup

### Creates
```
Meta_MiniMap_Rig (GameObject)
├─ Meta_MiniMap                 (MonoBehaviour, client-only, no NetworkIdentity)
├─ MiniMap_SceneTool            (ButtonsWiredExternally = true)
├─ MiniMapCamera                (ortho 40, localPos (0,0,0), rot (90,0,0), mask Everything,
│                                0.3/2000, depth 100, disabled until toggle, URP Base renderer 0)
├─ RadarBoostLight              (diagnostic only, disabled; Meta_MiniMap.UseRadarBoostLight)
└─ VRMiniMapPanel (inactive) └─ VRPlayerDirectionImage

Meta_MiniMap_HUD (Canvas, ScreenSpaceOverlay, sortingOrder 250)
├─ RadarCard  ** TOP-LEFT **, SQUARE 280x280 @ (+24,-24), CanvasGroup fade
│  ├─ Shadow / ShadowTight      soft square drop shadow
│  ├─ BezelEdge / OuterBezel    dark metal square stack (2 px edge)
│  ├─ InnerRim                  thin cyan square rim
│  ├─ MapMask                   RectMask2D (square clip, inset 12 -> 256 == RT size)
│  │  ├─ MiniMapView            RawImage = Meta_MiniMap_RT (256x256, white, no tint)
│  │  └─ MapDim                 5% black for blip contrast
│  ├─ NorthMark                 "N"
│  └─ CenterBlip                22px chevron, ALWAYS centred, rotates with player yaw

Meta_MiniMap_ControlPanel (Canvas, sortingOrder 260, bottom-left slot 0 @ (20,20), 220x130)
└─ ON | OFF   +   Zoom + | Zoom -   +   one thin status line
```

> **Client requirement:** the minimap is a **SQUARE panel anchored TOP-LEFT** at (+24, -24).
> It is *not* a circular GTA radar: square bezel, square rim, square `RectMask2D` clip.
> There is **no second dev strip on the main card**. `Fake Player` / `Respawn` are offline-only and
> stay callable from code: `MiniMap_SceneTool.SpawnFakePlayer()` / `SimulateRespawn()`.

### Behaviour
- **North-up radar by default** (`RotateWithPlayer = false`): the world scrolls under a
  fixed centre blip and the blip rotates with the player's yaw.
- `Toggle Radar` fades the whole `RadarCard` via `CanvasGroup` over 0.12 s
  (falls back to `SetActive` when no `CanvasGroup` is present).
- Online target resolves from `G7ThreeDModeController` -> `Local_Player_Cylinder`.
  `Fake Player` is an **offline helper only** (visible capsule + disabled rotation camera).
- Zoom changes `orthographicSize` only; camera altitude stays `Offset.y`.
- Self-correcting clip slice: the near plane cuts everything above the player's head
  (ceilings/roofs) but never clips the floor or the player.

---

## 2) DayNight - Tools/Meta/DayNight/Setup

```
Meta_DayNight_Rig
├─ Meta_DayNightCycle
├─ DayNight_SceneTool (ButtonsWiredExternally = true)
├─ Meta_Sun_Light     (directional, Soft shadows, aimed at the view, colour along the curve)
└─ Meta_Sun_Disc      (VISIBLE sun: gold #FFD27A + HDR emission + additive glow halo)
   └─ Meta_Sun_Disc_Glow

Meta_DayNight_ControlPanel (Canvas, sortingOrder 260, bottom-left slot 1 @ (20,158), 220x152)
├─ Title  "DAY / NIGHT" (14 bold, muted)
├─ ON | OFF               (primary row)
├─ Reset                  (quiet text button)
└─ StatusText             one thin line set: Cycle / Light / Disc / Ambient
```

A `Directional Light` renders nothing in the sky, so the module also drives the
**visible `Meta_Sun_Disc`**: parked at `focus - sunLight.forward * distance`, rotated to face the
focus point, tinted by the same elevation curve, hidden below the horizon unless `ShowDiscAtNight`.
`Meta_DayNightCycle` stores sun rotation, intensity, skybox and ambient in `Awake`
and restores **all** of them in `ResetToDefault` (`OnDisable`/`OnDestroy` stop + reset).

---

## 3) CameraForce - Tools/Meta/CameraForce/Setup

```
Meta_CameraForce_Rig
└─ Meta_CameraForce_Camera
   ├─ Camera
   └─ CameraForceRenderByIndex   (RendererIndex = 0, EnableLog = true)

Meta_CameraForce_ControlPanel (Canvas, sortingOrder 300, bottom-left slot 2 @ (20,316), 220x166)
├─ Title     "CAMERA RENDERER"                                   (13 bold, muted)
├─ Subtitle  "Pick which URP renderer this camera draws with."    (10 muted, wraps)
├─ [ Enable ] [ Disable ]        + tooltip on each chip
├─ [ Default (0) ] [ Alt (1) ]   + tooltip on each chip
└─ StatusText  "Status: on - using renderer 0 (Default)."        (11 muted)
```

Labels are deliberately **plain words, not code terms** (`Enable`/`Disable`, `Default (0)`/`Alt (1)`),
because a normal player has no idea what "Apply Index 0" means. Every chip carries a
`Meta_HudTooltip` (pointer enter/exit) explaining what it really does.

> Tooltips and labels are English on purpose: legacy uGUI `Text` does not perform Arabic script
> shaping, so Persian labels would render as disconnected, mis-ordered letters.

`CameraForceRenderByIndex` adds `UniversalAdditionalCameraData` when missing, validates
`RendererIndex` against `rendererDataList.Length`, and warns + clamps out-of-range values.
`Apply Invalid 99` and the live RENDERERS dump are **code/test only** - never on the HUD.

---

## Release checklist
- The control cards are dev HUDs. Delete or hide the `*_ControlPanel` canvases before a
  release build if the controls are not wanted in game.
- `RadarBoostLight` / `UseRadarBoostLight` stays `false` in production.
- MiniMap stays a client-only `MonoBehaviour`: never add `NetworkIdentity`, never
  `SetParent` the player.

## Requirements
- Unity 6000.2+ / URP (renderer 0 must exist in the URP asset)
- No TextMeshPro required (`LegacyRuntime.ttf`)