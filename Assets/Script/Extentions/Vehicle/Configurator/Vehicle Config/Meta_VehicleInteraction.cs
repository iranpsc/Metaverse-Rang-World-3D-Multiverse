using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using static Meta.Vehicle.Meta_VehicleSeat;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Vehicle Interaction")]
    [HelpURL("https://google.com")]
    public class Meta_VehicleInteraction : NetworkBehaviour
    {
        [Header("Interaction Setting")]
        public InputActionReference Interact;
        public float RayDistance = 4f;
        public LayerMask VehicleLayer;
        public Vector3 SeatOffset; // optional
        public float ExitDistance = 1.5f;

        [Header("Player Refs")]
        public MonoBehaviour PlayerMove;   // assign in inspector
        public Collider PlayerCollider;    // assign in inspector
        public CharacterController Controller;
        public Transform PlayerModel;      // assign the visual/player root

        // ==========================================================
        // متغیرهای شبکه ای (SyncVars)
        // ==========================================================
        [SyncVar] public uint _SyncedVehicleNetId = 0;
        [SyncVar] public int _SyncedSeatIndex = -1;

        // ==========================================================
        // مراجع محلی (Local References)
        // ==========================================================
        private Camera _cam;
        private int _CurrentSeatIndex = -1;
        private Meta_VehicleBase _CurrentVehicle;

        private void OnEnable()
        {
            Interact.action.performed += OnInteract;
            Interact.action.Enable();
        }
        private void OnDisable()
        {
            Interact.action.performed -= OnInteract;
            Interact.action.Disable();
        }
        private void Start()
        {
            // Initializes the camera reference
            _cam = Camera.main;
        }

        // ==========================================================
        // متد اصلی تعامل (فقط روی کلاینت محلی)
        // ==========================================================

        private void OnInteract(InputAction.CallbackContext ctx)
        {
            if (!isLocalPlayer) return;

            // 1. **EXCELSIOR**: Check if player is IN a vehicle
            if (_SyncedVehicleNetId != 0)
            {
                // Request to EXIT the vehicle

                if (_CurrentVehicle == null && NetworkClient.spawned.TryGetValue(_SyncedVehicleNetId, out NetworkIdentity vehicleNetId))
                {
                    _CurrentVehicle = vehicleNetId.GetComponent<Meta_VehicleBase>();
                }

                if (_CurrentVehicle != null)
                {
                    Debug.Log("Exit Request Sent to Server.");
                    // --- 🛑 UNCOMMENTED: CALL COMMAND ---
                    CmdExitVehicle();
                }
                else
                {
                    // Emergency reset
                    _SyncedVehicleNetId = 0;
                    _SyncedSeatIndex = -1;
                }
            }
            else // 2. **NO VEHICLE**: Try to enter
            {
                // Raycast logic is correct: shoots from camera center
                if (_cam == null || !Physics.Raycast(_cam.transform.position, _cam.transform.forward, out RaycastHit _Hit, RayDistance, VehicleLayer))
                    return;

                Meta_VehicleBase _Vehicle = _Hit.collider.GetComponentInParent<Meta_VehicleBase>();
                if (_Vehicle == null) return;

                // Find a free seat (requires logic in Meta_VehicleBase)
                (int _SeatIndex, Transform _SeatTransform) _FreeSeat = _Vehicle.GetFreeSeat();

                if (_FreeSeat._SeatTransform != null)
                {
                    Debug.Log($"Enter Request Sent to Server. Seat Index: {_FreeSeat._SeatIndex}");
                    // --- 🛑 UNCOMMENTED: CALL COMMAND ---
                    CmdEnterVehicle(_Vehicle.netIdentity, _FreeSeat._SeatIndex, netIdentity.netId);
                }
            }
        }

        // ==========================================================
        // Commands (Client -> Server)
        // ==========================================================

        [Command(requiresAuthority = false)]
        private void CmdEnterVehicle(NetworkIdentity _VehicleNetId, int _SeatIndex, uint _OccupiedNetId)
        {
            Meta_VehicleBase _Vehicle = _VehicleNetId.GetComponent<Meta_VehicleBase>();
            if (_Vehicle == null || _Vehicle.IsSeatOccupied(_OccupiedNetId)) return;

            // 1. Authority transfer logic
            bool isDriverSeat = _Vehicle.Seat.AllSeats[_SeatIndex].IsDriverSeat;
            if (isDriverSeat)
            {
                if (_Vehicle.netIdentity.connectionToClient != connectionToClient)
                {
                    _Vehicle.netIdentity.RemoveClientAuthority();
                    _Vehicle.netIdentity.AssignClientAuthority(connectionToClient);
                }
            }

            // 2. Set seat state on server
            _Vehicle.MarkSeatOccupied(_SeatIndex, _OccupiedNetId);

            // 3. Set SyncVars on the player (Server -> All Clients)
            _SyncedSeatIndex = _SeatIndex;
            _SyncedVehicleNetId = _VehicleNetId.netId;

            // 4. Call TargetRpc for local client actions (Disabling control, parenting)
            TargetEnterVehicle(connectionToClient, _VehicleNetId, _SeatIndex);
        }


        [Command(requiresAuthority = false)]
        private void CmdExitVehicle()
        {
            // 1. Server-side reference retrieval
            if (_CurrentVehicle == null && _SyncedVehicleNetId != 0)
            {
                if (NetworkServer.spawned.TryGetValue(_SyncedVehicleNetId, out NetworkIdentity vehicleNetId))
                {
                    _CurrentVehicle = vehicleNetId.GetComponent<Meta_VehicleBase>();
                }
            }

            if (_CurrentVehicle == null)
            {
                _SyncedVehicleNetId = 0;
                _SyncedSeatIndex = -1;
                return;
            }

            // 2. Find and free the occupied seat
            (int _SeatIndex, SeatState _SeatData) _SeatInfo = _CurrentVehicle.GetSeatByNetId(netIdentity.netId);

            if (_SeatInfo._SeatIndex == -1)
            {
                Debug.LogWarning($"Player {netId} tried to exit, but was not found. Forcing client exit.");
                if (_CurrentVehicle.netIdentity.isOwned && _CurrentVehicle.netIdentity.connectionToClient == connectionToClient)
                {
                    _CurrentVehicle.netIdentity.RemoveClientAuthority();
                }
            }
            else
            {
                _CurrentVehicle.MarkSeatFree(_SeatInfo._SeatIndex);

                // 3. Remove Authority if driver
                if (_SeatInfo._SeatData.IsDriver)
                {
                    _CurrentVehicle.netIdentity.RemoveClientAuthority();
                }
            }

            // 4. Reset SyncVars (Server -> All Clients)
            _SyncedSeatIndex = -1;
            _SyncedVehicleNetId = 0;

            // 5. TargetRpc for local client actions (Enabling control, unparenting)
            TargetExitVehicle(connectionToClient, _CurrentVehicle.netIdentity);

            // 6. Reset server local references
            _CurrentSeatIndex = -1;
            _CurrentVehicle = null;
        }

        // ==========================================================
        // TargetRpcs (Server -> Client)
        // ==========================================================

        [TargetRpc]
        private void TargetEnterVehicle(NetworkConnection _Target, NetworkIdentity _VehicleNetId, int _SeatIndex)
        {
            Meta_VehicleBase _Vehicle = _VehicleNetId.GetComponent<Meta_VehicleBase>();
            if (_Vehicle == null) return;

            // Local actions (Parenting, Disabling Control)
            Transform _SeatTransform = _Vehicle.Seat.AllSeats[_SeatIndex].SeatTransform;

            _CurrentSeatIndex = _SeatIndex;
            _CurrentVehicle = _Vehicle;

            // Parent directly to the seat transform
            PlayerModel.SetParent(_SeatTransform);
            PlayerModel.localPosition = SeatOffset;
            PlayerModel.localRotation = Quaternion.identity;

            PlayerMove.enabled = false;
            PlayerCollider.enabled = false;
            Controller.enabled = false;
        }

        [TargetRpc]
        private void TargetExitVehicle(NetworkConnection _Target, NetworkIdentity _VehicleNetId)
        {
            Meta_VehicleBase _Vehicle = _VehicleNetId.GetComponent<Meta_VehicleBase>();
            if (_Vehicle == null) return;

            // Local actions (Unparenting, Enabling Control)

            // 1. Determine exit position
            Vector3 _ExitOffset = _Vehicle.transform.right * ExitDistance;

            // 🛑 FIX: Added Vector3.up (world up) to the exit position for vertical offset.
            Vector3 _ExitPosition = _Vehicle.transform.position + _ExitOffset + Vector3.up;

            // 2. Unparent from the vehicle
            PlayerModel.SetParent(null);
            PlayerModel.position = _ExitPosition;

            // 3. Enable local movement/physics
            Controller.enabled = true;
            PlayerCollider.enabled = true;
            PlayerMove.enabled = true;

            // 4. Reset local references
            _CurrentSeatIndex = -1;
            _CurrentVehicle = null;
        }
    }
}