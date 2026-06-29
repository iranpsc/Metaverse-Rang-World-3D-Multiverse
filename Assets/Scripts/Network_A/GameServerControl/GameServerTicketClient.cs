using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Project.UI.MainMenu;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.GameServerControl
{
    public class GameServerTicketClient : MonoBehaviour
    {
        public static GameServerTicketClient Instance { get; private set; }

        [Header("Game Server Control")]
        [SerializeField] private string gameServerControlBaseUrl = "https://dev-world-3d.metarang.com";
        [SerializeField] private string ticketPath = "/game-server-control/client/ticket";
        [SerializeField] private int timeoutSeconds = 15;
        [SerializeField] private bool autoRefreshOnUnauthorized = true;
        [SerializeField] private bool logRawResponse = true;

        [Header("Test Request")]
        [SerializeField] private string testRoomId = "room_vps_test_001";
        [SerializeField] private string testRegion = "eu-central";
        [SerializeField] private string testZone = "de-1";

        public GameServerTicketResponseDto LastTicketResponse { get; private set; }

        public event Action<GameServerTicketResponseDto> TicketReceived;
        public event Action<string> TicketFailed;

        //* این تابع سینگلتون ساده اسکریپت تیکت گیم سرور را آماده می کند.
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                return;
            }

            Destroy(gameObject);
        }

        //* این تابع برای دکمه تست یونیتی است و با مقادیر تست اینسپکتور تیکت می گیرد.
        public void Btn_RequestTestGameTicket()
        {
            _ = RequestTestGameTicketAsync();
        }

        //* این تابع با روم تستی داخل اینسپکتور درخواست تیکت را اجرا می کند.
        public async Task<GameServerTicketResult> RequestTestGameTicketAsync()
        {
            return await RequestGameTicketAsync(testRoomId, testRegion, testZone, default(CancellationToken));
        }

        //* این تابع ورودی اصلی برای گرفتن تیکت گیم سرور از کدهای لابی و روم است.
        public async Task<GameServerTicketResult> RequestGameTicketAsync(string roomId, string region, string zone, CancellationToken cancellationToken = default(CancellationToken))
        {
            return await RequestGameTicketInternalAsync(roomId, region, zone, true, cancellationToken);
        }

        //* این تابع درخواست تیکت را مدیریت می کند و در صورت خطای آث، یک بار رفرش توکن انجام می دهد.
        private async Task<GameServerTicketResult> RequestGameTicketInternalAsync(string roomId, string region, string zone, bool allowRefreshRetry, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return Fail("roomId is empty.", 0, string.Empty);

            string accessToken = SecureTokenStorage.GetAccessToken();

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                MainMenuMessageManager.Warning("برای گرفتن تیکت ابتدا وارد حساب شوید.");
                return Fail("Access token is empty.", 401, string.Empty);
            }

            string url = BuildUrl(gameServerControlBaseUrl, ticketPath);
            string jsonBody = BuildTicketRequestJson(roomId, region, zone);
            GameServerTicketHttpResult httpResult = await SendTicketRequestAsync(url, jsonBody, accessToken, cancellationToken);

            if (ShouldRefreshAndRetry(httpResult, allowRefreshRetry))
            {
                MainMenuMessageManager.Info("نشست کاربری در حال تمدید است...");

                bool refreshOk = await TryRefreshTokenAsync(cancellationToken);
                if (refreshOk) return await RequestGameTicketInternalAsync(roomId, region, zone, false, cancellationToken);
            }

            if (!httpResult.IsSuccess)
            {
                string error = BuildErrorMessage(httpResult);
                MainMenuMessageManager.Error(error);
                TicketFailed?.Invoke(error);
                return Fail(error, httpResult.StatusCode, httpResult.RawBody);
            }

            return HandleTicketResponse(httpResult);
        }

        //* این تابع بدنه جیسون درخواست تیکت را با داده های روم و متادیتای کلاینت می سازد.
        private string BuildTicketRequestJson(string roomId, string region, string zone)
        {
            var requestDto = new GameServerTicketRequestDto
            {
                roomId = roomId,
                region = region,
                zone = zone,
                metadata = new GameServerTicketMetadataDto
                {
                    source = "unity_client",
                    unityVersion = Application.unityVersion,
                    appVersion = Application.version,
                    platform = Application.platform.ToString()
                }
            };

            return JsonUtility.ToJson(requestDto);
        }

        //* این تابع تشخیص می دهد که پاسخ سرور نیاز به رفرش توکن و تلاش دوباره دارد یا نه.
        private bool ShouldRefreshAndRetry(GameServerTicketHttpResult result, bool allowRefreshRetry)
        {
            if (result == null) return false;
            if (!allowRefreshRetry) return false;
            if (!autoRefreshOnUnauthorized) return false;

            return result.StatusCode == 401 && AuthManager.Instance != null;
        }

        //* این تابع از آث منیجر فعلی پروژه برای رفرش کردن توکن استفاده می کند.
        private async Task<bool> TryRefreshTokenAsync(CancellationToken cancellationToken)
        {
            if (AuthManager.Instance == null) return false;

            try
            {
                var refreshResult = await AuthManager.Instance.RefreshAsync(cancellationToken);
                return refreshResult != null && refreshResult.IsSuccess;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GameServerTicketClient] Refresh failed | " + ex.Message);
                return false;
            }
        }

        //* این تابع درخواست اچ تی تی پی تیکت را با اکسس توکن ذخیره شده به نود جی اس می فرستد.
        private async Task<GameServerTicketHttpResult> SendTicketRequestAsync(string url, string jsonBody, string accessToken, CancellationToken cancellationToken)
        {
            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

                request.timeout = Mathf.Max(5, timeoutSeconds);
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + accessToken);
                request.SetRequestHeader("X-Metaverse-Client", Application.platform.ToString());
                request.SetRequestHeader("X-Metaverse-Version", Application.version);

                try
                {
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                    while (!operation.isDone)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            request.Abort();
                            return GameServerTicketHttpResult.Fail(0, "Request cancelled.", string.Empty);
                        }

                        await Task.Delay(10, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    request.Abort();
                    return GameServerTicketHttpResult.Fail(0, "Request cancelled.", string.Empty);
                }
                catch (Exception ex)
                {
                    return GameServerTicketHttpResult.Fail(0, ex.Message, string.Empty);
                }

                string rawBody = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;

                if (logRawResponse) Debug.Log("[GameServerTicketClient] Status=" + request.responseCode + " Body=" + rawBody);

                bool ok = request.result == UnityWebRequest.Result.Success;
                int statusCode = (int)request.responseCode;
                string error = request.error ?? string.Empty;

                if (!ok) return GameServerTicketHttpResult.Fail(statusCode, error, rawBody);

                return GameServerTicketHttpResult.Success(statusCode, rawBody);
            }
        }

        //* این تابع پاسخ جیسون سرور را به دی تی او تبدیل می کند و نتیجه نهایی را برمی گرداند.
        private GameServerTicketResult HandleTicketResponse(GameServerTicketHttpResult httpResult)
        {
            GameServerTicketResponseDto response = null;

            try
            {
                response = JsonUtility.FromJson<GameServerTicketResponseDto>(httpResult.RawBody);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[GameServerTicketClient] Json decode failed | " + ex.Message);
            }

            if (response == null)
            {
                string error = "Game ticket response decode failed.";
                MainMenuMessageManager.Error("پاسخ تیکت قابل خواندن نیست.");
                TicketFailed?.Invoke(error);
                return Fail(error, httpResult.StatusCode, httpResult.RawBody);
            }

            if (!response.success)
            {
                string error = string.IsNullOrWhiteSpace(response.message) ? response.reason : response.message;
                MainMenuMessageManager.Error("دریافت تیکت انجام نشد.");
                TicketFailed?.Invoke(error);
                return Fail(error, httpResult.StatusCode, httpResult.RawBody);
            }

            LastTicketResponse = response;
            TicketReceived?.Invoke(response);
            MainMenuMessageManager.Success("تیکت اتصال به سرور بازی دریافت شد.");
            LogTicketResponse(response);

            return GameServerTicketResult.Success(response, httpResult.RawBody, httpResult.StatusCode);
        }

        //* این تابع آدرس کامل مسیر تیکت را از بیس یو آر ال و مسیر داخلی می سازد.
        private string BuildUrl(string baseUrl, string path)
        {
            string safeBase = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim().TrimEnd('/');
            string safePath = string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim();

            if (!safePath.StartsWith("/")) safePath = "/" + safePath;

            return safeBase + safePath;
        }

        //* این تابع خطای فنی درخواست تیکت را به پیام قابل نمایش برای کاربر تبدیل می کند.
        private string BuildErrorMessage(GameServerTicketHttpResult result)
        {
            if (result == null) return "Game ticket request failed.";
            if (result.StatusCode == 401) return "برای گرفتن تیکت باید دوباره وارد شوید.";
            if (result.StatusCode == 404) return "مسیر دریافت تیکت روی سرور پیدا نشد.";
            if (result.StatusCode == 0) return "اتصال به سرور تیکت برقرار نشد.";
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) return result.ErrorMessage;

            return "Game ticket request failed.";
        }

        //* این تابع خلاصه تیکت دریافتی را برای دیباگ در کنسول یونیتی چاپ می کند.
        private void LogTicketResponse(GameServerTicketResponseDto response)
        {
            if (response == null || response.data == null || response.data.connection == null)
            {
                Debug.LogWarning("[GameServerTicketClient] Ticket response has no connection data.");
                return;
            }

            GameServerConnectionDto connection = response.data.connection;

            Debug.Log("[GameServerTicketClient] Ticket received | serverId=" +
                      connection.serverId +
                      " | host=" +
                      connection.host +
                      " | port=" +
                      connection.port +
                      " | roomId=" +
                      connection.roomId +
                      " | sessionId=" +
                      connection.sessionId);
        }

        //* این تابع نتیجه خطا را با لاگ استاندارد همین اسکریپت می سازد.
        private GameServerTicketResult Fail(string error, int statusCode, string rawBody)
        {
            Debug.LogWarning("[GameServerTicketClient] Failed | status=" + statusCode + " error=" + error);
            return GameServerTicketResult.Fail(error, statusCode, rawBody);
        }
    }

    [Serializable]
    public class GameServerTicketRequestDto
    {
        public string roomId;
        public string region;
        public string zone;
        public GameServerTicketMetadataDto metadata;
    }

    [Serializable]
    public class GameServerTicketMetadataDto
    {
        public string source;
        public string unityVersion;
        public string appVersion;
        public string platform;
    }

    [Serializable]
    public class GameServerTicketResponseDto
    {
        public bool success;
        public string reason;
        public string message;
        public GameServerTicketDataDto data;
        public long ts;
    }

    [Serializable]
    public class GameServerTicketDataDto
    {
        public string userId;
        public GameServerTicketDto ticket;
        public GameServerConnectionDto connection;
    }

    [Serializable]
    public class GameServerTicketDto
    {
        public string ticketId;
        public string userId;
        public string serverId;
        public string roomId;
        public string signature;
        public long expiresAt;
        public GameServerTicketMetaDto metadata;
    }

    [Serializable]
    public class GameServerTicketMetaDto
    {
        public string sessionId;
    }

    [Serializable]
    public class GameServerConnectionDto
    {
        public string serverId;
        public string host;
        public int port;
        public string roomId;
        public string region;
        public string zone;
        public string sessionId;
    }

    public class GameServerTicketResult
    {
        public bool IsSuccess { get; private set; }
        public int StatusCode { get; private set; }
        public string ErrorMessage { get; private set; }
        public string RawBody { get; private set; }
        public GameServerTicketResponseDto Response { get; private set; }

        //* این تابع نتیجه موفق گرفتن تیکت را برای مصرف بقیه اسکریپت ها می سازد.
        public static GameServerTicketResult Success(GameServerTicketResponseDto response, string rawBody, int statusCode)
        {
            return new GameServerTicketResult
            {
                IsSuccess = true,
                Response = response,
                RawBody = rawBody,
                StatusCode = statusCode,
                ErrorMessage = string.Empty
            };
        }

        //* این تابع نتیجه ناموفق گرفتن تیکت را برای مصرف بقیه اسکریپت ها می سازد.
        public static GameServerTicketResult Fail(string errorMessage, int statusCode, string rawBody)
        {
            return new GameServerTicketResult
            {
                IsSuccess = false,
                Response = null,
                RawBody = rawBody,
                StatusCode = statusCode,
                ErrorMessage = errorMessage
            };
        }
    }

    internal class GameServerTicketHttpResult
    {
        public bool IsSuccess;
        public int StatusCode;
        public string ErrorMessage;
        public string RawBody;

        //* این تابع نتیجه موفق اچ تی تی پی را داخل مدل داخلی اسکریپت قرار می دهد.
        public static GameServerTicketHttpResult Success(int statusCode, string rawBody)
        {
            return new GameServerTicketHttpResult
            {
                IsSuccess = true,
                StatusCode = statusCode,
                ErrorMessage = string.Empty,
                RawBody = rawBody
            };
        }

        //* این تابع نتیجه ناموفق اچ تی تی پی را داخل مدل داخلی اسکریپت قرار می دهد.
        public static GameServerTicketHttpResult Fail(int statusCode, string errorMessage, string rawBody)
        {
            return new GameServerTicketHttpResult
            {
                IsSuccess = false,
                StatusCode = statusCode,
                ErrorMessage = errorMessage,
                RawBody = rawBody
            };
        }
    }

    /*
    توضیح مکتوب فایل:
    این اسکریپت پل ارتباطی یونیتی با ماژول گیم سرور کنترل در نود جی اس است.
    بعد از لاگین کاربر، اکسس توکن از سکیور توکن استوریج خوانده می شود.
    سپس درخواست تیکت به مسیر کلاینت تیکت ارسال می شود.
    سرور نود جی اس یوزر آیدی را از داخل اکسس توکن استخراج می کند.
    بنابراین یوزر آیدی از بدنه ریکوئست پذیرفته نمی شود.
    خروجی این اسکریپت شامل تیکت، سشن آیدی، سرور آیدی، هاست، پورت و روم آیدی است.
    این خروجی در مرحله بعد برای اتصال کلاینت به یونیتی ددیکیتد سرور استفاده می شود.
    */
}