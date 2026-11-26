using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Meta.Tools
{
    /// <summary>
    /// Tiny runtime "Meta Test" burger (top-left corner, sortingOrder 400).
    ///
    /// Lists the three module test cards WITHOUT opening them all at once:
    ///   MiniMap / DayNight / CameraForce - RADIO behaviour: picking an entry shows THAT module's
    ///   control HUD/runner and hides the other two; picking the active entry again hides all three.
    ///
    /// Plain color-only UI (Meta_HudStyle is editor-only). Spawned automatically in Play mode when
    /// the loaded scene contains any module control HUD/runner root; can also be force-added via
    /// Tools/Meta/Add 'Meta Test' Burger (Play Mode). The cards themselves are built by the editor
    /// Setup tools; this component only switches their visibility.
    /// </summary>
    [AddComponentMenu("Meta/Tools/Module Test Switcher")]
    [DisallowMultipleComponent]
    public class Meta_ModuleTestSwitcher : MonoBehaviour
    {
        internal const string ROOT_NAME = "Meta_ModuleTestSwitcher";

        private const string MINIMAP_HUD = "Meta_MiniMap_ControlHUD";
        private const string MINIMAP_RUNNER = "Meta_MiniMap_ControlRunner";
        private const string DAYNIGHT_HUD = "Meta_DayNight_ControlHUD";
        private const string DAYNIGHT_RUNNER = "Meta_DayNight_ControlRunner";
        private const string CAMERAFORCE_HUD = "Meta_CameraForce_ControlHUD";
        private const string CAMERAFORCE_RUNNER = "Meta_CameraForce_ControlRunner";

        // index 0 = MiniMap, 1 = DayNight, 2 = CameraForce; [0] = HUD canvas, [1] = runner root
        private static readonly string[][] Modules =
        {
            new[] { MINIMAP_HUD, MINIMAP_RUNNER },
            new[] { DAYNIGHT_HUD, DAYNIGHT_RUNNER },
            new[] { CAMERAFORCE_HUD, CAMERAFORCE_RUNNER },
        };

        private static readonly string[] ModuleTitles = { "MiniMap", "DayNight", "CameraForce" };

        private static readonly Color BurgerBG = new Color(0.12f, 0.14f, 0.18f, 0.92f);
        private static readonly Color PaletteBG = new Color(0.08f, 0.09f, 0.12f, 0.94f);
        private static readonly Color OptionBG = new Color(0.16f, 0.19f, 0.24f, 0.95f);
        private static readonly Color ActiveColor = new Color(1f, 0.82f, 0.35f);

        private static Meta_ModuleTestSwitcher _instance;
        private static bool _sceneLoadHooked;

        private GameObject _palette;
        private readonly Text[] _optionLabels = new Text[ModuleTitles.Length];

        // ------------------------------------------------------------------ spawn

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void HookSceneLoads()
        {
            if (_sceneLoadHooked) return;
            _sceneLoadHooked = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TrySpawn();   // re-check on every later scene load (the first one also has AfterSceneLoad)
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawnIfNeeded() => TrySpawn();

        private static void TrySpawn()
        {
            if (!Application.isPlaying) return;
            if (Object.FindFirstObjectByType<Meta_ModuleTestSwitcher>(FindObjectsInactive.Include) != null) return;

            // The new top-left Modules Dropdown replaces this legacy burger. Both live in the
            // same corner, so never spawn the burger when the dropdown HUD is present.
            if (Object.FindFirstObjectByType<MetaModulesPanelSwitcher>(FindObjectsInactive.Include) != null)
                return;

            if (!AnyModuleCardInScene()) return;   // no cards -> no burger

            new GameObject(ROOT_NAME).AddComponent<Meta_ModuleTestSwitcher>();
            Debug.Log("[MetaTest] 'Meta Test' burger spawned (module control cards found in scene).");
        }

        /// <summary>True when any module control HUD/runner ROOT exists in the active scene (GameObjects were created by the editor Setups).</summary>
        private static bool AnyModuleCardInScene()
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                string name = root.name;
                if (name == MINIMAP_HUD || name == MINIMAP_RUNNER ||
                    name == DAYNIGHT_HUD || name == DAYNIGHT_RUNNER ||
                    name == CAMERAFORCE_HUD || name == CAMERAFORCE_RUNNER)
                    return true;
            }
            return false;
        }

        /// <summary>Editor menu entry (Play mode): force-adds the burger even in scenes without cards.</summary>
        public static void SpawnManually()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[MetaTest] Spawn the burger in Play mode (it auto-spawns there anyway).");
                return;
            }
            if (Object.FindFirstObjectByType<MetaModulesPanelSwitcher>(FindObjectsInactive.Include) != null)
            {
                Debug.Log("[MetaTest] The new Modules Dropdown HUD is present - legacy burger is not needed.");
                return;
            }
            if (Object.FindFirstObjectByType<Meta_ModuleTestSwitcher>(FindObjectsInactive.Include) != null)
            {
                Debug.Log("[MetaTest] Burger already in scene.");
                return;
            }
            new GameObject(ROOT_NAME).AddComponent<Meta_ModuleTestSwitcher>();
            Debug.Log("[MetaTest] Burger added.");
        }

        // ------------------------------------------------------------------ UI

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            BuildUI();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void BuildUI()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 400;
            gameObject.AddComponent<CanvasScaler>();     // plain config, same as the module card canvases
            gameObject.AddComponent<GraphicRaycaster>();

            EnsureEventSystem();

            _palette = BuildPalette();

            var burger = BuildButton(transform, "Burger", "Meta Test", BurgerBG, Color.white);
            var rt = (RectTransform)burger.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(10f, -10f);
            rt.sizeDelta = new Vector2(108f, 30f);
            burger.onClick.AddListener(TogglePalette);

            _palette.SetActive(false);
        }

        private GameObject BuildPalette()
        {
            var root = new GameObject("Palette", typeof(RectTransform));
            root.transform.SetParent(transform, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(10f, -44f);
            rt.sizeDelta = new Vector2(120f, 108f);

            var bg = root.AddComponent<Image>();
            bg.color = PaletteBG;

            var layout = root.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(6, 6, 6, 6);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            for (int i = 0; i < ModuleTitles.Length; i++)
            {
                var btn = BuildButton(root.transform, ModuleTitles[i], ModuleTitles[i], OptionBG, Color.white);
                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 30f;
                le.flexibleWidth = 1f;

                int index = i;   // closure per entry
                btn.onClick.AddListener(() => ToggleModule(index));
                _optionLabels[i] = btn.GetComponentInChildren<Text>(true);
            }
            return root;
        }

        private static Button BuildButton(Transform parent, string name, string label, Color bg, Color labelColor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var img = go.AddComponent<Image>();
            img.color = bg;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.fadeDuration = 0.06f;
            btn.colors = colors;

            BuildText(go.transform, "Label", label, 12, labelColor);
            return btn;
        }

        private static Text BuildText(Transform parent, string name, string label, int size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            var txt = go.AddComponent<Text>();
            txt.text = label;
            txt.font = RuntimeFont();
            txt.fontSize = size;
            txt.color = color;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
            return txt;
        }

        private static Font RuntimeFont()
        {
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                   ?? (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
        }

        /// <summary>Scenes in gameplay have one EventSystem; only add a fallback when none exists.</summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (Object.FindFirstObjectByType<EventSystem>(FindObjectsInactive.Include) != null) return;

            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            var inputMod = System.Type.GetType(
                    "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
                ?? System.Type.GetType("UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
            if (inputMod != null) es.AddComponent(inputMod);
        }

        // ------------------------------------------------------------------ behaviour

        public void TogglePalette()
        {
            if (_palette == null) return;
            bool show = !_palette.activeSelf;
            if (show) RefreshPaletteHighlight();
            _palette.SetActive(show);
        }

        /// <summary>
        /// Radio: pick entry -> only that module's card is visible; pick it again -> all cards hidden.
        /// </summary>
        public void ToggleModule(int index)
        {
            bool wasVisible = IsShown(index);
            for (int m = 0; m < Modules.Length; m++)
                SetShown(m, (m == index) && !wasVisible);
            RefreshPaletteHighlight();
        }

        public bool IsShown(int index)
        {
            var hud = Find(ModuleTitles[index], Modules[index][0]);
            return hud != null && hud.activeInHierarchy;
        }

        private void SetShown(int index, bool shown)
        {
            var runner = Find(ModuleTitles[index], Modules[index][1]);
            var hud = Find(ModuleTitles[index], Modules[index][0]);
            if (runner == null && hud == null)
            {
                Debug.LogWarning($"[MetaTest] '{ModuleTitles[index]}' test card is not in this scene - " +
                                 "run its Tools/Meta setup in Edit mode first.");
                return;
            }
            if (runner != null) runner.SetActive(shown);
            if (hud != null) hud.SetActive(shown);
        }

        private void RefreshPaletteHighlight()
        {
            for (int i = 0; i < _optionLabels.Length; i++)
            {
                if (_optionLabels[i] == null) continue;
                _optionLabels[i].color = IsShown(i) ? ActiveColor : Color.white;
            }
        }

        private static GameObject Find(string moduleTitle, string name)
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                if (root != null && root.name == name)
                    return root;
            return null;
        }
    }
}
