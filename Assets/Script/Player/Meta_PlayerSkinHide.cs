using UnityEngine;
using Mirror;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerSkinHide")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerSkinHide : NetworkBehaviour
    {
        [Header("Optional: Debug")]
        public bool EnableLog = false;
        
        public override void OnStartLocalPlayer()
        {
            HideSelf();
        }

        private void HideSelf()
        {
            // Disable all MeshRenderers
            MeshRenderer[] meshRenderers = GetComponentsInChildren<MeshRenderer>(true);
            foreach (var renderer in meshRenderers)
                renderer.enabled = false;

            // Disable all SkinnedMeshRenderers and reset blend shapes
            SkinnedMeshRenderer[] skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var renderer in skinnedRenderers)
            {
                renderer.enabled = false;

                if (renderer.sharedMesh != null)
                {
                    int blendCount = renderer.sharedMesh.blendShapeCount;
                    for (int i = 0; i < blendCount; i++)
                        renderer.SetBlendShapeWeight(i, 0f);
                }
            }

            if (EnableLog)
                Debug.Log("[HideLocalPlayerBody] Local player renderers hidden.");
        }
    }
}