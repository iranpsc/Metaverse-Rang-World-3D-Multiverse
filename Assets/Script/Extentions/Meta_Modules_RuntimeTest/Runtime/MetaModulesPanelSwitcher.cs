using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Meta.Tools
{
    /// <summary>
    /// Compact top-left module dropdown for exactly three modules:
    /// MiniMap, DayNight and CameraForce.
    ///
    /// Behaviour:
    ///   - All module control panels are hidden on Awake / Start.
    ///   - Picking a dropdown item shows ONLY that module's HUD + control runner and hides
    ///     the other two - the three cards can never stack.
    ///   - "Close All" hides every module panel and closes the dropdown.
    ///   - The dropdown chrome stays visible; the panel itself opens/closes on demand.
    ///
    /// The UI is created by Tools/Meta/Modules Dropdown/Setup In Scene
    /// (root: Meta_Modules_DropdownHUD, sortingOrder 500, safe-area aware).
    /// This component only coordinates visibility; each module keeps its own runtime logic.
    /// It never touches a Camera, so the player MainCamera is unaffected.
    /// </summary>
    [AddComponentMenu("Meta/Tools/Modules Panel Switcher")]
    [DisallowMultipleComponent]
    public class MetaModulesPanelSwitcher : MonoBehaviour
    {
        /// <summary>Scene root created by the Setup tool.</summary>
        public const string ROOT_NAME = "Meta_Modules_DropdownHUD";

        /// <summary>Module identifiers used by Show / HideAll.</summary>
        public enum ModuleId
        {
            None = 0,
            MiniMap = 1,
            DayNight = 2,
            CameraForce = 3,
        }

        // Stable control roots (all are scene roots; inactive roots are still found).
        public const string MINIMAP_HUD = "Meta_MiniMap_ControlHUD";
        public const string MINIMAP_RUNNER = "Meta_MiniMap_ControlRunner";
        public const string DAYNIGHT_HUD = "Meta_DayNight_ControlHUD";
        public const string DAYNIGHT_RUNNER = "Meta_DayNight_ControlRunner";
        public const string CAMERAFORCE_HUD = "Meta_CameraForce_ControlHUD";
        public const string CAMERAFORCE_RUNNER = "Meta_CameraForce_ControlRunner";

        private static readonly ModuleId[] AllModules =
        {
            ModuleId.MiniMap,
            ModuleId.DayNight,
            ModuleId.CameraForce,
        };

        // Readable against the dark HUD (same cyan/white as Meta_HudStyle, but runtime-safe).
        private static readonly Color ActiveColor = new Color(0.494f, 0.878f, 1f, 1f);   // #7EE0FF
        private static readonly Color InactiveColor = new Color(0.910f, 0.925f, 0.945f, 1f); // #E8ECF1

        private const string ChromeIdle = "MODULES  v";
        private const string ChromeOpen = "MODULES  ^";

        [Header("Dropdown UI (wired by Tools/Meta/Modules Dropdown/Setup In Scene)")]
        [SerializeField] private RectTransform _safeArea;
        [SerializeField] private GameObject _dropdownPanel;
        [SerializeField] private GameObject _dropdownBlocker;
        [SerializeField] private Text _chromeLabel;
        [SerializeField] private Text[] _optionLabels = new Text[3];

        [SerializeField] private ModuleId _activeModule = ModuleId.None;
        private Rect _lastSafeArea;
        private Vector2Int _lastScreenSize;

        public ModuleId ActiveModule => _activeModule;

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            ApplySafeArea();

            // Shipping default: no module panel visible, dropdown closed, chrome still there.
            HideAll();
        }

        private void OnEnable()
        {
            ApplySafeArea();
        }

        private void Update()
        {
            // Re-apply only when the device safe area / resolution changes (rotation, notch).
            if (Screen.width != _lastScreenSize.x ||
                Screen.height != _lastScreenSize.y ||
                Screen.safeArea != _lastSafeArea)
            {
                ApplySafeArea();
            }
        }

        // ------------------------------------------------------------------ public API

        /// <summary>Exclusive show: the chosen module's HUD/runner on, the other two off.</summary>
        public void Show(ModuleId module)
        {
            if (module == ModuleId.None)
            {
                HideAll();
                return;
            }

            foreach (var id in AllModules)
                if (id != module)
                    SetModuleVisible(id, false);

            bool shown = SetModuleVisible(module, true);
            _activeModule = shown ? module : ModuleId.None;

            SetDropdownOpen(false);
            RefreshHighlight();

            if (!shown)
            {
                Debug.LogWarning(
                    $"[MetaModules] '{module}' control roots are not in this scene. " +
                    $"Run Tools/Meta/{module}/Setup first.");
            }
        }

        public void ShowMiniMap() => Show(ModuleId.MiniMap);
        public void ShowDayNight() => Show(ModuleId.DayNight);
        public void ShowCameraForce() => Show(ModuleId.CameraForce);

        /// <summary>Hides all three module panels, resets the active highlight and closes the dropdown.</summary>
        public void HideAll()
        {
            foreach (var id in AllModules)
                SetModuleVisible(id, false);

            _activeModule = ModuleId.None;
            SetDropdownOpen(false);
            RefreshHighlight();
        }

        /// <summary>Dropdown "Close All" entry.</summary>
        public void ShowNone() => HideAll();

        public void ToggleDropdown()
        {
            if (_dropdownPanel == null) return;
            SetDropdownOpen(!_dropdownPanel.activeSelf);
        }

        public void CloseDropdown() => SetDropdownOpen(false);

        /// <summary>
        /// Re-reads scene visibility without changing it. Used by the editor Show/Hide menus
        /// after they touch a module root directly.
        /// </summary>
        public void RefreshFromScene()
        {
            _activeModule = ModuleId.None;
            foreach (var id in AllModules)
            {
                if (!IsModuleVisible(id)) continue;
                _activeModule = id;
                break;
            }
            RefreshHighlight();
        }

        // ------------------------------------------------------------------ UI state

        private void SetDropdownOpen(bool open)
        {
            if (_dropdownPanel != null && _dropdownPanel.activeSelf != open)
                _dropdownPanel.SetActive(open);
            if (_dropdownBlocker != null && _dropdownBlocker.activeSelf != open)
                _dropdownBlocker.SetActive(open);

            if (_chromeLabel != null)
                _chromeLabel.text = (_dropdownPanel != null && _dropdownPanel.activeSelf)
                    ? ChromeOpen
                    : ChromeIdle;
        }

        private void RefreshHighlight()
        {
            if (_optionLabels == null) return;

            for (int i = 0; i < _optionLabels.Length; i++)
            {
                if (_optionLabels[i] == null) continue;
                ModuleId id = i < AllModules.Length ? AllModules[i] : ModuleId.None;
                _optionLabels[i].color = (id != ModuleId.None && id == _activeModule)
                    ? ActiveColor
                    : InactiveColor;
            }
        }

        // ------------------------------------------------------------------ module visibility

        /// <summary>Returns true when at least one of the module's two roots exists.</summary>
        private static bool SetModuleVisible(ModuleId module, bool visible)
        {
            Names(module, out string hudName, out string runnerName);

            var hud = FindSceneObject(hudName);
            var runner = FindSceneObject(runnerName);
            if (hud == null && runner == null) return false;

            if (hud != null && hud.activeSelf != visible) hud.SetActive(visible);
            if (runner != null && runner.activeSelf != visible) runner.SetActive(visible);
            return true;
        }

        private static bool IsModuleVisible(ModuleId module)
        {
            Names(module, out string hudName, out _);
            var hud = FindSceneObject(hudName);
            return hud != null && hud.activeInHierarchy;
        }

        private static void Names(ModuleId module, out string hudName, out string runnerName)
        {
            switch (module)
            {
                case ModuleId.MiniMap:
                    hudName = MINIMAP_HUD;
                    runnerName = MINIMAP_RUNNER;
                    return;
                case ModuleId.DayNight:
                    hudName = DAYNIGHT_HUD;
                    runnerName = DAYNIGHT_RUNNER;
                    return;
                case ModuleId.CameraForce:
                    hudName = CAMERAFORCE_HUD;
                    runnerName = CAMERAFORCE_RUNNER;
                    return;
                default:
                    hudName = null;
                    runnerName = null;
                    return;
            }
        }

        /// <summary>
        /// Inactive-aware active-scene lookup. Root scan first (the normal case), then a
        /// transform scan so a HUD reparented by hand is still found. Prefab assets and
        /// objects from other scenes are ignored.
        /// </summary>
        public static GameObject FindSceneObject(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;

            foreach (var root in scene.GetRootGameObjects())
                if (root != null && root.name == name)
                    return root;

            var transforms = Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in transforms)
            {
                if (t == null || t.name != name) continue;
                if (t.gameObject.scene == scene) return t.gameObject;
            }

            return null;
        }

        // ------------------------------------------------------------------ safe area

        /// <summary>Fits the SafeArea child to Screen.safeArea (notch / rounded corners / gesture bars).</summary>
        private void ApplySafeArea()
        {
            _lastScreenSize = new Vector2Int(Screen.width, Screen.height);
            _lastSafeArea = Screen.safeArea;

            if (_safeArea == null || Screen.width <= 0 || Screen.height <= 0) return;

            Rect safe = Screen.safeArea;
            if (safe.width <= 0f || safe.height <= 0f)
                safe = new Rect(0f, 0f, Screen.width, Screen.height);

            Vector2 min = safe.position;
            Vector2 max = safe.position + safe.size;
            min.x /= Screen.width;
            min.y /= Screen.height;
            max.x /= Screen.width;
            max.y /= Screen.height;

            _safeArea.anchorMin = min;
            _safeArea.anchorMax = max;
            _safeArea.offsetMin = Vector2.zero;
            _safeArea.offsetMax = Vector2.zero;
        }
    }
}
