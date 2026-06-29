using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer.Players;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.GameServer
{
    public class GameServerControlDedicatedClient : MonoBehaviour
    {
        [Header("Runtime")]
        [SerializeField] private DedicatedServerRuntime runtime;

        [Header("Http")]
        [SerializeField] private int timeoutSeconds = 15;
        [SerializeField] private bool autoRegisterOnRuntimeStarted = true;
        [SerializeField] private bool logRawResponse = false;
        [SerializeField] private bool logFullSuccessfulResponse = false;

        [Header("Shutdown")]
        [SerializeField] private bool sendOfflineOnRuntimeStopped = true;
        [SerializeField] private bool sendOfflineOnDestroy = true;

        public bool IsRegistered { get; private set; }
        public string LastRegisterReason { get; private set; }
        public string LastHeartbeatReason { get; private set; }
        public string LastError { get; private set; }

        public event Action RegisterSucceeded;
        public event Action<string> RegisterFailed;
        public event Action HeartbeatSucceeded;
        public event Action<string> HeartbeatFailed;
        public event Action OfflineHeartbeatSucceeded;
        public event Action<string> OfflineHeartbeatFailed;

        [SerializeField] private DedicatedPlayerRegistry playerRegistry;

        private bool isSendingOffline;
        private bool offlineSentForCurrentRuntime;

        //* این تابع رفرنس ران تایم ددیکیتد سرور را در شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureRuntimeReference();
        }

        //* این تابع هنگام فعال شدن آبجکت، رویدادهای ران تایم را گوش می دهد.
        private void OnEnable()
        {
            EnsureRuntimeReference();

            if (runtime != null)
            {
                runtime.RuntimeStarted += HandleRuntimeStarted;
                runtime.RuntimeStopped += HandleRuntimeStopped;
            }
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، اتصال رویدادها را پاک می کند.
        private void OnDisable()
        {
            if (runtime != null)
            {
                runtime.RuntimeStarted -= HandleRuntimeStarted;
                runtime.RuntimeStopped -= HandleRuntimeStopped;
            }
        }

        //* این تابع رفرنس ران تایم و رجیستری پلیر را از همین آبجکت، والد، فرزند یا سینگلتون پیدا می کند.
        private void EnsureRuntimeReference()
        {
            if (runtime == null)
            {
                runtime = GetComponent<DedicatedServerRuntime>();
                if (runtime == null) runtime = GetComponentInParent<DedicatedServerRuntime>();
                if (runtime == null) runtime = GetComponentInChildren<DedicatedServerRuntime>(true);
                if (runtime == null) runtime = DedicatedServerRuntime.Instance;
            }

            EnsurePlayerRegistryReference();
        }

        //* این تابع رجیستری پلیر را برای خواندن currentPlayers واقعی پیدا می کند.
        private void EnsurePlayerRegistryReference()
        {
            if (playerRegistry != null) return;

            playerRegistry = GetComponent<DedicatedPlayerRegistry>();
            if (playerRegistry == null) playerRegistry = GetComponentInParent<DedicatedPlayerRegistry>();
            if (playerRegistry == null) playerRegistry = GetComponentInChildren<DedicatedPlayerRegistry>(true);
            if (playerRegistry == null) playerRegistry = FindObjectOfType<DedicatedPlayerRegistry>();
        }

        //* این تابع بعد از شروع ران تایم، در صورت فعال بودن اتو رجیستر، ددیکیتد سرور را در نود جی اس ثبت می کند.
        private async void HandleRuntimeStarted(DedicatedServerConfigData config)
        {
            offlineSentForCurrentRuntime = false;
            isSendingOffline = false;

            if (!autoRegisterOnRuntimeStarted) return;

            await RegisterCurrentRuntimeAsync();
        }

        //* این تابع هنگام توقف ران تایم، وضعیت رجیستر محلی را پاک می کند و در صورت نیاز سرور را آفلاین می کند.
        private async void HandleRuntimeStopped()
        {
            if (sendOfflineOnRuntimeStopped)
            {
                await SendOfflineHeartbeatAsync();
            }

            IsRegistered = false;
        }

        //* این تابع از اینسپکتور برای تست دستی رجیستر استفاده می شود.
        [ContextMenu("Register Dedicated Server Now")]
        public async void Btn_RegisterNow()
        {
            await RegisterCurrentRuntimeAsync();
        }

        //* این تابع از اینسپکتور برای تست دستی هارت بیت استفاده می شود.
        [ContextMenu("Send Heartbeat Now")]
        public async void Btn_SendHeartbeatNow()
        {
            await SendHeartbeatAsync(0);
        }

        //* این تابع از اینسپکتور برای تست دستی آفلاین کردن ددیکیتد سرور استفاده می شود.
        [ContextMenu("Send Offline Heartbeat Now")]
        public async void Btn_SendOfflineHeartbeatNow()
        {
            await SendOfflineHeartbeatAsync();
        }

        //* این تابع ددیکیتد سرور فعلی را با داده های کانفیگ در نود جی اس رجیستر می کند.
        public async Task<bool> RegisterCurrentRuntimeAsync(CancellationToken cancellationToken = default)
        {
            DedicatedServerConfigData config = GetSafeConfig();

            if (config == null)
            {
                return FailRegister("Dedicated runtime config is missing.");
            }

            if (!ValidateServiceToken(config, out string tokenError))
            {
                return FailRegister(tokenError);
            }

            int currentPlayers = ReadCurrentPlayersFromRegistry(0);
            string heartbeatRoomId = ResolveHeartbeatRoomId(config);
            string heartbeatRoomName = ResolveHeartbeatRoomName(config);

            DedicatedRegisterRequestDto request = new DedicatedRegisterRequestDto
            {
                serviceToken = config.serviceToken,
                serverId = config.serverId,
                host = config.publicHost,
                port = config.publicPort,
                roomId = heartbeatRoomId,
                roomName = heartbeatRoomName,
                region = config.region,
                zone = config.zone,
                maxPlayers = config.maxPlayers,
                currentPlayers = currentPlayers,
                status = ResolveHeartbeatStatus(config, "online", currentPlayers, false),
                tickRate = config.tickRate,
                buildVersion = config.buildVersion
            };

            string url = BuildControlUrl(config.controlBaseUrl, "/game-server-control/dedicated/register");
            string json = JsonUtility.ToJson(request);

            DedicatedHttpResult httpResult = await SendJsonPostAsync(url, json, cancellationToken);

            if (!httpResult.IsSuccess)
            {
                return FailRegister(httpResult.ErrorMessage);
            }

            DedicatedBasicResponseDto response = ParseBasicResponse(httpResult.RawBody);

            if (response == null || !response.success)
            {
                string reason = response != null ? response.reason : "register_failed";
                string message = response != null ? response.message : httpResult.RawBody;
                return FailRegister(reason + " | " + message);
            }

            IsRegistered = true;
            offlineSentForCurrentRuntime = false;
            LastRegisterReason = response.reason;
            LastError = string.Empty;

            Debug.Log("[GameServerControlDedicatedClient] Register ok | reason=" + response.reason +
                      " | serverId=" + config.serverId +
                      " | roomId=" + request.roomId +
                      " | roomName=" + request.roomName +
                      " | currentPlayers=" + request.currentPlayers +
                      " | status=" + request.status);
            RegisterSucceeded?.Invoke();

            return true;
        }

        //* این تابع هارت بیت آنلاین ددیکیتد سرور را به نود جی اس ارسال می کند.
        public async Task<bool> SendHeartbeatAsync(int currentPlayers, CancellationToken cancellationToken = default)
        {
            return await SendHeartbeatWithStatusAsync(Mathf.Max(0, currentPlayers), "online", false, cancellationToken);
        }

        //* این تابع قبل از خروج یا توقف سرور، ددیکیتد سرور را در نود جی اس آفلاین می کند.
        public async Task<bool> SendOfflineHeartbeatAsync(CancellationToken cancellationToken = default)
        {
            if (offlineSentForCurrentRuntime)
            {
                Debug.Log("[GameServerControlDedicatedClient] Offline heartbeat already sent for current runtime.");
                return true;
            }

            if (isSendingOffline)
            {
                Debug.Log("[GameServerControlDedicatedClient] Offline heartbeat is already in progress.");
                return false;
            }

            isSendingOffline = true;

            try
            {
                bool result = await SendHeartbeatWithStatusAsync(0, "offline", true, cancellationToken);

                if (result)
                {
                    offlineSentForCurrentRuntime = true;
                    IsRegistered = false;
                }

                return result;
            }
            finally
            {
                isSendingOffline = false;
            }
        }

        //* این تابع هارت بیت را با وضعیت آنلاین یا آفلاین ارسال می کند.
        private async Task<bool> SendHeartbeatWithStatusAsync(int currentPlayers, string status, bool isOffline, CancellationToken cancellationToken)
        {
            DedicatedServerConfigData config = GetSafeConfig();

            if (config == null)
            {
                return isOffline ? FailOfflineHeartbeat("Dedicated runtime config is missing.") : FailHeartbeat("Dedicated runtime config is missing.");
            }

            if (!ValidateServiceToken(config, out string tokenError))
            {
                return isOffline ? FailOfflineHeartbeat(tokenError) : FailHeartbeat(tokenError);
            }

            int effectiveCurrentPlayers = isOffline ? 0 : ReadCurrentPlayersFromRegistry(currentPlayers);
            string heartbeatRoomId = ResolveHeartbeatRoomId(config);
            string heartbeatRoomName = ResolveHeartbeatRoomName(config);

            DedicatedHeartbeatRequestDto request = new DedicatedHeartbeatRequestDto
            {
                serviceToken = config.serviceToken,
                serverId = config.serverId,
                roomId = heartbeatRoomId,
                roomName = heartbeatRoomName,
                region = config.region,
                zone = config.zone,
                status = ResolveHeartbeatStatus(config, status, effectiveCurrentPlayers, isOffline),
                fps = isOffline ? 0f : ReadFps(),
                tickRate = config.tickRate,
                currentPlayers = Mathf.Max(0, effectiveCurrentPlayers),
                maxPlayers = config.maxPlayers,
                memoryMb = isOffline ? 0 : ReadMemoryMb(),
                cpuPercent = 0f,
                pingMs = 0,
                uptimeSeconds = Mathf.RoundToInt(Time.realtimeSinceStartup)
            };

            string url = BuildControlUrl(config.controlBaseUrl, "/game-server-control/dedicated/heartbeat");
            string json = JsonUtility.ToJson(request);

            Debug.Log("[GameServerControlDedicatedClient] Heartbeat request | currentPlayers=" +
                      request.currentPlayers + " | status=" + request.status + " | roomId=" + request.roomId);

            DedicatedHttpResult httpResult = await SendJsonPostAsync(url, json, cancellationToken);

            if (!httpResult.IsSuccess)
            {
                return isOffline ? FailOfflineHeartbeat(httpResult.ErrorMessage) : FailHeartbeat(httpResult.ErrorMessage);
            }

            DedicatedBasicResponseDto response = ParseBasicResponse(httpResult.RawBody);

            if (response == null || !response.success)
            {
                string reason = response != null ? response.reason : "heartbeat_failed";
                string message = response != null ? response.message : httpResult.RawBody;
                return isOffline ? FailOfflineHeartbeat(reason + " | " + message) : FailHeartbeat(reason + " | " + message);
            }

            LastHeartbeatReason = response.reason;
            LastError = string.Empty;

            if (isOffline)
            {
                Debug.Log("[GameServerControlDedicatedClient] Offline heartbeat ok | reason=" + response.reason);
                OfflineHeartbeatSucceeded?.Invoke();
            }
            else
            {
                Debug.Log("[GameServerControlDedicatedClient] Heartbeat ok | reason=" + response.reason);
                HeartbeatSucceeded?.Invoke();
            }

            return true;
        }


        private bool HasRoomContext(DedicatedServerConfigData config)
        {
            return config != null && !string.IsNullOrWhiteSpace(ResolveHeartbeatRoomId(config));
        }

        private string ResolveHeartbeatStatus(DedicatedServerConfigData config, string requestedStatus, int currentPlayers, bool isOffline)
        {
            string safeStatus = string.IsNullOrWhiteSpace(requestedStatus) ? "online" : requestedStatus.Trim();

            if (isOffline || string.Equals(safeStatus, "offline", StringComparison.OrdinalIgnoreCase))
            {
                return "offline";
            }

            if (currentPlayers > 0)
            {
                return "online";
            }

            return HasRoomContext(config) ? safeStatus : "idle";
        }

        private int ReadCurrentPlayersFromRegistry(int fallback)
        {
            EnsurePlayerRegistryReference();

            if (fallback > 0)
            {
                return Mathf.Max(0, fallback);
            }

            if (playerRegistry != null)
            {
                return Mathf.Max(0, playerRegistry.GetCurrentPlayerCount());
            }

            return Mathf.Max(0, fallback);
        }

        private string ResolveHeartbeatRoomId(DedicatedServerConfigData config)
        {
            string configRoomId = config == null ? string.Empty : SafeTrim(config.roomId);
            if (!string.IsNullOrWhiteSpace(configRoomId)) return configRoomId;

            EnsurePlayerRegistryReference();

            string registryRoomId = playerRegistry == null ? string.Empty : playerRegistry.GetPrimaryRoomId();
            return SafeTrim(registryRoomId);
        }

        private string ResolveHeartbeatRoomName(DedicatedServerConfigData config)
        {
            return config == null ? string.Empty : SafeTrim(config.roomName);
        }

        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع کانفیگ فعال ران تایم را به شکل امن برمی گرداند.
        private DedicatedServerConfigData GetSafeConfig()
        {
            EnsureRuntimeReference();

            if (runtime == null) return null;

            return runtime.GetCurrentConfig();
        }

        //* این تابع بررسی می کند که سرویس توکن برای رجیستر و هارت بیت وجود دارد یا نه.
        private bool ValidateServiceToken(DedicatedServerConfigData config, out string error)
        {
            if (config == null)
            {
                error = "Dedicated config is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.serviceToken))
            {
                error = "Service token is empty. Put the generated dedicated server service token in DedicatedServerConfig.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        //* این تابع درخواست جیسون پست را با یونیتی وب ریکوئست ارسال می کند.
        private async Task<DedicatedHttpResult> SendJsonPostAsync(string url, string json, CancellationToken cancellationToken)
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(1, timeoutSeconds);

                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("X-Metaverse-Dedicated-Server", "unity");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        return DedicatedHttpResult.Fail(0, "Request cancelled.", string.Empty);
                    }

                    await Task.Yield();
                }

                string rawBody = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                int statusCode = (int)request.responseCode;
                bool isHttpOk = statusCode >= 200 && statusCode < 300;

                if (logRawResponse)
                {
                    if (isHttpOk && !logFullSuccessfulResponse)
                    {
                        Debug.Log("[GameServerControlDedicatedClient] Http ok | status=" + statusCode);
                    }
                    else
                    {
                        Debug.Log("[GameServerControlDedicatedClient] Status=" + statusCode + " Body=" + rawBody);
                    }
                }

                bool hasTransportError =
                    request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.DataProcessingError;

                if (!isHttpOk || hasTransportError)
                {
                    string error = string.IsNullOrWhiteSpace(request.error) ? rawBody : request.error;
                    return DedicatedHttpResult.Fail(statusCode, error, rawBody);
                }

                return DedicatedHttpResult.Success(statusCode, rawBody);
            }
        }

        //* این تابع پاسخ پایه گیم سرور کنترل را از جیسون می خواند.
        private DedicatedBasicResponseDto ParseBasicResponse(string rawBody)
        {
            if (string.IsNullOrWhiteSpace(rawBody)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedBasicResponseDto>(rawBody);
            }
            catch (Exception ex)
            {
                Debug.LogError("[GameServerControlDedicatedClient] Parse failed | " + ex.Message);
                return null;
            }
        }

        //* این تابع آدرس کامل مسیرهای گیم سرور کنترل را می سازد.
        private string BuildControlUrl(string baseUrl, string path)
        {
            string safeBase = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim().TrimEnd('/');
            string safePath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();

            if (!safePath.StartsWith("/")) safePath = "/" + safePath;

            return safeBase + safePath;
        }

        //* این تابع مقدار تقریبی اف پی اس سرور را می خواند.
        private float ReadFps()
        {
            if (Time.smoothDeltaTime <= 0f) return 0f;

            return Mathf.Round(1f / Time.smoothDeltaTime);
        }

        //* این تابع مقدار تقریبی مموری استفاده شده را به مگابایت برمی گرداند.
        private int ReadMemoryMb()
        {
            long bytes = GC.GetTotalMemory(false);
            return Mathf.Max(0, Mathf.RoundToInt(bytes / 1024f / 1024f));
        }

        //* این تابع خطای رجیستر را ثبت و اعلام می کند.
        private bool FailRegister(string error)
        {
            IsRegistered = false;
            LastError = error;

            Debug.LogError("[GameServerControlDedicatedClient] Register failed | " + error);
            RegisterFailed?.Invoke(error);

            return false;
        }

        //* این تابع خطای هارت بیت را ثبت و اعلام می کند.
        private bool FailHeartbeat(string error)
        {
            LastError = error;

            Debug.LogError("[GameServerControlDedicatedClient] Heartbeat failed | " + error);
            HeartbeatFailed?.Invoke(error);

            return false;
        }

        //* این تابع خطای آفلاین کردن ددیکیتد سرور را ثبت و اعلام می کند.
        private bool FailOfflineHeartbeat(string error)
        {
            LastError = error;

            Debug.LogError("[GameServerControlDedicatedClient] Offline heartbeat failed | " + error);
            OfflineHeartbeatFailed?.Invoke(error);

            return false;
        }

        //* این تابع هنگام خروج یا حذف آبجکت، تلاش می کند ددیکیتد سرور در نود آفلاین شود.
        private async void OnDestroy()
        {
            if (!sendOfflineOnDestroy) return;
            if (offlineSentForCurrentRuntime) return;

            await SendOfflineHeartbeatAsync();
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت پل ارتباطی یونیتی ددیکیتد سرور با نود جی اس گیم سرور کنترل است.
        علاوه بر رجیستر و هارت بیت آنلاین، هنگام توقف ران تایم یک هارت بیت آفلاین می فرستد.
        اگر روم آی دی هنوز مشخص نباشد، سرور با وضعیت آیدل رجیستر و هارت بیت می فرستد.
        بعد از بایند روم یا وریفای تیکت، روم آی دی واقعی وارد همین مسیر می شود.
        سرویس توکن از کانفیگ ددیکیتد سرور خوانده می شود و نباید داخل کلاینت عمومی قرار بگیرد.
        */
    }

    [Serializable]
    public class DedicatedRegisterRequestDto
    {
        public string serviceToken;
        public string serverId;
        public string host;
        public int port;
        public string roomId;
        public string roomName;
        public string region;
        public string zone;
        public int maxPlayers;
        public int currentPlayers;
        public string status;
        public int tickRate;
        public string buildVersion;
    }

    [Serializable]
    public class DedicatedHeartbeatRequestDto
    {
        public string serviceToken;
        public string serverId;
        public string roomId;
        public string roomName;
        public string region;
        public string zone;
        public string status;
        public float fps;
        public int tickRate;
        public int currentPlayers;
        public int maxPlayers;
        public int memoryMb;
        public float cpuPercent;
        public int pingMs;
        public int uptimeSeconds;
    }

    [Serializable]
    public class DedicatedBasicResponseDto
    {
        public bool success;
        public string reason;
        public string message;
    }

    public class DedicatedHttpResult
    {
        public bool IsSuccess { get; private set; }
        public int StatusCode { get; private set; }
        public string ErrorMessage { get; private set; }
        public string RawBody { get; private set; }

        //* این تابع نتیجه موفق درخواست اچ تی تی پی را می سازد.
        public static DedicatedHttpResult Success(int statusCode, string rawBody)
        {
            return new DedicatedHttpResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                ErrorMessage = string.Empty,
                RawBody = rawBody
            };
        }

        //* این تابع نتیجه ناموفق درخواست اچ تی تی پی را می سازد.
        public static DedicatedHttpResult Fail(int statusCode, string errorMessage, string rawBody)
        {
            return new DedicatedHttpResult
            {
                IsSuccess = false,
                StatusCode = statusCode,
                ErrorMessage = errorMessage,
                RawBody = rawBody
            };
        }
    }
}
