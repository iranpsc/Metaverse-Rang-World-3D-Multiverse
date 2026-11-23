using System.Collections.Generic;
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

        [Header("Skybox by phase")]
        [Tooltip("Swap RenderSettings.skybox by clock phase (Day / Dusk / Night). An empty slot falls " +
                 "back to the fixed DayNightSkybox above; everything empty keeps the scene's own sky.")]
        public bool ApplySkybox = true;
        [Tooltip("Sky for the DAY band: Time01 inside [DayStart01, DuskStart01). Assign e.g. Sky_Anime_01_Day_a.")]
        public Material SkyDay;
        [Tooltip("Optional sky for the DUSK band: [DuskStart01, NightStart01). Assign e.g. Sky_Anime_01_dusk_a.")]
        public Material SkyDusk;
        [Tooltip("Sky for everything else (NIGHT). Assign e.g. Sky_Anime_01_Night_a / _c.")]
        public Material SkyNight;
        [Tooltip("Clock phase where the DAY sky starts. 0 = midnight, 0.5 = noon, 1 = next midnight.")]
        [Range(0f, 1f)] public float DayStart01 = 0.25f;
        [Tooltip("Clock phase where the DUSK sky starts. Must be >= DayStart01 (clamped at runtime).")]
        [Range(0f, 1f)] public float DuskStart01 = 0.55f;
        [Tooltip("Clock phase where the NIGHT sky starts. Must be >= DuskStart01 (clamped at runtime).")]
        [Range(0f, 1f)] public float NightStart01 = 0.70f;
        [Tooltip("Cross-fade the sky across each phase boundary instead of a hard swap: the assigned " +
                 "sky sinks to black exposure as a boundary nears and the next sky rises after it. " +
                 "Stateless - a pure function of Time01, so network time jumps land inside the window. " +
                 "Needs materials with an _Exposure property (Skybox/Cubemap has one); otherwise the " +
                 "previous hard-swap behavior is kept.")]
        public bool SmoothSkyTransitions = true;
        [Tooltip("Seconds of visual crossfade around every phase boundary: half before, half after. " +
                 "Internally converted to clock units by the current day length (360 / RotationSpeed), " +
                 "so changing day speed keeps the recorded seconds honest.")]
        [Range(0f, 30f)] public float SkyBlendSeconds = 15f;
        [Tooltip("Minimum fraction of the target exposure the sky keeps while blending. " +
                 "0.2-0.35 keeps every phase readable (dusk glow / night stars never go pure black); " +
                 "approaching 0 recovers the old full dip-to-black, 1 disables the brightness dip.")]
        [Range(0.05f, 1f)] public float SkyBlendFloor = 0.3f;

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
        private bool _persistSpeedWarned;

        /// <summary>
        /// Watchable band used when a speed arrives via <see cref="ApplyState"/> (persisted store
        /// load OR owner snapshot). Speeds INSIDE the band are applied; speeds outside it are
        /// IGNORED and the component keeps its scene/prefab value - no clamp-as-cure, so a stale
        /// 200 never turns into a locked-in 12. Values set directly on the component at runtime
        /// (Inspector / button drags) are never restricted by this band.
        /// </summary>
        public const float MinRestoredSpeed = 0.05f;
        /// <summary>
        /// Band ceiling: 6 deg/s = a 60 s day. The old demo values 12 and 200 deg/s are rejected
        /// as scene-pacing hijacks; the authored lobby value is 2 deg/s (180 s day).
        /// </summary>
        public const float MaxRestoredSpeed = 6f;

        // ---- Optional atmosphere cache ----
        private Color _defaultSunColor;
        private bool _defaultFog;
        private Color _defaultFogColor;
        private FogMode _defaultFogMode;
        private float _defaultFogDensity;
        private Material _createdSkyMaterial;

        // ---- Skybox by phase (change-only assignment; user materials are never destroyed) ----
        private Material _appliedSkyMaterial;

        // Smooth phase blends: RUNTIME COPIES carry the _Exposure tweening, so the user's shared
        // assets are mutated by nobody. Created lazily per shared material, destroyed on every
        // reset path - never recreated per frame, never leaked.
        private readonly Dictionary<Material, Material> _skyInstances = new Dictionary<Material, Material>();
        private readonly Dictionary<Material, float> _skyExposureTargets = new Dictionary<Material, float>();

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
        private bool _warnedHttpStoreWithoutBaseUrl;

        private Quaternion _cycleBaseRotation;   // sun orientation at _time01 == 0
        private bool _ownerDeniedLogged;
        private float _lastDiscUpdate = -999f;
        private bool _discVisible;
        private bool _environmentReady;
        private bool _sunReplacedLogged;

        // ---- Persistence ----
        private static IEnvironmentDayNightStore _defaultStore;
        private IEnvironmentDayNightStore _store;              // injected or the shared default
        private float _lastAutosaveAt = -999f;
        private bool _loadedFromStore;
        private int _lastSavedMinute = -1;
        private bool _lastSavedRunning;
        private float _lastSavedSpeed = -1f;
        private int _localActionVersion;                        // guards async restore vs. user ON/OFF
        private int _lastLocalWriteVersion = -1;                // _localActionVersion at the last owner PUT for this env
        private string _lastLocalWriteEnv = string.Empty;       // env of that last owner PUT
        private string _httpRestoreInFlightEnv = string.Empty;  // single-flight coalesce for async GETs
        private string _httpRestoreDropLoggedEnv = string.Empty;// one drop log per (env, version)
        private int _httpRestoreDropLoggedVersion = -1;
        private bool _hasPutThisSession;                        // any successful owner PUT this session

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

                if (_useHttpStore)
                {
                    if (!string.IsNullOrEmpty(_httpBaseUrl))
                    {
                        if (_httpStore == null) _httpStore = new EnvironmentDayNightHttpStore();
                        if (_httpStore.BaseUrl != _httpBaseUrl) _httpStore.BaseUrl = _httpBaseUrl;
                        return _httpStore;
                    }

                    WarnHttpStoreWithoutBaseUrlOnce();
                }

                if (_defaultStore == null) _defaultStore = new EnvironmentDayNightPlayerPrefsStore();
                return _store = _defaultStore;
            }
            set => _store = value;
        }

        /// <summary>One clear line when HTTP persistence is requested without a BaseUrl.</summary>
        private void WarnHttpStoreWithoutBaseUrlOnce()
        {
            if (_warnedHttpStoreWithoutBaseUrl) return;
            _warnedHttpStoreWithoutBaseUrl = true;
            Debug.LogWarning("[DayNight] Use Http Store is ON but Http Base Url is empty. " +
                             "Falling back to PlayerPrefs. Example Base Url: http://127.0.0.1:8788 " +
                             "(contract: {base}/environments/{environmentId}/daynight.json).");
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

        /// <summary>
        /// True when a light can actually drive the visible cycle: a directional light that is
        /// enabled and lives in the active hierarchy. An assigned but disabled / non-directional
        /// light (the lobby's RadarBoostLight is one) is NOT usable.
        /// </summary>
        public static bool IsUsableSun(Light light)
        {
            return light != null
                && light.type == LightType.Directional
                && light.enabled
                && light.gameObject.activeInHierarchy;
        }

        private static bool IsRadarBoost(Light light)
        {
            return light != null &&
                   light.name.IndexOf("RadarBoost", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Best enabled directional light in the scene. Prefers RenderSettings.sun, then a light
        /// named like a real sun, and never picks the minimap-only RadarBoostLight.
        /// </summary>
        private static Light FindBestDirectionalSun()
        {
            Light best = null;
            int bestScore = int.MinValue;

            foreach (var light in FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!IsUsableSun(light)) continue;

                int score = 0;
                if (light == RenderSettings.sun) score += 100;
                if (light.name.IndexOf("Directional", System.StringComparison.OrdinalIgnoreCase) >= 0) score += 10;
                if (light.name.IndexOf("Sun", System.StringComparison.OrdinalIgnoreCase) >= 0) score += 5;
                if (IsRadarBoost(light)) score -= 100;
                if (light.shadows != LightShadows.None) score += 2;
                if (light.intensity > 0f) score += 1;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = light;
                }
            }
            return best;
        }

        private void ResolveSun()
        {
            // Keep an intentionally assigned, usable sun (unless it is the minimap boost light).
            if (IsUsableSun(Sun) && !IsRadarBoost(Sun)) return;

            Light assigned = Sun;
            Light best = FindBestDirectionalSun();

            if (best != null)
            {
                if (assigned != null && assigned != best && !_sunReplacedLogged)
                {
                    _sunReplacedLogged = true;
                    Debug.LogWarning($"[Meta_DayNightCycle] Assigned Sun '{assigned.name}' is not a usable " +
                                     $"directional light; using the scene's main light '{best.name}' instead.");
                }
                Sun = best;
                return;
            }

            // No enabled directional light exists. Keep a directional assignment so a system that
            // enables it later can still drive the cycle; never keep a spot/point light as the sun.
            if (Sun != null && Sun.type == LightType.Directional)
            {
                if (EnableLog)
                    Debug.LogWarning($"[Meta_DayNightCycle] Sun '{Sun.name}' is disabled; the cycle " +
                                     "cannot render until it is enabled.");
                return;
            }

            Sun = null;

            if (CreateSunIfMissing)
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
            // Visitors never simulate: their visuals come only from ApplyState().
            if (SimulatesLocally && IsEnabled) ApplyToggle();
        }

        private void OnDisable()
        {
            if (ShouldPersistOnExit()) SaveEnvironmentState(); // the place remembers its time of day
            if (IsEnabled) ResetToDefault();
        }

        private void OnDestroy()
        {
            if (ShouldPersistOnExit()) SaveEnvironmentState();
            if (IsEnabled) ResetToDefault();
            DestroyCreatedObjects();
            DestroyPhaseSkyRuntime();   // belt & braces: ResetToDefault above already cleared them when set
        }

        /// <summary>
        /// True when an exit-save should actually PUT. Skips a redundant re-PUT of state that is
        /// already identical to the last successful PUT, so an exit never clobbers a newer local
        /// write with a stale snapshot restored earlier in the session. Always PUTs at least once
        /// per owner session so a freshly restored place is persisted.
        /// </summary>
        private bool ShouldPersistOnExit()
        {
            if (!PersistenceEnabled || !SimulatesLocally) return false;
            if (!_hasPutThisSession) return true;   // persist at least once this session
            int minute = Mathf.RoundToInt(_time01 * 1440f);
            return _lastSavedMinute != minute
                   || _lastSavedRunning != IsEnabled
                   || !Mathf.Approximately(_lastSavedSpeed, RotationSpeed);
        }

        // =====================================================================
        // Public API
        // =====================================================================
        public void ToggleCycle(bool enable)
        {
            if (!CanWriteEnvironment()) return;

            // Any explicit user choice wins over an HTTP restore that is still in flight.
            _localActionVersion++;

            if (enable)
            {
                // Fail closed: never report ON when there is no usable sun/environment to drive.
                if (!EnsureCycleEnvironment())
                {
                    IsEnabled = false;
                    ApplyToggle();                  // restores the cached lighting state
                    return;
                }

                IsEnabled = true;
                ApplyToggle();
                SaveIfMeaningfullyChanged();       // running state changed -> persist now
                return;
            }

            IsEnabled = false;
            ApplyToggle();
            SaveIfMeaningfullyChanged();
        }

        public void ForceResetToDefault()
        {
            if (!CanWriteEnvironment()) return;
            _localActionVersion++;
            ResetToDefault();
            IsEnabled = false;
            SaveIfMeaningfullyChanged();
        }

        public void RecacheInitialValues()
        {
            if (!CanWriteEnvironment()) return;
            CacheInitialValues();
            if (EnableLog) Debug.Log("[Meta_DayNightCycle] Initial lighting values re-cached");
        }

        /// <summary>
        /// ONE phrase for every DayNight status label (ControlPanel card, legacy SceneTool card), derived
        /// from exactly the fields the simulation uses (SimulatesLocally + IsEnabled), so the label can
        /// never contradict the moving numbers: while values keep changing because a running cycle -
        /// local OR remotely owned - drives them, this reports ON*, never a bare OFF.
        /// </summary>
        public string DescribeCycleState()
        {
            if (IsEnabled) return SimulatesLocally ? "ON" : "ON (synced from place owner)";
            return SimulatesLocally ? "OFF (manual time)" : "OFF (waiting for place owner)";
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

            string environmentId = _environmentId;
            // Single-flight coalesce: never have two async GETs for the same environment in flight.
            if (!string.IsNullOrEmpty(_httpRestoreInFlightEnv) && _httpRestoreInFlightEnv == environmentId)
                return true;
            _httpRestoreInFlightEnv = environmentId;

            _loadedFromStore = true;              // one request per environment id
            string describe = store.Describe;
            int actionAtRequest = _localActionVersion;
            asyncStore.TryLoadAsync(environmentId, (loaded, ok) =>
            {
                _httpRestoreInFlightEnv = string.Empty;   // this flight is complete
                if (!ok || _environmentId != environmentId) return;   // place changed / miss / server down

                // A user pressed ON/OFF/Reset while the GET was in flight: that explicit choice
                // must win over a stale snapshot, otherwise the label and the simulation disagree.
                if (_localActionVersion != actionAtRequest) return;

                // Never let a stale HTTP restore overwrite a local owner write unless a newer
                // local action has occurred since. Once an owner has PUT state for this env, a
                // restore at the same (or older) action version would clobber the fresh write.
                if (_lastLocalWriteEnv == environmentId
                    && _lastLocalWriteVersion >= 0
                    && _localActionVersion <= _lastLocalWriteVersion)
                {
                    // Log the drop once per (env, version) so the bootstrap loop doesn't spam.
                    if (_httpRestoreDropLoggedEnv != environmentId
                        || _httpRestoreDropLoggedVersion != _localActionVersion)
                    {
                        _httpRestoreDropLoggedEnv = environmentId;
                        _httpRestoreDropLoggedVersion = _localActionVersion;
                        if (EnableLog)
                            Debug.Log($"[Meta_DayNightCycle] HTTP restore dropped for '{environmentId}' - " +
                                      $"local action version {_localActionVersion} is not newer than " +
                                      $"last write {_lastLocalWriteVersion}; keeping current state.");
                    }
                    return;
                }

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

            // Remember what we just PUT so (a) a stale HTTP restore cannot be mistaken for newer
            // state, and (b) the exit-save can skip a redundant re-PUT of identical state.
            _lastLocalWriteVersion = _localActionVersion;
            _lastLocalWriteEnv = _environmentId;
            _lastSavedMinute = Mathf.RoundToInt(_time01 * 1440f);
            _lastSavedRunning = IsEnabled;
            _lastSavedSpeed = RotationSpeed;
            _hasPutThisSession = true;
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

            // Speed policy: a snapshot may only CARRY the owner's seated pacing - it must never
            // overwrite the scene design. A persisted/demo speed outside the watchable band
            // (e.g. a stale 200 deg/s = a ~1.8 s "day") is IGNORED: the component keeps its
            // scene/prefab value (~2 deg/s). Nothing is clamped into a fake 12; the next autosave
            // (or any ON/OFF toggle) writes the design speed back into the store, so the store
            // self-heals to the authored value. Direct runtime setters stay unclamped by design.
            if (state.RotationSpeed >= 0f)
            {
                if (state.RotationSpeed >= MinRestoredSpeed && state.RotationSpeed <= MaxRestoredSpeed)
                {
                    RotationSpeed = state.RotationSpeed;
                }
                else if (!_persistSpeedWarned)
                {
                    _persistSpeedWarned = true;
                    Debug.LogWarning($"[Meta_DayNightCycle] Ignored unsafe persisted rotation speed " +
                                     $"{state.RotationSpeed:F2} deg/s (watchable band {MinRestoredSpeed:F2}..{MaxRestoredSpeed:F2}) " +
                                     $"- keeping the scene-authored value {RotationSpeed:F2}. " +
                                     "Next autosave writes the design speed back into the store; " +
                                     "Tools/Meta/DayNight/Clear Stored Day-Night clears stale entries.");
                }
            }

            // Time01 and the cycle base are DATA even while the cycle is OFF: restore them so the
            // next ON resumes at the saved spot instead of jumping back to the scene's serialized 0.
            // (The lighting itself is still restored to the cached scene look in the OFF branch.)
            _time01 = Mathf.Repeat(state.Time01, 1f);
            if (state.BaseRotation != Quaternion.identity) _cycleBaseRotation = state.BaseRotation;

            if (state.CycleRunning)
            {
                // Local bootstrap: sun, disc, shadow config, RenderSettings.sun. Runs once.
                if (!EnsureCycleEnvironment())
                {
                    IsEnabled = false;
                    ResetToDefault();
                    ResetSaveBaseline();
                    return;
                }

                // The owner owns the canonical time -> rotation mapping; adopt it so a visitor on a
                // different scene still derives the exact same sun orientation.
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
            if (!ResolveSunSilent())
            {
                // The cycle cannot render without a sun. Report OFF instead of leaving a lying
                // "ON" label next to frozen lighting.
                IsEnabled = false;
                ResetToDefault();
                return;
            }

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

            // Sky phase FIRST, before the Sun guard: the sky matches the clock even where the
            // directional light is missing (visitors on half-rigged copies, a deleted sun...).
            UpdateSkyPhase(_time01);

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
            if (_environmentReady && IsUsableSun(Sun)) return true;   // snapshots must stay cheap

            ResolveSun();
            if (!IsUsableSun(Sun))
            {
                if (!_ownerDeniedLogged)
                {
                    _ownerDeniedLogged = true;
                    Debug.LogWarning("[Meta_DayNightCycle] Cannot run the cycle - no enabled directional Sun.");
                }
                return false;
            }

            // The sun must be the scene's main directional light for the cycle to be visible
            RenderSettings.sun = Sun;

            // Re-anchor from the CURRENT rotation so starting never teleports the sun, whatever
            // _time01 the session is currently at.
            _cycleBaseRotation = Sun.transform.rotation * Quaternion.AngleAxis(-_time01 * 360f, CycleAxis);

            // Phase sky owns the skybox when configured (the legacy fixed DayNightSkybox becomes
            // the fallback for empty phase slots); with everything empty the scene sky is kept.
            UpdateSkyPhase(_time01);

            // A Directional Light is invisible on its own - always give the player a real sun object.
            EnsureVisibleSun();

            ApplyShadowConfig();

            if (EnableLog)
                Debug.Log($"[Meta_DayNightCycle] Cycle active | mode={_authorityMode} | {RotationSpeed:F2} deg/s " +
                          $"(day = {360f / Mathf.Max(0.01f, RotationSpeed):F0} s) | shadows={Sun.shadows}");

            _environmentReady = true;
            return true;
        }

        // =====================================================================
        // Skybox by clock phase - Day / Dusk / Night material swap
        // =====================================================================

        /// <summary>True when the phase sky (not the fixed one) owns RenderSettings.skybox.</summary>
        public bool HasPhaseSky =>
            ApplySkybox && (SkyDay != null || SkyDusk != null || SkyNight != null);

        /// <summary>
        /// Material for the clock phase at <paramref name="time01"/> (0 = midnight, 0.5 = noon):
        /// DAY = [DayStart01, DuskStart01), DUSK = [DuskStart01, NightStart01), NIGHT = the rest.
        /// Thresholds are clamped in order, so unordered Inspector values still give clean bands.
        /// An empty slot falls back to the fixed <see cref="DayNightSkybox"/>; both empty return
        /// null and the caller keeps the currently assigned sky (the sky is never unset mid-run).
        /// </summary>
        public Material ResolvePhaseSky(float time01)
        {
            if (!ApplySkybox) return null;

            float dayStart = Mathf.Clamp01(DayStart01);
            float duskStart = Mathf.Max(dayStart, Mathf.Clamp01(DuskStart01));
            float nightStart = Mathf.Max(duskStart, Mathf.Clamp01(NightStart01));

            time01 = Mathf.Repeat(time01, 1f);
            Material phase;
            if (time01 >= dayStart && time01 < duskStart) phase = SkyDay;
            else if (time01 >= duskStart && time01 < nightStart) phase = SkyDusk;
            else phase = SkyNight;

            return phase != null ? phase : DayNightSkybox;
        }

        /// <summary>
        /// Applies the clock's phase sky to RenderSettings.skybox through pre-faded RUNTIME copies:
        /// the assigned material changes only when the phase band changes, and its _Exposure sinks
        /// to black approaching a real boundary and rises again after it - a STATELESS pure
        /// function of time01, so owner ticks and visitor network jumps all land inside the
        /// window without accumulated blend state, and shared user assets are never mutated.
        /// Called from <see cref="ApplyEnvironment"/>, which funnels BOTH the owner Tick and
        /// visitor ApplyState snapshots through it, so every peer swaps at the same clock phase.
        /// Materials are never destroyed here - only the reference is swapped.
        /// </summary>
        private void UpdateSkyPhase(float time01)
        {
            // Phase system disabled: keep ONLY the legacy fixed-sky behavior (hard assign, no blend).
            if (!ApplySkybox)
            {
                if (DayNightSkybox != null && DayNightSkybox != _appliedSkyMaterial)
                {
                    _appliedSkyMaterial = DayNightSkybox;
                    RenderSettings.skybox = DayNightSkybox;
                }
                return;
            }

            time01 = Mathf.Repeat(time01, 1f);
            Material shared = ResolvePhaseSky(time01);
            if (shared == null) return;   // no slot for this phase and no fixed sky - keep previous

            Material next = EnsureSkyRuntimeInstance(shared);
            if (next == null) return;

            if (next != _appliedSkyMaterial)
            {
                _appliedSkyMaterial = next;
                RenderSettings.skybox = next;
                if (EnableLog)
                    Debug.Log($"[Meta_DayNightCycle] Sky phase -> '{shared.name}' (Time01={time01:F3})");
            }

            // Blend curve: full target at the window edge, easing down to SkyBlendFloor × target at
            // the boundary - the swap lands on the softened low point instead of a black frame, so
            // the transition reads as a real sunset fade-in rather than a cut-to-black trick.
            ApplySkyBlendExposure(next, shared, NearestPhaseBlendWeight(time01, EffectiveBlendHalf01()));
        }

        private const string SkyExposureProperty = "_Exposure";   // Skybox/Cubemap and friends

        /// <summary>
        /// Blend window half-width in clock units: half of <see cref="SkyBlendSeconds"/>, converted
        /// by the current day length (360 deg / RotationSpeed) so recorded seconds stay honest
        /// while day speed changes live.
        /// </summary>
        private float EffectiveBlendHalf01()
        {
            float daySeconds = 360f / Mathf.Max(0.01f, RotationSpeed);
            return Mathf.Clamp01(SkyBlendSeconds * 0.5f / Mathf.Max(1f, daySeconds));
        }

        /// <summary>
        /// 0..1 blend weight of the NEAREST phase boundary whose two sides resolve to DIFFERENT
        /// materials. Invisible boundaries (same cubemap on both sides - e.g. a night band that
        /// wraps past midnight) are skipped, so the sky is never darkened for nothing. A pure
        /// function of time01: two boundaries close together simply resolve to the nearer one,
        /// which keeps the dip continuous and prevents overlapping double-darkening.
        /// </summary>
        private float NearestPhaseBlendWeight(float time01, float blendHalf01)
        {
            if (!SmoothSkyTransitions || blendHalf01 <= 0.00001f) return 0f;

            float bestW = 0f;
            ConsiderPhaseEdge(time01, blendHalf01, Mathf.Clamp01(DayStart01), ref bestW);
            ConsiderPhaseEdge(time01, blendHalf01,
                Mathf.Max(Mathf.Clamp01(DayStart01), Mathf.Clamp01(DuskStart01)), ref bestW);
            ConsiderPhaseEdge(time01, blendHalf01,
                Mathf.Max(Mathf.Max(Mathf.Clamp01(DayStart01), Mathf.Clamp01(DuskStart01)), Mathf.Clamp01(NightStart01)), ref bestW);
            return bestW;
        }

        /// <summary>Feed one boundary into the nearest-edge max. Skipped when both sides pick the
        /// same resolved material (no visible change there).</summary>
        private void ConsiderPhaseEdge(float time01, float blendHalf01, float edge, ref float bestW)
        {
            const float eps = 0.0005f;
            Material left = ResolvePhaseSky(Mathf.Repeat(edge - eps, 1f));
            Material right = ResolvePhaseSky(Mathf.Repeat(edge + eps, 1f));
            if (left == null || right == null || left == right) return;

            float dist = Mathf.Abs(Mathf.Repeat(time01 - edge + 0.5f, 1f) - 0.5f);
            if (dist < blendHalf01)
            {
                float w = 1f - dist / blendHalf01;
                if (w > bestW) bestW = w;
            }
        }

        /// <summary>Runtime copy of a shared sky material - the ONLY thing _Exposure is tweened on.
        /// Created once per shared material and reused; the user's asset is never mutated.</summary>
        private Material EnsureSkyRuntimeInstance(Material shared)
        {
            if (shared == null) return null;
            if (_skyInstances.TryGetValue(shared, out Material cached) && cached != null) return cached;

            var copy = new Material(shared) { name = $"{shared.name} (Meta blend)" };
            _skyInstances[shared] = copy;
            _skyExposureTargets[shared] = copy.HasProperty(SkyExposureProperty)
                ? copy.GetFloat(SkyExposureProperty)
                : 1f;
            if (EnableLog)
                Debug.Log($"[Meta_DayNightCycle] Sky blend copy created for '{shared.name}' (target exposure {_skyExposureTargets[shared]:F2})");
            return copy;
        }

        /// <summary>Drives the copy's _Exposure along the blend curve: full target at the window
        /// edge, easing down to <see cref="SkyBlendFloor"/> × target at the boundary - never to
        /// zero, so dusk stays warm and the night cubemap (stars) stays readable. Shaders without
        /// _Exposure silently keep the hard swap.</summary>
        private void ApplySkyBlendExposure(Material instance, Material shared, float blendWeight)
        {
            if (instance == null || shared == null) return;
            if (!instance.HasProperty(SkyExposureProperty)) return;

            if (!_skyExposureTargets.TryGetValue(shared, out float target))
                _skyExposureTargets[shared] = target = instance.GetFloat(SkyExposureProperty);

            float value = target * Mathf.Lerp(1f, Mathf.Clamp01(SkyBlendFloor), Mathf.SmoothStep(0f, 1f, blendWeight));
            if (Mathf.Abs(instance.GetFloat(SkyExposureProperty) - value) > 0.002f)
                instance.SetFloat(SkyExposureProperty, value);
        }

        /// <summary>Drops every runtime blend copy - called from every ResetToDefault path, so a
        /// stopped cycle can never leave instanced materials behind (no leaks across long runs).</summary>
        private void DestroyPhaseSkyRuntime()
        {
            foreach (var pair in _skyInstances)
            {
                if (pair.Value != null) SafeDestroy(pair.Value);
            }
            _skyInstances.Clear();
            _skyExposureTargets.Clear();
            _appliedSkyMaterial = null;
        }

        /// <summary>Short status phrase for cards: Day / Dusk / Night, plus the blend percentage
        /// while inside a transition window (e.g. "dusk (blending 42%)").</summary>
        public string DescribeSkyPhase(float time01)
        {
            if (!ApplySkybox) return "slots off";
            if (!HasPhaseSky) return "slots empty";

            time01 = Mathf.Repeat(time01, 1f);
            float dayStart = Mathf.Clamp01(DayStart01);
            float duskStart = Mathf.Max(dayStart, Mathf.Clamp01(DuskStart01));
            float nightStart = Mathf.Max(duskStart, Mathf.Clamp01(NightStart01));

            string band = time01 >= dayStart && time01 < duskStart ? "day"
                        : time01 >= duskStart && time01 < nightStart ? "dusk"
                        : "night";

            float weight = NearestPhaseBlendWeight(time01, EffectiveBlendHalf01());
            return weight > 0.02f
                ? $"{band} (blending {Mathf.RoundToInt(weight * 100)}%)"
                : band;
        }

        /// <summary>ResolveSun without the warning spam, for the per-frame tick.</summary>
        private bool ResolveSunSilent()
        {
            if (IsUsableSun(Sun)) return true;
            ResolveSun();
            if (IsUsableSun(Sun)) EnsureCycleEnvironment();
            return IsUsableSun(Sun);
        }

        private void ApplyToggle()
        {
            if (!IsEnabled)
            {
                ResetToDefault();
                return;
            }

            if (!EnsureCycleEnvironment())
            {
                IsEnabled = false;      // fail closed: status must never say ON with frozen lighting
                ResetToDefault();
                return;
            }

            Tick(0f);
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

            // Hide below the horizon unless the user wants a moon. The old code zeroed the
            // horizon unconditionally, which made the visible sun disc permanently hidden in
            // every scene that kept ShowDiscAtNight off (the default, including the Lobby).
            float horizon = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.02f, elevation));
            if (ShowDiscAtNight)
                horizon = Mathf.Max(horizon, 0.25f);   // moon-like disc continues below the horizon
            else if (elevation <= 0f)
                horizon = 0f;                          // hide below the horizon

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

            if (!CreateGradientSky || HasPhaseSky || DayNightSkybox != null) return;

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
            DestroyPhaseSkyRuntime();   // blend copies die AFTER the original sky is back - no dangling refs
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