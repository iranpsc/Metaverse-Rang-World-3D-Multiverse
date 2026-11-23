using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Network_A.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.UI
{
    /// <summary>
    /// مرکز سراسری و مستقل نمایش پیام‌ها روی Pnl_ServerDebug.
    ///
    /// این کامپوننت فقط یک‌بار ساخته می‌شود و همراه رابط پیام خودش
    /// در تمام صحنه‌ها باقی می‌ماند و بالاترین پیام فعال را نمایش می‌دهد.
    ///
    /// این کلاس هیچ Health Check، Login، Refresh، Realtime یا
    /// Dedicated reconnect انجام نمی‌دهد؛ فقط پیام‌ها و Callback
    /// دکمه‌های پنل را مدیریت می‌کند.
    /// </summary>
    [DefaultExecutionOrder(-9000)]
    public sealed class GlobalMessageManager : MonoBehaviour
    {
        public enum MessageSource
        {
            Network,
            Authentication,
            Realtime,
            DedicatedServer,
            Request,
            Gameplay,
            System
        }

        public enum MessageType
        {
            Information,
            Success,
            Warning,
            Error,
            AuthenticationRequired,
            NetworkStatus
        }

        public static class Priorities
        {
            public const int Gameplay = 20;
            public const int Information = 30;
            public const int Success = 40;
            public const int Warning = 50;
            public const int RequestError = 60;
            public const int AuthenticationRequired = 75;
            public const int Reconnecting = 85;

            // پیام‌های شبکه از این محدوده به بالا، بر همه پیام‌های
            // غیرشبکه‌ای اولویت قطعی دارند.
            public const int NetworkRecovering = 90;
            public const int ServerUnavailable = 95;
            public const int InternetUnavailable = 100;
        }

        private const int MaximumNonNetworkPriority = Priorities.NetworkRecovering - 1;
        private const int MaximumNetworkPriority = Priorities.InternetUnavailable;

        public static GlobalMessageManager Instance { get; private set; }

        public static bool IsReady
        {
            get { return Instance != null; }
        }

        public static string CurrentMessageId
        {
            get
            {
                return Instance != null && Instance.displayedMessage != null
                    ? Instance.displayedMessage.Id
                    : string.Empty;
            }
        }

        public static bool HasActiveNetworkPriorityMessage
        {
            get
            {
                return Instance != null &&
                       Instance.HasActiveNetworkPriorityMessageInternal();
            }
        }

        /// <summary>
        /// هنگامی اجرا می‌شود که پیام برنده پنل تغییر کند.
        /// مقدار خالی یعنی هیچ پیام مدیریت‌شده‌ای فعال نیست.
        /// </summary>
        public static event Action<string> OnDisplayedMessageChanged;


        [Header("رابط پیام سراسری")]
        [SerializeField] private GameObject pnl_ServerDebug;
        [SerializeField] private TMP_Text txt_ServerDebugTitle;
        [SerializeField] private TMP_Text txt_ServerDebugMessage;
        [SerializeField] private TMP_Text txt_ServerDebugTechnical;
        [SerializeField] private Button btn_Close;
        [SerializeField] private Button btn_Relogin;
        [SerializeField] private Button btn_Retry;


        [Header("Panel Behaviour")]
        [SerializeField] private bool showTechnicalDetails = true;
        [SerializeField] private bool hidePanelWhenManagedMessagesFinish = true;
        [SerializeField] private bool restoreManagedMessageIfAnotherScriptOverwritesPanel = true;
        [SerializeField, Min(0.05f)] private float panelOwnershipRefreshIntervalSeconds = 0.15f;
        [SerializeField, Min(0.05f)] private float expiredMessageCleanupIntervalSeconds = 0.25f;
        [SerializeField, Min(0f)] private float managedMessageHandoffDelaySeconds = 0.12f;

        private readonly Dictionary<string, MessageEntry> activeMessages =
            new Dictionary<string, MessageEntry>();

        private MessageEntry displayedMessage;
        private string displayedMessageId = string.Empty;
        private long displayedMessageRevision;

        private string dismissedMessageId = string.Empty;
        private long dismissedMessageRevision;

        private long messageRevision;
        private float nextCleanupAt;
        private float nextPanelOwnershipRefreshAt;
        private bool applicationIsQuitting;
        private bool managerOwnsCurrentPanel;
        private bool isActionRunning;
        private bool panelHidePending;
        private float panelHideAt;

        //* نمونه تکراری را همراه با شیء میزبان آن حذف می‌کند.
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                NetworkFileLogger.Warning(
                    "GLOBAL_MESSAGE",
                    "نمونه تکراری مدیر پیام شناسایی شد و حذف می‌شود."
                );

                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            if (HasValidPanelReference()) SetupPanelButtonListeners();
            else NetworkFileLogger.Error("GLOBAL_MESSAGE_UI", "مراجع رابط پیام سراسری در بازرس تنظیم نشده‌اند.");

            NetworkFileLogger.Info(
                "GLOBAL_MESSAGE",
                "مدیر پیام سراسری راه‌اندازی شد."
            );
        }

        private void Start()
        {
            if (Instance != this) return;

            RenderHighestPriorityMessage();
        }

        private void Update()
        {
            if (Instance != this || applicationIsQuitting) return;

            float now = Time.unscaledTime;

            if (now >= nextCleanupAt)
            {
                nextCleanupAt =
                    now + Mathf.Max(0.05f, expiredMessageCleanupIntervalSeconds);

                RemoveExpiredMessages(now);
            }

            ProcessPendingPanelHide(now);

            if (!restoreManagedMessageIfAnotherScriptOverwritesPanel) return;
            if (now < nextPanelOwnershipRefreshAt) return;

            nextPanelOwnershipRefreshAt =
                now + Mathf.Max(0.05f, panelOwnershipRefreshIntervalSeconds);

            EnsureDisplayedMessageOwnsPanel();
        }

        private void OnApplicationQuit()
        {
            applicationIsQuitting = true;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;

            DetachPanelButtonListeners();

            Instance = null;
        }

        //* فقط همان نمونه‌ای را که از قبل در صحنه قرار داده شده برمی‌گرداند.
        //* این تابع هیچ شیء جدیدی نمی‌سازد.
        public static GlobalMessageManager EnsureInstance()
        {
            if (Instance != null)
            {
                return Instance;
            }

            NetworkFileLogger.Warning(
                "GLOBAL_MESSAGE",
                "مدیر پیام سراسری در صحنه آغازین پیدا نشد.");

            return null;
        }

        /// <summary>
        /// پیام جدید را ثبت می‌کند یا پیام قبلی با همان شناسه را
        /// به‌روزرسانی می‌کند. خروجی، شناسه نهایی پیام است.
        /// </summary>
        public static string Publish(
            string messageId,
            MessageSource source,
            MessageType type,
            int priority,
            string title,
            string message,
            string technicalDetails = "",
            float durationSeconds = 0f,
            bool sticky = false,
            bool canClose = true,
            bool showRetry = false,
            Func<Task> retryAction = null,
            bool showRelogin = false,
            Func<Task> reloginAction = null
        )
        {
            GlobalMessageManager manager = Instance;

            if (manager == null)
            {
                NetworkFileLogger.Warning(
                    "GLOBAL_MESSAGE",
                    "پیام نمایش داده نشد، زیرا مدیر پیام سراسری در صحنه آغازین فعال نیست.");

                return string.Empty;
            }

            return manager.PublishInternal(
                messageId,
                source,
                type,
                priority,
                title,
                message,
                technicalDetails,
                durationSeconds,
                sticky,
                canClose,
                showRetry,
                retryAction,
                showRelogin,
                reloginAction
            );
        }

        public static string ShowInfo(
            string messageId,
            string title,
            string message,
            string technicalDetails = "",
            float durationSeconds = 3f,
            MessageSource source = MessageSource.System
        )
        {
            return Publish(
                messageId,
                source,
                MessageType.Information,
                Priorities.Information,
                title,
                message,
                technicalDetails,
                durationSeconds,
                false,
                true
            );
        }

        public static string ShowSuccess(
            string messageId,
            string title,
            string message,
            string technicalDetails = "",
            float durationSeconds = 3f,
            MessageSource source = MessageSource.System
        )
        {
            return Publish(
                messageId,
                source,
                MessageType.Success,
                Priorities.Success,
                title,
                message,
                technicalDetails,
                durationSeconds,
                false,
                true
            );
        }

        public static string ShowWarning(
            string messageId,
            string title,
            string message,
            string technicalDetails = "",
            float durationSeconds = 4f,
            MessageSource source = MessageSource.System
        )
        {
            return Publish(
                messageId,
                source,
                MessageType.Warning,
                Priorities.Warning,
                title,
                message,
                technicalDetails,
                durationSeconds,
                false,
                true
            );
        }

        public static string ShowError(
            string messageId,
            string title,
            string message,
            string technicalDetails = "",
            float durationSeconds = 0f,
            bool sticky = false,
            MessageSource source = MessageSource.System,
            bool showRetry = false,
            Func<Task> retryAction = null
        )
        {
            return Publish(
                messageId,
                source,
                MessageType.Error,
                Priorities.RequestError,
                title,
                message,
                technicalDetails,
                durationSeconds,
                sticky,
                true,
                showRetry,
                retryAction
            );
        }

        public static string ShowAuthenticationRequired(
            string messageId,
            string title,
            string message,
            string technicalDetails,
            Func<Task> reloginAction,
            Func<Task> retryAction = null
        )
        {
            return Publish(
                messageId,
                MessageSource.Authentication,
                MessageType.AuthenticationRequired,
                Priorities.AuthenticationRequired,
                title,
                message,
                technicalDetails,
                0f,
                true,
                true,
                retryAction != null,
                retryAction,
                reloginAction != null,
                reloginAction
            );
        }

        //* تا پایان بررسی قطعی ارتباط، پیام بررسی شبکه را بدون امکان بستن نمایش می‌دهد.
        public static string ShowNetworkChecking(
            string messageId,
            string title,
            string message,
            string technicalDetails = ""
        )
        {
            return Publish(
                messageId,
                MessageSource.Network,
                MessageType.NetworkStatus,
                Priorities.NetworkRecovering,
                title,
                message,
                technicalDetails,
                0f,
                true,
                false
            );
        }

        //* تا زمان تکمیل ورود، پیام بازیابی شبکه را بدون امکان بستن نمایش می‌دهد.
        public static string ShowNetworkRecovering(
            string messageId,
            string title,
            string message,
            string technicalDetails = ""
        )
        {
            return Publish(
                messageId,
                MessageSource.Network,
                MessageType.NetworkStatus,
                Priorities.NetworkRecovering,
                title,
                message,
                technicalDetails,
                0f,
                true,
                false
            );
        }

        public static string ShowServerUnavailable(
            string messageId,
            string title,
            string message,
            string technicalDetails,
            Func<Task> retryAction
        )
        {
            return Publish(
                messageId,
                MessageSource.Network,
                MessageType.NetworkStatus,
                Priorities.ServerUnavailable,
                title,
                message,
                technicalDetails,
                0f,
                true,
                true,
                retryAction != null,
                retryAction
            );
        }

        //* تا زمان برقراری دوباره ارتباط، پیام قطع اینترنت را بدون امکان بستن نمایش می‌دهد.
        public static string ShowInternetUnavailable(
            string messageId,
            string title,
            string message,
            string technicalDetails,
            Func<Task> retryAction
        )
        {
            return Publish(
                messageId,
                MessageSource.Network,
                MessageType.NetworkStatus,
                Priorities.InternetUnavailable,
                title,
                message,
                technicalDetails,
                0f,
                true,
                false,
                retryAction != null,
                retryAction
            );
        }

        /// <summary>
        /// پیام کوتاه وصل‌شدن کامل شبکه را نمایش می‌دهد.
        /// این پیام Sticky نیست و پس از مدت تعیین‌شده منقضی می‌شود.
        /// </summary>
        public static string ShowConnectionRestored(
            string messageId,
            string title,
            string message,
            string technicalDetails = "",
            float durationSeconds = 1.5f
        )
        {
            return Publish(
                messageId,
                MessageSource.Network,
                MessageType.Success,
                Priorities.NetworkRecovering,
                title,
                message,
                technicalDetails,
                Mathf.Max(0.1f, durationSeconds),
                false,
                true
            );
        }

        /// <summary>
        /// فقط پیام‌های متعلق به مانیتور شبکه را پاک می‌کند و
        /// پیام‌های Auth، Realtime، Dedicated و Gameplay را نگه می‌دارد.
        /// </summary>
        public static int ClearNetworkMessages()
        {
            return ClearFromSource(MessageSource.Network);
        }

        public static bool Clear(string messageId)
        {
            if (Instance == null || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            return Instance.ClearInternal(messageId.Trim());
        }

        public static int ClearFromSource(MessageSource source)
        {
            if (Instance == null) return 0;
            return Instance.ClearFromSourceInternal(source);
        }

        public static int ClearAll()
        {
            if (Instance == null) return 0;
            return Instance.ClearAllInternal();
        }

        public static bool Contains(string messageId)
        {
            if (Instance == null || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            return Instance.activeMessages.ContainsKey(messageId.Trim());
        }

        /// <summary>
        /// پس از فعال‌شدن دوباره رابط پیام دائمی می‌توان این تابع
        /// را برای ثبت دوباره دکمه‌ها و بازنمایش پیام جاری صدا زد.
        /// </summary>
        public static void RebindPanel()
        {
            if (Instance == null) return;

            Instance.SetupPanelButtonListeners();
            Instance.RenderHighestPriorityMessage();
        }

        private string PublishInternal(
            string messageId,
            MessageSource source,
            MessageType type,
            int priority,
            string title,
            string message,
            string technicalDetails,
            float durationSeconds,
            bool sticky,
            bool canClose,
            bool showRetry,
            Func<Task> retryAction,
            bool showRelogin,
            Func<Task> reloginAction
        )
        {
            string safeId = string.IsNullOrWhiteSpace(messageId)
                ? Guid.NewGuid().ToString("N")
                : messageId.Trim();

            int safePriority;

            if (source == MessageSource.Network)
            {
                safePriority = Mathf.Clamp(
                    priority,
                    0,
                    MaximumNetworkPriority
                );
            }
            else
            {
                safePriority = Mathf.Clamp(
                    priority,
                    0,
                    MaximumNonNetworkPriority
                );
            }

            float expiresAt = 0f;

            if (!sticky && durationSeconds > 0f)
            {
                expiresAt = Time.unscaledTime + durationSeconds;
            }

            MessageEntry entry = new MessageEntry
            {
                Id = safeId,
                Source = source,
                Type = type,
                Priority = safePriority,
                Title = string.IsNullOrWhiteSpace(title)
                    ? "گزارش سیستم"
                    : title,
                Message = string.IsNullOrWhiteSpace(message)
                    ? "-"
                    : message,
                TechnicalDetails = technicalDetails ?? string.Empty,
                Sticky = sticky,
                CanClose = canClose,
                ShowRetry = showRetry && retryAction != null,
                RetryAction = retryAction,
                ShowRelogin = showRelogin && reloginAction != null,
                ReloginAction = reloginAction,
                CreatedAt = Time.unscaledTime,
                ExpiresAt = expiresAt,
                Revision = ++messageRevision
            };

            activeMessages[safeId] = entry;

            // اگر همان پیام قبلاً توسط کاربر مخفی شده بود، نسخه جدید آن
            // دوباره اجازه نمایش دارد.
            if (dismissedMessageId == safeId &&
                dismissedMessageRevision != entry.Revision)
            {
                dismissedMessageId = string.Empty;
                dismissedMessageRevision = 0;
            }

            NetworkFileLogger.Info(
                "GLOBAL_MESSAGE",
                "Published id=" + safeId +
                " | source=" + source +
                " | type=" + type +
                " | priority=" + safePriority +
                " | sticky=" + sticky +
                " | duration=" + durationSeconds
            );

            RenderHighestPriorityMessage();
            return safeId;
        }

        private bool ClearInternal(string messageId)
        {
            if (!activeMessages.Remove(messageId))
            {
                return false;
            }

            if (dismissedMessageId == messageId)
            {
                dismissedMessageId = string.Empty;
                dismissedMessageRevision = 0;
            }

            NetworkFileLogger.Info(
                "GLOBAL_MESSAGE",
                "Cleared id=" + messageId
            );

            RenderHighestPriorityMessage();
            return true;
        }

        private int ClearFromSourceInternal(MessageSource source)
        {
            List<string> ids = new List<string>();

            foreach (KeyValuePair<string, MessageEntry> pair in activeMessages)
            {
                MessageEntry entry = pair.Value;

                if (entry != null && entry.Source == source)
                {
                    ids.Add(pair.Key);
                }
            }

            for (int i = 0; i < ids.Count; i++)
            {
                activeMessages.Remove(ids[i]);

                if (dismissedMessageId == ids[i])
                {
                    dismissedMessageId = string.Empty;
                    dismissedMessageRevision = 0;
                }
            }

            if (ids.Count > 0)
            {
                NetworkFileLogger.Info(
                    "GLOBAL_MESSAGE",
                    "Cleared source=" + source +
                    " | count=" + ids.Count
                );

                RenderHighestPriorityMessage();
            }

            return ids.Count;
        }

        private int ClearAllInternal()
        {
            int count = activeMessages.Count;

            activeMessages.Clear();
            dismissedMessageId = string.Empty;
            dismissedMessageRevision = 0;

            RenderHighestPriorityMessage();

            NetworkFileLogger.Info(
                "GLOBAL_MESSAGE",
                "Cleared all managed messages. count=" + count
            );

            return count;
        }

        private void RemoveExpiredMessages(float now)
        {
            if (activeMessages.Count == 0) return;

            List<string> expiredIds = null;

            foreach (KeyValuePair<string, MessageEntry> pair in activeMessages)
            {
                MessageEntry entry = pair.Value;

                if (entry == null ||
                    entry.ExpiresAt <= 0f ||
                    now < entry.ExpiresAt)
                {
                    continue;
                }

                if (expiredIds == null)
                {
                    expiredIds = new List<string>();
                }

                expiredIds.Add(pair.Key);
            }

            if (expiredIds == null || expiredIds.Count == 0) return;

            for (int i = 0; i < expiredIds.Count; i++)
            {
                string id = expiredIds[i];
                activeMessages.Remove(id);

                if (dismissedMessageId == id)
                {
                    dismissedMessageId = string.Empty;
                    dismissedMessageRevision = 0;
                }
            }

            NetworkFileLogger.Info(
                "GLOBAL_MESSAGE",
                "Expired messages removed. count=" + expiredIds.Count
            );

            RenderHighestPriorityMessage();
        }

        private MessageEntry FindHighestPriorityMessage()
        {
            MessageEntry best = null;

            foreach (KeyValuePair<string, MessageEntry> pair in activeMessages)
            {
                MessageEntry entry = pair.Value;
                if (entry == null) continue;

                if (best == null ||
                    entry.Priority > best.Priority ||
                    (entry.Priority == best.Priority &&
                     entry.Revision > best.Revision))
                {
                    best = entry;
                }
            }

            return best;
        }

        private bool HasActiveNetworkPriorityMessageInternal()
        {
            foreach (KeyValuePair<string, MessageEntry> pair in activeMessages)
            {
                MessageEntry entry = pair.Value;

                if (entry != null &&
                    entry.Source == MessageSource.Network &&
                    entry.Priority >= Priorities.NetworkRecovering)
                {
                    return true;
                }
            }

            return false;
        }

        private void RenderHighestPriorityMessage()
        {
            MessageEntry previous = displayedMessage;
            MessageEntry best = FindHighestPriorityMessage();

            displayedMessage = best;

            if (best == null)
            {
                displayedMessageId = string.Empty;
                displayedMessageRevision = 0;

                if (managerOwnsCurrentPanel &&
                    hidePanelWhenManagedMessagesFinish)
                {
                    SchedulePanelHideAfterMessageHandoff();
                }
                else
                {
                    CancelPendingPanelHide();
                    managerOwnsCurrentPanel = false;
                }

                NotifyDisplayedMessageChanged(
                    previous != null ? previous.Id : string.Empty,
                    string.Empty
                );

                return;
            }

            CancelPendingPanelHide();
            displayedMessageId = best.Id;
            displayedMessageRevision = best.Revision;

            bool isDismissed =
                dismissedMessageId == best.Id &&
                dismissedMessageRevision == best.Revision;

            if (isDismissed)
            {
                if (managerOwnsCurrentPanel)
                {
                    SetPanelVisible(false);
                }

                NotifyDisplayedMessageChanged(
                    previous != null ? previous.Id : string.Empty,
                    best.Id
                );

                return;
            }

            ApplyMessageToPanel(best);

            NotifyDisplayedMessageChanged(
                previous != null ? previous.Id : string.Empty,
                best.Id
            );
        }

        private void NotifyDisplayedMessageChanged(
            string previousId,
            string currentId
        )
        {
            if (string.Equals(
                    previousId,
                    currentId,
                    StringComparison.Ordinal))
            {
                return;
            }

            Action<string> handler = OnDisplayedMessageChanged;
            if (handler == null) return;

            try
            {
                handler(currentId ?? string.Empty);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception(
                    "GLOBAL_MESSAGE_CHANGED_EVENT",
                    ex
                );
            }
        }

        private void ApplyMessageToPanel(MessageEntry entry)
        {
            if (entry == null) return;

            CancelPendingPanelHide();

            if (!HasValidPanelReference()) return;

            if (txt_ServerDebugTitle != null)
            {
                txt_ServerDebugTitle.text = entry.Title ?? string.Empty;
            }

            if (txt_ServerDebugMessage != null)
            {
                txt_ServerDebugMessage.text = entry.Message ?? string.Empty;
            }

            if (txt_ServerDebugTechnical != null)
            {
                txt_ServerDebugTechnical.text =
                    showTechnicalDetails
                        ? entry.TechnicalDetails ?? string.Empty
                        : string.Empty;
            }

            ConfigureButton(
                btn_Close,
                entry.CanClose,
                entry.CanClose
            );

            ConfigureButton(
                btn_Retry,
                entry.ShowRetry && entry.RetryAction != null,
                !isActionRunning
            );

            ConfigureButton(
                btn_Relogin,
                entry.ShowRelogin && entry.ReloginAction != null,
                !isActionRunning
            );

            managerOwnsCurrentPanel = true;
            SetPanelVisible(true);
        }

        private void EnsureDisplayedMessageOwnsPanel()
        {
            if (displayedMessage == null) return;


            if (!HasValidPanelReference()) return;

            bool isDismissed =
                dismissedMessageId == displayedMessageId &&
                dismissedMessageRevision == displayedMessageRevision;

            if (isDismissed)
            {
                if (managerOwnsCurrentPanel &&
                    pnl_ServerDebug.activeSelf)
                {
                    SetPanelVisible(false);
                }

                return;
            }

            bool requiresRender = !pnl_ServerDebug.activeSelf;

            if (txt_ServerDebugTitle != null &&
                txt_ServerDebugTitle.text !=
                (displayedMessage.Title ?? string.Empty))
            {
                requiresRender = true;
            }

            if (txt_ServerDebugMessage != null &&
                txt_ServerDebugMessage.text !=
                (displayedMessage.Message ?? string.Empty))
            {
                requiresRender = true;
            }

            string expectedTechnical = showTechnicalDetails
                ? displayedMessage.TechnicalDetails ?? string.Empty
                : string.Empty;

            if (txt_ServerDebugTechnical != null &&
                txt_ServerDebugTechnical.text != expectedTechnical)
            {
                requiresRender = true;
            }

            if (requiresRender)
            {
                ApplyMessageToPanel(displayedMessage);
            }
        }

        //* این تابع خاموش‌شدن پنل را کمی عقب می‌اندازد تا پیام مرحله بعد بدون خاموش و روشن‌شدن پنل جایگزین شود.
        private void SchedulePanelHideAfterMessageHandoff()
        {
            float delaySeconds = Mathf.Max(0f, managedMessageHandoffDelaySeconds);

            if (delaySeconds <= 0f)
            {
                panelHidePending = false;
                panelHideAt = 0f;
                SetPanelVisible(false);
                managerOwnsCurrentPanel = false;
                return;
            }

            panelHidePending = true;
            panelHideAt = Time.unscaledTime + delaySeconds;
        }

        //* این تابع خاموش‌شدن معلق پنل را هنگام رسیدن پیام بعدی لغو می‌کند.
        private void CancelPendingPanelHide()
        {
            panelHidePending = false;
            panelHideAt = 0f;
        }

        //* این تابع پس از پایان مهلت تحویل، فقط وقتی هنوز هیچ پیام فعالی نیست پنل را خاموش می‌کند.
        private void ProcessPendingPanelHide(float now)
        {
            if (!panelHidePending) return;

            if (displayedMessage != null)
            {
                CancelPendingPanelHide();
                return;
            }

            if (now < panelHideAt) return;

            panelHidePending = false;
            panelHideAt = 0f;

            if (managerOwnsCurrentPanel &&
                hidePanelWhenManagedMessagesFinish)
            {
                SetPanelVisible(false);
            }

            managerOwnsCurrentPanel = false;
        }

        private void ConfigureButton(
            Button button,
            bool visible,
            bool interactable
        )
        {
            if (button == null) return;

            button.gameObject.SetActive(visible);
            button.interactable = visible && interactable;
        }

        private void SetPanelVisible(bool visible)
        {
            if (pnl_ServerDebug != null)
            {
                pnl_ServerDebug.SetActive(visible);
            }
        }

        private void HandleCloseClicked()
        {
            MessageEntry current = displayedMessage;

            if (current == null ||
                !current.CanClose ||
                isActionRunning)
            {
                return;
            }

            if (current.Sticky)
            {
                // پیام Sticky حذف نمی‌شود؛ فقط همان Revision مخفی می‌شود.
                // پیام‌های کم‌اولویت‌تر نیز جای آن را نمی‌گیرند.
                dismissedMessageId = current.Id;
                dismissedMessageRevision = current.Revision;

                CancelPendingPanelHide();
                SetPanelVisible(false);
                return;
            }

            ClearInternal(current.Id);
        }

        private async void HandleRetryClicked()
        {
            MessageEntry current = displayedMessage;

            if (current == null ||
                !current.ShowRetry ||
                current.RetryAction == null ||
                isActionRunning)
            {
                return;
            }

            isActionRunning = true;
            ApplyMessageToPanel(current);

            try
            {
                await current.RetryAction();
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception(
                    "GLOBAL_MESSAGE_RETRY",
                    ex
                );
            }
            finally
            {
                isActionRunning = false;
                RenderHighestPriorityMessage();
            }
        }

        private async void HandleReloginClicked()
        {
            MessageEntry current = displayedMessage;

            if (current == null ||
                !current.ShowRelogin ||
                current.ReloginAction == null ||
                isActionRunning)
            {
                return;
            }

            isActionRunning = true;
            ApplyMessageToPanel(current);

            try
            {
                await current.ReloginAction();
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception(
                    "GLOBAL_MESSAGE_RELOGIN",
                    ex
                );
            }
            finally
            {
                isActionRunning = false;
                RenderHighestPriorityMessage();
            }
        }

        //* این تابع معتبر بودن مرجع اصلی رابط پیام سراسری را بررسی می‌کند.
        private bool HasValidPanelReference()
        {
            return pnl_ServerDebug != null;
        }

        //* این تابع شنونده دکمه‌های رابط پیام دائمی را فقط روی مراجع دستی ثبت می‌کند.
        private void SetupPanelButtonListeners()
        {
            DetachPanelButtonListeners();

            if (btn_Close != null)
            {
                btn_Close.onClick.AddListener(
                    HandleCloseClicked
                );
            }

            if (btn_Retry != null)
            {
                btn_Retry.onClick.AddListener(
                    HandleRetryClicked
                );
            }

            if (btn_Relogin != null)
            {
                btn_Relogin.onClick.AddListener(
                    HandleReloginClicked
                );
            }
        }

        //* این تابع شنونده دکمه‌های رابط پیام دائمی را پیش از نابودی یا ثبت دوباره آزاد می‌کند.
        private void DetachPanelButtonListeners()
        {
            if (btn_Close != null)
            {
                btn_Close.onClick.RemoveListener(
                    HandleCloseClicked
                );
            }

            if (btn_Retry != null)
            {
                btn_Retry.onClick.RemoveListener(
                    HandleRetryClicked
                );
            }

            if (btn_Relogin != null)
            {
                btn_Relogin.onClick.RemoveListener(
                    HandleReloginClicked
                );
            }
        }

        private sealed class MessageEntry
        {
            public string Id;
            public MessageSource Source;
            public MessageType Type;
            public int Priority;
            public string Title;
            public string Message;
            public string TechnicalDetails;
            public bool Sticky;
            public bool CanClose;
            public bool ShowRetry;
            public Func<Task> RetryAction;
            public bool ShowRelogin;
            public Func<Task> ReloginAction;
            public float CreatedAt;
            public float ExpiresAt;
            public long Revision;
        }
    }
}
