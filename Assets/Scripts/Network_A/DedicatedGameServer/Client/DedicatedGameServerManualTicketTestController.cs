using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedGameServerManualTicketTestController : MonoBehaviour
    {
        [Header("Client")]
        [SerializeField] private DedicatedGameServerWsClient client;

        [Header("Connection")]
        [SerializeField] private string host = "127.0.0.1";
        [SerializeField] private int port = 7777;
        [SerializeField] private bool secureWebSocket = false;

        [Header("Ticket From Node.js")]
        [SerializeField] private string ticketId;
        [SerializeField] private string signature;
        [SerializeField] private string userId;
        [SerializeField] private string roomId = "room_vps_test_001";
        [SerializeField] private string serverId = "ds_vps_test_001";
        [SerializeField] private string sessionId;
        [SerializeField] private string playerId;
        [SerializeField] private string userName = "unity_test_player";

        [Header("Debug")]
        [SerializeField] private bool logEvents = true;

        //* این تابع رفرنس کلاینت ددیکیتد را از همین آبجکت یا سینگلتون پیدا می کند.
        private void Awake()
        {
            EnsureClientReference();
        }

        //* این تابع رویدادهای کلاینت را هنگام فعال شدن آبجکت وصل می کند.
        private void OnEnable()
        {
            EnsureClientReference();

            if (client == null) return;

            client.Connected += HandleConnected;
            client.Disconnected += HandleDisconnected;
            client.RawMessageReceived += HandleRawMessageReceived;
            client.Authenticated += HandleAuthenticated;
            client.AuthFailed += HandleAuthFailed;
        }

        //* این تابع رویدادهای کلاینت را هنگام غیرفعال شدن آبجکت جدا می کند.
        private void OnDisable()
        {
            if (client == null) return;

            client.Connected -= HandleConnected;
            client.Disconnected -= HandleDisconnected;
            client.RawMessageReceived -= HandleRawMessageReceived;
            client.Authenticated -= HandleAuthenticated;
            client.AuthFailed -= HandleAuthFailed;
        }

        //* این تابع کلاینت را از همین آبجکت یا سینگلتون پیدا می کند و اگر نبود می سازد.
        private void EnsureClientReference()
        {
            if (client != null) return;

            client = GetComponent<DedicatedGameServerWsClient>();
            if (client != null) return;

            client = DedicatedGameServerWsClient.Instance;
            if (client != null) return;

            client = gameObject.AddComponent<DedicatedGameServerWsClient>();
        }

        //* این تابع از اینسپکتور فقط اتصال خام به ددیکیتد سرور را تست می کند.
        [ContextMenu("01 Connect Only")]
        public async void Btn_ConnectOnly()
        {
            await ConnectOnlyAsync();
        }

        //* این تابع از اینسپکتور فقط تیکت موجود در فیلدها را ارسال می کند.
        [ContextMenu("02 Authenticate With Inspector Ticket")]
        public async void Btn_AuthenticateWithInspectorTicket()
        {
            await AuthenticateWithInspectorTicketAsync();
        }

        //* این تابع از اینسپکتور اتصال و احراز با تیکت را پشت سر هم اجرا می کند.
        [ContextMenu("03 Connect And Authenticate")]
        public async void Btn_ConnectAndAuthenticate()
        {
            await ConnectAndAuthenticateAsync();
        }

        //* این تابع اتصال خام وب سوکت را تست می کند.
        public async Task<bool> ConnectOnlyAsync()
        {
            EnsureClientReference();

            if (client == null)
            {
                Debug.LogError("[DedicatedGameServerManualTicketTestController] Client is missing.");
                return false;
            }

            return await client.ConnectToDedicatedServerAsync(host, port, secureWebSocket);
        }

        //* این تابع تیکت وارد شده در اینسپکتور را برای احراز ارسال می کند.
        public async Task<bool> AuthenticateWithInspectorTicketAsync()
        {
            EnsureClientReference();

            if (client == null)
            {
                Debug.LogError("[DedicatedGameServerManualTicketTestController] Client is missing.");
                return false;
            }

            return await client.AuthenticateWithTicketAsync(
                ticketId,
                signature,
                userId,
                roomId,
                serverId,
                sessionId,
                playerId,
                userName);
        }

        //* این تابع ابتدا وصل می شود و بعد تیکت را ارسال می کند.
        public async Task<bool> ConnectAndAuthenticateAsync()
        {
            bool connected = await ConnectOnlyAsync();

            if (!connected)
            {
                Debug.LogError("[DedicatedGameServerManualTicketTestController] Connect failed.");
                return false;
            }

            bool authenticated = await AuthenticateWithInspectorTicketAsync();

            if (!authenticated)
            {
                Debug.LogError("[DedicatedGameServerManualTicketTestController] Auth failed.");
                return false;
            }

            Debug.Log("[DedicatedGameServerManualTicketTestController] Connect and auth ok.");
            return true;
        }

        //* این تابع لاگ اتصال موفق را چاپ می کند.
        private void HandleConnected()
        {
            if (!logEvents) return;
            Debug.Log("[DedicatedGameServerManualTicketTestController] Connected.");
        }

        //* این تابع لاگ قطع اتصال را چاپ می کند.
        private void HandleDisconnected(string reason)
        {
            if (!logEvents) return;
            Debug.Log("[DedicatedGameServerManualTicketTestController] Disconnected | reason=" + reason);
        }

        //* این تابع پیام خام دریافتی را چاپ می کند.
        private void HandleRawMessageReceived(string raw)
        {
            if (!logEvents) return;
            Debug.Log("[DedicatedGameServerManualTicketTestController] Raw received | " + raw);
        }

        //* این تابع لاگ احراز موفق را چاپ می کند.
        private void HandleAuthenticated()
        {
            if (!logEvents) return;
            Debug.Log("[DedicatedGameServerManualTicketTestController] Authenticated.");
        }

        //* این تابع لاگ احراز ناموفق را چاپ می کند.
        private void HandleAuthFailed(string reason)
        {
            if (!logEvents) return;
            Debug.LogWarning("[DedicatedGameServerManualTicketTestController] Auth failed | reason=" + reason);
        }

        /*
        توضیح مکتوب فایل:
        این کنترلر فقط برای تست دستی DS-7A است.
        ابتدا با DedicatedGameServerWsClient به ددیکیتد سرور وصل می شود.
        سپس تیکت تازه ای را که از نود جی اس گرفته اید، از فیلدهای اینسپکتور می خواند و auth_ticket می فرستد.
        این فایل هنوز خودش تیکت را از نود جی اس نمی گیرد؛ اتصال خودکار Ticket Client در مرحله بعد اضافه می شود.
        */
    }
}
