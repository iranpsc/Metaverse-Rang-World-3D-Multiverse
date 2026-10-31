using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// MiniMap-only Play-mode control panel (ON/OFF + zoom + live status).
    /// No DayNight / CameraForce refs. Created by MiniMap_ControlPanel_Setup in Edit mode.
    ///
    /// NO [RequireComponent(Meta_MiniMap)] on purpose: Unity auto-adding a bare Meta_MiniMap onto
    /// this runner root produced a "ghost" instance (every reference null) which older Setup runs
    /// wired the ON/OFF buttons to - so the real radar never reacted. Resolution is explicit here
    /// instead: Awake always re-points the panel at the instance that actually owns a rig, which
    /// repairs legacy scenes by recompiling alone (no Setup rerun needed).
    /// </summary>
    [AddComponentMenu("Meta/Tools/MiniMap Control Panel")]
    [DisallowMultipleComponent]
    public class MiniMap_ControlPanel : MonoBehaviour
    {
        [Header("Auto-filled if on same GO")]
        [SerializeField] private Meta_MiniMap _miniMap;
        [SerializeField] private Button _onButton;
        [SerializeField] private Button _offButton;
        [SerializeField] private Button _zoomInButton;
        [SerializeField] private Button _zoomOutButton;
        [SerializeField] private Text _statusText;

        [Header("Set by Setup tool so Awake doesn't double-wire")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private float _nextStatusRefresh;
        private bool _lastIsEnabled;            // change detection: repaint the label instantly on any toggle

        private void Awake()
        {
            EnsureRealMiniMap();

            if (!ButtonsWiredExternally)
            {
                AutoFindButtons();
                ConnectButtons();
            }
        }

        private void OnEnable()
        {
            _nextStatusRefresh = 0f;    // force an immediate refresh on re-enable
            _lastIsEnabled = _miniMap != null && _miniMap.IsEnabled;
            UpdateStatus();
        }

        // ---------------------------------------------------------------- wiring

        /// <summary>
        /// Points this panel at the Meta_MiniMap that actually owns a rig (RadarCard / camera /
        /// RawImage), never at an empty duplicate. Called from Awake so recompiling the scripts
        /// fixes legacy scenes in Play mode without re-running Setup.
        /// </summary>
        public void EnsureRealMiniMap()
        {
            if (_miniMap != null && IsRigCandidate(_miniMap)) return;

            var own = GetComponent<Meta_MiniMap>();
            var best = BestMiniMapInstance(gameObject);
            if (best != null)
            {
                // Never replace a component mounted ON this runner root itself unless it is empty -
                // bare "own" fallback keeps single-Map_control cases working where no build exists.
                Meta_MiniMap chosen = IsRigCandidate(best) ? best : own;

                if (chosen != _miniMap)
                {
                    Debug.Log($"[MiniMap] ControlPanel wired to Meta_MiniMap '{(chosen != null ? chosen.gameObject.name : "null")}' " +
                              "(previous instance had no rig references - ghost duplicate).");
                    _miniMap = chosen;
                }
                return;
            }

            if (_miniMap == null) _miniMap = own;
        }

        /// <summary>True when the instance owns usable rig references (panel, camera or RawImage).</summary>
        public static bool IsRigCandidate(Meta_MiniMap map) =>
            map != null && (map.MiniMap != null || map.MiniMapCamera != null || map.MiniMapRenderImage != null);

        /// <summary>
        /// The Meta_MiniMap with rig references, scored by reference count; prefers one living under
        /// <paramref name="prefer"/> (the runner root), never returns an empty ghost instance.
        /// </summary>
        public static Meta_MiniMap BestMiniMapInstance(GameObject prefer = null)
        {
            Meta_MiniMap best = null;
            int bestScore = -1;

            foreach (var map in FindObjectsByType<Meta_MiniMap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (map == null || !IsRigCandidate(map)) continue;

                int score = (map.MiniMap != null ? 1 : 0)
                          + (map.MiniMapCamera != null ? 1 : 0)
                          + (map.MiniMapRenderImage != null ? 1 : 0)
                          + (map.MiniMapRenderTexture != null ? 1 : 0);
                if (prefer != null && map.transform.IsChildOf(prefer.transform)) score += 100;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = map;
                }
            }
            return best;
        }

        private void AutoFindButtons()
        {
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var panel = canvas.transform.Find("Meta_MiniMap_ControlPanel");
                if (panel == null) continue;

                _onButton = panel.Find("ON")?.GetComponent<Button>();
                _offButton = panel.Find("OFF")?.GetComponent<Button>();
                _zoomInButton = panel.Find("Zoom In")?.GetComponent<Button>();
                _zoomOutButton = panel.Find("Zoom Out")?.GetComponent<Button>();
                _statusText = panel.Find("StatusText")?.GetComponent<Text>();
                return;
            }
        }

        private void ConnectButtons()
        {
            if (_onButton != null) _onButton.onClick.AddListener(OnClicked);
            if (_offButton != null) _offButton.onClick.AddListener(OffClicked);
            if (_zoomInButton != null) _zoomInButton.onClick.AddListener(ZoomInClicked);
            if (_zoomOutButton != null) _zoomOutButton.onClick.AddListener(ZoomOutClicked);
        }

        // ---- Button targets (persistent listeners from Setup tool also call these) ----

        public void OnClicked()
        {
            if (_miniMap == null) EnsureRealMiniMap();
            if (_miniMap == null) return;
            _miniMap.ToggleMiniMap(true);
            _nextStatusRefresh = 0f;
            UpdateStatus();
        }

        public void OffClicked()
        {
            if (_miniMap == null) EnsureRealMiniMap();
            if (_miniMap == null) return;
            _miniMap.ToggleMiniMap(false);
            _nextStatusRefresh = 0f;
            UpdateStatus();
        }

        public void ZoomInClicked()
        {
            if (_miniMap == null) EnsureRealMiniMap();
            if (_miniMap == null) return;
            _miniMap.ZoomInButton();
            _nextStatusRefresh = 0f;
            UpdateStatus();
        }

        public void ZoomOutClicked()
        {
            if (_miniMap == null) EnsureRealMiniMap();
            if (_miniMap == null) return;
            _miniMap.ZoomOutButton();
            _nextStatusRefresh = 0f;
            UpdateStatus();
        }

        /// <summary>
        /// Live status: ALWAYS mirrors the wired instance's IsEnabled (HUD may never desync).
        /// Repaints INSTANTLY whenever IsEnabled changes (restore, ApplyState, Inspector edit,
        /// toggle) and otherwise at 5 Hz, so a stale "IsEnabled: False" can never linger next to
        /// a visible radar. Also prints Citizen (= PLAYER) and Environment (= PLACE) so a swapped
        /// id configuration is visible right on the HUD.
        /// </summary>
        private void UpdateStatus()
        {
            if (_statusText == null) return;

            if (_miniMap == null)
            {
                _statusText.text = "IsEnabled: -\nTarget: no rig\nRun Tools > Meta > MiniMap > Setup (Edit mode)";
                return;
            }

            bool enabledChanged = _miniMap.IsEnabled != _lastIsEnabled;
            if (!enabledChanged && Time.unscaledTime < _nextStatusRefresh) return;

            _lastIsEnabled = _miniMap.IsEnabled;
            if (enabledChanged) _nextStatusRefresh = Time.unscaledTime + 0.2f;

            string targetName = _miniMap.Target != null ? _miniMap.Target.name : "waiting for local player";
            string cam = _miniMap.MiniMapCamera != null ? _miniMap.MiniMapCamera.enabled.ToString() : "NULL";
            string citizen = string.IsNullOrEmpty(_miniMap.CitizenId) ? "<no citizen id> (PLAYER)" : _miniMap.CitizenId + " (PLAYER)";
            string place = _miniMap.EnvironmentId != null && _miniMap.EnvironmentId.Length > 0 ? _miniMap.EnvironmentId + " (PLACE)" : "<environment empty> (PLACE)";
            _statusText.text =
                $"IsEnabled: {_miniMap.IsEnabled}\n" +
                $"Target: {targetName}\n" +
                $"Camera: {cam}\n" +
                $"Platform: {_miniMap.CurrentPlatform}\n" +
                $"Citizen: {citizen}\n" +
                $"Env Id: {place}";
        }

        private void Update()
        {
            UpdateStatus();
        }
    }
}
