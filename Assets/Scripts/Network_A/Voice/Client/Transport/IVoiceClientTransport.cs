using System;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.Voice.Client.Transport
{
    public interface IVoiceClientTransport : IDisposable
    {
        event Action Connected;
        event Action<byte[]> PacketReceived;
        event Action<string> Failed;
        event Action<string> Disconnected;

        bool IsConnected { get; }

        Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken);
        Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken);
        Task DisconnectAsync(string reason, CancellationToken cancellationToken);
    }

    public static class VoiceClientTransportFactory
    {
        //* این تابع WebGL را فقط به WSS و Windows/Quest را فقط به gRPC هدایت می‌کند.
        public static IVoiceClientTransport CreateForCurrentPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new VoiceWebGlSocketTransport();
#else
            return new VoiceGrpcClientTransport();
#endif
        }
    }
}

/*
توضیح فایل:
این فایل رابط مشترک Transport کلاینت و انتخاب قطعی gRPC برای Windows/Quest و WSS برای WebGL را تعریف می‌کند.
*/
