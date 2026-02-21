using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.Core.Interfaces;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    /// <summary>
    /// کلاینت اصلی WebSocket با قابلیت‌های پایداری
    /// این کلاس پیاده‌سازی اینترفیس IWebSocketClient است
    /// </summary>
    public class WebSocketClient : IWebSocketClient, IDisposable
    {
        private ClientWebSocket webSocket;//* System
        private CancellationTokenSource cancellationTokenSource;
        private readonly MessageQueue messageQueue;
        private readonly HeartbeatMonitor heartbeatMonitor;
        private readonly ReconnectManager reconnectManager;

        private WebSocketConnectionState currentState = WebSocketConnectionState.Disconnected;
        private string currentConnectionId = Guid.NewGuid().ToString("N");
        private Dictionary<string, string> currentHeaders;
        private string currentUrl;

        // =========================
        // Stage 14: Guards (اضافه شده - حذف نشده)
        // =========================
        private volatile bool isDisconnecting = false;
        private volatile bool isReconnecting = false;

        // =========================
        // Stage 12: Auth Gate (اضافه شده - حذف نشده)
        // =========================
        /// <summary>
        /// اگر true باشد: تا قبل از دریافت auth_ok، پیام‌های غیر-auth در صف می‌مانند و flush نمی‌شوند.
        /// (برای WebGL که header ندارد معمولاً true می‌شود)
        /// </summary>
        public bool RequireAuthGate { get; set; } = false;

        private volatile bool authOk = false;
        private volatile bool authFailed = false;

        /// <summary>
        /// اگر AuthGate خاموش باشد => همیشه true
        /// اگر روشن باشد => فقط بعد از auth_ok true می‌شود
        /// </summary>
        public bool IsAuthenticated => !RequireAuthGate || authOk;

        // رویدادها
        public event Action OnConnected;
        public event Action<string> OnMessageReceived;
        public event Action<string> OnError;
        public event Action OnDisconnected;
        public event Action OnReconnected;

        // Stage12: رویدادهای اختیاری auth (اضافه شده - حذف نشده)
        public event Action OnAuthOk;
        public event Action<string> OnAuthFailed;

        // تنظیمات
        public bool AutoReconnect { get; set; } = true;
        public bool EnableHeartbeat { get; set; } = true;
        public bool EnableMessageQueue { get; set; } = true;
        public int ReceiveBufferSize { get; set; } = 4096;

        // ✅ دقیق‌تر شده، حذف نشده
        public bool IsConnected =>
            currentState == WebSocketConnectionState.Connected &&
            webSocket != null &&
            webSocket.State == WebSocketState.Open;

        public string ConnectionId => currentConnectionId;

        public WebSocketClient()
        {
            messageQueue = new MessageQueue();
            heartbeatMonitor = new HeartbeatMonitor();
            reconnectManager = new ReconnectManager();
        }

        /// <summary>
        /// اتصال به سرور WebSocket
        /// </summary>
        public async Task<bool> ConnectAsync(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            if (IsConnected)//if currentState == WebSocketConnectionState.Connected
            {
                Debug.LogWarning("هم‌اکنون به WebSocket متصل هستید");
                return true;
            }

            currentUrl = url;
            currentHeaders = headers ?? new Dictionary<string, string>();
            currentState = WebSocketConnectionState.Connecting;

            // Stage12/14: اتصال جدید => Gate و Guards
            ResetAuthGate();
            isDisconnecting = false; // اگر قبلاً disconnect بوده، اتصال جدید آزاد است

            try
            {
                // Stage14: قبل از ساخت سوکت جدید، cleanup سبک
                SafeCloseSocketInternal(disposeOnly: true);

                // new WebSocket
                webSocket = new ClientWebSocket();
                webSocket.Options.SetBuffer(ReceiveBufferSize, ReceiveBufferSize);

                // اضافه کردن هدرها
                foreach (var header in currentHeaders)
                {
                    webSocket.Options.SetRequestHeader(header.Key, header.Value);
                }

                // ایجاد CancellationToken
                cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // اتصال به سرور
                await webSocket.ConnectAsync(new Uri(url), cancellationTokenSource.Token);

                currentState = WebSocketConnectionState.Connected;
                currentConnectionId = Guid.NewGuid().ToString("N");

                Debug.Log($"اتصال WebSocket موفق: {url}");

                // فعال‌سازی Heartbeat
                if (EnableHeartbeat)
                {
                    heartbeatMonitor.StartMonitoring(
                        SendPingAsync,
                        OnConnectionLost,
                        OnConnectionRecovered
                    );
                }

                // شروع دریافت پیام‌ها
                _ = ReceiveMessagesAsync(cancellationTokenSource.Token);

                OnConnected?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                currentState = WebSocketConnectionState.Disconnected;
                string errorMsg = $"اتصال WebSocket شکست خورد: {ex.Message}";
                Debug.LogError(errorMsg);
                OnError?.Invoke(errorMsg);

                // Stage14: در صورت fail، cleanup
                SafeCloseSocketInternal(disposeOnly: true);

                return false;
            }
        }

        /// <summary>
        /// ارسال پیام به سرور
        /// </summary>
        public async Task<bool> SendAsync(string message, CancellationToken cancellationToken = default)
        {
            // if have message to send and not Connect -> Send To Queue
            if (!IsConnected || webSocket == null)
            {
                if (EnableMessageQueue)
                {
                    // ذخیره در صف برای ارسال بعدی
                    var wsMessage = WebSocketMessage.FromJson(message);
                    if (wsMessage != null)
                    {
                        messageQueue.Enqueue(wsMessage, false);
                        Debug.LogWarning("پیام در صف ذخیره شد (اتصال فعال نیست)");
                    }
                }
                return false;
            }

            // Stage12: وصل هستیم ولی auth_ok نداریم => فقط پیام auth را بفرست، بقیه را صف کن
            if (RequireAuthGate && !authOk)
            {
                var wsMessage = WebSocketMessage.FromJson(message);
                if (wsMessage != null)
                {
                    if (!IsAuthMessage(wsMessage))
                    {
                        if (EnableMessageQueue)
                        {
                            messageQueue.Enqueue(wsMessage, false);
                            Debug.LogWarning("پیام در صف ذخیره شد (auth_ok هنوز نیامده)");
                        }
                        return false;
                    }
                    // اگر پیام auth است => اجازه ارسال مستقیم
                }
                else
                {
                    // اگر پیام parse نشد، برای امنیت در حالت gate آن را نفرست
                    Debug.LogWarning("پیام قابل parse نیست؛ در حالت AuthGate ارسال مستقیم انجام نشد");
                    return false;
                }
            }

            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(message);
                var arraySegment = new ArraySegment<byte>(buffer);

                await webSocket.SendAsync(
                    arraySegment,
                    WebSocketMessageType.Text,
                    true,
                    cancellationTokenSource.Token
                );

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"ارسال پیام WebSocket شکست خورد: {ex.Message}");
                OnError?.Invoke(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// ارسال پیام با تأیید دریافت (Acknowledgment)
        /// </summary>
        public async Task<bool> SendWithAckAsync(string message, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var wsMessage = WebSocketMessage.FromJson(message);
            if (wsMessage == null)
            {
                Debug.LogError("پیام نامعتبر برای ارسال با ACK");
                return false;
            }

            wsMessage.requiresAck = true;

            // ارسال پیام
            bool sent = await SendAsync(wsMessage.ToJson(), cancellationToken);
            if (!sent)
                return false;

            Debug.LogWarning("SendWithAckAsync: ACK واقعی هنوز کانفیگ نشده (سرور نامشخص است). فعلاً فقط SendAsync انجام شد.");

            // TODO: پیاده‌سازی سیستم تأیید دریافت
            // در نسخه‌های آینده اضافه خواهد شد

            return true;
        }

        /// <summary>
        /// قطع اتصال از سرور
        /// </summary>
        public void Disconnect(WebSocketCloseCode closeCode = WebSocketCloseCode.Normal, string reason = "Client disconnect")
        {
            if (!IsConnected || webSocket == null)
                return;

            // Stage14: guard
            if (isDisconnecting)
                return;

            isDisconnecting = true;

            try
            {
                currentState = WebSocketConnectionState.Closing;

                // Stage12: قطع اتصال => gate reset
                ResetAuthGate();

                // توقف Heartbeat
                heartbeatMonitor.StopMonitoring();

                // توقف Reconnect
                reconnectManager.StopReconnect();
                isReconnecting = false;

                // بستن WebSocket
                // ⚠️ قبلاً .Wait() داشتی که می‌تونه deadlock بده.
                // حذفش نکردیم؛ فقط امنش کردیم با timeout.
                var closeStatus = MapCloseCode(closeCode);

                try
                {
                    var closeTask = webSocket.CloseAsync(closeStatus, reason, CancellationToken.None);

                    // Wait با timeout کوتاه (اگر گیر کرد، پایین Dispose می‌کنیم)
                    bool finished = closeTask.Wait(1000);
                    if (!finished)
                    {
                        Debug.LogWarning("CloseAsync timeout; disposing socket to avoid deadlock.");
                    }
                }
                catch (Exception exClose)
                {
                    Debug.LogWarning($"CloseAsync failed: {exClose.Message}");
                }

                currentState = WebSocketConnectionState.Disconnected;

                // Stage14: cleanup واحد
                SafeCloseSocketInternal(disposeOnly: true);

                Debug.Log($"اتصال WebSocket بسته شد: {reason}");

                OnDisconnected?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در بستن WebSocket: {ex.Message}");
            }
            finally
            {
                isDisconnecting = false;
            }
        }

        /// <summary>
        /// دریافت وضعیت فعلی اتصال
        /// </summary>
        public WebSocketConnectionState GetConnectionState()
        {
            return currentState;
        }

        /// <summary>
        /// حلقه دریافت پیام‌ها
        /// </summary>
        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[ReceiveBufferSize];

            // یک بافر جمع‌کننده برای پیام‌های چند تکه
            using var ms = new System.IO.MemoryStream();

            while (IsConnected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ms.SetLength(0); // شروع یک پیام جدید

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken
                        );

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Debug.LogWarning("سرور WebSocket را بست");
                            OnConnectionLost();
                            return;
                        }

                        // تکه‌ی فعلی را به MemoryStream اضافه کن
                        ms.Write(buffer, 0, result.Count);

                    } while (!result.EndOfMessage); // تا آخر پیام ادامه بده

                    // اگر پیام Text است: تبدیل به string
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(ms.ToArray());
                        ProcessMessage(message);
                    }
                    else
                    {
                        // فعلاً Binary را ignore می‌کنیم (اگر لازم بود جداگانه هندل می‌کنیم)
                        Debug.LogWarning("پیام باینری دریافت شد (فعلاً هندل نمی‌شود)");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"خطا در دریافت پیام WebSocket: {ex.Message}");
                    OnError?.Invoke(ex.Message);

                    // بهتر است اینجا هم اتصال را lost حساب کنیم
                    OnConnectionLost();
                    break;
                }
            }
        }

        /// <summary>
        /// پردازش پیام دریافتی
        /// </summary>
        private void ProcessMessage(string message)
        {
            try
            {
                // =========================
                // Stage 13: System message parsing (یک نقطه واحد)
                // =========================
                if (SystemMessageParser.TryParse(message, out var sysType, out var payload))
                {
                    // pong
                    if (sysType == SystemMessageType.Pong)
                    {
                        heartbeatMonitor.OnPongReceived();
                        return;
                    }

                    // auth messages => gate logic
                    if (sysType == SystemMessageType.AuthOk ||
                        sysType == SystemMessageType.AuthFail ||
                        sysType == SystemMessageType.Unauthorized)
                    {
                        if (TryHandleAuthMessages(message))
                            return;

                        // اگر به هر دلیل هندل نشد، باز هم سیستمی است
                        return;
                    }

                    // ack messages: فعلاً consume (تا وقتی ACK manager دقیق وصل شود)
                    if (sysType == SystemMessageType.Ack)
                    {
                        Debug.Log($"✅ ACK received. id={payload ?? "(null)"} raw={message}");
                        return; // consume
                    }

                    // server_error: می‌تونی مستقیم OnError کنی یا بگذاری به OnMessageReceived برسد
                    if (sysType == SystemMessageType.ServerError)
                    {
                        // گزینه ۱:
                        // OnError?.Invoke(payload ?? message);
                        // return;

                        // فعلاً بگذار ادامه بدهد تا مصرف‌کننده بالادست هم ببیند
                    }
                }

                // پیام اپلیکیشن (غیر سیستمی)
                OnMessageReceived?.Invoke(message);

                // =========================
                // Stage 12: Flush Queue only when authenticated
                // =========================
                if (EnableMessageQueue && IsAuthenticated && messageQueue.GetQueueCount() > 0)
                {
                    _ = SendQueuedMessagesAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در پردازش پیام WebSocket: {ex.Message}");
            }
        }

        // =========================
        // Stage12/13: helpers (اضافه شده - حذف نشده)
        // =========================
        private bool TryHandleAuthMessages(string raw)
        {
            // استفاده از parser واحد (Stage13)
            if (!SystemMessageParser.TryParse(raw, out var sysType, out var payload))
                return false;

            if (sysType == SystemMessageType.AuthOk)
            {
                authOk = true;
                authFailed = false;

                OnAuthOk?.Invoke();

                // حالا می‌توانیم صف را flush کنیم
                if (EnableMessageQueue && messageQueue.GetQueueCount() > 0)
                {
                    _ = SendQueuedMessagesAsync();
                }
                return true;
            }

            if (sysType == SystemMessageType.AuthFail || sysType == SystemMessageType.Unauthorized)
            {
                authOk = false;
                authFailed = true;

                OnAuthFailed?.Invoke(payload ?? raw);

                // Stage14: auth_fail => reconnect را متوقف کن (تا توکن درست شود)
                reconnectManager.StopReconnect();
                isReconnecting = false;

                // flush ممنوع
                return true;
            }

            return false;
        }

        private void ResetAuthGate()
        {
            authOk = false;
            authFailed = false;
        }

        private bool IsAuthMessage(WebSocketMessage msg)
        {
            if (msg == null) return false;

            // چون نام پیام auth در سرور نامشخص است، چند نام رایج
            return msg.type == "auth" ||
                   msg.type == "authenticate" ||
                   msg.type == "login" ||
                   msg.type == "auth_request";
        }

        /// <summary>
        /// ارسال پیام‌های صف‌شده
        /// </summary>
        private async Task SendQueuedMessagesAsync()
        {
            // Stage12: اگر gate فعال است و هنوز auth_ok نداریم، اصلاً flush نکن
            if (!IsAuthenticated)
                return;

            while (IsConnected && messageQueue.GetQueueCount() > 0)//تا زمانی که صف پیام دارد!
            {
                // Stage14: اگر وسط کار gate بسته شد (race)
                if (!IsAuthenticated)
                    break;

                var message = messageQueue.DequeueNext();
                if (message != null)
                {
                    bool sent = await SendAsync(message.ToJson());
                    if (!sent)
                    {
                        messageQueue.Requeue(message);
                        break; // تلاش بعدی در فرصت دیگر
                    }
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// ارسال ping برای Heartbeat
        /// </summary>
        private async Task<bool> SendPingAsync()
        {
            try
            {
                var pingMessage = new WebSocketMessage("ping", null);
                return await SendAsync(pingMessage.ToJson());
            }
            catch (Exception ex)
            {
                Debug.LogError($"ارسال ping شکست خورد: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// رویداد قطعی اتصال
        /// </summary>
        private void OnConnectionLost()
        {
            // Stage14: guard against multiple lost signals
            if (currentState == WebSocketConnectionState.Disconnected)
                return;

            currentState = WebSocketConnectionState.Disconnected;

            // Stage12: قطع اتصال => gate reset
            ResetAuthGate();

            // Stage14: جلوگیری از ping همزمان در حین reconnect
            heartbeatMonitor.StopMonitoring();

            // Stage14: cleanup سبک (سوکت قبلی را آزاد کن)
            SafeCloseSocketInternal(disposeOnly: true);

            if (AutoReconnect && reconnectManager.CanContinueReconnecting())
            {
                // Stage14: guard reconnect
                if (isReconnecting)
                    return;

                isReconnecting = true;

                Debug.LogWarning("اتصال WebSocket قطع شد - شروع بازاتصال...");

                reconnectManager.StartReconnect(
                    currentHeaders,
                    async (headers, token) =>
                    {
                        // قبل از تلاش اتصال
                        ResetAuthGate();
                        SafeCloseSocketInternal(disposeOnly: true);

                        bool ok = await ConnectAsync(currentUrl, headers, token);

                        // نکته: بعد از reconnect موفق، هنوز authOk نداریم تا auth_ok برسد.
                        return ok;
                    },
                    OnReconnectSuccess,
                    OnReconnectFailed
                );
            }
            else
            {
                OnDisconnected?.Invoke();
            }
        }

        /// <summary>
        /// رویداد بازیابی اتصال
        /// </summary>
        private void OnConnectionRecovered()
        {
            Debug.Log("اتصال WebSocket بازیابی شد");
            // نکته: بازیابی heartbeat به معنی auth_ok نیست
        }

        /// <summary>
        /// رویداد موفقیت‌آمیز بودن بازاتصال
        /// </summary>
        private void OnReconnectSuccess()
        {
            Debug.Log("بازاتصال WebSocket موفقیت‌آمیز بود");
            isReconnecting = false;
            OnReconnected?.Invoke();

            // Stage12: بعد از reconnect هم auth باید دوباره ok شود.
            // پس flush صف فقط وقتی انجام می‌شود که auth_ok برسد.
            if (EnableMessageQueue && IsAuthenticated && messageQueue.GetQueueCount() > 0)
            {
                _ = SendQueuedMessagesAsync();
            }
        }

        /// <summary>
        /// رویداد شکست بازاتصال
        /// </summary>
        private void OnReconnectFailed()
        {
            Debug.LogError("بازاتصال WebSocket شکست خورد");
            isReconnecting = false;
            OnDisconnected?.Invoke();
        }

        /// <summary>
        /// تبدیل WebSocketCloseCode به WebSocketCloseStatus
        /// </summary>
        private WebSocketCloseStatus MapCloseCode(WebSocketCloseCode code)
        {
            switch (code)
            {
                case WebSocketCloseCode.Normal: return WebSocketCloseStatus.NormalClosure;
                case WebSocketCloseCode.GoingAway: return WebSocketCloseStatus.EndpointUnavailable;
                case WebSocketCloseCode.ProtocolError: return WebSocketCloseStatus.ProtocolError;
                //   case WebSocketCloseCode.UnsupportedData: return WebSocketCloseStatus.UnsupportedData;
                case WebSocketCloseCode.InvalidPayloadData: return WebSocketCloseStatus.InvalidPayloadData;
                case WebSocketCloseCode.PolicyViolation: return WebSocketCloseStatus.PolicyViolation;
                case WebSocketCloseCode.MessageTooBig: return WebSocketCloseStatus.MessageTooBig;
                case WebSocketCloseCode.InternalServerError: return WebSocketCloseStatus.InternalServerError;
                default: return WebSocketCloseStatus.NormalClosure;
            }
        }

        /// <summary>
        /// دریافت اطلاعات برای دیباگ
        /// </summary>
        public string GetDebugInfo()
        {
            return $"State: {currentState}, " +
                   $"Connected: {IsConnected}, " +
                   $"ConnectionId: {currentConnectionId}, " +
                   $"AuthGate: {(RequireAuthGate ? (authOk ? "auth_ok" : (authFailed ? "auth_fail" : "pending")) : "disabled")}, " +
                   $"Heartbeat: {heartbeatMonitor.GetStatus()}, " +
                   $"Reconnect: {reconnectManager.GetStatus()}, " +
                   $"Queue: {messageQueue.GetQueueInfo()}, " +
                   $"Guards: disconnecting={isDisconnecting}, reconnecting={isReconnecting}";
        }

        // =========================
        // Stage14: Cleanup واحد (اضافه شده - حذف نشده)
        // =========================
        private void SafeCloseSocketInternal(bool disposeOnly)
        {
            try
            {
                if (!disposeOnly)
                {
                    try { cancellationTokenSource?.Cancel(); } catch { /* ignore */ }
                }
                else
                {
                    // حتی در disposeOnly هم cancel می‌کنیم چون receive loop ممکن است گیر کند
                    try { cancellationTokenSource?.Cancel(); } catch { /* ignore */ }
                }

                try { cancellationTokenSource?.Dispose(); } catch { /* ignore */ }
                cancellationTokenSource = null;

                try { webSocket?.Dispose(); } catch { /* ignore */ }
                webSocket = null;
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// پاک‌سازی منابع
        /// </summary>
        public void Dispose()
        {
            // Stage14: Dispose باید safe باشد و چندبار هم صدا بخورد مشکلی نداشته باشد
            try
            {
                Disconnect(WebSocketCloseCode.Normal, "Client disposed");
            }
            catch { /* ignore */ }

            try { heartbeatMonitor.StopMonitoring(); } catch { /* ignore */ }
            try { reconnectManager.StopReconnect(); } catch { /* ignore */ }

            try
            {
                cancellationTokenSource?.Cancel();
                cancellationTokenSource?.Dispose();
                cancellationTokenSource = null;
            }
            catch { /* ignore */ }

            try { webSocket?.Dispose(); } catch { /* ignore */ }
            webSocket = null;
        }

        ~WebSocketClient()
        {
            Dispose();
        }
    }
}



/* using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assets.Network.Core.Interfaces;
using UnityEngine;

namespace Assets.Network.WebSocket
{
    /// <summary>
    /// کلاینت اصلی WebSocket با قابلیت‌های پایداری
    /// این کلاس پیاده‌سازی اینترفیس IWebSocketClient است
    /// </summary>
    public class WebSocketClient : IWebSocketClient, IDisposable
    {
        private ClientWebSocket webSocket;//* System
        private CancellationTokenSource cancellationTokenSource;
        private readonly MessageQueue messageQueue;
        private readonly HeartbeatMonitor heartbeatMonitor;
        private readonly ReconnectManager reconnectManager;

        private WebSocketConnectionState currentState = WebSocketConnectionState.Disconnected;
        private string currentConnectionId = Guid.NewGuid().ToString("N");
        private Dictionary<string, string> currentHeaders;
        private string currentUrl;

        // رویدادها
        public event Action OnConnected;
        public event Action<string> OnMessageReceived;
        public event Action<string> OnError;
        public event Action OnDisconnected;
        public event Action OnReconnected;

        // تنظیمات
        public bool AutoReconnect { get; set; } = true;
        public bool EnableHeartbeat { get; set; } = true;
        public bool EnableMessageQueue { get; set; } = true;
        public int ReceiveBufferSize { get; set; } = 4096;

        public bool IsConnected => currentState == WebSocketConnectionState.Connected;
        public string ConnectionId => currentConnectionId;

        public WebSocketClient()
        {
            messageQueue = new MessageQueue();
            heartbeatMonitor = new HeartbeatMonitor();
            reconnectManager = new ReconnectManager();
        }

        /// <summary>
        /// اتصال به سرور WebSocket
        /// </summary>
        public async Task<bool> ConnectAsync(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            if (IsConnected)//if currentState == WebSocketConnectionState.Connected
            {
                Debug.LogWarning("هم‌اکنون به WebSocket متصل هستید");
                return true;
            }

            currentUrl = url;
            currentHeaders = headers ?? new Dictionary<string, string>();
            currentState = WebSocketConnectionState.Connecting;

            try
            {
                // new WebSocket
                webSocket = new ClientWebSocket();
                webSocket.Options.SetBuffer(ReceiveBufferSize, ReceiveBufferSize);

                // اضافه کردن هدرها
                foreach (var header in currentHeaders)
                {
                    webSocket.Options.SetRequestHeader(header.Key, header.Value);
                }

                // ایجاد CancellationToken
                cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // اتصال به سرور
                await webSocket.ConnectAsync(new Uri(url), cancellationTokenSource.Token);

                currentState = WebSocketConnectionState.Connected;
                currentConnectionId = Guid.NewGuid().ToString("N");

                Debug.Log($"اتصال WebSocket موفق: {url}");

                // فعال‌سازی Heartbeat
                if (EnableHeartbeat)
                {
                    heartbeatMonitor.StartMonitoring(
                        SendPingAsync,
                        OnConnectionLost,
                        OnConnectionRecovered
                    );
                }

                // شروع دریافت پیام‌ها
                _ = ReceiveMessagesAsync(cancellationTokenSource.Token);

                OnConnected?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                currentState = WebSocketConnectionState.Disconnected;
                string errorMsg = $"اتصال WebSocket شکست خورد: {ex.Message}";
                Debug.LogError(errorMsg);
                OnError?.Invoke(errorMsg);
                return false;
            }
        }

        /// <summary>
        /// ارسال پیام به سرور
        /// </summary>
        public async Task<bool> SendAsync(string message, CancellationToken cancellationToken = default)
        {
            //if have message to send and not Connect -> Send To Queue
            if (!IsConnected || webSocket == null)
            {
                if (EnableMessageQueue)
                {
                    // ذخیره در صف برای ارسال بعدی
                    var wsMessage = WebSocketMessage.FromJson(message);
                    if (wsMessage != null)
                    {
                        messageQueue.Enqueue(wsMessage, false);
                        Debug.LogWarning("پیام در صف ذخیره شد (اتصال فعال نیست)");
                    }
                }
                return false;
            }

            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(message);
                var arraySegment = new ArraySegment<byte>(buffer);

                await webSocket.SendAsync(
                    arraySegment,
                    WebSocketMessageType.Text,
                    true,
                    cancellationTokenSource.Token
                );

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"ارسال پیام WebSocket شکست خورد: {ex.Message}");
                OnError?.Invoke(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// ارسال پیام با تأیید دریافت (Acknowledgment)
        /// </summary>
        public async Task<bool> SendWithAckAsync(string message, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var wsMessage = WebSocketMessage.FromJson(message);
            if (wsMessage == null)
            {
                Debug.LogError("پیام نامعتبر برای ارسال با ACK");
                return false;
            }

            wsMessage.requiresAck = true;

            // ارسال پیام
            bool sent = await SendAsync(wsMessage.ToJson(), cancellationToken);
            if (!sent)
                return false;

            // TODO: پیاده‌سازی سیستم تأیید دریافت
            // در نسخه‌های آینده اضافه خواهد شد

            return true;
        }

        /// <summary>
        /// قطع اتصال از سرور
        /// </summary>
        public void Disconnect(WebSocketCloseCode closeCode = WebSocketCloseCode.Normal, string reason = "Client disconnect")
        {
            if (!IsConnected || webSocket == null)
                return;

            try
            {
                currentState = WebSocketConnectionState.Closing;

                // توقف Heartbeat
                heartbeatMonitor.StopMonitoring();

                // توقف Reconnect
                reconnectManager.StopReconnect();

                // بستن WebSocket
                var closeStatus = MapCloseCode(closeCode);
                webSocket.CloseAsync(closeStatus, reason, CancellationToken.None).Wait();

                currentState = WebSocketConnectionState.Disconnected;
                webSocket?.Dispose();
                webSocket = null;

                Debug.Log($"اتصال WebSocket بسته شد: {reason}");

                OnDisconnected?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در بستن WebSocket: {ex.Message}");
            }
        }

        /// <summary>
        /// دریافت وضعیت فعلی اتصال
        /// </summary>
        public WebSocketConnectionState GetConnectionState()
        {
            return currentState;
        }

        /// <summary>
        /// حلقه دریافت پیام‌ها
        /// </summary>
        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[ReceiveBufferSize];

            // یک بافر جمع‌کننده برای پیام‌های چند تکه
            using var ms = new System.IO.MemoryStream();

            while (IsConnected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ms.SetLength(0); // شروع یک پیام جدید

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            cancellationToken
                        );

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Debug.LogWarning("سرور WebSocket را بست");
                            OnConnectionLost();
                            return;
                        }

                        // تکه‌ی فعلی را به MemoryStream اضافه کن
                        ms.Write(buffer, 0, result.Count);

                    } while (!result.EndOfMessage); // تا آخر پیام ادامه بده

                    // اگر پیام Text است: تبدیل به string
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(ms.ToArray());
                        ProcessMessage(message);
                    }
                    else
                    {
                        // فعلاً Binary را ignore می‌کنیم (اگر لازم بود جداگانه هندل می‌کنیم)
                        Debug.LogWarning("پیام باینری دریافت شد (فعلاً هندل نمی‌شود)");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"خطا در دریافت پیام WebSocket: {ex.Message}");
                    OnError?.Invoke(ex.Message);

                    // بهتر است اینجا هم اتصال را lost حساب کنیم
                    OnConnectionLost();
                    break;
                }
            }
        }

        /// <summary>
        /// پردازش پیام دریافتی
        /// </summary>
        private void ProcessMessage(string message)
        {
            try
            {
                // بررسی پیام‌های سیستمی
                if (message == "pong" || message.Contains("\"type\":\"pong\""))
                {
                    heartbeatMonitor.OnPongReceived();
                    return;
                }

                // ارسال به رویداد
                OnMessageReceived?.Invoke(message);

                // ارسال پیام‌های صف‌شده (اگر اتصال بازیابی شده)
                if (EnableMessageQueue && messageQueue.GetQueueCount() > 0)
                {
                    _ = SendQueuedMessagesAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در پردازش پیام WebSocket: {ex.Message}");
            }
        }

        /// <summary>
        /// ارسال پیام‌های صف‌شده
        /// </summary>
        private async Task SendQueuedMessagesAsync()
        {
            while (IsConnected && messageQueue.GetQueueCount() > 0)//تا زمانی که صف پیام دارد!
            {
                var message = messageQueue.DequeueNext();
                if (message != null)
                {
                    // پیام را به متن(JSON) تبدیل کن
                    //بفرست به سرور
                    //منتظر بمان ببین موفق شد یا نه
                    bool sent = await SendAsync(message.ToJson());
                    if (!sent)
                    {

                        messageQueue.Requeue(message);
                        break; // تلاش بعدی در فرصت دیگر
                    }
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// ارسال ping برای Heartbeat
        /// </summary>
        private async Task<bool> SendPingAsync()
        {
            try
            {
                var pingMessage = new WebSocketMessage("ping", null);
                return await SendAsync(pingMessage.ToJson());
            }
            catch (Exception ex)
            {
                Debug.LogError($"ارسال ping شکست خورد: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// رویداد قطعی اتصال
        /// </summary>
        private void OnConnectionLost()
        {
            currentState = WebSocketConnectionState.Disconnected;

            if (AutoReconnect && reconnectManager.CanContinueReconnecting())
            {
                Debug.LogWarning("اتصال WebSocket قطع شد - شروع بازاتصال...");

                reconnectManager.StartReconnect(
                    currentHeaders,
                    async (headers, token) => await ConnectAsync(currentUrl, headers, token),
                    OnReconnectSuccess,
                    OnReconnectFailed
                );
            }
            else
            {
                OnDisconnected?.Invoke();
            }
        }

        /// <summary>
        /// رویداد بازیابی اتصال
        /// </summary>
        private void OnConnectionRecovered()
        {
            Debug.Log("اتصال WebSocket بازیابی شد");
        }

        /// <summary>
        /// رویداد موفقیت‌آمیز بودن بازاتصال
        /// </summary>
        private void OnReconnectSuccess()
        {
            Debug.Log("بازاتصال WebSocket موفقیت‌آمیز بود");
            OnReconnected?.Invoke();

            // ارسال پیام‌های صف‌شده
            if (EnableMessageQueue && messageQueue.GetQueueCount() > 0)
            {
                _ = SendQueuedMessagesAsync();
            }
        }

        /// <summary>
        /// رویداد شکست بازاتصال
        /// </summary>
        private void OnReconnectFailed()
        {
            Debug.LogError("بازاتصال WebSocket شکست خورد");
            OnDisconnected?.Invoke();
        }

        /// <summary>
        /// تبدیل WebSocketCloseCode به WebSocketCloseStatus
        /// </summary>
        private WebSocketCloseStatus MapCloseCode(WebSocketCloseCode code)
        {
            switch (code)
            {
                case WebSocketCloseCode.Normal: return WebSocketCloseStatus.NormalClosure;
                case WebSocketCloseCode.GoingAway: return WebSocketCloseStatus.EndpointUnavailable;
                case WebSocketCloseCode.ProtocolError: return WebSocketCloseStatus.ProtocolError;
                //   case WebSocketCloseCode.UnsupportedData: return WebSocketCloseStatus.UnsupportedData;
                case WebSocketCloseCode.InvalidPayloadData: return WebSocketCloseStatus.InvalidPayloadData;
                case WebSocketCloseCode.PolicyViolation: return WebSocketCloseStatus.PolicyViolation;
                case WebSocketCloseCode.MessageTooBig: return WebSocketCloseStatus.MessageTooBig;
                case WebSocketCloseCode.InternalServerError: return WebSocketCloseStatus.InternalServerError;
                default: return WebSocketCloseStatus.NormalClosure;
            }
        }

        /// <summary>
        /// دریافت اطلاعات برای دیباگ
        /// </summary>
        public string GetDebugInfo()
        {
            return $"State: {currentState}, " +
                   $"Connected: {IsConnected}, " +
                   $"ConnectionId: {currentConnectionId}, " +
                   $"Heartbeat: {heartbeatMonitor.GetStatus()}, " +
                   $"Reconnect: {reconnectManager.GetStatus()}, " +
                   $"Queue: {messageQueue.GetQueueInfo()}";
        }

        /// <summary>
        /// پاک‌سازی منابع
        /// </summary>
        public void Dispose()
        {
            Disconnect(WebSocketCloseCode.Normal, "Client disposed");
            heartbeatMonitor.StopMonitoring();
            reconnectManager.StopReconnect();
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();
        }

        ~WebSocketClient()
        {
            Dispose();
        }
    }
} */