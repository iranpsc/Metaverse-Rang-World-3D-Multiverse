using Network_A.DedicatedGameServer.Client;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.UI
{
    public class WebGLEnvironmentGameServerUIController : MonoBehaviour
    {
        #region رفرنس های رابط کاربری

        [Header("UI References")]
        [SerializeField] private Button disconnectGameServerButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI gameServerStateText;

        #endregion

        #region تنظیمات دیباگ

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        #endregion

        #region متغیرهای داخلی

        private DedicatedGameServerRealtimeRoomBinderWebGL binder;

        #endregion

        #region چرخه حیات

        //* این تابع رابط صحنه را در حالت اولیه امن آماده می کند.
        private void Awake()
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = false;
        }

        //* این تابع دکمه قطع اتصال و رویدادهای بایندر را فعال می کند.
        private void OnEnable()
        {
            BindDisconnectButton();
            BindBinder();
        }

        //* این تابع پس از آماده شدن کامل صحنه، رفرنس بایندر دائمی را دوباره بررسی می کند.
        private void Start()
        {
            if (binder == null) BindBinder();
        }

        //* این تابع هنگام خروج از صحنه همه رویدادها و دکمه ها را آزاد می کند.
        private void OnDisable()
        {
            UnbindBinder();
            UnbindDisconnectButton();
        }

        #endregion

        #region اتصال دکمه

        //* این تابع دکمه قطع اتصال را به تابع محلی وصل می کند.
        private void BindDisconnectButton()
        {
            if (disconnectGameServerButton == null) return;
            disconnectGameServerButton.onClick.RemoveListener(Btn_DisconnectGameServer);
            disconnectGameServerButton.onClick.AddListener(Btn_DisconnectGameServer);
        }

        //* این تابع اتصال دکمه قطع اتصال را پاک می کند.
        private void UnbindDisconnectButton()
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.onClick.RemoveListener(Btn_DisconnectGameServer);
        }

        #endregion

        #region اتصال بایندر دائمی

        //* این تابع کنترلر رابط صحنه را به نمونه دائمی بایندر متصل می کند.
        private void BindBinder()
        {
            DedicatedGameServerRealtimeRoomBinderWebGL currentBinder = DedicatedGameServerRealtimeRoomBinderWebGL.Instance;

            if (currentBinder == null)
            {
                ApplyMissingBinderState();
                Log("Persistent DedicatedGameServerRealtimeRoomBinderWebGL instance is missing.");
                return;
            }

            if (binder == currentBinder)
            {
                ApplyCurrentBinderState();
                return;
            }

            UnbindBinder();
            binder = currentBinder;
            binder.StatusChanged += HandleStatusChanged;
            binder.GameServerStateChanged += HandleGameServerStateChanged;
            binder.DisconnectAvailabilityChanged += HandleDisconnectAvailabilityChanged;
            ApplyCurrentBinderState();
            Log("Scene UI connected to the persistent DedicatedGameServerRealtimeRoomBinderWebGL.");
        }

        //* این تابع رویدادهای رابط صحنه را از بایندر دائمی جدا می کند.
        private void UnbindBinder()
        {
            if (binder == null) return;
            binder.StatusChanged -= HandleStatusChanged;
            binder.GameServerStateChanged -= HandleGameServerStateChanged;
            binder.DisconnectAvailabilityChanged -= HandleDisconnectAvailabilityChanged;
            binder = null;
        }

        #endregion

        #region نمایش وضعیت

        //* این تابع وضعیت فعلی بایندر را بلافاصله روی رابط صحنه اعمال می کند.
        private void ApplyCurrentBinderState()
        {
            if (binder == null)
            {
                ApplyMissingBinderState();
                return;
            }

            HandleStatusChanged(binder.CurrentStatus);
            HandleGameServerStateChanged(binder.CurrentGameServerState);
            HandleDisconnectAvailabilityChanged(binder.CanDisconnect);
        }

        //* این تابع هنگام نبود بایندر دائمی، رابط صحنه را در حالت امن قرار می دهد.
        private void ApplyMissingBinderState()
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = false;
            if (statusText != null) statusText.text = "مدیر اتصال گیم سرور در دسترس نیست.";
            if (gameServerStateText != null) gameServerStateText.text = string.Empty;
        }

        //* این تابع متن وضعیت عمومی اتصال را از بایندر دریافت و نمایش می دهد.
        private void HandleStatusChanged(string value)
        {
            if (statusText != null) statusText.text = Safe(value);
        }

        //* این تابع وضعیت داخل یا خارج بودن گیم سرور را از بایندر دریافت و نمایش می دهد.
        private void HandleGameServerStateChanged(string value)
        {
            if (gameServerStateText != null) gameServerStateText.text = Safe(value);
        }

        //* این تابع فعال یا غیرفعال بودن دکمه قطع اتصال را از بایندر دریافت می کند.
        private void HandleDisconnectAvailabilityChanged(bool canDisconnect)
        {
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = canDisconnect;
        }

        #endregion

        #region عملیات دکمه

        //* این تابع کلیک دکمه قطع اتصال را به بایندر دائمی ارسال می کند.
        public void Btn_DisconnectGameServer()
        {
            if (binder == null) BindBinder();

            if (binder == null)
            {
                ApplyMissingBinderState();
                return;
            }

            binder.Btn_DisconnectGameServer();
        }

        #endregion

        #region ابزارهای داخلی

        //* این تابع مقدار متنی را برای نمایش امن آماده می کند.
        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع لاگ های کنترلر رابط صحنه را ثبت می کند.
        private void Log(string message)
        {
            if (verboseLogs) Debug.Log("[WebGLEnvironmentGameServerUIController] " + message);
        }

        #endregion
    }
}
