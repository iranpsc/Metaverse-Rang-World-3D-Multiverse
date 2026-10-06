using System;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    internal static class VoiceMediaTransportFeedbackProbe
    {
        // این تابع آزمون مصنوعی فاز دوازده را فقط با آرگومان صریح اجرا می کند و به مسیر صدای واقعی متصل نمی شود.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void RunWhenRequested()
        {
            if (!HasArgument("--vme2-phase12-feedback-test")) return;
            try
            {
                RunSynthetic();
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE12_FEEDBACK_SYNTHETIC=FAIL | error=" + exception.Message + " | feedsProductionVoice=False");
            }
        }

        private static void RunSynthetic()
        {
            long clockMs = 1000;
            VoiceMediaTransportFeedbackTracker tracker = new VoiceMediaTransportFeedbackTracker(() => clockMs);

            for (uint sequence = 1; sequence <= 4; sequence++)
            {
                byte[] packet = BuildPacket(sequence, 0, 0).Encode();
                tracker.ObserveOutgoingPacket(packet, 2, true);
                clockMs += 20;
            }

            clockMs += 40;
            uint firstMask = (1u << 0) | (1u << 2);
            tracker.ObserveIncomingPacket(BuildPacket(100, 4, firstMask).Encode());

            for (uint sequence = 5; sequence <= 35; sequence++)
            {
                clockMs += 20;
                tracker.ObserveOutgoingPacket(BuildPacket(sequence, 0, 0).Encode(), 3, true);
            }

            clockMs += 60;
            tracker.ObserveIncomingPacket(BuildPacket(101, 35, uint.MaxValue).Encode());
            VoiceMediaTransportFeedbackSnapshot snapshot = tracker.GetSnapshot();

            bool selectiveAck = snapshot.AcknowledgedPackets == 34;
            bool finalizedLoss = snapshot.FinalizedLostPackets == 1;
            bool rttMeasured = snapshot.LatestRttMs > 0d && snapshot.SmoothedRttMs > 0d;
            bool throughputMeasured = snapshot.AcknowledgedThroughputKbps > 0d;
            bool inFlightCleared = snapshot.InFlightPackets == 0;
            bool pass = selectiveAck && finalizedLoss && rttMeasured && throughputMeasured && inFlightCleared;

            Debug.Log(
                "VME2_PHASE12_FEEDBACK_SYNTHETIC=" + (pass ? "PASS" : "FAIL") +
                " | selectiveAck=" + selectiveAck +
                " | acknowledgedPackets=" + snapshot.AcknowledgedPackets +
                " | finalizedLostPackets=" + snapshot.FinalizedLostPackets +
                " | finalizedLossPercent=" + snapshot.FinalizedLossPercent.ToString("F3") +
                " | latestRttMs=" + snapshot.LatestRttMs.ToString("F3") +
                " | smoothedRttMs=" + snapshot.SmoothedRttMs.ToString("F3") +
                " | throughputKbps=" + snapshot.AcknowledgedThroughputKbps.ToString("F3") +
                " | inFlight=" + snapshot.InFlightPackets +
                " | feedsProductionVoice=False");
        }

        private static VoiceMediaV2Packet BuildPacket(uint transportSequence, uint ackSequence, uint ackMask)
        {
            return new VoiceMediaV2Packet
            {
                Kind = VoiceMediaV2PacketKind.Ping,
                Codec = VoiceMediaV2Codec.None,
                Flags = VoiceMediaV2Flags.None,
                TransportSequence = transportSequence,
                MediaSequence = 0,
                MediaTimestamp100Ns = 0,
                AckTransportSequence = ackSequence,
                AckMask = ackMask,
                SessionId = VoiceMediaV2Constants.EmptyUuid,
                StreamId = "11111111-1111-4111-8111-111111111111",
                SenderId = VoiceMediaV2Constants.EmptyUuid,
                SecurityContextId = 0,
                Payload = Array.Empty<byte>()
            };
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
    }
}
