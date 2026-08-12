using System;
using System.Collections.Generic;
using Network_A.GameServer;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using UnityEngine;
using UnityEngine.SceneManagement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Network_A.Voice.Dedicated
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Network A/Voice/Dedicated/Voice Dedicated Stability Benchmark")]
    public sealed class VoiceDedicatedStabilityBenchmark : MonoBehaviour
    {
        private const int RequiredParticipantCount = 3;
        private const long SetupHoldMilliseconds = 5000;
        private const float StatusLogIntervalSeconds = 1.0f;

        private const float EnterSetupMinimumMeters = 3.01f;
        private const float EnterSetupMaximumMeters = 3.10f;
        private const float ExitSetupMinimumMeters = 3.40f;
        private const float ExitSetupMaximumMeters = 3.49f;

        private const float EnterCaptureMinimumMeters = 2.80f;
        private const float EnterCaptureMaximumMeters = 3.20f;
        private const float ExitCaptureMinimumMeters = 3.30f;
        private const float ExitCaptureMaximumMeters = 3.70f;

        [Header("Manual Benchmark References")]
        [SerializeField] private DedicatedServerRuntime runtime;
        [SerializeField] private DedicatedPlayerRegistry playerRegistry;
        [SerializeField] private DedicatedPlayerStateStore playerStateStore;

        [Header("Manual Benchmark Settings")]
        [SerializeField, Min(1)] private int benchmarkSampleCount = 2400;
        [SerializeField, Min(1)] private int maximumAcceptedStateAgeMs = 15000;
        [SerializeField] private bool configureOnStart = true;

        private readonly List<ResolvedParticipant> resolvedParticipants =
            new List<ResolvedParticipant>(RequiredParticipantCount);
        private readonly List<PairMeasurement> pairMeasurements =
            new List<PairMeasurement>(3);

        private VoiceDedicatedThresholdExcursionTracker enterExcursionTracker;
        private VoiceDedicatedThresholdExcursionTracker exitExcursionTracker;
        private readonly VoiceDedicatedDistanceStatistics enterStatistics =
            new VoiceDedicatedDistanceStatistics();
        private readonly VoiceDedicatedDistanceStatistics exitStatistics =
            new VoiceDedicatedDistanceStatistics();

        private int requiredSampleCount;
        private int capturedSampleCount;
        private int tickRate;
        private long sampleIntervalMs;
        private float sampleIntervalSeconds;
        private float nextSampleAtRealtime;
        private float nextStatusLogAtRealtime;
        private long setupValidSinceMs = -1;
        private string setupSignature = string.Empty;
        private string enterPairKey = string.Empty;
        private string exitPairKey = string.Empty;
        private string isolatedPairKey = string.Empty;
        private string enterPairLabel = string.Empty;
        private string exitPairLabel = string.Empty;
        private string isolatedPairLabel = string.Empty;
        private bool configured;
        private bool captureRunning;
        private bool completed;
        private bool faulted;

        public bool IsConfigured { get { return configured; } }
        public bool IsCaptureRunning { get { return captureRunning; } }
        public bool IsCompleted { get { return completed; } }
        public bool IsFaulted { get { return faulted; } }
        public int CapturedSampleCount { get { return capturedSampleCount; } }
        public string LastFailure { get; private set; }

        //* این تابع در شروع صحنه، ابزار بنچمارک دستی را با مقدار تعیین‌شده در بازرس آماده می‌کند.
        private void Start()
        {
            if (configured || !configureOnStart) return;

            Configure(benchmarkSampleCount);
        }

        //* این تابع ابزار را با تعداد نمونه تعیین‌شده برای همین اجرای سرور آماده می‌کند.
        public void Configure(int benchmarkSampleCount)
        {
            if (benchmarkSampleCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "benchmarkSampleCount",
                    "The benchmark sample count must be greater than zero.");
            }

            if (maximumAcceptedStateAgeMs <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "maximumAcceptedStateAgeMs",
                    "The maximum accepted state age must be greater than zero.");
            }

            requiredSampleCount = benchmarkSampleCount;
            configured = true;
            captureRunning = false;
            completed = false;
            faulted = false;
            LastFailure = string.Empty;
            nextSampleAtRealtime = 0.0f;
            nextStatusLogAtRealtime = 0.0f;
            setupValidSinceMs = -1;

            Debug.Log(
                "VOICE_V3_STABILITY_BENCHMARK_CONFIGURED=PASS" +
                " | requiredUsers=3" +
                " | requiredSampleCount=" + requiredSampleCount +
                " | enterSetupRange=3.01..3.10" +
                " | exitSetupRange=3.40..3.49" +
                " | source=DedicatedPlayerStateStore" +
                " | maxStateAgeMs=" + maximumAcceptedStateAgeMs +
                " | identity=userId+connectionId");
        }

        //* این تابع در هر فریم فقط در زمان نمونه‌برداری واقعی سرور وضعیت سه کاربر را بررسی می‌کند.
        private void Update()
        {
            if (!configured || completed || faulted) return;

            if (!TryResolveRuntimeDependencies()) return;

            if (!runtime.IsRunning)
            {
                LogWaitingStatus("dedicated_runtime_not_running", string.Empty);
                return;
            }

            if (sampleIntervalSeconds <= 0.0f)
            {
                DedicatedServerConfigData config = runtime.GetCurrentConfig();
                if (config == null)
                {
                    LogWaitingStatus("dedicated_runtime_config_missing", string.Empty);
                    return;
                }

                tickRate = Mathf.Max(1, config.tickRate);
                sampleIntervalSeconds = 1.0f / tickRate;
                sampleIntervalMs = Math.Max(
                    1L,
                    (long)Math.Ceiling(1000.0 / tickRate));
                nextSampleAtRealtime = Time.realtimeSinceStartup;

                Debug.Log(
                    "VOICE_V3_STABILITY_BENCHMARK_CLOCK=PASS" +
                    " | tickRate=" + tickRate +
                    " | sampleIntervalMs=" + sampleIntervalMs +
                    " | requiredSampleCount=" + requiredSampleCount);
            }

            if (Time.realtimeSinceStartup < nextSampleAtRealtime) return;
            nextSampleAtRealtime = Time.realtimeSinceStartup + sampleIntervalSeconds;

            try
            {
                SampleCurrentPlayers();
            }
            catch (Exception exception)
            {
                FailBenchmark("benchmark_exception | " + exception);
            }
        }

        //* این تابع وابستگی‌های واقعی سرور اختصاصی را پیدا و مسیر آبجکت بازیکن را در صورت نیاز نصب می‌کند.
        private bool TryResolveRuntimeDependencies()
        {
            if (runtime == null)
            {
                runtime = DedicatedServerRuntime.Instance;
            }

            if (runtime != null &&
                playerRegistry != null &&
                playerStateStore != null)
            {
                return true;
            }

            LogWaitingStatus(
                "manual_benchmark_references_not_ready",
                "runtime=" + (runtime != null) +
                " | playerRegistry=" + (playerRegistry != null) +
                " | playerStateStore=" + (playerStateStore != null));

            return false;
        }

        //* این تابع سه بازیکن معتبر را حل می‌کند و مرحله آماده‌سازی یا ثبت نمونه را ادامه می‌دهد.
        private void SampleCurrentPlayers()
        {
            string resolutionError;
            if (!TryResolveExactlyThreeParticipants(out resolutionError))
            {
                ResetSetupAndCapture("participants_not_ready");
                LogWaitingStatus("participants_not_ready", resolutionError);
                return;
            }

            BuildPairMeasurements();

            if (!captureRunning)
            {
                EvaluateSetup();
                return;
            }

            CaptureSample();
        }

        //* این تابع دقیقاً سه هویت معتبر را در همان سرور و همان روم از داده اقتدار سرور حل می‌کند.
        private bool TryResolveExactlyThreeParticipants(out string error)
        {
            resolvedParticipants.Clear();

            List<DedicatedPlayerSession> sessions = playerRegistry.CreateSnapshot();
            sessions.Sort(CompareSessions);

            for (int index = 0; index < sessions.Count; index += 1)
            {
                DedicatedPlayerSession session = sessions[index];
                if (session == null || !session.IsMirrorLikeReady) continue;

                string serverId = SafeTrim(session.serverId);
                string roomId = SafeTrim(session.roomId);
                string userId = SafeTrim(session.userId);
                string rawConnectionId = SafeTrim(session.connectionId);
                string connectionId = NormalizeConnectionId(rawConnectionId);

                if (serverId.Length == 0 ||
                    roomId.Length == 0 ||
                    userId.Length == 0 ||
                    connectionId.Length != 32)
                {
                    continue;
                }

                DedicatedPlayerStateRecord record =
                    playerStateStore.GetByUserIdInRoom(roomId, userId);

                if (record == null ||
                    !Matches(record.serverId, serverId) ||
                    !Matches(record.roomId, roomId) ||
                    !Matches(record.userId, userId) ||
                    !Matches(record.connectionId, rawConnectionId) ||
                    record.sequence <= 0 ||
                    record.serverTimestampUnixMs <= 0)
                {
                    continue;
                }

                long stateAgeMs =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() -
                    record.serverTimestampUnixMs;

                if (stateAgeMs < -5000 ||
                    stateAgeMs > maximumAcceptedStateAgeMs)
                {
                    continue;
                }

                Vector3 position = record.Position;
                if (!IsFinite(position.x) ||
                    !IsFinite(position.y) ||
                    !IsFinite(position.z))
                {
                    continue;
                }

                resolvedParticipants.Add(
                    new ResolvedParticipant(
                        serverId,
                        roomId,
                        userId,
                        connectionId,
                        position));
            }

            if (resolvedParticipants.Count != RequiredParticipantCount)
            {
                error =
                    "resolved=" + resolvedParticipants.Count +
                    " | registry=" + sessions.Count +
                    " | required=3" +
                    " | source=DedicatedPlayerStateStore";
                return false;
            }

            string expectedServerId = resolvedParticipants[0].ServerId;
            string expectedRoomId = resolvedParticipants[0].RoomId;

            for (int index = 1; index < resolvedParticipants.Count; index += 1)
            {
                if (!string.Equals(
                        resolvedParticipants[index].ServerId,
                        expectedServerId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        resolvedParticipants[index].RoomId,
                        expectedRoomId,
                        StringComparison.Ordinal))
                {
                    error = "three_users_are_not_in_the_same_server_and_room";
                    return false;
                }

                if (string.Equals(
                        resolvedParticipants[index - 1].UserId,
                        resolvedParticipants[index].UserId,
                        StringComparison.Ordinal))
                {
                    error = "duplicate_user_id_detected";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        //* این تابع سه زوج مستقل را از سه هویت معتبر می‌سازد و فاصله اقتدار سرور را اندازه می‌گیرد.
        private void BuildPairMeasurements()
        {
            pairMeasurements.Clear();

            for (int firstIndex = 0;
                 firstIndex < resolvedParticipants.Count;
                 firstIndex += 1)
            {
                for (int secondIndex = firstIndex + 1;
                     secondIndex < resolvedParticipants.Count;
                     secondIndex += 1)
                {
                    ResolvedParticipant first =
                        resolvedParticipants[firstIndex];
                    ResolvedParticipant second =
                        resolvedParticipants[secondIndex];

                    VoiceDedicatedParticipantPair pair =
                        new VoiceDedicatedParticipantPair(
                            first.ServerId,
                            first.RoomId,
                            first.UserId,
                            first.ConnectionId,
                            second.UserId,
                            second.ConnectionId);

                    float distanceMeters = Vector3.Distance(
                        first.AuthoritativePosition,
                        second.AuthoritativePosition);

                    pairMeasurements.Add(
                        new PairMeasurement(pair, distanceMeters));
                }
            }
        }

        //* این تابع چیدمان اولیه دو زوج کنترل و زوج جدا را برای پنج ثانیه پیوسته تأیید می‌کند.
        private void EvaluateSetup()
        {
            PairMeasurement enterPair;
            PairMeasurement exitPair;
            PairMeasurement isolatedPair;

            if (!TrySelectSetupPairs(
                    out enterPair,
                    out exitPair,
                    out isolatedPair))
            {
                setupValidSinceMs = -1;
                setupSignature = string.Empty;
                LogWaitingStatus(
                    "arrange_three_users",
                    BuildDistanceSummary() +
                    " | requiredEnter=3.01..3.10" +
                    " | requiredExit=3.40..3.49" +
                    " | requiredIsolated=>3.50");
                return;
            }

            string currentSignature =
                enterPair.Pair.PairKey + "|" +
                exitPair.Pair.PairKey + "|" +
                isolatedPair.Pair.PairKey;

            long nowMs = ReadMonotonicMilliseconds();

            if (!string.Equals(
                    setupSignature,
                    currentSignature,
                    StringComparison.Ordinal))
            {
                setupSignature = currentSignature;
                setupValidSinceMs = nowMs;
            }

            long heldMilliseconds = Math.Max(0, nowMs - setupValidSinceMs);

            LogWaitingStatus(
                "setup_holding",
                "heldMs=" + heldMilliseconds +
                " | requiredMs=" + SetupHoldMilliseconds +
                " | " + BuildDistanceSummary());

            if (heldMilliseconds < SetupHoldMilliseconds) return;

            StartCapture(enterPair, exitPair, isolatedPair);
        }

        //* این تابع از میان سه زوج، دو زوج کنترل نزدیک مرزهای ورود و خروج را بدون اشتراک زوج انتخاب می‌کند.
        private bool TrySelectSetupPairs(
            out PairMeasurement selectedEnterPair,
            out PairMeasurement selectedExitPair,
            out PairMeasurement selectedIsolatedPair)
        {
            selectedEnterPair = default(PairMeasurement);
            selectedExitPair = default(PairMeasurement);
            selectedIsolatedPair = default(PairMeasurement);

            double bestScore = double.MaxValue;
            bool found = false;

            for (int enterIndex = 0;
                 enterIndex < pairMeasurements.Count;
                 enterIndex += 1)
            {
                PairMeasurement enterPair = pairMeasurements[enterIndex];

                if (enterPair.DistanceMeters < EnterSetupMinimumMeters ||
                    enterPair.DistanceMeters > EnterSetupMaximumMeters)
                {
                    continue;
                }

                for (int exitIndex = 0;
                     exitIndex < pairMeasurements.Count;
                     exitIndex += 1)
                {
                    if (exitIndex == enterIndex) continue;

                    PairMeasurement exitPair = pairMeasurements[exitIndex];

                    if (exitPair.DistanceMeters < ExitSetupMinimumMeters ||
                        exitPair.DistanceMeters > ExitSetupMaximumMeters)
                    {
                        continue;
                    }

                    int isolatedIndex = 3 - enterIndex - exitIndex;
                    if (isolatedIndex < 0 ||
                        isolatedIndex >= pairMeasurements.Count)
                    {
                        continue;
                    }

                    PairMeasurement isolatedPair =
                        pairMeasurements[isolatedIndex];

                    if (isolatedPair.DistanceMeters <=
                        VoiceDedicatedProximityEvaluator.ExitDistanceMeters)
                    {
                        continue;
                    }

                    double score =
                        Math.Abs(enterPair.DistanceMeters - 3.05f) +
                        Math.Abs(exitPair.DistanceMeters - 3.45f);

                    if (score >= bestScore) continue;

                    bestScore = score;
                    selectedEnterPair = enterPair;
                    selectedExitPair = exitPair;
                    selectedIsolatedPair = isolatedPair;
                    found = true;
                }
            }

            return found;
        }

        //* این تابع ثبت نمونه را با هویت ثابت سه زوج و آمار خالی آغاز می‌کند.
        private void StartCapture(
            PairMeasurement enterPair,
            PairMeasurement exitPair,
            PairMeasurement isolatedPair)
        {
            enterPairKey = enterPair.Pair.PairKey;
            exitPairKey = exitPair.Pair.PairKey;
            isolatedPairKey = isolatedPair.Pair.PairKey;
            enterPairLabel = BuildPairLabel(enterPair.Pair);
            exitPairLabel = BuildPairLabel(exitPair.Pair);
            isolatedPairLabel = BuildPairLabel(isolatedPair.Pair);

            enterExcursionTracker =
                new VoiceDedicatedThresholdExcursionTracker(true, 3.0f);
            exitExcursionTracker =
                new VoiceDedicatedThresholdExcursionTracker(false, 3.5f);

            enterStatistics.Reset();
            exitStatistics.Reset();
            capturedSampleCount = 0;
            captureRunning = true;
            nextStatusLogAtRealtime = 0.0f;

            Debug.Log(
                "VOICE_V3_STABILITY_BENCHMARK_CAPTURE_STARTED=PASS" +
                " | enterPair=" + enterPairLabel +
                " | exitPair=" + exitPairLabel +
                " | isolatedPair=" + isolatedPairLabel +
                " | requiredSampleCount=" + requiredSampleCount +
                " | tickRate=" + tickRate +
                " | sampleIntervalMs=" + sampleIntervalMs);
        }

        //* این تابع یک نمونه معتبر را ثبت و پس از رسیدن به تعداد لازم نتیجه را محاسبه می‌کند.
        private void CaptureSample()
        {
            PairMeasurement enterPair;
            PairMeasurement exitPair;
            PairMeasurement isolatedPair;

            if (!TryGetMeasurement(enterPairKey, out enterPair) ||
                !TryGetMeasurement(exitPairKey, out exitPair) ||
                !TryGetMeasurement(isolatedPairKey, out isolatedPair))
            {
                ResetSetupAndCapture("pair_identity_changed");
                LogWaitingStatus(
                    "capture_reset_pair_identity_changed",
                    BuildDistanceSummary());
                return;
            }

            bool captureRangeValid =
                enterPair.DistanceMeters >= EnterCaptureMinimumMeters &&
                enterPair.DistanceMeters <= EnterCaptureMaximumMeters &&
                exitPair.DistanceMeters >= ExitCaptureMinimumMeters &&
                exitPair.DistanceMeters <= ExitCaptureMaximumMeters &&
                isolatedPair.DistanceMeters >
                    VoiceDedicatedProximityEvaluator.ExitDistanceMeters;

            if (!captureRangeValid)
            {
                ResetSetupAndCapture("capture_positions_left_control_ranges");
                LogWaitingStatus(
                    "capture_reset_positions_changed",
                    BuildDistanceSummary());
                return;
            }

            long nowMs = ReadMonotonicMilliseconds();

            enterExcursionTracker.Sample(enterPair.DistanceMeters, nowMs);
            exitExcursionTracker.Sample(exitPair.DistanceMeters, nowMs);
            enterStatistics.Add(enterPair.DistanceMeters);
            exitStatistics.Add(exitPair.DistanceMeters);
            capturedSampleCount += 1;

            if (Time.realtimeSinceStartup >= nextStatusLogAtRealtime)
            {
                nextStatusLogAtRealtime =
                    Time.realtimeSinceStartup + StatusLogIntervalSeconds;

                Debug.Log(
                    "VOICE_V3_STABILITY_BENCHMARK_PROGRESS" +
                    " | captured=" + capturedSampleCount +
                    " | required=" + requiredSampleCount +
                    " | enterDistance=" +
                    enterPair.DistanceMeters.ToString("F4") +
                    " | exitDistance=" +
                    exitPair.DistanceMeters.ToString("F4") +
                    " | isolatedDistance=" +
                    isolatedPair.DistanceMeters.ToString("F4") +
                    " | maxEnterTransientMs=" +
                    enterExcursionTracker.MaxCompletedDurationMs +
                    " | maxExitTransientMs=" +
                    exitExcursionTracker.MaxCompletedDurationMs);
            }

            if (capturedSampleCount < requiredSampleCount ||
                enterExcursionTracker.IsOpen ||
                exitExcursionTracker.IsOpen)
            {
                return;
            }

            CompleteBenchmark();
        }

        //* این تابع آمار نهایی را بررسی و کمترین تأخیر بزرگ‌تر از تمام نوسان‌های مشاهده‌شده را محاسبه می‌کند.
        private void CompleteBenchmark()
        {
            if (enterStatistics.Count < requiredSampleCount ||
                exitStatistics.Count < requiredSampleCount)
            {
                FailBenchmark("insufficient_completed_samples");
                return;
            }

            if (enterStatistics.Mean <=
                VoiceDedicatedProximityEvaluator.EnterDistanceMeters)
            {
                FailBenchmark(
                    "enter_control_mean_not_outside_threshold" +
                    " | mean=" + enterStatistics.Mean.ToString("F6"));
                return;
            }

            if (exitStatistics.Mean >=
                VoiceDedicatedProximityEvaluator.ExitDistanceMeters)
            {
                FailBenchmark(
                    "exit_control_mean_not_inside_threshold" +
                    " | mean=" + exitStatistics.Mean.ToString("F6"));
                return;
            }

            long selectedDelayMs =
                VoiceDedicatedStabilityDelaySelector.SelectDelayMilliseconds(
                    enterExcursionTracker.MaxCompletedDurationMs,
                    exitExcursionTracker.MaxCompletedDurationMs,
                    sampleIntervalMs);

            captureRunning = false;
            completed = true;

            Debug.Log(
                "VOICE_V3_STABILITY_BENCHMARK=PASS" +
                " | samples=" + capturedSampleCount +
                " | tickRate=" + tickRate +
                " | sampleIntervalMs=" + sampleIntervalMs +
                " | enterPair=" + enterPairLabel +
                " | exitPair=" + exitPairLabel +
                " | isolatedPair=" + isolatedPairLabel +
                " | enterMean=" + enterStatistics.Mean.ToString("F6") +
                " | enterMin=" + enterStatistics.Minimum.ToString("F6") +
                " | enterMax=" + enterStatistics.Maximum.ToString("F6") +
                " | enterTransientCount=" +
                enterExcursionTracker.CompletedCount +
                " | maxEnterTransientMs=" +
                enterExcursionTracker.MaxCompletedDurationMs +
                " | exitMean=" + exitStatistics.Mean.ToString("F6") +
                " | exitMin=" + exitStatistics.Minimum.ToString("F6") +
                " | exitMax=" + exitStatistics.Maximum.ToString("F6") +
                " | exitTransientCount=" +
                exitExcursionTracker.CompletedCount +
                " | maxExitTransientMs=" +
                exitExcursionTracker.MaxCompletedDurationMs +
                " | METAVERSE_VOICE_STABILITY_DELAY_MS=" +
                selectedDelayMs);
        }

        //* این تابع یک زوج ثبت‌شده را با کلید کامل هویت از نمونه جاری پیدا می‌کند.
        private bool TryGetMeasurement(
            string pairKey,
            out PairMeasurement measurement)
        {
            for (int index = 0; index < pairMeasurements.Count; index += 1)
            {
                if (!string.Equals(
                        pairMeasurements[index].Pair.PairKey,
                        pairKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                measurement = pairMeasurements[index];
                return true;
            }

            measurement = default(PairMeasurement);
            return false;
        }

        //* این تابع مرحله آماده‌سازی و آمار نیمه‌کاره را برای شروع دوباره پاک می‌کند.
        private void ResetSetupAndCapture(string reason)
        {
            if (captureRunning)
            {
                Debug.LogWarning(
                    "VOICE_V3_STABILITY_BENCHMARK_CAPTURE_RESET" +
                    " | reason=" + reason +
                    " | captured=" + capturedSampleCount);
            }

            captureRunning = false;
            capturedSampleCount = 0;
            setupValidSinceMs = -1;
            setupSignature = string.Empty;
            enterPairKey = string.Empty;
            exitPairKey = string.Empty;
            isolatedPairKey = string.Empty;
            enterPairLabel = string.Empty;
            exitPairLabel = string.Empty;
            isolatedPairLabel = string.Empty;
            enterExcursionTracker = null;
            exitExcursionTracker = null;
            enterStatistics.Reset();
            exitStatistics.Reset();
        }

        //* این تابع فاصله هر سه زوج را برای راهنمایی دقیق چیدمان داخل یک خط لاگ می‌سازد.
        private string BuildDistanceSummary()
        {
            if (pairMeasurements.Count == 0) return "pairs=none";

            string summary = string.Empty;

            for (int index = 0; index < pairMeasurements.Count; index += 1)
            {
                if (index > 0) summary += " ; ";

                summary += BuildPairLabel(pairMeasurements[index].Pair) + "=" +
                           pairMeasurements[index].DistanceMeters.ToString("F4");
            }

            return summary;
        }

        //* این تابع برچسب زوج را فقط از شناسه کاربر و شناسه اتصال همان دو عضو می‌سازد.
        private static string BuildPairLabel(
            VoiceDedicatedParticipantPair pair)
        {
            return pair.FirstUserId + "@" +
                   ShortConnectionId(pair.FirstConnectionId) + "<->" +
                   pair.SecondUserId + "@" +
                   ShortConnectionId(pair.SecondConnectionId);
        }

        //* این تابع بخش کوتاه قابل‌ردیابی شناسه اتصال را برای لاگ برمی‌گرداند.
        private static string ShortConnectionId(string connectionId)
        {
            string normalized = NormalizeConnectionId(connectionId);
            return normalized.Length <= 8
                ? normalized
                : normalized.Substring(0, 8);
        }

        //* این تابع وضعیت انتظار را با فاصله زمانی محدود ثبت می‌کند تا لاگ پر نشود.
        private void LogWaitingStatus(string reason, string details)
        {
            if (Time.realtimeSinceStartup < nextStatusLogAtRealtime) return;

            nextStatusLogAtRealtime =
                Time.realtimeSinceStartup + StatusLogIntervalSeconds;

            Debug.Log(
                "VOICE_V3_STABILITY_BENCHMARK_WAITING" +
                " | reason=" + reason +
                (string.IsNullOrWhiteSpace(details)
                    ? string.Empty
                    : " | " + details));
        }

        //* این تابع شکست قطعی بنچمارک را ثبت و نمونه‌برداری را متوقف می‌کند.
        private void FailBenchmark(string error)
        {
            if (faulted || completed) return;

            faulted = true;
            captureRunning = false;
            LastFailure = string.IsNullOrWhiteSpace(error)
                ? "unknown_stability_benchmark_failure"
                : error.Trim();

            Debug.LogError(
                "VOICE_V3_STABILITY_BENCHMARK=FAIL" +
                " | captured=" + capturedSampleCount +
                " | error=" + LastFailure);
        }

        //* این تابع اسنپ‌شات بازیکنان را برای تولید زوج‌های قطعی به ترتیب ثابت مرتب می‌کند.
        private static int CompareSessions(
            DedicatedPlayerSession first,
            DedicatedPlayerSession second)
        {
            if (ReferenceEquals(first, second)) return 0;
            if (first == null) return 1;
            if (second == null) return -1;

            int serverCompare = string.CompareOrdinal(
                SafeTrim(first.serverId),
                SafeTrim(second.serverId));

            if (serverCompare != 0) return serverCompare;

            int roomCompare = string.CompareOrdinal(
                SafeTrim(first.roomId),
                SafeTrim(second.roomId));

            if (roomCompare != 0) return roomCompare;

            int userCompare = string.CompareOrdinal(
                SafeTrim(first.userId),
                SafeTrim(second.userId));

            if (userCompare != 0) return userCompare;

            return string.CompareOrdinal(
                NormalizeConnectionId(first.connectionId),
                NormalizeConnectionId(second.connectionId));
        }

        //* این تابع زمان یکنواخت را برای اندازه‌گیری مدت نوسان به میلی‌ثانیه تبدیل می‌کند.
        private static long ReadMonotonicMilliseconds()
        {
            double milliseconds =
                Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

            if (milliseconds <= 0.0) return 0;
            if (milliseconds >= long.MaxValue) return long.MaxValue;
            return (long)milliseconds;
        }

        //* این تابع شناسه اتصال را به قالب ثابت سی‌ودو نویسه‌ای تبدیل می‌کند.
        private static string NormalizeConnectionId(string value)
        {
            return SafeTrim(value)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        //* این تابع برابری دقیق دو مقدار هویتی را پس از پاک‌سازی بررسی می‌کند.
        private static bool Matches(string first, string second)
        {
            return string.Equals(
                SafeTrim(first),
                SafeTrim(second),
                StringComparison.Ordinal);
        }

        //* این تابع معتبر بودن مقدار عددی مختصات را بررسی می‌کند.
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        //* این تابع متن هویتی را بدون ساخت مقدار جایگزین پاک‌سازی می‌کند.
        private static string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }

        //* این تابع یک آبجکت صحنه را همراه با آبجکت‌های غیرفعال پیدا می‌کند.
        private static T FindSceneObject<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<T>(
                FindObjectsInactive.Include);
#else
            T[] loadedObjects = Resources.FindObjectsOfTypeAll<T>();

            for (int index = 0; index < loadedObjects.Length; index += 1)
            {
                Component component = loadedObjects[index] as Component;
                if (component == null || !component.gameObject.scene.IsValid())
                {
                    continue;
                }

                return loadedObjects[index];
            }

            return null;
#endif
        }

        //* این تابع توقف پیش از پایان بنچمارک را با تعداد نمونه موجود ثبت می‌کند.
        private void OnDestroy()
        {
            if (!configured || completed || faulted) return;

            Debug.LogWarning(
                "VOICE_V3_STABILITY_BENCHMARK=INTERRUPTED" +
                " | captured=" + capturedSampleCount +
                " | required=" + requiredSampleCount);
        }

        private sealed class ResolvedParticipant
        {
            public string ServerId { get; private set; }
            public string RoomId { get; private set; }
            public string UserId { get; private set; }
            public string ConnectionId { get; private set; }
            public Vector3 AuthoritativePosition { get; private set; }

            //* این سازنده هویت معتبر بازیکن و آخرین موقعیت پذیرفته‌شده همان اتصال را کنار هم نگه می‌دارد.
            public ResolvedParticipant(
                string serverId,
                string roomId,
                string userId,
                string connectionId,
                Vector3 authoritativePosition)
            {
                ServerId = serverId;
                RoomId = roomId;
                UserId = userId;
                ConnectionId = connectionId;
                AuthoritativePosition = authoritativePosition;
            }
        }

        private struct PairMeasurement
        {
            public VoiceDedicatedParticipantPair Pair { get; private set; }
            public float DistanceMeters { get; private set; }

            //* این سازنده زوج هویتی و فاصله قطعی همان نمونه را نگه می‌دارد.
            public PairMeasurement(
                VoiceDedicatedParticipantPair pair,
                float distanceMeters)
            {
                Pair = pair;
                DistanceMeters = distanceMeters;
            }
        }
    }

    internal sealed class VoiceDedicatedThresholdExcursionTracker
    {
        private readonly bool activeWhenLessThanOrEqual;
        private readonly float thresholdMeters;
        private long openedAtMs = -1;

        public bool IsOpen { get { return openedAtMs >= 0; } }
        public int CompletedCount { get; private set; }
        public long MaxCompletedDurationMs { get; private set; }

        //* این سازنده جهت عبور و مرز فاصله را برای یک نوع نوسان ذخیره می‌کند.
        public VoiceDedicatedThresholdExcursionTracker(
            bool activeBelowOrEqual,
            float threshold)
        {
            if (float.IsNaN(threshold) ||
                float.IsInfinity(threshold) ||
                threshold < 0.0f)
            {
                throw new ArgumentOutOfRangeException("threshold");
            }

            activeWhenLessThanOrEqual = activeBelowOrEqual;
            thresholdMeters = threshold;
        }

        //* این تابع شروع و پایان یک عبور برگشتی از مرز را با زمان یکنواخت ثبت می‌کند.
        public void Sample(float distanceMeters, long monotonicMs)
        {
            if (float.IsNaN(distanceMeters) ||
                float.IsInfinity(distanceMeters) ||
                distanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException("distanceMeters");
            }

            if (monotonicMs < 0)
            {
                throw new ArgumentOutOfRangeException("monotonicMs");
            }

            bool excursionActive = activeWhenLessThanOrEqual
                ? distanceMeters <= thresholdMeters
                : distanceMeters >= thresholdMeters;

            if (excursionActive)
            {
                if (openedAtMs < 0) openedAtMs = monotonicMs;
                return;
            }

            if (openedAtMs < 0) return;

            long durationMs = Math.Max(0, monotonicMs - openedAtMs);
            if (durationMs > MaxCompletedDurationMs)
            {
                MaxCompletedDurationMs = durationMs;
            }

            CompletedCount += 1;
            openedAtMs = -1;
        }
    }

    internal sealed class VoiceDedicatedDistanceStatistics
    {
        private double sum;

        public int Count { get; private set; }
        public float Minimum { get; private set; }
        public float Maximum { get; private set; }
        public double Mean { get { return Count == 0 ? 0.0 : sum / Count; } }

        //* این تابع آمار فاصله را برای یک اجرای تازه پاک می‌کند.
        public void Reset()
        {
            sum = 0.0;
            Count = 0;
            Minimum = 0.0f;
            Maximum = 0.0f;
        }

        //* این تابع یک فاصله معتبر را بدون نگهداری آرایه نمونه‌ها به آمار اضافه می‌کند.
        public void Add(float distanceMeters)
        {
            if (float.IsNaN(distanceMeters) ||
                float.IsInfinity(distanceMeters) ||
                distanceMeters < 0.0f)
            {
                throw new ArgumentOutOfRangeException("distanceMeters");
            }

            if (Count == 0)
            {
                Minimum = distanceMeters;
                Maximum = distanceMeters;
            }
            else
            {
                Minimum = Math.Min(Minimum, distanceMeters);
                Maximum = Math.Max(Maximum, distanceMeters);
            }

            sum += distanceMeters;
            Count += 1;
        }
    }

    internal static class VoiceDedicatedStabilityDelaySelector
    {
        //* این تابع کمترین مضرب تیک را که یک تیک از بلندترین نوسان بزرگ‌تر است محاسبه می‌کند.
        public static long SelectDelayMilliseconds(
            long maxEnterTransientMs,
            long maxExitTransientMs,
            long sampleIntervalMs)
        {
            if (maxEnterTransientMs < 0)
            {
                throw new ArgumentOutOfRangeException("maxEnterTransientMs");
            }

            if (maxExitTransientMs < 0)
            {
                throw new ArgumentOutOfRangeException("maxExitTransientMs");
            }

            if (sampleIntervalMs <= 0)
            {
                throw new ArgumentOutOfRangeException("sampleIntervalMs");
            }

            long longestTransientMs = Math.Max(
                maxEnterTransientMs,
                maxExitTransientMs);

            if (longestTransientMs >
                long.MaxValue - sampleIntervalMs)
            {
                throw new OverflowException(
                    "The measured stability delay exceeded the supported range.");
            }

            long guardedDelayMs = longestTransientMs + sampleIntervalMs;
            long remainder = guardedDelayMs % sampleIntervalMs;

            if (remainder == 0) return guardedDelayMs;

            long adjustment = sampleIntervalMs - remainder;
            if (guardedDelayMs > long.MaxValue - adjustment)
            {
                throw new OverflowException(
                    "The rounded stability delay exceeded the supported range.");
            }

            return guardedDelayMs + adjustment;
        }
    }
}

/*
توضیح فایل:
این فایل فقط وقتی اجرا می‌شود که کامپوننت آن به‌صورت دستی روی صحنه سرور اختصاصی قرار گیرد. سه کاربر را با شناسه کاربر و شناسه اتصال در همان سرور و روم تطبیق می‌دهد، فاصله را از آخرین وضعیت پذیرفته‌شده در مخزن سرور می‌خواند، نوسان واقعی فاصله در مرزهای ورود و خروج را اندازه می‌گیرد و مقدار کاندید پایداری را فقط در لاگ گزارش می‌کند.
*/

