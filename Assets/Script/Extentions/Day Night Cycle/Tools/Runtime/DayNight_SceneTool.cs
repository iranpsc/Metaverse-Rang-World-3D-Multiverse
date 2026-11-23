using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// Runtime helper to connect DayNight control panel buttons to Meta_DayNightCycle.
    /// Buttons created by DayNight_TestRigCreator already carry PERSISTENT listeners
    /// that point at the public methods below; manual setups are wired in Awake.
    /// The tool never promotes a visitor to owner: ToggleCycle() is gated inside the cycle.
    /// </summary>
    [AddComponentMenu("Meta/Tools/DayNight Scene Tool")]
    public class DayNight_SceneTool : MonoBehaviour
    {
        /// <summary>
        /// Slider ceiling in deg/s. Matches the restored-speed band, so a drag can never create a
        /// value the next Play would reject as a scene-pacing hijack (old demo values 200/12).
        /// </summary>
        public const float MaxSliderSpeed = Meta_DayNightCycle.MaxRestoredSpeed;

        [Header("References (auto-filled if on same GO)")]
        [SerializeField] private Meta_DayNightCycle _dayNight;
        [SerializeField] private Button _toggleButton;
        [SerializeField] private Button _forceResetButton;
        [SerializeField] private Slider _rotationSpeedSlider;
        [SerializeField] private Text _statusText;

        [Header("Set by DayNight_TestRigCreator")]
        [Tooltip("True when the editor tool already serialized persistent listeners onto the buttons")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private void Awake()
        {
            // A serialized ghost duplicate (no sun / on the ControlRunner) must never shadow the
            // real cycle. Prefer a valid serialized reference, then the best rig in the scene.
            if (_dayNight == null || DayNight_ControlPanel.IsGhostCandidate(_dayNight))
            {
                var real = DayNight_ControlPanel.BestDayNightInstance(transform);
                _dayNight = real;
            }

            if (_dayNight == null)
            {
                var own = GetComponent<Meta_DayNightCycle>();
                if (own != null && !DayNight_ControlPanel.IsGhostCandidate(own)) _dayNight = own;
            }

            AutoFindUI();
            ConnectUI();
        }

        // ---- Button targets (referenced by the editor tool's persistent listeners) ----

        public void ToggleCycle()
        {
            if (_dayNight != null) _dayNight.ToggleCycle(!_dayNight.IsEnabled);
        }

        public void ForceReset()
        {
            if (_dayNight != null) _dayNight.ForceResetToDefault();
        }

        public void SetRotationSpeed(float value)
        {
            // The slider is a UI control, not a scripted override: keep it inside the same
            // watchable band the store accepts. Direct component writes stay unrestricted.
            if (_dayNight != null)
                _dayNight.RotationSpeed = Mathf.Clamp(value, 0f, MaxSliderSpeed);
        }

        /// <summary>
        /// Finds the control card under BOTH generations: the legacy rig card
        /// ("Meta_DayNight_Controls" with a Toggle Cycle + RotationSpeed Slider) and the current
        /// ControlRunner card ("Meta_DayNight_ControlPanel" with ON/OFF/Reset rows).
        /// Plain `??` is avoided: a missing GetComponent<Button>() is Unity fake-null, which `??`
        /// would happily accept and skip the fallback lookup.
        /// </summary>
        private void AutoFindUI()
        {
            Transform panel = FindPanelByName("Meta_DayNight_Controls");
            if (panel == null) panel = DayNight_ControlPanel.FindBestPanel();
            if (panel == null) return;

            if (_toggleButton == null)
            {
                Button toggle = panel.Find("Toggle Cycle")?.GetComponent<Button>();
                if (toggle == null) toggle = panel.Find(DayNight_ControlPanel.RowName + "/ON")?.GetComponent<Button>();
                _toggleButton = toggle;
            }

            if (_forceResetButton == null)
            {
                Button reset = panel.Find("Reset")?.GetComponent<Button>();
                if (reset == null) reset = panel.Find(DayNight_ControlPanel.RowName + "/Reset")?.GetComponent<Button>();
                _forceResetButton = reset;
            }

            if (_rotationSpeedSlider == null)
                _rotationSpeedSlider = panel.GetComponentInChildren<Slider>(true);

            if (_statusText == null)
                _statusText = panel.Find("StatusText")?.GetComponent<Text>();
        }

        private static Transform FindPanelByName(string name)
        {
            Transform best = null;
            int bestScore = int.MinValue;

            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || t.name != name) continue;

                int score = t.gameObject.activeInHierarchy ? 10 : 0;
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

        private void ConnectUI()
        {
            if (_dayNight == null) return;

            // Buttons may already carry persistent listeners from the editor tool. Only add a
            // runtime listener when the button has none (legacy/manual scene wiring).
            if (_toggleButton != null && _toggleButton.onClick.GetPersistentEventCount() == 0)
                _toggleButton.onClick.AddListener(ToggleCycle);
            if (_forceResetButton != null && _forceResetButton.onClick.GetPersistentEventCount() == 0)
                _forceResetButton.onClick.AddListener(ForceReset);

            BindSlider();
        }

        private void BindSlider()
        {
            if (_dayNight == null || _rotationSpeedSlider == null) return;

            // deg/s scale (matches the Inspector field): 2 = 180 s day. The old maxValue = 1f
            // silently capped user drags below 1 deg/s; 200 allowed a ~1.8 s day.
            _rotationSpeedSlider.minValue = 0f;
            _rotationSpeedSlider.maxValue = MaxSliderSpeed;
            _rotationSpeedSlider.SetValueWithoutNotify(
                Mathf.Clamp(_dayNight.RotationSpeed, 0f, MaxSliderSpeed));

            _rotationSpeedSlider.onValueChanged.RemoveListener(SetRotationSpeed);
            _rotationSpeedSlider.onValueChanged.AddListener(SetRotationSpeed);
        }

        private void Update()
        {
            if (_statusText == null || _dayNight == null) return;

            // The ControlPanel card owns its own StatusText - never fight over it (two writers on
            // the same label every frame is exactly the "label desynced from numbers" symptom).
            var parent = _statusText.transform.parent;
            if (parent != null && parent.name == DayNight_ControlPanel.PanelName) return;

            var sun = _dayNight.Sun;
            if (sun == null) return;

            // Same phrase helper as the ControlPanel card: label derived from the fields the
            // simulation actually uses, so numbers and label can never contradict.
            _statusText.text = $"Cycle: {_dayNight.DescribeCycleState()}\n" +
                               $"Rotation: {sun.transform.rotation.eulerAngles.x:F1}°\n" +
                               $"Intensity: {sun.intensity:F2}\n" +
                               $"Ambient: {RenderSettings.ambientIntensity:F2}\n" +
                               $"Speed: {_dayNight.RotationSpeed:F3}";
        }
    }
}
