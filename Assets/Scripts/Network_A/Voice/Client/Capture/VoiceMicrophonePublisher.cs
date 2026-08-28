using System;
using Network_A.Voice.Client.Codec;
using UnityEngine;

namespace Network_A.Voice.Client.Capture
{
    public sealed class VoiceMicrophonePublisher : MonoBehaviour
    {
        private const int ClipSeconds = 1;

        private VoiceNativeOpusCodec codec;
        private AudioClip microphoneClip;
        private string microphoneDevice;
        private int readPosition;
        private bool muted = true;
        private float[] frame = new float[VoiceNativeOpusCodec.FrameSamples];
        private int encodedFrameCount;
        private float levelRmsSum;
        private float levelPeakMax;

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
            int available = writePosition >= readPosition
                ? writePosition - readPosition
                : clipSamples - readPosition + writePosition;

            while (available >= VoiceNativeOpusCodec.FrameSamples)
            {
                ReadFrameFromClip(readPosition, frame);
                readPosition = (readPosition + VoiceNativeOpusCodec.FrameSamples) % clipSamples;
                available -= VoiceNativeOpusCodec.FrameSamples;

                try
                {
                    float rms;
                    float peak;
                    MeasureFrameLevel(frame, out rms, out peak);

                    byte[] packet = codec.Encode(frame);
                    bool dtx = packet.Length <= 3;
                    encodedFrameCount += 1;
                    levelRmsSum += rms;
                    if (peak > levelPeakMax) levelPeakMax = peak;

                    if (encodedFrameCount >= 50)
                    {
                        Debug.Log(
                            "VOICE_CLIENT_MIC_FRAME_LEVEL" +
                            " | device=" + SafeDeviceName(microphoneDevice) +
                            " | avgRms=" + (levelRmsSum / encodedFrameCount).ToString("0.000000") +
                            " | peak=" + levelPeakMax.ToString("0.000000") +
                            " | lastPacketBytes=" + packet.Length +
                            " | dtx=" + dtx);
                        encodedFrameCount = 0;
                        levelRmsSum = 0f;
                        levelPeakMax = 0f;
                    }

                    FrameEncoded?.Invoke(packet, dtx);
                }
                catch (Exception exception)
                {
                    Failed?.Invoke("Voice microphone encode failed: " + exception.Message);
                    SetMuted(true);
                    return;
                }
            }
        }

        private void StartCapture()
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                muted = true;
                Failed?.Invoke("No microphone device is available.");
                return;
            }

            microphoneDevice = Microphone.devices[0];
            Debug.Log(
                "VOICE_CLIENT_MIC_DEVICE_SELECTED=PASS" +
                " | selected=" + SafeDeviceName(microphoneDevice) +
                " | devices=" + string.Join(",", Microphone.devices));
            microphoneClip = Microphone.Start(microphoneDevice, true, ClipSeconds, VoiceNativeOpusCodec.SampleRate);
            readPosition = 0;
            encodedFrameCount = 0;
            levelRmsSum = 0f;
            levelPeakMax = 0f;
        }

        private void StopCapture()
        {
            if (!string.IsNullOrWhiteSpace(microphoneDevice) && Microphone.IsRecording(microphoneDevice))
                Microphone.End(microphoneDevice);
            microphoneClip = null;
            microphoneDevice = string.Empty;
            readPosition = 0;
            encodedFrameCount = 0;
            levelRmsSum = 0f;
            levelPeakMax = 0f;
        }

        private void ReadFrameFromClip(int start, float[] target)
        {
            int clipSamples = microphoneClip.samples;
            int firstLength = Mathf.Min(target.Length, clipSamples - start);
            float[] first = new float[firstLength];
            microphoneClip.GetData(first, start);
            Array.Copy(first, 0, target, 0, firstLength);

            int remaining = target.Length - firstLength;
            if (remaining <= 0) return;
            float[] second = new float[remaining];
            microphoneClip.GetData(second, 0);
            Array.Copy(second, 0, target, firstLength, remaining);
        }


        private static void MeasureFrameLevel(float[] samples, out float rms, out float peak)
        {
            double sum = 0.0;
            float max = 0f;
            for (int index = 0; index < samples.Length; index++)
            {
                float absolute = Mathf.Abs(samples[index]);
                sum += samples[index] * samples[index];
                if (absolute > max) max = absolute;
            }

            rms = Mathf.Sqrt((float)(sum / samples.Length));
            peak = max;
        }

        private static string SafeDeviceName(string deviceName)
        {
            return string.IsNullOrWhiteSpace(deviceName) ? "<empty>" : deviceName.Replace("|", "/");
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
این فایل Capture میکروفن را در فریم‌های دقیق 960 نمونه‌ای انجام می‌دهد؛ در حالت Mic Mute خود Microphone متوقف می‌شود و هیچ فریم یا Upload تولید نمی‌شود.
*/
