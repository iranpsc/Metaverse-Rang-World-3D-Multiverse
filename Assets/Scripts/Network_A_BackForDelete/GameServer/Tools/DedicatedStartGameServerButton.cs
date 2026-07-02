using System;
using System.Reflection;
using Network_A.GameServer;
using Network_A.DedicatedGameServer.Bootstrap;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.GameServer.Tools
{
    public class DedicatedStartGameServerButton : MonoBehaviour
    {
        [Header("Button")]
        [SerializeField] private Button startButton;

        [Header("Server Root")]
        [SerializeField] private GameObject serverRuntimeRoot;

        [Header("Runtime")]
        [SerializeField] private DedicatedServerRuntime dedicatedServerRuntime;

        [Header("Role Guard")]
        [SerializeField] private DedicatedRuntimeRoleSwitcher roleSwitcher;
        [SerializeField] private bool blockStartWhenRoleSwitcherIsClientOnly = true;
        [SerializeField] private bool disableButtonWhenServerRoleNotAllowed = true;

        [Header("Rules")]
        [SerializeField] private bool activateRootOnClick = true;
        [SerializeField] private bool enableKnownServerComponents = true;
        [SerializeField] private bool forceStartDedicatedRuntimeAfterEnable = true;
        [SerializeField] private bool callKnownStartMethods = true;
        [SerializeField] private bool keepButtonDisabledAfterStart = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        private bool startRequested;

        private readonly string[] serverComponentNames =
        {
            "DedicatedServerConfig",
            "DedicatedServerRuntime",
            "DedicatedHeartbeatLoop",
            "GameServerControlDedicatedClient",
            "DedicatedWebSocketServer",
            "DedicatedTicketVerifier",
            "DedicatedTicketHandshakeHandler",
            "DedicatedPlayerRegistry",
            "DedicatedServerAuthPipelineAutoBinder",
            "DedicatedServerGameplayPipelineAutoBinder",
            "DedicatedPlayerStateStore",
            "DedicatedGameMessageRouter"
        };

        private readonly string[] startMethodNames =
        {
            "StartServer",
            "StartRuntime",
            "StartDedicatedServer",
            "StartDedicatedRuntime",
            "StartListening",
            "StartHeartbeat",
            "StartLoop",
            "StartAsync"
        };

        //* This function binds the server start button.
        private void Awake()
        {
            EnsureButtonReference();
            EnsureDedicatedRuntimeReference();
            EnsureRoleSwitcherReference();
            ApplyRoleButtonState();

            if (startButton != null)
            {
                startButton.onClick.RemoveListener(OnStartButtonClicked);
                startButton.onClick.AddListener(OnStartButtonClicked);
            }

            Log("Server start button ready.");
        }

        private void OnEnable()
        {
            EnsureButtonReference();
            EnsureRoleSwitcherReference();
            ApplyRoleButtonState();
        }

        //* This function is called by the UI button.
        private void OnStartButtonClicked()
        {
            StartGameServer();
        }

        //* This context menu starts the server manually.
        [ContextMenu("Start Game Server Now")]
        public void StartGameServer()
        {
            EnsureRoleSwitcherReference();

            if (!IsServerStartAllowed())
            {
                ApplyRoleButtonState();
                Debug.LogWarning("[DedicatedStartGameServerButton] Start blocked because runtime role is ClientOnly.");
                return;
            }

            if (startRequested)
            {
                Log("Start ignored because it was already requested.");
                return;
            }

            startRequested = true;

            if (startButton != null && keepButtonDisabledAfterStart)
            {
                startButton.interactable = false;
            }

            Debug.Log("[DedicatedStartGameServerButton] Manual server start requested.");

            if (serverRuntimeRoot != null && activateRootOnClick)
            {
                serverRuntimeRoot.SetActive(true);
                Debug.Log("[DedicatedStartGameServerButton] Server root activated | root=" + serverRuntimeRoot.name);
            }

            if (enableKnownServerComponents)
            {
                EnableKnownServerComponents();
            }

            if (forceStartDedicatedRuntimeAfterEnable)
            {
                StartDedicatedRuntimeDirect();
            }

            if (callKnownStartMethods)
            {
                CallKnownStartMethods();
            }

            Debug.Log("[DedicatedStartGameServerButton] Manual server start flow finished.");
        }

        //* This function enables known server components under the selected root.
        private void EnableKnownServerComponents()
        {
            GameObject root = serverRuntimeRoot != null ? serverRuntimeRoot : gameObject;

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);

            int enabledCount = 0;

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;

                string typeName = behaviour.GetType().Name;

                if (!ContainsName(serverComponentNames, typeName)) continue;

                behaviour.enabled = true;
                enabledCount++;

                Log("Enabled server component | " + typeName);
            }

            Debug.Log("[DedicatedStartGameServerButton] Server components enabled | count=" + enabledCount);
        }

        //* This function starts DedicatedServerRuntime directly after all server components are enabled.
        private void StartDedicatedRuntimeDirect()
        {
            EnsureDedicatedRuntimeReference();

            if (dedicatedServerRuntime == null)
            {
                Debug.LogError("[DedicatedStartGameServerButton] DedicatedServerRuntime is missing. Runtime direct start failed.");
                return;
            }

            if (dedicatedServerRuntime.IsRunning)
            {
                Log("DedicatedServerRuntime is already running.");
                return;
            }

            Debug.Log("[DedicatedStartGameServerButton] Direct DedicatedServerRuntime start requested.");
            dedicatedServerRuntime.StartDedicatedRuntime();

            if (dedicatedServerRuntime.IsRunning)
            {
                Debug.Log("[DedicatedStartGameServerButton] Direct DedicatedServerRuntime start confirmed.");
            }
            else
            {
                Debug.LogWarning("[DedicatedStartGameServerButton] Direct DedicatedServerRuntime start finished but runtime is not running. Check DedicatedServerRuntime errors above.");
            }
        }

        //* This function tries to call known public or private start methods on server components.
        private void CallKnownStartMethods()
        {
            GameObject root = serverRuntimeRoot != null ? serverRuntimeRoot : gameObject;

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);

            int calledCount = 0;

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;

                string typeName = behaviour.GetType().Name;

                if (!ContainsName(serverComponentNames, typeName)) continue;

                if (TryCallAnyStartMethod(behaviour))
                {
                    calledCount++;
                }
            }

            Debug.Log("[DedicatedStartGameServerButton] Start methods called | count=" + calledCount);
        }

        //* This function calls the first matching start method that does not need parameters.
        private bool TryCallAnyStartMethod(MonoBehaviour behaviour)
        {
            Type type = behaviour.GetType();

            for (int i = 0; i < startMethodNames.Length; i++)
            {
                MethodInfo method = type.GetMethod(
                    startMethodNames[i],
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (method == null) continue;

                ParameterInfo[] parameters = method.GetParameters();

                if (parameters.Length != 0) continue;

                try
                {
                    method.Invoke(behaviour, null);
                    Debug.Log("[DedicatedStartGameServerButton] Called " + type.Name + "." + method.Name + "()");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[DedicatedStartGameServerButton] Start method failed | " +
                                     type.Name + "." + method.Name + " | " + ex.Message);
                    return false;
                }
            }

            return false;
        }

        //* This function finds the UI button reference.
        private void EnsureButtonReference()
        {
            if (startButton != null) return;
            startButton = GetComponent<Button>();
        }

        private void ApplyRoleButtonState()
        {
            if (startButton == null) return;
            if (!disableButtonWhenServerRoleNotAllowed) return;
            if (IsServerStartAllowed()) return;

            startButton.interactable = false;
            Log("Start button disabled because runtime role is ClientOnly.");
        }

        private bool IsServerStartAllowed()
        {
            if (!blockStartWhenRoleSwitcherIsClientOnly) return true;
            if (roleSwitcher == null) return true;
            return roleSwitcher.IsServerRoleAllowed;
        }

        private void EnsureRoleSwitcherReference()
        {
            if (roleSwitcher != null) return;
            roleSwitcher = FindObjectOfType<DedicatedRuntimeRoleSwitcher>(true);
        }

        //* This function finds the dedicated runtime reference from the selected root or scene.
        private void EnsureDedicatedRuntimeReference()
        {
            if (dedicatedServerRuntime != null) return;

            if (serverRuntimeRoot != null)
            {
                dedicatedServerRuntime = serverRuntimeRoot.GetComponentInChildren<DedicatedServerRuntime>(true);
                if (dedicatedServerRuntime != null) return;
            }

            dedicatedServerRuntime = GetComponent<DedicatedServerRuntime>();
            if (dedicatedServerRuntime != null) return;

            dedicatedServerRuntime = GetComponentInParent<DedicatedServerRuntime>(true);
            if (dedicatedServerRuntime != null) return;

            dedicatedServerRuntime = DedicatedServerRuntime.Instance;
            if (dedicatedServerRuntime != null) return;

            dedicatedServerRuntime = FindObjectOfType<DedicatedServerRuntime>(true);
        }

        //* This function checks component names.
        private bool ContainsName(string[] names, string target)
        {
            if (names == null || string.IsNullOrWhiteSpace(target)) return false;

            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], target, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        //* This function removes the button listener.
        private void OnDestroy()
        {
            if (startButton != null)
            {
                startButton.onClick.RemoveListener(OnStartButtonClicked);
            }
        }

        //* This function prints wrapper logs.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedStartGameServerButton] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این فایل فقط رپر دکمه شروع سرور است و هیچ فایل قبلی را تغییر نمی دهد.
        پیشنهاد اصلی این است که آبجکت Unity_Dedicated_Server_Runtime در شروع صحنه غیرفعال باشد.
        کاربر یا تستر دکمه Start Game Server را می زند و این رپر همان آبجکت را فعال می کند.
        اگر کامپوننت های سرور خاموش باشند، این رپر آن ها را روشن می کند.
        در این نسخه بعد از روشن شدن کامپوننت ها، ران تایم ددیکیتد مستقیم استارت می شود.
        */
    }
}
