using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// DayNight-only Play-mode control panel (ON/OFF/Reset). No MiniMap / CameraForce refs.
    /// Created by DayNight_ControlPanel_Setup in Edit mode.
    /// </summary>
    [AddComponentMenu("Meta/Tools/DayNight Control Panel")]
    [RequireComponent(typeof(Meta_DayNightCycle))]
    public class DayNight_ControlPanel : MonoBehaviour
    {
        [Header("Auto-filled if on same GO")]
        [SerializeField] private Meta_DayNightCycle _dayNight;
        [SerializeField] private Button _onButton;
        [SerializeField] private Button _offButton;
        [SerializeField] private Button _resetButton;
        [SerializeField] private Text _statusText;

        [Header("Set by Setup tool so Awake doesn't double-wire")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private void Awake()
        {
            if (_dayNight == null)
                _dayNight = GetComponent<Meta_DayNightCycle>();

            if (!ButtonsWiredExternally)
            {
                AutoFindButtons();
                ConnectButtons();
            }
        }

        private void AutoFindButtons()
        {
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var panel = canvas.transform.Find("Meta_DayNight_ControlPanel");
                if (panel == null) continue;

                _onButton = panel.Find("ON")?.GetComponent<Button>();
                _offButton = panel.Find("OFF")?.GetComponent<Button>();
                _resetButton = panel.Find("Reset")?.GetComponent<Button>();
                _statusText = panel.Find("StatusText")?.GetComponent<Text>();
                return;
            }
        }

        private void ConnectButtons()
        {
            if (_dayNight == null) return;
            if (_onButton != null) _onButton.onClick.AddListener(OnClicked);
            if (_offButton != null) _offButton.onClick.AddListener(OffClicked);
            if (_resetButton != null) _resetButton.onClick.AddListener(ResetClicked);
        }

        // ---- Button targets (persistent listeners from Setup tool also call these) ----

        public void OnClicked()
        {
            if (_dayNight == null) return;
            _dayNight.ToggleCycle(true);
            UpdateStatus();
        }

        public void OffClicked()
        {
            if (_dayNight == null) return;
            _dayNight.ToggleCycle(false);
            UpdateStatus();
        }

        public void ResetClicked()
        {
            if (_dayNight == null) return;
            _dayNight.ForceResetToDefault();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_statusText == null || _dayNight == null) return;

            string sunInfo = _dayNight.Sun != null
                ? $"Light: Int {_dayNight.Sun.intensity:F2}  RotX {_dayNight.Sun.transform.eulerAngles.x:F1}°"
                : "Light: NONE";

            // Proof for the user that the VISIBLE sun object exists (a light alone is invisible).
            string discInfo = _dayNight.HasVisibleSun
                ? $"Disc: {_dayNight.VisibleSunTransform.name}"
                : "Disc: none (runtime create on ON)";

            // Authority mode matters: an ApplyOnly peer ignores ON/OFF and only renders ApplyState().
            string role = _dayNight.SimulatesLocally ? "owner: simulate" : "visitor: apply-only";
            string place = _dayNight.PersistenceEnabled ? _dayNight.EnvironmentId : "place: not persisted";
            string time = $"Time: {_dayNight.Time01:F3} ({_dayNight.SunAngle:F0}deg)\n{role}\n{place}";

            _statusText.text = $"Cycle: {(_dayNight.IsEnabled ? "ON" : "OFF")}\n{sunInfo}\n{discInfo}\n{time}";
        }

        private void Update()
        {
            UpdateStatus();
        }
    }
}