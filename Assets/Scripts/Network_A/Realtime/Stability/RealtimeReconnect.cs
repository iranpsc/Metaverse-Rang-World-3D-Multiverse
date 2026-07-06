using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Core;
using UnityEngine;

namespace Network_A.Realtime.Stability
{
    //* بازاتصال ریل تایم را با بک آف نمایی مدیریت می کند و به نوع ترنسپورت وابسته نیست.
    public class RealtimeReconnect : IDisposable
    {
        private CancellationTokenSource reconnectCts;
        private Task reconnectLoopTask;
        private bool isRunning;
        private int attemptCount;
        private long startedUnixMs;

        public event Action<int, int> ReconnectAttemptStarted;
        public event Action<int> ReconnectSucceeded;
        public event Action<string> ReconnectFailed;
        public event Action<string> ReconnectLogReceived;

        public int maxAttempts = 10;
        public int initialDelayMs = 1000;
        public int maxDelayMs = 60000;
        public int totalTimeoutMs = 600000;
        public float delayMultiplier = 2f;
        public bool logReconnect;

        public bool IsRunning => isRunning;
        public int AttemptCount => attemptCount;

        //* بازاتصال را شروع می کند و تابع اتصال کُر را در هر تلاش صدا می زند.
        public void Start(Func<CancellationToken, Task<bool>> connectCallback)
        {
            if (connectCallback == null) throw new ArgumentNullException(nameof(connectCallback));
            if (isRunning) return;

            DisposeReconnectTokenSource();
            isRunning = true;
            attemptCount = 0;
            startedUnixMs = NowUnixMs();
            reconnectCts = new CancellationTokenSource();
            WriteLog("Realtime reconnect started.");
            reconnectLoopTask = RunReconnectLoopAsync(connectCallback, reconnectCts.Token);
        }

        //* بازاتصال فعال را متوقف می کند و توکن داخلی را حتی بعد از موفقیت چرخه قبلی پاک می کند.
        public void Stop()
        {
            if (!isRunning && reconnectCts == null) return;

            isRunning = false;
            DisposeReconnectTokenSource();
            reconnectLoopTask = null;
            WriteLog("Realtime reconnect stopped.");
        }

        //* شمارنده تلاش ها را برای چرخه جدید بازنشانی می کند.
        public void Reset()
        {
            attemptCount = 0;
            startedUnixMs = 0;
        }

        //* توکن داخلی ریکانکت را بدون وابستگی به وضعیت isRunning پاکسازی می کند.
        private void DisposeReconnectTokenSource()
        {
            reconnectCts?.Cancel();
            reconnectCts?.Dispose();
            reconnectCts = null;
        }


        //* حلقه بازاتصال را با تاخیر نمایی اجرا می کند تا اتصال دوباره برقرار شود یا شکست نهایی اعلام شود.
        private async Task RunReconnectLoopAsync(Func<CancellationToken, Task<bool>> connectCallback, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && CanContinue())
                {
                    attemptCount++;
                    int delayMs = CalculateDelayMs(attemptCount);
                    ReconnectAttemptStarted?.Invoke(attemptCount, delayMs);
                    WriteLog("Realtime reconnect attempt " + attemptCount + " after " + delayMs + "ms.");

                    bool canContinueAfterDelay = await WaitBeforeReconnectAttemptAsync(delayMs, cancellationToken);
                    if (!canContinueAfterDelay) return;

                    WriteLog("Realtime reconnect attempt " + attemptCount + " callback starting.");
                    bool connected = await connectCallback(cancellationToken);
                    WriteLog("Realtime reconnect attempt " + attemptCount + " callback result: " + connected);

                    if (connected)
                    {
                        isRunning = false;
                        reconnectLoopTask = null;
                        ReconnectSucceeded?.Invoke(attemptCount);
                        WriteLog("Realtime reconnect succeeded.");
                        return;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                WriteLog("Realtime reconnect loop canceled before the next connect callback.");
                return;
            }
            catch (Exception ex)
            {
                WriteLog("Realtime reconnect loop error: " + ex.Message);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                isRunning = false;
                reconnectLoopTask = null;
                string reason = "Realtime reconnect failed after " + attemptCount + " attempts.";
                ReconnectFailed?.Invoke(reason);
                WriteLog(reason);
            }
        }

        //* قبل از تلاش بعدی صبر می کند و اگر توکن ریکانکت لغو شود، دلیل را در لاگ قابل مشاهده می کند.
        private async Task<bool> WaitBeforeReconnectAttemptAsync(int delayMs, CancellationToken cancellationToken)
        {
            if (delayMs <= 0) return !cancellationToken.IsCancellationRequested;

#if UNITY_WEBGL && !UNITY_EDITOR
            return await WaitBeforeReconnectAttemptWithUnityCoroutineAsync(delayMs, cancellationToken);
#else
            try
            {
                await Task.Delay(delayMs, cancellationToken);
                return !cancellationToken.IsCancellationRequested;
            }
            catch (TaskCanceledException)
            {
                WriteLog("Realtime reconnect delay canceled before connect callback. attempt=" + attemptCount);
                return false;
            }
#endif
        }

        //* در WebGL تاخیر ریکانکت را با کوروتین یونیتی انجام می دهد تا روی تَسک دیلی مرورگر گیر نکند.
        private async Task<bool> WaitBeforeReconnectAttemptWithUnityCoroutineAsync(int delayMs, CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource<bool>();
            Coroutine delayCoroutine = null;
            CancellationTokenRegistration registration = default;

            try
            {
                WriteLog("Realtime reconnect unity delay started. attempt=" + attemptCount + " delayMs=" + delayMs);
                delayCoroutine = CoroutineRunner_A.Run(CompleteReconnectDelayWithUnityCoroutine(delayMs, cancellationToken, waiter));

                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(() =>
                    {
                        CoroutineRunner_A.Stop(delayCoroutine);
                        WriteLog("Realtime reconnect unity delay canceled before connect callback. attempt=" + attemptCount);
                        waiter.TrySetResult(false);
                    });
                }

                bool completed = await waiter.Task;
                if (!completed) return false;

                WriteLog("Realtime reconnect unity delay completed. attempt=" + attemptCount);
                return !cancellationToken.IsCancellationRequested;
            }
            finally
            {
                registration.Dispose();
                CoroutineRunner_A.Stop(delayCoroutine);
            }
        }

        //* تاخیر فریمی WebGL را روی مین ترد یونیتی جلو می برد و در پایان نتیجه را به تَسک برمی گرداند.
        private IEnumerator CompleteReconnectDelayWithUnityCoroutine(int delayMs, CancellationToken cancellationToken, TaskCompletionSource<bool> waiter)
        {
            float endTime = Time.realtimeSinceStartup + (Mathf.Max(0, delayMs) / 1000f);

            while (Time.realtimeSinceStartup < endTime)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    waiter.TrySetResult(false);
                    yield break;
                }

                yield return null;
            }

            waiter.TrySetResult(!cancellationToken.IsCancellationRequested);
        }

        //* بررسی می کند بازاتصال هنوز از نظر تعداد تلاش و زمان کلی مجاز است یا نه.
        private bool CanContinue()
        {
            if (maxAttempts > 0 && attemptCount >= maxAttempts) return false;
            if (totalTimeoutMs <= 0) return true;
            return NowUnixMs() - startedUnixMs < totalTimeoutMs;
        }

        //* تاخیر تلاش بعدی را با بک آف نمایی محاسبه می کند.
        private int CalculateDelayMs(int attempt)
        {
            int safeAttempt = Math.Max(1, attempt);
            double value = initialDelayMs * Math.Pow(Math.Max(1f, delayMultiplier), safeAttempt - 1);
            return Mathf.Clamp((int)value, 0, Math.Max(initialDelayMs, maxDelayMs));
        }

        //* زمان فعلی را با فرمت یونیکس میلی ثانیه برمی گرداند.
        private long NowUnixMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        //* لاگ داخلی بازاتصال را در صورت فعال بودن به بیرون می فرستد.
        private void WriteLog(string message)
        {
            if (logReconnect) Debug.Log("[RealtimeReconnect] " + message);
            ReconnectLogReceived?.Invoke(message);
        }

        //* منابع بازاتصال را آزاد می کند.
        public void Dispose()
        {
            Stop();
        }
    }
}

//* این فایل سیاست بازاتصال ریل تایم را نگه می دارد.
//* این فایل فقط زمان بندی تلاش ها را مدیریت می کند و به وب سوکت یا جی آر پی سی وابسته نیست.
