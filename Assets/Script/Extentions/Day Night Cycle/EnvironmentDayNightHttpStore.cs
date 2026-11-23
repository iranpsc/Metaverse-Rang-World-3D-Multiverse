using System;
using UnityEngine;
using UnityEngine.Networking;

namespace Meta
{
    /// <summary>
    /// HTTP store for the employer model (Node.js + JSON files). Plugs the environment day/night
    /// state into the module-bundled service at
    /// <c>Assets/Script/Extentions/Day Night Cycle/Server</c>; it is disabled until a base URL is
    /// configured, so nothing changes for the project until then.
    ///
    /// Contract (see <see cref="EnvironmentDayNightPath"/>):
    ///   GET {baseUrl}/environments/{environmentId}/daynight.json
    ///   PUT {baseUrl}/environments/{environmentId}/daynight.json   body = daynight schema
    /// Sent on every PUT: <c>X-Is-Environment-Owner: true</c> - only the owner ever calls
    /// <c>SaveEnvironmentState()</c>, and the header keeps the server's strict mode
    /// (<c>REQUIRE_OWNER=true</c>) working with no client change.
    ///
    /// JSON CASE: the named service contract is camelCase (<c>time01</c>, <c>cycleRunning</c>...)
    /// while <see cref="EnvironmentDayNightState"/> uses PascalCase fields (JsonUtility is
    /// case-sensitive), so this store translates both directions with a small wire struct.
    ///
    /// Why the load is asynchronous: <see cref="IEnvironmentDayNightStore"/> is synchronous because
    /// it must stay usable from Awake / button clicks. Blocking the render thread on an HTTP round
    /// trip would freeze the game, so <see cref="TryLoad"/> reports "unavailable" and
    /// <see cref="TryLoadAsync"/> is the real, callback based read.
    ///
    /// WRITE is fire-and-forget: a PUT does not need an answer; the place's state is monotonic.
    /// </summary>
    public class EnvironmentDayNightHttpStore : IEnvironmentDayNightStore, IEnvironmentDayNightStoreAsync
    {
        [Tooltip("Base URL of the environment service, e.g. http://127.0.0.1:8788. Empty = disabled.")]
        public string BaseUrl = "";

        [Tooltip("Seconds before a request is abandoned. 0 = default (10 s).")]
        public int TimeoutSeconds = 10;

        [Tooltip("Sent as the 'Content-Type' header on PUT.")]
        public string ContentType = "application/json";

        [Tooltip("Owner role header for PUT. The server rejects day/night writes without it " +
                 "when REQUIRE_OWNER is enabled.")]
        public const string OwnerHeader = "X-Is-Environment-Owner";

        private bool _warnedDisabled;
        private bool _warnedSyncStub;
        private bool _warnedVisitorSave;

        public string Describe => "Http";

        public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);

        // ------------------------------------------------------------------ read
        public bool TryLoad(string environmentId, out EnvironmentDayNightState state)
        {
            state = EnvironmentDayNightState.Default;

            if (!Enabled)
            {
                WarnDisabledOnce();
                return false;
            }

            // Synchronous read would stall the render thread; the caller must use TryLoadAsync.
            if (!_warnedSyncStub)
            {
                _warnedSyncStub = true;
                Debug.Log("[DayNight] HttpEnvironmentDayNightStore.TryLoad is a stub and always reports " +
                          "'unavailable'. Use TryLoadAsync(callback) so the read happens off the critical path.");
            }
            return false;
        }

        /// <summary>Non-blocking read. Callback: (state, ok). Safe when disabled (ok = false).</summary>
        public void TryLoadAsync(string environmentId, Action<EnvironmentDayNightState, bool> onLoaded)
        {
            if (onLoaded == null) return;

            if (!Enabled)
            {
                WarnDisabledOnce();
                onLoaded(EnvironmentDayNightState.Default, false);
                return;
            }
            if (string.IsNullOrEmpty(environmentId))
            {
                onLoaded(EnvironmentDayNightState.Default, false);
                return;
            }

            string url = BuildUrl(environmentId);
            HttpRunner.Start(url, null, null, ContentType, false, TimeoutSeconds,
                (payload, respOk) =>
                {
                    EnvironmentDayNightState parsed = EnvironmentDayNightState.Default;
                    bool loadOk = respOk && !string.IsNullOrEmpty(payload) &&
                                  TryParseWire(payload, environmentId, out parsed, respOk);
                    onLoaded(loadOk ? parsed : EnvironmentDayNightState.Default, loadOk);
                });
        }

        // ----------------------------------------------------------------- write
        public void Save(string environmentId, EnvironmentDayNightState state)
        {
            if (!Enabled)
            {
                WarnDisabledOnce();
                return;
            }
            if (string.IsNullOrEmpty(environmentId)) return;

            // The owner gate is enforced in Meta_DayNightCycle.SaveEnvironmentState; this second
            // check keeps direct/legacy callers from PUTting a visitor snapshot into the place.
            if (!state.IsOwner)
            {
                if (!_warnedVisitorSave)
                {
                    _warnedVisitorSave = true;
                    Debug.Log("[DayNight] Http store refused a visitor snapshot (IsOwner=false); " +
                              "only the environment owner writes daynight.json.");
                }
                return;
            }

            // Stamp here too: the module already stamps in CaptureState, but the store must not
            // depend on the caller doing it (and the contract requires a fresh updatedAt).
            EnvironmentDayNightState stamped = state.Stamp(environmentId);

            string body = JsonUtility.ToJson(BuildWireDoc(stamped));
            HttpRunner.Start(BuildUrl(environmentId), body, "PUT", ContentType,
                             stamped.IsOwner, TimeoutSeconds, null);
        }

        // ------------------------------------------------------------- JSON wire
        [Serializable]
        private struct WireQuat
        {
            public float x, y, z, w;
        }

        /// <summary>Portal document shape: camelCase, matching the Node.js contract exactly.</summary>
        [Serializable]
        private struct WireDoc
        {
            public int version;
            public string environmentId;
            public float time01;
            public bool cycleRunning;
            public float rotationSpeed;
            public WireQuat baseRotation;
            public string updatedAt;
            public string updatedBy;
        }

        private static WireDoc BuildWireDoc(EnvironmentDayNightState s)
        {
            var doc = new WireDoc
            {
                version = s.Version > 0 ? s.Version : EnvironmentDayNightState.StateVersion,
                environmentId = s.environmentId ?? string.Empty,
                time01 = Mathf.Repeat(s.Time01, 1f),
                cycleRunning = s.CycleRunning,
                rotationSpeed = s.RotationSpeed,
                baseRotation = new WireQuat
                {
                    x = s.BaseRotation.x,
                    y = s.BaseRotation.y,
                    z = s.BaseRotation.z,
                    w = s.BaseRotation.w
                },
                updatedAt = s.updatedAt ?? string.Empty,
                updatedBy = s.updatedBy ?? string.Empty
            };
            // Canonical mapping: identity is (0,0,0,1); JsonUtility cannot represent an "absent"
            // quaternion, so identity is sent as zeroed (server + reader treat identical zero as absent).
            if (s.BaseRotation == Quaternion.identity)
            {
                doc.baseRotation.x = 0f;
                doc.baseRotation.y = 0f;
                doc.baseRotation.z = 0f;
                doc.baseRotation.w = 0f;
            }
            return doc;
        }

        internal static bool TryParseWire(string json, string environmentId,
                                          out EnvironmentDayNightState state, bool ok)
        {
            state = EnvironmentDayNightState.Default;
            if (string.IsNullOrEmpty(json) || !ok) return false;

            WireDoc doc;
            try
            {
                doc = JsonUtility.FromJson<WireDoc>(json);
            }
            catch
            {
                // Corrupt payload: behave as "nothing stored", keep defaults.
                return false;
            }

            if (doc.version <= 0 || doc.version > EnvironmentDayNightState.StateVersion)
            {
                Debug.LogWarning($"[DayNightStore] Stored state for '{environmentId}' is v{doc.version}, " +
                                 $"this build supports up to v{EnvironmentDayNightState.StateVersion}. Ignoring it.");
                return false;
            }

            if (!string.IsNullOrEmpty(doc.environmentId) &&
                doc.environmentId != EnvironmentDayNightPath.Sanitize(environmentId))
            {
                Debug.LogWarning($"[DayNightStore] Payload claims environment '{doc.environmentId}' but the " +
                                 $"URL id is '{EnvironmentDayNightPath.Sanitize(environmentId)}'. Ignoring it.");
                return false;
            }

            state.environmentId = environmentId;
            state.updatedAt = doc.updatedAt ?? string.Empty;
            state.updatedBy = doc.updatedBy ?? string.Empty;
            state.Version = doc.version;

            // Take CycleRunning literally: a GET hit means the environment had a known state,
            // including "the cycle was OFF when the place was left".
            state.CycleRunning = doc.cycleRunning;
            state.Time01 = Mathf.Repeat(doc.time01, 1f);
            state.SunAngle = state.Time01 * 360f;
            state.RotationSpeed = doc.rotationSpeed >= 0f ? doc.rotationSpeed : EnvironmentDayNightState.Default.RotationSpeed;

            // (0,0,0,0) = field absent (never sent / stripped by an older service): keep identity.
            if (Mathf.Abs(doc.baseRotation.w) > 0.0001f || Mathf.Abs(doc.baseRotation.x) > 0.0001f ||
                Mathf.Abs(doc.baseRotation.y) > 0.0001f || Mathf.Abs(doc.baseRotation.z) > 0.0001f)
            {
                float norm = Mathf.Sqrt(
                    doc.baseRotation.x * doc.baseRotation.x +
                    doc.baseRotation.y * doc.baseRotation.y +
                    doc.baseRotation.z * doc.baseRotation.z +
                    doc.baseRotation.w * doc.baseRotation.w);
                if (norm > 0.5f && norm < 2f)   // near unit length = plausible quaternion
                {
                    state.BaseRotation = new Quaternion(doc.baseRotation.x / norm,
                                                        doc.baseRotation.y / norm,
                                                        doc.baseRotation.z / norm,
                                                        doc.baseRotation.w / norm);
                }
            }
            return true;
        }

        // ------------------------------------------------------------------ URLs
        public string BuildUrl(string environmentId)
        {
            string relative = EnvironmentDayNightPath.BuildRelativePath(environmentId);
            string root = BaseUrl.TrimEnd('/');
            return root + "/" + relative;
        }

        private void WarnDisabledOnce()
        {
            if (_warnedDisabled) return;
            _warnedDisabled = true;
            Debug.Log("[DayNight] HttpEnvironmentDayNightStore has no BaseUrl, so it stays disabled and " +
                      "the PlayerPrefs store is used instead. Set the BaseUrl to plug the Node service in.");
        }

        /// <summary>Fire-and-forget coroutine host. Hidden, not saved, created per request then destroyed.</summary>
        private sealed class HttpRunner : MonoBehaviour
        {
            private static bool _warnedUnsendable;

            public static void Start(string url, string body, string method, string contentType,
                                     bool isOwner, int timeout, Action<string, bool> onDone)
            {
                var go = new GameObject("~DayNightHttpRunner")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(go);
                go.AddComponent<HttpRunner>().Run(url, body, method, contentType, isOwner, timeout, onDone);
            }

            private void Run(string url, string body, string method, string contentType,
                             bool isOwner, int timeout, Action<string, bool> onDone)
            {
                StartCoroutine(RunRoutine(url, body, method, contentType, isOwner, timeout, onDone));
            }

            private System.Collections.IEnumerator RunRoutine(string url, string body, string method,
                                                              string contentType, bool isOwner, int timeout,
                                                              Action<string, bool> onDone)
            {
                bool isRead = string.IsNullOrEmpty(method) ||
                              string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);

                UnityWebRequest request = isRead
                    ? UnityWebRequest.Get(url)
                    : new UnityWebRequest(url, method);

                if (!string.IsNullOrEmpty(contentType)) request.SetRequestHeader("Content-Type", contentType);
                if (!isRead && isOwner)
                    request.SetRequestHeader(OwnerHeader, "true");

                if (!string.IsNullOrEmpty(body))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    request.uploadHandler = new UploadHandlerRaw(bytes);
                    request.downloadHandler = new DownloadHandlerBuffer();
                }
                request.timeout = timeout > 0 ? timeout : 10;

                bool ok = false;
                string payload = null;
                UnityWebRequestAsyncOperation operation = null;

                // Unity throws synchronously from SendWebRequest() when Player Settings disallows
                // insecure HTTP (a common misconfiguration for the plain-http module server). Catch
                // it here: a contained warning + PlayerPrefs fallback is better than a stack trace
                // per GET/PUT and a half-started coroutine.
                try
                {
                    operation = request.SendWebRequest();
                }
                catch (Exception e)
                {
                    WarnUnsendable(url, e);
                }

                try
                {
                    if (operation != null)
                    {
                        yield return operation;
                        ok = request.result == UnityWebRequest.Result.Success;
                        if (ok)
                        {
                            // Read the body BEFORE Dispose, otherwise downloadHandler is already gone.
                            payload = request.downloadHandler != null ? request.downloadHandler.text : null;
                        }
                        else
                        {
                            // A 404 on GET is the normal "this place has never been saved" first run,
                            // not an error worth a warning on every Play.
                            bool firstRunMiss = isRead && request.responseCode == 404;
                            if (!firstRunMiss)
                                Debug.LogWarning($"[DayNight] Environment state request '{request.url}' failed: {request.error}");
                        }
                    }
                }
                finally
                {
                    request.Dispose();
                }

                try
                {
                    onDone?.Invoke(payload, ok);
                }
                finally
                {
                    // One request per runner object: destroy the hidden HOST, not just the
                    // component, otherwise a DontDestroyOnLoad GameObject leaks per request.
                    if (this != null) Destroy(gameObject);
                }
            }

            private static void WarnUnsendable(string url, Exception e)
            {
                string message = e != null ? e.Message : "unknown error";
                bool insecure = message.IndexOf("Insecure", StringComparison.OrdinalIgnoreCase) >= 0;

                if (insecure)
                {
                    if (_warnedUnsendable) return;
                    _warnedUnsendable = true;
                    Debug.LogWarning("[DayNight] HTTP store skipped: Player Settings disallows insecure " +
                                     "HTTP. Use an https:// BaseUrl, or enable 'Allow downloads over HTTP' " +
                                     "for this build. PlayerPrefs persistence stays active. URL: " + url);
                    return;
                }

                Debug.LogWarning($"[DayNight] Environment state request '{url}' could not start: {message}");
            }
        }
    }
}
