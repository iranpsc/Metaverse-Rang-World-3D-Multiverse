using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Meta
{
    [AddComponentMenu("Meta/CameraForceRenderByIndex")]
    [HelpURL("https://github.com/DreamFaver")]
    [RequireComponent(typeof(Camera))]
    public class CameraForceRenderByIndex : MonoBehaviour
    {
        [Tooltip("Renderer index to force this camera to use (from URP Asset's rendererDataList).")]
        [Min(0)]
        public int RendererIndex = 0;

        private Camera _camera;
        private UniversalAdditionalCameraData _cameraData;
        private int _cachedRendererCount = -1;

        [Header("Debug")]
        [SerializeField] private bool _enableLog = false;

        public bool EnableLog
        {
            get => _enableLog;
            set => _enableLog = value;
        }

        void Awake()
        {
            _camera = GetComponent<Camera>();
            if (_camera == null)
            {
                Debug.LogWarning($"[CameraForceRenderByIndex] No Camera on {gameObject.name}. Nothing to configure.");
                return;
            }

            // Works even on a bare camera: create the URP data when it is absent.
            if (!_camera.TryGetComponent(out _cameraData))
            {
                _cameraData = _camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                if (_enableLog)
                    Debug.Log($"[CameraForceRenderByIndex] UniversalAdditionalCameraData was missing on {gameObject.name} and has been added.");
            }

            ValidateAndApplyRendererIndex();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            // Clamp negative values in Editor
            if (RendererIndex < 0)
            {
                RendererIndex = 0;
            }

            // Only apply in Play mode when component is already initialized
            if (_cameraData != null && Application.isPlaying)
            {
                ValidateAndApplyRendererIndex();
            }
        }
#endif

        /// <summary>
        /// Public method to trigger renderer index validation from tests, editor scripts, or runtime.
        /// </summary>
        public void ValidateRendererIndex()
        {
            ValidateAndApplyRendererIndex();
        }

        private void ValidateAndApplyRendererIndex()
        {
            var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
            {
                Debug.LogWarning("[CameraForceRenderByIndex] URP Asset not found in GraphicsSettings. Renderer index cannot be validated.");
                return;
            }

            // URP 17+ exposes rendererDataList as ReadOnlySpan<ScriptableRendererData>
            int rendererCount = urpAsset.rendererDataList.Length;

            // Cache renderer count to avoid repeated allocation
            if (_cachedRendererCount != rendererCount)
            {
                _cachedRendererCount = rendererCount;
            }

            if (RendererIndex >= 0 && RendererIndex < rendererCount)
            {
                _cameraData.SetRenderer(RendererIndex);

                if (_enableLog)
                    Debug.Log($"[CameraForceRenderByIndex] Renderer index set to {RendererIndex} (Total renderers: {rendererCount})");
            }
            else
            {
                Debug.LogWarning($"[CameraForceRenderByIndex] RendererIndex {RendererIndex} is out of range. Valid range: 0 to {Mathf.Max(0, rendererCount - 1)}. Total renderers: {rendererCount}");

                // Clamp to valid range as fallback (only if there are renderers available)
                if (rendererCount > 0)
                {
                    RendererIndex = Mathf.Clamp(RendererIndex, 0, rendererCount - 1);
                    _cameraData.SetRenderer(RendererIndex);
                }
            }
        }
    }
}