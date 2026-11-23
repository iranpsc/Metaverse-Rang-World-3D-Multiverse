using UnityEngine;

namespace Meta
{
    /// <summary>
    /// Who drives the environment.
    ///
    /// LocalAuthoritySimulate - the owner/host. It advances the clock itself in
    /// <c>Meta_DayNightCycle.Tick</c> and writes every visual, snapping the rotation so it never drifts.
    ///
    /// ApplyOnly - a visitor. It never simulates (so it cannot double-simulate the owner's cycle) and
    /// renders exclusively from incoming <see cref="EnvironmentDayNightState"/> snapshots applied
    /// through <c>Meta_DayNightCycle.ApplyState</c>.
    /// </summary>
    public enum EnvironmentAuthorityMode
    {
        /// <summary>Owner: advances time01 locally and writes every visual.</summary>
        LocalAuthoritySimulate = 0,

        /// <summary>Visitor: never advances the clock; ApplyState drives everything.</summary>
        ApplyOnly = 1
    }

    /// <summary>
    /// Serializable snapshot of one environment's day/night.
    ///
    /// This struct is the single contract between the three roles:
    ///   owner      - fills it with <c>CaptureState()</c> and broadcasts it,
    ///   visitor    - feeds it to <c>ApplyState()</c>,
    ///   persistence - stores and restores it through <c>IEnvironmentDayNightStore</c>.
    ///
    /// It is a plain [Serializable] struct on purpose: it can be embedded directly in a
    /// NetworkVariable, a message payload, or JSON (the PlayerPrefs store) with no extra code.
    /// </summary>
    [System.Serializable]
    public struct EnvironmentDayNightState
    {
        /// <summary>Bump only on breaking changes so old saves can be detected and discarded.</summary>
        public const int StateVersion = 1;

        [Tooltip("0 = midnight, 1 = next midnight.")]
        public float Time01;

        [Tooltip("Degrees travelled around the rotation axis. Always Time01 * 360.")]
        public float SunAngle;

        [Tooltip("Is the cycle running on the owner?")]
        public bool CycleRunning;

        [Tooltip("Owner degrees per second (0 = frozen day/night).")]
        public float RotationSpeed;

        [Tooltip("Owner flag carried in the snapshot so visitors can render the same role.")]
        public bool IsOwner;

        [Tooltip("Canonical sun orientation at time01 == 0. Shared so every client derives the " +
                 "same rotation from Time01 instead of using its own local scene value.")]
        public Quaternion BaseRotation;

        [Tooltip("PLACE this snapshot belongs to. Filled by the module on capture / by the " +
                 "store on save; the URL id always wins over this field.")]
        public string environmentId;

        [Tooltip("ISO-8601 UTC timestamp of the last write. Informational only.")]
        public string updatedAt;

        [Tooltip("Citizen / server that last wrote. Optional; empty = unknown.")]
        public string updatedBy;

        [Tooltip("Format version. 1 = current.")]
        public int Version;

        /// <summary>Midnight, frozen, default speed.</summary>
        public static EnvironmentDayNightState Default => new EnvironmentDayNightState
        {
            Time01 = 0f,
            SunAngle = 0f,
            CycleRunning = false,
            RotationSpeed = 2f,
            IsOwner = false,
            BaseRotation = Quaternion.identity,
            environmentId = string.Empty,
            updatedAt = string.Empty,
            updatedBy = string.Empty,
            Version = StateVersion
        };

        /// <summary>
        /// Stamps the snapshot for <paramref name="environmentId"/> with the current UTC time.
        /// The HTTP store relies on this for the Node contract fields; a URL id always wins.
        /// </summary>
        public EnvironmentDayNightState Stamp(string environmentId)
        {
            EnvironmentDayNightState stamped = this;
            stamped.Version = StateVersion;
            stamped.environmentId = environmentId ?? string.Empty;
            stamped.updatedAt = System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            return stamped;
        }

        public override string ToString() =>
            $"v{Version} t={Time01:F3} ({SunAngle:F1}deg) running={CycleRunning} speed={RotationSpeed:F2} " +
            $"owner={IsOwner} env='{environmentId}' updatedAt={updatedAt} updatedBy='{updatedBy}'";
    }
}