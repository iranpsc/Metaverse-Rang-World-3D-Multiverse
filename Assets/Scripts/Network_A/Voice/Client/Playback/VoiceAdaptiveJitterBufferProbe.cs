using System;
using System.Collections.Generic;
using Network_A.Voice.Client.Audio;
using Network_A.Voice.Client.Codec;
using UnityEngine;

namespace Network_A.Voice.Client.Playback
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal sealed class VoiceAdaptiveJitterBufferProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase7-jitter-test";
        private const long TimeUnitsPerMillisecond = 10000L;
        private const long FrameDuration100Ns = (long)VoiceAudioContract.FrameDurationMs * TimeUnitsPerMillisecond;

        private readonly struct ArrivalEvent
        {
            // این سازنده شماره بسته و زمان رسیدن شبیه سازی شده آن را برای اجرای سناریوهای نوسان شبکه نگه می دارد.
            public ArrivalEvent(int frameIndex, long arrivalTimestamp100Ns)
            {
                FrameIndex = frameIndex;
                ArrivalTimestamp100Ns = arrivalTimestamp100Ns;
            }

            public int FrameIndex { get; }
            public long ArrivalTimestamp100Ns { get; }
        }

        // این تابع فقط با آرگومان صریح آزمایش فاز هفت، آزمایشگر را در شروع برنامه ایجاد می کند تا اجرای عادی برنامه هیچ هزینه ای از این کد نداشته باشد.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasProbeArgument()) return;
            GameObject root = new GameObject("VME2_Phase7_Adaptive_Jitter_Buffer_Probe");
            DontDestroyOnLoad(root);
            root.AddComponent<VoiceAdaptiveJitterBufferProbe>();
        }

        // این تابع آرگومان های خط فرمان را می خواند و فقط وجود نشانگر اختصاصی فاز هفت را تایید می کند.
        private static bool HasProbeArgument()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++) if (string.Equals(arguments[index], ProbeArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // این تابع همه سناریوهای ضروری فاز هفت را یک بار اجرا می کند و پس از ثبت نتیجه، آزمایشگر دیگر در مسیر رسانه دخالتی ندارد.
        private void Awake()
        {
            Debug.Log("VME2_PHASE7_JITTER_PROBE=START | feedsProductionVoice=False");
            RunStableScenario();
            RunVariableJitterScenario();
            RunReorderScenario();
            RunDuplicateScenario();
            RunBurstRecoveryScenario();
            RunMissingScenario();
            Debug.Log("VME2_PHASE7_JITTER_PROBE=COMPLETE | feedsProductionVoice=False");
        }

        // این تابع رسیدن کاملا منظم بسته ها را آزمایش می کند و انتظار دارد تاخیر هدف در کمترین مقدار باقی بماند و همه فریم ها به ترتیب آزاد شوند.
        private static void RunStableScenario()
        {
            List<VoiceEncodedMediaFrame> frames = BuildEncodedFrames(60);
            List<ArrivalEvent> arrivals = new List<ArrivalEvent>(frames.Count);
            for (int index = 0; index < frames.Count; index++) arrivals.Add(new ArrivalEvent(index, MillisecondsTo100Ns(20 + index * 20)));
            ScenarioResult result = ExecuteScenario(frames, arrivals, -1, false);
            bool pass = result.ReadyFrames == 60 && result.MissingFrames == 0 && result.SequenceValid && result.Snapshot.TargetDelayMs == 40 && result.Snapshot.DuplicateDrops == 0 && result.Snapshot.LateDrops == 0;
            WriteResult("stable", pass, result);
        }

        // این تابع نوسان کنترل شده زمان رسیدن را اعمال می کند و انتظار دارد بافر تاخیر هدف را بدون از دست دادن ترتیب فریم ها افزایش دهد.
        private static void RunVariableJitterScenario()
        {
            List<VoiceEncodedMediaFrame> frames = BuildEncodedFrames(80);
            int[] jitterMs = { 0, 7, -5, 11, -3, 4, -8, 9, -2, 6 };
            List<ArrivalEvent> arrivals = new List<ArrivalEvent>(frames.Count);
            for (int index = 0; index < frames.Count; index++) arrivals.Add(new ArrivalEvent(index, MillisecondsTo100Ns(30 + index * 20 + jitterMs[index % jitterMs.Length])));
            arrivals.Sort((left, right) => left.ArrivalTimestamp100Ns.CompareTo(right.ArrivalTimestamp100Ns));
            ScenarioResult result = ExecuteScenario(frames, arrivals, -1, false);
            bool pass = result.ReadyFrames == 80 && result.MissingFrames == 0 && result.SequenceValid && result.PeakTargetDelayMs > 40 && result.Snapshot.OverflowDrops == 0;
            WriteResult("variable_jitter", pass, result);
        }

        // این تابع رسیدن چند بسته خارج از ترتیب را شبیه سازی می کند و بررسی می کند بافر پیش از زمان پخش، ترتیب اصلی شماره فریم ها را بازسازی کند.
        private static void RunReorderScenario()
        {
            List<VoiceEncodedMediaFrame> frames = BuildEncodedFrames(60);
            List<ArrivalEvent> arrivals = new List<ArrivalEvent>(frames.Count);
            for (int index = 0; index < frames.Count; index++)
            {
                int extraDelayMs = index == 20 || index == 40 ? 24 : 0;
                arrivals.Add(new ArrivalEvent(index, MillisecondsTo100Ns(30 + index * 20 + extraDelayMs)));
            }
            arrivals.Sort((left, right) => left.ArrivalTimestamp100Ns.CompareTo(right.ArrivalTimestamp100Ns));
            ScenarioResult result = ExecuteScenario(frames, arrivals, -1, false);
            bool pass = result.ReadyFrames == 60 && result.MissingFrames == 0 && result.SequenceValid && result.Snapshot.ReorderedFrames >= 2 && result.Snapshot.LateDrops == 0;
            WriteResult("reorder", pass, result);
        }

        // این تابع یک بسته را دوبار وارد می کند و بررسی می کند نسخه دوم بدون تاثیر بر ترتیب یا تعداد فریم های خروجی حذف شود.
        private static void RunDuplicateScenario()
        {
            List<VoiceEncodedMediaFrame> frames = BuildEncodedFrames(50);
            List<ArrivalEvent> arrivals = new List<ArrivalEvent>(frames.Count + 1);
            for (int index = 0; index < frames.Count; index++) arrivals.Add(new ArrivalEvent(index, MillisecondsTo100Ns(25 + index * 20)));
            arrivals.Add(new ArrivalEvent(15, MillisecondsTo100Ns(25 + 15 * 20 + 1)));
            arrivals.Sort((left, right) => left.ArrivalTimestamp100Ns.CompareTo(right.ArrivalTimestamp100Ns));
            ScenarioResult result = ExecuteScenario(frames, arrivals, -1, false);
            bool pass = result.ReadyFrames == 50 && result.MissingFrames == 0 && result.SequenceValid && result.Snapshot.DuplicateDrops == 1;
            WriteResult("duplicate", pass, result);
        }

        // این تابع یک دوره جهش شدید نوسان و سپس بازگشت طولانی به شرایط پایدار را اعمال می کند و بررسی می کند تاخیر هدف ابتدا بالا برود و سپس دوباره کاهش پیدا کند.
        private static void RunBurstRecoveryScenario()
        {
            List<VoiceEncodedMediaFrame> frames = BuildEncodedFrames(180);
            List<ArrivalEvent> arrivals = new List<ArrivalEvent>(frames.Count);
            int[] burstJitterMs = { 35, -15, 28, -10, 40, -12, 30, -8 };
            for (int index = 0; index < frames.Count; index++)
            {
                int jitterMs = index >= 30 && index < 70 ? burstJitterMs[(index - 30) % burstJitterMs.Length] : 0;
                arrivals.Add(new ArrivalEvent(index, MillisecondsTo100Ns(40 + index * 20 + jitterMs)));
            }
            arrivals.Sort((left, right) => left.ArrivalTimestamp100Ns.CompareTo(right.ArrivalTimestamp100Ns));
            ScenarioResult result = ExecuteScenario(frames, arrivals, -1, true);
            bool pass = result.ReadyFrames == 180 && result.MissingFrames == 0 && result.SequenceValid && result.PeakTargetDelayMs >= 80 && result.Snapshot.TargetDelayMs < result.PeakTargetDelayMs;
            WriteResult("burst_recovery", pass, result);
        }

        // این تابع یک بسته را عمدا حذف می کند و بررسی می کند بافر فقط همان فریم را گمشده اعلام کند و هیچ صدای جایگزینی در این فاز تولید نکند.
        private static void RunMissingScenario()
        {
            List<VoiceEncodedMediaFrame> frames = BuildEncodedFrames(50);
            List<ArrivalEvent> arrivals = new List<ArrivalEvent>(frames.Count - 1);
            for (int index = 0; index < frames.Count; index++) if (index != 20) arrivals.Add(new ArrivalEvent(index, MillisecondsTo100Ns(30 + index * 20)));
            ScenarioResult result = ExecuteScenario(frames, arrivals, 20, false);
            bool pass = result.ReadyFrames == 49 && result.MissingFrames == 1 && result.SequenceValid && result.MissingSequenceValid && result.Snapshot.MissingFrames == 1;
            WriteResult("missing", pass, result);
        }

        private readonly struct ScenarioResult
        {
            // این سازنده همه شاخص های نهایی یک سناریوی آزمایشی را برای تصمیم قبول یا رد همان سناریو کنار هم قرار می دهد.
            public ScenarioResult(int readyFrames, int missingFrames, bool sequenceValid, bool missingSequenceValid, int peakTargetDelayMs, VoiceJitterBufferSnapshot snapshot)
            {
                ReadyFrames = readyFrames;
                MissingFrames = missingFrames;
                SequenceValid = sequenceValid;
                MissingSequenceValid = missingSequenceValid;
                PeakTargetDelayMs = peakTargetDelayMs;
                Snapshot = snapshot;
            }

            public int ReadyFrames { get; }
            public int MissingFrames { get; }
            public bool SequenceValid { get; }
            public bool MissingSequenceValid { get; }
            public int PeakTargetDelayMs { get; }
            public VoiceJitterBufferSnapshot Snapshot { get; }
        }

        // این تابع رویدادهای رسیدن را روی یک ساعت یکنواخت شبیه سازی می کند، بافر را در هر گام تخلیه می کند و ترتیب خروجی و گزارش فریم گمشده را می سنجد.
        private static ScenarioResult ExecuteScenario(List<VoiceEncodedMediaFrame> frames, List<ArrivalEvent> arrivals, int expectedMissingIndex, bool allowLongRecovery)
        {
            VoiceAdaptiveJitterBuffer buffer = new VoiceAdaptiveJitterBuffer();
            int arrivalIndex = 0;
            int readyFrames = 0;
            int missingFrames = 0;
            bool sequenceValid = true;
            bool missingSequenceValid = true;
            ulong nextExpectedReadySequence = 0;
            int peakTargetDelayMs = 40;
            long finalArrival = arrivals.Count > 0 ? arrivals[arrivals.Count - 1].ArrivalTimestamp100Ns : 0;
            long simulationEnd = finalArrival + MillisecondsTo100Ns(allowLongRecovery ? 1200 : 500);

            for (long now = 0; now <= simulationEnd; now += MillisecondsTo100Ns(5))
            {
                while (arrivalIndex < arrivals.Count && arrivals[arrivalIndex].ArrivalTimestamp100Ns <= now)
                {
                    ArrivalEvent arrival = arrivals[arrivalIndex++];
                    buffer.Enqueue(frames[arrival.FrameIndex], arrival.ArrivalTimestamp100Ns);
                    peakTargetDelayMs = Math.Max(peakTargetDelayMs, buffer.GetSnapshot().TargetDelayMs);
                }

                while (true)
                {
                    VoiceJitterBufferReadResult read = buffer.TryDequeue(now);
                    if (read.Status == VoiceJitterBufferReadStatus.NotReady) break;
                    if (read.Status == VoiceJitterBufferReadStatus.MissingFrame)
                    {
                        missingFrames++;
                        if (expectedMissingIndex < 0 || read.Sequence != (ulong)expectedMissingIndex) missingSequenceValid = false;
                        if (nextExpectedReadySequence == read.Sequence) nextExpectedReadySequence++;
                        continue;
                    }

                    if (read.Sequence != nextExpectedReadySequence) sequenceValid = false;
                    if (read.Frame.Sequence != read.Sequence || read.Frame.MediaTimestamp100Ns != read.MediaTimestamp100Ns) sequenceValid = false;
                    readyFrames++;
                    nextExpectedReadySequence = read.Sequence + 1UL;
                }
            }

            return new ScenarioResult(readyFrames, missingFrames, sequenceValid, missingSequenceValid, peakTargetDelayMs, buffer.GetSnapshot());
        }

        // این تابع فریم های واقعی فشرده شده فاز شش را از سیگنال مصنوعی می سازد تا بافر فاز هفت با همان نوع بسته رسانه ای واقعی آزمایش شود.
        private static List<VoiceEncodedMediaFrame> BuildEncodedFrames(int frameCount)
        {
            List<VoiceEncodedMediaFrame> frames = new List<VoiceEncodedMediaFrame>(frameCount);
            using (VoiceOpusMediaCodecLayer codec = new VoiceOpusMediaCodecLayer(32))
            {
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    float[] samples = new float[VoiceAudioContract.SamplesPerFrame];
                    int absoluteStart = frameIndex * VoiceAudioContract.SamplesPerFrame;
                    for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++) samples[sampleIndex] = 0.20f * Mathf.Sin(2f * Mathf.PI * 440f * (absoluteStart + sampleIndex) / VoiceAudioContract.SampleRate);
                    ulong timestamp100Ns = checked((ulong)frameIndex * (ulong)FrameDuration100Ns);
                    VoiceScheduledAudioFrame scheduled = new VoiceScheduledAudioFrame((ulong)frameIndex, timestamp100Ns, new ArraySegment<float>(samples));
                    frames.Add(codec.Encode(scheduled));
                }
            }
            return frames;
        }

        // این تابع نتیجه یک سناریو را با همه شاخص های اصلی بافر در یک خط ثبت می کند تا مقایسه آزمایش ها ساده و قابل تکرار باشد.
        private static void WriteResult(string scenario, bool pass, ScenarioResult result)
        {
            VoiceJitterBufferSnapshot snapshot = result.Snapshot;
            Debug.Log("VME2_PHASE7_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | scenario=" + scenario + " | readyFrames=" + result.ReadyFrames + " | missingFrames=" + result.MissingFrames + " | sequenceValid=" + result.SequenceValid + " | missingSequenceValid=" + result.MissingSequenceValid + " | targetDelayMs=" + snapshot.TargetDelayMs + " | peakTargetDelayMs=" + result.PeakTargetDelayMs + " | estimatedJitterMs=" + snapshot.EstimatedJitterMs.ToString("F3") + " | duplicateDrops=" + snapshot.DuplicateDrops + " | lateDrops=" + snapshot.LateDrops + " | reorderedFrames=" + snapshot.ReorderedFrames + " | overflowDrops=" + snapshot.OverflowDrops + " | feedsProductionVoice=False");
        }

        // این تابع مقدار میلی ثانیه را به واحد صد نانوثانیه تبدیل می کند تا همه سناریوها روی یک ساعت یکنواخت و دقیق اجرا شوند.
        private static long MillisecondsTo100Ns(int milliseconds)
        {
            return checked((long)milliseconds * TimeUnitsPerMillisecond);
        }
    }
#endif
}
