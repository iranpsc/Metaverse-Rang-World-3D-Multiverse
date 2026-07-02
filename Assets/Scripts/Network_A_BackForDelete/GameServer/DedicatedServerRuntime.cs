using System;
using UnityEngine;

namespace Network_A.GameServer
{
    public class DedicatedServerRuntime : MonoBehaviour
    {
        public static DedicatedServerRuntime Instance { get; private set; }

        [Header("Config")]
        [SerializeField] private DedicatedServerConfig config;

        public bool IsRunning { get; private set; }
        public DedicatedServerConfigData CurrentConfig { get; private set; }

        public event Action<DedicatedServerConfigData> RuntimeStarted;
        public event Action RuntimeStopped;
        public event Action<string> RuntimeFailed;

        //* این تابع سینگلتون ساده ران تایم ددیکیتد سرور را آماده می کند.
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                EnsureConfigReference();
                return;
            }

            Destroy(gameObject);
        }

        //* این تابع در شروع صحنه بررسی می کند که ددیکیتد سرور باید خودکار شروع شود یا نه.
        private void Start()
        {
            EnsureConfigReference();

            if (config == null)
            {
                FailRuntime("DedicatedServerConfig not found.");
                return;
            }

            config.ApplyRuntimeOverridesIfNeeded();

            if (config.ShouldAutoStartRuntime())
            {
                StartDedicatedRuntime();
            }
            else
            {
                Log("Auto start is disabled or blocked until a real dedicated server start signal exists.");
            }
        }

        //* این تابع رفرنس کانفیگ را از همین آبجکت یا فرزندان آن پیدا می کند.
        private void EnsureConfigReference()
        {
            if (config != null) return;

            config = GetComponent<DedicatedServerConfig>();
            if (config != null) return;

            config = GetComponentInChildren<DedicatedServerConfig>(true);
        }


        public bool StartDedicatedRuntimeWithRoomContext(string roomId, string roomName)
        {
            if (!TryUpdateRuntimeRoom(roomId, roomName, out string error))
            {
                FailRuntime(error);
                return false;
            }

            StartDedicatedRuntime();
            return IsRunning;
        }

        //* این تابع از اینسپکتور یا دکمه تست برای شروع ران تایم ددیکیتد سرور استفاده می شود.
        [ContextMenu("Start Dedicated Runtime")]
        public void StartDedicatedRuntime()
        {
            if (IsRunning)
            {
                Log("Dedicated runtime is already running.");
                return;
            }

            EnsureConfigReference();

            if (config == null)
            {
                FailRuntime("DedicatedServerConfig is missing.");
                return;
            }

            config.ApplyRuntimeOverridesIfNeeded();

            if (!config.ValidateForRuntime(out string error))
            {
                FailRuntime(error);
                return;
            }

            CurrentConfig = config.CreateSnapshot();
            IsRunning = true;

            Log("Dedicated runtime started.");
            Log(config.ToDebugText());

            RuntimeStarted?.Invoke(CurrentConfig);
        }

        //* این تابع از اینسپکتور یا کد برای توقف ران تایم ددیکیتد سرور استفاده می شود.
        [ContextMenu("Stop Dedicated Runtime")]
        public void StopDedicatedRuntime()
        {
            if (!IsRunning)
            {
                Log("Dedicated runtime is already stopped.");
                return;
            }

            IsRunning = false;

            Log("Dedicated runtime stopped.");
            RuntimeStopped?.Invoke();
        }

        public bool TryUpdateRuntimeRoom(string roomId, string roomName, out string error)
        {
            error = string.Empty;
            EnsureConfigReference();

            if (config == null)
            {
                error = "DedicatedServerConfig is missing.";
                return false;
            }

            string safeRoomId = SafeTrim(roomId);
            string safeRoomName = SafeTrim(roomName);

            if (string.IsNullOrWhiteSpace(safeRoomId))
            {
                if (CurrentConfig == null) CurrentConfig = config.CreateSnapshot();
                Log("Runtime room update skipped because roomId is empty.");
                return true;
            }

            if (IsRunning)
            {
                string runningRoomId = CurrentConfig == null ? string.Empty : CurrentConfig.roomId;
                string safeRunningRoomId = SafeTrim(runningRoomId);

                if (!string.IsNullOrWhiteSpace(safeRunningRoomId) &&
                    !string.Equals(safeRunningRoomId, safeRoomId, StringComparison.Ordinal))
                {
                    error = "Dedicated runtime is already running for another room.";
                    return false;
                }
            }

            config.ApplyRealtimeRoom(safeRoomId, safeRoomName);
            CurrentConfig = config.CreateSnapshot();
            Log("Runtime room updated | roomId=" + CurrentConfig.roomId + " | roomName=" + CurrentConfig.roomName);
            return true;
        }

        public void RefreshRuntimeConfigSnapshot()
        {
            EnsureConfigReference();
            if (config == null) return;
            config.ApplyRuntimeOverridesIfNeeded();
            CurrentConfig = config.CreateSnapshot();
        }

        public DedicatedServerConfig GetConfigReference()
        {
            EnsureConfigReference();
            return config;
        }

        //* این تابع آخرین کانفیگ معتبر ران تایم را برمی گرداند.
        public DedicatedServerConfigData GetCurrentConfig()
        {
            if (CurrentConfig != null) return CurrentConfig;
            if (config == null) EnsureConfigReference();

            return config != null ? config.CreateSnapshot() : null;
        }

        //* این تابع مشخص می کند که ران تایم برای تست ویندوز ادیتور فعال است یا نه.
        public bool IsWindowsEditorTestRuntime()
        {
            DedicatedServerConfigData snapshot = GetCurrentConfig();
            return snapshot != null && snapshot.runMode == DedicatedServerRunMode.WindowsEditorTest;
        }

        //* این تابع مشخص می کند که ران تایم برای بیلد لینوکس هدلس فعال است یا نه.
        public bool IsLinuxHeadlessRuntime()
        {
            DedicatedServerConfigData snapshot = GetCurrentConfig();
            return snapshot != null && snapshot.runMode == DedicatedServerRunMode.LinuxHeadlessServer;
        }

        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع خطای شروع یا اجرای ران تایم را لاگ می کند و به شنونده ها خبر می دهد.
        private void FailRuntime(string error)
        {
            IsRunning = false;
            CurrentConfig = null;

            Debug.LogError("[DedicatedServerRuntime] Failed | " + error);
            RuntimeFailed?.Invoke(error);
        }

        //* این تابع لاگ های معمولی ددیکیتد سرور را در کنسول یونیتی چاپ می کند.
        private void Log(string message)
        {
            DedicatedServerConfigData snapshot = CurrentConfig ?? config?.CreateSnapshot();

            if (snapshot != null && !snapshot.verboseLogs) return;

            Debug.Log("[DedicatedServerRuntime] " + message);
        }

        //* این تابع هنگام خروج یا حذف آبجکت، ران تایم را تمیز متوقف می کند.
        private void OnDestroy()
        {
            if (Instance == this)
            {
                if (IsRunning) StopDedicatedRuntime();
                Instance = null;
            }
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت نقطه شروع ددیکیتد سرور یونیتی است.
        در فاز 11 کانفیگ را از اینسپکتور، کامندلاین یا انوایرومنت می خواند.
        اتو استارت در ادیتور بدون سیگنال واقعی سرور بلاک می شود تا تست اصلی با بیلد ویندوز انجام شود.
        سپس تنظیمات واقعی روم را نگه می دارد و رویداد RuntimeStarted را برای رجیستر، هارت بیت و لیسنر فعال می کند.
        فازهای بعدی با گوش دادن به رویداد RuntimeStarted به این ران تایم وصل می شوند.
        این فایل باید روی همان آبجکتی باشد که DedicatedServerConfig روی آن قرار دارد.
        */
    }
}
