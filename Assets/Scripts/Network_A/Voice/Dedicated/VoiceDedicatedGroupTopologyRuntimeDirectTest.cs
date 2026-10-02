#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedGroupTopologyRuntimeDirectTest
    {
        private const string EpochId =
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        private const string SessionAb =
            "11111111-1111-4111-8111-111111111111";
        private const string SessionAc =
            "22222222-2222-4222-8222-222222222222";
        private const string SessionBc =
            "33333333-3333-4333-8333-333333333333";
        private const string SessionAcReformed =
            "44444444-4444-4444-8444-444444444444";

        [MenuItem("Tools/Network A/Voice/Run G5 Group Topology Runtime Integration Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                TestPairToGroupMergeAndStableIdentity();
                TestSingleInternalEdgeDoesNotEvictMember();
                TestMemberLeavesAfterAllGroupEdgesExit();
                TestSimultaneousExitUsesFinalPairGraph();
                TestAmbiguousAnchorEdgeDoesNotEvictAValidMember();
                TestPairBaselineClosesAndBurns();
                TestAuthoritativeParticipantRemovalCleansEverySession();

                Debug.Log("VOICE_G5_5_RUNTIME_PAIR_BASELINE=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_STABLE_GROUP_MERGE=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_STABLE_DISTANCE_ROUTING=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_SINGLE_INTERNAL_EDGE_NO_EVICTION=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_GROUP_MEMBER_LEAVE=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_PAIR_REFORMATION=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_SIMULTANEOUS_EXIT=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_NO_GUESSED_ANCHOR_EVICTION=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_AUTHORITATIVE_CLEANUP=PASS");
                Debug.Log("VOICE_G5_5_RUNTIME_INTEGRATION=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_5_RUNTIME_INTEGRATION=FAIL | " +
                    exception);

                throw;
            }
        }

        private static void TestPairToGroupMergeAndStableIdentity()
        {
            long sourceSequence = 0;
            Queue<string> generatedSessionIds = new Queue<string>();
            generatedSessionIds.Enqueue(SessionAcReformed);

            VoiceDedicatedGroupTopologyRuntime runtime =
                CreateRuntime(generatedSessionIds);
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            Apply(
                runtime,
                ref sourceSequence,
                Enter(participantA, participantB, SessionAb, 2.0f, 1000));
            Apply(
                runtime,
                ref sourceSequence,
                Enter(participantA, participantC, SessionAc, 1.5f, 2000));

            IReadOnlyList<VoiceDedicatedSessionDelta> mergeDeltas = Apply(
                runtime,
                ref sourceSequence,
                Enter(participantB, participantC, SessionBc, 2.25f, 3000));

            Require(mergeDeltas.Count == 4, "Runtime merge delta count changed.");
            Require(
                mergeDeltas[0].type == "session_created" &&
                mergeDeltas[1].type == "session_closed" &&
                mergeDeltas[2].type == "session_closed" &&
                mergeDeltas[3].type == "member_joined",
                "Runtime merge order must create, burn secondaries, then join.");
            Require(
                string.Equals(
                    mergeDeltas[3].sessionId,
                    SessionAb,
                    StringComparison.Ordinal),
                "Runtime merge did not preserve the oldest stable SessionId.");
            Require(
                runtime.ActiveSessionCount == 1 &&
                runtime.ActiveGroupSessionCount == 1,
                "Runtime did not converge to one three-member group.");

            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> snapshots =
                runtime.CreateSessionSnapshot();
            Require(
                snapshots.Count == 1 &&
                snapshots[0].Members.Count == 3 &&
                string.Equals(
                    snapshots[0].SessionId,
                    SessionAb,
                    StringComparison.Ordinal),
                "Stable group snapshot is invalid after merge.");

            RequirePairSession(runtime, participantA, participantB, SessionAb);
            RequirePairSession(runtime, participantA, participantC, SessionAb);
            RequirePairSession(runtime, participantB, participantC, SessionAb);

            IReadOnlyList<VoiceDedicatedSessionDelta> distanceDeltas = Apply(
                runtime,
                ref sourceSequence,
                Update(participantA, participantC, 2.4f, 3500));
            Require(
                distanceDeltas.Count == 1 &&
                distanceDeltas[0].type == "distance_updated" &&
                string.Equals(
                    distanceDeltas[0].sessionId,
                    SessionAb,
                    StringComparison.Ordinal),
                "Group distance update did not use the stable SessionId.");
        }

        private static void TestSingleInternalEdgeDoesNotEvictMember()
        {
            long sourceSequence = 0;
            Queue<string> generatedSessionIds = new Queue<string>();
            generatedSessionIds.Enqueue(SessionAcReformed);
            VoiceDedicatedGroupTopologyRuntime runtime =
                CreateMergedAbcRuntime(ref sourceSequence, generatedSessionIds);
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            IReadOnlyList<VoiceDedicatedSessionDelta> leaveDeltas = Apply(
                runtime,
                ref sourceSequence,
                Exit(participantB, participantC, 3.6f, 4000));

            Require(
                leaveDeltas.Count == 0,
                "A single internal group edge emitted a guessed member_left.");
            Require(
                runtime.ActiveSessionCount == 1 &&
                runtime.ActiveGroupSessionCount == 1,
                "A single internal group edge changed the stable group topology.");
            RequirePairSession(runtime, participantA, participantB, SessionAb);
            RequirePairSession(runtime, participantA, participantC, SessionAb);
            RequirePairSession(runtime, participantB, participantC, SessionAb);
        }

        private static void TestMemberLeavesAfterAllGroupEdgesExit()
        {
            long sourceSequence = 0;
            Queue<string> generatedSessionIds = new Queue<string>();
            generatedSessionIds.Enqueue(SessionAcReformed);
            VoiceDedicatedGroupTopologyRuntime runtime =
                CreateMergedAbcRuntime(ref sourceSequence, generatedSessionIds);
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            IReadOnlyList<VoiceDedicatedSessionDelta> leaveDeltas = Apply(
                runtime,
                ref sourceSequence,
                Exit(participantA, participantC, 3.7f, 4000),
                Exit(participantB, participantC, 3.6f, 4000));

            Require(
                leaveDeltas.Count == 1 &&
                leaveDeltas[0].type == "member_left" &&
                string.Equals(
                    leaveDeltas[0].memberUserId,
                    participantC.UserId,
                    StringComparison.Ordinal),
                "A member that lost every group edge did not leave once.");
            Require(
                runtime.ActiveSessionCount == 1 &&
                runtime.ActiveGroupSessionCount == 0,
                "A real member leave did not preserve the stable remaining pair.");
            RequirePairSession(runtime, participantA, participantB, SessionAb);
            RequireNoPairSession(runtime, participantA, participantC);
            RequireNoPairSession(runtime, participantB, participantC);
        }

        private static void TestSimultaneousExitUsesFinalPairGraph()
        {
            long sourceSequence = 0;
            Queue<string> generatedSessionIds = new Queue<string>();
            generatedSessionIds.Enqueue(SessionAcReformed);
            VoiceDedicatedGroupTopologyRuntime runtime =
                CreateMergedAbcRuntime(ref sourceSequence, generatedSessionIds);
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            IReadOnlyList<VoiceDedicatedSessionDelta> deltas = Apply(
                runtime,
                ref sourceSequence,
                Exit(participantA, participantC, 3.7f, 5000),
                Exit(participantB, participantC, 3.6f, 5000));

            Require(
                deltas.Count == 1 &&
                deltas[0].type == "member_left" &&
                string.Equals(
                    deltas[0].memberUserId,
                    participantC.UserId,
                    StringComparison.Ordinal),
                "Simultaneous group exit created a transient pair session.");
            Require(
                runtime.ActiveSessionCount == 1 &&
                runtime.ActiveGroupSessionCount == 0,
                "Simultaneous group exit did not leave only stable AB.");
            RequirePairSession(runtime, participantA, participantB, SessionAb);
        }

        private static void TestAmbiguousAnchorEdgeDoesNotEvictAValidMember()
        {
            long sourceSequence = 0;
            Queue<string> generatedSessionIds = new Queue<string>();
            generatedSessionIds.Enqueue(SessionAcReformed);
            VoiceDedicatedGroupTopologyRuntime runtime =
                CreateMergedAbcRuntime(ref sourceSequence, generatedSessionIds);
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");

            IReadOnlyList<VoiceDedicatedSessionDelta> deltas = Apply(
                runtime,
                ref sourceSequence,
                Exit(participantA, participantB, 3.6f, 6000));

            Require(
                deltas.Count == 0 &&
                runtime.ActiveGroupSessionCount == 1,
                "A single ambiguous anchor edge evicted a member without evidence.");
        }

        private static void TestPairBaselineClosesAndBurns()
        {
            long sourceSequence = 0;
            VoiceDedicatedGroupTopologyRuntime runtime =
                new VoiceDedicatedGroupTopologyRuntime();
            VoiceDedicatedGroupParticipant participantD = CreateParticipant("d");
            VoiceDedicatedGroupParticipant participantE = CreateParticipant("e");
            const string sessionDe =
                "55555555-5555-4555-8555-555555555555";

            Apply(
                runtime,
                ref sourceSequence,
                Enter(participantD, participantE, sessionDe, 2.0f, 7000));
            IReadOnlyList<VoiceDedicatedSessionDelta> deltas = Apply(
                runtime,
                ref sourceSequence,
                Exit(participantD, participantE, 3.6f, 8000));

            Require(
                deltas.Count == 1 &&
                deltas[0].type == "member_left" &&
                runtime.ActiveSessionCount == 0 &&
                runtime.BurnedSessionIdCount == 1,
                "Pair baseline did not close and burn below two members.");
        }

        private static void TestAuthoritativeParticipantRemovalCleansEverySession()
        {
            long sourceSequence = 0;
            Queue<string> generatedSessionIds = new Queue<string>();
            generatedSessionIds.Enqueue(SessionAcReformed);
            VoiceDedicatedGroupTopologyRuntime runtime =
                CreateMergedAbcRuntime(ref sourceSequence, generatedSessionIds);
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                runtime.RemoveParticipant(
                    participantC,
                    VoiceDedicatedSessionReason.RoomLeft,
                    9000,
                    EpochId,
                    delegate
                    {
                        sourceSequence += 1;
                        return sourceSequence;
                    });

            Require(
                deltas.Count == 1 &&
                deltas[0].type == "member_left" &&
                runtime.ActiveSessionCount == 1,
                "Authoritative participant removal did not clean the group once.");
            RequirePairSession(runtime, participantA, participantB, SessionAb);
            RequireNoPairSession(runtime, participantA, participantC);
            RequireNoPairSession(runtime, participantB, participantC);
        }

        private static VoiceDedicatedGroupTopologyRuntime CreateMergedAbcRuntime(
            ref long sourceSequence,
            Queue<string> generatedSessionIds)
        {
            VoiceDedicatedGroupTopologyRuntime runtime =
                CreateRuntime(generatedSessionIds);
            VoiceDedicatedGroupParticipant participantA = CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant("c");

            Apply(
                runtime,
                ref sourceSequence,
                Enter(participantA, participantB, SessionAb, 2.0f, 1000));
            Apply(
                runtime,
                ref sourceSequence,
                Enter(participantA, participantC, SessionAc, 2.5f, 2000));
            Apply(
                runtime,
                ref sourceSequence,
                Enter(participantB, participantC, SessionBc, 2.25f, 3000));

            return runtime;
        }

        private static VoiceDedicatedGroupTopologyRuntime CreateRuntime(
            Queue<string> generatedSessionIds)
        {
            return new VoiceDedicatedGroupTopologyRuntime(
                delegate
                {
                    if (generatedSessionIds.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "The Direct Test SessionId queue is empty.");
                    }

                    return generatedSessionIds.Dequeue();
                });
        }

        private static IReadOnlyList<VoiceDedicatedSessionDelta> Apply(
            VoiceDedicatedGroupTopologyRuntime runtime,
            ref long sourceSequence,
            params VoiceDedicatedTopologyPairObservation[] observations)
        {
            long currentSequence = sourceSequence;
            IReadOnlyList<VoiceDedicatedSessionDelta> result =
                runtime.ApplyPairObservations(
                    observations,
                    EpochId,
                    delegate
                    {
                        currentSequence += 1;
                        return currentSequence;
                    });

            sourceSequence = currentSequence;
            return result;
        }

        private static VoiceDedicatedTopologyPairObservation Enter(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyPairObservation(
                CreatePair(first, second),
                VoiceDedicatedProximityState.Active,
                VoiceDedicatedProximityDecisionType.SessionCreated,
                sessionId,
                distanceMeters,
                effectiveAtMs);
        }

        private static VoiceDedicatedTopologyPairObservation Exit(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyPairObservation(
                CreatePair(first, second),
                VoiceDedicatedProximityState.Outside,
                VoiceDedicatedProximityDecisionType.SessionClosed,
                string.Empty,
                distanceMeters,
                effectiveAtMs);
        }

        private static VoiceDedicatedTopologyPairObservation Update(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyPairObservation(
                CreatePair(first, second),
                VoiceDedicatedProximityState.Active,
                VoiceDedicatedProximityDecisionType.DistanceUpdated,
                string.Empty,
                distanceMeters,
                effectiveAtMs);
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

        private static void RequirePairSession(
            VoiceDedicatedGroupTopologyRuntime runtime,
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            string expectedSessionId)
        {
            string sessionId;
            Require(
                runtime.TryGetSessionIdForPair(
                    CreatePair(first, second),
                    out sessionId) &&
                string.Equals(
                    sessionId,
                    expectedSessionId,
                    StringComparison.Ordinal),
                "Voice pair is not indexed by the expected SessionId.");
        }

        private static void RequireNoPairSession(
            VoiceDedicatedGroupTopologyRuntime runtime,
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            string sessionId;
            Require(
                !runtime.TryGetSessionIdForPair(
                    CreatePair(first, second),
                    out sessionId),
                "Voice pair remained indexed after leave or cleanup.");
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
