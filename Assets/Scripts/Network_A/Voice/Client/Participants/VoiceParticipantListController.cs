using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using UnityEngine;

namespace Network_A.Voice.Client.Participants
{
    public sealed class VoiceParticipantListController : MonoBehaviour
    {
        [Header("Inspector References")]
        [SerializeField] private Transform participantListContent;
        [SerializeField] private VoiceParticipantListItemView participantItemPrefab;

        [Header("Refresh")]
        [SerializeField, Min(0.1f)] private float refreshIntervalSeconds = 0.25f;

        private readonly Dictionary<string, VoiceParticipantListItemView> itemsByUserId =
            new Dictionary<string, VoiceParticipantListItemView>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> userNamesByUserId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> activeParticipantUserIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> usersToRemove = new List<string>();
        private readonly VoiceClientRuntimeReadOnlyAdapter runtimeAdapter =
            new VoiceClientRuntimeReadOnlyAdapter();
        private readonly VoiceDirectionalControlClient directionalControlClient =
            new VoiceDirectionalControlClient();

        private DedicatedRemotePlayerStateReceiver remotePlayerStateReceiver;
        private float nextRefreshAt;

        private void Update()
        {
            if (Time.unscaledTime < nextRefreshAt) return;
            nextRefreshAt = Time.unscaledTime + refreshIntervalSeconds;
            RefreshParticipantList();
        }

        private void OnDisable()
        {
            ClearItems();
        }

        private void RefreshParticipantList()
        {
            if (!ValidateInspectorReferences()) return;
            if (!TryResolveRemotePlayerStateReceiver()) return;
            if (!runtimeAdapter.TryCreateParticipantUserIdSnapshot(out IReadOnlyList<string> participants)) return;

            RebuildUserNameLookup();
            SyncParticipantItems(participants);
        }

        private bool TryResolveRemotePlayerStateReceiver()
        {
            if (remotePlayerStateReceiver != null) return true;

            DedicatedGameServerWsClient wsClient = DedicatedGameServerWsClient.Instance;
            if (wsClient != null)
            {
                remotePlayerStateReceiver = wsClient.GetComponent<DedicatedRemotePlayerStateReceiver>();
            }

            if (remotePlayerStateReceiver == null)
            {
                remotePlayerStateReceiver = FindObjectOfType<DedicatedRemotePlayerStateReceiver>(true);
            }

            return remotePlayerStateReceiver != null;
        }

        private void RebuildUserNameLookup()
        {
            userNamesByUserId.Clear();

            List<DedicatedRemotePlayerState> states = remotePlayerStateReceiver.CreateSnapshot();
            for (int i = 0; i < states.Count; i++)
            {
                DedicatedRemotePlayerState state = states[i];
                if (state == null) continue;

                string userId = Normalize(state.userId);
                if (userId.Length == 0) continue;

                string userName = Normalize(state.userName);
                userNamesByUserId[userId] = userName.Length > 0 ? userName : userId;
            }
        }

        private void SyncParticipantItems(IReadOnlyList<string> participants)
        {
            activeParticipantUserIds.Clear();
            string localUserId = ResolveLocalUserId();

            for (int i = 0; i < participants.Count; i++)
            {
                string userId = Normalize(participants[i]);
                if (userId.Length == 0 || string.Equals(userId, localUserId, StringComparison.Ordinal)) continue;

                activeParticipantUserIds.Add(userId);
                EnsureParticipantItem(userId);
            }

            RemoveInactiveItems();
        }

        private void EnsureParticipantItem(string userId)
        {
            string userName = ResolveUserName(userId);

            if (itemsByUserId.TryGetValue(userId, out VoiceParticipantListItemView existingItem))
            {
                if (existingItem != null && !string.Equals(existingItem.UserName, userName, StringComparison.Ordinal))
                {
                    existingItem.SetUserName(userName);
                }
                return;
            }

            VoiceParticipantListItemView item = Instantiate(participantItemPrefab, participantListContent, false);
            if (!item.Bind(userId, userName, directionalControlClient))
            {
                Destroy(item.gameObject);
                return;
            }

            itemsByUserId.Add(userId, item);
        }

        private void RemoveInactiveItems()
        {
            usersToRemove.Clear();

            foreach (KeyValuePair<string, VoiceParticipantListItemView> pair in itemsByUserId)
            {
                if (!activeParticipantUserIds.Contains(pair.Key)) usersToRemove.Add(pair.Key);
            }

            for (int i = 0; i < usersToRemove.Count; i++)
            {
                string userId = usersToRemove[i];
                if (!itemsByUserId.TryGetValue(userId, out VoiceParticipantListItemView item)) continue;

                itemsByUserId.Remove(userId);
                if (item != null) Destroy(item.gameObject);
            }
        }

        private void ClearItems()
        {
            foreach (VoiceParticipantListItemView item in itemsByUserId.Values)
            {
                if (item != null) Destroy(item.gameObject);
            }

            itemsByUserId.Clear();
            activeParticipantUserIds.Clear();
            usersToRemove.Clear();
        }

        private string ResolveUserName(string userId)
        {
            return userNamesByUserId.TryGetValue(userId, out string userName) && !string.IsNullOrWhiteSpace(userName)
                ? userName
                : userId;
        }

        private static string ResolveLocalUserId()
        {
            DedicatedGameServerWsClient wsClient = DedicatedGameServerWsClient.Instance;
            return wsClient == null ? string.Empty : Normalize(wsClient.UserId);
        }

        private bool ValidateInspectorReferences()
        {
            return participantListContent != null && participantItemPrefab != null;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
