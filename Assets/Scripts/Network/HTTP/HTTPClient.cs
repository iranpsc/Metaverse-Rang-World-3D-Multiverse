using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.Core.Interfaces;
using Assets.Scripts.Network.Core.Models;
using Assets.Scripts.Network.Core.Utils;
using Assets.Scripts.Network.Security;
using UnityEngine;
using UnityEngine.Networking;
using Assets.Scripts.Network.HTTP.Encoders;
namespace Assets.Scripts.Network.HTTP
{
    /// <summary>
    /// کلاینت اصلی HTTP با قابلیت‌های پیشرفته
    /// این کلاس پیاده‌سازی اینترفیس IRequest است و تمام درخواست‌های HTTP را مدیریت می‌کند
    /// </summary>
    public class HTTPClient : IRequest
    {
        private readonly HTTPHeadersManager headersManager;
        private readonly HTTPRetryPolicy retryPolicy;
        private readonly NetworkLogger logger;
        private readonly AuthManager authManager;

        private RequestState currentState = RequestState.Idle;
        private string currentRequestId = string.Empty;
        private CancellationTokenSource currentCancellationTokenSource;

        public RequestState State => currentState;
        public string RequestId => currentRequestId;

        // Refresh Gate (مرحله ۱)
        private readonly object refreshGateLock = new object();
        private Task<AuthResult> refreshGateTask; // اگر null نیست یعنی refresh در حال انجام است

        //Idempotent
        private const string TAG_IDEMPOTENT = "Idempotent";
        private const string TAG_NO_AUTH = "NoAuth";// proactive refresh trigger

        public NetworkLogger Logger => logger;// pass To All Scripts From Hier

        public HTTPClient(AuthManager authManager)
        {
            this.authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));

            headersManager = new HTTPHeadersManager(authManager);
            retryPolicy = new HTTPRetryPolicy();
            logger = new NetworkLogger
            {
                MinLogLevel = NetworkLogger.LogLevel.Debug,
                EnableConsoleLogging = true,
                EnableFileLogging = true // در تولید می‌توان فعال کرد
            };

            logger.LogInfo("HTTPClient initialized");
        }

        //  اگر ۱۰ request همزمان 401 بگیرند، همه‌شان می‌افتند داخل Gate و فقط یک refresh واقعی اجرا می‌شود.
        //  بعد از موفقیت refresh، همه می‌توانند retry کنند(که مرحله ۲ کنترلش می‌کند).
        private Task<AuthResult> GetOrCreateRefreshTask(CancellationToken token)
        {
            lock (refreshGateLock)
            {
                // اگر یک refresh در جریان است، همان را برگردان
                if (refreshGateTask != null && !refreshGateTask.IsCompleted)
                    return refreshGateTask;

                // در غیر این صورت یک refresh جدید بساز
                refreshGateTask = authManager.RefreshTokenAsync(token);
                return refreshGateTask;
            }
        }

        private bool CanRetryAfterRefresh(RequestModel request)
        {
            if (request == null) return false;

            // GET همیشه safe است
            if (request.Method == HttpMethod.GET)
                return true;

            // سایر متدها فقط اگر Idempotent تگ شده باشند
            if (request.Tags != null && request.Tags.Contains(TAG_IDEMPOTENT))
                return true;

            return false;
        }

        //* proactive refresh trigger
        private bool RequiresAuth(RequestModel request)
        {
            if (request == null) return false;

            // اگر تگ NoAuth داشت، یعنی این endpoint عمومی است (login/register/refresh)
            if (request.Tags != null && request.Tags.Contains(TAG_NO_AUTH))
                return false;

            // پیش‌فرض: همه‌ی درخواست‌ها auth می‌خوان
            return true;
        }

        //* proactive refresh trigger
        private async Task<ResponseModel> EnsureValidTokenBeforeSendAsync(RequestModel request, CancellationToken token)
        {
            // اگر این درخواست auth نمی‌خواد، کاری نکن
            if (!RequiresAuth(request))
                return null; // یعنی "همه چیز ok است، ادامه بده"

            // اگر authManager یا tokenStorage هنوز آماده نیست
            if (authManager == null)
            {
                var fail = ResponseModel.Failure(new NetworkError(NetworkErrorCode.Unknown, "AuthManager موجود نیست"));
                LogNetworkError(fail, request, "Proactive/AuthManagerNull");
                return fail;
            }

            // اگر همین الان توکن معتبر است، ادامه بده
            if (authManager.IsAuthenticated)
                return null;

            // اینجا یعنی توکن یا نداریم یا نزدیک انقضاست → refresh
            logger.LogInfo("Proactive Refresh: توکن معتبر نیست/نزدیک انقضاست، رفرش قبل از ارسال...");

            var refreshResult = await GetOrCreateRefreshTask(token); // همون Gate مرحله ۱

            if (refreshResult != null && refreshResult.IsSuccess)
            {
                logger.LogInfo("Proactive Refresh: موفق");
                return null; // ok
            }

            // اگر refresh شکست خورد → درخواست را ارسال نکن
            var proactiveFail = ResponseModel.Failure(
                new NetworkError(NetworkErrorCode.TokenExpired, "رفرش توکن شکست خورد - نیاز به لاگین"),
                string.Empty,
                401
            );

            LogNetworkError(proactiveFail, request, "Proactive/RefreshFailed");
            return proactiveFail;
        }

        /// <summary>
        /// ارسال درخواست به صورت همگام‌سازی‌نشده (Async)
        /// </summary>
        public async Task<IResponse> SendAsync(RequestModel request, CancellationToken cancellationToken = default)
        {
            // بررسی صحت درخواست
            if (request == null)
            {
                var nullReqErr = new NetworkError(NetworkErrorCode.InvalidRequest, "درخواست خالی است");
                logger.LogError(nullReqErr);
                return ResponseModel.Failure(nullReqErr);
            }

            // تولید شناسه درخواست
            currentRequestId = request.RequestId;
            currentState = RequestState.Sending;

            try
            {
                // ایجاد CancellationTokenSource برای مدیریت تایم‌اوت
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    // تنظیم تایم‌اوت
                    if (request.TimeoutMs > 0)
                        cts.CancelAfter(request.TimeoutMs);

                    currentCancellationTokenSource = cts;

                    // Proactive refresh (مرحله ۰)
                    var proactive = await EnsureValidTokenBeforeSendAsync(request, cts.Token);
                    if (proactive != null)
                    {
                        // یعنی refresh شکست خورد یا مشکلی بود → همین را برگردان
                        currentState = RequestState.Failed;

                        // ✅ log error with NetworkError code/details
                        if (!proactive.IsSuccess)
                            LogNetworkError(proactive, request, "SendAsync/ProactiveResult");

                        return proactive;
                    }

                    // اجرای درخواست با Retry Policy
                    ResponseModel response = await retryPolicy.ExecuteWithRetryAsync(
                        async (token) => await ExecuteRequestAsync(request, token),
                        cts.Token
                    );

                    // ✅ اگر بعد از retry هم fail است، همین‌جا استاندارد لاگ کن
                    if (!response.IsSuccess)
                        LogNetworkError(response, request, "SendAsync/AfterRetryPolicy");

                    // ==========================================================
                    // ✅ FIX: Reactive refresh MUST NOT run for NoAuth/public calls
                    // (مثل login/register/refresh). فقط برای درخواست‌های protected.
                    // ==========================================================
                    if (response.RequiresTokenRefresh() && RequiresAuth(request))
                    {
                        logger.LogWarning("توکن منقضی شده - ورود به Refresh Gate...");

                        // ✅ Gate: همه requestها یک refresh مشترک را await می‌کنند
                        var refreshResult = await GetOrCreateRefreshTask(cts.Token);

                        if (refreshResult != null && refreshResult.IsSuccess)
                        {
                            if (CanRetryAfterRefresh(request))
                            {
                                logger.LogInfo("توکن رفرش شد - درخواست مجدد (Retry Allowed) ...");

                                response = await ExecuteRequestAsync(request, cts.Token);

                                // ✅ اگر retry بعد از refresh هم fail شد، لاگ کن
                                if (!response.IsSuccess)
                                    LogNetworkError(response, request, "SendAsync/AfterRefreshRetry");
                            }
                            else
                            {
                                // retry ممنوع => باید به caller بگیم مجدد خودش تصمیم بگیره
                                logger.LogWarning("توکن رفرش شد اما Retry این درخواست مجاز نیست (Non-Idempotent).");

                                var nonIdempotentFail = ResponseModel.Failure(
                                    new NetworkError(
                                        NetworkErrorCode.Unauthorized,
                                        "درخواست نیاز به ارسال مجدد دارد (Retry غیرمجاز برای این متد)",
                                        $"Method: {request.Method}, RequestId: {request.RequestId}"
                                    ),
                                    response.RawData,
                                    response.StatusCode
                                );

                                LogNetworkError(nonIdempotentFail, request, "SendAsync/NonIdempotentAfterRefresh");
                                return nonIdempotentFail;
                            }
                        }
                        else
                        {
                            logger.LogError(new NetworkError(NetworkErrorCode.TokenExpired, "رفرش توکن شکست خورد"));
                            currentState = RequestState.Failed;

                            var refreshFail = ResponseModel.Failure(
                                new NetworkError(NetworkErrorCode.TokenExpired, "رفرش توکن شکست خورد", "Need login"),
                                response.RawData,
                                response.StatusCode
                            );

                            LogNetworkError(refreshFail, request, "SendAsync/RefreshGateFailed");
                            return refreshFail;
                        }
                    }

                    // تنظیم اطلاعات اضافی پاسخ
                    response.RelatedRequestId = currentRequestId;
                    response.Tags = request.Tags;
                    response.RequestEndTime = DateTime.UtcNow;

                    // لاگ‌گیری پاسخ
                    logger.LogResponse(response, currentRequestId);
                    Debug.Log(response.RawData);

                    currentState = response.IsSuccess ? RequestState.Completed : RequestState.Failed;
                    return response;
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("درخواست لغو شد", $"RequestId: {currentRequestId}");
                currentState = RequestState.Cancelled;
                return ResponseModel.Cancelled(currentRequestId);
            }
            catch (Exception ex)
            {
                logger.LogError(new NetworkError(NetworkErrorCode.Unknown, "خطای غیرمنتظره", ex.Message, ex), currentRequestId);
                currentState = RequestState.Failed;

                // یک Failure استاندارد برگردان
                var fail = ResponseModel.Failure(
                    new NetworkError(NetworkErrorCode.Unknown, "خطای غیرمنتظره", ex.Message, ex)
                );

                // ✅ و همین را هم لاگ استاندارد کن
                LogNetworkError(fail, request, "SendAsync/ExceptionCatch");

                return fail;
            }
            finally
            {
                currentCancellationTokenSource = null;
            }
        }

        /// <summary>
        /// لغو درخواست در حال اجرا
        /// </summary>
        public void Cancel()
        {
            currentCancellationTokenSource?.Cancel();
            currentState = RequestState.Cancelled;
            logger.LogWarning("درخواست به صورت دستی لغو شد", $"RequestId: {currentRequestId}");
        }

        /// <summary>
        /// اجرای واقعی درخواست با UnityWebRequest
        /// </summary>
        /// 
        /// 


        private async Task<ResponseModel> ExecuteRequestAsync(RequestModel request, CancellationToken cancellationToken)
        {
            // ساخت آدرس کامل
            string url = URLBuilder.BuildFromRequest(request);
            Debug.Log(url);
            // اعتبارسنجی آدرس
            if (!URLBuilder.IsValidUrl(url))
            {
                return ResponseModel.Failure(
                    new NetworkError(NetworkErrorCode.InvalidRequest, "آدرس نامعتبر است", $"URL: {url}")
                );
            }

            // دریافت هدرها
            var headers = headersManager.GetHeaders(request);

            // انتخاب متد HTTP
            string method = request.Method.ToString();

            // ✅ انتخاب Encoder (یکپارچه)
            var encoder = RequestBodyEncoderFactory.Get(request);

            // ✅ ساخت webRequest توسط encoder (ممکن است WWWForm بسازد یا UploadHandlerRaw)
            using (UnityWebRequest webRequest = encoder.Build(url, method, request))
            {
                // ✅ Fail-safe برای multipart:
                // اگر encoder گفت Content-Type دستی نزن، حذفش کن تا boundary خراب نشود.
                if (!encoder.ShouldSetContentTypeHeader)
                {
                    if (headers.ContainsKey("Content-Type"))
                        headers.Remove("Content-Type");
                }
                else
                {
                    // اگر قرار است دستی ست شود، از request.ContentType enforce کن
                    if (!string.IsNullOrEmpty(request.ContentType))
                        headers["Content-Type"] = request.ContentType;
                }

                // ✅ اینجا لاگ نهایی با هدرهای واقعی (بعد از حذف Content-Type برای multipart)
                logger.LogRequest(request, headers);

                // تنظیم هدرها
                foreach (var header in headers)
                    webRequest.SetRequestHeader(header.Key, header.Value);

                // تنظیم هندلر دانلود
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.timeout = Mathf.Max(5, request.TimeoutMs / 1000); // حداقل 5 ثانیه

                // شروع ارسال
                var asyncOperation = webRequest.SendWebRequest();

                // منتظر شدن با پشتیبانی از CancellationToken
                while (!asyncOperation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        return ResponseModel.Cancelled(request.RequestId);
                    }

                    await Task.Delay(10);
                }

                // بررسی خطاها
                if (webRequest.result == UnityWebRequest.Result.ConnectionError ||
                    webRequest.result == UnityWebRequest.Result.ProtocolError)
                {
                    NetworkErrorCode errorCode = DetermineErrorCode(webRequest);

                    return ResponseModel.Failure(
                        new NetworkError(
                            errorCode,
                            $"خطا در اتصال: {webRequest.error}",
                            $"URL: {url}, Status: {webRequest.responseCode}"
                        ),
                        webRequest.downloadHandler?.text ?? string.Empty,
                        (int)webRequest.responseCode
                    );
                }

                // موفقیت
                return ResponseModel.Success(
                    webRequest.downloadHandler.text,
                    (int)webRequest.responseCode,
                    webRequest.GetResponseHeaders()
                );
            }
        }



        /// <summary>
        /// تشخیص کد خطا از پاسخ UnityWebRequest
        /// </summary>
        private NetworkErrorCode DetermineErrorCode(UnityWebRequest webRequest)
        {
            if (webRequest.responseCode == 401)
                return NetworkErrorCode.Unauthorized;
            if (webRequest.responseCode == 403)
                return NetworkErrorCode.Forbidden;
            if (webRequest.responseCode == 404)
                return NetworkErrorCode.InvalidRequest;
            if (webRequest.responseCode >= 500 && webRequest.responseCode < 600)
                return NetworkErrorCode.ServerError;

            return NetworkErrorCode.ConnectionFailed;
        }

        /// <summary>
        /// متدهای کمکی برای درخواست‌های متداول
        /// </summary>
        public async Task<IResponse> GetAsync(string endpoint, Dictionary<string, string> queryParams = null, CancellationToken cancellationToken = default)
        {
            var request = new RequestModel
            {
                Method = HttpMethod.GET,
                Url = endpoint,
                QueryParams = queryParams ?? new Dictionary<string, string>()
            };

            return await SendAsync(request, cancellationToken);
        }

        public async Task<IResponse> PostAsync(string endpoint, object body, Dictionary<string, string> queryParams = null, CancellationToken cancellationToken = default)
        {
            var request = new RequestModel
            {
                Method = HttpMethod.POST,
                Url = endpoint,
                Body = body,
                QueryParams = queryParams ?? new Dictionary<string, string>()
            };

            return await SendAsync(request, cancellationToken);
        }

        public async Task<IResponse> PutAsync(string endpoint, object body, CancellationToken cancellationToken = default)
        {
            var request = new RequestModel
            {
                Method = HttpMethod.PUT,
                Url = endpoint,
                Body = body
            };

            return await SendAsync(request, cancellationToken);
        }

        public async Task<IResponse> DeleteAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            var request = new RequestModel
            {
                Method = HttpMethod.DELETE,
                Url = endpoint
            };

            return await SendAsync(request, cancellationToken);
        }

        /// <summary>
        /// دریافت وضعیت Circuit Breaker برای مانیتورینگ
        /// </summary>
        public string GetCircuitBreakerStatus()
        {
            return retryPolicy.GetCircuitBreakerStatus();
        }

        /// <summary>
        /// دریافت آمار لاگ‌ها
        /// </summary>
        public Dictionary<NetworkLogger.LogLevel, int> GetLogStatistics()
        {
            return logger.GetLogStatistics();
        }

        // =====================================================================================
        // ✅ NEW: Standard error logging with NetworkErrorCode + details (file + console)
        // =====================================================================================
        private void LogNetworkError(ResponseModel response, RequestModel request, string stage)
        {
            if (response == null) return;
            if (response.IsSuccess) return;

            NetworkError err = response.Error;

            // اگر Error نال بود، برای اینکه لاگ همیشه استاندارد باشد
            if (err == null)
            {
                err = new NetworkError(
                    NetworkErrorCode.Unknown,
                    "Request failed but ResponseModel.Error is null",
                    $"Stage={stage} | Method={request?.Method} | Url={request?.Url} | Status={response.StatusCode}"
                );
            }

            // Correlation
            string reqId = request?.RequestId ?? currentRequestId;
            err.RequestId = string.IsNullOrEmpty(err.RequestId) ? reqId : err.RequestId;

            // NOTE: RawData ممکن است بزرگ یا حساس باشد؛ پس خلاصه می‌کنیم
            string rawPreview = string.Empty;
            if (!string.IsNullOrEmpty(response.RawData))
            {
                int maxLen = Math.Min(200, response.RawData.Length);
                rawPreview = response.RawData.Substring(0, maxLen);
            }

            string tags = (request?.Tags == null) ? "null" : string.Join(",", request.Tags);

            string details =
                $"Stage={stage} | Method={request?.Method} | Url={request?.Url} | Status={response.StatusCode} | " +
                $"Tags={tags} | ErrorDetails={err.Details} | RawPreview={(string.IsNullOrEmpty(rawPreview) ? "EMPTY" : rawPreview)}";

            // یک NetworkError تازه می‌سازیم تا Details استاندارد و کامل شود
            var logErr = new NetworkError(err.Code, err.Message, details, err.OriginalException)
            {
                RequestId = err.RequestId,
                Timestamp = err.Timestamp
            };

            logger.LogError(logErr, logErr.RequestId);
        }
    }
}
