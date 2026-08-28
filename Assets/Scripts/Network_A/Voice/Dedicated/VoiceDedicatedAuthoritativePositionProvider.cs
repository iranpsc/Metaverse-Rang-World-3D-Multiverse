using System;
using System.Collections.Generic;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    [DisallowMultipleComponent]
    public sealed class VoiceDedicatedAuthoritativePositionProvider : MonoBehaviour
    {
        public const long DefaultMaxStateAgeMs = 15000;

        private DedicatedPlayerStateStore playerStateStore;
        private readonly HashSet<string> resolvedParticipantKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> lastRejectReasonByParticipantKey =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private long maxStateAgeMs;
        private bool configured;

        public bool IsConfigured { get { return configured; } }
        public long MaxStateAgeMs { get { return maxStateAgeMs; } }

        //* این تابع فراهم‌کننده موقعیت را به مخزن وضعیت پذیرفته‌شده سرور اختصاصی متصل می‌کند.
        public void Configure(
            DedicatedPlayerStateStore dedicatedPlayerStateStore,
            long acceptedMaxStateAgeMs)
        {
            if (dedicatedPlayerStateStore == null)
            {
                throw new ArgumentNullException("dedicatedPlayerStateStore");
            }

            if (acceptedMaxStateAgeMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "acceptedMaxStateAgeMs",
                    "The accepted state age must be greater than zero.");
            }

            playerStateStore = dedicatedPlayerStateStore;
            maxStateAgeMs = acceptedMaxStateAgeMs;
            configured = true;
            resolvedParticipantKeys.Clear();
            lastRejectReasonByParticipantKey.Clear();

            Debug.Log(
                "VOICE_V3_DEDICATED_POSITION_PROVIDER_CONFIGURED=PASS" +
                " | source=DedicatedPlayerStateStore" +
                " | maxStateAgeMs=" + maxStateAgeMs);
        }

        //* این تابع آخرین موقعیت پذیرفته‌شده همان کاربر را فقط در همان روم، اتصال و سرور برمی‌گرداند.
        public bool TryResolvePosition(
            DedicatedPlayerSession session,
            out Vector3 authoritativePosition,
            out string rejectReason)
        {
            authoritativePosition = Vector3.zero;
            rejectReason = string.Empty;

            if (!configured)
            {
                rejectReason = "position_provider_not_configured";
                return false;
            }

            if (session == null || !session.IsMirrorLikeReady)
            {
                rejectReason = "dedicated_session_not_ready";
                return false;
            }

            string serverId = SafeTrim(session.serverId);
            string roomId = SafeTrim(session.roomId);
            string userId = SafeTrim(session.userId);
            string connectionId = SafeTrim(session.connectionId);
            string participantKey = BuildParticipantKey(
                serverId,
                roomId,
                userId,
                connectionId);

            if (serverId.Length == 0 ||
                roomId.Length == 0 ||
                userId.Length == 0 ||
                connectionId.Length == 0)
            {
                rejectReason = "dedicated_session_identity_incomplete";
                LogRejectOnce(participantKey, userId, roomId, rejectReason);
                return false;
            }

            DedicatedPlayerStateRecord record =
                playerStateStore.GetByUserIdInRoom(roomId, userId);

            if (record == null)
            {
                rejectReason = "accepted_player_state_missing";
                LogRejectOnce(participantKey, userId, roomId, rejectReason);
                return false;
            }

            if (!Matches(record.serverId, serverId) ||
                !Matches(record.roomId, roomId) ||
                !Matches(record.userId, userId) ||
                !Matches(record.connectionId, connectionId))
            {
                rejectReason = "accepted_player_state_scope_mismatch";
                LogRejectOnce(participantKey, userId, roomId, rejectReason);
                return false;
            }

            long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long stateAgeMs = nowUnixMs - record.serverTimestampUnixMs;

            if (record.sequence <= 0 ||
                record.serverTimestampUnixMs <= 0 ||
                stateAgeMs < -5000 ||
                stateAgeMs > maxStateAgeMs)
            {
                rejectReason = "accepted_player_state_stale_or_invalid";
                LogRejectOnce(participantKey, userId, roomId, rejectReason);
                return false;
            }

            Vector3 position = record.Position;
            if (!IsFinite(position.x) ||
                !IsFinite(position.y) ||
                !IsFinite(position.z))
            {
                rejectReason = "accepted_player_position_invalid";
                LogRejectOnce(participantKey, userId, roomId, rejectReason);
                return false;
            }

            authoritativePosition = position;
            rejectReason = string.Empty;
            lastRejectReasonByParticipantKey.Remove(participantKey);

            if (resolvedParticipantKeys.Add(participantKey))
            {
                Debug.Log(
                    "VOICE_V3_DEDICATED_POSITION_RESOLVED=PASS" +
                    " | userId=" + userId +
                    " | roomId=" + roomId +
                    " | connectionId=" + connectionId +
                    " | sequence=" + record.sequence +
                    " | stateAgeMs=" + stateAgeMs +
                    " | position=" + authoritativePosition);
            }

            return true;
        }

        //* این تابع علت رد موقعیت را فقط هنگام تغییر علت ثبت می‌کند تا لاگ سرور تکراری نشود.
        private void LogRejectOnce(
            string participantKey,
            string userId,
            string roomId,
            string rejectReason)
        {
            string safeParticipantKey = string.IsNullOrWhiteSpace(participantKey)
                ? "unknown_participant"
                : participantKey;

            string previousReason;
            if (lastRejectReasonByParticipantKey.TryGetValue(
                    safeParticipantKey,
                    out previousReason) &&
                string.Equals(previousReason, rejectReason, StringComparison.Ordinal))
            {
                return;
            }

            lastRejectReasonByParticipantKey[safeParticipantKey] = rejectReason;

            Debug.LogWarning(
                "VOICE_V3_DEDICATED_POSITION_RESOLVED=WAIT" +
                " | userId=" + SafeTrim(userId) +
                " | roomId=" + SafeTrim(roomId) +
                " | reason=" + rejectReason);
        }

        //* این تابع کلید هویت جاری کاربر را در محدوده همان سرور و روم می‌سازد.
        private static string BuildParticipantKey(
            string serverId,
            string roomId,
            string userId,
            string connectionId)
        {
            return SafeTrim(serverId) + "::" +
                   SafeTrim(roomId) + "::" +
                   SafeTrim(userId) + "::" +
                   SafeTrim(connectionId);
        }

        //* این تابع برابری دقیق دو مقدار هویتی را پس از پاک‌سازی بررسی می‌کند.
        private static bool Matches(string first, string second)
        {
            return string.Equals(
                SafeTrim(first),
                SafeTrim(second),
                StringComparison.Ordinal);
        }

        //* این تابع معتبر بودن مقدار عددی مختصات را بررسی می‌کند.
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        //* این تابع متن هویتی را بدون ساخت مقدار جایگزین پاک‌سازی می‌کند.
        private static string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }
    }
}

/*
توضیح فایل:
این پوشش فقط در بخش صوت سرور اختصاصی اجرا می‌شود و هیچ فایل یا آبجکت اصلی سرور اختصاصی را تغییر نمی‌دهد.
موقعیت از آخرین وضعیت پذیرفته‌شده در مخزن سرور خوانده می‌شود و پیش از استفاده، کاربر، اتصال، روم، سرور، توالی و تازگی داده کنترل می‌شود.
*/
