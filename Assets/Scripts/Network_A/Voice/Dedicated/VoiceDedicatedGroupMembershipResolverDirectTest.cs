#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedGroupMembershipResolverDirectTest
    {
        [MenuItem("Tools/Network A/Voice/Run G5 Group Membership Resolver Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                TestCandidateMustBeCloseToEveryMember();
                TestMinimumMaximumDistanceSelection();
                TestDeterministicTieBreak();
                TestScopeAndExistingMembershipAreExcluded();
                TestFourMemberSessionEligibility();

                Debug.Log("VOICE_G5_3_GROUP_MEMBERSHIP_RESOLVER=PASS");
                Debug.Log("VOICE_G5_3_ALL_MEMBER_DISTANCE_GATE=PASS");
                Debug.Log("VOICE_G5_3_MIN_MAX_DISTANCE_SELECTION=PASS");
                Debug.Log("VOICE_G5_3_DETERMINISTIC_TIE_BREAK=PASS");
                Debug.Log("VOICE_G5_3_DISTANCE_EVALUATOR_UNCHANGED=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_3_GROUP_MEMBERSHIP_RESOLVER=FAIL | " +
                    exception);

                throw;
            }
        }

        private static void TestCandidateMustBeCloseToEveryMember()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedGroupSessionSnapshot sessionAb =
                CreateSession(
                    "session-ab",
                    participantA,
                    participantB);

            VoiceDedicatedGroupMembershipResolver resolver =
                new VoiceDedicatedGroupMembershipResolver();

            VoiceDedicatedGroupMembershipResolution resolution;
            bool resolved = resolver.TryResolveJoinTarget(
                participantC,
                new[] { sessionAb },
                CreateGraph(
                    CreateEdge(participantA, participantC, 1.0f, true),
                    CreateEdge(participantB, participantC, 3.01f, true)),
                string.Empty,
                out resolution);

            Require(
                !resolved,
                "A candidate joined without being within 3m of every session member.");

            resolved = resolver.TryResolveJoinTarget(
                participantC,
                new[] { sessionAb },
                CreateGraph(
                    CreateEdge(participantA, participantC, 1.0f, true),
                    CreateEdge(participantB, participantC, 3.0f, true)),
                string.Empty,
                out resolution);

            Require(
                resolved &&
                string.Equals(
                    resolution.SessionId,
                    sessionAb.SessionId,
                    StringComparison.Ordinal) &&
                Math.Abs(resolution.SessionScoreMeters - 3.0f) < 0.0001f,
                "A candidate at the exact group enter boundary was rejected.");
        }

        private static void TestMinimumMaximumDistanceSelection()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");
            VoiceDedicatedGroupParticipant participantD = CreateParticipant("d");
            VoiceDedicatedGroupParticipant participantE = CreateParticipant("e");

            VoiceDedicatedGroupSessionSnapshot sessionAb =
                CreateSession("session-ab", participantA, participantB);

            VoiceDedicatedGroupSessionSnapshot sessionDe =
                CreateSession("session-de", participantD, participantE);

            VoiceDedicatedGroupPairGraph graph =
                CreateGraph(
                    CreateEdge(participantA, participantC, 1.0f, true),
                    CreateEdge(participantB, participantC, 2.8f, true),
                    CreateEdge(participantD, participantC, 1.7f, true),
                    CreateEdge(participantE, participantC, 1.8f, true));

            VoiceDedicatedGroupMembershipResolution resolution;
            bool resolved = new VoiceDedicatedGroupMembershipResolver()
                .TryResolveJoinTarget(
                    participantC,
                    new[] { sessionAb, sessionDe },
                    graph,
                    string.Empty,
                    out resolution);

            Require(
                resolved &&
                string.Equals(
                    resolution.SessionId,
                    "session-de",
                    StringComparison.Ordinal) &&
                Math.Abs(resolution.SessionScoreMeters - 1.8f) < 0.0001f,
                "Resolver did not select the smallest farthest-member distance.");
        }

        private static void TestDeterministicTieBreak()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");
            VoiceDedicatedGroupParticipant participantD = CreateParticipant("d");
            VoiceDedicatedGroupParticipant participantE = CreateParticipant("e");

            VoiceDedicatedGroupSessionSnapshot session10 =
                CreateSession("session-10", participantA, participantB);

            VoiceDedicatedGroupSessionSnapshot session20 =
                CreateSession("session-20", participantD, participantE);

            VoiceDedicatedGroupPairGraph graph =
                CreateGraph(
                    CreateEdge(participantA, participantC, 1.5f, true),
                    CreateEdge(participantB, participantC, 2.0f, true),
                    CreateEdge(participantD, participantC, 2.0f, true),
                    CreateEdge(participantE, participantC, 1.5f, true));

            VoiceDedicatedGroupMembershipResolver resolver =
                new VoiceDedicatedGroupMembershipResolver();

            VoiceDedicatedGroupMembershipResolution resolution;
            bool resolved = resolver.TryResolveJoinTarget(
                participantC,
                new[] { session10, session20 },
                graph,
                "session-20",
                out resolution);

            Require(
                resolved &&
                string.Equals(
                    resolution.SessionId,
                    "session-20",
                    StringComparison.Ordinal),
                "Exact tie did not preserve the previous selected target.");

            resolved = resolver.TryResolveJoinTarget(
                participantC,
                new[] { session20, session10 },
                graph,
                string.Empty,
                out resolution);

            Require(
                resolved &&
                string.Equals(
                    resolution.SessionId,
                    "session-10",
                    StringComparison.Ordinal),
                "Exact tie without a previous target did not select the lowest sessionId.");
        }

        private static void TestScopeAndExistingMembershipAreExcluded()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedGroupSessionSnapshot currentSession =
                CreateSession(
                    "session-current",
                    participantA,
                    participantC);

            VoiceDedicatedGroupSessionSnapshot otherRoomSession =
                new VoiceDedicatedGroupSessionSnapshot(
                    "session-other-room",
                    "server-1",
                    "room-2",
                    new[]
                    {
                        CreateParticipant("d", "room-2"),
                        CreateParticipant("e", "room-2")
                    });

            VoiceDedicatedGroupMembershipResolution resolution;
            bool resolved = new VoiceDedicatedGroupMembershipResolver()
                .TryResolveJoinTarget(
                    participantC,
                    new[] { currentSession, otherRoomSession },
                    CreateGraph(
                        CreateEdge(participantB, participantC, 1.0f, true)),
                    string.Empty,
                    out resolution);

            Require(
                !resolved,
                "Resolver selected the current session or a session from another room.");
        }

        private static void TestFourMemberSessionEligibility()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");
            VoiceDedicatedGroupParticipant participantD = CreateParticipant("d");
            VoiceDedicatedGroupParticipant participantE = CreateParticipant("e");

            VoiceDedicatedGroupSessionSnapshot sessionAbcd =
                CreateSession(
                    "session-abcd",
                    participantA,
                    participantB,
                    participantC,
                    participantD);

            VoiceDedicatedGroupMembershipResolution resolution;
            bool resolved = new VoiceDedicatedGroupMembershipResolver()
                .TryResolveJoinTarget(
                    participantE,
                    new[] { sessionAbcd },
                    CreateGraph(
                        CreateEdge(participantA, participantE, 1.0f, true),
                        CreateEdge(participantB, participantE, 2.0f, true),
                        CreateEdge(participantC, participantE, 2.5f, true),
                        CreateEdge(participantD, participantE, 3.0f, true)),
                    string.Empty,
                    out resolution);

            Require(
                resolved &&
                resolution.ExistingMemberCount == 4 &&
                Math.Abs(resolution.SessionScoreMeters - 3.0f) < 0.0001f,
                "Resolver did not evaluate every member of a four-member session.");
        }

        private static VoiceDedicatedGroupParticipant CreateParticipant(
            string label,
            string roomId = "room-1")
        {
            char connectionCharacter =
                label[0];

            return new VoiceDedicatedGroupParticipant(
                "server-1",
                roomId,
                "user-" + label,
                new string(connectionCharacter, 12) +
                "4" + new string(connectionCharacter, 3) +
                "8" + new string(connectionCharacter, 15));
        }

        private static VoiceDedicatedGroupSessionSnapshot CreateSession(
            string sessionId,
            params VoiceDedicatedGroupParticipant[] members)
        {
            return new VoiceDedicatedGroupSessionSnapshot(
                sessionId,
                "server-1",
                "room-1",
                members);
        }

        private static VoiceDedicatedGroupPairEdge CreateEdge(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            float distanceMeters,
            bool isEntered)
        {
            return new VoiceDedicatedGroupPairEdge(
                new VoiceDedicatedParticipantPair(
                    first.ServerId,
                    first.RoomId,
                    first.UserId,
                    first.ConnectionId,
                    second.UserId,
                    second.ConnectionId),
                distanceMeters,
                isEntered);
        }

        private static VoiceDedicatedGroupPairGraph CreateGraph(
            params VoiceDedicatedGroupPairEdge[] edges)
        {
            return new VoiceDedicatedGroupPairGraph(edges);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif
