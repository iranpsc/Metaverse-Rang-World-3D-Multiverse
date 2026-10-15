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
        private static readonly Vector2 CARD_POS = new Vector2(20f, 316f);   // bottom-left stack, slot 2
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
                var oldRoot = GameObject.Find(ROOT_NAME);
                var oldHud = GameObject.Find(HUD_NAME);
                if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);
                if (oldHud != null) Undo.DestroyObjectImmediate(oldHud);
            }

            var root = GameObject.Find(ROOT_NAME);
            if (root != null)
            {
                Selection.activeGameObject = root;
                Debug.Log($"[CameraForceControl] Control card already present: {ROOT_NAME}. " +
                          "Alt+click Setup to rebuild it with the new labels.");
                return;
            }

            root = new GameObject(ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(root, "Create CameraForce Control Runner");

            var panelTool = Undo.AddComponent<CameraForce_ControlPanel>(root);
            panelTool.ButtonsWiredExternally = true;

            CreateHUD(root);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[CameraForceControl] Created {ROOT_NAME}. " +
                      "Card: Enable / Disable + Default (0) / Alt (1), each with a tooltip.");
        }

        private static void CreateHUD(GameObject root)
        {
            var canvasGO = GameObject.Find(HUD_NAME);
            if (canvasGO == null)
            {
                canvasGO = new GameObject(HUD_NAME);
                Undo.RegisterCreatedObjectUndo(canvasGO, "Create CameraForce Control HUD Canvas");
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 300;
                canvasGO.AddComponent<CanvasScaler>();
                canvasGO.AddComponent<GraphicRaycaster>();
            }

            // Never create a second EventSystem.
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
            Undo.RegisterCreatedObjectUndo(panelGO, "Create CameraForce Control Panel");
            panelGO.transform.SetParent(canvasGO.transform, false);
            var panelRect = panelGO.AddComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0f, 0f);
            panelRect.pivot = new Vector2(0f, 0f);
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
            var enableBtn = CreateButton(row1.transform, BTN_ENABLE, true,
                "Turn forced-renderer mode ON: this camera always uses the renderer you choose below.");
            var disableBtn = CreateButton(row1.transform, BTN_DISABLE, false,
                "Stop forcing a renderer. The camera keeps whatever renderer it already had.");

            // 4) Secondary row - which renderer, in words.
            var row2 = CreateButtonRow(panelGO.transform, ROW2_NAME);
            var r0 = CreateButton(row2.transform, BTN_RENDERER_0, false,
                "Use URP renderer 0 - the project's default renderer.");
            var r1 = CreateButton(row2.transform, BTN_RENDERER_1, false,
                "Use URP renderer 1. Only differs if the URP asset defines a second renderer.");

            // 5) Status - plain sentence, not "Enabled: True".
            var statusText = CreateLabel(panelGO.transform, "StatusText",
                "Status: off - press Enable to start.", 11, FontStyle.Normal);
            statusText.color = Meta_HudStyle.TextMuted;
            statusText.alignment = TextAnchor.UpperLeft;
            statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            SetPreferredHeight(statusText.gameObject, 30f);

            var runner = root.GetComponent<CameraForce_ControlPanel>();

            if (runner != null && enableBtn != null && disableBtn != null && r0 != null && r1 != null)
            {
                UnityEventTools.AddVoidPersistentListener(enableBtn.onClick, runner.OnClicked);
                UnityEventTools.AddVoidPersistentListener(disableBtn.onClick, runner.OffClicked);
                UnityEventTools.AddVoidPersistentListener(r0.onClick, runner.ApplyIndex0Clicked);
                UnityEventTools.AddVoidPersistentListener(r1.onClick, runner.ApplyIndex1Clicked);

                MiniMap_ControlPanel_Setup.SetField(runner, "_onButton", enableBtn);
                MiniMap_ControlPanel_Setup.SetField(runner, "_offButton", disableBtn);
                MiniMap_ControlPanel_Setup.SetField(runner, "_applyIndex0Button", r0);
                MiniMap_ControlPanel_Setup.SetField(runner, "_applyIndex1Button", r1);
                MiniMap_ControlPanel_Setup.SetField(runner, "_statusText", statusText);
                MiniMap_ControlPanel_Setup.SetField(runner, "_cameraForce", Object.FindFirstObjectByType<CameraForceRenderByIndex>());
                MiniMap_ControlPanel_Setup.SetField(runner, "_camera", Object.FindFirstObjectByType<Camera>());
            }
            else
            {
                Debug.LogWarning("[CameraForceControl] Wiring skipped (buttons or runner missing).");
            }

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