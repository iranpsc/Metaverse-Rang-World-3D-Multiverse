using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// DayNight-only Play-mode control panel (ON/OFF/Reset). No MiniMap / CameraForce refs.
    /// Created by DayNight_ControlPanel_Setup in Edit mode.
    ///
    /// OWNERSHIP: this card never promotes a visitor to owner. ON/OFF/Reset only write when the
    /// configured Meta_DayNightCycle is already LocalAuthoritySimulate; ApplyOnly visitors follow
    /// the place owner and only render ApplyState() snapshots.
    /// </summary>
    [AddComponentMenu("Meta/Tools/DayNight Control Panel")]
    public class DayNight_ControlPanel : MonoBehaviour
    {
        public const string RunnerName = "Meta_DayNight_ControlRunner";
        public const string PanelName = "Meta_DayNight_ControlPanel";
        public const string RowName = "ButtonsRow";
        public const string HudName = "Meta_DayNight_ControlHUD";

        [Header("Auto-filled if on same GO")]
        [SerializeField] private Meta_DayNightCycle _dayNight;
        [SerializeField] private Button _onButton;
        [SerializeField] private Button _offButton;
        [SerializeField] private Button _resetButton;
        [SerializeField] private Text _statusText;

        [Header("Set by Setup tool so Awake doesn't double-wire")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private bool _visitorActionLogged;
        private float _nextResolveAttempt;

        private void Awake()
        {
            EnsureRealDayNightCycle();

            if (_onButton == null || _offButton == null || _resetButton == null || _statusText == null)
                AutoFindButtons();

            ConnectMissingListeners();
        }

        // ---------------------------------------------------------------- real cycle wiring

        /// <summary>
        /// A ghost candidate is the RequireComponent duplicate Unity auto-added next to this panel
        /// on the runner root. It may even carry a serialized sun reference (the scene's main light)
        /// without owning the rig, and it activations together with the test UI. The real cycle lives
        /// on Meta_DayNight_Rig and has the visible-sun/disc wiring, so the runner root is never a
        /// valid target.
        /// </summary>
        public static bool IsGhostCandidate(Meta_DayNightCycle cycle)
        {
            if (cycle == null) return true;
            if (cycle.Sun == null) return true;
            if (cycle.gameObject.name == RunnerName) return true;
            return cycle.transform.parent != null && cycle.transform.parent.name == RunnerName;
        }

        /// <summary>
        /// Best configured Meta_DayNightCycle in the scene: scored by rig references (usable sun,
        /// visible disc, persisted place, SceneTool) and penalized when mounted on the ControlRunner.
        /// Ghosts are excluded rather than merely penalized.
        /// </summary>
        public static Meta_DayNightCycle BestDayNightInstance(Transform notOn = null)
        {
            Meta_DayNightCycle best = null;
            int bestScore = int.MinValue;

            foreach (var cycle in FindObjectsByType<Meta_DayNightCycle>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (cycle == null || IsGhostCandidate(cycle)) continue;

                int score = 0;
                if (cycle.Sun != null) score += 8;
                if (Meta_DayNightCycle.IsUsableSun(cycle.Sun)) score += 4;
                if (cycle.HasVisibleSun) score += 3;
                if (cycle.GetComponent<DayNight_SceneTool>() != null) score += 2;
                if (!string.IsNullOrEmpty(cycle.EnvironmentId)) score += 1;
                if (notOn != null && cycle.transform.IsChildOf(notOn)) score -= 20;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = cycle;
                }
            }
            return best;
        }

        /// <summary>
        /// Re-points this panel at the cycle that actually owns the rig. Called from Awake so
        /// recompiling repairs legacy scenes in Play mode, and from the editor wire/repair tools.
        /// </summary>
        public void EnsureRealDayNightCycle()
        {
            // Keep a valid serialized reference; only ghosts/empty refs are repaired.
            if (_dayNight != null && !IsGhostCandidate(_dayNight)) return;

            var best = BestDayNightInstance(transform);
            if (best == null)
            {
                if (_dayNight != null) _dayNight = null;
                return;
            }

            // A ghost duplicate on this same runner must never wake up later and drive a second
            // empty cycle (the old RequireComponent bug). Disable it; the editor repair removes it.
            var own = GetComponent<Meta_DayNightCycle>();
            if (own != null && own != best && IsGhostCandidate(own))
            {
                own.IsEnabled = false;
                own.enabled = false;
            }

            if (best != _dayNight)
            {
                Debug.Log($"[DayNightControl] Panel re-wired to Meta_DayNightCycle " +
                          $"'{(best != null ? best.gameObject.name : "null")}' " +
                          "(previous reference was missing or a runner ghost).");
                _dayNight = best;
            }
        }

        private void AutoFindButtons()
        {
            Transform panel = FindBestPanel();
            if (panel == null) return;

            if (_onButton == null) _onButton = FindButton(panel, "ON");
            if (_offButton == null) _offButton = FindButton(panel, "OFF");
            if (_resetButton == null) _resetButton = FindButton(panel, "Reset");
            if (_statusText == null) _statusText = panel.Find("StatusText")?.GetComponent<Text>();
        }

        private void ConnectMissingListeners()
        {
            // Persistent listeners from the Setup tool already cover the buttons; only add a
            // runtime listener when a button has none (legacy/manual scene wiring).
            if (_onButton != null && _onButton.onClick.GetPersistentEventCount() == 0)
                _onButton.onClick.AddListener(OnClicked);
            if (_offButton != null && _offButton.onClick.GetPersistentEventCount() == 0)
                _offButton.onClick.AddListener(OffClicked);
            if (_resetButton != null && _resetButton.onClick.GetPersistentEventCount() == 0)
                _resetButton.onClick.AddListener(ResetClicked);
        }

        // ---- Button targets (persistent listeners from Setup tool also call these) ----

        public void OnClicked()
        {
            EnsureRealDayNightCycle();
            if (_dayNight == null)
            {
                UpdateStatus();
                return;
            }

            if (!_dayNight.SimulatesLocally)
            {
                LogVisitorActionOnce();
                UpdateStatus();
                return;
            }

            _dayNight.ToggleCycle(true);
            UpdateStatus();   // label refresh immediately - Update() repaints every frame anyway
        }

        public void OffClicked()
        {
            EnsureRealDayNightCycle();
            if (_dayNight == null)
            {
                UpdateStatus();
                return;
            }

            if (!_dayNight.SimulatesLocally)
            {
                LogVisitorActionOnce();
                UpdateStatus();
                return;
            }

            _dayNight.ToggleCycle(false);
            UpdateStatus();
        }

        public void ResetClicked()
        {
            EnsureRealDayNightCycle();
            if (_dayNight == null)
            {
                UpdateStatus();
                return;
            }

            if (!_dayNight.SimulatesLocally)
            {
                LogVisitorActionOnce();
                UpdateStatus();
                return;
            }

            _dayNight.ForceResetToDefault();
            UpdateStatus();
        }

        private void LogVisitorActionOnce()
        {
            if (_visitorActionLogged) return;
            _visitorActionLogged = true;
            Debug.Log("[DayNightControl] This peer is a visitor (ApplyOnly). ON/OFF/Reset are " +
                      "disabled here; the environment owner drives the cycle and this client renders " +
                      "the owner's ApplyState() snapshots.");
        }

        private void UpdateStatus()
        {
            if (_statusText == null) return;

            if (_dayNight == null)
            {
                _statusText.text = "Cycle: no Meta_DayNightCycle found\n" +
                                   "Assign a rig or run Tools/Meta/DayNight/Setup.";
                return;
            }

            string sunInfo = _dayNight.Sun != null
                ? $"Light: Int {_dayNight.Sun.intensity:F2}  RotX {_dayNight.Sun.transform.eulerAngles.x:F1}°"
                : "Light: NONE";

            // Proof for the user that the VISIBLE sun object exists (a light alone is invisible).
            // Double guard: HasVisibleSun and VisibleSunTransform must never disagree under load
            // (NRE sweep - a raw .name on the transform is exactly the crash this guards against).
            string discInfo = _dayNight.HasVisibleSun && _dayNight.VisibleSunTransform != null
                ? $"Disc: {_dayNight.VisibleSunTransform.name}"
                : "Disc: none (runtime create on ON)";

            // Live sky phase readout incl. "blending %" while crossing a boundary window.
            // When the cycle is OFF the sky is the restored scene sky, so report the material that
            // is ACTUALLY assigned - never keep printing the clock phase next to a restored sky.
            string skyInfo;
            if (_dayNight.IsEnabled && _dayNight.HasPhaseSky)
                skyInfo = $"Sky: {_dayNight.DescribeSkyPhase(_dayNight.Time01)}";
            else if (RenderSettings.skybox != null)
                skyInfo = $"Sky: {RenderSettings.skybox.name}" +
                          (_dayNight.IsEnabled ? string.Empty : " (restored)");
            else
                skyInfo = "Sky: none";

            // Authority mode matters: an ApplyOnly peer ignores ON/OFF and only renders ApplyState().
            string role = _dayNight.SimulatesLocally ? "owner: simulate" : "visitor: apply-only";
            string place = _dayNight.PersistenceEnabled ? _dayNight.EnvironmentId : "not persisted";
            string time = $"Time: {_dayNight.Time01:F3} ({_dayNight.SunAngle:F0}deg)\n{role}\n{place}";

            // Single source of truth: the SAME phrase helper the SceneTool card uses, derived from
            // SimulatesLocally + IsEnabled, so the label can never lie next to moving numbers.
            _statusText.text = $"Cycle: {_dayNight.DescribeCycleState()}\n{sunInfo}\n{discInfo}\n{skyInfo}\n{time}";
        }

        private void Update()
        {
            // If the rig was inactive when this card woke, retry once per second until it appears.
            if (_dayNight == null && Time.unscaledTime >= _nextResolveAttempt)
            {
                _nextResolveAttempt = Time.unscaledTime + 1f;
                EnsureRealDayNightCycle();
                if (_onButton == null || _offButton == null || _resetButton == null || _statusText == null)
                    AutoFindButtons();
                ConnectMissingListeners();
            }

            UpdateStatus();
        }

        // ---------------------------------------------------------------- panel lookup

        /// <summary>
        /// Best DayNight control card in the scene. There are often TWO panels with the same name
        /// (an active current card plus an inactive stale duplicate from an older Setup run), so a
        /// plain Transform.Find is not enough: score by active hierarchy, HUD parent and the
        /// required ON/OFF/Reset + StatusText children.
        /// </summary>
        public static Transform FindBestPanel()
        {
            Transform best = null;
            int bestScore = int.MinValue;

            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || t.name != PanelName) continue;

                int score = t.gameObject.activeInHierarchy ? 10 : 0;
                if (t.parent != null && t.parent.name == HudName) score += 4;
                if (t.Find(RowName + "/ON") != null) score += 3;
                if (t.Find("StatusText") != null) score += 2;
                if (t.GetComponentInChildren<Button>(true) != null) score += 1;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = t;
                }
            }
            return best;
        }

        private static Button FindButton(Transform panel, string name)
        {
            Button direct = panel.Find(name)?.GetComponent<Button>();
            if (direct != null) return direct;
            return panel.Find(RowName + "/" + name)?.GetComponent<Button>();
        }
    }
}
