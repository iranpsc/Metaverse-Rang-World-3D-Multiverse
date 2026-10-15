using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// Runtime helper to connect DayNight control panel buttons to Meta_DayNightCycle.
    /// Buttons created by DayNight_TestRigCreator already carry PERSISTENT listeners
    /// that point at the public methods below; manual setups are wired in Awake.
    /// </summary>
    [AddComponentMenu("Meta/Tools/DayNight Scene Tool")]
    [RequireComponent(typeof(Meta_DayNightCycle))]
    public class DayNight_SceneTool : MonoBehaviour
    {
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
            if (_dayNight == null)
                _dayNight = GetComponent<Meta_DayNightCycle>();

            if (!ButtonsWiredExternally)
            {
                AutoFindUI();
                ConnectUI();
            }
            else
            {
                // Persistent listeners cover the buttons; the slider still needs a runtime bind.
                AutoFindUI();
                BindSliderOnly();
            }
        }

        private void BindSliderOnly()
        {
            if (_dayNight == null || _rotationSpeedSlider == null) return;

            _rotationSpeedSlider.minValue = 0f;
            _rotationSpeedSlider.maxValue = 1f;
            _rotationSpeedSlider.SetValueWithoutNotify(_dayNight.RotationSpeed);
            _rotationSpeedSlider.onValueChanged.AddListener(SetRotationSpeed);
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
            if (_dayNight != null) _dayNight.RotationSpeed = value;
        }

        private void AutoFindUI()
        {
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var panel = canvas.transform.Find("Meta_DayNight_Controls");
                if (panel == null) continue;

                _toggleButton = panel.Find("Toggle Cycle")?.GetComponent<Button>();
                _forceResetButton = panel.Find("Reset")?.GetComponent<Button>();
                _rotationSpeedSlider = panel.Find("RotationSpeed Slider")?.GetComponent<Slider>();
                _statusText = panel.Find("StatusText")?.GetComponent<Text>();
                return;
            }
        }

        private void ConnectUI()
        {
            if (_dayNight == null) return;

            // Buttons may already carry persistent listeners from the editor tool
            if (!ButtonsWiredExternally)
            {
                if (_toggleButton != null) _toggleButton.onClick.AddListener(ToggleCycle);
                if (_forceResetButton != null) _forceResetButton.onClick.AddListener(ForceReset);
            }

            // Slider is always bound at runtime (float persistent listeners need a static argument)
            if (_rotationSpeedSlider != null)
            {
                _rotationSpeedSlider.minValue = 0f;
                _rotationSpeedSlider.maxValue = 1f;
                _rotationSpeedSlider.SetValueWithoutNotify(_dayNight.RotationSpeed);
                _rotationSpeedSlider.onValueChanged.AddListener(SetRotationSpeed);
            }
        }

        private void Update()
        {
            if (_statusText == null || _dayNight == null) return;

            var sun = _dayNight.Sun;
            if (sun == null) return;

            _statusText.text = $"Cycle: {(_dayNight.IsEnabled ? "ON" : "OFF")}\n" +
                               $"Rotation: {sun.transform.rotation.eulerAngles.x:F1}°\n" +
                               $"Intensity: {sun.intensity:F2}\n" +
                               $"Ambient: {RenderSettings.ambientIntensity:F2}\n" +
                               $"Speed: {_dayNight.RotationSpeed:F3}";
        }
    }
}