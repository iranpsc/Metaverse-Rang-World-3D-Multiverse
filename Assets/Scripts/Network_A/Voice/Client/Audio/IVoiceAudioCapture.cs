using System;

namespace Network_A.Voice.Client.Audio
{
    public static class VoiceAudioContract
    {
        public const int SampleRate = 48000;
        public const int Channels = 1;
        public const int FrameDurationMs = 20;
        public const int SamplesPerFrame = 960;
    }

    public interface IVoiceAudioCapture : IDisposable
    {
        event Action<ArraySegment<float>> FrameCaptured;
        event Action<string> Failed;

        bool IsCapturing { get; }

        void Start();
        void Stop();
    }
}
