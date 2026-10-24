#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedSessionPositionDirectTest
    {
        private const string ServerId = "session-position-test-server";
        private const string RoomId = "session-position-test-room";
        private const string AuthorityEpochId = "session-position-test-epoch";

        [MenuItem("Tools/Network A/Voice/Run G5 SessionPosition Topology Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                RunApproachToSessionScenario();
                Debug.Log("VOICE_G5_SESSION_POSITION_NO_TRANSIENT_PAIR=PASS");
                Debug.Log("VOICE_G5_SESSION_POSITION_JOIN=PASS");

                RunIndependentPairAfterDelayScenario();
                Debug.Log("VOICE_G5_SESSION_MEMBER_PAIR_DELAY=PASS");
                Debug.Log("VOICE_G5_SESSION_MEMBER_INDEPENDENT_PAIR=PASS");
                Debug.Log("VOICE_G5_SESSION_POSITION_PAIR_TO_GROUP=PASS");

                RunFreePairImmediateScenario();
                Debug.Log("VOICE_G5_FREE_PAIR_IMMEDIATE=PASS");
                Debug.Log("VOICE_G5_SESSION_POSITION_TOPOLOGY=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_SESSION_POSITION_TOPOLOGY=FAIL | error=" +
                    exception);
                throw;
            }
        }

        private static void RunApproachToSessionScenario()
        {
            SequenceCounter sequence = new SequenceCounter();
            VoiceDedicatedGroupTopologyRuntime runtime =
                new VoiceDedicatedGroupTopologyRuntime();

            VoiceDedicatedGroupParticipant participantA = CreateParticipant(
                "user-a",
                "11111111111141118111111111111111");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant(
                "user-b",
                "22222222222242228222222222222222");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant(
                "user-c",
                "33333333333343338333333333333333");

            runtime.UpdateParticipantPositions(
                Positions(1000L,
                    Position(participantA, 0.0f, 1000L),
                    Position(participantB, 2.0f, 1000L),
                    Position(participantC, -2.5f, 1000L)),
                1000L);

            IReadOnlyList<VoiceDedicatedSessionDelta> createDeltas =
                runtime.ApplyPairObservations(
                    new[]
                    {
                        Enter(
                            CreatePair(participantA, participantB),
                            "11111111-1111-4111-8111-111111111111",
                            2.0f,
                            1000L)
                    },
                    AuthorityEpochId,
                    sequence.Next);

            Require(
                createDeltas.Count == 1 &&
                createDeltas[0].type == "session_created",
                "A/B did not create the baseline pair Session.");

            float sessionX;
            float sessionY;
            float sessionZ;
            Require(
                runtime.TryGetSessionPosition(
                    createDeltas[0].sessionId,
                    out sessionX,
                    out sessionY,
                    out sessionZ),
                "The baseline Session did not expose SessionPosition.");
            Require(
                Mathf.Abs(sessionX - 1.0f) < 0.0001f,
                "The baseline SessionPosition is not the A/B midpoint.");

            IReadOnlyList<VoiceDedicatedSessionDelta> pendingDeltas =
                runtime.ApplyPairObservations(
                    new[]
                    {
                        Enter(
                            CreatePair(participantA, participantC),
                            "22222222-2222-4222-8222-222222222222",
                            2.5f,
                            1100L)
                    },
                    AuthorityEpochId,
                    sequence.Next);

            Require(
                pendingDeltas.Count == 0 && runtime.ActiveSessionCount == 1,
                "Session member approach created a pair before SessionPosition decision.");

            Require(
                MoveAndTick(runtime, participantA, participantB, participantC, -2.35f, 1500L, sequence).Count == 0,
                "Progress toward SessionPosition created a transient pair.");
            Require(
                MoveAndTick(runtime, participantA, participantB, participantC, -2.2f, 1900L, sequence).Count == 0,
                "Slow progress toward SessionPosition created a transient pair.");
            Require(
                MoveAndTick(runtime, participantA, participantB, participantC, -2.05f, 2300L, sequence).Count == 0,
                "Progress reset did not protect a slow SessionPosition approach.");

            runtime.UpdateParticipantPositions(
                Positions(2600L,
                    Position(participantA, 0.0f, 2600L),
                    Position(participantB, 2.0f, 2600L),
                    Position(participantC, -1.9f, 2600L)),
                2600L);

            IReadOnlyList<VoiceDedicatedSessionDelta> joinDeltas =
                runtime.ApplyPairObservations(
                    Array.Empty<VoiceDedicatedTopologyPairObservation>(),
                    AuthorityEpochId,
                    sequence.Next);

            Require(
                joinDeltas.Count == 1 &&
                joinDeltas[0].type == "member_joined" &&
                joinDeltas[0].memberUserId == participantC.UserId,
                "Candidate reaching SessionPosition did not join directly.");
            Require(
                runtime.ActiveSessionCount == 1 &&
                runtime.ActiveGroupSessionCount == 1,
                "SessionPosition join created an extra Session.");
        }

        private static void RunIndependentPairAfterDelayScenario()
        {
            SequenceCounter sequence = new SequenceCounter();
            VoiceDedicatedGroupTopologyRuntime runtime =
                new VoiceDedicatedGroupTopologyRuntime();

            VoiceDedicatedGroupParticipant participantA = CreateParticipant(
                "user-a2",
                "44444444444444448444444444444444");
            VoiceDedicatedGroupParticipant participantB = CreateParticipant(
                "user-b2",
                "55555555555545558555555555555555");
            VoiceDedicatedGroupParticipant participantC = CreateParticipant(
                "user-c2",
                "66666666666646668666666666666666");

            runtime.UpdateParticipantPositions(
                Positions(1000L,
                    Position(participantA, 0.0f, 1000L),
                    Position(participantB, 2.0f, 1000L),
                    Position(participantC, -2.5f, 1000L)),
                1000L);

            runtime.ApplyPairObservations(
                new[]
                {
                    Enter(
                        CreatePair(participantA, participantB),
                        "33333333-3333-4333-8333-333333333333",
                        2.0f,
                        1000L)
                },
                AuthorityEpochId,
                sequence.Next);

            IReadOnlyList<VoiceDedicatedSessionDelta> beginPending =
                runtime.ApplyPairObservations(
                    new[]
                    {
                        Enter(
                            CreatePair(participantA, participantC),
                            "44444444-4444-4444-8444-444444444444",
                            2.5f,
                            1100L)
                    },
                    AuthorityEpochId,
                    sequence.Next);

            Require(beginPending.Count == 0, "Pending pair opened immediately.");

            Require(
                TickWithoutMovement(runtime, participantA, participantB, participantC, -2.5f, 1400L, sequence).Count == 0,
                "Pending pair opened before one second.");
            Require(
                TickWithoutMovement(runtime, participantA, participantB, participantC, -2.5f, 1800L, sequence).Count == 0,
                "Pending pair opened before one second.");

            IReadOnlyList<VoiceDedicatedSessionDelta> independentPairDeltas =
                TickWithoutMovement(
                    runtime,
                    participantA,
                    participantB,
                    participantC,
                    -2.5f,
                    2200L,
                    sequence);

            Require(
                independentPairDeltas.Count == 1 &&
                independentPairDeltas[0].type == "session_created",
                "Stable outside-center candidate did not get an independent pair after delay.");
            Require(
                runtime.ActiveSessionCount == 2,
                "Independent pair did not coexist with the original Session.");

            runtime.UpdateParticipantPositions(
                Positions(2500L,
                    Position(participantA, 0.0f, 2500L),
                    Position(participantB, 2.0f, 2500L),
                    Position(participantC, -1.8f, 2500L)),
                2500L);

            IReadOnlyList<VoiceDedicatedSessionDelta> mergeDeltas =
                runtime.ApplyPairObservations(
                    Array.Empty<VoiceDedicatedTopologyPairObservation>(),
                    AuthorityEpochId,
                    sequence.Next);

            Require(
                mergeDeltas.Count == 2 &&
                mergeDeltas[0].type == "session_closed" &&
                mergeDeltas[1].type == "member_joined",
                "Independent pair was not closed before SessionPosition group join.");
            Require(
                runtime.ActiveSessionCount == 1 &&
                runtime.ActiveGroupSessionCount == 1,
                "Pair-to-group transition did not end with one group Session.");
        }

        private static void RunFreePairImmediateScenario()
        {
            SequenceCounter sequence = new SequenceCounter();
            VoiceDedicatedGroupTopologyRuntime runtime =
                new VoiceDedicatedGroupTopologyRuntime();

            VoiceDedicatedGroupParticipant participantD = CreateParticipant(
                "user-d",
                "77777777777747778777777777777777");
            VoiceDedicatedGroupParticipant participantE = CreateParticipant(
                "user-e",
                "88888888888848888888888888888888");

            runtime.UpdateParticipantPositions(
                Positions(1000L,
                    Position(participantD, 0.0f, 1000L),
                    Position(participantE, 2.0f, 1000L)),
                1000L);

            IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                runtime.ApplyPairObservations(
                    new[]
                    {
                        Enter(
                            CreatePair(participantD, participantE),
                            "55555555-5555-4555-8555-555555555555",
                            2.0f,
                            1000L)
                    },
                    AuthorityEpochId,
                    sequence.Next);

            Require(
                deltas.Count == 1 && deltas[0].type == "session_created",
                "Two free users did not create a pair immediately.");
        }

        private static IReadOnlyList<VoiceDedicatedSessionDelta> MoveAndTick(
            VoiceDedicatedGroupTopologyRuntime runtime,
            VoiceDedicatedGroupParticipant participantA,
            VoiceDedicatedGroupParticipant participantB,
            VoiceDedicatedGroupParticipant participantC,
            float participantCX,
            long effectiveAtMs,
            SequenceCounter sequence)
        {
            return TickWithoutMovement(
                runtime,
                participantA,
                participantB,
                participantC,
                participantCX,
                effectiveAtMs,
                sequence);
        }

        private static IReadOnlyList<VoiceDedicatedSessionDelta> TickWithoutMovement(
            VoiceDedicatedGroupTopologyRuntime runtime,
            VoiceDedicatedGroupParticipant participantA,
            VoiceDedicatedGroupParticipant participantB,
            VoiceDedicatedGroupParticipant participantC,
            float participantCX,
            long effectiveAtMs,
            SequenceCounter sequence)
        {
            runtime.UpdateParticipantPositions(
                Positions(effectiveAtMs,
                    Position(participantA, 0.0f, effectiveAtMs),
                    Position(participantB, 2.0f, effectiveAtMs),
                    Position(participantC, participantCX, effectiveAtMs)),
                effectiveAtMs);

            return runtime.ApplyPairObservations(
                Array.Empty<VoiceDedicatedTopologyPairObservation>(),
                AuthorityEpochId,
                sequence.Next);
        }

        private static VoiceDedicatedTopologyParticipantPosition[] Positions(
            long effectiveAtMs,
            params VoiceDedicatedTopologyParticipantPosition[] positions)
        {
            return positions;
        }

        private static VoiceDedicatedTopologyParticipantPosition Position(
            VoiceDedicatedGroupParticipant participant,
            float x,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyParticipantPosition(
                participant,
                x,
                0.0f,
                0.0f,
                effectiveAtMs);
        }

        private static VoiceDedicatedTopologyPairObservation Enter(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyPairObservation(
                pair,
                VoiceDedicatedProximityState.Active,
                VoiceDedicatedProximityDecisionType.SessionCreated,
                sessionId,
                distanceMeters,
                effectiveAtMs);
        }

        private static VoiceDedicatedGroupParticipant CreateParticipant(
            string userId,
            string connectionId)
        {
            return new VoiceDedicatedGroupParticipant(
                ServerId,
                RoomId,
                userId,
                connectionId);
        }

        private static VoiceDedicatedParticipantPair CreatePair(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            return new VoiceDedicatedParticipantPair(
                ServerId,
                RoomId,
                first.UserId,
                first.ConnectionId,
                second.UserId,
                second.ConnectionId);
        }

        private sealed class SequenceCounter
        {
            private long value;

            public long Next()
            {
                value += 1L;
                return value;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
