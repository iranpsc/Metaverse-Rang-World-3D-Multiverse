using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using TMPro;
using Assets.Scripts.Network.Security;

/// <summary>
/// Simple UI interface for network testing with platform identification
/// </summary>
public class TestUI : MonoBehaviour
{
    [Header("--- UI Elements ---")]
    [SerializeField] private Button testHttpsButton;
    [SerializeField] private Button testWebSocketButton;
    [SerializeField] private Button runAllTestsButton;
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI latencyText;
    [SerializeField] private TextMeshProUGUI environmentText;
    [SerializeField] private TextMeshProUGUI platformText; //ص NEW: Dedicated platform display

    [SerializeField] private TMP_InputField inTx_UserName;
    [SerializeField] private TMP_InputField inTx_Email;
    [SerializeField] private TMP_InputField inTx_Password;
    [SerializeField] private Button registerButton;
    [SerializeField] private Button loginButton;
    [SerializeField] private Button getUserDataButton;

    private NetworkTestController testController;
    private string currentPlatform;

    private void Start()
    {
        testController = NetworkTestController.Instance;
        if (testController == null)
        {
            Debug.LogError("[TestUI] NetworkTestController instance not found!");
            enabled = false;
            return;
        }

        // Cache platform identifier (won't change during runtime)
        currentPlatform = GetPlatformIdentifier();

        // Setup button listeners
        testHttpsButton.onClick.AddListener(OnTestHttpsClicked);
        testWebSocketButton.onClick.AddListener(OnTestWebSocketClicked);
        runAllTestsButton.onClick.AddListener(OnRunAllTestsClicked);

        // Setup button listeners
        registerButton.onClick.AddListener(Register);
        loginButton.onClick.AddListener(Login);
        getUserDataButton.onClick.AddListener(GetUserData);

        // Display environment and platform info
        UpdateEnvironmentDisplay();
        UpdatePlatformDisplay();
    }
    #region < ________________________________________ Register Login Get User Data ________________________________________  > 

    async private void Register()
    {
        var res = await AuthManager.Instance.RegisterAsync(inTx_UserName.text ?? "Siyamak", inTx_Email.text, inTx_Password.text, "0", "siya111111");
        statusText.text = $"✅ res : {res.AuthResponse.user.username}!";
        statusText.color = Color.green;
    }

    async private void Login()
    {
        var res = await AuthManager.Instance.LoginAsync(inTx_UserName.text, inTx_Password.text);
        statusText.text = $"✅ res : {res.AuthResponse.user.email}!";
        statusText.color = Color.green;
    }

    async private void GetUserData()
    {
        var profileRes = await AuthManager.Instance.FetchProfileAsync();

        if (profileRes.IsSuccess)
            statusText.text = $"✅ Email: {profileRes.Response.user.email}";
        else
            statusText.text = $"❌ {profileRes.ErrorMessage}";
    }
    #endregion
    private void OnTestHttpsClicked()
    {
        StartCoroutine(RunTestWithUI(
            testController.TestHTTPSConnection(),
            $"Testing HTTPS on {currentPlatform}..."
        ));
    }

    private void OnTestWebSocketClicked()
    {
        StartCoroutine(RunTestWithUI(
            testController.TestWebSocketConnection(),
            $"Validating WebSocket on {currentPlatform}..."
        ));
    }

    private void OnRunAllTestsClicked()
    {
        StartCoroutine(RunTestWithUI(
            testController.RunFullTest(),
            $"Running full test on {currentPlatform}..."
        ));
    }

    private IEnumerator RunTestWithUI(IEnumerator testRoutine, string loadingMessage)
    {
        // Disable buttons during test
        SetButtonsInteractable(false);
        statusText.text = loadingMessage;
        statusText.color = Color.yellow;

        yield return testRoutine;

        // Update results display
        UpdateStatusDisplay();
        SetButtonsInteractable(true);
    }

    private void UpdateStatusDisplay()
    {
        if (testController.isHttpsWorking && testController.isWebSocketWorking)
        {
            statusText.text = $"✅ All tests passed on {currentPlatform}!";
            statusText.color = Color.green;
        }
        else if (!string.IsNullOrEmpty(testController.lastErrorMessage))
        {
            statusText.text = $"❌ Error on {currentPlatform}: {testController.lastErrorMessage}";
            statusText.color = Color.red;
        }
        else
        {
            statusText.text = $"⚠️ Some tests failed on {currentPlatform}";
            statusText.color = Color.yellow;
        }

        latencyText.text = $"Latency: {testController.latencyMs:F2}ms";
    }

    private void UpdateEnvironmentDisplay()
    {
        environmentText.text = $"Environment: {EnvironmentConfig.Instance.GetCurrentEnvironment()}";
    }

    private void UpdatePlatformDisplay()
    {
        // Visual indicator based on platform type
        string platformIcon = "";
        Color platformColor = Color.white;

        switch (currentPlatform)
        {
            case "WebGL":
                platformIcon = "🌐";
                platformColor = new Color(0.2f, 0.6f, 1.0f); // Blue
                break;
            case "MetaQuest":
                platformIcon = "👓";
                platformColor = new Color(0.8f, 0.2f, 0.8f); // Purple
                break;
            case "Android":
                platformIcon = "📱";
                platformColor = new Color(0.3f, 0.8f, 0.3f); // Green
                break;
            case "WindowsEXE":
                platformIcon = "💻";
                platformColor = new Color(0.2f, 0.5f, 0.9f); // Dark Blue
                break;
            default:
                platformIcon = "❓";
                platformColor = Color.yellow;
                break;
        }

        platformText.text = $"{platformIcon} Platform: {currentPlatform}";
        platformText.color = platformColor;
    }

    private void SetButtonsInteractable(bool interactable)
    {
        testHttpsButton.interactable = interactable;
        testWebSocketButton.interactable = interactable;
        runAllTestsButton.interactable = interactable;
    }

    /// <summary>
    /// Identify current platform (matches NetworkTestController logic)
    /// </summary>
    private string GetPlatformIdentifier()
    {
#if UNITY_WEBGL
        return "WebGL";
#elif UNITY_ANDROID && !UNITY_EDITOR
        if (SystemInfo.deviceModel.Contains("Quest"))
            return "MetaQuest";
        else
            return "Android";
#elif UNITY_STANDALONE_WIN
        return "WindowsEXE";
#else
        return "Unknown";
#endif
    }
}