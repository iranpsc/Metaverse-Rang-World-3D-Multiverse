#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedProximityEvaluatorDirectTest
    {
        private const long TestStabilityDelayMs = 1000;

        //* این تابع همه سناریوهای مستقیم ارزیاب فاصله را از منوی ادیتور اجرا می‌کند.
        [MenuItem("Tools/Network A/Voice/Run Dedicated Proximity Direct Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                TestBenchmarkDelayIsRequired();
                TestPairIdentityUsesUserIdAndRuntimeScope();
                TestStableEnterAndNonTransitivePairs();
                TestHysteresisAndStableExit();
                TestUnstableEnterCancellation();
                TestUnstableExitCancellation();
                TestSessionIdCannotBeReused();
                TestInvalidAndStaleInputs();

                Debug.Log("VOICE_V3_DEDICATED_PROXIMITY_EVALUATOR=PASS");
                Debug.Log("VOICE_V3_DEDICATED_HYSTERESIS=PASS");
                Debug.Log("VOICE_V3_DEDICATED_STABILITY=PASS");
                Debug.Log("VOICE_V3_DEDICATED_NON_TRANSITIVE_PAIR_GRAPH=PASS");
                Debug.Log("VOICE_V3_DEDICATED_USER_ID_AUTHORITY=PASS");
                Debug.Log("VOICE_V3_DEDICATED_SESSION_ID_REUSE_GUARD=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_V3_DEDICATED_PROXIMITY_EVALUATOR=FAIL | " +
                    exception);

                throw;
            }
        }

        //* این تابع اجباری‌بودن مقدار بنچمارک‌شده زمان پایداری را بررسی می‌کند.
        private static void TestBenchmarkDelayIsRequired()
        {
            bool rejectedZeroDelay = false;

            try
            {
                new VoiceDedicatedProximityEvaluator(0);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedZeroDelay = true;
            }

            Require(rejectedZeroDelay, "The evaluator accepted a zero stability delay.");
        }

        //* این تابع استفاده هم‌زمان از شناسه کاربر و اتصال و محدودبودن کلید زوج به سرور و روم را بررسی می‌کند.
        private static void TestPairIdentityUsesUserIdAndRuntimeScope()
        {
            VoiceDedicatedParticipantPair forwardPair =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "user-b");

            VoiceDedicatedParticipantPair reversePair =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-b",
                    "user-a");

            VoiceDedicatedParticipantPair otherRoomPair =
                CreateTestPair(
                    "server-1",
                    "room-2",
                    "user-a",
                    "user-b");

            VoiceDedicatedParticipantPair otherServerPair =
                CreateTestPair(
                    "server-2",
                    "room-1",
                    "user-a",
                    "user-b");

            VoiceDedicatedParticipantPair otherConnectionPair =
                new VoiceDedicatedParticipantPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "11111111111141118111111111111111",
                    "user-b",
                    "0000000000000000000000000000000b");

            Require(
                string.Equals(
                    forwardPair.PairKey,
                    reversePair.PairKey,
                    StringComparison.Ordinal),
                "Pair key changed when user order changed.");

            Require(
                !string.Equals(
                    forwardPair.PairKey,
                    otherRoomPair.PairKey,
                    StringComparison.Ordinal),
                "Pair key was not scoped to roomId.");

            Require(
                !string.Equals(
                    forwardPair.PairKey,
                    otherServerPair.PairKey,
                    StringComparison.Ordinal),
                "Pair key was not scoped to serverId.");

            Require(
                !string.Equals(
                    forwardPair.PairKey,
                    otherConnectionPair.PairKey,
                    StringComparison.Ordinal),
                "Pair key was not scoped to connectionId.");

            bool rejectedSameUser = false;

            try
            {
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "user-a");
            }
            catch (ArgumentException)
            {
                rejectedSameUser = true;
            }

            Require(rejectedSameUser, "The pair accepted the same userId twice.");
        }

        //* این تابع ورود پایدار دو زوج مستقل و ساخته‌نشدن زوج انتقالی سوم را بررسی می‌کند.
        private static void TestStableEnterAndNonTransitivePairs()
        {
            Queue<string> sessionIds = new Queue<string>(
                new[]
                {
                    "11111111-1111-4111-8111-111111111111",
                    "22222222-2222-4222-8222-222222222222"
                });

            VoiceDedicatedProximityEvaluator evaluator =
                new VoiceDedicatedProximityEvaluator(
                    TestStabilityDelayMs,
                    delegate
                    {
                        return sessionIds.Dequeue();
                    });

            VoiceDedicatedParticipantPair pairAb =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "user-b");

            VoiceDedicatedParticipantPair pairBc =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-b",
                    "user-c");

            VoiceDedicatedParticipantPair pairAc =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "user-c");

            Require(!evaluator.Evaluate(pairAb, 2.9f, 1000, 100000).HasDelta,
                "A-B emitted before stability confirmation.");
            Require(!evaluator.Evaluate(pairBc, 2.8f, 1000, 100000).HasDelta,
                "B-C emitted before stability confirmation.");
            Require(!evaluator.Evaluate(pairAc, 4.0f, 1000, 100000).HasDelta,
                "A-C emitted while outside.");

            VoiceDedicatedProximityDecision abCreated =
                evaluator.Evaluate(pairAb, 3.0f, 2000, 101000);

            VoiceDedicatedProximityDecision bcCreated =
                evaluator.Evaluate(pairBc, 2.7f, 2000, 101000);

            VoiceDedicatedProximityDecision acOutside =
                evaluator.Evaluate(pairAc, 4.0f, 2000, 101000);

            Require(
                abCreated.Type == VoiceDedicatedProximityDecisionType.SessionCreated,
                "Stable A-B was not created.");
            Require(
                bcCreated.Type == VoiceDedicatedProximityDecisionType.SessionCreated,
                "Stable B-C was not created.");
            Require(!acOutside.HasDelta, "A-C was created transitively.");
            Require(evaluator.ActiveSessionCount == 2,
                "Active pair session count must be exactly two.");
        }

        //* این تابع حفظ سشن در ناحیه هیسترزیس و خروج پایدار روی مرز سه و نیم متر را بررسی می‌کند.
        private static void TestHysteresisAndStableExit()
        {
            VoiceDedicatedProximityEvaluator evaluator =
                new VoiceDedicatedProximityEvaluator(
                    TestStabilityDelayMs,
                    delegate
                    {
                        return "33333333-3333-4333-8333-333333333333";
                    });

            VoiceDedicatedParticipantPair pair =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "user-b");

            evaluator.Evaluate(pair, 2.5f, 0, 200000);
            VoiceDedicatedProximityDecision created =
                evaluator.Evaluate(pair, 2.5f, 1000, 201000);

            Require(
                created.Type == VoiceDedicatedProximityDecisionType.SessionCreated,
                "The stable pair session was not created.");

            VoiceDedicatedProximityDecision hysteresisUpdate =
                evaluator.Evaluate(pair, 3.49f, 1100, 201100);

            Require(
                hysteresisUpdate.Type == VoiceDedicatedProximityDecisionType.DistanceUpdated &&
                hysteresisUpdate.State == VoiceDedicatedProximityState.Active,
                "The active session was not kept below the exit threshold.");

            VoiceDedicatedProximityDecision exitPending =
                evaluator.Evaluate(pair, 3.5f, 1200, 201200);

            Require(
                !exitPending.HasDelta &&
                exitPending.State == VoiceDedicatedProximityState.ExitPending,
                "The exact exit threshold did not start stability confirmation.");

            Require(
                !evaluator.Evaluate(pair, 3.7f, 2199, 202199).HasDelta,
                "The session closed before the full stability delay.");

            VoiceDedicatedProximityDecision closed =
                evaluator.Evaluate(pair, 3.6f, 2200, 202200);

            Require(
                closed.Type == VoiceDedicatedProximityDecisionType.SessionClosed &&
                closed.Reason == VoiceDedicatedProximityReason.ProximityExit,
                "The session did not close after stable proximity exit.");
            Require(
                evaluator.ActiveSessionCount == 0 &&
                evaluator.TrackedPairCount == 0,
                "Closed pair state was not cleaned up.");
        }

        //* این تابع لغو نامزد ورود را هنگام شکستن پیوستگی فاصله بررسی می‌کند.
        private static void TestUnstableEnterCancellation()
        {
            VoiceDedicatedProximityEvaluator evaluator =
                new VoiceDedicatedProximityEvaluator(TestStabilityDelayMs);

            VoiceDedicatedParticipantPair pair =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-d",
                    "user-e");

            evaluator.Evaluate(pair, 2.9f, 100, 300100);
            VoiceDedicatedProximityDecision cancelled =
                evaluator.Evaluate(pair, 3.01f, 1099, 301099);

            Require(
                cancelled.State == VoiceDedicatedProximityState.Outside &&
                !cancelled.HasDelta &&
                evaluator.TrackedPairCount == 0,
                "Unstable enter candidate was not cancelled.");
        }

        //* این تابع لغو نامزد خروج را هنگام بازگشت فاصله به زیر مرز خروج بررسی می‌کند.
        private static void TestUnstableExitCancellation()
        {
            VoiceDedicatedProximityEvaluator evaluator =
                new VoiceDedicatedProximityEvaluator(
                    TestStabilityDelayMs,
                    delegate
                    {
                        return "44444444-4444-4444-8444-444444444444";
                    });

            VoiceDedicatedParticipantPair pair =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-b",
                    "user-c");

            evaluator.Evaluate(pair, 2.7f, 0, 400000);
            evaluator.Evaluate(pair, 2.7f, 1000, 401000);
            evaluator.Evaluate(pair, 3.7f, 1100, 401100);

            VoiceDedicatedProximityDecision cancelledExit =
                evaluator.Evaluate(pair, 3.4f, 1500, 401500);

            Require(
                cancelledExit.Type == VoiceDedicatedProximityDecisionType.DistanceUpdated &&
                cancelledExit.State == VoiceDedicatedProximityState.Active &&
                evaluator.ActiveSessionCount == 1,
                "Unstable exit candidate was not restored to active state.");
        }

        //* این تابع غیرقابل‌استفاده‌بودن دوباره شناسه سشن قبلی را بررسی می‌کند.
        private static void TestSessionIdCannotBeReused()
        {
            Queue<string> sessionIds = new Queue<string>(
                new[]
                {
                    "55555555-5555-4555-8555-555555555555",
                    "55555555-5555-4555-8555-555555555555"
                });

            VoiceDedicatedProximityEvaluator evaluator =
                new VoiceDedicatedProximityEvaluator(
                    TestStabilityDelayMs,
                    delegate
                    {
                        return sessionIds.Dequeue();
                    });

            VoiceDedicatedParticipantPair pair =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "user-b");

            evaluator.Evaluate(pair, 2.0f, 0, 500000);
            evaluator.Evaluate(pair, 2.0f, 1000, 501000);
            evaluator.Evaluate(pair, 4.0f, 1100, 501100);
            evaluator.Evaluate(pair, 4.0f, 2100, 502100);
            evaluator.Evaluate(pair, 2.0f, 2200, 502200);

            bool rejectedReusedSessionId = false;

            try
            {
                evaluator.Evaluate(pair, 2.0f, 3200, 503200);
            }
            catch (InvalidOperationException)
            {
                rejectedReusedSessionId = true;
            }

            Require(rejectedReusedSessionId, "A closed sessionId was reused.");
        }

        //* این تابع رد فاصله نامعتبر و زمان یکنواخت عقب‌گردکرده را بررسی می‌کند.
        private static void TestInvalidAndStaleInputs()
        {
            VoiceDedicatedProximityEvaluator evaluator =
                new VoiceDedicatedProximityEvaluator(TestStabilityDelayMs);

            VoiceDedicatedParticipantPair pair =
                CreateTestPair(
                    "server-1",
                    "room-1",
                    "user-a",
                    "user-b");

            bool rejectedInvalidDistance = false;

            try
            {
                evaluator.Evaluate(pair, float.NaN, 100, 600100);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedInvalidDistance = true;
            }

            Require(rejectedInvalidDistance, "Invalid distance was accepted.");

            evaluator.Evaluate(pair, 2.5f, 1000, 601000);
            bool rejectedBackwardClock = false;

            try
            {
                evaluator.Evaluate(pair, 2.5f, 999, 601001);
            }
            catch (InvalidOperationException)
            {
                rejectedBackwardClock = true;
            }

            Require(rejectedBackwardClock, "Backward stability clock was accepted.");
        }

        //* این تابع زوج آزمایشی را با شناسه اتصال قطعی و متفاوت برای هر شناسه کاربر می‌سازد.
        private static VoiceDedicatedParticipantPair CreateTestPair(
            string serverId,
            string roomId,
            string firstUserId,
            string secondUserId)
        {
            return new VoiceDedicatedParticipantPair(
                serverId,
                roomId,
                firstUserId,
                CreateTestConnectionId(firstUserId),
                secondUserId,
                CreateTestConnectionId(secondUserId));
        }

        //* این تابع آخرین نویسه شناسه کاربر آزمایشی را به شناسه اتصال سی‌ودو نویسه‌ای تبدیل می‌کند.
        private static string CreateTestConnectionId(string userId)
        {
            string normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? string.Empty
                : userId.Trim().ToLowerInvariant();

            if (normalizedUserId.Length == 0)
            {
                throw new ArgumentException("Test userId is required.", "userId");
            }

            char suffix = normalizedUserId[normalizedUserId.Length - 1];
            bool isDigit = suffix >= '0' && suffix <= '9';
            bool isLowerHex = suffix >= 'a' && suffix <= 'f';

            if (!isDigit && !isLowerHex)
            {
                throw new ArgumentException(
                    "Test userId must end with a hexadecimal character.",
                    "userId");
            }

            return new string('0', 31) + suffix;
        }

        //* این تابع شرط تست را بررسی و در صورت شکست خطای دقیق تولید می‌کند.
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

/*
توضیح فایل:
این فایل فقط در ادیتور یونیتی کامپایل می‌شود و منطق فاصله، هیسترزیس، پایداری، هویت مبتنی بر شناسه کاربر، عدم انتقال‌پذیری زوج‌ها و جلوگیری از استفاده دوباره شناسه سشن را بدون نیاز به صحنه، اینسپکتور یا بیلد بررسی می‌کند.
*/
#endif
