using System;
using System.Collections.Generic;
using System.Text;

namespace Network_A.Voice.Dedicated
{
    public sealed class VoiceDedicatedGroupParticipant
    {
        public string ServerId { get; private set; }
        public string RoomId { get; private set; }
        public string UserId { get; private set; }
        public string ConnectionId { get; private set; }
        internal string IdentityKey { get; private set; }

        public VoiceDedicatedGroupParticipant(
            string serverId,
            string roomId,
            string userId,
            string connectionId)
        {
            ServerId = NormalizeRequiredText(serverId, "serverId");
            RoomId = NormalizeRequiredText(roomId, "roomId");
            UserId = NormalizeRequiredText(userId, "userId");
            ConnectionId = NormalizeConnectionId(
                connectionId,
                "connectionId");

            IdentityKey = BuildIdentityKey(
                ServerId,
                RoomId,
                UserId,
                ConnectionId);
        }

        public bool HasSameIdentity(
            VoiceDedicatedGroupParticipant other)
        {
            return other != null &&
                   string.Equals(ServerId, other.ServerId, StringComparison.Ordinal) &&
                   string.Equals(RoomId, other.RoomId, StringComparison.Ordinal) &&
                   string.Equals(UserId, other.UserId, StringComparison.Ordinal) &&
                   string.Equals(
                       ConnectionId,
                       other.ConnectionId,
                       StringComparison.Ordinal);
        }

        private static string NormalizeRequiredText(
            string value,
            string fieldName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(fieldName);
            }

            string normalized = value.Trim();
            if (normalized.Length == 0)
            {
                throw new ArgumentException(
                    fieldName + " is required.",
                    fieldName);
            }

            if (Encoding.UTF8.GetByteCount(normalized) > 512)
            {
                throw new ArgumentOutOfRangeException(
                    fieldName,
                    fieldName + " exceeds 512 UTF-8 bytes.");
            }

            return normalized;
        }

        private static string NormalizeConnectionId(
            string value,
            string fieldName)
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
                char character = normalized[index];
                bool isDigit = character >= '0' && character <= '9';
                bool isLowerHex = character >= 'a' && character <= 'f';

                if (!isDigit && !isLowerHex)
                {
                    throw new ArgumentException(
                        fieldName + " must contain only hexadecimal characters.",
                        fieldName);
                }
            }

            return normalized;
        }

        internal static string BuildIdentityKey(
            string serverId,
            string roomId,
            string userId,
            string connectionId)
        {
            return serverId.Length + ":" + serverId + "|" +
                   roomId.Length + ":" + roomId + "|" +
                   userId.Length + ":" + userId + "|" +
                   connectionId.Length + ":" + connectionId;
        }
    }

    public sealed class VoiceDedicatedGroupSessionSnapshot
    {
        private readonly VoiceDedicatedGroupParticipant[] members;

        public string SessionId { get; private set; }
        public string ServerId { get; private set; }
        public string RoomId { get; private set; }
        public IReadOnlyList<VoiceDedicatedGroupParticipant> Members
        {
            get { return members; }
        }

        public VoiceDedicatedGroupSessionSnapshot(
            string sessionId,
            string serverId,
            string roomId,
            IEnumerable<VoiceDedicatedGroupParticipant> sessionMembers)
        {
            SessionId = NormalizeRequiredText(sessionId, "sessionId");
            ServerId = NormalizeRequiredText(serverId, "serverId");
            RoomId = NormalizeRequiredText(roomId, "roomId");

            if (sessionMembers == null)
            {
                throw new ArgumentNullException("sessionMembers");
            }

            List<VoiceDedicatedGroupParticipant> normalizedMembers =
                new List<VoiceDedicatedGroupParticipant>();

            HashSet<string> participantKeys =
                new HashSet<string>(StringComparer.Ordinal);

            foreach (VoiceDedicatedGroupParticipant member in sessionMembers)
            {
                if (member == null)
                {
                    throw new ArgumentException(
                        "A Voice group session cannot contain a null member.",
                        "sessionMembers");
                }

                if (!string.Equals(
                        member.ServerId,
                        ServerId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        member.RoomId,
                        RoomId,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Every Voice group member must belong to the session server and room.",
                        "sessionMembers");
                }

                string participantKey =
                    BuildParticipantKey(member);

                if (!participantKeys.Add(participantKey))
                {
                    throw new ArgumentException(
                        "A Voice group session cannot contain a duplicate member.",
                        "sessionMembers");
                }

                normalizedMembers.Add(member);
            }

            if (normalizedMembers.Count < 2)
            {
                throw new ArgumentException(
                    "An active Voice group session requires at least two members.",
                    "sessionMembers");
            }

            members = normalizedMembers.ToArray();
        }

        public bool Contains(
            VoiceDedicatedGroupParticipant participant)
        {
            if (participant == null) return false;

            for (int index = 0; index < members.Length; index += 1)
            {
                if (members[index].HasSameIdentity(participant)) return true;
            }

            return false;
        }

        private static string BuildParticipantKey(
            VoiceDedicatedGroupParticipant participant)
        {
            return participant.UserId.Length + ":" + participant.UserId + "|" +
                   participant.ConnectionId.Length + ":" + participant.ConnectionId;
        }

        private static string NormalizeRequiredText(
            string value,
            string fieldName)
        {
            if (value == null)
            {
                throw new ArgumentNullException(fieldName);
            }

            string normalized = value.Trim();
            if (normalized.Length == 0)
            {
                throw new ArgumentException(
                    fieldName + " is required.",
                    fieldName);
            }

            return normalized;
        }
    }

    public sealed class VoiceDedicatedGroupPairEdge
    {
        public VoiceDedicatedParticipantPair Pair { get; private set; }
        public float DistanceMeters { get; private set; }
        public bool IsEntered { get; private set; }

        public VoiceDedicatedGroupPairEdge(
            VoiceDedicatedParticipantPair pair,
            float distanceMeters,
            bool isEntered)
        {
            if (string.IsNullOrWhiteSpace(pair.PairKey))
            {
                throw new ArgumentException(
                    "A Voice group edge requires a valid pair.",
                    "pair");
            }

            if (float.IsNaN(distanceMeters) ||
                float.IsInfinity(distanceMeters) ||
                distanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "distanceMeters",
                    "Voice group edge distance must be finite and non-negative.");
            }

            Pair = pair;
            DistanceMeters = distanceMeters;
            IsEntered = isEntered;
        }
    }

    public sealed class VoiceDedicatedGroupPairGraph
    {
        private readonly Dictionary<string, VoiceDedicatedGroupPairEdge> edgesByPairKey =
            new Dictionary<string, VoiceDedicatedGroupPairEdge>(StringComparer.Ordinal);

        private readonly Dictionary<string, Dictionary<string, VoiceDedicatedGroupPairEdge>>
            edgesByParticipantKey =
                new Dictionary<string, Dictionary<string, VoiceDedicatedGroupPairEdge>>(
                    StringComparer.Ordinal);

        public int EdgeCount { get { return edgesByPairKey.Count; } }

        public VoiceDedicatedGroupPairGraph(
            IEnumerable<VoiceDedicatedGroupPairEdge> pairEdges)
        {
            if (pairEdges == null)
            {
                throw new ArgumentNullException("pairEdges");
            }

            foreach (VoiceDedicatedGroupPairEdge edge in pairEdges)
            {
                if (edge == null)
                {
                    throw new ArgumentException(
                        "A Voice group pair graph cannot contain a null edge.",
                        "pairEdges");
                }

                if (edgesByPairKey.ContainsKey(edge.Pair.PairKey))
                {
                    throw new ArgumentException(
                        "A Voice group pair graph cannot contain a duplicate pair edge.",
                        "pairEdges");
                }

                edgesByPairKey.Add(
                    edge.Pair.PairKey,
                    edge);

                string firstParticipantKey =
                    VoiceDedicatedGroupParticipant.BuildIdentityKey(
                        edge.Pair.ServerId,
                        edge.Pair.RoomId,
                        edge.Pair.FirstUserId,
                        edge.Pair.FirstConnectionId);

                string secondParticipantKey =
                    VoiceDedicatedGroupParticipant.BuildIdentityKey(
                        edge.Pair.ServerId,
                        edge.Pair.RoomId,
                        edge.Pair.SecondUserId,
                        edge.Pair.SecondConnectionId);

                AddAdjacency(
                    firstParticipantKey,
                    secondParticipantKey,
                    edge);

                AddAdjacency(
                    secondParticipantKey,
                    firstParticipantKey,
                    edge);
            }
        }

        public bool TryGetEdge(
            VoiceDedicatedParticipantPair pair,
            out VoiceDedicatedGroupPairEdge edge)
        {
            return edgesByPairKey.TryGetValue(
                pair.PairKey,
                out edge);
        }

        public bool TryGetEdge(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            out VoiceDedicatedGroupPairEdge edge)
        {
            if (first == null)
            {
                throw new ArgumentNullException("first");
            }

            if (second == null)
            {
                throw new ArgumentNullException("second");
            }

            Dictionary<string, VoiceDedicatedGroupPairEdge> adjacentEdges;
            if (!edgesByParticipantKey.TryGetValue(
                    first.IdentityKey,
                    out adjacentEdges))
            {
                edge = null;
                return false;
            }

            return adjacentEdges.TryGetValue(
                second.IdentityKey,
                out edge);
        }

        private void AddAdjacency(
            string firstParticipantKey,
            string secondParticipantKey,
            VoiceDedicatedGroupPairEdge edge)
        {
            Dictionary<string, VoiceDedicatedGroupPairEdge> adjacentEdges;
            if (!edgesByParticipantKey.TryGetValue(
                    firstParticipantKey,
                    out adjacentEdges))
            {
                adjacentEdges =
                    new Dictionary<string, VoiceDedicatedGroupPairEdge>(
                        StringComparer.Ordinal);

                edgesByParticipantKey.Add(
                    firstParticipantKey,
                    adjacentEdges);
            }

            adjacentEdges.Add(
                secondParticipantKey,
                edge);
        }
    }

    public struct VoiceDedicatedGroupMembershipResolution
    {
        public string SessionId { get; private set; }
        public float SessionScoreMeters { get; private set; }
        public int ExistingMemberCount { get; private set; }

        public VoiceDedicatedGroupMembershipResolution(
            string sessionId,
            float sessionScoreMeters,
            int existingMemberCount)
        {
            SessionId = sessionId;
            SessionScoreMeters = sessionScoreMeters;
            ExistingMemberCount = existingMemberCount;
        }
    }

    public sealed class VoiceDedicatedGroupMembershipResolver
    {
        public const float DefaultEnterDistanceMeters = 3.0f;

        private readonly float enterDistanceMeters;

        public VoiceDedicatedGroupMembershipResolver(
            float confirmedEnterDistanceMeters = DefaultEnterDistanceMeters)
        {
            if (float.IsNaN(confirmedEnterDistanceMeters) ||
                float.IsInfinity(confirmedEnterDistanceMeters) ||
                confirmedEnterDistanceMeters <= 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    "confirmedEnterDistanceMeters",
                    "Voice group enter distance must be finite and greater than zero.");
            }

            enterDistanceMeters = confirmedEnterDistanceMeters;
        }

        public bool TryResolveJoinTarget(
            VoiceDedicatedGroupParticipant candidate,
            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> sessions,
            VoiceDedicatedGroupPairGraph pairGraph,
            string previousSelectedTargetSessionId,
            out VoiceDedicatedGroupMembershipResolution resolution)
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

            string previousTarget =
                string.IsNullOrWhiteSpace(previousSelectedTargetSessionId)
                    ? string.Empty
                    : previousSelectedTargetSessionId.Trim();

            bool found = false;
            float minimumScore = float.MaxValue;
            VoiceDedicatedGroupSessionSnapshot selectedSession = null;

            for (int sessionIndex = 0;
                 sessionIndex < sessions.Count;
                 sessionIndex += 1)
            {
                VoiceDedicatedGroupSessionSnapshot session =
                    sessions[sessionIndex];

                if (session == null)
                {
                    throw new ArgumentException(
                        "Voice group session snapshots cannot contain null.",
                        "sessions");
                }

                if (!string.Equals(
                        session.ServerId,
                        candidate.ServerId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        session.RoomId,
                        candidate.RoomId,
                        StringComparison.Ordinal) ||
                    session.Contains(candidate))
                {
                    continue;
                }

                float sessionScore;
                if (!TryCalculateEligibleSessionScore(
                        candidate,
                        session,
                        pairGraph,
                        out sessionScore))
                {
                    continue;
                }

                if (!found || sessionScore < minimumScore)
                {
                    found = true;
                    minimumScore = sessionScore;
                    selectedSession = session;
                    continue;
                }

                if (sessionScore != minimumScore) continue;

                bool candidateIsPrevious =
                    string.Equals(
                        session.SessionId,
                        previousTarget,
                        StringComparison.Ordinal);

                bool selectedIsPrevious =
                    selectedSession != null &&
                    string.Equals(
                        selectedSession.SessionId,
                        previousTarget,
                        StringComparison.Ordinal);

                if (candidateIsPrevious ||
                    (!selectedIsPrevious &&
                     string.CompareOrdinal(
                         session.SessionId,
                         selectedSession.SessionId) < 0))
                {
                    selectedSession = session;
                }
            }

            if (!found || selectedSession == null)
            {
                resolution =
                    default(VoiceDedicatedGroupMembershipResolution);

                return false;
            }

            resolution =
                new VoiceDedicatedGroupMembershipResolution(
                    selectedSession.SessionId,
                    minimumScore,
                    selectedSession.Members.Count);

            return true;
        }

        private bool TryCalculateEligibleSessionScore(
            VoiceDedicatedGroupParticipant candidate,
            VoiceDedicatedGroupSessionSnapshot session,
            VoiceDedicatedGroupPairGraph pairGraph,
            out float scoreMeters)
        {
            float maximumDistance = 0.0f;

            for (int memberIndex = 0;
                 memberIndex < session.Members.Count;
                 memberIndex += 1)
            {
                VoiceDedicatedGroupParticipant member =
                    session.Members[memberIndex];

                VoiceDedicatedGroupPairEdge edge;
                if (!pairGraph.TryGetEdge(candidate, member, out edge) ||
                    !edge.IsEntered ||
                    edge.DistanceMeters > enterDistanceMeters)
                {
                    scoreMeters = 0.0f;
                    return false;
                }

                maximumDistance = Math.Max(
                    maximumDistance,
                    edge.DistanceMeters);
            }

            scoreMeters = maximumDistance;
            return true;
        }
    }
}
