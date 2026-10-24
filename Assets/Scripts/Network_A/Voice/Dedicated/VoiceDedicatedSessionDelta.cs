using System;
using System.Text;

namespace Network_A.Voice.Dedicated
{
    public enum VoiceDedicatedSessionReason
    {
        None = 0,
        ProximityEnter = 1,
        ProximityExit = 2,
        RoomLeft = 3,
        AvatarDespawned = 4,
        DedicatedDisconnected = 5,
        VoiceDisconnected = 6,
        ReconnectExpired = 7,
        SessionClosed = 8,
        AccessRevoked = 9
    }

    [Serializable]
    public sealed class VoiceDedicatedSessionDelta
    {
        public string type;
        public string authority;
        public string authorityEpochId;
        public long sourceSequence;
        public string serverId;
        public string roomId;
        public string sessionId;
        public string firstUserId;
        public string firstConnectionId;
        public string secondUserId;
        public string secondConnectionId;
        public string memberUserId;
        public string memberConnectionId;
        public float distanceMeters;
        public int reason;
        public long effectiveAtMs;

        //* این تابع رویداد ایجاد یا تغییر فاصله یا خروج فاصله‌ای را از تصمیم ارزیاب می‌سازد.
        public static VoiceDedicatedSessionDelta FromProximityDecision(
            VoiceDedicatedProximityDecision decision,
            string authorityEpochId,
            long sourceSequence)
        {
            if (!decision.HasDelta)
            {
                throw new ArgumentException(
                    "A proximity decision without a delta cannot be serialized.",
                    "decision");
            }

            string deltaType;
            VoiceDedicatedSessionReason deltaReason;

            if (decision.Type == VoiceDedicatedProximityDecisionType.SessionCreated)
            {
                deltaType = "session_created";
                deltaReason = VoiceDedicatedSessionReason.ProximityEnter;
            }
            else if (decision.Type == VoiceDedicatedProximityDecisionType.DistanceUpdated)
            {
                deltaType = "distance_updated";
                deltaReason = VoiceDedicatedSessionReason.None;
            }
            else
            {
                deltaType = "session_closed";
                deltaReason = VoiceDedicatedSessionReason.ProximityExit;
            }

            VoiceDedicatedSessionDelta delta = new VoiceDedicatedSessionDelta
            {
                type = deltaType,
                authority = "dedicated_server",
                authorityEpochId = NormalizeRequiredText(authorityEpochId, "authorityEpochId"),
                sourceSequence = NormalizeSourceSequence(sourceSequence),
                serverId = decision.Pair.ServerId,
                roomId = decision.Pair.RoomId,
                sessionId = NormalizeSessionId(decision.SessionId),
                firstUserId = decision.Pair.FirstUserId,
                firstConnectionId = decision.Pair.FirstConnectionId,
                secondUserId = decision.Pair.SecondUserId,
                secondConnectionId = decision.Pair.SecondConnectionId,
                memberUserId = string.Empty,
                memberConnectionId = string.Empty,
                distanceMeters = decision.DistanceMeters,
                reason = (int)deltaReason,
                effectiveAtMs = NormalizeEventTime(decision.EffectiveAtMs)
            };

            delta.ValidateOrThrow();
            return delta;
        }

        //* این تابع رویداد افزودن عضو تازه به SessionId پایدار را با یک زوج لنگر موجود می‌سازد.
        public static VoiceDedicatedSessionDelta CreateMemberJoined(
            VoiceDedicatedParticipantPair anchorPair,
            string sessionId,
            string memberUserId,
            string memberConnectionId,
            float distanceMeters,
            long effectiveAtMs,
            string authorityEpochId,
            long sourceSequence)
        {
            VoiceDedicatedSessionDelta delta = new VoiceDedicatedSessionDelta
            {
                type = "member_joined",
                authority = "dedicated_server",
                authorityEpochId = NormalizeRequiredText(authorityEpochId, "authorityEpochId"),
                sourceSequence = NormalizeSourceSequence(sourceSequence),
                serverId = anchorPair.ServerId,
                roomId = anchorPair.RoomId,
                sessionId = NormalizeSessionId(sessionId),
                firstUserId = anchorPair.FirstUserId,
                firstConnectionId = anchorPair.FirstConnectionId,
                secondUserId = anchorPair.SecondUserId,
                secondConnectionId = anchorPair.SecondConnectionId,
                memberUserId = NormalizeRequiredText(memberUserId, "memberUserId"),
                memberConnectionId = NormalizeConnectionId(
                    memberConnectionId,
                    "memberConnectionId"),
                distanceMeters = NormalizeDistance(distanceMeters),
                reason = (int)VoiceDedicatedSessionReason.ProximityEnter,
                effectiveAtMs = NormalizeEventTime(effectiveAtMs)
            };

            delta.ValidateOrThrow();
            return delta;
        }

        //* این تابع تغییر فاصله یک Pair را با SessionId واقعی همان Pair یا Group می‌سازد.
        public static VoiceDedicatedSessionDelta CreateDistanceUpdated(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs,
            string authorityEpochId,
            long sourceSequence)
        {
            VoiceDedicatedSessionDelta delta = new VoiceDedicatedSessionDelta
            {
                type = "distance_updated",
                authority = "dedicated_server",
                authorityEpochId = NormalizeRequiredText(authorityEpochId, "authorityEpochId"),
                sourceSequence = NormalizeSourceSequence(sourceSequence),
                serverId = pair.ServerId,
                roomId = pair.RoomId,
                sessionId = NormalizeSessionId(sessionId),
                firstUserId = pair.FirstUserId,
                firstConnectionId = pair.FirstConnectionId,
                secondUserId = pair.SecondUserId,
                secondConnectionId = pair.SecondConnectionId,
                memberUserId = string.Empty,
                memberConnectionId = string.Empty,
                distanceMeters = NormalizeDistance(distanceMeters),
                reason = (int)VoiceDedicatedSessionReason.None,
                effectiveAtMs = NormalizeEventTime(effectiveAtMs)
            };

            delta.ValidateOrThrow();
            return delta;
        }

        //* این تابع رویداد خروج قطعی یک عضو از سشن زوجی را می‌سازد.
        public static VoiceDedicatedSessionDelta CreateMemberLeft(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            string memberUserId,
            float distanceMeters,
            VoiceDedicatedSessionReason reason,
            long effectiveAtMs,
            string authorityEpochId,
            long sourceSequence)
        {
            string normalizedMemberUserId =
                NormalizeRequiredText(memberUserId, "memberUserId");

            string normalizedMemberConnectionId;
            if (string.Equals(
                    normalizedMemberUserId,
                    pair.FirstUserId,
                    StringComparison.Ordinal))
            {
                normalizedMemberConnectionId = pair.FirstConnectionId;
            }
            else if (string.Equals(
                         normalizedMemberUserId,
                         pair.SecondUserId,
                         StringComparison.Ordinal))
            {
                normalizedMemberConnectionId = pair.SecondConnectionId;
            }
            else
            {
                throw new ArgumentException(
                    "memberUserId must belong to the dedicated Voice pair.",
                    "memberUserId");
            }

            VoiceDedicatedSessionDelta delta = new VoiceDedicatedSessionDelta
            {
                type = "member_left",
                authority = "dedicated_server",
                authorityEpochId = NormalizeRequiredText(authorityEpochId, "authorityEpochId"),
                sourceSequence = NormalizeSourceSequence(sourceSequence),
                serverId = pair.ServerId,
                roomId = pair.RoomId,
                sessionId = NormalizeSessionId(sessionId),
                firstUserId = pair.FirstUserId,
                firstConnectionId = pair.FirstConnectionId,
                secondUserId = pair.SecondUserId,
                secondConnectionId = pair.SecondConnectionId,
                memberUserId = normalizedMemberUserId,
                memberConnectionId = normalizedMemberConnectionId,
                distanceMeters = NormalizeDistance(distanceMeters),
                reason = (int)reason,
                effectiveAtMs = NormalizeEventTime(effectiveAtMs)
            };

            delta.ValidateOrThrow();
            return delta;
        }

        //* این تابع رویداد بسته‌شدن قطعی سشن را بدون وابستگی به خروج یک عضو می‌سازد.
        public static VoiceDedicatedSessionDelta CreateSessionClosed(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            VoiceDedicatedSessionReason reason,
            long effectiveAtMs,
            string authorityEpochId,
            long sourceSequence)
        {
            VoiceDedicatedSessionDelta delta = new VoiceDedicatedSessionDelta
            {
                type = "session_closed",
                authority = "dedicated_server",
                authorityEpochId = NormalizeRequiredText(authorityEpochId, "authorityEpochId"),
                sourceSequence = NormalizeSourceSequence(sourceSequence),
                serverId = pair.ServerId,
                roomId = pair.RoomId,
                sessionId = NormalizeSessionId(sessionId),
                firstUserId = pair.FirstUserId,
                firstConnectionId = pair.FirstConnectionId,
                secondUserId = pair.SecondUserId,
                secondConnectionId = pair.SecondConnectionId,
                memberUserId = string.Empty,
                memberConnectionId = string.Empty,
                distanceMeters = NormalizeDistance(distanceMeters),
                reason = (int)reason,
                effectiveAtMs = NormalizeEventTime(effectiveAtMs)
            };

            delta.ValidateOrThrow();
            return delta;
        }

        //* این تابع ساختار کامل یک رویداد را پیش از ورود به صف انتقال بررسی می‌کند.
        public void ValidateOrThrow()
        {
            if (type != "session_created" &&
                type != "distance_updated" &&
                type != "member_joined" &&
                type != "member_left" &&
                type != "session_closed")
            {
                throw new InvalidOperationException("Unknown dedicated Voice delta type.");
            }

            if (!string.Equals(authority, "dedicated_server", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Dedicated Voice delta authority must be dedicated_server.");
            }

            authorityEpochId = NormalizeRequiredText(authorityEpochId, "authorityEpochId");
            sourceSequence = NormalizeSourceSequence(sourceSequence);
            serverId = NormalizeRequiredText(serverId, "serverId");
            roomId = NormalizeRequiredText(roomId, "roomId");
            sessionId = NormalizeSessionId(sessionId);
            firstUserId = NormalizeRequiredText(firstUserId, "firstUserId");
            firstConnectionId = NormalizeConnectionId(
                firstConnectionId,
                "firstConnectionId");
            secondUserId = NormalizeRequiredText(secondUserId, "secondUserId");
            secondConnectionId = NormalizeConnectionId(
                secondConnectionId,
                "secondConnectionId");
            distanceMeters = NormalizeDistance(distanceMeters);
            effectiveAtMs = NormalizeEventTime(effectiveAtMs);

            if (string.Equals(firstUserId, secondUserId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A dedicated Voice delta requires two different userId values.");
            }

            if (string.Equals(
                    firstConnectionId,
                    secondConnectionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A dedicated Voice delta requires two different connectionId values.");
            }

            if (!Enum.IsDefined(typeof(VoiceDedicatedSessionReason), reason))
            {
                throw new InvalidOperationException("Unknown dedicated Voice session reason.");
            }

            if (type == "member_joined" || type == "member_left")
            {
                memberUserId = NormalizeRequiredText(memberUserId, "memberUserId");
                memberConnectionId = NormalizeConnectionId(
                    memberConnectionId,
                    "memberConnectionId");

                if (type == "member_joined")
                {
                    bool duplicateUser =
                        string.Equals(memberUserId, firstUserId, StringComparison.Ordinal) ||
                        string.Equals(memberUserId, secondUserId, StringComparison.Ordinal);

                    bool duplicateConnection =
                        string.Equals(
                            memberConnectionId,
                            firstConnectionId,
                            StringComparison.Ordinal) ||
                        string.Equals(
                            memberConnectionId,
                            secondConnectionId,
                            StringComparison.Ordinal);

                    if (duplicateUser || duplicateConnection)
                    {
                        throw new InvalidOperationException(
                            "member_joined requires a participant outside the anchor pair.");
                    }
                }

                if (type == "member_left" &&
                    !string.Equals(memberUserId, firstUserId, StringComparison.Ordinal) &&
                    !string.Equals(memberUserId, secondUserId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "memberUserId must belong to the dedicated Voice pair.");
                }

                if (type == "member_left")
                {
                    string expectedMemberConnectionId = string.Equals(
                        memberUserId,
                        firstUserId,
                        StringComparison.Ordinal)
                            ? firstConnectionId
                            : secondConnectionId;

                    if (!string.Equals(
                            memberConnectionId,
                            expectedMemberConnectionId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "memberConnectionId must match memberUserId.");
                    }
                }
            }
            else
            {
                memberUserId = string.Empty;
                memberConnectionId = string.Empty;
            }
        }

        //* این تابع علت حذف بازیکن در رجیستری را به علت استاندارد سشن صوتی تبدیل می‌کند.
        public static VoiceDedicatedSessionReason MapPlayerRemovalReason(string removalReason)
        {
            string value = string.IsNullOrWhiteSpace(removalReason)
                ? string.Empty
                : removalReason.Trim().ToLowerInvariant();

            if (value.Contains("reconnect_grace_expired"))
            {
                return VoiceDedicatedSessionReason.ReconnectExpired;
            }

            if (value.Contains("auth_failed") ||
                value.Contains("kicked") ||
                value.Contains("access_revoked"))
            {
                return VoiceDedicatedSessionReason.AccessRevoked;
            }

            if (value.Contains("server_stopped") ||
                value.Contains("shutdown") ||
                value.Contains("registry_destroyed") ||
                value.Contains("dedicated_disconnected"))
            {
                return VoiceDedicatedSessionReason.DedicatedDisconnected;
            }

            if (value.Contains("client_closed") ||
                value.Contains("manual") ||
                value.Contains("user_exit") ||
                value.Contains("leave_room") ||
                value.Contains("room_left"))
            {
                return VoiceDedicatedSessionReason.RoomLeft;
            }

            return VoiceDedicatedSessionReason.AvatarDespawned;
        }

        //* این تابع متن ضروری را پاک‌سازی و از نظر اندازه بررسی می‌کند.
        private static string NormalizeRequiredText(string value, string fieldName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(fieldName);
            }

            string normalized = value.Trim();
            if (normalized.Length == 0)
            {
                throw new ArgumentException(fieldName + " is required.", fieldName);
            }

            if (Encoding.UTF8.GetByteCount(normalized) > 512)
            {
                throw new ArgumentOutOfRangeException(
                    fieldName,
                    fieldName + " exceeds 512 UTF-8 bytes.");
            }

            return normalized;
        }

        //* این تابع شناسه اتصال را به قالب ثابت سی‌ودو نویسه‌ای تبدیل و اعتبارسنجی می‌کند.
        private static string NormalizeConnectionId(string value, string fieldName)
        {
            string normalized = NormalizeRequiredText(value, fieldName)
                .Replace("-", string.Empty)
                .ToLowerInvariant();

            if (normalized.Length != 32)
            {
                throw new ArgumentException(
                    fieldName + " must contain 32 hexadecimal characters.",
                    fieldName);
            }

            for (int index = 0; index < normalized.Length; index += 1)
            {
                char valueCharacter = normalized[index];
                bool isDigit = valueCharacter >= '0' && valueCharacter <= '9';
                bool isLowerHex = valueCharacter >= 'a' && valueCharacter <= 'f';

                if (!isDigit && !isLowerHex)
                {
                    throw new ArgumentException(
                        fieldName + " must contain only hexadecimal characters.",
                        fieldName);
                }
            }

            return normalized;
        }

        //* این تابع شناسه سشن را به قالب یکتا و معتبر تبدیل می‌کند.
        private static string NormalizeSessionId(string value)
        {
            string normalized = NormalizeRequiredText(value, "sessionId").ToLowerInvariant();
            Guid parsed;

            if (!Guid.TryParseExact(normalized, "D", out parsed))
            {
                throw new ArgumentException("sessionId must be a valid UUID.", "sessionId");
            }

            return normalized;
        }

        //* این تابع شماره ترتیبی رویداد را به عدد صحیح مثبت محدود می‌کند.
        private static long NormalizeSourceSequence(long value)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "sourceSequence",
                    "sourceSequence must be greater than zero.");
            }

            return value;
        }

        //* این تابع زمان رویداد را به عدد صحیح نامنفی محدود می‌کند.
        private static long NormalizeEventTime(long value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    "effectiveAtMs",
                    "effectiveAtMs must be non-negative.");
            }

            return value;
        }

        //* این تابع فاصله رویداد را به عدد محدود و نامنفی تبدیل می‌کند.
        private static float NormalizeDistance(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "distanceMeters",
                    "distanceMeters must be finite and non-negative.");
            }

            return value;
        }
    }

    [Serializable]
    public sealed class VoiceDedicatedSessionDeltaBatchRequest
    {
        public string serviceToken;
        public string serverId;
        public string authorityEpochId;
        public VoiceDedicatedSessionDelta[] events;
    }

    [Serializable]
    public sealed class VoiceDedicatedSessionDeltaBatchResponse
    {
        public bool success;
        public string reason;
        public string message;
        public VoiceDedicatedSessionDeltaBatchResponseData data;
    }

    [Serializable]
    public sealed class VoiceDedicatedSessionDeltaBatchResponseData
    {
        public int acceptedCount;
        public int duplicateCount;
        public int rejectedCount;
        public long lastAcceptedSequence;
    }
}

/*
توضیح فایل:
این فایل قرارداد جیسون رویدادهای زوجی صوت را برای ارسال از سرور اختصاصی یونیتی به نود نگه می‌دارد. هر عضو با userId و connectionId همان اتصال جاری در همان سرور و روم مشخص می‌شود و هیچ avatarId ارسال نمی‌شود.
*/
