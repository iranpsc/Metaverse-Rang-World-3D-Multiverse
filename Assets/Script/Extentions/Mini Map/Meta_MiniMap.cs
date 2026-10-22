// Client-only; uses Network_A environment for local Target.
// MiniMap follows local player in world space (no parenting).
// Target resolved from Network_A RealtimePlayerMovementSender or local player roots.

using UnityEngine;
using UnityEngine.UI;
using Mirror;
using System;
using System.Reflection;

namespace Meta
{
    [AddComponentMenu("Meta/Mini Map")]
    public class Meta_MiniMap : MonoBehaviour
    {
        [Header("Platform")]
        [SerializeField] private PlatformType _platform = PlatformType.Unknow;

        [Header("References")]
        [Tooltip("Target to follow (auto-assigned to local player if null)")]
        public Transform Target;
        public GameObject MiniMap;
        public GameObject VRMiniMap;
        public Camera MiniMapCamera;

        [Header("MiniMap Render Output")]
        [Tooltip("RawImage that displays the minimap camera output. Leave empty for viewport mode.")]
        public RawImage MiniMapRenderImage;
        [Tooltip("Optional explicit RenderTexture. Auto-created (256x256) if empty and MiniMapRenderImage is set.")]
        public RenderTexture MiniMapRenderTexture;
        [Range(32, 1024)] public int MiniMapTextureSize = 256;

        [Header("Player Direction UI")]
        public Image PlayerDirectionImage;
        public Image VRPlayerDirectionImage;

        [Header("Settings")]
        public Vector3 Offset = new Vector3(0, 100, 0);
        public float FollowSpeed = 10f;
        public bool RotateWithPlayer = true;
        public bool IsEnabled = false;

        [Header("Radar Camera")]
        [Tooltip("Cut everything ABOVE the player plane (ceilings, roofs, overhangs) with the near clip plane so the radar shows the floor instead of the top of the ceiling.")]
        public bool ClipAboveRigPlane = true;
        [Tooltip("Turns the rig's RadarBoostLight on (straight down, no shadows) while the map is ON. Only useful when renderersInFrustum > 0 but the world still renders too dark from above.")]
        public bool UseRadarBoostLight = false;

        private float _clipProbeTimer;
        private float _groundY;
        private float _headHeight = 1f;
        private bool _clipProbeValid;
        private bool _hadValidTarget;
        private Light _radarBoostLight;

        [Header("Zoom Settings")]
        public float ZoomSpeed = 50f;
        public float MinZoom = 5f;
        public float MaxZoom = 100f;

        // Runtime copies to preserve inspector values
        private float _runtimeMinZoom;
        private float _runtimeMaxZoom;
        private float _targetZoomValue;
        private float _initialOrthographicSize;
        private float _initialOffsetY;

        private Transform _cameraTransform;

        // Respawn/change verification (slower poll)
        private const float RESPAWN_CHECK_INTERVAL = 0.5f;
        private float _respawnCheckTimer;

        // HUD panel fade (CanvasGroup)
        private const float PANEL_FADE_SECONDS = 0.12f;
        private Coroutine _fadeRoutine;

        // Diagnostics: one full health dump per enable, throttled follow lines
        private bool _loggedEnabledDiagnostics;
        private bool _wasEnabled;
        private float _followLogTimer;

        // Cached type for RealtimePlayerMovementSender (Network_A)
        private static Type _realtimePlayerMovementSenderType;

        private void Awake()
        {
            // 7) Pure dedicated server: disable if no local client (batch mode = headless server)
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            // 5) Platform detection
            _platform = Meta_PlatformDetector.GetPlatformType();

            // Cache initial zoom values
            _runtimeMinZoom = MinZoom;
            _runtimeMaxZoom = MaxZoom;

            // Cache RealtimePlayerMovementSender type via reflection
            CacheRealtimePlayerMovementSenderType();
        }

        private void CacheRealtimePlayerMovementSenderType()
        {
            try
            {
                // Try to find the type in any loaded assembly
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType("Network_A.GameIntegration.Movement.RealtimePlayerMovementSender");
                    if (type != null)
                    {
                        _realtimePlayerMovementSenderType = type;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (EnableLog) Debug.LogWarning($"[MiniMap] Failed to cache RealtimePlayerMovementSender type: {ex.Message}");
            }
        }

        private void Start()
        {
            // Auto-find camera if not assigned
            if (MiniMapCamera == null)
                MiniMapCamera = GetComponentInChildren<Camera>(true);

            SetupRenderOutput();
            InitializeZoomValues();
            CacheRadarBoostLight();

            // Visual only: keep the panel in sync with IsEnabled so an OFF radar never shows
            // an empty scrap (the camera is disabled, so the RT would be blank).
            if (MiniMap != null) SetPanelVisible(MiniMap, IsEnabled && !IsVRPlatform());

            // Initial resolve attempt
            TryResolveTarget();
        }

        /// <summary>Optional downward light created by the test rig; enabled only while the map is on.</summary>
        private void CacheRadarBoostLight()
        {
            if (_radarBoostLight != null) return;

            var lights = GetComponentsInChildren<Light>(true);
            foreach (var l in lights)
            {
                if (l != null && l.name == "RadarBoostLight")
                {
                    _radarBoostLight = l;
                    return;
                }
            }
        }

        /// <summary>
        /// Wires the minimap camera into a RenderTexture + RawImage so the top-down
        /// world view is visible on the UI panel. If no RawImage is assigned the
        /// component runs in viewport mode (camera draws to a corner rect).
        /// </summary>
        private void SetupRenderOutput()
        {
            if (MiniMapCamera == null) return;

            MiniMapCamera.orthographic = true;
            MiniMapCamera.clearFlags = CameraClearFlags.SolidColor;
            // Blue-grey void instead of pure black so an empty radar still reads as a surface
            MiniMapCamera.backgroundColor = new Color(0.15f, 0.18f, 0.22f, 1f);

            // Render every layer: player, terrain, remote clones
            MiniMapCamera.cullingMask = ~0;

            // Near/far slice - THIS is what makes a top-down radar show the FLOOR
            ApplyCameraClipPlanes();

            // Straight-down top view. Never inherit player pitch/roll.
            MiniMapCamera.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            MiniMapCamera.transform.localPosition = Vector3.zero;

            ConfigureUrpCamera();

            if (MiniMapRenderImage == null)
            {
                // Viewport fallback: still visible, bottom-left corner
                MiniMapCamera.rect = new Rect(0f, 0f, 0.25f, 0.25f);
                MiniMapCamera.targetTexture = null;
                if (EnableLog)
                    Debug.LogWarning("[MiniMap] No MiniMapRenderImage assigned - viewport fallback (25% corner). Assign a RawImage for a clean panel.");
                return;
            }

            if (MiniMapRenderTexture == null)
            {
                MiniMapRenderTexture = new RenderTexture(
                    Mathf.Max(32, MiniMapTextureSize), Mathf.Max(32, MiniMapTextureSize), 16, RenderTextureFormat.ARGB32)
                {
                    name = "Meta_MiniMap_RT",
                    useMipMap = false,
                    antiAliasing = 1,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                MiniMapRenderTexture.Create();
            }

            MiniMapCamera.targetTexture = MiniMapRenderTexture;
            MiniMapRenderImage.texture = MiniMapRenderTexture;
            MiniMapRenderImage.raycastTarget = false;
            MiniMapCamera.rect = new Rect(0f, 0f, 1f, 1f);

            if (EnableLog)
                Debug.Log($"[MiniMap] RenderOutput ready: RT={MiniMapRenderTexture.width}x{MiniMapRenderTexture.height} orthoSize={MiniMapCamera.orthographicSize} mask={MiniMapCamera.cullingMask}");
        }

        /// <summary>
        /// Sets the clip slice for the top-down radar.
        /// With ClipAboveRigPlane everything ABOVE the player's head is clipped (ceiling,
        /// roof, overhangs), so the radar shows the floor instead of the top of the ceiling.
        /// The slice is self-correcting: clamped so the floor is NEVER clipped and the
        /// player's body is NEVER clipped, whatever the player Y or Offset.y happen to be.
        /// </summary>
        private void ApplyCameraClipPlanes()
        {
            if (MiniMapCamera == null) return;

            float altitude = Mathf.Max(1f, Offset.y);

            // No local player yet (room left / not spawned): there is no ground to slice
            // against, so use plain safe planes and never report a bogus groundY.
            if (Target == null)
            {
                _clipProbeValid = false;
                MiniMapCamera.nearClipPlane = 0.3f;
                MiniMapCamera.farClipPlane = 500f;
                return;
            }

            if (!ClipAboveRigPlane)
            {
                MiniMapCamera.nearClipPlane = 0.3f;
                MiniMapCamera.farClipPlane = 2000f;
                return;
            }

            RefreshClipProbe();                       // throttled ground/head probe

            float camY = MiniMapCamera.transform.position.y;
            float groundDistance = Mathf.Max(0.5f, camY - _groundY);      // floor must stay visible
            float headDistance = altitude + _headHeight;                  // body must stay visible

            float cut = headDistance + 0.25f;                              // just above the head
            float near = Mathf.Min(cut, groundDistance - 0.5f);            // but never into the floor

            MiniMapCamera.nearClipPlane = Mathf.Clamp(near, 0.3f, Mathf.Max(0.3f, groundDistance - 0.1f));
            MiniMapCamera.farClipPlane = groundDistance + 500f;
        }

        /// <summary>Ground height under the player + height of the player's head above its origin.</summary>
        private void RefreshClipProbe()
        {
            if (Target == null)
            {
                _clipProbeValid = false;
                return;
            }

            _clipProbeTimer += Time.unscaledDeltaTime;
            if (_clipProbeValid && _clipProbeTimer < 0.25f) return;
            _clipProbeTimer = 0f;

            _headHeight = EstimateHeadHeight();
            _groundY = EstimateGroundY();
            _clipProbeValid = true;
        }

        private float EstimateHeadHeight()
        {
            if (Target == null) return 1f;

            float maxY = 0f;
            var renderers = Target.GetComponentsInChildren<Renderer>(false);
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                maxY = Mathf.Max(maxY, r.bounds.max.y - Target.position.y);
            }
            return Mathf.Clamp(maxY, 0.25f, 6f);
        }

        private float EstimateGroundY()
        {
            if (Target == null) return 0f;   // no player -> no ground; never probe

            // Start slightly BELOW the player origin so the ray never starts inside the
            // player's own capsule / CharacterController.
            var origin = Target.position + Vector3.down * 0.5f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null) continue;
                if (Target != null && h.collider.transform.IsChildOf(Target)) continue;  // skip ourselves
                if (h.distance < best) best = h.distance;
            }

            return best < float.MaxValue ? origin.y - best : Target.position.y - 0.5f;
        }

        /// <summary>
        /// URP hardening for a RenderTexture camera: Base renderer 0, no post-processing,
        /// no shadows, no AA. Without this the RT can come out black/graded differently
        /// from the Game view.
        /// </summary>
        private void ConfigureUrpCamera()
        {
            if (MiniMapCamera == null) return;

            // Dedicated module wins if the rig uses it
            var forced = MiniMapCamera.GetComponent<CameraForceRenderByIndex>();
            if (forced != null)
            {
                forced.ValidateRendererIndex();
                return;
            }

            var urp = MiniMapCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (urp == null)
                urp = MiniMapCamera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();

            urp.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Base;
            urp.renderPostProcessing = false;
            urp.renderShadows = false;
            urp.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None;
            urp.requiresColorOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;
            urp.SetRenderer(0);   // renderer 0 = default forward pass (not a UI/overlay-only one)
        }

        /// <summary>Renderers inside the radar frustum - 0 means "nothing to see" (altitude/layer), >0 means lighting.</summary>
        private int CountRenderersInFrustum()
        {
            if (MiniMapCamera == null) return -1;

            var planes = GeometryUtility.CalculateFrustumPlanes(MiniMapCamera);
            int count = 0;
            var renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                var go = r.gameObject;
                if (go == MiniMapCamera.gameObject || go.transform.IsChildOf(transform)) continue;
                if (GeometryUtility.TestPlanesAABB(planes, r.bounds)) count++;
            }
            return count;
        }

        private void InitializeZoomValues()
        {
            if (MiniMapCamera != null && MiniMapCamera.orthographic)
            {
                _initialOrthographicSize = MiniMapCamera.orthographicSize;

                // Start from a useful overview size so the player is visible immediately
                // instead of a 1-unit close-up inherited from an unset inspector value.
                if (_initialOrthographicSize < 5f) _initialOrthographicSize = 40f;

                _targetZoomValue = Mathf.Clamp(_initialOrthographicSize, 5f, 200f);
                if (MiniMapCamera != null) MiniMapCamera.orthographicSize = _targetZoomValue;
            }
            else
            {
                _initialOffsetY = Offset.y;
                _targetZoomValue = _initialOffsetY;
            }

            // Ensure valid range for runtime copies
            _runtimeMinZoom = Mathf.Max(0.5f, _runtimeMinZoom);
            _runtimeMaxZoom = Mathf.Max(_runtimeMinZoom + 0.1f, _runtimeMaxZoom);
            _targetZoomValue = Mathf.Clamp(_targetZoomValue, _runtimeMinZoom, _runtimeMaxZoom);
        }

        /// <summary>
        /// Target resolution (STRICT priority).
        /// A0) G7ThreeDModeController.GetLocalPlayerTransform()   <- real local player
        /// A1) GameObject.Find("Local_Player_Cylinder")
        /// A2) child of Dedicated_Players* whose name starts with "Local_Player"
        /// A3) RealtimePlayerMovementSender (Network_A realtime rooms)
        /// A4) Mirror NetworkClient.localPlayer (last resort)
        /// Remote_Player_* clones are NEVER treated as the local target.
        /// </summary>
        public void TryResolveTarget()
        {
            if (Target != null) return;

            // A0) G7ThreeDModeController - authoritative local player for Lobby 1 WebGL
            var g7Transform = TryResolveViaG7Controller();
            if (g7Transform != null)
            {
                AssignTarget(g7Transform, "A0:G7ThreeDModeController.GetLocalPlayerTransform");
                return;
            }

            // A1) Direct name lookup
            var direct = GameObject.Find("Local_Player_Cylinder");
            if (direct != null && direct.activeInHierarchy)
            {
                AssignTarget(direct.transform, "A1:Local_Player_Cylinder");
                return;
            }

            // A2) Scan Dedicated_Players* roots for a "Local_Player*" child
            var underRoot = FindLocalPlayerUnderDedicatedRoot();
            if (underRoot != null)
            {
                AssignTarget(underRoot, "A2:Dedicated_Players_Root/Local_*");
                return;
            }

            // A3) Network_A realtime movement sender (other room flows)
            var movementSender = FindRealtimePlayerMovementSender();
            if (movementSender != null && movementSender.transform != null)
            {
                AssignTarget(movementSender.transform, "A3:RealtimePlayerMovementSender");
                return;
            }

            // A4) Mirror fallback
            if (NetworkClient.active && NetworkClient.localPlayer != null)
            {
                AssignTarget(NetworkClient.localPlayer.transform, "A4:NetworkClient.localPlayer");
                return;
            }

            // None found - log exactly what was searched vs what exists
            if (EnableLog) LogResolveFailure();
        }

        // ---------------------------------------------------------------- A0
        private static Type _g7ControllerType;
        private static MethodInfo _g7GetLocalPlayerTransform;

        private void CacheG7ControllerType()
        {
            if (_g7ControllerType != null && _g7GetLocalPlayerTransform != null) return;

            try
            {
                // G7ThreeDModeController lives in the global namespace (no namespace).
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = assembly.GetType("G7ThreeDModeController");
                    if (t == null) continue;

                    var m = t.GetMethod("GetLocalPlayerTransform", BindingFlags.Instance | BindingFlags.Public);
                    if (m != null && typeof(Transform).IsAssignableFrom(m.ReturnType))
                    {
                        _g7ControllerType = t;
                        _g7GetLocalPlayerTransform = m;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (EnableLog) Debug.LogWarning($"[MiniMap] Failed to cache G7ThreeDModeController type: {ex.Message}");
            }
        }

        private Transform TryResolveViaG7Controller()
        {
            CacheG7ControllerType();
            if (_g7ControllerType == null || _g7GetLocalPlayerTransform == null) return null;

            var controller = FindFirstObjectByType(_g7ControllerType);
            if (controller == null) return null;

            try
            {
                var result = _g7GetLocalPlayerTransform.Invoke(controller, null) as Transform;
                if (result == null || result.Equals(null)) return null;

                // Reject remote clones defensively
                if (result.name.StartsWith("Remote_Player", StringComparison.OrdinalIgnoreCase))
                    return null;

                return result;
            }
            catch (Exception ex)
            {
                if (EnableLog) Debug.LogWarning($"[MiniMap] G7 GetLocalPlayerTransform failed: {ex.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- A2
        private static Transform FindLocalPlayerUnderDedicatedRoot()
        {
            var roots = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var root in roots)
            {
                if (!root.name.Contains("Dedicated_Players")) continue;

                for (int i = 0; i < root.childCount; i++)
                {
                    var child = root.GetChild(i);
                    if (child == null) continue;

                    var n = child.name;
                    if (n.StartsWith("Local_Player", StringComparison.OrdinalIgnoreCase)
                        || n.Equals("Local_Player_Cylinder", StringComparison.OrdinalIgnoreCase))
                    {
                        if (n.StartsWith("Remote_Player", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!child.gameObject.activeInHierarchy) continue;
                        return child;
                    }
                }
            }
            return null;
        }

        private float _lastFailureLogTime;

        private void LogResolveFailure()
        {
            if (Time.unscaledTime - _lastFailureLogTime < 1f) return;
            _lastFailureLogTime = Time.unscaledTime;

            CacheG7ControllerType();
            string g7Status = _g7ControllerType == null
                ? "NOT FOUND in any loaded assembly"
                : $"resolved -> {_g7ControllerType.Assembly.GetName().Name}";
            int g7Count = _g7ControllerType != null
                ? FindObjectsByType(_g7ControllerType, FindObjectsInactive.Include, FindObjectsSortMode.None).Length
                : 0;

            bool hasDirect = GameObject.Find("Local_Player_Cylinder") != null;
            var underRoot = FindLocalPlayerUnderDedicatedRoot();

            var senderType = _realtimePlayerMovementSenderType;
            int senderCount = senderType != null
                ? FindObjectsByType(senderType, FindObjectsInactive.Include, FindObjectsSortMode.None).Length
                : 0;

            int netIdLocalCount = 0;
            var identities = FindObjectsByType<NetworkIdentity>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var id in identities)
                if (id != null && id.isLocalPlayer) netIdLocalCount++;

            var namedRoots = new System.Text.StringBuilder();
            int named = 0;
            foreach (var t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var n = t.name;
                if (n.Contains("Dedicated_Players") || n.Contains("Local_Player")
                    || n.Contains("LocalPlayer") || n.Contains("Remote_Player"))
                {
                    if (named > 0) namedRoots.Append(", ");
                    namedRoots.Append(n).Append("(active=").Append(t.gameObject.activeInHierarchy).Append(")");
                    named++;
                    if (named >= 8) break;
                }
            }

            Debug.LogWarning(
                $"[MiniMap] Target==null | A0:G7ThreeDModeController {g7Status} controllers={g7Count} " +
                $"| A1:Local_Player_Cylinder found={hasDirect} " +
                $"| A2:underDedicatedRoot found={(underRoot != null ? underRoot.name : "null")} " +
                $"| A3:senders={senderCount} " +
                $"| A4:NetworkClient.active={NetworkClient.active} localPlayer=" +
                $"{(NetworkClient.localPlayer != null ? NetworkClient.localPlayer.name : "null")} " +
                $"localPlayerIdentities={netIdLocalCount} networkIdentityTotal={identities.Length} " +
                $"| candidates=[{namedRoots}]");
        }

        private Component FindRealtimePlayerMovementSender()
        {
            if (_realtimePlayerMovementSenderType != null)
            {
                return FindFirstObjectByType(_realtimePlayerMovementSenderType) as Component;
            }
            return null;
        }

        private Transform FindLocalPlayerAvatar()
        {
            // Kept for reference; real resolution is A0 -> A1 -> A2 -> A3 -> A4.
            return FindLocalPlayerUnderDedicatedRoot();
        }

        /// <summary>
        /// Assigns a new target and resolves its camera for rotation reference.
        /// </summary>
        public void AssignTarget(Transform newTarget, string sourcePath = "Unknown")
        {
            if (newTarget == null) return;

            // public Target is the single source of truth for ApplyFollow
            Target = newTarget;
            _hadValidTarget = true;

            // Find player's camera for rotation reference
            Camera playerCamera = Target.GetComponentInChildren<Camera>(true);
            _cameraTransform = (playerCamera != null) ? playerCamera.transform : Target;

            // Snap the minimap rig so the FIRST frame is already correct
            // (otherwise the ortho camera lerps from the world origin and looks empty).
            if (MiniMapCamera != null && MiniMapCamera.orthographic)
            {
                transform.position = Target.position + new Vector3(Offset.x, Offset.y, Offset.z);
                MiniMapCamera.orthographicSize = _targetZoomValue;
            }

            // Camera must be enabled or the RenderTexture receives nothing
            if (MiniMapCamera != null && IsEnabled)
                MiniMapCamera.enabled = true;

            if (EnableLog)
            {
                Debug.Log($"[MiniMap] Target={Target.name} source={sourcePath} cameraRef={(_cameraTransform != null ? _cameraTransform.name : "null")} | Target ready - ToggleMiniMap(true) shows the map");
            }
        }

        private void LateUpdate()
        {
            // While Target == null: resolve every frame
            if (Target == null)
            {
                // Assemblies may load after Awake - retry type lookups each poll
                if (_g7ControllerType == null) CacheG7ControllerType();
                if (_realtimePlayerMovementSenderType == null) CacheRealtimePlayerMovementSenderType();
                TryResolveTarget();
            }
            else
            {
                // Verify every 0.5s: not destroyed, local sender unchanged
                _respawnCheckTimer += Time.unscaledDeltaTime;

                if (_respawnCheckTimer >= RESPAWN_CHECK_INTERVAL)
                {
                    _respawnCheckTimer = 0f;
                    CheckForRespawn();
                }
            }

            // INVARIANT: the camera flag always mirrors IsEnabled, every frame.
            // This is what stops "IsEnabled=True but cam.enabled=False" after a room leave.
            if (MiniMapCamera != null && MiniMapCamera.enabled != IsEnabled)
                MiniMapCamera.enabled = IsEnabled;

            // Boost light follows the map state (never leaks into the main view)
            if (_radarBoostLight != null && _radarBoostLight.enabled != (IsEnabled && UseRadarBoostLight))
                _radarBoostLight.enabled = IsEnabled && UseRadarBoostLight;

            // Enable path (Inspector or programmatic): resolve, re-assert the chain, report once
            if (IsEnabled && !_wasEnabled)
            {
                if (Target == null) TryResolveTarget();

                if (MiniMap != null) SetPanelVisible(MiniMap, !IsVRPlatform());

                if (MiniMapCamera != null && MiniMapRenderTexture != null)
                {
                    if (MiniMapCamera.targetTexture != MiniMapRenderTexture)
                        MiniMapCamera.targetTexture = MiniMapRenderTexture;
                    if (MiniMapRenderImage != null && MiniMapRenderImage.texture != MiniMapRenderTexture)
                        MiniMapRenderImage.texture = MiniMapRenderTexture;
                }
                // RT was never created (refs wired after Start) -> (re)build the chain
                else if (MiniMapCamera != null && MiniMapRenderTexture == null && MiniMapRenderImage != null)
                {
                    SetupRenderOutput();
                    InitializeZoomValues();
                }

                if (!_loggedEnabledDiagnostics)
                {
                    _loggedEnabledDiagnostics = true;
                    LogHealthDump(Target != null ? "ENABLED" : "ENABLED WAITING-FOR-LOCAL-PLAYER");
                }
            }
            _wasEnabled = IsEnabled;

            if (IsEnabled && Target != null)
                ApplyFollow();
        }

        private void CheckForRespawn()
        {
            // Destroyed target compares == null in Unity, so this branch MUST come first:
            // clear the reference and let LateUpdate keep polling (A0..A4) for a new player.
            if (Target == null)
            {
                if (_hadValidTarget)
                {
                    _hadValidTarget = false;
                    if (EnableLog) Debug.Log("[MiniMap] Target destroyed (room left) - clearing and re-resolving");
                }
                _cameraTransform = null;
                _clipProbeValid = false;      // stale ground probe must not survive the player
                ApplyCameraClipPlanes();      // Target == null -> safe near/far
                return;
            }

            _hadValidTarget = true;

            // G7 respawned a new local player -> re-bind
            var g7Transform = TryResolveViaG7Controller();
            if (g7Transform != null && g7Transform != Target)
            {
                if (EnableLog) Debug.Log($"[MiniMap] G7 local player changed {Target.name} -> {g7Transform.name}");
                AssignTarget(g7Transform, "A0:G7ThreeDModeController.GetLocalPlayerTransform(respawn)");
            }
        }

        private void ApplyFollow()
        {
            if (Target == null || MiniMapCamera == null) return;

            // Orthographic: camera height comes from Offset.y, zoom ONLY changes orthographicSize.
            // (Using _targetZoomValue as height was the bug - double offset + wrong altitude.)
            float followHeight = MiniMapCamera.orthographic ? Offset.y : _targetZoomValue;
            Vector3 desired = Target.position + new Vector3(Offset.x, followHeight, Offset.z);

            // Clamp t so high FollowSpeed cannot overshoot / extrapolate past the target.
            float t = Mathf.Clamp01(Time.deltaTime * Mathf.Max(0f, FollowSpeed));
            transform.position = Vector3.Lerp(transform.position, desired, t);

            // Zoom transition (orthographicSize only - never move the camera for zoom)
            if (MiniMapCamera.orthographic)
            {
                MiniMapCamera.orthographicSize = Mathf.Lerp(
                    MiniMapCamera.orthographicSize,
                    _targetZoomValue,
                    Mathf.Clamp01(Time.deltaTime * 5f));
            }
            else
            {
                Offset.y = Mathf.Lerp(Offset.y, _targetZoomValue, Mathf.Clamp01(Time.deltaTime * 5f));
            }

            // Rotation (camera is a child of this transform, so it inherits)
            if (RotateWithPlayer && _cameraTransform != null)
                transform.rotation = Quaternion.Euler(0f, _cameraTransform.eulerAngles.y, 0f);
            else
                transform.rotation = Quaternion.Euler(0f, 0f, 0f);

            // Keep the camera straight down; never inherit player pitch/roll
            if (MiniMapCamera.transform.localRotation != Quaternion.Euler(90f, 0f, 0f))
                MiniMapCamera.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            // Re-assert the render settings every frame (cheap) so a changed Offset.y
            // or an inspector edit cannot put the camera back into "black void" mode.
            if (MiniMapCamera.cullingMask != ~0) MiniMapCamera.cullingMask = ~0;
            ApplyCameraClipPlanes();

            UpdateDirectionImage();

            // Throttled health line (1/s) so the user can confirm follow is alive
            if (EnableLog)
            {
                _followLogTimer += Time.unscaledDeltaTime;
                if (_followLogTimer >= 1f)
                {
                    _followLogTimer = 0f;
                    Debug.Log($"[MiniMap] FOLLOW ok rig={transform.position:F1} desired={desired:F1} " +
                              $"cam.enabled={MiniMapCamera.enabled} size={MiniMapCamera.orthographicSize:F1} " +
                              $"target={Target.name} rt={(MiniMapRenderImage != null && MiniMapRenderImage.texture != null ? MiniMapRenderImage.texture.name : "NONE")}");
                }
            }
        }

        /// <summary>
        /// One-shot diagnostic dump when the map is enabled. Names every broken link
        /// in the render chain: Target -> Camera -> RenderTexture -> RawImage -> Panel.
        /// </summary>
        private void LogHealthDump(string reason)
        {
            if (!EnableLog) return;

            var cam = MiniMapCamera;
            var rt = MiniMapRenderTexture;
            var img = MiniMapRenderImage;
            var panel = MiniMap;

            string targetInfo = Target != null
                ? $"{Target.name} pos={Target.position:F1}"
                : "NULL (call TryResolveTarget)";

            string camInfo = cam != null
                ? $"enabled={cam.enabled} ortho={cam.orthographic} size={cam.orthographicSize:F1} " +
                  $"pos={cam.transform.position:F1} targetPos={(Target != null ? Target.position.ToString("F1") : "NULL")} " +
                  $"rot={cam.transform.localRotation.eulerAngles:F0} mask={cam.cullingMask} " +
                  $"near={cam.nearClipPlane:F1} far={cam.farClipPlane:F0} " +
                  $"groundY={(_clipProbeValid ? _groundY.ToString("F1") : "n/a")} headH={(_clipProbeValid ? _headHeight.ToString("F1") : "n/a")} " +
                  $"targetTex={(cam.targetTexture != null ? cam.targetTexture.name : "NULL")} " +
                  $"renderersInFrustum={CountRenderersInFrustum()}"
                : "NULL (assign MiniMapCamera)";

            string rtInfo = rt != null
                ? $"{rt.width}x{rt.height} created={rt.IsCreated()}"
                : "NULL (assign MiniMapRenderTexture or call SetupRenderOutput)";

            string imgInfo = img != null
                ? $"texture={(img.texture != null ? img.texture.name : "NULL")} rect={img.rectTransform.rect.size}"
                : "NULL (assign MiniMapRenderImage - nothing can be displayed without it!)";

            string panelInfo = panel != null
                ? $"active={panel.activeSelf} size={panel.GetComponent<RectTransform>()?.sizeDelta.ToString() ?? "?"}"
                : "NULL (assign MiniMap panel)";

            Debug.Log($"[MiniMap] HEALTH ({reason}) | Target: {targetInfo} | Camera: {camInfo} | " +
                      $"RT: {rtInfo} | RawImage: {imgInfo} | Panel: {panelInfo} | " +
                      $"IsEnabled={IsEnabled} Offset={Offset:F0}");
        }

        // 4) ZoomInButton / ZoomOutButton: null-check + Mathf.Clamp to runtime copies
        public void ZoomInButton()
        {
            if (MiniMapCamera == null) return;
            float step = MiniMapCamera.orthographic ? 2f : 10f;
            _targetZoomValue = Mathf.Clamp(_targetZoomValue - step, _runtimeMinZoom, _runtimeMaxZoom);
        }

        public void ZoomOutButton()
        {
            if (MiniMapCamera == null) return;
            float step = MiniMapCamera.orthographic ? 2f : 10f;
            _targetZoomValue = Mathf.Clamp(_targetZoomValue + step, _runtimeMinZoom, _runtimeMaxZoom);
        }

        public void ZoomIn(float deltaTime)
        {
            if (MiniMapCamera == null) return;
            _targetZoomValue = Mathf.Clamp(_targetZoomValue - ZoomSpeed * deltaTime, _runtimeMinZoom, _runtimeMaxZoom);
        }

        public void ZoomOut(float deltaTime)
        {
            if (MiniMapCamera == null) return;
            _targetZoomValue = Mathf.Clamp(_targetZoomValue + ZoomSpeed * deltaTime, _runtimeMinZoom, _runtimeMaxZoom);
        }

        public void SetZoomValue(float value)
        {
            if (MiniMapCamera == null) return;
            _targetZoomValue = Mathf.Clamp(value, _runtimeMinZoom, _runtimeMaxZoom);
        }

        /// <summary>
        /// Rotates the CENTRE blip. GTA default (RotateWithPlayer = false):
        /// the radar stays north-up and the blip points where the player faces.
        /// With RotateWithPlayer = true the whole radar yaws instead and the
        /// blip stays fixed pointing up.
        /// </summary>
        private void UpdateDirectionImage()
        {
            if (PlayerDirectionImage == null && VRPlayerDirectionImage == null) return;

            float yRotation = ResolveBlipYaw();
            Quaternion rotation = Quaternion.Euler(0f, 0f, -yRotation);   // yaw 90 -> blip points right
            bool isVR = _platform == PlatformType.VR;

            if (RotateWithPlayer)
            {
                if (PlayerDirectionImage != null)
                    PlayerDirectionImage.rectTransform.localRotation = Quaternion.identity;
                if (isVR && VRPlayerDirectionImage != null)
                    VRPlayerDirectionImage.rectTransform.localRotation = Quaternion.identity;
            }
            else
            {
                if (PlayerDirectionImage != null)
                    PlayerDirectionImage.rectTransform.localRotation = rotation;
                if (isVR && VRPlayerDirectionImage != null)
                    VRPlayerDirectionImage.rectTransform.localRotation = rotation;
            }
        }

        /// <summary>
        /// Yaw shown by the centre blip: the target body's facing first
        /// (matches what the player sees on the radar), camera yaw as fallback.
        /// </summary>
        private float ResolveBlipYaw()
        {
            if (Target != null) return Target.eulerAngles.y;
            if (_cameraTransform != null) return _cameraTransform.eulerAngles.y;
            return 0f;
        }

        // 6) ToggleMiniMap: non-VR platforms -> MiniMap, VR -> VRMiniMap
        public void ToggleMiniMap(bool enable)
        {
            IsEnabled = enable;

            // Resolve IMMEDIATELY on enable (room leave -> rejoin case).
            // LateUpdate would only get to it next frame; the player may already exist.
            if (enable && Target == null) TryResolveTarget();

            bool showRegularMap = !IsVRPlatform() && MiniMap != null;
            bool showVRMap = IsVRPlatform() && VRMiniMap != null;

            // Production HUD fades via CanvasGroup; falls back to SetActive when absent
            if (MiniMap != null) SetPanelVisible(MiniMap, showRegularMap && IsEnabled);
            if (VRMiniMap != null) VRMiniMap.SetActive(showVRMap && IsEnabled);

            if (MiniMapCamera != null)
            {
                // SAME FRAME, unconditional: never leave IsEnabled=true with a disabled camera
                MiniMapCamera.enabled = enable;

                if (enable)
                {
                    // Re-assert the render chain (a stale rig may have lost the links)
                    if (MiniMapRenderTexture != null)
                    {
                        if (MiniMapCamera.targetTexture != MiniMapRenderTexture)
                            MiniMapCamera.targetTexture = MiniMapRenderTexture;
                        if (MiniMapRenderImage != null && MiniMapRenderImage.texture != MiniMapRenderTexture)
                            MiniMapRenderImage.texture = MiniMapRenderTexture;
                    }
                    else if (MiniMapRenderImage != null)
                    {
                        // RT reference lost entirely -> rebuild the chain
                        SetupRenderOutput();
                        InitializeZoomValues();
                    }

                    if (MiniMapCamera.orthographicSize <= 0.01f)
                        MiniMapCamera.orthographicSize = _targetZoomValue;
                    ApplyCameraClipPlanes();

                    // Snap so the first visible frame is already over the player
                    if (Target != null)
                        transform.position = Target.position + new Vector3(Offset.x, Offset.y, Offset.z);
                }
            }

            if (enable)
            {
                LogHealthDump(Target != null ? "ToggleMiniMap(true)" : "ToggleMiniMap(true) WAITING-FOR-LOCAL-PLAYER");
                if (Target == null && EnableLog)
                {
                    Debug.LogWarning("[MiniMap] Radar ON but Target is still null - waiting for the local player " +
                                     "(A0 G7ThreeDModeController -> A1 Local_Player_Cylinder -> A2 Dedicated_Players* " +
                                     "-> A3 RealtimePlayerMovementSender -> A4 Mirror). Camera is enabled, " +
                                     "the radar starts following the moment the player spawns.");
                }
            }
        }

        /// <summary>0.12s alpha fade when the panel has a CanvasGroup, hard SetActive otherwise.</summary>
        private void SetPanelVisible(GameObject panel, bool visible)
        {
            var group = panel.GetComponent<CanvasGroup>();
            if (group == null)
            {
                panel.SetActive(visible);
                return;
            }

            if (_fadeRoutine != null)
            {
                StopCoroutine(_fadeRoutine);
                _fadeRoutine = null;
            }

            group.interactable = visible;
            group.blocksRaycasts = visible;
            _fadeRoutine = StartCoroutine(FadeCanvasGroup(group, visible ? 1f : 0f, PANEL_FADE_SECONDS));
        }

        private System.Collections.IEnumerator FadeCanvasGroup(CanvasGroup group, float target, float seconds)
        {
            if (seconds <= 0f)
            {
                group.alpha = target;
                _fadeRoutine = null;
                yield break;
            }

            float start = group.alpha;
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                group.alpha = Mathf.Lerp(start, target, Mathf.Clamp01(t / seconds));
                yield return null;
            }
            group.alpha = target;
            _fadeRoutine = null;
        }

        private bool IsVRPlatform()
        {
            return _platform == PlatformType.VR;
        }

        private void OnDestroy()
        {
            if (MiniMapRenderTexture != null)
            {
                if (MiniMapCamera != null && MiniMapCamera.targetTexture == MiniMapRenderTexture)
                    MiniMapCamera.targetTexture = null;

                MiniMapRenderTexture.Release();
                Destroy(MiniMapRenderTexture);
                MiniMapRenderTexture = null;
            }
        }

        [Header("Debug")]
        [SerializeField] private bool EnableLog = true;
    }
}