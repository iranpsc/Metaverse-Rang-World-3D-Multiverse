using System;
using System.Reflection;
using Network_A.GameServer;
using Network_A.DedicatedGameServer.Bootstrap;
using Network_A.Tests.Realtime;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.GameServer.Tools
{
    [DefaultExecutionOrder(-10000)]
    public class DedicatedServerStartGateAndRoomBinder : MonoBehaviour
    {
        [Header("Button")]
        [SerializeField] private Button startServerButton;

        [Header("Realtime Room")]
        [SerializeField] private RealtimeWebSocketG7RoomLobbyTestController realtimeRoomController;
        [SerializeField] private bool requireJoinedRealtimeRoom = true;
        [SerializeField] private bool disableStartButtonUntilRoomJoined = true;

        [Header("Dedicated Server")]
        [SerializeField] private DedicatedServerConfig serverConfig;
        [SerializeField] private DedicatedServerRuntime serverRuntime;
        [SerializeField] private DedicatedStartGameServerButton startGameServerButton;
        [SerializeField] private GameObject serverRuntimeRoot;

        [Header("Role Guard")]
        [SerializeField] private DedicatedRuntimeRoleSwitcher roleSwitcher;
        [SerializeField] private bool blockWhenRoleSwitcherIsClientOnly = true;
        [SerializeField] private bool disableButtonWhenServerRoleNotAllowed = true;

        [Header("Safety")]
        [SerializeField] private bool forceDedicatedAutoStartOff = true;
        [SerializeField] private bool disableServerRootBeforeManualStart = false;
        [SerializeField] private bool syncRoomIdOnEnable = true;
        [SerializeField] private bool bindButtonForPreStartSync = true;
        [SerializeField] private bool startServerFromGateAfterSync = true;

        [Header("Fallback")]
        [SerializeField] private string fallbackRoomId = "";
        [SerializeField] private string fallbackRoomName = "";

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        private bool manualStartRequested;

        private void Awake()
        {
            EnsureReferences();
            ApplyStartupSafety();
            BindButton();
            UpdateStartButtonState();
        }

        private void OnEnable()
        {
            EnsureReferences();

            if (realtimeRoomController != null)
            {
                realtimeRoomController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
                realtimeRoomController.OnRoomJoinedFor3D += HandleRealtimeRoomJoined;
                realtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                realtimeRoomController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                realtimeRoomController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;
                realtimeRoomController.OnRealtimeDisconnectedFor3D += HandleRealtimeDisconnected;
            }

            if (syncRoomIdOnEnable && HasUsableRealtimeRoom())
            {
                SyncDedicatedRoomIdFromRealtime();
            }

            UpdateStartButtonState();
        }

        private void OnDisable()
        {
            if (realtimeRoomController != null)
            {
                realtimeRoomController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
                realtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                realtimeRoomController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;
            }

            UnbindButton();
        }

        [ContextMenu("Sync Room Id Now")]
        public void Btn_SyncRoomIdNow()
        {
            EnsureReferences();
            SyncDedicatedRoomIdFromRealtime();
            UpdateStartButtonState();
        }

        [ContextMenu("Start Server For Current Realtime Room")]
        public void Btn_StartServerForCurrentRealtimeRoom()
        {
            EnsureReferences();

            if (!IsServerStartAllowed())
            {
                Debug.LogWarning("[DedicatedServerStartGateAndRoomBinder] Start blocked because runtime role is ClientOnly.");
                UpdateStartButtonState();
                return;
            }

            if (!SyncDedicatedRoomIdFromRealtime())
            {
                Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Start blocked because realtime room id is not ready.");
                UpdateStartButtonState();
                return;
            }

            manualStartRequested = true;
            EnableServerRootForManualStart();
            StartDedicatedServerAfterSync();
        }

        public bool SyncDedicatedRoomIdFromRealtime()
        {
            EnsureReferences();
            ApplyStartupSafety();

            if (!IsServerStartAllowed())
            {
                Log("Room sync skipped because runtime role is ClientOnly.");
                return false;
            }

            string roomId = ResolveRealtimeRoomId();
            string roomName = ResolveRealtimeRoomName();

            if (requireJoinedRealtimeRoom && string.IsNullOrWhiteSpace(roomId))
            {
                Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Realtime room id is empty. Join a room before starting dedicated server.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(roomId)) roomId = SafeTrim(fallbackRoomId);
            if (string.IsNullOrWhiteSpace(roomName)) roomName = SafeTrim(fallbackRoomName);
            if (string.IsNullOrWhiteSpace(roomName)) roomName = roomId;

            if (string.IsNullOrWhiteSpace(roomId))
            {
                Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Room id is empty and fallback room id is empty.");
                return false;
            }

            if (serverRuntime != null && serverRuntime.IsRunning)
            {
                string runningRoomId = serverRuntime.CurrentConfig == null ? string.Empty : serverRuntime.CurrentConfig.roomId;
                if (!string.Equals(SafeTrim(runningRoomId), roomId, StringComparison.Ordinal))
                {
                    Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Dedicated server is already running with another roomId. Restart Play Mode before testing. runningRoomId=" + runningRoomId + " targetRoomId=" + roomId);
                    return false;
                }

                return true;
            }

            bool roomSet = ApplyRoomToServerConfig(roomId, roomName);

            if (!roomSet)
            {
                Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Could not write room context into DedicatedServerConfig.");
                return false;
            }

            Log("Dedicated server room synced from realtime | roomId=" + roomId + " | roomName=" + roomName);
            return true;
        }

        private void HandleRealtimeRoomJoined(string roomId)
        {
            Log("Realtime room joined event received | roomId=" + roomId);
            SyncDedicatedRoomIdFromRealtime();
            UpdateStartButtonState();
        }

        private void HandleRealtimeRoomLeft(string roomId)
        {
            Log("Realtime room left event received | roomId=" + roomId);
            UpdateStartButtonState();
        }

        private void HandleRealtimeDisconnected(string reason)
        {
            Log("Realtime disconnected event received | reason=" + reason);
            UpdateStartButtonState();
        }

        private void HandleStartButtonClickedForPreSync()
        {
            EnsureReferences();

            if (!IsServerStartAllowed())
            {
                Debug.LogWarning("[DedicatedServerStartGateAndRoomBinder] Pre-start blocked because runtime role is ClientOnly.");
                UpdateStartButtonState();
                return;
            }

            if (!SyncDedicatedRoomIdFromRealtime())
            {
                Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Pre-start sync failed.");
                UpdateStartButtonState();
                return;
            }

            manualStartRequested = true;
            EnableServerRootForManualStart();

            if (startServerFromGateAfterSync)
            {
                StartDedicatedServerAfterSync();
                return;
            }

            Log("Pre-start sync finished. Existing start listener can continue now.");
        }

        private void StartDedicatedServerAfterSync()
        {
            EnsureReferences();

            if (startGameServerButton != null)
            {
                startGameServerButton.StartGameServer();
                EnsureReferences();
            }

            if (serverRuntime != null && !serverRuntime.IsRunning)
            {
                serverRuntime.StartDedicatedRuntime();
            }

            if (serverRuntime != null && serverRuntime.IsRunning)
            {
                Log("Dedicated server start confirmed after room sync.");
                UpdateStartButtonState();
                return;
            }

            Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Start failed. DedicatedStartGameServerButton and DedicatedServerRuntime are missing or runtime did not start.");
            UpdateStartButtonState();
        }

        private void EnsureReferences()
        {
            if (startServerButton == null) startServerButton = GetComponent<Button>();
            if (realtimeRoomController == null) realtimeRoomController = FindObjectOfType<RealtimeWebSocketG7RoomLobbyTestController>(true);
            if (serverConfig == null) serverConfig = FindObjectOfType<DedicatedServerConfig>(true);
            if (serverRuntime == null) serverRuntime = DedicatedServerRuntime.Instance;
            if (serverRuntime == null) serverRuntime = FindObjectOfType<DedicatedServerRuntime>(true);
            if (startGameServerButton == null) startGameServerButton = FindObjectOfType<DedicatedStartGameServerButton>(true);
            if (roleSwitcher == null) roleSwitcher = FindObjectOfType<DedicatedRuntimeRoleSwitcher>(true);
        }

        private void ApplyStartupSafety()
        {
            if (forceDedicatedAutoStartOff && serverConfig != null)
            {
                SetPrivateField(serverConfig, "autoStart", false);
            }

            if (!disableServerRootBeforeManualStart) return;
            if (manualStartRequested) return;
            if (serverRuntimeRoot == null) return;
            if (startServerButton != null && startServerButton.transform.IsChildOf(serverRuntimeRoot.transform)) return;
            if (transform.IsChildOf(serverRuntimeRoot.transform)) return;

            serverRuntimeRoot.SetActive(false);
        }

        private void EnableServerRootForManualStart()
        {
            if (serverRuntimeRoot == null) return;
            if (serverRuntimeRoot.activeSelf) return;

            serverRuntimeRoot.SetActive(true);
            Log("Server runtime root activated by manual start gate.");
        }

        private void BindButton()
        {
            if (!bindButtonForPreStartSync || startServerButton == null) return;

            startServerButton.onClick.RemoveListener(HandleStartButtonClickedForPreSync);
            startServerButton.onClick.AddListener(HandleStartButtonClickedForPreSync);
        }

        private void UnbindButton()
        {
            if (startServerButton == null) return;
            startServerButton.onClick.RemoveListener(HandleStartButtonClickedForPreSync);
        }

        private void UpdateStartButtonState()
        {
            if (!disableStartButtonUntilRoomJoined || startServerButton == null) return;

            bool roomReady = !requireJoinedRealtimeRoom || HasUsableRealtimeRoom();
            bool runtimeRunning = serverRuntime != null && serverRuntime.IsRunning;
            bool serverRoleAllowed = IsServerStartAllowed();

            startServerButton.interactable = roomReady && !runtimeRunning && (!disableButtonWhenServerRoleNotAllowed || serverRoleAllowed);

            Log("Start button state | interactable=" + startServerButton.interactable + " | roomReady=" + roomReady + " | runtimeRunning=" + runtimeRunning + " | serverRoleAllowed=" + serverRoleAllowed);
        }

        private bool IsServerStartAllowed()
        {
            if (!blockWhenRoleSwitcherIsClientOnly) return true;
            if (roleSwitcher == null) return true;
            return roleSwitcher.IsServerRoleAllowed;
        }

        private bool HasUsableRealtimeRoom()
        {
            if (realtimeRoomController == null) return false;
            if (!realtimeRoomController.IsJoinedRoom) return false;
            return !string.IsNullOrWhiteSpace(realtimeRoomController.CurrentRoomId);
        }

        private string ResolveRealtimeRoomId()
        {
            if (HasUsableRealtimeRoom()) return SafeTrim(realtimeRoomController.CurrentRoomId);
            return string.Empty;
        }

        private string ResolveRealtimeRoomName()
        {
            if (realtimeRoomController == null) return string.Empty;
            if (!realtimeRoomController.IsJoinedRoom) return string.Empty;
            return SafeTrim(realtimeRoomController.CurrentRoomName);
        }

        private bool ApplyRoomToServerConfig(string roomId, string roomName)
        {
            if (serverConfig == null) return false;

            serverConfig.ApplyRealtimeRoom(roomId, roomName);
            bool roomIdSet = SetPrivateField(serverConfig, "roomId", roomId, false);
            bool roomNameSet = SetPrivateField(serverConfig, "roomName", roomName, false);

            if (serverRuntime != null && !serverRuntime.IsRunning)
            {
                serverRuntime.RefreshRuntimeConfigSnapshot();
            }

            return roomIdSet || roomNameSet || !string.IsNullOrWhiteSpace(roomId);
        }

        private bool SetPrivateField(object target, string fieldName, object value)
        {
            return SetPrivateField(target, fieldName, value, true);
        }

        private bool SetPrivateField(object target, string fieldName, object value, bool logMissingField)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName)) return false;

            Type type = target.GetType();
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null)
            {
                if (logMissingField)
                {
                    Debug.LogError("[DedicatedServerStartGateAndRoomBinder] Field not found | type=" + type.Name + " | field=" + fieldName);
                }

                return false;
            }

            field.SetValue(target, value);
            return true;
        }

        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private void Log(string message)
        {
            if (!verboseLogs) return;
            Debug.Log("[DedicatedServerStartGateAndRoomBinder] " + message);
        }
    }
}
