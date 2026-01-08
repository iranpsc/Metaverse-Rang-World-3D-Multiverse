using Meta.Vehicle;
using Mirror;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

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
        public Transform SpawnPoint;
        public Transform PreviewAnchor;

        public SpawnKeys keys;

        [Header("Settings")]
        public float PreviewRotateSpeed = 120f;
        public float PreviewDistanceSpeed = 5f;
        public float MinSpawnDistance = 1f;
        public float MaxSpawnDistance = 15f;
        public float SpawnCheckRadius = 1.5f;
        public LayerMask CollisionLayers;

        private GameObject _CurrentPreview;
        private VehicleSpawnData _PreviewData;
        private bool _IsPreviewing;
        private Vector3 _CurrentOffset;

        #region Authority
        public override void OnStartLocalPlayer()
        {
            enabled = true;
                
            keys.RotateAxis.action.Enable();
            keys.DistanceAxis.action.Enable();
            keys.Confirm.action.Enable();
            keys.Cancel.action.Enable();

            keys.Confirm.action.started += OnConfirmSpawn;
            keys.Cancel.action.started += OnCancelSpawn;

            VehicleSpawnEvents.OnStartPreviewRequested += StartVehiclePreview;
        }

        public override void OnStopLocalPlayer()
        {
            enabled = false;

            keys.Confirm.action.started -= OnConfirmSpawn;
            keys.Cancel.action.started -= OnCancelSpawn;

            keys.RotateAxis.action.Disable();
            keys.DistanceAxis.action.Disable();
            keys.Confirm.action.Disable();
            keys.Cancel.action.Disable();

            VehicleSpawnEvents.OnStartPreviewRequested -= StartVehiclePreview;

            CleanupPreview();
        }
        #endregion

        #region Preview
        public void StartVehiclePreview(VehicleSpawnData data)
        {
            if (!isLocalPlayer) return;

            CleanupPreview();

            _PreviewData = data;
            _IsPreviewing = true;

            PreviewAnchor.localRotation = Quaternion.identity;
            _CurrentOffset = data.InitialOffset;

            // ⚠️ instantiate same prefab BUT sanitize it
            _CurrentPreview = Instantiate(data.VehiclePrefab, PreviewAnchor);
            _CurrentPreview.transform.localPosition = Vector3.zero;
            _CurrentPreview.transform.localRotation = Quaternion.identity;

            PreviewAnchor.localPosition = new Vector3(0, 0, _CurrentOffset.z);

            SanitizePreviewObject(_CurrentPreview);
        }

        private void SanitizePreviewObject(GameObject preview)
        {
            // ❌ Network
            if (preview.TryGetComponent(out NetworkIdentity net))
                net.enabled = false;

            // ❌ Vehicle logic
            foreach (var vehicle in preview.GetComponentsInChildren<Meta_VehicleSystem>())
                vehicle.enabled = false;

            // ❌ Any other vehicle base logic
            foreach (var v in preview.GetComponentsInChildren<Meta_VehicleBase>())
                v.enabled = false;

            // ❌ Physics
            foreach (var rb in preview.GetComponentsInChildren<Rigidbody>())
            {
                rb.isKinematic = true;
                rb.useGravity = false;   // 🔥 خیلی مهم
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            foreach (var col in preview.GetComponentsInChildren<Collider>())
                col.enabled = false;

            foreach (var wc in preview.GetComponentsInChildren<WheelCollider>())
                wc.enabled = false;
        }

        #endregion

        #region Update
        private void Update()
        {
            if (!_IsPreviewing || !isLocalPlayer) return;

            HandlePreviewRotation();
            HandlePreviewDistance();
            UpdatePreviewCheck();
        }

        private void HandlePreviewRotation()
        {
            float yaw = keys.RotateAxis.action.ReadValue<float>();
            if (Mathf.Abs(yaw) > 0.1f)
                PreviewAnchor.Rotate(Vector3.up, yaw * PreviewRotateSpeed * Time.deltaTime);
            Debug.Log("HandlePreviewRotation");

        }

        private void HandlePreviewDistance()
        {
            float dist = keys.DistanceAxis.action.ReadValue<float>();
            if (Mathf.Abs(dist) > 0.1f)
            {
                _CurrentOffset.z += dist * PreviewDistanceSpeed * Time.deltaTime;
                _CurrentOffset.z = Mathf.Clamp(_CurrentOffset.z, MinSpawnDistance, MaxSpawnDistance);
                PreviewAnchor.localPosition = new Vector3(0, 0, _CurrentOffset.z);
            }
            Debug.Log("HandlePreviewDistance");

        }

        private void UpdatePreviewCheck()
        {
            bool blocked = Physics.CheckSphere(
                PreviewAnchor.position,
                SpawnCheckRadius,
                CollisionLayers,
                QueryTriggerInteraction.Ignore
            );

            _CurrentPreview.name = blocked ? "PREVIEW_BLOCKED" : "PREVIEW_SAFE";
            Debug.Log("UpdatePreviewCheck");

        }
        #endregion

        #region Confirm / Cancel
        private void OnConfirmSpawn(InputAction.CallbackContext ctx)
        {
            Debug.Log($"Confirm from: {ctx.control.path} | device: {ctx.control.device}");

            if (!_IsPreviewing || !isLocalPlayer) return;
            if (_CurrentPreview.name == "PREVIEW_BLOCKED") return;

            var prefab = _PreviewData.VehiclePrefab;
            var identity = prefab.GetComponent<NetworkIdentity>();

            if (identity == null)
            {
                Debug.LogError("Vehicle prefab missing NetworkIdentity");
                CleanupPreview();
                return;
            }

            CmdSpawnVehicle(
                identity.assetId,
                PreviewAnchor.position,
                PreviewAnchor.rotation
            );

            CleanupPreview();
            //Debug.Log("OnConfirmSpawn");

        }

        private void OnCancelSpawn(InputAction.CallbackContext ctx)
        {
            if (!_IsPreviewing || !isLocalPlayer) return;
            CleanupPreview();
            Debug.Log("OnCancelSpawn");

        }

        private void CleanupPreview()
        {
            if (_CurrentPreview)
                Destroy(_CurrentPreview);

            Debug.Log("Destroy Successful");
            _CurrentPreview = null;
            _IsPreviewing = false;
            _PreviewData = null;

            if (PreviewAnchor)
            {
                PreviewAnchor.localPosition = Vector3.zero;
                PreviewAnchor.localRotation = Quaternion.identity;
            }
            Debug.Log("CleanupPreview");

        }
        #endregion

        #region Command
        [Command]
        private void CmdSpawnVehicle(uint assetId, Vector3 pos, Quaternion rot)
        {
            var prefab = NetworkManager.singleton.spawnPrefabs
                .FirstOrDefault(p => p.GetComponent<NetworkIdentity>().assetId == assetId);

            if (prefab == null) return;

            var vehicle = Instantiate(prefab, pos, rot);

            if (vehicle.TryGetComponent(out Rigidbody rb))
            {
                rb.isKinematic = false;
                rb.WakeUp();
            }

            NetworkServer.Spawn(vehicle);
            Debug.Log("CmdSpawnVehicle");

        }
        #endregion
    }
}
