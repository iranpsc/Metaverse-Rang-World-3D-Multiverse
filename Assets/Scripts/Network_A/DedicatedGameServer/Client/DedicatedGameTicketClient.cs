using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedGameTicketClient : MonoBehaviour
    {
        [Header("Node Game Server Control")]
        [SerializeField] private string controlBaseUrl = "https://dev-world-3d.metarang.com";

        [Header("Ticket Request")]
        [SerializeField] private string roomId = "";
        [SerializeField] private string roomName = "";
        [SerializeField] private int roomMaxPlayers = 50;
        [SerializeField] private string region = "eu-central";
        [SerializeField] private string zone = "de-1";
        [SerializeField] private string preferredServerId = "";
        [SerializeField] private int minFreeSlots = 1;
        [SerializeField] private int ticketTtlSeconds = 60;

        [Header("Http")]
        [SerializeField] private int timeoutSeconds = 15;
        [SerializeField] private bool logRawResponse = true;

        [Header("Http Transient Retry")]
        [SerializeField] private int maxTicketRequestAttempts = 3;
        [SerializeField] private int transientRetryBaseDelayMs = 500;
        [SerializeField] private int transientRetryMaxDelayMs = 2000;

        [Header("Auth Refresh Gate")]
        [SerializeField] private int accessTokenRefreshSkewSeconds = 60;

        public DedicatedGameTicketResponseDto LastResponse { get; private set; }
        public string LastError { get; private set; }
        public string LastRawBody { get; private set; }

        //* این تابع از اینسپکتور برای گرفتن دستی گیم تیکت استفاده می شود.
        [ContextMenu("Request Game Ticket")]
        public async void Btn_RequestGameTicket()
        {
            await RequestGameTicketAsync();
        }

        //* این تابع با اکسس توکن ذخیره شده، از نود جی اس گیم تیکت می گیرد.
        public async Task<DedicatedGameTicketResponseDto> RequestGameTicketAsync(CancellationToken cancellationToken = default)
        {
            LastResponse = null;
            LastError = string.Empty;
            LastRawBody = string.Empty;

            string accessToken = await EnsureFreshAccessTokenBeforeTicketAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                LastError = "access_token_missing_after_refresh_gate";
                Debug.LogError("[DedicatedGameTicketClient] Access token is missing after refresh gate. Login must complete before requesting game ticket.");
                return null;
            }

            DedicatedGameTicketRequestDto requestDto = new DedicatedGameTicketRequestDto
            {
                roomId = SafeTrim(roomId),
                roomName = SafeTrim(roomName),
                roomMaxPlayers = Mathf.Clamp(roomMaxPlayers, 1, 1024),
                region = SafeTrim(region),
                zone = SafeTrim(zone),
                preferredServerId = SafeTrim(preferredServerId),
                minFreeSlots = Mathf.Max(1, minFreeSlots),
                ticketTtlSeconds = Mathf.Clamp(ticketTtlSeconds, 5, 3600),
                metadata = new DedicatedGameTicketRequestMetadataDto
                {
                    source = "unity_client",
                    platform = Application.platform.ToString(),
                    unityVersion = Application.unityVersion,
                    client = "DedicatedGameTicketClient",
                    roomMaxPlayers = Mathf.Clamp(roomMaxPlayers, 1, 1024)
                }
            };

            if (string.IsNullOrWhiteSpace(requestDto.roomId))
            {
                LastError = "room_id_empty";
                Debug.LogError("[DedicatedGameTicketClient] Room id is empty.");
                return null;
            }

            string url = BuildControlUrl("/game-server-control/client/ticket");
            string json = JsonUtility.ToJson(requestDto);

            DedicatedTicketHttpResult httpResult = await SendJsonPostWithRetryAsync(
                url,
                json,
                accessToken,
                cancellationToken);

            LastRawBody = httpResult.RawBody;

            if (!httpResult.IsSuccess)
            {
                LastError = httpResult.ErrorMessage;
                Debug.LogError("[DedicatedGameTicketClient] Ticket request failed | " + httpResult.ErrorMessage);
                return null;
            }

            DedicatedGameTicketResponseDto response = ParseResponse(httpResult.RawBody);

            if (response == null)
            {
                LastError = "ticket_response_parse_failed";
                Debug.LogError("[DedicatedGameTicketClient] Ticket response parse failed.");
                return null;
            }

            if (!response.success)
            {
                LastError = response.reason + " | " + response.message;
                Debug.LogError("[DedicatedGameTicketClient] Ticket request rejected | reason=" + response.reason + " | message=" + response.message);
                return response;
            }

            if (!ValidateTicketResponse(response, out string validationError))
            {
                LastError = validationError;
                Debug.LogError("[DedicatedGameTicketClient] Ticket response invalid | " + validationError);
                return response;
            }

            LastResponse = response;
            LastError = string.Empty;

            Debug.Log("[DedicatedGameTicketClient] Ticket ok | ticketId=" +
                      response.data.ticket.ticketId +
                      " | serverId=" + response.data.connection.serverId +
                      " | roomId=" + response.data.connection.roomId +
                      " | roomName=" + SafeTrim(roomName) +
                      " | roomMaxPlayers=" + Mathf.Clamp(roomMaxPlayers, 1, 1024) +
                      " | host=" + response.data.connection.host +
                      " | port=" + response.data.connection.port +
                      " | secure=" + response.data.connection.secure +
                      " | path=" + SafeTrim(response.data.connection.path));

            return response;
        }

        public void SetRoomContext(string newRoomId, string newRoomName)
        {
            string safeRoomId = SafeTrim(newRoomId);
            string safeRoomName = SafeTrim(newRoomName);

            if (!string.IsNullOrWhiteSpace(safeRoomId)) roomId = safeRoomId;
            if (!string.IsNullOrWhiteSpace(safeRoomName)) roomName = safeRoomName;
        }

        public void SetRoomContext(string newRoomId, string newRoomName, int newRoomMaxPlayers)
        {
            SetRoomContext(newRoomId, newRoomName);
            SetRoomMaxPlayers(newRoomMaxPlayers);
        }

        public void SetRoomMaxPlayers(int newRoomMaxPlayers)
        {
            roomMaxPlayers = Mathf.Clamp(newRoomMaxPlayers, 1, 1024);
        }

        public void ClearRoomContext()
        {
            roomId = string.Empty;
            roomName = string.Empty;
            roomMaxPlayers = 50;
        }

        public void SetPreferredServerId(string newPreferredServerId)
        {
            preferredServerId = SafeTrim(newPreferredServerId);
        }

        public string GetCurrentRoomId()
        {
            return SafeTrim(roomId);
        }

        public string GetCurrentRoomName()
        {
            return SafeTrim(roomName);
        }

        public int GetCurrentRoomMaxPlayers()
        {
            return Mathf.Clamp(roomMaxPlayers, 1, 1024);
        }

        //* این تابع بررسی می کند پاسخ تیکت برای اتصال به ددیکیتد سرور کافی است یا نه.
        private bool ValidateTicketResponse(DedicatedGameTicketResponseDto response, out string error)
        {
            if (response == null || response.data == null)
            {
                error = "ticket_response_data_missing";
                return false;
            }

            if (response.data.ticket == null)
            {
                error = "ticket_missing";
                return false;
            }

            if (response.data.connection == null)
            {
                error = "connection_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.data.ticket.ticketId))
            {
                error = "ticket_id_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.data.ticket.signature))
            {
                error = "ticket_signature_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.data.userId))
            {
                error = "ticket_user_id_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.data.connection.roomId))
            {
                error = "connection_room_id_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.data.connection.serverId))
            {
                error = "connection_server_id_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.data.connection.sessionId))
            {
                error = "connection_session_id_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(response.data.connection.host))
            {
                error = "connection_host_missing";
                return false;
            }

            if (response.data.connection.port <= 0)
            {
                error = "connection_port_invalid";
                return false;
            }

            error = string.Empty;
            return true;
        }


        //* این تابع قبل از گرفتن تیکت، اکسس توکن را تازه می کند تا درخواست با توکن اکسپایر شده ارسال نشود.
        private async Task<string> EnsureFreshAccessTokenBeforeTicketAsync(CancellationToken cancellationToken)
        {
            string accessToken = SecureTokenStorage.GetAccessToken();

            if (!IsAccessTokenRefreshRequired(accessToken))
            {
                return string.IsNullOrWhiteSpace(accessToken) ? string.Empty : accessToken.Trim();
            }

            if (cancellationToken.IsCancellationRequested) return string.Empty;

            if (string.IsNullOrWhiteSpace(SecureTokenStorage.GetRefreshToken()))
            {
                Debug.LogWarning("[DedicatedGameTicketClient] Access token refresh is required before ticket request, but refresh token is empty.");
                return string.Empty;
            }

            Debug.Log("[DedicatedGameTicketClient] Access token is expired or near expiry. Refreshing before ticket request.");

            bool refreshed = await AuthRefreshManager.Refresh();

            if (!refreshed)
            {
                LastError = "refresh_before_ticket_failed";
                Debug.LogWarning("[DedicatedGameTicketClient] Refresh before ticket request failed.");
                return string.Empty;
            }

            string refreshedToken = SecureTokenStorage.GetAccessToken();

            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                LastError = "refresh_before_ticket_empty_access_token";
                Debug.LogWarning("[DedicatedGameTicketClient] Refresh before ticket request returned empty access token.");
                return string.Empty;
            }

            Debug.Log("[DedicatedGameTicketClient] Refresh before ticket request succeeded.");
            return refreshedToken.Trim();
        }

        //* این تابع تشخیص می دهد اکسس توکن خالی، اکسپایر شده یا نزدیک اکسپایر است یا نه.
        private bool IsAccessTokenRefreshRequired(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return true;

            if (!TryReadJwtExpiryUnixSeconds(accessToken, out long expiresAtUnixSeconds))
            {
                return false;
            }

            long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int safeSkewSeconds = Mathf.Clamp(accessTokenRefreshSkewSeconds, 0, 3600);

            return expiresAtUnixSeconds <= nowUnixSeconds + safeSkewSeconds;
        }

        //* این تابع زمان اکسپایر شدن توکن جی دبلیو تی را از کلیم exp می خواند.
        private static bool TryReadJwtExpiryUnixSeconds(string token, out long expiresAtUnixSeconds)
        {
            expiresAtUnixSeconds = 0;

            string payloadJson = ReadJwtPayloadJson(token);
            if (string.IsNullOrWhiteSpace(payloadJson)) return false;

            return TryExtractJsonLongValue(payloadJson, "exp", out expiresAtUnixSeconds);
        }

        //* این تابع پِیلود جی دبلیو تی را بدون وابستگی اضافه می خواند.
        private static string ReadJwtPayloadJson(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;

            string[] parts = token.Split('.');
            if (parts == null || parts.Length < 2) return string.Empty;

            return DecodeBase64UrlToString(parts[1]);
        }

        //* این تابع متن بیس شصت و چهار یو آر ال را دیکود می کند.
        private static string DecodeBase64UrlToString(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            string base64 = value.Replace('-', '+').Replace('_', '/');
            int padding = base64.Length % 4;
            if (padding == 2) base64 += "==";
            else if (padding == 3) base64 += "=";
            else if (padding != 0) return string.Empty;

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        //* این تابع مقدار عددی یک کلید جیسون را بدون وابستگی اضافه می خواند.
        private static bool TryExtractJsonLongValue(string json, string key, out long value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return false;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return false;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return false;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;

            bool quoted = valueStart < json.Length && json[valueStart] == '"';
            if (quoted) valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < json.Length)
            {
                char c = json[valueEnd];

                if (quoted)
                {
                    if (c == '"') break;
                }
                else if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c))
                {
                    break;
                }

                valueEnd++;
            }

            if (valueEnd <= valueStart) return false;

            string rawValue = json.Substring(valueStart, valueEnd - valueStart).Trim();
            return long.TryParse(rawValue, out value);
        }

        //* این تابع درخواست تیکت را در خطاهای موقت ترنسپورت با درخواست کاملاً جدید و تاخیر محدود دوباره ارسال می کند.
        private async Task<DedicatedTicketHttpResult> SendJsonPostWithRetryAsync(
            string url,
            string json,
            string accessToken,
            CancellationToken cancellationToken)
        {
            int safeMaxAttempts = maxTicketRequestAttempts <= 0
                ? 3
                : Mathf.Clamp(maxTicketRequestAttempts, 1, 10);
            DedicatedTicketHttpResult lastResult = null;

            for (int attempt = 1; attempt <= safeMaxAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return DedicatedTicketHttpResult.Fail(
                        0,
                        "request_cancelled",
                        string.Empty,
                        false,
                        "Cancelled");
                }

                lastResult = await SendJsonPostOnceAsync(
                    url,
                    json,
                    accessToken,
                    attempt,
                    safeMaxAttempts,
                    cancellationToken);

                if (lastResult.IsSuccess)
                {
                    return lastResult;
                }

                bool hasAnotherAttempt = attempt < safeMaxAttempts;
                bool shouldRetry =
                    hasAnotherAttempt &&
                    lastResult.IsTransientTransportFailure;

                if (!shouldRetry)
                {
                    return lastResult;
                }

                int delayMs = CalculateTransientRetryDelayMs(attempt);

                Debug.LogWarning(
                    "[DedicatedGameTicketClient] Transient ticket transport failure. " +
                    "A fresh request will be created after delay. " +
                    "attempt=" + attempt + "/" + safeMaxAttempts +
                    " | nextAttempt=" + (attempt + 1) +
                    " | delayMs=" + delayMs +
                    " | status=" + lastResult.StatusCode +
                    " | transportResult=" + lastResult.TransportResult +
                    " | error=" + lastResult.ErrorMessage);

                await Task.Delay(delayMs, cancellationToken);
            }

            return lastResult ?? DedicatedTicketHttpResult.Fail(
                0,
                "ticket_request_failed_without_result",
                string.Empty,
                false,
                "Unknown");
        }

        //* این تابع فاصله تلاش بعدی را به صورت افزایشی و محدود محاسبه می کند.
        private int CalculateTransientRetryDelayMs(int completedAttempt)
        {
            int configuredBaseDelayMs = transientRetryBaseDelayMs <= 0
                ? 500
                : transientRetryBaseDelayMs;

            int safeBaseDelayMs = Mathf.Clamp(
                configuredBaseDelayMs,
                100,
                10000);

            int configuredMaxDelayMs = transientRetryMaxDelayMs <= 0
                ? 2000
                : transientRetryMaxDelayMs;

            int safeMaxDelayMs = Mathf.Clamp(
                configuredMaxDelayMs,
                safeBaseDelayMs,
                30000);

            int multiplier = Mathf.Max(1, completedAttempt);
            long calculatedDelay = (long)safeBaseDelayMs * multiplier;

            return (int)Math.Min(calculatedDelay, safeMaxDelayMs);
        }

        //* این تابع فقط یک درخواست جیسون پست را با Authorization Bearer ارسال می کند و در پایان کامل Dispose می شود.
        private async Task<DedicatedTicketHttpResult> SendJsonPostOnceAsync(
            string url,
            string json,
            string accessToken,
            int attempt,
            int maxAttempts,
            CancellationToken cancellationToken)
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            float startedAt = Time.realtimeSinceStartup;

            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(1, timeoutSeconds);

                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + accessToken.Trim());
                request.SetRequestHeader("X-Metaverse-Client", "unity");

                Debug.Log(
                    "[DedicatedGameTicketClient] Ticket HTTP attempt started" +
                    " | attempt=" + attempt + "/" + maxAttempts +
                    " | timeoutSeconds=" + request.timeout +
                    " | url=" + url);

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();

                        return DedicatedTicketHttpResult.Fail(
                            0,
                            "request_cancelled",
                            string.Empty,
                            false,
                            "Cancelled");
                    }

                    await Task.Yield();
                }

                string rawBody = request.downloadHandler != null
                    ? request.downloadHandler.text
                    : string.Empty;

                int statusCode = (int)request.responseCode;
                int elapsedMs = Mathf.Max(
                    0,
                    Mathf.RoundToInt((Time.realtimeSinceStartup - startedAt) * 1000f));

                string transportResult = request.result.ToString();

                if (logRawResponse)
                {
                    Debug.Log(
                        "[DedicatedGameTicketClient] Ticket HTTP attempt completed" +
                        " | attempt=" + attempt + "/" + maxAttempts +
                        " | result=" + transportResult +
                        " | status=" + statusCode +
                        " | elapsedMs=" + elapsedMs +
                        " | Body=" + rawBody);
                }
                else
                {
                    Debug.Log(
                        "[DedicatedGameTicketClient] Ticket HTTP attempt completed" +
                        " | attempt=" + attempt + "/" + maxAttempts +
                        " | result=" + transportResult +
                        " | status=" + statusCode +
                        " | elapsedMs=" + elapsedMs);
                }

                bool isHttpOk = statusCode >= 200 && statusCode < 300;
                bool hasTransportError =
                    request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.DataProcessingError;

                bool isTransientTransportFailure =
                    statusCode == 0 &&
                    hasTransportError;

                if (!isHttpOk || hasTransportError)
                {
                    string error = string.IsNullOrWhiteSpace(request.error)
                        ? rawBody
                        : request.error;

                    return DedicatedTicketHttpResult.Fail(
                        statusCode,
                        error,
                        rawBody,
                        isTransientTransportFailure,
                        transportResult);
                }

                return DedicatedTicketHttpResult.Success(
                    statusCode,
                    rawBody,
                    transportResult);
            }
        }

        //* این تابع پاسخ جیسون گیم تیکت را می خواند.
        private DedicatedGameTicketResponseDto ParseResponse(string rawBody)
        {
            if (string.IsNullOrWhiteSpace(rawBody)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedGameTicketResponseDto>(rawBody);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedGameTicketClient] Parse exception | " + ex.Message);
                return null;
            }
        }

        //* این تابع آدرس کامل گیم سرور کنترل را می سازد.
        private string BuildControlUrl(string path)
        {
            string safeBase = SafeTrim(controlBaseUrl).TrimEnd('/');
            string safePath = SafeTrim(path);

            if (!safePath.StartsWith("/")) safePath = "/" + safePath;

            return safeBase + safePath;
        }

        //* این تابع مقدار رشته را بدون نال و فاصله اضافه برمی گرداند.
        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت سمت کلاینت، گیم تیکت را به صورت خودکار از نود جی اس می گیرد.
        اکسس توکن را از SecureTokenStorage می خواند و با هدر Authorization ارسال می کند.
        روم آی دی و روم نیم واقعی از مسیر ریل تایم روی همین کلاینت ست می شود.
        یوزر آی دی از بادی ارسال نمی شود، چون سرور باید آن را از اکسس توکن معتبر استخراج کند.
        مرحله بعد DedicatedGameServerAutoConnectController همین تیکت را به DedicatedGameServerWsClient می دهد.
        */
    }

    [Serializable]
    public class DedicatedGameTicketRequestDto
    {
        public string roomId;
        public string roomName;
        public int roomMaxPlayers;
        public string region;
        public string zone;
        public string preferredServerId;
        public int minFreeSlots;
        public int ticketTtlSeconds;
        public DedicatedGameTicketRequestMetadataDto metadata;
    }

    [Serializable]
    public class DedicatedGameTicketRequestMetadataDto
    {
        public string source;
        public string platform;
        public string unityVersion;
        public string client;
        public int roomMaxPlayers;
    }

    [Serializable]
    public class DedicatedGameTicketResponseDto
    {
        public bool success;
        public string reason;
        public string message;
        public DedicatedGameTicketResponseDataDto data;
        public long ts;
    }

    [Serializable]
    public class DedicatedGameTicketResponseDataDto
    {
        public string userId;
        public DedicatedGameTicketDto ticket;
        public DedicatedGameTicketConnectionDto connection;
        public DedicatedGameTicketRoomCapacityDto roomCapacity;
        public DedicatedGameTicketSessionDto session;
    }

    [Serializable]
    public class DedicatedGameTicketDto
    {
        public string ticketId;
        public string userId;
        public string roomId;
        public string serverId;
        public long expiresAt;
        public string signature;
    }

    [Serializable]
    public class DedicatedGameTicketConnectionDto
    {
        public string serverId;
        public string host;
        public int port;
        public bool secure;
        public string path;
        public string scheme;
        public string directHost;
        public int directPort;
        public string roomId;
        public string roomName;
        public int roomMaxPlayers;
        public string sessionId;
        public string region;
        public string zone;
        public int reservedPlayers;
        public int availableReservedSlots;
    }

    [Serializable]
    public class DedicatedGameTicketRoomCapacityDto
    {
        public string roomId;
        public string roomName;
        public int roomMaxPlayers;
        public string source;
        public int reservedPlayers;
        public int availableReservedSlots;
    }

    [Serializable]
    public class DedicatedGameTicketSessionDto
    {
        public string sessionId;
        public string roomId;
        public string serverId;
        public string status;
        public int maxPlayers;
        public int currentPlayers;
        public string region;
        public string zone;
    }

    public class DedicatedTicketHttpResult
    {
        public bool IsSuccess { get; private set; }
        public int StatusCode { get; private set; }
        public string ErrorMessage { get; private set; }
        public string RawBody { get; private set; }
        public bool IsTransientTransportFailure { get; private set; }
        public string TransportResult { get; private set; }

        //* این تابع نتیجه موفق اچ تی تی پی را می سازد.
        public static DedicatedTicketHttpResult Success(
            int statusCode,
            string rawBody,
            string transportResult = "Success")
        {
            return new DedicatedTicketHttpResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                ErrorMessage = string.Empty,
                RawBody = rawBody,
                IsTransientTransportFailure = false,
                TransportResult = transportResult ?? string.Empty
            };
        }

        //* این تابع نتیجه ناموفق اچ تی تی پی را می سازد و مشخص می کند خطا برای Retry موقت مناسب است یا نه.
        public static DedicatedTicketHttpResult Fail(
            int statusCode,
            string errorMessage,
            string rawBody,
            bool isTransientTransportFailure = false,
            string transportResult = "")
        {
            return new DedicatedTicketHttpResult
            {
                IsSuccess = false,
                StatusCode = statusCode,
                ErrorMessage = errorMessage ?? string.Empty,
                RawBody = rawBody ?? string.Empty,
                IsTransientTransportFailure = isTransientTransportFailure,
                TransportResult = transportResult ?? string.Empty
            };
        }
    }
}
