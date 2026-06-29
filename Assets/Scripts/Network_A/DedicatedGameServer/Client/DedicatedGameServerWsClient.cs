using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedGameServerWsClient : MonoBehaviour
    {
        public static DedicatedGameServerWsClient Instance { get; private set; }

        [Header("Connection")]
        [SerializeField] private string defaultHost = "127.0.0.1";
        [SerializeField] private int defaultPort = 7777;
        [SerializeField] private bool useSecureWebSocket = false;
        [SerializeField] private int connectTimeoutSeconds = 10;
        [SerializeField] private int authTimeoutSeconds = 10;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logRawMessages = true;

        private ClientWebSocket webSocket;
        private CancellationTokenSource connectionCts;
        private TaskCompletionSource<bool> authCompletionSource;

        public bool IsConnected { get; private set; }
        public bool IsAuthenticated { get; private set; }

        public string ConnectionId { get; private set; }
        public string UserId { get; private set; }
        public string PlayerId { get; private set; }
        public string RoomId { get; private set; }
        public string ServerId { get; private set; }
        public string SessionId { get; private set; }
        public string LastAuthReason { get; private set; }
        public string LastError { get; private set; }
        public string LastRawMessage { get; private set; }

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<string> RawMessageReceived;
        public event Action Authenticated;
        public event Action<string> AuthFailed;

        //* این تابع سینگلتون سبک کلاینت ددیکیتد را آماده می کند.
        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                return;
            }

            if (Instance != this)
            {
                Destroy(gameObject);
            }
        }

        //* این تابع هنگام حذف آبجکت، اتصال وب سوکت را تمیز می بندد.
        private void OnDestroy()
        {
            if (Instance == this)
            {
                Disconnect("destroyed");
                Instance = null;
            }
        }

        //* این تابع از اینسپکتور برای اتصال سریع به آدرس پیش فرض استفاده می شود.
        [ContextMenu("Connect Default Dedicated Server")]
        public async void Btn_ConnectDefault()
        {
            await ConnectToDedicatedServerAsync(defaultHost, defaultPort, useSecureWebSocket);
        }

        //* این تابع از اینسپکتور برای قطع اتصال استفاده می شود.
        [ContextMenu("Disconnect Dedicated Server")]
        public void Btn_Disconnect()
        {
            Disconnect("manual_disconnect");
        }

        //* این تابع به ددیکیتد سرور با هاست و پورت مشخص وصل می شود.
        public async Task<bool> ConnectToDedicatedServerAsync(
            string host,
            int port,
            bool secure,
            CancellationToken cancellationToken = default)
        {
            string safeHost = string.IsNullOrWhiteSpace(host) ? defaultHost : host.Trim();
            int safePort = Mathf.Max(1, port);
            string scheme = secure ? "wss" : "ws";
            string url = scheme + "://" + safeHost + ":" + safePort;

            return await ConnectAsync(url, cancellationToken);
        }

        //* این تابع اتصال خام وب سوکت به یونیتی ددیکیتد سرور را برقرار می کند.
        public async Task<bool> ConnectAsync(string url, CancellationToken cancellationToken = default)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            LastError = "DedicatedGameServerWsClient currently uses ClientWebSocket and is not enabled for WebGL build yet.";
            Debug.LogError("[DedicatedGameServerWsClient] " + LastError);
            return false;
#else
            if (IsConnected && webSocket != null && webSocket.State == WebSocketState.Open)
            {
                Log("Already connected.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return Fail("websocket_url_empty");
            }

            try
            {
                CleanupSocketOnly();

                webSocket = new ClientWebSocket();

                using (CancellationTokenSource connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectTimeoutCts.CancelAfter(Mathf.Max(1, connectTimeoutSeconds) * 1000);

                    Log("Connecting | url=" + url);
                    await webSocket.ConnectAsync(new Uri(url), connectTimeoutCts.Token);
                }

                IsConnected = webSocket.State == WebSocketState.Open;
                IsAuthenticated = false;
                LastError = string.Empty;

                if (!IsConnected)
                {
                    return Fail("websocket_connect_failed");
                }

                connectionCts = new CancellationTokenSource();

                Connected?.Invoke();

                Log("Connected.");
                _ = ReceiveLoopAsync(connectionCts.Token);

                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("connect_cancelled_or_timeout");
            }
            catch (Exception ex)
            {
                return Fail("connect_exception | " + ex.Message);
            }
#endif
        }

        //* این تابع بعد از اتصال، تیکت دریافتی از نود جی اس را برای ددیکیتد سرور ارسال می کند.
        public async Task<bool> AuthenticateWithTicketAsync(
            string ticketId,
            string signature,
            string userId,
            string roomId,
            string serverId,
            string sessionId,
            string playerId = "",
            string userName = "",
            CancellationToken cancellationToken = default)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            LastError = "DedicatedGameServerWsClient ticket auth is not enabled for WebGL build yet.";
            Debug.LogError("[DedicatedGameServerWsClient] " + LastError);
            return false;
#else
            if (!IsConnected || webSocket == null || webSocket.State != WebSocketState.Open)
            {
                return Fail("websocket_not_connected");
            }

            if (string.IsNullOrWhiteSpace(ticketId)) return Fail("ticket_id_empty");
            if (string.IsNullOrWhiteSpace(signature)) return Fail("signature_empty");
            if (string.IsNullOrWhiteSpace(userId)) return Fail("user_id_empty");
            if (string.IsNullOrWhiteSpace(roomId)) return Fail("room_id_empty");
            if (string.IsNullOrWhiteSpace(serverId)) return Fail("server_id_empty");
            if (string.IsNullOrWhiteSpace(sessionId)) return Fail("session_id_empty");

            DedicatedAuthTicketDto authTicket = new DedicatedAuthTicketDto
            {
                type = "auth_ticket",
                ticketId = ticketId.Trim(),
                signature = signature.Trim(),
                userId = userId.Trim(),
                roomId = roomId.Trim(),
                serverId = serverId.Trim(),
                sessionId = sessionId.Trim(),
                playerId = string.IsNullOrWhiteSpace(playerId) ? userId.Trim() : playerId.Trim(),
                userName = string.IsNullOrWhiteSpace(userName) ? userId.Trim() : userName.Trim()
            };

            authCompletionSource = new TaskCompletionSource<bool>();

            string json = JsonUtility.ToJson(authTicket);
            bool sent = await SendRawAsync(json, cancellationToken);

            if (!sent)
            {
                return Fail("auth_ticket_send_failed");
            }

            using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(Mathf.Max(1, authTimeoutSeconds) * 1000);

                try
                {
                    using (timeoutCts.Token.Register(() => authCompletionSource.TrySetCanceled()))
                    {
                        bool authOk = await authCompletionSource.Task;
                        return authOk;
                    }
                }
                catch (OperationCanceledException)
                {
                    return Fail("auth_timeout");
                }
            }
#endif
        }

        //* این تابع پیام وضعیت پلیر را بعد از احراز برای ددیکیتد سرور می فرستد.
        public async Task<bool> SendPlayerStateAsync(
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            long sequence,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || !IsAuthenticated)
            {
                return false;
            }

            DedicatedPlayerStateDto message = new DedicatedPlayerStateDto
            {
                type = "player_state",
                userId = UserId,
                playerId = PlayerId,
                roomId = RoomId,
                serverId = ServerId,
                sessionId = SessionId,
                sequence = sequence,
                timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),

                px = position.x,
                py = position.y,
                pz = position.z,

                rx = rotation.x,
                ry = rotation.y,
                rz = rotation.z,
                rw = rotation.w,

                vx = velocity.x,
                vy = velocity.y,
                vz = velocity.z
            };

            return await SendRawAsync(JsonUtility.ToJson(message), cancellationToken);
        }

        //* این تابع پیام خام جیسون را روی وب سوکت ارسال می کند.
        public async Task<bool> SendRawAsync(string text, CancellationToken cancellationToken = default)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            if (!IsConnected || webSocket == null || webSocket.State != WebSocketState.Open)
            {
                return false;
            }

            try
            {
                string safeText = text ?? string.Empty;
                byte[] bytes = Encoding.UTF8.GetBytes(safeText);
                ArraySegment<byte> segment = new ArraySegment<byte>(bytes);

                await webSocket.SendAsync(segment, WebSocketMessageType.Text, true, cancellationToken);

                if (logRawMessages)
                {
                    Log("Sent | " + safeText);
                }

                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogWarning("[DedicatedGameServerWsClient] Send failed | " + ex.Message);
                return false;
            }
#endif
        }

        //* این تابع اتصال وب سوکت را قطع می کند و وضعیت داخلی را پاک می کند.
        public void Disconnect(string reason = "client_disconnect")
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            IsConnected = false;
            IsAuthenticated = false;
#else
            bool wasConnected = IsConnected;

            try
            {
                if (connectionCts != null)
                {
                    connectionCts.Cancel();
                }

                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None).Wait(250);
                }
            }
            catch
            {
            }

            CleanupSocketOnly();

            IsConnected = false;
            IsAuthenticated = false;

            if (wasConnected)
            {
                Disconnected?.Invoke(reason);
            }

            Log("Disconnected | reason=" + reason);
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        //* این تابع حلقه دریافت پیام از ددیکیتد سرور را اجرا می کند.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[8192];

            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       webSocket != null &&
                       webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleDisconnected("server_closed");
                        return;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    while (!result.EndOfMessage)
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                        message += Encoding.UTF8.GetString(buffer, 0, result.Count);
                    }

                    HandleRawMessage(message);
                }
            }
            catch (OperationCanceledException)
            {
                if (IsConnected)
                {
                    HandleDisconnected("receive_cancelled");
                }
            }
            catch (Exception ex)
            {
                HandleDisconnected("receive_error | " + ex.Message);
            }
        }
#endif

        //* این تابع پیام خام دریافتی را لاگ می کند و پیام های احراز را پردازش می کند.
        private void HandleRawMessage(string message)
        {
            LastRawMessage = message ?? string.Empty;

            if (logRawMessages)
            {
                Log("Received | " + LastRawMessage);
            }

            RawMessageReceived?.Invoke(LastRawMessage);

            DedicatedMessageTypeDto typeDto = ParseMessageType(LastRawMessage);
            if (typeDto == null || string.IsNullOrWhiteSpace(typeDto.type)) return;

            if (typeDto.type == "server_hello")
            {
                return;
            }

            if (typeDto.type == "auth_ok")
            {
                HandleAuthOk(LastRawMessage);
                return;
            }

            if (typeDto.type == "auth_failed")
            {
                HandleAuthFailed(LastRawMessage);
                return;
            }
        }

        //* این تابع پیام auth_ok را پردازش و وضعیت کلاینت را احراز شده می کند.
        private void HandleAuthOk(string message)
        {
            DedicatedAuthOkDto authOk = null;

            try
            {
                authOk = JsonUtility.FromJson<DedicatedAuthOkDto>(message);
            }
            catch
            {
            }

            IsAuthenticated = authOk != null && authOk.ok;

            if (IsAuthenticated)
            {
                ConnectionId = authOk.connectionId;
                UserId = authOk.userId;
                PlayerId = authOk.playerId;
                RoomId = authOk.roomId;
                ServerId = authOk.serverId;
                SessionId = authOk.sessionId;
                LastAuthReason = authOk.reason;
                LastError = string.Empty;

                authCompletionSource?.TrySetResult(true);
                Authenticated?.Invoke();

                Log("Authenticated | userId=" + UserId + " | playerId=" + PlayerId);
            }
            else
            {
                authCompletionSource?.TrySetResult(false);
            }
        }

        //* این تابع پیام auth_failed را پردازش و وضعیت احراز را ناموفق می کند.
        private void HandleAuthFailed(string message)
        {
            DedicatedAuthFailedDto failed = null;

            try
            {
                failed = JsonUtility.FromJson<DedicatedAuthFailedDto>(message);
            }
            catch
            {
            }

            IsAuthenticated = false;
            LastAuthReason = failed != null ? failed.reason : "auth_failed";
            LastError = failed != null ? failed.message : message;

            authCompletionSource?.TrySetResult(false);
            AuthFailed?.Invoke(LastAuthReason);

            Debug.LogWarning("[DedicatedGameServerWsClient] Auth failed | reason=" + LastAuthReason + " | message=" + LastError);
        }

        //* این تابع تایپ پیام دریافتی را از جیسون می خواند.
        private DedicatedMessageTypeDto ParseMessageType(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedMessageTypeDto>(message);
            }
            catch
            {
                return null;
            }
        }

        //* این تابع قطع اتصال از سمت سرور یا خطای دریافت را پردازش می کند.
        private void HandleDisconnected(string reason)
        {
            bool wasConnected = IsConnected;

            CleanupSocketOnly();

            IsConnected = false;
            IsAuthenticated = false;

            if (wasConnected)
            {
                Disconnected?.Invoke(reason);
            }

            Log("Disconnected | reason=" + reason);
        }

        //* این تابع فقط منابع وب سوکت و توکن لغو را پاک می کند.
        private void CleanupSocketOnly()
        {
            try
            {
                if (connectionCts != null)
                {
                    connectionCts.Cancel();
                    connectionCts.Dispose();
                    connectionCts = null;
                }
            }
            catch
            {
            }

#if !UNITY_WEBGL || UNITY_EDITOR
            try
            {
                if (webSocket != null)
                {
                    webSocket.Dispose();
                    webSocket = null;
                }
            }
            catch
            {
            }
#endif
        }

        //* این تابع خطا را ثبت می کند و خروجی false می دهد.
        private bool Fail(string error)
        {
            LastError = error;
            Debug.LogError("[DedicatedGameServerWsClient] " + error);
            return false;
        }

        //* این تابع لاگ معمولی کلاینت ددیکیتد را چاپ می کند.
        private void Log(string message)
        {
            if (!verboseLogs) return;
            Debug.Log("[DedicatedGameServerWsClient] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این نسخه اصلاحی باعث می شود اتصال بعد از connectTimeoutSeconds قطع نشود.
        قبلاً توکن کنسل اتصال برای حلقه دریافت هم استفاده می شد و بعد از حدود ۱۰ ثانیه receive_cancelled می گرفتیم.
        حالا timeout فقط برای عملیات ConnectAsync است.
        بعد از اتصال، یک connectionCts جدا و بدون تایم اوت برای نگهداری اتصال ساخته می شود.
        قطع اتصال فقط با Disconnect، بستن سرور، خروج از پلی مود یا خطای واقعی انجام می شود.
        */

        [Serializable]
        private class DedicatedMessageTypeDto
        {
            public string type;
        }

        [Serializable]
        private class DedicatedAuthTicketDto
        {
            public string type;
            public string ticketId;
            public string signature;
            public string userId;
            public string roomId;
            public string serverId;
            public string sessionId;
            public string playerId;
            public string userName;
        }

        [Serializable]
        private class DedicatedAuthOkDto
        {
            public string type;
            public bool ok;
            public string reason;
            public string userId;
            public string playerId;
            public string connectionId;
            public string roomId;
            public string serverId;
            public string sessionId;
        }

        [Serializable]
        private class DedicatedAuthFailedDto
        {
            public string type;
            public bool ok;
            public string reason;
            public string message;
        }

        [Serializable]
        private class DedicatedPlayerStateDto
        {
            public string type;
            public string userId;
            public string playerId;
            public string roomId;
            public string serverId;
            public string sessionId;
            public long sequence;
            public long timestampUnixMs;

            public float px;
            public float py;
            public float pz;

            public float rx;
            public float ry;
            public float rz;
            public float rw;

            public float vx;
            public float vy;
            public float vz;
        }
    }
}
