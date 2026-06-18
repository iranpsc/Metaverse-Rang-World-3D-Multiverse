using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.WebSockets;
#endif

namespace Network_A.Realtime.Transport
{
    //* ترنسپورت وب‌سوکت ریل‌تایم است و فقط پیام خام را بین کُر و سرور جابه‌جا می‌کند.
    public class WebSocketRealtimeTransport : IRealtimeTransport, IDisposable
    {
        private const int ReceiveBufferSize = 8192;
        private const int GracefulCloseTimeoutMs = 2000;

#if !UNITY_WEBGL || UNITY_EDITOR
        private ClientWebSocket clientWebSocket;
#endif

        private CancellationTokenSource connectionCts;
        private RealtimeTransportState state = RealtimeTransportState.Disconnected;
        private bool isDisconnecting;

        public event Action Connected;
        public event Action<string> MessageReceived;
        public event Action<string> ErrorReceived;
        public event Action<string> Disconnected;

        public RealtimeTransportKind Kind => RealtimeTransportKind.WebSocket;
        public RealtimeTransportState State => state;
        public bool IsConnected => state == RealtimeTransportState.Connected && IsSocketOpen();

        //* ترنسپورت وب‌سوکت نیتیو را در کارخانه ریل‌تایم ثبت می‌کند و در WebGL واقعی ثبت را به آداپتر مرورگر می‌سپارد.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterTransportOnLoad()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return;
#else
            RealtimeTransportFactory.RegisterTransport(RealtimeTransportKind.WebSocket, () => new WebSocketRealtimeTransport());
#endif
        }

        //* اتصال وب‌سوکت را با آدرس و هدرهای داده‌شده شروع می‌کند.
        public async Task<bool> ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        {
            if (IsConnected) return true;
            if (string.IsNullOrWhiteSpace(url)) return FailConnect("WebSocket url is empty.");

#if UNITY_WEBGL && !UNITY_EDITOR
            return FailConnect("WebSocketRealtimeTransport needs WebGL adapter in WebGL build.");
#else
            try
            {
                await CleanupSocketAsync("Reconnect cleanup", CancellationToken.None);
                SetState(RealtimeTransportState.Connecting);
                isDisconnecting = false;
                connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                clientWebSocket = new ClientWebSocket();
                ApplyHeaders(headers);

                await clientWebSocket.ConnectAsync(new Uri(url), connectionCts.Token);
                SetState(RealtimeTransportState.Connected);
                Connected?.Invoke();
                _ = ReceiveLoopAsync(connectionCts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return FailConnect("WebSocket connect canceled.");
            }
            catch (Exception ex)
            {
                return FailConnect("WebSocket connect failed: " + ex.Message);
            }
#endif
        }

        //* پیام خام آماده‌شده توسط کُر را بدون تغییر به سرور می‌فرستد.
        public async Task<bool> SendAsync(string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(message)) return false;

#if UNITY_WEBGL && !UNITY_EDITOR
            ErrorReceived?.Invoke("WebSocket send needs WebGL adapter in WebGL build.");
            return false;
#else
            if (!IsConnected || clientWebSocket == null) return false;

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                using (var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCts.Token))
                {
                    await clientWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, sendCts.Token);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                ErrorReceived?.Invoke("WebSocket send canceled.");
                return false;
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke("WebSocket send failed: " + ex.Message);
                return false;
            }
#endif
        }

        //* اتصال وب‌سوکت را با دلیل مشخص از سمت کلاینت می‌بندد.
        public async Task DisconnectAsync(string reason = "Client disconnect", CancellationToken cancellationToken = default)
        {
            if (isDisconnecting) return;
            isDisconnecting = true;
            SetState(RealtimeTransportState.Disconnecting);
            await CleanupSocketAsync(reason, cancellationToken);
            SetState(RealtimeTransportState.Disconnected);
            Disconnected?.Invoke(reason ?? "Client disconnect");
            isDisconnecting = false;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        //* حلقه دریافت پیام است و فریم‌های چندبخشی وب‌سوکت را به یک پیام کامل تبدیل می‌کند.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[ReceiveBufferSize];
            var messageBuilder = new StringBuilder(ReceiveBufferSize);

            while (!cancellationToken.IsCancellationRequested && IsSocketOpen())
            {
                try
                {
                    WebSocketReceiveResult result = await clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await HandleRemoteCloseAsync(result.CloseStatusDescription, cancellationToken);
                        return;
                    }

                    if (result.Count > 0) messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    string message = messageBuilder.ToString();
                    messageBuilder.Length = 0;
                    if (!string.IsNullOrWhiteSpace(message)) MessageReceived?.Invoke(message);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!isDisconnecting) ErrorReceived?.Invoke("WebSocket receive failed: " + ex.Message);
                    await HandleRemoteCloseAsync("Receive failed", CancellationToken.None);
                    return;
                }
            }
        }

        //* هدرهای اتصال را روی کلاینت وب‌سوکت اعمال می‌کند.
        private void ApplyHeaders(Dictionary<string, string> headers)
        {
            if (headers == null) return;

            foreach (var pair in headers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                clientWebSocket.Options.SetRequestHeader(pair.Key, pair.Value ?? string.Empty);
            }
        }

        //* بسته شدن اتصال از سمت سرور یا خطای دریافت را به رویداد قطع اتصال تبدیل می‌کند.
        private async Task HandleRemoteCloseAsync(string reason, CancellationToken cancellationToken)
        {
            if (isDisconnecting) return;
            isDisconnecting = true;
            SetState(RealtimeTransportState.Disconnecting);
            await CleanupSocketAsync(reason, cancellationToken);
            SetState(RealtimeTransportState.Disconnected);
            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "WebSocket closed." : reason);
            isDisconnecting = false;
        }
#endif

        //* منابع اتصال وب‌سوکت را با close frame استاندارد می‌بندد و سپس پاکسازی می‌کند.
        private async Task CleanupSocketAsync(string reason, CancellationToken cancellationToken)
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            ClientWebSocket socketToClose = clientWebSocket;

            if (socketToClose != null)
            {
                try
                {
                    if (socketToClose.State == WebSocketState.Open || socketToClose.State == WebSocketState.CloseReceived)
                    {
                        using (CancellationTokenSource closeCts = CreateGracefulCloseToken(cancellationToken))
                        {
                            await socketToClose.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason ?? "Client disconnect", closeCts.Token);
                            await Task.Delay(120, closeCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // اگر close timeout شد، پاکسازی ادامه پیدا می‌کند تا ترنسپورت گیر نکند.
                }
                catch
                {
                    // بستن سوکت در زمان قطع شبکه ممکن است خطا بدهد و اینجا فقط پاکسازی مهم است.
                }

                connectionCts?.Cancel();
                socketToClose.Dispose();
                clientWebSocket = null;
            }
            else
            {
                connectionCts?.Cancel();
            }
#endif

            connectionCts?.Dispose();
            connectionCts = null;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        //* توکن جدا برای بستن تمیز وب‌سوکت می‌سازد تا توکن لغوشده تست باعث close code غیرعادی نشود.
        private CancellationTokenSource CreateGracefulCloseToken(CancellationToken cancellationToken)
        {
            CancellationTokenSource closeCts = cancellationToken.IsCancellationRequested
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            closeCts.CancelAfter(GracefulCloseTimeoutMs);
            return closeCts;
        }
#endif

        //* بررسی می‌کند سوکت واقعی در وضعیت باز قرار دارد یا نه.
        private bool IsSocketOpen()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            return clientWebSocket != null && clientWebSocket.State == WebSocketState.Open;
#endif
        }

        //* اتصال ناموفق را ثبت می‌کند و خطا را به کُر گزارش می‌دهد.
        private bool FailConnect(string message)
        {
            SetState(RealtimeTransportState.Failed);
            ErrorReceived?.Invoke(message);
            Debug.LogWarning("[WebSocketRealtimeTransport] " + message);
            return false;
        }

        //* وضعیت ترنسپورت را به شکل کنترل‌شده تغییر می‌دهد.
        private void SetState(RealtimeTransportState newState)
        {
            state = newState;
        }

        //* منابع ترنسپورت را هنگام خروج یا تعویض اتصال پاکسازی می‌کند.
        public void Dispose()
        {
            _ = DisconnectAsync("WebSocketRealtimeTransport disposed");
        }
    }
}

//* این فایل پیاده‌سازی ترنسپورت وب‌سوکت ریل‌تایم را نگه می‌دارد.
//* این فایل فقط وظیفه اتصال، ارسال، دریافت و قطع پیام خام را دارد.
//* منطق روم، بازی، اَک، آث و رُت داخل این فایل نوشته نمی‌شود.
