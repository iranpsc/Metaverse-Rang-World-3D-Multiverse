using System;
using System.Threading.Tasks;
using Network_A.GameServer;
using Network_A.GameServer.Players;
using Network_A.GameServer.Protocol;
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
        [SerializeField] private DedicatedServerRuntime runtime;

        [Header("Rules")]
        [SerializeField] private bool closeConnectionOnAuthFailed = true;
        [SerializeField] private bool ignoreNonAuthMessagesBeforeAuth = true;
        [SerializeField] private bool rejectSecondAuthTicket = true;

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

        //* این تابع هنگام غیرفعال شدن آبجکت، رویدادها را پاک می کند.
        private void OnDisable()
        {
            UnsubscribeEvents();
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

            DedicatedMessageTypeDto typeDto = ParseMessageType(text);

            if (typeDto == null || string.IsNullOrWhiteSpace(typeDto.type))
            {
                if (!isAuthenticated && ignoreNonAuthMessagesBeforeAuth)
                {
                    await SendAuthFailedAsync(connection, "invalid_message", "Message type is missing.");
                }

                return;
            }

            if (typeDto.type == "auth_ticket")
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
                      connection.ConnectionId + " | type=" + typeDto.type);
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

            DedicatedAuthTicketMessageDto authMessage = ParseAuthTicketMessage(text);

            if (authMessage == null)
            {
                await SendAuthFailedAndMaybeCloseAsync(connection, "auth_ticket_parse_failed", "Auth ticket message could not be parsed.");
                return;
            }

            Debug.Log("[DedicatedTicketHandshakeHandler] Auth ticket received | connectionId=" +
                      connection.ConnectionId + " | userId=" + authMessage.userId);

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

            await connection.SendTextAsync(json);

            Debug.Log("[DedicatedTicketHandshakeHandler] Auth ok sent | connectionId=" +
                      connection.ConnectionId + " | userId=" + message.userId);
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

            await connection.SendTextAsync(json);

            Debug.LogWarning("[DedicatedTicketHandshakeHandler] Auth failed sent | connectionId=" +
                             connection.ConnectionId + " | reason=" + message.reason);
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

        //* این تابع هنگام قطع کانکشن، پلیر مربوط به آن را از رجیستری حذف می کند.
        private void HandleClientDisconnected(DedicatedWebSocketConnection connection, string reason)
        {
            if (connection == null) return;

            EnsureReferences();

            if (playerRegistry != null)
            {
                playerRegistry.RemoveByConnectionId(connection.ConnectionId, reason);
            }

            Debug.Log("[DedicatedTicketHandshakeHandler] Client disconnected | connectionId=" +
                      connection.ConnectionId + " | reason=" + reason);
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
