using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Network_A.Tests.Realtime;
using TMPro;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public sealed class DedicatedGameServerRealtimeUiGuardWrapper : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RealtimeGrpcStreamingG7RoomLobbyTestController grpcRealtimeController;
        [SerializeField] private DedicatedRemotePlayerViewController dedicatedRemotePlayerViewController;
        [SerializeField] private DedicatedGameServerWsClient dedicatedWsClient;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private GameObject serverDebugPanel;
        [SerializeField] private bool autoFindReferences = true;

        [Header("Room Counter Guard")]
        [SerializeField] private bool protectRealtimePresenceCounterWhileInDedicated = true;
        [SerializeField] private bool decrementTrustedCounterOnDedicatedLeftWithoutAuthoritativeCount = true;
        [SerializeField] private float realtimePresenceCorrectionDelaySeconds = 0.05f;
        [SerializeField] private float authoritativeCountFreshSeconds = 0.25f;
        [SerializeField] private int minimumJoinedOnlineCount = 1;

        [Header("Room List Guard")]
        [SerializeField] private bool refreshRoomListAfterRealtimeReadyWhenNotJoined = true;
        [SerializeField] private int roomListRefreshAttempts = 3;
        [SerializeField] private float roomListRefreshInitialDelaySeconds = 0.4f;
        [SerializeField] private float roomListRefreshRetryDelaySeconds = 1.0f;

        [Header("Recovered Status Guard")]
        [SerializeField] private bool clearStaleNetworkStatusAfterFullRecovery = true;
        [SerializeField] private string recoveredStatusMessage = "اتصال برقرار است.";
        [SerializeField] private bool hideServerDebugPanelAfterFullRecovery = true;
        [SerializeField] private float recoveredStatusCheckIntervalSeconds = 0.25f;

        [Header("Logging")]
        [SerializeField] private bool verboseLogs = true;

        private readonly HashSet<string> dedicatedLeftDeltaAppliedPlayerIds = new HashSet<string>(StringComparer.Ordinal);

        private FieldInfo joinedRoomField;
        private FieldInfo onlineCountField;
        private FieldInfo maxPlayersField;
        private int trustedOnlineCount = -1;
        private int trustedMaxPlayers = 1;
        private float lastAuthoritativeCountAt = -1000f;
        private bool hasTrustedOnlineCount;
        private bool hasSeenDedicatedPresence;
        private bool listRefreshRunning;
        private float nextRecoveredStatusCheckAt;

        private void Awake()
        {
            ResolveReferences();
            CacheReflectionAccessors();
        }

        private void OnEnable()
        {
            ResolveReferences();
            CacheReflectionAccessors();
            BindEvents();
            StartCoroutine(CaptureTrustedOnlineCountAfterDelay("on_enable"));
            StartCoroutine(RefreshRoomListWhenReadyAsync("on_enable"));
        }

        private void OnDisable()
        {
            UnbindEvents();
            StopAllCoroutines();
            listRefreshRunning = false;
        }

        private void Update()
        {
            if (clearStaleNetworkStatusAfterFullRecovery)
            {
                TryClearStaleNetworkStatusAfterFullRecovery();
            }
        }

        private void BindEvents()
        {
            if (grpcRealtimeController != null)
            {
                grpcRealtimeController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
                grpcRealtimeController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                grpcRealtimeController.OnPlayerJoinedFor3D -= HandleRealtimePlayerJoined;
                grpcRealtimeController.OnPlayerLeftFor3D -= HandleRealtimePlayerLeft;

                grpcRealtimeController.OnRoomJoinedFor3D += HandleRealtimeRoomJoined;
                grpcRealtimeController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                grpcRealtimeController.OnPlayerJoinedFor3D += HandleRealtimePlayerJoined;
                grpcRealtimeController.OnPlayerLeftFor3D += HandleRealtimePlayerLeft;
            }

            if (dedicatedRemotePlayerViewController != null)
            {
                dedicatedRemotePlayerViewController.DedicatedRemotePlayerJoinedForUi -= HandleDedicatedRemotePlayerJoined;
                dedicatedRemotePlayerViewController.DedicatedRemotePlayerLeftForUi -= HandleDedicatedRemotePlayerLeft;
                dedicatedRemotePlayerViewController.DedicatedRoomOnlineCountChangedForUi -= HandleDedicatedRoomOnlineCountChanged;

                dedicatedRemotePlayerViewController.DedicatedRemotePlayerJoinedForUi += HandleDedicatedRemotePlayerJoined;
                dedicatedRemotePlayerViewController.DedicatedRemotePlayerLeftForUi += HandleDedicatedRemotePlayerLeft;
                dedicatedRemotePlayerViewController.DedicatedRoomOnlineCountChangedForUi += HandleDedicatedRoomOnlineCountChanged;
            }
        }

        private void UnbindEvents()
        {
            if (grpcRealtimeController != null)
            {
                grpcRealtimeController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
                grpcRealtimeController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                grpcRealtimeController.OnPlayerJoinedFor3D -= HandleRealtimePlayerJoined;
                grpcRealtimeController.OnPlayerLeftFor3D -= HandleRealtimePlayerLeft;
            }

            if (dedicatedRemotePlayerViewController != null)
            {
                dedicatedRemotePlayerViewController.DedicatedRemotePlayerJoinedForUi -= HandleDedicatedRemotePlayerJoined;
                dedicatedRemotePlayerViewController.DedicatedRemotePlayerLeftForUi -= HandleDedicatedRemotePlayerLeft;
                dedicatedRemotePlayerViewController.DedicatedRoomOnlineCountChangedForUi -= HandleDedicatedRoomOnlineCountChanged;
            }
        }

        private void HandleRealtimeRoomJoined(string roomId)
        {
            dedicatedLeftDeltaAppliedPlayerIds.Clear();
            StartCoroutine(CaptureTrustedOnlineCountAfterDelay("realtime_room_joined:" + Safe(roomId)));
        }

        private void HandleRealtimeRoomLeft(string roomId)
        {
            hasTrustedOnlineCount = false;
            trustedOnlineCount = -1;
            dedicatedLeftDeltaAppliedPlayerIds.Clear();
            StartCoroutine(RefreshRoomListWhenReadyAsync("realtime_room_left:" + Safe(roomId)));
        }

        private void HandleRealtimePlayerJoined(string playerId, string displayName)
        {
            if (!ShouldProtectRealtimePresenceCounter()) return;

            StartCoroutine(ReapplyTrustedOnlineCountAfterRealtimePresence(
                "realtime_player_joined:" + Safe(playerId),
                false
            ));
        }

        private void HandleRealtimePlayerLeft(string playerId, string displayName)
        {
            if (!ShouldProtectRealtimePresenceCounter()) return;

            StartCoroutine(ReapplyTrustedOnlineCountAfterRealtimePresence(
                "realtime_player_left:" + Safe(playerId),
                false
            ));
        }

        private void HandleDedicatedRemotePlayerJoined(string playerId, string displayName)
        {
            hasSeenDedicatedPresence = true;
            dedicatedLeftDeltaAppliedPlayerIds.Remove(Safe(playerId));

            if (!hasTrustedOnlineCount)
            {
                StartCoroutine(CaptureTrustedOnlineCountAfterDelay("dedicated_joined_seen:" + Safe(playerId)));
            }
        }

        private void HandleDedicatedRemotePlayerLeft(string playerId, string displayName)
        {
            hasSeenDedicatedPresence = true;

            if (!decrementTrustedCounterOnDedicatedLeftWithoutAuthoritativeCount) return;
            if (HasFreshAuthoritativeCount()) return;

            string safePlayerId = Safe(playerId);
            if (!dedicatedLeftDeltaAppliedPlayerIds.Add(safePlayerId)) return;

            if (!hasTrustedOnlineCount)
            {
                CaptureTrustedOnlineCountFromController("dedicated_left_no_trusted_count");
            }

            int maxPlayers = Mathf.Max(1, trustedMaxPlayers);
            int current = hasTrustedOnlineCount ? trustedOnlineCount : ReadJoinedRoomOnlineCountFallback();
            int minUsers = grpcRealtimeController != null && grpcRealtimeController.IsJoinedRoom
                ? Mathf.Max(1, minimumJoinedOnlineCount)
                : 0;
            int next = Mathf.Clamp(current - 1, minUsers, maxPlayers);

            trustedOnlineCount = next;
            trustedMaxPlayers = maxPlayers;
            hasTrustedOnlineCount = true;
            ApplyTrustedOnlineCount("dedicated_left_without_authoritative_count:" + safePlayerId);
        }

        private void HandleDedicatedRoomOnlineCountChanged(int onlineCount)
        {
            if (onlineCount <= 0) return;

            int maxPlayers = Mathf.Max(1, trustedMaxPlayers);
            if (TryReadJoinedRoomCounts(out _, out int currentMaxPlayers))
            {
                maxPlayers = Mathf.Max(1, currentMaxPlayers);
            }

            trustedMaxPlayers = maxPlayers;
            trustedOnlineCount = Mathf.Clamp(onlineCount, minimumJoinedOnlineCount, maxPlayers);
            hasTrustedOnlineCount = true;
            lastAuthoritativeCountAt = Time.unscaledTime;

            ApplyTrustedOnlineCount("dedicated_authoritative_event");
        }

        private IEnumerator CaptureTrustedOnlineCountAfterDelay(string source)
        {
            yield return null;

            float delay = Mathf.Max(0f, realtimePresenceCorrectionDelaySeconds);
            if (delay > 0f)
            {
                yield return new WaitForSecondsRealtime(delay);
            }

            CaptureTrustedOnlineCountFromController(source);
        }

        private IEnumerator ReapplyTrustedOnlineCountAfterRealtimePresence(string source, bool captureBeforeApply)
        {
            float delay = Mathf.Max(0f, realtimePresenceCorrectionDelaySeconds);
            if (delay > 0f)
            {
                yield return new WaitForSecondsRealtime(delay);
            }
            else
            {
                yield return null;
            }

            if (captureBeforeApply || !hasTrustedOnlineCount)
            {
                CaptureTrustedOnlineCountFromController(source + ":capture");
            }

            ApplyTrustedOnlineCount(source);
        }

        private IEnumerator RefreshRoomListWhenReadyAsync(string source)
        {
            if (!refreshRoomListAfterRealtimeReadyWhenNotJoined) yield break;
            if (listRefreshRunning) yield break;

            listRefreshRunning = true;

            if (roomListRefreshInitialDelaySeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(roomListRefreshInitialDelaySeconds);
            }

            int attempts = Mathf.Max(1, roomListRefreshAttempts);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (grpcRealtimeController == null) break;

                if (grpcRealtimeController.IsRealtimeReadyState && !grpcRealtimeController.IsJoinedRoom)
                {
                    Task<bool> task = null;

                    try
                    {
                        task = grpcRealtimeController.ListRoomsAsync();
                    }
                    catch (Exception ex)
                    {
                        Log("Room list wrapper refresh start failed. source=" + source + " | error=" + ex.Message);
                    }

                    if (task != null)
                    {
                        while (!task.IsCompleted)
                        {
                            yield return null;
                        }

                        if (task.IsFaulted)
                        {
                            string error = task.Exception != null ? task.Exception.GetBaseException().Message : "unknown";
                            Log("Room list wrapper refresh failed. source=" + source + " | attempt=" + attempt + " | error=" + error);
                        }
                        else if (task.IsCanceled)
                        {
                            Log("Room list wrapper refresh canceled. source=" + source + " | attempt=" + attempt);
                        }
                        else
                        {
                            Log("Room list wrapper refresh completed. source=" + source + " | attempt=" + attempt + " | result=" + task.Result);
                            break;
                        }
                    }
                }

                if (attempt < attempts)
                {
                    yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, roomListRefreshRetryDelaySeconds));
                }
            }

            listRefreshRunning = false;
        }

        private void TryClearStaleNetworkStatusAfterFullRecovery()
        {
            if (Time.unscaledTime < nextRecoveredStatusCheckAt) return;

            nextRecoveredStatusCheckAt = Time.unscaledTime + Mathf.Max(0.05f, recoveredStatusCheckIntervalSeconds);

            if (grpcRealtimeController == null || statusText == null) return;
            if (!grpcRealtimeController.IsRealtimeReadyState) return;
            if (!grpcRealtimeController.IsJoinedRoom) return;
            if (grpcRealtimeController.IsRealtimeReconnectRunningState) return;
            if (!IsDedicatedAuthenticated()) return;

            string currentStatus = statusText.text ?? string.Empty;
            if (!IsStaleNetworkStatus(currentStatus)) return;

            statusText.text = string.IsNullOrWhiteSpace(recoveredStatusMessage)
                ? "اتصال برقرار است."
                : recoveredStatusMessage.Trim();

            if (hideServerDebugPanelAfterFullRecovery && serverDebugPanel != null)
            {
                serverDebugPanel.SetActive(false);
            }

            Log("Stale network status cleared after full recovery.");
        }

        private bool ShouldProtectRealtimePresenceCounter()
        {
            if (!protectRealtimePresenceCounterWhileInDedicated) return false;
            if (grpcRealtimeController == null) return false;
            if (!grpcRealtimeController.IsJoinedRoom) return false;
            return IsDedicatedAuthenticatedOrRecentlySeen();
        }

        private bool IsDedicatedAuthenticatedOrRecentlySeen()
        {
            if (IsDedicatedAuthenticated()) return true;
            return hasSeenDedicatedPresence;
        }

        private bool IsDedicatedAuthenticated()
        {
            return dedicatedWsClient != null &&
                   dedicatedWsClient.IsConnected &&
                   dedicatedWsClient.IsAuthenticated;
        }

        private bool HasFreshAuthoritativeCount()
        {
            return Time.unscaledTime - lastAuthoritativeCountAt <= Mathf.Max(0.05f, authoritativeCountFreshSeconds);
        }

        private void CaptureTrustedOnlineCountFromController(string source)
        {
            if (!TryReadJoinedRoomCounts(out int onlineCount, out int maxPlayers)) return;

            trustedOnlineCount = Mathf.Clamp(
                onlineCount,
                grpcRealtimeController != null && grpcRealtimeController.IsJoinedRoom ? Mathf.Max(1, minimumJoinedOnlineCount) : 0,
                Mathf.Max(1, maxPlayers)
            );
            trustedMaxPlayers = Mathf.Max(1, maxPlayers);
            hasTrustedOnlineCount = true;

            Log("Trusted room users captured. source=" + source + " | users=" + trustedOnlineCount + "/" + trustedMaxPlayers);
        }

        private void ApplyTrustedOnlineCount(string source)
        {
            if (grpcRealtimeController == null) return;
            if (!hasTrustedOnlineCount) return;
            if (!grpcRealtimeController.IsJoinedRoom) return;

            int safeMaxPlayers = Mathf.Max(1, trustedMaxPlayers);
            int safeOnlineCount = Mathf.Clamp(trustedOnlineCount, Mathf.Max(1, minimumJoinedOnlineCount), safeMaxPlayers);
            grpcRealtimeController.ApplyDedicatedAuthoritativeOnlineCount(
                safeOnlineCount,
                "wrapper_" + source
            );

            Log("Trusted room users applied. source=" + source + " | users=" + safeOnlineCount + "/" + safeMaxPlayers);
        }

        private bool TryReadJoinedRoomCounts(out int onlineCount, out int maxPlayers)
        {
            onlineCount = -1;
            maxPlayers = 1;

            if (grpcRealtimeController == null || joinedRoomField == null) return false;

            object room = joinedRoomField.GetValue(grpcRealtimeController);
            if (room == null) return false;

            if (onlineCountField == null || maxPlayersField == null)
            {
                Type roomType = room.GetType();
                onlineCountField = roomType.GetField("onlineCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                maxPlayersField = roomType.GetField("maxPlayers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            if (onlineCountField == null) return false;

            onlineCount = Convert.ToInt32(onlineCountField.GetValue(room));
            maxPlayers = maxPlayersField != null ? Convert.ToInt32(maxPlayersField.GetValue(room)) : Mathf.Max(1, trustedMaxPlayers);
            return true;
        }

        private int ReadJoinedRoomOnlineCountFallback()
        {
            return TryReadJoinedRoomCounts(out int onlineCount, out _)
                ? Mathf.Max(0, onlineCount)
                : Mathf.Max(minimumJoinedOnlineCount, trustedOnlineCount);
        }

        private void CacheReflectionAccessors()
        {
            if (grpcRealtimeController == null) return;

            joinedRoomField = grpcRealtimeController
                .GetType()
                .GetField("joinedRoom", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        private void ResolveReferences()
        {
            if (!autoFindReferences) return;

            if (grpcRealtimeController == null)
            {
                grpcRealtimeController = FindFirstObjectByType<RealtimeGrpcStreamingG7RoomLobbyTestController>();
            }

            if (dedicatedRemotePlayerViewController == null)
            {
                dedicatedRemotePlayerViewController = FindFirstObjectByType<DedicatedRemotePlayerViewController>();
            }

            if (dedicatedWsClient == null)
            {
                dedicatedWsClient = FindFirstObjectByType<DedicatedGameServerWsClient>();
            }

            if (statusText == null)
            {
                GameObject statusObject = GameObject.Find("Status Text");
                if (statusObject != null) statusText = statusObject.GetComponent<TextMeshProUGUI>();
            }

            if (serverDebugPanel == null)
            {
                serverDebugPanel = GameObject.Find("Pnl_ServerDebug");
            }
        }

        private static bool IsStaleNetworkStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            return value.Contains("اینترنت شما قطع شده است") ||
                   value.Contains("اتصال اینترنت را بررسی کنید") ||
                   value.Contains("ارتباط Realtime موقتاً قطع شد") ||
                   value.Contains("در حال بازیابی اتصال");
        }

        private void Log(string message)
        {
            if (!verboseLogs) return;
            Debug.Log("[DedicatedGameServerRealtimeUiGuardWrapper] " + message);
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
