using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Core;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* این Wrapper زمان انتظار CheckNet در WebGL را مستقل از فایل مشترک AuthManager محدود می کند.
    public static class WebGLCheckNetFastWrapper
    {
        //* در WebGL یک Timeout قطعی روی Task اعمال می کند و در سایر پلتفرم ها رفتار اصلی AuthManager را بدون تغییر عبور می دهد.
        public static async Task<bool> CheckNetFastSilentAsync(
            AuthManager authManager,
            int timeoutMs = 2500,
            CancellationToken externalToken = default(CancellationToken)
        )
        {
            if (authManager == null) return false;

#if UNITY_WEBGL && !UNITY_EDITOR
            int safeTimeoutMs = Mathf.Clamp(timeoutMs, 700, 5000);

            using (CancellationTokenSource requestCts =
                   CancellationTokenSource.CreateLinkedTokenSource(externalToken))
            using (CancellationTokenSource timeoutCts =
                   CancellationTokenSource.CreateLinkedTokenSource(externalToken))
            {
                Task<bool> checkTask = authManager.CheckNetFastSilentAsync(
                    safeTimeoutMs,
                    requestCts.Token
                );

                Task<bool> timeoutTask = WaitForRealtimeTimeoutAsync(
                    safeTimeoutMs,
                    timeoutCts.Token
                );

                Task completedTask = await Task.WhenAny(checkTask, timeoutTask);

                if (completedTask == checkTask)
                {
                    timeoutCts.Cancel();
                    return await checkTask;
                }

                requestCts.Cancel();

                _ = checkTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default
                );

                NetworkFileLogger.Warning(
                    "CHECK_NET_FAST_WEBGL_WRAPPER",
                    "silent=false | strict timeout/cancel | timeoutMs=" +
                    safeTimeoutMs
                );

                return false;
            }
#else
            return await authManager.CheckNetFastSilentAsync(
                timeoutMs,
                externalToken
            );
#endif
        }

        //* این تابع Delay حلقه Reconnect را در WebGL با زمان واقعی Unity اجرا می کند و در سایر پلتفرم ها همان Task.Delay را نگه می دارد.
        public static async Task DelayRealtimeAsync(
            int delayMs,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            int safeDelayMs = Mathf.Max(0, delayMs);

#if UNITY_WEBGL && !UNITY_EDITOR
            bool completed = await WaitForRealtimeTimeoutAsync(
                safeDelayMs,
                cancellationToken
            );

            if (!completed && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
#else
            await Task.Delay(safeDelayMs, cancellationToken);
#endif
        }

        //* این تابع Timeout را با Coroutine یونیتی اجرا می کند تا در WebGL به Task.Delay وابسته نباشد.
        private static async Task<bool> WaitForRealtimeTimeoutAsync(
            int timeoutMs,
            CancellationToken cancellationToken
        )
        {
            var waiter = new TaskCompletionSource<bool>();
            Coroutine timeoutCoroutine = null;
            CancellationTokenRegistration registration = default(CancellationTokenRegistration);

            try
            {
                timeoutCoroutine = CoroutineRunner_A.Run(
                    WaitForRealtimeTimeoutCoroutine(
                        timeoutMs,
                        cancellationToken,
                        waiter
                    )
                );

                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(() =>
                    {
                        CoroutineRunner_A.Stop(timeoutCoroutine);
                        waiter.TrySetResult(false);
                    });
                }

                return await waiter.Task;
            }
            finally
            {
                registration.Dispose();
                CoroutineRunner_A.Stop(timeoutCoroutine);
            }
        }

        //* این Coroutine زمان واقعی Unity را فریم به فریم کنترل می کند و در پایان Timeout را کامل می کند.
        private static IEnumerator WaitForRealtimeTimeoutCoroutine(
            int timeoutMs,
            CancellationToken cancellationToken,
            TaskCompletionSource<bool> waiter
        )
        {
            float endTime =
                Time.realtimeSinceStartup + (Mathf.Max(0, timeoutMs) / 1000f);

            while (Time.realtimeSinceStartup < endTime)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    waiter.TrySetResult(false);
                    yield break;
                }

                yield return null;
            }

            waiter.TrySetResult(true);
        }
    }
}
