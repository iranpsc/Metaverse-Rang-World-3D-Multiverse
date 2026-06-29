using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer.Protocol;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.GameServer.Auth
{
    public class DedicatedTicketVerifier : MonoBehaviour
    {
        [Header("Runtime")]
        [SerializeField] private DedicatedServerRuntime runtime;

        [Header("Http")]
        [SerializeField] private int timeoutSeconds = 15;
        [SerializeField] private bool logRawResponse = true;

        public string LastReason { get; private set; }
        public string LastError { get; private set; }

        //* این تابع رفرنس ران تایم ددیکیتد سرور را هنگام شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureRuntimeReference();
        }

        //* این تابع رفرنس ران تایم را از همین آبجکت، والد یا سینگلتون پیدا می کند.
        private void EnsureRuntimeReference()
        {
            if (runtime != null) return;

            runtime = GetComponent<DedicatedServerRuntime>();
            if (runtime != null) return;

            runtime = GetComponentInParent<DedicatedServerRuntime>();
            if (runtime != null) return;

            runtime = DedicatedServerRuntime.Instance;
        }

        //* این تابع تیکت دریافتی از کلاینت را با نود جی اس وریفای می کند.
        public async Task<DedicatedVerifyTicketResult> VerifyTicketAsync(
            DedicatedAuthTicketMessageDto authMessage,
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            DedicatedServerConfigData config = GetSafeConfig();

            if (config == null)
            {
                return Fail("runtime_config_missing", "Dedicated runtime config is missing.");
            }

            if (string.IsNullOrWhiteSpace(config.serviceToken))
            {
                return Fail("service_token_missing", "Service token is empty.");
            }

            if (!ValidateAuthMessage(authMessage, out string validationError))
            {
                return Fail("auth_ticket_invalid", validationError);
            }

            DedicatedVerifyTicketRequestDto requestDto = new DedicatedVerifyTicketRequestDto
            {
                serviceToken = config.serviceToken,
                serverId = SafeValue(authMessage.serverId, config.serverId),
                roomId = SafeValue(authMessage.roomId, config.roomId),
                userId = SafeTrim(authMessage.userId),
                ticketId = SafeTrim(authMessage.ticketId),
                signature = SafeTrim(authMessage.signature),
                sessionId = SafeTrim(authMessage.sessionId),
                connectionId = SafeTrim(connectionId),
                playerId = BuildPlayerId(authMessage),
                userName = SafeValue(authMessage.userName, SafeTrim(authMessage.userId)),
                metadata = new DedicatedVerifyTicketMetadataDto
                {
                    source = "unity_dedicated_server",
                    platform = Application.platform.ToString(),
                    unityVersion = Application.unityVersion,
                    connectionType = "websocket"
                }
            };

            string url = BuildControlUrl(config.controlBaseUrl, "/game-server-control/dedicated/verify-ticket");
            string json = JsonUtility.ToJson(requestDto);

            DedicatedVerifyHttpResult httpResult = await SendJsonPostAsync(url, json, cancellationToken);

            if (!httpResult.IsSuccess)
            {
                return Fail("verify_http_failed", httpResult.ErrorMessage, httpResult.StatusCode, httpResult.RawBody);
            }

            DedicatedVerifyTicketResponseDto response = ParseResponse(httpResult.RawBody);

            if (response == null)
            {
                return Fail("verify_response_parse_failed", "Verify response could not be parsed.", httpResult.StatusCode, httpResult.RawBody);
            }

            LastReason = response.reason;
            LastError = response.success ? string.Empty : response.message;

            if (!response.success)
            {
                return Fail(response.reason, response.message, httpResult.StatusCode, httpResult.RawBody, response);
            }

            Debug.Log("[DedicatedTicketVerifier] Verify ok | reason=" + response.reason +
                      " | userId=" + requestDto.userId +
                      " | connectionId=" + requestDto.connectionId);

            return DedicatedVerifyTicketResult.Success(httpResult.StatusCode, httpResult.RawBody, response, requestDto);
        }

        //* این تابع کانفیگ فعال ران تایم را امن برمی گرداند.
        private DedicatedServerConfigData GetSafeConfig()
        {
            EnsureRuntimeReference();

            if (runtime == null) return null;

            return runtime.GetCurrentConfig();
        }

        //* این تابع پیام auth_ticket را قبل از ارسال به نود جی اس اعتبارسنجی می کند.
        private bool ValidateAuthMessage(DedicatedAuthTicketMessageDto authMessage, out string error)
        {
            if (authMessage == null)
            {
                error = "Auth ticket message is empty.";
                return false;
            }

            if (SafeTrim(authMessage.type) != "auth_ticket")
            {
                error = "Message type is not auth_ticket.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(authMessage.ticketId))
            {
                error = "ticketId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(authMessage.signature))
            {
                error = "signature is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(authMessage.userId))
            {
                error = "userId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(authMessage.roomId))
            {
                error = "roomId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(authMessage.serverId))
            {
                error = "serverId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(authMessage.sessionId))
            {
                error = "sessionId is required.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        //* این تابع درخواست جیسون پست را با یونیتی وب ریکوئست ارسال می کند.
        private async Task<DedicatedVerifyHttpResult> SendJsonPostAsync(string url, string json, CancellationToken cancellationToken)
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
                        return DedicatedVerifyHttpResult.Fail(0, "Request cancelled.", string.Empty);
                    }

                    await Task.Yield();
                }

                string rawBody = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                int statusCode = (int)request.responseCode;

                if (logRawResponse)
                {
                    Debug.Log("[DedicatedTicketVerifier] Status=" + statusCode + " Body=" + rawBody);
                }

                bool isHttpOk = statusCode >= 200 && statusCode < 300;
                bool hasTransportError =
                    request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.DataProcessingError;

                if (!isHttpOk || hasTransportError)
                {
                    string error = string.IsNullOrWhiteSpace(request.error) ? rawBody : request.error;
                    return DedicatedVerifyHttpResult.Fail(statusCode, error, rawBody);
                }

                return DedicatedVerifyHttpResult.Success(statusCode, rawBody);
            }
        }

        //* این تابع پاسخ وریفای تیکت را از جیسون می خواند.
        private DedicatedVerifyTicketResponseDto ParseResponse(string rawBody)
        {
            if (string.IsNullOrWhiteSpace(rawBody)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedVerifyTicketResponseDto>(rawBody);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedTicketVerifier] Parse failed | " + ex.Message);
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

        //* این تابع پلیر آی دی را از پیام کلاینت یا یوزر آی دی می سازد.
        private string BuildPlayerId(DedicatedAuthTicketMessageDto authMessage)
        {
            string playerId = SafeTrim(authMessage.playerId);
            if (!string.IsNullOrWhiteSpace(playerId)) return playerId;

            return SafeTrim(authMessage.userId);
        }

        //* این تابع اگر مقدار اول خالی باشد مقدار جایگزین را برمی گرداند.
        private string SafeValue(string value, string fallback)
        {
            string safe = SafeTrim(value);
            if (!string.IsNullOrWhiteSpace(safe)) return safe;

            return SafeTrim(fallback);
        }

        //* این تابع مقدار رشته را بدون نال و فاصله اضافه برمی گرداند.
        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع نتیجه ناموفق وریفای را می سازد.
        private DedicatedVerifyTicketResult Fail(
            string reason,
            string message,
            int statusCode = 0,
            string rawBody = "",
            DedicatedVerifyTicketResponseDto response = null)
        {
            LastReason = reason;
            LastError = message;

            Debug.LogError("[DedicatedTicketVerifier] Verify failed | reason=" + reason + " | message=" + message);

            return DedicatedVerifyTicketResult.Fail(statusCode, rawBody, reason, message, response);
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت مسئول وریفای کردن تیکت پلیر با نود جی اس است.
        ددیکیتد سرور تیکت را از کلاینت می گیرد ولی خودش به تنهایی به آن اعتماد نمی کند.
        این فایل تیکت، امضا، یوزر آی دی، روم، سرور و سشن را به نود جی اس می فرستد.
        اگر نود جی اس پاسخ game_ticket_verified بدهد، هندشیک اجازه ورود پلیر را صادر می کند.
        سرویس توکن فقط در ددیکیتد سرور استفاده می شود و نباید در کلاینت عمومی باشد.
        */
    }

    public class DedicatedVerifyTicketResult
    {
        public bool IsSuccess { get; private set; }
        public int StatusCode { get; private set; }
        public string RawBody { get; private set; }
        public string Reason { get; private set; }
        public string Message { get; private set; }
        public DedicatedVerifyTicketResponseDto Response { get; private set; }
        public DedicatedVerifyTicketRequestDto Request { get; private set; }

        //* این تابع نتیجه موفق وریفای تیکت را می سازد.
        public static DedicatedVerifyTicketResult Success(
            int statusCode,
            string rawBody,
            DedicatedVerifyTicketResponseDto response,
            DedicatedVerifyTicketRequestDto request)
        {
            return new DedicatedVerifyTicketResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                RawBody = rawBody,
                Reason = response != null ? response.reason : "game_ticket_verified",
                Message = response != null ? response.message : "Game ticket verified.",
                Response = response,
                Request = request
            };
        }

        //* این تابع نتیجه ناموفق وریفای تیکت را می سازد.
        public static DedicatedVerifyTicketResult Fail(
            int statusCode,
            string rawBody,
            string reason,
            string message,
            DedicatedVerifyTicketResponseDto response)
        {
            return new DedicatedVerifyTicketResult
            {
                IsSuccess = false,
                StatusCode = statusCode,
                RawBody = rawBody,
                Reason = string.IsNullOrWhiteSpace(reason) ? "verify_failed" : reason,
                Message = string.IsNullOrWhiteSpace(message) ? "Verify ticket failed." : message,
                Response = response
            };
        }
    }

    public class DedicatedVerifyHttpResult
    {
        public bool IsSuccess { get; private set; }
        public int StatusCode { get; private set; }
        public string ErrorMessage { get; private set; }
        public string RawBody { get; private set; }

        //* این تابع نتیجه موفق درخواست اچ تی تی پی را می سازد.
        public static DedicatedVerifyHttpResult Success(int statusCode, string rawBody)
        {
            return new DedicatedVerifyHttpResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                ErrorMessage = string.Empty,
                RawBody = rawBody
            };
        }

        //* این تابع نتیجه ناموفق درخواست اچ تی تی پی را می سازد.
        public static DedicatedVerifyHttpResult Fail(int statusCode, string errorMessage, string rawBody)
        {
            return new DedicatedVerifyHttpResult
            {
                IsSuccess = false,
                StatusCode = statusCode,
                ErrorMessage = errorMessage,
                RawBody = rawBody
            };
        }
    }
}
