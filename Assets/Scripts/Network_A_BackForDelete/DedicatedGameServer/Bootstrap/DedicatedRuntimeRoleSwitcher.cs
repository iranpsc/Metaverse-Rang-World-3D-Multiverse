using System;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Bootstrap
{
    public enum DedicatedRuntimeRole
    {
        ServerOnly = 0,
        ClientOnly = 1,
        ServerAndClientEditorTest = 2
    }

    public class DedicatedRuntimeRoleSwitcher : MonoBehaviour
    {
        [Header("Role")]
        [SerializeField] private DedicatedRuntimeRole runtimeRole = DedicatedRuntimeRole.ServerOnly;

        [Header("Apply Time")]
        [SerializeField] private bool applyInAwake = true;
        [SerializeField] private bool applyInStart = false;

        [Header("Root Objects")]
        [SerializeField] private bool applyRootObjects = true;
        [SerializeField] private GameObject[] serverRoots;
        [SerializeField] private GameObject[] clientRoots;

        [Header("Component Rules")]
        [SerializeField] private bool applyComponentRules = true;
        [SerializeField] private bool includeInactiveObjects = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

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
            "DedicatedGameMessageRouter",
            "DedicatedServerStartGateAndRoomBinder",
            "DedicatedStartGameServerButton"
        };

        private readonly string[] clientComponentNames =
        {
            "GameServerTicketClient",
            "DedicatedGameServerWsClient",
            "DedicatedGameTicketClient",
            "DedicatedGameServerAutoConnectController",
            "DedicatedGameServerManualTicketTestController",
            "DedicatedPlayerStateAutoSender",
            "DedicatedRemotePlayerStateReceiver",
            "DedicatedClientTwoUserAutoFlowController",
            "DedicatedRemotePlayerViewController",
            "DedicatedConnectGameServerButton",
            "DedicatedConnectGameServerFromRealtimeRoomButton",
            "DedicatedConnectAfterAuthCurrentUserWrapper",
            "DedicatedConnectAfterUsernameLoginWrapper",
            "DedicatedConnectGameServerAfterLatestLoginButton",
            "DedicatedGameServerManualTicketTestController"
        };

        public DedicatedRuntimeRole CurrentRole => runtimeRole;
        public bool IsServerRoleAllowed => runtimeRole == DedicatedRuntimeRole.ServerOnly || runtimeRole == DedicatedRuntimeRole.ServerAndClientEditorTest;
        public bool IsClientRoleAllowed => runtimeRole == DedicatedRuntimeRole.ClientOnly || runtimeRole == DedicatedRuntimeRole.ServerAndClientEditorTest;
        public bool IsClientOnlyRole => runtimeRole == DedicatedRuntimeRole.ClientOnly;
        public bool IsServerOnlyRole => runtimeRole == DedicatedRuntimeRole.ServerOnly;

        //* این تابع در اویک نقش اجرایی را اعمال می کند تا قبل از استارت، کامپوننت های اشتباه خاموش شوند.
        private void Awake()
        {
            if (!applyInAwake) return;

            ApplyRole();
        }

        //* این تابع در استارت نقش اجرایی را دوباره اعمال می کند، اگر ترتیب اجرای یونیتی نیاز داشت.
        private void Start()
        {
            if (!applyInStart) return;

            ApplyRole();
        }

        //* این تابع از اینسپکتور نقش فعلی را اعمال می کند.
        [ContextMenu("Apply Current Role")]
        public void ApplyRole()
        {
            bool serverEnabled = runtimeRole == DedicatedRuntimeRole.ServerOnly ||
                                 runtimeRole == DedicatedRuntimeRole.ServerAndClientEditorTest;

            bool clientEnabled = runtimeRole == DedicatedRuntimeRole.ClientOnly ||
                                 runtimeRole == DedicatedRuntimeRole.ServerAndClientEditorTest;

            if (applyRootObjects)
            {
                ApplyRootArray(serverRoots, serverEnabled, "serverRoots");
                ApplyRootArray(clientRoots, clientEnabled, "clientRoots");
            }

            if (applyComponentRules)
            {
                ApplyComponentRules(serverEnabled, clientEnabled);
            }

            Debug.Log("[DedicatedRuntimeRoleSwitcher] Role applied | role=" +
                      runtimeRole + " | serverEnabled=" + serverEnabled +
                      " | clientEnabled=" + clientEnabled);
        }

        //* این تابع از اینسپکتور نقش سرور تنها را انتخاب و اعمال می کند.
        [ContextMenu("Set Role Server Only")]
        public void SetRoleServerOnly()
        {
            runtimeRole = DedicatedRuntimeRole.ServerOnly;
            ApplyRole();
        }

        //* این تابع از اینسپکتور نقش کلاینت تنها را انتخاب و اعمال می کند.
        [ContextMenu("Set Role Client Only")]
        public void SetRoleClientOnly()
        {
            runtimeRole = DedicatedRuntimeRole.ClientOnly;
            ApplyRole();
        }

        //* این تابع از اینسپکتور نقش تست ادیتور سرور و کلاینت را انتخاب و اعمال می کند.
        [ContextMenu("Set Role Server And Client Editor Test")]
        public void SetRoleServerAndClientEditorTest()
        {
            runtimeRole = DedicatedRuntimeRole.ServerAndClientEditorTest;
            ApplyRole();
        }

        //* این تابع آبجکت های روت سرور یا کلاینت را فعال یا غیرفعال می کند.
        private void ApplyRootArray(GameObject[] roots, bool active, string groupName)
        {
            if (roots == null) return;

            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null) continue;

                if (root == gameObject)
                {
                    Debug.LogWarning("[DedicatedRuntimeRoleSwitcher] Switcher object cannot disable itself | group=" + groupName);
                    continue;
                }

                root.SetActive(active);

                Log("Root applied | group=" + groupName + " | name=" + root.name + " | active=" + active);
            }
        }

        //* این تابع کامپوننت های سرور و کلاینت را بر اساس نقش انتخاب شده روشن یا خاموش می کند.
        private void ApplyComponentRules(bool serverEnabled, bool clientEnabled)
        {
            MonoBehaviour[] behaviours = FindRuntimeBehaviours();

            int serverCount = 0;
            int clientCount = 0;

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || behaviour == this) continue;
                if (behaviour.gameObject == null) continue;
                if (!behaviour.gameObject.scene.IsValid()) continue;

                string componentName = behaviour.GetType().Name;

                if (ContainsName(serverComponentNames, componentName))
                {
                    behaviour.enabled = serverEnabled;
                    serverCount++;
                    continue;
                }

                if (ContainsName(clientComponentNames, componentName))
                {
                    behaviour.enabled = clientEnabled;
                    clientCount++;
                }
            }

            Log("Component rules applied | serverComponents=" + serverCount +
                " | clientComponents=" + clientCount);
        }

        //* این تابع مونوبیهیویرهای صحنه را پیدا می کند.
        private MonoBehaviour[] FindRuntimeBehaviours()
        {
#if UNITY_2020_1_OR_NEWER
            return FindObjectsOfType<MonoBehaviour>(includeInactiveObjects);
#else
            return FindObjectsOfType<MonoBehaviour>();
#endif
        }

        //* این تابع بررسی می کند نام کامپوننت در لیست مورد نظر وجود دارد یا نه.
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

        //* این تابع لاگ معمولی سوییچر نقش را چاپ می کند.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedRuntimeRoleSwitcher] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت برای جلوگیری از قاطی شدن نقش سرور و کلاینت اضافه شده است.
        روی یک آبجکت جدا مثل Dedicated_Runtime_Role_Switcher قرار می گیرد.
        اگر نقش ServerOnly باشد، کامپوننت های کلاینت ددیکیتد خاموش می شوند.
        اگر نقش ClientOnly باشد، کامپوننت های سرور ددیکیتد خاموش می شوند و پورت ۷۷۷۷ گرفته نمی شود.
        اگر نقش ServerAndClientEditorTest باشد، هر دو مسیر برای تست سریع داخل ادیتور روشن می مانند.
        بهتر است این اسکریپت روی خود Unity_Dedicated_Server_Runtime یا Dedicated_Game_Server_Client_Test قرار نگیرد.
        */
    }
}
