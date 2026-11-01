using System;
using System.Reflection;
using Network_A.GameServer.Auth;
using Network_A.GameServer.Players;
using Network_A.GameServer.WebSocket;
using UnityEngine;

namespace Network_A.GameServer.Tools
{
    public class DedicatedServerAuthPipelineAutoBinder : MonoBehaviour
    {
        [Header("Auto Fix")]
        [SerializeField] private bool autoAddMissingComponents = true;
        [SerializeField] private bool disableWebSocketEcho = true;
        [SerializeField] private bool runOnAwake = true;
        [SerializeField] private bool runOnStart = true;
        [SerializeField] private bool allowRepeatedAutoFix = false;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        private DedicatedWebSocketServer webSocketServer;
        private DedicatedTicketVerifier ticketVerifier;
        private DedicatedTicketHandshakeHandler ticketHandshakeHandler;
        private DedicatedPlayerRegistry playerRegistry;
        private bool autoFixApplied;

        //* این تابع در اویک مسیر هندشیک ددیکیتد سرور را قبل از شروع تست آماده می کند.
        private void Awake()
        {
            if (!runOnAwake) return;

            FixAuthPipeline();
        }

        //* این تابع در استارت یک بار دیگر مسیر هندشیک را بررسی می کند تا اگر ترتیب اجرای یونیتی متفاوت بود، باز هم درست شود.
        private void Start()
        {
            if (!runOnStart) return;

            FixAuthPipeline();
        }

        //* این تابع از اینسپکتور برای اجرای دستی بررسی و اتصال مسیر آث تیکت استفاده می شود.
        [ContextMenu("Fix Dedicated Auth Pipeline")]
        public void FixAuthPipeline()
        {
            if (autoFixApplied && !allowRepeatedAutoFix)
            {
                Log("Dedicated auth pipeline auto fix skipped. It was already applied.");
                return;
            }

            FindOrCreateComponents();
            BindReferences();
            ApplyWebSocketEchoRule();
            PrintStatus();
            autoFixApplied = true;
        }

        //* این تابع کامپوننت های لازم مسیر auth_ticket را پیدا می کند یا در صورت نیاز اضافه می کند.
        private void FindOrCreateComponents()
        {
            webSocketServer = GetComponent<DedicatedWebSocketServer>();
            ticketVerifier = GetComponent<DedicatedTicketVerifier>();
            ticketHandshakeHandler = GetComponent<DedicatedTicketHandshakeHandler>();
            playerRegistry = GetComponent<DedicatedPlayerRegistry>();

            if (!autoAddMissingComponents) return;

            if (webSocketServer == null)
            {
                webSocketServer = gameObject.AddComponent<DedicatedWebSocketServer>();
                Log("Added missing DedicatedWebSocketServer.");
            }

            if (ticketVerifier == null)
            {
                ticketVerifier = gameObject.AddComponent<DedicatedTicketVerifier>();
                Log("Added missing DedicatedTicketVerifier.");
            }

            if (playerRegistry == null)
            {
                playerRegistry = gameObject.AddComponent<DedicatedPlayerRegistry>();
                Log("Added missing DedicatedPlayerRegistry.");
            }

            if (ticketHandshakeHandler == null)
            {
                ticketHandshakeHandler = gameObject.AddComponent<DedicatedTicketHandshakeHandler>();
                Log("Added missing DedicatedTicketHandshakeHandler.");
            }
        }

        //* این تابع رفرنس های خصوصی هندشیک را با رفلکشن پر می کند تا حتماً به وب سوکت، وریفایر و رجیستری وصل باشد.
        private void BindReferences()
        {
            if (ticketHandshakeHandler == null) return;

            SetPrivateField(ticketHandshakeHandler, "webSocketServer", webSocketServer);
            SetPrivateField(ticketHandshakeHandler, "ticketVerifier", ticketVerifier);
            SetPrivateField(ticketHandshakeHandler, "playerRegistry", playerRegistry);

            Log("DedicatedTicketHandshakeHandler references bound.");
        }

        //* این تابع اکو تست وب سوکت را خاموش می کند تا auth_ticket فقط از مسیر هندشیک جواب بگیرد.
        private void ApplyWebSocketEchoRule()
        {
            if (!disableWebSocketEcho || webSocketServer == null) return;

            SetPrivateField(webSocketServer, "echoTestMessages", false);
            Log("DedicatedWebSocketServer echoTestMessages disabled.");
        }

        //* این تابع وضعیت کامپوننت های لازم را در کنسول چاپ می کند.
        private void PrintStatus()
        {
            string status =
                "Dedicated auth pipeline status" +
                " | webSocketServer=" + BoolText(webSocketServer != null) +
                " | ticketVerifier=" + BoolText(ticketVerifier != null) +
                " | ticketHandshakeHandler=" + BoolText(ticketHandshakeHandler != null) +
                " | playerRegistry=" + BoolText(playerRegistry != null);

            Debug.Log("[DedicatedServerAuthPipelineAutoBinder] " + status);

            if (webSocketServer == null ||
                ticketVerifier == null ||
                ticketHandshakeHandler == null ||
                playerRegistry == null)
            {
                Debug.LogError("[DedicatedServerAuthPipelineAutoBinder] Dedicated auth pipeline is incomplete.");
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
                Debug.LogWarning("[DedicatedServerAuthPipelineAutoBinder] Field not found | type=" +
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

            Debug.Log("[DedicatedServerAuthPipelineAutoBinder] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت برای جلوگیری از تنظیمات دستی زیاد در مرحله DS-7B اضافه شده است.
        روی آبجکت Unity_Dedicated_Server_Runtime قرار می گیرد.
        اگر DedicatedTicketHandshakeHandler، DedicatedTicketVerifier یا DedicatedPlayerRegistry کم باشد، خودش اضافه می کند.
        سپس هندشیک را به DedicatedWebSocketServer، وریفایر و رجیستری وصل می کند.
        همچنین echoTestMessages را خاموش می کند تا پیام auth_ticket به جای server_received وارد مسیر verify-ticket شود.
        این فایل هیچ ارتباطی با GameServerClient قدیمی G7 ندارد و فقط سمت Unity Dedicated Server کار می کند.
        */
    }
}
