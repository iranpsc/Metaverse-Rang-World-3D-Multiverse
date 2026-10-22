using System;
using System.Globalization;
using UnityEngine;

namespace Meta
{
    /// <summary>
    /// Per-citizen minimap settings: the preferences of ONE player, as opposed to the shared
    /// environment state (that is <c>EnvironmentDayNightState</c> in the DayNight module).
    ///
    /// Field names match the JSON contract exactly (camelCase) so the same payload can be written by
    /// the offline PlayerPrefs store and by the Node.js store later without a translation layer:
    ///
    /// <code>
    /// {
    ///   "version": 1,
    ///   "citizenId": "string",
    ///   "radarEnabled": true,
    ///   "zoomOrOrthoSize": 25.0,
    ///   "offsetY": 50.0,
    ///   "updatedAt": "2026-10-18T03:10:08.1234567Z"
    /// }
    /// </code>
    ///
    /// All fields are public FIELDS (not properties) on purpose: Unity's <c>JsonUtility</c> only
    /// serialises fields, which keeps the offline fallback byte-compatible with the HTTP store.
    /// </summary>
    [System.Serializable]
    public struct MiniMapCitizenSettings
    {
        /// <summary>Bump only on breaking changes, so old payloads can be detected and discarded.</summary>
        public const int SettingsVersion = 1;

        [Tooltip("Format version of this payload. 1 = current.")]
        public int version;

        [Tooltip("Owner of these preferences.")]
        public string citizenId;

        [Tooltip("Was the radar switched on by this citizen?")]
        public bool radarEnabled;

        [Tooltip("Orthographic size for an ortho radar camera, or follow height for a perspective one.")]
        public float zoomOrOrthoSize;

        [Tooltip("Camera height above the player (Offset.y). 0 = keep the scene value.")]
        public float offsetY;

        [Tooltip("ISO-8601 UTC timestamp of the last write. Informational only.")]
        public string updatedAt;

        /// <summary>Radar off, 40 m ortho size, no altitude override, no owner.</summary>
        public static MiniMapCitizenSettings Default => new MiniMapCitizenSettings
        {
            version = SettingsVersion,
            citizenId = string.Empty,
            radarEnabled = false,
            zoomOrOrthoSize = 40f,
            offsetY = 0f,
            updatedAt = string.Empty
        };

        /// <summary>Stamps the payload for <paramref name="citizenId"/> with the current time.</summary>
        public MiniMapCitizenSettings Stamp(string citizenId)
        {
            MiniMapCitizenSettings stamped = this;
            stamped.version = SettingsVersion;
            stamped.citizenId = citizenId ?? string.Empty;
            stamped.updatedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            return stamped;
        }

        /// <summary>True when this build can read the payload.</summary>
        public bool IsSupportedVersion => version > 0 && version <= SettingsVersion;

        public override string ToString() =>
            $"v{version} citizen='{citizenId}' radar={(radarEnabled ? "on" : "off")} " +
            $"zoom={zoomOrOrthoSize:F1} offsetY={offsetY:F1} updatedAt={updatedAt}";
    }
}