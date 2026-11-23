#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Meta.Tools
{
    /// <summary>
    /// Runtime half of DayNight_PlayVerifier. The editor tool drops a trigger file and enters
    /// Play; this probe (which survives enter-play domain/scene reloads) drives the real
    /// DayNight_ControlPanel and writes a PASS/FAIL evidence file. Inert unless triggered.
    /// </summary>
    internal sealed class DayNight_PlayVerifierProbe : MonoBehaviour
    {
        private const string TriggerFileName = "DayNightPlayVerify.trigger";
        private const float DuskTime01 = 0.53f;
        private const float DuskSampleSeconds = 6f;
        private const float DuskSampleStep = 0.2f;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string TriggerPath =>
            Path.Combine(ProjectRoot, "Temp", TriggerFileName);

        private static string ResultPath =>
            Path.Combine(ProjectRoot, "Logs", "daynight_play_result.txt");

        private Meta_DayNightCycle _cycle;
        private DayNight_ControlPanel _panel;
        private Text _status;
        private Renderer _discRenderer;

        private Quaternion _initialSunRotation;
        private Material _initialSky;
        private string _initialSkyName;
        private float _initialTime01;
        private Material _onSky;

        private readonly List<float> _exposures = new List<float>();
        private int _failures;
        private bool _finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!File.Exists(TriggerPath)) return;

            // Consume the trigger exactly once, so a later play session never reruns it.
            try { File.Delete(TriggerPath); }
            catch (Exception e) { Debug.LogWarning("[DayNightPlay] Could not consume trigger: " + e.Message); }

            var go = new GameObject("~DayNightPlayVerifierProbe")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(go);
            go.AddComponent<DayNight_PlayVerifierProbe>();
        }

        private void Start()
        {
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            // Let Awake/OnEnable and the scene's first UI Update settle before touching anything.
            yield return new WaitForSecondsRealtime(1.0f);

            AddInfo($"probe started | scene='{SceneManager.GetActiveScene().name}'");
            if (!Setup())
            {
                AddFail("Setup", "Could not find the DayNight rig, its control card or an enabled sun.");
                Finish();
                yield break;
            }

            // Deterministic, offline baseline: the editor tool already disabled PlayerPrefs/HTTP
            // on the serialized scene object before Play. Force the scene-authored look and a clean
            // Time01=0 anchor so the run does not depend on whatever was last stored.
            _cycle.ForceResetToDefault();
            SetPrivate(_cycle, "_time01", 0f);
            _cycle.RotationSpeed = 2f;            // scene design speed, not a stored demo value
            _cycle.AuthorityMode = EnvironmentAuthorityMode.LocalAuthoritySimulate;
            _cycle.RecacheInitialValues();
            yield return null;

            _initialSunRotation = _cycle.Sun != null ? _cycle.Sun.transform.rotation : Quaternion.identity;
            _initialSky = RenderSettings.skybox;
            _initialSkyName = _initialSky != null ? _initialSky.name : "null";
            _initialTime01 = _cycle.Time01;

            AddCheck("owner authority", _cycle.SimulatesLocally, $"Authority={_cycle.AuthorityMode}");
            AddCheck("sky slots assigned", _cycle.SkyDay != null && _cycle.SkyDusk != null && _cycle.SkyNight != null,
                $"day={Name(_cycle.SkyDay)} dusk={Name(_cycle.SkyDusk)} night={Name(_cycle.SkyNight)}");
            AddInfo($"baseline sun='{_cycle.Sun.name}' speed={_cycle.RotationSpeed:F2} " +
                    $"time={_initialTime01:F4} sky='{_initialSkyName}'");

            // ---- ON: drive the real card, then observe the live cycle.
            _panel.OnClicked();
            float onStartTime = _cycle.Time01;
            Quaternion onStartRotation = _cycle.Sun.transform.rotation;
            yield return new WaitForSecondsRealtime(2.5f);

            AddCheck("ON enabled", _cycle.IsEnabled, $"IsEnabled={_cycle.IsEnabled}");
            AddCheck("ON time advances", _cycle.Time01 > onStartTime + 0.005f,
                $"Time01 {onStartTime:F4} -> {_cycle.Time01:F4}");
            float onRotationDelta = Quaternion.Angle(onStartRotation, _cycle.Sun.transform.rotation);
            AddCheck("ON sun rotates", onRotationDelta > 2f, $"Delta={onRotationDelta:F2}deg");

            string onStatus = _status != null ? _status.text ?? string.Empty : string.Empty;
            AddCheck("ON status", onStatus.Contains("Cycle: ON"), "Status='" + OneLine(onStatus) + "'");

            _onSky = RenderSettings.skybox;
            AddCheck("ON sky assigned", _onSky != null && _onSky != _initialSky,
                $"Sky '{_initialSkyName}' -> '{Name(_onSky)}'");
            AddCheck("ON phase sky", _cycle.HasPhaseSky, $"HasPhaseSky={_cycle.HasPhaseSky}");
            AddCheck("ON visible disc", _cycle.VisibleSunTransform != null,
                $"Disc={(_cycle.VisibleSunTransform != null ? _cycle.VisibleSunTransform.name : "none")}");

            _discRenderer = _cycle.VisibleSunTransform != null
                ? _cycle.VisibleSunTransform.GetComponent<Renderer>()
                : null;
            AddInfo($"night disc renderer enabled={(_discRenderer != null && _discRenderer.enabled)} " +
                    $"(ShowDiscAtNight={_cycle.ShowDiscAtNight})");

            // ---- DAY: the same disc must become visible and the sun must actually light the scene.
            var day = _cycle.CaptureState();
            day.Time01 = 0.40f;
            day.SunAngle = 0.40f * 360f;
            day.CycleRunning = true;
            day.RotationSpeed = 2f;
            day.IsOwner = true;
            day.Version = EnvironmentDayNightState.StateVersion;
            _cycle.ApplyState(day, snap: true);
            yield return new WaitForSecondsRealtime(0.75f);

            string dayPhase = _cycle.DescribeSkyPhase(_cycle.Time01);
            AddCheck("Day phase reached", dayPhase.IndexOf("day", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Time01={_cycle.Time01:F4} phase='{dayPhase}'");
            AddCheck("Day sun lit", _cycle.Sun != null && _cycle.Sun.intensity > 0.1f,
                $"Intensity={(_cycle.Sun != null ? _cycle.Sun.intensity : -1f):F2}");

            _discRenderer = _cycle.VisibleSunTransform != null
                ? _cycle.VisibleSunTransform.GetComponent<Renderer>()
                : null;
            AddCheck("Day disc renderer visible", _discRenderer != null && _discRenderer.enabled,
                _discRenderer != null ? $"Disc renderer enabled={_discRenderer.enabled}" : "Disc renderer missing");

            // ---- DUSK: jump the owner clock to the dusk band and sample the blend.
            var dusk = _cycle.CaptureState();
            dusk.Time01 = DuskTime01;
            dusk.SunAngle = DuskTime01 * 360f;
            dusk.CycleRunning = true;
            dusk.RotationSpeed = 2f;
            dusk.IsOwner = true;
            dusk.Version = EnvironmentDayNightState.StateVersion;
            _cycle.ApplyState(dusk, snap: true);

            AddInfo($"jumped to dusk: Time01={_cycle.Time01:F4} phase='{_cycle.DescribeSkyPhase(_cycle.Time01)}'");
            yield return new WaitForSecondsRealtime(0.5f);

            _exposures.Clear();
            for (float t = 0f; t < DuskSampleSeconds; t += DuskSampleStep)
            {
                SampleSky();
                yield return new WaitForSecondsRealtime(DuskSampleStep);
            }

            CheckDusk();

            // ---- OFF: the same real card must stop and restore the scene lighting.
            _panel.OffClicked();
            yield return new WaitForSecondsRealtime(1.0f);

            AddCheck("OFF disabled", !_cycle.IsEnabled, $"IsEnabled={_cycle.IsEnabled}");
            string offStatus = _status != null ? _status.text ?? string.Empty : string.Empty;
            AddCheck("OFF status", offStatus.Contains("Cycle: OFF"), "Status='" + OneLine(offStatus) + "'");
            AddCheck("OFF status sky truthful", offStatus.Contains("(restored)") || offStatus.Contains("Sky: none"),
                "Status='" + OneLine(offStatus) + "'");

            float offDelta = _cycle.Sun != null
                ? Quaternion.Angle(_initialSunRotation, _cycle.Sun.transform.rotation)
                : 999f;
            AddCheck("OFF sun restored", offDelta < 0.5f, $"Delta={offDelta:F3}deg");

            Material offSky = RenderSettings.skybox;
            AddCheck("OFF sky restored", offSky == _initialSky,
                $"Sky '{Name(offSky)}' vs '{_initialSkyName}'");

            // ---- PERSISTENCE: real PlayerPrefs round-trip, isolated to a throwaway place id.
            // The Play run itself is offline; this proves the default store still persists the
            // exact values the module claims (time01 + cycleRunning + speed) without touching
            // the real lobby_1 slot.
            string tempEnvironmentId = "play_verify_" + Guid.NewGuid().ToString("N");
            SetPrivate(_cycle, "_useHttpStore", false);
            SetPrivate(_cycle, "_persistEnvironment", true);
            _cycle.EnvironmentId = tempEnvironmentId;

            var persisted = _cycle.CaptureState();
            persisted.Time01 = 0.321f;
            persisted.SunAngle = 0.321f * 360f;
            persisted.CycleRunning = false;
            persisted.RotationSpeed = 2f;
            persisted.IsOwner = true;
            persisted.Version = EnvironmentDayNightState.StateVersion;
            _cycle.ApplyState(persisted, snap: true);

            bool saved = _cycle.SaveEnvironmentState();
            AddCheck("PlayerPrefs save", saved, $"env='{tempEnvironmentId}'");

            SetPrivate(_cycle, "_time01", 0.85f);   // clobber memory: only the store can restore it
            bool loaded = _cycle.LoadEnvironmentState();
            float loadedDelta = Mathf.Abs(Mathf.Repeat(_cycle.Time01 - 0.321f + 0.5f, 1f) - 0.5f);
            AddCheck("PlayerPrefs round-trip", loaded && loadedDelta < 0.001f && !_cycle.IsEnabled,
                $"loaded={loaded} Time01={_cycle.Time01:F4} expected=0.3210 enabled={_cycle.IsEnabled}");
            AddCheck("PlayerPrefs speed restored", Mathf.Abs(_cycle.RotationSpeed - 2f) < 0.001f,
                $"Speed={_cycle.RotationSpeed:F3}");

            string prefsKey = EnvironmentDayNightPlayerPrefsStore.KeyPrefix + tempEnvironmentId;
            PlayerPrefs.DeleteKey(prefsKey);
            PlayerPrefs.Save();
            AddCheck("PlayerPrefs cleanup", !PlayerPrefs.HasKey(prefsKey), "temp key removed");

            _cycle.EnvironmentId = string.Empty;
            SetPrivate(_cycle, "_persistEnvironment", false);

            Finish();
        }

        private bool Setup()
        {
            _cycle = DayNight_ControlPanel.BestDayNightInstance();
            if (_cycle == null)
            {
                AddInfo("setup: BestDayNightInstance() is null.");
                return false;
            }

            ActivateHierarchy(_cycle.transform);
            _cycle.enabled = true;
            _cycle.AuthorityMode = EnvironmentAuthorityMode.LocalAuthoritySimulate;

            // The card's script lives on the ControlRunner root (often inactive in the scene);
            // the visible card GameObject only holds the buttons/label. Find the component, not
            // just the UI transform, then wake its hierarchy so its Update refreshes the status.
            _panel = FindBestPanelComponent(_cycle);
            if (_panel == null)
            {
                AddInfo("setup: no DayNight_ControlPanel component found.");
                return false;
            }

            ActivateHierarchy(_panel.transform);
            _panel.EnsureRealDayNightCycle();

            Transform panelTransform = DayNight_ControlPanel.FindBestPanel();
            if (panelTransform == null)
            {
                AddInfo("setup: FindBestPanel() is null.");
                return false;
            }

            ActivateHierarchy(panelTransform);
            _status = panelTransform.Find("StatusText")?.GetComponent<Text>();
            if (_status == null) _status = GetPrivate<Text>(_panel, "_statusText");
            if (_status == null)
            {
                AddInfo($"setup: status label missing on '{panelTransform.name}'.");
                return false;
            }

            if (_cycle.Sun == null || !Meta_DayNightCycle.IsUsableSun(_cycle.Sun))
            {
                AddInfo($"setup: sun unusable ({( _cycle.Sun != null ? _cycle.Sun.name : "null")}).");
                return false;
            }

            // Belt and braces: never let this deterministic Play check read or write a real place.
            SetPrivate(_cycle, "_persistEnvironment", false);
            SetPrivate(_cycle, "_useHttpStore", false);
            _cycle.EnvironmentId = string.Empty;

            AddInfo($"setup: cycle='{_cycle.gameObject.name}' panel='{_panel.gameObject.name}' " +
                    $"status='{_status.gameObject.name}' sun='{_cycle.Sun.name}'");
            return true;
        }

        private static DayNight_ControlPanel FindBestPanelComponent(Meta_DayNightCycle cycle)
        {
            DayNight_ControlPanel best = null;
            int bestScore = int.MinValue;

            foreach (var p in FindObjectsByType<DayNight_ControlPanel>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (p == null) continue;

                int score = 0;
                if (GetPrivate<Meta_DayNightCycle>(p, "_dayNight") == cycle) score += 20;
                if (p.transform.name == DayNight_ControlPanel.RunnerName) score += 5;
                if (GetPrivate<Text>(p, "_statusText") != null) score += 3;
                if (p.GetComponent<Meta_DayNightCycle>() != null) score -= 10;   // ghost on the runner

                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            return best;
        }

        private static T GetPrivate<T>(object target, string field) where T : class
        {
            var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            return info != null ? info.GetValue(target) as T : null;
        }

        private void CheckDusk()
        {
            string phase = _cycle.DescribeSkyPhase(_cycle.Time01);
            Material sky = RenderSettings.skybox;

            AddCheck("Dusk phase reached", phase.IndexOf("dusk", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Time01={_cycle.Time01:F4} phase='{phase}'");
            AddCheck("Dusk sky assigned", sky != null, $"Sky='{Name(sky)}'");
            AddCheck("Dusk phase sky", _cycle.HasPhaseSky, $"HasPhaseSky={_cycle.HasPhaseSky}");
            AddCheck("Dusk sky changed", sky != _onSky,
                $"ON sky='{Name(_onSky)}' -> dusk sky='{Name(sky)}'");

            float min = float.MaxValue;
            float max = float.MinValue;
            int valid = 0;
            foreach (float e in _exposures)
            {
                if (e < 0f) continue;
                min = Mathf.Min(min, e);
                max = Mathf.Max(max, e);
                valid++;
            }

            if (valid >= 3)
            {
                AddCheck("Dusk exposure dip/rise", (max - min) > 0.01f,
                    $"samples={valid} min={min:F3} max={max:F3} " +
                    $"values=[{string.Join(", ", _exposures.ConvertAll(v => v.ToString("F3")))}]");
                AddCheck("Dusk never black", min > 0.01f, $"min exposure={min:F3}");
            }
            else
            {
                AddInfo("Dusk exposure skipped: the assigned sky shader has no _Exposure property.");
            }

            string status = _status != null ? _status.text ?? string.Empty : string.Empty;
            AddCheck("Dusk status ON", status.Contains("Cycle: ON"), "Status='" + OneLine(status) + "'");
        }

        private void SampleSky()
        {
            Material sky = RenderSettings.skybox;
            float exposure = -1f;
            if (sky != null && sky.HasProperty("_Exposure")) exposure = sky.GetFloat("_Exposure");
            _exposures.Add(exposure);

            Debug.Log($"[DayNightPlay] t={_cycle.Time01:F4} sky='{Name(sky)}' " +
                      $"exposure={exposure:F3} phase='{_cycle.DescribeSkyPhase(_cycle.Time01)}'");
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;

            bool pass = _failures == 0;
            AppendLine("RESULT: " + (pass ? "PASS" : "FAIL"));
            Debug.Log($"[DayNightPlay] RESULT: {(pass ? "PASS" : "FAIL")} ({_failures} failure(s))");

            // Give the file writer a frame, then leave Play. The editor verifier owns batch exit.
            StartCoroutine(ExitPlayAfterDelay(pass));
        }

        private IEnumerator ExitPlayAfterDelay(bool pass)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            if (Application.isPlaying)
                EditorApplication.ExitPlaymode();
        }

        private void AddCheck(string name, bool ok, string detail)
        {
            if (!ok) _failures++;
            AppendLine($"{(ok ? "PASS" : "FAIL")} {name} | {detail}");
        }

        private void AddFail(string name, string detail)
        {
            _failures++;
            AppendLine($"FAIL {name} | {detail}");
        }

        private void AddInfo(string detail)
        {
            AppendLine("INFO " + detail);
            Debug.Log("[DayNightPlay] " + detail);
        }

        private static void AppendLine(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
                File.AppendAllText(ResultPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the verification down.
            }
            if (!line.StartsWith("INFO ", StringComparison.Ordinal))
                Debug.Log("[DayNightPlay] " + line);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info != null) info.SetValue(target, value);
        }

        private static void ActivateHierarchy(Transform transform)
        {
            for (Transform t = transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            }
        }

        private static string Name(UnityEngine.Object obj)
        {
            return obj != null ? obj.name : "null";
        }

        private static string OneLine(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : text.Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
#endif
