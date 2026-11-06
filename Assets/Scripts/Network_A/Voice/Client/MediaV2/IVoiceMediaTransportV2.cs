using System;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.Voice.Client.MediaV2
{
    public interface IVoiceMediaTransportV2 : IDisposable
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

    public static class VoiceMediaTransportV2Factory
    {
        //* این تابع برای وب جی ال راه انتقال وب سوکت و برای ویندوز راه انتقال جی آر پی سی مستقل رسانه را انتخاب می کند.
        public static IVoiceMediaTransportV2 CreateForCurrentPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return new VoiceWebGlMediaTransportV2();
#else
            return new VoiceGrpcMediaTransportV2();
#endif
        }
    }
}
