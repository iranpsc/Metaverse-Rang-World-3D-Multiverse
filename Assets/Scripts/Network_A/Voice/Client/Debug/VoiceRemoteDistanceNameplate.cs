using System.Globalization;
using TMPro;
using UnityEngine;

namespace Network_A.Voice.Client.Diagnostics
{
    [DisallowMultipleComponent]
    public sealed class VoiceRemoteDistanceNameplate : MonoBehaviour
    {
        [Header("Direct Binding")]
        [SerializeField] private TMP_Text distanceText;
        [SerializeField] private Transform ownerPlayerRoot;
        [SerializeField] private Transform localPlayerRoot;

        [Header("Scene Names")]
        [SerializeField] private string localPlayerNamePrefix = "Local_Player";
        [SerializeField] private string remotePlayerNamePrefix = "Remote_Player";
        [SerializeField] private string distanceTextObjectName = "Text_Distance";

        [Header("Display")]
        [SerializeField] private float updateIntervalSeconds = 0.1f;
        [SerializeField] private int decimalDigits = 2;
        [SerializeField] private string meterSuffix = " m";
        [SerializeField] private bool logBindingResult = true;

        private float nextUpdateRealtime;
        private bool bindingPassLogged;
        private bool waitingLogged;

        //* این تابع هنگام آماده‌شدن آبجکت، اتصال متن فاصله و ریشه بازیکن را بدون تغییر فعال‌بودن آبجکت‌ها انجام می‌دهد.
        private void Awake()
        {
            BindRequiredReferences();
        }

        //* این تابع پس از فعال‌شدن آبجکت، زمان به‌روزرسانی را از نو تنظیم می‌کند.
        private void OnEnable()
        {
            nextUpdateRealtime = 0.0f;
            BindRequiredReferences();
        }

        //* این تابع در بازه کوتاه ثابت، فاصله بازیکن محلی تا همین بازیکن ریموت را داخل متن موجود می‌نویسد.
        private void Update()
        {
            if (Time.realtimeSinceStartup < nextUpdateRealtime) return;

            float safeInterval = Mathf.Max(0.02f, updateIntervalSeconds);
            nextUpdateRealtime = Time.realtimeSinceStartup + safeInterval;

            BindRequiredReferences();

            if (distanceText == null) return;

            if (ownerPlayerRoot == null || localPlayerRoot == null)
            {
                distanceText.text = string.Empty;
                LogWaitingOnce(
                    "missing_reference" +
                    " | ownerRoot=" + (ownerPlayerRoot != null) +
                    " | localRoot=" + (localPlayerRoot != null) +
                    " | distanceText=" + (distanceText != null));
                return;
            }

            string ownerName = ownerPlayerRoot.name ?? string.Empty;
            if (!ownerName.StartsWith(remotePlayerNamePrefix, System.StringComparison.Ordinal))
            {
                distanceText.text = string.Empty;
                return;
            }

            float distanceMeters = Vector3.Distance(
                localPlayerRoot.position,
                ownerPlayerRoot.position);

            if (float.IsNaN(distanceMeters) || float.IsInfinity(distanceMeters))
            {
                distanceText.text = string.Empty;
                LogWaitingOnce("invalid_distance");
                return;
            }

            int safeDigits = Mathf.Clamp(decimalDigits, 0, 3);
            distanceText.text =
                distanceMeters.ToString("F" + safeDigits, CultureInfo.InvariantCulture) +
                meterSuffix;

            if (logBindingResult && !bindingPassLogged)
            {
                bindingPassLogged = true;
                waitingLogged = false;

                UnityEngine.Debug.Log(
                    "VOICE_G3_DISTANCE_NAMEPLATE_BINDING=PASS" +
                    " | object=" + gameObject.name +
                    " | ownerRoot=" + ownerPlayerRoot.name +
                    " | localRoot=" + localPlayerRoot.name +
                    " | textObject=" + distanceText.gameObject.name +
                    " | activationChanged=0" +
                    " | mode=direct_scene_distance");
            }
        }

        //* این تابع ریشه بازیکن، متن فاصله و بازیکن محلی را از ساختار واقعی صحنه پیدا می‌کند.
        private void BindRequiredReferences()
        {
            if (distanceText == null)
            {
                TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);
                for (int index = 0; index < texts.Length; index += 1)
                {
                    if (texts[index] == null) continue;

                    if (string.Equals(
                            texts[index].gameObject.name,
                            distanceTextObjectName,
                            System.StringComparison.Ordinal))
                    {
                        distanceText = texts[index];
                        break;
                    }
                }
            }

            if (ownerPlayerRoot == null)
            {
                Transform current = transform;
                while (current != null)
                {
                    string currentName = current.name ?? string.Empty;
                    if (currentName.StartsWith(localPlayerNamePrefix, System.StringComparison.Ordinal) ||
                        currentName.StartsWith(remotePlayerNamePrefix, System.StringComparison.Ordinal))
                    {
                        ownerPlayerRoot = current;
                        break;
                    }

                    current = current.parent;
                }
            }

            if (localPlayerRoot == null)
            {
                Transform[] transforms =
#if UNITY_2023_1_OR_NEWER
                    Object.FindObjectsByType<Transform>(
                        FindObjectsInactive.Include,
                        FindObjectsSortMode.None);
#else
                    Resources.FindObjectsOfTypeAll<Transform>();
#endif

                for (int index = 0; index < transforms.Length; index += 1)
                {
                    Transform candidate = transforms[index];
                    if (candidate == null) continue;
                    if (!candidate.gameObject.scene.IsValid()) continue;
                    if (!candidate.gameObject.scene.isLoaded) continue;

                    string candidateName = candidate.name ?? string.Empty;
                    if (!candidateName.StartsWith(
                            localPlayerNamePrefix,
                            System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    localPlayerRoot = candidate;
                    break;
                }
            }
        }

        //* این تابع وضعیت انتظار را فقط یک بار در لاگ می‌نویسد تا خروجی تکراری تولید نشود.
        private void LogWaitingOnce(string reason)
        {
            if (!logBindingResult || waitingLogged) return;
            waitingLogged = true;

            UnityEngine.Debug.LogWarning(
                "VOICE_G3_DISTANCE_NAMEPLATE_BINDING=WAITING" +
                " | object=" + gameObject.name +
                " | reason=" + reason +
                " | expectedTextObject=" + distanceTextObjectName +
                " | expectedLocalPrefix=" + localPlayerNamePrefix +
                " | expectedRemotePrefix=" + remotePlayerNamePrefix +
                " | activationChanged=0");
        }
    }
}

/*
توضیح فایل:
این اسکریپت فقط برای نمایش کمکی فاصله در تست فاز جی سه است. روی آبجکت نوشته نام قرار می‌گیرد، متن Text_Distance را از میان فرزندان پیدا می‌کند، ریشه بازیکن محلی را با نام Local_Player و ریشه بازیکن‌های ریموت را با نام Remote_Player تشخیص می‌دهد و فاصله صحنه‌ای آن‌ها را بدون هیچ تغییر فعال یا غیرفعال‌سازی نمایش می‌دهد.
*/
