using UnityEngine;

namespace Meta
{
    /// <summary>
    /// Default store: local PlayerPrefs JSON. Works offline today, no server, no files to manage,
    /// and produces the exact same payload shape as the HTTP store.
    ///
    /// Key shape: <c>citizen_minimap_{environmentId}_{citizenId}</c>, or the legacy
    /// <c>citizen_minimap_{citizenId}</c> when no environment id is set (still readable).
    /// </summary>
    public class PlayerPrefsMiniMapStore : IMiniMapCitizenStore
    {
        [Tooltip("Flush to disk on every write. Turn off to batch (SetString itself is cheap, " +
                 "PlayerPrefs.Save is not).")]
        public bool FlushToDisk = true;

        public string Describe => "PlayerPrefs";

        public bool TryLoad(string environmentId, string citizenId, out MiniMapCitizenSettings settings)
        {
            settings = MiniMapCitizenSettings.Default;
            if (string.IsNullOrEmpty(citizenId)) return false;

            string json = PlayerPrefs.GetString(
                MiniMapCitizenPath.BuildPlayerPrefsKey(environmentId, citizenId), string.Empty);

            // Backwards compatibility: payloads written before environments existed.
            if (string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(environmentId))
                json = PlayerPrefs.GetString(MiniMapCitizenPath.BuildLegacyPlayerPrefsKey(citizenId), string.Empty);

            if (string.IsNullOrEmpty(json)) return false;
            return TryParse(json, citizenId, out settings);
        }

        public bool Save(string environmentId, string citizenId, MiniMapCitizenSettings settings)
        {
            if (string.IsNullOrEmpty(citizenId)) return false;

            MiniMapCitizenSettings stamped = settings.Stamp(citizenId);
            PlayerPrefs.SetString(
                MiniMapCitizenPath.BuildPlayerPrefsKey(environmentId, citizenId),
                JsonUtility.ToJson(stamped));
            if (FlushToDisk) PlayerPrefs.Save();
            return true;
        }

        /// <summary>Forget one citizen's settings (debug / "reset this player" helper).</summary>
        public bool Delete(string environmentId, string citizenId)
        {
            if (string.IsNullOrEmpty(citizenId)) return false;

            PlayerPrefs.DeleteKey(MiniMapCitizenPath.BuildPlayerPrefsKey(environmentId, citizenId));
            PlayerPrefs.DeleteKey(MiniMapCitizenPath.BuildLegacyPlayerPrefsKey(citizenId));
            if (FlushToDisk) PlayerPrefs.Save();
            return true;
        }

        /// <summary>Shared JSON parsing + version guard, so every store rejects bad payloads the same way.</summary>
        internal static bool TryParse(string json, string citizenId, out MiniMapCitizenSettings settings)
        {
            settings = MiniMapCitizenSettings.Default;
            if (string.IsNullOrEmpty(json)) return false;

            MiniMapCitizenSettings parsed;
            try
            {
                parsed = JsonUtility.FromJson<MiniMapCitizenSettings>(json);
            }
            catch
            {
                return false;   // corrupt payload: behave as "nothing stored"
            }

            if (!parsed.IsSupportedVersion)
            {
                Debug.LogWarning($"[MiniMap] Stored settings for '{citizenId}' are v{parsed.version}; " +
                                 $"this build supports up to v{MiniMapCitizenSettings.SettingsVersion}. Ignoring them.");
                return false;
            }

            // A payload never decides who it belongs to: trust the id the caller asked for.
            parsed.citizenId = citizenId;
            settings = parsed;
            return true;
        }
    }
}