using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using UnityEngine;

namespace Network_A.Realtime.Lobby
{
    public class RealtimeLobbyClient : IDisposable
    {
        private readonly RealtimeClient realtimeClient;
        private bool isDisposed;

        public event Action<string> LogReceived;
        public event Action<RealtimeAck> AckReceived;
        public event Action<RealtimeError> ErrorReceived;
        public event Action<RealtimeRoomDto> RoomCreatedReceived;
        public event Action<RealtimeRoomDto> RoomUpdatedReceived;
        public event Action<RealtimeRoomDto> RoomClosedReceived;

        public RealtimeLobbyClient(RealtimeClient realtimeClient)
        {
            this.realtimeClient = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
            RegisterLobbyHandlers();
        }

        public async Task<RealtimeLobbyCreateRoomResult> CreateRoomAsync(RealtimeCreateRoomRequestDto request, RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return RealtimeLobbyCreateRoomResult.Failed("RealtimeLobbyClient is disposed.");
            if (request == null) return RealtimeLobbyCreateRoomResult.Failed("Create room request is null.");

            request.Normalize();

            if (!request.IsValid()) return RealtimeLobbyCreateRoomResult.Failed("Room name is invalid.");
            if (realtimeClient == null || !realtimeClient.IsConnected) return RealtimeLobbyCreateRoomResult.Failed("Realtime client is disconnected.");

            string payloadJson = request.ToPayloadJson();
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(
                RealtimeEnvelope.CreateMessageId("create_room"),
                RealtimeChannels.Lobby,
                RealtimeMessageTypes.CreateRoom,
                payloadJson,
                string.Empty,
                true
            );

            RealtimeReliableSendResult sendResult = await realtimeClient.SendEnvelopeReliableWithPolicyAsync(
                envelope,
                RealtimeDeliveryPolicy.ReliableNoQueue,
                true,
                options ?? RealtimeReliableSendOptions.Default(),
                cancellationToken
            );

            if (sendResult == null) return RealtimeLobbyCreateRoomResult.Failed("Create room result is null.");
            if (!sendResult.isSuccess) return RealtimeLobbyCreateRoomResult.Failed(sendResult.errorMessage, sendResult);

            RealtimeAck ack = sendResult.ack;
            if (ack == null) return RealtimeLobbyCreateRoomResult.Failed("Create room ack is null.", sendResult);

            AckReceived?.Invoke(ack);

            RealtimeRoomDto room = RealtimeCreateRoomResponseDto.ReadRoomFromAck(ack);
            if (room == null || !room.HasValidRoomId()) return RealtimeLobbyCreateRoomResult.Failed("Create room ack does not contain a valid room.", sendResult);

            LogReceived?.Invoke("Lobby room created: " + room.roomId);
            return RealtimeLobbyCreateRoomResult.Success(room, sendResult);
        }

        public async Task<RealtimeLobbyListRoomsResult> ListRoomsAsync(RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return RealtimeLobbyListRoomsResult.Failed("RealtimeLobbyClient is disposed.");
            if (realtimeClient == null || !realtimeClient.IsConnected) return RealtimeLobbyListRoomsResult.Failed("Realtime client is disconnected.");

            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(
                RealtimeEnvelope.CreateMessageId("list_rooms"),
                RealtimeChannels.Lobby,
                RealtimeMessageTypes.ListRooms,
                "{}",
                string.Empty,
                true
            );

            RealtimeReliableSendResult sendResult = await realtimeClient.SendEnvelopeReliableWithPolicyAsync(
                envelope,
                RealtimeDeliveryPolicy.ReliableNoQueue,
                true,
                options ?? RealtimeReliableSendOptions.Default(),
                cancellationToken
            );

            if (sendResult == null) return RealtimeLobbyListRoomsResult.Failed("List rooms result is null.");
            if (!sendResult.isSuccess) return RealtimeLobbyListRoomsResult.Failed(sendResult.errorMessage, sendResult);

            RealtimeAck ack = sendResult.ack;
            if (ack == null) return RealtimeLobbyListRoomsResult.Failed("List rooms ack is null.", sendResult);

            AckReceived?.Invoke(ack);

            RealtimeRoomListResponseDto response = RealtimeRoomListResponseDto.FromAck(ack);
            if (response == null) return RealtimeLobbyListRoomsResult.Failed("List rooms ack details are invalid.", sendResult);

            response.Normalize();
            LogReceived?.Invoke("Lobby rooms listed. count=" + response.count);
            return RealtimeLobbyListRoomsResult.Success(response, sendResult);
        }

        private void RegisterLobbyHandlers()
        {
            realtimeClient.Router.RegisterHandler(RealtimeChannels.Lobby, RealtimeMessageTypes.RoomCreated, HandleRoomCreatedEnvelope);
            realtimeClient.Router.RegisterHandler(RealtimeChannels.Lobby, RealtimeMessageTypes.RoomUpdated, HandleRoomUpdatedEnvelope);
            realtimeClient.Router.RegisterHandler(RealtimeChannels.Lobby, RealtimeMessageTypes.RoomClosed, HandleRoomClosedEnvelope);
            realtimeClient.ErrorEnvelopeReceived += HandleErrorEnvelope;
        }

        private void UnregisterLobbyHandlers()
        {
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.Lobby, RealtimeMessageTypes.RoomCreated);
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.Lobby, RealtimeMessageTypes.RoomUpdated);
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.Lobby, RealtimeMessageTypes.RoomClosed);
            realtimeClient.ErrorEnvelopeReceived -= HandleErrorEnvelope;
        }

        private void HandleRoomCreatedEnvelope(RealtimeEnvelope envelope)
        {
            RealtimeRoomDto room = ReadRoomFromBroadcast(envelope);
            if (room == null) return;

            LogReceived?.Invoke("Lobby room_created received: " + room.roomId);
            RoomCreatedReceived?.Invoke(room);
        }

        private void HandleRoomUpdatedEnvelope(RealtimeEnvelope envelope)
        {
            RealtimeRoomDto room = ReadRoomFromBroadcast(envelope);
            if (room == null) return;

            LogReceived?.Invoke("Lobby room_updated received: " + room.roomId);
            RoomUpdatedReceived?.Invoke(room);
        }

        private void HandleRoomClosedEnvelope(RealtimeEnvelope envelope)
        {
            RealtimeRoomDto room = ReadRoomFromBroadcast(envelope);
            if (room == null) return;

            LogReceived?.Invoke("Lobby room_closed received: " + room.roomId);
            RoomClosedReceived?.Invoke(room);
        }

        private void HandleErrorEnvelope(RealtimeError error)
        {
            if (error == null) return;
            ErrorReceived?.Invoke(error);
            LogReceived?.Invoke("Lobby error: " + error.code + " | " + error.message);
        }

        private static RealtimeRoomDto ReadRoomFromBroadcast(RealtimeEnvelope envelope)
        {
            if (envelope == null || string.IsNullOrWhiteSpace(envelope.payloadJson)) return null;

            string roomJson = RealtimeJsonUtil.ReadRawValue(envelope.payloadJson, "room", "{}");
            RealtimeRoomDto room = RealtimeRoomDto.FromJson(roomJson);
            if (room == null || !room.HasValidRoomId()) return null;

            return room;
        }

        public void Dispose()
        {
            if (isDisposed) return;

            isDisposed = true;
            UnregisterLobbyHandlers();
        }
    }

    [Serializable]
    public class RealtimeLobbyCreateRoomResult
    {
        public bool isSuccess;
        public string errorMessage = string.Empty;
        public RealtimeRoomDto room;
        public RealtimeReliableSendResult sendResult;

        public static RealtimeLobbyCreateRoomResult Success(RealtimeRoomDto room, RealtimeReliableSendResult sendResult)
        {
            return new RealtimeLobbyCreateRoomResult
            {
                isSuccess = true,
                room = room,
                sendResult = sendResult
            };
        }

        public static RealtimeLobbyCreateRoomResult Failed(string errorMessage, RealtimeReliableSendResult sendResult = null)
        {
            return new RealtimeLobbyCreateRoomResult
            {
                isSuccess = false,
                errorMessage = errorMessage ?? string.Empty,
                sendResult = sendResult
            };
        }
    }

    [Serializable]
    public class RealtimeLobbyListRoomsResult
    {
        public bool isSuccess;
        public string errorMessage = string.Empty;
        public RealtimeRoomListResponseDto response;
        public RealtimeReliableSendResult sendResult;

        public RealtimeRoomDto[] Rooms
        {
            get
            {
                if (response == null || response.rooms == null) return new RealtimeRoomDto[0];
                return response.rooms;
            }
        }

        public int Count
        {
            get
            {
                if (response == null) return 0;
                return response.count;
            }
        }

        public static RealtimeLobbyListRoomsResult Success(RealtimeRoomListResponseDto response, RealtimeReliableSendResult sendResult)
        {
            return new RealtimeLobbyListRoomsResult
            {
                isSuccess = true,
                response = response,
                sendResult = sendResult
            };
        }

        public static RealtimeLobbyListRoomsResult Failed(string errorMessage, RealtimeReliableSendResult sendResult = null)
        {
            return new RealtimeLobbyListRoomsResult
            {
                isSuccess = false,
                errorMessage = errorMessage ?? string.Empty,
                sendResult = sendResult
            };
        }
    }
}