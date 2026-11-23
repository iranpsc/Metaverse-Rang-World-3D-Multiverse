#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Meta;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Play-mode verification for the DayNight module against the real Lobby scene wiring.
    ///
    /// The editor side prepares the scene (offline: PlayerPrefs/HTTP disabled) and enters Play.
    /// The runtime probe DayNight_PlayVerifierProbe then drives the real DayNight_ControlPanel
    /// ON -> dusk -> OFF, samples the sun + sky + status, and writes:
    ///   Logs/daynight_play_result.txt
    ///
    /// Menu: Tools/Meta/DayNight/Play Verify ON-OFF + Sky.
    /// Batch: -executeMethod Meta.EditorTools.DayNight_PlayVerifier.Run (NO -quit; it exits itself).
    /// </summary>
    [InitializeOnLoad]
    public static class DayNight_PlayVerifier
    {
        private const string ScenePath = DayNight_SceneRepair.LobbyScenePath;
        private const string TriggerName = "DayNightPlayVerify.trigger";
        private const string RunMarkerName = "DayNightPlayVerify.run";
        private const string PlayStartedName = "DayNightPlayVerify.playstarted";
        private const string BatchMarkerName = "DayNightPlayVerify.batch";
        private const double PlayStartTimeoutSeconds = 60.0;
        private const double PlayRunTimeoutSeconds = 180.0;

        private static bool _finalizing;
        private static bool _playRequested;

        private static string TempRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Temp"));
        private static string TriggerPath => Path.Combine(TempRoot, TriggerName);
        private static string RunMarkerPath => Path.Combine(TempRoot, RunMarkerName);
        private static string PlayStartedPath => Path.Combine(TempRoot, PlayStartedName);
        private static string BatchMarkerPath => Path.Combine(TempRoot, BatchMarkerName);

        private static string ResultPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "daynight_play_result.txt"));

        static DayNight_PlayVerifier()
        {
            EditorApplication.update += Monitor;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += Monitor;
        }

        [MenuItem("Tools/Meta/DayNight/Play Verify ON-OFF + Sky", priority = 107)]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[DayNightPlay] Exit Play mode first.");
                return;
            }

            _finalizing = false;
            _playRequested = false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
                Directory.CreateDirectory(TempRoot);
                File.WriteAllText(ResultPath,
                    "DAYNIGHT PLAY VERIFICATION" + Environment.NewLine +
                    DateTime.UtcNow.ToString("o") + Environment.NewLine);
                DeleteIfExists(TriggerPath);
                DeleteIfExists(RunMarkerPath);
                DeleteIfExists(PlayStartedPath);
                DeleteIfExists(BatchMarkerPath);
                if (Application.isBatchMode)
                    File.WriteAllText(BatchMarkerPath,
                        DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                Debug.LogError("[DayNightPlay] Cannot prepare verification files: " + e.Message);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            try
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            catch (Exception e)
            {
                FailBeforePlay("OpenScene", e.Message);
                return;
            }

            if (!PrepareScene())
            {
                FailBeforePlay("PrepareScene", "No usable DayNight rig/panel in the Lobby scene.");
                return;
            }

            try
            {
                File.WriteAllText(RunMarkerPath,
                    DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(TriggerPath, "run");
            }
            catch (Exception e)
            {
                FailBeforePlay("WriteTrigger", e.Message);
                return;
            }

            Debug.Log("[DayNightPlay] Scene prepared offline; entering Play for the runtime probe.");
            EditorApplication.delayCall += RequestPlay;
        }

        private static bool PrepareScene()
        {
            var cycle = DayNight_ControlPanel.BestDayNightInstance();
            if (cycle == null)
            {
                Debug.LogError("[DayNightPlay] BestDayNightInstance() is null.");
                return false;
            }

            if (DayNight_ControlPanel.FindBestPanel() == null)
            {
                Debug.LogError("[DayNightPlay] No DayNight control card in the scene.");
                return false;
            }

            // The Play run must be deterministic and offline, so it can never race a remote
            // environment server or restore a stale PlayerPrefs time. These edits are in-memory
            // only; the scene is reopened from disk after the run.
            var so = new SerializedObject(cycle);
            SetBool(so, "_persistEnvironment", false);
            SetBool(so, "_useHttpStore", false);
            SetString(so, "_environmentId", string.Empty);
            SetEnum(so, "_authorityMode", (int)EnvironmentAuthorityMode.LocalAuthoritySimulate);
            SetFloat(so, "_time01", 0f);
            SetBool(so, "IsEnabled", false);
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[DayNightPlay] Prepared '{cycle.gameObject.name}': owner, Time01=0, " +
                      "PlayerPrefs/HTTP disabled for this run.");
            return true;
        }

        private static void RequestPlay()
        {
            if (_finalizing) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += RequestPlay;
                return;
            }

            _playRequested = true;
            try
            {
                EditorApplication.isPlaying = true;
            }
            catch (Exception e)
            {
                FailBeforePlay("EnterPlay", e.Message);
            }
        }

        private static void Monitor()
        {
            if (_finalizing) return;
            if (!File.Exists(RunMarkerPath)) return;

            double elapsed = SecondsSince(RunMarkerPath);
            bool playing = EditorApplication.isPlaying;
            bool changing = EditorApplication.isPlayingOrWillChangePlaymode;

            if (playing)
            {
                if (elapsed > PlayRunTimeoutSeconds)
                {
                    AppendLine($"FAIL Timeout | Play run exceeded {PlayRunTimeoutSeconds:F0}s");
                    Debug.LogWarning("[DayNightPlay] Play run timed out - leaving Play mode.");
                    EditorApplication.ExitPlaymode();
                    return;
                }

                // The probe consumes the trigger immediately after the scene loads. If it is
                // still there much later, Play started without the probe (or it never ran).
                if (File.Exists(TriggerPath) && elapsed > 30.0)
                {
                    AppendLine("FAIL ProbeStart | Play is running but the runtime probe never consumed the trigger.");
                    EditorApplication.ExitPlaymode();
                }
                return;
            }

            if (changing) return;   // editor is transitioning; wait for the state event

            if (HasFinalResult())
            {
                FinalizeRun();
                return;
            }

            if (File.Exists(PlayStartedPath))
            {
                AppendLine("FAIL ProbeResult | Play ended without writing a RESULT line.");
                FinalizeRun();
                return;
            }

            if (!File.Exists(TriggerPath))
            {
                AppendLine("FAIL ProbeResult | Probe consumed the trigger but wrote no RESULT line.");
                FinalizeRun();
                return;
            }

            if (elapsed > PlayStartTimeoutSeconds)
            {
                AppendLine($"FAIL PlayStart | Play did not start within {PlayStartTimeoutSeconds:F0}s.");
                FinalizeRun();
                return;
            }

            if (!_playRequested && elapsed > 1.0)
                RequestPlay();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                try
                {
                    File.WriteAllText(PlayStartedPath,
                        DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                }
                catch
                {
                    // Marker is diagnostic only; the result file remains authoritative.
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.delayCall += Monitor;
            }
        }

        private static void FinalizeRun()
        {
            if (_finalizing) return;
            _finalizing = true;

            bool batch = File.Exists(BatchMarkerPath) || Application.isBatchMode;
            bool pass = LastResultIsPass();

            if (!HasFinalResult())
            {
                AppendLine("RESULT: FAIL");
                pass = false;
            }

            DeleteIfExists(TriggerPath);
            DeleteIfExists(RunMarkerPath);
            DeleteIfExists(PlayStartedPath);
            DeleteIfExists(BatchMarkerPath);

            // Drop the in-memory offline overrides: restore the scene exactly as authored on disk.
            try
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DayNightPlay] Could not restore the scene after the run: " + e.Message);
            }

            Debug.Log("[DayNightPlay] RESULT: " + (pass ? "PASS" : "FAIL") + " | " + ResultPath);

            if (batch)
            {
                EditorApplication.Exit(pass ? 0 : 1);
                return;
            }

            _finalizing = false;
            _playRequested = false;
        }

        private static void FailBeforePlay(string name, string detail)
        {
            AppendLine($"FAIL {name} | {detail}");
            AppendLine("RESULT: FAIL");
            DeleteIfExists(TriggerPath);
            DeleteIfExists(RunMarkerPath);
            DeleteIfExists(PlayStartedPath);
            DeleteIfExists(BatchMarkerPath);
            Debug.LogError($"[DayNightPlay] {name}: {detail}");

            if (Application.isBatchMode) EditorApplication.Exit(1);
        }

        private static bool HasFinalResult()
        {
            if (!File.Exists(ResultPath)) return false;
            try
            {
                foreach (string line in File.ReadAllLines(ResultPath))
                    if (line.StartsWith("RESULT:", StringComparison.Ordinal)) return true;
            }
            catch
            {
                // A locked file simply means "not finished yet".
            }
            return false;
        }

        private static bool LastResultIsPass()
        {
            if (!File.Exists(ResultPath)) return false;
            try
            {
                string last = null;
                foreach (string line in File.ReadAllLines(ResultPath))
                    if (line.StartsWith("RESULT:", StringComparison.Ordinal)) last = line;
                return last != null && last.IndexOf("PASS", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void AppendLine(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
                File.AppendAllText(ResultPath, line + Environment.NewLine);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DayNightPlay] Cannot append result: " + e.Message);
            }

            if (line.StartsWith("FAIL ", StringComparison.Ordinal) ||
                line.StartsWith("RESULT:", StringComparison.Ordinal))
                Debug.Log("[DayNightPlay] " + line);
        }

        private static double SecondsSince(string path)
        {
            try
            {
                string raw = File.ReadAllText(path).Trim();
                long ticks;
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                {
                    var at = new DateTime(ticks, DateTimeKind.Utc);
                    return (DateTime.UtcNow - at).TotalSeconds;
                }
                return (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
            }
            catch
            {
                return 0.0;
            }
        }

        private static void SetBool(SerializedObject so, string field, bool value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null) p.boolValue = value;
        }

        private static void SetString(SerializedObject so, string field, string value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null) p.stringValue = value;
        }

        private static void SetFloat(SerializedObject so, string field, float value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null) p.floatValue = value;
        }

        private static void SetEnum(SerializedObject so, string field, int value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null) p.enumValueIndex = value;
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
#endif
