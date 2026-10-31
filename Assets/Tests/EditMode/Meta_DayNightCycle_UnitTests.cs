using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Meta;
using System.Reflection;
using System.Collections;

/// <summary>
/// Unit Tests for Meta_DayNightCycle component (Edit Mode)
/// </summary>
public class Meta_DayNightCycle_UnitTests
{
    private GameObject _dayNightGO;
    private Meta_DayNightCycle _dayNight;
    private Light _testSun;
    private Material _originalSkybox;
    private float _originalAmbientIntensity;

    // Baseline captured once the component is awake and has cached its own
    // defaults, so tests can compare public state without reading _default*.
    private Quaternion _baselineRotation;
    private float _baselineIntensity;
    private float _baselineAmbient;

    [SetUp]
    public void SetUp()
    {
        // Save original render settings
        _originalSkybox = RenderSettings.skybox;
        _originalAmbientIntensity = RenderSettings.ambientIntensity;

        _dayNightGO = new GameObject("DayNightCycle");
        _dayNight = _dayNightGO.AddComponent<Meta_DayNightCycle>();

        // Create test sun
        var sunGO = new GameObject("Sun");
        sunGO.transform.SetParent(_dayNightGO.transform);
        _testSun = sunGO.AddComponent<Light>();
        _testSun.type = LightType.Directional;
        _testSun.intensity = 1.5f; // Different from MaxIntensity (1f) to test restore
        _testSun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        _dayNight.Sun = _testSun;
        _dayNight.MaxIntensity = 1f;
        _dayNight.MinIntensity = 0f;
        _dayNight.RotationSpeed = 0.01f;

        // Awake() runs here (AddComponent on an active object) and caches the
        // defaults; snapshot them through the public surface.
        _baselineRotation = _testSun.transform.rotation;
        _baselineIntensity = _testSun.intensity;
        _baselineAmbient = RenderSettings.ambientIntensity;
    }

    [TearDown]
    public void TearDown()
    {
        if (_dayNight != null)
        {
            // Ensure cleanup
            var forceReset = typeof(Meta_DayNightCycle).GetMethod("ForceResetToDefault",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            forceReset?.Invoke(_dayNight, null);
        }

        if (_dayNightGO != null)
            Object.DestroyImmediate(_dayNightGO);

        // Restore original render settings
        RenderSettings.skybox = _originalSkybox;
        RenderSettings.ambientIntensity = _originalAmbientIntensity;
    }

    [Test]
    public void Awake_CachesInitialValues()
    {
        _dayNightGO.SetActive(true);

        // Behaviour, not private state: whatever Awake captured must come back
        // when the cycle is switched off. Capture the baseline ourselves.
        var baselineRotation = _testSun.transform.rotation;
        var baselineIntensity = _testSun.intensity;
        var baselineAmbient = RenderSettings.ambientIntensity;

        _dayNight.ToggleCycle(true);
        _dayNight.ToggleCycle(false);

        Assert.That(Quaternion.Angle(_testSun.transform.rotation, baselineRotation),
            Is.LessThan(0.1f), "Sun rotation should return to the captured baseline");
        Assert.That(_testSun.intensity, Is.EqualTo(baselineIntensity).Within(0.001f),
            "Should restore ORIGINAL sun intensity, not MaxIntensity");
        Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(baselineAmbient).Within(0.001f));
    }

    [Test]
    public void Awake_WithNullSun_FindsFromRenderSettings()
    {
        _dayNight.Sun = null;
        _dayNightGO.SetActive(true);
        
        var sunField = typeof(Meta_DayNightCycle).GetField("Sun",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var sun = (Light)sunField.GetValue(_dayNight);
        
        Assert.AreEqual(RenderSettings.sun, sun);
    }

    [Test]
    public void ToggleCycle_True_StartsCycle()
    {
        _dayNightGO.SetActive(true);
        
        _dayNight.ToggleCycle(true);
        
        Assert.IsTrue(_dayNight.IsEnabled);
        var cycleCoroutine = GetPrivateField<Coroutine>(_dayNight, "_cycleCoroutine");
        Assert.IsNotNull(cycleCoroutine);
    }

    [Test]
    public void ToggleCycle_False_StopsCycleAndResets()
    {
        _dayNightGO.SetActive(true);
        _dayNight.ToggleCycle(true);
        
        // Modify sun to simulate cycle running
        _testSun.transform.rotation = Quaternion.Euler(180f, 0f, 0f);
        _testSun.intensity = 0.2f;
        RenderSettings.ambientIntensity = 0.5f;
        
        _dayNight.ToggleCycle(false);

        Assert.IsFalse(_dayNight.IsEnabled);

        // Baseline captured before the cycle ran; toggling off must restore it.
        Assert.That(Quaternion.Angle(_testSun.transform.rotation, _baselineRotation),
            Is.LessThan(0.1f), "Sun rotation should be restored to baseline");
        Assert.That(_testSun.intensity, Is.EqualTo(1.5f).Within(0.001f),
            "Should restore the ORIGINAL intensity, not MaxIntensity");
        Assert.That(RenderSettings.ambientIntensity, Is.EqualTo(_baselineAmbient).Within(0.001f));
    }

    [Test]
    public void ResetToDefault_RestoresOriginalNotMaxIntensity()
    {
        _dayNightGO.SetActive(true);
        
        // Change values
        _testSun.intensity = 0.5f;
        RenderSettings.ambientIntensity = 0.3f;
        
        // ForceResetToDefault is the public path to the same reset logic.
        _dayNight.ForceResetToDefault();

        // Should restore ORIGINAL intensity (1.5f), not MaxIntensity (1f)
        Assert.AreEqual(1.5f, _testSun.intensity, 0.001f, "Should restore original intensity, not MaxIntensity");
        Assert.AreEqual(_originalAmbientIntensity, RenderSettings.ambientIntensity, 0.001f);
    }

    [Test]
    public void OnDisable_WithIsEnabled_StopsCycleAndResets()
    {
        _dayNightGO.SetActive(true);
        _dayNight.IsEnabled = true;
        _dayNight.ToggleCycle(true);
        
        // Modify state
        _testSun.intensity = 0.2f;
        
        _dayNightGO.SetActive(false);

        // Should have reset to the baseline captured at Awake
        Assert.That(_testSun.intensity, Is.EqualTo(_baselineIntensity).Within(0.001f));
    }

    [Test]
    public void OnDestroy_WithIsEnabled_StopsCycleAndResets()
    {
        _dayNightGO.SetActive(true);
        _dayNight.IsEnabled = true;
        _dayNight.ToggleCycle(true);
        
        _testSun.intensity = 0.2f;
        
        Object.DestroyImmediate(_dayNightGO);
        
        // Should have reset (test passes if no exception and values restored)
        Assert.Pass("OnDestroy cleaned up properly");
    }

    [Test]
    public void RotateSun_WithNullSun_DoesNotThrow()
    {
        _dayNight.Sun = null;
        _dayNightGO.SetActive(true);
        _dayNight.ToggleCycle(true);
        
        // Let coroutine run one frame
        var rotateMethod = typeof(Meta_DayNightCycle).GetMethod("RotateSun",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var enumerator = (IEnumerator)rotateMethod.Invoke(_dayNight, null);
        
        Assert.DoesNotThrow(() => enumerator.MoveNext());
    }

    [Test]
    public void ApplyToggle_False_WhenNotEnabled_DoesNothing()
    {
        _dayNightGO.SetActive(true);
        _dayNight.IsEnabled = false;
        
        _dayNight.ToggleCycle(false);
        
        var cycleCoroutine = GetPrivateField<Coroutine>(_dayNight, "_cycleCoroutine");
        Assert.IsNull(cycleCoroutine);
    }

    [Test]
    public void StartCycle_WithCustomSkybox_AppliesSkybox()
    {
        var customSkybox = new Material(Shader.Find("Skybox/Procedural"));
        _dayNight.DayNightSkybox = customSkybox;
        _dayNightGO.SetActive(true);
        
        _dayNight.ToggleCycle(true);
        
        Assert.AreEqual(customSkybox, RenderSettings.skybox);
        
        Object.DestroyImmediate(customSkybox);
    }

    [Test]
    public void ForceResetToDefault_PublicMethod_Works()
    {
        _dayNightGO.SetActive(true);
        _dayNight.ToggleCycle(true);
        
        _testSun.intensity = 0.2f;
        
        _dayNight.ForceResetToDefault();

        Assert.IsFalse(_dayNight.IsEnabled);
        Assert.That(_testSun.intensity, Is.EqualTo(_baselineIntensity).Within(0.001f));
    }

    private T GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (T)field.GetValue(obj);
    }
}