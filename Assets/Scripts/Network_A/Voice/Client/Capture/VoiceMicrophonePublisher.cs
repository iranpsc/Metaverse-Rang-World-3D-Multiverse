using System;
using System.IO;
using Network_A.Voice.Client.Codec;
using UnityEngine;

namespace Network_A.Voice.Client.Capture
{
    public sealed class VoiceMicrophonePublisher : MonoBehaviour
    {
        private const int ClipSeconds = 1;
        private const int CaptureMaxAgeMs = 120;
        private const int CaptureMaxLagSamples =
            VoiceNativeOpusCodec.SampleRate * CaptureMaxAgeMs / 1000;

        // Diagnostic only: keep a local copy of the exact PCM read from Unity
        // before Opus encoding. This copy is never fed back into the live path.
        private const int RawDiagnosticMaxSeconds = 90;
        private const int RawDiagnosticMaxBytes =
            VoiceNativeOpusCodec.SampleRate *
            sizeof(short) *
            RawDiagnosticMaxSeconds;

        private VoiceNativeOpusCodec codec;
        private AudioClip microphoneClip;
        private string microphoneDevice;
        private int readPosition;
        private bool muted = true;
        private float[] frame = new float[VoiceNativeOpusCodec.FrameSamples];
        private float[] interleavedFrame;
        private int encodedFrameCount;
        private float levelRmsSum;
        private float levelPeakMax;
        private int maxCaptureLagSamplesSinceLog;
        private int captureStaleDropCount;
        private long captureDroppedSamples;

        private MemoryStream rawDiagnosticPcmStream;
        private byte[] rawDiagnosticPcm16Frame =
            new byte[VoiceNativeOpusCodec.FrameSamples * sizeof(short)];
        private string rawDiagnosticCaptureId = string.Empty;
        private long rawDiagnosticSamples;

        public event Action<byte[], bool> FrameEncoded;
        public event Action<bool> MuteChanged;
        public event Action<string> Failed;

        public bool IsMuted { get { return muted; } }

        //* این تابع Codec را با Bitrate انتخاب‌شده آماده می‌کند.
        public void Initialize(int bitrateKbps)
        {
            codec?.Dispose();
            codec = new VoiceNativeOpusCodec(bitrateKbps);
        }

        //* این تابع Mute را اعمال و هنگام Mute واقعی Capture میکروفن را کامل متوقف می‌کند.
        public void SetMuted(bool value)
        {
            if (muted == value) return;
            muted = value;

            if (muted) StopCapture();
            else StartCapture();

            MuteChanged?.Invoke(muted);
        }

        private void Update()
        {
            if (muted || microphoneClip == null || codec == null) return;

            int writePosition = Microphone.GetPosition(microphoneDevice);
            if (writePosition < 0) return;

            int clipSamples = microphoneClip.samples;
            if (clipSamples <= 0) return;

            int available = CircularDistance(
                readPosition,
                writePosition,
                clipSamples);

            if (available > maxCaptureLagSamplesSinceLog)
                maxCaptureLagSamplesSinceLog = available;

            // Voice is a live stream. If Unity's microphone read head falls behind
            // the current capture head, old PCM must not be encoded later.
            // Keep only the latest complete 20 ms frame and drop stale backlog.
            if (available > CaptureMaxLagSamples)
            {
                int previousReadPosition = readPosition;
                int latestFrameStart =
                    writePosition - VoiceNativeOpusCodec.FrameSamples;

                if (latestFrameStart < 0)
                    latestFrameStart += clipSamples;

                int droppedSamples = CircularDistance(
                    previousReadPosition,
                    latestFrameStart,
                    clipSamples);

                readPosition = latestFrameStart;
                available = VoiceNativeOpusCodec.FrameSamples;
                captureStaleDropCount += 1;
                captureDroppedSamples += droppedSamples;

                Debug.LogWarning(
                    "VOICE_CLIENT_MIC_CAPTURE_STALE_DROP=PASS" +
                    " | device=" + SafeDeviceName(microphoneDevice) +
                    " | writePosition=" + writePosition +
                    " | previousReadPosition=" + previousReadPosition +
                    " | newReadPosition=" + readPosition +
                    " | lagBeforeMs=" + SamplesToMilliseconds(
                        CircularDistance(
                            previousReadPosition,
                            writePosition,
                            clipSamples)) +
                    " | lagAfterMs=" + SamplesToMilliseconds(available) +
                    " | droppedSamples=" + droppedSamples +
                    " | droppedMs=" + SamplesToMilliseconds(droppedSamples) +
                    " | totalStaleDrops=" + captureStaleDropCount +
                    " | totalDroppedSamples=" + captureDroppedSamples);
            }

            while (available >= VoiceNativeOpusCodec.FrameSamples)
            {
                ReadFrameFromClip(readPosition, frame);
                readPosition =
                    (readPosition + VoiceNativeOpusCodec.FrameSamples) %
                    clipSamples;

                available -= VoiceNativeOpusCodec.FrameSamples;

                try
                {
                    float rms;
                    float peak;
                    MeasureFrameLevel(frame, out rms, out peak);

                    // Diagnostic copy only. The original frame is not modified.
                    AppendRawDiagnosticFrame(frame);

                    byte[] packet = codec.Encode(frame);
                    bool dtx = packet.Length <= 3;

                    encodedFrameCount += 1;
                    levelRmsSum += rms;

                    if (peak > levelPeakMax)
                        levelPeakMax = peak;

                    if (encodedFrameCount >= 50)
                    {
                        Debug.Log(
                            "VOICE_CLIENT_MIC_FRAME_LEVEL" +
                            " | device=" + SafeDeviceName(microphoneDevice) +
                            " | clipChannels=" + microphoneClip.channels +
                            " | clipFrequency=" + microphoneClip.frequency +
                            " | avgRms=" +
                            (levelRmsSum / encodedFrameCount).ToString("0.000000") +
                            " | peak=" + levelPeakMax.ToString("0.000000") +
                            " | lastPacketBytes=" + packet.Length +
                            " | dtx=" + dtx +
                            " | captureLagMs=" + SamplesToMilliseconds(available) +
                            " | maxCaptureLagMs=" + SamplesToMilliseconds(
                                maxCaptureLagSamplesSinceLog) +
                            " | captureMaxAgeMs=" + CaptureMaxAgeMs +
                            " | staleDrops=" + captureStaleDropCount +
                            " | droppedSamples=" + captureDroppedSamples);

                        encodedFrameCount = 0;
                        levelRmsSum = 0f;
                        levelPeakMax = 0f;
                        maxCaptureLagSamplesSinceLog = available;
                    }

                    FrameEncoded?.Invoke(packet, dtx);
                }
                catch (Exception exception)
                {
                    Failed?.Invoke(
                        "Voice microphone encode failed: " +
                        exception.Message);

                    SetMuted(true);
                    return;
                }
            }
        }

        private void StartCapture()
        {
            string[] devices = Microphone.devices;

            if (devices == null || devices.Length == 0)
            {
                muted = true;
                Failed?.Invoke(
                    "No microphone device is available.");

                return;
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string windowsSelectedInput;
            string resolverError;

            if (!WindowsSelectedCaptureDeviceResolver
                    .TryGetSoundSettingsInputDevice(
                        out windowsSelectedInput,
                        out resolverError))
            {
                muted = true;

                Failed?.Invoke(
                    "Windows Sound selected input device could not be resolved. " +
                    resolverError);

                return;
            }

            microphoneDevice =
                FindExactUnityDevice(
                    devices,
                    windowsSelectedInput);

            if (string.IsNullOrWhiteSpace(microphoneDevice))
            {
                muted = true;

                Failed?.Invoke(
                    "The microphone selected in Windows Sound settings is not available in Unity. " +
                    "WindowsSelected=" +
                    windowsSelectedInput +
                    " | UnityDevices=" +
                    string.Join(",", devices));

                return;
            }

            Debug.Log(
                "VOICE_CLIENT_MIC_WINDOWS_SOUND_SELECTION=PASS" +
                " | windowsSelected=" +
                SafeDeviceName(windowsSelectedInput) +
                " | unitySelected=" +
                SafeDeviceName(microphoneDevice) +
                " | unityDevices=" +
                string.Join(",", devices));
#else
            microphoneDevice = null;
#endif

            microphoneClip = Microphone.Start(
                microphoneDevice,
                true,
                ClipSeconds,
                VoiceNativeOpusCodec.SampleRate);

            if (microphoneClip == null)
            {
                muted = true;

                Failed?.Invoke(
                    "Microphone capture could not be started for device: " +
                    SafeDeviceName(microphoneDevice));

                return;
            }

            int channels =
                Mathf.Max(1, microphoneClip.channels);

            interleavedFrame =
                channels > 1
                    ? new float[
                        VoiceNativeOpusCodec.FrameSamples *
                        channels]
                    : null;

            StartRawDiagnosticCapture();

            Debug.Log(
                "VOICE_CLIENT_MIC_DEVICE_SELECTED=PASS" +
                " | selected=" +
                SafeDeviceName(microphoneDevice) +
                " | devices=" +
                string.Join(",", devices) +
                " | clipChannels=" +
                microphoneClip.channels +
                " | clipFrequency=" +
                microphoneClip.frequency +
                " | clipSamples=" +
                microphoneClip.samples);

            readPosition = 0;
            encodedFrameCount = 0;
            levelRmsSum = 0f;
            levelPeakMax = 0f;
            maxCaptureLagSamplesSinceLog = 0;
            captureStaleDropCount = 0;
            captureDroppedSamples = 0;
        }

        private void StopCapture()
        {
            SaveRawDiagnosticCapture();

            if (microphoneClip != null &&
                !string.IsNullOrWhiteSpace(microphoneDevice) &&
                Microphone.IsRecording(microphoneDevice))
            {
                Microphone.End(microphoneDevice);
            }

            microphoneClip = null;
            microphoneDevice = string.Empty;
            interleavedFrame = null;
            readPosition = 0;
            encodedFrameCount = 0;
            levelRmsSum = 0f;
            levelPeakMax = 0f;
            maxCaptureLagSamplesSinceLog = 0;
            captureStaleDropCount = 0;
            captureDroppedSamples = 0;
        }

        //* این تابع یک بافر تشخیصی محلی برای ذخیره PCM خام قبل از Opus ایجاد می‌کند.
        //* این بافر در مسیر Live Voice هیچ تغییری ایجاد نمی‌کند و فقط کپی نمونه‌ها را نگه می‌دارد.
        private void StartRawDiagnosticCapture()
        {
            rawDiagnosticPcmStream?.Dispose();
            rawDiagnosticPcmStream =
                new MemoryStream(
                    Math.Min(
                        RawDiagnosticMaxBytes,
                        VoiceNativeOpusCodec.SampleRate *
                        sizeof(short) *
                        30));

            rawDiagnosticCaptureId =
                DateTime.UtcNow.ToString(
                    "yyyyMMdd_HHmmss_fff");

            rawDiagnosticSamples = 0;

            Debug.Log(
                "VOICE_CLIENT_RAW_PCM_DIAGNOSTIC_STARTED=PASS" +
                " | captureId=" +
                rawDiagnosticCaptureId +
                " | sampleRate=" +
                VoiceNativeOpusCodec.SampleRate +
                " | channels=1" +
                " | maxSeconds=" +
                RawDiagnosticMaxSeconds);
        }

        //* این تابع نمونه‌های PCM خام را بدون Gain، Normalize، Filter یا هر پردازش دیگری کپی می‌کند.
        private void AppendRawDiagnosticFrame(
            float[] samples)
        {
            if (rawDiagnosticPcmStream == null ||
                samples == null ||
                samples.Length !=
                VoiceNativeOpusCodec.FrameSamples)
            {
                return;
            }

            if (rawDiagnosticPcmStream.Length +
                rawDiagnosticPcm16Frame.Length >
                RawDiagnosticMaxBytes)
            {
                return;
            }

            for (int index = 0;
                 index < samples.Length;
                 index++)
            {
                float clamped =
                    Mathf.Clamp(
                        samples[index],
                        -1f,
                        1f);

                short pcm16 =
                    (short)Mathf.RoundToInt(
                        clamped *
                        short.MaxValue);

                int byteIndex =
                    index * sizeof(short);

                rawDiagnosticPcm16Frame[
                    byteIndex] =
                    (byte)(pcm16 & 0xff);

                rawDiagnosticPcm16Frame[
                    byteIndex + 1] =
                    (byte)((pcm16 >> 8) & 0xff);
            }

            rawDiagnosticPcmStream.Write(
                rawDiagnosticPcm16Frame,
                0,
                rawDiagnosticPcm16Frame.Length);

            rawDiagnosticSamples +=
                samples.Length;
        }

        //* این تابع در پایان Capture فایل WAV تشخیصی را در persistentDataPath ذخیره می‌کند.
        private void SaveRawDiagnosticCapture()
        {
            if (rawDiagnosticPcmStream == null)
                return;

            MemoryStream pcmStream =
                rawDiagnosticPcmStream;

            rawDiagnosticPcmStream = null;

            try
            {
                if (pcmStream.Length <= 0)
                    return;

                string directory =
                    Path.Combine(
                        Application.persistentDataPath,
                        "VoiceDiagnostics");

                Directory.CreateDirectory(
                    directory);

                string captureId =
                    string.IsNullOrWhiteSpace(
                        rawDiagnosticCaptureId)
                        ? DateTime.UtcNow.ToString(
                            "yyyyMMdd_HHmmss_fff")
                        : rawDiagnosticCaptureId;

                string path =
                    Path.Combine(
                        directory,
                        "VoiceRawMic_" +
                        captureId +
                        ".wav");

                using (FileStream fileStream =
                    new FileStream(
                        path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read))
                using (BinaryWriter writer =
                    new BinaryWriter(
                        fileStream))
                {
                    int dataLength =
                        checked(
                            (int)pcmStream.Length);

                    const short channels = 1;
                    const short bitsPerSample = 16;

                    int byteRate =
                        VoiceNativeOpusCodec.SampleRate *
                        channels *
                        bitsPerSample /
                        8;

                    short blockAlign =
                        (short)(
                            channels *
                            bitsPerSample /
                            8);

                    writer.Write(
                        new char[]
                        {
                            'R', 'I', 'F', 'F'
                        });

                    writer.Write(
                        36 + dataLength);

                    writer.Write(
                        new char[]
                        {
                            'W', 'A', 'V', 'E'
                        });

                    writer.Write(
                        new char[]
                        {
                            'f', 'm', 't', ' '
                        });

                    writer.Write(16);
                    writer.Write((short)1);
                    writer.Write(channels);
                    writer.Write(
                        VoiceNativeOpusCodec.SampleRate);
                    writer.Write(byteRate);
                    writer.Write(blockAlign);
                    writer.Write(bitsPerSample);

                    writer.Write(
                        new char[]
                        {
                            'd', 'a', 't', 'a'
                        });

                    writer.Write(dataLength);
                    writer.Flush();

                    pcmStream.Position = 0;
                    pcmStream.CopyTo(
                        fileStream);
                }

                double durationSeconds =
                    rawDiagnosticSamples /
                    (double)
                    VoiceNativeOpusCodec.SampleRate;

                Debug.Log(
                    "VOICE_CLIENT_RAW_PCM_DIAGNOSTIC_SAVED=PASS" +
                    " | path=" +
                    path.Replace("|", "/") +
                    " | samples=" +
                    rawDiagnosticSamples +
                    " | durationSeconds=" +
                    durationSeconds.ToString("0.000") +
                    " | processing=none_pre_opus");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_CLIENT_RAW_PCM_DIAGNOSTIC_SAVE_FAILED" +
                    " | error=" +
                    exception.Message);
            }
            finally
            {
                pcmStream.Dispose();
                rawDiagnosticCaptureId =
                    string.Empty;
                rawDiagnosticSamples = 0;
            }
        }

        private static int CircularDistance(
            int from,
            int to,
            int length)
        {
            if (length <= 0) return 0;

            return to >= from
                ? to - from
                : length - from + to;
        }

        private static int SamplesToMilliseconds(
            long samples)
        {
            if (samples <= 0) return 0;

            return (int)Math.Round(
                samples * 1000.0 /
                VoiceNativeOpusCodec.SampleRate);
        }

        private void ReadFrameFromClip(
            int start,
            float[] target)
        {
            if (microphoneClip == null)
            {
                throw new InvalidOperationException(
                    "Microphone clip is not available.");
            }

            if (target == null ||
                target.Length !=
                VoiceNativeOpusCodec.FrameSamples)
            {
                throw new ArgumentException(
                    "Voice microphone frame must contain exactly " +
                    VoiceNativeOpusCodec.FrameSamples +
                    " mono samples.",
                    nameof(target));
            }

            int channels =
                Mathf.Max(1, microphoneClip.channels);

            if (channels == 1)
            {
                if (!microphoneClip.GetData(
                        target,
                        start))
                {
                    throw new InvalidOperationException(
                        "Microphone AudioClip.GetData failed.");
                }

                return;
            }

            int requiredSamples =
                target.Length * channels;

            if (interleavedFrame == null ||
                interleavedFrame.Length !=
                requiredSamples)
            {
                interleavedFrame =
                    new float[requiredSamples];
            }

            if (!microphoneClip.GetData(
                    interleavedFrame,
                    start))
            {
                throw new InvalidOperationException(
                    "Microphone AudioClip.GetData failed.");
            }

            for (int sampleIndex = 0;
                 sampleIndex < target.Length;
                 sampleIndex++)
            {
                int sourceIndex =
                    sampleIndex * channels;

                float sum = 0f;

                for (int channelIndex = 0;
                     channelIndex < channels;
                     channelIndex++)
                {
                    sum +=
                        interleavedFrame[
                            sourceIndex +
                            channelIndex];
                }

                target[sampleIndex] =
                    sum / channels;
            }
        }

        private static string FindExactUnityDevice(
            string[] devices,
            string windowsSelectedInput)
        {
            if (devices == null ||
                string.IsNullOrWhiteSpace(
                    windowsSelectedInput))
            {
                return string.Empty;
            }

            for (int index = 0;
                 index < devices.Length;
                 index++)
            {
                if (string.Equals(
                        devices[index],
                        windowsSelectedInput,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return devices[index];
                }
            }

            return string.Empty;
        }

        private static void MeasureFrameLevel(
            float[] samples,
            out float rms,
            out float peak)
        {
            double sum = 0.0;
            float max = 0f;

            for (int index = 0;
                 index < samples.Length;
                 index++)
            {
                float absolute =
                    Mathf.Abs(samples[index]);

                sum +=
                    samples[index] *
                    samples[index];

                if (absolute > max)
                    max = absolute;
            }

            rms =
                Mathf.Sqrt(
                    (float)(sum / samples.Length));

            peak = max;
        }

        private static string SafeDeviceName(
            string deviceName)
        {
            return string.IsNullOrWhiteSpace(
                    deviceName)
                ? "<empty>"
                : deviceName.Replace("|", "/");
        }

        private void OnDestroy()
        {
            StopCapture();

            codec?.Dispose();
            codec = null;
        }
    }
}

/*
توضیح فایل:
در Windows فقط ورودی‌ای استفاده می‌شود که در Settings > System > Sound > Input انتخاب شده است.
انتخاب بر اساس اولین دستگاه Unity یا صرفا دستگاه Active انجام نمی‌شود.
نام Capture Endpoint انتخاب‌شده از Windows Core Audio خوانده می‌شود و همان نام صریحا به Unity Microphone.Start داده می‌شود.
اگر Windows یک دستگاه را انتخاب کرده باشد ولی Unity همان دستگاه را در Microphone.devices نبیند، سیستم حدس نمی‌زند و Capture را با خطای روشن متوقف می‌کند.
Capture صوت به صورت Live نگه داشته می‌شود؛ اگر readPosition بیش از 120ms از writePosition عقب بماند، PCM قدیمی Drop می‌شود و خواندن به آخرین Frame کامل برمی‌گردد.
لاگ VOICE_CLIENT_MIC_CAPTURE_STALE_DROP و فیلدهای captureLagMs/maxCaptureLagMs برای اثبات یا رد عقب‌افتادگی Capture ثبت می‌شوند.
نسخه Diagnostic یک کپی PCM خام قبل از Opus را بدون هیچ Gain/Normalize/Filter در persistentDataPath/VoiceDiagnostics ذخیره می‌کند تا علت افت کیفیت بین Capture و مراحل بعدی جدا شود.
رفتار پلتفرم‌های غیر Windows از نظر انتخاب دستگاه تغییر داده نشده است.
*/
