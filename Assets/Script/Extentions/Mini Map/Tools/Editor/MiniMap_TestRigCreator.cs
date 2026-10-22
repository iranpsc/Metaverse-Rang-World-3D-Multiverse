#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Production HUD setup for the minimap radar (menu: Tools/Meta/MiniMap/Setup Radar In Scene).
    /// Class/file name kept for GUID safety; MENU + hierarchy are the HUD/production naming.
    /// </summary>
    public static class MiniMap_TestRigCreator
    {
        public const string RIG_ROOT_NAME = "Meta_MiniMap_Rig";
        public const string HUD_NAME = "Meta_MiniMap_HUD";
        public const string CONTROLS_NAME = "Meta_MiniMap_Controls";
        private const string RADAR_CARD = "RadarCard";
        private const string CAMERA_NAME = "MiniMapCamera";
        private const string RT_NAME = "Meta_MiniMap_RT";

        private const int RT_SIZE = 256;    // RenderTexture resolution
        private const int CARD_SIZE = 280;  // radar diameter
        private const int MAP_INSET = 12;   // bezel thickness -> 256 == RT size
        private const int MAP_SIZE = CARD_SIZE - MAP_INSET * 2;
        private const int BLIP_SIZE = 22;
        private const int ROW_HEIGHT = 24;  // dev strip rows

        private const string DEFAULT_ENVIRONMENT_ID = "lobby_1";   // the PLACE (room)
        private const string DEFAULT_CITIZEN_ID = "qa_citizen_1";  // the PLAYER

        // SINGLE menu entry for the MiniMap module: builds the radar HUD + the ON/OFF control panel.
        // Alt + click = force recreate everything for this module.
        [MenuItem("Tools/Meta/MiniMap/Setup", priority = 100)]
        public static void CreateTestRig()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[MiniMap] Setup only in Edit mode. Exit Play first.");
                return;
            }

            bool forceRecreate = Event.current != null && Event.current.alt;

            var existing = FindAny(RIG_ROOT_NAME);
            if (existing != null && !forceRecreate)
            {
                Selection.activeGameObject = existing;
                RepairRig(existing);
                CleanupGhostMiniMaps();
                MiniMap_ControlPanel_Setup.CleanupExtraEventSystems();
                MiniMap_ControlPanel_Setup.BuildOrSelect(false);
                Debug.Log("[MiniMap] Radar rig present - repaired on the active scene " +
                          "(refs, platform, ids, IsEnabled, ghosts). Alt+click Setup to force recreate.");
                return;
            }

            if (forceRecreate)
            {
                var oldHud = FindAny(HUD_NAME);
                if (existing != null) Undo.DestroyObjectImmediate(existing);
                if (oldHud != null) Undo.DestroyObjectImmediate(oldHud);
                Debug.Log("[MiniMap] Alt+Setup = force recreate (radar rig + HUD removed).");
            }

            Build();
            CleanupDuplicates();
            CleanupGhostMiniMaps();
            MiniMap_ControlPanel_Setup.CleanupExtraEventSystems();
            MiniMap_ControlPanel_Setup.BuildOrSelect(forceRecreate);
            Debug.Log($"[MiniMap] Setup complete: Platform=Windows, IsEnabled=true, " +
                      $"CitizenId='{DEFAULT_CITIZEN_ID}' (player), EnvironmentId='{DEFAULT_ENVIRONMENT_ID}' (place).");
        }

        private static void Build()
        {
            // ---------------------------------------------------------------- rig
            var rigRoot = new GameObject(RIG_ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(rigRoot, "Create MiniMap Radar Rig");

            var miniMap = Undo.AddComponent<Meta_MiniMap>(rigRoot);
            var tool = Undo.AddComponent<MiniMap_SceneTool>(rigRoot);
            tool.ButtonsWiredExternally = true;   // buttons carry persistent listeners below

            // Camera: orthographic, straight down. localPos MUST stay 0 (altitude = Offset.y)
            var camGO = new GameObject(CAMERA_NAME);
            Undo.RegisterCreatedObjectUndo(camGO, "Create MiniMap Camera");
            camGO.transform.SetParent(rigRoot.transform, false);
            camGO.transform.localPosition = Vector3.zero;
            camGO.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var cam = camGO.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 40f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Meta_HudStyle.Void;
            cam.cullingMask = ~0;
            cam.nearClipPlane = 0.3f;              // Meta_MiniMap re-slices per Offset.y at runtime
            cam.farClipPlane = 2000f;
            cam.depth = 100f;
            cam.enabled = false;
            miniMap.MiniMapCamera = cam;

            var urpCam = camGO.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            urpCam.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Base;
            urpCam.renderPostProcessing = false;
            urpCam.renderShadows = false;
            urpCam.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
            urpCam.requiresColorOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;
            urpCam.SetRenderer(0);

            var rt = new RenderTexture(RT_SIZE, RT_SIZE, 16, RenderTextureFormat.ARGB32)
            {
                name = RT_NAME,
                useMipMap = false,
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            miniMap.MiniMapRenderTexture = rt;
            miniMap.MiniMapTextureSize = RT_SIZE;

            // Optional top-down fill light (diagnostic only, off by default)
            var boostGO = new GameObject("RadarBoostLight");
            Undo.RegisterCreatedObjectUndo(boostGO, "RadarBoostLight");
            boostGO.transform.SetParent(rigRoot.transform, false);
            boostGO.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var boost = boostGO.AddComponent<Light>();
            boost.type = LightType.Directional;
            boost.color = Color.white;
            boost.intensity = 0.45f;
            boost.shadows = LightShadows.None;
            boost.enabled = false;
            miniMap.UseRadarBoostLight = false;

            // VR panel (inactive)
            var vrPanel = new GameObject("VRMiniMapPanel");
            Undo.RegisterCreatedObjectUndo(vrPanel, "VRMiniMapPanel");
            vrPanel.transform.SetParent(rigRoot.transform, false);
            vrPanel.SetActive(false);
            miniMap.VRMiniMap = vrPanel;
            var vrBlip = new GameObject("VRPlayerDirectionImage");
            Undo.RegisterCreatedObjectUndo(vrBlip, "VRPlayerDirectionImage");
            vrBlip.transform.SetParent(vrPanel.transform, false);
            var vrRect = vrBlip.AddComponent<RectTransform>();
            vrRect.sizeDelta = new Vector2(BLIP_SIZE, BLIP_SIZE);
            var vrImg = vrBlip.AddComponent<UnityEngine.UI.Image>();
            vrImg.sprite = Meta_HudStyle.Blip(64);
            vrImg.raycastTarget = false;
            miniMap.VRPlayerDirectionImage = vrImg;

            // ---------------------------------------------------------------- HUD
            var canvas = Meta_HudStyle.EnsureCanvas(HUD_NAME, 250);
            Meta_HudStyle.EnsureEventSystem();

            CreateRadarCard(miniMap, rt, canvas.transform);
            // Single control card lives in MiniMap_ControlPanel_Setup (bottom-left).
            // No duplicate dev strip here.

            // ---------------------------------------------------------------- tuning
            miniMap.MinZoom = 5f;
            miniMap.MaxZoom = 200f;
            miniMap.Offset = new Vector3(0f, 60f, 0f);
            miniMap.FollowSpeed = 10f;
            miniMap.RotateWithPlayer = false;   // north-up radar; centre blip rotates with yaw
            miniMap.Target = null;

            // Radar ON by default for the test/production rig: pressing Play must SHOW the radar.
            // A stored citizen preference (radar OFF) is applied later by the normal restore.
            miniMap.IsEnabled = true;

            SetField(miniMap, "_platform", PlatformType.Windows);   // never "Unknow" in a fresh rig

            // Ids: citizen = PLAYER, environment = PLACE (the exact mix-up seen in real scenes).
            SetField(miniMap, "_citizenId", DEFAULT_CITIZEN_ID);
            SetField(miniMap, "_environmentId", DEFAULT_ENVIRONMENT_ID);

            Selection.activeGameObject = rigRoot;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[MiniMap] HUD ready: circular radar (north-up, centre blip) + compact dev strip.");
        }

        // =====================================================================
        // RadarCard - SQUARE panel, TOP-LEFT (client requirement)
        // =====================================================================
        private static void CreateRadarCard(Meta_MiniMap miniMap, RenderTexture rt, Transform hud)
        {
            var card = new GameObject(RADAR_CARD);
            Undo.RegisterCreatedObjectUndo(card, RADAR_CARD);
            card.transform.SetParent(hud, false);
            var cardRect = card.AddComponent<RectTransform>();

            // TOP-LEFT: anchors + pivot (0,1), inset (+24, -24)
            cardRect.anchorMin = new Vector2(0f, 1f);
            cardRect.anchorMax = new Vector2(0f, 1f);
            cardRect.pivot = new Vector2(0f, 1f);
            cardRect.anchoredPosition = new Vector2(24f, -24f);
            cardRect.sizeDelta = new Vector2(CARD_SIZE, CARD_SIZE);   // SQUARE: width == height

            // Visible in Edit mode for layout review; Meta_MiniMap.Start hides it if the radar is OFF
            var group = card.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = false;
            group.blocksRaycasts = false;

            miniMap.MiniMap = card;   // ToggleMiniMap hides/fades the whole card

            // Square sprites: hard rounded for bezel, soft rounded for shadow
            var square = Meta_HudStyle.Rounded(160, 14);
            var softSquare = Meta_HudStyle.Rounded(128, 20, 14f);

            // 1) soft drop shadow (floats above the HUD)
            Meta_HudStyle.Layer(card.transform, "Shadow", softSquare, new Color(0f, 0f, 0f, 0.18f),
                new Vector2(CARD_SIZE + 18f, CARD_SIZE + 18f), new Vector2(3f, -6f));
            Meta_HudStyle.Layer(card.transform, "ShadowTight", softSquare, new Color(0f, 0f, 0f, 0.34f),
                new Vector2(CARD_SIZE + 8f, CARD_SIZE + 8f), new Vector2(2f, -4f));

            // 2) bezel + 2px edge (lighter square behind, darker square in front)
            Meta_HudStyle.Layer(card.transform, "BezelEdge", square, Meta_HudStyle.BezelEdge,
                new Vector2(CARD_SIZE, CARD_SIZE), Vector2.zero, true);
            Meta_HudStyle.Layer(card.transform, "OuterBezel", square, Meta_HudStyle.Bezel,
                new Vector2(CARD_SIZE - 3f, CARD_SIZE - 3f), Vector2.zero, true);

            // 3) bright square inner rim (behind the map area)
            Meta_HudStyle.Layer(card.transform, "InnerRim", square, Meta_HudStyle.AccentSoft,
                new Vector2(MAP_SIZE + 6f, MAP_SIZE + 6f), Vector2.zero, true);

            // 4) SQUARE clip for the world RT (RectMask2D = plain rectangle, no circular mask)
            var maskGO = new GameObject("MapMask");
            Undo.RegisterCreatedObjectUndo(maskGO, "MapMask");
            maskGO.transform.SetParent(card.transform, false);
            var maskRect = maskGO.AddComponent<RectTransform>();
            maskRect.anchorMin = maskRect.anchorMax = maskRect.pivot = new Vector2(0.5f, 0.5f);
            maskRect.sizeDelta = new Vector2(MAP_SIZE, MAP_SIZE);      // 256 == RT size
            maskGO.AddComponent<RectMask2D>();                        // square clip

            var rawGO = new GameObject("MiniMapView");
            Undo.RegisterCreatedObjectUndo(rawGO, "MiniMapView");
            rawGO.transform.SetParent(maskGO.transform, false);
            var rawRect = rawGO.AddComponent<RectTransform>();
            rawRect.anchorMin = Vector2.zero;
            rawRect.anchorMax = Vector2.one;
            rawRect.offsetMin = rawRect.offsetMax = Vector2.zero;
            var raw = rawGO.AddComponent<UnityEngine.UI.RawImage>();
            raw.texture = rt;              // Meta_MiniMap re-asserts this at runtime
            raw.color = Color.white;      // keep the world view as bright as possible
            raw.raycastTarget = false;
            miniMap.MiniMapRenderImage = raw;

            // very light dim so the blip reads without darkening the map
            Meta_HudStyle.Layer(maskGO.transform, "MapDim", square, new Color(0f, 0f, 0f, 0.05f),
                new Vector2(MAP_SIZE, MAP_SIZE), Vector2.zero, true);

            // 5) north mark - readable size/contrast (client requirement)
            var north = Meta_HudStyle.Label(card.transform, "NorthMark", "N", 13,
                new Color(0.69f, 0.72f, 0.77f, 1f), TextAnchor.MiddleCenter);   // #B0B8C4
            north.fontStyle = FontStyle.Bold;
            var nr = north.rectTransform;
            nr.anchorMin = nr.anchorMax = nr.pivot = new Vector2(0.5f, 0.5f);
            nr.sizeDelta = new Vector2(24f, 16f);
            nr.anchoredPosition = new Vector2(0f, CARD_SIZE * 0.5f - 14f);

            // 6) centre blip - ALWAYS centred, world scrolls underneath
            var blipGO = new GameObject("CenterBlip");
            Undo.RegisterCreatedObjectUndo(blipGO, "CenterBlip");
            blipGO.transform.SetParent(card.transform, false);
            var br = blipGO.AddComponent<RectTransform>();
            br.anchorMin = br.anchorMax = br.pivot = new Vector2(0.5f, 0.5f);
            br.sizeDelta = new Vector2(BLIP_SIZE, BLIP_SIZE);
            var blip = blipGO.AddComponent<UnityEngine.UI.Image>();
            blip.sprite = Meta_HudStyle.Blip(64);
            blip.raycastTarget = false;
            miniMap.PlayerDirectionImage = blip;
        }

        // =====================================================================
        // Dev-only strip (bottom-right): small dark chips, NOT on the radar
        // =====================================================================
        private static void CreateDevStrip(Meta_MiniMap miniMap, MiniMap_SceneTool tool, Transform hud)
        {
            // BOTTOM-LEFT - the bottom-right corner belongs to the room list / QA buttons
            var strip = Meta_HudStyle.Card(hud, CONTROLS_NAME, new Vector2(200f, 88f),
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(20f, 20f));
            Meta_HudStyle.Border(strip.transform, new Vector2(200f, 88f));

            var layout = strip.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // Ship-facing actions only. Fake Player / Respawn stay callable through
            // MiniMap_SceneTool (SpawnFakePlayer / SimulateRespawn) for QA - no dev clutter on the HUD.
            Meta_HudStyle.Chip(strip.transform, "Toggle Radar", tool.ToggleMap, ROW_HEIGHT, true);
            Meta_HudStyle.Chip(strip.transform, "Zoom +", tool.ZoomIn, ROW_HEIGHT);
            Meta_HudStyle.Chip(strip.transform, "Zoom -", tool.ZoomOut, ROW_HEIGHT);
        }

        // =====================================================================
        // Repair of an EXISTING rig (Setup menu, no Alt) - never destroys unrelated scene objects
        // =====================================================================
        /// <summary>
        /// Fixes the well-known broken states left in scenes by older Setup runs:
        ///  * missing camera / RT / RawImage / blip references
        ///  * Platform = Unknow                -> Windows (PC drives the RadarCard, never VR-only)
        ///  * swapped citizid/environment ids  -> citizen = PLAYER, environment = PLACE
        ///  * IsEnabled=false with nothing stored -> ON (so Play actually shows the radar)
        ///  * ghost duplicate Meta_MiniMap on the control runner (it swallowed the ON/OFF wiring)
        /// </summary>
        internal static void RepairRig(GameObject rigRoot)
        {
            if (rigRoot == null) return;

            var miniMap = rigRoot.GetComponent<Meta_MiniMap>();
            if (miniMap == null)
            {
                Debug.LogWarning("[MiniMap] Repair: Meta_MiniMap component missing on the rig root - re-created.");
                miniMap = Undo.AddComponent<Meta_MiniMap>(rigRoot);
            }

            Undo.RecordObject(miniMap, "Repair MiniMap Rig");

            // ---- camera -------------------------------------------------------
            if (miniMap.MiniMapCamera == null)
            {
                var cam = rigRoot.GetComponentInChildren<Camera>(true);
                if (cam == null)
                {
                    var camGO = new GameObject(CAMERA_NAME);
                    Undo.RegisterCreatedObjectUndo(camGO, "Repair MiniMap Camera");
                    camGO.transform.SetParent(rigRoot.transform, false);
                    camGO.transform.localPosition = Vector3.zero;
                    camGO.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    cam = camGO.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = 40f;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = Meta_HudStyle.Void;
                    cam.cullingMask = ~0;
                    cam.nearClipPlane = 0.3f;
                    cam.farClipPlane = 2000f;
                    cam.depth = 100f;
                    cam.enabled = false;
                    camGO.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                }
                miniMap.MiniMapCamera = cam;
                Debug.Log("[MiniMap] Repair: MiniMapCamera reference re-assigned.");
            }
            else if (miniMap.MiniMapCamera.transform.parent != rigRoot.transform)
            {
                // A lost parent (deleted rig / new camera picked up from the scene) - keep it, but
                // only when it is at least scene-local; otherwise rebuild a clean one on the rig.
                if (miniMap.MiniMapCamera.gameObject.scene != rigRoot.scene)
                    miniMap.MiniMapCamera = rigRoot.GetComponentInChildren<Camera>(true);
            }

            // ---- RenderTexture --------------------------------------------------
            if (miniMap.MiniMapRenderTexture == null)
            {
                if (miniMap.MiniMapRenderImage != null && miniMap.MiniMapRenderImage.texture is RenderTexture existingRt &&
                    existingRt != null)
                {
                    miniMap.MiniMapRenderTexture = existingRt;
                    Debug.Log("[MiniMap] Repair: RenderTexture taken from the RawImage texture.");
                }
                else
                {
                    var rt = new RenderTexture(RT_SIZE, RT_SIZE, 16, RenderTextureFormat.ARGB32)
                    {
                        name = RT_NAME,
                        useMipMap = false,
                        antiAliasing = 1,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    rt.Create();
                    miniMap.MiniMapRenderTexture = rt;
                    Debug.Log("[MiniMap] Repair: RenderTexture created (256x256).");
                }
            }

            // ---- panel + RawImage + blip ---------------------------------------
            var card = miniMap.MiniMap;
            if (card == null)
            {
                card = FindAny(RADAR_CARD);
                if (card != null)
                {
                    miniMap.MiniMap = card;
                    Debug.Log("[MiniMap] Repair: RadarCard found in scene and re-wired.");
                }
            }

            if (miniMap.MiniMapRenderImage == null && card != null)
            {
                var raw = card.GetComponentInChildren<RawImage>(true);
                if (raw != null)
                {
                    miniMap.MiniMapRenderImage = raw;
                    Debug.Log("[MiniMap] Repair: MiniMapRenderImage re-wired from the RadarCard.");
                }
            }

            if (miniMap.PlayerDirectionImage == null && card != null)
            {
                var blip = card.transform.Find("CenterBlip")?.GetComponent<Image>();
                if (blip != null) miniMap.PlayerDirectionImage = blip;
            }

            // ---- platform --------------------------------------------------------
            SetField(miniMap, "_platform", PlatformType.Windows);

            // ---- ids (player/place swap detector) --------------------------------
            FixSwappedIds(miniMap);

            // ---- IsEnabled ---------------------------------------------------------
            if (TryReadStoredRadarEnabled(miniMap, out bool storedRadar))
            {
                miniMap.IsEnabled = storedRadar;   // respect a stored citizen preference
                Debug.Log($"[MiniMap] Repair: stored preference found - IsEnabled stays {storedRadar}.");
            }
            else
            {
                // Nothing stored: the ON default must survive Play (Start keeps serialized state).
                if (!miniMap.IsEnabled)
                {
                    miniMap.IsEnabled = true;
                    Debug.Log("[MiniMap] Repair: no stored preferences - IsEnabled set to true so Play shows the radar.");
                }
            }

            EditorSceneManager.MarkSceneDirty(rigRoot.gameObject.scene);
        }

        /// <summary>
        /// Fixes the known "ids swapped" mistake seen in real scenes: the Citizen Id must be the
        /// PLAYER and the Environment Id must be the PLACE. Detected by content ("lobby..."
        /// in the citizen slot AND "...citizen/player..." in the environment slot), so honest data
        /// is never touched.
        /// </summary>
        private static void FixSwappedIds(Meta_MiniMap map)
        {
            string cit = map.CitizenId;
            string env = map.EnvironmentId;
            if (string.IsNullOrEmpty(cit) || string.IsNullOrEmpty(env)) return;

            bool citizenLooksLikePlace =
                cit.StartsWith("lobby", System.StringComparison.OrdinalIgnoreCase) ||
                cit.StartsWith("env", System.StringComparison.OrdinalIgnoreCase) ||
                cit.StartsWith("environment", System.StringComparison.OrdinalIgnoreCase) ||
                cit.StartsWith("room", System.StringComparison.OrdinalIgnoreCase) ||
                cit.StartsWith("place", System.StringComparison.OrdinalIgnoreCase);

            bool envLooksLikeCitizen =
                env.IndexOf("citizen", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                env.IndexOf("user", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                env.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) >= 0;

            if (citizenLooksLikePlace && envLooksLikeCitizen)
            {
                map.CitizenId = env;
                map.EnvironmentId = cit;
                Debug.Log($"[MiniMap] Repair: swapped ids fixed - CitizenId='{env}' (player), EnvironmentId='{cit}' (place).");
            }
        }

        /// <summary>
        /// Reads the stored radar preference for the rig's ids through the shared static reader
        /// (PlayerPrefs store, contract untouched). Returns false when nothing is stored.
        /// </summary>
        private static bool TryReadStoredRadarEnabled(Meta_MiniMap map, out bool radarEnabled)
        {
            radarEnabled = false;

            if (Meta.Meta_MiniMap.TryReadCitizenSettings(map.EnvironmentId, map.CitizenId, out var settings))
            {
                radarEnabled = settings.radarEnabled;
                return true;
            }

            // Legacy keys were written without an environment id: still readable, never written.
            if (!string.IsNullOrEmpty(map.CitizenId) &&
                Meta.Meta_MiniMap.TryReadCitizenSettings(null, map.CitizenId, out var legacy))
            {
                radarEnabled = legacy.radarEnabled;
                return true;
            }
            return false;
        }

        /// <summary>Sets a private serialized field (used for _platform/_citizenId/_environmentId).</summary>
        private static void SetField(object target, string fieldName, object value)
        {
            var ft = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            ft?.SetValue(target, value);
        }

        // =====================================================================
        // Cleanup of legacy / duplicate objects (keeps exactly ONE control card)
        // =====================================================================
        /// <summary>Removes old canvases and duplicate control cards for this module.</summary>
        internal static void CleanupDuplicates()
        {
            DestroyIfFound("Meta_MiniMap_Controls");       // removed dev strip
            DestroyIfFound("Meta_MiniMap_TestCanvas");     // legacy naming
            DestroyIfFound("Meta_MiniMap_ControlPanel");   // legacy control card
        }

        /// <summary>
        /// Removes the empty Meta_MiniMap "ghosts" that [RequireComponent] + the old Setup wiring
        /// left on tool-created host objects (e.g. Meta_MiniMap_ControlRunner). A ghost has every
        /// reference null, is not the rig's own component, and lives on a tool-created host - user
        /// instances are never touched.
        /// </summary>
        internal static void CleanupGhostMiniMaps()
        {
            var rigRoot = FindAny(RIG_ROOT_NAME);
            var ghosts = new System.Collections.Generic.List<Meta_MiniMap>();

            foreach (var map in Object.FindObjectsByType<Meta_MiniMap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (map == null) continue;
                if (rigRoot != null && map.transform.IsChildOf(rigRoot.transform)) continue;   // the real rig
                if (map.MiniMap != null || map.MiniMapCamera != null || map.MiniMapRenderImage != null) continue;   // has refs

                var host = map.gameObject.name;
                bool toolHost = host.StartsWith("Meta_MiniMap_Control", System.StringComparison.OrdinalIgnoreCase) ||
                                host.Equals(HUD_NAME, System.StringComparison.OrdinalIgnoreCase);
                if (!toolHost) continue;

                ghosts.Add(map);
            }

            foreach (var ghost in ghosts)
            {
                Debug.Log($"[MiniMap] Removed ghost duplicate Meta_MiniMap on '{ghost.gameObject.name}' " +
                          "(no rig references; it used to swallow the ON/OFF control wiring).");
                Undo.DestroyObjectImmediate(ghost);
            }
        }

        internal static void DestroyIfFound(string name)
        {
            var go = FindAny(name);
            if (go != null) Undo.DestroyObjectImmediate(go);
        }

        private static GameObject FindAny(string name)
        {
            var go = GameObject.Find(name);
            if (go != null) return go;
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t.gameObject;
            return null;
        }
    }
}
#endif