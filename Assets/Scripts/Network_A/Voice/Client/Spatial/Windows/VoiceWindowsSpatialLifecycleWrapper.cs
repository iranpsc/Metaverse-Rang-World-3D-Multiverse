#if UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using UnityEngine;

namespace Network_A.Voice.Client.Spatial.Windows
{
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsSpatialLifecycleWrapper : MonoBehaviour
    {
        private const string ListenerAnchorName = "VoiceListenerAnchor";
        private const string SpatialAnchorName = "VoiceSpatialAnchor";
        private const float ScanIntervalSeconds = 0.20f;

        private readonly Dictionary<string, Transform> boundRemoteAnchors =
            new Dictionary<string, Transform>(StringComparer.Ordinal);

        private global::G7ThreeDModeController threeDModeController;
        private DedicatedRemotePlayerStateReceiver remoteStateReceiver;
        private DedicatedRemotePlayerStateReceiver subscribedStateReceiver;

        private AudioListener activeLocalListener;
        private int localPlayerInstanceId;
        private int localListenerFailureInstanceId;
        private float nextScanAt;
        private bool readyLogged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            VoiceWindowsSpatialLifecycleWrapper existing =
                FindObjectOfType<VoiceWindowsSpatialLifecycleWrapper>(true);

            if (existing != null)
            {
                return;
            }

            GameObject root =
                new GameObject("VoiceWindowsSpatialLifecycleWrapper");

            DontDestroyOnLoad(root);
            root.AddComponent<VoiceWindowsSpatialLifecycleWrapper>();

            Debug.Log(
                "VOICE_WINDOWS_SPATIAL_LIFECYCLE_WRAPPER=INSTALLED" +
                " | platformScope=Windows" +
                " | existingFilesModified=False" +
                " | sharedVoiceChanged=False" +
                " | webglChanged=False");
        }

        private void OnEnable()
        {
            ResolveReferences();
            BindStateReceiver();
            nextScanAt = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime < nextScanAt)
            {
                return;
            }

            nextScanAt = Time.unscaledTime + ScanIntervalSeconds;

            ResolveReferences();
            BindStateReceiver();
            EnforceLocalVoiceListener();
            RecoverRemoteSpatialBindings();
            CleanupDestroyedBindings();
            LogReadyOnce();
        }

        private void OnDisable()
        {
            UnbindStateReceiver();
        }

        private void OnDestroy()
        {
            UnbindStateReceiver();
        }

        private void ResolveReferences()
        {
            if (threeDModeController == null)
            {
                threeDModeController =
                    FindObjectOfType<global::G7ThreeDModeController>(true);
            }

            if (remoteStateReceiver == null)
            {
                remoteStateReceiver =
                    FindObjectOfType<DedicatedRemotePlayerStateReceiver>(true);
            }
        }

        private void BindStateReceiver()
        {
            if (subscribedStateReceiver == remoteStateReceiver)
            {
                return;
            }

            UnbindStateReceiver();

            subscribedStateReceiver = remoteStateReceiver;

            if (subscribedStateReceiver == null)
            {
                return;
            }

            subscribedStateReceiver.RemotePlayerStateReceived +=
                HandleRemotePlayerStateReceived;
        }

        private void UnbindStateReceiver()
        {
            if (subscribedStateReceiver == null)
            {
                return;
            }

            subscribedStateReceiver.RemotePlayerStateReceived -=
                HandleRemotePlayerStateReceived;

            subscribedStateReceiver = null;
        }

        private void HandleRemotePlayerStateReceived(
            DedicatedRemotePlayerState state)
        {
            if (state == null)
            {
                return;
            }

            nextScanAt = 0f;
        }

        private void EnforceLocalVoiceListener()
        {
            if (threeDModeController == null)
            {
                return;
            }

            GameObject localPlayer =
                threeDModeController.LocalPlayerInstance;

            if (localPlayer == null)
            {
                return;
            }

            int ownerInstanceId = localPlayer.GetInstanceID();

            Transform listenerAnchor =
                FindNamedChild(
                    localPlayer.transform,
                    ListenerAnchorName);

            if (listenerAnchor == null)
            {
                LogLocalListenerFailureOnce(
                    ownerInstanceId,
                    localPlayer.name,
                    "voice_listener_anchor_missing");

                return;
            }

            AudioListener targetListener =
                listenerAnchor.GetComponent<AudioListener>();

            if (targetListener == null)
            {
                LogLocalListenerFailureOnce(
                    ownerInstanceId,
                    localPlayer.name,
                    "audio_listener_missing");

                return;
            }

            localListenerFailureInstanceId = 0;

            listenerAnchor.localPosition = Vector3.zero;
            listenerAnchor.localRotation = Quaternion.identity;

            bool targetWasEnabled = targetListener.enabled;
            int disabledOtherListeners = 0;

            AudioListener[] listeners =
                FindObjectsOfType<AudioListener>(true);

            for (int i = 0; i < listeners.Length; i++)
            {
                AudioListener listener = listeners[i];

                if (listener == null ||
                    listener == targetListener ||
                    !listener.enabled)
                {
                    continue;
                }

                listener.enabled = false;
                disabledOtherListeners++;
            }

            targetListener.enabled = true;

            int activeListenerCount = 0;

            for (int i = 0; i < listeners.Length; i++)
            {
                AudioListener listener = listeners[i];

                if (listener != null && listener.enabled)
                {
                    activeListenerCount++;
                }
            }

            bool changed =
                activeLocalListener != targetListener ||
                localPlayerInstanceId != ownerInstanceId ||
                !targetWasEnabled ||
                disabledOtherListeners > 0;

            activeLocalListener = targetListener;
            localPlayerInstanceId = ownerInstanceId;

            if (!changed)
            {
                return;
            }

            Debug.Log(
                "VOICE_WINDOWS_LOCAL_LISTENER_WRAPPER=PASS" +
                " | player=" + localPlayer.name +
                " | target=" + ListenerAnchorName +
                " | localPosition=" + listenerAnchor.localPosition +
                " | localRotation=" +
                    listenerAnchor.localRotation.eulerAngles +
                " | activeListenerCount=" + activeListenerCount +
                " | disabledOtherListeners=" +
                    disabledOtherListeners +
                " | existingFilesModified=False" +
                " | platformScope=Windows");
        }

        private void LogLocalListenerFailureOnce(
            int ownerInstanceId,
            string playerName,
            string reason)
        {
            if (localListenerFailureInstanceId == ownerInstanceId)
            {
                return;
            }

            localListenerFailureInstanceId = ownerInstanceId;

            Debug.LogError(
                "VOICE_WINDOWS_LOCAL_LISTENER_WRAPPER=FAIL" +
                " | player=" + playerName +
                " | reason=" + reason +
                " | existingFilesModified=False");
        }

        private void RecoverRemoteSpatialBindings()
        {
            if (threeDModeController == null ||
                remoteStateReceiver == null)
            {
                return;
            }

            Transform playersRoot =
                threeDModeController.PlayersRoot;

            if (playersRoot == null)
            {
                return;
            }

            List<DedicatedRemotePlayerState> snapshot =
                remoteStateReceiver.CreateSnapshot();

            for (int i = 0; i < snapshot.Count; i++)
            {
                DedicatedRemotePlayerState state = snapshot[i];

                if (state == null)
                {
                    continue;
                }

                string playerId =
                    SafeTrim(state.ResolvePlayerId());

                if (string.IsNullOrEmpty(playerId))
                {
                    continue;
                }

                string displayName =
                    ResolveDisplayName(
                        state.userName,
                        playerId);

                GameObject remotePlayer =
                    FindRemotePlayer(
                        playersRoot,
                        displayName,
                        playerId);

                if (remotePlayer == null)
                {
                    continue;
                }

                Transform anchor =
                    FindNamedChild(
                        remotePlayer.transform,
                        SpatialAnchorName);

                if (anchor == null)
                {
                    continue;
                }

                anchor.localPosition = Vector3.zero;
                anchor.localRotation = Quaternion.identity;

                MetaverseNetworkIdentity identity =
                    anchor.GetComponent<MetaverseNetworkIdentity>();

                bool identityCreated = false;

                if (identity == null)
                {
                    identity =
                        anchor.gameObject.AddComponent<
                            MetaverseNetworkIdentity>();

                    identityCreated = true;
                }

                if (!string.IsNullOrWhiteSpace(
                        identity.OwnerUserId) &&
                    !identity.IsOwnedByUser(playerId))
                {
                    Debug.LogError(
                        "VOICE_WINDOWS_REMOTE_SPATIAL_WRAPPER=FAIL" +
                        " | playerId=" + playerId +
                        " | avatar=" + remotePlayer.name +
                        " | reason=anchor_identity_owned_by_different_user");

                    continue;
                }

                bool ownershipChanged =
                    !identity.IsOwnedByUser(playerId);

                if (ownershipChanged)
                {
                    identity.SetOwnerInfo(
                        string.Empty,
                        playerId,
                        playerId,
                        false);
                }

                bool newBinding =
                    !boundRemoteAnchors.TryGetValue(
                        playerId,
                        out Transform previousAnchor) ||
                    previousAnchor != anchor;

                boundRemoteAnchors[playerId] = anchor;

                if (!newBinding &&
                    !identityCreated &&
                    !ownershipChanged)
                {
                    continue;
                }

                Debug.Log(
                    "VOICE_WINDOWS_REMOTE_SPATIAL_WRAPPER=PASS" +
                    " | playerId=" + playerId +
                    " | avatar=" + remotePlayer.name +
                    " | target=" + SpatialAnchorName +
                    " | identityCreated=" + identityCreated +
                    " | ownershipChanged=" + ownershipChanged +
                    " | localPosition=" + anchor.localPosition +
                    " | existingFilesModified=False" +
                    " | sharedVoiceChanged=False" +
                    " | webglChanged=False" +
                    " | platformScope=Windows");
            }
        }

        private void CleanupDestroyedBindings()
        {
            if (boundRemoteAnchors.Count == 0)
            {
                return;
            }

            List<string> stalePlayerIds = null;

            foreach (
                KeyValuePair<string, Transform> pair
                in boundRemoteAnchors)
            {
                if (pair.Value != null)
                {
                    continue;
                }

                if (stalePlayerIds == null)
                {
                    stalePlayerIds = new List<string>();
                }

                stalePlayerIds.Add(pair.Key);
            }

            if (stalePlayerIds == null)
            {
                return;
            }

            for (int i = 0; i < stalePlayerIds.Count; i++)
            {
                boundRemoteAnchors.Remove(stalePlayerIds[i]);
            }
        }

        private void LogReadyOnce()
        {
            if (readyLogged ||
                threeDModeController == null ||
                remoteStateReceiver == null)
            {
                return;
            }

            readyLogged = true;

            Debug.Log(
                "VOICE_WINDOWS_SPATIAL_LIFECYCLE_WRAPPER=READY" +
                " | listenerTarget=" + ListenerAnchorName +
                " | remoteTarget=" + SpatialAnchorName +
                " | snapshotRecovery=True" +
                " | existingFilesModified=False" +
                " | sharedVoiceChanged=False" +
                " | mediaChanged=False" +
                " | microphoneChanged=False" +
                " | webglChanged=False" +
                " | platformScope=Windows");
        }

        private static GameObject FindRemotePlayer(
            Transform playersRoot,
            string displayName,
            string playerId)
        {
            if (playersRoot == null)
            {
                return null;
            }

            string expectedDisplayName =
                ResolveDisplayName(
                    displayName,
                    playerId);

            string expectedObjectName =
                "Remote_Player_" +
                SanitizeObjectName(expectedDisplayName);

            for (int i = 0; i < playersRoot.childCount; i++)
            {
                Transform child = playersRoot.GetChild(i);

                if (child == null)
                {
                    continue;
                }

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

        private static Transform FindNamedChild(
            Transform root,
            string targetName)
        {
            if (root == null ||
                string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);

                if (child == null)
                {
                    continue;
                }

                if (string.Equals(
                        child.name,
                        targetName,
                        StringComparison.Ordinal))
                {
                    return child;
                }

                Transform nested =
                    FindNamedChild(
                        child,
                        targetName);

                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static string ResolveDisplayName(
            string displayName,
            string playerId)
        {
            string safeDisplayName =
                SafeTrim(displayName);

            if (!string.IsNullOrEmpty(safeDisplayName))
            {
                return safeDisplayName;
            }

            string safePlayerId =
                SafeTrim(playerId);

            if (!string.IsNullOrEmpty(safePlayerId))
            {
                return safePlayerId;
            }

            return "Player";
        }

        private static string SanitizeObjectName(
            string value)
        {
            string safeValue =
                ResolveDisplayName(
                    value,
                    "Player");

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

        private static string SafeTrim(
            string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }
    }
}
#endif
