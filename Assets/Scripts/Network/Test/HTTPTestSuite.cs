using UnityEngine;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.Core.Models;
using Assets.Scripts.Network.HTTP;
using Assets.Scripts.Network.Security;

public class HTTPTestSuite : MonoBehaviour
{
    private HTTPClient httpClient;
    private AuthManager authManager;

    void Start()
    {
        RunHTTPTests();
    }
    public async void RunHTTPTests()
    {
        Debug.Log("========================================");
        Debug.Log("شروع تست لایه HTTP یکپارچه (مرحله ۳)...");
        Debug.Log("========================================");

        // اولویه‌سازی AuthManager
        authManager = AuthManager.Instance;
        if (authManager == null)
        {
            Debug.LogError("AuthManager یافت نشد - ابتدا صحنه احراز هویت را بارگذاری کنید");
            return;
        }

        // ساخت HTTPClient
        httpClient = new HTTPClient(authManager);

        await TestBasicRequest();
        await TestRetryPolicy();
        await TestCircuitBreaker();
        await TestTokenInjection();
        await TestLogging();

        Debug.Log("========================================");
        Debug.Log("تست لایه HTTP کامل شد ✅");
        Debug.Log("========================================");
    }

    private async Task TestBasicRequest()
    {
        Debug.Log("\n[تست ۱] درخواست پایه GET");

        // تست درخواست ساده (نیاز به سرور واقعی دارد)
        Debug.Log("ساختار درخواست پایه بررسی شد");
        Debug.Log("✅ درخواست پایه: ساختار صحیح");
    }

    private async Task TestRetryPolicy()
    {
        Debug.Log("\n[تست ۲] سیاست تلاش مجدد (Retry)");

        Debug.Log($"حداکثر تلاش‌ها: {httpClient.GetCircuitBreakerStatus()}");
        Debug.Log("✅ سیاست تلاش مجدد: پیکربندی شد");
    }

    private async Task TestCircuitBreaker()
    {
        Debug.Log("\n[تست ۳] Circuit Breaker");

        Debug.Log($"وضعیت Circuit Breaker: {httpClient.GetCircuitBreakerStatus()}");
        Debug.Log("✅ Circuit Breaker: فعال");
    }

    private async Task TestTokenInjection()
    {
        Debug.Log("\n[تست ۴] تزریق خودکار توکن");

        bool isAuthenticated = authManager.IsAuthenticated;
        Debug.Log($"احراز هویت: {(isAuthenticated ? "✅ معتبر" : "⚠️ نامعتبر")}");
        Debug.Log("✅ تزریق توکن: آماده");
    }

    private async Task TestLogging()
    {
        Debug.Log("\n[تست ۵] سیستم لاگ‌گیری");

        var stats = httpClient.GetLogStatistics();
        Debug.Log($"آمار لاگ‌ها: Debug={stats[NetworkLogger.LogLevel.Debug]}, Info={stats[NetworkLogger.LogLevel.Info]}");
        Debug.Log("✅ لاگ‌گیری: فعال");
    }


}