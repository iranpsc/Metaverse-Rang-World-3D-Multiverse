using System;
using Network_A.Voice.Client.MediaV2.Congestion;
using UnityEngine;

namespace Network_A.Voice.Client.Codec.Adaptation
{
    internal static class VoiceOpusAdaptationProbe
    {
        private const string Argument = "--vme2-phase12-adaptation-test";
        private const string NativeArgument = "--vme2-phase12-adaptation-native-test";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Run()
        {
            if (!HasArgument(Argument)) return;

            try
            {
                RecordingTarget target = new RecordingTarget();
                VoiceOpusAdaptationProfile healthy = CreateProfile(
                    VoiceMediaCongestionState.Healthy,
                    40,
                    false,
                    0);
                VoiceOpusAdaptationProfile degraded = CreateProfile(
                    VoiceMediaCongestionState.Degraded,
                    32,
                    true,
                    20);
                VoiceOpusAdaptationProfile congested = CreateProfile(
                    VoiceMediaCongestionState.Congested,
                    28,
                    true,
                    20);
                VoiceOpusAdaptationProfile critical = CreateProfile(
                    VoiceMediaCongestionState.Critical,
                    28,
                    true,
                    20);

                VoiceOpusAdaptationController controller =
                    new VoiceOpusAdaptationController(
                        target,
                        healthy,
                        degraded,
                        congested,
                        critical);

                RequireSkipped(
                    controller,
                    target,
                    VoiceMediaCongestionState.Unknown,
                    0,
                    "unknown");
                RequireApplied(controller, target, healthy, 1);
                RequireSkipped(
                    controller,
                    target,
                    VoiceMediaCongestionState.Healthy,
                    1,
                    "repeated healthy");
                RequireApplied(controller, target, degraded, 2);
                RequireApplied(controller, target, congested, 3);
                RequireApplied(controller, target, critical, 4);

                controller.Reset();
                target.FailNextApply = true;
                RequireFailedApplyDoesNotAdvance(controller, target, healthy, 4);
                RequireApplied(controller, target, healthy, 5);

                Debug.Log(
                    "VME2_PHASE12_ADAPTATION_SYNTHETIC=PASS" +
                    " | healthyProfile=True" +
                    " | degradedProfile=True" +
                    " | congestedProfile=True" +
                    " | criticalProfile=True" +
                    " | duplicateApplySuppressed=True" +
                    " | failedApplyRetry=True" +
                    " | productionProfilesLocked=False" +
                    " | changesProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VME2_PHASE12_ADAPTATION_SYNTHETIC=FAIL" +
                    " | error=" + Safe(exception.Message) +
                    " | changesProductionVoice=False");
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void RunNative()
        {
            if (!HasArgument(NativeArgument)) return;

            try
            {
                using (VoiceNativeOpusCodec codec = new VoiceNativeOpusCodec(40))
                {
                    VoiceNativeOpusEncoderAdaptationTarget target =
                        new VoiceNativeOpusEncoderAdaptationTarget(codec);
                    VoiceOpusAdaptationProfile healthy = CreateProfile(
                        VoiceMediaCongestionState.Healthy,
                        40,
                        false,
                        0);
                    VoiceOpusAdaptationProfile degraded = CreateProfile(
                        VoiceMediaCongestionState.Degraded,
                        32,
                        true,
                        10);
                    VoiceOpusAdaptationProfile congested = CreateProfile(
                        VoiceMediaCongestionState.Congested,
                        28,
                        true,
                        20);
                    VoiceOpusAdaptationProfile critical = CreateProfile(
                        VoiceMediaCongestionState.Critical,
                        28,
                        true,
                        30);
                    VoiceOpusAdaptationController controller =
                        new VoiceOpusAdaptationController(
                            target,
                            healthy,
                            degraded,
                            congested,
                            critical);
                    float[] samples = CreateTestFrame();
                    int encodedProfiles = 0;

                    encodedProfiles += RequireNativeApplied(controller, codec, healthy, samples);
                    RequireNativeSkipped(controller, VoiceMediaCongestionState.Healthy);
                    encodedProfiles += RequireNativeApplied(controller, codec, degraded, samples);
                    encodedProfiles += RequireNativeApplied(controller, codec, congested, samples);
                    encodedProfiles += RequireNativeApplied(controller, codec, critical, samples);
                    controller.Reset();
                    encodedProfiles += RequireNativeApplied(controller, codec, healthy, samples);

                    Debug.Log(
                        "VME2_PHASE12_ADAPTATION_NATIVE=PASS" +
                        " | encoderCtlBitrateAccepted=True" +
                        " | encoderCtlInbandFecAccepted=True" +
                        " | encoderCtlPacketLossAccepted=True" +
                        " | fecDisableAfterRecovery=True" +
                        " | duplicateApplySuppressed=True" +
                        " | encodedProfiles=" + encodedProfiles +
                        " | productionProfilesLocked=False" +
                        " | changesProductionVoice=False");
                }
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VME2_PHASE12_ADAPTATION_NATIVE=FAIL" +
                    " | error=" + Safe(exception.Message) +
                    " | changesProductionVoice=False");
            }
        }

        private static VoiceOpusAdaptationProfile CreateProfile(
            VoiceMediaCongestionState state,
            int bitrateKbps,
            bool inbandFecEnabled,
            int expectedPacketLossPercent)
        {
            return new VoiceOpusAdaptationProfile(
                state,
                bitrateKbps,
                inbandFecEnabled,
                expectedPacketLossPercent);
        }

        private static void RequireApplied(
            VoiceOpusAdaptationController controller,
            RecordingTarget target,
            VoiceOpusAdaptationProfile expectedProfile,
            int expectedApplyCount)
        {
            bool applied = controller.TryApplyState(
                expectedProfile.State,
                out VoiceOpusAdaptationProfile actualProfile);

            if (!applied) throw new InvalidOperationException(expectedProfile.State + " was not applied.");
            if (!ReferenceEquals(actualProfile, expectedProfile)) throw new InvalidOperationException(expectedProfile.State + " selected a different profile.");
            if (!ReferenceEquals(controller.CurrentProfile, expectedProfile)) throw new InvalidOperationException(expectedProfile.State + " was not retained by the controller.");
            if (!ReferenceEquals(target.LastProfile, expectedProfile)) throw new InvalidOperationException(expectedProfile.State + " was not delivered to the target.");
            if (target.ApplyCount != expectedApplyCount) throw new InvalidOperationException(expectedProfile.State + " apply count was invalid.");
        }

        private static void RequireSkipped(
            VoiceOpusAdaptationController controller,
            RecordingTarget target,
            VoiceMediaCongestionState state,
            int expectedApplyCount,
            string stage)
        {
            bool applied = controller.TryApplyState(
                state,
                out VoiceOpusAdaptationProfile appliedProfile);

            if (applied) throw new InvalidOperationException(stage + " unexpectedly applied a profile.");
            if (appliedProfile != null) throw new InvalidOperationException(stage + " returned a profile.");
            if (target.ApplyCount != expectedApplyCount) throw new InvalidOperationException(stage + " changed the apply count.");
        }

        private static void RequireFailedApplyDoesNotAdvance(
            VoiceOpusAdaptationController controller,
            RecordingTarget target,
            VoiceOpusAdaptationProfile profile,
            int expectedApplyCount)
        {
            bool failed = false;
            try
            {
                controller.TryApplyState(
                    profile.State,
                    out VoiceOpusAdaptationProfile ignoredProfile);
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }

            if (!failed) throw new InvalidOperationException("Synthetic target failure was not propagated.");
            if (controller.CurrentState != VoiceMediaCongestionState.Unknown) throw new InvalidOperationException("Failed apply advanced the controller state.");
            if (controller.CurrentProfile != null) throw new InvalidOperationException("Failed apply retained a profile.");
            if (target.ApplyCount != expectedApplyCount) throw new InvalidOperationException("Failed apply changed the successful apply count.");
        }

        private static int RequireNativeApplied(
            VoiceOpusAdaptationController controller,
            VoiceNativeOpusCodec codec,
            VoiceOpusAdaptationProfile expectedProfile,
            float[] samples)
        {
            bool applied = controller.TryApplyState(
                expectedProfile.State,
                out VoiceOpusAdaptationProfile actualProfile);

            if (!applied) throw new InvalidOperationException(expectedProfile.State + " was not applied to the native encoder.");
            if (!ReferenceEquals(actualProfile, expectedProfile)) throw new InvalidOperationException(expectedProfile.State + " selected a different native profile.");
            if (codec.BitrateKbps != expectedProfile.BitrateKbps) throw new InvalidOperationException(expectedProfile.State + " native bitrate was not retained.");
            if (codec.InbandFecEnabled != expectedProfile.InbandFecEnabled) throw new InvalidOperationException(expectedProfile.State + " native FEC state was not retained.");
            if (codec.ExpectedPacketLossPercent != expectedProfile.ExpectedPacketLossPercent) throw new InvalidOperationException(expectedProfile.State + " native packet loss percentage was not retained.");

            byte[] packet = codec.Encode(samples);
            if (packet == null || packet.Length == 0) throw new InvalidOperationException(expectedProfile.State + " did not encode a native Opus packet.");
            return 1;
        }

        private static void RequireNativeSkipped(
            VoiceOpusAdaptationController controller,
            VoiceMediaCongestionState state)
        {
            bool applied = controller.TryApplyState(
                state,
                out VoiceOpusAdaptationProfile profile);

            if (applied) throw new InvalidOperationException(state + " was applied twice to the native encoder.");
            if (profile != null) throw new InvalidOperationException(state + " returned a duplicate native profile.");
        }

        private static float[] CreateTestFrame()
        {
            float[] samples = new float[VoiceNativeOpusCodec.FrameSamples];
            const double frequencyHz = 440d;
            const double amplitude = 0.05d;

            for (int index = 0; index < samples.Length; index++)
            {
                double phase = 2d * Math.PI * frequencyHz * index / VoiceNativeOpusCodec.SampleRate;
                samples[index] = (float)(Math.Sin(phase) * amplitude);
            }

            return samples;
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
            return (value ?? string.Empty)
                .Replace('\n', '_')
                .Replace('\r', '_')
                .Replace('|', '/');
        }

        private sealed class RecordingTarget : IVoiceOpusAdaptationTarget
        {
            public int ApplyCount { get; private set; }
            public VoiceOpusAdaptationProfile LastProfile { get; private set; }
            public bool FailNextApply { get; set; }

            public void ApplyOpusAdaptation(VoiceOpusAdaptationProfile profile)
            {
                if (profile == null) throw new ArgumentNullException(nameof(profile));
                if (FailNextApply)
                {
                    FailNextApply = false;
                    throw new InvalidOperationException("Synthetic target failure.");
                }

                LastProfile = profile;
                ApplyCount++;
            }
        }
    }
}
