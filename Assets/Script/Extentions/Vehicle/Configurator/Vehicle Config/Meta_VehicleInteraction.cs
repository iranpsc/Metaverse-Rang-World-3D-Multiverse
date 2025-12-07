using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using static Meta.Vehicle.Meta_VehicleSeat;
using static Mirror.NetworkRuntimeProfiler;
namespace Meta.Vehicle
{
    public class Meta_VehicleInteraction : NetworkBehaviour
    {
        public InputActionReference Interact;
        public float RayDistance = 4f;
        public LayerMask VehicleLayer;
        public Vector3 SeatOffset; // optional
        public float ExitDistance = 0.5f;
        [Header("Player Refs")]
        public MonoBehaviour PlayerMove;   // assign in inspector
        public Collider PlayerCollider;    // assign in inspector
        public CharacterController Controller;
        public Transform PlayerModel;      // assign the visual/player root
        private Camera _cam;
        private VehicleSeat _CurrentSeat;
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
            _cam = Camera.main;
        }
        private void OnInteract(InputAction.CallbackContext ctx)
        {

            // If player is already in a vehicle → exit
            if (_CurrentSeat != null && _CurrentVehicle != null)
            {
                CmdExitVehicle();
                return;
            }
            // Raycast for vehicle
            Ray ray = _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, RayDistance, VehicleLayer))
            {
                var vehicle = hit.collider.GetComponentInParent<Meta_VehicleBase>();
                if (!vehicle) return;
                VehicleSeat seat = vehicle.GetFreeSeat();
                if (seat.Seat == null)
                {
                    Debug.Log("No free seat.");
                    return;
                }
                CmdEnterVehicle(vehicle, seat);
            }
        }
        
        private void CmdEnterVehicle(Meta_VehicleBase vehicle, VehicleSeat seat)
        {
            // The logic to mark the seat and set HasDriver must happen on the Server
            vehicle.MarkSeatOccupied(seat.Seat);
            if (!vehicle.HasDriver)
                vehicle.HasDriver = true;

            // Assign authority of the vehicle to the client who is entering
            vehicle.netIdentity.AssignClientAuthority(connectionToClient); // ADD THIS

            // Call an Rpc to execute the client-side changes on the player who entered
            TargetEnterVehicle(connectionToClient, vehicle.netIdentity, seat.Seat);
        }

        // --- TargetEnterVehicle (Executed on the specific Client that entered) ---
        
        private void TargetEnterVehicle(NetworkConnection target, NetworkIdentity vehicleNetId, Transform seatTransform)
        {
            // Re-find the seat object on this client using the passed Transform
            Meta_VehicleBase vehicle = vehicleNetId.GetComponent<Meta_VehicleBase>();
            VehicleSeat seat = vehicle.Seat.AllSeats.Find(s => s.Seat == seatTransform);

            if (seat == null) return;

            // Disable player movement + collider
            PlayerMove.enabled = false;
            PlayerCollider.enabled = false;
            Controller.enabled = false;

            // Move player to seat
            PlayerModel.position = seat.Seat.position + SeatOffset;
            PlayerModel.rotation = seat.Seat.rotation;
            PlayerModel.SetParent(seat.Seat);

            // Save current references (Local-only)
            _CurrentSeat = seat;
            _CurrentVehicle = vehicle;
            Debug.Log("Player entered vehicle at seat: " + seat.Seat.name);
        }

        // --- CmdExitVehicle (Needs to be a [Command] executed on the Server) ---
        
        private void CmdExitVehicle()
        {
            if (_CurrentSeat == null || _CurrentVehicle == null) return;

            // The logic to free the seat and reset HasDriver must happen on the Server
            _CurrentVehicle.MarkSeatFree(_CurrentSeat.Seat);
            if (_CurrentVehicle.HasDriver)
                _CurrentVehicle.HasDriver = false;

            _CurrentVehicle.netIdentity.RemoveClientAuthority(); // ADD THIS

            // Call an Rpc to execute the client-side changes on the player who exited
            TargetExitVehicle(connectionToClient, _CurrentVehicle.netIdentity);
        }

        // --- TargetExitVehicle (Executed on the specific Client that exited) ---
        
        private void TargetExitVehicle(NetworkConnection target, NetworkIdentity vehicleNetId)
        {
            Meta_VehicleBase vehicle = vehicleNetId.GetComponent<Meta_VehicleBase>();
            if (vehicle == null) return;

            // Determine exit position (calculate locally)
            Vector3 exitOffset = new Vector3(1f, 0.1f, 0f);
            Vector3 exitPosition = vehicle.transform.TransformPoint(exitOffset * ExitDistance);

            // Unparent player
            PlayerModel.SetParent(null);
            // Move player to safe position
            PlayerModel.position = exitPosition;

            // Re-enable movement
            PlayerMove.enabled = true;
            PlayerCollider.enabled = true;
            Controller.enabled = true;

            Debug.Log("Player exited vehicle.");
            // Reset references (Local-only)
            _CurrentSeat = null;
            _CurrentVehicle = null;
        }
    }
}