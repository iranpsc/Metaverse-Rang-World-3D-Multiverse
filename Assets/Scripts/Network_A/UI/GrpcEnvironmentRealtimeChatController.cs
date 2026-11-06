using System;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Core;
using Network_A.Realtime.Controllers;
using Network_A.Realtime.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.UI
{
    public sealed class GrpcEnvironmentRealtimeChatController : MonoBehaviour
    {
        #region تنظیمات رابط

        [Header("Chat UI")]
        [SerializeField] private TMP_InputField inputMessage;
        [SerializeField] private Button sendMessageButton;
        [SerializeField] private TextMeshProUGUI messageText;

        [Header("Chat Rules")]
        [SerializeField, Min(1)] private int maximumMessageCharacters = 500;
        [SerializeField, Min(100)] private int maximumDisplayCharacters = 5000;
        [SerializeField] private bool clearInputAfterSuccessfulSend = true;
        [SerializeField] private string localMessagePrefix = "من";
        [SerializeField] private string chatActionType = "webgl_g7_lobby_chat";
        [SerializeField] private bool verboseLogs = true;

        #endregion

        #region متغیرهای داخلی

        private RealtimeRoomGameServerManager realtimeManager;
        private bool sendMessageRunning;
        private float nextManagerRefreshAt;
        private string lastLocalSender = string.Empty;
        private string lastLocalText = string.Empty;
        private float lastLocalMessageAt = -100f;

        #endregion

        #region چرخه حیات

        //* این تابع محدودیت ورودی و رویدادهای رابط پیام را هنگام ساخته شدن آبجکت آماده می کند.
        private void Awake()
        {
            ApplyInputCharacterLimit();
            BindInputMessage();
            BindSendMessageButton();
            RefreshSendMessageButton();
        }

        //* این تابع هنگام فعال شدن صحنه، کنترلر را به مدیر دائمی ریل تایم متصل می کند.
        private void OnEnable()
        {
            BindInputMessage();
            BindSendMessageButton();
            BindRealtimeManager();
            RefreshSendMessageButton();
        }

        //* این تابع پس از آماده شدن کامل صحنه، اتصال مدیر و وضعیت دکمه ارسال را دوباره بررسی می کند.
        private void Start()
        {
            BindRealtimeManager();
            RefreshSendMessageButton();
        }

        //* این تابع تغییر نمونه مدیر پس از بازاتصال و وضعیت دکمه ارسال را با فاصله محدود بررسی می کند.
        private void Update()
        {
            if (Time.realtimeSinceStartup < nextManagerRefreshAt) return;
            nextManagerRefreshAt = Time.realtimeSinceStartup + 0.25f;
            BindRealtimeManager();
            RefreshSendMessageButton();
        }

        //* این تابع هنگام خروج از صحنه تمام رویدادهای رابط و مدیر ریل تایم را آزاد می کند.
        private void OnDisable()
        {
            UnbindRealtimeManager();
            UnbindInputMessage();
            UnbindSendMessageButton();
        }

        #endregion

        #region اتصال رابط

        //* این تابع تغییر متن ورودی را به بررسی وضعیت دکمه ارسال متصل می کند.
        private void BindInputMessage()
        {
            if (inputMessage == null) return;
            inputMessage.onValueChanged.RemoveListener(HandleInputMessageChanged);
            inputMessage.onValueChanged.AddListener(HandleInputMessageChanged);
        }

        //* این تابع رویداد تغییر متن ورودی را آزاد می کند.
        private void UnbindInputMessage()
        {
            if (inputMessage != null) inputMessage.onValueChanged.RemoveListener(HandleInputMessageChanged);
        }

        //* این تابع دکمه ارسال را به تابع ارسال پیام متصل می کند.
        private void BindSendMessageButton()
        {
            if (sendMessageButton == null) return;
            sendMessageButton.onClick.RemoveListener(Btn_SendMessage);
            sendMessageButton.onClick.AddListener(Btn_SendMessage);
        }

        //* این تابع رویداد دکمه ارسال را آزاد می کند.
        private void UnbindSendMessageButton()
        {
            if (sendMessageButton != null) sendMessageButton.onClick.RemoveListener(Btn_SendMessage);
        }

        #endregion

        #region اتصال مدیر ریل تایم

        //* این تابع کنترلر صحنه را بدون Find به نمونه دائمی مدیر ریل تایم متصل می کند.
        private void BindRealtimeManager()
        {
            RealtimeRoomGameServerManager currentManager = RealtimeRoomGameServerManager.Instance;
            if (currentManager == realtimeManager) return;

            UnbindRealtimeManager();
            realtimeManager = currentManager;

            if (realtimeManager == null) return;

            realtimeManager.RealtimeEnvelopeReceived += HandleRealtimeEnvelopeReceived;
            RealtimeRoomGameServerManager.OnStateChanged += HandleRealtimeStateChanged;
            RealtimeRoomGameServerManager.OnRealtimeReady += HandleRealtimeReady;
            RealtimeRoomGameServerManager.OnRoomJoinedFor3D += HandleRoomJoined;
            RealtimeRoomGameServerManager.OnRoomLeftFor3D += HandleRoomLeft;
            RealtimeRoomGameServerManager.OnRealtimeDisconnected += HandleRealtimeDisconnected;
            Log("کنترلر چت صحنه به مدیر ریل تایم متصل شد.");
        }

        //* این تابع همه رویدادهای کنترلر صحنه را از مدیر ریل تایم جدا می کند.
        private void UnbindRealtimeManager()
        {
            if (realtimeManager != null) realtimeManager.RealtimeEnvelopeReceived -= HandleRealtimeEnvelopeReceived;
            RealtimeRoomGameServerManager.OnStateChanged -= HandleRealtimeStateChanged;
            RealtimeRoomGameServerManager.OnRealtimeReady -= HandleRealtimeReady;
            RealtimeRoomGameServerManager.OnRoomJoinedFor3D -= HandleRoomJoined;
            RealtimeRoomGameServerManager.OnRoomLeftFor3D -= HandleRoomLeft;
            RealtimeRoomGameServerManager.OnRealtimeDisconnected -= HandleRealtimeDisconnected;
            realtimeManager = null;
        }

        #endregion

        #region وضعیت دکمه ارسال

        //* این تابع پس از تغییر متن ورودی، فعال بودن دکمه ارسال را تازه می کند.
        private void HandleInputMessageChanged(string value)
        {
            RefreshSendMessageButton();
        }

        //* این تابع وضعیت دکمه ارسال را بر اساس متن، عضویت روم و آماده بودن اتصال تعیین می کند.
        private void RefreshSendMessageButton()
        {
            if (sendMessageButton == null) return;
            sendMessageButton.interactable = !sendMessageRunning &&
                                             inputMessage != null &&
                                             !string.IsNullOrWhiteSpace(inputMessage.text) &&
                                             realtimeManager != null &&
                                             realtimeManager.CanSendRoomPlayerAction;
        }

        //* این تابع بیشترین طول مجاز ورودی پیام را روی ورودی صحنه اعمال می کند.
        private void ApplyInputCharacterLimit()
        {
            if (inputMessage != null) inputMessage.characterLimit = Mathf.Max(1, maximumMessageCharacters);
        }

        #endregion

        #region ارسال پیام

        //* این تابع متن ورودی را از مسیر مطمئن ریل تایم برای کاربران همان روم ارسال می کند.
        public async void Btn_SendMessage()
        {
            if (sendMessageRunning) return;
            if (realtimeManager == null) BindRealtimeManager();

            string text = inputMessage != null && !string.IsNullOrWhiteSpace(inputMessage.text) ? inputMessage.text.Trim() : string.Empty;

            if (realtimeManager == null || !realtimeManager.CanSendRoomPlayerAction || string.IsNullOrWhiteSpace(text))
            {
                RefreshSendMessageButton();
                return;
            }

            sendMessageRunning = true;
            RefreshSendMessageButton();

            try
            {
                string sender = ResolveLocalSenderLabel();
                string payloadJson = BuildChatPayload(sender, realtimeManager.CurrentRoomId, text);
                bool sent = await realtimeManager.SendRoomPlayerActionReliableAsync(chatActionType, payloadJson);

                if (!sent)
                {
                    LogWarning("ارسال پیام انجام نشد.");
                    return;
                }

                lastLocalSender = sender;
                lastLocalText = text;
                lastLocalMessageAt = Time.realtimeSinceStartup;
                ShowMessage(localMessagePrefix, text);

                if (clearInputAfterSuccessfulSend && inputMessage != null) inputMessage.text = string.Empty;
                Log("پیام از مسیر ریل تایم ارسال شد.");
            }
            catch (Exception error)
            {
                LogWarning("خطای ارسال پیام: " + error.Message);
            }
            finally
            {
                sendMessageRunning = false;
                RefreshSendMessageButton();
            }
        }

        #endregion

        #region دریافت پیام

        //* این تابع فقط پیام های چت مربوط به روم جاری را از میان انولوپ های ریل تایم جدا می کند.
        private void HandleRealtimeEnvelopeReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null || realtimeManager == null) return;
            if (!string.Equals(envelope.ch, RealtimeChannels.Game, StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(envelope.t, RealtimeMessageTypes.PlayerAction, StringComparison.OrdinalIgnoreCase)) return;

            string payloadJson = envelope.payloadJson ?? string.Empty;
            if (!ContainsJsonStringValue(payloadJson, "kind", "chat")) return;
            if (!ContainsJsonStringValue(payloadJson, "actionType", chatActionType)) return;

            string roomId = ReadJsonString(payloadJson, "roomId", string.Empty);
            if (!string.IsNullOrWhiteSpace(roomId) && !string.Equals(roomId, realtimeManager.CurrentRoomId, StringComparison.OrdinalIgnoreCase)) return;

            string sender = ReadJsonString(payloadJson, "senderLabel", "کاربر");
            string text = ReadJsonString(payloadJson, "text", string.Empty);
            if (string.IsNullOrWhiteSpace(text)) return;
            if (IsLocalEcho(sender, text)) return;

            ShowMessage(sender, text);
            Log("پیام کاربر مقابل دریافت شد.");
        }

        //* این تابع بازتاب پیام همین کاربر را تشخیص می دهد تا پیام دوبار در رابط نمایش داده نشود.
        private bool IsLocalEcho(string sender, string text)
        {
            if (Time.realtimeSinceStartup - lastLocalMessageAt > 5f) return false;
            if (!string.Equals(sender, lastLocalSender, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(text, lastLocalText, StringComparison.Ordinal);
        }

        #endregion

        #region ساخت و خواندن اطلاعات پیام

        //* این تابع اطلاعات پیام را با همان قرارداد قبلی چت ریل تایم می سازد.
        private string BuildChatPayload(string sender, string roomId, string text)
        {
            return "{"
                   + "\"kind\":\"chat\","
                   + "\"actionType\":\"" + EscapeJson(chatActionType) + "\","
                   + "\"senderLabel\":\"" + EscapeJson(sender) + "\","
                   + "\"roomId\":\"" + EscapeJson(roomId) + "\","
                   + "\"text\":\"" + EscapeJson(text) + "\","
                   + "\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                   + "}";
        }

        //* این تابع نام قابل نمایش کاربر فعلی را از مدیر ورود یا شناسه ریل تایم آماده می کند.
        private string ResolveLocalSenderLabel()
        {
            AuthUserDto currentUser = GlobalAuthManager.Instance != null ? GlobalAuthManager.Instance.CurrentUser : null;
            if (currentUser != null && !string.IsNullOrWhiteSpace(currentUser.emailOrUsername)) return currentUser.emailOrUsername.Trim();
            if (realtimeManager != null && !string.IsNullOrWhiteSpace(realtimeManager.RealtimeUserId)) return realtimeManager.RealtimeUserId.Trim();
            return "کاربر";
        }

        //* این تابع یک مقدار متنی را از بدنه ساده جیسون پیام می خواند.
        private static string ReadJsonString(string json, string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return fallback;

            string marker = "\"" + key + "\":\"";
            int start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return fallback;

            start += marker.Length;
            var value = new System.Text.StringBuilder();

            for (int i = start; i < json.Length; i++)
            {
                char current = json[i];

                if (current == '\\' && i + 1 < json.Length)
                {
                    char escaped = json[++i];

                    switch (escaped)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        default: value.Append(escaped); break;
                    }

                    continue;
                }

                if (current == '"') return value.ToString();
                value.Append(current);
            }

            return fallback;
        }

        //* این تابع وجود یک کلید و مقدار متنی مشخص را در بدنه جیسون بررسی می کند.
        private static bool ContainsJsonStringValue(string json, string key, string value)
        {
            return string.Equals(ReadJsonString(json, key, string.Empty), value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        //* این تابع متن را برای قرار گرفتن امن داخل جیسون آماده می کند.
        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        #endregion

        #region نمایش پیام

        //* این تابع پیام جدید را جایگزین پیام قبلی می کند تا فقط آخرین پیام در رابط باقی بماند.
        private void ShowMessage(string sender, string text)
        {
            if (messageText == null || string.IsNullOrWhiteSpace(text)) return;

            string safeSender = string.IsNullOrWhiteSpace(sender) ? "کاربر" : sender.Trim();
            string value = safeSender + ": " + text.Trim();
            int safeMaximum = Mathf.Max(100, maximumDisplayCharacters);

            if (value.Length > safeMaximum) value = value.Substring(0, safeMaximum).TrimEnd();
            messageText.text = value;
        }

        //* این تابع آخرین پیام نمایشی صحنه را پاک می کند.
        public void ClearMessages()
        {
            if (messageText != null) messageText.text = string.Empty;
        }

        #endregion

        #region واکنش به وضعیت ریل تایم

        //* این تابع پس از تغییر وضعیت مدیر ریل تایم، وضعیت دکمه ارسال را تازه می کند.
        private void HandleRealtimeStateChanged(RealtimeRoomGameServerManager.FlowState state)
        {
            RefreshSendMessageButton();
        }

        //* این تابع پس از آماده شدن ریل تایم، وضعیت دکمه ارسال را تازه می کند.
        private void HandleRealtimeReady()
        {
            RefreshSendMessageButton();
        }

        //* این تابع پس از ورود به روم، وضعیت دکمه ارسال را تازه می کند.
        private void HandleRoomJoined(string roomId)
        {
            RefreshSendMessageButton();
        }

        //* این تابع پس از خروج از روم، دکمه ارسال را غیرفعال می کند.
        private void HandleRoomLeft(string roomId)
        {
            RefreshSendMessageButton();
        }

        //* این تابع پس از قطع ریل تایم، دکمه ارسال را غیرفعال می کند.
        private void HandleRealtimeDisconnected(string reason)
        {
            RefreshSendMessageButton();
        }

        #endregion

        #region گزارش

        //* این تابع گزارش عادی کنترلر چت صحنه را ثبت می کند.
        private void Log(string message)
        {
            if (verboseLogs) Debug.Log("[GrpcEnvironmentRealtimeChatController] " + message);
        }

        //* این تابع گزارش هشدار کنترلر چت صحنه را ثبت می کند.
        private void LogWarning(string message)
        {
            Debug.LogWarning("[GrpcEnvironmentRealtimeChatController] " + message);
        }

        #endregion
    }
}
