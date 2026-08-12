using System;
using Network_A.GameServer;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    [AddComponentMenu("Network A/Voice/Dedicated/Voice Dedicated Authority Manual Binder")]
    public sealed class VoiceDedicatedAuthorityManualBinder : MonoBehaviour
    {
        [Header("Dedicated References")]
        [SerializeField] private DedicatedServerRuntime dedicatedRuntime;
        [SerializeField] private DedicatedPlayerRegistry playerRegistry;
        [SerializeField] private DedicatedPlayerStateStore playerStateStore;
        [SerializeField] private GameServerControlDedicatedClient controlClient;

        [Header("Voice References")]
        [SerializeField] private VoiceDedicatedAuthoritativePositionProvider positionProvider;
        [SerializeField] private VoiceDedicatedSessionDeltaSender deltaSender;
        [SerializeField] private VoiceDedicatedAuthorityMonitor authorityMonitor;

        [Header("Voice Settings")]
        [SerializeField] private bool deltaTransportEnabled = true;
        [SerializeField, Min(1)] private int maximumAcceptedStateAgeMs = 15000;
        [SerializeField, Min(0)] private int stabilityDelayMs;

        private bool configured;

        public bool IsConfigured { get { return configured; } }
        public string LastFailure { get; private set; }

        //* این تابع پس از آماده‌شدن اجزای صحنه، اتصال‌های تعیین‌شده در بازرس را اعمال می‌کند.
        private void Start()
        {
            ConfigureNow();
        }

        //* این تابع همه وابستگی‌های دستی را بررسی و سه بخش صوت سرور اختصاصی را به یکدیگر متصل می‌کند.
        [ContextMenu("Configure Dedicated Voice Authority")]
        public void ConfigureNow()
        {
            if (configured)
            {
                return;
            }

            if (dedicatedRuntime == null ||
                playerRegistry == null ||
                playerStateStore == null ||
                controlClient == null ||
                positionProvider == null ||
                deltaSender == null ||
                authorityMonitor == null)
            {
                Fail(
                    "One or more manual references are missing" +
                    " | runtime=" + (dedicatedRuntime != null) +
                    " | playerRegistry=" + (playerRegistry != null) +
                    " | playerStateStore=" + (playerStateStore != null) +
                    " | controlClient=" + (controlClient != null) +
                    " | positionProvider=" + (positionProvider != null) +
                    " | deltaSender=" + (deltaSender != null) +
                    " | authorityMonitor=" + (authorityMonitor != null));
                return;
            }

            if (maximumAcceptedStateAgeMs <= 0)
            {
                Fail("Maximum accepted state age must be greater than zero.");
                return;
            }

            if (stabilityDelayMs <= 0)
            {
                Fail(
                    "Stability delay is not configured. " +
                    "Enter the benchmark-confirmed value in the Inspector.");
                return;
            }

            try
            {
                positionProvider.Configure(
                    playerStateStore,
                    maximumAcceptedStateAgeMs);

                deltaSender.Configure(
                    dedicatedRuntime,
                    controlClient,
                    deltaTransportEnabled);

                authorityMonitor.Configure(
                    dedicatedRuntime,
                    playerRegistry,
                    positionProvider,
                    deltaSender,
                    stabilityDelayMs);

                configured = true;
                LastFailure = string.Empty;

                Debug.Log(
                    "VOICE_V3_DEDICATED_MANUAL_BINDER=PASS" +
                    " | object=" + gameObject.name +
                    " | runtime=" + dedicatedRuntime.name +
                    " | playerRegistry=" + playerRegistry.name +
                    " | playerStateStore=" + playerStateStore.name +
                    " | controlClient=" + controlClient.name +
                    " | positionProvider=" + positionProvider.name +
                    " | deltaSender=" + deltaSender.name +
                    " | authorityMonitor=" + authorityMonitor.name +
                    " | deltaTransportEnabled=" + deltaTransportEnabled +
                    " | maximumAcceptedStateAgeMs=" + maximumAcceptedStateAgeMs +
                    " | stabilityDelayMs=" + stabilityDelayMs);
            }
            catch (Exception exception)
            {
                Fail(exception.ToString());
            }
        }

        //* این تابع شکست اتصال دستی را با علت کامل ثبت می‌کند.
        private void Fail(string error)
        {
            configured = false;
            LastFailure = string.IsNullOrWhiteSpace(error)
                ? "unknown_manual_binding_failure"
                : error.Trim();

            Debug.LogError(
                "VOICE_V3_DEDICATED_MANUAL_BINDER=FAIL" +
                " | object=" + gameObject.name +
                " | error=" + LastFailure);
        }
    }
}

/*
توضیح فایل:
این فایل هیچ آبجکت یا کامپوننتی را به‌صورت خودکار ایجاد نمی‌کند.
تمام وابستگی‌ها باید در صحنه سرور اختصاصی ساخته و در بازرس به این اتصال‌دهنده داده شوند.
پس از آغاز صحنه، موقعیت پذیرفته‌شده بازیکنان، فرستنده رویداد و پایشگر فاصله را به یکدیگر متصل می‌کند.
*/
