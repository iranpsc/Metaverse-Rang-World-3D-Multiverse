using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.GameServer
{
    //* کنترلر تست ساده برای اتصال، اَث، ورود به روم و ارسال اکشن از داخل یونیتی است.
    public class GameServerTestController : MonoBehaviour
    {
        [Header("Realtime")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080";
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private bool connectOnStart;
        [SerializeField] private bool authenticateAfterConnect = true;

        [Header("Room")]
        [SerializeField] private string roomId = "unity_test_room_01";
        [SerializeField] private string testActionType = "unity_test_action";

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست گیم‌سرور را در شروع آبجکت می‌سازد.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
        }

        //* اگر از اینسپکتور فعال باشد، اتصال تست را بعد از شروع اجرا می‌کند.
        private async void Start()
        {
            if (!connectOnStart) return;
            await ConnectAndAuthenticateAsync();
        }

        //* هنگام حذف آبجکت، اتصال و رویدادها را تمیز می‌کند.
        private async void OnDestroy()
        {
            lifecycleCts?.Cancel();

            if (gameServerClient != null) gameServerClient.Dispose();
            if (realtimeAuthClient != null) realtimeAuthClient.Dispose();
            if (realtimeClient != null) await realtimeClient.DisconnectAsync("GameServerTestController destroyed");
            if (realtimeClient != null) realtimeClient.Dispose();

            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Public Test Buttons>

        //* از اینسپکتور یا دکمه یوآی برای اتصال و اَث تستی صدا زده می‌شود.
        public async void ConnectAndAuthenticateButton()
        {
            await ConnectAndAuthenticateAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای ورود به روم تستی صدا زده می‌شود.
        public async void JoinRoomButton()
        {
            await JoinRoomAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای ارسال اکشن تستی صدا زده می‌شود.
        public async void SendPlayerActionButton()
        {
            await SendPlayerActionAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای ارسال وضعیت پلیر تستی صدا زده می‌شود.
        public async void SendPlayerStateButton()
        {
            await SendPlayerStateAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای خروج از روم تستی صدا زده می‌شود.
        public async void LeaveRoomButton()
        {
            await LeaveRoomAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای قطع اتصال تستی صدا زده می‌شود.
        public async void DisconnectButton()
        {
            await DisconnectAsync();
        }

        #endregion

        #region <Test Flow>

        //* کُر ریل‌تایم را وصل می‌کند و در صورت نیاز پیام اَث را می‌فرستد.
        public async Task<bool> ConnectAndAuthenticateAsync()
        {
            if (realtimeClient == null) CreateClients();

            bool connected = await realtimeClient.ConnectAsync(null, lifecycleCts.Token);
            Debug.Log("[GameServerTest] Connected: " + connected);
            if (!connected) return false;

            if (!authenticateAfterConnect) return true;

            bool authSent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            Debug.Log("[GameServerTest] Auth sent: " + authSent);
            return authSent;
        }

        //* درخواست ورود به روم تستی را ارسال می‌کند.
        public async Task<bool> JoinRoomAsync()
        {
            bool sent = await gameServerClient.JoinRoomAsync(roomId, lifecycleCts.Token);
            Debug.Log("[GameServerTest] Join room sent: " + sent);
            return sent;
        }

        //* اکشن تستی پلیر را به گیم‌سرور می‌فرستد.
        public async Task<bool> SendPlayerActionAsync()
        {
            string payloadJson = "{\"source\":\"unity\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
            bool sent = await gameServerClient.SendPlayerActionAsync(testActionType, payloadJson, lifecycleCts.Token);
            Debug.Log("[GameServerTest] Player action sent: " + sent);
            return sent;
        }

        //* وضعیت موقعیت و چرخش همین آبجکت را به گیم‌سرور می‌فرستد.
        public async Task<bool> SendPlayerStateAsync()
        {
            bool sent = await gameServerClient.SendPlayerStateAsync(transform.position, transform.rotation, lifecycleCts.Token);
            Debug.Log("[GameServerTest] Player state sent: " + sent);
            return sent;
        }

        //* درخواست خروج از روم تستی را ارسال می‌کند.
        public async Task<bool> LeaveRoomAsync()
        {
            bool sent = await gameServerClient.LeaveRoomAsync(roomId, lifecycleCts.Token);
            Debug.Log("[GameServerTest] Leave room sent: " + sent);
            return sent;
        }

        //* اتصال تستی را از سمت کُر ریل‌تایم قطع می‌کند.
        public async Task DisconnectAsync()
        {
            if (realtimeClient == null) return;
            await realtimeClient.DisconnectAsync("Manual test disconnect", lifecycleCts.Token);
        }

        #endregion

        #region <Client Setup>

        //* کلاینت‌های کُر، اَث و گیم‌سرور را می‌سازد و رویدادهای تست را وصل می‌کند.
        private void CreateClients()
        {
            var config = new RealtimeConfig
            {
                serverUrl = serverUrl,
                transportKind = transportKind,
                connectTimeoutMs = 10000,
                sendTimeoutMs = 10000,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = true,
                logOutgoingMessages = true
            };

            realtimeClient = new RealtimeClient(config);
            realtimeAuthClient = new RealtimeAuthClient(realtimeClient);
            gameServerClient = new GameServerClient(realtimeClient);
            BindEvents();
        }

        //* رویدادهای کلاینت‌ها را برای نمایش در کنسول یونیتی وصل می‌کند.
        private void BindEvents()
        {
            realtimeClient.StateChanged += state => Debug.Log("[GameServerTest] Realtime state: " + state);
            realtimeClient.TransportErrorReceived += error => Debug.LogWarning("[GameServerTest] Transport error: " + error);
            realtimeClient.Disconnected += reason => Debug.LogWarning("[GameServerTest] Disconnected: " + reason);
            realtimeAuthClient.Authenticated += (connectionId, userId) => Debug.Log("[GameServerTest] Authenticated: " + connectionId + " | " + userId);
            realtimeAuthClient.AuthenticationFailed += error => Debug.LogWarning("[GameServerTest] Auth failed: " + (error != null ? error.code + " | " + error.message : "unknown"));
            realtimeAuthClient.AuthLogReceived += message => Debug.Log("[GameServerTest] Auth: " + message);
            gameServerClient.Events.LogReceived += message => Debug.Log("[GameServerTest] Game: " + message);
            gameServerClient.Events.AckReceived += ack => Debug.Log("[GameServerTest] Ack: " + ack.originalMessageId + " | " + ack.status + " | " + ack.detailsJson);
            gameServerClient.Events.WorldEventReceived += envelope => Debug.Log("[GameServerTest] World event: " + envelope.payloadJson);
            gameServerClient.Events.PlayerStateReceived += envelope => Debug.Log("[GameServerTest] Player state: " + envelope.payloadJson);
            gameServerClient.Events.ErrorReceived += error => Debug.LogWarning("[GameServerTest] Game error: " + (error != null ? error.code + " | " + error.message : "unknown"));
        }

        #endregion
    }
}

//* این فایل کنترلر تست گیم‌سرور در یونیتی است.
//* این فایل برای تست دستی Connect، Auth، Join Room، Player Action و Leave Room استفاده می‌شود.
