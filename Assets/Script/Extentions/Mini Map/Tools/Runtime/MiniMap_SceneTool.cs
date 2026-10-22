using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// Runtime helper for MiniMap control buttons.
    /// Online Target: G7ThreeDModeController → Local_Player_Cylinder.
    /// FakePlayer is OFFLINE test only. NO NetworkIdentity.
    /// </summary>
    [AddComponentMenu("Meta/Tools/MiniMap Scene Tool")]
    [RequireComponent(typeof(Meta_MiniMap))]
    public class MiniMap_SceneTool : MonoBehaviour
    {
        [SerializeField] private Meta_MiniMap _miniMap;

        /// <summary>Set by editor tool so Awake does not double-wire persistent listeners.</summary>
        [HideInInspector] public bool ButtonsWiredExternally;

        [SerializeField] private Button _toggleButton;
        [SerializeField] private Button _zoomInButton;
        [SerializeField] private Button _zoomOutButton;
        [SerializeField] private Button _spawnPlayerButton;
        [SerializeField] private Button _simulateRespawnButton;

        private GameObject _fakePlayer;

        private void Awake()
        {
            if (_miniMap == null)
                _miniMap = GetComponent<Meta_MiniMap>();

            // Never keep an empty ghost instance: prefer the component that owns the rig.
            if (_miniMap == null || !MiniMap_ControlPanel.IsRigCandidate(_miniMap))
            {
                var best = MiniMap_ControlPanel.BestMiniMapInstance(gameObject);
                if (best != null && best != _miniMap)
                {
                    _miniMap = best;
                    Debug.Log($"[MiniMap_SceneTool] Wired to Meta_MiniMap '{best.gameObject.name}' (previous instance had no rig references).");
                }
            }

            if (!ButtonsWiredExternally)
            {
                AutoFindButtons();
                ConnectButtons();
            }
        }

        private void AutoFindButtons()
        {
            var canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Transform panel = null;
            foreach (var c in canvases)
            {
                var t = c.transform.Find("Meta_MiniMap_Controls");
                if (t != null) { panel = t; break; }
            }
            if (panel == null) return;

            _toggleButton = panel.Find("Toggle Radar")?.GetComponent<Button>();
            _zoomInButton = panel.Find("Zoom +")?.GetComponent<Button>();
            _zoomOutButton = panel.Find("Zoom -")?.GetComponent<Button>();
            _spawnPlayerButton = panel.Find("Fake Player")?.GetComponent<Button>();
            _simulateRespawnButton = panel.Find("Respawn")?.GetComponent<Button>();
        }

        private void ConnectButtons()
        {
            if (_miniMap == null) return;
            if (_toggleButton != null) _toggleButton.onClick.AddListener(ToggleMap);
            if (_zoomInButton != null) _zoomInButton.onClick.AddListener(ZoomIn);
            if (_zoomOutButton != null) _zoomOutButton.onClick.AddListener(ZoomOut);
            if (_spawnPlayerButton != null) _spawnPlayerButton.onClick.AddListener(SpawnFakePlayer);
            if (_simulateRespawnButton != null) _simulateRespawnButton.onClick.AddListener(SimulateRespawn);
        }

        public void ToggleMap()
        {
            if (_miniMap == null) return;
            _miniMap.ToggleMiniMap(!_miniMap.IsEnabled);
        }

        public void ZoomIn()
        {
            if (_miniMap != null) _miniMap.ZoomInButton();
        }

        public void ZoomOut()
        {
            if (_miniMap != null) _miniMap.ZoomOutButton();
        }

        public void SpawnFakePlayer()
        {
            if (_miniMap == null) return;
            if (_fakePlayer != null) return;

            _fakePlayer = new GameObject("FakePlayer");
            _fakePlayer.transform.position = new Vector3(0f, 1.2f, 0f);

            // Visible body for RT
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(_fakePlayer.transform, false);
            body.transform.localPosition = Vector3.zero;
            var col = body.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Rotation reference only — must stay DISABLED (enabled camera steals screen)
            var camGO = new GameObject("PlayerCamera");
            camGO.transform.SetParent(_fakePlayer.transform, false);
            camGO.transform.localPosition = new Vector3(0f, 1.7f, 0f);
            var refCam = camGO.AddComponent<Camera>();
            refCam.enabled = false;

            EnsureTestFloor(_fakePlayer.transform.position);

            _miniMap.AssignTarget(_fakePlayer.transform, "SceneTool:FakePlayer");
            _miniMap.ToggleMiniMap(true);
            Debug.Log("[MiniMap_SceneTool] FakePlayer spawned + Target assigned + map enabled.");
        }

        public void SimulateRespawn()
        {
            if (_fakePlayer != null)
            {
                if (_miniMap != null)
                {
                    _miniMap.Target = null;
                    _miniMap.ToggleMiniMap(false);
                }
                if (Application.isPlaying) Destroy(_fakePlayer);
                else DestroyImmediate(_fakePlayer);
                _fakePlayer = null;
            }
            SpawnFakePlayer();
        }

        private static void EnsureTestFloor(Vector3 playerPos)
        {
            if (GameObject.Find("MiniMap_TestFloor") != null) return;

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "MiniMap_TestFloor";
            floor.transform.position = new Vector3(playerPos.x, -0.01f, playerPos.z);
            floor.transform.localScale = new Vector3(3f, 1f, 3f);
        }

        private void OnDestroy()
        {
            if (_fakePlayer == null) return;
            if (Application.isPlaying) Destroy(_fakePlayer);
            else DestroyImmediate(_fakePlayer);
        }
    }
}