using Mirror;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_Sample")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_Sample : NetworkBehaviour
    {

        [Header("Interaction Settings")]
        public string targetObjectName = "Sample";
        public KeyCode interactionKey = KeyCode.E;
        public Vector3 localSeatOffset = Vector3.zero;

        [Header("Player Components")]
        public MonoBehaviour PlayerMovement;

        // We store the Root NetworkIdentity to sync over the network
        [SyncVar(hook = nameof(OnParentChange))]
        public NetworkIdentity currentVehicleRoot;

        void Update()
        {
            if (!isLocalPlayer) return;

            if (Input.GetKeyDown(interactionKey))
            {
                if (currentVehicleRoot == null)
                {
                    // Try to find the vehicle root in the scene to send to server
                    GameObject seatGO = GameObject.Find(targetObjectName);
                    if (seatGO != null)
                    {
                        NetworkIdentity rootId = seatGO.GetComponentInParent<NetworkIdentity>();
                        if (rootId != null) CmdToggleParent(rootId);
                    }
                }
                else
                {
                    CmdToggleParent(null);
                }
            }
        }

        [Command]
        private void CmdToggleParent(NetworkIdentity vehicleRoot)
        {
            currentVehicleRoot = vehicleRoot;
            TargetToggleControl(connectionToClient, vehicleRoot != null);
        }

        // This runs on EVERY client
        private void OnParentChange(NetworkIdentity oldRoot, NetworkIdentity newRoot)
        {
            if (newRoot != null)
            {
                // 1. We found the Root, now find the specific CHILD named "Sample"
                Transform actualSeat = FindChildRecursive(newRoot.transform, targetObjectName);

                if (actualSeat != null)
                {
                    // 2. Parent to the SEAT, not the Root
                    transform.SetParent(actualSeat);
                    transform.localPosition = localSeatOffset;
                    transform.localRotation = Quaternion.identity;
                    Debug.Log($"Successfully parented to {actualSeat.name} under {newRoot.name}");
                }
                else
                {
                    // Fallback: Parent to root if seat not found
                    transform.SetParent(newRoot.transform);
                    Debug.LogWarning("Seat not found, parented to root instead.");
                }
            }
            else
            {
                transform.SetParent(null);
                // Add your exit logic/position here
            }
        }

        // Helper function to find a child by name anywhere in the hierarchy
        private Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            foreach (Transform child in parent)
            {
                Transform result = FindChildRecursive(child, name);
                if (result != null) return result;
            }
            return null;
        }

        [TargetRpc]
        private void TargetToggleControl(NetworkConnection target, bool isEntering)
        {
            if (PlayerMovement != null) PlayerMovement.enabled = !isEntering;
            if (TryGetComponent(out CharacterController cc)) cc.enabled = !isEntering;
        }
    }
}
