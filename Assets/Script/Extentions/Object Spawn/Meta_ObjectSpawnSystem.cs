using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Player/ObjectSpawner")]
    public class Meta_ObjectSpawnerSystem : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform SpawnPoint;
        public static Meta_ObjectSpawnerSystem Instance;

        [Header("UI References")]
        [SerializeField] private GameObject SpawnPanel;
        [SerializeField] private Button ButtonSubmit;
        [SerializeField] private Button ButtonCancel;
        [SerializeField] private Button ButtonForward;
        [SerializeField] private Button ButtonBackward;
        [SerializeField] private Button ButtonRotateLeft;
        [SerializeField] private Button ButtonRotateRight;

        [Header("Settings")]
        public GameObject ObjectToSpawn;
        public LayerMask CollisionMask;
        public float MoveSpeed = 2f;
        public float RotateSpeed = 50f;
        public float MaxDistance = 5f;

        [Header("Input Actions")]
        [SerializeField] private InputActionReference Move;
        [SerializeField] private InputActionReference Rotate;
        [SerializeField] private InputActionReference Submit;
        [SerializeField] private InputActionReference Cancel;

        [Header("Debug")]
        [SerializeField] private bool DebugMode;

        private GameObject PreviewInstance;
        private bool IsPlacing;
        private bool CanPlace = true;

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            Instance = this;

            Move.action.Enable();
            Rotate.action.Enable();
            Submit.action.Enable();
            Cancel.action.Enable();

            Move.action.performed += ctx => MoveObject(ctx.ReadValue<float>());
            Rotate.action.performed += ctx => RotateObject(ctx.ReadValue<float>());

            Submit.action.performed += OnSubmit;
            Cancel.action.performed += OnCancel;

            SetupUI();
        }

        private void SetupUI()
        {
            if (SpawnPanel == null)
            {
                SpawnPanel = GameObject.Find("SpawnPanel");
                if (SpawnPanel == null)
                {
                    Debug.LogWarning("SpawnPanel not found in scene.");
                    return;
                }
            }

            if (ButtonSubmit == null)
                ButtonSubmit = SpawnPanel.transform.Find("ButtonSubmit")?.GetComponent<Button>();
            if (ButtonCancel == null)
                ButtonCancel = SpawnPanel.transform.Find("ButtonCancel")?.GetComponent<Button>();
            if (ButtonForward == null)
                ButtonForward = SpawnPanel.transform.Find("ButtonForward")?.GetComponent<Button>();
            if (ButtonBackward == null)
                ButtonBackward = SpawnPanel.transform.Find("ButtonBackward")?.GetComponent<Button>();
            if (ButtonRotateLeft == null)
                ButtonRotateLeft = SpawnPanel.transform.Find("ButtonRotateLeft")?.GetComponent<Button>();
            if (ButtonRotateRight == null)
                ButtonRotateRight = SpawnPanel.transform.Find("ButtonRotateRight")?.GetComponent<Button>();

            if (ButtonSubmit != null) ButtonSubmit.onClick.AddListener(OnSubmitUI);
            if (ButtonCancel != null) ButtonCancel.onClick.AddListener(OnCancelUI);
            if (ButtonForward != null) ButtonForward.onClick.AddListener(() => MoveObject(1));
            if (ButtonBackward != null) ButtonBackward.onClick.AddListener(() => MoveObject(-1));
            if (ButtonRotateLeft != null) ButtonRotateLeft.onClick.AddListener(() => RotateObject(-1));
            if (ButtonRotateRight != null) ButtonRotateRight.onClick.AddListener(() => RotateObject(1));

            SpawnPanel.SetActive(false);
        }

        public void DoSpawn()
        {
            if (ObjectToSpawn == null || SpawnPoint == null)
            {
                Debug.LogWarning("Missing ObjectToSpawn or SpawnPoint.");
                return;
            }

            if (IsPlacing)
            {
                Debug.LogWarning("Already placing an object.");
                return;
            }

            PreviewInstance = Instantiate(ObjectToSpawn, SpawnPoint.position, Quaternion.identity, SpawnPoint);
            IsPlacing = true;

            Rigidbody _rb = PreviewInstance.GetComponent<Rigidbody>();
            if (_rb != null) _rb.isKinematic = true;

            Collider _col = PreviewInstance.GetComponent<Collider>();
            Collider _playerCol = GetComponent<Collider>();
            if (_col != null && _playerCol != null)
                Physics.IgnoreCollision(_col, _playerCol, true);

            if (SpawnPanel != null)
                SpawnPanel.SetActive(true);

            if (DebugMode) Debug.Log("Preview spawned and parented to SpawnPoint.");
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            if (!IsPlacing || PreviewInstance == null) return;

            float _moveInput = Move.action.ReadValue<float>();
            if (Mathf.Abs(_moveInput) > 0.01f)
                MoveObject(_moveInput);

            float _rotateInput = Rotate.action.ReadValue<float>();
            if (Mathf.Abs(_rotateInput) > 0.01f)
                RotateObject(_rotateInput);

            CheckPlacement();
        }

        private void CheckPlacement()
        {
            Collider _col = PreviewInstance.GetComponent<Collider>();
            if (_col == null) return;

            Bounds _b = _col.bounds;
            Vector3 _halfExtents = _b.extents;
            Quaternion _rot = PreviewInstance.transform.rotation;
            Vector3 _center = _b.center;

            Collider[] _hits = Physics.OverlapBox(_center, _halfExtents, _rot, CollisionMask);

            bool _wasCanPlace = CanPlace;
            CanPlace = true;
            foreach (var _hit in _hits)
            {
                if (_hit.transform == PreviewInstance.transform || _hit.transform == transform) continue;
                CanPlace = false;
                break;
            }

            // Visual sign (color or highlight)
            Renderer _r = PreviewInstance.GetComponentInChildren<Renderer>();
            if (_r != null)
            {
                _r.material.color = CanPlace ? Color.green : Color.red;
            }

            if (_wasCanPlace != CanPlace && DebugMode)
                Debug.Log("CanPlace changed: " + CanPlace);
        }

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

        private void RotateObject(float _dir)
        {
            if (!IsPlacing || PreviewInstance == null) return;
            PreviewInstance.transform.Rotate(Vector3.up, _dir * RotateSpeed * Time.deltaTime, Space.Self);
        }

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
                if (DebugMode) Debug.LogWarning("Cannot place object: collision detected.");
                return;
            }

            Vector3 _pos = PreviewInstance.transform.position;
            Quaternion _rot = PreviewInstance.transform.rotation;

            Destroy(PreviewInstance);
            PreviewInstance = null;
            IsPlacing = false;

            if (SpawnPanel != null)
                SpawnPanel.SetActive(false);

            CmdSpawnObject(_pos, _rot);

            if (DebugMode) Debug.Log("Submitted object for network spawn.");
        }

        private void OnCancelUI()
        {
            if (!IsPlacing || PreviewInstance == null) return;

            Destroy(PreviewInstance);
            IsPlacing = false;
            PreviewInstance = null;

            if (SpawnPanel != null)
                SpawnPanel.SetActive(false);

            if (DebugMode) Debug.Log("Object placement canceled.");
        }

        [Command]
        private void CmdSpawnObject(Vector3 _pos, Quaternion _rot)
        {
            GameObject _spawned = Instantiate(ObjectToSpawn, _pos, _rot);
            Rigidbody _rb = _spawned.GetComponent<Rigidbody>();
            if (_rb != null) _rb.isKinematic = false;

            NetworkServer.Spawn(_spawned);

            if (DebugMode) Debug.Log($"Server spawned object at {_pos}");
        }
    }
}
