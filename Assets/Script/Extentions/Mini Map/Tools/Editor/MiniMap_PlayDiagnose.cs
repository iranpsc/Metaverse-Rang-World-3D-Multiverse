#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Meta.Tools;

namespace Meta.EditorTools
{
    /// <summary>
    /// Play-mode self-test for the MiniMap module (menu: Tools/Meta/MiniMap/Diagnose Play).
    ///
    /// In Edit mode it first runs the normal Setup (repair) against the ACTIVE scene, saves the
    /// scene, then enters Play and samples the live rig every few seconds:
    ///   - component refs + live state (IsEnabled, Target, camera, RT link, panel alpha)
    ///   - forced ToggleMiniMap(true) -> panel visible?
    ///   - forced ToggleMiniMap(false) -> panel hidden?
    ///   - forced ToggleMiniMap(true) again -> panel restored? (the "map may vanish" bug)
    /// It exits Play automatically and writes the collected evidence to
    /// %TEMP%/MetaMiniMapDiagnoseReport.txt (plus normal Debug.Log lines, one per check).
    ///
    /// Headless/automation support: writing the marker file
    ///   &lt;project&gt;/Temp/MetaMiniMapDiagnose.trigger
    /// makes the next editor tick consume it and run one automatic diagnosis (used by pipelines
    /// that fix + play + verify without touching the menu).
    /// </summary>
    [InitializeOnLoad]
    public static class MiniMap_PlayDiagnose
    {
        private const string TriggerFileName = "MetaMiniMapDiagnose.trigger";

        private static Diagnosis _active;

        static MiniMap_PlayDiagnose()
        {
            EditorApplication.update += Tick;
        }

        [MenuItem("Tools/Meta/MiniMap/Diagnose Play", priority = 130)]
        public static void MenuRun()
        {
            if (Application.isPlaying)
            {
                Debug.LogWarning("[MiniMap] Diagnose Play: already playing - exit Play first for a clean run.");
                return;
            }
            Start("menu");
        }

        private static void Tick()
        {
            if (_active != null)
            {
                _active.Pump();
                return;
            }

            // Trigger-file support: one automatic run after the next editor refresh.
            string path = TriggerPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { File.Delete(path); } catch { /* consumed on best effort */ }

            if (Application.isPlaying)
            {
                Debug.LogWarning("[MiniMap] Diagnose trigger seen while playing - ignored (exit Play first).");
                return;
            }
            Start("trigger");
        }

        private static string TriggerPath()
        {
            try
            {
                string project = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(project, "Temp", TriggerFileName);
            }
            catch
            {
                return null;
            }
        }

        internal static void Start(string reason)
        {
            if (_active != null) return;

            Debug.Log($"[MiniMap] Diagnose Play started (reason={reason}) - setup + save scene, then Play.");
            _active = new Diagnosis();

            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    Debug.LogWarning("[MiniMap] Diagnose aborted: play mode was already changing.");
                    _active.Cancel();
                    _active = null;
                    return;
                }

                try
                {
                    MiniMap_TestRigCreator.CreateTestRig();       // repair the ACTIVE scene
                    if (!Application.isPlaying)
                        EditorSceneManager.SaveOpenScenes();      // the fix must survive the Play run
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MiniMap] Diagnose setup failed: " + ex.Message);
                }
                finally
                {
                    EditorApplication.isPlaying = true;
                }
            };
        }

        private sealed class Diagnosis
        {
            private readonly List<string> _report = new List<string>();
            private double _nextSample;
            private double _deadline;
            private double _startedAt;
            private int _stage;
            private bool _exiting;
            private bool _cancelled;

            public void Cancel()
            {
                _cancelled = true;
                WriteReport("CANCELLED before Play could start.");
            }

            public void Pump()
            {
                // Play exited -> release the runner so the tool can run again later.
                if (_exiting && !EditorApplication.isPlaying)
                {
                    _exiting = false;
                    _cancelled = true;
                    MiniMap_PlayDiagnose._active = null;
                    return;
                }
                if (_cancelled || _exiting) return;
                if (EditorApplication.isCompiling) return;
                if (!EditorApplication.isPlaying)
                {
                    // Play entry refused for too long (compile error / modal dialog) -> bail out.
                    if (_startedAt > 0 && EditorApplication.timeSinceStartup - _startedAt > 60.0)
                    {
                        Finish("Play did not start within 60 s (see Console for errors).");
                    }
                    return;
                }

                double now = EditorApplication.timeSinceStartup;
                if (_startedAt <= 0.0)
                {
                    _startedAt = now;
                    _nextSample = now + 3.0;
                    _deadline = now + 30.0;
                    _report.Add($"{DateTime.Now:HH:mm:ss} PLAY ENTERED - sampling every 3 s until t+30 s");
                    Debug.Log("[MiniMap] Diagnose: play mode entered.");
                    return;
                }

                if (now >= _deadline) { Finish("timeout"); return; }
                if (now < _nextSample) return;

                _nextSample = now + 3.0;
                Sample(_stage++);
            }

            // ------------------------------------------------------------------ stages
            private void Sample(int stage)
            {
                double t = EditorApplication.timeSinceStartup - _startedAt;
                string head = $"t+{t:F1}s stage{stage}";

                switch (stage)
                {
                    case 0:
                    {
                        // Observe the natural state first (what Play actually produced).
                        var lines = DumpAll();
                        _report.Add($"{head}: {lines}");
                        Debug.Log($"[MiniMap] Diagnose {head}: {lines}");
                        break;
                    }
                    case 1:
                    {
                        // Force ON (user pressed the ON chip equivalent) if not already on.
                        var rig = BestRig();
                        bool was = rig != null && rig.IsEnabled;
                        if (rig != null && !was) rig.ToggleMiniMap(true);
                        var after = DumpAll();
                        _report.Add($"{head}: forced ON (was {was}); {after}");
                        Debug.Log($"[MiniMap] Diagnose {head}: forced ON (was {was}); {after}");
                        break;
                    }
                    case 2:
                    {
                        // Verify the ON state is fully assembled (panel + camera + RT link).
                        var rig = BestRig();
                        bool ok = rig != null && rig.IsEnabled && PanelVisible(rig) && CameraOn(rig);
                        _report.Add($"{head}: ON check -> {(ok ? "PASS" : "FAIL")}; {DumpAll()}");
                        Debug.Log($"[MiniMap] Diagnose {head}: ON check = {(ok ? "PASS" : "FAIL")}; {DumpAll()}");

                        if (ok)
                        {
                            rig.ToggleMiniMap(false);   // now the OFF path
                            _report.Add($"{head}: forced OFF");
                            Debug.Log($"[MiniMap] Diagnose {head}: forced OFF.");
                        }
                        break;
                    }
                    case 3:
                    {
                        var rig = BestRig();
                        bool hidden = rig == null || !rig.IsEnabled || !PanelVisible(rig);
                        _report.Add($"{head}: OFF check -> {(hidden ? "PASS" : "FAIL")}; {DumpAll()}");
                        Debug.Log($"[MiniMap] Diagnose {head}: OFF check = {(hidden ? "PASS" : "FAIL")}; {DumpAll()}");

                        _report.Add($"{head}: forcing ON again (restore test)");
                        Debug.Log($"[MiniMap] Diagnose {head}: forcing ON again (restore test).");
                        if (rig != null) rig.ToggleMiniMap(true);
                        break;
                    }
                    default:
                    {
                        var rig = BestRig();
                        bool restored = rig != null && rig.IsEnabled && PanelVisible(rig) && CameraOn(rig);
                        _report.Add($"{head}: RESTORE check -> {(restored ? "PASS" : "FAIL")}; {DumpAll()}");
                        Debug.Log($"[MiniMap] Diagnose {head}: RESTORE check = {(restored ? "PASS" : "FAIL")}; {DumpAll()}");
                        Finish(restored ? "done (restore PASS)" : "done (restore FAIL)");
                        break;
                    }
                }
            }

            private void Finish(string reason)
            {
                if (_exiting) return;
                _exiting = true;
                _report.Add($"FINISHED: {reason} - exiting Play.");
                WriteReport($"last stage: {reason}");
                Debug.Log("[MiniMap] Diagnose finished - exiting Play.");
                EditorApplication.delayCall += () =>
                {
                    if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
                };
            }

            // ------------------------------------------------------------------ state probes
            private static Meta_MiniMap BestRig() => MiniMap_ControlPanel.BestMiniMapInstance();

            private static bool PanelVisible(Meta_MiniMap map)
            {
                var go = map.MiniMap;
                if (go == null) return false;
                var group = go.GetComponent<CanvasGroup>();
                return go.activeInHierarchy && (group == null || group.alpha > 0.5f);
            }

            private static bool CameraOn(Meta_MiniMap map)
                => map.MiniMapCamera != null && map.MiniMapCamera.enabled;

            private static string DumpAll()
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (var map in UnityEngine.Object.FindObjectsByType<Meta_MiniMap>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!first) sb.Append(" || ");
                    first = false;
                    sb.Append(DumpOne(map));
                }
                if (first) sb.Append("Meta_MiniMap: NONE IN PLAYING SCENE");
                return sb.ToString();
            }

            private static string DumpOne(Meta_MiniMap map)
            {
                if (map == null) return "null";

                var panelObj = map.MiniMap;
                string panelInfo = "null";
                if (panelObj != null)
                {
                    var group = panelObj.GetComponent<CanvasGroup>();
                    float alpha = group != null ? group.alpha : 1f;
                    panelInfo = $"active={panelObj.activeSelf} inHierarchy={panelObj.activeInHierarchy} alpha={alpha:F2}";
                }

                string camInfo = map.MiniMapCamera != null
                    ? $"on={map.MiniMapCamera.enabled} tex={(map.MiniMapCamera.targetTexture != null ? map.MiniMapCamera.targetTexture.name : "null")}"
                    : "NULL";

                string imageInfo = map.MiniMapRenderImage != null
                    ? (map.MiniMapRenderImage.texture != null ? map.MiniMapRenderImage.texture.name : "texture=null")
                    : "NULL";

                string ghost = MiniMap_ControlPanel.IsRigCandidate(map) ? "" : " (GHOST - no rig refs)";

                return $"go='{map.gameObject.name}' enabled={map.IsEnabled} " +
                       $"target={(map.Target != null ? map.Target.name : "null")} cam={camInfo} " +
                       $"image={imageInfo} panel={panelInfo} platform={map.CurrentPlatform} " +
                       $"citizen='{map.CitizenId}' env='{map.EnvironmentId}'{ghost}";
            }

            // ------------------------------------------------------------------ report file
            private void WriteReport(string tail)
            {
                try
                {
                    string path = Path.Combine(Path.GetTempPath(), "MetaMiniMapDiagnoseReport.txt");
                    var lines = new List<string>(_report) { tail };
                    File.WriteAllLines(path, lines, Encoding.UTF8);
                    Debug.Log($"[MiniMap] Diagnose report written: {path}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MiniMap] Diagnose report could not be written: {ex.Message}");
                }
            }
        }
    }
}
#endif
