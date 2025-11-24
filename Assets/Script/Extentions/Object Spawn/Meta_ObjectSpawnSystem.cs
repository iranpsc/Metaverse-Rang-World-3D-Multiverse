using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using System.Collections.Generic;

namespace Meta
{
    [AddComponentMenu("Meta/Player/ObjectSpawner")]
    public class Meta_ObjectSpawnerSystem : NetworkBehaviour
    {
        [Header("References")]
        public Transform SpawnPoint;
        public static Meta_ObjectSpawnerSystem Instance;

        [Header("UI References")]
        public GameObject SpawnPanel;
        public Button ButtonSubmit;
        public Button ButtonCancel;
        public Button ButtonForward;
        public Button ButtonBackward;
        public Button ButtonRotateLeft;
        public Button ButtonRotateRight;

        [Header("Settings")]
        // Backwards-compatible: you can set ObjectToSpawn directly (old button).
        // For multiplayer reliability prefer using SpawnablePrefabs + SelectedIndex.
        public GameObject ObjectToSpawn;
        public List<GameObject> SpawnablePrefabs = new List<GameObject>();

        [Header("Preview")]
        // Optional material applied only on preview instances (keeps original meshes intact in server spawn)
        public Material PreviewMaterial;
        public float MaxDistance = 5f;
        public float MoveSpeed = 2f;
        public float RotateSpeed = 50f;

        [Header("Input Actions (keep your keybinds here)")]
        public InputActionReference Move;
        public InputActionReference RotateY;
        public InputActionReference RotateZ;
        public InputActionReference Submit;
        public InputActionReference Cancel;

        [Header("Debug")]
        public bool DebugMode = false;

        // runtime
        [HideInInspector] public int SelectedIndex = -1; // used for index-based spawn buttons
        private GameObject PreviewInstance;
        private bool IsPlacing = false;
        private bool CanPlace = true;

        private Material[] _PreviewOriginalMaterials; // stored when applying preview
        private Rigidbody[] _PreviewOriginalRigidbodies; // stored components to tweak

        #region Unity Events

        private void Awake()
        {
            Instance = this;

            // If the spawn list is empty but NetworkManager has spawnPrefabs, auto-fill.
            if (SpawnablePrefabs == null) SpawnablePrefabs = new List<GameObject>();
            if (SpawnablePrefabs.Count == 0 && NetworkManager.singleton != null)
            {
                foreach (var _p in NetworkManager.singleton.spawnPrefabs)
                    if (_p != null && !SpawnablePrefabs.Contains(_p))
                        SpawnablePrefabs.Add(_p);
                if (DebugMode) Debug.Log($"[Spawner] Auto-loaded {SpawnablePrefabs.Count} prefabs from NetworkManager.");
            }
        }

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();

            // Enable input actions on authority (same as your original)
            if (Move != null) Move.action.Enable();
            if (RotateY != null) RotateY.action.Enable();
            if (RotateZ != null) RotateZ.action.Enable();
            if (Submit != null) Submit.action.Enable();
            if (Cancel != null) Cancel.action.Enable();

            if (Submit != null) Submit.action.performed += OnSubmit;
            if (Cancel != null) Cancel.action.performed += OnCancel;

            SetupUI();
        }

        private void OnDestroy()
        {
            if (Submit != null) Submit.action.performed -= OnSubmit;
            if (Cancel != null) Cancel.action.performed -= OnCancel;
        }

        private void SetupUI()
        {
            // Keep behavior compatible with your previous auto-find logic
            if (SpawnPanel == null)
            {
                SpawnPanel = GameObject.Find("SpawnPanel");
            }

            if (SpawnPanel != null)
            {
                if (ButtonSubmit == null) ButtonSubmit = SpawnPanel.transform.Find("ButtonSubmit")?.GetComponent<Button>();
                if (ButtonCancel == null) ButtonCancel = SpawnPanel.transform.Find("ButtonCancel")?.GetComponent<Button>();
                if (ButtonForward == null) ButtonForward = SpawnPanel.transform.Find("ButtonForward")?.GetComponent<Button>();
                if (ButtonBackward == null) ButtonBackward = SpawnPanel.transform.Find("ButtonBackward")?.GetComponent<Button>();
                if (ButtonRotateLeft == null) ButtonRotateLeft = SpawnPanel.transform.Find("ButtonRotateLeft")?.GetComponent<Button>();
                if (ButtonRotateRight == null) ButtonRotateRight = SpawnPanel.transform.Find("ButtonRotateRight")?.GetComponent<Button>();
            }

            if (ButtonSubmit != null) ButtonSubmit.onClick.AddListener(OnSubmitUI);
            if (ButtonCancel != null) ButtonCancel.onClick.AddListener(OnCancelUI);
            if (ButtonForward != null) ButtonForward.onClick.AddListener(() => MoveObject(1f));
            if (ButtonBackward != null) ButtonBackward.onClick.AddListener(() => MoveObject(-1f));
            if (ButtonRotateLeft != null) ButtonRotateLeft.onClick.AddListener(() => RotateObjectY(-1f));
            if (ButtonRotateRight != null) ButtonRotateRight.onClick.AddListener(() => RotateObjectY(1f));

            if (SpawnPanel != null) SpawnPanel.SetActive(false);
        }

        private void Update()
        {
            // keep input only for local player
            if (!isLocalPlayer) return;
            if (!IsPlacing || PreviewInstance == null) return;

            float _moveInput = 0f;
            if (Move != null) _moveInput = Move.action.ReadValue<float>();
            if (Mathf.Abs(_moveInput) > 0.01f) MoveObject(_moveInput);

            float _rotY = 0f;
            if (RotateY != null) _rotY = RotateY.action.ReadValue<float>();
            if (Mathf.Abs(_rotY) > 0.01f) RotateObjectY(_rotY);

            float _rotZ = 0f;
            if (RotateZ != null) _rotZ = RotateZ.action.ReadValue<float>();
            if (Mathf.Abs(_rotZ) > 0.01f) RotateObjectZ(_rotZ);

            CheckPlacement();
        }

        #endregion

        #region Public Spawn API (keeps old behaviour + new safe way)

        // Old behavior: client code can set ObjectToSpawn then call DoSpawn()
        // New recommended behavior: set SelectedIndex and have SpawnablePrefabs filled.
        public void DoSpawn()
        {
            // choose prefab: priority - SelectedIndex (if valid) -> ObjectToSpawn (if set) -> error
            GameObject _prefabToUse = null;
            if (SelectedIndex >= 0 && SelectedIndex < SpawnablePrefabs.Count)
                _prefabToUse = SpawnablePrefabs[SelectedIndex];
            else if (ObjectToSpawn != null)
                _prefabToUse = ObjectToSpawn;

            if (_prefabToUse == null)
            {
                if (DebugMode) Debug.LogError("[Spawner] Invalid spawn index or ObjectToSpawn is null.");
                return;
            }

            DoSpawn(_prefabToUse);
        }

        // Safe explicit API: pass the prefab you want to preview / spawn.
        // This supports your Meta_ObjectSpawnButton that calls DoSpawn(prefab) as well.
        public void DoSpawn(GameObject prefab)
        {
            if (prefab == null)
            {
                if (DebugMode) Debug.LogError("[Spawner] DoSpawn called with null prefab.");
                return;
            }

            if (SpawnPoint == null)
            {
                if (DebugMode) Debug.LogError("[Spawner] SpawnPoint is not assigned.");
                return;
            }

            if (IsPlacing)
            {
                if (DebugMode) Debug.LogWarning("[Spawner] Already placing an object.");
                return;
            }

            // create local preview copy
            PreviewInstance = Instantiate(prefab, SpawnPoint.position, SpawnPoint.rotation, SpawnPoint);
            IsPlacing = true;

            // store rigidbody adjustments
            _PreviewOriginalRigidbodies = PreviewInstance.GetComponentsInChildren<Rigidbody>();
            foreach (var _rb in _PreviewOriginalRigidbodies)
            {
                if (_rb != null) _rb.isKinematic = true;
            }

            // optionally apply preview material while keeping original materials saved
            if (PreviewMaterial != null)
            {
                var _renderers = PreviewInstance.GetComponentsInChildren<MeshRenderer>();
                if (_renderers != null && _renderers.Length > 0)
                {
                    // store original materials of the root renderer only (not strictly necessary but safe)
                    // we won't restore them because preview is destroyed; we store to debug if needed
                    _PreviewOriginalMaterials = new Material[_renderers.Length];
                    for (int i = 0; i < _renderers.Length; i++)
                    {
                        _PreviewOriginalMaterials[i] = _renderers[i].material;
                        _renderers[i].material = PreviewMaterial;
                    }
                }
            }

            // ignore collisions with the player if possible
            var _playerCollider = GetComponent<Collider>();
            var _previewColliders = PreviewInstance.GetComponentsInChildren<Collider>();
            if (_playerCollider != null && _previewColliders != null)
            {
                foreach (var _pc in _previewColliders)
                {
                    if (_pc != null) Physics.IgnoreCollision(_pc, _playerCollider, true);
                }
            }

            if (SpawnPanel != null) SpawnPanel.SetActive(true);
            if (DebugMode) Debug.Log($"[Spawner] Preview spawned for {prefab.name}");
        }

        #endregion

        #region Preview Controls

        private void MoveObject(float _dir)
        {
            if (!IsPlacing || PreviewInstance == null) return;

            Vector3 _move = Vector3.forward * _dir * MoveSpeed * Time.deltaTime;
            PreviewInstance.transform.localPosition += _move;

            float _dist = Mathf.Clamp(PreviewInstance.transform.localPosition.z, 0.5f, MaxDistance);
            PreviewInstance.transform.localPosition = new Vector3(
                PreviewInstance.transform.localPosition.x,
                PreviewInstance.transform.localPosition.y,
                _dist
            );
        }

        private void RotateObjectY(float _dir)
        {
            if (!IsPlacing || PreviewInstance == null) return;
            PreviewInstance.transform.Rotate(Vector3.up, _dir * RotateSpeed * Time.deltaTime, Space.Self);
        }

        private void RotateObjectZ(float _dir)
        {
            if (!IsPlacing || PreviewInstance == null) return;
            PreviewInstance.transform.Rotate(Vector3.forward, _dir * RotateSpeed * Time.deltaTime, Space.Self);
        }

        private void CheckPlacement()
        {
            if (PreviewInstance == null) return;
            var _col = PreviewInstance.GetComponent<Collider>();
            if (_col == null) return;

            Bounds _b = _col.bounds;
            Vector3 _halfExtents = _b.extents;
            Quaternion _rot = PreviewInstance.transform.rotation;
            Vector3 _center = _b.center;

            Collider[] _hits = Physics.OverlapBox(_center, _halfExtents, _rot, Physics.AllLayers);
            bool _wasCanPlace = CanPlace;
            CanPlace = true;
            foreach (var _hit in _hits)
            {
                if (_hit.transform == PreviewInstance.transform || _hit.transform == transform) continue;
                CanPlace = false;
                break;
            }

            var _r = PreviewInstance.GetComponentInChildren<Renderer>();
            if (_r != null)
            {
                // use green/red on preview (only if PreviewMaterial wasn't provided)
                if (PreviewMaterial == null)
                {
                    _r.material.color = CanPlace ? Color.green : Color.red;
                }
            }

            if (_wasCanPlace != CanPlace && DebugMode) Debug.Log("[Spawner] CanPlace changed: " + CanPlace);
        }

        #endregion

        #region Submit / Cancel (Input & UI)

        private void OnSubmit(InputAction.CallbackContext _ctx)
        {
            if (_ctx.performed) OnSubmitUI();
        }

        private void OnCancel(InputAction.CallbackContext _ctx)
        {
            if (_ctx.performed) OnCancelUI();
        }

        private void OnSubmitUI()
        {
            if (!IsPlacing || PreviewInstance == null) return;
            if (!CanPlace)
            {
                if (DebugMode) Debug.LogWarning("[Spawner] Cannot place object: collision detected.");
                return;
            }

            // gather spawn info
            Vector3 _pos = PreviewInstance.transform.position;
            Quaternion _rot = PreviewInstance.transform.rotation;

            // determine which prefab was previewed - prefer SelectedIndex mapping, otherwise try ObjectToSpawn or match by name
            GameObject _prefab = null;
            if (SelectedIndex >= 0 && SelectedIndex < SpawnablePrefabs.Count)
                _prefab = SpawnablePrefabs[SelectedIndex];
            else if (ObjectToSpawn != null)
                _prefab = ObjectToSpawn;
            else
            {
                // try to find by name from preview instance
                string _prefabName = PreviewInstance.name.Replace("(Clone)", "").Trim();
                foreach (var _p in SpawnablePrefabs)
                {
                    if (_p != null && _p.name == _prefabName)
                    {
                        _prefab = _p;
                        break;
                    }
                }
            }

            // cleanup preview locally
            Destroy(PreviewInstance);
            PreviewInstance = null;
            IsPlacing = false;
            if (SpawnPanel != null) SpawnPanel.SetActive(false);

            if (_prefab == null)
            {
                if (DebugMode) Debug.LogError("[Spawner] Could not determine prefab to spawn on server.");
                return;
            }

            // If the user used direct ObjectToSpawn flow, ensure server knows about that prefab
            string _prefabNameToSend = _prefab.name;
            CmdSpawnObject(_prefabNameToSend, _pos, _rot);
        }

        private void OnCancelUI()
        {
            if (!IsPlacing || PreviewInstance == null) return;

            Destroy(PreviewInstance);
            IsPlacing = false;
            PreviewInstance = null;
            if (SpawnPanel != null) SpawnPanel.SetActive(false);

            if (DebugMode) Debug.Log("[Spawner] Placement canceled.");
        }

        #endregion

        #region Server Spawn

        // We send the prefab name to the server. Server looks up the matching prefab in its registry/list.
        [Command]
        private void CmdSpawnObject(string prefabName, Vector3 pos, Quaternion rot)
        {
            // Try to find prefab in SpawnablePrefabs by name first
            GameObject _serverPrefab = null;

            // Check list first (if the server build used same list)
            foreach (var _p in SpawnablePrefabs)
            {
                if (_p != null && _p.name == prefabName)
                {
                    _serverPrefab = _p;
                    break;
                }
            }

            // If not found, try NetworkManager.spawnPrefabs as fallback
            if (_serverPrefab == null && NetworkManager.singleton != null)
            {
                foreach (var _p in NetworkManager.singleton.spawnPrefabs)
                {
                    if (_p != null && _p.name == prefabName)
                    {
                        _serverPrefab = _p;
                        break;
                    }
                }
            }

            // If still not found, we can't spawn
            if (_serverPrefab == null)
            {
                Debug.LogError($"[Spawner][Server] Prefab '{prefabName}' not found in server lists. Make sure it's registered in NetworkManager.spawnPrefabs or added to SpawnablePrefabs on server.");
                return;
            }

            GameObject _spawned = Instantiate(_serverPrefab, pos, rot);
            _spawned.transform.SetParent(null);

            // Make sure physics acts correctly on server object
            var _rb = _spawned.GetComponent<Rigidbody>();
            if (_rb != null) _rb.isKinematic = false;

            // ensure prefab is registered in NetworkManager.spawnPrefabs (safety)
            if (NetworkManager.singleton != null && !NetworkManager.singleton.spawnPrefabs.Contains(_serverPrefab))
            {
                NetworkManager.singleton.spawnPrefabs.Add(_serverPrefab);
                if (DebugMode) Debug.Log($"[Spawner][Server] Auto-registered {_serverPrefab.name} in NetworkManager.spawnPrefabs.");
            }

            NetworkServer.Spawn(_spawned, connectionToClient);

            if (DebugMode) Debug.Log($"[Spawner][Server] Spawned '{_spawned.name}' at {pos}");
        }

        #endregion
    }
}
