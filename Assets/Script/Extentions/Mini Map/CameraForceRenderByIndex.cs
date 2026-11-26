using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Meta
{
    [AddComponentMenu("Meta/CameraForceRenderByIndex")]
    [HelpURL("https://github.com/DreamFaver")]
    // No [RequireComponent(typeof(Camera))]: adding this component to a UI/tool object must never
    // auto-create a Camera. The CameraForce tools create their helper camera explicitly.
    public class CameraForceRenderByIndex : MonoBehaviour
    {
        [Tooltip("Renderer index to force this camera to use (from URP Asset's rendererDataList).")]
        [Min(0)]
        public int RendererIndex = 0;

        [Header("Debug")]
        [SerializeField] private bool _enableLog = false;

        private Camera _camera;
        private UniversalAdditionalCameraData _cameraData;

        // URP exposes SetRenderer(int) but no public getter. Cache the original value before the
        // first force so disabling the component can put the camera back exactly as it was.
        private int _originalRendererIndex = int.MinValue;
        private bool _isApplied;
        private int _lastAppliedIndex = int.MinValue;
        private int _lastInvalidIndex = int.MinValue;

        private bool _warnedMissingCamera;
        private bool _warnedMissingCameraData;
        private bool _warnedMissingUrp;

        private static FieldInfo s_RendererIndexField;

        public bool EnableLog
        {
            get => _enableLog;
            set => _enableLog = value;
        }

        /// <summary>True while this component is actively overriding the camera's renderer.</summary>
        public bool IsApplied => _isApplied;

        /// <summary>Renderer index the camera had before the current force was applied.</summary>
        public int OriginalRendererIndex => _originalRendererIndex;

        /// <summary>
        /// Renderer index currently stored on the camera data (-1 = URP default renderer).
        /// Safe to read while the component is disabled/restored.
        /// </summary>
        public int CurrentRendererIndex => ReadRendererIndex(_cameraData);

        /// <summary>Number of renderers in the active URP asset (0 when URP is unavailable).</summary>
        public int AvailableRendererCount => GetUrpRendererCount();

        private void Awake()
        {
            ResolveReferences();
        }

        private void OnEnable()
        {
            if (ResolveReferences())
                ApplyRendererIndex();
        }

        private void OnDisable()
        {
            RestoreOriginalRendererIndex();
        }

        private void OnDestroy()
        {
            RestoreOriginalRendererIndex();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (RendererIndex < 0)
                RendererIndex = 0;

            // Only apply in Play mode while the behaviour is actually enabled.
            if (Application.isPlaying && isActiveAndEnabled)
                ApplyRendererIndex();
        }
#endif

        /// <summary>
        /// Public method to trigger renderer index validation from tests, editor scripts, or runtime.
        /// While disabled it only clamps the serialized value; it never touches the camera.
        /// </summary>
        public void ValidateRendererIndex()
        {
            if (!isActiveAndEnabled)
            {
                ClampFieldIfPossible();
                return;
            }

            ApplyRendererIndex();
        }

        private bool ResolveReferences()
        {
            if (_camera == null)
                _camera = GetComponent<Camera>();

            if (_camera == null)
            {
                WarnOnce(ref _warnedMissingCamera,
                    $"[CameraForceRenderByIndex] No Camera on '{gameObject.name}'. Nothing to configure.");
                return false;
            }

            if (_cameraData == null)
                _cameraData = _camera.GetUniversalAdditionalCameraData();

            if (_cameraData == null)
            {
                WarnOnce(ref _warnedMissingCameraData,
                    $"[CameraForceRenderByIndex] Could not get UniversalAdditionalCameraData for '{_camera.name}'.");
                return false;
            }

            return true;
        }

        private void ApplyRendererIndex()
        {
            if (!ResolveReferences())
                return;

            int rendererCount = GetUrpRendererCount();
            if (rendererCount <= 0)
            {
                WarnOnce(ref _warnedMissingUrp,
                    "[CameraForceRenderByIndex] No URP asset found. Renderer index cannot be applied.");
                return;
            }

            int requestedIndex = RendererIndex;
            int appliedIndex = requestedIndex;

            if (requestedIndex < 0 || requestedIndex >= rendererCount)
            {
                appliedIndex = Mathf.Clamp(requestedIndex, 0, rendererCount - 1);

                // Warn once per distinct invalid value, not on every button press / OnValidate tick.
                if (_lastInvalidIndex != requestedIndex)
                {
                    _lastInvalidIndex = requestedIndex;
                    Debug.LogWarning(
                        $"[CameraForceRenderByIndex] RendererIndex {requestedIndex} is out of range. " +
                        $"Valid range: 0 to {rendererCount - 1} (Total renderers: {rendererCount}). " +
                        $"Clamping to {appliedIndex}.");
                }

                RendererIndex = appliedIndex;
            }
            else
            {
                _lastInvalidIndex = int.MinValue;
            }

            if (!_isApplied)
            {
                _originalRendererIndex = ReadRendererIndex(_cameraData);
                _isApplied = true;
            }

            _cameraData.SetRenderer(appliedIndex);

            if (_enableLog && _lastAppliedIndex != appliedIndex)
            {
                Debug.Log($"[CameraForceRenderByIndex] Renderer index set to {appliedIndex} " +
                          $"(Total renderers: {rendererCount}).");
            }

            _lastAppliedIndex = appliedIndex;
        }

        private void RestoreOriginalRendererIndex()
        {
            if (!_isApplied)
                return;

            _isApplied = false;

            var data = _cameraData;
            if (data == null && _camera != null)
                data = _camera.GetUniversalAdditionalCameraData();

            if (data == null)
            {
                _originalRendererIndex = int.MinValue;
                _lastAppliedIndex = int.MinValue;
                return;
            }

            // -1 means "use the pipeline's default renderer", which is URP's unset value.
            int original = _originalRendererIndex == int.MinValue ? -1 : _originalRendererIndex;
            data.SetRenderer(original);

            if (_enableLog)
            {
                Debug.Log($"[CameraForceRenderByIndex] Restored original renderer index " +
                          $"{(original < 0 ? "(default)" : original.ToString())} on '{_camera.name}'.");
            }

            // Re-capture the camera's current index the next time the force is enabled, so an
            // external system that changed it while we were off is respected.
            _originalRendererIndex = int.MinValue;
            _lastAppliedIndex = int.MinValue;
        }

        private void ClampFieldIfPossible()
        {
            int rendererCount = GetUrpRendererCount();
            if (rendererCount <= 0)
            {
                if (RendererIndex < 0)
                    RendererIndex = 0;
                return;
            }

            if (RendererIndex < 0 || RendererIndex >= rendererCount)
                RendererIndex = Mathf.Clamp(RendererIndex, 0, rendererCount - 1);
        }

        private static int GetUrpRendererCount()
        {
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
                urpAsset = UniversalRenderPipeline.asset;

            return urpAsset != null ? urpAsset.rendererDataList.Length : 0;
        }

        /// <summary>
        /// Reads URP's private serialized renderer index. This is the only reliable way to know the
        /// camera's current index on all URP versions (SetRenderer is public, the getter is not).
        /// </summary>
        private static int ReadRendererIndex(UniversalAdditionalCameraData data)
        {
            if (data == null)
                return -1;

            if (s_RendererIndexField == null)
            {
                s_RendererIndexField = typeof(UniversalAdditionalCameraData).GetField(
                    "m_RendererIndex",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (s_RendererIndexField != null)
            {
                try
                {
                    return (int)s_RendererIndexField.GetValue(data);
                }
                catch
                {
                    // Fall through to the safe default below.
                }
            }

            return -1;
        }

        private static void WarnOnce(ref bool flag, string message)
        {
            if (flag)
                return;

            flag = true;
            Debug.LogWarning(message);
        }
    }
}
