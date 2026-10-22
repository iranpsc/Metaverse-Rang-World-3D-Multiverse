using UnityEngine;
using UnityEngine.Rendering;

namespace Meta
{
    /// <summary>
    /// Real day/night cycle: the sun light physically rotates, its intensity, shadow strength
    /// and the ambient light all follow the sun elevation curve. Shadows are mandatory so the
    /// player and the floor visibly light up as the sun moves.
    ///
    /// Lifecycle contract (kept stable):
    ///   Awake            -> caches rotation / intensity / ambient / skybox / shadow settings
    ///   ToggleCycle(true)-> starts the rotation coroutine + applies shadow config
    ///   ToggleCycle(false)-> stops it and restores the EXACT cached lighting state
    ///   OnDisable/OnDestroy -> stop + restore if it was running
    /// </summary>
    [AddComponentMenu("Meta/Day Night Cycle")]
    [HelpURL("https://google.com")]
    public class Meta_DayNightCycle : MonoBehaviour
    {
        /// <summary>Name of the directional light this module creates when the scene has none.</summary>
        public const string SUN_LIGHT_NAME = "Meta_Sun_Light";
        /// <summary>Name of the visible sun sphere this module creates when none is assigned.</summary>
        public const string SUN_DISC_NAME = "Meta_Sun_Disc";

        [Header("References")]
        public Light Sun;
        public Material DayNightSkybox;

        [Header("Cycle")]
        [Tooltip("Degrees per second. 360 / value = seconds for one full day. 2 = 180 s per day.")]
        public float RotationSpeed = 2f;
        [Tooltip("World axis the sun orbits around (X = classic east-west sun path).")]
        public Vector3 RotationAxis = Vector3.right;
        public float MaxIntensity = 1.2f;
        public float MinIntensity = 0f;            // night
        [Tooltip("Ambient intensity at noon / at midnight.")]
        public float AmbientDayIntensity = 1f;
        public float AmbientNightIntensity = 0.2f;
        [Tooltip("Tint the ambient sky colour along the day/night curve.")]
        public bool TintAmbientColor = true;
        public Color AmbientDayColor = new Color(0.62f, 0.69f, 0.78f, 1f);
        public Color AmbientNightColor = new Color(0.10f, 0.12f, 0.20f, 1f);

        [Header("Sun Elevation Curve")]
        [Tooltip("Sun forward . up at which the day starts / is fully bright.")]
        public float DayElevationStart = -0.15f;
        public float DayElevationFull = 0.35f;

        [Header("Shadows")]
        public bool EnableShadows = true;
        public LightShadows ShadowType = LightShadows.Soft;
        [Range(0f, 1f)] public float DayShadowStrength = 0.9f;
        [Range(0f, 1f)] public float NightShadowStrength = 0.15f;
        public float ShadowBias = 0.05f;
        public float ShadowNormalBias = 0.5f;

        [Header("Visible Sun Disc")]
        [Tooltip("Optional. Assign any object (sphere / quad) to be driven as the VISIBLE sun. " +
                 "Leave empty and one named 'Meta_Sun_Disc' is created automatically at runtime.")]
        public Transform VisibleSun;
        [Tooltip("Create 'Meta_Sun_Disc' when VisibleSun is empty.")]
        public bool CreateVisibleSunIfMissing = true;
        [Tooltip("How far from the focus point the disc sits. Auto-clamped inside the camera far clip plane.")]
        [Range(50f, 2000f)] public float SunDiscDistance = 300f;
        [Tooltip("Apparent size of the disc. It is auto-scaled with distance so the on-screen size stays constant.")]
        [Range(5f, 80f)] public float SunDiscScale = 25f;
        [Tooltip("What the disc orbits around. Empty = Camera.main, else this transform.")]
        public Transform SunFocusPoint;
        [Tooltip("Disc tint at noon.")]
        public Color SunDiscDayColor = new Color(1f, 0.97f, 0.86f, 1f);
        [Tooltip("Disc tint at the horizon (dawn / dusk).")]
        public Color SunDiscHorizonColor = new Color(1f, 0.58f, 0.24f, 1f);
        [Tooltip("Keep the disc rendered while the sun is below the horizon (moon-like).")]
        public bool ShowDiscAtNight = false;
        [Tooltip("Base albedo of the disc. #FFD27A keeps it gold instead of blowing out to white.")]
        public Color DiscBaseColor = new Color(1f, 0.824f, 0.478f, 1f);
        [Range(1f, 5f)] public float DiscEmissionIntensity = 2.2f;
        [Tooltip("Add a larger additive halo around the disc. This is what makes it read as a sun " +
                 "even when the URP asset has no bloom post-processing.")]
        public bool DiscGlow = true;
        [Range(1.1f, 3f)] public float DiscGlowScale = 1.45f;
        [Range(0f, 1f)] public float DiscGlowAlpha = 0.45f;
        [Tooltip("Subtle breathing scale so the sun never looks like a static debug sphere.")]
        [Range(0f, 0.1f)] public float DiscPulse = 0.02f;
        public float DiscPulseSpeed = 0.7f;

        [Header("Light Colour")]
        [Tooltip("Directional light colour at noon.")]
        public Color SunDayColor = new Color(1f, 0.957f, 0.878f, 1f);      // #FFF4E0
        [Tooltip("Directional light colour near the horizon (dawn / dusk).")]
        public Color SunDuskColor = new Color(1f, 0.706f, 0.541f, 1f);     // #FFB48A
        [Tooltip("Directional light colour during twilight.")]
        public Color SunNightColor = new Color(0.56f, 0.66f, 0.85f, 1f);

        [Header("Atmosphere (optional)")]
        [Tooltip("Optional distance fog tinted with the time of day. Off by default; the full fog " +
                 "state is cached and restored when the cycle stops.")]
        public bool EnableFog = false;
        [Range(0f, 0.02f)] public float FogDensity = 0.004f;
        [Tooltip("Optional procedural gradient sky, used ONLY when DayNightSkybox is empty. " +
                 "The original skybox is always restored when the cycle stops.")]
        public bool CreateGradientSky = false;

        [Header("Setup")]
        [Tooltip("Create a Directional Light named Meta_Sun_Light when the scene has none.")]
        public bool CreateSunIfMissing = true;

        public bool IsEnabled = false;

        // ---- Cached initial state (restored byte-for-byte by ResetToDefault) ----
        private Quaternion _defaultSunRotation;
        private Material _defaultSkybox;
        private float _defaultSunIntensity;
        private float _defaultAmbientIntensity;
        private Color _defaultAmbientSkyColor;
        private LightShadows _defaultShadows;
        private float _defaultShadowStrength;
        private float _defaultShadowBias;
        private float _defaultShadowNormalBias;
        private bool _defaultWasRenderSettingsSun;
        private Light _defaultRenderSettingsSun;
        private bool _shadowsApplied;

        // ---- Visible sun disc: only objects WE create are ever destroyed ----
        private GameObject _createdDisc;
        private GameObject _createdGlow;
        private Light _createdSunLight;
        private Material _discMaterial;
        private Material _glowMaterial;
        private Texture2D _discTexture;
        private Texture2D _glowTexture;
        private bool _warnedFarClip;
        private bool _warnedShader;

        // ---- Optional atmosphere cache ----
        private Color _defaultSunColor;
        private bool _defaultFog;
        private Color _defaultFogColor;
        private FogMode _defaultFogMode;
        private float _defaultFogDensity;
        private Material _createdSkyMaterial;

        // ---- Absolute time model + authority (part 1/6 state, part 2/6 modes) ----
        [SerializeField, Tooltip("Who drives the environment. LocalAuthoritySimulate = this peer " +
                                 "advances the clock itself; ApplyOnly = this peer only renders " +
                                 "incoming ApplyState() snapshots.")]
        private EnvironmentAuthorityMode _authorityMode = EnvironmentAuthorityMode.ApplyOnly;

        [SerializeField, Range(0f, 1f), Tooltip("0 = midnight, 1 = next midnight. Single source of truth " +
                                                "for the cycle: the sun rotation is derived from it.")]
        private float _time01 = 0f;

        [Header("Remote Smoothing")]
        [Tooltip("Incoming jumps smaller than this are chased with a short slerp (kills packet jitter). " +
                 "Bigger jumps SNAP, so a joining client never watches the sun fly across the sky.")]
        [Range(1f, 90f)] public float RemoteSnapThresholdDeg = 25f;

        [Tooltip("Smoothing sharpness for small remote corrections. Higher = snappier. " +
                 "4 settles a small correction in roughly 0.25 s.")]
        [Range(0.5f, 30f)] public float RemoteSmoothingSharpness = 4f;

        [Tooltip("How often the visible sun disc + glow are repositioned. Rotation, light, ambient and " +
                 "shadows update EVERY frame; only the disc is throttled (0.08 s = ~12 Hz).")]
        [Range(0.02f, 0.5f)] public float DiscUpdateInterval = 0.08f;

        [Header("Persistence (per place)")]
        [Tooltip("Identifies the PLACE whose time of day is persisted - e.g. 'lobby_1'. " +
                 "Leave empty to disable persistence entirely.")]
        [SerializeField] private string _environmentId = "lobby_1";

        [Tooltip("Persist the environment state through IEnvironmentDayNightStore.")]
        [SerializeField] private bool _persistEnvironment = true;

        [Tooltip("Seconds between autosaves while the cycle runs. Meaningful changes (toggle, speed, " +
                 "restored time) save immediately regardless of this.")]
        [Min(1f)] public float EnvironmentAutosaveInterval = 10f;

        [Header("HTTP Store (Node.js JSON files)")]
        [SerializeField, Tooltip("Use the HTTP store (Node.js) instead of local PlayerPrefs. Requires a " +
                                 "Base URL; falls back to PlayerPrefs automatically while it is empty.")]
        private bool _useHttpStore = false;

        [SerializeField, Tooltip("Base URL of the environment service, e.g. http://127.0.0.1:8788. " +
                                 "Contract: {base}/environments/{environmentId}/daynight.json")]
        private string _httpBaseUrl = "";

        private EnvironmentDayNightHttpStore _httpStore;

        private Quaternion _cycleBaseRotation;   // sun orientation at _time01 == 0
        private bool _ownerDeniedLogged;
        private float _lastDiscUpdate = -999f;
        private bool _discVisible;
        private bool _environmentReady;

        // ---- Persistence ----
        private static IEnvironmentDayNightStore _defaultStore;
        private IEnvironmentDayNightStore _store;              // injected or the shared default
        private float _lastAutosaveAt = -999f;
        private bool _loadedFromStore;
        private int _lastSavedMinute = -1;
        private bool _lastSavedRunning;
        private float _lastSavedSpeed = -1f;

        /// <summary>The PLACE whose day/night is persisted. Empty disables persistence.</summary>
        public string EnvironmentId
        {
            get => _environmentId;
            set { if (_environmentId == value) return; _environmentId = value; _loadedFromStore = false; }
        }

        /// <summary>
        /// Assign the PLACE id from the lobby bootstrap. Flushes the outgoing place first (owner
        /// only - the gate inside <see cref="SaveEnvironmentState"/>) and then loads the new place's
        /// stored state, so "enter environment -> load daynight.json" is one call.
        /// </summary>
        public void SetEnvironmentId(string environmentId)
        {
            if (_environmentId == (environmentId ?? string.Empty)) return;

            // Leaving the previous place: persist ITS time of day under the OLD id first.
            SaveEnvironmentState();

            EnvironmentId = environmentId;
            _lastAutosaveAt = -999f;

            if (PersistenceEnabled) LoadEnvironmentState();
        }

        /// <summary>True once a load from the store was started (sync hit or HTTP request fired).</summary>
        public bool LoadedFromStore => _loadedFromStore;

        public bool PersistenceEnabled => _persistEnvironment && !string.IsNullOrEmpty(_environmentId);

        /// <summary>
        /// Store used for persistence. Defaults to a shared <see cref="EnvironmentDayNightPlayerPrefsStore"/>;
        /// inject a server-backed implementation with <see cref="SetStore"/> before the cycle starts.
        /// </summary>
        public IEnvironmentDayNightStore Store
        {
            get
            {
                if (_store != null) return _store;

                if (_useHttpStore && !string.IsNullOrEmpty(_httpBaseUrl))
                {
                    if (_httpStore == null) _httpStore = new EnvironmentDayNightHttpStore();
                    if (_httpStore.BaseUrl != _httpBaseUrl) _httpStore.BaseUrl = _httpBaseUrl;
                    return _httpStore;
                }

                if (_defaultStore == null) _defaultStore = new EnvironmentDayNightPlayerPrefsStore();
                return _store = _defaultStore;
            }
            set => _store = value;
        }

        /// <summary>
        /// SERVER HOOK: inject a store backed by Network_A / the dedicated server so the state is
        /// shared by every machine instead of living in PlayerPrefs. Called from the lobby bootstrap,
        /// NOT from this module - Network_A is intentionally untouched here.
        /// </summary>
        public void SetStore(IEnvironmentDayNightStore store)
        {
            _store = store;
            if (store == null) _loadedFromStore = false;
        }

        // ---- Cached lookups: never Find per frame ----
        private Camera _focusCamera;
        private float _focusCameraCheckedAt = -999f;
        private Renderer _discRenderer;
        private Renderer _glowRenderer;

        /// <summary>The visible sun object, or null when the cycle has none.</summary>
        public Transform VisibleSunTransform => VisibleSun;
        public bool HasVisibleSun => VisibleSun != null;

        /// <summary>
        /// Who drives the environment. LocalAuthoritySimulate advances the clock in
        /// <see cref="Tick"/>; ApplyOnly never simulates and only renders incoming snapshots, so a
        /// visitor can never double-simulate the owner's cycle.
        /// </summary>
        // Enum lives at namespace level in EnvironmentDayNightState.cs (shared with the store).

        // ---- Environment state (public read-only) ----
        /// <summary>
        /// Write permission and simulation role in ONE flag, so the two can never disagree:
        /// the owner simulates, a visitor only applies.
        /// </summary>
        public bool IsEnvironmentOwner
        {
            get => _authorityMode == EnvironmentAuthorityMode.LocalAuthoritySimulate;
            set => _authorityMode = value
                ? EnvironmentAuthorityMode.LocalAuthoritySimulate
                : EnvironmentAuthorityMode.ApplyOnly;
        }

        /// <summary>Authority mode of this peer.</summary>
        public EnvironmentAuthorityMode AuthorityMode
        {
            get => _authorityMode;
            set => _authorityMode = value;
        }

        /// <summary>True when this peer advances the clock itself.</summary>
        public bool SimulatesLocally => _authorityMode == EnvironmentAuthorityMode.LocalAuthoritySimulate;

        // ---- Environment state (public read-only) ----
        /// <summary>Absolute cycle position: 0 = midnight, 1 = next midnight.</summary>
        public float Time01 => _time01;

        /// <summary>Degrees travelled around <see cref="RotationAxis"/> (0-360).</summary>
        public float SunAngle => _time01 * 360f;

        /// <summary>Normalized world axis the sun orbits around.</summary>
        public Vector3 CycleAxis =>
            RotationAxis.sqrMagnitude < 0.0001f ? Vector3.right : RotationAxis.normalized;

        /// <summary>Format version of <see cref="EnvironmentDayNightState"/>.</summary>
        public const int STATE_VERSION = EnvironmentDayNightState.StateVersion;

        // Public read-only access (tests / tools)
        public Quaternion DefaultSunRotation => _defaultSunRotation;
        public float DefaultSunIntensity => _defaultSunIntensity;
        public float DefaultAmbientIntensity => _defaultAmbientIntensity;
        public Material DefaultSkybox => _defaultSkybox;

        [Header("Debugger")]
        [SerializeField] private bool _enableLog = false;
        public bool EnableLog
        {
            get => _enableLog;
            set => _enableLog = value;
        }

        private void Awake()
        {
            CacheInitialValues();
            LoadEnvironmentState();
        }

        /// <summary>Auto-find or create the directional sun, then cache the whole lighting state.</summary>
        private void CacheInitialValues()
        {
            _defaultSkybox = RenderSettings.skybox;
            _defaultAmbientIntensity = RenderSettings.ambientIntensity;
            _defaultAmbientSkyColor = RenderSettings.ambientSkyColor;
            _defaultRenderSettingsSun = RenderSettings.sun;
            _defaultWasRenderSettingsSun = _defaultRenderSettingsSun != null;
            _defaultFog = RenderSettings.fog;
            _defaultFogColor = RenderSettings.fogColor;
            _defaultFogMode = RenderSettings.fogMode;
            _defaultFogDensity = RenderSettings.fogDensity;

            ResolveSun();

            if (Sun != null)
            {
                _defaultSunRotation = Sun.transform.rotation;
                _defaultSunIntensity = Sun.intensity;
                _defaultSunColor = Sun.color;
                _defaultShadows = Sun.shadows;

                // Anchor the absolute-time model so the cycle continues from the current sun angle.
                _time01 = Mathf.Repeat(_time01, 1f);
                _cycleBaseRotation = _defaultSunRotation * Quaternion.AngleAxis(-_time01 * 360f, CycleAxis);
                _defaultShadowStrength = Sun.shadowStrength;
                _defaultShadowBias = Sun.shadowBias;
                _defaultShadowNormalBias = Sun.shadowNormalBias;
            }
        }

        private void ResolveSun()
        {
            if (Sun == null) Sun = RenderSettings.sun;

            if (Sun == null)
            {
                var lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (var l in lights)
                {
                    if (l != null && l.type == LightType.Directional) { Sun = l; break; }
                }
            }

            if (Sun == null && CreateSunIfMissing)
            {
                var go = new GameObject(SUN_LIGHT_NAME);
                go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                Sun = go.AddComponent<Light>();
                Sun.type = LightType.Directional;
                Sun.intensity = MaxIntensity;
                Sun.shadows = ShadowType;
                _createdSunLight = Sun;
                if (EnableLog)
                    Debug.LogWarning($"[Meta_DayNightCycle] No directional light in scene - created '{SUN_LIGHT_NAME}'.");
            }

            if (Sun == null && EnableLog)
                Debug.LogWarning("[Meta_DayNightCycle] No Sun available and CreateSunIfMissing is off.");
        }

        private void OnEnable()
        {
            if (IsEnabled) ApplyToggle();
        }

        private void OnDisable()
        {
            SaveEnvironmentState();          // the place remembers its time of day
            if (IsEnabled) ResetToDefault();
        }

        private void OnDestroy()
        {
            SaveEnvironmentState();
            if (IsEnabled) ResetToDefault();
            DestroyCreatedObjects();
        }

        // =====================================================================
        // Public API
        // =====================================================================
        public void ToggleCycle(bool enable)
        {
            if (!CanWriteEnvironment()) return;
            IsEnabled = enable;
            ApplyToggle();
            SaveIfMeaningfullyChanged();       // running state changed -> persist now
        }

        public void ForceResetToDefault()
        {
            if (!CanWriteEnvironment()) return;
            ResetToDefault();
            IsEnabled = false;
        }

        public void RecacheInitialValues()
        {
            if (!CanWriteEnvironment()) return;
            CacheInitialValues();
            if (EnableLog) Debug.Log("[Meta_DayNightCycle] Initial lighting values re-cached");
        }

        // =====================================================================
        // Persistence (per PLACE, via IEnvironmentDayNightStore)
        // =====================================================================

        /// <summary>
        /// Restore the stored time of day for this environment. Runs from <c>Awake</c> and is allowed on
        /// EVERY client: a visitor loading the place's last known time is correct, and the owner's live
        /// snapshot simply overwrites it as soon as the first packet arrives.
        /// </summary>
        public bool LoadEnvironmentState()
        {
            if (!PersistenceEnabled) return false;

            IEnvironmentDayNightStore store = Store;
            EnvironmentDayNightState stored;
            if (store.TryLoad(_environmentId, out stored))
            {
                stored.IsOwner = SimulatesLocally;                 // data, not a command: the role stays local
                ApplyState(stored, snap: true);
                _loadedFromStore = true;

                if (EnableLog)
                    Debug.Log($"[Meta_DayNightCycle] Restored '{_environmentId}' from {store.Describe} -> {stored}");
                return true;
            }

            // HTTP store: the synchronous read is a stub by design (a blocking read would stall the
            // render thread), so the real read is fired here and applied in the callback.
            var asyncStore = store as IEnvironmentDayNightStoreAsync;
            if (asyncStore == null) return false;

            _loadedFromStore = true;              // one request per environment id
            string environmentId = _environmentId;
            string describe = store.Describe;
            asyncStore.TryLoadAsync(environmentId, (loaded, ok) =>
            {
                if (!ok || _environmentId != environmentId) return;   // place changed / miss / server down

                loaded.IsOwner = SimulatesLocally;                // data, not a command: the role stays local
                ApplyState(loaded, snap: true);

                if (EnableLog)
                    Debug.Log($"[Meta_DayNightCycle] Restored '{environmentId}' from {describe} -> {loaded}");
            });
            return true;
        }

        /// <summary>Lobby wiring name for <see cref="LoadEnvironmentState"/> - same behaviour.</summary>
        public bool TryLoadEnvironmentState() => LoadEnvironmentState();

        /// <summary>
        /// Persist the current time of day for this environment. Only the owner writes: a visitor's
        /// local copy must never overwrite the place's state.
        /// </summary>
        public bool SaveEnvironmentState()
        {
            if (!PersistenceEnabled) return false;
            if (!SimulatesLocally) return false;

            // CaptureState already reports IsOwner = SimulatesLocally = true here, and the store
            // (PlayerPrefs or Http) re-stamps version / environmentId / updatedAt.
            Store.Save(_environmentId, CaptureState());
            _lastAutosaveAt = Time.unscaledTime;
            return true;
        }

        /// <summary>Autosave on a slow timer so a crash mid-session loses at most a few seconds.</summary>
        private void AutosaveIfDue()
        {
            if (!PersistenceEnabled || !SimulatesLocally || !IsEnabled) return;
            if (Time.unscaledTime - _lastAutosaveAt < Mathf.Max(1f, EnvironmentAutosaveInterval)) return;
            SaveEnvironmentState();
        }

        /// <summary>Saves when something actually changed: running state, speed, or time-of-day.</summary>
        private void SaveIfMeaningfullyChanged()
        {
            if (!PersistenceEnabled || !SimulatesLocally) return;

            int minute = Mathf.RoundToInt(_time01 * 1440f);
            bool changed = _lastSavedMinute != minute
                           || _lastSavedRunning != IsEnabled
                           || !Mathf.Approximately(_lastSavedSpeed, RotationSpeed);

            if (!changed) return;

            _lastSavedMinute = minute;
            _lastSavedRunning = IsEnabled;
            _lastSavedSpeed = RotationSpeed;
            SaveEnvironmentState();
        }

        private void ResetSaveBaseline()
        {
            _lastSavedMinute = Mathf.RoundToInt(_time01 * 1440f);
            _lastSavedRunning = IsEnabled;
            _lastSavedSpeed = RotationSpeed;
        }

        // =====================================================================
        // State capture / apply (no UI, no ownership required to APPLY)
        // =====================================================================

        /// <summary>Owner-side: read the full environment state to broadcast.</summary>
        public EnvironmentDayNightState CaptureState()
        {
            return new EnvironmentDayNightState
            {
                Time01 = _time01,
                SunAngle = SunAngle,
                CycleRunning = IsEnabled,
                RotationSpeed = RotationSpeed,
                IsOwner = SimulatesLocally,
                BaseRotation = _cycleBaseRotation,
                Version = STATE_VERSION
            }.Stamp(_environmentId);              // environmentId + fresh updatedAt for the HTTP contract
        }

        /// <summary>
        /// Visitor-side: consume a snapshot. Always allowed, even without ownership - this is what a
        /// joining client calls. Applies the sun rotation, light intensity, ambient, shadows and the
        /// visible disc, without touching any UI.
        ///
        /// Snap policy (documented): a correction smaller than <see cref="RemoteSnapThresholdDeg"/> is
        /// chased with a short slerp so packet jitter never shows as a stutter; anything larger SNAPS,
        /// because interpolating a big jump (join, resync, host fast-forward) would visibly fly the sun
        /// across the sky. Callers that want an instant jump can force it with <paramref name="snap"/>.
        /// </summary>
        /// <param name="state">Snapshot from <see cref="CaptureState"/>.</param>
        /// <param name="snap">Force an instant rotation instead of the short smoothing.</param>
        public void ApplyState(EnvironmentDayNightState state, bool snap = false)
        {
            if (state.Version <= 0) state.Version = STATE_VERSION;   // tolerate default-initialised payloads

            // Speed only ever comes from the owner snapshot; 0 means "frozen day/night".
            if (state.RotationSpeed >= 0f) RotationSpeed = state.RotationSpeed;

            if (state.CycleRunning)
            {
                // Local bootstrap: sun, disc, shadow config, RenderSettings.sun. Runs once.
                EnsureCycleEnvironment();

                // The owner owns the canonical time -> rotation mapping; adopt it so a visitor on a
                // different scene still derives the exact same sun orientation.
                if (state.BaseRotation != Quaternion.identity) _cycleBaseRotation = state.BaseRotation;

                IsEnabled = true;
                ApplyEnvironment(state.Time01, Time.deltaTime, snap);
            }
            else
            {
                IsEnabled = false;
                ResetToDefault();
            }

            // Re-baseline: whatever we just applied is what should already be stored.
            ResetSaveBaseline();
        }
        private bool CanWriteEnvironment()
        {
            if (SimulatesLocally) return true;

            if (!_ownerDeniedLogged)
            {
                _ownerDeniedLogged = true;
                Debug.LogWarning("[Meta_DayNightCycle] Ignored a write action: this peer is in " +
                                 "ApplyOnly mode. The environment owner drives it with CaptureState() " +
                                 "-> ApplyState().");
            }
            return false;
        }

        /// <summary>Day brightness for HUD readouts: 0 = night, 1 = full day.</summary>
        public float GetDayFactor()
        {
            if (Sun == null) return 0f;
            float elevation = Vector3.Dot(Sun.transform.forward, Vector3.up);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(DayElevationStart, DayElevationFull, elevation));
        }

        // =====================================================================
        // Cycle - ONE apply path, shared by the owner tick and by ApplyState
        // =====================================================================
        private void Update()
        {
            // Visitors (ApplyOnly) never simulate: their visuals come only from ApplyState.
            if (!SimulatesLocally || !IsEnabled) return;
            Tick(Time.deltaTime);
            AutosaveIfDue();
        }

        /// <summary>
        /// Owner tick: advances the clock and pushes the result through the same path a visitor uses.
        /// Public so a host can drive it deterministically (fixed timestep, rollback, tests).
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (Sun == null && !ResolveSunSilent()) return;

            // Absolute time is the source of truth; rotation is DERIVED, so it never drifts.
            _time01 = Mathf.Repeat(_time01 + (RotationSpeed / 360f) * deltaTime, 1f);
            ApplyEnvironment(_time01, deltaTime, snapRotation: true);
        }

        /// <summary>
        /// THE single write path for the environment: rotation -> light -> ambient -> disc.
        /// Both <see cref="Tick"/> (owner) and <see cref="ApplyState"/> (visitor) funnel through here,
        /// which is what guarantees "same state, same picture".
        /// </summary>
        private void ApplyEnvironment(float time01, float deltaTime, bool snapRotation)
        {
            _time01 = Mathf.Repeat(time01, 1f);
            if (Sun == null) return;

            // 1) Rotation. Owner snaps (exact, drift free); visitors chase small corrections smoothly.
            Quaternion target = _cycleBaseRotation * Quaternion.AngleAxis(_time01 * 360f, CycleAxis);
            if (snapRotation)
            {
                Sun.transform.rotation = target;
            }
            else
            {
                // Angle between orientations (works for any RotationAxis, not just yaw).
                float delta = Quaternion.Angle(Sun.transform.rotation, target);

                if (delta >= RemoteSnapThresholdDeg)
                {
                    Sun.transform.rotation = target;          // big jump (join / resync) -> snap
                }
                else
                {
                    float k = 1f - Mathf.Exp(-Mathf.Max(0.01f, RemoteSmoothingSharpness) * Mathf.Max(0f, deltaTime));
                    Sun.transform.rotation = Quaternion.Slerp(Sun.transform.rotation, target, k);
                }
            }

            // 2) Light + ambient, derived from the rotation that was actually applied.
            float dayFactor = GetDayFactor();
            Sun.intensity = Mathf.Lerp(MinIntensity, MaxIntensity, dayFactor);
            Sun.color = EvaluateSunColor(dayFactor);

            if (EnableShadows)
                Sun.shadowStrength = Mathf.Lerp(NightShadowStrength, DayShadowStrength, dayFactor);

            RenderSettings.ambientIntensity = Mathf.Lerp(AmbientNightIntensity, AmbientDayIntensity, dayFactor);
            if (TintAmbientColor)
                RenderSettings.ambientSkyColor = Color.Lerp(AmbientNightColor, AmbientDayColor, dayFactor);

            ApplyAtmosphere(dayFactor);

            // 3) Disc: throttled to ~12 Hz, but always immediate when it appears or disappears.
            bool wantVisible = Sun.transform.forward.y > -0.02f;
            bool due = Time.unscaledTime - _lastDiscUpdate >= DiscUpdateInterval;
            if (wantVisible != _discVisible || due)
            {
                _discVisible = wantVisible;
                _lastDiscUpdate = Time.unscaledTime;
                UpdateVisibleSun(dayFactor);
            }
        }

        /// <summary>Local one-time bootstrap: sun reference, disc, shadows, RenderSettings.sun.</summary>
        private bool EnsureCycleEnvironment()
        {
            if (_environmentReady && Sun != null) return true;   // snapshots must stay cheap

            ResolveSun();
            if (Sun == null)
            {
                if (!_ownerDeniedLogged)
                {
                    _ownerDeniedLogged = true;
                    Debug.LogWarning("[Meta_DayNightCycle] Cannot run the cycle - Sun is null.");
                }
                return false;
            }

            // The sun must be the scene's main directional light for the cycle to be visible
            RenderSettings.sun = Sun;

            // Re-anchor from the CURRENT rotation so starting never teleports the sun, whatever
            // _time01 the session is currently at.
            _cycleBaseRotation = Sun.transform.rotation * Quaternion.AngleAxis(-_time01 * 360f, CycleAxis);

            if (DayNightSkybox != null) RenderSettings.skybox = DayNightSkybox;

            // A Directional Light is invisible on its own - always give the player a real sun object.
            EnsureVisibleSun();

            ApplyShadowConfig();

            if (EnableLog)
                Debug.Log($"[Meta_DayNightCycle] Cycle active | mode={_authorityMode} | {RotationSpeed:F2} deg/s " +
                          $"(day = {360f / Mathf.Max(0.01f, RotationSpeed):F0} s) | shadows={Sun.shadows}");

            _environmentReady = true;
            return true;
        }

        /// <summary>ResolveSun without the warning spam, for the per-frame tick.</summary>
        private bool ResolveSunSilent()
        {
            if (Sun != null) return true;
            ResolveSun();
            if (Sun != null) EnsureCycleEnvironment();
            return Sun != null;
        }

        private void ApplyToggle()
        {
            if (IsEnabled) { if (EnsureCycleEnvironment()) Tick(0f); }
            else ResetToDefault();
        }

        // =====================================================================
        // Visible sun disc - a real object in the sky, driven by the same curve
        // =====================================================================
        private void EnsureVisibleSun()
        {
            if (VisibleSun == null)
            {
                var existing = FindAnyTransform(SUN_DISC_NAME);
                if (existing != null) VisibleSun = existing;
            }

            if (VisibleSun == null)
            {
                if (!CreateVisibleSunIfMissing) return;

                var disc = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                disc.name = SUN_DISC_NAME;
                SafeDestroy(disc.GetComponent<Collider>());   // pure visual, no physics
                _createdDisc = disc;
                VisibleSun = disc.transform;
                if (EnableLog)
                    Debug.Log($"[Meta_DayNightCycle] No visible sun assigned - created '{SUN_DISC_NAME}'.");
            }

            if (VisibleSun != null) ApplyDiscMaterial();
            EnsureGlow();
        }

        /// <summary>
        /// Premium disc: URP/Lit with HDR emission over a gold albedo, falling back to Unlit.
        /// A user's own material is never overwritten - only Unity's <c>Default-*</c> material is.
        /// </summary>
        private void ApplyDiscMaterial()
        {
            var rend = VisibleSun.GetComponent<Renderer>();
            if (rend == null) return;
            _discRenderer = rend;              // cached: no GetComponent per frame
            MakeSunFriendly(rend);

            var current = rend.sharedMaterial;
            bool userSupplied = current != null && !current.name.StartsWith("Default", System.StringComparison.OrdinalIgnoreCase);
            if (userSupplied) return;   // never overwrite the user's own material

            if (_discMaterial == null) _discMaterial = BuildDiscMaterial();
            if (_discMaterial == null) return;

            rend.sharedMaterial = _discMaterial;
        }

        private Material BuildDiscMaterial()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");

            if (lit == null && unlit == null)
            {
                if (!_warnedShader)
                {
                    _warnedShader = true;
                    Debug.LogWarning("[Meta_DayNightCycle] No URP Lit/Unlit shader found - " +
                                     "the sun disc keeps the default material.");
                }
                return null;
            }

            var mat = new Material(lit ?? unlit) { name = "Meta_Sun_Disc_Material" };

            SetMaterialColor(mat, DiscBaseColor);
            SetMaterialTexture(mat, BuildDiscTexture());

            if (lit != null)
            {
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            return mat;
        }

        /// <summary>Larger additive halo parented to the disc - the "bloom" without post-processing.</summary>
        private void EnsureGlow()
        {
            if (!DiscGlow || VisibleSun == null) return;

            if (_createdGlow == null)
            {
                var found = VisibleSun.Find(SUN_DISC_NAME + "_Glow");
                if (found != null) _createdGlow = found.gameObject;
            }

            if (_createdGlow == null)
            {
                var glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                glow.name = SUN_DISC_NAME + "_Glow";
                SafeDestroy(glow.GetComponent<Collider>());
                glow.transform.SetParent(VisibleSun, false);
                glow.transform.localPosition = Vector3.zero;
                glow.transform.localRotation = Quaternion.identity;
                _createdGlow = glow;
                _glowRenderer = null;          // re-resolve once on the next update
            }

            var glowRenderer = ResolveGlowRenderer();
            if (glowRenderer == null) return;
            MakeSunFriendly(glowRenderer);

            if (_glowMaterial == null) _glowMaterial = BuildGlowMaterial();
            if (_glowMaterial == null) return;

            glowRenderer.sharedMaterial = _glowMaterial;
            _createdGlow.transform.localScale = Vector3.one * DiscGlowScale;
        }

        private Material BuildGlowMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (shader == null) return null;

            var mat = new Material(shader) { name = "Meta_Sun_Glow_Material" };
            ConfigureAdditiveBlend(mat);
            SetMaterialColor(mat, Color.white);
            SetMaterialTexture(mat, BuildGlowTexture());
            return mat;
        }

        /// <summary>Additive + no depth write, so the halo never z-fights or hides behind the disc.</summary>
        private static void ConfigureAdditiveBlend(Material m)
        {
            if (m == null) return;
            m.SetFloat("_Surface", 1f);                                    // URP: transparent
            m.SetFloat("_Blend", 1f);                                      // URP: additive
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.One);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        /// <summary>A sun must never be culled, shadowed or lit by probes - that is what makes it look fake.</summary>
        private static void MakeSunFriendly(Renderer r)
        {
            if (r == null) return;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic = false;     // walls must never hide the sun
        }

        /// <summary>Hot white core fading to orange at the rim - baked as an opaque gradient.</summary>
        private Texture2D BuildDiscTexture()
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Meta_Sun_Disc_Gradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var px = new Color32[size * size];
            var core = new Color(1f, 0.99f, 0.92f);
            var rim = new Color(1f, 0.52f, 0.16f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));

                    Color c = Color.Lerp(core, rim, Mathf.SmoothStep(0f, 1f, d));
                    c *= Mathf.Lerp(1.15f, 0.62f, d);     // falloff towards the edge
                    px[y * size + x] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            _discTexture = tex;
            return tex;
        }

        /// <summary>Alpha falloff for the halo: opaque at the centre, fully transparent at the rim.</summary>
        private Texture2D BuildGlowTexture()
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Meta_Sun_Glow_Gradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));

                    Color c = new Color(1f, 0.86f, 0.62f);
                    c.a = Mathf.Pow(1f - d, 2.2f);          // soft, natural halo falloff
                    px[y * size + x] = c;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            _glowTexture = tex;
            return tex;
        }

        private static void SetMaterialColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private static void SetMaterialTexture(Material m, Texture t)
        {
            if (m == null || t == null) return;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
        }

        /// <summary>Parks the disc opposite the light direction, facing the focus point, tinted by the curve.</summary>
        private void UpdateVisibleSun(float dayFactor)
        {
            // Recover if the disc was deleted from the scene while the cycle was running.
            if (VisibleSun == null)
            {
                if (!CreateVisibleSunIfMissing) return;
                EnsureVisibleSun();
                if (VisibleSun == null) return;
            }

            var rend = _discRenderer != null ? _discRenderer : VisibleSun.GetComponent<Renderer>();
            if (rend == null) return;
            _discRenderer = rend;              // cached: no GetComponent per frame

            var glow = ResolveGlowRenderer();  // cached the same way

            float elevation = Sun != null ? Vector3.Dot(Sun.transform.forward, Vector3.up) : 1f;

            // Hide below the horizon unless the user wants a moon.
            float horizon = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.02f, elevation));
            if (!ShowDiscAtNight) horizon = 0f;

            if (horizon <= 0.001f)
            {
                rend.enabled = false;
                if (glow != null) glow.enabled = false;
                return;
            }
            rend.enabled = true;
            if (glow != null) glow.enabled = DiscGlow;

            // Distance must stay inside the camera far clip or the disc is clipped away.
            float dist = SunDiscDistance;
            var cam = _focusCamera;
            if (cam != null && dist > cam.farClipPlane * 0.6f)
            {
                dist = Mathf.Max(50f, cam.farClipPlane * 0.6f);
                if (!_warnedFarClip && EnableLog)
                {
                    _warnedFarClip = true;
                    Debug.LogWarning($"[Meta_DayNightCycle] SunDiscDistance {SunDiscDistance} exceeds the camera far plane " +
                                     $"({cam.farClipPlane:F0}) - clamped to {dist:F0}. Raise the camera far clip for a distant sky sun.");
                }
            }

            if (Sun != null)
            {
                Vector3 focus = GetSunFocusPosition();
                Vector3 pos = focus - Sun.transform.forward * dist;
                VisibleSun.position = pos;
                Vector3 toFocus = focus - pos;
                if (toFocus.sqrMagnitude > 0.0001f)
                    VisibleSun.rotation = Quaternion.LookRotation(toFocus.normalized, Vector3.up);
            }

            // Keep the apparent size constant even when the distance was clamped.
            float size = SunDiscScale * (dist / Mathf.Max(1f, SunDiscDistance));
            size *= Mathf.Lerp(0.75f, 1.1f, dayFactor);
            size *= Mathf.Lerp(0.55f, 1f, horizon);
            size *= 1f + DiscPulse * Mathf.Sin(Time.time * DiscPulseSpeed * Mathf.PI * 2f);

            Vector3 parentScale = VisibleSun.parent != null ? VisibleSun.parent.lossyScale : Vector3.one;
            float k = Mathf.Max(0.0001f, Mathf.Abs(parentScale.x));
            VisibleSun.localScale = Vector3.one * (size / k);

            if (_createdGlow != null)
                _createdGlow.transform.localScale = Vector3.one * (DiscGlowScale * Mathf.Lerp(0.8f, 1.05f, dayFactor));

            // Warm gold at noon, deep orange at the horizon, cool and dim once the sun is down.
            Color tint = Color.Lerp(SunDiscHorizonColor, SunDiscDayColor, dayFactor);
            if (!ShowDiscAtNight)
                tint = Color.Lerp(SunNightColor * 0.35f, tint, horizon);

            ApplyDiscEmission(tint, Mathf.Lerp(0.35f, 1f, horizon));
        }

        /// <summary>
        /// HDR emission, but never allowed to clip to pure white - the gold hue has to survive
        /// on pipelines without HDR / bloom, otherwise the sun turns into a white debug ball.
        /// </summary>
        private void ApplyDiscEmission(Color tint, float strength)
        {
            if (_discMaterial == null) return;

            Color e = tint * (DiscEmissionIntensity * Mathf.Max(0.05f, strength));
            float peak = Mathf.Max(e.r, Mathf.Max(e.g, e.b));
            if (peak > 1.35f) e *= 1.35f / peak;

            if (_discMaterial.HasProperty("_EmissionColor"))
            {
                _discMaterial.SetColor("_EmissionColor", e);
                SetMaterialColor(_discMaterial, DiscBaseColor * Mathf.Lerp(0.35f, 1f, strength));
            }
            else
            {
                SetMaterialColor(_discMaterial, e);   // unlit fallback: the colour IS the emission
            }

            if (_glowMaterial != null)
            {
                Color g = tint * Mathf.Lerp(0.25f, DiscGlowAlpha, strength);
                g.a = 1f;                              // alpha lives in the gradient texture
                SetMaterialColor(_glowMaterial, g);
            }
        }

        /// <summary>Cached glow renderer; re-resolved only when the halo object is (re)created.</summary>
        private Renderer ResolveGlowRenderer()
        {
            if (_glowRenderer != null) return _glowRenderer;
            if (_createdGlow == null) return null;
            _glowRenderer = _createdGlow.GetComponent<Renderer>();
            return _glowRenderer;
        }

        /// <summary>Where the disc orbits around: explicit point, else the main camera, else this transform.</summary>
        private Vector3 GetSunFocusPosition()
        {
            if (SunFocusPoint != null) return SunFocusPoint.position;

            // Cached: Camera.main is a Find, so it is only re-resolved when the camera is gone
            // or the cached one has been alive for a while (scene swaps, camera respawns).
            if (_focusCamera == null || Time.unscaledTime - _focusCameraCheckedAt > 5f)
            {
                _focusCamera = Camera.main;
                _focusCameraCheckedAt = Time.unscaledTime;
            }

            if (_focusCamera != null) return _focusCamera.transform.position;
            return transform.position;
        }

        private void HideVisibleSun()
        {
            var rend = _discRenderer;
            if (rend == null && VisibleSun != null) rend = VisibleSun.GetComponent<Renderer>();
            if (rend != null) rend.enabled = false;

            var glow = ResolveGlowRenderer();
            if (glow != null) glow.enabled = false;
        }

        private static Transform FindAnyTransform(string name)
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t;
            }
            return null;
        }

        private static void SafeDestroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        /// <summary>Removes ONLY the objects this module created. User assets are never touched.</summary>
        private void DestroyCreatedObjects()
        {
            var disc = _createdDisc;
            _createdDisc = null;
            if (disc != null)
            {
                SafeDestroy(disc);
                if (VisibleSun == disc.transform) VisibleSun = null;
            }

            var light = _createdSunLight;
            _createdSunLight = null;
            if (light != null)
            {
                SafeDestroy(light.gameObject);
                if (Sun == light) Sun = null;
            }

            if (_discMaterial != null) { SafeDestroy(_discMaterial); _discMaterial = null; }
            if (_glowMaterial != null) { SafeDestroy(_glowMaterial); _glowMaterial = null; }
            if (_discTexture != null) { SafeDestroy(_discTexture); _discTexture = null; }
            if (_glowTexture != null) { SafeDestroy(_glowTexture); _glowTexture = null; }

            _discRenderer = null;
            _glowRenderer = null;
            _focusCamera = null;
        }

        /// <summary>
        /// A Directional Light is invisible, so "the sun is in the sky" only works if the player
        /// faces it. This aims the sun in front of the current view so the disc is visible on the
        /// very first frame instead of after half a rotation.
        /// </summary>
        /// <returns>false when there is no camera or no sun to aim.</returns>
        public bool AimSunInFrontOfCamera(float elevation = 0.38f)
        {
            if (Sun == null) return false;

            var cam = Camera.main;
            if (cam == null) cam = Camera.current;
            if (cam == null) return false;

            Vector3 flat = cam.transform.forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.0001f) flat = Vector3.forward;
            flat.Normalize();

            // Light travels towards the camera => the disc lands in front of the viewer.
            Vector3 lightDir = new Vector3(-flat.x * 0.9f, -Mathf.Clamp(elevation, 0.1f, 0.9f), -flat.z * 0.9f).normalized;
            Sun.transform.rotation = Quaternion.LookRotation(lightDir);
            return true;
        }

        private void ApplyShadowConfig()
        {
            if (Sun == null || !EnableShadows || _shadowsApplied) return;

            Sun.shadows = ShadowType;
            Sun.shadowBias = ShadowBias;
            Sun.shadowNormalBias = ShadowNormalBias;
            Sun.shadowStrength = DayShadowStrength;
            _shadowsApplied = true;
        }

        /// <summary>Warm white at noon, orange at dawn/dusk, cool twilight below the horizon.</summary>
        private Color EvaluateSunColor(float dayFactor)
        {
            if (dayFactor <= 0.35f)
                return Color.Lerp(SunNightColor, SunDuskColor, Mathf.InverseLerp(0f, 0.35f, dayFactor));
            return Color.Lerp(SunDuskColor, SunDayColor, Mathf.InverseLerp(0.35f, 1f, dayFactor));
        }

        /// <summary>
        /// Optional extras. Both are off by default and both restore exactly on OFF, so a scene
        /// that already drives its own fog or skybox is never touched.
        /// </summary>
        private void ApplyAtmosphere(float dayFactor)
        {
            if (EnableFog)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogDensity = FogDensity;
                RenderSettings.fogColor = Color.Lerp(AmbientNightColor, AmbientDayColor, dayFactor);
            }

            if (!CreateGradientSky || DayNightSkybox != null) return;

            if (_createdSkyMaterial == null)
            {
                var skyShader = Shader.Find("Skybox/Procedural");
                if (skyShader == null)
                {
                    if (!_warnedShader)
                    {
                        _warnedShader = true;
                        Debug.LogWarning("[Meta_DayNightCycle] Skybox/Procedural not available " +
                                         "(possibly stripped from the build) - keeping the scene skybox.");
                    }
                    return;
                }

                _createdSkyMaterial = new Material(skyShader) { name = "Meta_Gradient_Sky" };
                RenderSettings.skybox = _createdSkyMaterial;
            }

            if (_createdSkyMaterial == null) return;

            Color horizon = Color.Lerp(new Color(0.98f, 0.52f, 0.28f), new Color(0.72f, 0.82f, 0.96f), dayFactor);
            Color ground = Color.Lerp(new Color(0.08f, 0.09f, 0.14f), new Color(0.32f, 0.30f, 0.28f), dayFactor);
            _createdSkyMaterial.SetColor("_SkyTint", horizon);
            _createdSkyMaterial.SetColor("_GroundColor", ground);
            if (_createdSkyMaterial.HasProperty("_AtmosphereThickness")) _createdSkyMaterial.SetFloat("_AtmosphereThickness", 0.75f);
            if (_createdSkyMaterial.HasProperty("_Exposure")) _createdSkyMaterial.SetFloat("_Exposure", 1.15f);
            if (_createdSkyMaterial.HasProperty("_SunDisk")) _createdSkyMaterial.SetFloat("_SunDisk", 0f);   // our disc draws the sun
        }

        // =====================================================================
        // Restore
        // =====================================================================
        private void ResetToDefault()
        {
            if (Sun != null)
            {
                Sun.transform.rotation = _defaultSunRotation;
                Sun.intensity = _defaultSunIntensity;   // ORIGINAL intensity, not MaxIntensity
                Sun.color = _defaultSunColor;
                Sun.shadows = _defaultShadows;
                Sun.shadowStrength = _defaultShadowStrength;
                Sun.shadowBias = _defaultShadowBias;
                Sun.shadowNormalBias = _defaultShadowNormalBias;
            }
            _shadowsApplied = false;
            _environmentReady = false;

            HideVisibleSun();   // the disc must never be left hanging over a restored night scene

            RenderSettings.sun = _defaultWasRenderSettingsSun ? _defaultRenderSettingsSun : null;
            RenderSettings.skybox = _defaultSkybox;
            RenderSettings.ambientIntensity = _defaultAmbientIntensity;
            RenderSettings.ambientSkyColor = _defaultAmbientSkyColor;

            RenderSettings.fog = _defaultFog;
            RenderSettings.fogColor = _defaultFogColor;
            RenderSettings.fogMode = _defaultFogMode;
            RenderSettings.fogDensity = _defaultFogDensity;

            if (_createdSkyMaterial != null)
            {
                SafeDestroy(_createdSkyMaterial);
                _createdSkyMaterial = null;
            }

            DynamicGI.UpdateEnvironment();

            if (EnableLog)
                Debug.Log("[Meta_DayNightCycle] Lighting restored to the cached initial state");
        }
    }
}