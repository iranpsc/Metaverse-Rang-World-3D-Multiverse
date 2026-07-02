using System;
using System.Collections;
using System.Reflection;
using Network_A.Auth;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedConnectAfterUsernameLoginWrapper : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedGameServerAutoConnectController autoConnectController;

        [Header("Login UI")]
        [SerializeField] private Button loginButton;
        [SerializeField] private Button registerButton;

        [Header("Username Inputs")]
        [SerializeField] private UnityEngine.Object loginUserNameInput;
        [SerializeField] private UnityEngine.Object registerUserNameInput;

        [Header("Optional Logged User Check")]
        [SerializeField] private UnityEngine.Object loggedUserNameText;
        [SerializeField] private bool requireLoggedUserNameMatch = false;

        [Header("Timing")]
        [SerializeField] private bool runAfterLoginClick = true;
        [SerializeField] private bool runAfterRegisterClick = true;
        [SerializeField] private float firstCheckDelaySeconds = 0.5f;
        [SerializeField] private float maxWaitSeconds = 60f;
        [SerializeField] private float pollIntervalSeconds = 0.25f;
        [SerializeField] private float stableTokenDelaySeconds = 1.0f;

        [Header("Safety")]
        [SerializeField] private bool runOnlyOnce = true;
        [SerializeField] private bool disconnectBeforeNewUserConnect = true;
        [SerializeField] private bool ignoreEmptyUserName = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logTokenChecks = false;

        private Coroutine waitRoutine;
        private bool hasStartedDedicatedConnect;
        private string expectedUserName;
        private string tokenAtClick;

        //* این تابع رفرنس ها را پیدا می کند و به دکمه های لاگین و رجیستر وصل می شود.
        private void Awake()
        {
            EnsureReferences();
            BindButtons();

            Log("Wrapper ready.");
        }

        //* این تابع رفرنس اتو کانکت را از همین آبجکت یا صحنه پیدا می کند.
        private void EnsureReferences()
        {
            if (autoConnectController == null)
            {
                autoConnectController = GetComponent<DedicatedGameServerAutoConnectController>();
            }

            if (autoConnectController == null)
            {
                autoConnectController = FindObjectOfType<DedicatedGameServerAutoConnectController>(true);
            }
        }

        //* این تابع لیسنرهای دکمه لاگین و رجیستر را بدون تغییر فایل های قبلی اضافه می کند.
        private void BindButtons()
        {
            if (loginButton != null)
            {
                loginButton.onClick.RemoveListener(OnLoginClicked);
                loginButton.onClick.AddListener(OnLoginClicked);
            }

            if (registerButton != null)
            {
                registerButton.onClick.RemoveListener(OnRegisterClicked);
                registerButton.onClick.AddListener(OnRegisterClicked);
            }
        }

        //* این تابع بعد از کلیک لاگین، نام کاربر همان کلاینت را می گیرد و منتظر توکن تازه می ماند.
        private void OnLoginClicked()
        {
            if (!runAfterLoginClick) return;

            string nameFromInput = ReadTextFromObject(loginUserNameInput);
            StartWaitForThisUser("login_click", nameFromInput);
        }

        //* این تابع بعد از کلیک رجیستر، نام کاربر همان کلاینت را می گیرد و منتظر توکن تازه می ماند.
        private void OnRegisterClicked()
        {
            if (!runAfterRegisterClick) return;

            string nameFromInput = ReadTextFromObject(registerUserNameInput);
            StartWaitForThisUser("register_click", nameFromInput);
        }

        //* این تابع از کانتکست منو برای تست دستی پس از لاگین استفاده می شود.
        [ContextMenu("Start Dedicated After Current Username")]
        public void Btn_StartDedicatedAfterCurrentUsername()
        {
            string nameFromLogin = ReadTextFromObject(loginUserNameInput);
            string nameFromRegister = ReadTextFromObject(registerUserNameInput);
            string selectedName = !string.IsNullOrWhiteSpace(nameFromLogin) ? nameFromLogin : nameFromRegister;

            StartWaitForThisUser("manual_context_menu", selectedName);
        }

        //* این تابع روند انتظار برای لاگین واقعی همین نام کاربر را شروع می کند.
        public void StartWaitForThisUser(string reason, string userName)
        {
            EnsureReferences();

            if (runOnlyOnce && hasStartedDedicatedConnect)
            {
                Log("Ignored because dedicated connect already started. reason=" + reason);
                return;
            }

            expectedUserName = NormalizeUserName(userName);

            if (ignoreEmptyUserName && string.IsNullOrWhiteSpace(expectedUserName))
            {
                Debug.LogWarning("[DedicatedConnectAfterUsernameLoginWrapper] Username is empty. Dedicated connect wait was not started. reason=" + reason);
                return;
            }

            tokenAtClick = SafeToken();

            if (waitRoutine != null)
            {
                StopCoroutine(waitRoutine);
                waitRoutine = null;
            }

            waitRoutine = StartCoroutine(WaitForFreshLoginThenConnect(reason));

            Log("Wait started | reason=" + reason + " | expectedUserName=" + expectedUserName +
                " | tokenAtClickHash=" + HashForLog(tokenAtClick));
        }

        //* این تابع منتظر می ماند تا بعد از کلیک کاربر، توکن تازه و در صورت نیاز نام کاربر درست آماده شود.
        private IEnumerator WaitForFreshLoginThenConnect(string reason)
        {
            if (firstCheckDelaySeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(firstCheckDelaySeconds);
            }

            float startedAt = Time.realtimeSinceStartup;
            string firstFreshToken = string.Empty;
            float freshTokenSeenAt = -1f;

            while (Time.realtimeSinceStartup - startedAt <= maxWaitSeconds)
            {
                string currentToken = SafeToken();
                bool hasToken = !string.IsNullOrWhiteSpace(currentToken);
                bool tokenChanged = hasToken && !string.Equals(currentToken, tokenAtClick, StringComparison.Ordinal);

                if (logTokenChecks)
                {
                    Log("Token check | hasToken=" + hasToken +
                        " | tokenChanged=" + tokenChanged +
                        " | currentHash=" + HashForLog(currentToken));
                }

                if (tokenChanged)
                {
                    if (string.IsNullOrWhiteSpace(firstFreshToken))
                    {
                        firstFreshToken = currentToken;
                        freshTokenSeenAt = Time.realtimeSinceStartup;

                        Log("Fresh token detected | expectedUserName=" + expectedUserName +
                            " | tokenHash=" + HashForLog(currentToken));
                    }

                    bool tokenStable = Time.realtimeSinceStartup - freshTokenSeenAt >= stableTokenDelaySeconds;
                    bool userNameOk = IsLoggedUserNameReady();

                    if (tokenStable && userNameOk)
                    {
                        yield return StartCoroutine(RunDedicatedConnect(reason));
                        yield break;
                    }
                }

                yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, pollIntervalSeconds));
            }

            Debug.LogError("[DedicatedConnectAfterUsernameLoginWrapper] Timeout waiting for fresh login. expectedUserName=" +
                           expectedUserName + " | reason=" + reason);
        }

        //* این تابع اگر لازم باشد نام کاربر لاگین شده را با نام کاربر کلیک شده تطبیق می دهد.
        private bool IsLoggedUserNameReady()
        {
            if (!requireLoggedUserNameMatch) return true;

            string loggedName = NormalizeUserName(ReadTextFromObject(loggedUserNameText));

            if (string.IsNullOrWhiteSpace(loggedName))
            {
                return false;
            }

            bool matched =
                string.Equals(loggedName, expectedUserName, StringComparison.OrdinalIgnoreCase) ||
                loggedName.IndexOf(expectedUserName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                expectedUserName.IndexOf(loggedName, StringComparison.OrdinalIgnoreCase) >= 0;

            if (!matched)
            {
                Log("Logged username not matched yet | expected=" + expectedUserName + " | logged=" + loggedName);
            }

            return matched;
        }

        //* این تابع اتو کانکت ددیکیتد را فقط بعد از لاگین همان نام کاربر اجرا می کند.
        private IEnumerator RunDedicatedConnect(string reason)
        {
            if (autoConnectController == null)
            {
                Debug.LogError("[DedicatedConnectAfterUsernameLoginWrapper] DedicatedGameServerAutoConnectController is missing.");
                yield break;
            }

            if (runOnlyOnce && hasStartedDedicatedConnect)
            {
                yield break;
            }

            if (disconnectBeforeNewUserConnect)
            {
                DedicatedGameServerWsClient wsClient = GetComponent<DedicatedGameServerWsClient>();

                if (wsClient == null)
                {
                    wsClient = DedicatedGameServerWsClient.Instance;
                }

                if (wsClient != null && wsClient.IsConnected)
                {
                    wsClient.Disconnect("wrapper_new_user_login");
                    yield return new WaitForSecondsRealtime(0.25f);
                }
            }

            hasStartedDedicatedConnect = true;

            Debug.Log("[DedicatedConnectAfterUsernameLoginWrapper] Starting dedicated auto connect | expectedUserName=" +
                      expectedUserName + " | reason=" + reason);

            var task = autoConnectController.RunAutoTicketConnectAndAuthAsync();

            while (task != null && !task.IsCompleted)
            {
                yield return null;
            }

            if (task != null && task.IsFaulted)
            {
                Debug.LogError("[DedicatedConnectAfterUsernameLoginWrapper] Dedicated auto connect task failed | " + task.Exception);
                yield break;
            }

            bool result = task != null && task.Result;

            Debug.Log("[DedicatedConnectAfterUsernameLoginWrapper] Dedicated auto connect finished | result=" +
                      result + " | expectedUserName=" + expectedUserName);
        }

        //* این تابع متن را از آبجکت های InputField، TMP_InputField، Text و TMP_Text با رفلکشن می خواند.
        private string ReadTextFromObject(UnityEngine.Object source)
        {
            if (source == null) return string.Empty;

            try
            {
                Type type = source.GetType();

                PropertyInfo textProperty = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (textProperty != null && textProperty.PropertyType == typeof(string))
                {
                    return (string)textProperty.GetValue(source, null);
                }

                FieldInfo textField = type.GetField("text", BindingFlags.Instance | BindingFlags.Public);
                if (textField != null && textField.FieldType == typeof(string))
                {
                    return (string)textField.GetValue(source);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectAfterUsernameLoginWrapper] Could not read text from object | type=" +
                                 source.GetType().Name + " | error=" + ex.Message);
            }

            return string.Empty;
        }

        //* این تابع نام کاربر را برای مقایسه ساده و امن آماده می کند.
        private string NormalizeUserName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            return value.Trim();
        }

        //* این تابع اکسس توکن را بدون پرتاب خطا می خواند.
        private string SafeToken()
        {
            try
            {
                return SecureTokenStorage.GetAccessToken();
            }
            catch
            {
                return string.Empty;
            }
        }

        //* این تابع برای لاگ، هش کوتاه توکن را برمی گرداند و خود توکن را چاپ نمی کند.
        private string HashForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "empty";

            unchecked
            {
                int hash = 17;

                for (int i = 0; i < value.Length; i++)
                {
                    hash = hash * 31 + value[i];
                }

                return hash.ToString("X8");
            }
        }

        //* این تابع لیسنرها را هنگام حذف آبجکت پاک می کند.
        private void OnDestroy()
        {
            if (loginButton != null)
            {
                loginButton.onClick.RemoveListener(OnLoginClicked);
            }

            if (registerButton != null)
            {
                registerButton.onClick.RemoveListener(OnRegisterClicked);
            }
        }

        //* این تابع لاگ های معمولی رپر را چاپ می کند.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedConnectAfterUsernameLoginWrapper] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این فایل فقط یک رپر است و هیچ فایل قبلی را تغییر نمی دهد.
        مشکل دو بیلد ویندوز این بود که اتو کانکت ددیکیتد روی استارت زود اجرا می شد و با توکن قبلی وارد می شد.
        این رپر به دکمه لاگین و رجیستر وصل می شود و نام کاربر همان کلاینت را از اینپوت می خواند.
        بعد منتظر می ماند تا اکسس توکن بعد از همان کلیک تازه شود.
        سپس DedicatedGameServerAutoConnectController را اجرا می کند.
        Product Name لازم نیست تغییر کند و SecureTokenStorage هم دست نخورده می ماند.
        */
    }
}
