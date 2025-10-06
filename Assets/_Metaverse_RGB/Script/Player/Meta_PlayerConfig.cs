using Mirror;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_Player Config")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerConfig : NetworkBehaviour
    {

        [Header("References")]
        [SerializeField] private GameObject MoveAction;
        [SerializeField] private GameObject Username;
        [SerializeField] private GameObject GroundCheck;
        [SerializeField] private GameObject Skin;
        [SerializeField] private GameObject Camera;

        [Header("Settings")]


        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            HideLocalPlayerMesh();
            if (EnableLog) Debug.Log("[Meta_PlayerConfig] PutLogHere");
        }

        private void HideLocalPlayerMesh()
        {
            if (!isLocalPlayer) return;
            if (Skin == null) return;
            foreach (var renderer in Skin.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                renderer.enabled = false;
            foreach (var renderer in Skin.GetComponentsInChildren<MeshRenderer>(true))
                renderer.enabled = false;
        }
    }
}