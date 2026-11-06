#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedStableGroupMergePlannerDirectTest
    {
        [MenuItem("Tools/Network A/Voice/Run G5 Stable Group Merge Planner Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                TestStableTargetAndSecondaryBurnOrder();
                TestIneligibleCandidateDoesNotProducePlan();
                TestGroupToGroupConflictIsRejected();
                TestMemberJoinedDeltaContract();

                Debug.Log("VOICE_G5_4_STABLE_SESSION_IDENTITY=PASS");
                Debug.Log("VOICE_G5_4_PAIR_TO_GROUP_MERGE_PLAN=PASS");
                Debug.Log("VOICE_G5_4_SECONDARY_SESSION_BURN_ORDER=PASS");
                Debug.Log("VOICE_G5_4_MEMBER_JOINED_DELTA=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_4_STABLE_GROUP_MERGE=FAIL | " +
                    exception);

                throw;
            }
        }

        private static void TestStableTargetAndSecondaryBurnOrder()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedGroupSessionSnapshot sessionAb =
                CreateSession(
                    "11111111-1111-4111-8111-111111111111",
                    participantA,
                    participantB);

            VoiceDedicatedGroupSessionSnapshot sessionAc =
                CreateSession(
                    "33333333-3333-4333-8333-333333333333",
                    participantA,
                    participantC);

            VoiceDedicatedGroupSessionSnapshot sessionBc =
                CreateSession(
                    "22222222-2222-4222-8222-222222222222",
                    participantB,
                    participantC);

            VoiceDedicatedStableGroupMergePlan plan;
            bool planned = new VoiceDedicatedStableGroupMergePlanner()
                .TryCreatePlan(
                    participantC,
                    new[] { sessionAc, sessionAb, sessionBc },
                    CreateGraph(
                        CreateEdge(participantA, participantC, 1.5f, true),
                        CreateEdge(participantB, participantC, 2.25f, true)),
                    sessionAb.SessionId,
                    out plan);

            Require(planned && plan != null, "Stable merge plan was not created.");
            Require(
                string.Equals(
                    plan.TargetSessionId,
                    sessionAb.SessionId,
                    StringComparison.Ordinal),
                "The existing target SessionId was not preserved.");
            Require(
                plan.SecondarySessionIdsToBurn.Count == 2 &&
                string.Equals(
                    plan.SecondarySessionIdsToBurn[0],
                    sessionBc.SessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    plan.SecondarySessionIdsToBurn[1],
                    sessionAc.SessionId,
                    StringComparison.Ordinal),
                "Secondary pair sessions were not returned in deterministic burn order.");
            Require(
                Math.Abs(plan.SessionScoreMeters - 2.25f) < 0.0001f,
                "Stable merge plan did not preserve the min(maxDistance) score.");
        }

        private static void TestIneligibleCandidateDoesNotProducePlan()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedStableGroupMergePlan plan;
            bool planned = new VoiceDedicatedStableGroupMergePlanner()
                .TryCreatePlan(
                    participantC,
                    new[] { CreateSession("session-ab", participantA, participantB) },
                    CreateGraph(
                        CreateEdge(participantA, participantC, 1.0f, true),
                        CreateEdge(participantB, participantC, 3.01f, true)),
                    string.Empty,
                    out plan);

            Require(
                !planned && plan == null,
                "An ineligible candidate produced a stable merge plan.");
        }

        private static void TestGroupToGroupConflictIsRejected()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");
            VoiceDedicatedGroupParticipant participantD = CreateParticipant("d");

            VoiceDedicatedStableGroupMergePlan plan;
            bool planned = new VoiceDedicatedStableGroupMergePlanner()
                .TryCreatePlan(
                    participantC,
                    new[]
                    {
                        CreateSession("session-ab", participantA, participantB),
                        CreateSession(
                            "session-acd",
                            participantA,
                            participantC,
                            participantD)
                    },
                    CreateGraph(
                        CreateEdge(participantA, participantC, 1.0f, true),
                        CreateEdge(participantB, participantC, 2.0f, true)),
                    string.Empty,
                    out plan);

            Require(
                !planned && plan == null,
                "G.5.4 attempted an undefined group-to-group merge.");
        }

        private static void TestMemberJoinedDeltaContract()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedParticipantPair anchorPair =
                new VoiceDedicatedParticipantPair(
                    participantA.ServerId,
                    participantA.RoomId,
                    participantA.UserId,
                    participantA.ConnectionId,
                    participantB.UserId,
                    participantB.ConnectionId);

            VoiceDedicatedSessionDelta delta =
                VoiceDedicatedSessionDelta.CreateMemberJoined(
                    anchorPair,
                    "11111111-1111-4111-8111-111111111111",
                    participantC.UserId,
                    participantC.ConnectionId,
                    2.25f,
                    1786000000000,
                    "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                    7);

            Require(
                string.Equals(delta.type, "member_joined", StringComparison.Ordinal) &&
                string.Equals(
                    delta.memberUserId,
                    participantC.UserId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    delta.memberConnectionId,
                    participantC.ConnectionId,
                    StringComparison.Ordinal) &&
                delta.reason == (int)VoiceDedicatedSessionReason.ProximityEnter,
                "member_joined delta contract is incomplete.");
        }

        private static VoiceDedicatedGroupParticipant CreateParticipant(string label)
        {
            char connectionCharacter = label[0];

            return new VoiceDedicatedGroupParticipant(
                "server-1",
                "room-1",
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
