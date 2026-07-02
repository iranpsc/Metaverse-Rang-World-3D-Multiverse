using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedGameServerWsClient : MonoBehaviour
    {
        public static DedicatedGameServerWsClient Instance { get; private set; }

        [Header("Connection")]
        [SerializeField] private string defaultHost = "127.0.0.1";
        [SerializeField] private int defaultPort = 7777;
        [SerializeField] private bool useSecureWebSocket = false;
        [SerializeField] private int connectTimeoutSeconds = 10;
        [SerializeField] private int authTimeoutSeconds = 10;

        [Header("Realtime Transport")]
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logRawMessages = true;

        private IRealtimeTransport transport;
        private CancellationTokenSource connectionCts;
        private TaskCompletionSource<bool> authCompletionSource;
        private bool transportEventsBound;

        public bool IsConnected { get; private set; }
        public bool IsAuthenticated { get; private set; }

        public string ConnectionId { get; private set; }
        public string UserId { get; private set; }
        public string PlayerId { get; private set; }
        public string RoomId { get; private set; }
        public string ServerId { get; private set; }
        public string SessionId { get; private set; }
        public string LastAuthReason { get; private set; }
        public string LastError { get; private set; }
        public string LastRawMessage { get; private set; }
        public string LastMessageFormat { get; private set; }
        public string LastRoute { get; private set; }
        public string LastMirrorLikeRoute { get; private set; }
        public string LastPayloadJson { get; private set; }
        public string LastEnvelopeChannel { get; private set; }
        public string LastEnvelopeType { get; private set; }
        public string LastEnvelopeRoom { get; private set; }
        public bool IsReady => IsConnected && IsAuthenticated;

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<string> RawMessageReceived;
        public event Action<RealtimeEnvelope> EnvelopeReceived;
        public event Action<string, string, string> RoutedMessageReceived;
        public event Action Authenticated;
        public event Action<string> AuthFailed;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                return;
            }

            if (Instance != this)
            {
                Destroy(gameObject);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Disconnect("destroyed");
                Instance = null;
            }
        }

        [ContextMenu("Connect Default Dedicated Server")]
        public async void Btn_ConnectDefault()
        {
            await ConnectToDedicatedServerAsync(defaultHost, defaultPort, useSecureWebSocket);
        }

        [ContextMenu("Disconnect Dedicated Server")]
        public void Btn_Disconnect()
        {
            Disconnect("manual_disconnect");
        }

        public async Task<bool> ConnectToDedicatedServerAsync(
            string host,
            int port,
            bool secure,
            CancellationToken cancellationToken = default)
        {
            return await ConnectToDedicatedServerAsync(host, port, secure, string.Empty, cancellationToken);
        }

        public async Task<bool> ConnectToDedicatedServerAsync(
            string host,
            int port,
            bool secure,
            string path,
            CancellationToken cancellationToken = default)
        {
            string safeHost = string.IsNullOrWhiteSpace(host) ? defaultHost : host.Trim();
            int safePort = Mathf.Max(1, port);
            string url = BuildWebSocketUrl(safeHost, safePort, secure, path);

            return await ConnectAsync(url, cancellationToken);
        }

        private string BuildWebSocketUrl(string host, int port, bool secure, string path)
        {
            string safeHost = string.IsNullOrWhiteSpace(host) ? defaultHost : host.Trim();
            int safePort = Mathf.Max(1, port);
            string scheme = secure ? "wss" : "ws";
            string safePath = NormalizeWebSocketPath(path);
            bool omitDefaultPort = (secure && safePort == 443) || (!secure && safePort == 80);

            if (omitDefaultPort)
            {
                return scheme + "://" + safeHost + safePath;
            }

            return scheme + "://" + safeHost + ":" + safePort + safePath;
        }

        private string NormalizeWebSocketPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string safePath = path.Trim();
            if (!safePath.StartsWith("/")) safePath = "/" + safePath;

            return safePath;
        }

        public async Task<bool> ConnectAsync(string url, CancellationToken cancellationToken = default)
        {
            if (IsConnected && transport != null && transport.IsConnected)
            {
                Log("Already connected.");
                return true;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return Fail("websocket_url_empty");
            }

            try
            {
                await CleanupTransportOnlyAsync("reconnect_cleanup");

                transport = RealtimeTransportFactory.Create(transportKind == RealtimeTransportKind.Auto ? RealtimeTransportKind.WebSocket : transportKind);
                if (transport == null)
                {
                    return Fail("realtime_transport_not_registered | kind=" + transportKind);
                }

                BindTransportEvents(transport);
                connectionCts = new CancellationTokenSource();

                using (CancellationTokenSource connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCts.Token))
                {
                    connectTimeoutCts.CancelAfter(Mathf.Max(1, connectTimeoutSeconds) * 1000);

                    Log("Connecting | url=" + url + " | transport=" + transport.Kind);
                    bool connected = await transport.ConnectAsync(url, new Dictionary<string, string>(), connectTimeoutCts.Token);

                    if (!connected || !transport.IsConnected)
                    {
                        return Fail("websocket_connect_failed");
                    }
                }

                if (!IsConnected)
                {
                    HandleTransportConnected();
                }

                IsAuthenticated = false;
                LastError = string.Empty;

                Log("Connected.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("connect_cancelled_or_timeout");
            }
            catch (Exception ex)
            {
                return Fail("connect_exception | " + ex.Message);
            }
        }

        public async Task<bool> AuthenticateWithTicketAsync(
            string ticketId,
            string signature,
            string userId,
            string roomId,
            string serverId,
            string sessionId,
            string playerId = "",
            string userName = "",
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || transport == null || !transport.IsConnected)
            {
                return Fail("websocket_not_connected");
            }

            if (string.IsNullOrWhiteSpace(ticketId)) return Fail("ticket_id_empty");
            if (string.IsNullOrWhiteSpace(signature)) return Fail("signature_empty");
            if (string.IsNullOrWhiteSpace(userId)) return Fail("user_id_empty");
            if (string.IsNullOrWhiteSpace(roomId)) return Fail("room_id_empty");
            if (string.IsNullOrWhiteSpace(serverId)) return Fail("server_id_empty");
            if (string.IsNullOrWhiteSpace(sessionId)) return Fail("session_id_empty");

            DedicatedAuthTicketDto authTicket = new DedicatedAuthTicketDto
            {
                type = RealtimeMessageTypes.AuthTicket,
                ticketId = ticketId.Trim(),
                signature = signature.Trim(),
                userId = userId.Trim(),
                roomId = roomId.Trim(),
                serverId = serverId.Trim(),
                sessionId = sessionId.Trim(),
                playerId = string.IsNullOrWhiteSpace(playerId) ? userId.Trim() : playerId.Trim(),
                userName = string.IsNullOrWhiteSpace(userName) ? userId.Trim() : userName.Trim()
            };

            UserId = authTicket.userId;
            PlayerId = authTicket.playerId;
            RoomId = authTicket.roomId;
            ServerId = authTicket.serverId;
            SessionId = authTicket.sessionId;

            authCompletionSource = new TaskCompletionSource<bool>();

            string payloadJson = JsonUtility.ToJson(authTicket);
            bool sent = await SendEnvelopeAsync(
                RealtimeChannels.System,
                RealtimeMessageTypes.AuthTicket,
                payloadJson,
                authTicket.roomId,
                false,
                cancellationToken);

            if (!sent)
            {
                return Fail("auth_ticket_send_failed");
            }

            using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(Mathf.Max(1, authTimeoutSeconds) * 1000);

                try
                {
                    using (timeoutCts.Token.Register(() => authCompletionSource.TrySetCanceled()))
                    {
                        bool authOk = await authCompletionSource.Task;
                        return authOk;
                    }
                }
                catch (OperationCanceledException)
                {
                    return Fail("auth_timeout");
                }
            }
        }

        public async Task<bool> SendPlayerStateAsync(
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            long sequence,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || !IsAuthenticated)
            {
                return false;
            }

            DedicatedPlayerStateDto message = new DedicatedPlayerStateDto
            {
                type = RealtimeMessageTypes.PlayerState,
                userId = UserId,
                playerId = PlayerId,
                roomId = RoomId,
                serverId = ServerId,
                sessionId = SessionId,
                sequence = sequence,
                timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),

                px = position.x,
                py = position.y,
                pz = position.z,

                rx = rotation.x,
                ry = rotation.y,
                rz = rotation.z,
                rw = rotation.w,

                vx = velocity.x,
                vy = velocity.y,
                vz = velocity.z
            };

            return await SendEnvelopeAsync(
                RealtimeChannels.Presence,
                RealtimeMessageTypes.PlayerState,
                JsonUtility.ToJson(message),
                RoomId,
                false,
                cancellationToken);
        }

        public async Task<bool> SendRawAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!IsConnected || transport == null || !transport.IsConnected)
            {
                return false;
            }

            try
            {
                string safeText = text ?? string.Empty;
                bool sent = await transport.SendAsync(safeText, cancellationToken);

                if (sent)
                {
                    Log("Sent message | messageFormat=" + ReadMessageFormatForLog(safeText) +
                        " | route=" + ReadRouteForLog(safeText));

                    if (logRawMessages)
                    {
                        Log("Sent raw | " + safeText);
                    }
                }

                if (!sent)
                {
                    LastError = "realtime_transport_send_failed";
                }

                return sent;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogWarning("[DedicatedGameServerWsClient] Send failed | " + ex.Message);
                return false;
            }
        }



        public async Task<bool> SendGameEnvelopeAsync(
            string messageType,
            string payloadJson,
            bool requiresAck = false,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || !IsAuthenticated) return false;
            return await SendEnvelopeAsync(RealtimeChannels.Game, SafeTrim(messageType), string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson.Trim(), RoomId, requiresAck, cancellationToken);
        }

        public async Task<bool> SendNetworkRpcPayloadAsync(
            global::MetaverseNetworkRpcPayload payload,
            string messageType,
            CancellationToken cancellationToken = default)
        {
            if (payload == null) return false;
            payload.roomId = string.IsNullOrWhiteSpace(payload.roomId) ? RoomId : payload.roomId.Trim();
            payload.senderConnectionId = string.IsNullOrWhiteSpace(payload.senderConnectionId) ? ConnectionId : payload.senderConnectionId.Trim();
            payload.senderUserId = string.IsNullOrWhiteSpace(payload.senderUserId) ? UserId : payload.senderUserId.Trim();
            payload.senderPlayerId = string.IsNullOrWhiteSpace(payload.senderPlayerId) ? PlayerId : payload.senderPlayerId.Trim();
            payload.NormalizeDefaults();

            string safeType = string.IsNullOrWhiteSpace(messageType) ? payload.type : messageType.Trim();
            string rawJson = global::MetaverseNetworkRpcMessageCodec.CreateEnvelopeJson(safeType, payload, payload.roomId);
            return await SendRawAsync(rawJson, cancellationToken);
        }

        public async Task<bool> SendMirrorLikeCommandAsync(
            int netId,
            string prefabId,
            string commandName,
            string payloadJson = "{}",
            CancellationToken cancellationToken = default)
        {
            var payload = new global::MetaverseNetworkRpcPayload
            {
                type = global::MetaverseDedicatedMessageTypes.Command,
                netId = netId,
                prefabId = string.IsNullOrWhiteSpace(prefabId) ? string.Empty : prefabId.Trim(),
                methodName = string.IsNullOrWhiteSpace(commandName) ? string.Empty : commandName.Trim(),
                commandName = string.IsNullOrWhiteSpace(commandName) ? string.Empty : commandName.Trim(),
                payloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson.Trim(),
                roomId = RoomId,
                senderConnectionId = ConnectionId,
                senderUserId = UserId,
                senderPlayerId = PlayerId,
                mirrorRoute = global::MetaverseDedicatedMessageTypes.MirrorRouteCmd,
                clientTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            return await SendNetworkRpcPayloadAsync(payload, global::MetaverseDedicatedMessageTypes.Command, cancellationToken);
        }

        public async Task<bool> SendPlayerInputPayloadAsync(
            global::MetaverseNetworkPlayerInputPayload payload,
            CancellationToken cancellationToken = default)
        {
            if (payload == null) return false;
            payload.roomId = string.IsNullOrWhiteSpace(payload.roomId) ? RoomId : payload.roomId.Trim();
            payload.connectionId = string.IsNullOrWhiteSpace(payload.connectionId) ? ConnectionId : payload.connectionId.Trim();
            payload.userId = string.IsNullOrWhiteSpace(payload.userId) ? UserId : payload.userId.Trim();
            payload.playerId = string.IsNullOrWhiteSpace(payload.playerId) ? PlayerId : payload.playerId.Trim();
            payload.NormalizeDefaults();

            string rawJson = global::MetaverseNetworkPlayerInputMessageCodec.CreatePlayerInputEnvelopeJson(payload, payload.roomId);
            return await SendRawAsync(rawJson, cancellationToken);
        }

        public async Task<bool> SendMirrorLikeOwnerInputAsync(
            int netId,
            string prefabId,
            float moveX,
            float moveZ,
            float deltaTime,
            long sequence,
            CancellationToken cancellationToken = default)
        {
            var payload = new global::MetaverseNetworkPlayerInputPayload
            {
                type = global::MetaverseDedicatedMessageTypes.PlayerInput,
                netId = netId,
                prefabId = string.IsNullOrWhiteSpace(prefabId) ? string.Empty : prefabId.Trim(),
                roomId = RoomId,
                connectionId = ConnectionId,
                userId = UserId,
                playerId = PlayerId,
                moveX = moveX,
                moveZ = moveZ,
                deltaTime = deltaTime,
                sequence = sequence,
                mirrorRoute = global::MetaverseDedicatedMessageTypes.MirrorRouteOwnerInput,
                clientTimeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            return await SendPlayerInputPayloadAsync(payload, cancellationToken);
        }

        public string GetDebugSummary()
        {
            return "connected=" + IsConnected +
                   " | authenticated=" + IsAuthenticated +
                   " | ready=" + IsReady +
                   " | connectionId=" + ConnectionId +
                   " | userId=" + UserId +
                   " | playerId=" + PlayerId +
                   " | roomId=" + RoomId +
                   " | serverId=" + ServerId +
                   " | sessionId=" + SessionId +
                   " | lastRoute=" + LastRoute +
                   " | lastMirrorRoute=" + LastMirrorLikeRoute +
                   " | lastError=" + LastError;
        }

        public void ClearLastMessageDiagnostics()
        {
            LastRawMessage = string.Empty;
            LastMessageFormat = string.Empty;
            LastRoute = string.Empty;
            LastMirrorLikeRoute = string.Empty;
            LastPayloadJson = string.Empty;
            LastEnvelopeChannel = string.Empty;
            LastEnvelopeType = string.Empty;
            LastEnvelopeRoom = string.Empty;
        }


        public void Disconnect(string reason = "client_disconnect")
        {
            bool wasConnected = IsConnected;
            IRealtimeTransport transportToDisconnect = transport;

            try
            {
                connectionCts?.Cancel();
            }
            catch
            {
            }

            UnbindTransportEvents();
            transport = null;
            IsConnected = false;
            IsAuthenticated = false;
            authCompletionSource?.TrySetResult(false);

            if (transportToDisconnect != null)
            {
                _ = DisconnectTransportAsync(transportToDisconnect, reason);
            }

            CleanupSocketOnly();

            if (wasConnected)
            {
                Disconnected?.Invoke(reason);
            }

            Log("Disconnected | reason=" + reason);
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

        private void HandleRawMessage(string message)
        {
            LastRawMessage = message ?? string.Empty;
            LastMessageFormat = ReadMessageFormatForLog(LastRawMessage);
            LastRoute = ReadRouteForLog(LastRawMessage);
            LastMirrorLikeRoute = ReadMirrorLikeRouteForLog(LastRawMessage);
            LastPayloadJson = ReadPayloadJsonForLog(LastRawMessage);

            Log("Received message | messageFormat=" + LastMessageFormat +
                " | route=" + LastRoute +
                " | mirrorRoute=" + LastMirrorLikeRoute);

            if (logRawMessages)
            {
                Log("Received raw | " + LastRawMessage);
            }

            RawMessageReceived?.Invoke(LastRawMessage);
            RoutedMessageReceived?.Invoke(LastMessageFormat, LastRoute, LastRawMessage);

            if (TryParseEnvelope(LastRawMessage, out RealtimeEnvelope envelope))
            {
                LastEnvelopeChannel = envelope.ch;
                LastEnvelopeType = envelope.t;
                LastEnvelopeRoom = envelope.room;
                LastPayloadJson = envelope.payloadJson;
                EnvelopeReceived?.Invoke(envelope);

                if (envelope.ch == RealtimeChannels.System && envelope.t == RealtimeMessageTypes.AuthOk)
                {
                    HandleAuthOk(envelope.payloadJson);
                    return;
                }

                if (envelope.ch == RealtimeChannels.System && envelope.t == RealtimeMessageTypes.AuthFailed)
                {
                    HandleAuthFailed(envelope.payloadJson);
                    return;
                }

                return;
            }

            DedicatedMessageTypeDto typeDto = ParseMessageType(LastRawMessage);
            if (typeDto == null || string.IsNullOrWhiteSpace(typeDto.type)) return;

            if (typeDto.type == "server_hello")
            {
                return;
            }

            if (typeDto.type == "auth_ok")
            {
                HandleAuthOk(LastRawMessage);
                return;
            }

            if (typeDto.type == "auth_failed")
            {
                HandleAuthFailed(LastRawMessage);
                return;
            }
        }

        private void HandleAuthOk(string message)
        {
            DedicatedAuthOkDto authOk = null;

            try
            {
                authOk = JsonUtility.FromJson<DedicatedAuthOkDto>(message);
            }
            catch
            {
            }

            IsAuthenticated = authOk != null && authOk.ok;

            if (IsAuthenticated)
            {
                ConnectionId = authOk.connectionId;
                UserId = authOk.userId;
                PlayerId = authOk.playerId;
                RoomId = authOk.roomId;
                ServerId = authOk.serverId;
                SessionId = authOk.sessionId;
                LastAuthReason = authOk.reason;
                LastError = string.Empty;

                authCompletionSource?.TrySetResult(true);
                Authenticated?.Invoke();

                Log("Authenticated | userId=" + UserId + " | playerId=" + PlayerId +
                    " | messageFormat=envelope_expected");
            }
            else
            {
                authCompletionSource?.TrySetResult(false);
            }
        }

        private void HandleAuthFailed(string message)
        {
            DedicatedAuthFailedDto failed = null;

            try
            {
                failed = JsonUtility.FromJson<DedicatedAuthFailedDto>(message);
            }
            catch
            {
            }

            IsAuthenticated = false;
            LastAuthReason = failed != null ? failed.reason : "auth_failed";
            LastError = failed != null ? failed.message : message;

            authCompletionSource?.TrySetResult(false);
            AuthFailed?.Invoke(LastAuthReason);

            Debug.LogWarning("[DedicatedGameServerWsClient] Auth failed | reason=" + LastAuthReason + " | message=" + LastError);
        }

        private DedicatedMessageTypeDto ParseMessageType(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;

            if (TryParseEnvelope(message, out RealtimeEnvelope envelope))
            {
                return new DedicatedMessageTypeDto { type = envelope.t };
            }

            try
            {
                return JsonUtility.FromJson<DedicatedMessageTypeDto>(message);
            }
            catch
            {
                return null;
            }
        }

        private void HandleDisconnected(string reason)
        {
            bool wasConnected = IsConnected;

            CleanupSocketOnly();
            UnbindTransportEvents();
            transport = null;

            IsConnected = false;
            IsAuthenticated = false;
            authCompletionSource?.TrySetResult(false);

            if (wasConnected)
            {
                Disconnected?.Invoke(reason);
            }

            Log("Disconnected | reason=" + reason);
        }

        private void CleanupSocketOnly()
        {
            try
            {
                if (connectionCts != null)
                {
                    connectionCts.Cancel();
                    connectionCts.Dispose();
                    connectionCts = null;
                }
            }
            catch
            {
            }
        }

        private async Task CleanupTransportOnlyAsync(string reason)
        {
            IRealtimeTransport oldTransport = transport;
            UnbindTransportEvents();
            transport = null;
            CleanupSocketOnly();

            if (oldTransport != null)
            {
                await DisconnectTransportAsync(oldTransport, reason);
            }

            IsConnected = false;
            IsAuthenticated = false;
        }

        private async Task DisconnectTransportAsync(IRealtimeTransport targetTransport, string reason)
        {
            if (targetTransport == null) return;

            try
            {
                await targetTransport.DisconnectAsync(reason ?? "client_disconnect", CancellationToken.None);
            }
            catch
            {
            }
        }

        private void BindTransportEvents(IRealtimeTransport targetTransport)
        {
            if (targetTransport == null || transportEventsBound) return;

            targetTransport.Connected += HandleTransportConnected;
            targetTransport.MessageReceived += HandleTransportMessageReceived;
            targetTransport.ErrorReceived += HandleTransportErrorReceived;
            targetTransport.Disconnected += HandleTransportDisconnected;
            transportEventsBound = true;
        }

        private void UnbindTransportEvents()
        {
            if (transport == null || !transportEventsBound)
            {
                transportEventsBound = false;
                return;
            }

            transport.Connected -= HandleTransportConnected;
            transport.MessageReceived -= HandleTransportMessageReceived;
            transport.ErrorReceived -= HandleTransportErrorReceived;
            transport.Disconnected -= HandleTransportDisconnected;
            transportEventsBound = false;
        }

        private void HandleTransportConnected()
        {
            IsConnected = true;
            LastError = string.Empty;
            Connected?.Invoke();
        }

        private void HandleTransportMessageReceived(string message)
        {
            HandleRawMessage(message);
        }

        private void HandleTransportErrorReceived(string error)
        {
            LastError = string.IsNullOrWhiteSpace(error) ? "realtime_transport_error" : error;
            Debug.LogWarning("[DedicatedGameServerWsClient] Transport error | " + LastError);
        }

        private void HandleTransportDisconnected(string reason)
        {
            HandleDisconnected(string.IsNullOrWhiteSpace(reason) ? "transport_disconnected" : reason);
        }

        private async Task<bool> SendEnvelopeAsync(
            string channel,
            string messageType,
            string payloadJson,
            string roomId,
            bool requiresAck,
            CancellationToken cancellationToken)
        {
            RealtimeEnvelope envelope = RealtimeEnvelope.Create(channel, messageType, payloadJson, roomId, requiresAck);
            return await SendRawAsync(envelope.ToJson(), cancellationToken);
        }

        private string ReadMessageFormatForLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "empty";
            if (TryParseEnvelope(message, out RealtimeEnvelope _)) return "envelope";

            DedicatedMessageTypeDto typeDto = TryParseLegacyType(message);
            if (typeDto != null && !string.IsNullOrWhiteSpace(typeDto.type)) return "legacy";

            return "invalid";
        }

        private string ReadRouteForLog(string message)
        {
            if (TryParseEnvelope(message, out RealtimeEnvelope envelope))
            {
                string channel = string.IsNullOrWhiteSpace(envelope.ch) ? "unknown" : envelope.ch.Trim();
                string type = string.IsNullOrWhiteSpace(envelope.t) ? "unknown" : envelope.t.Trim();
                return channel + "/" + type;
            }

            DedicatedMessageTypeDto typeDto = TryParseLegacyType(message);
            string legacyType = typeDto == null || string.IsNullOrWhiteSpace(typeDto.type) ? "unknown" : typeDto.type.Trim();
            return "legacy/" + legacyType;
        }

        private DedicatedMessageTypeDto TryParseLegacyType(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedMessageTypeDto>(message);
            }
            catch
            {
                return null;
            }
        }

        private bool TryParseEnvelope(string message, out RealtimeEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(message)) return false;

            RealtimeEnvelope parsed = RealtimeEnvelope.FromJson(message);
            if (parsed == null || !parsed.IsValidBasic()) return false;

            envelope = parsed;
            return true;
        }



        private string ReadMirrorLikeRouteForLog(string message)
        {
            if (TryParseEnvelope(message, out RealtimeEnvelope envelope))
            {
                return global::MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(envelope.t);
            }

            DedicatedMessageTypeDto typeDto = TryParseLegacyType(message);
            string legacyType = typeDto == null || string.IsNullOrWhiteSpace(typeDto.type) ? string.Empty : typeDto.type.Trim();
            return global::MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(legacyType);
        }

        private string ReadPayloadJsonForLog(string message)
        {
            if (TryParseEnvelope(message, out RealtimeEnvelope envelope)) return envelope.payloadJson;
            return message ?? string.Empty;
        }

        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private bool Fail(string error)
        {
            LastError = error;
            Debug.LogError("[DedicatedGameServerWsClient] " + error);
            return false;
        }

        private void Log(string message)
        {
            if (!verboseLogs) return;
            Debug.Log("[DedicatedGameServerWsClient] " + message);
        }

        [Serializable]
        private class DedicatedMessageTypeDto
        {
            public string type;
        }

        [Serializable]
        private class DedicatedAuthTicketDto
        {
            public string type;
            public string ticketId;
            public string signature;
            public string userId;
            public string roomId;
            public string serverId;
            public string sessionId;
            public string playerId;
            public string userName;
        }

        [Serializable]
        private class DedicatedAuthOkDto
        {
            public string type;
            public bool ok;
            public string reason;
            public string userId;
            public string playerId;
            public string connectionId;
            public string roomId;
            public string serverId;
            public string sessionId;
        }

        [Serializable]
        private class DedicatedAuthFailedDto
        {
            public string type;
            public bool ok;
            public string reason;
            public string message;
        }

        [Serializable]
        private class DedicatedPlayerStateDto
        {
            public string type;
            public string userId;
            public string playerId;
            public string roomId;
            public string serverId;
            public string sessionId;
            public long sequence;
            public long timestampUnixMs;

            public float px;
            public float py;
            public float pz;

            public float rx;
            public float ry;
            public float rz;
            public float rw;

            public float vx;
            public float vy;
            public float vz;
        }
    }
}
