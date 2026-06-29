using System;
using System.Reflection;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using Network_A.GameServer.WebSocket;
using UnityEngine;

namespace Network_A.GameServer.Tools
{
    public class DedicatedServerGameplayPipelineAutoBinder : MonoBehaviour
    {
        [Header("Auto Fix")]
        [SerializeField] private bool autoAddMissingComponents = true;
        [SerializeField] private bool runOnAwake = true;
        [SerializeField] private bool runOnStart = true;
        [SerializeField] private bool allowRepeatedAutoFix = false;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        private DedicatedWebSocketServer webSocketServer;
        private DedicatedPlayerRegistry playerRegistry;
        private DedicatedPlayerStateStore playerStateStore;
        private DedicatedGameMessageRouter gameMessageRouter;
        private bool autoFixApplied;

        //* این تابع در اویک مسیر پیام های گیم ددیکیتد سرور را آماده می کند.
        private void Awake()
        {
            if (!runOnAwake) return;

            FixGameplayPipeline();
        }

        //* این تابع در استارت یک بار دیگر مسیر پیام های گیم را بررسی می کند.
        private void Start()
        {
            if (!runOnStart) return;

            FixGameplayPipeline();
        }

        //* این تابع از اینسپکتور برای اجرای دستی بررسی و اتصال مسیر گیم استفاده می شود.
        [ContextMenu("Fix Dedicated Gameplay Pipeline")]
        public void FixGameplayPipeline()
        {
            if (autoFixApplied && !allowRepeatedAutoFix)
            {
                Log("Dedicated gameplay pipeline auto fix skipped. It was already applied.");
                return;
            }

            FindOrCreateComponents();
            BindReferences();
            PrintStatus();
            autoFixApplied = true;
        }

        //* این تابع کامپوننت های لازم مسیر player_state را پیدا می کند یا در صورت نیاز اضافه می کند.
        private void FindOrCreateComponents()
        {
            webSocketServer = GetComponent<DedicatedWebSocketServer>();
            playerRegistry = GetComponent<DedicatedPlayerRegistry>();
            playerStateStore = GetComponent<DedicatedPlayerStateStore>();
            gameMessageRouter = GetComponent<DedicatedGameMessageRouter>();

            if (!autoAddMissingComponents) return;

            if (webSocketServer == null)
            {
                webSocketServer = gameObject.AddComponent<DedicatedWebSocketServer>();
                Log("Added missing DedicatedWebSocketServer.");
            }

            if (playerRegistry == null)
            {
                playerRegistry = gameObject.AddComponent<DedicatedPlayerRegistry>();
                Log("Added missing DedicatedPlayerRegistry.");
            }

            if (playerStateStore == null)
            {
                playerStateStore = gameObject.AddComponent<DedicatedPlayerStateStore>();
                Log("Added missing DedicatedPlayerStateStore.");
            }

            if (gameMessageRouter == null)
            {
                gameMessageRouter = gameObject.AddComponent<DedicatedGameMessageRouter>();
                Log("Added missing DedicatedGameMessageRouter.");
            }
        }

        //* این تابع رفرنس های خصوصی رُتر گیم را با رفلکشن پر می کند.
        private void BindReferences()
        {
            if (gameMessageRouter == null) return;

            SetPrivateField(gameMessageRouter, "webSocketServer", webSocketServer);
            SetPrivateField(gameMessageRouter, "playerRegistry", playerRegistry);
            SetPrivateField(gameMessageRouter, "playerStateStore", playerStateStore);

            gameMessageRouter.Rebind();

            Log("DedicatedGameMessageRouter references bound.");
        }

        //* این تابع وضعیت کامپوننت های لازم را در کنسول چاپ می کند.
        private void PrintStatus()
        {
            string status =
                "Dedicated gameplay pipeline status" +
                " | webSocketServer=" + BoolText(webSocketServer != null) +
                " | playerRegistry=" + BoolText(playerRegistry != null) +
                " | playerStateStore=" + BoolText(playerStateStore != null) +
                " | gameMessageRouter=" + BoolText(gameMessageRouter != null);

            Debug.Log("[DedicatedServerGameplayPipelineAutoBinder] " + status);

            if (webSocketServer == null ||
                playerRegistry == null ||
                playerStateStore == null ||
                gameMessageRouter == null)
            {
                Debug.LogError("[DedicatedServerGameplayPipelineAutoBinder] Dedicated gameplay pipeline is incomplete.");
            }
        }

        //* این تابع فیلد خصوصی یک کامپوننت را با نام مشخص مقداردهی می کند.
        private void SetPrivateField(object target, string fieldName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName)) return;

            Type type = target.GetType();
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null)
            {
                Debug.LogWarning("[DedicatedServerGameplayPipelineAutoBinder] Field not found | type=" +
                                 type.Name + " | field=" + fieldName);
                return;
            }

            field.SetValue(target, value);
        }

        //* این تابع مقدار بول را به متن خوانا تبدیل می کند.
        private string BoolText(bool value)
        {
            return value ? "OK" : "MISSING";
        }

        //* این تابع لاگ معمولی اتوبایندر را چاپ می کند.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedServerGameplayPipelineAutoBinder] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت برای کم کردن تنظیمات دستی مرحله DS-8 اضافه شده است.
        روی آبجکت Unity_Dedicated_Server_Runtime قرار می گیرد.
        اگر DedicatedPlayerStateStore یا DedicatedGameMessageRouter کم باشد، خودش اضافه می کند.
        سپس رُتر گیم را به DedicatedWebSocketServer و DedicatedPlayerRegistry وصل می کند.
        این فایل فقط مسیر player_state را آماده می کند و با GameServerClient قدیمی تداخل ندارد.
        */
    }
}
