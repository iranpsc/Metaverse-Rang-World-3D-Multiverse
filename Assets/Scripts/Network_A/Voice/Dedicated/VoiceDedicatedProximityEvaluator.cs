using System;
using System.Collections.Generic;
using System.Text;

namespace Network_A.Voice.Dedicated
{
    public enum VoiceDedicatedProximityState
    {
        Outside = 0,
        EnterPending = 1,
        Active = 2,
        ExitPending = 3
    }

    public enum VoiceDedicatedProximityDecisionType
    {
        None = 0,
        SessionCreated = 1,
        DistanceUpdated = 2,
        SessionClosed = 3
    }

    public enum VoiceDedicatedProximityReason
    {
        None = 0,
        ProximityEnter = 1,
        ProximityExit = 2
    }

    public struct VoiceDedicatedParticipantPair
    {
        public string ServerId { get; private set; }
        public string RoomId { get; private set; }
        public string FirstUserId { get; private set; }
        public string FirstConnectionId { get; private set; }
        public string SecondUserId { get; private set; }
        public string SecondConnectionId { get; private set; }
        public string PairKey { get; private set; }

        //* این سازنده قدیمی از ایجاد زوج بدون شناسه اتصال جلوگیری می‌کند.
        public VoiceDedicatedParticipantPair(
            string serverId,
            string roomId,
            string firstUserId,
            string secondUserId)
        {
            this = default(VoiceDedicatedParticipantPair);

            throw new ArgumentException(
                "A Voice proximity pair requires userId and connectionId for both participants.");
        }

        //* این سازنده هویت دو اتصال کاربر را در محدوده همان سرور و همان روم پاک‌سازی و به ترتیب ثابت ذخیره می‌کند.
        public VoiceDedicatedParticipantPair(
            string serverId,
            string roomId,
            string firstUserId,
            string firstConnectionId,
            string secondUserId,
            string secondConnectionId)
        {
            string safeServerId = NormalizeRequiredText(serverId, "serverId");
            string safeRoomId = NormalizeRequiredText(roomId, "roomId");
            string safeFirstUserId = NormalizeRequiredText(firstUserId, "firstUserId");
            string safeFirstConnectionId = NormalizeConnectionId(
                firstConnectionId,
                "firstConnectionId");
            string safeSecondUserId = NormalizeRequiredText(secondUserId, "secondUserId");
            string safeSecondConnectionId = NormalizeConnectionId(
                secondConnectionId,
                "secondConnectionId");

            if (string.Equals(safeFirstUserId, safeSecondUserId, StringComparison.Ordinal))
            {
                throw new ArgumentException("A Voice proximity pair requires two different userId values.");
            }

            if (string.Equals(
                    safeFirstConnectionId,
                    safeSecondConnectionId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A Voice proximity pair requires two different connectionId values.");
            }

            if (CompareUtf8Ordinal(safeFirstUserId, safeSecondUserId) <= 0)
            {
                FirstUserId = safeFirstUserId;
                FirstConnectionId = safeFirstConnectionId;
                SecondUserId = safeSecondUserId;
                SecondConnectionId = safeSecondConnectionId;
            }
            else
            {
                FirstUserId = safeSecondUserId;
                FirstConnectionId = safeSecondConnectionId;
                SecondUserId = safeFirstUserId;
                SecondConnectionId = safeFirstConnectionId;
            }

            ServerId = safeServerId;
            RoomId = safeRoomId;
            PairKey = BuildPairKey(
                ServerId,
                RoomId,
                FirstUserId,
                FirstConnectionId,
                SecondUserId,
                SecondConnectionId);
        }

        //* این تابع یک متن ضروری را برای استفاده در هویت زوج پاک‌سازی و از نظر اندازه بررسی می‌کند.
        private static string NormalizeRequiredText(string value, string fieldName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(fieldName);
            }

            string normalizedValue = value.Trim();
            if (normalizedValue.Length == 0)
            {
                throw new ArgumentException(fieldName + " is required.", fieldName);
            }

            if (Encoding.UTF8.GetByteCount(normalizedValue) > 512)
            {
                throw new ArgumentOutOfRangeException(
                    fieldName,
                    fieldName + " exceeds 512 UTF-8 bytes.");
            }

            return normalizedValue;
        }

        //* این تابع شناسه اتصال سرور اختصاصی را به قالب ثابت سی‌ودو نویسه‌ای تبدیل می‌کند.
        private static string NormalizeConnectionId(string value, string fieldName)
        {
            string normalizedValue = NormalizeRequiredText(value, fieldName)
                .Replace("-", string.Empty)
                .ToLowerInvariant();

            if (normalizedValue.Length != 32)
            {
                throw new ArgumentException(
                    fieldName + " must contain 32 hexadecimal characters.",
                    fieldName);
            }

            for (int index = 0; index < normalizedValue.Length; index += 1)
            {
                char valueCharacter = normalizedValue[index];
                bool isDigit = valueCharacter >= '0' && valueCharacter <= '9';
                bool isLowerHex = valueCharacter >= 'a' && valueCharacter <= 'f';

                if (!isDigit && !isLowerHex)
                {
                    throw new ArgumentException(
                        fieldName + " must contain only hexadecimal characters.",
                        fieldName);
                }
            }

            return normalizedValue;
        }

        //* این تابع دو شناسه کاربر را دقیقاً بر اساس ترتیب بایت‌های یو‌تی‌اف هشت مقایسه می‌کند.
        private static int CompareUtf8Ordinal(string firstValue, string secondValue)
        {
            byte[] firstBytes = Encoding.UTF8.GetBytes(firstValue);
            byte[] secondBytes = Encoding.UTF8.GetBytes(secondValue);
            int sharedLength = Math.Min(firstBytes.Length, secondBytes.Length);

            for (int index = 0; index < sharedLength; index += 1)
            {
                if (firstBytes[index] == secondBytes[index]) continue;
                return firstBytes[index] < secondBytes[index] ? -1 : 1;
            }

            if (firstBytes.Length == secondBytes.Length) return 0;
            return firstBytes.Length < secondBytes.Length ? -1 : 1;
        }

        //* این تابع کلید بدون برخورد زوج را با محدوده سرور، روم و دو شناسه مرتب‌شده می‌سازد.
        private static string BuildPairKey(
            string serverId,
            string roomId,
            string firstUserId,
            string firstConnectionId,
            string secondUserId,
            string secondConnectionId)
        {
            return "voice_pair|" +
                   Encoding.UTF8.GetByteCount(serverId) + ":" + serverId + "|" +
                   Encoding.UTF8.GetByteCount(roomId) + ":" + roomId + "|" +
                   Encoding.UTF8.GetByteCount(firstUserId) + ":" + firstUserId + "|" +
                   Encoding.UTF8.GetByteCount(firstConnectionId) + ":" + firstConnectionId + "|" +
                   Encoding.UTF8.GetByteCount(secondUserId) + ":" + secondUserId + "|" +
                   Encoding.UTF8.GetByteCount(secondConnectionId) + ":" + secondConnectionId;
        }
    }

    public struct VoiceDedicatedProximityDecision
    {
        public VoiceDedicatedProximityDecisionType Type { get; private set; }
        public VoiceDedicatedProximityState State { get; private set; }
        public VoiceDedicatedProximityReason Reason { get; private set; }
        public VoiceDedicatedParticipantPair Pair { get; private set; }
        public string SessionId { get; private set; }
        public float DistanceMeters { get; private set; }
        public long EffectiveAtMs { get; private set; }
        public bool HasDelta { get { return Type != VoiceDedicatedProximityDecisionType.None; } }

        //* این سازنده نتیجه یک ارزیابی زوج را بدون وابستگی به مسیر انتقال می‌سازد.
        public VoiceDedicatedProximityDecision(
            VoiceDedicatedProximityDecisionType type,
            VoiceDedicatedProximityState state,
            VoiceDedicatedProximityReason reason,
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            Type = type;
            State = state;
            Reason = reason;
            Pair = pair;
            SessionId = sessionId ?? string.Empty;
            DistanceMeters = distanceMeters;
            EffectiveAtMs = effectiveAtMs;
        }
    }

    public sealed class VoiceDedicatedProximityEvaluator
    {
        public const float EnterDistanceMeters = 3.0f;
        public const float ExitDistanceMeters = 3.5f;

        private readonly long stabilityDelayMs;
        private readonly Func<string> sessionIdFactory;
        private readonly Dictionary<string, PairRuntimeState> stateByPairKey =
            new Dictionary<string, PairRuntimeState>(StringComparer.Ordinal);
        private readonly HashSet<string> usedSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private int activeSessionCount;

        public long StabilityDelayMs { get { return stabilityDelayMs; } }
        public int TrackedPairCount { get { return stateByPairKey.Count; } }
        public int ActiveSessionCount { get { return activeSessionCount; } }
        public int UsedSessionIdCount { get { return usedSessionIds.Count; } }

        //* این سازنده ارزیاب را با زمان پایداری تعیین‌شده توسط بنچمارک آماده می‌کند.
        public VoiceDedicatedProximityEvaluator(long stabilityDelayMs)
            : this(stabilityDelayMs, null)
        {
        }

        //* این سازنده ارزیاب را با زمان پایداری و سازنده شناسه قابل‌کنترل برای تست آماده می‌کند.
        public VoiceDedicatedProximityEvaluator(
            long stabilityDelayMs,
            Func<string> sessionIdFactory)
        {
            if (stabilityDelayMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "stabilityDelayMs",
                    "stabilityDelayMs must be selected by benchmark and must be greater than zero.");
            }

            this.stabilityDelayMs = stabilityDelayMs;
            this.sessionIdFactory = sessionIdFactory;
        }

        //* این تابع فاصله قطعی یک زوج را با زمان یکنواخت بررسی و فقط تغییر واقعی سشن را برمی‌گرداند.
        public VoiceDedicatedProximityDecision Evaluate(
            VoiceDedicatedParticipantPair pair,
            float distanceMeters,
            long stabilityClockMs,
            long effectiveAtMs)
        {
            if (string.IsNullOrWhiteSpace(pair.PairKey))
            {
                throw new ArgumentException("pair must be initialized.", "pair");
            }

            if (float.IsNaN(distanceMeters) ||
                float.IsInfinity(distanceMeters) ||
                distanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "distanceMeters",
                    "distanceMeters must be a finite non-negative value.");
            }

            if (stabilityClockMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    "stabilityClockMs",
                    "stabilityClockMs must be non-negative.");
            }

            if (effectiveAtMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    "effectiveAtMs",
                    "effectiveAtMs must be non-negative.");
            }

            PairRuntimeState runtimeState;

            if (!stateByPairKey.TryGetValue(pair.PairKey, out runtimeState))
            {
                if (distanceMeters > EnterDistanceMeters)
                {
                    return CreateNoDelta(
                        pair,
                        VoiceDedicatedProximityState.Outside,
                        string.Empty,
                        distanceMeters,
                        effectiveAtMs);
                }

                runtimeState = new PairRuntimeState
                {
                    State = VoiceDedicatedProximityState.EnterPending,
                    CandidateSinceClockMs = stabilityClockMs,
                    LastEvaluationClockMs = stabilityClockMs,
                    LastEmittedDistanceMeters = distanceMeters,
                    SessionId = string.Empty
                };

                stateByPairKey.Add(pair.PairKey, runtimeState);

                return CreateNoDelta(
                    pair,
                    runtimeState.State,
                    runtimeState.SessionId,
                    distanceMeters,
                    effectiveAtMs);
            }

            if (stabilityClockMs < runtimeState.LastEvaluationClockMs)
            {
                throw new InvalidOperationException(
                    "stabilityClockMs moved backwards for pair " + pair.PairKey + ".");
            }

            runtimeState.LastEvaluationClockMs = stabilityClockMs;

            if (runtimeState.State == VoiceDedicatedProximityState.EnterPending)
            {
                if (distanceMeters > EnterDistanceMeters)
                {
                    stateByPairKey.Remove(pair.PairKey);

                    return CreateNoDelta(
                        pair,
                        VoiceDedicatedProximityState.Outside,
                        string.Empty,
                        distanceMeters,
                        effectiveAtMs);
                }

                if (stabilityClockMs - runtimeState.CandidateSinceClockMs < stabilityDelayMs)
                {
                    return CreateNoDelta(
                        pair,
                        runtimeState.State,
                        runtimeState.SessionId,
                        distanceMeters,
                        effectiveAtMs);
                }

                string createdSessionId = CreateUniqueSessionId();
                runtimeState.State = VoiceDedicatedProximityState.Active;
                runtimeState.SessionId = createdSessionId;
                runtimeState.LastEmittedDistanceMeters = distanceMeters;
                activeSessionCount += 1;

                return new VoiceDedicatedProximityDecision(
                    VoiceDedicatedProximityDecisionType.SessionCreated,
                    runtimeState.State,
                    VoiceDedicatedProximityReason.ProximityEnter,
                    pair,
                    runtimeState.SessionId,
                    distanceMeters,
                    effectiveAtMs);
            }

            if (runtimeState.State == VoiceDedicatedProximityState.Active)
            {
                if (distanceMeters >= ExitDistanceMeters)
                {
                    runtimeState.State = VoiceDedicatedProximityState.ExitPending;
                    runtimeState.CandidateSinceClockMs = stabilityClockMs;

                    return CreateNoDelta(
                        pair,
                        runtimeState.State,
                        runtimeState.SessionId,
                        distanceMeters,
                        effectiveAtMs);
                }

                if (runtimeState.LastEmittedDistanceMeters.Equals(distanceMeters))
                {
                    return CreateNoDelta(
                        pair,
                        runtimeState.State,
                        runtimeState.SessionId,
                        distanceMeters,
                        effectiveAtMs);
                }

                runtimeState.LastEmittedDistanceMeters = distanceMeters;

                return new VoiceDedicatedProximityDecision(
                    VoiceDedicatedProximityDecisionType.DistanceUpdated,
                    runtimeState.State,
                    VoiceDedicatedProximityReason.None,
                    pair,
                    runtimeState.SessionId,
                    distanceMeters,
                    effectiveAtMs);
            }

            if (distanceMeters < ExitDistanceMeters)
            {
                runtimeState.State = VoiceDedicatedProximityState.Active;

                if (runtimeState.LastEmittedDistanceMeters.Equals(distanceMeters))
                {
                    return CreateNoDelta(
                        pair,
                        runtimeState.State,
                        runtimeState.SessionId,
                        distanceMeters,
                        effectiveAtMs);
                }

                runtimeState.LastEmittedDistanceMeters = distanceMeters;

                return new VoiceDedicatedProximityDecision(
                    VoiceDedicatedProximityDecisionType.DistanceUpdated,
                    runtimeState.State,
                    VoiceDedicatedProximityReason.None,
                    pair,
                    runtimeState.SessionId,
                    distanceMeters,
                    effectiveAtMs);
            }

            if (stabilityClockMs - runtimeState.CandidateSinceClockMs < stabilityDelayMs)
            {
                return CreateNoDelta(
                    pair,
                    runtimeState.State,
                    runtimeState.SessionId,
                    distanceMeters,
                    effectiveAtMs);
            }

            string closedSessionId = runtimeState.SessionId;
            stateByPairKey.Remove(pair.PairKey);
            activeSessionCount -= 1;

            return new VoiceDedicatedProximityDecision(
                VoiceDedicatedProximityDecisionType.SessionClosed,
                VoiceDedicatedProximityState.Outside,
                VoiceDedicatedProximityReason.ProximityExit,
                pair,
                closedSessionId,
                distanceMeters,
                effectiveAtMs);
        }

        //* این تابع نتیجه بدون تغییر سشن را با داده‌های همان ارزیابی می‌سازد.
        private static VoiceDedicatedProximityDecision CreateNoDelta(
            VoiceDedicatedParticipantPair pair,
            VoiceDedicatedProximityState state,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedProximityDecision(
                VoiceDedicatedProximityDecisionType.None,
                state,
                VoiceDedicatedProximityReason.None,
                pair,
                sessionId,
                distanceMeters,
                effectiveAtMs);
        }

        //* این تابع شناسه تازه را بررسی و از استفاده دوباره هر شناسه قبلی جلوگیری می‌کند.
        private string CreateUniqueSessionId()
        {
            string sessionId = sessionIdFactory == null
                ? Guid.NewGuid().ToString("D")
                : sessionIdFactory();

            sessionId = sessionId == null
                ? string.Empty
                : sessionId.Trim().ToLowerInvariant();

            Guid parsedSessionId;
            bool validUuid = Guid.TryParseExact(sessionId, "D", out parsedSessionId);
            bool validVersion =
                sessionId.Length == 36 &&
                sessionId[14] >= '1' &&
                sessionId[14] <= '5';

            char variant = sessionId.Length == 36 ? sessionId[19] : '\0';
            bool validVariant =
                variant == '8' ||
                variant == '9' ||
                variant == 'a' ||
                variant == 'b';

            if (!validUuid || !validVersion || !validVariant)
            {
                throw new InvalidOperationException(
                    "The dedicated Voice sessionId factory returned an invalid UUID.");
            }

            if (!usedSessionIds.Add(sessionId))
            {
                throw new InvalidOperationException(
                    "The dedicated Voice sessionId factory attempted to reuse a previous sessionId.");
            }

            return sessionId;
        }

        private sealed class PairRuntimeState
        {
            public VoiceDedicatedProximityState State;
            public long CandidateSinceClockMs;
            public long LastEvaluationClockMs;
            public float LastEmittedDistanceMeters;
            public string SessionId;
        }
    }
}

/*
توضیح فایل:
این فایل فقط منطق مستقل زوج‌های صوتی را بر اساس شناسه کاربر، محدوده سرور و روم، فاصله قطعی سرور اختصاصی، هیسترزیس و زمان پایداری اجرا می‌کند. این فایل به یونیتی، وب‌سوکت، جی‌آر‌پی‌سی یا نود وابسته نیست و هیچ موقعیتی را از کلاینت دریافت نمی‌کند.
*/
