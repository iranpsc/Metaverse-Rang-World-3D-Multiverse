using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Core;
using Network_A.UI;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Bootstrap
{
    //* این کلاس در هر صحنه وضعیت واقعی ارتباط برنامه با سرور را بررسی می‌کند.
    //* هنگام قطع یا بازیابی ارتباط فقط وضعیت و رویدادها را اعلام می‌کند و هیچ صحنه‌ای را تغییر نمی‌دهد.
    [DefaultExecutionOrder(-8500)]
    public sealed class StartupNetworkSceneRouter : MonoBehaviour
    {
        #region وضعیت و تنظیمات

        public enum NetworkState { Unknown, Checking, Online, InternetUnavailable, ServerUnavailable }

        public static StartupNetworkSceneRouter Instance { get; private set; }
        public static NetworkState CurrentState { get; private set; } = NetworkState.Unknown;
        public static bool IsOnline => CurrentState == NetworkState.Online;

        public static event Action<NetworkState> OnNetworkStateChanged;
        public static event Action OnNetworkLost;
        public static event Action OnNetworkRecovered;

        private const string NetworkStateMessageId = "GLOBAL_NETWORK_STATE";
        private const string NetworkCheckingMessageId = "GLOBAL_NETWORK_CHECKING";

        [Header("بررسی سلامت سرور")]
        [SerializeField] private string fallbackHealthUrl = "https://dev-world-3d.metarang.com/health";
        [SerializeField, Min(3000)] private int requestTimeoutMs = 3000;
        [SerializeField, Min(10000)] private int totalDecisionTimeoutMs = 10000;
        [SerializeField, Min(0)] private int delayBetweenAttemptsMs = 200;
        [SerializeField, Min(0.5f)] private float monitorIntervalSeconds = 2f;

        private readonly SemaphoreSlim healthCheckGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource lifecycleCts;

        #endregion

        #region چرخه حیات

        //* پیش از ساخته‌شدن صحنه، وضعیت و شنونده‌های باقی‌مانده از اجرای قبلی را پاک می‌کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            CurrentState = NetworkState.Unknown;
            OnNetworkStateChanged = null;
            OnNetworkLost = null;
            OnNetworkRecovered = null;
        }

        //* تنها نمونه معتبر مدیر شبکه را آماده می‌کند و آن را هنگام تغییر صحنه نگه می‌دارد.
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                NetworkFileLogger.Warning("GLOBAL_NETWORK", "نمونه تکراری مدیر سراسری شبکه حذف شد.");

                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            lifecycleCts = new CancellationTokenSource();
            Application.runInBackground = true;

            NetworkFileLogger.Info("GLOBAL_NETWORK", "راه‌اندازی شد | تنظیمات سرور آماده است=" + ServerConfigBootstrap.HasAppliedConfiguration + " | محیط=" + ServerConfigBootstrap.AppliedEnvironment + " | روش ارتباط=" + ServerConfig.CurrentTransportKind + " | نشانی سرور=" + ServerConfig.CurrentEndpoint + " | نشانی پشتیبان=" + fallbackHealthUrl + " | مهلت هر درخواست=" + requestTimeoutMs + " | مهلت کل تصمیم=" + totalDecisionTimeoutMs);
        }

        //* در شروع برنامه ارتباط واقعی با سرور را بررسی می‌کند و سپس بررسی دوره‌ای را آغاز می‌کند.
        private async void Start()
        {
            if (Instance != this) return;

            try
            {
                await CheckNowAsync();
                _ = MonitorLoopAsync(lifecycleCts.Token);
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Warning("GLOBAL_NETWORK", "جریان بررسی اولیه شبکه لغو شد.");
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("GLOBAL_NETWORK_START", ex);
            }
        }

        //* هنگام نابودشدن نمونه اصلی، تمام عملیات در حال اجرا را لغو می‌کند
        //* و منابع مربوط به لغو عملیات را آزاد می‌کند.
        private void OnDestroy()
        {
            if (Instance != this) return;

            if (lifecycleCts != null)
            {
                if (!lifecycleCts.IsCancellationRequested) lifecycleCts.Cancel();

                lifecycleCts.Dispose();
                lifecycleCts = null;
            }

            Instance = null;
        }

        #endregion

        #region بررسی ارتباط

        //* ارتباط برنامه با سرور را بررسی می‌کند و نتیجه را در وضعیت سراسری همین صحنه ثبت می‌کند.
        private async Task<bool> CheckNowAsync()
        {
            bool initialCheck = CurrentState == NetworkState.Unknown;
            if (initialCheck) SetNetworkState(NetworkState.Checking, "بررسی اولیه ارتباط با سرور آغاز شد.");

            try
            {
                ConnectivityCheckResult connectivityResult = await RunConnectivityCheckAsync(CancellationToken.None, true);

                if (connectivityResult.IsSuccess)
                {
                    SetNetworkState(NetworkState.Online, connectivityResult.Details);
                    return true;
                }

                SetNetworkState(connectivityResult.FailureState, connectivityResult.Details);
                return false;
            }
            finally
            {
                GlobalMessageManager.Clear(NetworkCheckingMessageId);
            }
        }

        //* ارتباط واقعی با سرور را بدون نمایش پیام، تغییر صحنه یا تغییر وضعیت عمومی بررسی می‌کند.
        public async Task<bool> CheckNetFastSilentAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (lifecycleCts == null || lifecycleCts.IsCancellationRequested) return false;

            try
            {
                ConnectivityCheckResult connectivityResult = await RunConnectivityCheckAsync(cancellationToken, false);
                return connectivityResult.IsSuccess;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        //* مسیر مشترک بررسی سلامت را اجرا می‌کند تا بررسی عادی و بررسی بی‌صدا منطق تکراری نداشته باشند.
        private async Task<ConnectivityCheckResult> RunConnectivityCheckAsync(CancellationToken externalToken, bool showCheckingMessage)
        {
            if (lifecycleCts == null || lifecycleCts.IsCancellationRequested) return ConnectivityCheckResult.Failure("مدیر بررسی شبکه در حال پایان است.");

            CancellationToken lifecycleToken = lifecycleCts.Token;

            using (CancellationTokenSource gateCts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken, externalToken))
            {
                await healthCheckGate.WaitAsync(gateCts.Token);

                try
                {
                    NetworkReachability reportedReachability = Application.internetReachability;
                    NetworkFileLogger.Info("GLOBAL_NETWORK_REACHABILITY", "گزارش دسترسی دستگاه=" + reportedReachability + " | این مقدار فقط ثبت می‌شود و ملاک نهایی نیست.");

                    int safeTotalTimeoutMs = Mathf.Clamp(totalDecisionTimeoutMs, 10000, 15000);

                    using (CancellationTokenSource totalTimeoutCts = new CancellationTokenSource(safeTotalTimeoutMs))
                    using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken, externalToken, totalTimeoutCts.Token))
                    {
                        return await CheckServerWithConfirmationAsync(reportedReachability, linkedCts.Token, lifecycleToken, safeTotalTimeoutMs, showCheckingMessage);
                    }
                }
                finally
                {
                    healthCheckGate.Release();
                }
            }
        }

        //* ابتدا مسیر اصلی، سپس مسیر پشتیبان و در پایان مسیر اصلی را دوباره بررسی می‌کند.
        //* پس از اولین شکست، فقط در بررسی عادی پیام بررسی ارتباط را نمایش می‌دهد و مسیر بی‌صدا رابط را تغییر نمی‌دهد.
        //* کل این جریان در مهلت کلی تعیین‌شده متوقف می‌شود.
        private async Task<ConnectivityCheckResult> CheckServerWithConfirmationAsync(NetworkReachability reportedReachability, CancellationToken cancellationToken, CancellationToken lifecycleToken, int safeTotalTimeoutMs, bool showCheckingMessage)
        {
            ProbeResult firstPrimary = null;
            ProbeResult fallback = null;
            ProbeResult finalPrimary = null;

            try
            {
                firstPrimary = await CheckPrimaryHealthAsync("GLOBAL_HEALTH_PRIMARY_1", cancellationToken, lifecycleToken);

                if (firstPrimary.IsSuccess) return ConnectivityCheckResult.Success(BuildSuccessDetails(reportedReachability, firstPrimary, 1));

                if (showCheckingMessage && (CurrentState == NetworkState.Online || CurrentState == NetworkState.Checking))
                {
                    GlobalMessageManager.ShowNetworkChecking(NetworkCheckingMessageId, "بررسی ارتباط", "ارتباط با سرور دچار مشکل شده است. در حال بررسی مجدد هستیم...", firstPrimary.Details ?? string.Empty);
                }

                await DelayBeforeNextAttemptAsync(cancellationToken);

                fallback = await CheckFallbackHealthAsync("GLOBAL_HEALTH_FALLBACK", cancellationToken, lifecycleToken);

                /*
                 * پاسخ مسیر عمومی وب فقط ثابت می کند دامنه و اینترنت در دسترس هستند.
                 * برای Online شدن برنامه باید همان مسیر اصلی مورد استفاده پلتفرم نیز پاسخ بدهد؛
                 * در ویندوز این مسیر جی آر پی سی نیتیو است و بدون آن Auth و Realtime کار نمی کنند.
                 */
                await DelayBeforeNextAttemptAsync(cancellationToken);

                finalPrimary = await CheckPrimaryHealthAsync("GLOBAL_HEALTH_PRIMARY_2", cancellationToken, lifecycleToken);

                if (finalPrimary.IsSuccess) return ConnectivityCheckResult.Success(BuildSuccessDetails(reportedReachability, finalPrimary, 3));
            }
            catch (OperationCanceledException)
            {
                if (lifecycleToken.IsCancellationRequested) throw;
            }

            NetworkState failureState = fallback != null && fallback.IsSuccess
                ? NetworkState.ServerUnavailable
                : NetworkState.InternetUnavailable;

            string details = "ارتباط مسیر اصلی سرور پس از درخواست های تأییدی برقرار نشد | وضعیت نهایی=" + failureState + " | مهلت کل=" + safeTotalTimeoutMs + " | گزارش دسترسی دستگاه=" + reportedReachability + " | تلاش نخست=" + FormatProbeResult(firstPrimary) + " | تلاش پشتیبان=" + FormatProbeResult(fallback) + " | تلاش نهایی=" + FormatProbeResult(finalPrimary);

            NetworkFileLogger.Warning("GLOBAL_NETWORK_CONFIRMED_UNAVAILABLE", details);

            return ConnectivityCheckResult.Failure(details, failureState);
        }

        //* پیش از تلاش بعدی، فاصله کوتاه تعیین‌شده را اعمال می‌کند.
        private async Task DelayBeforeNextAttemptAsync(CancellationToken cancellationToken)
        {
            int safeDelayMs = Mathf.Clamp(delayBetweenAttemptsMs, 0, 250);

            if (safeDelayMs <= 0) return;

            await Task.Delay(safeDelayMs, cancellationToken);
        }

        //* سلامت سرور را با روش اصلی انتخاب‌شده برای پلتفرم بررسی می‌کند.
        private async Task<ProbeResult> CheckPrimaryHealthAsync(string logTag, CancellationToken externalToken, CancellationToken lifecycleToken)
        {
            int safeTimeoutMs = Mathf.Clamp(requestTimeoutMs, 3000, 5000);

            using (CancellationTokenSource timeoutCts = new CancellationTokenSource(safeTimeoutMs))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, externalToken))
            {
                try
                {
                    if (!ServerConfigBootstrap.HasAppliedConfiguration) return ProbeResult.Failure("مسیر اصلی", 0, "تنظیمات مرکزی سرور هنوز اعمال نشده است.");

                    ApiResult<byte[]> result;

                    if (ServerConfig.IsGrpcNative())
                    {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                        result = await GrpcNativeUnaryClient.SendAsync(ServerConfig.HealthServiceName, "Check", AuthProtoMapper.EncodeEmptyRequest(), false, null, linkedCts.Token, logTag);
#else
                        result = ApiResult<byte[]>.Failure("بررسی سلامت با روش ارتباط بومی برای این پلتفرم فعال نیست.", 0, true);
#endif
                    }
                    else
                    {
                        byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(AuthProtoMapper.EncodeEmptyRequest());

                        result = await RequestManager.Send<byte[]>(ServerConfig.HealthUrl, UnityWebRequest.kHttpVerbPOST, frame, false, AuthService.BuildGrpcWebHeaders(), linkedCts.Token, logTag);
                    }

                    if (result != null && result.IsSuccess) return ProbeResult.Success("مسیر اصلی", result.StatusCode, "بررسی سلامت اصلی موفق بود.");

                    return ProbeResult.Failure("مسیر اصلی", result != null ? result.StatusCode : 0, BuildPrimaryFailureReason(result));
                }
                catch (OperationCanceledException)
                {
                    if (lifecycleToken.IsCancellationRequested) throw;

                    return ProbeResult.Failure("مسیر اصلی", 0, "مهلت درخواست پس از " + safeTimeoutMs + " میلی‌ثانیه پایان یافت.");
                }
                catch (Exception ex)
                {
                    return ProbeResult.Failure("مسیر اصلی", 0, ex.Message);
                }
            }
        }

        //* مسیر عمومی سلامت سرور را روی ارتباط امن وب بررسی می‌کند.
        //* دریافت هر پاسخ معتبر از سرور، برقرار بودن مسیر ارتباطی را ثابت می‌کند.
        private async Task<ProbeResult> CheckFallbackHealthAsync(string logTag, CancellationToken externalToken, CancellationToken lifecycleToken)
        {
            if (string.IsNullOrWhiteSpace(fallbackHealthUrl)) return ProbeResult.Failure("مسیر پشتیبان", 0, "نشانی مسیر پشتیبان خالی است.");

            int safeTimeoutMs = Mathf.Clamp(requestTimeoutMs, 3000, 5000);

            using (CancellationTokenSource timeoutCts = new CancellationTokenSource(safeTimeoutMs))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, externalToken))
            using (UnityWebRequest request = UnityWebRequest.Get(fallbackHealthUrl.Trim()))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(1, Mathf.CeilToInt(safeTimeoutMs / 1000f));

                request.SetRequestHeader("X-Metaverse-Client", Application.platform.ToString());
                request.SetRequestHeader("X-Metaverse-Version", Application.version);

                NetworkFileLogger.Info(logTag, "درخواست مسیر پشتیبان ارسال شد | نشانی=" + fallbackHealthUrl + " | مهلت=" + safeTimeoutMs);

                try
                {
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                    while (!operation.isDone)
                    {
                        if (linkedCts.Token.IsCancellationRequested)
                        {
                            request.Abort();
                            linkedCts.Token.ThrowIfCancellationRequested();
                        }

                        await Task.Yield();
                    }

                    long responseCode = request.responseCode;

                    //* وجود کد پاسخ یعنی درخواست به یک سرور وب رسیده است.
                    if (responseCode > 0)
                    {
                        string responseDetails = "سرور مسیر پشتیبان پاسخ داد | کد وضعیت=" + responseCode + " | نتیجه=" + request.result;

                        NetworkFileLogger.Info(logTag, responseDetails);

                        return ProbeResult.Success("مسیر پشتیبان", (int)responseCode, responseDetails);
                    }

                    return ProbeResult.Failure("مسیر پشتیبان", 0, request.error ?? "مسیر پشتیبان بدون کد پاسخ پایان یافت.");
                }
                catch (OperationCanceledException)
                {
                    request.Abort();

                    if (lifecycleToken.IsCancellationRequested) throw;

                    return ProbeResult.Failure("مسیر پشتیبان", 0, "مهلت درخواست پس از " + safeTimeoutMs + " میلی‌ثانیه پایان یافت.");
                }
                catch (Exception ex)
                {
                    request.Abort();

                    return ProbeResult.Failure("مسیر پشتیبان", 0, ex.Message);
                }
            }
        }

        //* علت شکست مسیر اصلی را برای ثبت در گزارش آماده می‌کند.
        private string BuildPrimaryFailureReason(ApiResult<byte[]> result)
        {
            if (result == null) return "نتیجه بررسی سلامت سرور خالی است.";

            string error = result.ErrorMessage ?? string.Empty;
            string body = result.RawBody ?? string.Empty;

            if (string.IsNullOrWhiteSpace(body)) return string.IsNullOrWhiteSpace(error) ? "علت شکست مسیر اصلی مشخص نیست." : error;

            if (string.IsNullOrWhiteSpace(error)) return body;

            return error + " | " + body;
        }

        //* نتیجه موفق بررسی را همراه با منبع پاسخ در گزارش ثبت می‌کند.
        private string BuildSuccessDetails(NetworkReachability reportedReachability, ProbeResult successfulProbe, int attemptNumber)
        {
            string details = "ارتباط با سرور تأیید شد | شماره تلاش=" + attemptNumber + " | منبع پاسخ=" + successfulProbe.Source + " | کد وضعیت=" + successfulProbe.StatusCode + " | گزارش دسترسی دستگاه=" + reportedReachability + " | جزئیات=" + successfulProbe.Details;

            if (reportedReachability == NetworkReachability.NotReachable) NetworkFileLogger.Warning("GLOBAL_NETWORK_REACHABILITY_MISMATCH", "دستگاه نبود دسترسی را گزارش کرد، اما سرور پاسخ داد؛ پاسخ سرور ملاک قرار گرفت.");

            return details;
        }

        //* نتیجه هر تلاش را به یک متن کوتاه برای گزارش نهایی تبدیل می‌کند.
        private string FormatProbeResult(ProbeResult result)
        {
            if (result == null) return "اجرا نشد";

            return "منبع=" + result.Source + ", موفق=" + result.IsSuccess + ", کد=" + result.StatusCode + ", علت=" + result.Details;
        }

        //* بررسی دوره‌ای شبکه را اجرا می‌کند و پیش از هر بررسی صبر می‌کند.
        private async Task MonitorLoopAsync(CancellationToken cancellationToken)
        {
            NetworkFileLogger.Info("GLOBAL_NETWORK_MONITOR", "مانیتور دوره‌ای شبکه آغاز شد.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int delayMs = Mathf.RoundToInt(Mathf.Max(0.5f, monitorIntervalSeconds) * 1000f);

                    await Task.Delay(delayMs, cancellationToken);
                    await CheckNowAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("GLOBAL_NETWORK_MONITOR", ex);
                }
            }

            NetworkFileLogger.Info("GLOBAL_NETWORK_MONITOR", "مانیتور دوره‌ای شبکه متوقف شد.");
        }


        #endregion

        #region وضعیت و رویدادها

        //* وضعیت سراسری شبکه را فقط هنگام تغییر واقعی به‌روزرسانی می‌کند.
        //* پیام بررسی یا قطعی را پیش از آگاه‌کردن شنونده‌ها نمایش می‌دهد و سپس رویدادهای مربوط را می‌فرستد.
        private void SetNetworkState(NetworkState newState, string technicalDetails)
        {
            NetworkState previousState = CurrentState;

            if (previousState == newState) return;

            CurrentState = newState;
            bool networkLost = previousState == NetworkState.Online && (newState == NetworkState.InternetUnavailable || newState == NetworkState.ServerUnavailable);
            bool networkRecovered = (previousState == NetworkState.InternetUnavailable || previousState == NetworkState.ServerUnavailable) && newState == NetworkState.Online;

            NetworkFileLogger.Info("GLOBAL_NETWORK_STATE", "قبلی=" + previousState + " | فعلی=" + newState + " | روش ارتباط=" + ServerConfig.CurrentTransportKind + " | نشانی سرور=" + ServerConfig.CurrentEndpoint + " | جزئیات=" + (technicalDetails ?? string.Empty));

            if (newState == NetworkState.Checking)
            {
                GlobalMessageManager.ShowNetworkChecking(NetworkCheckingMessageId, "بررسی ارتباط", "ارتباط با سرور در حال بررسی است. لطفاً چند لحظه منتظر بمانید.", technicalDetails ?? string.Empty);
            }
            else if (newState == NetworkState.InternetUnavailable)
            {
                GlobalMessageManager.Clear(NetworkCheckingMessageId);
                GlobalMessageManager.ShowInternetUnavailable(NetworkStateMessageId, "اینترنت قطع شد", "ارتباط با سرور برقرار نیست. پس از بازگشت اینترنت بازیابی در همین صحنه انجام می‌شود.", technicalDetails ?? string.Empty, null);
            }
            else if (newState == NetworkState.ServerUnavailable)
            {
                GlobalMessageManager.Clear(NetworkCheckingMessageId);
                GlobalMessageManager.ShowServerUnavailable(NetworkStateMessageId, "سرور در دسترس نیست", "اینترنت برقرار است، اما مسیر اصلی سرور هنوز آماده نیست. بازیابی به صورت خودکار ادامه دارد.", technicalDetails ?? string.Empty, null);
            }
            else if (networkRecovered)
            {
                GlobalMessageManager.Clear(NetworkCheckingMessageId);
                GlobalMessageManager.ShowNetworkRecovering(NetworkStateMessageId, "در حال ورود مجدد", "ارتباط با سرور برقرار شد. ورود مجدد در حال انجام است.", technicalDetails ?? string.Empty);
            }
            else if (newState == NetworkState.Online)
            {
                GlobalMessageManager.Clear(NetworkCheckingMessageId);
                GlobalMessageManager.Clear(NetworkStateMessageId);
            }

            Action<NetworkState> stateChangedHandler = OnNetworkStateChanged;

            if (stateChangedHandler != null)
            {
                try
                {
                    stateChangedHandler(newState);
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("GLOBAL_NETWORK_STATE_EVENT", ex);
                }
            }

            if (networkLost)
            {
                Action lostHandler = OnNetworkLost;

                if (lostHandler != null)
                {
                    try
                    {
                        lostHandler();
                    }
                    catch (Exception ex)
                    {
                        NetworkFileLogger.Exception("GLOBAL_NETWORK_LOST_EVENT", ex);
                    }
                }

            }
            if (networkRecovered)
            {
                Action recoveredHandler = OnNetworkRecovered;

                if (recoveredHandler != null)
                {
                    try
                    {
                        recoveredHandler();
                    }
                    catch (Exception ex)
                    {
                        NetworkFileLogger.Exception("GLOBAL_NETWORK_RECOVERED_EVENT", ex);
                    }
                }
            }
        }

        #endregion

        #region مدل‌های نتیجه

        //* نتیجه یک درخواست منفرد بررسی سلامت را نگه می‌دارد.
        private sealed class ProbeResult
        {
            public bool IsSuccess;
            public string Source;
            public int StatusCode;
            public string Details;

            //* نتیجه موفق یک درخواست سلامت را همراه با منبع و کد پاسخ می‌سازد.
            public static ProbeResult Success(string source, int statusCode, string details) => new ProbeResult { IsSuccess = true, Source = source ?? string.Empty, StatusCode = statusCode, Details = details ?? string.Empty };

            //* نتیجه ناموفق یک درخواست سلامت را همراه با منبع و علت شکست می‌سازد.
            public static ProbeResult Failure(string source, int statusCode, string details) => new ProbeResult { IsSuccess = false, Source = source ?? string.Empty, StatusCode = statusCode, Details = details ?? string.Empty };
        }

        //* نتیجه نهایی سه درخواست تأییدی را نگه می‌دارد.
        private sealed class ConnectivityCheckResult
        {
            public bool IsSuccess;
            public string Details;
            public NetworkState FailureState;

            //* نتیجه موفق بررسی کامل ارتباط را با جزئیات قطعی می‌سازد.
            public static ConnectivityCheckResult Success(string details) => new ConnectivityCheckResult { IsSuccess = true, Details = details ?? string.Empty, FailureState = NetworkState.Online };

            //* نتیجه ناموفق بررسی کامل ارتباط را همراه با نوع واقعی قطعی می‌سازد.
            public static ConnectivityCheckResult Failure(string details, NetworkState failureState = NetworkState.InternetUnavailable)
            {
                NetworkState safeFailureState = failureState == NetworkState.ServerUnavailable
                    ? NetworkState.ServerUnavailable
                    : NetworkState.InternetUnavailable;

                return new ConnectivityCheckResult
                {
                    IsSuccess = false,
                    Details = details ?? string.Empty,
                    FailureState = safeFailureState
                };
            }
        }

        #endregion

        //* پایان این اسکریپت: این مدیر فقط سلامت ارتباط را می‌سنجد، وضعیت سراسری را اعلام می‌کند و هیچ صحنه‌ای را تغییر نمی‌دهد.
    }
}
