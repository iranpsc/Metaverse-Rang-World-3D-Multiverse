using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    /// <summary>
    /// نظارت بر سلامت اتصال WebSocket با ارسال دوره‌ای ping
    /// </summary>
    public class HeartbeatMonitor
    {
        // تنظیمات
        public int PingIntervalMs { get; set; } = 30000; // هر ۳۰ ثانیه یک ping
        public int PongTimeoutMs { get; set; } = 10000;  // ۱۰ ثانیه انتظار pong
        public int MaxMissedPongs { get; set; } = 3;     // بعد از ۳ بار، اتصال lost

        private int missedPongs = 0;
        private DateTime lastPongReceivedUtc = DateTime.MinValue;

        private CancellationTokenSource heartbeatCts;
        private Action onConnectionLost;
        private Action onConnectionRecovered;
        private Func<Task<bool>> sendPingCallback;

        // وضعیت داخلی
        private bool isLost = false;              // آیا اتصال را lost تشخیص داده‌ایم؟
        private bool wasUnhealthyBeforePong = false; // برای تشخیص recovery

        public void StartMonitoring(Func<Task<bool>> sendPingCallback, Action onConnectionLost, Action onConnectionRecovered)
        {
            this.sendPingCallback = sendPingCallback ?? throw new ArgumentNullException(nameof(sendPingCallback));//the WebSocketClient_cs.SendPingAsync() Address
            this.onConnectionLost = onConnectionLost;
            this.onConnectionRecovered = onConnectionRecovered;

            StopMonitoring(); // اطمینان از توقف قبلی

            missedPongs = 0;
            isLost = false;
            wasUnhealthyBeforePong = false;

            // جلوگیری از timeout اولیه‌ی کاذب
            lastPongReceivedUtc = DateTime.UtcNow;

            heartbeatCts = new CancellationTokenSource();
            _ = MonitorConnectionAsync(heartbeatCts.Token);

            Debug.Log($"Heartbeat Monitor شروع شد (Interval: {PingIntervalMs}ms, Timeout: {PongTimeoutMs}ms)");
        }

        public void StopMonitoring()
        {
            if (heartbeatCts != null)
            {
                heartbeatCts.Cancel();
                heartbeatCts.Dispose();
                heartbeatCts = null;
            }

            missedPongs = 0;
            isLost = false;
            wasUnhealthyBeforePong = false;
            lastPongReceivedUtc = DateTime.MinValue;

            Debug.Log("Heartbeat Monitor متوقف شد");
        }

        /// <summary>
        /// هنگام دریافت pong فراخوانی می‌شود
        /// </summary>
        public void OnPongReceived()
        {
            // قبل از reset، وضعیت قبلی را نگه دار
            bool wasLost = isLost;
            bool wasUnhealthy = (missedPongs >= MaxMissedPongs) || wasUnhealthyBeforePong;

            // ثبت pong
            lastPongReceivedUtc = DateTime.UtcNow;
            missedPongs = 0;
            isLost = false;
            wasUnhealthyBeforePong = false;

            // اگر قبلاً lost/unhealthy بودیم و الان pong آمد => recovery
            if (wasLost || wasUnhealthy)
            {
                Debug.Log("اتصال WebSocket بازیابی شد (pong دریافت شد)");
                onConnectionRecovered?.Invoke();
            }
        }

        /// <summary>
        /// وقتی pong در زمان مقرر دریافت نشود
        /// </summary>
        private void OnPongTimeout()
        {
            missedPongs++;
            Debug.LogWarning($"Pong timeout #{missedPongs}/{MaxMissedPongs}");

            if (missedPongs >= MaxMissedPongs)
            {
                wasUnhealthyBeforePong = true;// helper flag

                if (!isLost)
                {
                    isLost = true;
                    Debug.LogError($"اتصال WebSocket قطع شد (بدون پاسخ به {missedPongs} ping)");
                    onConnectionLost?.Invoke();
                }
            }
        }

        private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // ارسال ping
                    bool pingSent = await sendPingCallback();

                    if (!pingSent)
                    {
                        Debug.LogWarning("ارسال ping شکست خورد");
                        OnPongTimeout();
                    }
                    else
                    {
                        // منتظر pong
                        await Task.Delay(PongTimeoutMs, cancellationToken);
                        //lastPongReceivedUtc
                        // این متغییر هر لحظه می تواند در آن رسیو مقدار دهی شود و مقدارش کوچک شده و آن پونگ تایم آوت نشود          
                        var elapsedMs = (DateTime.UtcNow - lastPongReceivedUtc).TotalMilliseconds;
                        if (elapsedMs > PongTimeoutMs)
                        {
                            OnPongTimeout();
                        }
                    }

                    // فاصله تا ping بعدی
                    await Task.Delay(PingIntervalMs, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"خطا در Heartbeat Monitor: {ex.Message}");
                    // کمی مکث تا اسپم نشود
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        public string GetStatus()
        {
            if (lastPongReceivedUtc == DateTime.MinValue)
                return $"Missed Pongs: {missedPongs}/{MaxMissedPongs}, Last Pong: (none), Interval: {PingIntervalMs}ms";

            var agoSec = (DateTime.UtcNow - lastPongReceivedUtc).TotalSeconds;
            return $"Missed Pongs: {missedPongs}/{MaxMissedPongs}, Lost: {isLost}, Last Pong: {agoSec:F1}s ago, Interval: {PingIntervalMs}ms";
        }

        public bool IsConnectionHealthy()
        {
            return !isLost && missedPongs < MaxMissedPongs;
        }
    }
}
