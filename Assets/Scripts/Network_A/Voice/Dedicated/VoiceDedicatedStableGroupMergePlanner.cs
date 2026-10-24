using System;
using System.Collections.Generic;

namespace Network_A.Voice.Dedicated
{
    public sealed class VoiceDedicatedStableGroupMergePlan
    {
        private readonly string[] secondarySessionIdsToBurn;

        public string TargetSessionId { get; private set; }
        public VoiceDedicatedParticipantPair TargetAnchorPair { get; private set; }
        public VoiceDedicatedGroupParticipant JoiningMember { get; private set; }
        public float SessionScoreMeters { get; private set; }
        public IReadOnlyList<string> SecondarySessionIdsToBurn
        {
            get { return secondarySessionIdsToBurn; }
        }

        public VoiceDedicatedStableGroupMergePlan(
            string targetSessionId,
            VoiceDedicatedParticipantPair targetAnchorPair,
            VoiceDedicatedGroupParticipant joiningMember,
            float sessionScoreMeters,
            IEnumerable<string> secondarySessionIds)
        {
            if (string.IsNullOrWhiteSpace(targetSessionId))
            {
                throw new ArgumentException(
                    "A stable Voice merge plan requires targetSessionId.",
                    "targetSessionId");
            }

            if (string.IsNullOrWhiteSpace(targetAnchorPair.PairKey))
            {
                throw new ArgumentException(
                    "A stable Voice merge plan requires an initialized anchor pair.",
                    "targetAnchorPair");
            }

            if (joiningMember == null)
            {
                throw new ArgumentNullException("joiningMember");
            }

            if (float.IsNaN(sessionScoreMeters) ||
                float.IsInfinity(sessionScoreMeters) ||
                sessionScoreMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "sessionScoreMeters",
                    "Stable Voice merge score must be finite and non-negative.");
            }

            if (secondarySessionIds == null)
            {
                throw new ArgumentNullException("secondarySessionIds");
            }

            List<string> normalizedSecondaryIds = new List<string>();
            HashSet<string> uniqueSecondaryIds =
                new HashSet<string>(StringComparer.Ordinal);

            foreach (string secondarySessionId in secondarySessionIds)
            {
                string normalized = string.IsNullOrWhiteSpace(secondarySessionId)
                    ? string.Empty
                    : secondarySessionId.Trim();

                if (normalized.Length == 0)
                {
                    throw new ArgumentException(
                        "A secondary Voice session id cannot be empty.",
                        "secondarySessionIds");
                }

                if (string.Equals(
                        normalized,
                        targetSessionId.Trim(),
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The stable target session cannot be burned.",
                        "secondarySessionIds");
                }

                if (uniqueSecondaryIds.Add(normalized))
                {
                    normalizedSecondaryIds.Add(normalized);
                }
            }

            normalizedSecondaryIds.Sort(StringComparer.Ordinal);

            TargetSessionId = targetSessionId.Trim();
            TargetAnchorPair = targetAnchorPair;
            JoiningMember = joiningMember;
            SessionScoreMeters = sessionScoreMeters;
            secondarySessionIdsToBurn = normalizedSecondaryIds.ToArray();
        }
    }

    public sealed class VoiceDedicatedStableGroupMergePlanner
    {
        private readonly VoiceDedicatedGroupMembershipResolver membershipResolver;

        public VoiceDedicatedStableGroupMergePlanner(
            VoiceDedicatedGroupMembershipResolver resolver = null)
        {
            membershipResolver = resolver ??
                new VoiceDedicatedGroupMembershipResolver();
        }

        public bool TryCreatePlan(
            VoiceDedicatedGroupParticipant candidate,
            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> sessions,
            VoiceDedicatedGroupPairGraph pairGraph,
            string previousSelectedTargetSessionId,
            out VoiceDedicatedStableGroupMergePlan plan)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException("candidate");
            }

            if (sessions == null)
            {
                throw new ArgumentNullException("sessions");
            }

            if (pairGraph == null)
            {
                throw new ArgumentNullException("pairGraph");
            }

            VoiceDedicatedGroupMembershipResolution resolution;
            if (!membershipResolver.TryResolveJoinTarget(
                    candidate,
                    sessions,
                    pairGraph,
                    previousSelectedTargetSessionId,
                    out resolution))
            {
                plan = null;
                return false;
            }

            VoiceDedicatedGroupSessionSnapshot targetSession = null;
            for (int index = 0; index < sessions.Count; index += 1)
            {
                VoiceDedicatedGroupSessionSnapshot session = sessions[index];
                if (session == null)
                {
                    throw new ArgumentException(
                        "Voice group session snapshots cannot contain null.",
                        "sessions");
                }

                if (string.Equals(
                        session.SessionId,
                        resolution.SessionId,
                        StringComparison.Ordinal))
                {
                    targetSession = session;
                    break;
                }
            }

            if (targetSession == null || targetSession.Members.Count < 2)
            {
                throw new InvalidOperationException(
                    "Resolved Voice group target session is missing or invalid.");
            }

            List<string> secondarySessionIds = new List<string>();

            for (int sessionIndex = 0;
                 sessionIndex < sessions.Count;
                 sessionIndex += 1)
            {
                VoiceDedicatedGroupSessionSnapshot session = sessions[sessionIndex];

                if (string.Equals(
                        session.SessionId,
                        targetSession.SessionId,
                        StringComparison.Ordinal) ||
                    !session.Contains(candidate))
                {
                    continue;
                }

                bool overlapsTarget = false;
                for (int memberIndex = 0;
                     memberIndex < targetSession.Members.Count;
                     memberIndex += 1)
                {
                    if (session.Contains(targetSession.Members[memberIndex]))
                    {
                        overlapsTarget = true;
                        break;
                    }
                }

                if (!overlapsTarget) continue;

                if (session.Members.Count != 2)
                {
                    plan = null;
                    return false;
                }

                secondarySessionIds.Add(session.SessionId);
            }

            VoiceDedicatedGroupParticipant firstAnchor =
                targetSession.Members[0];
            VoiceDedicatedGroupParticipant secondAnchor =
                targetSession.Members[1];

            VoiceDedicatedParticipantPair anchorPair =
                new VoiceDedicatedParticipantPair(
                    targetSession.ServerId,
                    targetSession.RoomId,
                    firstAnchor.UserId,
                    firstAnchor.ConnectionId,
                    secondAnchor.UserId,
                    secondAnchor.ConnectionId);

            plan = new VoiceDedicatedStableGroupMergePlan(
                targetSession.SessionId,
                anchorPair,
                candidate,
                resolution.SessionScoreMeters,
                secondarySessionIds);

            return true;
        }
    }
}
