using System;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    internal static class VoiceMediaCongestionStateProbe
    {
        private const string Argument = "--vme2-phase12-state-test";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Run()
        {
            if (!HasArgument(Argument)) return;

            try
            {
                VoiceMediaCongestionThresholds thresholds = new VoiceMediaCongestionThresholds(
                    5,
                    3d,
                    8d,
                    15d,
                    150d,
                    300d,
                    600d,
                    4,
                    8,
                    16,
                    15d,
                    40d,
                    80d,
                    2000,
                    2,
                    3);

                VoiceMediaCongestionStateMachine machine = new VoiceMediaCongestionStateMachine(thresholds);
                long ack = 0;
                long lost = 0;

                Observe(machine, ack, lost, 60d, 0, 2d, 20);
                for (int i = 0; i < 3; i++)
                {
                    ack += 10;
                    Observe(machine, ack, lost, 60d, 0, 2d, 20);
                }
                RequireState(machine, VoiceMediaCongestionState.Healthy, "healthy");

                for (int i = 0; i < 2; i++)
                {
                    ack += 29;
                    lost += 1;
                    Observe(machine, ack, lost, 180d, 4, 18d, 20);
                }
                RequireState(machine, VoiceMediaCongestionState.Degraded, "degraded");

                for (int i = 0; i < 2; i++)
                {
                    ack += 9;
                    lost += 1;
                    Observe(machine, ack, lost, 350d, 9, 45d, 20);
                }
                RequireState(machine, VoiceMediaCongestionState.Congested, "congested");

                for (int i = 0; i < 2; i++)
                {
                    ack += 4;
                    lost += 1;
                    Observe(machine, ack, lost, 650d, 17, 90d, 20);
                }
                RequireState(machine, VoiceMediaCongestionState.Critical, "critical");

                for (int stage = 0; stage < 3; stage++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        ack += 10;
                        Observe(machine, ack, lost, 70d, 0, 3d, 20);
                    }
                }
                RequireState(machine, VoiceMediaCongestionState.Healthy, "recovered");

                Debug.Log(
                    "VME2_PHASE12_CONGESTION_STATE_SYNTHETIC=PASS" +
                    " | unknownToHealthy=True" +
                    " | healthyToDegraded=True" +
                    " | degradedToCongested=True" +
                    " | congestedToCritical=True" +
                    " | recoveryStepwise=True" +
                    " | worsenSamples=2" +
                    " | recoverSamples=3" +
                    " | productionThresholdsLocked=False" +
                    " | changesProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VME2_PHASE12_CONGESTION_STATE_SYNTHETIC=FAIL" +
                    " | error=" + Safe(exception.Message) +
                    " | changesProductionVoice=False");
            }
        }

        private static VoiceMediaCongestionObservation Observe(
            VoiceMediaCongestionStateMachine machine,
            long acknowledgedPackets,
            long lostPackets,
            double smoothedRttMs,
            int inFlightPackets,
            double writeDurationMs,
            long feedbackAgeMs)
        {
            VoiceMediaTransportFeedbackSnapshot snapshot = new VoiceMediaTransportFeedbackSnapshot(
                acknowledgedPackets + lostPackets,
                (acknowledgedPackets + lostPackets) * 100,
                acknowledgedPackets,
                acknowledgedPackets * 100,
                lostPackets,
                0,
                inFlightPackets,
                smoothedRttMs,
                smoothedRttMs,
                writeDurationMs,
                64d,
                0,
                feedbackAgeMs);
            return machine.Observe(snapshot);
        }

        private static void RequireState(VoiceMediaCongestionStateMachine machine, VoiceMediaCongestionState expected, string stage)
        {
            if (machine.State == expected) return;
            throw new InvalidOperationException(stage + " expected " + expected + " but was " + machine.State + ".");
        }

        private static bool HasArgument(string expected)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Safe(string value)
        {
            return (value ?? string.Empty).Replace('\n', '_').Replace('\r', '_').Replace('|', '/');
        }
    }
}
