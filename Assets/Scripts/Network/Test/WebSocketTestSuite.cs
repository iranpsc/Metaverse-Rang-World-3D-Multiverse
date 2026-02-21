using UnityEngine;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.WebSocket;

public class WebSocketTestSuite : MonoBehaviour
{
    private WebSocketClient webSocketClient;

    // Stage12/13/14 test helpers
    private CancellationTokenSource testCts;

    // 🔧 این URL را با سرور خودت جایگزین کن
    private const string TEST_URL = "wss://echo.websocket.events"; // نمونه عمومی (اگر خواستی عوض کن)

    void Start()
    {
        RunWebSocketTests();
    }

    public async void RunWebSocketTests()
    {
        Debug.Log("========================================");
        Debug.Log("شروع تست لایه WebSocket پایدار (Stage 12+13+14)...");
        Debug.Log("========================================");

        testCts = new CancellationTokenSource();

        // ساخت WebSocketClient
        webSocketClient = new WebSocketClient
        {
            AutoReconnect = true,
            EnableHeartbeat = true,
            EnableMessageQueue = true
        };

        // ✅ اگر WebGL هستیم: AuthGate لازم است
#if UNITY_WEBGL && !UNITY_EDITOR
        webSocketClient.RequireAuthGate = true;
#else
        webSocketClient.RequireAuthGate = false;
#endif

        // ثبت رویدادها
        webSocketClient.OnConnected += OnConnected;
        webSocketClient.OnDisconnected += OnDisconnected;
        webSocketClient.OnMessageReceived += OnMessageReceived;
        webSocketClient.OnError += OnError;
        webSocketClient.OnReconnected += OnReconnected;

        // (اختیاری) رویدادهای AuthGate اگر در WebSocketClient اضافه کرده‌ای
        webSocketClient.OnAuthOk += OnAuthOk;
        webSocketClient.OnAuthFailed += OnAuthFailed;

        await TestConnection();
        await TestSendMessage();
        await TestHeartbeat();
        await TestReconnect();
        await TestMessageQueue();

        Debug.Log("========================================");
        Debug.Log("تست لایه WebSocket کامل شد ✅");
        Debug.Log("========================================");
    }

    private void OnConnected()
    {
        Debug.Log("✅ WebSocket متصل شد");

        // اگر AuthGate روشن است، همینجا پیام auth را بفرست
        if (webSocketClient != null && webSocketClient.RequireAuthGate)
        {
            Debug.Log("🔐 AuthGate فعال است → ارسال پیام auth...");
            _ = webSocketClient.SendAsync(BuildAuthMessage());
        }
    }

    private void OnDisconnected()
    {
        Debug.Log("⚠️ WebSocket قطع شد");
    }

    private void OnMessageReceived(string message)
    {
        Debug.Log($"📨 پیام دریافت شد: {message}");
    }

    private void OnError(string error)
    {
        Debug.LogWarning($"❌ خطای WebSocket: {error}");
    }

    private void OnReconnected()
    {
        Debug.Log("🔄 WebSocket بازاتصال موفق");

        // بعد از reconnect اگر AuthGate فعال است، دوباره auth بفرست
        if (webSocketClient != null && webSocketClient.RequireAuthGate)
        {
            Debug.Log("🔐 Reconnect انجام شد → ارسال مجدد پیام auth...");
            _ = webSocketClient.SendAsync(BuildAuthMessage());
        }
    }

    // رویدادهای AuthGate (اگر وجود داشته باشند)
    private void OnAuthOk()
    {
        Debug.Log("✅ AUTH_OK دریافت شد → صف پیام‌ها می‌تواند flush شود");
        Debug.Log(webSocketClient != null ? webSocketClient.GetDebugInfo() : "No client");
    }

    private void OnAuthFailed(string raw)
    {
        Debug.LogWarning($"❌ AUTH_FAIL/UNAUTHORIZED دریافت شد: {raw}");
        Debug.Log(webSocketClient != null ? webSocketClient.GetDebugInfo() : "No client");
    }

    private async Task TestConnection()
    {
        Debug.Log("\n[تست ۱] اتصال به سرور WebSocket");

        // برای تست واقعی، ConnectAsync را صدا می‌زنیم
        bool ok = await webSocketClient.ConnectAsync(TEST_URL, headers: null, cancellationToken: testCts.Token);

        Debug.Log(ok
            ? $"✅ اتصال برقرار شد: {TEST_URL}"
            : $"❌ اتصال برقرار نشد: {TEST_URL}");

        Debug.Log(webSocketClient.GetDebugInfo());
    }

    private async Task TestSendMessage()
    {
        Debug.Log("\n[تست ۲] ارسال پیام");

        var message = new WebSocketMessage("test", new { data = "test" });
        string json = message.ToJson();
        Debug.Log($"پیام آماده: {json}");

        bool sent = await webSocketClient.SendAsync(json, testCts.Token);

        // اگر AuthGate روشن باشد و auth_ok نیامده باشد، sent معمولاً false می‌شود و پیام queue می‌شود
        Debug.Log(sent
            ? "✅ ارسال پیام انجام شد"
            : "⚠️ ارسال مستقیم انجام نشد (احتمالاً در صف قرار گرفت یا اتصال/گیت آماده نبود)");

        Debug.Log(webSocketClient.GetDebugInfo());
    }

    private async Task TestHeartbeat()
    {
        Debug.Log("\n[تست ۳] Heartbeat Monitor");

        Debug.Log($"Heartbeat فعال: {webSocketClient.EnableHeartbeat}");
        Debug.Log("✅ Heartbeat: پیکربندی شد");
        await Task.Yield();
    }

    private async Task TestReconnect()
    {
        Debug.Log("\n[تست ۴] Reconnect Manager");

        Debug.Log($"AutoReconnect فعال: {webSocketClient.AutoReconnect}");
        Debug.Log("✅ Reconnect: پیکربندی شد");
        await Task.Yield();
    }

    private async Task TestMessageQueue()
    {
        Debug.Log("\n[تست ۵] Message Queue");

        Debug.Log($"MessageQueue فعال: {webSocketClient.EnableMessageQueue}");
        Debug.Log("✅ MessageQueue: پیکربندی شد");
        await Task.Yield();
    }

    // نمونه پیام auth (تا وقتی سرور مشخص شود)
    private string BuildAuthMessage()
    {
        // ⚠️ شکل واقعی auth را سرور تعیین می‌کند
        // چند نمونه رایج:
        // {"type":"auth","token":"..."}
        // {"type":"login","accessToken":"..."}
        // {"action":"authenticate","jwt":"..."}
        return "{\"type\":\"auth\",\"token\":\"TEST_TOKEN\"}";
    }

    private void OnDestroy()
    {
        try { testCts?.Cancel(); } catch { }
        try { testCts?.Dispose(); } catch { }
        testCts = null;

        webSocketClient?.Dispose();
    }
}
