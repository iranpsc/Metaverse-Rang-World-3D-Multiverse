using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using UnityEngine;

namespace Network_A.GameServer
{
    //* کلاینت گیم‌سرور است و فقط از RealtimeClient برای ارسال پیام‌های بازی استفاده می‌کند.
    public class GameServerClient : IDisposable
    {
        private readonly RealtimeClient realtimeClient;
        private readonly GameServerEvents events;
        private bool isDisposed;
        private string currentRoomId = string.Empty;
        private string lastKnownRoomId = string.Empty;

        public GameServerEvents Events => events;
        public string CurrentRoomId => currentRoomId;
        public string LastKnownRoomId => lastKnownRoomId;
        public bool HasRoom => !string.IsNullOrWhiteSpace(currentRoomId);
        public bool HasLastKnownRoom => !string.IsNullOrWhiteSpace(lastKnownRoomId);

        #region <Constructor>

        //* گیم‌سرورکلاینت را به کُر ریل‌تایم وصل می‌کند و هَندلِرهای گیم را ثبت می‌کند.
        public GameServerClient(RealtimeClient realtimeClient)
        {
            this.realtimeClient = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
            events = new GameServerEvents();
            RegisterGameHandlers();
        }

        #endregion

        #region <Room Flow>

        //* درخواست ورود به روم را از طریق کانال game به سرور می‌فرستد.
        public async Task<bool> JoinRoomAsync(string roomId, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return false;
            if (string.IsNullOrWhiteSpace(roomId)) return FailSend("Room id is empty.");

            string safeRoomId = EscapeJson(roomId.Trim());
            string payloadJson = "{\"roomId\":\"" + safeRoomId + "\"}";
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("join_room"), RealtimeChannels.Game, RealtimeMessageTypes.JoinRoom, payloadJson, roomId.Trim(), true);
            bool sent = await realtimeClient.SendEnvelopeWithPolicyAsync(envelope, RealtimeDeliveryPolicy.ReliableNoQueue, true, cancellationToken);

            if (sent)
            {
                currentRoomId = roomId.Trim();
                lastKnownRoomId = currentRoomId;
                events.RaiseLog("Join room message sent: " + currentRoomId);
            }
            else
            {
                events.RaiseLog("Join room message was not sent: " + roomId);
            }

            return sent;
        }

        //* درخواست ورود به روم را با انتظار اَک داخلی کُر می‌فرستد و برای ریکانکت خودکار استفاده می‌شود.
        public async Task<RealtimeReliableSendResult> JoinRoomReliableAsync(string roomId, RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return RealtimeReliableSendResult.Failed(string.Empty, 0, "GameServerClient is disposed.");
            if (string.IsNullOrWhiteSpace(roomId)) return RealtimeReliableSendResult.Failed(string.Empty, 0, "Room id is empty.");

            string targetRoomId = roomId.Trim();
            string payloadJson = "{\"roomId\":\"" + EscapeJson(targetRoomId) + "\"}";
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("join_room"), RealtimeChannels.Game, RealtimeMessageTypes.JoinRoom, payloadJson, targetRoomId, true);
            RealtimeReliableSendResult result = await realtimeClient.SendEnvelopeReliableWithPolicyAsync(envelope, RealtimeDeliveryPolicy.ReliableNoQueue, true, options, cancellationToken);

            if (result != null && result.isSuccess)
            {
                currentRoomId = targetRoomId;
                lastKnownRoomId = currentRoomId;
                events.RaiseLog("Join room reliable acked: " + currentRoomId + " | attempts=" + result.attempts);
            }
            else
            {
                events.RaiseLog("Join room reliable failed: " + targetRoomId + " | error=" + (result == null ? "null" : result.errorMessage));
            }

            return result ?? RealtimeReliableSendResult.Failed(envelope.id, 0, "Join reliable result is null.");
        }

        //* درخواست خروج از روم فعلی یا روم داده‌شده را به سرور می‌فرستد.
        public async Task<bool> LeaveRoomAsync(string roomId = null, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return false;

            string targetRoomId = string.IsNullOrWhiteSpace(roomId) ? currentRoomId : roomId.Trim();
            if (string.IsNullOrWhiteSpace(targetRoomId)) return FailSend("Room id is empty.");

            string payloadJson = "{\"roomId\":\"" + EscapeJson(targetRoomId) + "\"}";
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("leave_room"), RealtimeChannels.Game, RealtimeMessageTypes.LeaveRoom, payloadJson, targetRoomId, true);
            bool sent = await realtimeClient.SendEnvelopeWithPolicyAsync(envelope, RealtimeDeliveryPolicy.ReliableNoQueue, true, cancellationToken);

            if (sent)
            {
                events.RaiseLog("Leave room message sent: " + targetRoomId);

                if (string.Equals(currentRoomId, targetRoomId, StringComparison.OrdinalIgnoreCase)) currentRoomId = string.Empty;
                if (string.Equals(lastKnownRoomId, targetRoomId, StringComparison.OrdinalIgnoreCase)) lastKnownRoomId = string.Empty;
            }
            else
            {
                events.RaiseLog("Leave room message was not sent: " + targetRoomId);
            }

            return sent;
        }

        #endregion

        #region <Gameplay Send>

        //* وضعیت پلیر را به کانال presence می‌فرستد تا بقیه کلاینت‌ها بتوانند آن را دریافت کنند.
        public async Task<bool> SendPlayerStateAsync(Vector3 position, Quaternion rotation, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return false;

            string targetRoomId = ResolveRoomIdForSend(true);
            if (string.IsNullOrWhiteSpace(targetRoomId)) return FailSend("Player state needs an active room or last known room.");

            string payloadJson = BuildPlayerStatePayloadJson(targetRoomId, position, rotation);
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("player_state"), RealtimeChannels.Presence, RealtimeMessageTypes.PlayerState, payloadJson, targetRoomId, false);
            return await SendGameEnvelopeAsync(envelope, "Player state", cancellationToken);
        }

        //* وضعیت حرکتی پلیر را با آیدی پلیر، سرعت و شماره ترتیب می‌فرستد تا برای سینک واقعی حرکت استفاده شود.
        public async Task<bool> SendPlayerStateAsync(string playerId, Vector3 position, Quaternion rotation, Vector3 velocity, long sequence, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return false;

            string targetRoomId = ResolveRoomIdForSend(true);
            if (string.IsNullOrWhiteSpace(targetRoomId)) return FailSend("Player state needs an active room or last known room.");

            string payloadJson = BuildPlayerStatePayloadJson(targetRoomId, playerId, position, rotation, velocity, sequence);
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("player_state"), RealtimeChannels.Presence, RealtimeMessageTypes.PlayerState, payloadJson, targetRoomId, false);
            return await SendGameEnvelopeAsync(envelope, "Player movement state", cancellationToken);
        }

        //* اکشن پلیر را به کانال game می‌فرستد و برای آن اَک درخواست می‌کند.
        public async Task<bool> SendPlayerActionAsync(string actionType, string actionPayloadJson = "{}", CancellationToken cancellationToken = default)
        {
            if (isDisposed) return false;

            string targetRoomId = ResolveRoomIdForSend(true);
            if (string.IsNullOrWhiteSpace(targetRoomId)) return FailSend("Player action needs an active room.");
            if (string.IsNullOrWhiteSpace(actionType)) return FailSend("Action type is empty.");

            string payloadJson = BuildPlayerActionPayloadJson(targetRoomId, actionType, actionPayloadJson);
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("player_action"), RealtimeChannels.Game, RealtimeMessageTypes.PlayerAction, payloadJson, targetRoomId, true);
            return await SendGameEnvelopeAsync(envelope, "Player action", cancellationToken);
        }


        //* اکشن مهم پلیر را می‌فرستد و خود تابع تا رسیدن اَک یا تایم اوت نتیجه نهایی را برمی‌گرداند.
        public async Task<RealtimeReliableSendResult> SendPlayerActionReliableAsync(string actionType, string actionPayloadJson = "{}", RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return RealtimeReliableSendResult.Failed(string.Empty, 0, "GameServerClient is disposed.");

            string targetRoomId = ResolveRoomIdForSend(true);
            if (string.IsNullOrWhiteSpace(targetRoomId)) return RealtimeReliableSendResult.Failed(string.Empty, 0, "Player action needs an active room.");
            if (string.IsNullOrWhiteSpace(actionType)) return RealtimeReliableSendResult.Failed(string.Empty, 0, "Action type is empty.");

            string payloadJson = BuildPlayerActionPayloadJson(targetRoomId, actionType, actionPayloadJson);
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("player_action"), RealtimeChannels.Game, RealtimeMessageTypes.PlayerAction, payloadJson, targetRoomId, true);
            return await SendGameEnvelopeReliableAsync(envelope, "Player action reliable", RealtimeDeliveryPolicy.ReliableQueued, options, cancellationToken);
        }

        //* یک رویداد جهان را به کانال game می‌فرستد تا سرور بتواند آن را پردازش یا پخش کند.
        public async Task<bool> SendWorldEventAsync(string eventType, string eventPayloadJson = "{}", CancellationToken cancellationToken = default)
        {
            if (isDisposed) return false;

            string targetRoomId = ResolveRoomIdForSend(true);
            if (string.IsNullOrWhiteSpace(targetRoomId)) return FailSend("World event needs an active room.");
            if (string.IsNullOrWhiteSpace(eventType)) return FailSend("World event type is empty.");

            string payloadJson = BuildWorldEventPayloadJson(targetRoomId, eventType, eventPayloadJson);
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("world_event"), RealtimeChannels.Game, RealtimeMessageTypes.WorldEvent, payloadJson, targetRoomId, true);
            return await SendGameEnvelopeAsync(envelope, "World event", cancellationToken);
        }


        //* رویداد مهم جهان را می‌فرستد و تا دریافت اَک یا تایم اوت نتیجه نهایی را برمی‌گرداند.
        public async Task<RealtimeReliableSendResult> SendWorldEventReliableAsync(string eventType, string eventPayloadJson = "{}", RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return RealtimeReliableSendResult.Failed(string.Empty, 0, "GameServerClient is disposed.");

            string targetRoomId = ResolveRoomIdForSend(true);
            if (string.IsNullOrWhiteSpace(targetRoomId)) return RealtimeReliableSendResult.Failed(string.Empty, 0, "World event needs an active room.");
            if (string.IsNullOrWhiteSpace(eventType)) return RealtimeReliableSendResult.Failed(string.Empty, 0, "World event type is empty.");

            string payloadJson = BuildWorldEventPayloadJson(targetRoomId, eventType, eventPayloadJson);
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("world_event"), RealtimeChannels.Game, RealtimeMessageTypes.WorldEvent, payloadJson, targetRoomId, true);
            return await SendGameEnvelopeReliableAsync(envelope, "World event reliable", RealtimeDeliveryPolicy.ReliableQueued, options, cancellationToken);
        }

        //* تغییر state یک آبجکت جهان را به صورت رویداد قابل اطمینان می‌فرستد تا کلاینت‌های دیگر همان تغییر را اعمال کنند.
        public async Task<RealtimeReliableSendResult> SendWorldObjectStateReliableAsync(string senderPlayerId, string objectId, string stateKey, bool boolValue, long sequence = 0, string stringValue = "", float numberValue = 0f, RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return RealtimeReliableSendResult.Failed(string.Empty, 0, "GameServerClient is disposed.");
            if (string.IsNullOrWhiteSpace(objectId)) return RealtimeReliableSendResult.Failed(string.Empty, 0, "World object id is empty.");
            if (string.IsNullOrWhiteSpace(stateKey)) return RealtimeReliableSendResult.Failed(string.Empty, 0, "World state key is empty.");

            string eventPayloadJson = BuildWorldObjectStatePayloadJson(senderPlayerId, objectId, stateKey, boolValue, sequence, stringValue, numberValue);
            return await SendWorldEventReliableAsync("set_object_state", eventPayloadJson, options, cancellationToken);
        }

        #endregion

        #region <Router Binding>

        //* هَندلِرهای مورد نیاز گیم‌سرور را روی رُتِر ریل‌تایم ثبت می‌کند.
        private void RegisterGameHandlers()
        {
            realtimeClient.Router.RegisterHandler(RealtimeChannels.System, RealtimeMessageTypes.Ack, HandleAckEnvelope);
            realtimeClient.Router.RegisterHandler(RealtimeChannels.Game, RealtimeMessageTypes.WorldEvent, HandleWorldEventEnvelope);
            realtimeClient.Router.RegisterHandler(RealtimeChannels.Presence, RealtimeMessageTypes.PlayerState, HandlePlayerStateEnvelope);
            realtimeClient.Router.RegisterHandler(RealtimeChannels.Presence, RealtimeMessageTypes.PlayerJoined, HandlePlayerJoinedEnvelope);
            realtimeClient.Router.RegisterHandler(RealtimeChannels.Presence, RealtimeMessageTypes.PlayerLeft, HandlePlayerLeftEnvelope);
            realtimeClient.ErrorEnvelopeReceived += HandleRealtimeError;
            realtimeClient.Disconnected += HandleRealtimeDisconnected;
        }

        //* هَندلِرهای گیم‌سرور را از رُتِر جدا می‌کند تا نشتی رویداد نداشته باشیم.
        private void UnregisterGameHandlers()
        {
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.System, RealtimeMessageTypes.Ack);
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.Game, RealtimeMessageTypes.WorldEvent);
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.Presence, RealtimeMessageTypes.PlayerState);
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.Presence, RealtimeMessageTypes.PlayerJoined);
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.Presence, RealtimeMessageTypes.PlayerLeft);
            realtimeClient.ErrorEnvelopeReceived -= HandleRealtimeError;
            realtimeClient.Disconnected -= HandleRealtimeDisconnected;
        }

        #endregion

        #region <Envelope Handlers>

        //* اَک دریافتی از سرور را به رویداد سطح گیم‌سرور تبدیل می‌کند.
        private void HandleAckEnvelope(RealtimeEnvelope envelope)
        {
            GameServerAckResult ack = GameServerAckResult.FromEnvelope(envelope);
            if (ack == null) return;

            events.RaiseAck(ack);
            events.RaiseLog("Ack received: " + ack.originalMessageId + " | " + ack.status);
        }

        //* رویداد جهان دریافتی را به لایه گیم‌پلی اعلام می‌کند.
        private void HandleWorldEventEnvelope(RealtimeEnvelope envelope)
        {
            events.RaiseWorldEvent(envelope);
        }

        //* وضعیت پلیر دریافتی را به لایه گیم‌پلی اعلام می‌کند.
        private void HandlePlayerStateEnvelope(RealtimeEnvelope envelope)
        {
            events.RaisePlayerState(envelope);
        }


        //* ورود پلیر دیگر به روم را به رویداد سطح گیم‌سرور تبدیل می‌کند.
        private void HandlePlayerJoinedEnvelope(RealtimeEnvelope envelope)
        {
            GameServerPresenceEvent presenceEvent = GameServerPresenceEvent.FromEnvelope(envelope);
            if (presenceEvent == null || !presenceEvent.IsValid()) return;

            events.RaisePlayerJoined(presenceEvent);
            events.RaiseLog("Player joined received: " + presenceEvent.ResolveNetworkPlayerId() + " | room=" + presenceEvent.roomId);
        }

        //* خروج پلیر دیگر از روم را به رویداد سطح گیم‌سرور تبدیل می‌کند.
        private void HandlePlayerLeftEnvelope(RealtimeEnvelope envelope)
        {
            GameServerPresenceEvent presenceEvent = GameServerPresenceEvent.FromEnvelope(envelope);
            if (presenceEvent == null || !presenceEvent.IsValid()) return;

            events.RaisePlayerLeft(presenceEvent);
            events.RaiseLog("Player left received: " + presenceEvent.ResolveNetworkPlayerId() + " | room=" + presenceEvent.roomId);
        }

        //* خطای ریل‌تایم را به رویداد خطای گیم‌سرور تبدیل می‌کند.
        private void HandleRealtimeError(RealtimeError error)
        {
            events.RaiseError(error);
        }

        //* بعد از قطع اتصال، وضعیت روم فعال را پاک می‌کند ولی روم قبلی را برای ریکانکت و صف نگه می‌دارد.
        private void HandleRealtimeDisconnected(string reason)
        {
            currentRoomId = string.Empty;
            events.RaiseLog("Game server active room state reset after disconnect: " + reason + " | lastKnownRoom=" + lastKnownRoomId);
        }

        #endregion

        #region <Payload Builders>

        //* پِیلود وضعیت پلیر را با روم فعال فعلی و مقدارهای position و rotation می‌سازد.
        private string BuildPlayerStatePayloadJson(Vector3 position, Quaternion rotation)
        {
            return BuildPlayerStatePayloadJson(currentRoomId, position, rotation);
        }

        //* پِیلود وضعیت پلیر را با روم داده‌شده و مقدارهای position و rotation می‌سازد.
        private string BuildPlayerStatePayloadJson(string roomId, Vector3 position, Quaternion rotation)
        {
            return "{"
                + "\"roomId\":\"" + EscapeJson(roomId) + "\","
                + "\"position\":{\"x\":" + FormatFloat(position.x) + ",\"y\":" + FormatFloat(position.y) + ",\"z\":" + FormatFloat(position.z) + "},"
                + "\"rotation\":{\"x\":" + FormatFloat(rotation.x) + ",\"y\":" + FormatFloat(rotation.y) + ",\"z\":" + FormatFloat(rotation.z) + ",\"w\":" + FormatFloat(rotation.w) + "}"
                + "}";
        }

        //* پِیلود وضعیت حرکتی پلیر را برای سینک واقعی position و rotation می‌سازد.
        private string BuildPlayerStatePayloadJson(string roomId, string playerId, Vector3 position, Quaternion rotation, Vector3 velocity, long sequence)
        {
            return "{"
                + "\"roomId\":\"" + EscapeJson(roomId) + "\","
                + "\"playerId\":\"" + EscapeJson(playerId) + "\","
                + "\"sequence\":" + sequence + ","
                + "\"sentAtMs\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ","
                + "\"position\":{\"x\":" + FormatFloat(position.x) + ",\"y\":" + FormatFloat(position.y) + ",\"z\":" + FormatFloat(position.z) + "},"
                + "\"rotation\":{\"x\":" + FormatFloat(rotation.x) + ",\"y\":" + FormatFloat(rotation.y) + ",\"z\":" + FormatFloat(rotation.z) + ",\"w\":" + FormatFloat(rotation.w) + "},"
                + "\"velocity\":{\"x\":" + FormatFloat(velocity.x) + ",\"y\":" + FormatFloat(velocity.y) + ",\"z\":" + FormatFloat(velocity.z) + "}"
                + "}";
        }

        //* پِیلود اکشن پلیر را با روم فعال فعلی، تایپ اکشن و دیتای خام می‌سازد.
        private string BuildPlayerActionPayloadJson(string actionType, string actionPayloadJson)
        {
            return BuildPlayerActionPayloadJson(currentRoomId, actionType, actionPayloadJson);
        }

        //* پِیلود اکشن پلیر را با روم داده‌شده، تایپ اکشن و دیتای خام می‌سازد.
        private string BuildPlayerActionPayloadJson(string roomId, string actionType, string actionPayloadJson)
        {
            return "{"
                + "\"roomId\":\"" + EscapeJson(roomId) + "\","
                + "\"actionType\":\"" + EscapeJson(actionType.Trim()) + "\","
                + "\"action\":" + NormalizePayloadJson(actionPayloadJson)
                + "}";
        }

        //* پِیلود رویداد جهان را با روم فعال فعلی، تایپ رویداد و دیتای خام می‌سازد.
        private string BuildWorldEventPayloadJson(string eventType, string eventPayloadJson)
        {
            return BuildWorldEventPayloadJson(currentRoomId, eventType, eventPayloadJson);
        }

        //* پِیلود رویداد جهان را با روم داده‌شده، تایپ رویداد و دیتای خام می‌سازد.
        private string BuildWorldEventPayloadJson(string roomId, string eventType, string eventPayloadJson)
        {
            return "{"
                + "\"roomId\":\"" + EscapeJson(roomId) + "\","
                + "\"eventType\":\"" + EscapeJson(eventType.Trim()) + "\","
                + "\"event\":" + NormalizePayloadJson(eventPayloadJson)
                + "}";
        }

        //* پِیلود تغییر state آبجکت جهان را برای world_event می‌سازد.
        private string BuildWorldObjectStatePayloadJson(string senderPlayerId, string objectId, string stateKey, bool boolValue, long sequence, string stringValue, float numberValue)
        {
            return "{"
                + "\"senderPlayerId\":\"" + EscapeJson(senderPlayerId) + "\","
                + "\"objectId\":\"" + EscapeJson(objectId.Trim()) + "\","
                + "\"stateKey\":\"" + EscapeJson(stateKey.Trim()) + "\","
                + "\"boolValue\":" + (boolValue ? "true" : "false") + ","
                + "\"numberValue\":" + FormatFloat(numberValue) + ","
                + "\"stringValue\":\"" + EscapeJson(stringValue) + "\","
                + "\"sequence\":" + sequence + ","
                + "\"sentAtMs\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                + "}";
        }

        #endregion

        #region <Send Helpers>

        //* روم مناسب ارسال را از روم فعال یا روم آخرین جوین موفق انتخاب می‌کند.
        private string ResolveRoomIdForSend(bool allowLastKnownRoomWhenDisconnected)
        {
            if (!string.IsNullOrWhiteSpace(currentRoomId)) return currentRoomId;
            if (allowLastKnownRoomWhenDisconnected && !realtimeClient.IsConnected && !string.IsNullOrWhiteSpace(lastKnownRoomId)) return lastKnownRoomId;
            return string.Empty;
        }

        //* اِنولوپ گیم را از طریق کُر ریل‌تایم و بر اساس سیاست ارسال مشخص‌شده مدیریت می‌کند.
        private async Task<bool> SendGameEnvelopeAsync(RealtimeEnvelope envelope, string label, CancellationToken cancellationToken)
        {
            RealtimeDeliveryPolicy deliveryPolicy = ResolveDeliveryPolicy(envelope);
            bool sentOrQueued = await SendGameEnvelopeAsync(envelope, label, deliveryPolicy, cancellationToken);
            return sentOrQueued;
        }

        //* اِنولوپ گیم را با سیاست ارسال داده‌شده ارسال، صف، یا حذف کنترل‌شده می‌کند.
        private async Task<bool> SendGameEnvelopeAsync(RealtimeEnvelope envelope, string label, RealtimeDeliveryPolicy deliveryPolicy, CancellationToken cancellationToken)
        {
            bool wasConnectedBeforeSend = realtimeClient.IsConnected;
            bool sentOrQueued = await realtimeClient.SendEnvelopeWithPolicyAsync(envelope, deliveryPolicy, deliveryPolicy == RealtimeDeliveryPolicy.ReliableQueued, cancellationToken);

            if (sentOrQueued && wasConnectedBeforeSend) events.RaiseLog(label + " sent: " + envelope.id + " | policy=" + deliveryPolicy);
            else if (sentOrQueued) events.RaiseLog(label + " queued: " + envelope.id + " | policy=" + deliveryPolicy);
            else events.RaiseLog(label + " dropped or not sent: " + (envelope == null ? "null" : envelope.id) + " | policy=" + deliveryPolicy);

            return sentOrQueued;
        }


        //* اِنولوپ گیم را با انتظار اَک ارسال می‌کند و نتیجه کامل قابل اطمینان را برمی‌گرداند.
        private async Task<RealtimeReliableSendResult> SendGameEnvelopeReliableAsync(RealtimeEnvelope envelope, string label, RealtimeDeliveryPolicy deliveryPolicy, RealtimeReliableSendOptions options, CancellationToken cancellationToken)
        {
            bool wasConnectedBeforeSend = realtimeClient.IsConnected;
            RealtimeReliableSendResult result = await realtimeClient.SendEnvelopeReliableWithPolicyAsync(envelope, deliveryPolicy, deliveryPolicy == RealtimeDeliveryPolicy.ReliableQueued, options, cancellationToken);

            if (result != null && result.isSuccess && result.wasQueued) events.RaiseLog(label + " queued: " + envelope.id + " | policy=" + deliveryPolicy);
            else if (result != null && result.isSuccess && wasConnectedBeforeSend) events.RaiseLog(label + " acked: " + envelope.id + " | attempts=" + result.attempts + " | status=" + result.ackStatus);
            else events.RaiseLog(label + " failed: " + (envelope == null ? "null" : envelope.id) + " | error=" + (result == null ? "null" : result.errorMessage));

            return result ?? RealtimeReliableSendResult.Failed(envelope == null ? string.Empty : envelope.id, 0, "Reliable result is null.");
        }

        //* بر اساس کانال و نوع پیام، سیاست ارسال مناسب را برای پیام گیم انتخاب می‌کند.
        private RealtimeDeliveryPolicy ResolveDeliveryPolicy(RealtimeEnvelope envelope)
        {
            if (envelope == null) return RealtimeDeliveryPolicy.UnreliableDropWhenDisconnected;
            if (string.Equals(envelope.ch, RealtimeChannels.Presence, StringComparison.OrdinalIgnoreCase)) return RealtimeDeliveryPolicy.UnreliableLatestOnly;
            if (string.Equals(envelope.t, RealtimeMessageTypes.PlayerAction, StringComparison.OrdinalIgnoreCase)) return RealtimeDeliveryPolicy.ReliableQueued;
            if (string.Equals(envelope.t, RealtimeMessageTypes.WorldEvent, StringComparison.OrdinalIgnoreCase)) return RealtimeDeliveryPolicy.ReliableQueued;
            if (envelope.requiresAck) return RealtimeDeliveryPolicy.ReliableQueued;
            return RealtimeDeliveryPolicy.UnreliableDropWhenDisconnected;
        }

        //* شکست ارسال را ثبت می‌کند و مقدار false برمی‌گرداند.
        private bool FailSend(string message)
        {
            events.RaiseLog(message);
            events.RaiseError(RealtimeError.Create(RealtimeErrorCodes.InvalidMessage, message));
            return false;
        }

        #endregion

        #region <Format Helpers>

        //* مقدار عددی را با فرمت ثابت برای جیسون می‌سازد.
        private static string FormatFloat(float value)
        {
            return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        //* دیتای خام جیسون را برای قرار گرفتن داخل پِیلود آماده می‌کند.
        private static string NormalizePayloadJson(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return "{}";
            string trimmed = rawJson.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("[") || trimmed == "null") return trimmed;
            return "\"" + EscapeJson(trimmed) + "\"";
        }

        //* متن را برای قرار گرفتن داخل جیسون escape می‌کند.
        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        #endregion

        #region <Dispose>

        //* هَندلِرهای گیم‌سرور را جدا می‌کند و وضعیت داخلی را پاک می‌کند.
        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;
            UnregisterGameHandlers();
            currentRoomId = string.Empty;
            lastKnownRoomId = string.Empty;
        }

        #endregion
    }
}

//* این فایل کلاینت سطح گیم‌سرور را برای یونیتی می‌سازد.
//* گیم‌سرورکلاینت فقط RealtimeClient را صدا می‌زند و به وب‌سوکت یا جی‌آرپی‌سی وابسته نیست.
