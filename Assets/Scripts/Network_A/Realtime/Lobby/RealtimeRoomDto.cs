using System;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.Realtime.Lobby
{
    [Serializable]
    public class RealtimeRoomDto
    {
        public string roomId = string.Empty;
        public string roomName = string.Empty;
        public string description = string.Empty;
        public string ownerUserId = string.Empty;
        public string ownerUserName = string.Empty;
        public string visibility = "public";
        public string status = "open";
        public int maxPlayers = 20;
        public int onlineCount = 0;
        public long createdAtUnix = 0;
        public long updatedAtUnix = 0;
        public long lastActiveAtUnix = 0;
        public long closedAtUnix = 0;
        public bool canJoin = false;

        public bool HasValidRoomId()
        {
            return !string.IsNullOrWhiteSpace(roomId);
        }

        public bool IsPublic()
        {
            return string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsClosed()
        {
            return string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsFull()
        {
            return string.Equals(status, "full", StringComparison.OrdinalIgnoreCase) || onlineCount >= maxPlayers;
        }

        public bool CanJoin()
        {
            return canJoin && HasValidRoomId() && !IsClosed() && !IsFull();
        }

        public void Normalize()
        {
            if (roomId == null) roomId = string.Empty;
            if (roomName == null) roomName = string.Empty;
            if (description == null) description = string.Empty;
            if (ownerUserId == null) ownerUserId = string.Empty;
            if (ownerUserName == null) ownerUserName = string.Empty;
            if (string.IsNullOrWhiteSpace(visibility)) visibility = "public";
            if (string.IsNullOrWhiteSpace(status)) status = "open";
            if (maxPlayers <= 0) maxPlayers = 20;
            if (onlineCount < 0) onlineCount = 0;
        }

        public string ToDisplayText()
        {
            Normalize();
            return roomName + " | " + onlineCount + "/" + maxPlayers + " | " + status;
        }

        public static RealtimeRoomDto FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                RealtimeRoomDto room = JsonUtility.FromJson<RealtimeRoomDto>(json);
                room?.Normalize();
                return room;
            }
            catch (Exception ex)
            {
                Debug.LogError("[RealtimeRoomDto] FromJson failed: " + ex.Message);
                return null;
            }
        }
    }
}