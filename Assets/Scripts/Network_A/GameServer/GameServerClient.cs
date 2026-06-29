using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using UnityEngine;

namespace Network_A.GameServer
{
    //* این کلاس فَسِید قدیمی گیم سرور روی RealtimeClient است و برای مسیر G7 و تست‌های قدیمی استفاده می‌شود.
    public class GameServerClient : IDisposable
    {
        private readonly RealtimeClient realtimeClient;
        private bool isDisposed;

        public GameServerClientEvents Events { get; private set; } = new GameServerClientEvents();

        public bool HasRoom { get; private set; }
        public string CurrentRoomId { get; private set; } = string.Empty;
        public string RoomId => CurrentRoomId;
        public string LastKnownRoomId { get; private set; } = string.Empty;

        //* این سازنده فقط برای سازگاری با تست‌هایی است که کلاینت را بدون ریل تایم کلاینت می‌سازند.
        public GameServerClient()
        {
            Events.RaiseLog("GameServerClient created without RealtimeClient.");
        }

        //* این سازنده مسیر اصلی G7 است و گیم سرور کلاینت را به RealtimeClient وصل می‌کند.
        public GameServerClient(RealtimeClient realtimeClient)
        {
            this.realtimeClient = realtimeClient;
            BindRealtimeEvents();
            Events.RaiseLog("GameServerClient bound to RealtimeClient.");
        }

        //* این تابع درخواست جوین روم را با مسیر reliable ارسال می‌کند.
        public async Task<RealtimeReliableSendResult> JoinRoomReliableAsync(
            string roomId,
            RealtimeReliableSendOptions options,
            CancellationToken cancellationToken = default)
        {
            string safeRoomId = SafeTrim(roomId);
            if (string.IsNullOrWhiteSpace(safeRoomId))
            {
                return CreateReliableResult(false, 0, "room_id_empty");
            }

            string messageId = "join_room_" + Guid.NewGuid().ToString("N");
            string payloadJson = "{\"roomId\":\"" + EscapeJson(safeRoomId) + "\"}";
            RealtimeEnvelope envelope = BuildEnvelope("game", "join_room", safeRoomId, payloadJson, true, messageId);

            RealtimeReliableSendResult result = await SendReliableEnvelopeAsync(envelope, options, cancellationToken);

            if (IsReliableSuccess(result))
            {
                HasRoom = true;
                CurrentRoomId = safeRoomId;
                LastKnownRoomId = safeRoomId;
                Events.RaiseAck(GameServerAckResult.Processed(messageId, "join_room_processed", safeRoomId));
            }

            return result;
        }

        //* این تابع درخواست جوین روم را با مسیر ساده ارسال می‌کند.
        public async Task<bool> JoinRoomAsync(string roomId, CancellationToken cancellationToken = default)
        {
            RealtimeReliableSendResult result = await JoinRoomReliableAsync(roomId, null, cancellationToken);
            return IsReliableSuccess(result);
        }

        //* این تابع درخواست خروج از روم را ارسال می‌کند و بعد از ارسال موفق، اَک داخلی سازگار با G7 می‌سازد.
        public async Task<bool> LeaveRoomAsync(string roomId, CancellationToken cancellationToken = default)
        {
            string safeRoomId = SafeTrim(roomId);
            if (string.IsNullOrWhiteSpace(safeRoomId)) safeRoomId = CurrentRoomId;
            if (string.IsNullOrWhiteSpace(safeRoomId)) safeRoomId = LastKnownRoomId;

            if (string.IsNullOrWhiteSpace(safeRoomId))
            {
                Events.RaiseLog("Leave skipped. Room id is empty.");
                return false;
            }

            string messageId = "leave_room_" + Guid.NewGuid().ToString("N");
            string payloadJson = "{\"roomId\":\"" + EscapeJson(safeRoomId) + "\"}";
            RealtimeEnvelope envelope = BuildEnvelope("game", "leave_room", safeRoomId, payloadJson, true, messageId);

            bool sent = await SendEnvelopeWithPolicyAsync(envelope, "Reliable", true, cancellationToken);

            if (sent)
            {
                HasRoom = false;
                CurrentRoomId = string.Empty;
                LastKnownRoomId = safeRoomId;
                Events.RaiseAck(GameServerAckResult.Processed(messageId, "leave_room_processed", safeRoomId));
            }

            return sent;
        }

        //* این تابع درخواست خروج از روم را با خروجی reliable برای تست‌های قدیمی فراهم می‌کند.
        public async Task<RealtimeReliableSendResult> LeaveRoomReliableAsync(
            string roomId,
            RealtimeReliableSendOptions options,
            CancellationToken cancellationToken = default)
        {
            bool sent = await LeaveRoomAsync(roomId, cancellationToken);
            return CreateReliableResult(sent, 1, sent ? string.Empty : "leave_room_send_failed");
        }

        //* این تابع اکشن گیم را با مسیر reliable ارسال می‌کند.
        public async Task<RealtimeReliableSendResult> SendPlayerActionReliableAsync(
            string actionType,
            string payloadJson,
            RealtimeReliableSendOptions options,
            CancellationToken cancellationToken = default)
        {
            if (!HasRoom || string.IsNullOrWhiteSpace(CurrentRoomId))
            {
                return CreateReliableResult(false, 0, "client_has_no_room");
            }

            string safeActionType = SafeTrim(actionType);
            string safePayloadJson = BuildPlayerActionPayload(safeActionType, payloadJson);

            string messageId = "player_action_" + Guid.NewGuid().ToString("N");
            RealtimeEnvelope envelope = BuildEnvelope("game", "player_action", CurrentRoomId, safePayloadJson, true, messageId);

            return await SendReliableEnvelopeAsync(envelope, options, cancellationToken);
        }

        //* این تابع اکشن گیم را با مسیر ساده ارسال می‌کند.
        public async Task<bool> SendPlayerActionAsync(
            string actionType,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            RealtimeReliableSendResult result = await SendPlayerActionReliableAsync(actionType, payloadJson, null, cancellationToken);
            return IsReliableSuccess(result);
        }

        //* این تابع وضعیت حرکت پلیر را با امضای قدیمی بدون پلیر آی دی می‌فرستد.
        public async Task<bool> SendPlayerStateAsync(
            Vector3 position,
            Quaternion rotation,
            CancellationToken cancellationToken = default)
        {
            return await SendPlayerStateAsync(string.Empty, position, rotation, Vector3.zero, 0L, cancellationToken);
        }

        //* این تابع وضعیت حرکت پلیر را با امضای قدیمی بدون وِلوسیتی می‌فرستد.
        public async Task<bool> SendPlayerStateAsync(
            string playerId,
            Vector3 position,
            Quaternion rotation,
            CancellationToken cancellationToken = default)
        {
            return await SendPlayerStateAsync(playerId, position, rotation, Vector3.zero, 0L, cancellationToken);
        }

        //* این تابع وضعیت حرکت پلیر را روی کانال presence ارسال می‌کند.
        public async Task<bool> SendPlayerStateAsync(
            string playerId,
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            long sequence,
            CancellationToken cancellationToken = default)
        {
            string roomId = string.IsNullOrWhiteSpace(CurrentRoomId) ? LastKnownRoomId : CurrentRoomId;
            if (string.IsNullOrWhiteSpace(roomId))
            {
                return false;
            }

            string safePlayerId = SafeTrim(playerId);
            string payloadJson =
                "{" +
                "\"playerId\":\"" + EscapeJson(safePlayerId) + "\"," +
                "\"networkPlayerId\":\"" + EscapeJson(safePlayerId) + "\"," +
                "\"roomId\":\"" + EscapeJson(roomId) + "\"," +
                "\"sequence\":" + sequence + "," +
                "\"px\":" + FloatToJson(position.x) + "," +
                "\"py\":" + FloatToJson(position.y) + "," +
                "\"pz\":" + FloatToJson(position.z) + "," +
                "\"rx\":" + FloatToJson(rotation.x) + "," +
                "\"ry\":" + FloatToJson(rotation.y) + "," +
                "\"rz\":" + FloatToJson(rotation.z) + "," +
                "\"rw\":" + FloatToJson(rotation.w) + "," +
                "\"vx\":" + FloatToJson(velocity.x) + "," +
                "\"vy\":" + FloatToJson(velocity.y) + "," +
                "\"vz\":" + FloatToJson(velocity.z) +
                "}";

            string messageId = "player_state_" + Guid.NewGuid().ToString("N");
            RealtimeEnvelope envelope = BuildEnvelope("presence", "player_state", roomId, payloadJson, false, messageId);

            return await SendEnvelopeWithPolicyAsync(envelope, "Unreliable", false, cancellationToken);
        }

        //* این تابع رویداد دنیا را با مسیر reliable ارسال می‌کند.
        public async Task<RealtimeReliableSendResult> SendWorldEventReliableAsync(
            string eventType,
            string payloadJson,
            RealtimeReliableSendOptions options,
            CancellationToken cancellationToken = default)
        {
            string safePayloadJson = BuildWorldEventPayload(eventType, payloadJson);
            string messageId = "world_event_" + Guid.NewGuid().ToString("N");
            RealtimeEnvelope envelope = BuildEnvelope("world", "world_event", CurrentRoomId, safePayloadJson, true, messageId);

            return await SendReliableEnvelopeAsync(envelope, options, cancellationToken);
        }

        //* این تابع رویداد دنیا را با مسیر ساده ارسال می‌کند.
        public async Task<bool> SendWorldEventAsync(
            string eventType,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            RealtimeReliableSendResult result = await SendWorldEventReliableAsync(eventType, payloadJson, null, cancellationToken);
            return IsReliableSuccess(result);
        }

        //* این تابع وضعیت یک آبجکت دنیا را با مسیر reliable ارسال می‌کند.
        public async Task<RealtimeReliableSendResult> SendWorldObjectStateReliableAsync(
            string networkPlayerId,
            string worldObjectId,
            string stateKey,
            bool boolValue,
            long sequence,
            string action,
            float numericValue,
            RealtimeReliableSendOptions options,
            CancellationToken cancellationToken = default)
        {
            string payloadJson =
                "{" +
                "\"networkPlayerId\":\"" + EscapeJson(networkPlayerId) + "\"," +
                "\"worldObjectId\":\"" + EscapeJson(worldObjectId) + "\"," +
                "\"stateKey\":\"" + EscapeJson(stateKey) + "\"," +
                "\"boolValue\":" + (boolValue ? "true" : "false") + "," +
                "\"sequence\":" + sequence + "," +
                "\"action\":\"" + EscapeJson(action) + "\"," +
                "\"numericValue\":" + FloatToJson(numericValue) +
                "}";

            string messageId = "world_object_state_" + Guid.NewGuid().ToString("N");
            RealtimeEnvelope envelope = BuildEnvelope("world", "world_object_state", CurrentRoomId, payloadJson, true, messageId);

            return await SendReliableEnvelopeAsync(envelope, options, cancellationToken);
        }

        //* این تابع منابع و رویدادهای داخلی را آزاد می‌کند.
        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            UnbindRealtimeEvents();
            HasRoom = false;
            CurrentRoomId = string.Empty;

            Events.RaiseLog("GameServerClient disposed.");
        }

        //* این تابع رویدادهای RealtimeClient را وصل می‌کند.
        private void BindRealtimeEvents()
        {
            if (realtimeClient == null) return;
            realtimeClient.EnvelopeReceived += HandleRealtimeEnvelopeReceived;
        }

        //* این تابع رویدادهای RealtimeClient را جدا می‌کند.
        private void UnbindRealtimeEvents()
        {
            if (realtimeClient == null) return;
            realtimeClient.EnvelopeReceived -= HandleRealtimeEnvelopeReceived;
        }

        //* این تابع اِنولوپ‌های دریافتی را به رویدادهای گیم سرور تبدیل می‌کند.
        private void HandleRealtimeEnvelopeReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;

            string channel = ReadMemberString(envelope, "ch");
            string type = ReadMemberString(envelope, "t");
            string payloadJson = ReadMemberString(envelope, "payloadJson");
            string roomId = ReadMemberString(envelope, "room");
            string id = ReadMemberString(envelope, "id");
            string replyTo = ReadMemberString(envelope, "replyTo");

            if (string.IsNullOrWhiteSpace(roomId)) roomId = CurrentRoomId;
            if (string.IsNullOrWhiteSpace(roomId)) roomId = LastKnownRoomId;

            if (IsAckEnvelope(channel, type))
            {
                Events.RaiseAck(GameServerAckResult.FromEnvelope(id, replyTo, payloadJson, roomId));
                return;
            }

            if (IsPresenceJoin(type))
            {
                Events.RaisePlayerJoined(GameServerPresenceEvent.FromEnvelope(type, roomId, payloadJson));
                return;
            }

            if (IsPresenceLeft(type))
            {
                Events.RaisePlayerLeft(GameServerPresenceEvent.FromEnvelope(type, roomId, payloadJson));
                return;
            }

            if (IsPresenceState(type))
            {
                Events.RaisePlayerState(envelope);
                return;
            }

            if (IsWorldEvent(channel, type))
            {
                Events.RaiseWorld(envelope);
            }
        }

        //* این تابع اِنولوپ را از مسیر reliable می‌فرستد و خروجی سازگار با RealtimeReliableSendResult می‌سازد.
        private async Task<RealtimeReliableSendResult> SendReliableEnvelopeAsync(
            RealtimeEnvelope envelope,
            RealtimeReliableSendOptions options,
            CancellationToken cancellationToken)
        {
            if (realtimeClient == null || envelope == null)
            {
                return CreateReliableResult(false, 0, "realtime_client_or_envelope_missing");
            }

            object result = await InvokeRealtimeSendAsync(envelope, options, "Reliable", true, cancellationToken);

            if (result is RealtimeReliableSendResult reliableResult)
            {
                return reliableResult;
            }

            bool ok = result is bool boolResult && boolResult;
            return CreateReliableResult(ok, 1, ok ? string.Empty : "reliable_send_failed");
        }

        //* این تابع اِنولوپ را با policy دلخواه ارسال می‌کند.
        private async Task<bool> SendEnvelopeWithPolicyAsync(
            RealtimeEnvelope envelope,
            string policyName,
            bool isPriority,
            CancellationToken cancellationToken)
        {
            if (realtimeClient == null || envelope == null) return false;

            object result = await InvokeRealtimeSendAsync(envelope, null, policyName, isPriority, cancellationToken);

            if (result is bool boolResult) return boolResult;
            if (result is RealtimeReliableSendResult reliableResult) return IsReliableSuccess(reliableResult);

            return result != null;
        }

        //* این تابع با رفلکشن مسیر ارسال مناسب روی RealtimeClient را پیدا و اجرا می‌کند.
        private async Task<object> InvokeRealtimeSendAsync(
            RealtimeEnvelope envelope,
            RealtimeReliableSendOptions options,
            string policyName,
            bool isPriority,
            CancellationToken cancellationToken)
        {
            Type clientType = realtimeClient.GetType();

            MethodInfo reliableMethod = FindMethod(clientType, "SendEnvelopeReliableAsync");
            if (reliableMethod != null)
            {
                object[] args = BuildArguments(reliableMethod, envelope, options, policyName, isPriority, cancellationToken);
                return await AwaitMethodResultAsync(reliableMethod.Invoke(realtimeClient, args));
            }

            MethodInfo policyMethod = FindMethod(clientType, "SendEnvelopeWithPolicyAsync");
            if (policyMethod != null)
            {
                object[] args = BuildArguments(policyMethod, envelope, options, policyName, isPriority, cancellationToken);
                return await AwaitMethodResultAsync(policyMethod.Invoke(realtimeClient, args));
            }

            MethodInfo simpleMethod = FindMethod(clientType, "SendEnvelopeAsync");
            if (simpleMethod != null)
            {
                object[] args = BuildArguments(simpleMethod, envelope, options, policyName, isPriority, cancellationToken);
                return await AwaitMethodResultAsync(simpleMethod.Invoke(realtimeClient, args));
            }

            Events.RaiseLog("No compatible send method found on RealtimeClient.");
            return false;
        }

        //* این تابع متد را با نام مشخص پیدا می‌کند.
        private MethodInfo FindMethod(Type type, string methodName)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == methodName) return methods[i];
            }

            return null;
        }

        //* این تابع آرگومان‌های لازم متدهای مختلف ارسال را می‌سازد.
        private object[] BuildArguments(
            MethodInfo method,
            RealtimeEnvelope envelope,
            RealtimeReliableSendOptions options,
            string policyName,
            bool isPriority,
            CancellationToken cancellationToken)
        {
            ParameterInfo[] parameters = method.GetParameters();
            object[] args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;

                if (parameterType == typeof(RealtimeEnvelope))
                {
                    args[i] = envelope;
                    continue;
                }

                if (parameterType == typeof(RealtimeReliableSendOptions))
                {
                    args[i] = options;
                    continue;
                }

                if (parameterType == typeof(CancellationToken))
                {
                    args[i] = cancellationToken;
                    continue;
                }

                if (parameterType == typeof(bool))
                {
                    args[i] = isPriority;
                    continue;
                }

                if (parameterType.IsEnum)
                {
                    args[i] = CreateEnumValue(parameterType, policyName);
                    continue;
                }

                args[i] = GetDefaultValue(parameterType);
            }

            return args;
        }

        //* این تابع خروجی تسک‌های مختلف را می‌خواند.
        private async Task<object> AwaitMethodResultAsync(object invokeResult)
        {
            if (invokeResult == null) return null;

            if (invokeResult is Task task)
            {
                await task;

                Type taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    PropertyInfo resultProperty = taskType.GetProperty("Result");
                    return resultProperty == null ? null : resultProperty.GetValue(task, null);
                }

                return true;
            }

            return invokeResult;
        }

        //* این تابع اِنولوپ ریل تایم را با فیلدهای شناخته‌شده پروژه می‌سازد.
        private RealtimeEnvelope BuildEnvelope(
            string channel,
            string type,
            string roomId,
            string payloadJson,
            bool requiresAck,
            string messageId)
        {
            object envelope = Activator.CreateInstance(typeof(RealtimeEnvelope));

            SetMember(envelope, "v", 1);
            SetMember(envelope, "ch", channel);
            SetMember(envelope, "channel", channel);
            SetMember(envelope, "t", type);
            SetMember(envelope, "type", type);
            SetMember(envelope, "id", messageId);
            SetMember(envelope, "messageId", messageId);
            SetMember(envelope, "ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SetMember(envelope, "room", roomId);
            SetMember(envelope, "roomId", roomId);
            SetMember(envelope, "payloadJson", payloadJson ?? "{}");
            SetMember(envelope, "payload", payloadJson ?? "{}");
            SetMember(envelope, "requiresAck", requiresAck);

            return (RealtimeEnvelope)envelope;
        }

        //* این تابع نتیجه reliable را بدون وابستگی به سازنده خاص می‌سازد.
        private RealtimeReliableSendResult CreateReliableResult(bool success, int attempts, string errorMessage)
        {
            object result = Activator.CreateInstance(typeof(RealtimeReliableSendResult));

            SetMember(result, "isSuccess", success);
            SetMember(result, "success", success);
            SetMember(result, "attempts", attempts);
            SetMember(result, "errorMessage", errorMessage ?? string.Empty);

            return (RealtimeReliableSendResult)result;
        }

        //* این تابع مشخص می‌کند نتیجه reliable موفق بوده یا نه.
        private bool IsReliableSuccess(RealtimeReliableSendResult result)
        {
            object value = ReadMemberObject(result, "isSuccess");
            if (value is bool isSuccess) return isSuccess;

            value = ReadMemberObject(result, "success");
            return value is bool success && success;
        }

        //* این تابع مقدار enum را با نام امن می‌سازد.
        private object CreateEnumValue(Type enumType, string preferredName)
        {
            if (!enumType.IsEnum) return GetDefaultValue(enumType);

            try
            {
                if (!string.IsNullOrWhiteSpace(preferredName) && Enum.IsDefined(enumType, preferredName))
                {
                    return Enum.Parse(enumType, preferredName);
                }

                string[] names = Enum.GetNames(enumType);
                if (names.Length > 0) return Enum.Parse(enumType, names[0]);
            }
            catch
            {
            }

            return GetDefaultValue(enumType);
        }

        //* این تابع مقدار پیش‌فرض هر نوع را می‌سازد.
        private object GetDefaultValue(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        //* این تابع فیلد یا پراپرتی را با رفلکشن ست می‌کند.
        private void SetMember(object target, string name, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(name)) return;

            Type type = target.GetType();

            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { field.SetValue(target, value); } catch { }
                return;
            }

            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(target, value, null); } catch { }
            }
        }

        //* این تابع مقدار رشته‌ای فیلد یا پراپرتی را می‌خواند.
        private string ReadMemberString(object target, string name)
        {
            object value = ReadMemberObject(target, name);
            return value == null ? string.Empty : value.ToString();
        }

        //* این تابع مقدار خام فیلد یا پراپرتی را می‌خواند.
        private object ReadMemberObject(object target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name)) return null;

            Type type = target.GetType();

            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);

            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead) return property.GetValue(target, null);

            return null;
        }

        //* این تابع payload اکشن پلیر را آماده می‌کند.
        private string BuildPlayerActionPayload(string actionType, string payloadJson)
        {
            if (!string.IsNullOrWhiteSpace(payloadJson) &&
                payloadJson.TrimStart().StartsWith("{", StringComparison.Ordinal) &&
                payloadJson.Contains("\"actionType\""))
            {
                return payloadJson;
            }

            string safePayload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson.Trim();

            return "{" +
                   "\"actionType\":\"" + EscapeJson(actionType) + "\"," +
                   "\"payload\":" + safePayload +
                   "}";
        }

        //* این تابع payload رویداد دنیا را آماده می‌کند.
        private string BuildWorldEventPayload(string eventType, string payloadJson)
        {
            string safePayload = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson.Trim();

            if (safePayload.StartsWith("{", StringComparison.Ordinal) && safePayload.Contains("\"eventType\""))
            {
                return safePayload;
            }

            return "{" +
                   "\"eventType\":\"" + EscapeJson(eventType) + "\"," +
                   "\"payload\":" + safePayload +
                   "}";
        }

        private bool IsAckEnvelope(string channel, string type)
        {
            return IsSame(type, "ack") || IsSame(type, "system/ack") ||
                   (IsSame(channel, "system") && IsSame(type, "ack"));
        }

        private bool IsPresenceJoin(string type)
        {
            return IsSame(type, "player_joined") || IsSame(type, "presence/player_joined");
        }

        private bool IsPresenceLeft(string type)
        {
            return IsSame(type, "player_left") || IsSame(type, "presence/player_left");
        }

        private bool IsPresenceState(string type)
        {
            return IsSame(type, "player_state") || IsSame(type, "presence/player_state");
        }

        private bool IsWorldEvent(string channel, string type)
        {
            return IsSame(channel, "world") || (type != null && type.StartsWith("world", StringComparison.OrdinalIgnoreCase));
        }

        private bool IsSame(string a, string b)
        {
            return string.Equals(SafeTrim(a), SafeTrim(b), StringComparison.OrdinalIgnoreCase);
        }

        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }

        private string FloatToJson(float value)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    //* این کلاس رویدادهای گیم سرور کلاینت را نگه می‌دارد و با اسکریپت‌های قدیمی سازگار است.
    public class GameServerClientEvents
    {
        public event Action<string> LogReceived;
        public event Action<RealtimeError> ErrorReceived;
        public event Action<GameServerAckResult> AckReceived;

        public event Action<GameServerPresenceEvent> PlayerJoinedReceived;
        public event Action<GameServerPresenceEvent> PlayerLeftReceived;

        public event Action<RealtimeEnvelope> PlayerStateReceived;
        public event Action<RealtimeEnvelope> WorldEventReceived;

        internal void RaiseLog(string message) { LogReceived?.Invoke(message); }
        internal void RaiseError(RealtimeError error) { ErrorReceived?.Invoke(error); }
        internal void RaiseAck(GameServerAckResult ack) { AckReceived?.Invoke(ack); }

        internal void RaisePlayerJoined(GameServerPresenceEvent presence) { PlayerJoinedReceived?.Invoke(presence); }
        internal void RaisePlayerLeft(GameServerPresenceEvent presence) { PlayerLeftReceived?.Invoke(presence); }

        internal void RaisePlayerState(RealtimeEnvelope envelope) { PlayerStateReceived?.Invoke(envelope); }
        internal void RaiseWorld(RealtimeEnvelope envelope) { WorldEventReceived?.Invoke(envelope); }
    }

    //* این مدل نتیجه اَک پیام‌های گیم سرور را برای تست‌های قدیمی نگه می‌دارد.
    [Serializable]
    public class GameServerAckResult
    {
        public bool success;
        public bool isSuccess;
        public bool processed;
        public bool isProcessed;
        public string status;
        public string reason;
        public string message;
        public string errorMessage;
        public string originalMessageId;
        public string messageId;
        public string replyTo;
        public string roomId;
        public string detailsJson;
        public string rawJson;

        public bool IsProcessed()
        {
            if (processed || isProcessed) return true;
            return string.Equals(status, "processed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ||
                   success ||
                   isSuccess;
        }

        public static GameServerAckResult Processed(string originalMessageId, string reason, string roomId)
        {
            string details = "{\"status\":\"processed\",\"reason\":\"" + JsonEscape(reason) + "\"}";

            return new GameServerAckResult
            {
                success = true,
                isSuccess = true,
                processed = true,
                isProcessed = true,
                status = "processed",
                reason = reason,
                message = reason,
                originalMessageId = originalMessageId,
                replyTo = originalMessageId,
                roomId = roomId,
                detailsJson = details
            };
        }

        public static GameServerAckResult FromEnvelope(string id, string replyTo, string payloadJson, string roomId)
        {
            GameServerAckResult ack = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(payloadJson))
                {
                    ack = JsonUtility.FromJson<GameServerAckResult>(payloadJson);
                }
            }
            catch
            {
            }

            if (ack == null) ack = new GameServerAckResult();

            ack.rawJson = payloadJson ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ack.detailsJson)) ack.detailsJson = payloadJson ?? string.Empty;
            if (string.IsNullOrWhiteSpace(ack.messageId)) ack.messageId = id;
            if (string.IsNullOrWhiteSpace(ack.replyTo)) ack.replyTo = replyTo;
            if (string.IsNullOrWhiteSpace(ack.originalMessageId)) ack.originalMessageId = !string.IsNullOrWhiteSpace(replyTo) ? replyTo : ack.replyTo;
            if (string.IsNullOrWhiteSpace(ack.roomId)) ack.roomId = roomId;

            if (string.IsNullOrWhiteSpace(ack.status)) ack.status = "processed";
            ack.success = true;
            ack.isSuccess = true;
            ack.processed = true;
            ack.isProcessed = true;

            return ack;
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    //* این مدل ایونت حضور پلیر را برای مسیر گیم اینتگریشن و تست‌های قدیمی نگه می‌دارد.
    [Serializable]
    public class GameServerPresenceEvent
    {
        public string type;
        public string userId;
        public string playerId;
        public string networkPlayerId;
        public string id;
        public string userName;
        public string username;
        public string playerName;
        public string displayName;
        public string connectionId;
        public string roomId;
        public string serverId;
        public string sessionId;
        public string reason;
        public string rawJson;

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

        public Vector3 Position => new Vector3(px, py, pz);
        public Quaternion Rotation => new Quaternion(rx, ry, rz, rw == 0f ? 1f : rw);
        public Vector3 Velocity => new Vector3(vx, vy, vz);

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(ResolveNetworkPlayerId());
        }

        public string ResolveNetworkPlayerId()
        {
            if (!string.IsNullOrWhiteSpace(userId)) return userId.Trim();
            if (!string.IsNullOrWhiteSpace(playerId)) return playerId.Trim();
            if (!string.IsNullOrWhiteSpace(networkPlayerId)) return networkPlayerId.Trim();
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
            if (!string.IsNullOrWhiteSpace(connectionId)) return connectionId.Trim();
            return string.Empty;
        }

        public static GameServerPresenceEvent FromEnvelope(string type, string roomId, string payloadJson)
        {
            GameServerPresenceEvent evt = null;

            try
            {
                if (!string.IsNullOrWhiteSpace(payloadJson))
                {
                    evt = JsonUtility.FromJson<GameServerPresenceEvent>(payloadJson);
                }
            }
            catch
            {
            }

            if (evt == null) evt = new GameServerPresenceEvent();

            evt.type = string.IsNullOrWhiteSpace(evt.type) ? type : evt.type;
            evt.roomId = string.IsNullOrWhiteSpace(evt.roomId) ? roomId : evt.roomId;
            evt.rawJson = payloadJson ?? string.Empty;

            return evt;
        }
    }
}
