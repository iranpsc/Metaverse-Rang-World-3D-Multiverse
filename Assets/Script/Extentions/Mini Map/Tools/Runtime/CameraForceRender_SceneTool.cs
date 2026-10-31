using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// Runtime helper to connect the CameraForceRender control panel to CameraForceRenderByIndex.
    /// Buttons created by CameraForceRender_TestRigCreator already carry PERSISTENT listeners
    /// that point at the public methods below; manual setups are wired in Awake.
    /// </summary>
    [AddComponentMenu("Meta/Tools/CameraForceRender Scene Tool")]
    [RequireComponent(typeof(CameraForceRenderByIndex))]
    [RequireComponent(typeof(Camera))]
    public class CameraForceRender_SceneTool : MonoBehaviour
    {
        [Header("References (auto-filled if on same GO)")]
        [SerializeField] private CameraForceRenderByIndex _forceRender;
        [SerializeField] private Camera _camera;
        [SerializeField] private Button _applyIndex0Button;
        [SerializeField] private Button _applyIndex1Button;
        [SerializeField] private Button _applyInvalidButton;
        [SerializeField] private Button _reinitButton;
        [SerializeField] private Slider _indexSlider;
        [SerializeField] private Text _statusText;

        [Header("Set by CameraForceRender_TestRigCreator")]
        [Tooltip("True when the editor tool already serialized persistent listeners onto the buttons")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private void Awake()
        {
            if (_forceRender == null)
                _forceRender = GetComponent<CameraForceRenderByIndex>();
            if (_camera == null)
                _camera = GetComponent<Camera>();

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
            if (_forceRender == null || _indexSlider == null) return;

            _indexSlider.minValue = 0;
            _indexSlider.maxValue = 10;
            _indexSlider.wholeNumbers = true;
            _indexSlider.SetValueWithoutNotify(_forceRender.RendererIndex);
            _indexSlider.onValueChanged.AddListener(SetIndexFromSlider);
        }

        // ---- Button targets (referenced by the editor tool's persistent listeners) ----

        public void ApplyIndex0() => ApplyIndex(0);

        public void ApplyIndex1() => ApplyIndex(1);

        public void ApplyInvalid99() => ApplyIndex(99);

        public void Reinitialize()
        {
            if (_forceRender != null) _forceRender.ValidateRendererIndex();
        }

        public void SetIndexFromSlider(float value)
        {
            if (_forceRender != null)
            {
                _forceRender.RendererIndex = Mathf.RoundToInt(value);
                _forceRender.ValidateRendererIndex();
            }
        }

        private void ApplyIndex(int index)
        {
            if (_forceRender == null) return;
            _forceRender.RendererIndex = Mathf.Max(0, index);
            _forceRender.ValidateRendererIndex();
        }

        private void AutoFindUI()
        {
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var panel = canvas.transform.Find("Meta_CameraForce_Controls");
                if (panel == null) continue;

                _applyIndex0Button = panel.Find("Apply Index 0")?.GetComponent<Button>();
                _applyIndex1Button = panel.Find("Apply Index 1")?.GetComponent<Button>();
                _applyInvalidButton = panel.Find("Apply Invalid 99")?.GetComponent<Button>();
                _reinitButton = panel.Find("Reinitialize")?.GetComponent<Button>();
                _indexSlider = panel.Find("RendererIndex Slider")?.GetComponent<Slider>();
                _statusText = panel.Find("InfoText")?.GetComponent<Text>();
                return;
            }
        }

        private void ConnectUI()
        {
            if (_forceRender == null) return;

            // Buttons may already carry persistent listeners from the editor tool
            if (!ButtonsWiredExternally)
            {
                if (_applyIndex0Button != null) _applyIndex0Button.onClick.AddListener(ApplyIndex0);
                if (_applyIndex1Button != null) _applyIndex1Button.onClick.AddListener(ApplyIndex1);
                if (_applyInvalidButton != null) _applyInvalidButton.onClick.AddListener(ApplyInvalid99);
                if (_reinitButton != null) _reinitButton.onClick.AddListener(Reinitialize);
            }

            // Slider is always bound at runtime (float persistent listeners need a static argument)
            if (_indexSlider != null)
            {
                _indexSlider.minValue = 0;
                _indexSlider.maxValue = 10;
                _indexSlider.wholeNumbers = true;
                _indexSlider.SetValueWithoutNotify(_forceRender.RendererIndex);
                _indexSlider.onValueChanged.AddListener(SetIndexFromSlider);
            }
        }

        private void Update()
        {
            if (_statusText == null || _forceRender == null || _camera == null) return;

            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            int rendererCount = urpAsset != null ? urpAsset.rendererDataList.Length : 0;
            var camData = _camera.GetUniversalAdditionalCameraData();
            int currentIndex = -1;
            if (camData != null)
            {
                // URP version differences: property first, then method
                var rendererIndexProperty = camData.GetType().GetProperty("rendererIndex");
                var getRendererIndexMethod = camData.GetType().GetMethod("GetRendererIndex");
                if (rendererIndexProperty != null)
                    currentIndex = (int)rendererIndexProperty.GetValue(camData);
                else if (getRendererIndexMethod != null)
                    currentIndex = (int)getRendererIndexMethod.Invoke(camData, null);
            }

            _statusText.text = $"Renderers: {rendererCount}\n" +
                               $"Current Index: {currentIndex}\n" +
                               $"Target Index: {_forceRender.RendererIndex}\n" +
                               $"CameraData: {(camData != null ? "OK" : "MISSING")}";
        }
    }
}