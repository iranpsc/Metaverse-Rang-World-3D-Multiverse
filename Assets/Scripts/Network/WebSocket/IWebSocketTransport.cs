using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assets.Scripts.Network.WebSocket
{
    public enum TransportState
    {
        Disconnected,
        Connecting,
        Connected,
        Closing
    }

    public interface IWebSocketTransport : IDisposable
    {
        TransportState State { get; }

        // رویدادهای ترنسپورت (همان چیزی که WebSocketClient لازم دارد)
        event Action OnOpen;
        event Action<string> OnTextMessage;
        event Action<string> OnError;
        event Action<int, string> OnClose; // code, reason

        Task ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken ct);
        Task SendTextAsync(string text, CancellationToken ct);
        Task CloseAsync(int code, string reason, CancellationToken ct);
    }
}
