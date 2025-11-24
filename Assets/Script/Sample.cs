using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Sample")]
    [HelpURL("https://google.com")]
    public class Sample : NetworkBehaviour
    {

        [Header("References")]


        [Header("Settings")]
        public bool IsControlled;

        [Header("Inputs")]
        public InputActionReference Interact;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Sample] PutLogHere");
        }

        void Update()
        {
            if (Interact.action.IsPressed())
            {
                if (TryGetComponent(out NetworkIdentity _NetId))
                {
                    NetworkConnectionToClient _Conn = _NetId.connectionToClient;
                    // hide enter button after player got far
                    NetworkServer.ReplacePlayerForConnection(_Conn, _Conn.authenticationData as GameObject, ReplacePlayerOptions.KeepActive);
                    print("E");
                }
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!isClient || !other.gameObject.CompareTag("Player")) return;

            if (other.TryGetComponent(out NetworkIdentity _NetId))
            {
                if (_NetId == NetworkClient.localPlayer)
                {
                    NetworkConnectionToClient _Conn = _NetId.connectionToClient;
                    // hide enter button after player got far
                    NetworkServer.ReplacePlayerForConnection(_Conn, gameObject, ReplacePlayerOptions.Unspawn);
                }
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!isClient || !other.gameObject.CompareTag("Player")) return;

            if (other.TryGetComponent(out NetworkIdentity _NetId))
            {
                if (_NetId == NetworkClient.localPlayer)
                {
                    NetworkConnectionToClient _Conn = _NetId.connectionToClient;
                    // hide enter button after player got far
                    NetworkServer.ReplacePlayerForConnection(_Conn, _Conn.authenticationData as GameObject, ReplacePlayerOptions.KeepActive);
                }
            }
        }
    }
}