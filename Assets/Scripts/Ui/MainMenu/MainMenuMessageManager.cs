using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using RTLTMPro;
using UnityEngine.UI;
using Network_A.Core;

namespace Project.UI.MainMenu
{
    //* این اسکریپت خودش باز و بسته می شود و با مین منو پنل منجیر کاری ندارد 
    //* یعنی در روی همه منو ها باز شده و خودش و یا با دکمه خودش بسته می شود 
    /// <summary>
    /// مدیریت پیام‌ها به صورت مستقل و سراسری (بدون نیاز به EventManager)
    /// ✅ Singleton + Static API + Thread-Safe Queue
    /// </summary>
    public class MainMenuMessageManager : MonoBehaviour
    {
        #region Enums & Types

        /// <summary>
        /// انواع پیام‌ها (محصور در این کلاس)
        /// </summary>
        public enum MessageType { Info, Warning, Error, Success }

        private readonly struct MessageData
        {
            public string Text { get; }
            public MessageType Type { get; }
            public Action OnButtonClick { get; }

            public MessageData(string text, MessageType type, Action onButtonClick)
            {
                Text = text;
                Type = type;
                OnButtonClick = onButtonClick;
            }
        }

        #endregion

        #region Singleton

        private static MainMenuMessageManager _instance;

        public static MainMenuMessageManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<MainMenuMessageManager>();
                    if (_instance == null)
                    {
                        // تلاش برای ساخت خودکار در صورت عدم وجود
                        var canvas = FindFirstObjectByType<Canvas>();
                        if (canvas != null)
                        {
                            var obj = new GameObject("[MainMenuMessageManager]");
                            obj.transform.SetParent(canvas.transform, false);
                            _instance = obj.AddComponent<MainMenuMessageManager>();
                        }
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Serialized Fields

        [Header("🎨 UI References")]
        [SerializeField] private GameObject messagePopup;
        [SerializeField] private CanvasGroup messagePanel;
        [SerializeField] private RTLTextMeshPro messageText;
        [SerializeField] private Image messageIcon;
        [SerializeField] private Button actionButton;

        [Header("⚙️ Configuration")]
        [SerializeField]
        private Color[] typeColors = new Color[]
        {
            new Color(0.2f, 0.6f, 0.9f, 1f),  // Info
            new Color(0.9f, 0.7f, 0.1f, 1f),  // Warning
            new Color(0.9f, 0.2f, 0.2f, 1f),  // Error
            new Color(0.2f, 0.8f, 0.4f, 1f)   // Success
        };

        [SerializeField][Range(1f, 10f)] private float showDuration = 3f;
        [SerializeField][Range(0.1f, 1f)] private float fadeInDuration = 0.25f;
        [SerializeField][Range(0.1f, 1f)] private float fadeOutDuration = 0.25f;
        [SerializeField][Range(5f, 30f)] private float buttonTimeout = 10f;
        [SerializeField] private bool dontDestroyOnLoad = false;

        #endregion

        #region Private Fields

        private readonly Queue<MessageData> messageQueue = new();
        private bool _isProcessing = false;
        private readonly object _lock = new();
        private Action _currentClickAction;
        private Coroutine _currentRoutine;
        private bool _isInitialized = false;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
            Init();
        }

        private void OnDestroy()
        {
            if (actionButton != null) actionButton.onClick.RemoveListener(OnButtonClicked);
            if (_instance == this) _instance = null;
        }

        private void Init()
        {
            if (_isInitialized) return;
            _isInitialized = true;

            messagePopup.SetActive(false);

            if (actionButton != null)
            {
                actionButton.onClick.AddListener(OnButtonClicked);
                actionButton.gameObject.SetActive(false);
            }
        }

        #endregion

        #region Public Static API (دسترسی سریع از هر جا)

        /// <summary>
        /// نمایش پیام (روش اصلی)
        /// </summary>
        public static void Show(string text, MessageType type = MessageType.Info, Action onButtonClick = null)
        {
            if (Instance != null)
                Instance.Enqueue(text, type, onButtonClick);
        }

        // --- شورت‌کات‌های سریع ---
        public static void Info(string text) => Show(text, MessageType.Info);
        public static void Warning(string text) => Show(text, MessageType.Warning);
        public static void Error(string text, Action retryAction = null) => Show(text, MessageType.Error, retryAction);
        public static void Success(string text) => Show(text, MessageType.Success);

        /// <summary>
        /// نمایش فوری (پاک کردن صف قبلی)
        /// </summary>
        public static void ShowImmediate(string text, MessageType type, Action action = null)
        {
            if (Instance != null)
            {
                Instance.ClearQueue();
                Instance.Enqueue(text, type, action);
            }
        }

        public static void Clear() => Instance?.ClearQueue();

        #endregion

        #region Logic
        //پیام جدید را گرفته، داخل صف قرار دهد، و اگر سیستم آزاد بود نمایش را شروع کند.
        private void Enqueue(string text, MessageType type, Action action)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (_lock) messageQueue.Enqueue(new MessageData(text, type, action));//اگر چند Thread همزمان خواستند پیام بفرستند، فقط یکی یکی اجازه ورود دارند.
            TryProcess();
        }
        //بررسی کند آیا الان می‌توان پیام بعدی را از صف برداشت و نمایش داد یا نه.
        private void TryProcess()
        {
            if (_isProcessing || messageQueue.Count == 0) return;
            _isProcessing = true;
            lock (_lock)
            {
                if (messageQueue.Count > 0)
                    _currentRoutine = CoroutineRunner_A.Run(ProcessMessage(messageQueue.Dequeue()));//نمایش پیام شروع شود.
            }
        }
        /*
        ✅ آماده می‌کند
        ✅ نمایش می‌دهد
        ✅ صبر می‌کند
        ✅ مخفی می‌کند
        ✅ پیام بعدی را اجرا می‌کند */
        private IEnumerator ProcessMessage(MessageData data)
        {
            // ✅ فعال‌سازی پنل قبل از شروع انیمیشن
            if (!messagePopup.activeSelf)
                messagePopup.SetActive(true);

            SetupUI(data);
            messagePanel.alpha = 0f;

            yield return FadeTo(1f, fadeInDuration);

            if (data.OnButtonClick != null)//یعنی آیا این پیام دکمه دارد؟
                yield return WaitForClickOrTimeout();// صبر کن کار دکمه را بزند یا زمان تایم اوت تموم شود 
            else
                yield return new WaitForSecondsRealtime(showDuration);// در پنل ساده به مدت زمان مشخص صبر کن و بعد محو شو

            yield return FadeTo(0f, fadeOutDuration);//پنل آرام محو شود.

            // ✅ غیرفعال کردن پنل بعد از اتمام
            messagePopup.SetActive(false);

            _currentClickAction = null;
            _isProcessing = false;
            _currentRoutine = null;
            TryProcess();//اگر پیام دیگری در صف است، شروع شود.
        }

        private void SetupUI(MessageData data)
        {
            messageText.text = data.Text;
            int idx = (int)data.Type;
            if (messageIcon != null && idx < typeColors.Length)
                messageIcon.color = typeColors[idx];

            if (actionButton != null)
            {
                _currentClickAction = data.OnButtonClick;
                actionButton.gameObject.SetActive(data.OnButtonClick != null);

                if (data.OnButtonClick != null)
                {
                    var btnTxt = actionButton.GetComponentInChildren<RTLTextMeshPro>();
                    if (btnTxt != null) btnTxt.text = GetBtnText(data.Type);
                }
            }
        }

        private IEnumerator WaitForClickOrTimeout()
        {
            float t = 0f;
            while (t < buttonTimeout && _currentClickAction != null)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            _currentClickAction = null;
        }

        private IEnumerator FadeTo(float target, float dur)
        {
            float start = messagePanel.alpha;
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                messagePanel.alpha = Mathf.Lerp(start, target, Mathf.SmoothStep(0, 1, t / dur));
                yield return null;
            }
            messagePanel.alpha = target;
        }
        /*   
         کاربر کلیک کرد
        ↓
        OnButtonClicked()

        1- اکشن اجرا شد
        2- اکشن پاک شد
        3- سیستم آزاد شد
        4- پیام بعدی شروع شد */
        private void OnButtonClicked()
        {
            _currentClickAction?.Invoke();
            _currentClickAction = null;
            _isProcessing = false;
            TryProcess();
        }

        private string GetBtnText(MessageType type) => type switch
        {
            MessageType.Error => "تلاش مجدد",
            MessageType.Warning => "متوجه شدم",
            _ => "بستن"
        };

        private void ClearQueue()
        {
            lock (_lock) { messageQueue.Clear(); _currentClickAction = null; }
            if (_currentRoutine != null) CoroutineRunner_A.Stop(_currentRoutine);
            _isProcessing = false;
            if (messagePanel != null) messagePanel.alpha = 0f;

            // ✅ اضافه کردن این خط برای اطمینان از غیرفعال شدن قطعی
            if (messagePopup.activeSelf)
                messagePopup.SetActive(false);
        }

        #endregion

#if UNITY_EDITOR
        [UnityEngine.ContextMenu("Test: Error with Button")]
        void Test() => Error("تست خطا!", () => Debug.Log("Retry Clicked"));
#endif
    }
}