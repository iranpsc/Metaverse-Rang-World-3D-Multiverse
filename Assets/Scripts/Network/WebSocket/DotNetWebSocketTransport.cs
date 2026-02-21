using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    public class DotNetWebSocketTransport : IWebSocketTransport
    {
        private ClientWebSocket ws;
        private CancellationTokenSource internalCts;
        private int receiveBufferSize;

        public TransportState State { get; private set; } = TransportState.Disconnected;

        public event Action OnOpen;
        public event Action<string> OnTextMessage;
        public event Action<string> OnError;
        public event Action<int, string> OnClose;

        public DotNetWebSocketTransport(int receiveBufferSize = 4096)
        {
            this.receiveBufferSize = receiveBufferSize;
        }

        public async Task ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken ct)
        {
            if (State == TransportState.Connected || State == TransportState.Connecting)
                return;

            State = TransportState.Connecting;

            ws = new ClientWebSocket();
            ws.Options.SetBuffer(receiveBufferSize, receiveBufferSize);

            if (headers != null)
            {
                foreach (var h in headers)
                    ws.Options.SetRequestHeader(h.Key, h.Value);
            }

            internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                await ws.ConnectAsync(new Uri(url), internalCts.Token);

                State = TransportState.Connected;
                OnOpen?.Invoke();

                _ = ReceiveLoopAsync(internalCts.Token);
            }
            catch (Exception ex)
            {
                State = TransportState.Disconnected;
                OnError?.Invoke(ex.Message);
                SafeDispose();
                throw;
            }
        }

        public async Task SendTextAsync(string text, CancellationToken ct)
        {
            if (State != TransportState.Connected || ws == null)
                throw new InvalidOperationException("Transport not connected.");

            var bytes = Encoding.UTF8.GetBytes(text);
            var seg = new ArraySegment<byte>(bytes);

            await ws.SendAsync(seg, WebSocketMessageType.Text, true, internalCts?.Token ?? ct);
        }

        public async Task CloseAsync(int code, string reason, CancellationToken ct)
        {
            if (State == TransportState.Disconnected)
                return;

            State = TransportState.Closing;

            try
            {
                internalCts?.Cancel();

                if (ws != null && ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
                }
            }
            catch { }
            finally
            {
                State = TransportState.Disconnected;
                OnClose?.Invoke(code, reason);
                SafeDispose();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[receiveBufferSize];
            using var ms = new System.IO.MemoryStream();

            while (!ct.IsCancellationRequested && ws != null && ws.State == WebSocketState.Open)
            {
                try
                {
                    ms.SetLength(0);

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            var closeCode = (int)(ws.CloseStatus ?? WebSocketCloseStatus.NormalClosure);
                            var closeReason = ws.CloseStatusDescription ?? "Server closed";
                            State = TransportState.Disconnected;
                            OnClose?.Invoke(closeCode, closeReason);
                            SafeDispose();
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);

                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = Encoding.UTF8.GetString(ms.ToArray());
                        OnTextMessage?.Invoke(msg);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex.Message);
                    State = TransportState.Disconnected;
                    OnClose?.Invoke((int)WebSocketCloseStatus.InternalServerError, ex.Message);
                    SafeDispose();
                    return;
                }
            }
        }

        private void SafeDispose()
        {
            try { internalCts?.Cancel(); } catch { }
            try { internalCts?.Dispose(); } catch { }
            internalCts = null;

            try { ws?.Dispose(); } catch { }
            ws = null;

            State = TransportState.Disconnected;
        }

        public void Dispose() => SafeDispose();
    }
}
