using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// Runtime helper to connect the legacy CameraForceRender dev card to CameraForceRenderByIndex.
    /// Buttons created by CameraForceRender_TestRigCreator (old versions) may carry persistent
    /// listeners; this component adds a runtime listener only when one is missing.
    /// </summary>
    [AddComponentMenu("Meta/Tools/CameraForceRender Scene Tool")]
    // No [RequireComponent]: the old chain auto-added CameraForceRenderByIndex + Camera to UI/tool
    // objects, which produced rogue cameras and duplicate AudioListeners. References are resolved
    // explicitly and the tool is a no-op when no real rig camera exists.
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

        [Header("Legacy serialized flag (listeners are checked at runtime)")]
        [Tooltip("Kept for old scenes. Persistent listeners are detected at runtime, not trusted blindly.")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private string _lastStatus;
        private float _nextStatusRefresh;

        private void Awake()
        {
            ResolveReferences();
            AutoFindUI();
            WireButtons();
            BindSlider();
        }

        private void ResolveReferences()
        {
            if (_forceRender == null)
                _forceRender = GetComponent<CameraForceRenderByIndex>();

            if (_camera == null && _forceRender != null)
                _camera = _forceRender.GetComponent<Camera>();

            if (_camera == null)
                _camera = GetComponent<Camera>();
        }

        // ---- Button targets (referenced by the editor tool's persistent listeners) ----

        public void ApplyIndex0() => ApplyIndex(0);

        public void ApplyIndex1() => ApplyIndex(1);

        public void ApplyInvalid99() => ApplyIndex(99);

        public void Reinitialize()
        {
            if (_forceRender != null)
                _forceRender.ValidateRendererIndex();
        }

        public void SetIndexFromSlider(float value)
        {
            if (_forceRender == null)
                return;

            _forceRender.RendererIndex = Mathf.Max(0, Mathf.RoundToInt(value));
            _forceRender.ValidateRendererIndex();
        }

        private void ApplyIndex(int index)
        {
            if (_forceRender == null)
                return;

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

        private void WireButtons()
        {
            Bind(_applyIndex0Button, nameof(ApplyIndex0), ApplyIndex0);
            Bind(_applyIndex1Button, nameof(ApplyIndex1), ApplyIndex1);
            Bind(_applyInvalidButton, nameof(ApplyInvalid99), ApplyInvalid99);
            Bind(_reinitButton, nameof(Reinitialize), Reinitialize);
        }

        private void Bind(Button button, string methodName, UnityAction action)
        {
            if (button == null || action == null)
                return;

            var evt = button.onClick;
            int count = evt.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                if (evt.GetPersistentTarget(i) == this &&
                    evt.GetPersistentMethodName(i) == methodName)
                {
                    return;
                }
            }

            button.onClick.AddListener(action);
        }

        private void BindSlider()
        {
            if (_forceRender == null || _indexSlider == null)
                return;

            int count = Mathf.Max(1, _forceRender.AvailableRendererCount);
            _indexSlider.minValue = 0;
            _indexSlider.maxValue = Mathf.Max(10, count - 1);
            _indexSlider.wholeNumbers = true;
            _indexSlider.SetValueWithoutNotify(_forceRender.RendererIndex);
            _indexSlider.onValueChanged.AddListener(SetIndexFromSlider);
        }

        private void Update()
        {
            if (_statusText == null || _forceRender == null)
                return;

            if (Time.unscaledTime < _nextStatusRefresh)
                return;
            _nextStatusRefresh = Time.unscaledTime + 0.25f;

            int rendererCount = _forceRender.AvailableRendererCount;
            int currentIndex = _forceRender.CurrentRendererIndex;
            if (currentIndex < 0)
                currentIndex = _forceRender.RendererIndex;

            string text = $"Renderers: {rendererCount}\n" +
                          $"Current Index: {currentIndex}\n" +
                          $"Target Index: {_forceRender.RendererIndex}\n" +
                          $"Force: {(_forceRender.isActiveAndEnabled ? "ON" : "OFF")}";

            if (_lastStatus == text)
                return;

            _lastStatus = text;
            _statusText.text = text;
        }
    }
}
