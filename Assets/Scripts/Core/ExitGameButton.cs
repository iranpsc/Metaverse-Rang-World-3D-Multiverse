using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Bootstrap;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.UI;

public class ExitGameButton : MonoBehaviour
{
    #region Singleton و عمر سراسری

    public static ExitGameButton Instance { get; private set; }

    [Header("Lifetime")]
    [Tooltip("ریشه اختصاصی Canvas دکمه خروج که باید از Login تا پایان محیط سه بعدی باقی بماند.")]
    [SerializeField] private GameObject persistentRoot;
    [SerializeField] private bool persistAcrossScenes = true;
    [SerializeField] private bool destroyDuplicateRoot = true;

    [Header("UI")]
    [SerializeField] private Button exitButton;
    [SerializeField] private bool disableExitButtonAfterClick = true;

    private GameObject resolvedPersistentRoot;
    private bool exitRunning;
    private bool applicationQuitStarted;
    private readonly HashSet<int> scheduledRuntimeRootIds = new HashSet<int>();

    #endregion

    #region تنظیمات خروج امن

    [Header("Graceful Network Exit")]
    [SerializeField] private DedicatedGameServerRealtimeRoomBinder dedicatedGameServerBinder;
    [SerializeField] private bool autoFindDedicatedGameServerBinder = true;
    [SerializeField] private int gracefulExitTimeoutMs = 5000;
    [SerializeField] private bool destroyRuntimeNetworkManagersBeforeQuit = true;

    [Header("Windows Fallback")]
    [SerializeField] private bool forceKillWindowsBuild = true;
    [SerializeField] private int forceKillFallbackDelayMs = 2000;

    #endregion

    #region چرخه حیات

    //* این تابع مقدار Singleton باقی مانده از اجرای قبلی را پیش از شروع Play پاک می کند.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    //* این تابع فقط یک نمونه از دکمه خروج را نگه می دارد و ریشه اختصاصی آن را بین همه صحنه ها حفظ می کند.
    private void Awake()
    {
        resolvedPersistentRoot = ResolvePersistentRoot();

        if (Instance != null && Instance != this)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] Duplicate ExitGameButton detected and removed.");

            if (destroyDuplicateRoot && resolvedPersistentRoot != null && resolvedPersistentRoot != Instance.resolvedPersistentRoot)
            {
                Destroy(resolvedPersistentRoot);
            }
            else
            {
                Destroy(gameObject);
            }

            return;
        }

        Instance = this;

        if (persistAcrossScenes && resolvedPersistentRoot != null)
        {
            if (resolvedPersistentRoot.transform.parent != null)
            {
                UnityEngine.Debug.LogWarning("[ExitGameButton] Persistent Root must be a root GameObject. It was moved to the Scene root.");
                resolvedPersistentRoot.transform.SetParent(null, true);
            }

            DontDestroyOnLoad(resolvedPersistentRoot);
        }
    }

    //* این تابع هنگام فعال شدن رابط، دکمه خروج را فقط یک بار به تابع اصلی وصل می کند.
    private void OnEnable()
    {
        BindExitButton();
    }

    //* این تابع هنگام غیرفعال شدن رابط، Listener دکمه خروج را آزاد می کند.
    private void OnDisable()
    {
        UnbindExitButton();
    }

    //* این تابع شروع بسته شدن برنامه را ثبت می کند تا خروج دوباره اجرا نشود.
    private void OnApplicationQuit()
    {
        applicationQuitStarted = true;
        exitRunning = true;
    }

    //* این تابع هنگام نابودی نمونه اصلی، Listener و Singleton را پاک می کند.
    private void OnDestroy()
    {
        UnbindExitButton();
        if (Instance == this) Instance = null;
    }

    #endregion

    #region اتصال رابط کاربری

    //* این تابع ریشه اختصاصی دکمه خروج را از Inspector یا ریشه فعلی آبجکت مشخص می کند.
    private GameObject ResolvePersistentRoot()
    {
        if (persistentRoot != null)
        {
            bool containsThisComponent = persistentRoot == gameObject || transform.IsChildOf(persistentRoot.transform);
            if (containsThisComponent) return persistentRoot;

            UnityEngine.Debug.LogWarning("[ExitGameButton] Assigned Persistent Root does not contain ExitGameButton. Transform root will be used instead.");
        }

        return transform.root != null ? transform.root.gameObject : gameObject;
    }

    //* این تابع دکمه خروج را بدون Listener تکراری به تابع خروج متصل می کند.
    private void BindExitButton()
    {
        if (exitButton == null) return;
        exitButton.onClick.RemoveListener(ExitGame);
        exitButton.onClick.AddListener(ExitGame);
        exitButton.interactable = !exitRunning && !applicationQuitStarted;
    }

    //* این تابع Listener زمان اجرای دکمه خروج را حذف می کند.
    private void UnbindExitButton()
    {
        if (exitButton != null) exitButton.onClick.RemoveListener(ExitGame);
    }

    //* این تابع وضعیت قابل کلیک بودن دکمه خروج را تغییر می دهد.
    private void SetExitButtonInteractable(bool interactable)
    {
        if (exitButton != null) exitButton.interactable = interactable;
    }

    #endregion

    #region جریان اصلی خروج

    //* این تابع با یک کلیک، همه مسیرهای شبکه فعال را جمع می کند و سپس برنامه را می بندد.
    public async void ExitGame()
    {
        if (exitRunning || applicationQuitStarted)
        {
            UnityEngine.Debug.Log("[ExitGameButton] Exit ignored. Graceful exit is already running.");
            return;
        }

        exitRunning = true;
        if (disableExitButtonAfterClick) SetExitButtonInteractable(false);
        UnityEngine.Debug.Log("[ExitGameButton] ExitGame clicked.");

        try
        {
            await RunGracefulNetworkExitAsync();
        }
        catch (Exception cleanupError)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] Graceful network exit failed. Quit will continue | error=" + cleanupError.Message);
        }

#if UNITY_EDITOR
        UnityEngine.Debug.Log("[ExitGameButton] Editor graceful exit completed. Stopping play mode.");
        UnityEditor.EditorApplication.isPlaying = false;
        return;
#else
#if UNITY_STANDALONE_WIN
        ScheduleWindowsForceKillFallback();
#endif

        UnityEngine.Debug.Log("[ExitGameButton] Application.Quit called after graceful network exit.");
        Application.Quit();
#endif
    }

    //* این تابع خروج رسمی Dedicated و Room را اجرا می کند، اتصال Realtime باقی مانده را می بندد و مدیرهای دائمی را آزاد می کند.
    private async Task RunGracefulNetworkExitAsync()
    {
        Stopwatch exitTimer = Stopwatch.StartNew();
        int safeTimeoutMs = Mathf.Max(1000, gracefulExitTimeoutMs);

        await CloseVoiceRuntimeAsync(exitTimer, safeTimeoutMs);
        await CloseDedicatedGameServerAndRoomAsync(exitTimer, safeTimeoutMs);
        await CloseRemainingRealtimeRoomAsync(exitTimer, safeTimeoutMs);

        if (destroyRuntimeNetworkManagersBeforeQuit)
        {
            DestroyRuntimeNetworkManagers();
            await Task.Yield();
            await Task.Yield();
        }

        UnityEngine.Debug.Log("[ExitGameButton] Graceful runtime shutdown finished | elapsedMs=" + exitTimer.ElapsedMilliseconds + " | timeoutMs=" + safeTimeoutMs);
    }

    //* این تابع پیش از خروج Dedicated و Room، اتصال Voice فعال را با پیام پروتکلی DISCONNECT می‌بندد.
    private async Task CloseVoiceRuntimeAsync(Stopwatch exitTimer, int totalTimeoutMs)
    {
        VoiceClientRuntime voiceRuntime = FindObjectOfType<VoiceClientRuntime>(true);
        if (voiceRuntime == null)
        {
            UnityEngine.Debug.Log("[ExitGameButton] Voice runtime is not active in the current application stage.");
            return;
        }

        int remainingMs = Mathf.Max(0, totalTimeoutMs - (int)exitTimer.ElapsedMilliseconds);
        int voiceTimeoutMs = Mathf.Min(2000, remainingMs);
        if (voiceTimeoutMs <= 0)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] Graceful exit budget finished before Voice cleanup.");
            return;
        }

        try
        {
            Task<bool> cleanupTask = voiceRuntime.DisconnectGracefullyAsync(
                "user_exit_whole_game",
                voiceTimeoutMs,
                CancellationToken.None);

            bool completedInTime = await WaitForBooleanTaskWithinBudgetAsync(
                cleanupTask,
                exitTimer,
                totalTimeoutMs,
                "voice_cleanup");

            if (!completedInTime) return;

            UnityEngine.Debug.Log(
                "[ExitGameButton] Voice graceful exit completed | result=" + cleanupTask.Result);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogWarning(
                "[ExitGameButton] Voice graceful exit failed | error=" + error.Message);
        }
    }

    //* این تابع در صورت وجود بایندر، سوکت Dedicated را می بندد و خروج رسمی از Room را انجام می دهد.
    private async Task CloseDedicatedGameServerAndRoomAsync(Stopwatch exitTimer, int totalTimeoutMs)
    {
        ResolveDedicatedGameServerBinder();

        if (dedicatedGameServerBinder == null)
        {
            UnityEngine.Debug.Log("[ExitGameButton] Dedicated binder is not active in the current application stage.");
            return;
        }

        try
        {
            Task<bool> cleanupTask = dedicatedGameServerBinder.DisconnectGameServerAndLeaveRoomAsync("user_exit_whole_game");
            bool completedInTime = await WaitForBooleanTaskWithinBudgetAsync(cleanupTask, exitTimer, totalTimeoutMs, "dedicated_and_room_cleanup");

            if (!completedInTime) return;

            UnityEngine.Debug.Log("[ExitGameButton] Dedicated and room graceful exit completed | result=" + cleanupTask.Result);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] Dedicated and room graceful exit failed | error=" + error.Message);
        }
    }

    //* این تابع اگر پس از مرحله Dedicated هنوز داخل Room ریل تایم باشیم، خروج رسمی Room را به عنوان مسیر پشتیبان اجرا می کند.
    private async Task CloseRemainingRealtimeRoomAsync(Stopwatch exitTimer, int totalTimeoutMs)
    {
        RealtimeRoomGameServerManager realtimeManager = RealtimeRoomGameServerManager.Instance;
        if (realtimeManager == null || !realtimeManager.IsJoinedRoom) return;

        try
        {
            Task<bool> leaveTask = realtimeManager.LeaveCurrentRoomAsync(true);
            bool completedInTime = await WaitForBooleanTaskWithinBudgetAsync(leaveTask, exitTimer, totalTimeoutMs, "realtime_room_leave_fallback");

            if (!completedInTime) return;

            UnityEngine.Debug.Log("[ExitGameButton] Remaining realtime room leave completed | result=" + leaveTask.Result);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] Remaining realtime room leave failed | error=" + error.Message);
        }
    }

    //* این تابع یک Task بولی را فقط در مهلت باقی مانده خروج منتظر می ماند و در پایان Timeout اجازه ادامه خروج را می دهد.
    private async Task<bool> WaitForBooleanTaskWithinBudgetAsync(Task<bool> task, Stopwatch exitTimer, int totalTimeoutMs, string stage)
    {
        if (task == null) return true;

        int remainingMs = Mathf.Max(0, totalTimeoutMs - (int)exitTimer.ElapsedMilliseconds);
        if (remainingMs <= 0)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] Graceful exit budget finished before stage=" + stage);
            return false;
        }

        Task completedTask = await Task.WhenAny(task, Task.Delay(remainingMs));
        if (completedTask == task)
        {
            await task;
            return true;
        }

        UnityEngine.Debug.LogWarning("[ExitGameButton] Graceful exit timeout reached | stage=" + stage + " | remainingMs=" + remainingMs);
        return false;
    }

    //* این تابع بایندر دائمی گیم سرور را هنگام نیاز پیدا می کند، بدون ساختن نمونه جدید.
    private void ResolveDedicatedGameServerBinder()
    {
        if (dedicatedGameServerBinder != null) return;

        dedicatedGameServerBinder = DedicatedGameServerRealtimeRoomBinder.Instance;
        if (dedicatedGameServerBinder != null || !autoFindDedicatedGameServerBinder) return;

        dedicatedGameServerBinder = FindObjectOfType<DedicatedGameServerRealtimeRoomBinder>(true);
    }

    #endregion

    #region آزادسازی مدیرها و حلقه های دائمی

    //* این تابع پس از تلاش خروج رسمی، ریشه های شبکه دائمی را نابود می کند تا Eventها، Heartbeat، Reconnect، Refresh و Transportها آزاد شوند.
    private void DestroyRuntimeNetworkManagers()
    {
        if (dedicatedGameServerBinder != null) ScheduleRuntimeRootDestroy(dedicatedGameServerBinder.transform.root.gameObject, "dedicated_game_server_root");

        DedicatedGameServerWsClient wsClient = DedicatedGameServerWsClient.Instance;
        if (wsClient != null) ScheduleRuntimeRootDestroy(wsClient.transform.root.gameObject, "dedicated_ws_client_root");

        RealtimeRoomGameServerManager realtimeManager = RealtimeRoomGameServerManager.Instance;
        if (realtimeManager != null) ScheduleRuntimeRootDestroy(realtimeManager.transform.root.gameObject, "realtime_manager_root");

        GlobalAuthManager authManager = GlobalAuthManager.Instance;
        if (authManager != null) ScheduleRuntimeRootDestroy(authManager.transform.root.gameObject, "global_auth_root");

        StartupNetworkSceneRouter networkRouter = StartupNetworkSceneRouter.Instance;
        if (networkRouter != null) ScheduleRuntimeRootDestroy(networkRouter.transform.root.gameObject, "network_router_root");

        VoiceClientRuntime voiceRuntime = FindObjectOfType<VoiceClientRuntime>(true);
        if (voiceRuntime != null) ScheduleRuntimeRootDestroy(voiceRuntime.transform.root.gameObject, "voice_client_runtime_root");
    }

    //* این تابع هر ریشه Runtime را فقط یک بار برای نابودی ثبت می کند و ریشه خود دکمه خروج را نگه می دارد.
    private void ScheduleRuntimeRootDestroy(GameObject root, string source)
    {
        if (root == null) return;
        if (resolvedPersistentRoot != null && root == resolvedPersistentRoot)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] Network manager root equals Exit persistent root and was not destroyed | source=" + source);
            return;
        }

        int rootId = root.GetInstanceID();
        if (!scheduledRuntimeRootIds.Add(rootId)) return;

        UnityEngine.Debug.Log("[ExitGameButton] Runtime root scheduled for cleanup | source=" + source + " | object=" + root.name);
        Destroy(root);
    }

    #endregion

    #region Fallback ویندوز

    //* این تابع قبل از Application.Quit یک پروسس مستقل ویندوز می سازد تا اگر یونیتی در Shutdown گیر کرد، همان PID را اجباری ببندد.
    private void ScheduleWindowsForceKillFallback()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!forceKillWindowsBuild) return;

        int safeDelayMs = Mathf.Max(500, forceKillFallbackDelayMs);
        int safeDelaySeconds = Mathf.Max(1, Mathf.CeilToInt(safeDelayMs / 1000f));
        int currentProcessId = Process.GetCurrentProcess().Id;

        try
        {
            ProcessStartInfo watchdogStartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C \"timeout /T " + safeDelaySeconds + " /NOBREAK >NUL & taskkill /PID " + currentProcessId + " /F >NUL 2>&1\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(watchdogStartInfo);
            UnityEngine.Debug.Log("[ExitGameButton] Windows force-kill watchdog scheduled before Application.Quit | pid=" + currentProcessId + " | delaySeconds=" + safeDelaySeconds);
        }
        catch (Exception watchdogError)
        {
            UnityEngine.Debug.LogWarning("[ExitGameButton] External force-kill watchdog could not start. Managed fallback will be used | error=" + watchdogError.Message);
            StartManagedWindowsForceKillFallback(currentProcessId, safeDelayMs);
        }
#endif
    }

    //* این تابع فقط مسیر پشتیبان دوم است و روی ترد مستقل، بعد از مهلت تعیین شده PID فعلی را می بندد.
    private static void StartManagedWindowsForceKillFallback(int processId, int delayMs)
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        Thread fallbackThread = new Thread(() =>
        {
            try
            {
                Thread.Sleep(Math.Max(500, delayMs));
                Process process = Process.GetProcessById(processId);
                if (!process.HasExited) process.Kill();
            }
            catch
            {
                try
                {
                    Environment.Exit(0);
                }
                catch
                {
                }
            }
        });

        fallbackThread.IsBackground = true;
        fallbackThread.Name = "ExitGameForceKillFallback";
        fallbackThread.Start();
#endif
    }

    #endregion
}
