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
                var oldRoot = MetaModuleTestUI_Menus.FindObject(ROOT_NAME);
                var oldHud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
                if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot);
                if (oldHud != null) Undo.DestroyObjectImmediate(oldHud);
            }

            var root = MetaModuleTestUI_Menus.FindObject(ROOT_NAME);   // inactive-aware
            if (root != null)
            {
                RepairRunner(root);
                RepositionPanelBottomRight();
                Selection.activeGameObject = root;
                MetaModuleTestUI_Menus.ApplyDefaultHidden(ROOT_NAME, HUD_NAME);
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log("[DayNightControl] Control panel already present - re-wired and hidden by default (Tools/Meta/DayNight/Show Control UI).");
                return;
            }

            root = new GameObject(ROOT_NAME);
            Undo.RegisterCreatedObjectUndo(root, "Create DayNight Control Runner");

            var panelTool = Undo.AddComponent<DayNight_ControlPanel>(root);
            panelTool.ButtonsWiredExternally = true;

            CreateHUD(root);
            RepairRunner(root);   // wires the rig's SceneTool + removes any stale duplicate card

            // Test control UI must never ship visible (it would stack with the other two cards).
            MetaModuleTestUI_Menus.ApplyDefaultHidden(ROOT_NAME, HUD_NAME);

            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[DayNightControl] Created {ROOT_NAME} bottom-right, hidden by default. Play -> Meta Test burger or Tools/Meta/DayNight/Show Control UI.");
        }

        /// <summary>
        /// Repair the WIRING only (no card creation): remove the runner's RequireComponent ghost,
        /// remove stale duplicate cards, point the runner + every legacy DayNight_SceneTool at the
        /// REAL Meta_DayNightCycle, and wire the current card's buttons/status.
        /// </summary>
        private static void RepairRunner(GameObject root)
        {
            // [RequireComponent] used to auto-add a second Meta_DayNightCycle on the runner. It
            // activates with the test UI and can drive an empty cycle, so remove it in Edit mode.
            RemoveGhostCycles(root);

            var runner = root.GetComponent<DayNight_ControlPanel>();
            if (runner == null)
            {
                runner = Undo.AddComponent<DayNight_ControlPanel>(root);
                runner.ButtonsWiredExternally = true;
            }
            runner.ButtonsWiredExternally = true;

            var hud = MetaModuleTestUI_Menus.FindObject(HUD_NAME);
            Transform panelGO = DayNight_ControlPanel.FindBestPanel();
            RemoveDuplicatePanels(hud, panelGO);

            if (panelGO != null)
            {
                MiniMap_ControlPanel_Setup.SetField(runner, "_onButton", panelGO.Find(ROW_NAME + "/ON")?.GetComponent<Button>());
                MiniMap_ControlPanel_Setup.SetField(runner, "_offButton", panelGO.Find(ROW_NAME + "/OFF")?.GetComponent<Button>());
                MiniMap_ControlPanel_Setup.SetField(runner, "_resetButton", panelGO.Find(ROW_NAME + "/Reset")?.GetComponent<Button>());
                MiniMap_ControlPanel_Setup.SetField(runner, "_statusText", panelGO.Find("StatusText")?.GetComponent<Text>());
            }
            else
            {
                Debug.LogWarning("[DayNightControl] Control panel UI not found for rewiring - delete " +
                                 ROOT_NAME + " and run DayNight Setup again to rebuild it.");
            }

            // NEVER the ghost duplicate mounted on the runner root itself.
            SetRealCycle(runner, root.transform);

            // Any legacy SceneTool next to the rig (or stray in the scene) gets the same real refs.
            // The status text can point at the current card: SceneTool.Update skips writing when
            // its parent is Meta_DayNight_ControlPanel, so there is still exactly one writer.
            Transform legacyPanel = hud != null ? hud.transform.Find("Meta_DayNight_Controls") : null;
            foreach (var tool in Object.FindObjectsByType<DayNight_SceneTool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (tool == null) continue;

                var real = DayNight_ControlPanel.BestDayNightInstance(tool.transform);
                if (real == null)
                {
                    var own = tool.GetComponent<Meta_DayNightCycle>();
                    if (own != null && !DayNight_ControlPanel.IsGhostCandidate(own)) real = own;
                }
                MiniMap_ControlPanel_Setup.SetField(tool, "_dayNight", real);
                MiniMap_ControlPanel_Setup.SetField(tool, "_toggleButton", panelGO?.Find(ROW_NAME + "/ON")?.GetComponent<Button>());
                MiniMap_ControlPanel_Setup.SetField(tool, "_forceResetButton", panelGO?.Find(ROW_NAME + "/Reset")?.GetComponent<Button>());
                MiniMap_ControlPanel_Setup.SetField(tool, "_statusText",
                    legacyPanel?.Find("StatusText")?.GetComponent<Text>() ??
                    panelGO?.Find("StatusText")?.GetComponent<Text>());
                MiniMap_ControlPanel_Setup.SetField(tool, "_rotationSpeedSlider",
                    legacyPanel != null ? legacyPanel.GetComponentInChildren<Slider>(true) : null);
                EditorUtility.SetDirty(tool);
            }

            EditorUtility.SetDirty(runner);
        }

        /// <summary>Removes the auto-added ghost cycle on the runner root (Edit mode only).</summary>
        private static void RemoveGhostCycles(GameObject root)
        {
            if (root == null) return;

            foreach (var cycle in root.GetComponents<Meta_DayNightCycle>())
            {
                if (cycle == null || !DayNight_ControlPanel.IsGhostCandidate(cycle)) continue;
                Debug.Log($"[DayNightControl] Removing ghost Meta_DayNightCycle from '{root.name}' " +
                          "(no rig ownership; the real cycle is on Meta_DayNight_Rig).");
                Undo.DestroyObjectImmediate(cycle);
            }
        }

        /// <summary>Keeps only the best control card; older Setup runs left an inactive duplicate.</summary>
        private static void RemoveDuplicatePanels(GameObject hud, Transform keep)
        {
            if (hud == null) return;

            foreach (var t in hud.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t == keep || t.name != PANEL_NAME) continue;
                Debug.Log($"[DayNightControl] Removing duplicate control panel '{t.name}' " +
                          $"(kept '{(keep != null ? keep.name : "none")}').");
                Undo.DestroyObjectImmediate(t.gameObject);
            }
        }

        /// <summary>Points runner at the best-scored (non-ghost) cycle; never at a runner duplicate.</summary>
        private static void SetRealCycle(DayNight_ControlPanel runner, Transform notOn)
        {
            var best = DayNight_ControlPanel.BestDayNightInstance(notOn);
            if (best != null)
            {
                MiniMap_ControlPanel_Setup.SetField(runner, "_dayNight", best);
                return;
            }

            Debug.LogWarning("[DayNightControl] No real Meta_DayNightCycle found (sun assigned) - " +
                             "run Tools/Meta/DayNight/Setup to rebuild the rig.");
        }

        /// <summary>Wiring-only entry for the Tools menus (NO card creation).</summary>
        internal static void Rewire()
        {
            var root = MetaModuleTestUI_Menus.FindObject(ROOT_NAME);
            if (root == null)
            {
                Debug.Log("[DayNightControl] No control runner in scene - nothing to rewire.");
                return;
            }
            RepairRunner(root);
            RepositionPanelBottomRight();
            Debug.Log("[DayNightControl] Runner re-wired (real cycle + card buttons + legacy SceneTools).");
        }
        /// <summary>
        /// The cards used to stack bottom-left; MiniMap keeps its bottom-left slot and CameraForce
        /// moved top-right, so DayNight now owns the BOTTOM-RIGHT corner. Also repairs legacy scenes
        /// (a panel built with the old coordinates). Transform writes are safe on inactive objects.
        /// </summary>
        internal static void RepositionPanelBottomRight()
        {
            var panel = DayNight_ControlPanel.FindBestPanel();
            var rect = panel != null ? panel.GetComponent<RectTransform>() : null;
            if (rect == null)
            {
                Debug.LogWarning("[DayNightControl] Control panel transform not found - reposition skipped.");
                return;
            }
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-20f, 20f);
        }

        private static void CreateHUD(GameObject root)
        {
            var canvasGO = MetaModuleTestUI_Menus.FindObject(HUD_NAME);   // inactive-aware
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

            // A force-recreate may leave an inactive stale card behind if the HUD itself was reused;
            // never stack two cards with the same name.
            foreach (var stale in canvasGO.GetComponentsInChildren<Transform>(true))
            {
                if (stale != null && stale.name == PANEL_NAME)
                    Undo.DestroyObjectImmediate(stale.gameObject);
            }

            var panelGO = new GameObject(PANEL_NAME);
            Undo.RegisterCreatedObjectUndo(panelGO, "Create DayNight Control Panel");
            panelGO.transform.SetParent(canvasGO.transform, false);
            var panelRect = panelGO.AddComponent<RectTransform>();
            // BOTTOM-RIGHT corner - MiniMap keeps bottom-LEFT (y=20) and CameraForce sits top-RIGHT,
            // so up to two cards can be shown together without stacking onto each other.
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(1f, 0f);
            panelRect.anchoredPosition = new Vector2(-20f, 20f);
            // Taller than the other two cards: the status line shows 6 rows (cycle / light / disc /
            // time / authority mode / persisted place).
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
                SetRealCycle(runner, root.transform);   // real rig cycle, never the runner ghost
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