#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Show / Hide menus for the per-module PLAY-MODE TEST control UIs.
    ///
    /// Scope (intentionally narrow) - exactly the three tool cards:
    ///   Meta_MiniMap_ControlRunner     + Meta_MiniMap_ControlHUD     (MiniMap ON/OFF/Zoom)
    ///   Meta_DayNight_ControlRunner    + Meta_DayNight_ControlHUD    (DayNight ON/OFF/Reset)
    ///   Meta_CameraForce_ControlRunner + Meta_CameraForce_ControlHUD (CameraForce renderer card)
    ///
    /// Product UI is NOT touched: the radar rig (Meta_MiniMap_Rig / Meta_MiniMap_HUD), the
    /// gameplay Meta_MiniMap panel itself, and the E2E tester HUD (Meta_Modules_TestRunner).
    ///
    /// Default state: every module Setup run now ends with ApplyDefaultHidden, so a scene ships
    /// with all three cards SetActive(false). Single module is opened via its Show menu, all via
    /// Tools/Meta/Hide All Module Test UIs, or the runtime "Meta Test" burger (Meta_ModuleTestSwitcher)
    /// which shows ONE module at a time (radio behaviour).
    ///
    /// Layout: MiniMap = bottom-left (slot 0), DayNight = bottom-right, CameraForce = top-right,
    /// so ONE (preferred) or TWO cards can be visible at once without stacking over each other.
    /// </summary>
    public static class MetaModuleTestUI_Menus
    {
        private enum Kind { MiniMap, DayNight, CameraForce }

        // ------------------------------------------------------------------ menus

        [MenuItem("Tools/Meta/MiniMap/Show Control UI", priority = 110)]
        private static void ShowMiniMap() => Show(Kind.MiniMap);

        [MenuItem("Tools/Meta/MiniMap/Hide Control UI", priority = 111)]
        private static void HideMiniMap() => Hide(Kind.MiniMap);

        [MenuItem("Tools/Meta/DayNight/Show Control UI", priority = 120)]
        private static void ShowDayNight() => Show(Kind.DayNight);

        [MenuItem("Tools/Meta/DayNight/Hide Control UI", priority = 121)]
        private static void HideDayNight() => Hide(Kind.DayNight);

        [MenuItem("Tools/Meta/CameraForce/Show Control UI", priority = 140)]
        private static void ShowCameraForce() => Show(Kind.CameraForce);

        [MenuItem("Tools/Meta/CameraForce/Hide Control UI", priority = 141)]
        private static void HideCameraForce() => Hide(Kind.CameraForce);

        [MenuItem("Tools/Meta/Hide All Module Test UIs", priority = 150)]
        private static void HideAllMenu()
        {
            int touched = 0;
            foreach (Kind kind in System.Enum.GetValues(typeof(Kind)))
                touched += HideSilent(kind);

            // Keep the dropdown's own state/highlight in sync (and close it).
            var switcher = FindSwitcher();
            if (switcher != null)
            {
                switcher.HideAll();
                EditorUtility.SetDirty(switcher);
            }

            MarkSceneDirty();
            Debug.Log($"[MetaModuleTestUI] Hide All Module Test UIs - objects hidden: {touched}.");
        }

        /// <summary>
        /// Force-adds the runtime "Meta Test" burger (Play mode only; in Edit mode the burger has
        /// no UI until Awake runs in Play). Otherwise it auto-spawns whenever a scene contains a
        /// module control HUD/runner root.
        /// </summary>
        [MenuItem("Tools/Meta/Add 'Meta Test' Burger (Play Mode)", priority = 151)]
        private static void AddBurgerMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.Log("[MetaModuleTestUI] Enter Play mode first - the burger auto-spawns there " +
                          "whenever a module control HUD is in the scene.");
                return;
            }
            Meta_ModuleTestSwitcher.SpawnManually();
        }

        // ------------------------------------------------------------------ actions

        private static void Show(Kind kind)
        {
            Names(kind, out string runnerName, out string hudName, out string menu);

            // Preferred path: the top-left dropdown owns visibility, so picking one module
            // always hides the other two (never stack all three cards).
            var switcher = FindSwitcher();
            if (switcher != null)
            {
                switcher.Show(ToModuleId(kind));
                EditorUtility.SetDirty(switcher);
                MarkSceneDirty();
                Debug.Log($"[MetaModuleTestUI] {menu} control UI shown exclusively " +
                          "(other module panels hidden by MetaModulesPanelSwitcher).");
                return;
            }

            var runner = FindObject(runnerName);
            var hud = FindObject(hudName);
            if (runner == null && hud == null)
            {
                Debug.LogError($"[MetaModuleTestUI] No {menu} test UI in this scene. " +
                               $"Run Tools/Meta/{menu}/Setup (Edit mode) first.");
                return;
            }

            // Fallback without the dropdown: still exclusive, never additive.
            foreach (Kind other in System.Enum.GetValues(typeof(Kind)))
                if (other != kind)
                    HideSilent(other);

            if (runner != null) runner.SetActive(true);
            if (hud != null) hud.SetActive(true);
            MarkSceneDirty();
            Debug.Log($"[MetaModuleTestUI] {menu} control UI shown exclusively ({runnerName} / {hudName}).");
        }

        private static void Hide(Kind kind)
        {
            Names(kind, out string runnerName, out string hudName, out string menu);
            int touched = HideSilent(kind);

            var switcher = FindSwitcher();
            if (switcher != null)
            {
                if (switcher.ActiveModule == ToModuleId(kind))
                    switcher.HideAll();
                else
                    switcher.RefreshFromScene();
                EditorUtility.SetDirty(switcher);
            }

            MarkSceneDirty();
            Debug.Log(touched > 0
                ? $"[MetaModuleTestUI] {menu} control UI hidden."
                : $"[MetaModuleTestUI] {menu} test UI not present in this scene ({runnerName}).");
        }

        /// <summary>Only for the module's own Show/Hide pair - no error spam when absent.</summary>
        private static int HideSilent(Kind kind)
        {
            Names(kind, out string runnerName, out string hudName, out _);
            int touched = 0;
            var runner = FindObject(runnerName);
            if (runner != null && runner.activeSelf) { runner.SetActive(false); touched++; }
            var hud = FindObject(hudName);
            if (hud != null && hud.activeSelf) { hud.SetActive(false); touched++; }
            return touched;
        }

        // ------------------------------------------------------------------ helpers (used by the setup tools)

        /// <summary>
        /// Scene-default for module test cards: runner + HUD SetActive(false), scene marked dirty.
        /// Called by every *_ControlPanel_Setup at the end of EVERY BuildOrSelect path (create and
        /// repair), so a scene can never silently ship with all three cards visible again.
        /// Returns true when at least one object was actually hidden.
        /// </summary>
        internal static bool ApplyDefaultHidden(string runnerName, string hudName)
        {
            bool touched = false;
            var runner = FindObject(runnerName);
            if (runner != null && runner.activeSelf) { runner.SetActive(false); touched = true; }
            var hud = FindObject(hudName);
            if (hud != null && hud.activeSelf) { hud.SetActive(false); touched = true; }

            // If the dropdown already exists, keep its highlight in sync with the scene.
            FindSwitcher()?.RefreshFromScene();

            if (!touched) return false;
            MarkSceneDirty();
            Debug.Log($"[MetaModuleTestUI] '{runnerName}' / '{hudName}' hidden by default " +
                      "(Tools/Meta menus or the Meta Test burger re-open it).");
            return true;
        }

        /// <summary>
        /// Scene lookup that tolerates INACTIVE objects - GameObject.Find cannot see them, and after
        /// ApplyDefaultHidden a module card is almost always inactive when these tools run. Scans the
        /// active scene's root objects first, then falls back to every Canvas that belongs to the
        /// active scene (covers HUDs that were reparented by hand).
        /// </summary>
        internal static GameObject FindObject(string name)
        {
            var scene = SceneManager.GetActiveScene();

            foreach (var root in scene.GetRootGameObjects())
                if (root != null && root.name == name)
                    return root;

            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (canvas == null) continue;
                var go = canvas.gameObject;
                if (go.name != name) continue;
                if (!go.scene.IsValid() || go.scene != scene) continue;   // skips prefab assets / other scenes
                return go;
            }
            return null;
        }

        /// <summary>
        /// The top-left dropdown switcher, when present. Looked up through the stable HUD root
        /// so inactive objects (chrome hidden in Edit mode) are found without touching prefabs.
        /// </summary>
        internal static MetaModulesPanelSwitcher FindSwitcher()
        {
            var hud = FindObject(MetaModulesPanelSwitcher.ROOT_NAME);
            return hud != null ? hud.GetComponent<MetaModulesPanelSwitcher>() : null;
        }

        private static MetaModulesPanelSwitcher.ModuleId ToModuleId(Kind kind)
        {
            switch (kind)
            {
                case Kind.MiniMap: return MetaModulesPanelSwitcher.ModuleId.MiniMap;
                case Kind.DayNight: return MetaModulesPanelSwitcher.ModuleId.DayNight;
                default: return MetaModulesPanelSwitcher.ModuleId.CameraForce;
            }
        }

        private static void MarkSceneDirty()
        {
            if (Application.isPlaying) return;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        /// <summary>
        /// Editor menu: full repair for OLD scenes that shipped all module cards ACTIVE with legacy
        /// wiring (e.g. Lobby 1 WebGL). Steps: rewire runner components + scene tools to the REAL
        /// module instances, retrofit the new corner layout, then hide every module test card.
        /// </summary>
        [MenuItem("Tools/Meta/Repair Scene: Hide All Module Test UIs", priority = 160)]
        private static void RepairSceneMenu()
        {
            int hidden = 0;
            foreach (Kind kind in System.Enum.GetValues(typeof(Kind)))
                hidden += HideSilent(kind);

            // Retrofit legacy corners (only where the card exists).
            if (FindObject(DayNight_ControlPanel_Setup.HUD_NAME) != null)
                DayNight_ControlPanel_Setup.RepositionPanelBottomRight();
            if (FindObject(CameraForce_ControlPanel_Setup.HUD_NAME) != null)
                CameraForce_ControlPanel_Setup.RepositionCardTopRight();

            // Re-wire ControlPanel runners + legacy SceneTools to the REAL module instances
            // (never the RequireComponent ghosts auto-added next to the runner components).
            MiniMap_ControlPanel_Setup.Rewire();
            DayNight_ControlPanel_Setup.Rewire();
            CameraForce_ControlPanel_Setup.Rewire();

            FindSwitcher()?.RefreshFromScene();

            MarkSceneDirty();
            Debug.Log($"[MetaModuleTestUI] Scene repair done: {hidden} module test UI objects hidden.");
        }

        /// <summary>
        /// Repair the WIRING of every already-present module runner in the scene (real refs only,
        /// NO card is created and nothing changes visually). Useful when a runner component shows
        /// None / ghost references in the Inspector.
        /// </summary>
        [MenuItem("Tools/Meta/Wire Module Control References", priority = 152)]
        private static void WireSceneMenu()
        {
            MiniMap_ControlPanel_Setup.Rewire();
            DayNight_ControlPanel_Setup.Rewire();
            CameraForce_ControlPanel_Setup.Rewire();
            MarkSceneDirty();
            Debug.Log("[MetaModuleTestUI] Module control references re-wired (real card buttons, real module instances).");
        }

        private static void Names(Kind kind, out string runner, out string hud, out string menu)
        {
            switch (kind)
            {
                case Kind.MiniMap:
                    runner = MiniMap_ControlPanel_Setup.ROOT_NAME;
                    hud = MiniMap_ControlPanel_Setup.HUD_NAME;
                    menu = "MiniMap";
                    break;
                case Kind.DayNight:
                    runner = DayNight_ControlPanel_Setup.ROOT_NAME;
                    hud = DayNight_ControlPanel_Setup.HUD_NAME;
                    menu = "DayNight";
                    break;
                default:
                    runner = CameraForce_ControlPanel_Setup.ROOT_NAME;
                    hud = CameraForce_ControlPanel_Setup.HUD_NAME;
                    menu = "CameraForce";
                    break;
            }
        }
    }
}
#endif
