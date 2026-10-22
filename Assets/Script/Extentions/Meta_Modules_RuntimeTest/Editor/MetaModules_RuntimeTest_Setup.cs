#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Meta.Tools;
using Meta.EditorTools;   // <- Meta_HudStyle lives here

namespace Meta.EditorTools
{
    /// <summary>
    /// Editor tool to create the Meta Modules Runtime Test HUD in the scene.
    /// Menu: Tools/Meta/Setup Runtime Module Tester
    /// </summary>
    public static class MetaModules_RuntimeTest_Setup
    {
        private const string ROOT_NAME = "Meta_Modules_TestRunner";
        private const string HUD_NAME = "Meta_Modules_TestHUD";
        private const string PANEL_NAME = "Meta_Modules_TestPanel";
        private const string RUN_BUTTON = "RunAllTestsButton";
        private const string CLEAR_BUTTON = "ClearButton";
        private const string RESULTS_NAME = "ResultsText";
        private const string MODULE_TOGGLES = "ModuleToggles";

        [MenuItem("Tools/Meta/Setup Runtime Module Tester", priority = 90)]
        public static void CreateTestRunner()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[MetaModulesTest] Setup only in Edit mode. Exit Play, run Tools/Meta/Setup Runtime Module Tester, then Play.");
                return;
            }

            var root = GameObject.Find(ROOT_NAME);
            var hud = GameObject.Find(HUD_NAME);
            if (root != null)
            {
                Selection.activeGameObject = root;
                Debug.Log($"[MetaModulesTest] Test runner already exists: {ROOT_NAME}");
                return;
            }

            root = new GameObject(ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(root, "Create Meta Modules Test Runner");

            var tester = Undo.AddComponent<MetaModules_RuntimeTester>(root);

            CreateHUD(tester);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[MetaModulesTest] Created {ROOT_NAME} with dark HUD. Play -> click RUN ALL TESTS.");
        }

        [MenuItem("Tools/Meta/Force Recreate Runtime Module Tester", priority = 91)]
        public static void ForceRecreateTestRunner()
        {
            if (Application.isPlaying)
            {
                Debug.LogError("[MetaModulesTest] Setup only in Edit mode. Exit Play, run Tools/Meta/Setup Runtime Module Tester, then Play.");
                return;
            }

            var root = GameObject.Find(ROOT_NAME);
            var hud = GameObject.Find(HUD_NAME);
            if (root != null) Undo.DestroyObjectImmediate(root);
            if (hud != null) Undo.DestroyObjectImmediate(hud);
            CreateTestRunner();
        }

        private static void CreateHUD(MetaModules_RuntimeTester tester)
        {
            // Ensure HUD canvas
            var canvasGO = GameObject.Find(HUD_NAME);
            Canvas canvas = null;
            if (canvasGO == null)
            {
                canvasGO = new GameObject(HUD_NAME);
                Undo.RegisterCreatedObjectUndo(canvasGO, "Create Test HUD Canvas");
                canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 300;
                canvasGO.AddComponent<CanvasScaler>();
                canvasGO.AddComponent<GraphicRaycaster>();
            }
            else
            {
                canvas = canvasGO.GetComponent<Canvas>();
            }

            // EventSystem
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

            // Panel (bottom-center, dark glass)
            var panelGO = new GameObject(PANEL_NAME);
            Undo.RegisterCreatedObjectUndo(panelGO, "Create Test Panel");
            panelGO.transform.SetParent(canvasGO.transform, false);
            var panelRect = panelGO.AddComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0f);
            panelRect.pivot = new Vector2(0.5f, 0f);
            panelRect.anchoredPosition = new Vector2(0f, 20f);
            panelRect.sizeDelta = new Vector2(480f, 380f);

            // Try to use Meta_HudStyle for consistent dark-glass look; fall back to plain UI on any error
            bool styleOk = false;
            try
            {
                // Card
                var card = Meta_HudStyle.Card(
                    panelGO.transform, PANEL_NAME, new Vector2(480f, 380f),
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 20f)
                );
                // Border
                Meta_HudStyle.Border(panelGO.transform, new Vector2(480f, 380f));
                styleOk = true;
            }
            catch (Exception e)
            {
                // Fallback: plain dark panel
                var bg = panelGO.AddComponent<Image>();
                bg.color = new Color(0.055f, 0.07f, 0.094f, 0.95f);
                bg.raycastTarget = false;
                Debug.LogWarning($"[MetaModulesTest] Meta_HudStyle unavailable ({e.Message}); using fallback UI.");
            }

            var layout = panelGO.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(16, 16, 12, 16);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // Title
            var title = CreateLabel(panelGO.transform, "Title", "META MODULES TEST", 16, FontStyle.Bold);
            title.color = new Color(0.91f, 0.93f, 0.95f, 1f);
            var titleLe = title.gameObject.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 28f;

            // Module toggles row
            var togglesGO = new GameObject(MODULE_TOGGLES);
            Undo.RegisterCreatedObjectUndo(togglesGO, "Module Toggles");
            togglesGO.transform.SetParent(panelGO.transform, false);
            var togglesRect = togglesGO.AddComponent<RectTransform>();
            var togglesLayout = togglesGO.AddComponent<HorizontalLayoutGroup>();
            togglesLayout.spacing = 8f;
            togglesLayout.childAlignment = TextAnchor.MiddleCenter;
            togglesLayout.childControlWidth = true;
            togglesLayout.childControlHeight = true;
            togglesLayout.childForceExpandWidth = true;

            var miniMapToggle = CreateToggle(togglesGO.transform, "MiniMap", "MiniMap");
            var dayNightToggle = CreateToggle(togglesGO.transform, "DayNight", "DayNight");
            var cameraForceToggle = CreateToggle(togglesGO.transform, "CameraForce", "CameraForce");

            // Buttons row
            var buttonsGO = new GameObject("ButtonsRow");
            Undo.RegisterCreatedObjectUndo(buttonsGO, "Buttons Row");
            buttonsGO.transform.SetParent(panelGO.transform, false);
            var btnRect = buttonsGO.AddComponent<RectTransform>();
            var btnLayout = buttonsGO.AddComponent<HorizontalLayoutGroup>();
            btnLayout.spacing = 12f;
            btnLayout.childAlignment = TextAnchor.MiddleCenter;
            btnLayout.childControlWidth = true;
            btnLayout.childControlHeight = true;
            btnLayout.childForceExpandWidth = true;

            var runButton = CreateButton(buttonsGO.transform, RUN_BUTTON, "RUN ALL TESTS", true);
            var clearButton = CreateButton(buttonsGO.transform, CLEAR_BUTTON, "CLEAR", false);

            // Results text (scrollable readout)
            var resultsGO = new GameObject(RESULTS_NAME);
            Undo.RegisterCreatedObjectUndo(resultsGO, "Results Text");
            resultsGO.transform.SetParent(panelGO.transform, false);
            var resultsRect = resultsGO.AddComponent<RectTransform>();
            resultsRect.anchorMin = Vector2.zero;
            resultsRect.anchorMax = Vector2.one;
            resultsRect.offsetMin = Vector2.zero;
            resultsRect.offsetMax = Vector2.zero;

            var mask = resultsGO.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            var content = new GameObject("Content");
            Undo.RegisterCreatedObjectUndo(content, "Results Content");
            content.transform.SetParent(resultsGO.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero;
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, 200f);

            var resultsText = content.AddComponent<Text>();
            resultsText.text = "Click RUN ALL TESTS to begin...";
            resultsText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            resultsText.fontSize = 11;
            resultsText.color = new Color(0.55f, 0.58f, 0.63f, 1f);
            resultsText.alignment = TextAnchor.UpperLeft;
            resultsText.raycastTarget = false;

            var scroll = resultsGO.AddComponent<ScrollRect>();
            scroll.content = contentRect;
            scroll.viewport = resultsGO.AddComponent<RectTransform>();
            scroll.vertical = true;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            // Wire the runtime tester
            var runner = (MetaModules_RuntimeTester)panelGO.GetComponentInParent<MetaModules_RuntimeTester>();
            if (runner != null)
            {
                var runBtn = runButton.GetComponent<Button>();
                var clrBtn = clearButton.GetComponent<Button>();

                // Persistent listeners survive Play mode
                UnityEventTools.AddVoidPersistentListener(runBtn.onClick, runner.RunAllTests);
                UnityEventTools.AddVoidPersistentListener(clrBtn.onClick, runner.ClearResults);

                // Assign serialized fields
                var ft = typeof(MetaModules_RuntimeTester).GetField("_runAllButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ft?.SetValue(runner, runBtn);
                ft = typeof(MetaModules_RuntimeTester).GetField("_clearButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ft?.SetValue(runner, clrBtn);
                ft = typeof(MetaModules_RuntimeTester).GetField("_resultsText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ft?.SetValue(runner, resultsText);
                ft = typeof(MetaModules_RuntimeTester).GetField("_miniMapToggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ft?.SetValue(runner, miniMapToggle);
                ft = typeof(MetaModules_RuntimeTester).GetField("_dayNightToggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ft?.SetValue(runner, dayNightToggle);
                ft = typeof(MetaModules_RuntimeTester).GetField("_cameraForceToggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                ft?.SetValue(runner, cameraForceToggle);
            }

            Undo.RegisterCreatedObjectUndo(panelGO, "Create Test Panel Complete");
        }

        private static Text CreateLabel(Transform parent, string name, string text, int size, FontStyle style = FontStyle.Normal)
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
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            txt.fontSize = size;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.fontStyle = style;
            txt.raycastTarget = false;
            return txt;
        }

        private static Toggle CreateToggle(Transform parent, string name, string label)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(100f, 24f);

            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.1f, 0.12f, 0.15f, 0.8f);
            bg.raycastTarget = true;

            var tgl = go.AddComponent<Toggle>();
            tgl.targetGraphic = bg;

            var check = new GameObject("Checkmark");
            check.transform.SetParent(go.transform, false);
            var checkRect = check.AddComponent<RectTransform>();
            checkRect.anchorMin = checkRect.anchorMax = checkRect.pivot = new Vector2(0.5f, 0.5f);
            checkRect.sizeDelta = new Vector2(16f, 16f);
            var checkImg = check.AddComponent<Image>();
            checkImg.color = new Color(0.5f, 0.9f, 1f, 1f);
            tgl.graphic = checkImg;

            var lbl = new GameObject("Label");
            lbl.transform.SetParent(go.transform, false);
            var lblRect = lbl.AddComponent<RectTransform>();
            lblRect.anchorMin = Vector2.zero;
            lblRect.anchorMax = Vector2.one;
            lblRect.offsetMin = new Vector2(24f, 0f);
            lblRect.offsetMax = Vector2.zero;
            var lblText = lbl.AddComponent<Text>();
            lblText.text = label;
            lblText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            lblText.fontSize = 11;
            lblText.color = new Color(0.55f, 0.58f, 0.63f, 1f);
            lblText.alignment = TextAnchor.MiddleLeft;
            lblText.raycastTarget = false;

            tgl.isOn = true;
            return tgl;
        }

        private static Button CreateButton(Transform parent, string name, string label, bool accent)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var btn = go.AddComponent<Button>();

            var bg = go.AddComponent<Image>();
            bg.sprite = null;  // No built-in sprite dependency - use color-only Image
            bg.type = Image.Type.Sliced;
            bg.color = accent ? new Color(0.2f, 0.3f, 0.4f, 1f) : new Color(0.1f, 0.12f, 0.15f, 0.9f);
            bg.raycastTarget = true;
            btn.targetGraphic = bg;

            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.2f, 1.2f, 1.3f, 1f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.75f, 1f);
            colors.disabledColor = new Color(0.4f, 0.4f, 0.45f, 0.5f);
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
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            txt.fontSize = 12;
            txt.color = accent ? new Color(0.5f, 0.88f, 1f, 1f) : new Color(0.55f, 0.58f, 0.63f, 1f);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;

            return btn;
        }
    }
}
#endif