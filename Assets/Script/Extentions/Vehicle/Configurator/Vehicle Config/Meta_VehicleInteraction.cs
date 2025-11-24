using UnityEngine;
using UnityEngine.InputSystem;
using static Meta.Vehicle.Meta_VehicleSeat;

namespace Meta.Vehicle
{
    public class Meta_VehicleInteraction : MonoBehaviour
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
                ExitVehicle();
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

                EnterVehicle(vehicle, seat);
            }
        }

        private void EnterVehicle(Meta_VehicleBase vehicle, VehicleSeat seat)
        {
            // Disable player movement + collider
            PlayerMove.enabled = false;
            PlayerCollider.enabled = false;
            Controller.enabled = false;

            // Move player to seat
            PlayerModel.position = seat.Seat.position + SeatOffset;
            PlayerModel.rotation = seat.Seat.rotation;
            PlayerModel.SetParent(seat.Seat);

            // Mark seat as occupied
            vehicle.MarkSeatOccupied(seat.Seat);

            // If vehicle has no driver yet, mark it
            if (!vehicle.HasDriver)
                vehicle.HasDriver = true;

            // Save current references
            _CurrentSeat = seat;
            _CurrentVehicle = vehicle;

            Debug.Log("Player entered vehicle at seat: " + seat.Seat.name);
        }

        private void ExitVehicle()
        {
            if (_CurrentSeat == null || _CurrentVehicle == null) return;

            // Free seat
            _CurrentVehicle.MarkSeatFree(_CurrentSeat.Seat);

            // Determine exit position
            Vector3 exitOffset = new Vector3(1f, 0.1f, 0f);
            // right side + small Y offset to prevent clipping
            Vector3 exitPosition = _CurrentVehicle.transform.TransformPoint(exitOffset * ExitDistance);

            // Unparent player
            PlayerModel.SetParent(null);

            // Move player to safe position
            PlayerModel.position = exitPosition;

            // Re-enable movement
            PlayerMove.enabled = true;
            PlayerCollider.enabled = true;
            Controller.enabled = true;

            // Reset vehicle HasDriver if it was this player
            if (_CurrentVehicle.HasDriver)
                _CurrentVehicle.HasDriver = false;

            Debug.Log("Player exited vehicle.");

            // Reset references
            _CurrentSeat = null;
            _CurrentVehicle = null;
        }
    }
}
