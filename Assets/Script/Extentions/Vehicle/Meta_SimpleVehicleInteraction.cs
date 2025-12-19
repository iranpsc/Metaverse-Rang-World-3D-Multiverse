using Meta.Vehicle;
using Mirror;
using Mirror.Examples.Benchmark;
using Mirror.Examples.Common;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Simple Vehicle Interaction")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_SimpleVehicleInteraction : NetworkBehaviour
    {
        [Header("Interaction Setting")]
        public InputActionReference Interact;
        public InputActionReference DestroyVehicle;

        public float RayDistance = 4f;
        public LayerMask VehicleLayer;
        public Vector3 SeatOffset; // optional
        public float ExitDistance = 1.5f;

        [Header("Player Refs")]
        public MonoBehaviour PlayerMove;   // assign in inspector
        public Collider PlayerCollider;    // assign in inspector
        public CharacterController Controller;
        public Transform PlayerModel;      // assign the visual/player root
        public Camera PlayerCamera;
        public CinemachinePanTilt CameraRot;

        // ==========================================================
        // متغیرهای شبکه ای (SyncVars)
        // ==========================================================
        [SyncVar] public uint _SyncedVehicleNetId = 0;
        [SyncVar] public int _SyncedSeatIndex = -1;

        // ==========================================================
        // مراجع محلی (Local References)
        // ==========================================================
        private int _CurrentSeatIndex = -1;
        private Meta_VehicleBase _CurrentVehicle;

        private RaycastHit _DebugHit;
        private bool _HasHit = false;
        // /---------------------------------------------------------/ //
        public string targetObjectName;

        [SyncVar(hook = nameof(OnParentChange))]
        public NetworkIdentity currentVehicleRoot;

        public override void OnStartLocalPlayer()  
        {
            if (PlayerCamera == null) PlayerCamera = Camera.main;

            Interact.action.performed += OnInteract;
            DestroyVehicle.action.performed += OnDestroyVehicle;
            Interact.action.Enable();
            DestroyVehicle.action.Enable();
        }
        public override void OnStopLocalPlayer()
        {
            Interact.action.performed -= OnInteract;
            DestroyVehicle.action.performed -= OnDestroyVehicle;
            Interact.action.Disable();
            DestroyVehicle.action.Disable();
            if (_CurrentVehicle != null)
            {
                Debug.Log("Exit Request Sent to Server.");
                // --- 🛑 UNCOMMENTED: CALL COMMAND ---
                CmdExitVehicle();
            }
        }
        private void OnDestroyVehicle(InputAction.CallbackContext ctx)
        {
            RequestDestroyVehicle();
        }
        public void RequestDestroyVehicle()
        {
            if (!isLocalPlayer) return;

            // Raycast to find the vehicle, using the exact logic you requested.
            if (PlayerCamera == null ||
                !Physics.Raycast(PlayerCamera.transform.position, PlayerCamera.transform.forward,
                                 out RaycastHit _Hit, RayDistance, VehicleLayer))
            {
                // No vehicle found in range
                return;
            }

            // Get the Vehicle Base component from the hit object's parent
            Meta_VehicleBase _Vehicle = _Hit.collider.GetComponentInParent<Meta_VehicleBase>();

            if (_Vehicle != null)
            {
                // Send command to the server to check and destroy
                CmdDestroyVehicle(_Vehicle.netIdentity);
            }
        }

        // 🛑 New Server-Side Command (This runs the check and destruction)
        [Command(requiresAuthority = false)]
        private void CmdDestroyVehicle(NetworkIdentity _VehicleNetId)
        {
            // 1. Ensure this runs only on the server
            if (!NetworkServer.active) return;

            Meta_VehicleBase _Vehicle = _VehicleNetId.GetComponent<Meta_VehicleBase>();
            if (_Vehicle == null)
            {
                Debug.LogWarning("CmdDestroyVehicle: Vehicle object not found on server.");
                return;
            }

            // 2. Check if the vehicle is empty by iterating over its SyncList of seat states
            bool isEmpty = true;

            // We assume the _SeatState SyncList is available on the Meta_VehicleBase
            foreach (var seatState in _Vehicle._SeatState)
            {
                // If the occupant's NetId is not 0, the seat is occupied
                if (seatState.OccupantNetId != 0)
                {
                    isEmpty = false;
                    break;
                }
            }

            if (isEmpty)
            {
                Debug.Log($"Server is destroying empty vehicle: {_Vehicle.name}");
                // 3. Destroy the networked object for ALL clients
                NetworkServer.UnSpawn(_Vehicle.gameObject);
            }
            else
            {
                // Optional: Send a TargetRpc back to the client to notify them the vehicle is occupied
                Debug.Log($"Vehicle {_Vehicle.name} is occupied and cannot be destroyed.");
            }
        }

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
                    CameraRot.ReferenceFrame = CinemachinePanTilt.ReferenceFrames.World;
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
                if (PlayerCamera == null || !Physics.Raycast(PlayerCamera.transform.position, PlayerCamera.transform.forward, out RaycastHit _Hit, RayDistance, VehicleLayer)) return;

                Meta_VehicleBase _Vehicle = _Hit.collider.GetComponentInParent<Meta_VehicleBase>();
                if (_Vehicle == null) return;

                _CurrentVehicle = _Vehicle.GetComponent<Meta_VehicleBase>();
                // Find a free seat (requires logic in Meta_VehicleBase)
                (int _SeatIndex, Transform _SeatTransform) _FreeSeat = _Vehicle.GetFreeSeat();

                if (_FreeSeat._SeatTransform != null)
                {
                    targetObjectName = _FreeSeat._SeatTransform.name;

                    Debug.Log($"Enter Request Sent to Server. Seat Index: {_FreeSeat._SeatIndex}");
                    CameraRot.ReferenceFrame = CinemachinePanTilt.ReferenceFrames.ParentObject;
                    // --- 🛑 UNCOMMENTED: CALL COMMAND ---
                    _Vehicle.ValidateSeat();
                    CmdEnterVehicle(_Vehicle.netIdentity, _FreeSeat._SeatIndex, netIdentity.netId);
                }
            }
            // /----------------------------------------------------------------------------/ //
            if (currentVehicleRoot == null)
            {
                NetworkIdentity rootId = _CurrentVehicle.GetComponent<NetworkIdentity>();
                if (rootId != null) CmdToggleParent(rootId);
            }
            else
            {
                CmdToggleParent(null);
            }
        }
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (PlayerCamera == null) return;

            // Draw ray
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(PlayerCamera.transform.position, PlayerCamera.transform.forward * RayDistance);

            if (!Physics.Raycast(PlayerCamera.transform.position, PlayerCamera.transform.forward, out RaycastHit _Hit, RayDistance, VehicleLayer))
            {
                _HasHit = false;
                return;
            }

            _DebugHit = _Hit;
            _HasHit = true;
            if (_HasHit)
            {
                // Draw hit sphere
                Gizmos.color = Color.red;
                Gizmos.DrawSphere(_DebugHit.point, 0.15f);

                // Try to detect vehicle
                Meta_VehicleBase vehicle = _DebugHit.collider.GetComponentInParent<Meta_VehicleBase>();
                if (vehicle != null)
                {
                    Debug.Log($"Raycast Hit Vehicle: {vehicle.name}");
                }
            }
        }
#endif
        [Command]
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
            //TargetEnterVehicle(connectionToClient, _VehicleNetId, _SeatIndex);
            Transform _SeatTransform = _Vehicle.Seat.AllSeats[_SeatIndex].SeatTransform;//*

            _CurrentSeatIndex = _SeatIndex;//*
            _CurrentVehicle = _Vehicle;//*
        }


        [Command]
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
            //TargetExitVehicle(connectionToClient, _CurrentVehicle.netIdentity);

            // 6. Reset server local references
            _CurrentSeatIndex = -1;
            _CurrentVehicle = null;
        }

        [TargetRpc]
        private void TargetEnterVehicle(NetworkConnection _Target, NetworkIdentity _VehicleNetId, int _SeatIndex)
        {
            Meta_VehicleBase _Vehicle = _VehicleNetId.GetComponent<Meta_VehicleBase>();
            if (_Vehicle == null) return;

            // Local actions (Parenting, Disabling Control)
            Transform _SeatTransform = _Vehicle.Seat.AllSeats[_SeatIndex].SeatTransform;//*

            _CurrentSeatIndex = _SeatIndex;//*
            _CurrentVehicle = _Vehicle;//*

            // Parent directly to the seat transform
            PlayerModel.SetParent(_SeatTransform);//
            PlayerModel.localPosition = SeatOffset;//
            PlayerModel.localRotation = Quaternion.identity;//

            PlayerMove.enabled = false;//
            PlayerCollider.enabled = false;//
            Controller.enabled = false;//
        }
        [TargetRpc]
        private void TargetExitVehicle(NetworkConnection _Target, NetworkIdentity _VehicleNetId)
        {
            Meta_VehicleBase _Vehicle = _VehicleNetId.GetComponent<Meta_VehicleBase>();//
            if (_Vehicle == null) return;//

            // Local actions (Unparenting, Enabling Control)

            // 1. Determine exit position
            Vector3 _ExitOffset = _Vehicle.transform.right * ExitDistance;//

            // 🛑 FIX: Added Vector3.up (world up) to the exit position for vertical offset.
            Vector3 _ExitPosition = _Vehicle.transform.position + _ExitOffset + Vector3.up;//

            // 2. Unparent from the vehicle
            PlayerModel.SetParent(null);//
            PlayerModel.position = _ExitPosition;//

            // 3. Enable local movement/physics
            Controller.enabled = true;//
            PlayerCollider.enabled = true;//
            PlayerMove.enabled = true;//

            // 4. Reset local references
            _CurrentSeatIndex = -1;//
            _CurrentVehicle = null;//
        }

        // /--------------------------------------------------------------------/ //
        [Command]
        private void CmdToggleParent(NetworkIdentity vehicleRoot)
        {
            //TargetToggleControl(connectionToClient, vehicleRoot != null); I DONT NEED IT ANYMORE
            currentVehicleRoot = vehicleRoot;
            //RpcEnter(connectionToClient, vehicleRoot != null); I DONT NEED IT ANYMORE

        }
        // File: Meta_SimpleVehicleInteraction.cs (Updated OnParentChange)

        private void OnParentChange(NetworkIdentity oldRoot, NetworkIdentity newRoot)
        {
            if (newRoot != null)
            {
                // 1. Get the Vehicle Base component from the new root
                Meta_VehicleBase vehicleBase = newRoot.GetComponent<Meta_VehicleBase>();

                // 2. Perform the seat lookup using the synchronized index
                if (vehicleBase != null && _SyncedSeatIndex != -1)
                {
                    // Ensure the seat index is valid for the vehicle's list
                    if (_SyncedSeatIndex >= 0 && _SyncedSeatIndex < vehicleBase.Seat.AllSeats.Count)
                    {
                        // Get the definitive, network-confirmed Seat Transform
                        Transform actualSeat = vehicleBase.Seat.AllSeats[_SyncedSeatIndex].SeatTransform;

                        if (actualSeat != null)
                        {
                            // 🛑 FIX: Parent the entire player root object (this.transform) to the seat
                            transform.SetParent(actualSeat);
                            transform.localPosition = Vector3.zero;
                            transform.localPosition = SeatOffset;


                            if (PlayerMove != null) PlayerMove.enabled = false;
                            if (Controller != null) Controller.enabled = false;
                            if (PlayerCollider != null) PlayerCollider.enabled = false;

                            transform.localRotation = Quaternion.identity;
                            Debug.Log($"Successfully parented to Seat at index {_SyncedSeatIndex} on {newRoot.name}");


                            return; // Successfully parented, exit the hook
                        }
                    }
                }

                // --- Fallback (Occurs if seat data or index is invalid/not ready) ---

                // Original logic for when FindChildRecursive failed is no longer needed. 
                // We use the Vehicle Root as the final fallback if the seat data lookup fails.
                transform.SetParent(newRoot.transform);
                // We can slightly modify the warning to be more specific:
                Debug.LogWarning($"Seat data not ready or invalid index ({_SyncedSeatIndex}). Parented to Vehicle Root instead.");
            }
            else
            {
                transform.SetParent(null);

                // Unparenting/Exit logic remains the same (using transform to move the whole player)
                Vector3 _ExitOffset = PlayerModel.transform.right * ExitDistance;
                Vector3 _ExitPosition = PlayerModel.transform.position + _ExitOffset + Vector3.up;
                PlayerModel.position = _ExitPosition; // You might want to move the entire player root here: transform.position = _ExitPosition;

                if (PlayerMove != null) PlayerMove.enabled = true;
                if (Controller != null) Controller.enabled = true;
                if (PlayerCollider != null) PlayerCollider.enabled = true;

                _CurrentSeatIndex = -1;
                _CurrentVehicle = null;
                targetObjectName = null;
            }
        }
        [TargetRpc]
        private void RpcEnter(NetworkConnection target, bool isEntering)
        {
            if (isEntering)
                transform.localPosition = SeatOffset;
        }
        // NOTE: The FindChildRecursive method is no longer needed.
        [TargetRpc]
        private void TargetToggleControl(NetworkConnection target, bool isEntering)
        {
            if (PlayerMove != null) PlayerMove.enabled = !isEntering;
            if (Controller != null) Controller.enabled = !isEntering;
            if (PlayerCollider != null) PlayerCollider.enabled = !isEntering;
        }
    }
}