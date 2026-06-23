using System;
using Network_A.Realtime.Protocol;

namespace Network_A.Realtime.Lobby
{
    [Serializable]
    public class RealtimeCreateRoomRequestDto
    {
        public string roomName = string.Empty;
        public string description = string.Empty;
        public string visibility = "public";
        public int maxPlayers = 20;

        public RealtimeCreateRoomRequestDto()
        {
        }

        public RealtimeCreateRoomRequestDto(string roomName, string description = "", string visibility = "public", int maxPlayers = 20)
        {
            this.roomName = roomName ?? string.Empty;
            this.description = description ?? string.Empty;
            this.visibility = string.IsNullOrWhiteSpace(visibility) ? "public" : visibility.Trim();
            this.maxPlayers = maxPlayers;
            Normalize();
        }

        public void Normalize()
        {
            roomName = roomName == null ? string.Empty : roomName.Trim();
            description = description == null ? string.Empty : description.Trim();
            visibility = visibility == "private" ? "private" : "public";
            if (maxPlayers < 1) maxPlayers = 1;
            if (maxPlayers > 100) maxPlayers = 100;
        }

        public bool IsValid()
        {
            Normalize();
            return roomName.Length >= 2 && roomName.Length <= 64;
        }

        public string ToPayloadJson()
        {
            Normalize();

            return "{"
                + "\"roomName\":\"" + RealtimeJsonUtil.Escape(roomName) + "\","
                + "\"description\":\"" + RealtimeJsonUtil.Escape(description) + "\","
                + "\"visibility\":\"" + RealtimeJsonUtil.Escape(visibility) + "\","
                + "\"maxPlayers\":" + maxPlayers
                + "}";
        }
    }
}