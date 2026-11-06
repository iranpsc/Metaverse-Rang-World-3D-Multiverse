using System;
using System.Collections.Generic;

namespace Network_A.Voice.Dedicated
{
    public sealed class VoiceDedicatedPairReformationCandidate
    {
        public VoiceDedicatedParticipantPair Pair { get; private set; }
        public float DistanceMeters { get; private set; }

        public VoiceDedicatedPairReformationCandidate(
            VoiceDedicatedParticipantPair pair,
            float distanceMeters)
        {
            if (string.IsNullOrWhiteSpace(pair.PairKey))
            {
                throw new ArgumentException(
                    "A Voice pair reformation candidate requires an initialized pair.",
                    "pair");
            }

            if (float.IsNaN(distanceMeters) ||
                float.IsInfinity(distanceMeters) ||
                distanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "distanceMeters",
                    "Voice pair reformation distance must be finite and non-negative.");
            }

            Pair = pair;
            DistanceMeters = distanceMeters;
        }
    }

    public sealed class VoiceDedicatedGroupLeaveReformationPlan
    {
        private readonly VoiceDedicatedGroupParticipant[] remainingMembers;
        private readonly VoiceDedicatedPairReformationCandidate[] pairReformations;

        public string StableSessionId { get; private set; }
        public VoiceDedicatedGroupParticipant LeavingMember { get; private set; }
        public VoiceDedicatedParticipantPair LeaveAnchorPair { get; private set; }
        public float LeaveDistanceMeters { get; private set; }
        public bool ClosesStableSession { get; private set; }
        public IReadOnlyList<VoiceDedicatedGroupParticipant> RemainingMembers
        {
            get { return remainingMembers; }
        }
        public IReadOnlyList<VoiceDedicatedPairReformationCandidate> PairReformations
        {
            get { return pairReformations; }
        }

        public VoiceDedicatedGroupLeaveReformationPlan(
            string stableSessionId,
            VoiceDedicatedGroupParticipant leavingMember,
            VoiceDedicatedParticipantPair leaveAnchorPair,
            float leaveDistanceMeters,
            bool closesStableSession,
            IEnumerable<VoiceDedicatedGroupParticipant> stableRemainingMembers,
            IEnumerable<VoiceDedicatedPairReformationCandidate> reformationCandidates)
        {
            if (string.IsNullOrWhiteSpace(stableSessionId))
            {
                throw new ArgumentException(
                    "A Voice group leave plan requires stableSessionId.",
                    "stableSessionId");
            }

            if (leavingMember == null)
            {
                throw new ArgumentNullException("leavingMember");
            }

            if (string.IsNullOrWhiteSpace(leaveAnchorPair.PairKey))
            {
                throw new ArgumentException(
                    "A Voice group leave plan requires an initialized leave anchor pair.",
                    "leaveAnchorPair");
            }

            if (float.IsNaN(leaveDistanceMeters) ||
                float.IsInfinity(leaveDistanceMeters) ||
                leaveDistanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "leaveDistanceMeters",
                    "Voice group leave distance must be finite and non-negative.");
            }

            if (stableRemainingMembers == null)
            {
                throw new ArgumentNullException("stableRemainingMembers");
            }

            if (reformationCandidates == null)
            {
                throw new ArgumentNullException("reformationCandidates");
            }

            List<VoiceDedicatedGroupParticipant> normalizedRemainingMembers =
                new List<VoiceDedicatedGroupParticipant>();

            foreach (VoiceDedicatedGroupParticipant member in stableRemainingMembers)
            {
                if (member == null)
                {
                    throw new ArgumentException(
                        "A Voice group leave plan cannot contain a null remaining member.",
                        "stableRemainingMembers");
                }

                normalizedRemainingMembers.Add(member);
            }

            if (closesStableSession && normalizedRemainingMembers.Count != 1)
            {
                throw new ArgumentException(
                    "A closing pair session must leave exactly one former member.",
                    "stableRemainingMembers");
            }

            if (!closesStableSession && normalizedRemainingMembers.Count < 2)
            {
                throw new ArgumentException(
                    "A preserved Voice session requires at least two remaining members.",
                    "stableRemainingMembers");
            }

            List<VoiceDedicatedPairReformationCandidate> normalizedReformations =
                new List<VoiceDedicatedPairReformationCandidate>();

            foreach (VoiceDedicatedPairReformationCandidate candidate in reformationCandidates)
            {
                if (candidate == null)
                {
                    throw new ArgumentException(
                        "A Voice group leave plan cannot contain a null pair reformation.",
                        "reformationCandidates");
                }

                normalizedReformations.Add(candidate);
            }

            normalizedReformations.Sort(
                delegate(
                    VoiceDedicatedPairReformationCandidate first,
                    VoiceDedicatedPairReformationCandidate second)
                {
                    return string.CompareOrdinal(
                        first.Pair.PairKey,
                        second.Pair.PairKey);
                });

            StableSessionId = stableSessionId.Trim();
            LeavingMember = leavingMember;
            LeaveAnchorPair = leaveAnchorPair;
            LeaveDistanceMeters = leaveDistanceMeters;
            ClosesStableSession = closesStableSession;
            remainingMembers = normalizedRemainingMembers.ToArray();
            pairReformations = normalizedReformations.ToArray();
        }
    }

    public sealed class VoiceDedicatedGroupLeaveReformationPlanner
    {
        public bool TryCreatePlan(
            VoiceDedicatedGroupSessionSnapshot session,
            VoiceDedicatedGroupParticipant leavingMember,
            VoiceDedicatedParticipantPair exitedPair,
            float exitedDistanceMeters,
            VoiceDedicatedGroupPairGraph pairGraph,
            out VoiceDedicatedGroupLeaveReformationPlan plan)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            if (leavingMember == null)
            {
                throw new ArgumentNullException("leavingMember");
            }

            if (pairGraph == null)
            {
                throw new ArgumentNullException("pairGraph");
            }

            if (!session.Contains(leavingMember))
            {
                throw new ArgumentException(
                    "The leaving Voice participant must belong to the session.",
                    "leavingMember");
            }

            if (float.IsNaN(exitedDistanceMeters) ||
                float.IsInfinity(exitedDistanceMeters) ||
                exitedDistanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "exitedDistanceMeters",
                    "Exited Voice pair distance must be finite and non-negative.");
            }

            VoiceDedicatedGroupParticipant exitedPeer =
                ResolveExitedPeer(
                    session,
                    leavingMember,
                    exitedPair);

            VoiceDedicatedGroupPairEdge exitedEdge;
            if (!pairGraph.TryGetEdge(
                    leavingMember,
                    exitedPeer,
                    out exitedEdge) ||
                exitedEdge.IsEntered)
            {
                plan = null;
                return false;
            }

            List<VoiceDedicatedGroupParticipant> remainingMembers =
                new List<VoiceDedicatedGroupParticipant>();

            List<VoiceDedicatedPairReformationCandidate> reformations =
                new List<VoiceDedicatedPairReformationCandidate>();

            bool hasInvalidMembershipEdge = false;

            for (int index = 0; index < session.Members.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant member =
                    session.Members[index];

                if (member.HasSameIdentity(leavingMember))
                {
                    continue;
                }

                remainingMembers.Add(member);

                VoiceDedicatedGroupPairEdge edge;
                if (!pairGraph.TryGetEdge(
                        leavingMember,
                        member,
                        out edge) ||
                    !edge.IsEntered)
                {
                    hasInvalidMembershipEdge = true;
                    continue;
                }

                reformations.Add(
                    new VoiceDedicatedPairReformationCandidate(
                        edge.Pair,
                        edge.DistanceMeters));
            }

            if (!hasInvalidMembershipEdge)
            {
                plan = null;
                return false;
            }

            bool closesStableSession =
                session.Members.Count == 2;

            if (closesStableSession)
            {
                reformations.Clear();
            }

            plan =
                new VoiceDedicatedGroupLeaveReformationPlan(
                    session.SessionId,
                    leavingMember,
                    exitedPair,
                    exitedDistanceMeters,
                    closesStableSession,
                    remainingMembers,
                    reformations);

            return true;
        }

        private static VoiceDedicatedGroupParticipant ResolveExitedPeer(
            VoiceDedicatedGroupSessionSnapshot session,
            VoiceDedicatedGroupParticipant leavingMember,
            VoiceDedicatedParticipantPair exitedPair)
        {
            if (string.IsNullOrWhiteSpace(exitedPair.PairKey))
            {
                throw new ArgumentException(
                    "A Voice group leave requires an initialized exited pair.",
                    "exitedPair");
            }

            if (!string.Equals(
                    exitedPair.ServerId,
                    session.ServerId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    exitedPair.RoomId,
                    session.RoomId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The exited Voice pair must match the session server and room.",
                    "exitedPair");
            }

            bool leavingIsFirst =
                string.Equals(
                    exitedPair.FirstUserId,
                    leavingMember.UserId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    exitedPair.FirstConnectionId,
                    leavingMember.ConnectionId,
                    StringComparison.Ordinal);

            bool leavingIsSecond =
                string.Equals(
                    exitedPair.SecondUserId,
                    leavingMember.UserId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    exitedPair.SecondConnectionId,
                    leavingMember.ConnectionId,
                    StringComparison.Ordinal);

            if (!leavingIsFirst && !leavingIsSecond)
            {
                throw new ArgumentException(
                    "The exited Voice pair must contain the leaving member.",
                    "exitedPair");
            }

            string peerUserId = leavingIsFirst
                ? exitedPair.SecondUserId
                : exitedPair.FirstUserId;

            string peerConnectionId = leavingIsFirst
                ? exitedPair.SecondConnectionId
                : exitedPair.FirstConnectionId;

            for (int index = 0; index < session.Members.Count; index += 1)
            {
                VoiceDedicatedGroupParticipant member = session.Members[index];

                if (string.Equals(
                        member.UserId,
                        peerUserId,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        member.ConnectionId,
                        peerConnectionId,
                        StringComparison.Ordinal))
                {
                    return member;
                }
            }

            throw new ArgumentException(
                "The exited Voice pair peer must belong to the session.",
                "exitedPair");
        }
    }
}
