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

    public sealed class VoiceDedicatedGroupTopologyRuntime
    {
        private readonly Dictionary<string, PairEdgeState> edgesByPairKey =
            new Dictionary<string, PairEdgeState>(StringComparer.Ordinal);
        private readonly Dictionary<string, RuntimeSession> sessionsById =
            new Dictionary<string, RuntimeSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> sessionIdByPairKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> previousTargetByParticipantKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> usedSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> burnedSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly VoiceDedicatedStableGroupMergePlanner mergePlanner;
        private readonly VoiceDedicatedGroupLeaveReformationPlanner leavePlanner;
        private readonly Func<string> sessionIdFactory;

        private long nextSessionOrder;

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

            for (int index = 0; index < observations.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation =
                    observations[index];

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

            HashSet<string> createdPairKeys =
                new HashSet<string>(StringComparer.Ordinal);

            for (int index = 0; index < entered.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation = entered[index];

                if (sessionIdByPairKey.ContainsKey(observation.Pair.PairKey))
                {
                    continue;
                }

                string sessionId = NormalizeAndReserveSessionId(
                    observation.SuggestedSessionId);

                CreatePairSession(
                    observation.Pair,
                    sessionId,
                    observation.DistanceMeters,
                    observation.EffectiveAtMs);

                deltas.Add(
                    CreatePairSessionDelta(
                        observation.Pair,
                        sessionId,
                        observation.DistanceMeters,
                        observation.EffectiveAtMs,
                        authorityEpochId,
                        nextSourceSequence()));

                createdPairKeys.Add(observation.Pair.PairKey);
            }

            ApplyAllEligibleStableMerges(
                authorityEpochId,
                nextSourceSequence,
                deltas);

            for (int index = 0; index < entered.Count; index += 1)
            {
                VoiceDedicatedTopologyPairObservation observation = entered[index];
                if (createdPairKeys.Contains(observation.Pair.PairKey)) continue;

                AddDistanceDeltaIfMapped(
                    observation,
                    authorityEpochId,
                    nextSourceSequence,
                    deltas);
            }

            for (int index = 0; index < updated.Count; index += 1)
            {
                AddDistanceDeltaIfMapped(
                    updated[index],
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
                bool closesDisconnectedGroup;
                if (!TryResolveGraphBackedGroupExit(
                        session,
                        out leavingMember,
                        out closesDisconnectedGroup))
                {
                    return;
                }

                if (closesDisconnectedGroup)
                {
                    deltas.Add(
                        VoiceDedicatedSessionDelta.CreateSessionClosed(
                            session.AnchorPair,
                            session.SessionId,
                            observation.DistanceMeters,
                            VoiceDedicatedSessionReason.ProximityExit,
                            observation.EffectiveAtMs,
                            authorityEpochId,
                            nextSourceSequence()));

                    RemoveAndBurnSession(session);
                    return;
                }
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
        }

        private void RemoveAndBurnSession(RuntimeSession session)
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
                edgesByPairKey.Remove(removals[index]);
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
