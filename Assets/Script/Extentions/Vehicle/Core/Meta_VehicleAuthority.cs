using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VehicleAuthority")]
    [HelpURL("https://google.com")]
    public class Meta_VehicleAuthority : NetworkBehaviour
    {

        [Header("References")]


        [Header("Settings")]
        [SyncVar(hook = nameof(OnIsControlledChanged))]
        public bool IsControlled;
        public bool IsToggled;
        [Header("Inputs")]
        public InputActionReference Interact;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        protected override void OnValidate()
        {
            if (Application.isPlaying) return;

            base.OnValidate();
            Reset();
        }

        private void OnEnable()
        {
            if (Interact != null)
                Interact.action.performed += OnToggle;
        }
        private void OnDisable()
        {
            if (Interact != null)
                Interact.action.performed -= OnToggle;
        }

        [ClientCallback]
        private void OnToggle(InputAction.CallbackContext _)
        {
            IsToggled = !IsToggled;

            if (IsToggled)
            {
                CmdTakeControl();
            }
            else
            {
                if (isOwned)
                {
                    CmdReleaseControl();
                }
            }
        }
        public void OnIsControlledChanged(bool _, bool newValue)
        {
            // active or deactive engine
        }

        public void Reset()
        {
            // Do something
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!isClient || !other.gameObject.CompareTag("Player")) return;

            if (other.TryGetComponent(out NetworkIdentity _netId))
            {
                if (_netId == NetworkClient.localPlayer)
                {
                    // show enter button after player got close
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
                    // hide enter button after player got far
                }
            }
        }

        private void CmdTakeControl(NetworkConnectionToClient _Conn = null)
        {
            if (connectionToClient != null)
            {
                Debug.Log("Someone Else Is Already Controlling This Vehicle");
                // DO NOT RETURN !!! player must seat at passenger vehicle
                return;
            }

            _Conn.authenticationData = _Conn.identity.gameObject;

            IsControlled = true;

            NetworkServer.ReplacePlayerForConnection(_Conn, gameObject, ReplacePlayerOptions.Unspawn);
        }

        [Command]
        private void CmdReleaseControl()
        {
            if (connectionToClient.authenticationData is GameObject _Player)
            {
                Vector3 _Pos = transform.position + transform.right * 3 + Vector3.up;
                _Player.transform.SetLocalPositionAndRotation(_Pos, transform.rotation);

                IsControlled = false;

                connectionToClient.authenticationData = null;

                NetworkServer.ReplacePlayerForConnection(connectionToClient, _Player, ReplacePlayerOptions.KeepActive);
            }
        }
    }
}