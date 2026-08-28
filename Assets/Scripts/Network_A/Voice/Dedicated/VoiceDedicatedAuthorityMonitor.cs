using System;
using System.Collections.Generic;
using Network_A.GameServer;
using Network_A.GameServer.Players;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Network_A.Voice.Dedicated
{
    [DisallowMultipleComponent]
    public sealed class VoiceDedicatedAuthorityMonitor : MonoBehaviour
    {
        private DedicatedServerRuntime runtime;
        private DedicatedPlayerRegistry playerRegistry;
        private VoiceDedicatedAuthoritativePositionProvider positionProvider;
        private VoiceDedicatedSessionDeltaSender deltaSender;
        private readonly Dictionary<string, PairTracker> trackersByPairKey =
            new Dictionary<string, PairTracker>(StringComparer.Ordinal);
        private readonly HashSet<string> observedPairKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> currentScopeUserKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> currentScopeParticipantKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> currentResolvedParticipantKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> pendingTrackerRemovals =
            new List<string>();
        private readonly List<VoiceDedicatedTopologyPairObservation> pairObservations =
            new List<VoiceDedicatedTopologyPairObservation>();

        private VoiceDedicatedGroupTopologyRuntime groupTopologyRuntime;

        private const int MaximumAuthorityEpochResetCount = 8;

        private long stabilityDelayMs;
        private string authorityEpochId = string.Empty;
        private long sourceSequence;
        private float sampleIntervalSeconds;
        private float nextSampleAtRealtime;
        private bool configured;
        private bool registryEventsBound;
        private bool runtimeEventsBound;
        private bool authorityRunning;
        private bool authorityFaulted;
        private int authorityEpochResetCount;

        public bool IsConfigured { get { return configured; } }
        public bool IsAuthorityRunning { get { return authorityRunning; } }
        public bool IsAuthorityFaulted { get { return authorityFaulted; } }
        public int TrackedPairCount { get { return trackersByPairKey.Count; } }
        public int ActiveTopologySessionCount
        {
            get
            {
                return groupTopologyRuntime == null
                    ? 0
                    : groupTopologyRuntime.ActiveSessionCount;
            }
        }
        public string AuthorityEpochId { get { return authorityEpochId; } }
        public string LastFailure { get; private set; }

        //* این تابع پایشگر را به نمونه‌های واقعی رجیستری، آبجکت بازیکن و فرستنده رویداد متصل می‌کند.
        public void Configure(
            DedicatedServerRuntime dedicatedRuntime,
            DedicatedPlayerRegistry dedicatedPlayerRegistry,
            VoiceDedicatedAuthoritativePositionProvider dedicatedPositionProvider,
            VoiceDedicatedSessionDeltaSender dedicatedDeltaSender,
            long confirmedStabilityDelayMs)
        {
            if (dedicatedRuntime == null)
            {
                throw new ArgumentNullException("dedicatedRuntime");
            }

            if (dedicatedPlayerRegistry == null)
            {
                throw new ArgumentNullException("dedicatedPlayerRegistry");
            }

            if (dedicatedPositionProvider == null)
            {
                throw new ArgumentNullException("dedicatedPositionProvider");
            }

            if (dedicatedDeltaSender == null)
            {
                throw new ArgumentNullException("dedicatedDeltaSender");
            }

            if (confirmedStabilityDelayMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "confirmedStabilityDelayMs",
                    "The stability delay must be selected by benchmark and must be greater than zero.");
            }

            UnbindEvents();

            runtime = dedicatedRuntime;
            playerRegistry = dedicatedPlayerRegistry;
            positionProvider = dedicatedPositionProvider;
            deltaSender = dedicatedDeltaSender;
            stabilityDelayMs = confirmedStabilityDelayMs;
            authorityEpochId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            sourceSequence = 0;
            groupTopologyRuntime = new VoiceDedicatedGroupTopologyRuntime();
            configured = true;
            authorityFaulted = false;
            LastFailure = string.Empty;
            trackersByPairKey.Clear();
            pendingTrackerRemovals.Clear();
            observedPairKeys.Clear();
            currentScopeUserKeys.Clear();
            currentScopeParticipantKeys.Clear();
            currentResolvedParticipantKeys.Clear();
            pairObservations.Clear();
            authorityEpochResetCount = 0;
            nextSampleAtRealtime = 0.0f;

            BindEvents();

            if (runtime.IsRunning)
            {
                StartAuthority(runtime.GetCurrentConfig());
            }

            Debug.Log(
                "[VoiceDedicatedAuthorityMonitor] Configured" +
                " | authorityEpochId=" + authorityEpochId +
                " | stabilityDelayMs=" + stabilityDelayMs);
        }

        //* این تابع پس از شروع رانتایم، فاصله نمونه‌برداری را از نرخ تیک واقعی سرور محاسبه و پایش را فعال می‌کند.
        private void StartAuthority(DedicatedServerConfigData config)
        {
            if (!configured || authorityFaulted) return;

            if (config == null)
            {
                FailAuthority("Dedicated runtime config is missing.");
                return;
            }

            int tickRate = Mathf.Max(1, config.tickRate);
            sampleIntervalSeconds = 1.0f / tickRate;
            nextSampleAtRealtime = Time.realtimeSinceStartup;
            authorityRunning = true;

            Debug.Log(
                "[VoiceDedicatedAuthorityMonitor] Authority started" +
                " | serverId=" + SafeTrim(config.serverId) +
                " | tickRate=" + tickRate +
                " | sampleIntervalSeconds=" + sampleIntervalSeconds.ToString("F4") +
                " | stabilityDelayMs=" + stabilityDelayMs);
        }

        //* این تابع هنگام توقف رانتایم، همه سشن‌های فعال را با علت قطع سرور می‌بندد.
        private void HandleRuntimeStopped()
        {
            CloseAllActiveTrackers(
                VoiceDedicatedSessionReason.DedicatedDisconnected,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            authorityRunning = false;
            nextSampleAtRealtime = 0.0f;

            Debug.Log(
                "[VoiceDedicatedAuthorityMonitor] Authority stopped" +
                " | remainingTrackers=" + trackersByPairKey.Count);
        }

        //* این تابع رویداد شروع رانتایم را به فعال‌شدن پایش فاصله متصل می‌کند.
        private void HandleRuntimeStarted(DedicatedServerConfigData config)
        {
            StartAuthority(config);
        }

        //* این تابع در هر فریم فقط در زمان نمونه‌برداری تعیین‌شده، زوج‌های همان سرور و روم را بررسی می‌کند.
        private void Update()
        {
            if (!configured ||
                !authorityRunning ||
                authorityFaulted ||
                Time.realtimeSinceStartup < nextSampleAtRealtime)
            {
                return;
            }

            nextSampleAtRealtime = Time.realtimeSinceStartup + sampleIntervalSeconds;

            try
            {
                EvaluateCurrentPairs();
            }
            catch (Exception exception)
            {
                FailAuthority(exception.ToString());
            }
        }

        //* این تابع اسنپ‌شات معتبر بازیکنان را می‌گیرد، آبجکت قطعی آن‌ها را پیدا می‌کند و فقط زوج‌های همان روم را ارزیابی می‌کند.
        private void EvaluateCurrentPairs()
        {
            List<DedicatedPlayerSession> sessions = playerRegistry.CreateSnapshot();
            sessions.Sort(CompareSessions);

            List<ResolvedParticipant> participants =
                new List<ResolvedParticipant>(sessions.Count);

            observedPairKeys.Clear();
            currentScopeUserKeys.Clear();
            currentScopeParticipantKeys.Clear();
            currentResolvedParticipantKeys.Clear();
            pairObservations.Clear();

            for (int index = 0; index < sessions.Count; index += 1)
            {
                DedicatedPlayerSession session = sessions[index];
                if (session == null || !session.IsMirrorLikeReady) continue;

                string serverId = SafeTrim(session.serverId);
                string roomId = SafeTrim(session.roomId);
                string userId = SafeTrim(session.userId);
                string connectionId = SafeTrim(session.connectionId);

                if (serverId.Length == 0 ||
                    roomId.Length == 0 ||
                    userId.Length == 0 ||
                    connectionId.Length == 0)
                {
                    continue;
                }

                currentScopeUserKeys.Add(BuildScopeUserKey(serverId, roomId, userId));
                currentScopeParticipantKeys.Add(
                    BuildScopeParticipantKey(
                        serverId,
                        roomId,
                        userId,
                        connectionId));

                Vector3 authoritativePosition;
                string positionRejectReason;

                if (!positionProvider.TryResolvePosition(
                        session,
                        out authoritativePosition,
                        out positionRejectReason))
                {
                    continue;
                }

                participants.Add(
                    new ResolvedParticipant(
                        session,
                        authoritativePosition));

                currentResolvedParticipantKeys.Add(
                    BuildScopeParticipantKey(
                        serverId,
                        roomId,
                        userId,
                        connectionId));
            }

            long stabilityClockMs = ReadMonotonicMilliseconds();
            long effectiveAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (int firstIndex = 0; firstIndex < participants.Count; firstIndex += 1)
            {
                ResolvedParticipant first = participants[firstIndex];

                for (int secondIndex = firstIndex + 1;
                     secondIndex < participants.Count;
                     secondIndex += 1)
                {
                    ResolvedParticipant second = participants[secondIndex];

                    if (!string.Equals(
                            first.Session.serverId,
                            second.Session.serverId,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            first.Session.roomId,
                            second.Session.roomId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    VoiceDedicatedParticipantPair pair =
                        new VoiceDedicatedParticipantPair(
                            first.Session.serverId,
                            first.Session.roomId,
                            first.Session.userId,
                            first.Session.connectionId,
                            second.Session.userId,
                            second.Session.connectionId);

                    PairTracker tracker;
                    if (!trackersByPairKey.TryGetValue(pair.PairKey, out tracker))
                    {
                        tracker = new PairTracker(
                            pair,
                            new VoiceDedicatedProximityEvaluator(stabilityDelayMs));

                        trackersByPairKey.Add(pair.PairKey, tracker);
                    }

                    observedPairKeys.Add(pair.PairKey);

                    float distanceMeters = Vector3.Distance(
                        first.AuthoritativePosition,
                        second.AuthoritativePosition);

                    tracker.LastDistanceMeters = distanceMeters;

                    VoiceDedicatedProximityDecision decision =
                        tracker.Evaluator.Evaluate(
                            pair,
                            distanceMeters,
                            stabilityClockMs,
                            effectiveAtMs);

                    pairObservations.Add(
                        VoiceDedicatedTopologyPairObservation.FromDecision(
                            decision));

                    if (!tracker.SessionActive &&
                        tracker.Evaluator.TrackedPairCount == 0)
                    {
                        pendingTrackerRemovals.Add(pair.PairKey);
                    }
                }
            }

            RemoveUnavailableTopologyParticipants(effectiveAtMs);

            IReadOnlyList<VoiceDedicatedSessionDelta> topologyDeltas =
                groupTopologyRuntime.ApplyPairObservations(
                    pairObservations,
                    authorityEpochId,
                    NextSourceSequence);

            EnqueueAllOrFail(topologyDeltas);
            SynchronizePairTrackerAssignments();
            CloseAndRemoveUnobservedTrackers(effectiveAtMs);
            RemovePendingTrackers();
        }

        //* این تابع تصمیم ایجاد، تغییر فاصله یا خروج فاصله‌ای را به صف رویدادهای نود منتقل می‌کند.
        private void ApplyProximityDecision(
            PairTracker tracker,
            VoiceDedicatedProximityDecision decision)
        {
            if (tracker == null)
            {
                throw new ArgumentNullException("tracker");
            }

            IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                groupTopologyRuntime.ApplyPairObservations(
                    new[]
                    {
                        VoiceDedicatedTopologyPairObservation.FromDecision(
                            decision)
                    },
                    authorityEpochId,
                    NextSourceSequence);

            EnqueueAllOrFail(deltas);
            SynchronizePairTrackerAssignment(tracker);
        }

        //* این تابع زوج‌هایی را که دیگر هر دو عضو معتبر ندارند با علت مستند می‌بندد و از حافظه حذف می‌کند.
        private void CloseAndRemoveUnobservedTrackers(long effectiveAtMs)
        {
            foreach (KeyValuePair<string, PairTracker> entry in trackersByPairKey)
            {
                if (observedPairKeys.Contains(entry.Key)) continue;

                PairTracker tracker = entry.Value;

                string activeSessionId;
                if (groupTopologyRuntime.TryGetSessionIdForPair(
                        tracker.Pair,
                        out activeSessionId))
                {
                    throw new InvalidOperationException(
                        "An unobserved Voice pair remained assigned after participant cleanup" +
                        " | sessionId=" + activeSessionId +
                        " | effectiveAtMs=" + effectiveAtMs);
                }

                groupTopologyRuntime.ForgetUnassignedPair(entry.Key);

                pendingTrackerRemovals.Add(entry.Key);
            }
        }

        //* این تابع عضو فاقد موقعیت معتبر یا خارج‌شده از Scope را یک بار از تمام Sessionهای گروهی حذف می‌کند.
        private void RemoveUnavailableTopologyParticipants(long effectiveAtMs)
        {
            IReadOnlyList<VoiceDedicatedGroupParticipant> participants =
                groupTopologyRuntime.CreateParticipantSnapshot();

            for (int index = 0; index < participants.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant participant = participants[index];
                string participantKey = BuildScopeParticipantKey(
                    participant.ServerId,
                    participant.RoomId,
                    participant.UserId,
                    participant.ConnectionId);

                if (currentResolvedParticipantKeys.Contains(participantKey)) continue;

                bool userStillInScope = currentScopeUserKeys.Contains(
                    BuildScopeUserKey(
                        participant.ServerId,
                        participant.RoomId,
                        participant.UserId));
                bool connectionStillInScope =
                    currentScopeParticipantKeys.Contains(participantKey);

                VoiceDedicatedSessionReason reason = !userStillInScope
                    ? VoiceDedicatedSessionReason.RoomLeft
                    : !connectionStillInScope
                        ? VoiceDedicatedSessionReason.DedicatedDisconnected
                        : VoiceDedicatedSessionReason.AvatarDespawned;

                IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                    groupTopologyRuntime.RemoveParticipant(
                        participant,
                        reason,
                        effectiveAtMs,
                        authorityEpochId,
                        NextSourceSequence);

                EnqueueAllOrFail(deltas);
            }
        }

        //* این تابع SessionId واقعی هر Pair را پس از Join، Leave، Merge یا Burn به Tracker منعکس می‌کند.
        private void SynchronizePairTrackerAssignments()
        {
            foreach (PairTracker tracker in trackersByPairKey.Values)
            {
                SynchronizePairTrackerAssignment(tracker);
            }
        }

        //* این تابع نگاشت یک Tracker را بدون تغییر وضعیت داخلی Distance Evaluator تازه می‌کند.
        private void SynchronizePairTrackerAssignment(PairTracker tracker)
        {
            string sessionId;
            tracker.SessionActive = groupTopologyRuntime.TryGetSessionIdForPair(
                tracker.Pair,
                out sessionId);
            tracker.SessionId = tracker.SessionActive
                ? sessionId
                : string.Empty;
        }

        //* این تابع حذف قطعی بازیکن از رجیستری را فقط برای سشن‌های شامل همان userId اعمال می‌کند.
        private void HandlePlayerRemoved(
            DedicatedPlayerSession session,
            string removalReason)
        {
            if (!configured || session == null) return;

            string serverId = SafeTrim(session.serverId);
            string roomId = SafeTrim(session.roomId);
            string userId = SafeTrim(session.userId);
            string connectionId = SafeTrim(session.connectionId);

            if (serverId.Length == 0 ||
                roomId.Length == 0 ||
                userId.Length == 0 ||
                connectionId.Length == 0)
            {
                return;
            }

            long effectiveAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            VoiceDedicatedSessionReason reason =
                VoiceDedicatedSessionDelta.MapPlayerRemovalReason(removalReason);

            VoiceDedicatedGroupParticipant participant =
                new VoiceDedicatedGroupParticipant(
                    serverId,
                    roomId,
                    userId,
                    connectionId);

            IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                groupTopologyRuntime.RemoveParticipant(
                    participant,
                    reason,
                    effectiveAtMs,
                    authorityEpochId,
                    NextSourceSequence);

            EnqueueAllOrFail(deltas);

            foreach (KeyValuePair<string, PairTracker> entry in trackersByPairKey)
            {
                PairTracker tracker = entry.Value;

                bool sameScope =
                    string.Equals(tracker.Pair.ServerId, serverId, StringComparison.Ordinal) &&
                    string.Equals(tracker.Pair.RoomId, roomId, StringComparison.Ordinal);

                bool containsParticipant =
                    (string.Equals(
                         tracker.Pair.FirstUserId,
                         userId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         tracker.Pair.FirstConnectionId,
                         connectionId,
                         StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(
                         tracker.Pair.SecondUserId,
                         userId,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         tracker.Pair.SecondConnectionId,
                         connectionId,
                         StringComparison.OrdinalIgnoreCase));

                if (!sameScope || !containsParticipant) continue;

                pendingTrackerRemovals.Add(entry.Key);
            }

            RemovePendingTrackers();

            if (playerRegistry.CurrentPlayerCount == 0 &&
                (authorityFaulted ||
                 (deltaSender != null && deltaSender.IsQueueFaulted)))
            {
                ResetFaultedAuthorityAfterEmptyRegistry(removalReason);
            }
        }

        //* این تابع فقط پس از حذف قطعی آخرین بازیکن، وضعیت خراب پایش و صف را برای ورود تازه بعدی از صفر آماده می‌کند.
        private void ResetFaultedAuthorityAfterEmptyRegistry(string removalReason)
        {
            if (!configured || playerRegistry == null || deltaSender == null) return;
            if (playerRegistry.CurrentPlayerCount != 0) return;
            if (!authorityFaulted && !deltaSender.IsQueueFaulted) return;

            string previousEpochId = authorityEpochId;
            authorityEpochId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            sourceSequence = 0;
            trackersByPairKey.Clear();
            pendingTrackerRemovals.Clear();
            observedPairKeys.Clear();
            currentScopeUserKeys.Clear();
            currentScopeParticipantKeys.Clear();
            currentResolvedParticipantKeys.Clear();
            pairObservations.Clear();
            groupTopologyRuntime.ResetState();
            authorityEpochResetCount = 0;
            authorityFaulted = false;
            LastFailure = string.Empty;

            deltaSender.ResetQueueAfterAuthorityEpochReset(
                "authority_epoch_reset_after_empty_registry_cleanup");

            if (runtime != null && runtime.IsRunning)
            {
                authorityRunning = true;
                nextSampleAtRealtime = Time.realtimeSinceStartup +
                                       Mathf.Max(0.0f, sampleIntervalSeconds);
            }
            else
            {
                authorityRunning = false;
                nextSampleAtRealtime = 0.0f;
            }

            Debug.LogWarning(
                "VOICE_G4_EMPTY_REGISTRY_FAULT_RESET=PASS" +
                " | previousEpochId=" + previousEpochId +
                " | newEpochId=" + authorityEpochId +
                " | reason=" + CompactForLog(removalReason));
        }

        //* این تابع تمام سشن‌های فعال را هنگام توقف رانتایم با یک علت واحد می‌بندد.
        private void CloseAllActiveTrackers(
            VoiceDedicatedSessionReason reason,
            long effectiveAtMs)
        {
            IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                groupTopologyRuntime.CloseAll(
                    reason,
                    effectiveAtMs,
                    authorityEpochId,
                    NextSourceSequence);

            EnqueueAllOrFail(deltas);

            trackersByPairKey.Clear();
            pendingTrackerRemovals.Clear();
            pairObservations.Clear();
        }

        //* این تابع یک رویداد را وارد صف می‌کند و در صورت شکست، پایش را برای جلوگیری از واگرایی متوقف می‌کند.
        private void EnqueueOrFail(VoiceDedicatedSessionDelta delta)
        {
            string error;
            if (deltaSender.Enqueue(delta, out error)) return;
            FailAuthority("Voice delta enqueue failed: " + error);
        }

        //* این تابع Deltaهای یک تصمیم توپولوژی را با ترتیب SourceSequence وارد همان صف موجود می‌کند.
        private void EnqueueAllOrFail(
            IReadOnlyList<VoiceDedicatedSessionDelta> deltas)
        {
            if (deltas == null)
            {
                throw new ArgumentNullException("deltas");
            }

            for (int index = 0; index < deltas.Count; index += 1)
            {
                EnqueueOrFail(deltas[index]);
                if (authorityFaulted) return;
                LogTopologyDeltaQueued(deltas[index]);
            }
        }

        //* این تابع فقط تغییرات عضویت و عمر Session را برای تست واقعی ثبت می‌کند و Distance Update را لاگ نمی‌کند.
        private static void LogTopologyDeltaQueued(
            VoiceDedicatedSessionDelta delta)
        {
            if (delta == null ||
                string.Equals(
                    delta.type,
                    "distance_updated",
                    StringComparison.Ordinal))
            {
                return;
            }

            Debug.Log(
                "VOICE_G5_RUNTIME_DELTA_QUEUED" +
                " | type=" + CompactForLog(delta.type) +
                " | sessionId=" + CompactForLog(delta.sessionId) +
                " | firstUserId=" + CompactForLog(delta.firstUserId) +
                " | secondUserId=" + CompactForLog(delta.secondUserId) +
                " | memberUserId=" + CompactForLog(delta.memberUserId) +
                " | reason=" + delta.reason +
                " | sourceSequence=" + delta.sourceSequence);
        }

        //* این تابع شماره ترتیبی یکتای رویداد را در محدوده همین اجرای سرور افزایش می‌دهد.
        private long NextSourceSequence()
        {
            if (sourceSequence == long.MaxValue)
            {
                throw new InvalidOperationException(
                    "Dedicated Voice source sequence overflowed.");
            }

            sourceSequence += 1;
            return sourceSequence;
        }

        //* این تابع کلید حضور یک userId را در محدوده همان سرور و روم می‌سازد.
        private static string BuildScopeUserKey(
            string serverId,
            string roomId,
            string userId)
        {
            return SafeTrim(serverId) + "::" +
                   SafeTrim(roomId) + "::" +
                   SafeTrim(userId);
        }

        //* این تابع کلید هویت جاری یک کاربر را همراه با شناسه اتصال همان سرور و روم می‌سازد.
        private static string BuildScopeParticipantKey(
            string serverId,
            string roomId,
            string userId,
            string connectionId)
        {
            return BuildScopeUserKey(serverId, roomId, userId) + "::" +
                   SafeTrim(connectionId).Replace("-", string.Empty).ToLowerInvariant();
        }

        //* این تابع اسنپ‌شات بازیکنان را برای تولید زوج‌های قطعی به ترتیب ثابت مرتب می‌کند.
        private static int CompareSessions(
            DedicatedPlayerSession first,
            DedicatedPlayerSession second)
        {
            if (ReferenceEquals(first, second)) return 0;
            if (first == null) return 1;
            if (second == null) return -1;

            int serverCompare = string.CompareOrdinal(
                SafeTrim(first.serverId),
                SafeTrim(second.serverId));

            if (serverCompare != 0) return serverCompare;

            int roomCompare = string.CompareOrdinal(
                SafeTrim(first.roomId),
                SafeTrim(second.roomId));

            if (roomCompare != 0) return roomCompare;

            int userCompare = string.CompareOrdinal(
                SafeTrim(first.userId),
                SafeTrim(second.userId));

            if (userCompare != 0) return userCompare;

            return string.CompareOrdinal(
                SafeTrim(first.connectionId),
                SafeTrim(second.connectionId));
        }

        //* این تابع زمان یکنواخت را برای تأیید پایداری به میلی‌ثانیه تبدیل می‌کند.
        private static long ReadMonotonicMilliseconds()
        {
            double milliseconds =
                Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

            if (milliseconds <= 0.0) return 0;
            if (milliseconds >= long.MaxValue) return long.MaxValue;
            return (long)milliseconds;
        }

        //* این تابع متن را برای مقایسه هویتی بدون تولید مقدار حدسی پاک‌سازی می‌کند.
        private static string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }

        //* این تابع کلیدهای علامت‌خورده را بعد از پایان پیمایش از دیکشنری حذف می‌کند.
        private void RemovePendingTrackers()
        {
            for (int index = 0; index < pendingTrackerRemovals.Count; index += 1)
            {
                string pairKey = pendingTrackerRemovals[index];
                trackersByPairKey.Remove(pairKey);

                if (groupTopologyRuntime != null)
                {
                    groupTopologyRuntime.ForgetUnassignedPair(pairKey);
                }
            }

            pendingTrackerRemovals.Clear();
        }

        //* این تابع رویدادهای رجیستری، رانتایم و خطای صف را فقط یک بار متصل می‌کند.
        private void BindEvents()
        {
            if (!registryEventsBound && playerRegistry != null)
            {
                playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
                playerRegistry.PlayerRemoved += HandlePlayerRemoved;
                registryEventsBound = true;
            }

            if (!runtimeEventsBound && runtime != null)
            {
                runtime.RuntimeStarted -= HandleRuntimeStarted;
                runtime.RuntimeStopped -= HandleRuntimeStopped;
                runtime.RuntimeStarted += HandleRuntimeStarted;
                runtime.RuntimeStopped += HandleRuntimeStopped;
                runtimeEventsBound = true;
            }

            if (deltaSender != null)
            {
                deltaSender.QueueFaulted -= HandleSenderQueueFaulted;
                deltaSender.QueueFaulted += HandleSenderQueueFaulted;
            }
        }

        //* این تابع همه اشتراک‌های رویدادی را هنگام غیرفعال‌شدن یا تنظیم دوباره پاک می‌کند.
        private void UnbindEvents()
        {
            if (playerRegistry != null)
            {
                playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
            }

            if (runtime != null)
            {
                runtime.RuntimeStarted -= HandleRuntimeStarted;
                runtime.RuntimeStopped -= HandleRuntimeStopped;
            }

            if (deltaSender != null)
            {
                deltaSender.QueueFaulted -= HandleSenderQueueFaulted;
            }

            registryEventsBound = false;
            runtimeEventsBound = false;
        }

        //* این تابع خطای صف انتقال را بررسی می‌کند و خطای آماده نبودن اتصال صوتی را به بازنشانی نرم تبدیل می‌کند.
        private void HandleSenderQueueFaulted(string error)
        {
            if (IsVoiceConnectionReadinessFault(error))
            {
                ResetAuthorityEpochAfterVoiceConnectionReadinessFault(error);
                return;
            }

            ResetAuthorityEpochAfterSenderQueueFault(error);
        }

        //* این تابع خطای آماده نبودن اتصال صوتی را بدون خراب‌کردن پایشگر درمان می‌کند تا بعد از آماده‌شدن دو اتصال، زوج تازه دوباره ساخته شود.
        private void ResetAuthorityEpochAfterVoiceConnectionReadinessFault(string error)
        {
            if (!configured || deltaSender == null) return;

            string preservedEpochId = authorityEpochId;
            LastFailure = string.Empty;
            authorityFaulted = false;
            authorityEpochResetCount = 0;

            deltaSender.ResumeQueueAfterTransientConflict(
                "voice_connection_not_ready_same_epoch_retry | " +
                CompactForLog(error));

            if (runtime != null && runtime.IsRunning)
            {
                authorityRunning = true;
                nextSampleAtRealtime = Time.realtimeSinceStartup +
                                       Mathf.Max(sampleIntervalSeconds, 0.25f);
            }

            Debug.LogWarning(
                "VOICE_G4_PAIR_CONNECTION_NOT_READY_SAME_EPOCH_RETRY=PASS" +
                " | authorityEpochId=" + preservedEpochId +
                " | sourceSequence=" + sourceSequence +
                " | reason=" + CompactForLog(error));
        }

        //* این تابع تشخیص می‌دهد خطای صف فقط به آماده نبودن اتصال صوتی دو عضو مربوط است.
        private static bool IsVoiceConnectionReadinessFault(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;

            return error.IndexOf(
                       "voice_delta_pair_voice_connection_not_ready",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf(
                       "Both userId values must have one active Voice connection",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        //* این تابع پس از رد قطعی دسته رویداد، دوره اقتدار را تازه و صف دوره قبلی را پاک می‌کند.
        private void ResetAuthorityEpochAfterSenderQueueFault(string error)
        {
            if (!configured || deltaSender == null) return;

            authorityEpochResetCount += 1;
            if (authorityEpochResetCount > MaximumAuthorityEpochResetCount)
            {
                FailAuthority(
                    "Voice delta sender queue fault repeated too many times: " +
                    error);
                return;
            }

            string previousEpochId = authorityEpochId;
            authorityEpochId = Guid.NewGuid().ToString("D").ToLowerInvariant();
            sourceSequence = 0;
            trackersByPairKey.Clear();
            pendingTrackerRemovals.Clear();
            observedPairKeys.Clear();
            currentScopeUserKeys.Clear();
            currentScopeParticipantKeys.Clear();
            currentResolvedParticipantKeys.Clear();
            pairObservations.Clear();
            groupTopologyRuntime.ResetState();
            LastFailure = string.Empty;
            authorityFaulted = false;

            deltaSender.ResetQueueAfterAuthorityEpochReset(
                "authority_epoch_reset_after_sender_queue_fault");

            if (runtime != null && runtime.IsRunning)
            {
                authorityRunning = true;
                nextSampleAtRealtime = Time.realtimeSinceStartup +
                                       Mathf.Max(0.0f, sampleIntervalSeconds);
            }

            Debug.LogWarning(
                "VOICE_G3_AUTHORITY_EPOCH_RESET=PASS" +
                " | previousEpochId=" + previousEpochId +
                " | newEpochId=" + authorityEpochId +
                " | resetCount=" + authorityEpochResetCount +
                " | reason=" + CompactForLog(error));
        }

        //* این تابع متن خطا را برای ثبت کوتاه و یک‌خطی آماده می‌کند.
        private static string CompactForLog(string value)
        {
            string text = string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("\r", " ").Replace("\n", " ");

            return text.Length <= 512
                ? text
                : text.Substring(0, 512);
        }

        //* این تابع خطای قطعی را ثبت و تولید رویدادهای تازه را متوقف می‌کند.
        private void FailAuthority(string error)
        {
            if (authorityFaulted) return;

            authorityFaulted = true;
            authorityRunning = false;
            LastFailure = string.IsNullOrWhiteSpace(error)
                ? "unknown_voice_dedicated_authority_failure"
                : error.Trim();

            Debug.LogError(
                "[VoiceDedicatedAuthorityMonitor] Authority faulted" +
                " | trackedPairs=" + trackersByPairKey.Count +
                " | error=" + LastFailure);
        }

        //* این تابع هنگام فعال‌شدن دوباره آبجکت، اشتراک‌های لازم را بازیابی می‌کند.
        private void OnEnable()
        {
            if (configured) BindEvents();
        }

        //* این تابع هنگام غیرفعال‌شدن آبجکت، اشتراک‌های رویدادی را پاک می‌کند.
        private void OnDisable()
        {
            UnbindEvents();
        }

        private sealed class ResolvedParticipant
        {
            public DedicatedPlayerSession Session { get; private set; }
            public Vector3 AuthoritativePosition { get; private set; }

            //* این سازنده سشن تأییدشده و موقعیت پذیرفته‌شده همان کاربر را کنار هم نگه می‌دارد.
            public ResolvedParticipant(
                DedicatedPlayerSession session,
                Vector3 authoritativePosition)
            {
                Session = session;
                AuthoritativePosition = authoritativePosition;
            }
        }

        private sealed class PairTracker
        {
            public VoiceDedicatedParticipantPair Pair { get; private set; }
            public VoiceDedicatedProximityEvaluator Evaluator { get; private set; }
            public bool SessionActive;
            public string SessionId;
            public float LastDistanceMeters;

            //* این سازنده وضعیت مستقل یک زوج غیرانتقالی را آماده می‌کند.
            public PairTracker(
                VoiceDedicatedParticipantPair pair,
                VoiceDedicatedProximityEvaluator evaluator)
            {
                Pair = pair;
                Evaluator = evaluator;
                SessionActive = false;
                SessionId = string.Empty;
                LastDistanceMeters = 0.0f;
            }
        }
    }
}

/*
توضیح فایل:
این فایل فقط داخل سرور اختصاصی یونیتی اجرا می‌شود. هویت بازیکن را از سشن معتبر سرور می‌گیرد و موقعیت پذیرفته‌شده را از مخزن وضعیت سرور دریافت می‌کند. فراهم‌کننده پیش از تحویل موقعیت، تطبیق کاربر، اتصال، روم، سرور، توالی و تازگی داده را کنترل می‌کند. زوج‌ها فقط در همان سرور و همان روم ساخته می‌شوند.
*/
