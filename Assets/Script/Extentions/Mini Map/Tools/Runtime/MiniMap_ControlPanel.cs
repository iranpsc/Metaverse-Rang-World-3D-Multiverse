using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// MiniMap-only Play-mode control panel (ON/OFF). No DayNight / CameraForce refs.
    /// Created by MiniMap_ControlPanel_Setup in Edit mode.
    /// </summary>
    [AddComponentMenu("Meta/Tools/MiniMap Control Panel")]
    [RequireComponent(typeof(Meta_MiniMap))]
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

        private void Awake()
        {
            if (_miniMap == null)
                _miniMap = GetComponent<Meta_MiniMap>();

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
            if (_miniMap == null) return;
            if (_onButton != null) _onButton.onClick.AddListener(OnClicked);
            if (_offButton != null) _offButton.onClick.AddListener(OffClicked);
            if (_zoomInButton != null) _zoomInButton.onClick.AddListener(ZoomInClicked);
            if (_zoomOutButton != null) _zoomOutButton.onClick.AddListener(ZoomOutClicked);
        }

        // ---- Button targets (persistent listeners from Setup tool also call these) ----

        public void OnClicked()
        {
            if (_miniMap == null) return;
            _miniMap.ToggleMiniMap(true);
            UpdateStatus();
        }

        public void OffClicked()
        {
            if (_miniMap == null) return;
            _miniMap.ToggleMiniMap(false);
            UpdateStatus();
        }

        public void ZoomInClicked()
        {
            if (_miniMap == null) return;
            _miniMap.ZoomInButton();
            UpdateStatus();
        }

        public void ZoomOutClicked()
        {
            if (_miniMap == null) return;
            _miniMap.ZoomOutButton();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_statusText == null || _miniMap == null) return;

            string targetName = _miniMap.Target != null ? _miniMap.Target.name : "NULL (waiting for local player)";
            _statusText.text = $"IsEnabled: {_miniMap.IsEnabled}\nTarget: {targetName}\nCamera: {(_miniMap.MiniMapCamera != null ? _miniMap.MiniMapCamera.enabled.ToString() : "NULL")}";
        }

        private void Update()
        {
            UpdateStatus();
        }
    }
}