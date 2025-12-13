using Mirror;
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;
using System.Collections.Generic;

namespace Meta
{
    // --- Input Structure for Spawn System ---
    [Serializable]
    public struct SpawnKeys
    {
        [Tooltip("1D Axis for Yaw rotation (e.g., Q/E keyboard setup).")]
        public InputActionReference RotateAxis;
        [Tooltip("1D Axis for distance (e.g., R/T keyboard setup).")]
        public InputActionReference DistanceAxis;
        public InputActionReference Confirm; // F to confirm
        public InputActionReference Cancel;  // Escape to cancel
    }

    [AddComponentMenu("Meta/Vehicle Spawn Controller")]
    public class Meta_VehicleSpawnController : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("The Transform that has the GlobalAlignmentController (child of player/camera).")]
        public Transform SpawnPoint; // Used as the world-aligned starting point

        [Tooltip("The Transform inside SpawnPoint that will hold and manipulate the preview vehicle.")]
        public Transform PreviewAnchor; // CRITICAL: This is the object that moves (R/T) and rotates (Q/E)

        public SpawnKeys keys;

        [Header("Settings")]
        public float PreviewRotateSpeed = 120f;
        public float PreviewDistanceSpeed = 5f;
        public float MinSpawnDistance = 1f;
        public float MaxSpawnDistance = 15f;
        [Tooltip("Radius for collision check before final spawn.")]
        public float SpawnCheckRadius = 1.5f;
        public LayerMask CollisionLayers;

        // --- Runtime State ---
        private GameObject _CurrentPreview;
        private VehicleSpawnData _PreviewData;
        private bool _IsPreviewing = false;
        private Vector3 _CurrentOffset;

        // --- Setup & Input Toggles ---

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            this.enabled = true;

            // Input setup
            keys.Confirm.action.performed += OnConfirmSpawn;
            keys.Cancel.action.performed += OnCancelSpawn;
            keys.RotateAxis.action.Enable();
            keys.DistanceAxis.action.Enable();
            keys.Confirm.action.Enable();
            keys.Cancel.action.Enable();

            // Subscribe to static UI event (safe, local-only subscription)
            VehicleSpawnEvents.OnStartPreviewRequested += StartVehiclePreview;
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();
            this.enabled = false;

            // Unsubscribe and cleanup
            keys.Confirm.action.performed -= OnConfirmSpawn;
            keys.Cancel.action.performed -= OnCancelSpawn;
            keys.RotateAxis.action.Disable();
            keys.DistanceAxis.action.Disable();
            keys.Confirm.action.Disable();
            keys.Cancel.action.Disable();

            VehicleSpawnEvents.OnStartPreviewRequested -= StartVehiclePreview;

            CleanupPreview();
        }

        // --- UI Call (Subscriber method) ---
        // This method receives the data when VehicleSpawnEvents.OnStartPreviewRequested is invoked.
        public void StartVehiclePreview(VehicleSpawnData data)
        {
            CleanupPreview();

            if (PreviewAnchor == null)
            {
                Debug.LogError("PreviewAnchor is not assigned! Cannot start preview.");
                return;
            }

            _PreviewData = data;
            _IsPreviewing = true;

            // Reset the anchor's rotation before use
            PreviewAnchor.localRotation = Quaternion.identity;

            _CurrentOffset = data.InitialOffset;

            // 1. Instantiate the preview model, PARENTED TO THE ANCHOR
            // IMPORTANT: This uses the prefab for the preview, NOT the registered network prefab.
            _CurrentPreview = Instantiate(data.VehiclePrefab, PreviewAnchor);

            // 2. Reset the vehicle model's local position/rotation
            _CurrentPreview.transform.localPosition = Vector3.zero;
            _CurrentPreview.transform.localRotation = Quaternion.identity;

            // 3. Set the anchor's local Z position for initial distance
            PreviewAnchor.localPosition = new Vector3(0, 0, _CurrentOffset.z);

            // Optional: Disable physics and network components for the preview
            if (_CurrentPreview.GetComponent<Rigidbody>())
                _CurrentPreview.GetComponent<Rigidbody>().isKinematic = true;
            if (_CurrentPreview.GetComponent<NetworkIdentity>())
                _CurrentPreview.GetComponent<NetworkIdentity>().enabled = false;
        }

        // --- Preview Manipulation ---

        private void Update()
        {
            if (!_IsPreviewing || !isOwned) return;

            HandlePreviewRotation();
            HandlePreviewDistance();
            UpdatePreviewVisuals();
        }

        private void HandlePreviewRotation()
        {
            float yawInput = keys.RotateAxis.action.ReadValue<float>();
            if (Mathf.Abs(yawInput) > 0.1f)
            {
                // Rotate the anchor
                PreviewAnchor.transform.Rotate(Vector3.up, yawInput * PreviewRotateSpeed * Time.deltaTime, Space.Self);
            }
        }

        private void HandlePreviewDistance()
        {
            float distanceInput = keys.DistanceAxis.action.ReadValue<float>();
            if (Mathf.Abs(distanceInput) > 0.1f)
            {
                _CurrentOffset.z += distanceInput * PreviewDistanceSpeed * Time.deltaTime;
                _CurrentOffset.z = Mathf.Clamp(_CurrentOffset.z, MinSpawnDistance, MaxSpawnDistance);

                // Move the anchor along its local Z-axis (forward)
                PreviewAnchor.transform.localPosition = new Vector3(0, 0, _CurrentOffset.z);
            }
        }

        private void UpdatePreviewVisuals()
        {
            // Check for collision at the PreviewAnchor's World Position
            bool isColliding = Physics.CheckSphere(PreviewAnchor.transform.position, SpawnCheckRadius, CollisionLayers);

            // Use the object's name to store collision state for confirmation check
            _CurrentPreview.name = isColliding ? "PREVIEW_BLOCKED" : "PREVIEW_SAFE";

            // Optional: Add visual feedback here (e.g., change material color)
        }

        // --- Confirmation & Cancellation ---

        private void OnConfirmSpawn(InputAction.CallbackContext ctx)
        {
            if (!_IsPreviewing || !isOwned) return;

            // Check if the preview is currently colliding
            if (_CurrentPreview.name == "PREVIEW_BLOCKED")
            {
                Debug.LogWarning("Cannot spawn vehicle: Location blocked.");
                return;
            }

            GameObject prefab = _PreviewData.VehiclePrefab;
            NetworkIdentity identity = prefab.GetComponent<NetworkIdentity>();

            if (identity == null)
            {
                Debug.LogError($"Vehicle Prefab '{prefab.name}' is missing a NetworkIdentity component. Cannot spawn.");
                CleanupPreview();
                return;
            }

            // Get the Asset ID (network-safe identifier)
            uint assetId = identity.assetId;

            // Get final spawn position and rotation from the PreviewAnchor
            Vector3 spawnPos = PreviewAnchor.transform.position;
            Quaternion spawnRot = PreviewAnchor.transform.rotation;

            // Call the command, passing the safe Asset ID
            CmdSpawnVehicle(assetId, spawnPos, spawnRot);

            CleanupPreview();
        }

        private void OnCancelSpawn(InputAction.CallbackContext ctx)
        {
            if (!_IsPreviewing || !isOwned) return;
            CleanupPreview();
        }

        private void CleanupPreview()
        {
            if (_CurrentPreview != null)
            {
                Destroy(_CurrentPreview);
                _CurrentPreview = null;
            }

            if (PreviewAnchor != null)
            {
                // Reset the PreviewAnchor's local state for the next use
                PreviewAnchor.localRotation = Quaternion.identity;
                PreviewAnchor.localPosition = Vector3.zero;
            }
            _IsPreviewing = false;
            _PreviewData = null;
        }

        // --- Networking Command ---
        [Command]
        private void CmdSpawnVehicle(uint assetId, Vector3 position, Quaternion rotation)
        {
            // 1. Server looks up the prefab using the received Asset ID
            // We use Linq's FirstOrDefault which requires 'using System.Linq;'
            GameObject prefab = NetworkManager.singleton.spawnPrefabs.FirstOrDefault(p => p.GetComponent<NetworkIdentity>().assetId == assetId);

            if (prefab == null)
            {
                Debug.LogError($"Failed to spawn vehicle: Asset ID {assetId} is not registered in NetworkManager's spawnable prefabs list.");
                return;
            }

            // 2. Instantiate on the server
            GameObject newVehicle = Instantiate(prefab, position, rotation);

            // 3. Set Rigidbody.isKinematic to false (as requested)
            Rigidbody rb = newVehicle.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.WakeUp();
            }

            // 4. Spawn it on the network
            NetworkServer.Spawn(newVehicle);
        }
    }
}