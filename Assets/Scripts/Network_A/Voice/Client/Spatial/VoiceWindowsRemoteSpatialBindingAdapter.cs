using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using UnityEngine;

namespace Network_A.Voice.Client.Spatial
{
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsRemoteSpatialBindingAdapter : MonoBehaviour
    {
        private const string SpatialAnchorName = "VoiceSpatialAnchor";
        private const float RetryIntervalSeconds = 0.20f;

        [Header("Optional References")]
        [SerializeField] private G7ThreeDModeController threeDModeController;
        [SerializeField] private DedicatedRemotePlayerViewController remotePlayerViewController;

        private readonly Dictionary<string, string> pendingDisplayNameByPlayerId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<string, Transform> boundAnchorByPlayerId =
            new Dictionary<string, Transform>(StringComparer.Ordinal);

        private readonly Dictionary<string, MetaverseNetworkIdentity> createdIdentityByPlayerId =
            new Dictionary<string, MetaverseNetworkIdentity>(StringComparer.Ordinal);

        private DedicatedRemotePlayerViewController subscribedRemoteController;
        private float nextResolveAt;
        private bool readyLogged;

        private static bool IsWindowsRuntime
        {
            get
            {
                return Application.platform == RuntimePlatform.WindowsPlayer ||
                       Application.platform == RuntimePlatform.WindowsEditor;
            }
        }

        private void OnEnable()
        {
            if (!IsWindowsRuntime)
            {
                Debug.Log(
                    "VOICE_WINDOWS_REMOTE_SPATIAL_BINDING=SKIPPED | platform=" +
                    Application.platform +
                    " | reason=windows_only"
                );
                enabled = false;
                return;
            }

            TryResolveReferences();
            TrySubscribe();
            nextResolveAt = 0f;
        }

        private void Start()
        {
            if (!IsWindowsRuntime) return;
            TryResolveReferences();
            TrySubscribe();
            LogReadyOnce();
        }

        private void Update()
        {
            if (!IsWindowsRuntime) return;

            if (threeDModeController == null || remotePlayerViewController == null)
            {
                TryResolveReferences();
                TrySubscribe();
            }

            if (Time.unscaledTime < nextResolveAt) return;
            nextResolveAt = Time.unscaledTime + RetryIntervalSeconds;

            ResolvePendingBindings();
            CleanupDestroyedBindings();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
        }

        private void TryResolveReferences()
        {
            if (threeDModeController == null)
            {
                threeDModeController = FindObjectOfType<G7ThreeDModeController>(true);
            }

            if (remotePlayerViewController == null)
            {
                remotePlayerViewController =
                    FindObjectOfType<DedicatedRemotePlayerViewController>(true);
            }
        }

        private void TrySubscribe()
        {
            if (remotePlayerViewController == null) return;
            if (subscribedRemoteController == remotePlayerViewController) return;

            Unsubscribe();

            remotePlayerViewController.DedicatedRemotePlayerJoinedForUi +=
                HandleRemotePlayerJoined;
            remotePlayerViewController.DedicatedRemotePlayerLeftForUi +=
                HandleRemotePlayerLeft;

            subscribedRemoteController = remotePlayerViewController;
            LogReadyOnce();
        }

        private void Unsubscribe()
        {
            if (subscribedRemoteController == null) return;

            subscribedRemoteController.DedicatedRemotePlayerJoinedForUi -=
                HandleRemotePlayerJoined;
            subscribedRemoteController.DedicatedRemotePlayerLeftForUi -=
                HandleRemotePlayerLeft;

            subscribedRemoteController = null;
        }

        private void HandleRemotePlayerJoined(string playerId, string displayName)
        {
            string safePlayerId = SafeTrim(playerId);
            if (string.IsNullOrEmpty(safePlayerId)) return;

            pendingDisplayNameByPlayerId[safePlayerId] =
                ResolveDisplayName(displayName, safePlayerId);

            nextResolveAt = 0f;
        }

        private void HandleRemotePlayerLeft(string playerId, string displayName)
        {
            string safePlayerId = SafeTrim(playerId);
            if (string.IsNullOrEmpty(safePlayerId)) return;

            pendingDisplayNameByPlayerId.Remove(safePlayerId);
            boundAnchorByPlayerId.Remove(safePlayerId);
            createdIdentityByPlayerId.Remove(safePlayerId);

            Debug.Log(
                "VOICE_WINDOWS_REMOTE_SPATIAL_UNBOUND=PASS | playerId=" +
                safePlayerId +
                " | reason=dedicated_remote_left"
            );
        }

        private void ResolvePendingBindings()
        {
            if (pendingDisplayNameByPlayerId.Count == 0) return;
            if (threeDModeController == null) return;

            Transform playersRoot = threeDModeController.PlayersRoot;
            if (playersRoot == null) return;

            List<string> playerIds =
                new List<string>(pendingDisplayNameByPlayerId.Keys);

            for (int i = 0; i < playerIds.Count; i++)
            {
                string playerId = playerIds[i];

                if (boundAnchorByPlayerId.TryGetValue(playerId, out Transform existingAnchor) &&
                    existingAnchor != null &&
                    existingAnchor.gameObject.activeInHierarchy)
                {
                    pendingDisplayNameByPlayerId.Remove(playerId);
                    continue;
                }

                string displayName = pendingDisplayNameByPlayerId[playerId];
                GameObject remotePlayer = FindRemotePlayer(playersRoot, displayName, playerId);
                if (remotePlayer == null) continue;

                Transform anchor = FindNamedChild(remotePlayer.transform, SpatialAnchorName);
                if (anchor == null)
                {
                    Debug.LogError(
                        "VOICE_WINDOWS_REMOTE_SPATIAL_BINDING=FAIL | playerId=" +
                        playerId +
                        " | avatar=" +
                        remotePlayer.name +
                        " | reason=voice_spatial_anchor_missing"
                    );
                    pendingDisplayNameByPlayerId.Remove(playerId);
                    continue;
                }

                anchor.localPosition = Vector3.zero;
                anchor.localRotation = Quaternion.identity;

                MetaverseNetworkIdentity identity =
                    anchor.GetComponent<MetaverseNetworkIdentity>();

                bool identityCreated = false;
                if (identity == null)
                {
                    identity = anchor.gameObject.AddComponent<MetaverseNetworkIdentity>();
                    identityCreated = true;
                }

                if (!string.IsNullOrWhiteSpace(identity.OwnerUserId) &&
                    !identity.IsOwnedByUser(playerId))
                {
                    Debug.LogError(
                        "VOICE_WINDOWS_REMOTE_SPATIAL_BINDING=FAIL | playerId=" +
                        playerId +
                        " | avatar=" +
                        remotePlayer.name +
                        " | reason=anchor_identity_owned_by_different_user"
                    );
                    pendingDisplayNameByPlayerId.Remove(playerId);
                    continue;
                }

                identity.SetOwnerInfo(
                    string.Empty,
                    playerId,
                    playerId,
                    false
                );

                boundAnchorByPlayerId[playerId] = anchor;
                if (identityCreated)
                {
                    createdIdentityByPlayerId[playerId] = identity;
                }

                pendingDisplayNameByPlayerId.Remove(playerId);

                Debug.Log(
                    "VOICE_WINDOWS_REMOTE_SPATIAL_BINDING=PASS | playerId=" +
                    playerId +
                    " | avatar=" +
                    remotePlayer.name +
                    " | target=" +
                    SpatialAnchorName +
                    " | localPosition=" +
                    anchor.localPosition +
                    " | listenerChanged=false" +
                    " | audioSourceSettingsChanged=false" +
                    " | reflectionUsed=false" +
                    " | stableCoreChanged=false"
                );
            }
        }

        private void CleanupDestroyedBindings()
        {
            if (boundAnchorByPlayerId.Count == 0) return;

            List<string> stalePlayerIds = null;

            foreach (KeyValuePair<string, Transform> pair in boundAnchorByPlayerId)
            {
                if (pair.Value != null) continue;

                if (stalePlayerIds == null) stalePlayerIds = new List<string>();
                stalePlayerIds.Add(pair.Key);
            }

            if (stalePlayerIds == null) return;

            for (int i = 0; i < stalePlayerIds.Count; i++)
            {
                string playerId = stalePlayerIds[i];
                boundAnchorByPlayerId.Remove(playerId);
                createdIdentityByPlayerId.Remove(playerId);
            }
        }

        private static GameObject FindRemotePlayer(
            Transform playersRoot,
            string displayName,
            string playerId)
        {
            if (playersRoot == null) return null;

            string expectedDisplayName = ResolveDisplayName(displayName, playerId);
            string expectedObjectName =
                "Remote_Player_" + SanitizeObjectName(expectedDisplayName);

            for (int i = 0; i < playersRoot.childCount; i++)
            {
                Transform child = playersRoot.GetChild(i);
                if (child == null) continue;

                if (string.Equals(
                        child.name,
                        expectedObjectName,
                        StringComparison.Ordinal))
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private static Transform FindNamedChild(Transform root, string targetName)
        {
            if (root == null || string.IsNullOrEmpty(targetName)) return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null) continue;

                if (string.Equals(child.name, targetName, StringComparison.Ordinal))
                {
                    return child;
                }

                Transform nested = FindNamedChild(child, targetName);
                if (nested != null) return nested;
            }

            return null;
        }

        private void LogReadyOnce()
        {
            if (readyLogged) return;
            if (threeDModeController == null || remotePlayerViewController == null) return;

            readyLogged = true;
            Debug.Log(
                "VOICE_WINDOWS_REMOTE_SPATIAL_BINDING=READY" +
                " | platformScope=Windows" +
                " | listenerChanged=false" +
                " | audioSourceSettingsChanged=false" +
                " | mediaChanged=false" +
                " | microphoneChanged=false" +
                " | reflectionUsed=false" +
                " | stableCoreChanged=false"
            );
        }

        private static string ResolveDisplayName(string displayName, string playerId)
        {
            string safeDisplayName = SafeTrim(displayName);
            if (!string.IsNullOrEmpty(safeDisplayName)) return safeDisplayName;

            string safePlayerId = SafeTrim(playerId);
            if (!string.IsNullOrEmpty(safePlayerId)) return safePlayerId;

            return "Player";
        }

        private static string SanitizeObjectName(string value)
        {
            string safeValue = ResolveDisplayName(value, "Player");
            return safeValue
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace(":", "_")
                .Replace("*", "_")
                .Replace("?", "_")
                .Replace("\"", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("|", "_");
        }

        private static string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
