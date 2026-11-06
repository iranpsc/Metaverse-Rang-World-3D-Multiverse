using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer.Players;
using UnityEngine;

namespace Network_A.GameServer
{
    public class DedicatedHeartbeatLoop : MonoBehaviour
    {
        [Header("Runtime")]
        [SerializeField] private DedicatedServerRuntime runtime;
        [SerializeField] private GameServerControlDedicatedClient controlClient;
        [SerializeField] private DedicatedPlayerRegistry playerRegistry;

        [Header("Loop")]
        [SerializeField] private bool autoStartOnRuntimeStarted = true;
        [SerializeField] private bool registerBeforeHeartbeat = true;
        [SerializeField] private int currentPlayersForTest = 0;
        [SerializeField] private bool usePlayerRegistryCount = true;

        [Header("Immediate Metrics")]
        [SerializeField] private bool sendImmediateHeartbeatOnPlayerCountChanged = true;
        [SerializeField] private bool logPlayerCountChanges = true;

        private CancellationTokenSource heartbeatCts;
        private bool isLoopRunning;
        private bool playerRegistryEventsBound;
        private bool immediateHeartbeatQueueRunning;
        private int lastLoggedPlayerCount = -1;

        private readonly object immediateQueueLock = new object();
        private readonly Queue<ImmediateHeartbeatItem> queue_immediateHeartbeatItems = new Queue<ImmediateHeartbeatItem>();

        //* این تابع رفرنس های لازم را در شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureReferences();
        }

        //* این تابع هنگام فعال شدن آبجکت، رویداد شروع و توقف ران تایم و تغییر پلیرها را گوش می دهد.
        private void OnEnable()
        {
            EnsureReferences();
            BindPlayerRegistryEvents();

            if (runtime != null)
            {
                runtime.RuntimeStarted -= HandleRuntimeStarted;
                runtime.RuntimeStopped -= HandleRuntimeStopped;
                runtime.RuntimeStarted += HandleRuntimeStarted;
                runtime.RuntimeStopped += HandleRuntimeStopped;
            }
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، رویدادها را پاک و حلقه هارت بیت را متوقف می کند.
        private void OnDisable()
        {
            if (runtime != null)
            {
                runtime.RuntimeStarted -= HandleRuntimeStarted;
                runtime.RuntimeStopped -= HandleRuntimeStopped;
            }

            UnbindPlayerRegistryEvents();
            StopHeartbeatLoop();
        }

        //* این تابع رفرنس های ران تایم، کلاینت کنترل و رجیستری پلیر را از همین آبجکت، والد، فرزند یا صحنه پیدا می کند.
        private void EnsureReferences()
        {
            if (runtime == null)
            {
                runtime = GetComponent<DedicatedServerRuntime>();
                if (runtime == null) runtime = GetComponentInParent<DedicatedServerRuntime>();
                if (runtime == null) runtime = GetComponentInChildren<DedicatedServerRuntime>(true);
                if (runtime == null) runtime = DedicatedServerRuntime.Instance;
            }

            if (controlClient == null)
            {
                controlClient = GetComponent<GameServerControlDedicatedClient>();
                if (controlClient == null) controlClient = GetComponentInParent<GameServerControlDedicatedClient>();
                if (controlClient == null) controlClient = GetComponentInChildren<GameServerControlDedicatedClient>(true);
                if (controlClient == null) controlClient = FindObjectOfType<GameServerControlDedicatedClient>();
            }

            if (playerRegistry == null)
            {
                playerRegistry = GetComponent<DedicatedPlayerRegistry>();
                if (playerRegistry == null) playerRegistry = GetComponentInParent<DedicatedPlayerRegistry>();
                if (playerRegistry == null) playerRegistry = GetComponentInChildren<DedicatedPlayerRegistry>(true);
                if (playerRegistry == null) playerRegistry = FindObjectOfType<DedicatedPlayerRegistry>();
            }
        }

        //* این تابع رویدادهای رجیستری پلیر را یک بار وصل می کند تا بعد از ورود و خروج، هارت بیت فوری ارسال شود.
        private void BindPlayerRegistryEvents()
        {
            if (playerRegistryEventsBound || playerRegistry == null) return;

            playerRegistry.PlayerRegistered += HandlePlayerRegistered;
            playerRegistry.PlayerRemoved += HandlePlayerRemoved;
            playerRegistryEventsBound = true;
        }

        //* این تابع اتصال رویدادهای رجیستری پلیر را پاک می کند.
        private void UnbindPlayerRegistryEvents()
        {
            if (!playerRegistryEventsBound || playerRegistry == null) return;

            playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
            playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
            playerRegistryEventsBound = false;
        }

        //* این تابع بعد از شروع ران تایم، حلقه هارت بیت را شروع می کند.
        private void HandleRuntimeStarted(DedicatedServerConfigData config)
        {
            EnsureReferences();
            BindPlayerRegistryEvents();

            if (!autoStartOnRuntimeStarted) return;

            StartHeartbeatLoop();
        }

        //* این تابع بعد از توقف ران تایم، حلقه هارت بیت را متوقف می کند.
        private void HandleRuntimeStopped()
        {
            StopHeartbeatLoop();
        }

        //* این تابع وقتی پلیر جدید احراز شد، هارت بیت فوری با تعداد جدید صف می کند.
        private void HandlePlayerRegistered(DedicatedPlayerSession session)
        {
            QueueImmediateHeartbeat("player_registered");
        }

        //* این تابع وقتی پلیر از رجیستری حذف شد، ابتدا خروج را گزارش می کند و بعد هارت بیت فوری با تعداد جدید صف می کند.
        private async void HandlePlayerRemoved(DedicatedPlayerSession session, string reason)
        {
            if (controlClient == null) EnsureReferences();

            bool playerLeftReported = false;

            if (controlClient != null && session != null)
            {
                playerLeftReported = await controlClient.ReportPlayerLeftAsync(session, reason);
            }

            string heartbeatReason = playerLeftReported
                ? "player_removed_" + SafeReason(reason)
                : "player_removed_report_pending_" + SafeReason(reason);

            QueueImmediateHeartbeat(heartbeatReason);
        }

        //* این تابع از اینسپکتور برای شروع دستی حلقه هارت بیت استفاده می شود.
        [ContextMenu("Start Heartbeat Loop")]
        public void StartHeartbeatLoop()
        {
            if (isLoopRunning)
            {
                Debug.Log("[DedicatedHeartbeatLoop] Heartbeat loop is already running.");
                return;
            }

            EnsureReferences();
            BindPlayerRegistryEvents();

            if (runtime == null)
            {
                Debug.LogError("[DedicatedHeartbeatLoop] Runtime is missing.");
                return;
            }

            if (controlClient == null)
            {
                Debug.LogError("[DedicatedHeartbeatLoop] GameServerControlDedicatedClient is missing.");
                return;
            }

            DedicatedServerConfigData config = runtime.GetCurrentConfig();

            if (config == null)
            {
                Debug.LogError("[DedicatedHeartbeatLoop] Runtime config is missing.");
                return;
            }

            heartbeatCts = new CancellationTokenSource();
            isLoopRunning = true;

            Debug.Log("[DedicatedHeartbeatLoop] Heartbeat loop started.");
            RunHeartbeatLoopAsync(config, heartbeatCts.Token);
        }

        //* این تابع از اینسپکتور یا کد برای توقف دستی حلقه هارت بیت استفاده می شود.
        [ContextMenu("Stop Heartbeat Loop")]
        public void StopHeartbeatLoop()
        {
            if (!isLoopRunning) return;

            isLoopRunning = false;

            if (heartbeatCts != null)
            {
                heartbeatCts.Cancel();
                heartbeatCts.Dispose();
                heartbeatCts = null;
            }

            lock (immediateQueueLock)
            {
                queue_immediateHeartbeatItems.Clear();
            }

            immediateHeartbeatQueueRunning = false;

            Debug.Log("[DedicatedHeartbeatLoop] Heartbeat loop stopped.");
        }

        //* این تابع حلقه اصلی رجیستر و هارت بیت را اجرا می کند.
        private async void RunHeartbeatLoopAsync(DedicatedServerConfigData config, CancellationToken cancellationToken)
        {
            try
            {
                if (registerBeforeHeartbeat)
                {
                    bool registerOk = await controlClient.RegisterCurrentRuntimeAsync(cancellationToken);

                    if (!registerOk)
                    {
                        Debug.LogError("[DedicatedHeartbeatLoop] Register before heartbeat failed.");
                    }
                }

                QueueImmediateHeartbeat("loop_started");

                while (!cancellationToken.IsCancellationRequested && runtime != null && runtime.IsRunning)
                {
                    int currentPlayers = ReadCurrentPlayers();
                    LogPlayerCountIfChanged(currentPlayers, "scheduled_heartbeat");

                    await controlClient.RenewServiceTokenIfNeededAsync(cancellationToken);
                    Debug.Log("[DedicatedHeartbeatLoop] HEARTBEAT_MULTIROOM_V2 snapshot | currentPlayers=" +
                              currentPlayers + " | " + BuildActiveRoomsDebugText());
                    await controlClient.SendHeartbeatAsync(currentPlayers, cancellationToken);
                    await DelaySeconds(config.heartbeatIntervalSeconds, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[DedicatedHeartbeatLoop] Heartbeat loop cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedHeartbeatLoop] Heartbeat loop error | " + ex.Message);
            }
            finally
            {
                isLoopRunning = false;
            }
        }

        //* این تابع هارت بیت فوری را برای تغییر تعداد پلیرها در صف قرار می دهد.
        private void QueueImmediateHeartbeat(string reason)
        {
            if (!sendImmediateHeartbeatOnPlayerCountChanged) return;
            if (controlClient == null) EnsureReferences();
            if (controlClient == null) return;
            if (runtime == null || !runtime.IsRunning) return;

            int currentPlayers = ReadCurrentPlayers();
            LogPlayerCountIfChanged(currentPlayers, reason);

            lock (immediateQueueLock)
            {
                queue_immediateHeartbeatItems.Enqueue(new ImmediateHeartbeatItem
                {
                    currentPlayers = currentPlayers,
                    reason = SafeReason(reason)
                });
            }

            if (immediateHeartbeatQueueRunning) return;

            immediateHeartbeatQueueRunning = true;
            RunImmediateHeartbeatQueueAsync();
        }

        //* این تابع صف هارت بیت های فوری را به ترتیب ارسال می کند تا ۰، ۱، ۲، ۳ و خروج ها در نود جی اس ثبت شوند.
        private async void RunImmediateHeartbeatQueueAsync()
        {
            try
            {
                while (true)
                {
                    ImmediateHeartbeatItem item;

                    lock (immediateQueueLock)
                    {
                        if (queue_immediateHeartbeatItems.Count <= 0)
                        {
                            immediateHeartbeatQueueRunning = false;
                            return;
                        }

                        item = queue_immediateHeartbeatItems.Dequeue();
                    }

                    if (controlClient == null) EnsureReferences();
                    if (controlClient == null) continue;
                    if (runtime == null || !runtime.IsRunning) continue;

                    await controlClient.RenewServiceTokenIfNeededAsync();
                    Debug.Log("[DedicatedHeartbeatLoop] HEARTBEAT_MULTIROOM_V2 immediate snapshot | reason=" +
                              item.reason + " | currentPlayers=" + item.currentPlayers +
                              " | " + BuildActiveRoomsDebugText());
                    await controlClient.SendHeartbeatAsync(item.currentPlayers);
                    Debug.Log("[DedicatedHeartbeatLoop] Immediate heartbeat sent | reason=" +
                              item.reason + " | currentPlayers=" + item.currentPlayers);
                }
            }
            catch (Exception ex)
            {
                immediateHeartbeatQueueRunning = false;
                Debug.LogError("[DedicatedHeartbeatLoop] Immediate heartbeat error | " + ex.Message);
            }
        }

        //* این تابع تعداد پلیرهای فعلی را از رجیستری یا مقدار تستی می خواند.
        private int ReadCurrentPlayers()
        {
            EnsureReferences();

            if (usePlayerRegistryCount && playerRegistry != null)
            {
                return Mathf.Max(0, playerRegistry.GetCurrentPlayerCount());
            }

            return Mathf.Max(0, currentPlayersForTest);
        }

        //* این تابع تغییر تعداد پلیر را فقط هنگام تغییر واقعی لاگ می کند.
        private void LogPlayerCountIfChanged(int currentPlayers, string reason)
        {
            if (!logPlayerCountChanges) return;
            if (lastLoggedPlayerCount == currentPlayers) return;

            lastLoggedPlayerCount = currentPlayers;

            Debug.Log("[DedicatedHeartbeatLoop] Current players changed | count=" +
                      currentPlayers + " | reason=" + SafeReason(reason));
        }

        //* این تابع لیست روم های فعال را برای لاگ هارت بیت چند رومی آماده می کند.
        private string BuildActiveRoomsDebugText()
        {
            EnsureReferences();

            if (playerRegistry == null)
            {
                return "roomCount=0 | rooms=";
            }

            List<string> list_roomIds = playerRegistry.CreateActiveRoomIdSnapshot();

            if (list_roomIds == null || list_roomIds.Count <= 0)
            {
                string primaryRoomId = playerRegistry.GetPrimaryRoomId();

                if (string.IsNullOrWhiteSpace(primaryRoomId))
                {
                    return "roomCount=0 | rooms=";
                }

                int primaryCount = playerRegistry.GetCurrentPlayerCountInRoom(primaryRoomId);
                return "roomCount=1 | rooms=" + primaryRoomId + ":" + Mathf.Max(0, primaryCount);
            }

            List<string> list_parts = new List<string>();

            for (int index = 0; index < list_roomIds.Count; index++)
            {
                string roomId = list_roomIds[index];
                if (string.IsNullOrWhiteSpace(roomId)) continue;

                int roomPlayers = playerRegistry.GetCurrentPlayerCountInRoom(roomId);
                list_parts.Add(roomId + ":" + Mathf.Max(0, roomPlayers));
            }

            return "roomCount=" + list_parts.Count + " | rooms=" + string.Join(",", list_parts);
        }

        //* این تابع تاخیر قابل کنسل برای فاصله بین هارت بیت ها می سازد.
        private async Task DelaySeconds(float seconds, CancellationToken cancellationToken)
        {
            float safeSeconds = Mathf.Max(1f, seconds);
            int milliseconds = Mathf.RoundToInt(safeSeconds * 1000f);

            await Task.Delay(milliseconds, cancellationToken);
        }

        private string SafeReason(string reason)
        {
            return string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        }

        //* این تابع هنگام حذف آبجکت، حلقه هارت بیت را تمیز متوقف می کند.
        private void OnDestroy()
        {
            UnbindPlayerRegistryEvents();
            StopHeartbeatLoop();
        }

        private struct ImmediateHeartbeatItem
        {
            public int currentPlayers;
            public string reason;
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت حلقه دائمی هارت بیت ددیکیتد سرور را مدیریت می کند.
        وقتی DedicatedServerRuntime شروع شد، ابتدا در صورت نیاز رجیستر انجام می شود.
        سپس در فاصله مشخص شده داخل کانفیگ، هارت بیت به نود جی اس ارسال می شود.
        در این نسخه، اگر DedicatedPlayerRegistry وصل باشد، تعداد واقعی پلیرها به نود جی اس فرستاده می شود.
        بعد از ورود یا خروج پلیر، یک هارت بیت فوری هم ارسال می شود تا currentPlayers در نود جی اس عقب نماند.
        اگر رجیستری وصل نباشد، مقدار currentPlayersForTest استفاده می شود.
        */
    }
}
