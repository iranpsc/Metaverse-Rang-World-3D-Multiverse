using System.Reflection;
using Network_A.DedicatedGameServer.Client;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    [DefaultExecutionOrder(-10000)]
    public sealed class WebGLDedicatedRemotePlayerFocusGuard : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedRemotePlayerViewController remotePlayerViewController;
        [SerializeField] private RealtimeWebSocketG7RoomLobbyTestController webSocketRealtimeController;

        [Header("WebGL Background Focus Policy")]
        [SerializeField] private bool disableStateSilenceDeactivation = false;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        private const string StateSilenceDeactivationFieldName =
            "deactivateRemotePlayerWhenStateSilent";

        private bool policyApplied;

        //* این تابع قبل از Update کنترلر مشترک، سیاست مخصوص WebGL را اعمال می کند.
        private void Awake()
        {
            ResolveReferences();
            ApplyWebGLBackgroundFocusPolicy();
        }

        //* این تابع در صورت فعال شدن دوباره آبجکت، از باقی ماندن سیاست WebGL مطمئن می شود.
        private void OnEnable()
        {
            if (policyApplied) return;

            ResolveReferences();
            ApplyWebGLBackgroundFocusPolicy();
        }

        //* این تابع رفرنس های Wrapper را بدون ایجاد وابستگی در کنترلر مشترک پیدا می کند.
        private void ResolveReferences()
        {
            if (remotePlayerViewController == null)
            {
                remotePlayerViewController =
                    GetComponent<DedicatedRemotePlayerViewController>();
            }

            if (remotePlayerViewController == null)
            {
                remotePlayerViewController =
                    FindObjectOfType<DedicatedRemotePlayerViewController>(true);
            }

            if (webSocketRealtimeController == null)
            {
                webSocketRealtimeController =
                    FindObjectOfType<RealtimeWebSocketG7RoomLobbyTestController>(true);
            }
        }

        //* این تابع فقط در صحنه WebSocket/WebGL، غیرفعال سازی اشتباه ناشی از سکوت تب پس زمینه را خاموش می کند.
        private void ApplyWebGLBackgroundFocusPolicy()
        {
#if !UNITY_WEBGL && !UNITY_EDITOR
            return;
#else
            if (!disableStateSilenceDeactivation) return;

            if (webSocketRealtimeController == null)
            {
                LogWarning(
                    "WebSocket realtime controller was not found. " +
                    "WebGL background-focus policy was not applied."
                );
                return;
            }

            if (remotePlayerViewController == null)
            {
                LogWarning(
                    "DedicatedRemotePlayerViewController was not found. " +
                    "WebGL background-focus policy was not applied."
                );
                return;
            }

            FieldInfo stateSilenceDeactivationField =
                typeof(DedicatedRemotePlayerViewController).GetField(
                    StateSilenceDeactivationFieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic
                );

            if (stateSilenceDeactivationField == null ||
                stateSilenceDeactivationField.FieldType != typeof(bool))
            {
                LogWarning(
                    "Expected state-silence deactivation field was not found. " +
                    "Shared controller was left unchanged."
                );
                return;
            }

            stateSilenceDeactivationField.SetValue(
                remotePlayerViewController,
                true
            );

            policyApplied = true;

            if (verboseLogs)
            {
                Debug.Log(
                    "[WebGLDedicatedRemotePlayerFocusGuard] " +
                    "State-silence remote deactivation kept enabled for WebGL reconnect visibility."
                );
            }
#endif
        }

        //* این تابع هشدار Wrapper را با پیشوند ثابت ثبت می کند.
        private void LogWarning(string message)
        {
            if (!verboseLogs) return;

            Debug.LogWarning(
                "[WebGLDedicatedRemotePlayerFocusGuard] " +
                message
            );
        }
    }
}
