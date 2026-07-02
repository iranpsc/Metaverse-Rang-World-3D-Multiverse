using System;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.Realtime.Lobby
{
    [Serializable]
    public class RealtimeRoomListResponseDto
    {
        public bool ok = false;
        public RealtimeRoomDto[] rooms = Array.Empty<RealtimeRoomDto>();
        public int count = 0;

        public bool HasRooms()
        {
            return ok && rooms != null && rooms.Length > 0;
        }

        public void Normalize()
        {
            if (rooms == null) rooms = Array.Empty<RealtimeRoomDto>();
            if (count < 0) count = 0;
            if (count == 0 && rooms.Length > 0) count = rooms.Length;

            for (int i = 0; i < rooms.Length; i++)
            {
                rooms[i]?.Normalize();
            }
        }

        public RealtimeRoomDto FindRoomById(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) || rooms == null) return null;

            string targetRoomId = roomId.Trim();

            for (int i = 0; i < rooms.Length; i++)
            {
                if (rooms[i] == null) continue;
                if (string.Equals(rooms[i].roomId, targetRoomId, StringComparison.OrdinalIgnoreCase)) return rooms[i];
            }

            return null;
        }

        public static RealtimeRoomListResponseDto FromAckDetailsJson(string detailsJson)
        {
            if (string.IsNullOrWhiteSpace(detailsJson)) return null;

            try
            {
                RealtimeRoomListResponseDto response = JsonUtility.FromJson<RealtimeRoomListResponseDto>(detailsJson);
                response?.Normalize();
                return response;
            }
            catch (Exception ex)
            {
                Debug.LogError("[RealtimeRoomListResponseDto] FromAckDetailsJson failed: " + ex.Message);
                return null;
            }
        }

        public static RealtimeRoomListResponseDto FromAck(RealtimeAck ack)
        {
            if (ack == null || string.IsNullOrWhiteSpace(ack.detailsJson)) return null;
            return FromAckDetailsJson(ack.detailsJson);
        }
    }
}