using System;
using UnityEngine;
using UnityEngine.Networking;

namespace Meta
{
    /// <summary>
    /// HTTP store for the employer model (Node.js + JSON). Deliberately a STUB: it is disabled until
    /// a base URL is configured, so nothing changes for the project today, but the URL/JSON wiring is
    /// finished so plugging the service in is a configuration change, not a rewrite.
    ///
    /// Contract (see <see cref="MiniMapCitizenPath"/>):
    ///   GET  {baseUrl}/environments/{environmentId}/minimap/{citizenId}.json
    ///   PUT  {baseUrl}/environments/{environmentId}/minimap/{citizenId}.json   body = settings JSON
    ///
    /// Why the load is asynchronous: <see cref="IMiniMapCitizenStore"/> is synchronous because the
    /// interface must stay usable from button clicks and lifecycle callbacks. Blocking the render
    /// thread on an HTTP round trip would freeze the game, so <see cref="TryLoad"/> reports
    /// "unavailable" and <see cref="TryLoadAsync"/> is the real, callback based read.
    ///
    /// WRITE is fire-and-forget: a PUT does not need an answer to keep the local settings correct,
    /// and the local PlayerPrefs store remains the source of truth until the service is live.
    /// </summary>
    public class HttpMiniMapStore : IMiniMapCitizenStore, IMiniMapCitizenStoreAsync
    {
        [Tooltip("Base URL of the settings service, e.g. http://127.0.0.1:8787. Empty = disabled.")]
        public string BaseUrl = "";

        [Tooltip("Seconds before a PUT is abandoned. 0 = default (10 s).")]
        public int TimeoutSeconds = 10;

        [Tooltip("Sent as the 'Content-Type' header on PUT.")]
        public string ContentType = "application/json";

        private bool _warnedDisabled;

        public string Describe => "Http";

        public bool Enabled => !string.IsNullOrEmpty(BaseUrl);

        // ------------------------------------------------------------------ read
        public bool TryLoad(string environmentId, string citizenId, out MiniMapCitizenSettings settings)
        {
            settings = MiniMapCitizenSettings.Default;

            if (!Enabled)
            {
                WarnDisabledOnce();
                return false;
            }

            if (string.IsNullOrEmpty(citizenId)) return false;

            // Synchronous read would stall the render thread; the caller must use TryLoadAsync.
            Debug.Log("[MiniMap] HttpMiniMapStore.TryLoad is a stub and always reports 'unavailable'. " +
                      "Use TryLoadAsync(callback) so the read happens off the critical path.");
            return false;
        }

        /// <summary>
        /// Non-blocking read. <paramref name="onLoaded"/> receives (settings, ok). Safe to call when the
        /// store is disabled: the callback fires with ok = false.
        /// </summary>
        public void TryLoadAsync(string environmentId, string citizenId, Action<MiniMapCitizenSettings, bool> onLoaded)
        {
            if (onLoaded == null) return;

            if (!Enabled)
            {
                WarnDisabledOnce();
                onLoaded(MiniMapCitizenSettings.Default, false);
                return;
            }
            if (string.IsNullOrEmpty(citizenId))
            {
                onLoaded(MiniMapCitizenSettings.Default, false);
                return;
            }

            string url = BuildGetUrl(environmentId, citizenId);
            HttpRunner.Start(url, null, null, ContentType, TimeoutSeconds,
                (payload, ok) =>
                {
                    MiniMapCitizenSettings parsed = MiniMapCitizenSettings.Default;
                    if (ok && !string.IsNullOrEmpty(payload))
                        PlayerPrefsMiniMapStore.TryParse(payload, citizenId, out parsed);
                    onLoaded(parsed, ok);
                });
        }

        // ----------------------------------------------------------------- write
        public bool Save(string environmentId, string citizenId, MiniMapCitizenSettings settings)
        {
            if (!Enabled)
            {
                WarnDisabledOnce();
                return false;
            }
            if (string.IsNullOrEmpty(citizenId)) return false;

            MiniMapCitizenSettings stamped = settings.Stamp(citizenId);
            HttpRunner.Start(BuildPutUrl(environmentId, citizenId), JsonUtility.ToJson(stamped), "PUT",
                             ContentType, TimeoutSeconds, null);
            return true;
        }

        // ------------------------------------------------------------------ URLs
        public string BuildGetUrl(string environmentId, string citizenId) =>
            BuildUrl(environmentId, citizenId);

        public string BuildPutUrl(string environmentId, string citizenId) =>
            BuildUrl(environmentId, citizenId);

        private string BuildUrl(string environmentId, string citizenId)
        {
            string relative = MiniMapCitizenPath.BuildRelativePath(environmentId, citizenId);
            string root = BaseUrl.TrimEnd('/');
            return root + "/" + relative;
        }

        private void WarnDisabledOnce()
        {
            if (_warnedDisabled) return;
            _warnedDisabled = true;
            Debug.Log("[MiniMap] HttpMiniMapStore has no BaseUrl, so it stays disabled and the " +
                      "PlayerPrefs store is used instead. Set the BaseUrl to plug the Node service in.");
        }

        /// <summary>Fire-and-forget coroutine host. Hidden and not saved, created on first use only.</summary>
        private sealed class HttpRunner : MonoBehaviour
        {
            public static void Start(string url, string body, string method, string contentType, int timeout,
                                     Action<string, bool> onDone)
            {
                var go = new GameObject("~MiniMapHttpRunner")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(go);
                go.AddComponent<HttpRunner>().Run(url, body, method, contentType, timeout, onDone);
            }

            private void Run(string url, string body, string method, string contentType, int timeout,
                             Action<string, bool> onDone)
            {
                StartCoroutine(RunRoutine(url, body, method, contentType, timeout, onDone));
            }

            private System.Collections.IEnumerator RunRoutine(string url, string body, string method, string contentType,
                                                             int timeout, Action<string, bool> onDone)
            {
                UnityWebRequest request = string.IsNullOrEmpty(method)
                    ? UnityWebRequest.Get(url)
                    : new UnityWebRequest(url, method);

                if (!string.IsNullOrEmpty(contentType)) request.SetRequestHeader("Content-Type", contentType);
                if (!string.IsNullOrEmpty(body))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    request.uploadHandler = new UploadHandlerRaw(bytes);
                    request.downloadHandler = new DownloadHandlerBuffer();
                }
                request.timeout = timeout > 0 ? timeout : 10;

                bool ok = false;
                string payload = null;
                try
                {
                    yield return request.SendWebRequest();
                    ok = request.result == UnityWebRequest.Result.Success;
                    if (ok)
                    {
                        // Read the body BEFORE Dispose, otherwise downloadHandler is already gone.
                        payload = request.downloadHandler != null ? request.downloadHandler.text : null;
                    }
                    else
                    {
                        Debug.LogWarning($"[MiniMap] Settings request '{request.url}' failed: {request.error}");
                    }
                }
                finally
                {
                    request.Dispose();
                }

                onDone?.Invoke(payload, ok);

                // One request per runner object: clean up as soon as this coroutine is done.
                Destroy(this);
            }
        }
    }
}