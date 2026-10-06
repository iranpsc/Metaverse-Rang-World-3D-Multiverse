using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using Network_A.GameServer.Protocol;
using Network_A.Realtime.Protocol;
using Network_A.GameServer.WebSocket;
using UnityEngine;

namespace Network_A.GameServer.Auth
{
    public class DedicatedTicketHandshakeHandler : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedWebSocketServer webSocketServer;
        [SerializeField] private DedicatedTicketVerifier ticketVerifier;
        [SerializeField] private DedicatedPlayerRegistry playerRegistry;
        [SerializeField] private DedicatedPlayerStateStore playerStateStore;
        [SerializeField] private DedicatedServerRuntime runtime;

        [Header("Rules")]
        [SerializeField] private bool closeConnectionOnAuthFailed = true;
        [SerializeField] private bool ignoreNonAuthMessagesBeforeAuth = true;
        [SerializeField] private bool rejectSecondAuthTicket = true;

        [Header("Reconnect Grace")]
        [SerializeField] private bool preserveUnexpectedDisconnectForReconnect = true;
        [SerializeField] private float reconnectGraceSeconds = 210f;

        [Header("Debug")]
        [SerializeField] private bool logMessageFormat = true;

        private const string PlayerResumeStateMessageType = "player_resume_state";

        private readonly object reconnectGraceLock = new object();
        private readonly Dictionary<string, CancellationTokenSource> dict_reconnectGraceCtsByRoomUserKey =
            new Dictionary<string, CancellationTokenSource>();

        private bool eventsSubscribed;

        //* این تابع رفرنس های لازم را هنگام شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureReferences();
        }

        //* این تابع هنگام فعال شدن آبجکت، پیام های وب سوکت را گوش می دهد.
        private void OnEnable()
        {
            EnsureReferences();
            SubscribeEvents();
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، رویدادها و تایمرهای گریس ریکانکت را پاک می کند.
        private void OnDisable()
        {
            UnsubscribeEvents();
            CancelAllPendingReconnectGrace("handshake_handler_disabled");
        }

        //* این تابع رویدادهای وب سوکت را فقط یک بار وصل می کند.
        private void SubscribeEvents()
        {
            if (eventsSubscribed) return;

            if (webSocketServer != null)
            {
                webSocketServer.TextMessageReceived -= HandleTextMessageReceived;
                webSocketServer.ClientDisconnected -= HandleClientDisconnected;
                webSocketServer.TextMessageReceived += HandleTextMessageReceived;
                webSocketServer.ClientDisconnected += HandleClientDisconnected;
                eventsSubscribed = true;
            }
        }

        //* این تابع رویدادهای وب سوکت را جدا می کند.
        private void UnsubscribeEvents()
        {
            if (webSocketServer != null)
            {
                webSocketServer.TextMessageReceived -= HandleTextMessageReceived;
                webSocketServer.ClientDisconnected -= HandleClientDisconnected;
            }

            eventsSubscribed = false;
        }

        //* این تابع رفرنس های وب سوکت سرور، تیکت وریفایر، رجیستری پلیر و ران تایم را از همین آبجکت پیدا می کند.
        private void EnsureReferences()
        {
            if (webSocketServer == null)
            {
                webSocketServer = GetComponent<DedicatedWebSocketServer>();
                if (webSocketServer == null) webSocketServer = GetComponentInParent<DedicatedWebSocketServer>();
                if (webSocketServer == null) webSocketServer = GetComponentInChildren<DedicatedWebSocketServer>(true);
            }

            if (ticketVerifier == null)
            {
                ticketVerifier = GetComponent<DedicatedTicketVerifier>();
                if (ticketVerifier == null) ticketVerifier = GetComponentInParent<DedicatedTicketVerifier>();
                if (ticketVerifier == null) ticketVerifier = GetComponentInChildren<DedicatedTicketVerifier>(true);
            }

            if (playerRegistry == null)
            {
                playerRegistry = GetComponent<DedicatedPlayerRegistry>();
                if (playerRegistry == null) playerRegistry = GetComponentInParent<DedicatedPlayerRegistry>();
                if (playerRegistry == null) playerRegistry = GetComponentInChildren<DedicatedPlayerRegistry>(true);
            }

            if (playerStateStore == null)
            {
                playerStateStore = GetComponent<DedicatedPlayerStateStore>();
                if (playerStateStore == null) playerStateStore = GetComponentInParent<DedicatedPlayerStateStore>();
                if (playerStateStore == null) playerStateStore = GetComponentInChildren<DedicatedPlayerStateStore>(true);
#if UNITY_2023_1_OR_NEWER
                if (playerStateStore == null) playerStateStore = FindFirstObjectByType<DedicatedPlayerStateStore>();
#else
                if (playerStateStore == null) playerStateStore = FindObjectOfType<DedicatedPlayerStateStore>();
#endif
            }

            if (runtime == null)
            {
                runtime = GetComponent<DedicatedServerRuntime>();
                if (runtime == null) runtime = GetComponentInParent<DedicatedServerRuntime>();
                if (runtime == null) runtime = GetComponentInChildren<DedicatedServerRuntime>(true);
                if (runtime == null) runtime = DedicatedServerRuntime.Instance;
            }
        }

        //* این تابع پیام متنی وب سوکت را می گیرد و مسیر احراز یا پیام بعد از احراز را جدا می کند.
        private async void HandleTextMessageReceived(DedicatedWebSocketConnection connection, string text)
        {
            if (connection == null) return;

            EnsureReferences();

            bool isAuthenticated = playerRegistry != null &&
                                   playerRegistry.IsConnectionAuthenticated(connection.ConnectionId);

            string messageType = DedicatedRealtimeEnvelopeCodec.ReadMessageType(text);
            string messageChannel = DedicatedRealtimeEnvelopeCodec.ReadChannel(text);
            string messageFormat = DedicatedRealtimeEnvelopeCodec.ReadMessageFormat(text);
            string messageRoute = DedicatedRealtimeEnvelopeCodec.ReadRouteForLog(text);

            if (string.IsNullOrWhiteSpace(messageType))
            {
                if (!isAuthenticated && ignoreNonAuthMessagesBeforeAuth)
                {
                    await SendAuthFailedAsync(connection, "invalid_message", "Message type is missing.");
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(messageChannel) &&
                messageChannel != RealtimeChannels.System &&
                !isAuthenticated)
            {
                if (ignoreNonAuthMessagesBeforeAuth)
                {
                    await SendAuthFailedAsync(connection, "auth_ticket_required", "First valid message must be system/auth_ticket.");
                }

                return;
            }

            if (messageType == RealtimeMessageTypes.AuthTicket)
            {
                if (isAuthenticated && rejectSecondAuthTicket)
                {
                    await SendAuthFailedAsync(connection, "already_authenticated", "Connection is already authenticated.");
                    return;
                }

                await VerifyAuthTicketMessageAsync(connection, text);
                return;
            }

            if (!isAuthenticated)
            {
                if (ignoreNonAuthMessagesBeforeAuth)
                {
                    await SendAuthFailedAsync(connection, "auth_ticket_required", "First valid message must be auth_ticket.");
                }

                return;
            }

            playerRegistry.TouchConnection(connection.ConnectionId);

            Debug.Log("[DedicatedTicketHandshakeHandler] Authenticated message allowed | connectionId=" +
                      connection.ConnectionId + " | type=" + messageType +
                      " | messageFormat=" + messageFormat + " | route=" + messageRoute);
        }

        //* این تابع پیام auth_ticket را پارس می کند و نتیجه وریفای را به رجیستری پلیر وصل می کند.
        private async Task VerifyAuthTicketMessageAsync(DedicatedWebSocketConnection connection, string text)
        {
            if (ticketVerifier == null)
            {
                await SendAuthFailedAndMaybeCloseAsync(connection, "ticket_verifier_missing", "DedicatedTicketVerifier is missing.");
                return;
            }

            if (playerRegistry == null)
            {
                await SendAuthFailedAndMaybeCloseAsync(connection, "player_registry_missing", "DedicatedPlayerRegistry is missing.");
                return;
            }

            DedicatedAuthTicketMessageDto authMessage = ParseAuthTicketMessage(DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(text));

            if (authMessage == null)
            {
                await SendAuthFailedAndMaybeCloseAsync(connection, "auth_ticket_parse_failed", "Auth ticket message could not be parsed.");
                return;
            }

            if (logMessageFormat)
            {
                Debug.Log("[DedicatedTicketHandshakeHandler] Auth ticket received | connectionId=" +
                          connection.ConnectionId + " | userId=" + authMessage.userId +
                          " | messageFormat=" + DedicatedRealtimeEnvelopeCodec.ReadMessageFormat(text) +
                          " | route=" + DedicatedRealtimeEnvelopeCodec.ReadRouteForLog(text));
            }
            else
            {
                Debug.Log("[DedicatedTicketHandshakeHandler] Auth ticket received | connectionId=" +
                          connection.ConnectionId + " | userId=" + authMessage.userId);
            }

            DedicatedVerifyTicketResult result = await ticketVerifier.VerifyTicketAsync(authMessage, connection.ConnectionId);

            if (!result.IsSuccess)
            {
                await SendAuthFailedAndMaybeCloseAsync(connection, result.Reason, result.Message);
                return;
            }

            if (!TryBindRuntimeRoomFromVerifiedTicket(result, out string roomBindError))
            {
                await SendAuthFailedAndMaybeCloseAsync(connection, "runtime_room_bind_failed", roomBindError);
                return;
            }

            if (!playerRegistry.TryRegisterVerifiedPlayer(connection, result, out DedicatedPlayerSession session, out string registryError))
            {
                await SendAuthFailedAndMaybeCloseAsync(connection, "player_registry_failed", registryError);
                return;
            }

            CancelPendingReconnectGrace(
                session.roomId,
                session.userId,
                "player_reauthenticated"
            );

            await SendPlayerResumeStateIfAvailableAsync(connection, session);
            await SendAuthOkAsync(connection, result, session);
        }

        private bool TryBindRuntimeRoomFromVerifiedTicket(DedicatedVerifyTicketResult result, out string error)
        {
            error = string.Empty;

            if (result == null || result.Request == null)
            {
                error = "Verified ticket request is missing.";
                return false;
            }

            string roomId = result.Request.roomId;

            if (string.IsNullOrWhiteSpace(roomId))
            {
                error = "Verified ticket room id is empty.";
                return false;
            }

            EnsureReferences();

            if (runtime == null)
            {
                error = "DedicatedServerRuntime is missing.";
                return false;
            }

            bool ok = runtime.TryUpdateRuntimeRoom(roomId, string.Empty, out error);

            if (ok)
            {
                Debug.Log("[DedicatedTicketHandshakeHandler] Runtime room bound from ticket | roomId=" + roomId);
            }

            return ok;
        }

        //* این تابع قبل از auth_ok آخرین وضعیت معتبر پلیر را برای بازیابی ریکانکت ارسال می کند.
        private async Task<bool> SendPlayerResumeStateIfAvailableAsync(
            DedicatedWebSocketConnection connection,
            DedicatedPlayerSession session)
        {
            if (connection == null || session == null) return false;

            EnsureReferences();

            if (playerStateStore == null)
            {
                Debug.LogWarning("[DedicatedTicketHandshakeHandler] Player resume skipped. DedicatedPlayerStateStore is missing.");
                return false;
            }

            DedicatedPlayerStateRecord record =
                playerStateStore.GetByUserIdInRoom(session.roomId, session.userId);

            if (record == null || record.sequence <= 0)
            {
                Debug.Log("[DedicatedTicketHandshakeHandler] Player resume state not found | userId=" +
                          session.userId + " | roomId=" + session.roomId);

                return false;
            }

            bool rebound = playerStateStore.RebindConnectionForUser(
                session,
                "auth_ticket_verified_resume"
            );

            if (!rebound)
            {
                Debug.LogWarning("[DedicatedTicketHandshakeHandler] Player resume rebind failed | userId=" +
                                 session.userId + " | roomId=" + session.roomId);

                return false;
            }

            record = playerStateStore.GetByUserIdInRoom(
                session.roomId,
                session.userId
            );

            if (record == null) return false;

            DedicatedPlayerStateBroadcastDto message =
                DedicatedPlayerStateBroadcastDto.FromRecord(record);

            message.type = PlayerResumeStateMessageType;

            string envelopeJson = DedicatedRealtimeEnvelopeCodec.WrapPresencePayload(
                PlayerResumeStateMessageType,
                JsonUtility.ToJson(message),
                session.roomId
            );

            await connection.SendTextAsync(envelopeJson);

            Debug.Log("[DedicatedTicketHandshakeHandler] Player resume state sent before auth_ok | userId=" +
                      session.userId + " | playerId=" + session.playerId +
                      " | roomId=" + session.roomId +
                      " | sequence=" + record.sequence +
                      " | position=" + record.Position +
                      " | route=" + RealtimeChannels.Presence + "/" + PlayerResumeStateMessageType);

            return true;
        }

        //* این تابع بعد از وریفای و ثبت موفق، پیام auth_ok را به کلاینت ارسال می کند.
        private async Task SendAuthOkAsync(
            DedicatedWebSocketConnection connection,
            DedicatedVerifyTicketResult result,
            DedicatedPlayerSession session)
        {
            DedicatedAuthOkMessageDto message = new DedicatedAuthOkMessageDto
            {
                type = "auth_ok",
                ok = true,
                reason = result.Reason,
                userId = session != null ? session.userId : string.Empty,
                playerId = session != null ? session.playerId : string.Empty,
                connectionId = session != null ? session.connectionId : connection.ConnectionId,
                roomId = session != null ? session.roomId : string.Empty,
                serverId = session != null ? session.serverId : string.Empty,
                sessionId = session != null ? session.sessionId : string.Empty
            };

            string json = JsonUtility.ToJson(message);
            string envelopeJson = DedicatedRealtimeEnvelopeCodec.WrapSystemPayload(
                RealtimeMessageTypes.AuthOk,
                json,
                message.roomId);

            await connection.SendTextAsync(envelopeJson);

            Debug.Log("[DedicatedTicketHandshakeHandler] Auth ok sent | connectionId=" +
                      connection.ConnectionId + " | userId=" + message.userId +
                      " | messageFormat=envelope | route=" + RealtimeChannels.System + "/" + RealtimeMessageTypes.AuthOk);
        }

        //* این تابع پیام auth_failed را می فرستد و اگر قانون فعال باشد کانکشن را می بندد.
        private async Task SendAuthFailedAndMaybeCloseAsync(DedicatedWebSocketConnection connection, string reason, string messageText)
        {
            await SendAuthFailedAsync(connection, reason, messageText);

            if (closeConnectionOnAuthFailed)
            {
                await connection.CloseAsync(reason);
            }
        }

        //* این تابع پیام auth_failed را به کلاینت ارسال می کند.
        private async Task SendAuthFailedAsync(DedicatedWebSocketConnection connection, string reason, string messageText)
        {
            DedicatedAuthFailedMessageDto message = new DedicatedAuthFailedMessageDto
            {
                type = "auth_failed",
                ok = false,
                reason = string.IsNullOrWhiteSpace(reason) ? "auth_failed" : reason,
                message = string.IsNullOrWhiteSpace(messageText) ? "Auth failed." : messageText
            };

            string json = JsonUtility.ToJson(message);
            string envelopeJson = DedicatedRealtimeEnvelopeCodec.WrapSystemPayload(
                RealtimeMessageTypes.AuthFailed,
                json);

            await connection.SendTextAsync(envelopeJson);

            Debug.LogWarning("[DedicatedTicketHandshakeHandler] Auth failed sent | connectionId=" +
                             connection.ConnectionId + " | reason=" + message.reason +
                             " | messageFormat=envelope | route=" + RealtimeChannels.System + "/" + RealtimeMessageTypes.AuthFailed);
        }

        //* این تابع تایپ پیام ورودی را از جیسون می خواند.
        private DedicatedMessageTypeDto ParseMessageType(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedMessageTypeDto>(text);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedTicketHandshakeHandler] Type parse failed | " + ex.Message);
                return null;
            }
        }

        //* این تابع پیام auth_ticket را از جیسون می خواند.
        private DedicatedAuthTicketMessageDto ParseAuthTicketMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedAuthTicketMessageDto>(text);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedTicketHandshakeHandler] Auth ticket parse failed | " + ex.Message);
                return null;
            }
        }

        //* این تابع قطع عمدی را فوری حذف می کند و قطع غیرمنتظره را برای ریکانکت وارد گریس ۲۱۰ ثانیه ای می کند.
        private void HandleClientDisconnected(DedicatedWebSocketConnection connection, string reason)
        {
            if (connection == null) return;

            EnsureReferences();

            if (playerRegistry == null)
            {
                Debug.LogWarning(
                    "[DedicatedTicketHandshakeHandler] Client disconnect ignored. Player registry is missing | connectionId=" +
                    connection.ConnectionId + " | reason=" + NormalizeText(reason)
                );

                return;
            }

            DedicatedPlayerSession session =
                playerRegistry.GetByConnectionId(connection.ConnectionId);

            if (session == null)
            {
                Debug.Log(
                    "[DedicatedTicketHandshakeHandler] Client disconnect has no active registry session | connectionId=" +
                    connection.ConnectionId + " | reason=" + NormalizeText(reason)
                );

                return;
            }

            if (!preserveUnexpectedDisconnectForReconnect ||
                ShouldRemoveDisconnectedPlayerImmediately(reason))
            {
                CancelPendingReconnectGrace(
                    session.roomId,
                    session.userId,
                    "immediate_disconnect_cleanup"
                );

                playerRegistry.RemoveByConnectionId(
                    connection.ConnectionId,
                    NormalizeText(reason)
                );

                Debug.Log(
                    "[DedicatedTicketHandshakeHandler] Client disconnected and removed immediately | connectionId=" +
                    connection.ConnectionId + " | userId=" + session.userId +
                    " | roomId=" + session.roomId + " | reason=" + NormalizeText(reason)
                );

                return;
            }

            ScheduleReconnectGraceRemoval(session, reason, connection.InactiveSeconds);
        }

        //* این تابع حذف قطعی پلیر قطع شده را تا پایان گریس عقب می اندازد.
        private void ScheduleReconnectGraceRemoval(
            DedicatedPlayerSession session,
            string disconnectReason,
            float inactiveBeforeDisconnectSeconds)
        {
            if (session == null) return;

            string roomUserKey = BuildRoomUserKey(session.roomId, session.userId);
            if (string.IsNullOrWhiteSpace(roomUserKey)) return;

            CancellationTokenSource graceCts = new CancellationTokenSource();
            CancellationTokenSource previousCts = null;

            lock (reconnectGraceLock)
            {
                if (dict_reconnectGraceCtsByRoomUserKey.TryGetValue(
                        roomUserKey,
                        out previousCts))
                {
                    dict_reconnectGraceCtsByRoomUserKey.Remove(roomUserKey);
                }

                dict_reconnectGraceCtsByRoomUserKey[roomUserKey] = graceCts;
            }

            CancelAndDispose(previousCts);

            float contractGraceSeconds = Mathf.Max(1f, reconnectGraceSeconds);
            float safeInactiveBeforeDisconnectSeconds = Mathf.Clamp(inactiveBeforeDisconnectSeconds, 0f, contractGraceSeconds);
            float remainingGraceSeconds = Mathf.Max(1f, contractGraceSeconds - safeInactiveBeforeDisconnectSeconds);
            string safeDisconnectReason = NormalizeText(disconnectReason);

            Debug.Log(
                "[DedicatedTicketHandshakeHandler] Reconnect grace started | userId=" +
                session.userId + " | roomId=" + session.roomId +
                " | connectionId=" + session.connectionId +
                " | contractGraceSeconds=" + contractGraceSeconds.ToString("F1") +
                " | inactiveBeforeDisconnectSeconds=" + safeInactiveBeforeDisconnectSeconds.ToString("F1") +
                " | remainingGraceSeconds=" + remainingGraceSeconds.ToString("F1") +
                " | reason=" + safeDisconnectReason
            );

            _ = RemovePlayerAfterReconnectGraceAsync(
                roomUserKey,
                session.connectionId,
                session.roomId,
                session.userId,
                safeDisconnectReason,
                remainingGraceSeconds,
                contractGraceSeconds,
                safeInactiveBeforeDisconnectSeconds,
                graceCts
            );
        }

        //* این تابع بعد از پایان گریس فقط همان کانکشن قدیمی را حذف می کند؛ ریکانکت جدید را دست نمی زند.
        private async Task RemovePlayerAfterReconnectGraceAsync(
            string roomUserKey,
            string disconnectedConnectionId,
            string roomId,
            string userId,
            string disconnectReason,
            float remainingGraceSeconds,
            float contractGraceSeconds,
            float inactiveBeforeDisconnectSeconds,
            CancellationTokenSource graceCts)
        {
            try
            {
                int delayMilliseconds = Mathf.Max(
                    1000,
                    Mathf.RoundToInt(remainingGraceSeconds * 1000f)
                );

                await Task.Delay(delayMilliseconds, graceCts.Token);

                EnsureReferences();

                if (playerRegistry == null) return;

                DedicatedPlayerSession currentSession =
                    playerRegistry.GetByConnectionId(disconnectedConnectionId);

                if (currentSession == null)
                {
                    Debug.Log(
                        "[DedicatedTicketHandshakeHandler] Reconnect grace expired but old connection was already rebound or removed | userId=" +
                        NormalizeText(userId) + " | roomId=" + NormalizeText(roomId) +
                        " | oldConnectionId=" + NormalizeText(disconnectedConnectionId)
                    );

                    return;
                }

                if (!string.Equals(
                        NormalizeText(currentSession.userId),
                        NormalizeText(userId),
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        NormalizeText(currentSession.roomId),
                        NormalizeText(roomId),
                        StringComparison.Ordinal))
                {
                    Debug.LogWarning(
                        "[DedicatedTicketHandshakeHandler] Reconnect grace cleanup skipped because registry ownership changed | oldConnectionId=" +
                        NormalizeText(disconnectedConnectionId) +
                        " | expectedUserId=" + NormalizeText(userId) +
                        " | currentUserId=" + NormalizeText(currentSession.userId)
                    );

                    return;
                }

                string finalReason =
                    "reconnect_grace_expired:" + NormalizeText(disconnectReason);

                bool removed = playerRegistry.RemoveByConnectionId(
                    disconnectedConnectionId,
                    finalReason
                );

                Debug.Log(
                    "[DedicatedTicketHandshakeHandler] Reconnect grace expired | userId=" +
                    NormalizeText(userId) + " | roomId=" + NormalizeText(roomId) +
                    " | connectionId=" + NormalizeText(disconnectedConnectionId) +
                    " | removed=" + removed +
                    " | contractGraceSeconds=" + contractGraceSeconds.ToString("F1") +
                    " | inactiveBeforeDisconnectSeconds=" + inactiveBeforeDisconnectSeconds.ToString("F1") +
                    " | remainingGraceSeconds=" + remainingGraceSeconds.ToString("F1") +
                    " | reason=" + finalReason
                );
            }
            catch (OperationCanceledException)
            {
                Debug.Log(
                    "[DedicatedTicketHandshakeHandler] Reconnect grace cancelled | userId=" +
                    NormalizeText(userId) + " | roomId=" + NormalizeText(roomId) +
                    " | oldConnectionId=" + NormalizeText(disconnectedConnectionId)
                );
            }
            catch (Exception error)
            {
                Debug.LogError(
                    "[DedicatedTicketHandshakeHandler] Reconnect grace cleanup failed | userId=" +
                    NormalizeText(userId) + " | roomId=" + NormalizeText(roomId) +
                    " | oldConnectionId=" + NormalizeText(disconnectedConnectionId) +
                    " | error=" + error.Message
                );
            }
            finally
            {
                RemoveReconnectGraceToken(roomUserKey, graceCts);
            }
        }

        //* این تابع تایمر گریس همان یوزر و روم را بعد از ریکانکت یا خروج قطعی لغو می کند.
        private void CancelPendingReconnectGrace(
            string roomId,
            string userId,
            string reason)
        {
            string roomUserKey = BuildRoomUserKey(roomId, userId);
            if (string.IsNullOrWhiteSpace(roomUserKey)) return;

            CancellationTokenSource graceCts = null;

            lock (reconnectGraceLock)
            {
                if (dict_reconnectGraceCtsByRoomUserKey.TryGetValue(
                        roomUserKey,
                        out graceCts))
                {
                    dict_reconnectGraceCtsByRoomUserKey.Remove(roomUserKey);
                }
            }

            if (graceCts == null) return;

            CancelAndDispose(graceCts);

            Debug.Log(
                "[DedicatedTicketHandshakeHandler] Pending reconnect grace cancelled | userId=" +
                NormalizeText(userId) + " | roomId=" + NormalizeText(roomId) +
                " | reason=" + NormalizeText(reason)
            );
        }

        //* این تابع همه تایمرهای گریس را هنگام خاموش شدن هندلر لغو می کند.
        private void CancelAllPendingReconnectGrace(string reason)
        {
            List<CancellationTokenSource> graceTokens;

            lock (reconnectGraceLock)
            {
                graceTokens = new List<CancellationTokenSource>(
                    dict_reconnectGraceCtsByRoomUserKey.Values
                );

                dict_reconnectGraceCtsByRoomUserKey.Clear();
            }

            for (int i = 0; i < graceTokens.Count; i++)
            {
                CancelAndDispose(graceTokens[i]);
            }

            if (graceTokens.Count > 0)
            {
                Debug.Log(
                    "[DedicatedTicketHandshakeHandler] All reconnect grace timers cancelled | count=" +
                    graceTokens.Count + " | reason=" + NormalizeText(reason)
                );
            }
        }

        //* این تابع توکن پایان یافته را فقط اگر هنوز همان نمونه ثبت شده باشد از دیکشنری حذف می کند.
        private void RemoveReconnectGraceToken(
            string roomUserKey,
            CancellationTokenSource expectedCts)
        {
            lock (reconnectGraceLock)
            {
                if (!dict_reconnectGraceCtsByRoomUserKey.TryGetValue(
                        roomUserKey,
                        out CancellationTokenSource currentCts))
                {
                    return;
                }

                if (!ReferenceEquals(currentCts, expectedCts)) return;

                dict_reconnectGraceCtsByRoomUserKey.Remove(roomUserKey);
            }

            expectedCts?.Dispose();
        }

        //* این تابع دلایل خروج عمدی یا بسته شدن سرور را از قطع غیرمنتظره جدا می کند.
        private static bool ShouldRemoveDisconnectedPlayerImmediately(string reason)
        {
            string value = NormalizeText(reason).ToLowerInvariant();

            if (value.Contains("client_closed")) return true;
            if (value.Contains("manual")) return true;
            if (value.Contains("user_exit")) return true;
            if (value.Contains("leave_room")) return true;
            if (value.Contains("room_left")) return true;
            if (value.Contains("server_stopped")) return true;
            if (value.Contains("shutdown")) return true;
            if (value.Contains("auth_failed")) return true;
            if (value.Contains("kicked")) return true;
            if (value.Contains("duplicate_user_replaced")) return true;

            return false;
        }

        //* این تابع کلید پایدار گریس را از روم و یوزر می سازد.
        private static string BuildRoomUserKey(string roomId, string userId)
        {
            string safeRoomId = NormalizeText(roomId);
            string safeUserId = NormalizeText(userId);

            if (string.IsNullOrWhiteSpace(safeRoomId) ||
                string.IsNullOrWhiteSpace(safeUserId))
            {
                return string.Empty;
            }

            return safeRoomId + "::" + safeUserId;
        }

        //* این تابع متن های ورودی را برای مقایسه و لاگ امن می کند.
        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }

        //* این تابع توکن گریس را بدون پرتاب خطا لغو و آزاد می کند.
        private static void CancelAndDispose(CancellationTokenSource cts)
        {
            if (cts == null) return;

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            try
            {
                cts.Dispose();
            }
            catch
            {
            }
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت هندشیک ورود پلیر به یونیتی ددیکیتد سرور را مدیریت می کند.
        پیام auth_ticket از وب سوکت دریافت می شود و به DedicatedTicketVerifier سپرده می شود.
        اگر نود جی اس تیکت را تأیید کند، پلیر داخل DedicatedPlayerRegistry ثبت می شود.
        سپس پیام auth_ok برای کلاینت ارسال می شود.
        اگر تیکت نامعتبر باشد، auth_failed ارسال می شود و در صورت فعال بودن قانون، کانکشن بسته می شود.
        پیام های غیر از auth_ticket قبل از احراز اجازه ورود به منطق بازی را ندارند.
        */
    }
}
