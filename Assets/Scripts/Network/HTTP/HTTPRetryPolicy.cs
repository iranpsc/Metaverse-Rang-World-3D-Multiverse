using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.Core.Models;
using UnityEngine;

namespace Assets.Scripts.Network.HTTP
{
    /// <summary>
    /// سیاست تلاش مجدد هوشمند برای درخواست‌های HTTP
    /// شامل قابلیت‌های زیر:
    /// - Retry با فاصله نمایی (Exponential Backoff)
    /// - Circuit Breaker برای جلوگیری از overload
    /// - تشخیص خطاها و تصمیم‌گیری برای تلاش مجدد
    /// </summary>
    public class HTTPRetryPolicy
    {
        // تنظیمات Retry
        public int MaxRetryAttempts { get; set; } = 3;
        public int InitialRetryDelayMs { get; set; } = 1000; // 1 ثانیه
        public double RetryDelayMultiplier { get; set; } = 2.0; // نمایی: 1s → 2s → 4s

        // تنظیمات Circuit Breaker
        public int CircuitBreakerFailureThreshold { get; set; } = 5;
        public int CircuitBreakerResetTimeoutMs { get; set; } = 30000; // 30 ثانیه
        /// <summary>
        /// Tracks whether a single test request is currently in progress
        /// while the Circuit Breaker is in the Half-Open state.
        /// This prevents multiple concurrent test executions and ensures
        /// correct Circuit Breaker behavior.
        /// </summary> 
        private bool halfOpenTestInProgress = false;
        // حالت‌های Circuit Breaker
        private enum CircuitState
        {
            Closed,    // عادی - درخواست‌ها ارسال می‌شوند
            Open,      // باز - درخواست‌ها رد می‌شوند (به دلیل خطا)
            HalfOpen   // نیمه‌باز - یک درخواست تست برای بررسی سلامت
        }

        private CircuitState circuitState = CircuitState.Closed;
        private int consecutiveFailures = 0;
        private DateTime circuitOpenedTime = DateTime.MinValue;

        /// <summary>
        /// اجرای درخواست با سیاست Retry و Circuit Breaker
        /// </summary>
        public async Task<ResponseModel> ExecuteWithRetryAsync(Func<CancellationToken, Task<ResponseModel>> requestFunc, CancellationToken cancellationToken)
        {
            // بررسی وضعیت Circuit Breaker
            if (!CanExecuteRequest())
            {
                return ResponseModel.Failure(
                    new NetworkError(NetworkErrorCode.ServiceUnavailable,
                        "سرویس در دسترس نیست - Circuit Breaker فعال شده است",
                        "لطفاً بعداً دوباره امتحان کنید"
                    )
                );
            }

            Exception lastException = null;
            ResponseModel lastResponse = null;

            for (int attempt = 0; attempt <= MaxRetryAttempts; attempt++)
            {
                try
                {
                    //* send request
                    ResponseModel response = await requestFunc(cancellationToken);

                    // بررسی موفقیت پاسخ
                    if (response.IsSuccess || !ShouldRetry(response))
                    {
                        // موفقیت - بازنشانی Circuit Breaker
                        ResetCircuitBreaker();
                        return response;
                    }

                    // ذخیره پاسخ برای بررسی در تلاش بعدی
                    lastResponse = response;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Debug.LogWarning($"تلاش {attempt + 1}/{MaxRetryAttempts + 1} شکست خورد: {ex.Message}");
                }

                // اگر این آخرین تلاش بود، خارج شو
                if (attempt == MaxRetryAttempts)
                    break;

                // محاسبه تأخیر برای تلاش بعدی
                int delayMs = (int)(InitialRetryDelayMs * Math.Pow(RetryDelayMultiplier, attempt));// 1,2,4
                Debug.Log($"تلاش مجدد در {delayMs}ms...");

                try
                {
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    // لغو شده - خارج شو
                    return ResponseModel.Cancelled(Guid.NewGuid().ToString());
                }
            }

            // تمام تلاش‌ها شکست خوردند
            IncrementCircuitBreakerFailures();

            if (lastResponse != null)
            {
                return lastResponse;
            }

            return ResponseModel.Failure(
                new NetworkError(
                    NetworkErrorCode.ConnectionFailed,
                    "تمام تلاش‌ها برای اتصال شکست خورد",
                    $"آخرین خطا: {lastException?.Message}"
                )
            );
        }

        /// <summary>
        /// بررسی آیا می‌توان درخواست را اجرا کرد یا نه (بر اساس Circuit Breaker)
        /// </summary>


        private bool CanExecuteRequest()
        {
            if (circuitState == CircuitState.Closed)
                return true;

            if (circuitState == CircuitState.Open)
            {
                if ((DateTime.UtcNow - circuitOpenedTime).TotalMilliseconds > CircuitBreakerResetTimeoutMs)
                {
                    circuitState = CircuitState.HalfOpen;
                    halfOpenTestInProgress = false;
                    Debug.Log("Circuit Breaker: ورود به حالت Half-Open");
                    return true;
                }

                return false;
            }
            //* Use Only With One Request
            if (circuitState == CircuitState.HalfOpen)
            {
                //* فقط یک درخواست تست
                if (halfOpenTestInProgress)
                    return false;

                halfOpenTestInProgress = true;
                return true;
            }

            return true;
        }


        /// <summary>
        /// بازنشانی Circuit Breaker پس از موفقیت
        /// </summary>
        private void ResetCircuitBreaker()
        {
            consecutiveFailures = 0;
            circuitState = CircuitState.Closed;
            halfOpenTestInProgress = false;

            Debug.Log("Circuit Breaker: بازگشت به حالت Closed");
        }


        /// <summary>
        /// افزایش شمارنده خطاها و فعال‌سازی Circuit Breaker در صورت نیاز
        /// </summary>

        private void IncrementCircuitBreakerFailures()
        {
            // اگر تست Half-Open شکست خورد → Open فوری
            if (circuitState == CircuitState.HalfOpen)
            {
                circuitState = CircuitState.Open;
                circuitOpenedTime = DateTime.UtcNow;
                halfOpenTestInProgress = false;

                Debug.LogWarning("Circuit Breaker: تست Half-Open شکست خورد → Open");
                return;
            }

            // فقط در حالت Closed شمارش کن
            if (circuitState != CircuitState.Closed)
                return;

            consecutiveFailures++;

            if (consecutiveFailures >= CircuitBreakerFailureThreshold)
            {
                circuitState = CircuitState.Open;
                circuitOpenedTime = DateTime.UtcNow;

                Debug.LogWarning($"Circuit Breaker فعال شد پس از {consecutiveFailures} خطا متوالی");
            }
        }


        /// <summary>
        /// بررسی آیا پاسخ نیاز به تلاش مجدد دارد یا نه
        /// </summary>
        private bool ShouldRetry(ResponseModel response)
        {
            // تلاش مجدد برای خطاهای سرور (5xx)
            if (response.StatusCode >= 500 && response.StatusCode < 600)
                return true;

            // تلاش مجدد برای خطای تایم‌اوت
            if (response.Error?.Code == NetworkErrorCode.Timeout)
                return true;

            // تلاش مجدد برای خطای اتصال
            if (response.Error?.Code == NetworkErrorCode.ConnectionFailed)
                return true;

            // برای سایر خطاها (4xx) تلاش مجدد نکن
            return false;
        }

        /// <summary>
        /// دریافت وضعیت فعلی Circuit Breaker برای مانیتورینگ
        /// </summary>
        public string GetCircuitBreakerStatus()
        {
            return $"State: {circuitState}, Failures: {consecutiveFailures}/{CircuitBreakerFailureThreshold}";
        }
    }
}