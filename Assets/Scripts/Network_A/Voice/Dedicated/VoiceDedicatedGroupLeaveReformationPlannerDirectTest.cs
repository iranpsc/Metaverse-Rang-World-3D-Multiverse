#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedGroupLeaveReformationPlannerDirectTest
    {
        [MenuItem("Tools/Network A/Voice/Run G5 Group Leave Reformation Planner Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                TestGroupMemberLeavePreservesStableSession();
                TestNoPairReformationWhenEveryEdgeExited();
                TestOriginalAnchorCanLeave();
                TestEligibleMemberCannotLeave();
                TestPairBaselineClosesAndBurnsThroughDelta();

                Debug.Log("VOICE_G5_5_GROUP_MEMBER_LEAVE_PLAN=PASS");
                Debug.Log("VOICE_G5_5_STABLE_REMAINING_SESSION_PLAN=PASS");
                Debug.Log("VOICE_G5_5_PAIR_REFORMATION_PLAN=PASS");
                Debug.Log("VOICE_G5_5_PAIR_BASELINE_LEAVE_DELTA=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_5_GROUP_LEAVE_REFORMATION=FAIL | " +
                    exception);

                throw;
            }
        }

        private static void TestGroupMemberLeavePreservesStableSession()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedGroupSessionSnapshot session =
                CreateSession(
                    "11111111-1111-4111-8111-111111111111",
                    participantA,
                    participantB,
                    participantC);

            VoiceDedicatedGroupPairEdge edgeAb =
                CreateEdge(participantA, participantB, 2.0f, true);
            VoiceDedicatedGroupPairEdge edgeAc =
                CreateEdge(participantA, participantC, 3.2f, true);
            VoiceDedicatedGroupPairEdge edgeBc =
                CreateEdge(participantB, participantC, 3.6f, false);

            VoiceDedicatedGroupLeaveReformationPlan plan;
            bool planned = new VoiceDedicatedGroupLeaveReformationPlanner()
                .TryCreatePlan(
                    session,
                    participantC,
                    edgeBc.Pair,
                    edgeBc.DistanceMeters,
                    CreateGraph(edgeAb, edgeAc, edgeBc),
                    out plan);

            Require(planned && plan != null, "Group leave plan was not created.");
            Require(
                string.Equals(
                    plan.StableSessionId,
                    session.SessionId,
                    StringComparison.Ordinal),
                "The stable SessionId was not preserved after group leave.");
            Require(
                !plan.ClosesStableSession &&
                plan.RemainingMembers.Count == 2 &&
                plan.PairReformations.Count == 1,
                "Group leave did not preserve AB and reform only AC.");
            Require(
                string.Equals(
                    plan.PairReformations[0].Pair.PairKey,
                    edgeAc.Pair.PairKey,
                    StringComparison.Ordinal) &&
                Math.Abs(
                    plan.PairReformations[0].DistanceMeters -
                    3.2f) < 0.0001f,
                "The still-entered AC edge was not selected for pair reformation.");
        }

        private static void TestNoPairReformationWhenEveryEdgeExited()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedGroupPairEdge edgeAc =
                CreateEdge(participantA, participantC, 3.7f, false);
            VoiceDedicatedGroupPairEdge edgeBc =
                CreateEdge(participantB, participantC, 3.6f, false);

            VoiceDedicatedGroupLeaveReformationPlan plan;
            bool planned = new VoiceDedicatedGroupLeaveReformationPlanner()
                .TryCreatePlan(
                    CreateSession("session-abc", participantA, participantB, participantC),
                    participantC,
                    edgeBc.Pair,
                    edgeBc.DistanceMeters,
                    CreateGraph(
                        CreateEdge(participantA, participantB, 2.0f, true),
                        edgeAc,
                        edgeBc),
                    out plan);

            Require(
                planned &&
                plan != null &&
                plan.PairReformations.Count == 0,
                "A pair was reformed for a member with no entered edge.");
        }

        private static void TestOriginalAnchorCanLeave()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedGroupPairEdge edgeAb =
                CreateEdge(participantA, participantB, 3.6f, false);

            VoiceDedicatedGroupLeaveReformationPlan plan;
            bool planned = new VoiceDedicatedGroupLeaveReformationPlanner()
                .TryCreatePlan(
                    CreateSession("session-abc", participantA, participantB, participantC),
                    participantA,
                    edgeAb.Pair,
                    edgeAb.DistanceMeters,
                    CreateGraph(
                        edgeAb,
                        CreateEdge(participantA, participantC, 3.7f, false),
                        CreateEdge(participantB, participantC, 2.0f, true)),
                    out plan);

            Require(planned && plan != null, "Anchor leave plan was not created.");
            Require(
                plan.RemainingMembers.Count == 2 &&
                plan.RemainingMembers[0].HasSameIdentity(participantB) &&
                plan.RemainingMembers[1].HasSameIdentity(participantC),
                "Original anchor leave did not preserve the remaining BC session.");
        }

        private static void TestEligibleMemberCannotLeave()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            VoiceDedicatedGroupPairEdge edgeBc =
                CreateEdge(participantB, participantC, 2.0f, true);

            VoiceDedicatedGroupLeaveReformationPlan plan;
            bool planned = new VoiceDedicatedGroupLeaveReformationPlanner()
                .TryCreatePlan(
                    CreateSession("session-abc", participantA, participantB, participantC),
                    participantC,
                    edgeBc.Pair,
                    edgeBc.DistanceMeters,
                    CreateGraph(
                        CreateEdge(participantA, participantB, 1.0f, true),
                        CreateEdge(participantA, participantC, 1.5f, true),
                        edgeBc),
                    out plan);

            Require(
                !planned && plan == null,
                "A fully eligible member produced a group leave plan.");
        }

        private static void TestPairBaselineClosesAndBurnsThroughDelta()
        {
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");

            VoiceDedicatedGroupPairEdge edgeAb =
                CreateEdge(participantA, participantB, 3.6f, false);

            VoiceDedicatedGroupLeaveReformationPlan plan;
            bool planned = new VoiceDedicatedGroupLeaveReformationPlanner()
                .TryCreatePlan(
                    CreateSession("session-ab", participantA, participantB),
                    participantB,
                    edgeAb.Pair,
                    edgeAb.DistanceMeters,
                    CreateGraph(edgeAb),
                    out plan);

            Require(
                planned &&
                plan != null &&
                plan.ClosesStableSession &&
                plan.PairReformations.Count == 0,
                "Pair baseline leave did not request full session close.");

            VoiceDedicatedSessionDelta delta =
                VoiceDedicatedSessionDelta.CreateMemberLeft(
                    edgeAb.Pair,
                    "11111111-1111-4111-8111-111111111111",
                    participantB.UserId,
                    edgeAb.DistanceMeters,
                    VoiceDedicatedSessionReason.ProximityExit,
                    1786001000000,
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
                    9);

            Require(
                string.Equals(delta.type, "member_left", StringComparison.Ordinal) &&
                string.Equals(
                    delta.memberUserId,
                    participantB.UserId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    delta.memberConnectionId,
                    participantB.ConnectionId,
                    StringComparison.Ordinal),
                "Pair baseline member_left delta changed unexpectedly.");
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
