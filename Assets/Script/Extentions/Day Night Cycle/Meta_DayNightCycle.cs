using System.Collections;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Day Night Cycle")]
    [HelpURL("https://google.com")]
    public class Meta_DayNightCycle : MonoBehaviour
    {
        [Header("References")]
        public Light Sun;
        public Material DayNightSkybox;

        [Header("Settings")]
        public float RotationSpeed = 0.01f; // degrees per second
        public float MaxIntensity = 1f;
        public float MinIntensity = 0f; // night
        public bool IsEnabled = false;

        private Quaternion DefaultSunRotation;
        private Material DefaultSkybox;
        private Coroutine CycleCoroutine;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        private void Awake()
        {
            if (Sun == null)
            {
                Sun = RenderSettings.sun;
                if (Sun == null && EnableLog)
                    Debug.LogWarning("[Meta] No Sun Assigned");
            }

            if (Sun != null)
                DefaultSunRotation = Sun.transform.rotation;

            DefaultSkybox = RenderSettings.skybox;
        }

        private void OnEnable()
        {
            ApplyToggle();
        }

        public void ToggleCycle(bool _Enable)
        {
            IsEnabled = _Enable;
            ApplyToggle();
        }

        private void ApplyToggle()
        {
            if (IsEnabled)
            {
                StartCycle();
            }
            else
            {
                StopCycle();
                ResetToDefault();
            }
        }

        private void StartCycle()
        {
            if (CycleCoroutine != null)
                StopCoroutine(CycleCoroutine);

            if (DayNightSkybox != null)
                RenderSettings.skybox = DayNightSkybox;

            if (Sun != null)
                CycleCoroutine = StartCoroutine(RotateSun());
        }

        private void StopCycle()
        {
            if (CycleCoroutine != null)
            {
                StopCoroutine(CycleCoroutine);
                CycleCoroutine = null;
            }
        }

        private IEnumerator RotateSun()
        {
            while (true)
            {
                if (Sun != null)
                {
                    // Rotate around the X axis to simulate rising/setting
                    Sun.transform.Rotate(Vector3.right * (RotationSpeed * Time.deltaTime));

                    // Adjust intensity based on the angle relative to the horizon
                    float _Dot = Vector3.Dot(Sun.transform.forward, Vector3.down);

                    // _Dot = 1 when directly above, -1 when below horizon
                    float _T = Mathf.InverseLerp(-0.1f, 0.4f, _Dot);
                    float _Intensity = Mathf.Lerp(MinIntensity, MaxIntensity, _T);

                    Sun.intensity = _Intensity;

                    // Optional: slightly change ambient lighting too
                    RenderSettings.ambientIntensity = Mathf.Lerp(0.2f, 1f, _T);
                }

                yield return null;
            }
        }

        private void ResetToDefault()
        {
            if (Sun != null)
            {
                Sun.transform.rotation = DefaultSunRotation;
                Sun.intensity = MaxIntensity; // restore full daylight
            }

            RenderSettings.skybox = DefaultSkybox;
            DynamicGI.UpdateEnvironment();
        }
    }
}
