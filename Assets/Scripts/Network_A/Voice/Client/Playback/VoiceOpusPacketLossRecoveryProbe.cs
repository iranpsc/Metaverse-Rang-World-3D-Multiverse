using System;
using System.Collections.Generic;
using Network_A.Voice.Client.Audio;
using Network_A.Voice.Client.Codec;
using UnityEngine;

namespace Network_A.Voice.Client.Playback
{
    internal sealed class VoiceOpusPacketLossRecoveryProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase8-loss-recovery-test";
        private const ulong FrameDuration100Ns = 200000UL;
        private const long ArrivalBase100Ns = 10000000L;

        // این تابع پس از بارگذاری نخستین صحنه فقط در صورت وجود آرگومان آزمایش، نمونه مستقل فاز ۸ را ایجاد می کند و به مسیر صوت اصلی دست نمی زند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasProbeArgument()) return;
            if (FindAnyObjectByType<VoiceOpusPacketLossRecoveryProbe>() != null) return;
            GameObject root = new GameObject("VME2_PHASE8_Loss_Recovery_Probe");
            DontDestroyOnLoad(root);
            root.AddComponent<VoiceOpusPacketLossRecoveryProbe>();
        }

        // این تابع آرگومان خط فرمان را بررسی می کند تا آزمایش فاز ۸ در اجرای معمولی برنامه هیچ فعالیتی نداشته باشد.
        private static bool HasProbeArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index < args.Length; index++) if (string.Equals(args[index], ProbeArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // این تابع همه آزمایش های فاز ۸ را یک بار اجرا می کند و نتیجه هر حالت را جداگانه در گزارش می نویسد.
        private void Awake()
        {
            Debug.Log("VME2_PHASE8_LOSS_RECOVERY_PROBE=START | feedsProductionVoice=False");
            RunForwardErrorCorrectionTest();
            RunPacketLossConcealmentTest();
            RunConsecutivePacketLossConcealmentTest();
            RunJitterBufferIntegrationTest();
            Debug.Log("VME2_PHASE8_LOSS_RECOVERY_PROBE=COMPLETE | feedsProductionVoice=False");
        }

        // این تابع یک فریم میانی را حذف می کند و بررسی می کند که بسته بعدی بتواند فریم گم شده را از اصلاح خطای درون بسته با شباهت بیشتر به مرجع همان فریم بازیابی کند.
        private static void RunForwardErrorCorrectionTest()
        {
            const int frameCount = 40;
            const int missingIndex = 20;
            List<VoiceEncodedMediaFrame> packets = BuildEncodedFrames(frameCount);
            float[] referenceMissing;
            float[] referenceNext;
            using (VoiceOpusPacketLossRecoveryCodec reference = new VoiceOpusPacketLossRecoveryCodec(32, 20))
            {
                referenceMissing = null;
                referenceNext = null;
                for (int index = 0; index < packets.Count; index++)
                {
                    VoiceRecoveredMediaFrame decoded = reference.DecodeNormal(packets[index]);
                    if (index == missingIndex) referenceMissing = decoded.Samples;
                    if (index == missingIndex + 1) referenceNext = decoded.Samples;
                }
            }

            bool pass;
            double distanceToMissing;
            double distanceToNext;
            float peak;
            using (VoiceOpusPacketLossRecoveryCodec recovery = new VoiceOpusPacketLossRecoveryCodec(32, 20))
            {
                for (int index = 0; index < missingIndex; index++) recovery.DecodeNormal(packets[index]);
                VoiceRecoveredMediaFrame recovered = recovery.DecodeForwardErrorCorrection((ulong)missingIndex, (ulong)missingIndex * FrameDuration100Ns, packets[missingIndex + 1]);
                recovery.DecodeNormal(packets[missingIndex + 1]);
                distanceToMissing = MeanAbsoluteDifference(recovered.Samples, referenceMissing);
                distanceToNext = MeanAbsoluteDifference(recovered.Samples, referenceNext);
                peak = MeasurePeak(recovered.Samples);
                pass = recovered.Samples.Length == VoiceAudioContract.SamplesPerFrame && AllFinite(recovered.Samples) && peak > 0.00001f && distanceToMissing < distanceToNext;
            }

            Debug.Log("VME2_PHASE8_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | scenario=fec_single_loss | recoveredSamples=960 | distanceToMissing=" + distanceToMissing.ToString("F6") + " | distanceToNext=" + distanceToNext.ToString("F6") + " | peak=" + peak.ToString("F6") + " | feedsProductionVoice=False");
        }

        // این تابع یک فریم را بدون بسته بعدی قابل استفاده حذف می کند و بررسی می کند که پوشاندن گم شدن بسته دقیقا ۹۶۰ نمونه محدود و معتبر تولید کند.
        private static void RunPacketLossConcealmentTest()
        {
            List<VoiceEncodedMediaFrame> packets = BuildEncodedFrames(20);
            bool pass;
            float peak;
            using (VoiceOpusPacketLossRecoveryCodec recovery = new VoiceOpusPacketLossRecoveryCodec(32, 20))
            {
                for (int index = 0; index < 10; index++) recovery.DecodeNormal(packets[index]);
                VoiceRecoveredMediaFrame concealed = recovery.DecodePacketLossConcealment(10UL, 10UL * FrameDuration100Ns);
                peak = MeasurePeak(concealed.Samples);
                pass = concealed.Samples.Length == VoiceAudioContract.SamplesPerFrame && AllFinite(concealed.Samples) && peak > 0.00001f;
            }

            Debug.Log("VME2_PHASE8_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | scenario=plc_single_loss | recoveredSamples=960 | peak=" + peak.ToString("F6") + " | feedsProductionVoice=False");
        }

        // این تابع دو بسته پیاپی را حذف می کند تا پایداری پوشاندن گم شدن پشت سر هم و سلامت نمونه های هر دو فریم بررسی شود.
        private static void RunConsecutivePacketLossConcealmentTest()
        {
            List<VoiceEncodedMediaFrame> packets = BuildEncodedFrames(20);
            bool pass;
            float firstPeak;
            float secondPeak;
            using (VoiceOpusPacketLossRecoveryCodec recovery = new VoiceOpusPacketLossRecoveryCodec(32, 20))
            {
                for (int index = 0; index < 8; index++) recovery.DecodeNormal(packets[index]);
                VoiceRecoveredMediaFrame first = recovery.DecodePacketLossConcealment(8UL, 8UL * FrameDuration100Ns);
                VoiceRecoveredMediaFrame second = recovery.DecodePacketLossConcealment(9UL, 9UL * FrameDuration100Ns);
                firstPeak = MeasurePeak(first.Samples);
                secondPeak = MeasurePeak(second.Samples);
                pass = first.Samples.Length == VoiceAudioContract.SamplesPerFrame && second.Samples.Length == VoiceAudioContract.SamplesPerFrame && AllFinite(first.Samples) && AllFinite(second.Samples) && firstPeak > 0.00001f && secondPeak > 0.00001f;
            }

            Debug.Log("VME2_PHASE8_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | scenario=plc_consecutive_loss | firstPeak=" + firstPeak.ToString("F6") + " | secondPeak=" + secondPeak.ToString("F6") + " | feedsProductionVoice=False");
        }

        // این تابع خروجی گم شدن فاز ۷ را به بازیابی فاز ۸ متصل می کند و بررسی می کند که فریم بعدی برای اصلاح خطای رو به جلو و سپس بازکردن عادی همان فریم قابل استفاده باشد.
        private static void RunJitterBufferIntegrationTest()
        {
            const int frameCount = 50;
            const int missingIndex = 24;
            List<VoiceEncodedMediaFrame> packets = BuildEncodedFrames(frameCount);
            VoiceAdaptiveJitterBuffer jitterBuffer = new VoiceAdaptiveJitterBuffer();
            for (int index = 0; index < packets.Count; index++)
            {
                if (index == missingIndex) continue;
                long arrival = ArrivalBase100Ns + index * (long)FrameDuration100Ns;
                jitterBuffer.Enqueue(packets[index], arrival);
            }

            int normalFrames = 0;
            int recoveredFrames = 0;
            int missingReports = 0;
            bool sequenceValid = true;
            ulong expectedSequence = 0;
            bool pendingMissing = false;
            ulong pendingSequence = 0;
            ulong pendingTimestamp = 0;
            long now = ArrivalBase100Ns + 20000000L;

            using (VoiceOpusPacketLossRecoveryCodec recovery = new VoiceOpusPacketLossRecoveryCodec(32, 20))
            {
                for (int guard = 0; guard < 200; guard++)
                {
                    VoiceJitterBufferReadResult result = jitterBuffer.TryDequeue(now);
                    if (result.Status == VoiceJitterBufferReadStatus.NotReady) break;
                    if (result.Sequence != expectedSequence) sequenceValid = false;

                    if (result.Status == VoiceJitterBufferReadStatus.MissingFrame)
                    {
                        pendingMissing = true;
                        pendingSequence = result.Sequence;
                        pendingTimestamp = result.MediaTimestamp100Ns;
                        missingReports++;
                        expectedSequence++;
                        continue;
                    }

                    if (pendingMissing)
                    {
                        VoiceRecoveredMediaFrame recovered = recovery.DecodeForwardErrorCorrection(pendingSequence, pendingTimestamp, result.Frame);
                        if (recovered.Sequence != pendingSequence || recovered.MediaTimestamp100Ns != pendingTimestamp || recovered.Samples.Length != VoiceAudioContract.SamplesPerFrame || !AllFinite(recovered.Samples)) sequenceValid = false;
                        recoveredFrames++;
                        pendingMissing = false;
                    }

                    VoiceRecoveredMediaFrame normal = recovery.DecodeNormal(result.Frame);
                    if (normal.Sequence != result.Sequence || normal.MediaTimestamp100Ns != result.MediaTimestamp100Ns || normal.Samples.Length != VoiceAudioContract.SamplesPerFrame || !AllFinite(normal.Samples)) sequenceValid = false;
                    normalFrames++;
                    expectedSequence++;
                }

                if (pendingMissing)
                {
                    VoiceRecoveredMediaFrame concealed = recovery.DecodePacketLossConcealment(pendingSequence, pendingTimestamp);
                    if (concealed.Samples.Length != VoiceAudioContract.SamplesPerFrame || !AllFinite(concealed.Samples)) sequenceValid = false;
                    recoveredFrames++;
                    pendingMissing = false;
                }
            }

            bool pass = sequenceValid && missingReports == 1 && recoveredFrames == 1 && normalFrames == frameCount - 1;
            Debug.Log("VME2_PHASE8_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | scenario=jitter_integration | normalFrames=" + normalFrames + " | recoveredFrames=" + recoveredFrames + " | missingReports=" + missingReports + " | sequenceValid=" + sequenceValid + " | feedsProductionVoice=False");
        }

        // این تابع دنباله ای از فریم های صوتی با تغییر فرکانس میان فریم ها می سازد و همه آن ها را با اصلاح خطای درون بسته فعال فشرده می کند.
        private static List<VoiceEncodedMediaFrame> BuildEncodedFrames(int frameCount)
        {
            List<VoiceEncodedMediaFrame> packets = new List<VoiceEncodedMediaFrame>(frameCount);
            using (VoiceOpusPacketLossRecoveryCodec encoder = new VoiceOpusPacketLossRecoveryCodec(32, 20))
            {
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    float[] samples = BuildFrameSignal(frameIndex);
                    VoiceScheduledAudioFrame frame = new VoiceScheduledAudioFrame((ulong)frameIndex, (ulong)frameIndex * FrameDuration100Ns, new ArraySegment<float>(samples));
                    packets.Add(encoder.Encode(frame));
                }
            }
            return packets;
        }

        // این تابع برای هر شماره فریم یک سیگنال متفاوت اما پیوسته می سازد تا بازیابی فریم قبلی از بازکردن اشتباه فریم بعدی قابل تفکیک باشد.
        private static float[] BuildFrameSignal(int frameIndex)
        {
            float[] samples = new float[VoiceAudioContract.SamplesPerFrame];
            double frequency = frameIndex % 4 == 0 ? 320d : frameIndex % 4 == 1 ? 620d : frameIndex % 4 == 2 ? 980d : 1380d;
            long globalStart = (long)frameIndex * VoiceAudioContract.SamplesPerFrame;
            for (int index = 0; index < samples.Length; index++)
            {
                double phase = 2d * Math.PI * frequency * (globalStart + index) / VoiceAudioContract.SampleRate;
                samples[index] = 0.22f * (float)Math.Sin(phase);
            }
            return samples;
        }

        // این تابع میانگین اختلاف قدر مطلق دو فریم هم اندازه را محاسبه می کند تا شباهت بازیابی اصلاح خطا با مرجع فریم گم شده سنجیده شود.
        private static double MeanAbsoluteDifference(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return double.MaxValue;
            double sum = 0d;
            for (int index = 0; index < left.Length; index++) sum += Math.Abs(left[index] - right[index]);
            return sum / left.Length;
        }

        // این تابع بیشترین قدر مطلق نمونه های یک فریم را اندازه می گیرد تا خروجی کاملا خاموش به عنوان بازیابی موفق پذیرفته نشود.
        private static float MeasurePeak(float[] samples)
        {
            float peak = 0f;
            for (int index = 0; index < samples.Length; index++) peak = Math.Max(peak, Math.Abs(samples[index]));
            return peak;
        }

        // این تابع همه نمونه های یک فریم را از نظر عدد نامعتبر بررسی می کند تا خروجی خراب وارد نتیجه موفق نشود.
        private static bool AllFinite(float[] samples)
        {
            if (samples == null) return false;
            for (int index = 0; index < samples.Length; index++) if (float.IsNaN(samples[index]) || float.IsInfinity(samples[index])) return false;
            return true;
        }
    }
}
