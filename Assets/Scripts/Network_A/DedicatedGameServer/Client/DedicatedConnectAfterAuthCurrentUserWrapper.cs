using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.DedicatedGameServer.Bootstrap;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    [DefaultExecutionOrder(-9000)]
    public class DedicatedConnectAfterAuthCurrentUserWrapper : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedGameServerAutoConnectController autoConnectController;
        [SerializeField] private DedicatedGameServerWsClient wsClient;

        [Header("Role Guard")]
        [SerializeField] private bool runOnlyInClientRole = true;

        [Header("Auto Login Watch")]
        [SerializeField] private bool autoStartMonitoring = true;
        [SerializeField] private float initialDelaySeconds = 0.5f;
        [SerializeField] private float pollIntervalSeconds = 0.25f;
        [SerializeField] private float stableUserDelaySeconds = 1.0f;
        [SerializeField] private bool requireAccessToken = true;

        [Header("Reconnect Rules")]
        [SerializeField] private bool reconnectWhenCurrentUserChanges = true;
        [SerializeField] private bool disconnectBeforeReconnect = true;
        [SerializeField] private bool runOnlyOncePerUser = true;

        [Header("AutoConnect Safety")]
        [SerializeField] private bool forceAutoRunOnStartOff = true;
        [SerializeField] private bool forceRetrySettingsOn = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logPolling = false;

        private Coroutine monitorRoutine;
        private bool connectInProgress;
        private string lastConnectedUserKey;
        private string pendingUserKey;
        private float pendingUserStartedAt;

        //* این تابع زودتر از استارت بقیه اجرا می شود و جلوی اجرای زودهنگام اتوکانکت را می گیرد.
        private void Awake()
        {
            EnsureReferences();
            ApplyAutoConnectSafety();

            Log("Wrapper awake | roleAllowed=" + IsRoleAllowed());
        }

        //* این تابع مانیتور لاگین اتوماتیک را شروع می کند.
        private void Start()
        {
            if (!autoStartMonitoring) return;

            StartMonitoring();
        }

        //* این تابع از کانتکست منو مانیتور را روشن می کند.
        [ContextMenu("Start Auth CurrentUser Monitor")]
        public void StartMonitoring()
        {
            if (!IsRoleAllowed())
            {
                Log("Monitor not started because runtime role is not client role.");
                return;
            }

            if (monitorRoutine != null)
            {
                StopCoroutine(monitorRoutine);
            }

            monitorRoutine = StartCoroutine(MonitorAuthCurrentUserRoutine());

            Log("Monitor started.");
        }

        //* این تابع از کانتکست منو مانیتور را خاموش می کند.
        [ContextMenu("Stop Auth CurrentUser Monitor")]
        public void StopMonitoring()
        {
            if (monitorRoutine != null)
            {
                StopCoroutine(monitorRoutine);
                monitorRoutine = null;
            }

            Log("Monitor stopped.");
        }

        //* این تابع رفرنس های لازم را پیدا می کند.
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

            if (wsClient == null)
            {
                wsClient = GetComponent<DedicatedGameServerWsClient>();
            }

            if (wsClient == null)
            {
                wsClient = DedicatedGameServerWsClient.Instance;
            }

            if (wsClient == null)
            {
                wsClient = FindObjectOfType<DedicatedGameServerWsClient>(true);
            }
        }

        //* این تابع با رفلکشن تنظیمات خطرناک اجرای زودهنگام را خاموش می کند.
        private void ApplyAutoConnectSafety()
        {
            if (autoConnectController == null) return;

            if (forceAutoRunOnStartOff)
            {
                SetPrivateField(autoConnectController, "autoRunOnStart", false);
                Log("AutoConnect autoRunOnStart forced OFF by wrapper.");
            }

            if (forceRetrySettingsOn)
            {
                SetPrivateField(autoConnectController, "waitForAccessToken", true);
                SetPrivateField(autoConnectController, "waitForAccessTokenSeconds", 60f);
                SetPrivateField(autoConnectController, "retryUntilAuthenticated", true);
                SetPrivateField(autoConnectController, "maxAutoFlowSeconds", 90f);
                SetPrivateField(autoConnectController, "retryDelaySeconds", 2f);
                SetPrivateField(autoConnectController, "maxTicketAttempts", 30);
                SetPrivateField(autoConnectController, "retryOnUnauthorizedTicket", true);

                Log("AutoConnect retry settings forced ON by wrapper.");
            }
        }

        //* این تابع فقط در نقش کلاینت اجازه اجرای ددیکیتد کانکت را می دهد.
        private bool IsRoleAllowed()
        {
            if (!runOnlyInClientRole) return true;

            DedicatedRuntimeRoleSwitcher switcher = FindObjectOfType<DedicatedRuntimeRoleSwitcher>(true);

            if (switcher == null)
            {
                return true;
            }

            string roleName = ReadRuntimeRoleName(switcher);

            if (string.IsNullOrWhiteSpace(roleName))
            {
                return true;
            }

            return roleName == "ClientOnly" || roleName == "ServerAndClientEditorTest";
        }

        //* این تابع نام نقش فعلی RoleSwitcher را با رفلکشن می خواند.
        private string ReadRuntimeRoleName(DedicatedRuntimeRoleSwitcher switcher)
        {
            if (switcher == null) return string.Empty;

            FieldInfo field = switcher.GetType().GetField(
                "runtimeRole",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null) return string.Empty;

            object value = field.GetValue(switcher);
            return value != null ? value.ToString() : string.Empty;
        }

        //* این تابع CurrentUser آث منیجر را مانیتور می کند و بعد از آماده شدن، ددیکیتد کانکت را اجرا می کند.
        private IEnumerator MonitorAuthCurrentUserRoutine()
        {
            if (initialDelaySeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(initialDelaySeconds);
            }

            while (true)
            {
                if (!IsRoleAllowed())
                {
                    if (logPolling) Log("Polling skipped. role is not client role.");
                    yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, pollIntervalSeconds));
                    continue;
                }

                AuthUserSnapshot snapshot = ReadAuthUserSnapshot();

                if (!snapshot.IsReady)
                {
                    if (logPolling) Log("CurrentUser not ready yet.");
                    ResetPendingUser();
                    yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, pollIntervalSeconds));
                    continue;
                }

                if (requireAccessToken && string.IsNullOrWhiteSpace(SafeAccessToken()))
                {
                    if (logPolling) Log("Access token not ready yet for userKey=" + snapshot.UserKey);
                    ResetPendingUser();
                    yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, pollIntervalSeconds));
                    continue;
                }

                if (ShouldIgnoreBecauseAlreadyConnected(snapshot.UserKey))
                {
                    yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, pollIntervalSeconds));
                    continue;
                }

                if (pendingUserKey != snapshot.UserKey)
                {
                    pendingUserKey = snapshot.UserKey;
                    pendingUserStartedAt = Time.realtimeSinceStartup;

                    Debug.Log("[DedicatedConnectAfterAuthCurrentUserWrapper] Auth user detected | userKey=" +
                              snapshot.UserKey + " | displayName=" + snapshot.DisplayName);
                }

                bool stable = Time.realtimeSinceStartup - pendingUserStartedAt >= stableUserDelaySeconds;

                if (stable && !connectInProgress)
                {
                    yield return StartCoroutine(RunDedicatedConnectForUser(snapshot));
                }

                yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, pollIntervalSeconds));
            }
        }

        //* این تابع تشخیص می دهد برای همین یوزر قبلاً ددیکیتد وصل شده یا نه.
        private bool ShouldIgnoreBecauseAlreadyConnected(string userKey)
        {
            if (string.IsNullOrWhiteSpace(userKey)) return true;

            if (runOnlyOncePerUser && string.Equals(lastConnectedUserKey, userKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (!reconnectWhenCurrentUserChanges &&
                !string.IsNullOrWhiteSpace(lastConnectedUserKey) &&
                !string.Equals(lastConnectedUserKey, userKey, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        //* این تابع ددیکیتد کانکت را برای CurrentUser آماده شده اجرا می کند.
        private IEnumerator RunDedicatedConnectForUser(AuthUserSnapshot snapshot)
        {
            EnsureReferences();
            ApplyAutoConnectSafety();

            if (autoConnectController == null)
            {
                Debug.LogError("[DedicatedConnectAfterAuthCurrentUserWrapper] DedicatedGameServerAutoConnectController is missing.");
                yield break;
            }

            connectInProgress = true;

            if (disconnectBeforeReconnect && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("auth_user_changed");
                yield return new WaitForSecondsRealtime(0.25f);
            }

            SetPrivateField(autoConnectController, "fallbackUserName", snapshot.DisplayName);

            Debug.Log("[DedicatedConnectAfterAuthCurrentUserWrapper] Starting dedicated auto connect | userKey=" +
                      snapshot.UserKey + " | displayName=" + snapshot.DisplayName);

            Task<bool> task = autoConnectController.RunAutoTicketConnectAndAuthAsync();

            while (task != null && !task.IsCompleted)
            {
                yield return null;
            }

            connectInProgress = false;

            if (task == null)
            {
                Debug.LogError("[DedicatedConnectAfterAuthCurrentUserWrapper] Dedicated auto connect task is null.");
                yield break;
            }

            if (task.IsFaulted)
            {
                Debug.LogError("[DedicatedConnectAfterAuthCurrentUserWrapper] Dedicated auto connect task failed | " + task.Exception);
                yield break;
            }

            bool ok = task.Result;

            if (ok)
            {
                lastConnectedUserKey = snapshot.UserKey;

                Debug.Log("[DedicatedConnectAfterAuthCurrentUserWrapper] Dedicated auto connect finished | result=True | userKey=" +
                          snapshot.UserKey + " | displayName=" + snapshot.DisplayName);
            }
            else
            {
                Debug.LogWarning("[DedicatedConnectAfterAuthCurrentUserWrapper] Dedicated auto connect finished | result=False | userKey=" +
                                 snapshot.UserKey + " | displayName=" + snapshot.DisplayName);
            }
        }

        //* این تابع CurrentUser را از AuthManager می خواند.
        private AuthUserSnapshot ReadAuthUserSnapshot()
        {
            AuthUserSnapshot snapshot = new AuthUserSnapshot();

            try
            {
                AuthManager authManager = AuthManager.Instance;

                if (authManager == null)
                {
                    return snapshot;
                }

                object currentUser = authManager.CurrentUser;

                if (currentUser == null)
                {
                    return snapshot;
                }

                string id = ReadStringMember(currentUser, "id");
                string emailOrUsername = ReadStringMember(currentUser, "emailOrUsername");

                snapshot.userId = id;
                snapshot.emailOrUsername = emailOrUsername;

                if (!string.IsNullOrWhiteSpace(id))
                {
                    snapshot.UserKey = id.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(emailOrUsername))
                {
                    snapshot.UserKey = emailOrUsername.Trim();
                }

                if (!string.IsNullOrWhiteSpace(emailOrUsername))
                {
                    snapshot.DisplayName = emailOrUsername.Trim();
                }
                else
                {
                    snapshot.DisplayName = snapshot.UserKey;
                }

                snapshot.IsReady = !string.IsNullOrWhiteSpace(snapshot.UserKey);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectAfterAuthCurrentUserWrapper] Could not read AuthManager.CurrentUser | " + ex.Message);
            }

            return snapshot;
        }

        //* این تابع رشته یک فیلد یا پراپرتی را از آبجکت می خواند.
        private string ReadStringMember(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName)) return string.Empty;

            Type type = target.GetType();

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(string))
            {
                return property.GetValue(target, null) as string;
            }

            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(string))
            {
                return field.GetValue(target) as string;
            }

            return string.Empty;
        }

        //* این تابع یک فیلد خصوصی را روی آبجکت مقصد مقداردهی می کند.
        private void SetPrivateField(object target, string fieldName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName)) return;

            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null) return;

            try
            {
                field.SetValue(target, value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectAfterAuthCurrentUserWrapper] Could not set field | field=" +
                                 fieldName + " | error=" + ex.Message);
            }
        }

        //* این تابع اکسس توکن را بدون خطا می خواند.
        private string SafeAccessToken()
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

        //* این تابع وضعیت یوزر منتظر را پاک می کند.
        private void ResetPendingUser()
        {
            pendingUserKey = string.Empty;
            pendingUserStartedAt = 0f;
        }

        //* این تابع هنگام حذف آبجکت مانیتور را خاموش می کند.
        private void OnDestroy()
        {
            StopMonitoring();
        }

        //* این تابع لاگ معمولی رپر را چاپ می کند.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedConnectAfterAuthCurrentUserWrapper] " + message);
        }

        private struct AuthUserSnapshot
        {
            public bool IsReady;
            public string UserKey;
            public string DisplayName;
            public string userId;
            public string emailOrUsername;
        }

        /*
        توضیح مکتوب فایل:
        این فایل فقط رپر است و هیچ فایل قبلی را تغییر نمی دهد.
        این نسخه مخصوص حالتی است که همه بیلدها Auto Login دارند.
        رپر به دکمه لاگین وابسته نیست و AuthManager.CurrentUser را مانیتور می کند.
        هر وقت CurrentUser آماده یا عوض شد، DedicatedGameServerAutoConnectController را اجرا می کند.
        همچنین با رفلکشن Auto Run On Start اتوکانکت را خاموش می کند تا اتصال زودتر از آث انجام نشود.
        */
    }
}
