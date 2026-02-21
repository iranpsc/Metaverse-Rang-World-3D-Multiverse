using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    /// <summary>
    /// مدیریت بازاتصال هوشمند به سرور WebSocket
    /// این کلاس با استراتژی فاصله نمایی (Exponential Backoff) کار می‌کند
    /// </summary>
    public class ReconnectManager
    {
        // تنظیمات بازاتصال
        public int MaxReconnectAttempts { get; set; } = 10; // حداکثر تلاش‌ها
        public int InitialDelayMs { get; set; } = 1000; // تأخیر اولیه ۱ ثانیه
        public double DelayMultiplier { get; set; } = 2.0; // ضریب نمایی: 1s → 2s → 4s → 8s
        public int MaxDelayMs { get; set; } = 60000; // حداکثر تأخیر ۶۰ ثانیه
        public int TotalReconnectTimeoutMs { get; set; } = 600000; // ۱۰ دقیقه حداکثر

        private int reconnectAttempt = 0;
        private DateTime reconnectStartTime = DateTime.MinValue;
        private CancellationTokenSource reconnectCts;
        private Func<Dictionary<string, string>, CancellationToken, Task<bool>> connectCallback;
        private Action onReconnectSuccess;
        private Action onReconnectFailed;
        private Dictionary<string, string> connectionHeaders;

        /// <summary>
        /// شروع فرآیند بازاتصال
        /// </summary>
        public void StartReconnect(
            Dictionary<string, string> headers,
            Func<Dictionary<string, string>, CancellationToken, Task<bool>> connectCallback,
            Action onReconnectSuccess,
            Action onReconnectFailed)
        {
            this.connectionHeaders = headers ?? new Dictionary<string, string>();
            this.connectCallback = connectCallback ?? throw new ArgumentNullException(nameof(connectCallback));
            this.onReconnectSuccess = onReconnectSuccess;
            this.onReconnectFailed = onReconnectFailed;

            reconnectAttempt = 0;
            reconnectStartTime = DateTime.UtcNow;
            reconnectCts = new CancellationTokenSource();

            Debug.LogWarning("فرآیند بازاتصال WebSocket شروع شد...");
            _ = AttemptReconnectAsync(reconnectCts.Token);
        }

        /// <summary>
        /// توقف بازاتصال
        /// </summary>
        public void StopReconnect()
        {
            reconnectCts?.Cancel();
            reconnectCts = null;
            reconnectAttempt = 0;
            Debug.Log("فرآیند بازاتصال متوقف شد");
        }

        /// <summary>
        /// تلاش برای بازاتصال
        /// </summary>
        private async Task AttemptReconnectAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // بررسی تایم‌اوت کلی
                if ((DateTime.UtcNow - reconnectStartTime).TotalMilliseconds > TotalReconnectTimeoutMs)
                {
                    Debug.LogError($"بازاتصال WebSocket به دلیل تایم‌اوت کلی ({TotalReconnectTimeoutMs}ms) متوقف شد");
                    onReconnectFailed?.Invoke();
                    return;
                }

                // بررسی حداکثر تلاش‌ها
                if (reconnectAttempt >= MaxReconnectAttempts)
                {
                    Debug.LogError($"بازاتصال WebSocket پس از {MaxReconnectAttempts} تلاش ناموفق متوقف شد");
                    onReconnectFailed?.Invoke();
                    return;
                }

                reconnectAttempt++;
                int delayMs = CalculateDelay();

                Debug.LogWarning($"تلاش {reconnectAttempt}/{MaxReconnectAttempts} برای بازاتصال در {delayMs}ms...");

                try
                {
                    // تأخیر قبل از تلاش
                    await Task.Delay(delayMs, cancellationToken);

                    // تلاش برای اتصال
                    bool connected = await connectCallback(connectionHeaders, cancellationToken);

                    if (connected)
                    {
                        Debug.Log($"بازاتصال WebSocket موفقیت‌آمیز بود (تلاش {reconnectAttempt})");
                        onReconnectSuccess?.Invoke();
                        return;
                    }
                    else
                    {
                        Debug.LogWarning($"تلاش {reconnectAttempt} برای بازاتصال شکست خورد");
                    }
                }
                catch (TaskCanceledException)
                {
                    // لغو شده - خارج شو
                    Debug.Log("بازاتصال WebSocket لغو شد");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"خطا در تلاش {reconnectAttempt} برای بازاتصال: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// محاسبه تأخیر برای تلاش بعدی (فاصله نمایی)
        /// </summary>
        private int CalculateDelay()
        {
            int delay = (int)(InitialDelayMs * Math.Pow(DelayMultiplier, reconnectAttempt - 1));
            return Mathf.Min(delay, MaxDelayMs);
        }

        /// <summary>
        /// بازنشانی شمارنده تلاش‌ها
        /// </summary>
        public void Reset()
        {
            reconnectAttempt = 0;
            reconnectStartTime = DateTime.MinValue;
        }

        /// <summary>
        /// دریافت وضعیت فعلی
        /// </summary>
        public string GetStatus()
        {
            return $"Attempt: {reconnectAttempt}/{MaxReconnectAttempts}, " +
                   $"Delay: {CalculateDelay()}ms, " +
                   $"Elapsed: {(DateTime.UtcNow - reconnectStartTime).TotalSeconds:F1}s";
        }

        /// <summary>
        /// بررسی آیا هنوز می‌توان تلاش کرد یا خیر
        /// </summary>
        public bool CanContinueReconnecting()
        {
            return reconnectAttempt < MaxReconnectAttempts &&
                   (DateTime.UtcNow - reconnectStartTime).TotalMilliseconds < TotalReconnectTimeoutMs;
        }
    }
}