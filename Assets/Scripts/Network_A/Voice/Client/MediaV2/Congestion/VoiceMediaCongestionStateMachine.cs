using System;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    public enum VoiceMediaCongestionState
    {
        Unknown = 0,
        Healthy = 1,
        Degraded = 2,
        Congested = 3,
        Critical = 4
    }

    public sealed class VoiceMediaCongestionThresholds
    {
        public VoiceMediaCongestionThresholds(
            int minimumPacketsPerWindow,
            double degradedLossPercent,
            double congestedLossPercent,
            double criticalLossPercent,
            double degradedRttMs,
            double congestedRttMs,
            double criticalRttMs,
            int degradedInFlightPackets,
            int congestedInFlightPackets,
            int criticalInFlightPackets,
            double degradedWriteDurationMs,
            double congestedWriteDurationMs,
            double criticalWriteDurationMs,
            long staleFeedbackMs,
            int worsenSamplesRequired,
            int recoverSamplesRequired)
        {
            if (minimumPacketsPerWindow <= 0) throw new ArgumentOutOfRangeException("minimumPacketsPerWindow");
            RequireAscending(degradedLossPercent, congestedLossPercent, criticalLossPercent, "lossPercent");
            RequireAscending(degradedRttMs, congestedRttMs, criticalRttMs, "rttMs");
            RequireAscending(degradedInFlightPackets, congestedInFlightPackets, criticalInFlightPackets, "inFlightPackets");
            RequireAscending(degradedWriteDurationMs, congestedWriteDurationMs, criticalWriteDurationMs, "writeDurationMs");
            if (staleFeedbackMs <= 0) throw new ArgumentOutOfRangeException("staleFeedbackMs");
            if (worsenSamplesRequired <= 0) throw new ArgumentOutOfRangeException("worsenSamplesRequired");
            if (recoverSamplesRequired <= 0) throw new ArgumentOutOfRangeException("recoverSamplesRequired");

            MinimumPacketsPerWindow = minimumPacketsPerWindow;
            DegradedLossPercent = degradedLossPercent;
            CongestedLossPercent = congestedLossPercent;
            CriticalLossPercent = criticalLossPercent;
            DegradedRttMs = degradedRttMs;
            CongestedRttMs = congestedRttMs;
            CriticalRttMs = criticalRttMs;
            DegradedInFlightPackets = degradedInFlightPackets;
            CongestedInFlightPackets = congestedInFlightPackets;
            CriticalInFlightPackets = criticalInFlightPackets;
            DegradedWriteDurationMs = degradedWriteDurationMs;
            CongestedWriteDurationMs = congestedWriteDurationMs;
            CriticalWriteDurationMs = criticalWriteDurationMs;
            StaleFeedbackMs = staleFeedbackMs;
            WorsenSamplesRequired = worsenSamplesRequired;
            RecoverSamplesRequired = recoverSamplesRequired;
        }

        public int MinimumPacketsPerWindow { get; }
        public double DegradedLossPercent { get; }
        public double CongestedLossPercent { get; }
        public double CriticalLossPercent { get; }
        public double DegradedRttMs { get; }
        public double CongestedRttMs { get; }
        public double CriticalRttMs { get; }
        public int DegradedInFlightPackets { get; }
        public int CongestedInFlightPackets { get; }
        public int CriticalInFlightPackets { get; }
        public double DegradedWriteDurationMs { get; }
        public double CongestedWriteDurationMs { get; }
        public double CriticalWriteDurationMs { get; }
        public long StaleFeedbackMs { get; }
        public int WorsenSamplesRequired { get; }
        public int RecoverSamplesRequired { get; }

        private static void RequireAscending(double first, double second, double third, string name)
        {
            if (first < 0d || second <= first || third <= second) throw new ArgumentOutOfRangeException(name);
        }

        private static void RequireAscending(int first, int second, int third, string name)
        {
            if (first < 0 || second <= first || third <= second) throw new ArgumentOutOfRangeException(name);
        }
    }

    public readonly struct VoiceMediaCongestionObservation
    {
        public VoiceMediaCongestionObservation(
            VoiceMediaCongestionState state,
            VoiceMediaCongestionState candidate,
            bool stateChanged,
            long acknowledgedPacketsInWindow,
            long lostPacketsInWindow,
            double lossPercentInWindow,
            double smoothedRttMs,
            int inFlightPackets,
            double smoothedWriteDurationMs,
            long feedbackAgeMs)
        {
            State = state;
            Candidate = candidate;
            StateChanged = stateChanged;
            AcknowledgedPacketsInWindow = acknowledgedPacketsInWindow;
            LostPacketsInWindow = lostPacketsInWindow;
            LossPercentInWindow = lossPercentInWindow;
            SmoothedRttMs = smoothedRttMs;
            InFlightPackets = inFlightPackets;
            SmoothedWriteDurationMs = smoothedWriteDurationMs;
            FeedbackAgeMs = feedbackAgeMs;
        }

        public VoiceMediaCongestionState State { get; }
        public VoiceMediaCongestionState Candidate { get; }
        public bool StateChanged { get; }
        public long AcknowledgedPacketsInWindow { get; }
        public long LostPacketsInWindow { get; }
        public double LossPercentInWindow { get; }
        public double SmoothedRttMs { get; }
        public int InFlightPackets { get; }
        public double SmoothedWriteDurationMs { get; }
        public long FeedbackAgeMs { get; }
    }

    public sealed class VoiceMediaCongestionStateMachine
    {
        private readonly VoiceMediaCongestionThresholds thresholds;
        private bool hasPreviousSnapshot;
        private long previousAcknowledgedPackets;
        private long previousLostPackets;
        private VoiceMediaCongestionState state = VoiceMediaCongestionState.Unknown;
        private VoiceMediaCongestionState pendingWorseState = VoiceMediaCongestionState.Unknown;
        private int worsenSamples;
        private int recoverSamples;

        public VoiceMediaCongestionStateMachine(VoiceMediaCongestionThresholds thresholds)
        {
            this.thresholds = thresholds ?? throw new ArgumentNullException("thresholds");
        }

        public VoiceMediaCongestionState State => state;

        public VoiceMediaCongestionObservation Observe(VoiceMediaTransportFeedbackSnapshot snapshot)
        {
            long acknowledgedInWindow = 0;
            long lostInWindow = 0;
            double lossPercentInWindow = 0d;

            if (hasPreviousSnapshot)
            {
                acknowledgedInWindow = Math.Max(0L, snapshot.AcknowledgedPackets - previousAcknowledgedPackets);
                lostInWindow = Math.Max(0L, snapshot.FinalizedLostPackets - previousLostPackets);
                long packetTotal = acknowledgedInWindow + lostInWindow;
                lossPercentInWindow = packetTotal <= 0 ? 0d : lostInWindow * 100d / packetTotal;
            }

            previousAcknowledgedPackets = snapshot.AcknowledgedPackets;
            previousLostPackets = snapshot.FinalizedLostPackets;

            if (!hasPreviousSnapshot)
            {
                hasPreviousSnapshot = true;
                return BuildObservation(snapshot, VoiceMediaCongestionState.Unknown, false, acknowledgedInWindow, lostInWindow, lossPercentInWindow);
            }

            VoiceMediaCongestionState candidate = Classify(snapshot, acknowledgedInWindow, lostInWindow, lossPercentInWindow);
            VoiceMediaCongestionState previousState = state;

            if (candidate == VoiceMediaCongestionState.Unknown)
            {
                ResetTransitionCounters();
                return BuildObservation(snapshot, candidate, false, acknowledgedInWindow, lostInWindow, lossPercentInWindow);
            }

            if (state == VoiceMediaCongestionState.Unknown)
            {
                if (candidate == VoiceMediaCongestionState.Healthy)
                {
                    recoverSamples++;
                    if (recoverSamples >= thresholds.RecoverSamplesRequired)
                    {
                        state = VoiceMediaCongestionState.Healthy;
                        ResetTransitionCounters();
                    }
                }
                else
                {
                    TrackWorsening(candidate);
                    if (worsenSamples >= thresholds.WorsenSamplesRequired)
                    {
                        state = pendingWorseState;
                        ResetTransitionCounters();
                    }
                }

                return BuildObservation(snapshot, candidate, state != previousState, acknowledgedInWindow, lostInWindow, lossPercentInWindow);
            }

            if ((int)candidate > (int)state)
            {
                TrackWorsening(candidate);
                recoverSamples = 0;
                if (worsenSamples >= thresholds.WorsenSamplesRequired)
                {
                    state = pendingWorseState;
                    ResetTransitionCounters();
                }
            }
            else if ((int)candidate < (int)state)
            {
                pendingWorseState = VoiceMediaCongestionState.Unknown;
                worsenSamples = 0;
                recoverSamples++;
                if (recoverSamples >= thresholds.RecoverSamplesRequired)
                {
                    state = StepDown(state);
                    recoverSamples = 0;
                }
            }
            else
            {
                ResetTransitionCounters();
            }

            return BuildObservation(snapshot, candidate, state != previousState, acknowledgedInWindow, lostInWindow, lossPercentInWindow);
        }

        private VoiceMediaCongestionState Classify(
            VoiceMediaTransportFeedbackSnapshot snapshot,
            long acknowledgedInWindow,
            long lostInWindow,
            double lossPercentInWindow)
        {
            if (snapshot.FeedbackAgeMs < 0) return VoiceMediaCongestionState.Unknown;
            if (snapshot.FeedbackAgeMs >= thresholds.StaleFeedbackMs) return VoiceMediaCongestionState.Critical;

            long packetTotal = acknowledgedInWindow + lostInWindow;
            if (packetTotal < thresholds.MinimumPacketsPerWindow) return VoiceMediaCongestionState.Unknown;

            VoiceMediaCongestionState result = VoiceMediaCongestionState.Healthy;
            result = Max(result, ClassifyLoss(lossPercentInWindow));
            result = Max(result, ClassifyRtt(snapshot.SmoothedRttMs));
            result = Max(result, ClassifyInFlight(snapshot.InFlightPackets));
            result = Max(result, ClassifyWriteDuration(snapshot.SmoothedWriteDurationMs));
            return result;
        }

        private VoiceMediaCongestionState ClassifyLoss(double value)
        {
            if (value >= thresholds.CriticalLossPercent) return VoiceMediaCongestionState.Critical;
            if (value >= thresholds.CongestedLossPercent) return VoiceMediaCongestionState.Congested;
            if (value >= thresholds.DegradedLossPercent) return VoiceMediaCongestionState.Degraded;
            return VoiceMediaCongestionState.Healthy;
        }

        private VoiceMediaCongestionState ClassifyRtt(double value)
        {
            if (value >= thresholds.CriticalRttMs) return VoiceMediaCongestionState.Critical;
            if (value >= thresholds.CongestedRttMs) return VoiceMediaCongestionState.Congested;
            if (value >= thresholds.DegradedRttMs) return VoiceMediaCongestionState.Degraded;
            return VoiceMediaCongestionState.Healthy;
        }

        private VoiceMediaCongestionState ClassifyInFlight(int value)
        {
            if (value >= thresholds.CriticalInFlightPackets) return VoiceMediaCongestionState.Critical;
            if (value >= thresholds.CongestedInFlightPackets) return VoiceMediaCongestionState.Congested;
            if (value >= thresholds.DegradedInFlightPackets) return VoiceMediaCongestionState.Degraded;
            return VoiceMediaCongestionState.Healthy;
        }

        private VoiceMediaCongestionState ClassifyWriteDuration(double value)
        {
            if (value >= thresholds.CriticalWriteDurationMs) return VoiceMediaCongestionState.Critical;
            if (value >= thresholds.CongestedWriteDurationMs) return VoiceMediaCongestionState.Congested;
            if (value >= thresholds.DegradedWriteDurationMs) return VoiceMediaCongestionState.Degraded;
            return VoiceMediaCongestionState.Healthy;
        }

        private void TrackWorsening(VoiceMediaCongestionState candidate)
        {
            if (pendingWorseState != candidate)
            {
                pendingWorseState = candidate;
                worsenSamples = 1;
                return;
            }
            worsenSamples++;
        }

        private void ResetTransitionCounters()
        {
            pendingWorseState = VoiceMediaCongestionState.Unknown;
            worsenSamples = 0;
            recoverSamples = 0;
        }

        private static VoiceMediaCongestionState StepDown(VoiceMediaCongestionState value)
        {
            if (value <= VoiceMediaCongestionState.Healthy) return VoiceMediaCongestionState.Healthy;
            return (VoiceMediaCongestionState)((int)value - 1);
        }

        private static VoiceMediaCongestionState Max(VoiceMediaCongestionState first, VoiceMediaCongestionState second)
        {
            return (int)first >= (int)second ? first : second;
        }

        private VoiceMediaCongestionObservation BuildObservation(
            VoiceMediaTransportFeedbackSnapshot snapshot,
            VoiceMediaCongestionState candidate,
            bool changed,
            long acknowledgedInWindow,
            long lostInWindow,
            double lossPercentInWindow)
        {
            return new VoiceMediaCongestionObservation(
                state,
                candidate,
                changed,
                acknowledgedInWindow,
                lostInWindow,
                lossPercentInWindow,
                snapshot.SmoothedRttMs,
                snapshot.InFlightPackets,
                snapshot.SmoothedWriteDurationMs,
                snapshot.FeedbackAgeMs);
        }
    }
}
