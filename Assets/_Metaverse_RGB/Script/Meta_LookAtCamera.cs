using Mirror;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/LookAt Camera")]
    public class Meta_LookAtCamera : NetworkBehaviour
    {
        [SerializeField] private Camera GameCamera;
        private void Start()
        {
            if (isLocalPlayer)
            {
                enabled = false;
                return;
            }
            GameCamera = Camera.main;
        }
        private void Update()
        {
            transform.forward = GameCamera.transform.forward;
        }
    }
}