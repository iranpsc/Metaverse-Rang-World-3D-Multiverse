#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Meta.Tools;
using Meta.EditorTools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Creates a MiniMap-only Play-mode control panel (ON/OFF + status).
    /// Invoked by Tools/Meta/MiniMap/Setup - no separate menu item.
    /// </summary>
    public static class MiniMap_ControlPanel_Setup
    {
        internal const string ROOT_NAME = "Meta_MiniMap_ControlRunner";
        internal const string HUD_NAME = "Meta_MiniMap_ControlHUD";
        private const string PANEL_NAME = "Meta_MiniMap_ControlPanel";
        private const string ROW_NAME = "ButtonsRow";

        /// <summary>Called by Tools/Meta/MiniMap/Setup. Selects existing, otherwise builds.</summary>
        internal static void BuildOrSelect(bool forceRecreate)
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[MiniMapControl] Setup only in Edit mode. Exit Play first.");
                return;
            }

            if (forceRecreate)
            {
                var oldRoot = GameObject.Find(ROOT_NAME);
                var oldHud = GameObject.Find(HUD_NAME);
                if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);
                if (oldHud != null) Undo.DestroyObjectImmediate(oldHud);
            }

            var root = GameObject.Find(ROOT_NAME);
            if (root != null)
            {
                RepairRunner(root);
                Selection.activeGameObject = root;
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log("[MiniMapControl] Control panel already present - re-wired to the real Meta_MiniMap rig.");
                return;
            }

            root = new GameObject(ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(root, "Create MiniMap Control Runner");

            var panelTool = Undo.AddComponent<MiniMap_ControlPanel>(root);
            panelTool.ButtonsWiredExternally = true;

            CreateHUD(root);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[MiniMapControl] Created {ROOT_NAME}. Play -> use ON/OFF.");
        }

        /// <summary>
        /// (Re)wires an existing runner: buttons, status label and — above all — the Meta_MiniMap
        /// instance, which must be the one that owns the rig (never the empty ghost duplicate).
        /// </summary>
        private static void RepairRunner(GameObject root)
        {
            var runner = root.GetComponent<MiniMap_ControlPanel>();
            if (runner == null)
            {
                runner = Undo.AddComponent<MiniMap_ControlPanel>(root);
                runner.ButtonsWiredExternally = true;
            }

            runner.ButtonsWiredExternally = true;

            var hudCanvas = GameObject.Find(HUD_NAME);
            var panelTransform = hudCanvas != null ? hudCanvas.transform.Find(PANEL_NAME) : null;

            if (panelTransform != null)
            {
                SetField(runner, "_onButton", panelTransform.Find("PrimaryRow/ON")?.GetComponent<Button>());
                SetField(runner, "_offButton", panelTransform.Find("PrimaryRow/OFF")?.GetComponent<Button>());
                SetField(runner, "_zoomInButton", panelTransform.Find("SecondaryRow/Zoom In")?.GetComponent<Button>());
                SetField(runner, "_zoomOutButton", panelTransform.Find("SecondaryRow/Zoom Out")?.GetComponent<Button>());
                SetField(runner, "_statusText", panelTransform.Find("StatusText")?.GetComponent<Text>());
            }
            else
            {
                Debug.LogWarning("[MiniMapControl] Control panel UI not found - create one by deleting " +
                                 ROOT_NAME + " and running MiniMap Setup again.");
            }

            SetField(runner, "_miniMap", FindRigMiniMapInstance());

            RefreshStatusLabel(runner);
        }

        /// <summary>
        /// Prepares the status label with the wired instance's REAL state so the label is correct
        /// before the first Play tick (the runtime panel keeps it live from then on).
        /// </summary>
        private static void RefreshStatusLabel(MiniMap_ControlPanel runner)
        {
            var statusField = typeof(MiniMap_ControlPanel).GetField("_statusText",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var statusText = statusField?.GetValue(runner) as Text;
            var rigMap = FindRigMiniMapInstance();
            if (statusText != null && rigMap != null)
            {
                string tgt = rigMap.Target != null ? rigMap.Target.name : "waiting for local player";
                statusText.text = $"IsEnabled: {rigMap.IsEnabled}\nTarget: {tgt}\n" +
                                  $"Camera: {(rigMap.MiniMapCamera != null ? rigMap.MiniMapCamera.enabled.ToString() : "NULL")}\n" +
                                  $"Platform: {rigMap.CurrentPlatform}";
            }
        }

        /// <summary>The Meta_MiniMap that owns a rig; falls back to any instance when none has refs.</summary>
        internal static Meta_MiniMap FindRigMiniMapInstance()
        {
            return MiniMap_ControlPanel.BestMiniMapInstance()
                   ?? Object.FindFirstObjectByType<Meta_MiniMap>(FindObjectsInactive.Include);
        }

        private static void CreateHUD(GameObject root)
        {
            var canvasGO = GameObject.Find(HUD_NAME);
            if (canvasGO == null)
            {
                canvasGO = new GameObject(HUD_NAME);
                Undo.RegisterCreatedObjectUndo(canvasGO, "Create MiniMap Control HUD Canvas");
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 260;   // above the radar chrome, below system UI (room list stays clear)
                canvasGO.AddComponent<CanvasScaler>();
                canvasGO.AddComponent<GraphicRaycaster>();
            }

            if (UnityEngine.EventSystems.EventSystem.current == null
                && Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>(FindObjectsInactive.Include) == null)
            {
                var es = new GameObject("EventSystem");
                Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
                es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                var inputMod = System.Type.GetType(
                    "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
                    ?? System.Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
                if (inputMod != null) es.AddComponent(inputMod);
            }

            // ---- Panel: dark glass background lives ON the panel (no extra child,
            //      otherwise the VerticalLayoutGroup would treat it as a layout row) ----
            var panelGO = new GameObject(PANEL_NAME);
            Undo.RegisterCreatedObjectUndo(panelGO, "Create MiniMap Control Panel");
            panelGO.transform.SetParent(canvasGO.transform, false);
            var panelRect = panelGO.AddComponent<RectTransform>();
            // BOTTOM-LEFT stack, slot 0 (DayNight = slot 1, CameraForce = slot 2)
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0f, 0f);
            panelRect.pivot = new Vector2(0f, 0f);
            panelRect.anchoredPosition = new Vector2(20f, 20f);
            panelRect.sizeDelta = new Vector2(220f, 130f);

            ApplyDarkGlass(panelGO);

            // ONE layout group per RectTransform (Unity returns null for a second one)
            var layout = panelGO.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(16, 16, 12, 16);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateLabel(panelGO.transform, "Title", "MINIMAP", 13, FontStyle.Bold);
            title.color = Meta_HudStyle.TextMuted;
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 20f;

            // PRIMARY row: ON | OFF (same size chips)
            var row1 = NewRow(panelGO.transform, "PrimaryRow");
            CreateButton(row1.transform, "ON", "ON", true);
            CreateButton(row1.transform, "OFF", "OFF", false);

            // SECONDARY row: module actions (Zoom only - FakePlayer/Respawn stay off the HUD)
            var row2 = NewRow(panelGO.transform, "SecondaryRow");
            CreateButton(row2.transform, "Zoom In", "Zoom +", false);
            CreateButton(row2.transform, "Zoom Out", "Zoom -", false);

            // Status text
            var statusGO = new GameObject("StatusText");
            Undo.RegisterCreatedObjectUndo(statusGO, "StatusText");
            statusGO.transform.SetParent(panelGO.transform, false);
            var statusRect = statusGO.AddComponent<RectTransform>();
            statusRect.anchorMin = Vector2.zero;
            statusRect.anchorMax = Vector2.one;
            statusRect.offsetMin = Vector2.zero;
            statusRect.offsetMax = Vector2.zero;
            var statusText = statusGO.AddComponent<Text>();
            statusText.text = "Click ON to start...";
            statusText.font = GetFont();
            statusText.fontSize = 11;
            statusText.color = Meta_HudStyle.TextMuted;
            statusText.alignment = TextAnchor.UpperLeft;
            statusText.raycastTarget = false;

            // ---- Wire the runtime component ----
            var runner = root.GetComponent<MiniMap_ControlPanel>();
            var onBtn = panelGO.transform.Find("PrimaryRow/ON")?.GetComponent<Button>();
            var offBtn = panelGO.transform.Find("PrimaryRow/OFF")?.GetComponent<Button>();
            var zoomIn = panelGO.transform.Find("SecondaryRow/Zoom In")?.GetComponent<Button>();
            var zoomOut = panelGO.transform.Find("SecondaryRow/Zoom Out")?.GetComponent<Button>();

            if (runner != null && onBtn != null && offBtn != null && zoomIn != null && zoomOut != null)
            {
                // Persistent listeners (survive Edit -> Play)
                UnityEventTools.AddVoidPersistentListener(onBtn.onClick, runner.OnClicked);
                UnityEventTools.AddVoidPersistentListener(offBtn.onClick, runner.OffClicked);
                UnityEventTools.AddVoidPersistentListener(zoomIn.onClick, runner.ZoomInClicked);
                UnityEventTools.AddVoidPersistentListener(zoomOut.onClick, runner.ZoomOutClicked);

                SetField(runner, "_onButton", onBtn);
                SetField(runner, "_offButton", offBtn);
                SetField(runner, "_zoomInButton", zoomIn);
                SetField(runner, "_zoomOutButton", zoomOut);
                SetField(runner, "_statusText", statusText);
                // NB: must be the instance that OWNS the rig, never a bare auto-added duplicate.
                SetField(runner, "_miniMap", FindRigMiniMapInstance());

                RefreshStatusLabel(runner);
            }
            else
            {
                Debug.LogWarning("[MiniMapControl] Wiring skipped (buttons or runner missing).");
            }

            Undo.RegisterCreatedObjectUndo(panelGO, "Create MiniMap Control Panel Complete");
        }

        /// <summary>Creates a horizontal button row (one LayoutGroup per RectTransform).</summary>
        private static GameObject NewRow(Transform parent, string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create MiniMap " + name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 28f;
            le.minHeight = 28f;
            return go;
        }

        /// <summary>Dark glass background + 1px border, applied to the panel itself.</summary>
        internal static void ApplyDarkGlass(GameObject go)
        {
            var img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            img.sprite = Meta_HudStyle.Rounded(96, 14);
            img.type = Image.Type.Sliced;
            img.color = Meta_HudStyle.PanelBG;
            img.raycastTarget = false;

            var outline = go.GetComponent<Outline>();
            if (outline == null) outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.16f, 0.19f, 0.24f, 0.85f);
            outline.effectDistance = new Vector2(1f, -1f);
        }

        internal static void SetField(object target, string fieldName, object value)
        {
            var ft = target.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            ft?.SetValue(target, value);
        }

        /// <summary>
        /// Scenes must have EXACTLY one EventSystem. The MiniMap HUD Setup used to add
        /// a second one beside the scene's own - Unity then spams
        /// "There are 2 event systems in the scene." every frame. Only plain-named duplicates are
        /// removed; custom systems are never touched.
        /// </summary>
        internal static void CleanupExtraEventSystems()
        {
            var all = Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length <= 1) return;

            UnityEngine.EventSystems.EventSystem keep = null;
            foreach (var es in all)
                if (es != null && es.isActiveAndEnabled) { keep = es; break; }
            if (keep == null) keep = all[0];

            foreach (var es in all)
            {
                if (es == null || es == keep) continue;
                if (es.gameObject.name != "EventSystem") continue;   // never destroy custom-named systems

                Debug.Log($"[MiniMapControl] Removed duplicate EventSystem '{es.gameObject.name}' " +
                          "(scenes must have exactly one).");
                Undo.DestroyObjectImmediate(es.gameObject);
            }
        }

        internal static Font GetFont()
        {
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                   ?? (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
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
            txt.font = GetFont();
            txt.fontSize = size;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.fontStyle = style;
            txt.raycastTarget = false;
            return txt;
        }

        private static void CreateButton(Transform parent, string name, string label, bool accent)
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

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 28f;
            le.minHeight = 28f;
            le.flexibleWidth = 1f;

            var labelGO = new GameObject("Text");
            labelGO.transform.SetParent(go.transform, false);
            var labelRect = labelGO.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var txt = labelGO.AddComponent<Text>();
            txt.text = label;
            txt.font = GetFont();
            txt.fontSize = Meta_HudStyle.BodySize;
            txt.color = accent ? Meta_HudStyle.Accent : Meta_HudStyle.TextMuted;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
        }
    }
}
#endif