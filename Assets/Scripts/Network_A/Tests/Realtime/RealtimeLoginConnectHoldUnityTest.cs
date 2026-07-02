using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.Tests.Realtime
{
    public class RealtimeRawWebSocketHoldUnityTest : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string websocketUrl = "wss://dev-world-3d.metarang.com:443/ws";

        [Header("Timing")]
        [SerializeField] private int connectUiTimeoutMs = 10000;
        [SerializeField] private int holdMs = 45000;
        [SerializeField] private int tickMs = 1000;
        [SerializeField] private int receiveBufferSize = 8192;

        [Header("UI")]
        [SerializeField] private Button startButton;
        [SerializeField] private Button stopButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI logText;

        private ClientWebSocket socket;
        private CancellationTokenSource lifecycleCts;
        private Task receiveTask;
        private readonly StringBuilder logBuffer = new StringBuilder(12000);
        private bool isRunning;
        private bool authOkReceived;
        private string authMessageId;

        //* این تابع وضعیت اولیه تست خام وب سوکت را آماده می کند.
        private void Awake()
        {
            EnsureLifecycleToken();
            UpdateButtons();
            SetStatus("Ready");
            Log("Raw websocket hold test ready. url=" + websocketUrl);
        }

        //* این تابع دکمه های تست را در هر فریم با وضعیت تست هماهنگ نگه می دارد.
        private void Update()
        {
            UpdateButtons();
        }

        //* این تابع هنگام حذف آبجکت، اتصال خام وب سوکت را تمیز می بندد.
        private async void OnDestroy()
        {
            await CleanupAsync("Raw test destroyed");
        }

        //* این تابع از دکمه شروع تست صدا زده می شود.
        public async void StartTestButton()
        {
            if (isRunning) return;
            await RunTestAsync();
        }

        //* این تابع از دکمه توقف تست صدا زده می شود.
        public async void StopTestButton()
        {
            await CleanupAsync("Manual stop");
        }

        //* این تابع تست کامل اتصال خام، آث و نگه داشتن کانکشن را اجرا می کند.
        public async Task<bool> RunTestAsync()
        {
            if (isRunning) return false;

            isRunning = true;
            authOkReceived = false;
            UpdateButtons();

            try
            {
                EnsureLifecycleToken();

                string accessToken = SecureTokenStorage.GetAccessToken();
                if (string.IsNullOrWhiteSpace(accessToken)) return Fail("Stored access token is empty. Login first from normal Auth UI.");

                Log("Raw test started. holdMs=" + holdMs);

                bool connected = await ConnectRawWebSocketAsync();
                if (!connected) return Fail("Raw websocket connect failed.");

                receiveTask = ReceiveLoopAsync(lifecycleCts.Token);

                bool sentAuth = await SendRealtimeAuthAsync(accessToken);
                if (!sentAuth) return Fail("Realtime auth send failed.");

                bool authOk = await WaitForAuthOkAsync(15000, lifecycleCts.Token);
                if (!authOk) return Fail("Realtime auth_ok timeout.");

                bool held = await HoldRawSocketAsync();
                if (!held) return Fail("Raw hold failed.");

                SetStatus("PASSED");
                Log("Raw websocket hold test passed.");
                return true;
            }
            finally
            {
                isRunning = false;
                UpdateButtons();
            }
        }

        //* این تابع وب سوکت خام دات نت را بدون استفاده از ریل تایم کلاینت پروژه وصل می کند.
        private async Task<bool> ConnectRawWebSocketAsync()
        {
            socket?.Dispose();
            socket = new ClientWebSocket();

            Uri uri = new Uri(websocketUrl.Trim());
            Log("Raw connect started. uiTimeoutMs=" + connectUiTimeoutMs);

            Task connectTask = socket.ConnectAsync(uri, lifecycleCts.Token);

            if (connectUiTimeoutMs > 0)
            {
                Task timeoutTask = Task.Delay(Mathf.Max(1000, connectUiTimeoutMs), lifecycleCts.Token);
                Task completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask != connectTask)
                {
                    Log("Raw connect UI timeout. The lifetime token was not cancelled.");
                    return false;
                }
            }

            await connectTask;
            bool connected = socket.State == WebSocketState.Open;
            Log("Raw connect result: " + connected + " | state=" + socket.State);
            return connected;
        }

        //* این تابع پیام آث استاندارد ریل تایم را روی وب سوکت خام ارسال می کند.
        private async Task<bool> SendRealtimeAuthAsync(string accessToken)
        {
            if (socket == null || socket.State != WebSocketState.Open) return false;

            authMessageId = "raw_auth_" + Guid.NewGuid().ToString("D");
            string json = "{"
                          + "\"v\":1,"
                          + "\"ch\":\"system\","
                          + "\"t\":\"auth\","
                          + "\"id\":\"" + authMessageId + "\","
                          + "\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ","
                          + "\"room\":\"\","
                          + "\"payload\":{\"accessToken\":\"" + EscapeJson(accessToken) + "\"},"
                          + "\"requiresAck\":false,"
                          + "\"replyTo\":\"\""
                          + "}";

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, lifecycleCts.Token);
            Log("Raw auth sent. id=" + authMessageId + " | bytes=" + bytes.Length);
            return true;
        }

        //* این تابع تا دریافت پیام آث اوکی منتظر می ماند.
        private async Task<bool> WaitForAuthOkAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            int elapsedMs = 0;
            int stepMs = 100;

            while (!authOkReceived && elapsedMs < timeoutMs && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(stepMs, cancellationToken);
                elapsedMs += stepMs;
            }

            Log("Raw auth wait finished. authOk=" + authOkReceived + " | elapsedMs=" + elapsedMs);
            return authOkReceived;
        }

        //* این تابع اتصال خام را برای مدت مشخص نگه می دارد و اگر قطع شد گزارش می دهد.
        private async Task<bool> HoldRawSocketAsync()
        {
            int elapsedMs = 0;
            int safeTickMs = Mathf.Max(250, tickMs);

            Log("Raw hold started. holdMs=" + holdMs);

            while (elapsedMs < holdMs && !lifecycleCts.IsCancellationRequested)
            {
                await Task.Delay(safeTickMs, lifecycleCts.Token);
                elapsedMs += safeTickMs;

                if (socket == null || socket.State != WebSocketState.Open)
                {
                    Log("Raw hold failed. state=" + (socket == null ? "null" : socket.State.ToString()) + " | elapsedMs=" + elapsedMs);
                    return false;
                }

                if (elapsedMs % 5000 == 0 || elapsedMs >= holdMs)
                {
                    Log("Raw hold tick. elapsedMs=" + elapsedMs + " | state=" + socket.State);
                }
            }

            Log("Raw hold passed. elapsedMs=" + elapsedMs);
            return true;
        }

        //* این تابع همه پیام های دریافتی را از وب سوکت خام می خواند.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[Mathf.Max(1024, receiveBufferSize)];
            ArraySegment<byte> segment = new ArraySegment<byte>(buffer);

            while (!cancellationToken.IsCancellationRequested && socket != null && socket.State == WebSocketState.Open)
            {
                try
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(segment, cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log("Raw receive close. closeStatus=" + socket.CloseStatus + " | description=" + socket.CloseStatusDescription);
                        return;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Log("Raw message received. bytes=" + result.Count + " | text=" + Truncate(message, 700));

                    if (message.Contains("\"ch\":\"system\"") && message.Contains("\"t\":\"auth_ok\""))
                    {
                        authOkReceived = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log("Raw receive error: " + ex.GetType().Name + " | " + ex.Message + " | state=" + (socket == null ? "null" : socket.State.ToString()));
                    return;
                }
            }
        }

        //* این تابع اتصال خام وب سوکت را تمیز می بندد.
        private async Task CleanupAsync(string reason)
        {
            try
            {
                Log("Raw cleanup started. reason=" + reason);
                lifecycleCts?.Cancel();

                if (socket != null && socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log("Raw cleanup warning: " + ex.Message);
            }
            finally
            {
                socket?.Dispose();
                socket = null;

                lifecycleCts?.Dispose();
                lifecycleCts = null;
                isRunning = false;
                authOkReceived = false;

                EnsureLifecycleToken();
                UpdateButtons();
                Log("Raw cleanup completed.");
            }
        }

        //* این تابع توکن عمر تست را در صورت نیاز می سازد.
        private void EnsureLifecycleToken()
        {
            if (lifecycleCts != null && !lifecycleCts.IsCancellationRequested) return;
            lifecycleCts?.Dispose();
            lifecycleCts = new CancellationTokenSource();
        }

        //* این تابع دکمه ها را با وضعیت تست هماهنگ می کند.
        private void UpdateButtons()
        {
            if (startButton != null) startButton.interactable = !isRunning;
            if (stopButton != null) stopButton.interactable = isRunning || (socket != null && socket.State == WebSocketState.Open);
        }

        //* این تابع وضعیت کوتاه تست را روی یو آی می نویسد.
        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message;
        }

        //* این تابع لاگ را در کنسول و متن یو آی می نویسد.
        private void Log(string message)
        {
            string line = DateTime.UtcNow.ToString("HH:mm:ss.fff") + " | " + message;
            Debug.Log("[RealtimeRawWebSocketHoldUnityTest] " + line);

            logBuffer.AppendLine(line);
            if (logBuffer.Length > 12000) logBuffer.Remove(0, Mathf.Min(4000, logBuffer.Length));
            if (logText != null) logText.text = logBuffer.ToString();
        }

        //* این تابع تست را با خطا تمام می کند.
        private bool Fail(string message)
        {
            Log("FAILED: " + message);
            SetStatus("FAILED");
            return false;
        }

        //* این تابع متن را برای قرار گرفتن داخل جیسون امن می کند.
        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        //* این تابع متن بلند را برای لاگ کوتاه می کند.
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "...";
        }
    }
}
