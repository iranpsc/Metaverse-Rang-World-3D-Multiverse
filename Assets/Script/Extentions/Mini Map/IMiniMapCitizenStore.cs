using System;
using System.Text;

namespace Meta
{
    /// <summary>
    /// Storage contract for one citizen's minimap settings.
    ///
    /// Two implementations ship with the module:
    ///   * <see cref="PlayerPrefsMiniMapStore"/> - works today, offline, no server needed.
    ///   * <see cref="HttpMiniMapStore"/>        - GET/PUT JSON stub for the Node.js employer model.
    ///
    /// Swapping them is one line on the component, so the Node service can be introduced without
    /// touching <c>Meta_MiniMap</c> at all.
    ///
    /// THREADING: called from the Unity main thread only (Awake/Start/OnDisable/button clicks).
    /// Implementations may block, but a slow store stalls the main thread.
    /// </summary>
    public interface IMiniMapCitizenStore
    {
        /// <summary>Read the settings for one citizen in one environment.</summary>
        /// <returns><c>false</c> when nothing is stored, the payload is unreadable, or its version is unsupported.</returns>
        bool TryLoad(string environmentId, string citizenId, out MiniMapCitizenSettings settings);

        /// <summary>Persist the settings for one citizen in one environment.</summary>
        /// <returns><c>false</c> when the write could not be completed.</returns>
        bool Save(string environmentId, string citizenId, MiniMapCitizenSettings settings);

        /// <summary>Short name used in logs, e.g. <c>PlayerPrefs</c> or <c>Http</c>.</summary>
        string Describe { get; }
    }

    /// <summary>
    /// Optional companion of <see cref="IMiniMapCitizenStore"/> for stores whose synchronous
    /// <c>TryLoad</c> is a stub for threading reasons (HTTP): the real read happens in a callback
    /// on the Unity main thread. <see cref="Meta_MiniMap"/> uses this automatically when the active
    /// store implements it, so enabling the HTTP store needs no other wiring.
    /// </summary>
    public interface IMiniMapCitizenStoreAsync
    {
        /// <summary>
        /// Non-blocking read for one citizen in one environment.
        /// <paramref name="onLoaded"/> receives (settings, ok); on a miss or a server error it fires
        /// with ok = false and the callback must keep the defaults. Never called synchronously.
        /// </summary>
        void TryLoadAsync(string environmentId, string citizenId, Action<MiniMapCitizenSettings, bool> onLoaded);
    }

    /// <summary>
    /// Path and key shapes for the citizen settings. Shared by every store so the offline fallback
    /// and the Node.js service agree on where a citizen's data lives.
    /// </summary>
    public static class MiniMapCitizenPath
    {
        /// <summary>Used when the caller has no environment id yet.</summary>
        public const string DefaultEnvironmentId = "default";

        /// <summary>PlayerPrefs key prefix (offline store).</summary>
        public const string CitizenKeyPrefix = "citizen_minimap_";

        /// <summary>Preferred contract shape: <c>environments/{environmentId}/minimap/{citizenId}.json</c>.</summary>
        public const string RelativeTemplate = "environments/{0}/minimap/{1}.json";

        /// <summary>
        /// Replaces characters that are unsafe in a URL path segment, a file name or a PlayerPrefs key.
        /// Deterministic and reversible enough for ids: letters, digits, '-', '_' and '.' survive.
        /// </summary>
        public static string Sanitize(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return string.Empty;

            var sb = new StringBuilder(segment.Length);
            foreach (char c in segment)
            {
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                            (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                sb.Append(safe ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>The store-relative path, e.g. <c>environments/lobby_1/minimap/qa_user.json</c>.</summary>
        public static string BuildRelativePath(string environmentId, string citizenId)
        {
            string env = Sanitize(string.IsNullOrEmpty(environmentId) ? DefaultEnvironmentId : environmentId);
            string cit = Sanitize(citizenId);
            return string.Format(RelativeTemplate, env, cit);
        }

        /// <summary>
        /// PlayerPrefs key. Includes the environment when one is set, so two places can never share
        /// a key; with no environment it falls back to the legacy
        /// <c>citizen_minimap_ + citizenId</c> shape written by earlier versions.
        /// </summary>
        public static string BuildPlayerPrefsKey(string environmentId, string citizenId)
        {
            string cit = Sanitize(citizenId);
            if (string.IsNullOrEmpty(environmentId)) return CitizenKeyPrefix + cit;
            return CitizenKeyPrefix + Sanitize(environmentId) + "_" + cit;
        }

        /// <summary>Legacy key from before environments were addressed (still read, never written).</summary>
        public static string BuildLegacyPlayerPrefsKey(string citizenId) => CitizenKeyPrefix + Sanitize(citizenId);
    }
}