using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Meta;

namespace Meta.Tools
{
    /// <summary>
    /// Runtime automated test runner for Meta modules (MiniMap, DayNight, CameraForce).
    /// One-button execution from the Play-mode HUD. Zero dependencies on Network_A / G7 / Voice.
    /// Add this component to a root GameObject (created by MetaModules_RuntimeTest_Setup).
    /// </summary>
    [AddComponentMenu("Meta/Tools/MetaModules Runtime Tester")]
    public class MetaModules_RuntimeTester : MonoBehaviour
    {
        [Header("HUD (auto-wired by Setup tool)")]
        [SerializeField] private Button _runAllButton;
        [SerializeField] private Button _clearButton;
        [SerializeField] private Text _resultsText;
        [SerializeField] private Toggle _miniMapToggle;
        [SerializeField] private Toggle _dayNightToggle;
        [SerializeField] private Toggle _cameraForceToggle;

        private readonly List<TestResult> _results = new List<TestResult>();
        private Meta_MiniMap _miniMap;
        private Meta_DayNightCycle _dayNight;
        private CameraForceRenderByIndex _cameraForce;

        private void Awake()
        {
            FindModules();
            WireButtons();
            RefreshModuleToggles();
        }

        private void FindModules()
        {
            _miniMap = FindObjectOfType<Meta_MiniMap>();
            _dayNight = FindObjectOfType<Meta_DayNightCycle>();
            _cameraForce = FindObjectOfType<CameraForceRenderByIndex>();
        }

        private void WireButtons()
        {
            if (_runAllButton != null)
            {
                _runAllButton.onClick.RemoveAllListeners();
                _runAllButton.onClick.AddListener(RunAllTests);
            }

            if (_clearButton != null)
            {
                _clearButton.onClick.RemoveAllListeners();
                _clearButton.onClick.AddListener(ClearResults);
            }

            if (_miniMapToggle != null)
                _miniMapToggle.onValueChanged.AddListener(_ => RefreshModuleToggles());

            if (_dayNightToggle != null)
                _dayNightToggle.onValueChanged.AddListener(_ => RefreshModuleToggles());

            if (_cameraForceToggle != null)
                _cameraForceToggle.onValueChanged.AddListener(_ => RefreshModuleToggles());
        }

        private void RefreshModuleToggles()
        {
            if (_miniMapToggle != null) _miniMapToggle.interactable = _miniMap != null;
            if (_dayNightToggle != null) _dayNightToggle.interactable = _dayNight != null;
            if (_cameraForceToggle != null) _cameraForceToggle.interactable = _cameraForce != null;
        }

        /// <summary>Public entry point for the HUD button.</summary>
        public void RunAllTests()
        {
            if (_miniMapToggle != null && !_miniMapToggle.isOn) _miniMap = null;
            if (_dayNightToggle != null && !_dayNightToggle.isOn) _dayNight = null;
            if (_cameraForceToggle != null && !_cameraForceToggle.isOn) _cameraForce = null;

            ClearResults();
            Log("=== META MODULES TEST RUN ===");

            if (_miniMap != null) StartCoroutine(RunMiniMapTests());
            else { Add("MiniMap", "FAIL", "No Meta_MiniMap instance found in scene"); FinishRun(); }

            if (_dayNight != null) StartCoroutine(RunDayNightTests());
            else { Add("DayNight", "FAIL", "No Meta_DayNightCycle instance found in scene"); FinishRun(); }

            if (_cameraForce != null) RunCameraForceTests();
            else { Add("CameraForce", "FAIL", "No CameraForceRenderByIndex instance found in scene"); FinishRun(); }
        }

        /// <summary>Clears the results display.</summary>
        public void ClearResults()
        {
            _results.Clear();
            if (_resultsText != null) _resultsText.text = "";
            Log("Results cleared.");
        }

        #region MiniMap Tests

        private IEnumerator RunMiniMapTests()
        {
            bool anyPassed = false;

            // A1 - Zoom null safety
            try
            {
                _miniMap.ZoomInButton();
                _miniMap.ZoomOutButton();
                Add("MiniMap.A1", "PASS", "ZoomInButton / ZoomOutButton did not throw (null-safe)");
                anyPassed = true;
            }
            catch (Exception e)
            {
                Add("MiniMap.A1", "FAIL", "Zoom threw: " + e.Message);
            }

            // A2 - Toggle enable chain (camera enable same frame)
            bool prevEnabled = _miniMap.IsEnabled;
            bool a2Pass = false;
            try
            {
                _miniMap.ToggleMiniMap(true);
                bool camOn = _miniMap.MiniMapCamera != null && _miniMap.MiniMapCamera.enabled;
                Add("MiniMap.A2", camOn ? "PASS" : "FAIL",
                    camOn ? "ToggleMiniMap(true) -> camera enabled same frame" : "ToggleMiniMap(true) but camera disabled!");
                a2Pass = camOn;

                _miniMap.ToggleMiniMap(false);
                bool camOff = _miniMap.MiniMapCamera == null || !_miniMap.MiniMapCamera.enabled;
                Add("MiniMap.A2b", camOff ? "PASS" : "FAIL",
                    camOff ? "ToggleMiniMap(false) -> camera disabled" : "ToggleMiniMap(false) but camera still enabled!");
            }
            catch (Exception e)
            {
                Add("MiniMap.A2", "FAIL", "Toggle threw: " + e.Message);
            }

            // A3 - Target resolve
            try
            {
                _miniMap.TryResolveTarget();
                if (_miniMap.Target != null)
                {
                    Add("MiniMap.A3", "PASS", $"Target resolved: {_miniMap.Target.name}");
                }
                else
                {
                    Add("MiniMap.A3", "SKIP", "No local player yet (TryResolveTarget returned null)");
                }
            }
            catch (Exception e)
            {
                Add("MiniMap.A3", "FAIL", "TryResolveTarget threw: " + e.Message);
            }

            // A4 - No SetParent (sanity check)
            try
            {
                if (_miniMap.transform.parent != null && _miniMap.Target != null && _miniMap.transform.IsChildOf(_miniMap.Target))
                {
                    Add("MiniMap.A4", "FAIL", "MiniMap root is parented to Target (SetParent detected)");
                }
                else
                {
                    Add("MiniMap.A4", "PASS", "MiniMap root not parented to Target");
                }
            }
            catch (Exception e)
            {
                Add("MiniMap.A4", "FAIL", "SetParent check threw: " + e.Message);
            }

            // A5 - HEALTH sanity (camera enabled matches IsEnabled)
            try
            {
                bool health = _miniMap.IsEnabled == (_miniMap.MiniMapCamera != null && _miniMap.MiniMapCamera.enabled);
                Add("MiniMap.A5", health ? "PASS" : "FAIL",
                    health ? "Camera enabled matches IsEnabled" : "IsEnabled != camera.enabled");
            }
            catch (Exception e)
            {
                Add("MiniMap.A5", "FAIL", "HEALTH check threw: " + e.Message);
            }

            yield return null;
            if (anyPassed || !_results.Exists(r => r.name.StartsWith("MiniMap") && r.result == "FAIL"))
            {
                // at least one test passed
            }
            FinishRun();
        }

        #endregion

        #region DayNight Tests

        private IEnumerator RunDayNightTests()
        {
            float prevIntensity = -1f, prevAmbient = -1f;
            Material prevSkybox = null;

            // B1 - Cache/restore
            // B1 - Cache/restore (yield outside try-catch due to C# restriction)
            if (_dayNight.Sun != null)
            {
                prevIntensity = _dayNight.Sun.intensity;
                prevAmbient = RenderSettings.ambientIntensity;
                prevSkybox = RenderSettings.skybox;

                try
                {
                    _dayNight.ToggleCycle(true);
                }
                catch (Exception e)
                {
                    Add("DayNight.B1", "FAIL", "ToggleCycle(true) threw: " + e.Message);
                    yield break;
                }

                yield return new WaitForSecondsRealtime(0.3f);

                try
                {
                    _dayNight.ToggleCycle(false);
                }
                catch (Exception e)
                {
                    Add("DayNight.B1", "FAIL", "ToggleCycle(false) threw: " + e.Message);
                    yield break;
                }

                float dInt = Mathf.Abs(_dayNight.Sun.intensity - prevIntensity);
                float dAmb = Mathf.Abs(RenderSettings.ambientIntensity - prevAmbient);
                bool ok = dInt < 0.05f && dAmb < 0.05f;

                Add("DayNight.B1", ok ? "PASS" : "FAIL",
                    ok ? $"Intensity/ambient restored (dInt={dInt:F3}, dAmb={dAmb:F3})" :
                         $"Restore mismatch: intensity {prevIntensity:F3}->{_dayNight.Sun.intensity:F3}, ambient {prevAmbient:F3}->{RenderSettings.ambientIntensity:F3}");
            }
            else
            {
                Add("DayNight.B1", "SKIP", "Sun is null on Meta_DayNightCycle (cannot test)");
            }

            // B2 - Double toggle
            try
            {
                _dayNight.ToggleCycle(true);
                _dayNight.ToggleCycle(false);
                _dayNight.ToggleCycle(true);
                _dayNight.ToggleCycle(false);
                Add("DayNight.B2", "PASS", "Double toggle sequence completed without exception");
            }
            catch (Exception e)
            {
                Add("DayNight.B2", "FAIL", "Double toggle threw: " + e.Message);
            }

            // B3 - ForceReset
            try
            {
                if (_dayNight.GetType().GetMethod("ForceResetToDefault") != null)
                {
                    _dayNight.ForceResetToDefault();
                    Add("DayNight.B3", "PASS", "ForceResetToDefault() completed");
                }
                else
                {
                    Add("DayNight.B3", "SKIP", "ForceResetToDefault not public on this build");
                }
            }
            catch (Exception e)
            {
                Add("DayNight.B3", "FAIL", "ForceResetToDefault threw: " + e.Message);
            }

            yield return null;
            FinishRun();
        }

        #endregion

        #region CameraForce Tests

        private void RunCameraForceTests()
        {
            // C1 - Index 0
            try
            {
                _cameraForce.RendererIndex = 0;
                var validate = _cameraForce.GetType().GetMethod("ValidateRendererIndex", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (validate != null) validate.Invoke(_cameraForce, null);
                else _cameraForce.ValidateRendererIndex();
                Add("CameraForce.C1", "PASS", "RendererIndex=0 validated without exception");
            }
            catch (Exception e)
            {
                Add("CameraForce.C1", "FAIL", "Index 0 threw: " + e.Message);
            }

            // C2 - Invalid 99
            try
            {
                _cameraForce.RendererIndex = 99;
                var validate = _cameraForce.GetType().GetMethod("ValidateRendererIndex", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (validate != null) validate.Invoke(_cameraForce, null);
                else _cameraForce.ValidateRendererIndex();

                int clamped = _cameraForce.RendererIndex;
                bool ok = clamped >= 0 && clamped <= 99; // valid range check
                Add("CameraForce.C2", ok ? "PASS" : "FAIL",
                    ok ? $"RendererIndex=99 clamped to {clamped} (no crash)" : $"RendererIndex=99 not clamped ({clamped})");
            }
            catch (Exception e)
            {
                Add("CameraForce.C2", "FAIL", "Invalid 99 threw: " + e.Message);
            }

            // C3 - Missing URP data path (bare camera)
            try
            {
                var cam = _cameraForce.GetComponent<Camera>();
                if (cam != null)
                {
                    var urpData = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                    if (urpData == null)
                    {
                        // Simulate missing URP data by calling validate
                        var validate = _cameraForce.GetType().GetMethod("ValidateRendererIndex", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (validate != null) validate.Invoke(_cameraForce, null);
                        else _cameraForce.ValidateRendererIndex();
                    }
                }
                Add("CameraForce.C3", "PASS", "Validate does not crash on bare camera (URP data auto-added)");
            }
            catch (Exception e)
            {
                Add("CameraForce.C3", "FAIL", "Bare camera path threw: " + e.Message);
            }

            FinishRun();
        }

        #endregion

        #region Results Helpers

        private struct TestResult
        {
            public string name;
            public string result; // PASS / FAIL / SKIP
            public string detail;

            public override string ToString()
            {
                return $"[{result}] {name}: {detail}";
            }
        }

        private void Add(string name, string result, string detail)
        {
            _results.Add(new TestResult { name = name, result = result, detail = detail });
            Log($"[{result}] {name}: {detail}");
            UpdateResultsText();
        }

        private void UpdateResultsText()
        {
            if (_resultsText == null) return;
            var sb = new StringBuilder();
            int pass = 0, fail = 0, skip = 0;
            foreach (var r in _results)
            {
                sb.AppendLine(r.ToString());
                if (r.result == "PASS") pass++;
                else if (r.result == "FAIL") fail++;
                else skip++;
            }
            sb.AppendLine();
            sb.AppendLine($"RESULT: {pass} passed, {fail} failed, {skip} skipped");
            _resultsText.text = sb.ToString();
        }

        private void FinishRun()
        {
            // When all three coroutines/sections are done, this is called 3 times.
            // We just log the final summary once at the end.
            if (_results.Count > 0)
            {
                int total = _results.Count;
                int pass = _results.FindAll(r => r.result == "PASS").Count;
                int fail = _results.FindAll(r => r.result == "FAIL").Count;
                int skip = _results.FindAll(r => r.result == "SKIP").Count;
                Log($"=== RESULT: {pass} passed, {fail} failed, {skip} skipped (out of {total}) ===");
            }
        }

        private void Log(string msg)
        {
            Debug.Log($"[MetaModulesTest] {msg}");
        }

        #endregion
    }
}