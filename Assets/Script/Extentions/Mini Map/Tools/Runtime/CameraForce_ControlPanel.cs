using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// CameraForce-only Play-mode control card (Enable / Disable + Default (0) / Alt (1)).
    /// No MiniMap / DayNight refs. Created by CameraForce_ControlPanel_Setup in Edit mode.
    /// </summary>
    [AddComponentMenu("Meta/Tools/CameraForce Control Panel")]
    // No [RequireComponent(CameraForceRenderByIndex)]: the control card only references the real
    // rig camera. Requiring the component made Unity auto-add CameraForceRenderByIndex + Camera to
    // the UI runner root, creating a rogue camera and a ghost renderer that could steal rendering.
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

        [Header("Legacy serialized flag (listeners are checked at runtime)")]
        [HideInInspector] public bool ButtonsWiredExternally;

        private string _lastStatus;
        private float _nextStatusRefresh;

        private void Awake()
        {
            ResolveReferences();
            AutoFindButtons();   // also repairs missing serialized refs in old scenes
            WireButtons();       // adds only the listeners that are not already persistent
        }

        private void ResolveReferences()
        {
            if (_cameraForce == null)
                _cameraForce = GetComponent<CameraForceRenderByIndex>();

            if (_camera == null && _cameraForce != null)
                _camera = _cameraForce.GetComponent<Camera>();

            if (_camera == null)
                _camera = GetComponent<Camera>();
        }

        private void AutoFindButtons()
        {
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var panel = canvas.transform.Find("Meta_CameraForce_ControlPanel");
                if (panel == null) continue;

                // Setup creates ButtonsRow/IndexRow; older cards used flat children.
                _onButton = FindButton(panel, "ButtonsRow/Enable", "Enable");
                _offButton = FindButton(panel, "ButtonsRow/Disable", "Disable");
                _applyIndex0Button = FindButton(panel, "IndexRow/Default (0)", "Default (0)");
                _applyIndex1Button = FindButton(panel, "IndexRow/Alt (1)", "Alt (1)");
                _statusText = panel.Find("StatusText")?.GetComponent<Text>();
                return;
            }
        }

        private static Button FindButton(Transform panel, string nestedPath, string flatName)
        {
            var nested = panel.Find(nestedPath);
            if (nested != null) return nested.GetComponent<Button>();

            var flat = panel.Find(flatName);
            return flat != null ? flat.GetComponent<Button>() : null;
        }

        private void WireButtons()
        {
            Bind(_onButton, nameof(OnClicked), OnClicked);
            Bind(_offButton, nameof(OffClicked), OffClicked);
            Bind(_applyIndex0Button, nameof(ApplyIndex0Clicked), ApplyIndex0Clicked);
            Bind(_applyIndex1Button, nameof(ApplyIndex1Clicked), ApplyIndex1Clicked);
        }

        /// <summary>
        /// Adds a runtime listener only when the button has no persistent listener pointing at this
        /// component's method. Setup creates persistent listeners, so Play mode does not double-fire.
        /// </summary>
        private void Bind(Button button, string methodName, UnityAction action)
        {
            if (button == null || action == null)
                return;

            if (HasPersistentListener(button, methodName))
                return;

            button.onClick.AddListener(action);
        }

        private bool HasPersistentListener(Button button, string methodName)
        {
            var evt = button.onClick;
            int count = evt.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                if (evt.GetPersistentTarget(i) == this &&
                    evt.GetPersistentMethodName(i) == methodName)
                {
                    return true;
                }
            }

            return false;
        }

        public void OnClicked()
        {
            if (_cameraForce == null)
            {
                UpdateStatus();
                return;
            }

            _cameraForce.enabled = true;             // OnEnable applies the renderer index
            _cameraForce.ValidateRendererIndex();
            UpdateStatus();
        }

        public void OffClicked()
        {
            if (_cameraForce == null)
            {
                UpdateStatus();
                return;
            }

            _cameraForce.enabled = false;            // OnDisable restores the original renderer
            UpdateStatus();
        }

        public void ApplyIndex0Clicked()
        {
            ApplyIndex(0);
        }

        public void ApplyIndex1Clicked()
        {
            ApplyIndex(1);
        }

        private void ApplyIndex(int index)
        {
            if (_cameraForce == null)
            {
                UpdateStatus();
                return;
            }

            _cameraForce.RendererIndex = index;
            _cameraForce.ValidateRendererIndex();
            UpdateStatus();
        }

        /// <summary>
        /// Plain-language status. "Enabled: True" tells a normal user nothing, so the line reads
        /// as a sentence and names the renderer that is actually in use.
        /// </summary>
        private void UpdateStatus()
        {
            if (_statusText == null)
                return;

            string text;

            if (_cameraForce == null)
            {
                text = "Status: not connected - run Tools/Meta/CameraForce/Setup.";
            }
            else if (!_cameraForce.isActiveAndEnabled)
            {
                text = "Status: off - the camera is not using a forced renderer.";
            }
            else
            {
                int rendererCount = _cameraForce.AvailableRendererCount;
                if (rendererCount <= 0)
                {
                    text = "Status: on - no URP renderer data found in this project.";
                }
                else
                {
                    int currentIndex = _cameraForce.CurrentRendererIndex;
                    if (currentIndex < 0)
                        currentIndex = _cameraForce.RendererIndex;

                    string which = currentIndex == 1
                        ? "Alt (1)"
                        : currentIndex == 0
                            ? "Default (0)"
                            : $"renderer {currentIndex}";

                    string note = rendererCount <= 1
                        ? "\nOnly 1 renderer exists here, so both choices look the same."
                        : "";

                    text = $"Status: on - using renderer {currentIndex} ({which}).{note}";
                }
            }

            if (_lastStatus == text)
                return;

            _lastStatus = text;
            _statusText.text = text;
        }

        private void Update()
        {
            // Slow refresh: an external script may enable/disable or change the index, but there is
            // no need to rebuild the status string every frame.
            if (Time.unscaledTime < _nextStatusRefresh)
                return;

            _nextStatusRefresh = Time.unscaledTime + 0.25f;
            UpdateStatus();
        }
    }
}
