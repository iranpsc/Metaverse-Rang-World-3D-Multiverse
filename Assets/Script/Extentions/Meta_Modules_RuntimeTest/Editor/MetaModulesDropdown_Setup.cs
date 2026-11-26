#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Meta.Tools;
using Meta.EditorTools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Builds the top-left Meta Modules dropdown:
    ///
    ///   Meta_Modules_DropdownHUD          Canvas, sortingOrder 500, safe-area child
    ///     SafeArea
    ///       ChromeButton                  "MODULES  v" - always visible
    ///       DropdownBlocker               invisible full-screen click-away layer (open only)
    ///       DropdownPanel                 MiniMap / DayNight / CameraForce / Close All
    ///
    /// Menu: Tools/Meta/Modules Dropdown/Setup In Scene.
    /// Setup also repairs the three module control cards when their rigs exist, then hides
    /// every card so a scene ships with only the dropdown chrome visible.
    /// </summary>
    public static class MetaModulesDropdown_Setup
    {
        public const string HUD_NAME = MetaModulesPanelSwitcher.ROOT_NAME;

        private const string SAFE_AREA_NAME = "SafeArea";
        private const string CHROME_NAME = "ChromeButton";
        private const string BLOCKER_NAME = "DropdownBlocker";
        private const string PANEL_NAME = "DropdownPanel";
        private const string TEXT_NAME = "Text";

        private const int CANVAS_SORT_ORDER = 500;
        private const float EDGE = 24f;
        private const float CHROME_WIDTH = 300f;
        private const float CHROME_HEIGHT = 72f;
        private const float ROW_HEIGHT = 64f;
        private const float PANEL_WIDTH = 300f;
        private const float PANEL_HEIGHT = 316f;
        private const int TITLE_SIZE = 24;

        // ------------------------------------------------------------------ menus

        [MenuItem("Tools/Meta/Modules Dropdown/Setup In Scene", priority = 60)]
        public static void SetupInScene()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[MetaModulesDropdown] Setup only in Edit mode. Exit Play first.");
                return;
            }

            // Repair / create the three control cards only where their module rig already exists.
            EnsureModuleCards();

            var canvasGO = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
            if (canvasGO == null)
            {
                canvasGO = new GameObject(HUD_NAME);
                Undo.RegisterCreatedObjectUndo(canvasGO, "Create Meta Modules Dropdown HUD");
                Debug.Log($"[MetaModulesDropdown] Created '{HUD_NAME}'.");
            }

            ConfigureCanvas(canvasGO);
            if (canvasGO.GetComponent<Canvas>() == null)
            {
                Debug.LogError("[MetaModulesDropdown] Setup aborted: the HUD root is not a Canvas.");
                return;
            }

            EnsureEventSystem();

            var safeArea = EnsureSafeArea(canvasGO.transform);
            if (safeArea == null)
            {
                Debug.LogError("[MetaModulesDropdown] SafeArea could not be created - setup aborted.");
                return;
            }

            Button chrome = EnsureChrome(safeArea);
            Button blocker = EnsureBlocker(safeArea);
            GameObject panel = EnsurePanel(safeArea);
            EnsureOptions(panel, out Button[] moduleButtons, out Text[] moduleLabels,
                out Button closeButton, out Text closeLabel);

            // Keep deterministic draw/raycast order: chrome, blocker, panel.
            chrome.transform.SetSiblingIndex(0);
            blocker.transform.SetSiblingIndex(1);
            panel.transform.SetSiblingIndex(2);

            var switcher = canvasGO.GetComponent<MetaModulesPanelSwitcher>();
            if (switcher == null)
                switcher = Undo.AddComponent<MetaModulesPanelSwitcher>(canvasGO);
            switcher.enabled = true;

            MiniMap_ControlPanel_Setup.SetField(switcher, "_safeArea", safeArea);
            MiniMap_ControlPanel_Setup.SetField(switcher, "_dropdownPanel", panel);
            MiniMap_ControlPanel_Setup.SetField(switcher, "_dropdownBlocker", blocker.gameObject);
            MiniMap_ControlPanel_Setup.SetField(switcher, "_chromeLabel",
                chrome.GetComponentInChildren<Text>(true));
            MiniMap_ControlPanel_Setup.SetField(switcher, "_optionLabels", moduleLabels);

            // Persistent listeners: survive Edit -> Play and repeated Setup runs.
            Wire(chrome, switcher, nameof(MetaModulesPanelSwitcher.ToggleDropdown), switcher.ToggleDropdown);
            Wire(blocker, switcher, nameof(MetaModulesPanelSwitcher.CloseDropdown), switcher.CloseDropdown);
            Wire(moduleButtons[0], switcher, nameof(MetaModulesPanelSwitcher.ShowMiniMap), switcher.ShowMiniMap);
            Wire(moduleButtons[1], switcher, nameof(MetaModulesPanelSwitcher.ShowDayNight), switcher.ShowDayNight);
            Wire(moduleButtons[2], switcher, nameof(MetaModulesPanelSwitcher.ShowCameraForce), switcher.ShowCameraForce);
            Wire(closeButton, switcher, nameof(MetaModulesPanelSwitcher.HideAll), switcher.HideAll);

            // Default: chrome visible, dropdown closed, all three module cards hidden.
            canvasGO.SetActive(true);
            safeArea.gameObject.SetActive(true);
            chrome.gameObject.SetActive(true);
            switcher.HideAll();
            EnsureMiniMapSceneDefaultOff();
            EditorUtility.SetDirty(switcher);

            Selection.activeGameObject = canvasGO;
            MarkSceneDirty();

            Debug.Log("[MetaModulesDropdown] Setup complete: 'Meta_Modules_DropdownHUD' (Canvas 500) " +
                      "with safe-area chrome visible and MiniMap / DayNight / CameraForce panels hidden. " +
                      "Play -> MODULES -> pick one module.");
        }

        [MenuItem("Tools/Meta/Modules Dropdown/Show Dropdown Chrome", priority = 61)]
        private static void ShowChrome() => SetChrome(true);

        [MenuItem("Tools/Meta/Modules Dropdown/Hide Dropdown Chrome", priority = 62)]
        private static void HideChrome() => SetChrome(false);

        [MenuItem("Tools/Meta/Modules Dropdown/Hide All Module Panels", priority = 63)]
        private static void HideAllFromDropdownMenu() => HideAllModules();

        [MenuItem("Tools/Meta/Hide All Module Panels", priority = 149)]
        private static void HideAllFromRootMenu() => HideAllModules();

        // ------------------------------------------------------------------ setup helpers

        /// <summary>
        /// Repairs or creates a module control card ONLY when that module's rig exists.
        /// Scenes without a rig are not polluted with dead control cards.
        /// </summary>
        private static void EnsureModuleCards()
        {
            if (MetaModuleTestUI_Menus.FindObject(MiniMap_TestRigCreator.RIG_ROOT_NAME) != null)
                MiniMap_ControlPanel_Setup.BuildOrSelect(false);
            else
                Debug.Log("[MetaModulesDropdown] MiniMap rig not found - run Tools/Meta/MiniMap/Setup to add it.");

            if (MetaModuleTestUI_Menus.FindObject(DayNight_TestRigCreator.RIG_ROOT_NAME) != null)
                DayNight_ControlPanel_Setup.BuildOrSelect(false);
            else
                Debug.Log("[MetaModulesDropdown] DayNight rig not found - run Tools/Meta/DayNight/Setup to add it.");

            if (MetaModuleTestUI_Menus.FindObject(CameraForceRender_TestRigCreator.RIG_ROOT_NAME) != null)
                CameraForce_ControlPanel_Setup.BuildOrSelect(false);
            else
                Debug.Log("[MetaModulesDropdown] CameraForce rig not found - run Tools/Meta/CameraForce/Setup to add it.");
        }

        /// <summary>Keeps the scene-serialized radar default OFF even after a repair left it stale.</summary>
        private static void EnsureMiniMapSceneDefaultOff()
        {
            var mini = UnityEngine.Object.FindFirstObjectByType<Meta_MiniMap>(
                FindObjectsInactive.Include);
            if (mini == null) return;

            bool changed = false;
            if (mini.IsEnabled)
            {
                mini.IsEnabled = false;
                changed = true;
            }

            if (mini.MiniMapCamera != null && mini.MiniMapCamera.enabled)
            {
                mini.MiniMapCamera.enabled = false;
                changed = true;
            }

            if (!changed) return;

            EditorUtility.SetDirty(mini);
            Debug.Log("[MetaModulesDropdown] MiniMap scene default forced OFF (IsEnabled=false, camera disabled).");
        }

        private static void ConfigureCanvas(GameObject canvasGO)
        {
            var canvas = canvasGO.GetComponent<Canvas>();
            if (canvas == null)
                canvas = Undo.AddComponent<Canvas>(canvasGO);

            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CANVAS_SORT_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = Undo.AddComponent<CanvasScaler>(canvasGO);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (canvasGO.GetComponent<GraphicRaycaster>() == null)
                Undo.AddComponent<GraphicRaycaster>(canvasGO);
        }

        private static RectTransform EnsureSafeArea(Transform canvas)
        {
            Transform child = canvas.Find(SAFE_AREA_NAME);
            if (child == null)
            {
                var go = new GameObject(SAFE_AREA_NAME, typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(go, "Create Modules Safe Area");
                go.transform.SetParent(canvas, false);
                child = go.transform;
            }

            var rect = child.GetComponent<RectTransform>();
            if (rect == null)
            {
                Debug.LogError($"[MetaModulesDropdown] '{SAFE_AREA_NAME}' has no RectTransform.");
                return null;
            }

            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            child.gameObject.SetActive(true);
            return rect;
        }

        private static Button EnsureChrome(RectTransform safeArea)
        {
            GameObject go = EnsureChild(safeArea, CHROME_NAME, "Create Modules Dropdown Chrome");

            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(EDGE, -EDGE);
            rect.sizeDelta = new Vector2(CHROME_WIDTH, CHROME_HEIGHT);

            var img = EnsureComponent<Image>(go);
            img.color = Meta_HudStyle.ButtonNormal;
            img.raycastTarget = true;

            var outline = EnsureComponent<Outline>(go);
            outline.effectColor = Meta_HudStyle.AccentSoft;
            outline.effectDistance = new Vector2(1f, -1f);

            var btn = EnsureComponent<Button>(go);
            btn.targetGraphic = img;
            ApplyButtonColors(btn);

            var label = EnsureText(go.transform, TEXT_NAME, "MODULES  v", TITLE_SIZE,
                Meta_HudStyle.TextPrimary, TextAnchor.MiddleCenter);
            label.fontStyle = FontStyle.Bold;

            return btn;
        }

        private static Button EnsureBlocker(RectTransform safeArea)
        {
            GameObject go = EnsureChild(safeArea, BLOCKER_NAME, "Create Modules Dropdown Blocker");

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var img = EnsureComponent<Image>(go);
            img.color = new Color(0f, 0f, 0f, 0f);   // invisible but raycastable
            img.raycastTarget = true;

            var btn = EnsureComponent<Button>(go);
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;

            go.SetActive(false);   // closed by default
            return btn;
        }

        private static GameObject EnsurePanel(RectTransform safeArea)
        {
            GameObject go = EnsureChild(safeArea, PANEL_NAME, "Create Modules Dropdown Panel");

            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(EDGE, -(EDGE + CHROME_HEIGHT + 12f));
            rect.sizeDelta = new Vector2(PANEL_WIDTH, PANEL_HEIGHT);

            var img = EnsureComponent<Image>(go);
            img.color = Meta_HudStyle.PanelBG;
            img.raycastTarget = true;

            var outline = EnsureComponent<Outline>(go);
            outline.effectColor = new Color(0.16f, 0.19f, 0.24f, 0.85f);
            outline.effectDistance = new Vector2(1f, -1f);

            var layout = EnsureComponent<VerticalLayoutGroup>(go);
            layout.spacing = 8f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            go.SetActive(false);   // closed by default
            return go;
        }

        private static void EnsureOptions(GameObject panel,
            out Button[] moduleButtons, out Text[] moduleLabels,
            out Button closeButton, out Text closeLabel)
        {
            moduleButtons = new Button[3];
            moduleLabels = new Text[3];

            moduleButtons[0] = EnsureOption(panel.transform, "MiniMap", Meta_HudStyle.TextPrimary, out moduleLabels[0]);
            moduleButtons[1] = EnsureOption(panel.transform, "DayNight", Meta_HudStyle.TextPrimary, out moduleLabels[1]);
            moduleButtons[2] = EnsureOption(panel.transform, "CameraForce", Meta_HudStyle.TextPrimary, out moduleLabels[2]);
            closeButton = EnsureOption(panel.transform, "Close All", Meta_HudStyle.TextMuted, out closeLabel);

            // Layout order = list order.
            moduleButtons[0].transform.SetSiblingIndex(0);
            moduleButtons[1].transform.SetSiblingIndex(1);
            moduleButtons[2].transform.SetSiblingIndex(2);
            closeButton.transform.SetSiblingIndex(3);
        }

        private static Button EnsureOption(Transform parent, string name, Color labelColor, out Text label)
        {
            GameObject go = EnsureChild(parent, name, "Create Module Option " + name);

            var img = EnsureComponent<Image>(go);
            img.color = Meta_HudStyle.ButtonNormal;
            img.raycastTarget = true;

            var btn = EnsureComponent<Button>(go);
            btn.targetGraphic = img;
            ApplyButtonColors(btn);

            var le = EnsureComponent<LayoutElement>(go);
            le.preferredHeight = ROW_HEIGHT;
            le.minHeight = ROW_HEIGHT;
            le.flexibleWidth = 1f;

            label = EnsureText(go.transform, TEXT_NAME, name, TITLE_SIZE, labelColor, TextAnchor.MiddleCenter);
            label.fontStyle = FontStyle.Bold;

            return btn;
        }

        // ------------------------------------------------------------------ menu actions

        private static void SetChrome(bool visible)
        {
            var hud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
            if (hud == null)
            {
                Debug.LogError("[MetaModulesDropdown] No dropdown HUD in this scene. " +
                               "Run Tools/Meta/Modules Dropdown/Setup In Scene first.");
                return;
            }

            hud.SetActive(visible);
            if (visible)
                hud.GetComponent<MetaModulesPanelSwitcher>()?.CloseDropdown();

            MarkSceneDirty();
            Debug.Log(visible
                ? "[MetaModulesDropdown] Dropdown chrome shown."
                : "[MetaModulesDropdown] Dropdown chrome hidden (module panels are unchanged).");
        }

        private static void HideAllModules()
        {
            var switcher = FindSwitcher();
            if (switcher != null)
            {
                switcher.HideAll();
            }
            else
            {
                HideRoot(MetaModulesPanelSwitcher.MINIMAP_HUD);
                HideRoot(MetaModulesPanelSwitcher.MINIMAP_RUNNER);
                HideRoot(MetaModulesPanelSwitcher.DAYNIGHT_HUD);
                HideRoot(MetaModulesPanelSwitcher.DAYNIGHT_RUNNER);
                HideRoot(MetaModulesPanelSwitcher.CAMERAFORCE_HUD);
                HideRoot(MetaModulesPanelSwitcher.CAMERAFORCE_RUNNER);
            }

            MarkSceneDirty();
            Debug.Log("[MetaModulesDropdown] All module panels hidden.");
        }

        private static void HideRoot(string name)
        {
            var go = MetaModulesPanelSwitcher.FindSceneObject(name);
            if (go != null && go.activeSelf) go.SetActive(false);
        }

        private static MetaModulesPanelSwitcher FindSwitcher()
        {
            var hud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
            return hud != null ? hud.GetComponent<MetaModulesPanelSwitcher>() : null;
        }

        // ------------------------------------------------------------------ small helpers

        private static GameObject EnsureChild(Transform parent, string name, string undoLabel)
        {
            Transform child = parent.Find(name);
            if (child != null) return child.gameObject;

            var go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, undoLabel);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            var component = go.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(go);
        }

        private static Text EnsureText(Transform parent, string name, string text,
            int size, Color color, TextAnchor align)
        {
            GameObject go = EnsureChild(parent, name, "Create " + name);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var txt = EnsureComponent<Text>(go);
            txt.text = text;
            txt.font = MiniMap_ControlPanel_Setup.GetFont();
            txt.fontSize = size;
            txt.color = color;
            txt.alignment = align;
            txt.raycastTarget = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Truncate;
            return txt;
        }

        private static void ApplyButtonColors(Button button)
        {
            if (button == null) return;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Meta_HudStyle.Tint(Meta_HudStyle.ButtonHover);
            colors.pressedColor = Meta_HudStyle.Tint(new Color(0.08f, 0.10f, 0.15f, 1f));
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.06f;
            button.colors = colors;
        }

        /// <summary>Adds exactly one persistent listener for our method (idempotent on re-run).</summary>
        private static void Wire(Button button, MetaModulesPanelSwitcher switcher,
            string methodName, UnityAction action)
        {
            if (button == null || switcher == null || action == null) return;

            var evt = button.onClick;
            for (int i = evt.GetPersistentEventCount() - 1; i >= 0; i--)
                if (evt.GetPersistentMethodName(i) == methodName)
                    UnityEventTools.RemovePersistentListener(evt, i);

            UnityEventTools.AddVoidPersistentListener(evt, action);
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>(
                    FindObjectsInactive.Include) != null)
                return;

            var es = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            var inputMod = System.Type.GetType(
                    "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
                ?? System.Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
            if (inputMod != null) es.AddComponent(inputMod);
        }

        private static void MarkSceneDirty()
        {
            if (Application.isPlaying) return;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }
    }
}
#endif
