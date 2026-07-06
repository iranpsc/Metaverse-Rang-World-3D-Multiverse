//using Meta.Vehicle;
//using Mirror;
//using System;
//using System.Linq;
//using UnityEngine;
//using UnityEngine.InputSystem;

//namespace Meta
//{
//    // --- Input Structure for Spawn System ---
//    [Serializable]
//    public struct SpawnKeys
//    {
//        [Tooltip("1D Axis for Yaw rotation (e.g., Q/E keyboard setup).")]
//        public InputActionReference RotateAxis;
//        [Tooltip("1D Axis for distance (e.g., R/T keyboard setup).")]
//        public InputActionReference DistanceAxis;
//        public InputActionReference Confirm; // F to confirm
//        public InputActionReference Cancel;  // Escape to cancel
//    }
//    [AddComponentMenu("Meta/Vehicle Spawn Controller")]
//    public class Meta_VehicleSpawnController : NetworkBehaviour
//    {
//        [Header("References")]
//        public Transform SpawnPoint;
//        public Transform PreviewAnchor;

//        public SpawnKeys keys;

//        [Header("Settings")]
//        public float PreviewRotateSpeed = 120f;
//        public float PreviewDistanceSpeed = 5f;
//        public float MinSpawnDistance = 1f;
//        public float MaxSpawnDistance = 15f;
//        public float SpawnCheckRadius = 1.5f;
//        public LayerMask CollisionLayers;

//        private GameObject _CurrentPreview;
//        private VehicleSpawnData _PreviewData;
//        private bool _IsPreviewing;
//        private Vector3 _CurrentOffset;

//        #region Authority
//        public override void OnStartLocalPlayer()
//        {
//            enabled = true;

//            keys.RotateAxis.action.Enable();
//            keys.DistanceAxis.action.Enable();
//            keys.Confirm.action.Enable();
//            keys.Cancel.action.Enable();

//            keys.Confirm.action.started += OnConfirmSpawn;
//            keys.Cancel.action.started += OnCancelSpawn;

//            VehicleSpawnEvents.OnStartPreviewRequested += StartVehiclePreview;
//        }

//        public override void OnStopLocalPlayer()
//        {
//            enabled = false;

//            keys.Confirm.action.started -= OnConfirmSpawn;
//            keys.Cancel.action.started -= OnCancelSpawn;

//            keys.RotateAxis.action.Disable();
//            keys.DistanceAxis.action.Disable();
//            keys.Confirm.action.Disable();
//            keys.Cancel.action.Disable();

//            VehicleSpawnEvents.OnStartPreviewRequested -= StartVehiclePreview;

//            CleanupPreview();
//        }
//        #endregion

//        #region Preview
//        public void StartVehiclePreview(VehicleSpawnData data)
//        {
//            if (!isLocalPlayer) return;

//            CleanupPreview();

//            _PreviewData = data;
//            _IsPreviewing = true;

//            PreviewAnchor.localRotation = Quaternion.identity;
//            _CurrentOffset = data.InitialOffset;

//            // ⚠️ instantiate same prefab BUT sanitize it
//            _CurrentPreview = Instantiate(data.VehiclePrefab, PreviewAnchor);
//            _CurrentPreview.transform.localPosition = Vector3.zero;
//            _CurrentPreview.transform.localRotation = Quaternion.identity;

//            PreviewAnchor.localPosition = new Vector3(0, 0, _CurrentOffset.z);

//            SanitizePreviewObject(_CurrentPreview);
//        }

//        private void SanitizePreviewObject(GameObject preview)
//        {
//            // ❌ Network
//            if (preview.TryGetComponent(out NetworkIdentity net))
//                net.enabled = false;

//            // ❌ Vehicle logic
//            foreach (var vehicle in preview.GetComponentsInChildren<Meta_VehicleSystem>())
//                vehicle.enabled = false;

//            // ❌ Any other vehicle base logic
//            foreach (var v in preview.GetComponentsInChildren<Meta_VehicleBase>())
//                v.enabled = false;

//            // ❌ Physics
//            foreach (var rb in preview.GetComponentsInChildren<Rigidbody>())
//            {
//                rb.isKinematic = true;
//                rb.useGravity = false;   // 🔥 خیلی مهم
//                rb.linearVelocity = Vector3.zero;
//                rb.angularVelocity = Vector3.zero;
//            }

//            foreach (var col in preview.GetComponentsInChildren<Collider>())
//                col.enabled = false;

//            foreach (var wc in preview.GetComponentsInChildren<WheelCollider>())
//                wc.enabled = false;
//        }

//        #endregion

//        #region Update
//        private void Update()
//        {
//            if (!_IsPreviewing || !isLocalPlayer) return;

//            HandlePreviewRotation();
//            HandlePreviewDistance();
//            UpdatePreviewCheck();
//        }

//        private void HandlePreviewRotation()
//        {
//            float yaw = keys.RotateAxis.action.ReadValue<float>();
//            if (Mathf.Abs(yaw) > 0.1f)
//                PreviewAnchor.Rotate(Vector3.up, yaw * PreviewRotateSpeed * Time.deltaTime);
//        }

//        private void HandlePreviewDistance()
//        {
//            float dist = keys.DistanceAxis.action.ReadValue<float>();
//            if (Mathf.Abs(dist) > 0.1f)
//            {
//                _CurrentOffset.z += dist * PreviewDistanceSpeed * Time.deltaTime;
//                _CurrentOffset.z = Mathf.Clamp(_CurrentOffset.z, MinSpawnDistance, MaxSpawnDistance);
//                PreviewAnchor.localPosition = new Vector3(0, 0, _CurrentOffset.z);
//            }
//        }

//        private void UpdatePreviewCheck()
//        {
//            bool blocked = Physics.CheckSphere(
//                PreviewAnchor.position,
//                SpawnCheckRadius,
//                CollisionLayers,
//                QueryTriggerInteraction.Ignore
//            );

//            _CurrentPreview.name = blocked ? "PREVIEW_BLOCKED" : "PREVIEW_SAFE";
//        }
//        #endregion

//        #region Confirm / Cancel
//        private void OnConfirmSpawn(InputAction.CallbackContext ctx)
//        {
//            Debug.Log($"Confirm from: {ctx.control.path} | device: {ctx.control.device}");

//            if (!_IsPreviewing || !isLocalPlayer)
//            {
//                Debug.Log("No Preview / Not Local Player");
//                return;
//            }
//            if (_CurrentPreview.name == "PREVIEW_BLOCKED")
//            {
//                Debug.Log("Preview Blocked");
//                return;
//            }

//            var prefab = _PreviewData.VehiclePrefab;
//            var identity = prefab.GetComponent<NetworkIdentity>();

//            if (identity == null)
//            {
//                Debug.LogError("Vehicle prefab missing NetworkIdentity");
//                CleanupPreview();
//                return;
//            }
//            //CleanupPreview();

//            CmdSpawnVehicle(
//                identity.assetId,
//                PreviewAnchor.position,
//                PreviewAnchor.rotation
//            );

//            Debug.Log("OnConfirmSpawn");

//        }

//        private void OnCancelSpawn(InputAction.CallbackContext ctx)
//        {
//            if (!_IsPreviewing || !isLocalPlayer) return;
//            CleanupPreview();
//            Debug.Log("OnCancelSpawn");

//        }

//        private void CleanupPreview()
//        {
//            if (_CurrentPreview)
//                Destroy(_CurrentPreview);

//            Debug.Log("Destroy Successful");
//            _CurrentPreview = null;
//            _IsPreviewing = false;
//            _PreviewData = null;

//            if (PreviewAnchor)
//            {
//                PreviewAnchor.localPosition = Vector3.zero;
//                PreviewAnchor.localRotation = Quaternion.identity;
//            }
//            Debug.Log("CleanupPreview");

//        }
//        #endregion

//        #region Command
//        [Command]
//        private void CmdSpawnVehicle(uint assetId, Vector3 pos, Quaternion rot)
//        {
//            var prefab = NetworkManager.singleton.spawnPrefabs
//                .FirstOrDefault(p => p.GetComponent<NetworkIdentity>().assetId == assetId);
//            Debug.Log(prefab.name);

//            if (prefab == null) return;

//            var vehicle = Instantiate(prefab, pos, rot);

//            if (vehicle.TryGetComponent(out Rigidbody rb))
//            {
//                rb.isKinematic = false;
//                rb.WakeUp();
//            }

//            NetworkServer.Spawn(vehicle);
//            Debug.Log("CmdSpawnVehicle");

//        }
//        #endregion
//    }
//}
using Mirror;
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;
using System.Collections.Generic;
using System.Collections;

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
        private bool IsBusy;

        // --- Setup & Input Toggles ---

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            this.enabled = true;

            // Input setup
            keys.RotateAxis.action.Enable();
            keys.DistanceAxis.action.Enable();
            keys.Confirm.action.Enable();
            keys.Cancel.action.Enable();

            keys.Confirm.action.started += OnConfirmSpawn;
            keys.Cancel.action.started += OnCancelSpawn;

            // Subscribe to static UI event (safe, local-only subscription)
            VehicleSpawnEvents.OnStartPreviewRequested += StartVehiclePreview;
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();
            this.enabled = false;

            // Unsubscribe and cleanup
            keys.Confirm.action.started -= OnConfirmSpawn;
            keys.Cancel.action.started -= OnCancelSpawn;

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
            Debug.Log($"Confirm from: {ctx.control.path} | device: {ctx.control.device}");
            StartCoroutine(SpawnVehicle());
        }

        private IEnumerator SpawnVehicle()
        {
            if (IsBusy) yield break;
            if (!_IsPreviewing || !isOwned)
                yield break;

            // Check if the preview is currently colliding
            if (_CurrentPreview.name == "PREVIEW_BLOCKED")
            {
                Debug.LogWarning("Cannot spawn vehicle: Location blocked.");
                yield break;
            }
            IsBusy = true;
            GameObject prefab = _PreviewData.VehiclePrefab;
            NetworkIdentity identity = prefab.GetComponent<NetworkIdentity>();

            if (identity == null)
            {
                Debug.LogError($"Vehicle Prefab '{prefab.name}' is missing a NetworkIdentity component. Cannot spawn.");
                CleanupPreview();
                yield break;
            }

            // Get the Asset ID (network-safe identifier)
            uint assetId = identity.assetId;

            // Get final spawn position and rotation from the PreviewAnchor
            Vector3 spawnPos = PreviewAnchor.transform.position;
            Quaternion spawnRot = PreviewAnchor.transform.rotation;

            // Call the command, passing the safe Asset ID
            CmdSpawnVehicle(assetId, spawnPos, spawnRot);

            CleanupPreview();
            IsBusy = false;

            yield return null;
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
            newVehicle.name = "SERVER SPAWN";
            // 4. Spawn it on the network
            NetworkServer.Spawn(newVehicle);
        }
    }
}