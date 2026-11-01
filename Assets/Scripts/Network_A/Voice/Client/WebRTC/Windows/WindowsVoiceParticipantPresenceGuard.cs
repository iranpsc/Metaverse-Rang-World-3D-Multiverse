#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Network_A.DedicatedGameServer.Client;
using Network_A.Voice.Client.Participants;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    // Windows-only presentation guard. It NEVER changes Voice sessions, networking, or the shared list controller.
    // A Voice session peer snapshot can lag after Dedicated has authoritatively announced player_left.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(32000)]
    public sealed class WindowsVoiceParticipantPresenceGuard : MonoBehaviour
    {
        private const string VoiceScene = "Grpc_Enviroment_Voice";
        private const string GuardObjectName = "VME2_Windows_Voice_Participant_Presence_Guard";
        private const float RefreshSeconds = 0.20f;
        private static readonly FieldInfo ListItemsField = typeof(VoiceParticipantListController).GetField(
            "itemsByUserId", BindingFlags.Instance | BindingFlags.NonPublic);
        private static WindowsVoiceParticipantPresenceGuard instance;

        private readonly HashSet<string> validUserIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> removeKeys = new List<string>();
        private VoiceParticipantListController controller;
        private DedicatedRemotePlayerStateReceiver receiver;
        private DedicatedGameServerWsClient dedicated;
        private float nextSweepAt;
        private float nextOrphanSweepAt;
        private bool warnedMissingField;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallInitialScene()
        {
            InstallForScene(SceneManager.GetActiveScene());
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            InstallForScene(scene);
        }

        private static void InstallForScene(Scene scene)
        {
            if (!scene.IsValid() || !string.Equals(scene.name, VoiceScene, StringComparison.Ordinal)) return;
            if (instance != null) return;
            GameObject host = new GameObject(GuardObjectName);
            instance = host.AddComponent<WindowsVoiceParticipantPresenceGuard>();
        }

        private void OnEnable()
        {
            nextSweepAt = 0f;
            nextOrphanSweepAt = 0f;
        }

        private void LateUpdate()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, VoiceScene, StringComparison.Ordinal)) return;
            bool refresh = Time.unscaledTime >= nextSweepAt;
            if (refresh)
            {
                nextSweepAt = Time.unscaledTime + RefreshSeconds;
                ResolveReferences();
                RefreshAuthoritativeParticipants();
            }
            // Run the removal AFTER the shared controller's Update in every rendered frame.
            // Otherwise that controller can briefly recreate a departed player's stale row.
            RemoveNonMembers("periodic", Time.unscaledTime >= nextOrphanSweepAt);
            if (Time.unscaledTime >= nextOrphanSweepAt)
                nextOrphanSweepAt = Time.unscaledTime + RefreshSeconds;
        }

        private void ResolveReferences()
        {
            if (controller == null || !controller.gameObject.scene.IsValid() ||
                !string.Equals(controller.gameObject.scene.name, VoiceScene, StringComparison.Ordinal))
            {
                controller = FindObjectOfType<VoiceParticipantListController>(true);
            }

            DedicatedGameServerWsClient currentDedicated = DedicatedGameServerWsClient.Instance;
            if (dedicated != currentDedicated) dedicated = currentDedicated;

            DedicatedRemotePlayerStateReceiver nextReceiver = null;
            if (dedicated != null)
                nextReceiver = dedicated.GetComponent<DedicatedRemotePlayerStateReceiver>();
            if (nextReceiver == null)
                nextReceiver = FindObjectOfType<DedicatedRemotePlayerStateReceiver>(true);

            if (receiver == nextReceiver) return;
            if (receiver != null) receiver.RemotePlayerLeft -= OnRemotePlayerLeft;
            receiver = nextReceiver;
            if (receiver != null)
            {
                receiver.RemotePlayerLeft -= OnRemotePlayerLeft;
                receiver.RemotePlayerLeft += OnRemotePlayerLeft;
            }
        }

        private void OnRemotePlayerLeft(DedicatedRemotePresenceEvent left)
        {
            // The Dedicated receiver removes the player from its snapshot BEFORE this event is raised.
            // Do not rely on the possibly-stale Voice session peer list when removing this UI row.
            RefreshAuthoritativeParticipants();
            RemoveNonMembers("dedicated_player_left", true);
        }

        private void RefreshAuthoritativeParticipants()
        {
            // The current authenticated Dedicated connection is the authority for who is STILL in the room.
            // Never confuse a stale Voice session membership with current player presence.
            validUserIds.Clear();
            if (dedicated != null && dedicated.IsReady && receiver != null)
            {
                string currentRoomId = Normalize(dedicated.RoomId);
                string selfId = Normalize(dedicated.UserId);
                List<DedicatedRemotePlayerState> snapshot = receiver.CreateSnapshot();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    DedicatedRemotePlayerState state = snapshot[i];
                    if (state == null) continue;
                    string memberId = Normalize(state.userId);
                    if (memberId.Length == 0) memberId = Normalize(state.playerId);
                    if (memberId.Length == 0 || string.Equals(memberId, selfId, StringComparison.Ordinal)) continue;
                    string memberRoomId = Normalize(state.roomId);
                    if (memberRoomId.Length > 0 && currentRoomId.Length > 0 &&
                        !string.Equals(memberRoomId, currentRoomId, StringComparison.Ordinal)) continue;
                    validUserIds.Add(memberId);
                }
            }
        }

        private void RemoveNonMembers(string reason, bool sweepOrphans)
        {
            if (controller == null || ListItemsField == null)
            {
                if (ListItemsField == null && !warnedMissingField)
                {
                    warnedMissingField = true;
                    Debug.LogError("VME2_WIN_VOICE_PARTICIPANTS=GUARD_UNAVAILABLE | reason=shared_list_field_missing");
                }
                return;
            }

            IDictionary items = ListItemsField.GetValue(controller) as IDictionary;
            if (items == null) return;

            removeKeys.Clear();
            foreach (DictionaryEntry pair in items)
            {
                string userId = Normalize(pair.Key as string);
                if (userId.Length > 0 && !validUserIds.Contains(userId))
                    removeKeys.Add(userId);
            }

            // All list rows are owned by the existing shared controller; alter only its UI dictionary,
            // not sessions, mic/speaker settings, presence caches, or the shared source file.
            for (int i = 0; i < removeKeys.Count; i++)
            {
                string key = removeKeys[i];
                VoiceParticipantListItemView view = items[key] as VoiceParticipantListItemView;
                if (view != null)
                {
                    view.gameObject.SetActive(false); // immediate, before deferred Destroy
                    Destroy(view.gameObject);
                }
                items.Remove(key);
                Debug.Log("VME2_WIN_VOICE_PARTICIPANTS=STALE_ROW_REMOVED | userId=" + key +
                    " | reason=" + reason + " | dedicatedReady=" + (dedicated != null && dedicated.IsReady) +
                    " | authoritativeRemoteCount=" + validUserIds.Count);
            }

            // Also clean orphan rows that escaped the controller dictionary during scene/reload cleanup.
            if (!sweepOrphans) return;
            Transform content = null;
            FieldInfo contentField = typeof(VoiceParticipantListController).GetField(
                "participantListContent", BindingFlags.Instance | BindingFlags.NonPublic);
            if (contentField != null) content = contentField.GetValue(controller) as Transform;
            if (content == null) return;
            VoiceParticipantListItemView[] children = content.GetComponentsInChildren<VoiceParticipantListItemView>(true);
            for (int i = 0; i < children.Length; i++)
            {
                VoiceParticipantListItemView view = children[i];
                if (view == null) continue;
                string userId = Normalize(view.UserId);
                if (userId.Length == 0 || validUserIds.Contains(userId)) continue;
                view.gameObject.SetActive(false);
                Destroy(view.gameObject);
            }
        }

        private void OnDisable()
        {
            if (receiver != null) receiver.RemotePlayerLeft -= OnRemotePlayerLeft;
            receiver = null;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private static string Normalize(string raw)
        {
            return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();
        }
    }
}
#endif
