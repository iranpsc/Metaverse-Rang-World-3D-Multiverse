using System;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.Realtime.Lobby
{
    [Serializable]
    public class RealtimeCreateRoomResponseDto
    {
        public bool ok = false;
        public RealtimeRoomDto room = null;

        public bool HasRoom()
        {
            return ok && room != null && room.HasValidRoomId();
        }

        public void Normalize()
        {
            if (room != null) room.Normalize();
        }

        public static RealtimeCreateRoomResponseDto FromAckDetailsJson(string detailsJson)
        {
            if (string.IsNullOrWhiteSpace(detailsJson)) return null;

            try
            {
                RealtimeCreateRoomResponseDto response = JsonUtility.FromJson<RealtimeCreateRoomResponseDto>(detailsJson);
                response?.Normalize();
                return response;
            }
            catch (Exception ex)
            {
                Debug.LogError("[RealtimeCreateRoomResponseDto] FromAckDetailsJson failed: " + ex.Message);
                return null;
            }
        }

        public static RealtimeRoomDto ReadRoomFromAck(RealtimeAck ack)
        {
            if (ack == null || string.IsNullOrWhiteSpace(ack.detailsJson)) return null;

            RealtimeCreateRoomResponseDto response = FromAckDetailsJson(ack.detailsJson);
            if (response == null || !response.HasRoom()) return null;

            return response.room;
        }
    }
}