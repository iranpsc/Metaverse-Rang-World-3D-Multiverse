using UnityEngine;
using System;

/// <summary>
/// مدیریت محیط‌های مختلف پروژه (لوکال، تست، تولید)
/// این کلاس مستقل از صحنه است و در همه جا قابل استفاده است
/// </summary>
public class EnvironmentConfig : MonoBehaviour
{
    public static EnvironmentConfig Instance { get; private set; }

    // انواع محیط‌ها
    public enum EnvironmentType
    {
        Localhost,
        Staging,
        Production
    }

    [Header("تنظیمات محیط")]
    [SerializeField] private EnvironmentType currentEnvironment = EnvironmentType.Localhost;
    [SerializeField] private bool useHttps = true; // همیشه true برای سازگاری با WebGL

    // آدرس‌های پایه برای هر محیط
    [Header("آدرس‌های سرور")]
    [SerializeField] private string localhostBaseUrl = "https://localhost:8443";
    [SerializeField] private string stagingBaseUrl = "https://staging-api.metaverse.gov.ir";
    [SerializeField] private string productionBaseUrl = "https://accounts.irpsc.com";

    // آدرس‌های WebSocket
    [Header("آدرس‌های WebSocket")]
    [SerializeField] private string localhostWsUrl = "wss://localhost:8443/ws";
    [SerializeField] private string stagingWsUrl = "wss://staging-ws.metaverse.gov.ir";
    [SerializeField] private string productionWsUrl = "wss://ws.accounts.irpsc.com";

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }

        // بارگذاری محیط از ذخیره‌سازی (اگر قبلاً تنظیم شده)
        LoadEnvironmentFromPrefs();
    }

    //* ست کردن آدرس اولیه سرور بر اساس لوکال - تست - یا وی پی اس
    public string GetApiBaseUrl()
    {
        switch (currentEnvironment)
        {
            case EnvironmentType.Localhost:
                return localhostBaseUrl;
            case EnvironmentType.Staging:
                return stagingBaseUrl;
            case EnvironmentType.Production:
                return productionBaseUrl;
            default:
                return localhostBaseUrl;
        }
    }

    /// <summary>
    /// دریافت آدرس WebSocket بر اساس محیط فعلی
    /// </summary>
    public string GetWebSocketUrl()
    {
        switch (currentEnvironment)
        {
            case EnvironmentType.Localhost:
                return localhostWsUrl;
            case EnvironmentType.Staging:
                return stagingWsUrl;
            case EnvironmentType.Production:
                return productionWsUrl;
            default:
                return localhostWsUrl;
        }
    }

    //* از اینسپکتور تنظیم می شود 
    public void SetEnvironment(EnvironmentType environment)
    {
        currentEnvironment = environment;
        SaveEnvironmentToPrefs();
        Debug.Log($"محیط تغییر کرد به: {environment}");
    }

    //* Save To Prefs
    private void SaveEnvironmentToPrefs()
    {
        PlayerPrefs.SetInt("EnvironmentConfig_Environment", (int)currentEnvironment);
        PlayerPrefs.Save();
    }

    //* Read From Prefs
    private void LoadEnvironmentFromPrefs()
    {
        if (PlayerPrefs.HasKey("EnvironmentConfig_Environment"))
        {
            int savedEnv = PlayerPrefs.GetInt("EnvironmentConfig_Environment");
            currentEnvironment = (EnvironmentType)savedEnv;
        }
    }

    /// <summary>
    /// دریافت نوع محیط فعلی
    /// </summary>
    public EnvironmentType GetCurrentEnvironment()
    {
        return currentEnvironment;
    }

    /// <summary>
    /// بررسی آیا محیط فعلی لوکال است یا خیر
    /// </summary>
    public bool IsLocalhost()
    {
        return currentEnvironment == EnvironmentType.Localhost;
    }

    //* خذف الش تکراری از قسمت دوم آدرس که ار توابع دیگر دریافت می شود
    //* string baseUrl = "https://api.example.com/";
    //* string endpoint = "/auth/login";
    public string GetFullUrl(string endpoint)
    {
        // حذف اسلش اضافه در ابتدا
        if (endpoint.StartsWith("/"))
            endpoint = endpoint.Substring(1);

        return $"{GetApiBaseUrl()}/{endpoint}";
    }
    //* تنظیم دستی نوع محیط از اینسپکتور
#if UNITY_EDITOR
    // ابزار توسعه: تغییر سریع محیط از ادیتور
    [ContextMenu("Switch to Localhost")]
    private void SwitchToLocalhostEditor()
    {
        SetEnvironment(EnvironmentType.Localhost);
    }

    [ContextMenu("Switch to Staging")]
    private void SwitchToStagingEditor()
    {
        SetEnvironment(EnvironmentType.Staging);
    }

    [ContextMenu("Switch to Production")]
    private void SwitchToProductionEditor()
    {
        SetEnvironment(EnvironmentType.Production);
    }
#endif
}