using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly HashSet<string> eligibleParticipantKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> transientPositionLossStartedAt =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly HashSet<string> transientPositionGraceKeys =
            new HashSet<string>(StringComparer.Ordinal);
        // Kept serialized for scene/prefab compatibility; no longer expires members.
        [SerializeField, Min(0f)] private float transientPositionLossGraceSeconds = 1.5f;
        private readonly List<string> pendingTrackerRemovals =
            new List<string>();
        private readonly List<VoiceDedicatedTopologyPairObservation> pairObservations =
            new List<VoiceDedicatedTopologyPairObservation>();

        private VoiceDedicatedGroupTopologyRuntime groupTopologyRuntime;

        private const int MaximumAuthorityEpochResetCount = 8;
        private const float SessionEligibilityRefreshIntervalSeconds = 0.5f;
        private const float SessionGuardHeartbeatIntervalSeconds = 2.0f;
        private const float MissingSessionAuditIntervalSeconds = 2.0f;
        private const float MinimumReadinessRecoveryIntervalSeconds = 30.0f;

        private long stabilityDelayMs;
        private string authorityEpochId = string.Empty;
        private long sourceSequence;
        private float sampleIntervalSeconds;
        private float nextSampleAtRealtime;
        private float nextMissingSessionAuditAtRealtime;
        private bool configured;
        private bool registryEventsBound;
        private bool runtimeEventsBound;
        private bool authorityRunning;
        private bool authorityFaulted;
        private bool eligibilitySnapshotReady;
        private bool eligibilityRefreshRunning;
        private float nextEligibilityRefreshAtRealtime;
        private CancellationTokenSource eligibilityCts;
        private int authorityEpochResetCount;
        private float nextReadinessRecoveryAtRealtime;

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
            transientPositionLossStartedAt.Clear();
            transientPositionGraceKeys.Clear();
            eligibleParticipantKeys.Clear();
            pairObservations.Clear();
            eligibilitySnapshotReady = false;
            eligibilityRefreshRunning = false;
            nextEligibilityRefreshAtRealtime = 0.0f;
            authorityEpochResetCount = 0;
            nextReadinessRecoveryAtRealtime = 0.0f;
            nextSampleAtRealtime = 0.0f;
            nextMissingSessionAuditAtRealtime = 0.0f;

            ResetEligibilityCancellationSource();
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
            nextMissingSessionAuditAtRealtime = 0.0f;
            nextEligibilityRefreshAtRealtime = 0.0f;
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
            nextMissingSessionAuditAtRealtime = 0.0f;
            eligibilitySnapshotReady = false;
            eligibilityRefreshRunning = false;
            nextEligibilityRefreshAtRealtime = 0.0f;
            eligibleParticipantKeys.Clear();
            transientPositionLossStartedAt.Clear();
            transientPositionGraceKeys.Clear();

            Debug.Log(
                "[VoiceDedicatedAuthorityMonitor] Authority stopped" +
                " | remainingTrackers=" + trackersByPairKey.Count);
        }

        //* این تابع رویداد شروع رانتایم را به فعال‌شدن پایش فاصله متصل می‌کند.
        private void HandleRuntimeStarted(DedicatedServerConfigData config)
        {
            StartAuthority(config);
        }

        //* این تابع Snapshot شرط Mic/Speaker را مستقل تازه می‌کند و فقط با Snapshot معتبر زوج‌های همان سرور و روم را ارزیابی می‌کند.
        private void Update()
        {
            if (!configured ||
                !authorityRunning ||
                authorityFaulted)
            {
                return;
            }

            if (playerRegistry == null || playerRegistry.CurrentPlayerCount == 0)
            {
                eligibilitySnapshotReady = false;
                eligibleParticipantKeys.Clear();
                transientPositionLossStartedAt.Clear();
                transientPositionGraceKeys.Clear();
                return;
            }

            TryStartSessionEligibilityRefresh();

            if (!eligibilitySnapshotReady ||
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

        //* این تابع Poll امن Eligibility را با فاصله محدود و بدون هم‌پوشانی درخواست‌ها شروع می‌کند.
        private void TryStartSessionEligibilityRefresh()
        {
            if (eligibilityRefreshRunning ||
                deltaSender == null ||
                !deltaSender.IsConfigured ||
                !deltaSender.IsTransportEnabled ||
                eligibilityCts == null ||
                eligibilityCts.IsCancellationRequested ||
                Time.realtimeSinceStartup < nextEligibilityRefreshAtRealtime)
            {
                return;
            }

            eligibilityRefreshRunning = true;
            nextEligibilityRefreshAtRealtime =
                Time.realtimeSinceStartup + SessionEligibilityRefreshIntervalSeconds;

            _ = RefreshSessionEligibilityAsync(eligibilityCts.Token);
        }

        //* این تابع Snapshot فقط-خواندنی Mic/Speaker را می‌گیرد و شکست HTTP را از صف Session Delta جدا نگه می‌دارد.
        private async Task RefreshSessionEligibilityAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                VoiceDedicatedSessionEligibilitySnapshot snapshot =
                    await deltaSender.FetchSessionEligibilitySnapshotAsync(
                        cancellationToken);

                if (snapshot != null)
                {
                    ApplySessionEligibilitySnapshot(snapshot);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "VOICE_MS5_ELIGIBILITY_REFRESH=FAIL" +
                    " | error=" + CompactForLog(exception.Message));
            }
            finally
            {
                eligibilityRefreshRunning = false;
            }
        }

        //* این تابع فقط participantهای eligible همان serverId را در Gate مستقل Topology جایگزین می‌کند.
        private void ApplySessionEligibilitySnapshot(
            VoiceDedicatedSessionEligibilitySnapshot snapshot)
        {
            if (snapshot == null || runtime == null) return;

            DedicatedServerConfigData config = runtime.GetCurrentConfig();
            string expectedServerId = config == null
                ? string.Empty
                : SafeTrim(config.serverId);

            string snapshotServerId = SafeTrim(snapshot.serverId);
            if (expectedServerId.Length == 0 ||
                !string.Equals(
                    expectedServerId,
                    snapshotServerId,
                    StringComparison.Ordinal))
            {
                Debug.LogWarning(
                    "VOICE_MS5_ELIGIBILITY_SNAPSHOT_REJECTED=PASS" +
                    " | expectedServerId=" + expectedServerId +
                    " | snapshotServerId=" + snapshotServerId);
                return;
            }

            HashSet<string> nextEligibleParticipantKeys =
                new HashSet<string>(StringComparer.Ordinal);

            VoiceDedicatedSessionEligibilityParticipant[] participants =
                snapshot.participants ??
                Array.Empty<VoiceDedicatedSessionEligibilityParticipant>();

            for (int index = 0; index < participants.Length; index += 1)
            {
                VoiceDedicatedSessionEligibilityParticipant participant =
                    participants[index];

                if (participant == null || !participant.eligible) continue;

                string roomId = SafeTrim(participant.roomId);
                string userId = SafeTrim(participant.userId);
                string connectionId = SafeTrim(participant.connectionId);

                if (roomId.Length == 0 ||
                    userId.Length == 0 ||
                    connectionId.Length == 0)
                {
                    continue;
                }

                nextEligibleParticipantKeys.Add(
                    BuildScopeParticipantKey(
                        snapshotServerId,
                        roomId,
                        userId,
                        connectionId));
            }

            bool changed =
                !eligibilitySnapshotReady ||
                !eligibleParticipantKeys.SetEquals(
                    nextEligibleParticipantKeys);

            eligibleParticipantKeys.Clear();
            eligibleParticipantKeys.UnionWith(nextEligibleParticipantKeys);
            eligibilitySnapshotReady = true;

            TryRecoverSustainedReadinessConflict(snapshotServerId);

            if (changed)
            {
                Debug.Log(
                    "VOICE_MS5_DEDICATED_ELIGIBILITY_SYNC=PASS" +
                    " | serverId=" + snapshotServerId +
                    " | eligibleParticipants=" + eligibleParticipantKeys.Count +
                    " | snapshotParticipants=" + participants.Length);
            }
        }

        //* این تابع فقط با اثبات عضویت فعلی یا منسوخ‌شدن شناسه Dedicated، صف بلوکه‌شده را پس از مهلت Retry بازسازی می‌کند.
        private void TryRecoverSustainedReadinessConflict(string serverId)
        {
            if (deltaSender == null || playerRegistry == null ||
                groupTopologyRuntime == null || !authorityRunning || authorityFaulted ||
                Time.realtimeSinceStartup < nextReadinessRecoveryAtRealtime)
            {
                return;
            }

            VoiceDedicatedSessionDelta head;
            float blockedSeconds;
            if (!deltaSender.TryGetSustainedReadinessBlockedHead(
                    out head, out blockedSeconds) || head == null ||
                !string.Equals(head.serverId, serverId, StringComparison.Ordinal))
            {
                return;
            }

            bool firstStillCurrent = false;
            bool secondStillCurrent = false;
            List<DedicatedPlayerSession> players = playerRegistry.CreateSnapshot();

            for (int index = 0; index < players.Count; index += 1)
            {
                DedicatedPlayerSession player = players[index];
                if (player == null || !player.IsMirrorLikeReady ||
                    !string.Equals(player.serverId, serverId, StringComparison.Ordinal) ||
                    !string.Equals(player.roomId, head.roomId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(player.userId, head.firstUserId, StringComparison.Ordinal) &&
                    string.Equals(player.connectionId, head.firstConnectionId, StringComparison.OrdinalIgnoreCase))
                {
                    firstStillCurrent = true;
                }

                if (string.Equals(player.userId, head.secondUserId, StringComparison.Ordinal) &&
                    string.Equals(player.connectionId, head.secondConnectionId, StringComparison.OrdinalIgnoreCase))
                {
                    secondStillCurrent = true;
                }
            }

            bool obsoletePair = !firstStillCurrent || !secondStillCurrent;
            bool bothVoiceReady =
                eligibleParticipantKeys.Contains(BuildScopeParticipantKey(
                    serverId, head.roomId, head.firstUserId, head.firstConnectionId)) &&
                eligibleParticipantKeys.Contains(BuildScopeParticipantKey(
                    serverId, head.roomId, head.secondUserId, head.secondConnectionId));

            // Never discard an unresolved, still-authoritative pair during a
            // temporary Voice outage. The same FIFO batch can succeed on retry.
            if (!obsoletePair && !bothVoiceReady) return;

            // Sequence/epoch is scoped to the whole Dedicated process. An
            // epoch reset would also discard deltas from unrelated rooms.
            // Permit it only when the blocked pair is the sole local session.
            if (!deltaSender.PendingEventsBelongToRoom(head.roomId)) return;

            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> activeSessions =
                groupTopologyRuntime.CreateSessionSnapshot();
            for (int index = 0; index < activeSessions.Count; index += 1)
            {
                VoiceDedicatedGroupSessionSnapshot session = activeSessions[index];
                if (session == null ||
                    !string.Equals(session.SessionId, head.sessionId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(session.RoomId, head.roomId, StringComparison.Ordinal) ||
                    session.Members == null || session.Members.Count != 2)
                {
                    return;
                }
            }

            nextReadinessRecoveryAtRealtime =
                Time.realtimeSinceStartup + MinimumReadinessRecoveryIntervalSeconds;

            Debug.LogWarning(
                "VOICE_G4_STALE_READINESS_DELTA_RECOVERY=PASS" +
                " | roomId=" + CompactForLog(head.roomId) +
                " | sessionId=" + CompactForLog(head.sessionId) +
                " | sourceSequence=" + head.sourceSequence +
                " | blockedSeconds=" + blockedSeconds.ToString("F1") +
                " | obsoletePair=" + obsoletePair +
                " | bothVoiceReady=" + bothVoiceReady);

            ResetAuthorityEpochAfterSenderQueueFault(
                "voice_readiness_conflict_recovered_after_authoritative_snapshot" +
                " | obsoletePair=" + obsoletePair +
                " | bothVoiceReady=" + bothVoiceReady);
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
            transientPositionGraceKeys.Clear();
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

                string participantKey =
                    BuildScopeParticipantKey(
                        serverId,
                        roomId,
                        userId,
                        connectionId);

                currentScopeParticipantKeys.Add(participantKey);

                if (!IsVoiceSessionEligible(participantKey))
                {
                    continue;
                }

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

                currentResolvedParticipantKeys.Add(participantKey);
                if (transientPositionLossStartedAt.Remove(participantKey))
                {
                    Debug.Log("VOICE_G5_TRANSIENT_POSITION_RECOVERED=PASS" +
                              " | participantKey=" + CompactForLog(participantKey));
                }
            }

            long stabilityClockMs = ReadMonotonicMilliseconds();
            long effectiveAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            List<VoiceDedicatedTopologyParticipantPosition> participantPositions =
                new List<VoiceDedicatedTopologyParticipantPosition>(participants.Count);

            for (int index = 0; index < participants.Count; index += 1)
            {
                ResolvedParticipant resolved = participants[index];
                DedicatedPlayerSession session = resolved.Session;
                Vector3 position = resolved.AuthoritativePosition;

                participantPositions.Add(
                    new VoiceDedicatedTopologyParticipantPosition(
                        new VoiceDedicatedGroupParticipant(
                            session.serverId,
                            session.roomId,
                            session.userId,
                            session.connectionId),
                        position.x,
                        position.y,
                        position.z,
                        effectiveAtMs));
            }

            // Retain an existing member while the exact registered connection and
            // voice eligibility are still valid, even when position samples stop.
            // Never synthesize a position or create a new proximity entry from stale data.
            // Definitive registry removal, room/connection changes and revoked
            // eligibility still remove the member through their original paths.
            List<string> protectedTopologyIdentityKeys =
                PrepareTransientPositionGraceForCurrentSnapshot();

            groupTopologyRuntime.UpdateParticipantPositions(
                participantPositions,
                effectiveAtMs,
                protectedTopologyIdentityKeys);

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
            ReconcileMissingPairSessions(stabilityClockMs, effectiveAtMs);
            GuardActivePairSessions(effectiveAtMs);
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

        //* این تابع نقطه واحد تصمیم‌گیری عضویت Voice Session است و شروط بعدی فقط به همین Gate اضافه می‌شوند.
        private bool IsVoiceSessionEligible(string participantKey)
        {
            return eligibilitySnapshotReady &&
                   eligibleParticipantKeys.Contains(participantKey);
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
                    if (IsPairTemporarilyMissingPosition(tracker.Pair))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        "An unobserved Voice pair remained assigned after participant cleanup" +
                        " | sessionId=" + activeSessionId +
                        " | effectiveAtMs=" + effectiveAtMs);
                }

                groupTopologyRuntime.ForgetUnassignedPair(entry.Key);

                pendingTrackerRemovals.Add(entry.Key);
            }
        }

        // Build per-sample protection for existing session members only.
        // A position timeout cannot revoke a still-registered, eligible member.
        // The same live room, connection and identity must remain in the registry.
        // Protection ends through the existing player removal, room change or
        // eligibility-revocation paths, not through a position-age timeout.
        private List<string> PrepareTransientPositionGraceForCurrentSnapshot()
        {
            List<string> protectedIdentityKeys = new List<string>();
            IReadOnlyList<VoiceDedicatedGroupParticipant> existingMembers =
                groupTopologyRuntime.CreateParticipantSnapshot();

            for (int index = 0; index < existingMembers.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant participant = existingMembers[index];
                string key = BuildScopeParticipantKey(
                    participant.ServerId,
                    participant.RoomId,
                    participant.UserId,
                    participant.ConnectionId);
                if (currentResolvedParticipantKeys.Contains(key)) continue;

                bool sameRoomAndUser = currentScopeUserKeys.Contains(
                    BuildScopeUserKey(
                        participant.ServerId,
                        participant.RoomId,
                        participant.UserId));
                bool sameLiveConnection = currentScopeParticipantKeys.Contains(key);
                bool stillEligible = IsVoiceSessionEligible(key);

                if (sameRoomAndUser && sameLiveConnection && stillEligible &&
                    IsWithinTransientPositionGrace(key))
                {
                    protectedIdentityKeys.Add(participant.IdentityKey);
                }
            }

            return protectedIdentityKeys;
        }

        // Remove only definitively absent, rebound, or ineligible participants.
        // A still-registered, eligible member with missing position is protected
        // and must not be treated as AvatarDespawned.
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

                bool voiceEligible =
                    IsVoiceSessionEligible(participantKey);

                // A live, eligible registry identity must never be removed
                // just because a transient position marker was not populated.
                // Re-arm protection here before topology and tracker cleanup.
                if (userStillInScope && connectionStillInScope && voiceEligible)
                {
                    IsWithinTransientPositionGrace(participantKey);
                    continue;
                }

                transientPositionLossStartedAt.Remove(participantKey);

                VoiceDedicatedSessionReason reason = !userStillInScope
                    ? VoiceDedicatedSessionReason.RoomLeft
                    : !connectionStillInScope
                        ? VoiceDedicatedSessionReason.DedicatedDisconnected
                        : !voiceEligible
                            ? VoiceDedicatedSessionReason.AccessRevoked
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

        // Legacy method name retained for source compatibility. The caller has
        // already verified exact registry identity, room and voice eligibility.
        // Do not use elapsed position age as a reason to remove that member.
        private bool IsWithinTransientPositionGrace(string participantKey)
        {
            float startedAt;
            if (!transientPositionLossStartedAt.TryGetValue(participantKey, out startedAt))
            {
                transientPositionLossStartedAt[participantKey] =
                    Time.realtimeSinceStartup;
                Debug.LogWarning("VOICE_G5_POSITION_UNCERTAIN_SESSION_PRESERVED=PASS" +
                                 " | participantKey=" + CompactForLog(participantKey) +
                                 " | reason=authoritative_position_unavailable" +
                                 " | until=registry_or_eligibility_change" +
                                 " | syntheticPosition=False" +
                                 " | legacyGraceSecondsIgnored=" +
                                 transientPositionLossGraceSeconds.ToString("F2"));
            }

            transientPositionGraceKeys.Add(participantKey);
            return true;
        }

        private bool IsPairTemporarilyMissingPosition(VoiceDedicatedParticipantPair pair)
        {
            return transientPositionGraceKeys.Contains(BuildScopeParticipantKey(
                       pair.ServerId, pair.RoomId, pair.FirstUserId, pair.FirstConnectionId)) ||
                   transientPositionGraceKeys.Contains(BuildScopeParticipantKey(
                       pair.ServerId, pair.RoomId, pair.SecondUserId, pair.SecondConnectionId));
        }

        //* این تابع SessionId واقعی هر Pair را پس از Join، Leave، Merge یا Burn به Tracker منعکس می‌کند.
        private void SynchronizePairTrackerAssignments()
        {
            foreach (PairTracker tracker in trackersByPairKey.Values)
            {
                SynchronizePairTrackerAssignment(tracker);
            }
        }

        //* این تابع نگاشت یک زوج را بدون تغییر وضعیت داخلی ارزیاب فاصله تازه می‌کند.
        private void SynchronizePairTrackerAssignment(PairTracker tracker)
        {
            bool previousActive = tracker.SessionActive;
            string previousSessionId = tracker.SessionId ?? string.Empty;

            string sessionId;
            tracker.SessionActive = groupTopologyRuntime.TryGetSessionIdForPair(
                tracker.Pair,
                out sessionId);
            tracker.SessionId = tracker.SessionActive
                ? sessionId
                : string.Empty;

            bool assignmentChanged =
                previousActive != tracker.SessionActive ||
                !string.Equals(
                    previousSessionId,
                    tracker.SessionId,
                    StringComparison.OrdinalIgnoreCase);

            if (!tracker.SessionActive)
            {
                tracker.NextSessionGuardAtRealtime = 0.0f;
                tracker.SessionGuardHeartbeatCount = 0;
                return;
            }

            tracker.MissingSessionSinceClockMs = -1L;
            tracker.NextMissingSessionAttemptClockMs = 0L;

            if (assignmentChanged)
            {
                tracker.NextSessionGuardAtRealtime =
                    Time.realtimeSinceStartup +
                    SessionGuardHeartbeatIntervalSeconds;
                tracker.SessionGuardHeartbeatCount = 0;
            }
        }

        // Verify an orphaned, stable pair against the complete topology before
        // replaying the normal session-create transition. Never split a group.
        private void ReconcileMissingPairSessions(
            long stabilityClockMs,
            long effectiveAtMs)
        {
            if (groupTopologyRuntime == null ||
                deltaSender == null ||
                !deltaSender.IsConfigured ||
                !deltaSender.IsTransportEnabled ||
                deltaSender.IsQueueFaulted ||
                Time.realtimeSinceStartup < nextMissingSessionAuditAtRealtime)
            {
                return;
            }

            nextMissingSessionAuditAtRealtime =
                Time.realtimeSinceStartup + MissingSessionAuditIntervalSeconds;

            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> sessions =
                groupTopologyRuntime.CreateSessionSnapshot();
            HashSet<string> assignedMembers =
                new HashSet<string>(StringComparer.Ordinal);

            for (int sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex += 1)
            {
                VoiceDedicatedGroupSessionSnapshot session = sessions[sessionIndex];
                for (int memberIndex = 0; memberIndex < session.Members.Count; memberIndex += 1)
                {
                    assignedMembers.Add(session.Members[memberIndex].IdentityKey);
                }
            }

            long requiredStabilityMs = Math.Max(
                stabilityDelayMs,
                (long)(MissingSessionAuditIntervalSeconds * 1000f));

            for (int index = 0; index < pairObservations.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation = pairObservations[index];
                PairTracker tracker;
                if (!trackersByPairKey.TryGetValue(observation.Pair.PairKey, out tracker))
                {
                    continue;
                }

                if (tracker.SessionActive ||
                    observation.State != VoiceDedicatedProximityState.Active ||
                    observation.DistanceMeters >= VoiceDedicatedProximityEvaluator.EnterDistanceMeters ||
                    tracker.Evaluator.ActiveSessionCount == 0 ||
                    !observedPairKeys.Contains(observation.Pair.PairKey))
                {
                    tracker.MissingSessionSinceClockMs = -1L;
                    tracker.NextMissingSessionAttemptClockMs = 0L;
                    continue;
                }

                VoiceDedicatedParticipantPair pair = observation.Pair;
                VoiceDedicatedGroupParticipant first = new VoiceDedicatedGroupParticipant(
                    pair.ServerId, pair.RoomId, pair.FirstUserId, pair.FirstConnectionId);
                VoiceDedicatedGroupParticipant second = new VoiceDedicatedGroupParticipant(
                    pair.ServerId, pair.RoomId, pair.SecondUserId, pair.SecondConnectionId);

                if (assignedMembers.Contains(first.IdentityKey) ||
                    assignedMembers.Contains(second.IdentityKey))
                {
                    // Group topology is authoritative for an already assigned member.
                    tracker.MissingSessionSinceClockMs = -1L;
                    tracker.NextMissingSessionAttemptClockMs = 0L;
                    continue;
                }

                if (tracker.MissingSessionSinceClockMs < 0L)
                {
                    tracker.MissingSessionSinceClockMs = stabilityClockMs;
                    continue;
                }

                if (stabilityClockMs - tracker.MissingSessionSinceClockMs < requiredStabilityMs ||
                    stabilityClockMs < tracker.NextMissingSessionAttemptClockMs)
                {
                    continue;
                }

                tracker.NextMissingSessionAttemptClockMs =
                    stabilityClockMs + requiredStabilityMs;

                VoiceDedicatedTopologyPairObservation reenter =
                    new VoiceDedicatedTopologyPairObservation(
                        pair,
                        VoiceDedicatedProximityState.Active,
                        VoiceDedicatedProximityDecisionType.SessionCreated,
                        Guid.NewGuid().ToString("D"),
                        observation.DistanceMeters,
                        effectiveAtMs);

                IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                    groupTopologyRuntime.ApplyPairObservations(
                        new[] { reenter },
                        authorityEpochId,
                        NextSourceSequence);

                EnqueueAllOrFail(deltas);
                if (authorityFaulted) return;

                SynchronizePairTrackerAssignments();

                Debug.Log(
                    "VOICE_W4_MISSING_SESSION_RECONCILIATION" +
                    " | pairKey=" + CompactForLog(pair.PairKey) +
                    " | distanceMeters=" + observation.DistanceMeters.ToString("F3") +
                    " | deltaCount=" + deltas.Count +
                    " | assigned=" + tracker.SessionActive);

                // Allow the next audit to see the updated group membership.
                return;
            }
        }

        //* این تابع برای نشست زوجی فعال یک اعلان ایجاد تکرارپذیر می‌فرستد تا اگر وضعیت نشست در بک‌اند از بین رفته باشد بدون انتظار برای حرکت دوباره ساخته شود.
        private void GuardActivePairSessions(long effectiveAtMs)
        {
            if (groupTopologyRuntime == null ||
                deltaSender == null ||
                !deltaSender.IsConfigured ||
                !deltaSender.IsTransportEnabled ||
                deltaSender.IsQueueFaulted)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> sessions =
                groupTopologyRuntime.CreateSessionSnapshot();

            for (int index = 0; index < sessions.Count; index += 1)
            {
                VoiceDedicatedGroupSessionSnapshot session = sessions[index];
                if (session == null ||
                    session.Members == null ||
                    session.Members.Count != 2)
                {
                    continue;
                }

                VoiceDedicatedGroupParticipant first = session.Members[0];
                VoiceDedicatedGroupParticipant second = session.Members[1];

                VoiceDedicatedParticipantPair pair =
                    new VoiceDedicatedParticipantPair(
                        session.ServerId,
                        session.RoomId,
                        first.UserId,
                        first.ConnectionId,
                        second.UserId,
                        second.ConnectionId);

                PairTracker tracker;
                if (!trackersByPairKey.TryGetValue(pair.PairKey, out tracker) ||
                    !tracker.SessionActive ||
                    !observedPairKeys.Contains(pair.PairKey) ||
                    !string.Equals(
                        tracker.SessionId,
                        session.SessionId,
                        StringComparison.OrdinalIgnoreCase) ||
                    tracker.LastDistanceMeters >=
                        VoiceDedicatedProximityEvaluator.ExitDistanceMeters ||
                    now < tracker.NextSessionGuardAtRealtime ||
                    deltaSender.ShouldDeferSessionGuard(
                        session.RoomId, session.SessionId))
                {
                    continue;
                }

                VoiceDedicatedProximityDecision heartbeatDecision =
                    new VoiceDedicatedProximityDecision(
                        VoiceDedicatedProximityDecisionType.SessionCreated,
                        VoiceDedicatedProximityState.Active,
                        VoiceDedicatedProximityReason.ProximityEnter,
                        tracker.Pair,
                        session.SessionId,
                        tracker.LastDistanceMeters,
                        effectiveAtMs);

                VoiceDedicatedSessionDelta heartbeat =
                    VoiceDedicatedSessionDelta.FromProximityDecision(
                        heartbeatDecision,
                        authorityEpochId,
                        NextSourceSequence());

                EnqueueOrFail(heartbeat);
                if (authorityFaulted) return;

                tracker.NextSessionGuardAtRealtime =
                    now + SessionGuardHeartbeatIntervalSeconds;
                tracker.SessionGuardHeartbeatCount += 1;

                if (tracker.SessionGuardHeartbeatCount == 1 ||
                    tracker.SessionGuardHeartbeatCount % 10 == 0)
                {
                    Debug.Log(
                        "VOICE_DEDICATED_SESSION_GUARD_HEARTBEAT=PASS" +
                        " | sessionId=" + session.SessionId +
                        " | pairKey=" + CompactForLog(pair.PairKey) +
                        " | distanceMeters=" +
                            tracker.LastDistanceMeters.ToString("F3") +
                        " | heartbeatCount=" +
                            tracker.SessionGuardHeartbeatCount +
                        " | intervalSeconds=" +
                            SessionGuardHeartbeatIntervalSeconds.ToString("F1"));
                }
            }
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

            string removedParticipantKey = BuildScopeParticipantKey(
                serverId, roomId, userId, connectionId);
            transientPositionLossStartedAt.Remove(removedParticipantKey);
            transientPositionGraceKeys.Remove(removedParticipantKey);

            long effectiveAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            VoiceDedicatedSessionReason reason =
                VoiceDedicatedSessionDelta.MapPlayerRemovalReason(removalReason);
            Debug.LogWarning("VOICE_G5_REGISTRY_PLAYER_REMOVED=OBSERVED" +
                             " | userId=" + CompactForLog(userId) +
                             " | connectionId=" + CompactForLog(connectionId) +
                             " | removalReason=" + CompactForLog(removalReason) +
                             " | mappedReason=" + (int)reason);

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
            transientPositionLossStartedAt.Clear();
            transientPositionGraceKeys.Clear();
            eligibleParticipantKeys.Clear();
            pairObservations.Clear();
            eligibilitySnapshotReady = false;
            nextEligibilityRefreshAtRealtime = 0.0f;
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
            transientPositionLossStartedAt.Clear();
            transientPositionGraceKeys.Clear();
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
                " | distanceMeters=" + delta.distanceMeters.ToString("F3") +
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
            transientPositionLossStartedAt.Clear();
            transientPositionGraceKeys.Clear();
            eligibleParticipantKeys.Clear();
            pairObservations.Clear();
            eligibilitySnapshotReady = false;
            nextEligibilityRefreshAtRealtime = 0.0f;
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

        //* این تابع منبع لغو Poll Eligibility را بدون نشت Task از نو آماده می‌کند.
        private void ResetEligibilityCancellationSource()
        {
            if (eligibilityCts != null)
            {
                eligibilityCts.Cancel();
                eligibilityCts.Dispose();
            }

            eligibilityCts = new CancellationTokenSource();
            eligibilityRefreshRunning = false;
        }

        //* این تابع هنگام فعال‌شدن دوباره آبجکت، اشتراک‌ها و Poll Eligibility را بازیابی می‌کند.
        private void OnEnable()
        {
            if (!configured) return;

            if (eligibilityCts == null || eligibilityCts.IsCancellationRequested)
            {
                ResetEligibilityCancellationSource();
            }

            nextEligibilityRefreshAtRealtime = 0.0f;
            BindEvents();
        }

        //* این تابع هنگام غیرفعال‌شدن آبجکت، Poll در حال اجرا و اشتراک‌های رویدادی را پاک می‌کند.
        private void OnDisable()
        {
            if (eligibilityCts != null && !eligibilityCts.IsCancellationRequested)
            {
                eligibilityCts.Cancel();
            }

            eligibilityRefreshRunning = false;
            UnbindEvents();
        }

        //* این تابع منبع لغو Eligibility را هنگام حذف آبجکت آزاد می‌کند.
        private void OnDestroy()
        {
            if (eligibilityCts == null) return;

            eligibilityCts.Cancel();
            eligibilityCts.Dispose();
            eligibilityCts = null;
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
            public float NextSessionGuardAtRealtime;
            public int SessionGuardHeartbeatCount;
            public long MissingSessionSinceClockMs;
            public long NextMissingSessionAttemptClockMs;

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
                NextSessionGuardAtRealtime = 0.0f;
                SessionGuardHeartbeatCount = 0;
                MissingSessionSinceClockMs = -1L;
                NextMissingSessionAttemptClockMs = 0L;
            }
        }
    }
}

/*
توضیح فایل:
این فایل فقط داخل سرور اختصاصی یونیتی اجرا می‌شود. هویت بازیکن را از سشن معتبر سرور می‌گیرد و موقعیت پذیرفته‌شده را از مخزن وضعیت سرور دریافت می‌کند. فراهم‌کننده پیش از تحویل موقعیت، تطبیق کاربر، اتصال، روم، سرور، توالی و تازگی داده را کنترل می‌کند. زوج‌ها فقط در همان سرور و همان روم ساخته می‌شوند.
*/
