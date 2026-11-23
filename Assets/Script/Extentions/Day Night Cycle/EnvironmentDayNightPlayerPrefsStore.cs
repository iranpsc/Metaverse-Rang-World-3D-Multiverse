using UnityEngine;

namespace Meta
{
    /// <summary>
    /// Default <see cref="IEnvironmentDayNightStore"/>: per-machine PlayerPrefs, one key per
    /// environment (<c>"env_daynight_" + environmentId</c>).
    ///
    /// This is a PLACE-persistence prototype: it survives a reload on the same machine. It does NOT
    /// survive a reinstall or share between machines - that is the job of the server-backed store
    /// described in <see cref="IEnvironmentDayNightStore"/>.
    ///
    /// The payload is JSON so new fields can be added later without breaking old saves; the struct's
    /// own <see cref="EnvironmentDayNightState.Version"/> guards incompatible ones.
    /// </summary>
    public class EnvironmentDayNightPlayerPrefsStore : IEnvironmentDayNightStore
    {
        public const string KeyPrefix = "env_daynight_";

        [Tooltip("Also flush to disk immediately. Keep ON for safety; turn OFF to batch writes " +
                 "(PlayerPrefs.Save is the expensive part, SetString itself is not).")]
        public bool FlushToDisk = true;

        public string Describe => "PlayerPrefs";

        public void Save(string environmentId, EnvironmentDayNightState state)
        {
            if (string.IsNullOrEmpty(environmentId)) return;

            EnvironmentDayNightState snapshot = state;
            snapshot.Version = EnvironmentDayNightState.StateVersion;

            PlayerPrefs.SetString(Key(environmentId), JsonUtility.ToJson(snapshot));
            if (FlushToDisk) PlayerPrefs.Save();
        }

        public bool TryLoad(string environmentId, out EnvironmentDayNightState state)
        {
            state = EnvironmentDayNightState.Default;
            if (string.IsNullOrEmpty(environmentId)) return false;

            string json = PlayerPrefs.GetString(Key(environmentId), string.Empty);
            if (string.IsNullOrEmpty(json)) return false;

            EnvironmentDayNightState loaded;
            try
            {
                loaded = JsonUtility.FromJson<EnvironmentDayNightState>(json);
            }
            catch
            {
                // Corrupt payload (interrupted write, older format): treat it as "nothing stored".
                return false;
            }

            if (loaded.Version <= 0 || loaded.Version > EnvironmentDayNightState.StateVersion)
            {
                Debug.LogWarning($"[DayNightStore] Stored state for '{environmentId}' is v{loaded.Version}, " +
                                 $"this build supports up to v{EnvironmentDayNightState.StateVersion}. Ignoring it.");
                return false;
            }

            // Derived fields are recomputed by the module; make sure they are never trusted as saved.
            loaded.Time01 = Mathf.Repeat(loaded.Time01, 1f);
            loaded.SunAngle = loaded.Time01 * 360f;

            state = loaded;
            return true;
        }

        /// <summary>Forget one environment (debug / "reset the place" helper).</summary>
        public void Delete(string environmentId)
        {
            if (string.IsNullOrEmpty(environmentId)) return;
            PlayerPrefs.DeleteKey(Key(environmentId));
            if (FlushToDisk) PlayerPrefs.Save();
        }

        private static string Key(string environmentId) => KeyPrefix + environmentId;
    }
}