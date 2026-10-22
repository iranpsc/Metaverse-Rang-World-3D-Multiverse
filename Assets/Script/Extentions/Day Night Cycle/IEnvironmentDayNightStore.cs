using System;

namespace Meta
{
    /// <summary>
    /// Persistence hook for one environment's day/night state.
    ///
    /// The intent is to persist the state of the PLACE, not of the citizen: the same lobby keeps its
    /// time of day for whoever walks in next, on any machine, and a playerPrefs-only prototype is
    /// enough until the dedicated server owns it.
    ///
    /// Default implementation: <see cref="EnvironmentDayNightPlayerPrefsStore"/>.
    ///
    /// SERVER HOOK (not implemented yet, by design - Network_A is out of scope here):
    /// implement this interface with a server-backed store and inject it before the cycle starts:
    ///
    /// <code>
    /// // one line, in whatever bootstraps the lobby scene (NOT in this folder):
    /// dayNight.SetStore(new ServerEnvironmentDayNightStore(rpcClient));
    /// </code>
    ///
    /// The server implementation would fetch/send the snapshot over the existing room RPC instead of
    /// PlayerPrefs. Everything else in the module stays the same: the module only ever talks to
    /// <see cref="Save"/> and <see cref="TryLoad"/>.
    ///
    /// Threading: called on the Unity main thread only (from Awake/OnEnable/Tick/OnDisable).
    /// Implementations may block, but a slow store will stall the cycle tick.
    /// </summary>
    public interface IEnvironmentDayNightStore
    {
        /// <summary>Persist the state for one environment. Overwrites any previous value.</summary>
        void Save(string environmentId, EnvironmentDayNightState state);

        /// <summary>
        /// Read back a stored state.
        /// </summary>
        /// <returns>
        /// <c>false</c> when nothing is stored for that environment, when the stored payload is
        /// unreadable, or when its <see cref="EnvironmentDayNightState.Version"/> is not supported.
        /// </returns>
        bool TryLoad(string environmentId, out EnvironmentDayNightState state);

        /// <summary>Short name used in logs, e.g. <c>PlayerPrefs</c> or <c>Http</c>.</summary>
        string Describe { get; }
    }

    /// <summary>
    /// Optional companion of <see cref="IEnvironmentDayNightStore"/> for stores whose synchronous
    /// <c>TryLoad</c> is a stub for threading reasons (HTTP): the real read happens in a callback on
    /// the Unity main thread. <see cref="Meta_DayNightCycle"/> uses this automatically when the
    /// active store implements it.
    /// </summary>
    public interface IEnvironmentDayNightStoreAsync
    {
        /// <summary>
        /// Non-blocking read for one environment. <paramref name="onLoaded"/> receives (state, ok);
        /// on a miss or a server error it fires with ok = false and the caller keeps its defaults.
        /// Never called synchronously from within this method's stack.
        /// </summary>
        void TryLoadAsync(string environmentId, Action<EnvironmentDayNightState, bool> onLoaded);
    }

    /// <summary>
    /// Path shapes for the environment day/night document. Shared by every store so the local
    /// fallback and the Node.js service agree on where a place's data lives.
    /// </summary>
    public static class EnvironmentDayNightPath
    {
        /// <summary>Used when the caller has no environment id yet.</summary>
        public const string DefaultEnvironmentId = "default";

        /// <summary>Preferred contract shape: <c>environments/{environmentId}/daynight.json</c>.</summary>
        public const string RelativeTemplate = "environments/{0}/daynight.json";

        /// <summary>
        /// Replaces characters that are unsafe in a URL path segment, a file name or a PlayerPrefs
        /// key. Same rule as <c>MiniMapCitizenPath.Sanitize</c>: letters, digits, '-', '_' and '.'
        /// survive; everything else becomes '_'.
        /// </summary>
        public static string Sanitize(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return string.Empty;

            var sb = new System.Text.StringBuilder(segment.Length);
            foreach (char c in segment)
            {
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                            (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                sb.Append(safe ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>The store-relative path, e.g. <c>environments/lobby_1/daynight.json</c>.</summary>
        public static string BuildRelativePath(string environmentId)
        {
            string env = Sanitize(string.IsNullOrEmpty(environmentId) ? DefaultEnvironmentId : environmentId);
            return string.Format(RelativeTemplate, env);
        }
    }
}