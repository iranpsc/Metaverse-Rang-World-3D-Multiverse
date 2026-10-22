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
    /// Creates a DayNight-only Play-mode control panel (ON/OFF/Reset + status).
    /// Invoked by Tools/Meta/DayNight/Setup - no separate menu item.
    /// </summary>
    public static class DayNight_ControlPanel_Setup
    {
        internal const string ROOT_NAME = "Meta_DayNight_ControlRunner";
        internal const string HUD_NAME = "Meta_DayNight_ControlHUD";
        private const string PANEL_NAME = "Meta_DayNight_ControlPanel";
        private const string ROW_NAME = "ButtonsRow";

        /// <summary>Called by Tools/Meta/DayNight/Setup. Selects existing, otherwise builds.</summary>
        internal static void BuildOrSelect(bool forceRecreate)
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[DayNightControl] Setup only in Edit mode. Exit Play first.");
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
                Selection.activeGameObject = root;
                Debug.Log($"[DayNightControl] Control panel already present: {ROOT_NAME}");
                return;
            }

            root = new GameObject(ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(root, "Create DayNight Control Runner");

            var panelTool = Undo.AddComponent<DayNight_ControlPanel>(root);
            panelTool.ButtonsWiredExternally = true;

            CreateHUD(root);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[DayNightControl] Created {ROOT_NAME}. Play -> use ON/OFF/Reset.");
        }

        private static void CreateHUD(GameObject root)
        {
            var canvasGO = GameObject.Find(HUD_NAME);
            if (canvasGO == null)
            {
                canvasGO = new GameObject(HUD_NAME);
                Undo.RegisterCreatedObjectUndo(canvasGO, "Create DayNight Control HUD Canvas");
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 300;
                canvasGO.AddComponent<CanvasScaler>();
                canvasGO.AddComponent<GraphicRaycaster>();
            }

            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                var es = new GameObject("EventSystem");
                Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
                es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                var inputMod = System.Type.GetType(
                    "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
                    ?? System.Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
                if (inputMod != null) es.AddComponent(inputMod);
            }

            var panelGO = new GameObject(PANEL_NAME);
            Undo.RegisterCreatedObjectUndo(panelGO, "Create DayNight Control Panel");
            panelGO.transform.SetParent(canvasGO.transform, false);
            var panelRect = panelGO.AddComponent<RectTransform>();
            // BOTTOM-LEFT stack, slot 1 (above MiniMap, below CameraForce)
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0f, 0f);
            panelRect.pivot = new Vector2(0f, 0f);
            panelRect.anchoredPosition = new Vector2(20f, 158f);
            // Taller than the other two cards: the status line now shows 5 rows (cycle / light / disc /
// time / authority mode). BOTTOM-LEFT stack slot 1, 8 px below the MiniMap card.
            // Taller than the other two cards: the status line shows 6 rows (cycle / light / disc /
            // time / authority mode / persisted place). BOTTOM-LEFT stack slot 1.
            panelRect.sizeDelta = new Vector2(220f, 190f);

            MiniMap_ControlPanel_Setup.ApplyDarkGlass(panelGO);

            var layout = panelGO.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(16, 16, 12, 16);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateLabel(panelGO.transform, "Title", "DAY / NIGHT", 13, FontStyle.Bold);
            title.color = Meta_HudStyle.TextMuted;
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 20f;

            // Buttons in their own row (one LayoutGroup per RectTransform)
            var rowGO = new GameObject(ROW_NAME);
            Undo.RegisterCreatedObjectUndo(rowGO, "Create DayNight Buttons Row");
            rowGO.transform.SetParent(panelGO.transform, false);
            rowGO.AddComponent<RectTransform>();
            var rowLayout = rowGO.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 8f;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            var rowLe = rowGO.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 28f;

            CreateButton(rowGO.transform, "ON", "ON", true);
            CreateButton(rowGO.transform, "OFF", "OFF", false);
            CreateButton(rowGO.transform, "Reset", "Reset", false);

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
            statusText.font = MiniMap_ControlPanel_Setup.GetFont();
            statusText.fontSize = 11;
            statusText.color = Meta_HudStyle.TextMuted;
            statusText.alignment = TextAnchor.UpperLeft;
            statusText.raycastTarget = false;

            var runner = root.GetComponent<DayNight_ControlPanel>();
            var onBtn = rowGO.transform.Find("ON")?.GetComponent<Button>();
            var offBtn = rowGO.transform.Find("OFF")?.GetComponent<Button>();
            var resetBtn = rowGO.transform.Find("Reset")?.GetComponent<Button>();

            if (runner != null && onBtn != null && offBtn != null && resetBtn != null)
            {
                UnityEventTools.AddVoidPersistentListener(onBtn.onClick, runner.OnClicked);
                UnityEventTools.AddVoidPersistentListener(offBtn.onClick, runner.OffClicked);
                UnityEventTools.AddVoidPersistentListener(resetBtn.onClick, runner.ResetClicked);

                MiniMap_ControlPanel_Setup.SetField(runner, "_onButton", onBtn);
                MiniMap_ControlPanel_Setup.SetField(runner, "_offButton", offBtn);
                MiniMap_ControlPanel_Setup.SetField(runner, "_resetButton", resetBtn);
                MiniMap_ControlPanel_Setup.SetField(runner, "_statusText", statusText);
                MiniMap_ControlPanel_Setup.SetField(runner, "_dayNight", Object.FindFirstObjectByType<Meta_DayNightCycle>());
            }
            else
            {
                Debug.LogWarning("[DayNightControl] Wiring skipped (buttons or runner missing).");
            }

            Undo.RegisterCreatedObjectUndo(panelGO, "Create DayNight Control Panel Complete");
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
            txt.font = MiniMap_ControlPanel_Setup.GetFont();
            txt.fontSize = Meta_HudStyle.BodySize;
            txt.color = accent ? Meta_HudStyle.Accent : Meta_HudStyle.TextMuted;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
        }
    }
}
#endif