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
        [Tooltip("Renderer index to force this camera to use.")]
        public int RendererIndex = 0;

        private Camera _Camera;
        private UniversalAdditionalCameraData _CameraData;

        void Awake()
        {
            _Camera = GetComponent<Camera>();
            _CameraData = _Camera.GetUniversalAdditionalCameraData();

            if (_CameraData != null)
            {
                // Make sure the index exists
                int rendererCount = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp
                    ? urp.rendererDataList.Length
                    : 0;

                if (RendererIndex >= 0 && RendererIndex < rendererCount)
                {
                    _CameraData.SetRenderer(RendererIndex);
                }
                else
                {
                    Debug.LogWarning($"RendererIndex {RendererIndex} is out of range. Total renderers: {rendererCount}");
                }
            }
        }
    }
}