#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Meta.Tools;
using Meta.EditorTools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Creates the CameraForce Play-mode control card.
    ///
    /// Card language (plain, not technical):
    ///   CAMERA RENDERER
    ///   Pick which URP renderer this camera draws with.
    ///   [ Enable ]  [ Disable ]
    ///   [ Default (0) ]  [ Alt (1) ]
    ///   Status: On - Using renderer 0 (Default)
    ///
    /// Every button carries a tooltip explaining what it really does.
    /// Invoked by Tools/Meta/CameraForce/Setup - no separate menu item, no duplicate HUD.
    /// The card always targets a non-player camera; a camera tagged MainCamera is never used.
    /// </summary>
    public static class CameraForce_ControlPanel_Setup
    {
        internal const string ROOT_NAME = "Meta_CameraForce_ControlRunner";
        internal const string HUD_NAME = "Meta_CameraForce_ControlHUD";
        private const string PANEL_NAME = "Meta_CameraForce_ControlPanel";
        private const string ROW1_NAME = "ButtonsRow";
        private const string ROW2_NAME = "IndexRow";

        private const float CARD_WIDTH = 220f;
        private const float CARD_HEIGHT = 166f;
        private static readonly Vector2 CARD_POS = new Vector2(-20f, -20f);  // TOP-RIGHT corner
        private static readonly Vector2 TOOLTIP_SIZE = new Vector2(200f, 40f);

        // Button labels are the single source of truth for both the chips and the runtime lookup.
        internal const string BTN_ENABLE = "Enable";
        internal const string BTN_DISABLE = "Disable";
        internal const string BTN_RENDERER_0 = "Default (0)";
        internal const string BTN_RENDERER_1 = "Alt (1)";

        /// <summary>Called by Tools/Meta/CameraForce/Setup. Selects existing, otherwise builds.</summary>
        internal static void BuildOrSelect(bool forceRecreate)
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[CameraForceControl] Setup only in Edit mode. Exit Play first.");
                return;
            }

            if (forceRecreate)
            {
                var oldRoot = MetaModuleTestUI_Menus.FindObject(ROOT_NAME);
                var oldHud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
                if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);
                if (oldHud != null) Undo.DestroyObjectImmediate(oldHud);
            }

            bool created = false;
            var root = MetaModuleTestUI_Menus.FindObject(ROOT_NAME);   // inactive-aware
            if (root == null)
            {
                root = new GameObject(ROOT_NAME);
                Undo.RegisterCreatedObjectUndo(root, "Create CameraForce Control Runner");
                created = true;
            }

            var runner = root.GetComponent<CameraForce_ControlPanel>();
            if (runner == null)
                runner = Undo.AddComponent<CameraForce_ControlPanel>(root);

            // Kept for old serialized scenes; the runtime now checks listeners instead of trusting it.
            runner.ButtonsWiredExternally = true;

            EnsureCard();
            RepairRunner(root);
            RepositionCardTopRight();

            // Test control UI must never ship visible (it would stack with the other module cards).
            MetaModuleTestUI_Menus.ApplyDefaultHidden(ROOT_NAME, HUD_NAME);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log(created
                ? $"[CameraForceControl] Created {ROOT_NAME} top-right, hidden by default. " +
                  "Card: Enable / Disable + Default (0) / Alt (1), each with a tooltip."
                : $"[CameraForceControl] Control card already present - card and listeners re-wired, hidden by default. " +
                  "Show: Tools/Meta/CameraForce/Show Control UI.");
        }

        /// <summary>
        /// Ensures the production HUD canvas and exactly one card panel exist. Reuses an existing
        /// card instead of creating a duplicate beside it.
        /// </summary>
        private static void EnsureCard()
        {
            var hud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
            if (hud != null && hud.GetComponent<Canvas>() == null)
            {
                Debug.LogError($"[CameraForceControl] '{HUD_NAME}' exists but is not a Canvas. " +
                               "Move it out of the way and run Setup again.");
                return;
            }

            if (hud == null)
            {
                hud = new GameObject(HUD_NAME);
                Undo.RegisterCreatedObjectUndo(hud, "Create CameraForce Control HUD Canvas");
                var canvas = hud.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 300;
                hud.AddComponent<CanvasScaler>();
                hud.AddComponent<GraphicRaycaster>();
            }

            EnsureEventSystem();

            if (hud.transform.Find(PANEL_NAME) == null)
                CreatePanel(hud.transform);
        }

        private static void EnsureEventSystem()
        {
            // Include inactive objects: EventSystem.current is null in Edit mode even when a valid
            // (possibly inactive) EventSystem already exists. Creating a second one spams warnings.
            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>(
                    FindObjectsInactive.Include) != null)
            {
                return;
            }

            var es = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            var inputMod = System.Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
                ?? System.Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
            if (inputMod != null) es.AddComponent(inputMod);
        }

        /// <summary>
        /// Repair the WIRING and PERSISTENT LISTENERS (no card creation): runner component refs to
        /// the real card buttons and to the REAL non-player CameraForceRenderByIndex / Camera of the
        /// scene - never a RequireComponent ghost or the avatar/player MainCamera.
        /// </summary>
        private static void RepairRunner(GameObject root)
        {
            // Old Setup runs hit the removed RequireComponent chain: Unity had auto-added a
            // CameraForceRenderByIndex + Camera (plus AudioListener) onto this UI runner root.
            // That rogue camera renders the scene a second time - strip it before re-wiring.
            RemoveGhostCameraChain(root);

            var runner = root.GetComponent<CameraForce_ControlPanel>();
            if (runner == null)
            {
                runner = Undo.AddComponent<CameraForce_ControlPanel>(root);
            }
            runner.ButtonsWiredExternally = true;

            var hud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
            var panel = hud != null ? hud.transform.Find(PANEL_NAME) : null;

            if (panel != null)
            {
                Button enable = panel.Find(ROW1_NAME + "/" + BTN_ENABLE)?.GetComponent<Button>();
                Button disable = panel.Find(ROW1_NAME + "/" + BTN_DISABLE)?.GetComponent<Button>();
                Button index0 = panel.Find(ROW2_NAME + "/" + BTN_RENDERER_0)?.GetComponent<Button>();
                Button index1 = panel.Find(ROW2_NAME + "/" + BTN_RENDERER_1)?.GetComponent<Button>();
                Text status = panel.Find("StatusText")?.GetComponent<Text>();

                MiniMap_ControlPanel_Setup.SetField(runner, "_onButton", enable);
                MiniMap_ControlPanel_Setup.SetField(runner, "_offButton", disable);
                MiniMap_ControlPanel_Setup.SetField(runner, "_applyIndex0Button", index0);
                MiniMap_ControlPanel_Setup.SetField(runner, "_applyIndex1Button", index1);
                MiniMap_ControlPanel_Setup.SetField(runner, "_statusText", status);

                // Persistent listeners are the shipping path. Remove stale/duplicate entries for
                // our own methods, then add exactly one per button.
                WirePersistent(enable, nameof(runner.OnClicked), runner.OnClicked);
                WirePersistent(disable, nameof(runner.OffClicked), runner.OffClicked);
                WirePersistent(index0, nameof(runner.ApplyIndex0Clicked), runner.ApplyIndex0Clicked);
                WirePersistent(index1, nameof(runner.ApplyIndex1Clicked), runner.ApplyIndex1Clicked);
            }
            else
            {
                Debug.LogWarning("[CameraForceControl] Card panel is missing. " +
                                 "Run Tools/Meta/CameraForce/Setup to rebuild it.");
            }

            var force = FindBestForce(root.transform);
            if (force == null)
            {
                Debug.LogWarning("[CameraForceControl] No non-player CameraForceRenderByIndex found. " +
                                 "Run Tools/Meta/CameraForce/Setup to create the helper camera. " +
                                 "The player MainCamera is never used.");
            }

            MiniMap_ControlPanel_Setup.SetField(runner, "_cameraForce", force);
            MiniMap_ControlPanel_Setup.SetField(runner, "_camera", force != null ? force.GetComponent<Camera>() : null);

            foreach (var tool in Object.FindObjectsByType<CameraForceRender_SceneTool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (tool == null) continue;

                var ownForce = tool.GetComponent<CameraForceRenderByIndex>();
                var target = IsUsableForce(ownForce, root.transform) ? ownForce : force;

                MiniMap_ControlPanel_Setup.SetField(tool, "_forceRender", target);
                MiniMap_ControlPanel_Setup.SetField(tool, "_camera", target != null ? target.GetComponent<Camera>() : null);
            }
        }

        private static void WirePersistent(Button button, string methodName, UnityAction action)
        {
            if (button == null || action == null) return;

            var evt = button.onClick;

            // Remove entries for this method (including stale null-target duplicates).
            for (int i = evt.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                if (evt.GetPersistentMethodName(i) == methodName)
                    UnityEventTools.RemovePersistentListener(evt, i);
            }

            UnityEventTools.AddVoidPersistentListener(evt, action);
        }

        /// <summary>
        /// Removes the ghost CameraForceRenderByIndex / Camera / AudioListener chain that old
        /// RequireComponent attributes auto-created on the control runner root. The real rig camera
        /// lives under <c>Meta_CameraForce_Rig</c> and is never touched.
        /// </summary>
        private static void RemoveGhostCameraChain(GameObject root)
        {
            if (root == null) return;

            var ghostForce = root.GetComponent<CameraForceRenderByIndex>();
            if (ghostForce != null)
            {
                Undo.DestroyObjectImmediate(ghostForce);
                Debug.Log("[CameraForceControl] Removed ghost CameraForceRenderByIndex from the control runner root.");
            }

            var ghostCamera = root.GetComponent<Camera>();
            if (ghostCamera != null)
            {
                Undo.DestroyObjectImmediate(ghostCamera);
                Debug.Log("[CameraForceControl] Removed rogue Camera from the control runner root.");
            }

            var ghostListener = root.GetComponent<AudioListener>();
            if (ghostListener != null)
            {
                Undo.DestroyObjectImmediate(ghostListener);
                Debug.Log("[CameraForceControl] Removed duplicate AudioListener from the control runner root.");
            }
        }

        /// <summary>
        /// A real force must be on a Camera, must not live under the UI runner, and must not drive
        /// a camera tagged MainCamera. Rig cameras are preferred over any other non-player camera.
        /// </summary>
        private static bool IsUsableForce(CameraForceRenderByIndex force, Transform runner)
        {
            if (force == null) return false;
            if (runner != null && force.transform.IsChildOf(runner)) return false;

            var camera = force.GetComponent<Camera>();
            if (camera == null) return false;
            if (camera.CompareTag("MainCamera")) return false;

            return true;
        }

        private static CameraForceRenderByIndex FindBestForce(Transform runner)
        {
            CameraForceRenderByIndex fallback = null;
            var rig = MetaModuleTestUI_Menus.FindObject(CameraForceRender_TestRigCreator.RIG_ROOT_NAME);

            foreach (var force in Object.FindObjectsByType<CameraForceRenderByIndex>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsUsableForce(force, runner)) continue;

                if (rig != null && force.transform.IsChildOf(rig.transform))
                    return force;

                if (fallback == null)
                    fallback = force;
            }

            return fallback;
        }

        /// <summary>Wiring-only entry for the Tools menus (NO card creation).</summary>
        internal static void Rewire()
        {
            var root = MetaModuleTestUI_Menus.FindObject(ROOT_NAME);
            if (root == null)
            {
                Debug.Log("[CameraForceControl] No control runner in scene - nothing to rewire.");
                return;
            }

            RepairRunner(root);
            RepositionCardTopRight();
            Debug.Log("[CameraForceControl] Runner re-wired (real non-player CameraForceRenderByIndex, " +
                      "card buttons, legacy SceneTools).");
        }

        internal static void RepositionCardTopRight()
        {
            var hud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
            var panel = hud != null ? hud.transform.Find(PANEL_NAME) : null;
            var rect = panel != null ? panel.GetComponent<RectTransform>() : null;
            if (rect == null)
            {
                Debug.LogWarning("[CameraForceControl] Card transform not found - reposition skipped.");
                return;
            }
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = CARD_POS;
        }

        private static void CreatePanel(Transform canvas)
        {
            var panelGO = new GameObject(PANEL_NAME);
            Undo.RegisterCreatedObjectUndo(panelGO, "Create CameraForce Control Panel");
            panelGO.transform.SetParent(canvas, false);
            var panelRect = panelGO.AddComponent<RectTransform>();
            // TOP-RIGHT corner - MiniMap keeps bottom-LEFT and DayNight sits bottom-RIGHT.
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            panelRect.anchoredPosition = CARD_POS;
            panelRect.sizeDelta = new Vector2(CARD_WIDTH, CARD_HEIGHT);

            MiniMap_ControlPanel_Setup.ApplyDarkGlass(panelGO);

            var layout = panelGO.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(14, 14, 12, 12);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // 1) Title - names the system in words, not in code terms.
            var title = CreateLabel(panelGO.transform, "Title", "CAMERA RENDERER", 13, FontStyle.Bold);
            title.color = Meta_HudStyle.TextMuted;
            title.alignment = TextAnchor.MiddleLeft;
            SetPreferredHeight(title.gameObject, 18f);

            // 2) One muted sentence so nobody has to guess what the buttons do.
            var subtitle = CreateLabel(panelGO.transform, "Subtitle",
                "Pick which URP renderer this camera draws with.", 10, FontStyle.Normal);
            subtitle.color = Meta_HudStyle.TextMuted;
            subtitle.alignment = TextAnchor.UpperLeft;
            subtitle.horizontalOverflow = HorizontalWrapMode.Wrap;
            subtitle.verticalOverflow = VerticalWrapMode.Truncate;
            SetPreferredHeight(subtitle.gameObject, 26f);

            // 3) Primary row - turn the forced-renderer mode on or off.
            var row1 = CreateButtonRow(panelGO.transform, ROW1_NAME);
            CreateButton(row1.transform, BTN_ENABLE, true,
                "Turn forced-renderer mode ON: this camera always uses the renderer you choose below.");
            CreateButton(row1.transform, BTN_DISABLE, false,
                "Stop forcing a renderer. The camera returns to the renderer it had before.");

            // 4) Secondary row - which renderer, in words.
            var row2 = CreateButtonRow(panelGO.transform, ROW2_NAME);
            CreateButton(row2.transform, BTN_RENDERER_0, false,
                "Use URP renderer 0 - the project's default renderer.");
            CreateButton(row2.transform, BTN_RENDERER_1, false,
                "Use URP renderer 1. Only differs if the URP asset defines a second renderer.");

            // 5) Status - plain sentence, not "Enabled: True".
            var statusText = CreateLabel(panelGO.transform, "StatusText",
                "Status: off - press Enable to start.", 11, FontStyle.Normal);
            statusText.color = Meta_HudStyle.TextMuted;
            statusText.alignment = TextAnchor.UpperLeft;
            statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            SetPreferredHeight(statusText.gameObject, 30f);

            Undo.RegisterCreatedObjectUndo(panelGO, "Create CameraForce Control Panel Complete");
        }

        private static GameObject CreateButtonRow(Transform parent, string name)
        {
            var row = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(row, "Create CameraForce Row " + name);
            row.transform.SetParent(parent, false);
            row.AddComponent<RectTransform>();

            var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 8f;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = true;

            SetPreferredHeight(row, 28f);
            return row;
        }

        private static void SetPreferredHeight(GameObject go, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }

        private static Text CreateLabel(Transform parent, string name, string text, int size, FontStyle style)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var txt = go.AddComponent<Text>();
            txt.text = text;
            txt.font = MiniMap_ControlPanel_Setup.GetFont();
            txt.fontSize = size;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.fontStyle = style;
            txt.raycastTarget = false;
            return txt;
        }

        private static Button CreateButton(Transform parent, string name, bool accent, string tooltip)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);

            var bg = go.AddComponent<Image>();
            bg.sprite = Meta_HudStyle.Rounded(64, 10);
            bg.type = Image.Type.Sliced;
            bg.color = Meta_HudStyle.ButtonNormal;
            bg.raycastTarget = true;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Meta_HudStyle.Tint(Meta_HudStyle.ButtonHover);
            colors.pressedColor = Meta_HudStyle.Tint(new Color(0.08f, 0.10f, 0.15f, 1f));
            colors.fadeDuration = 0.06f;
            btn.colors = colors;

            // The chip label is the button name, so it always says what it does.
            var labelGO = new GameObject("Text");
            Undo.RegisterCreatedObjectUndo(labelGO, "Create Chip Label");
            labelGO.transform.SetParent(go.transform, false);
            var labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var txt = labelGO.AddComponent<Text>();
            txt.text = name;
            txt.font = MiniMap_ControlPanel_Setup.GetFont();
            txt.fontSize = 11;
            txt.color = accent ? Meta_HudStyle.Accent : Meta_HudStyle.TextPrimary;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.raycastTarget = false;

            CreateTooltip(go, tooltip);

            return btn;
        }

        /// <summary>
        /// Tooltip attached to a chip. Row 1 opens downwards, row 2 upwards, so neither one
        /// covers the button the pointer is resting on.
        /// </summary>
        private static void CreateTooltip(GameObject button, string message)
        {
            bool below = button.transform.parent != null && button.transform.parent.name == ROW1_NAME;

            var tipGO = new GameObject("Tooltip");
            Undo.RegisterCreatedObjectUndo(tipGO, "Create Tooltip");
            tipGO.transform.SetParent(button.transform, false);

            var tipRect = tipGO.AddComponent<RectTransform>();
            tipRect.anchorMin = tipRect.anchorMax = new Vector2(0.5f, below ? 0f : 1f);
            tipRect.pivot = new Vector2(0.5f, below ? 1f : 0f);
            tipRect.anchoredPosition = new Vector2(0f, below ? -6f : 6f);
            tipRect.sizeDelta = TOOLTIP_SIZE;

            var bg = tipGO.AddComponent<Image>();
            bg.sprite = Meta_HudStyle.Rounded(64, 10);
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0.06f, 0.07f, 0.10f, 0.97f);
            bg.raycastTarget = false;

            var group = tipGO.AddComponent<CanvasGroup>();
            group.alpha = 0f;                 // hidden until the pointer arrives
            group.blocksRaycasts = false;
            group.interactable = false;

            var labelGO = new GameObject("Text");
            Undo.RegisterCreatedObjectUndo(labelGO, "Create Tooltip Label");
            labelGO.transform.SetParent(tipGO.transform, false);
            var labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 4f);
            labelRect.offsetMax = new Vector2(-8f, -4f);
            var tipText = labelGO.AddComponent<Text>();
            tipText.font = MiniMap_ControlPanel_Setup.GetFont();
            tipText.fontSize = 10;
            tipText.text = message;
            tipText.color = Meta_HudStyle.TextPrimary;
            tipText.alignment = TextAnchor.MiddleCenter;
            tipText.horizontalOverflow = HorizontalWrapMode.Wrap;
            tipText.verticalOverflow = VerticalWrapMode.Truncate;
            tipText.raycastTarget = false;

            var tip = Undo.AddComponent<Meta_HudTooltip>(button);
            tip.Configure(message, tipText, group, below, TOOLTIP_SIZE);
        }
    }
}
#endif
