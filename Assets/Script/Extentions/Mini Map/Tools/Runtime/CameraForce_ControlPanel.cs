using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// CameraForce-only Play-mode control card (Enable / Disable + Default (0) / Alt (1)).
    /// No MiniMap / DayNight refs. Created by CameraForce_ControlPanel_Setup in Edit mode.
    /// </summary>
    [AddComponentMenu("Meta/Tools/CameraForce Control Panel")]
    [RequireComponent(typeof(CameraForceRenderByIndex))]
    public class CameraForce_ControlPanel : MonoBehaviour
    {
        [Header("Auto-filled if on same GO")]
        [SerializeField] private CameraForceRenderByIndex _cameraForce;
        [SerializeField] private Camera _camera;
        [SerializeField] private Button _onButton;
        [SerializeField] private Button _offButton;
        [SerializeField] private Button _applyIndex0Button;
        [SerializeField] private Button _applyIndex1Button;
        [SerializeField] private Text _statusText;

        [Header("Set by Setup tool so Awake doesn't double-wire")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private void Awake()
        {
            if (_cameraForce == null)
                _cameraForce = GetComponent<CameraForceRenderByIndex>();
            if (_camera == null)
                _camera = GetComponent<Camera>();

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
                var panel = canvas.transform.Find("Meta_CameraForce_ControlPanel");
                if (panel == null) continue;

                // Button names must match CameraForce_ControlPanel_Setup.
                _onButton = panel.Find("Enable")?.GetComponent<Button>();
                _offButton = panel.Find("Disable")?.GetComponent<Button>();
                _applyIndex0Button = panel.Find("Default (0)")?.GetComponent<Button>();
                _applyIndex1Button = panel.Find("Alt (1)")?.GetComponent<Button>();
                _statusText = panel.Find("StatusText")?.GetComponent<Text>();
                return;
            }
        }

        private void ConnectButtons()
        {
            if (_cameraForce == null) return;
            if (_onButton != null) _onButton.onClick.AddListener(OnClicked);
            if (_offButton != null) _offButton.onClick.AddListener(OffClicked);
            if (_applyIndex0Button != null) _applyIndex0Button.onClick.AddListener(ApplyIndex0Clicked);
            if (_applyIndex1Button != null) _applyIndex1Button.onClick.AddListener(ApplyIndex1Clicked);
        }

        public void OnClicked()
        {
            if (_cameraForce == null) return;
            _cameraForce.enabled = true;
            _cameraForce.ValidateRendererIndex();
            UpdateStatus();
        }

        public void OffClicked()
        {
            if (_cameraForce == null) return;
            _cameraForce.enabled = false;
            UpdateStatus();
        }

        public void ApplyIndex0Clicked()
        {
            if (_cameraForce == null) return;
            _cameraForce.RendererIndex = 0;
            _cameraForce.ValidateRendererIndex();
            UpdateStatus();
        }

        public void ApplyIndex1Clicked()
        {
            if (_cameraForce == null) return;
            _cameraForce.RendererIndex = 1;
            _cameraForce.ValidateRendererIndex();
            UpdateStatus();
        }

        /// <summary>
        /// Plain-language status. "Enabled: True" tells a normal user nothing, so the line reads
        /// as a sentence and names the renderer that is actually in use.
        /// </summary>
        private void UpdateStatus()
        {
            if (_statusText == null || _cameraForce == null || _camera == null) return;

            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int rendererCount = urpAsset != null ? urpAsset.rendererDataList.Length : 0;
            var camData = _camera.GetUniversalAdditionalCameraData();
            int currentIndex = -1;
            if (camData != null)
            {
                var prop = camData.GetType().GetProperty("rendererIndex");
                var method = camData.GetType().GetMethod("GetRendererIndex");
                if (prop != null) currentIndex = (int)prop.GetValue(camData);
                else if (method != null) currentIndex = (int)method.Invoke(camData, null);
            }

            if (!_cameraForce.enabled)
            {
                _statusText.text = "Status: off - the camera is not using a forced renderer.";
                return;
            }

            if (rendererCount <= 0)
            {
                _statusText.text = "Status: on - no URP renderer data found in this project.";
                return;
            }

            string which = currentIndex == 1 ? "Alt (1)" : "Default (0)";
            string note = rendererCount <= 1
                ? "\nOnly 1 renderer exists here, so both choices look the same."
                : "";

            _statusText.text = $"Status: on - using renderer {currentIndex} ({which}).{note}";
        }

        private void Update()
        {
            UpdateStatus();
        }
    }
}