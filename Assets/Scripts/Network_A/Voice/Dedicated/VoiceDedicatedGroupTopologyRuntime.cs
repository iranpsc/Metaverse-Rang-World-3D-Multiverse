using System;
using System.Collections.Generic;

namespace Network_A.Voice.Dedicated
{
    public struct VoiceDedicatedTopologyPairObservation
    {
        public VoiceDedicatedParticipantPair Pair { get; private set; }
        public VoiceDedicatedProximityState State { get; private set; }
        public VoiceDedicatedProximityDecisionType TransitionType { get; private set; }
        public string SuggestedSessionId { get; private set; }
        public float DistanceMeters { get; private set; }
        public long EffectiveAtMs { get; private set; }
        public bool IsEntered
        {
            get
            {
                return State == VoiceDedicatedProximityState.Active ||
                       State == VoiceDedicatedProximityState.ExitPending;
            }
        }

        public VoiceDedicatedTopologyPairObservation(
            VoiceDedicatedParticipantPair pair,
            VoiceDedicatedProximityState state,
            VoiceDedicatedProximityDecisionType transitionType,
            string suggestedSessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            if (string.IsNullOrWhiteSpace(pair.PairKey))
            {
                throw new ArgumentException(
                    "A Voice topology observation requires an initialized pair.",
                    "pair");
            }

            if (!Enum.IsDefined(typeof(VoiceDedicatedProximityState), state))
            {
                throw new ArgumentOutOfRangeException("state");
            }

            if (!Enum.IsDefined(
                    typeof(VoiceDedicatedProximityDecisionType),
                    transitionType))
            {
                throw new ArgumentOutOfRangeException("transitionType");
            }

            if (float.IsNaN(distanceMeters) ||
                float.IsInfinity(distanceMeters) ||
                distanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "distanceMeters",
                    "Voice topology distance must be finite and non-negative.");
            }

            if (effectiveAtMs < 0)
            {
                throw new ArgumentOutOfRangeException(
                    "effectiveAtMs",
                    "Voice topology event time must be non-negative.");
            }

            if (transitionType == VoiceDedicatedProximityDecisionType.SessionCreated &&
                state != VoiceDedicatedProximityState.Active)
            {
                throw new ArgumentException(
                    "A Voice pair enter transition must finish in Active state.",
                    "transitionType");
            }

            if (transitionType == VoiceDedicatedProximityDecisionType.SessionClosed &&
                state != VoiceDedicatedProximityState.Outside)
            {
                throw new ArgumentException(
                    "A Voice pair exit transition must finish in Outside state.",
                    "transitionType");
            }

            Pair = pair;
            State = state;
            TransitionType = transitionType;
            SuggestedSessionId = suggestedSessionId ?? string.Empty;
            DistanceMeters = distanceMeters;
            EffectiveAtMs = effectiveAtMs;
        }

        public static VoiceDedicatedTopologyPairObservation FromDecision(
            VoiceDedicatedProximityDecision decision)
        {
            return new VoiceDedicatedTopologyPairObservation(
                decision.Pair,
                decision.State,
                decision.Type,
                decision.SessionId,
                decision.DistanceMeters,
                decision.EffectiveAtMs);
        }
    }

    public struct VoiceDedicatedTopologyParticipantPosition
    {
        public VoiceDedicatedGroupParticipant Participant { get; private set; }
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Z { get; private set; }
        public long EffectiveAtMs { get; private set; }

        public VoiceDedicatedTopologyParticipantPosition(
            VoiceDedicatedGroupParticipant participant,
            float x,
            float y,
            float z,
            long effectiveAtMs)
        {
            if (participant == null)
            {
                throw new ArgumentNullException("participant");
            }

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                throw new ArgumentOutOfRangeException(
                    "x",
                    "Voice participant position components must be finite.");
            }

            if (effectiveAtMs < 0)
            {
                throw new ArgumentOutOfRangeException("effectiveAtMs");
            }

            Participant = participant;
            X = x;
            Y = y;
            Z = z;
            EffectiveAtMs = effectiveAtMs;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public sealed class VoiceDedicatedGroupTopologyRuntime
    {
        public const float SessionPositionEnterDistanceMeters = 3.0f;
        public const long SessionPositionRefreshIntervalMs = 300L;
        public const long SessionMemberPairDelayMs = 1000L;
        public const float SessionPositionProgressResetDistanceMeters = 0.02f;

        private readonly Dictionary<string, PairEdgeState> edgesByPairKey =
            new Dictionary<string, PairEdgeState>(StringComparer.Ordinal);
        private readonly Dictionary<string, RuntimeSession> sessionsById =
            new Dictionary<string, RuntimeSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> sessionIdByPairKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> previousTargetByParticipantKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, RuntimeParticipantPosition> participantPositionsByKey =
            new Dictionary<string, RuntimeParticipantPosition>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingSessionMemberPair> pendingSessionMemberPairsByPairKey =
            new Dictionary<string, PendingSessionMemberPair>(StringComparer.Ordinal);
        private readonly HashSet<string> usedSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> burnedSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> blockedGroupExitPairKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly VoiceDedicatedStableGroupMergePlanner mergePlanner;
        private readonly VoiceDedicatedGroupLeaveReformationPlanner leavePlanner;
        private readonly Func<string> sessionIdFactory;

        private long nextSessionOrder;
        private long latestParticipantPositionEffectiveAtMs;

        public int ActiveSessionCount { get { return sessionsById.Count; } }
        public int ActiveGroupSessionCount
        {
            get
            {
                int count = 0;
                foreach (RuntimeSession session in sessionsById.Values)
                {
                    if (session.Members.Count > 2) count += 1;
                }

                return count;
            }
        }
        public int BurnedSessionIdCount { get { return burnedSessionIds.Count; } }

        public VoiceDedicatedGroupTopologyRuntime(
            Func<string> dedicatedSessionIdFactory = null,
            VoiceDedicatedStableGroupMergePlanner stableMergePlanner = null,
            VoiceDedicatedGroupLeaveReformationPlanner groupLeavePlanner = null)
        {
            sessionIdFactory = dedicatedSessionIdFactory;
            mergePlanner = stableMergePlanner ??
                new VoiceDedicatedStableGroupMergePlanner();
            leavePlanner = groupLeavePlanner ??
                new VoiceDedicatedGroupLeaveReformationPlanner();
        }

        public void UpdateParticipantPositions(
            IReadOnlyList<VoiceDedicatedTopologyParticipantPosition> positions,
            long effectiveAtMs)
        {
            if (positions == null) throw new ArgumentNullException("positions");
            if (effectiveAtMs < 0) throw new ArgumentOutOfRangeException("effectiveAtMs");

            participantPositionsByKey.Clear();
            latestParticipantPositionEffectiveAtMs = effectiveAtMs;

            for (int index = 0; index < positions.Count; index += 1)
            {
                VoiceDedicatedTopologyParticipantPosition position = positions[index];
                VoiceDedicatedGroupParticipant participant = position.Participant;
                if (participant == null) continue;

                participantPositionsByKey[participant.IdentityKey] =
                    new RuntimeParticipantPosition(
                        participant,
                        position.X,
                        position.Y,
                        position.Z,
                        position.EffectiveAtMs);
            }

            RefreshAllSessionPositions(effectiveAtMs, false);
        }

        public bool TryGetSessionPosition(
            string sessionId,
            out float x,
            out float y,
            out float z)
        {
            RuntimeSession session;
            if (string.IsNullOrWhiteSpace(sessionId) ||
                !sessionsById.TryGetValue(sessionId, out session) ||
                !session.HasSessionPosition)
            {
                x = 0.0f;
                y = 0.0f;
                z = 0.0f;
                return false;
            }

            x = session.SessionPositionX;
            y = session.SessionPositionY;
            z = session.SessionPositionZ;
            return true;
        }

        public IReadOnlyList<VoiceDedicatedSessionDelta> ApplyPairObservations(
            IReadOnlyList<VoiceDedicatedTopologyPairObservation> observations,
            string authorityEpochId,
            Func<long> nextSourceSequence)
        {
            if (observations == null)
            {
                throw new ArgumentNullException("observations");
            }

            ValidateEmissionContext(authorityEpochId, nextSourceSequence);

            List<VoiceDedicatedTopologyPairObservation> entered =
                new List<VoiceDedicatedTopologyPairObservation>();
            List<VoiceDedicatedTopologyPairObservation> exited =
                new List<VoiceDedicatedTopologyPairObservation>();
            List<VoiceDedicatedTopologyPairObservation> updated =
                new List<VoiceDedicatedTopologyPairObservation>();
            HashSet<string> outsidePairKeys =
                new HashSet<string>(StringComparer.Ordinal);
            long latestObservationEffectiveAtMs = 0L;

            for (int index = 0; index < observations.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation =
                    observations[index];
                latestObservationEffectiveAtMs = Math.Max(
                    latestObservationEffectiveAtMs,
                    observation.EffectiveAtMs);

                PairEdgeState edgeState;
                if (!edgesByPairKey.TryGetValue(
                        observation.Pair.PairKey,
                        out edgeState))
                {
                    edgeState = new PairEdgeState(observation.Pair);
                    edgesByPairKey.Add(observation.Pair.PairKey, edgeState);
                }

                edgeState.State = observation.State;
                edgeState.DistanceMeters = observation.DistanceMeters;
                edgeState.EffectiveAtMs = observation.EffectiveAtMs;

                if (observation.State == VoiceDedicatedProximityState.Outside)
                {
                    outsidePairKeys.Add(observation.Pair.PairKey);
                }

                if (observation.TransitionType ==
                    VoiceDedicatedProximityDecisionType.SessionCreated)
                {
                    entered.Add(observation);
                }
                else if (observation.TransitionType ==
                         VoiceDedicatedProximityDecisionType.SessionClosed)
                {
                    exited.Add(observation);
                }
                else if (observation.TransitionType ==
                         VoiceDedicatedProximityDecisionType.DistanceUpdated)
                {
                    updated.Add(observation);
                }
            }

            entered.Sort(CompareObservations);
            exited.Sort(CompareObservations);
            updated.Sort(CompareObservations);

            long topologyEffectiveAtMs = Math.Max(
                latestObservationEffectiveAtMs,
                latestParticipantPositionEffectiveAtMs);

            List<VoiceDedicatedSessionDelta> deltas =
                new List<VoiceDedicatedSessionDelta>();

            for (int index = 0; index < exited.Count; index += 1)
            {
                ApplyExitedObservation(
                    exited[index],
                    authorityEpochId,
                    nextSourceSequence,
                    deltas);
            }

            ReleaseBlockedPairsWithOutsideEvidence(outsidePairKeys);
            CancelPendingPairsWithOutsideEvidence(outsidePairKeys);

            HashSet<string> createdPairKeys =
                new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> topologyTransitionPairKeys =
                new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> recentlyLeftSessionKeys =
                new HashSet<string>(StringComparer.Ordinal);

            RefreshAllSessionPositions(topologyEffectiveAtMs, false);

            ApplySessionPositionGroupExits(
                authorityEpochId,
                nextSourceSequence,
                deltas,
                recentlyLeftSessionKeys,
                topologyEffectiveAtMs);

            ApplySessionPositionJoins(
                authorityEpochId,
                nextSourceSequence,
                deltas,
                topologyTransitionPairKeys,
                recentlyLeftSessionKeys,
                topologyEffectiveAtMs);

            ProcessPendingSessionMemberPairs(
                authorityEpochId,
                nextSourceSequence,
                deltas,
                topologyTransitionPairKeys,
                topologyEffectiveAtMs);

            for (int index = 0; index < entered.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation = entered[index];

                if (sessionIdByPairKey.ContainsKey(observation.Pair.PairKey))
                {
                    continue;
                }

                if (blockedGroupExitPairKeys.Contains(observation.Pair.PairKey))
                {
                    topologyTransitionPairKeys.Add(observation.Pair.PairKey);
                    continue;
                }

                VoiceDedicatedGroupParticipant first =
                    CreateParticipantFromPairEndpoint(observation.Pair, true);
                VoiceDedicatedGroupParticipant second =
                    CreateParticipantFromPairEndpoint(observation.Pair, false);

                if (PairRequiresSessionMemberDelay(first, second))
                {
                    BeginPendingSessionMemberPair(observation);
                    topologyTransitionPairKeys.Add(observation.Pair.PairKey);
                    continue;
                }

                string sessionId = NormalizeAndReserveSessionId(
                    observation.SuggestedSessionId);

                deltas.Add(
                    OpenPairSession(
                        observation.Pair,
                        sessionId,
                        observation.DistanceMeters,
                        observation.EffectiveAtMs,
                        authorityEpochId,
                        nextSourceSequence()));

                createdPairKeys.Add(observation.Pair.PairKey);
            }

            for (int index = 0; index < entered.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation = entered[index];
                if (createdPairKeys.Contains(observation.Pair.PairKey) ||
                    topologyTransitionPairKeys.Contains(observation.Pair.PairKey))
                {
                    continue;
                }

                AddDistanceDeltaIfMapped(
                    observation,
                    authorityEpochId,
                    nextSourceSequence,
                    deltas);
            }

            for (int index = 0; index < updated.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation = updated[index];
                if (createdPairKeys.Contains(observation.Pair.PairKey) ||
                    topologyTransitionPairKeys.Contains(observation.Pair.PairKey))
                {
                    continue;
                }

                AddDistanceDeltaIfMapped(
                    observation,
                    authorityEpochId,
                    nextSourceSequence,
                    deltas);
            }

            return deltas;
        }

        public IReadOnlyList<VoiceDedicatedSessionDelta> RemoveParticipant(
            VoiceDedicatedGroupParticipant participant,
            VoiceDedicatedSessionReason reason,
            long effectiveAtMs,
            string authorityEpochId,
            Func<long> nextSourceSequence)
        {
            if (participant == null)
            {
                throw new ArgumentNullException("participant");
            }

            if (reason == VoiceDedicatedSessionReason.None)
            {
                throw new ArgumentException(
                    "Removing a Voice participant requires a non-zero reason.",
                    "reason");
            }

            if (effectiveAtMs < 0)
            {
                throw new ArgumentOutOfRangeException("effectiveAtMs");
            }

            ValidateEmissionContext(authorityEpochId, nextSourceSequence);

            List<RuntimeSession> affectedSessions =
                new List<RuntimeSession>();

            foreach (RuntimeSession session in sessionsById.Values)
            {
                if (session.Contains(participant))
                {
                    affectedSessions.Add(session);
                }
            }

            affectedSessions.Sort(CompareRuntimeSessions);

            List<VoiceDedicatedSessionDelta> deltas =
                new List<VoiceDedicatedSessionDelta>();

            for (int index = 0; index < affectedSessions.Count; index += 1)
            {
                RuntimeSession session = affectedSessions[index];
                RuntimeMember peer = session.FindFirstPeer(participant);
                if (peer == null) continue;

                VoiceDedicatedParticipantPair anchorPair =
                    CreatePair(participant, peer.Participant);
                float distanceMeters = ResolvePairDistance(
                    anchorPair,
                    session.LastDistanceMeters);

                deltas.Add(
                    VoiceDedicatedSessionDelta.CreateMemberLeft(
                        anchorPair,
                        session.SessionId,
                        participant.UserId,
                        distanceMeters,
                        reason,
                        effectiveAtMs,
                        authorityEpochId,
                        nextSourceSequence()));

                if (session.Members.Count == 2)
                {
                    RemoveAndBurnSession(session);
                }
                else
                {
                    RemoveMemberFromSession(session, participant);
                }
            }

            RemoveParticipantEdges(participant);
            participantPositionsByKey.Remove(participant.IdentityKey);
            previousTargetByParticipantKey.Remove(participant.IdentityKey);
            return deltas;
        }

        public IReadOnlyList<VoiceDedicatedSessionDelta> CloseAll(
            VoiceDedicatedSessionReason reason,
            long effectiveAtMs,
            string authorityEpochId,
            Func<long> nextSourceSequence)
        {
            if (reason == VoiceDedicatedSessionReason.None)
            {
                throw new ArgumentException(
                    "Closing Voice topology requires a non-zero reason.",
                    "reason");
            }

            ValidateEmissionContext(authorityEpochId, nextSourceSequence);

            List<RuntimeSession> sessions = CreateOrderedRuntimeSessions();
            List<VoiceDedicatedSessionDelta> deltas =
                new List<VoiceDedicatedSessionDelta>(sessions.Count);

            for (int index = 0; index < sessions.Count; index += 1)
            {
                RuntimeSession session = sessions[index];
                deltas.Add(
                    VoiceDedicatedSessionDelta.CreateSessionClosed(
                        session.AnchorPair,
                        session.SessionId,
                        session.LastDistanceMeters,
                        reason,
                        effectiveAtMs,
                        authorityEpochId,
                        nextSourceSequence()));

                burnedSessionIds.Add(session.SessionId);
            }

            sessionsById.Clear();
            sessionIdByPairKey.Clear();
            edgesByPairKey.Clear();
            previousTargetByParticipantKey.Clear();
            participantPositionsByKey.Clear();
            pendingSessionMemberPairsByPairKey.Clear();
            latestParticipantPositionEffectiveAtMs = 0L;
            blockedGroupExitPairKeys.Clear();
            return deltas;
        }

        public void ResetState()
        {
            foreach (string sessionId in sessionsById.Keys)
            {
                burnedSessionIds.Add(sessionId);
            }

            sessionsById.Clear();
            sessionIdByPairKey.Clear();
            edgesByPairKey.Clear();
            previousTargetByParticipantKey.Clear();
            participantPositionsByKey.Clear();
            pendingSessionMemberPairsByPairKey.Clear();
            latestParticipantPositionEffectiveAtMs = 0L;
            blockedGroupExitPairKeys.Clear();
        }

        public bool TryGetSessionIdForPair(
            VoiceDedicatedParticipantPair pair,
            out string sessionId)
        {
            if (string.IsNullOrWhiteSpace(pair.PairKey))
            {
                sessionId = string.Empty;
                return false;
            }

            return sessionIdByPairKey.TryGetValue(pair.PairKey, out sessionId);
        }

        public bool ForgetUnassignedPair(string pairKey)
        {
            if (string.IsNullOrWhiteSpace(pairKey) ||
                sessionIdByPairKey.ContainsKey(pairKey))
            {
                return false;
            }

            blockedGroupExitPairKeys.Remove(pairKey);
            return edgesByPairKey.Remove(pairKey);
        }

        public IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> CreateSessionSnapshot()
        {
            List<RuntimeSession> sessions = CreateOrderedRuntimeSessions();
            List<VoiceDedicatedGroupSessionSnapshot> snapshots =
                new List<VoiceDedicatedGroupSessionSnapshot>(sessions.Count);

            for (int index = 0; index < sessions.Count; index += 1)
            {
                snapshots.Add(sessions[index].CreateSnapshot());
            }

            return snapshots;
        }

        public IReadOnlyList<VoiceDedicatedGroupParticipant> CreateParticipantSnapshot()
        {
            Dictionary<string, VoiceDedicatedGroupParticipant> participantsByKey =
                new Dictionary<string, VoiceDedicatedGroupParticipant>(
                    StringComparer.Ordinal);

            foreach (RuntimeSession session in sessionsById.Values)
            {
                for (int index = 0; index < session.Members.Count; index += 1)
                {
                    VoiceDedicatedGroupParticipant participant =
                        session.Members[index].Participant;
                    participantsByKey[participant.IdentityKey] = participant;
                }
            }

            List<VoiceDedicatedGroupParticipant> participants =
                new List<VoiceDedicatedGroupParticipant>(participantsByKey.Values);
            participants.Sort(CompareParticipants);
            return participants;
        }

        private void ApplyExitedObservation(
            VoiceDedicatedTopologyPairObservation observation,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas)
        {
            string sessionId;
            if (!sessionIdByPairKey.TryGetValue(
                    observation.Pair.PairKey,
                    out sessionId))
            {
                return;
            }

            RuntimeSession session;
            if (!sessionsById.TryGetValue(sessionId, out session))
            {
                throw new InvalidOperationException(
                    "Voice topology pair index references a missing session.");
            }

            VoiceDedicatedGroupParticipant leavingMember;
            if (session.Members.Count == 2)
            {
                leavingMember = FindPairParticipant(
                    session,
                    observation.Pair.SecondUserId,
                    observation.Pair.SecondConnectionId);
            }
            else
            {
                return;
            }

            VoiceDedicatedGroupLeaveReformationPlan plan;
            if (!leavePlanner.TryCreatePlan(
                    session.CreateSnapshot(),
                    leavingMember,
                    observation.Pair,
                    observation.DistanceMeters,
                    CreatePairGraph(),
                    out plan))
            {
                return;
            }

            deltas.Add(
                VoiceDedicatedSessionDelta.CreateMemberLeft(
                    plan.LeaveAnchorPair,
                    plan.StableSessionId,
                    plan.LeavingMember.UserId,
                    plan.LeaveDistanceMeters,
                    VoiceDedicatedSessionReason.ProximityExit,
                    observation.EffectiveAtMs,
                    authorityEpochId,
                    nextSourceSequence()));

            if (plan.ClosesStableSession)
            {
                RemoveAndBurnSession(session);
                return;
            }

            RemoveMemberFromSession(session, plan.LeavingMember);

            for (int index = 0; index < plan.PairReformations.Count; index += 1)
            {
                VoiceDedicatedPairReformationCandidate candidate =
                    plan.PairReformations[index];

                if (sessionIdByPairKey.ContainsKey(candidate.Pair.PairKey))
                {
                    throw new InvalidOperationException(
                        "A reformed Voice pair is already indexed by an active session.");
                }

                string reformedSessionId = CreateUniqueSessionId();
                CreatePairSession(
                    candidate.Pair,
                    reformedSessionId,
                    candidate.DistanceMeters,
                    observation.EffectiveAtMs);

                deltas.Add(
                    CreatePairSessionDelta(
                        candidate.Pair,
                        reformedSessionId,
                        candidate.DistanceMeters,
                        observation.EffectiveAtMs,
                        authorityEpochId,
                        nextSourceSequence()));
            }
        }

        private void ApplySessionPositionGroupExits(
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> recentlyLeftSessionKeys,
            long effectiveAtMs)
        {
            List<RuntimeSession> sessions = CreateOrderedRuntimeSessions();

            for (int sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex += 1)
            {
                RuntimeSession session = sessions[sessionIndex];

                while (session.Members.Count > 2)
                {
                    RefreshSessionPosition(session, effectiveAtMs, false);
                    if (!session.HasSessionPosition) break;

                    RuntimeMember leavingMember = null;
                    float leavingDistanceMeters = SessionPositionEnterDistanceMeters;

                    for (int memberIndex = 0; memberIndex < session.Members.Count; memberIndex += 1)
                    {
                        RuntimeMember member = session.Members[memberIndex];
                        RuntimeParticipantPosition position;
                        if (!participantPositionsByKey.TryGetValue(
                                member.Participant.IdentityKey,
                                out position))
                        {
                            continue;
                        }

                        float distanceMeters = DistanceToSessionPosition(position, session);
                        if (distanceMeters <= SessionPositionEnterDistanceMeters) continue;

                        if (leavingMember == null ||
                            distanceMeters > leavingDistanceMeters ||
                            (Math.Abs(distanceMeters - leavingDistanceMeters) < 0.0001f &&
                             string.CompareOrdinal(
                                 member.Participant.IdentityKey,
                                 leavingMember.Participant.IdentityKey) < 0))
                        {
                            leavingMember = member;
                            leavingDistanceMeters = distanceMeters;
                        }
                    }

                    if (leavingMember == null) break;

                    if (!LeaveGroupMemberFromSession(
                            session,
                            leavingMember.Participant,
                            leavingDistanceMeters,
                            effectiveAtMs,
                            authorityEpochId,
                            nextSourceSequence,
                            deltas,
                            recentlyLeftSessionKeys))
                    {
                        break;
                    }
                }
            }
        }

        private void ApplySessionPositionJoins(
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> topologyTransitionPairKeys,
            HashSet<string> recentlyLeftSessionKeys,
            long effectiveAtMs)
        {
            List<RuntimeParticipantPosition> candidates =
                new List<RuntimeParticipantPosition>(participantPositionsByKey.Values);
            candidates.Sort(CompareRuntimeParticipantPositions);

            for (int index = 0; index < candidates.Count; index += 1)
            {
                RuntimeParticipantPosition candidatePosition = candidates[index];
                VoiceDedicatedGroupParticipant candidate = candidatePosition.Participant;

                RuntimeSession target;
                float distanceMeters;
                if (!TryResolveNearestSessionByPosition(
                        candidatePosition,
                        recentlyLeftSessionKeys,
                        out target,
                        out distanceMeters))
                {
                    continue;
                }

                long joinAtMs = Math.Max(
                    effectiveAtMs,
                    candidatePosition.EffectiveAtMs);

                if (!CloseConflictingPairSessionsBeforeJoin(
                        target,
                        candidate,
                        joinAtMs,
                        authorityEpochId,
                        nextSourceSequence,
                        deltas,
                        topologyTransitionPairKeys))
                {
                    continue;
                }

                JoinParticipantToSession(
                    target,
                    candidate,
                    distanceMeters,
                    joinAtMs,
                    authorityEpochId,
                    nextSourceSequence,
                    deltas,
                    topologyTransitionPairKeys);
            }
        }

        private bool TryResolveNearestSessionByPosition(
            RuntimeParticipantPosition candidatePosition,
            HashSet<string> recentlyLeftSessionKeys,
            out RuntimeSession selectedSession,
            out float selectedDistanceMeters)
        {
            selectedSession = null;
            selectedDistanceMeters = float.MaxValue;

            List<RuntimeSession> sessions = CreateOrderedRuntimeSessions();
            for (int index = 0; index < sessions.Count; index += 1)
            {
                RuntimeSession session = sessions[index];
                VoiceDedicatedGroupParticipant candidate = candidatePosition.Participant;

                if (!session.HasSessionPosition ||
                    session.Contains(candidate) ||
                    !string.Equals(
                        session.AnchorPair.ServerId,
                        candidate.ServerId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        session.AnchorPair.RoomId,
                        candidate.RoomId,
                        StringComparison.Ordinal) ||
                    recentlyLeftSessionKeys.Contains(
                        BuildParticipantSessionKey(candidate, session.SessionId)))
                {
                    continue;
                }

                float distanceMeters = DistanceToSessionPosition(
                    candidatePosition,
                    session);
                if (distanceMeters > SessionPositionEnterDistanceMeters) continue;

                bool betterDistance =
                    distanceMeters < selectedDistanceMeters - 0.0001f;
                bool equalDistance =
                    Math.Abs(distanceMeters - selectedDistanceMeters) < 0.0001f;
                bool betterSessionId =
                    selectedSession == null ||
                    string.CompareOrdinal(
                        session.SessionId,
                        selectedSession.SessionId) < 0;

                if (selectedSession == null ||
                    betterDistance ||
                    (equalDistance && betterSessionId))
                {
                    selectedSession = session;
                    selectedDistanceMeters = distanceMeters;
                }
            }

            return selectedSession != null;
        }

        private bool PairRequiresSessionMemberDelay(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            return IsParticipantInSessionWithoutPeer(first, second) ||
                   IsParticipantInSessionWithoutPeer(second, first);
        }

        private bool IsParticipantInSessionWithoutPeer(
            VoiceDedicatedGroupParticipant member,
            VoiceDedicatedGroupParticipant peer)
        {
            foreach (RuntimeSession session in sessionsById.Values)
            {
                if (session.Contains(member) && !session.Contains(peer))
                {
                    return true;
                }
            }

            return false;
        }

        private void BeginPendingSessionMemberPair(
            VoiceDedicatedTopologyPairObservation observation)
        {
            PendingSessionMemberPair pending;
            if (!pendingSessionMemberPairsByPairKey.TryGetValue(
                    observation.Pair.PairKey,
                    out pending))
            {
                pending = new PendingSessionMemberPair(
                    observation.Pair,
                    observation.SuggestedSessionId,
                    observation.EffectiveAtMs);
                pendingSessionMemberPairsByPairKey.Add(
                    observation.Pair.PairKey,
                    pending);
            }

            float sessionDistanceMeters;
            if (TryResolveNearestRelevantSessionDistance(
                    observation.Pair,
                    out sessionDistanceMeters))
            {
                pending.ObserveSessionDistance(
                    sessionDistanceMeters,
                    observation.EffectiveAtMs);
            }
        }

        private void ProcessPendingSessionMemberPairs(
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> topologyTransitionPairKeys,
            long effectiveAtMs)
        {
            if (pendingSessionMemberPairsByPairKey.Count == 0) return;

            List<string> pairKeys =
                new List<string>(pendingSessionMemberPairsByPairKey.Keys);
            pairKeys.Sort(StringComparer.Ordinal);

            for (int index = 0; index < pairKeys.Count; index += 1)
            {
                string pairKey = pairKeys[index];
                PendingSessionMemberPair pending;
                if (!pendingSessionMemberPairsByPairKey.TryGetValue(
                        pairKey,
                        out pending))
                {
                    continue;
                }

                if (sessionIdByPairKey.ContainsKey(pairKey))
                {
                    pendingSessionMemberPairsByPairKey.Remove(pairKey);
                    continue;
                }

                PairEdgeState edge;
                if (!edgesByPairKey.TryGetValue(pairKey, out edge) ||
                    edge.State == VoiceDedicatedProximityState.Outside)
                {
                    pendingSessionMemberPairsByPairKey.Remove(pairKey);
                    continue;
                }

                if (edge.State != VoiceDedicatedProximityState.Active)
                {
                    topologyTransitionPairKeys.Add(pairKey);
                    continue;
                }

                VoiceDedicatedGroupParticipant first =
                    CreateParticipantFromPairEndpoint(pending.Pair, true);
                VoiceDedicatedGroupParticipant second =
                    CreateParticipantFromPairEndpoint(pending.Pair, false);

                if (!PairRequiresSessionMemberDelay(first, second))
                {
                    OpenPendingPairSession(
                        pending,
                        edge,
                        authorityEpochId,
                        nextSourceSequence,
                        deltas,
                        topologyTransitionPairKeys,
                        effectiveAtMs);
                    continue;
                }

                float sessionDistanceMeters;
                if (!TryResolveNearestRelevantSessionDistance(
                        pending.Pair,
                        out sessionDistanceMeters))
                {
                    continue;
                }

                if (sessionDistanceMeters <= SessionPositionEnterDistanceMeters)
                {
                    pendingSessionMemberPairsByPairKey.Remove(pairKey);
                    topologyTransitionPairKeys.Add(pairKey);
                    continue;
                }

                pending.ObserveSessionDistance(
                    sessionDistanceMeters,
                    effectiveAtMs);

                if (effectiveAtMs - pending.LastProgressAtMs <
                    SessionMemberPairDelayMs)
                {
                    topologyTransitionPairKeys.Add(pairKey);
                    continue;
                }

                OpenPendingPairSession(
                    pending,
                    edge,
                    authorityEpochId,
                    nextSourceSequence,
                    deltas,
                    topologyTransitionPairKeys,
                    effectiveAtMs);
            }
        }

        private void OpenPendingPairSession(
            PendingSessionMemberPair pending,
            PairEdgeState edge,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> topologyTransitionPairKeys,
            long effectiveAtMs)
        {
            string sessionId = NormalizeAndReserveSessionId(
                pending.SuggestedSessionId);
            long openAtMs = Math.Max(effectiveAtMs, edge.EffectiveAtMs);

            deltas.Add(
                OpenPairSession(
                    pending.Pair,
                    sessionId,
                    edge.DistanceMeters,
                    openAtMs,
                    authorityEpochId,
                    nextSourceSequence()));

            pendingSessionMemberPairsByPairKey.Remove(pending.Pair.PairKey);
            topologyTransitionPairKeys.Add(pending.Pair.PairKey);
        }

        private bool TryResolveNearestRelevantSessionDistance(
            VoiceDedicatedParticipantPair pair,
            out float selectedDistanceMeters)
        {
            selectedDistanceMeters = float.MaxValue;
            bool found = false;

            VoiceDedicatedGroupParticipant first =
                CreateParticipantFromPairEndpoint(pair, true);
            VoiceDedicatedGroupParticipant second =
                CreateParticipantFromPairEndpoint(pair, false);

            found |= TryResolveNearestSessionDistanceForMemberAndPeer(
                first,
                second,
                ref selectedDistanceMeters);
            found |= TryResolveNearestSessionDistanceForMemberAndPeer(
                second,
                first,
                ref selectedDistanceMeters);

            return found;
        }

        private bool TryResolveNearestSessionDistanceForMemberAndPeer(
            VoiceDedicatedGroupParticipant member,
            VoiceDedicatedGroupParticipant peer,
            ref float selectedDistanceMeters)
        {
            RuntimeParticipantPosition peerPosition;
            if (!participantPositionsByKey.TryGetValue(
                    peer.IdentityKey,
                    out peerPosition))
            {
                return false;
            }

            bool found = false;
            foreach (RuntimeSession session in sessionsById.Values)
            {
                if (!session.HasSessionPosition ||
                    !session.Contains(member) ||
                    session.Contains(peer))
                {
                    continue;
                }

                float distanceMeters = DistanceToSessionPosition(
                    peerPosition,
                    session);
                if (!found || distanceMeters < selectedDistanceMeters)
                {
                    selectedDistanceMeters = distanceMeters;
                    found = true;
                }
            }

            return found;
        }

        private bool CloseConflictingPairSessionsBeforeJoin(
            RuntimeSession target,
            VoiceDedicatedGroupParticipant participant,
            long effectiveAtMs,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> topologyTransitionPairKeys)
        {
            List<RuntimeSession> conflicts = new List<RuntimeSession>();

            for (int index = 0; index < target.Members.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant peer =
                    target.Members[index].Participant;
                VoiceDedicatedParticipantPair pair = CreatePair(participant, peer);
                string sessionId;

                if (!sessionIdByPairKey.TryGetValue(pair.PairKey, out sessionId) ||
                    string.Equals(
                        sessionId,
                        target.SessionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RuntimeSession conflict;
                if (!sessionsById.TryGetValue(sessionId, out conflict))
                {
                    throw new InvalidOperationException(
                        "Voice SessionPosition join references a missing conflicting Session.");
                }

                if (conflict.Members.Count != 2)
                {
                    return false;
                }

                if (!conflicts.Contains(conflict)) conflicts.Add(conflict);
                topologyTransitionPairKeys.Add(pair.PairKey);
            }

            conflicts.Sort(CompareRuntimeSessions);

            for (int index = 0; index < conflicts.Count; index += 1)
            {
                RuntimeSession conflict = conflicts[index];
                deltas.Add(
                    VoiceDedicatedSessionDelta.CreateSessionClosed(
                        conflict.AnchorPair,
                        conflict.SessionId,
                        conflict.LastDistanceMeters,
                        VoiceDedicatedSessionReason.SessionClosed,
                        effectiveAtMs,
                        authorityEpochId,
                        nextSourceSequence()));
                RemoveAndBurnSession(conflict);
            }

            return true;
        }

        private void CancelPendingPairsWithOutsideEvidence(
            HashSet<string> outsidePairKeys)
        {
            if (outsidePairKeys == null || outsidePairKeys.Count == 0) return;

            foreach (string pairKey in outsidePairKeys)
            {
                pendingSessionMemberPairsByPairKey.Remove(pairKey);
            }
        }

        private VoiceDedicatedSessionDelta OpenPairSession(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs,
            string authorityEpochId,
            long sourceSequence)
        {
            CreatePairSession(
                pair,
                sessionId,
                distanceMeters,
                effectiveAtMs);

            return CreatePairSessionDelta(
                pair,
                sessionId,
                distanceMeters,
                effectiveAtMs,
                authorityEpochId,
                sourceSequence);
        }

        private void JoinParticipantToSession(
            RuntimeSession session,
            VoiceDedicatedGroupParticipant participant,
            float distanceMeters,
            long effectiveAtMs,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> topologyTransitionPairKeys)
        {
            deltas.Add(
                VoiceDedicatedSessionDelta.CreateMemberJoined(
                    session.AnchorPair,
                    session.SessionId,
                    participant.UserId,
                    participant.ConnectionId,
                    distanceMeters,
                    effectiveAtMs,
                    authorityEpochId,
                    nextSourceSequence()));

            MarkParticipantSessionPairKeys(
                participant,
                session,
                topologyTransitionPairKeys);

            AddMemberToSession(
                session,
                participant,
                distanceMeters,
                effectiveAtMs);

            previousTargetByParticipantKey[participant.IdentityKey] =
                session.SessionId;
        }

        private bool LeaveGroupMemberFromSession(
            RuntimeSession session,
            VoiceDedicatedGroupParticipant participant,
            float distanceMeters,
            long effectiveAtMs,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> recentlyLeftSessionKeys)
        {
            if (session == null ||
                participant == null ||
                session.Members.Count <= 2 ||
                !session.Contains(participant))
            {
                return false;
            }

            RuntimeMember peer = session.FindFirstPeer(participant);
            if (peer == null) return false;

            VoiceDedicatedParticipantPair leavePair = CreatePair(
                participant,
                peer.Participant);

            deltas.Add(
                VoiceDedicatedSessionDelta.CreateMemberLeft(
                    leavePair,
                    session.SessionId,
                    participant.UserId,
                    distanceMeters,
                    VoiceDedicatedSessionReason.ProximityExit,
                    effectiveAtMs,
                    authorityEpochId,
                    nextSourceSequence()));

            recentlyLeftSessionKeys.Add(
                BuildParticipantSessionKey(
                    participant,
                    session.SessionId));

            session.LastEffectiveAtMs = effectiveAtMs;
            RemoveMemberFromSession(session, participant);
            previousTargetByParticipantKey.Remove(participant.IdentityKey);
            return true;
        }

        private void MarkParticipantSessionPairKeys(
            VoiceDedicatedGroupParticipant participant,
            RuntimeSession session,
            HashSet<string> pairKeys)
        {
            for (int index = 0; index < session.Members.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant peer =
                    session.Members[index].Participant;
                if (peer.HasSameIdentity(participant)) continue;
                string pairKey = CreatePair(participant, peer).PairKey;
                pairKeys.Add(pairKey);
                pendingSessionMemberPairsByPairKey.Remove(pairKey);
            }
        }

        private void RefreshAllSessionPositions(long effectiveAtMs, bool force)
        {
            foreach (RuntimeSession session in sessionsById.Values)
            {
                RefreshSessionPosition(session, effectiveAtMs, force);
            }
        }

        private void RefreshSessionPosition(
            RuntimeSession session,
            long effectiveAtMs,
            bool force)
        {
            if (session == null || session.Members.Count < 2) return;

            if (!force &&
                session.HasSessionPosition &&
                effectiveAtMs - session.LastSessionPositionUpdateAtMs <
                    SessionPositionRefreshIntervalMs)
            {
                return;
            }

            float sumX = 0.0f;
            float sumY = 0.0f;
            float sumZ = 0.0f;

            for (int index = 0; index < session.Members.Count; index += 1)
            {
                RuntimeParticipantPosition position;
                if (!participantPositionsByKey.TryGetValue(
                        session.Members[index].Participant.IdentityKey,
                        out position))
                {
                    return;
                }

                sumX += position.X;
                sumY += position.Y;
                sumZ += position.Z;
            }

            float inverseCount = 1.0f / session.Members.Count;
            session.SetSessionPosition(
                sumX * inverseCount,
                sumY * inverseCount,
                sumZ * inverseCount,
                effectiveAtMs);
        }

        private static float DistanceToSessionPosition(
            RuntimeParticipantPosition participantPosition,
            RuntimeSession session)
        {
            float deltaX = participantPosition.X - session.SessionPositionX;
            float deltaY = participantPosition.Y - session.SessionPositionY;
            float deltaZ = participantPosition.Z - session.SessionPositionZ;
            return (float)Math.Sqrt(
                deltaX * deltaX +
                deltaY * deltaY +
                deltaZ * deltaZ);
        }

        private static string BuildParticipantSessionKey(
            VoiceDedicatedGroupParticipant participant,
            string sessionId)
        {
            return participant.IdentityKey + "|" + sessionId;
        }

        private static int CompareRuntimeParticipantPositions(
            RuntimeParticipantPosition first,
            RuntimeParticipantPosition second)
        {
            if (ReferenceEquals(first, second)) return 0;
            if (first == null) return -1;
            if (second == null) return 1;
            return CompareParticipants(first.Participant, second.Participant);
        }

        private bool TryApplyDirectStableJoinForEnteredPair(
            VoiceDedicatedParticipantPair pair,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas,
            HashSet<string> topologyTransitionPairKeys)
        {
            VoiceDedicatedGroupParticipant first =
                CreateParticipantFromPairEndpoint(pair, true);
            VoiceDedicatedGroupParticipant second =
                CreateParticipantFromPairEndpoint(pair, false);

            VoiceDedicatedStableGroupMergePlan plan;
            if (TryCreateDirectStableJoinPlan(first, second, out plan) ||
                TryCreateDirectStableJoinPlan(second, first, out plan))
            {
                MarkDirectJoinPairKeys(
                    plan,
                    topologyTransitionPairKeys);

                ApplyStableMerge(
                    plan,
                    authorityEpochId,
                    nextSourceSequence,
                    deltas);
                return true;
            }

            return false;
        }

        private bool TryCreateDirectStableJoinPlan(
            VoiceDedicatedGroupParticipant candidate,
            VoiceDedicatedGroupParticipant connectedPeer,
            out VoiceDedicatedStableGroupMergePlan selectedPlan)
        {
            if (IsParticipantInAnySession(candidate))
            {
                selectedPlan = null;
                return false;
            }

            List<RuntimeSession> orderedSessions = CreateOrderedRuntimeSessions();
            orderedSessions.Sort(CompareStableMergeTargets);
            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> snapshots =
                CreateSnapshots(orderedSessions);
            VoiceDedicatedGroupPairGraph pairGraph = CreatePairGraph();

            for (int index = 0; index < orderedSessions.Count; index += 1)
            {
                RuntimeSession target = orderedSessions[index];
                if (!target.Contains(connectedPeer) || target.Contains(candidate))
                {
                    continue;
                }

                string previousTarget;
                if (!previousTargetByParticipantKey.TryGetValue(
                        candidate.IdentityKey,
                        out previousTarget))
                {
                    previousTarget = target.SessionId;
                }

                VoiceDedicatedStableGroupMergePlan plan;
                if (!mergePlanner.TryCreatePlan(
                        candidate,
                        snapshots,
                        pairGraph,
                        previousTarget,
                        out plan) ||
                    !string.Equals(
                        plan.TargetSessionId,
                        target.SessionId,
                        StringComparison.OrdinalIgnoreCase) ||
                    plan.SecondarySessionIdsToBurn.Count != 0)
                {
                    continue;
                }

                selectedPlan = plan;
                return true;
            }

            selectedPlan = null;
            return false;
        }

        private bool IsParticipantInAnySession(
            VoiceDedicatedGroupParticipant participant)
        {
            foreach (RuntimeSession session in sessionsById.Values)
            {
                if (session.Contains(participant)) return true;
            }

            return false;
        }

        private static VoiceDedicatedGroupParticipant
            CreateParticipantFromPairEndpoint(
                VoiceDedicatedParticipantPair pair,
                bool first)
        {
            return new VoiceDedicatedGroupParticipant(
                pair.ServerId,
                pair.RoomId,
                first ? pair.FirstUserId : pair.SecondUserId,
                first ? pair.FirstConnectionId : pair.SecondConnectionId);
        }

        private void MarkDirectJoinPairKeys(
            VoiceDedicatedStableGroupMergePlan plan,
            HashSet<string> topologyTransitionPairKeys)
        {
            RuntimeSession target;
            if (!sessionsById.TryGetValue(plan.TargetSessionId, out target))
            {
                return;
            }

            for (int index = 0; index < target.Members.Count; index += 1)
            {
                VoiceDedicatedParticipantPair pair = CreatePair(
                    plan.JoiningMember,
                    target.Members[index].Participant);
                topologyTransitionPairKeys.Add(pair.PairKey);
            }
        }

        private void ApplyAllEligibleStableMerges(
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas)
        {
            int maximumMergeCount = Math.Max(1, sessionsById.Count * 2);

            for (int mergeIndex = 0;
                 mergeIndex < maximumMergeCount;
                 mergeIndex += 1)
            {
                VoiceDedicatedStableGroupMergePlan plan;
                if (!TryResolveNextStableMerge(out plan)) return;

                ApplyStableMerge(
                    plan,
                    authorityEpochId,
                    nextSourceSequence,
                    deltas);
            }

            throw new InvalidOperationException(
                "Voice topology exceeded its bounded stable merge count.");
        }

        private bool TryResolveNextStableMerge(
            out VoiceDedicatedStableGroupMergePlan selectedPlan)
        {
            List<RuntimeSession> orderedSessions = CreateOrderedRuntimeSessions();
            orderedSessions.Sort(CompareStableMergeTargets);
            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> snapshots =
                CreateSnapshots(orderedSessions);
            VoiceDedicatedGroupPairGraph pairGraph = CreatePairGraph();
            List<VoiceDedicatedGroupParticipant> participants =
                new List<VoiceDedicatedGroupParticipant>(CreateParticipantSnapshot());

            for (int sessionIndex = 0;
                 sessionIndex < orderedSessions.Count;
                 sessionIndex += 1)
            {
                RuntimeSession target = orderedSessions[sessionIndex];

                for (int participantIndex = 0;
                     participantIndex < participants.Count;
                     participantIndex += 1)
                {
                    VoiceDedicatedGroupParticipant candidate =
                        participants[participantIndex];
                    if (target.Contains(candidate)) continue;

                    string previousTarget;
                    if (!previousTargetByParticipantKey.TryGetValue(
                            candidate.IdentityKey,
                            out previousTarget))
                    {
                        previousTarget = target.SessionId;
                    }

                    VoiceDedicatedStableGroupMergePlan plan;
                    if (!mergePlanner.TryCreatePlan(
                            candidate,
                            snapshots,
                            pairGraph,
                            previousTarget,
                            out plan) ||
                        !string.Equals(
                            plan.TargetSessionId,
                            target.SessionId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !SecondarySessionsAreNotOlder(plan, target))
                    {
                        continue;
                    }

                    selectedPlan = plan;
                    return true;
                }
            }

            selectedPlan = null;
            return false;
        }

        private void ApplyStableMerge(
            VoiceDedicatedStableGroupMergePlan plan,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas)
        {
            RuntimeSession target;
            if (!sessionsById.TryGetValue(plan.TargetSessionId, out target))
            {
                throw new InvalidOperationException(
                    "Voice stable merge target disappeared before apply.");
            }

            for (int index = 0;
                 index < plan.SecondarySessionIdsToBurn.Count;
                 index += 1)
            {
                string secondarySessionId = plan.SecondarySessionIdsToBurn[index];
                RuntimeSession secondary;
                if (!sessionsById.TryGetValue(secondarySessionId, out secondary))
                {
                    throw new InvalidOperationException(
                        "Voice stable merge secondary session disappeared before burn.");
                }

                deltas.Add(
                    VoiceDedicatedSessionDelta.CreateSessionClosed(
                        secondary.AnchorPair,
                        secondary.SessionId,
                        secondary.LastDistanceMeters,
                        VoiceDedicatedSessionReason.SessionClosed,
                        secondary.LastEffectiveAtMs,
                        authorityEpochId,
                        nextSourceSequence()));

                RemoveAndBurnSession(secondary);
            }

            deltas.Add(
                VoiceDedicatedSessionDelta.CreateMemberJoined(
                    target.AnchorPair,
                    target.SessionId,
                    plan.JoiningMember.UserId,
                    plan.JoiningMember.ConnectionId,
                    plan.SessionScoreMeters,
                    ResolveLatestParticipantEventTime(plan.JoiningMember),
                    authorityEpochId,
                    nextSourceSequence()));

            AddMemberToSession(
                target,
                plan.JoiningMember,
                plan.SessionScoreMeters,
                ResolveLatestParticipantEventTime(plan.JoiningMember));

            previousTargetByParticipantKey[plan.JoiningMember.IdentityKey] =
                target.SessionId;
        }

        private bool SecondarySessionsAreNotOlder(
            VoiceDedicatedStableGroupMergePlan plan,
            RuntimeSession target)
        {
            for (int index = 0;
                 index < plan.SecondarySessionIdsToBurn.Count;
                 index += 1)
            {
                RuntimeSession secondary;
                if (!sessionsById.TryGetValue(
                        plan.SecondarySessionIdsToBurn[index],
                        out secondary) ||
                    secondary.CreatedOrder < target.CreatedOrder)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryResolveGraphBackedGroupExit(
            RuntimeSession session,
            out VoiceDedicatedGroupParticipant leavingMember,
            out bool closesDisconnectedGroup)
        {
            leavingMember = null;
            closesDisconnectedGroup = false;

            List<List<RuntimeMember>> components =
                CreateEnteredMembershipComponents(session);

            if (components.Count <= 1)
            {
                return false;
            }

            List<List<RuntimeMember>> stableComponents =
                new List<List<RuntimeMember>>();
            List<RuntimeMember> isolatedMembers =
                new List<RuntimeMember>();

            for (int index = 0; index < components.Count; index += 1)
            {
                if (components[index].Count >= 2)
                {
                    stableComponents.Add(components[index]);
                }
                else if (components[index].Count == 1)
                {
                    isolatedMembers.Add(components[index][0]);
                }
            }

            if (stableComponents.Count == 0)
            {
                closesDisconnectedGroup = true;
                return true;
            }

            if (stableComponents.Count == 1 && isolatedMembers.Count == 1)
            {
                leavingMember = isolatedMembers[0].Participant;
                return true;
            }

            return false;
        }

        private List<List<RuntimeMember>> CreateEnteredMembershipComponents(
            RuntimeSession session)
        {
            List<List<RuntimeMember>> components =
                new List<List<RuntimeMember>>();
            HashSet<string> visited =
                new HashSet<string>(StringComparer.Ordinal);

            for (int index = 0; index < session.Members.Count; index += 1)
            {
                RuntimeMember start = session.Members[index];
                if (!visited.Add(start.Participant.IdentityKey)) continue;

                List<RuntimeMember> component = new List<RuntimeMember>();
                Queue<RuntimeMember> queue = new Queue<RuntimeMember>();
                queue.Enqueue(start);

                while (queue.Count > 0)
                {
                    RuntimeMember current = queue.Dequeue();
                    component.Add(current);

                    for (int peerIndex = 0;
                         peerIndex < session.Members.Count;
                         peerIndex += 1)
                    {
                        RuntimeMember peer = session.Members[peerIndex];
                        if (visited.Contains(peer.Participant.IdentityKey)) continue;
                        if (!HasEnteredMembershipEdge(
                                current.Participant,
                                peer.Participant))
                        {
                            continue;
                        }

                        visited.Add(peer.Participant.IdentityKey);
                        queue.Enqueue(peer);
                    }
                }

                components.Add(component);
            }

            return components;
        }

        private bool HasEnteredMembershipEdge(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            VoiceDedicatedParticipantPair pair = CreatePair(first, second);
            PairEdgeState edge;
            return edgesByPairKey.TryGetValue(pair.PairKey, out edge) &&
                   edge.IsEntered;
        }

        private int CountInvalidMembershipEdges(
            RuntimeSession session,
            VoiceDedicatedGroupParticipant participant)
        {
            int invalidCount = 0;

            for (int index = 0; index < session.Members.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant peer =
                    session.Members[index].Participant;
                if (peer.HasSameIdentity(participant)) continue;

                VoiceDedicatedParticipantPair pair = CreatePair(participant, peer);
                PairEdgeState edge;
                if (!edgesByPairKey.TryGetValue(pair.PairKey, out edge) ||
                    !edge.IsEntered)
                {
                    invalidCount += 1;
                }
            }

            return invalidCount;
        }

        private void AddDistanceDeltaIfMapped(
            VoiceDedicatedTopologyPairObservation observation,
            string authorityEpochId,
            Func<long> nextSourceSequence,
            List<VoiceDedicatedSessionDelta> deltas)
        {
            string sessionId;
            if (!sessionIdByPairKey.TryGetValue(
                    observation.Pair.PairKey,
                    out sessionId))
            {
                return;
            }

            RuntimeSession session;
            if (!sessionsById.TryGetValue(sessionId, out session))
            {
                throw new InvalidOperationException(
                    "Voice distance update references a missing topology session.");
            }

            session.LastDistanceMeters = observation.DistanceMeters;
            session.LastEffectiveAtMs = observation.EffectiveAtMs;

            deltas.Add(
                VoiceDedicatedSessionDelta.CreateDistanceUpdated(
                    observation.Pair,
                    sessionId,
                    observation.DistanceMeters,
                    observation.EffectiveAtMs,
                    authorityEpochId,
                    nextSourceSequence()));
        }

        private void CreatePairSession(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            if (sessionsById.ContainsKey(sessionId) ||
                sessionIdByPairKey.ContainsKey(pair.PairKey))
            {
                throw new InvalidOperationException(
                    "Voice pair session or pair index already exists.");
            }

            nextSessionOrder += 1;
            RuntimeSession session = new RuntimeSession(
                sessionId,
                pair,
                distanceMeters,
                effectiveAtMs,
                nextSessionOrder);

            sessionsById.Add(session.SessionId, session);
            sessionIdByPairKey.Add(pair.PairKey, session.SessionId);
            RefreshSessionPosition(session, effectiveAtMs, true);
        }

        private void AddMemberToSession(
            RuntimeSession session,
            VoiceDedicatedGroupParticipant participant,
            float distanceMeters,
            long effectiveAtMs)
        {
            if (session.Contains(participant)) return;

            session.Add(participant);
            session.LastDistanceMeters = distanceMeters;
            session.LastEffectiveAtMs = effectiveAtMs;

            for (int index = 0; index < session.Members.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant peer =
                    session.Members[index].Participant;
                if (peer.HasSameIdentity(participant)) continue;

                VoiceDedicatedParticipantPair pair = CreatePair(participant, peer);
                string existingSessionId;
                if (sessionIdByPairKey.TryGetValue(pair.PairKey, out existingSessionId) &&
                    !string.Equals(
                        existingSessionId,
                        session.SessionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Voice group join found an unburned secondary pair session.");
                }

                sessionIdByPairKey[pair.PairKey] = session.SessionId;
            }

            RefreshSessionPosition(session, effectiveAtMs, true);
        }

        private void RemoveMemberFromSession(
            RuntimeSession session,
            VoiceDedicatedGroupParticipant participant)
        {
            for (int index = 0; index < session.Members.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant peer =
                    session.Members[index].Participant;
                if (peer.HasSameIdentity(participant)) continue;

                VoiceDedicatedParticipantPair pair = CreatePair(participant, peer);
                string indexedSessionId;
                if (sessionIdByPairKey.TryGetValue(pair.PairKey, out indexedSessionId) &&
                    string.Equals(
                        indexedSessionId,
                        session.SessionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    sessionIdByPairKey.Remove(pair.PairKey);
                }
            }

            session.Remove(participant);
            session.RefreshAnchorPair();
            RefreshSessionPosition(session, session.LastEffectiveAtMs, true);
        }

        private void RemoveAndBurnSession(RuntimeSession session)
        {
            RemoveAndBurnSession(session, false);
        }

        private void RemoveAndBurnSession(
            RuntimeSession session,
            bool blockFormerPairReentryUntilOutside)
        {
            for (int firstIndex = 0;
                 firstIndex < session.Members.Count;
                 firstIndex += 1)
            {
                for (int secondIndex = firstIndex + 1;
                     secondIndex < session.Members.Count;
                     secondIndex += 1)
                {
                    VoiceDedicatedParticipantPair pair = CreatePair(
                        session.Members[firstIndex].Participant,
                        session.Members[secondIndex].Participant);
                    string indexedSessionId;
                    if (sessionIdByPairKey.TryGetValue(pair.PairKey, out indexedSessionId) &&
                        string.Equals(
                            indexedSessionId,
                            session.SessionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        sessionIdByPairKey.Remove(pair.PairKey);
                    }

                    pendingSessionMemberPairsByPairKey.Remove(pair.PairKey);

                    if (blockFormerPairReentryUntilOutside)
                    {
                        blockedGroupExitPairKeys.Add(pair.PairKey);
                    }
                }
            }

            sessionsById.Remove(session.SessionId);
            burnedSessionIds.Add(session.SessionId);
        }

        private void RemoveParticipantEdges(
            VoiceDedicatedGroupParticipant participant)
        {
            List<string> removals = new List<string>();
            foreach (KeyValuePair<string, PairEdgeState> entry in edgesByPairKey)
            {
                if (PairContainsParticipant(entry.Value.Pair, participant))
                {
                    removals.Add(entry.Key);
                }
            }

            for (int index = 0; index < removals.Count; index += 1)
            {
                string pairKey = removals[index];
                edgesByPairKey.Remove(pairKey);
                pendingSessionMemberPairsByPairKey.Remove(pairKey);
                blockedGroupExitPairKeys.Remove(pairKey);
            }
        }

        private VoiceDedicatedGroupPairGraph CreatePairGraph()
        {
            List<VoiceDedicatedGroupPairEdge> edges =
                new List<VoiceDedicatedGroupPairEdge>(edgesByPairKey.Count);

            foreach (PairEdgeState state in edgesByPairKey.Values)
            {
                edges.Add(
                    new VoiceDedicatedGroupPairEdge(
                        state.Pair,
                        state.DistanceMeters,
                        state.IsEntered));
            }

            return new VoiceDedicatedGroupPairGraph(edges);
        }

        private void ReleaseBlockedPairsWithOutsideEvidence(
            HashSet<string> outsidePairKeys)
        {
            if (outsidePairKeys == null || outsidePairKeys.Count == 0) return;
            if (blockedGroupExitPairKeys.Count == 0) return;

            foreach (string pairKey in outsidePairKeys)
            {
                blockedGroupExitPairKeys.Remove(pairKey);
            }
        }

        private List<RuntimeSession> CreateOrderedRuntimeSessions()
        {
            List<RuntimeSession> sessions =
                new List<RuntimeSession>(sessionsById.Values);
            sessions.Sort(CompareRuntimeSessions);
            return sessions;
        }

        private static IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> CreateSnapshots(
            List<RuntimeSession> sessions)
        {
            List<VoiceDedicatedGroupSessionSnapshot> snapshots =
                new List<VoiceDedicatedGroupSessionSnapshot>(sessions.Count);

            for (int index = 0; index < sessions.Count; index += 1)
            {
                snapshots.Add(sessions[index].CreateSnapshot());
            }

            return snapshots;
        }

        private float ResolvePairDistance(
            VoiceDedicatedParticipantPair pair,
            float fallbackDistanceMeters)
        {
            PairEdgeState edge;
            return edgesByPairKey.TryGetValue(pair.PairKey, out edge)
                ? edge.DistanceMeters
                : fallbackDistanceMeters;
        }

        private long ResolveLatestParticipantEventTime(
            VoiceDedicatedGroupParticipant participant)
        {
            long effectiveAtMs = 0;
            foreach (PairEdgeState edge in edgesByPairKey.Values)
            {
                if (PairContainsParticipant(edge.Pair, participant))
                {
                    effectiveAtMs = Math.Max(effectiveAtMs, edge.EffectiveAtMs);
                }
            }

            return effectiveAtMs;
        }

        private string CreateUniqueSessionId()
        {
            for (int attempt = 0; attempt < 16; attempt += 1)
            {
                string candidate = sessionIdFactory == null
                    ? Guid.NewGuid().ToString("D")
                    : sessionIdFactory();

                string normalized = NormalizeSessionId(candidate);
                if (usedSessionIds.Add(normalized)) return normalized;
            }

            throw new InvalidOperationException(
                "Voice topology could not allocate a unique SessionId.");
        }

        private string NormalizeAndReserveSessionId(string sessionId)
        {
            string normalized = NormalizeSessionId(sessionId);
            if (!usedSessionIds.Add(normalized) ||
                burnedSessionIds.Contains(normalized))
            {
                throw new InvalidOperationException(
                    "Voice topology attempted to reuse a SessionId.");
            }

            return normalized;
        }

        private static string NormalizeSessionId(string sessionId)
        {
            string normalized = string.IsNullOrWhiteSpace(sessionId)
                ? string.Empty
                : sessionId.Trim().ToLowerInvariant();
            Guid parsed;

            bool validUuid = Guid.TryParseExact(normalized, "D", out parsed);
            bool validVersion =
                normalized.Length == 36 &&
                normalized[14] >= '1' &&
                normalized[14] <= '5';
            char variant = normalized.Length == 36 ? normalized[19] : '\0';
            bool validVariant =
                variant == '8' ||
                variant == '9' ||
                variant == 'a' ||
                variant == 'b';

            if (!validUuid || !validVersion || !validVariant)
            {
                throw new ArgumentException(
                    "Voice topology SessionId must be a valid UUID.",
                    "sessionId");
            }

            return normalized;
        }

        private static VoiceDedicatedSessionDelta CreatePairSessionDelta(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs,
            string authorityEpochId,
            long sourceSequence)
        {
            VoiceDedicatedProximityDecision decision =
                new VoiceDedicatedProximityDecision(
                    VoiceDedicatedProximityDecisionType.SessionCreated,
                    VoiceDedicatedProximityState.Active,
                    VoiceDedicatedProximityReason.ProximityEnter,
                    pair,
                    sessionId,
                    distanceMeters,
                    effectiveAtMs);

            return VoiceDedicatedSessionDelta.FromProximityDecision(
                decision,
                authorityEpochId,
                sourceSequence);
        }

        private static VoiceDedicatedParticipantPair CreatePair(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            return new VoiceDedicatedParticipantPair(
                first.ServerId,
                first.RoomId,
                first.UserId,
                first.ConnectionId,
                second.UserId,
                second.ConnectionId);
        }

        private static VoiceDedicatedGroupParticipant FindPairParticipant(
            RuntimeSession session,
            string userId,
            string connectionId)
        {
            RuntimeMember member = session.Find(userId, connectionId);
            if (member == null)
            {
                throw new InvalidOperationException(
                    "Voice pair participant is missing from its indexed session.");
            }

            return member.Participant;
        }

        private static bool PairContainsParticipant(
            VoiceDedicatedParticipantPair pair,
            VoiceDedicatedGroupParticipant participant)
        {
            return
                (string.Equals(
                     pair.FirstUserId,
                     participant.UserId,
                     StringComparison.Ordinal) &&
                 string.Equals(
                     pair.FirstConnectionId,
                     participant.ConnectionId,
                     StringComparison.Ordinal)) ||
                (string.Equals(
                     pair.SecondUserId,
                     participant.UserId,
                     StringComparison.Ordinal) &&
                 string.Equals(
                     pair.SecondConnectionId,
                     participant.ConnectionId,
                     StringComparison.Ordinal));
        }

        private static void ValidateEmissionContext(
            string authorityEpochId,
            Func<long> nextSourceSequence)
        {
            if (string.IsNullOrWhiteSpace(authorityEpochId))
            {
                throw new ArgumentException(
                    "Voice topology requires authorityEpochId.",
                    "authorityEpochId");
            }

            if (nextSourceSequence == null)
            {
                throw new ArgumentNullException("nextSourceSequence");
            }
        }

        private static int CompareObservations(
            VoiceDedicatedTopologyPairObservation first,
            VoiceDedicatedTopologyPairObservation second)
        {
            return string.CompareOrdinal(first.Pair.PairKey, second.Pair.PairKey);
        }

        private static int CompareRuntimeSessions(
            RuntimeSession first,
            RuntimeSession second)
        {
            int orderCompare = first.CreatedOrder.CompareTo(second.CreatedOrder);
            return orderCompare != 0
                ? orderCompare
                : string.CompareOrdinal(first.SessionId, second.SessionId);
        }

        private static int CompareStableMergeTargets(
            RuntimeSession first,
            RuntimeSession second)
        {
            int memberCountCompare = second.Members.Count.CompareTo(first.Members.Count);
            return memberCountCompare != 0
                ? memberCountCompare
                : CompareRuntimeSessions(first, second);
        }

        private static int CompareParticipants(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            return string.CompareOrdinal(first.IdentityKey, second.IdentityKey);
        }

        private sealed class PairEdgeState
        {
            public VoiceDedicatedParticipantPair Pair { get; private set; }
            public VoiceDedicatedProximityState State;
            public float DistanceMeters;
            public long EffectiveAtMs;
            public bool IsEntered
            {
                get
                {
                    return State == VoiceDedicatedProximityState.Active ||
                           State == VoiceDedicatedProximityState.ExitPending;
                }
            }

            public PairEdgeState(VoiceDedicatedParticipantPair pair)
            {
                Pair = pair;
                State = VoiceDedicatedProximityState.Outside;
            }
        }

        private sealed class PendingSessionMemberPair
        {
            public VoiceDedicatedParticipantPair Pair { get; private set; }
            public string SuggestedSessionId { get; private set; }
            public long LastProgressAtMs { get; private set; }
            public float LastSessionDistanceMeters { get; private set; }
            public bool HasSessionDistance { get; private set; }

            public PendingSessionMemberPair(
                VoiceDedicatedParticipantPair pair,
                string suggestedSessionId,
                long firstSeenAtMs)
            {
                if (string.IsNullOrWhiteSpace(suggestedSessionId))
                {
                    throw new ArgumentException(
                        "A pending Voice pair requires its original SessionId.",
                        "suggestedSessionId");
                }

                Pair = pair;
                SuggestedSessionId = suggestedSessionId.Trim();
                LastProgressAtMs = firstSeenAtMs;
                LastSessionDistanceMeters = float.MaxValue;
                HasSessionDistance = false;
            }

            public void ObserveSessionDistance(
                float distanceMeters,
                long effectiveAtMs)
            {
                if (!HasSessionDistance)
                {
                    LastSessionDistanceMeters = distanceMeters;
                    LastProgressAtMs = effectiveAtMs;
                    HasSessionDistance = true;
                    return;
                }

                if (LastSessionDistanceMeters - distanceMeters >=
                    SessionPositionProgressResetDistanceMeters)
                {
                    LastProgressAtMs = effectiveAtMs;
                }

                LastSessionDistanceMeters = distanceMeters;
            }
        }

        private sealed class RuntimeParticipantPosition
        {
            public VoiceDedicatedGroupParticipant Participant { get; private set; }
            public float X { get; private set; }
            public float Y { get; private set; }
            public float Z { get; private set; }
            public long EffectiveAtMs { get; private set; }

            public RuntimeParticipantPosition(
                VoiceDedicatedGroupParticipant participant,
                float x,
                float y,
                float z,
                long effectiveAtMs)
            {
                if (participant == null) throw new ArgumentNullException("participant");
                Participant = participant;
                X = x;
                Y = y;
                Z = z;
                EffectiveAtMs = effectiveAtMs;
            }
        }

        private sealed class RuntimeMember
        {
            public VoiceDedicatedGroupParticipant Participant { get; private set; }
            public int JoinOrder { get; private set; }

            public RuntimeMember(
                VoiceDedicatedGroupParticipant participant,
                int joinOrder)
            {
                Participant = participant;
                JoinOrder = joinOrder;
            }
        }

        private sealed class RuntimeSession
        {
            public string SessionId { get; private set; }
            public VoiceDedicatedParticipantPair AnchorPair { get; private set; }
            public List<RuntimeMember> Members { get; private set; }
            public float LastDistanceMeters;
            public long LastEffectiveAtMs;
            public bool HasSessionPosition { get; private set; }
            public float SessionPositionX { get; private set; }
            public float SessionPositionY { get; private set; }
            public float SessionPositionZ { get; private set; }
            public long LastSessionPositionUpdateAtMs { get; private set; }
            public long CreatedOrder { get; private set; }
            private int nextJoinOrder;

            public RuntimeSession(
                string sessionId,
                VoiceDedicatedParticipantPair anchorPair,
                float distanceMeters,
                long effectiveAtMs,
                long createdOrder)
            {
                SessionId = sessionId;
                AnchorPair = anchorPair;
                LastDistanceMeters = distanceMeters;
                LastEffectiveAtMs = effectiveAtMs;
                CreatedOrder = createdOrder;
                HasSessionPosition = false;
                SessionPositionX = 0.0f;
                SessionPositionY = 0.0f;
                SessionPositionZ = 0.0f;
                LastSessionPositionUpdateAtMs = 0L;
                Members = new List<RuntimeMember>
                {
                    new RuntimeMember(
                        new VoiceDedicatedGroupParticipant(
                            anchorPair.ServerId,
                            anchorPair.RoomId,
                            anchorPair.FirstUserId,
                            anchorPair.FirstConnectionId),
                        0),
                    new RuntimeMember(
                        new VoiceDedicatedGroupParticipant(
                            anchorPair.ServerId,
                            anchorPair.RoomId,
                            anchorPair.SecondUserId,
                            anchorPair.SecondConnectionId),
                        1)
                };
                nextJoinOrder = 2;
            }

            public void SetSessionPosition(
                float x,
                float y,
                float z,
                long effectiveAtMs)
            {
                SessionPositionX = x;
                SessionPositionY = y;
                SessionPositionZ = z;
                LastSessionPositionUpdateAtMs = effectiveAtMs;
                HasSessionPosition = true;
            }

            public bool Contains(VoiceDedicatedGroupParticipant participant)
            {
                return Find(participant.UserId, participant.ConnectionId) != null;
            }

            public RuntimeMember Find(string userId, string connectionId)
            {
                for (int index = 0; index < Members.Count; index += 1)
                {
                    RuntimeMember member = Members[index];
                    if (string.Equals(
                            member.Participant.UserId,
                            userId,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            member.Participant.ConnectionId,
                            connectionId,
                            StringComparison.Ordinal))
                    {
                        return member;
                    }
                }

                return null;
            }

            public RuntimeMember FindFirstPeer(
                VoiceDedicatedGroupParticipant participant)
            {
                for (int index = 0; index < Members.Count; index += 1)
                {
                    if (!Members[index].Participant.HasSameIdentity(participant))
                    {
                        return Members[index];
                    }
                }

                return null;
            }

            public void Add(VoiceDedicatedGroupParticipant participant)
            {
                if (Contains(participant)) return;
                Members.Add(new RuntimeMember(participant, nextJoinOrder));
                nextJoinOrder += 1;
            }

            public void Remove(VoiceDedicatedGroupParticipant participant)
            {
                for (int index = Members.Count - 1; index >= 0; index -= 1)
                {
                    if (Members[index].Participant.HasSameIdentity(participant))
                    {
                        Members.RemoveAt(index);
                        return;
                    }
                }

                throw new InvalidOperationException(
                    "Voice topology attempted to remove a missing session member.");
            }

            public void RefreshAnchorPair()
            {
                if (Members.Count < 2)
                {
                    throw new InvalidOperationException(
                        "An active Voice topology session cannot have fewer than two members.");
                }

                AnchorPair = CreatePair(
                    Members[0].Participant,
                    Members[1].Participant);
            }

            public VoiceDedicatedGroupSessionSnapshot CreateSnapshot()
            {
                List<VoiceDedicatedGroupParticipant> participants =
                    new List<VoiceDedicatedGroupParticipant>(Members.Count);

                for (int index = 0; index < Members.Count; index += 1)
                {
                    participants.Add(Members[index].Participant);
                }

                return new VoiceDedicatedGroupSessionSnapshot(
                    SessionId,
                    AnchorPair.ServerId,
                    AnchorPair.RoomId,
                    participants);
            }
        }
    }
}
