using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Rendering;
using Meta;
using System.Collections;

/// <summary>
/// Play Mode Integration Tests for Meta_DayNightCycle
/// </summary>
public class DayNightCycle_IntegrationTests
{
    private GameObject _dayNightGO;
    private Meta_DayNightCycle _dayNight;
    private Light _sun;
    private Material _originalSkybox;
    private float _originalAmbientIntensity;

    [SetUp]
    public void SetUp()
    {
        _originalSkybox = RenderSettings.skybox;
        _originalAmbientIntensity = RenderSettings.ambientIntensity;

        _dayNightGO = new GameObject("DayNightCycle");
        _dayNight = _dayNightGO.AddComponent<Meta_DayNightCycle>();

        var sunGO = new GameObject("Sun");
        sunGO.transform.SetParent(_dayNightGO.transform);
        _sun = sunGO.AddComponent<Light>();
        _sun.type = LightType.Directional;
        _sun.intensity = 1.2f;
        _sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        _dayNight.Sun = _sun;
        _dayNight.MaxIntensity = 1f;
        _dayNight.MinIntensity = 0f;
        _dayNight.RotationSpeed = 10f;
    }

    [TearDown]
    public void TearDown()
    {
        if (_dayNight != null)
        {
            var forceReset = typeof(Meta_DayNightCycle).GetMethod("ForceResetToDefault",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            forceReset?.Invoke(_dayNight, null);
        }

        if (_dayNightGO != null)
            Object.DestroyImmediate(_dayNightGO);

        RenderSettings.skybox = _originalSkybox;
        RenderSettings.ambientIntensity = _originalAmbientIntensity;
    }

    [UnityTest]
    public IEnumerator ToggleCycle_UIButton_OnOff_SunRotatesAndResets()
    {
        _dayNightGO.SetActive(true);
        yield return null;

        var originalRotation = _sun.transform.rotation;
        var originalIntensity = _sun.intensity;
        var originalAmbient = RenderSettings.ambientIntensity;

        _dayNight.ToggleCycle(true);
        yield return null;

        Assert.IsTrue(_dayNight.IsEnabled);

        yield return new WaitForSeconds(0.5f);

        Assert.AreNotEqual(originalRotation, _sun.transform.rotation);
        Assert.AreNotEqual(originalIntensity, _sun.intensity);

        _dayNight.ToggleCycle(false);
        yield return null;

        Assert.IsFalse(_dayNight.IsEnabled);
        Assert.IsTrue(Quaternion.Angle(originalRotation, _sun.transform.rotation) < 0.1f, "Sun rotation should be restored");
        Assert.AreEqual(originalIntensity, _sun.intensity, 0.01f);
        Assert.AreEqual(originalAmbient, RenderSettings.ambientIntensity, 0.01f);
    }

    [UnityTest]
    public IEnumerator QualityLevelChange_DayNightContinuesWorking()
    {
        _dayNightGO.SetActive(true);
        yield return null;

        _dayNight.ToggleCycle(true);
        yield return null;

        QualitySettings.SetQualityLevel(Mathf.Min(1, QualitySettings.names.Length - 1), true);
        yield return null;

        Assert.IsTrue(_dayNight.IsEnabled);
        var coroutine = GetPrivateField<Coroutine>(_dayNight, "_cycleCoroutine");
        Assert.IsNotNull(coroutine);
    }

    [UnityTest]
    public IEnumerator PauseResume_TimeScaleZero_CyclePausesCorrectly()
    {
        _dayNightGO.SetActive(true);
        yield return null;

        _dayNight.ToggleCycle(true);
        yield return null;

        var rotationBeforePause = _sun.transform.rotation;

        Time.timeScale = 0f;
        yield return new WaitForSecondsRealtime(0.2f);

        Assert.AreEqual(rotationBeforePause, _sun.transform.rotation);

        Time.timeScale = 1f;
        yield return new WaitForSeconds(0.2f);

        Assert.AreNotEqual(rotationBeforePause, _sun.transform.rotation);
        Time.timeScale = 1f;
    }

    [UnityTest]
    public IEnumerator DisableGameObject_ThenEnable_CycleRestartsCleanly()
    {
        _dayNightGO.SetActive(true);
        yield return null;

        _dayNight.ToggleCycle(true);
        yield return null;

        yield return new WaitForSeconds(0.2f);

        _dayNightGO.SetActive(false);
        yield return null;

        _dayNightGO.SetActive(true);
        yield return null;

        Assert.IsTrue(_dayNight.IsEnabled);
        var coroutine = GetPrivateField<Coroutine>(_dayNight, "_cycleCoroutine");
        Assert.IsNotNull(coroutine);
    }

    [UnityTest]
    public IEnumerator DestroyGameObject_WhileRunning_NoLeaks()
    {
        _dayNightGO.SetActive(true);
        yield return null;

        _dayNight.ToggleCycle(true);
        yield return null;

        // Capture expected values before destruction
        var expectedRotation = _dayNight.DefaultSunRotation;
        var expectedIntensity = _dayNight.DefaultSunIntensity;
        var expectedAmbient = _dayNight.DefaultAmbientIntensity;

        Object.DestroyImmediate(_dayNightGO);
        yield return null;

        Assert.IsTrue(Quaternion.Angle(expectedRotation, _sun.transform.rotation) < 0.1f);
        Assert.AreEqual(expectedIntensity, _sun.intensity, 0.01f);
        Assert.AreEqual(expectedAmbient, RenderSettings.ambientIntensity, 0.01f);
    }

    [UnityTest]
    public IEnumerator RapidToggle_OnOffOnOff_StableNoLeaks()
    {
        _dayNightGO.SetActive(true);
        yield return null;

        for (int i = 0; i < 20; i++)
        {
            _dayNight.ToggleCycle(true);
            yield return null;
            _dayNight.ToggleCycle(false);
            yield return null;
        }

        Assert.IsFalse(_dayNight.IsEnabled);
        Assert.IsTrue(Quaternion.Angle(_dayNight.DefaultSunRotation, _sun.transform.rotation) < 0.1f);
        Assert.AreEqual(_dayNight.DefaultSunIntensity, _sun.intensity, 0.01f);
        Assert.AreEqual(_dayNight.DefaultAmbientIntensity, RenderSettings.ambientIntensity, 0.01f);
        
        var coroutine = GetPrivateField<Coroutine>(_dayNight, "_cycleCoroutine");
        Assert.IsNull(coroutine);
    }

    [UnityTest]
    public IEnumerator CustomSkybox_AppliedOnEnable_RestoredOnDisable()
    {
        var customSkybox = new Material(Shader.Find("Skybox/Procedural"));
        customSkybox.SetColor("_SkyTint", Color.red);
        
        _dayNight.DayNightSkybox = customSkybox;
        _dayNightGO.SetActive(true);
        yield return null;

        var defaultSkybox = RenderSettings.skybox;

        _dayNight.ToggleCycle(true);
        yield return null;
        Assert.AreEqual(customSkybox, RenderSettings.skybox);

        _dayNight.ToggleCycle(false);
        yield return null;
        Assert.AreEqual(defaultSkybox, RenderSettings.skybox);

        Object.DestroyImmediate(customSkybox);
    }

    private T GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (T)field.GetValue(obj);
    }
}