using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Text;

/// <summary>
/// Network connection test controller for three platforms
/// This script is independent from the main system and used only for testing
/// </summary>
public class NetworkTestController : MonoBehaviour
{
    public static NetworkTestController Instance { get; private set; }

    [Header("Test Results")]
    public bool isHttpsWorking = false;
    public bool isWebSocketWorking = false;
    public string lastErrorMessage = string.Empty;
    public float latencyMs = 0f;

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
    }

    /// <summary>
    /// Test HTTPS connection to server
    /// </summary>
    public IEnumerator TestHTTPSConnection()
    {
        string testUrl = EnvironmentConfig.Instance.GetFullUrl("health");

        Debug.Log($"[HTTPS Test] Connecting to server: {testUrl}");

        using (UnityWebRequest request = UnityWebRequest.Get(testUrl))
        {
            request.timeout = 10; // 10 seconds timeout

            // Add headers for client identification
            request.SetRequestHeader("X-Metaverse-Client", GetPlatformIdentifier());
            request.SetRequestHeader("X-Metaverse-Version", Application.version);

            float startTime = Time.realtimeSinceStartup;
            yield return request.SendWebRequest();
            latencyMs = (Time.realtimeSinceStartup - startTime) * 1000f;

            if (request.result != UnityWebRequest.Result.Success)
            {
                isHttpsWorking = false;
                lastErrorMessage = $"HTTPS Error: {request.error}\nStatus Code: {request.responseCode}";
                Debug.LogError(lastErrorMessage);
            }
            else
            {
                isHttpsWorking = true;
                lastErrorMessage = string.Empty;
                Debug.Log($"[HTTPS Success] Latency: {latencyMs:F2}ms\nResponse: {request.downloadHandler.text}");
            }
        }
    }

    /// <summary>
    /// Test WebSocket connection (simplified for Stage 0)
    /// </summary>
    public IEnumerator TestWebSocketConnection()
    {
        // Stage 0 only validates WebSocket URL format
        // Full WebSocket implementation will be added in Stage 4

        string wsUrl = EnvironmentConfig.Instance.GetWebSocketUrl();

        // Validate URL format
        if (wsUrl.StartsWith("wss://") || wsUrl.StartsWith("ws://"))
        {
            isWebSocketWorking = true;
            lastErrorMessage = string.Empty;
            Debug.Log($"[WebSocket] URL validation successful: {wsUrl}");
            yield return new WaitForSeconds(0.5f); // Simulate connection
        }
        else
        {
            isWebSocketWorking = false;
            lastErrorMessage = $"[WebSocket] Invalid URL format: {wsUrl}";
            Debug.LogError(lastErrorMessage);
        }
    }

    /// <summary>
    /// Identify current platform for logging
    /// </summary>
    private string GetPlatformIdentifier()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
    return "WebGL";
#elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
    return "Windows";
#elif UNITY_ANDROID && !UNITY_EDITOR
    return "AndroidQuest";
#else
        return "Editor";
#endif
    }


    /// <summary>
    /// Run complete network test suite
    /// </summary>
    public IEnumerator RunFullTest()
    {
        Debug.Log("========================================");
        Debug.Log("Starting full network test...");
        Debug.Log($"Platform: {GetPlatformIdentifier()}");
        Debug.Log($"Environment: {EnvironmentConfig.Instance.GetCurrentEnvironment()}");
        Debug.Log($"API Base URL: {EnvironmentConfig.Instance.GetApiBaseUrl()}");
        Debug.Log("========================================");

        // Test 1: HTTPS Connection
        yield return TestHTTPSConnection();

        // Test 2: WebSocket Validation
        yield return TestWebSocketConnection();

        Debug.Log("========================================");
        Debug.Log($"HTTPS Test Result: {(isHttpsWorking ? "✅ SUCCESS" : "❌ FAILED")}");
        Debug.Log($"WebSocket Test Result: {(isWebSocketWorking ? "✅ VALID" : "❌ INVALID")}");
        Debug.Log($"Network Latency: {latencyMs:F2}ms");
        if (!string.IsNullOrEmpty(lastErrorMessage))
            Debug.Log($"Error Details: {lastErrorMessage}");
        Debug.Log("========================================");
    }
}