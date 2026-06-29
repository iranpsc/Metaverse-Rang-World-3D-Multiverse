using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.GameServer.WebSocket
{
    public class DedicatedWebSocketConnection
    {
        private readonly TcpClient tcpClient;
        private readonly CancellationToken serverCancellationToken;
        private NetworkStream stream;
        private bool isClosed;

        public string ConnectionId { get; private set; }
        public string RemoteEndPoint { get; private set; }
        public bool IsOpen { get; private set; }

        public event Action<DedicatedWebSocketConnection> Opened;
        public event Action<DedicatedWebSocketConnection, string> TextReceived;
        public event Action<DedicatedWebSocketConnection, string> Closed;

        //* این تابع یک کانکشن جدید وب سوکت را با تی سی پی کلاینت ورودی می سازد.
        public DedicatedWebSocketConnection(TcpClient tcpClient, CancellationToken serverCancellationToken)
        {
            this.tcpClient = tcpClient;
            this.serverCancellationToken = serverCancellationToken;

            ConnectionId = Guid.NewGuid().ToString("N");
            RemoteEndPoint = tcpClient.Client.RemoteEndPoint != null
                ? tcpClient.Client.RemoteEndPoint.ToString()
                : "unknown";
        }

        //* این تابع هندشیک وب سوکت را انجام می دهد و سپس حلقه دریافت پیام را شروع می کند.
        public async Task StartAsync()
        {
            try
            {
                stream = tcpClient.GetStream();

                await PerformHandshakeAsync(serverCancellationToken);

                IsOpen = true;
                Opened?.Invoke(this);

                await SendTextAsync("{\"type\":\"server_hello\",\"message\":\"unity_dedicated_websocket_ready\"}", serverCancellationToken);

                await ReceiveLoopAsync(serverCancellationToken);
            }
            catch (OperationCanceledException)
            {
                await CloseInternalAsync("cancelled");
            }
            catch (Exception ex)
            {
                await CloseInternalAsync(ex.Message);
            }
        }

        //* این تابع یک پیام متنی را برای کلاینت ارسال می کند.
        public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!IsOpen || stream == null) return;

            byte[] frame = DedicatedWebSocketFrameCodec.BuildTextFrame(text);

            await stream.WriteAsync(frame, 0, frame.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        //* این تابع کانکشن را با ارسال فریم کلوز می بندد.
        public async Task CloseAsync(string reason)
        {
            await CloseInternalAsync(reason);
        }

        //* این تابع هندشیک اچ تی تی پی آپگرید وب سوکت را انجام می دهد.
        private async Task PerformHandshakeAsync(CancellationToken cancellationToken)
        {
            string requestText = await ReadHttpHeaderAsync(cancellationToken);
            Dictionary<string, string> headers = ParseHeaders(requestText);

            if (!headers.TryGetValue("sec-websocket-key", out string websocketKey))
            {
                throw new InvalidOperationException("Missing Sec-WebSocket-Key.");
            }

            string acceptKey = CreateWebSocketAcceptKey(websocketKey);

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                "\r\n";

            byte[] responseBytes = Encoding.ASCII.GetBytes(response);

            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        //* این تابع حلقه دریافت پیام های وب سوکت را تا زمان قطع کانکشن اجرا می کند.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsOpen)
            {
                DedicatedWebSocketFrame frame = await DedicatedWebSocketFrameCodec.ReadFrameAsync(stream, cancellationToken);

                if (frame.IsClose())
                {
                    await CloseInternalAsync("client_closed");
                    return;
                }

                if (frame.IsPing())
                {
                    byte[] pong = DedicatedWebSocketFrameCodec.BuildPongFrame(frame.payload);
                    await stream.WriteAsync(pong, 0, pong.Length, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    continue;
                }

                if (frame.IsPong())
                {
                    continue;
                }

                if (frame.IsText())
                {
                    string text = frame.ReadText();
                    TextReceived?.Invoke(this, text);
                    continue;
                }
            }
        }

        //* این تابع هدر اچ تی تی پی اولیه کلاینت را تا پایان هدر می خواند.
        private async Task<string> ReadHttpHeaderAsync(CancellationToken cancellationToken)
        {
            List<byte> bytes = new List<byte>();
            byte[] buffer = new byte[1];

            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, 0, 1, cancellationToken);

                if (read <= 0)
                {
                    throw new InvalidOperationException("Client disconnected during websocket handshake.");
                }

                bytes.Add(buffer[0]);

                int count = bytes.Count;

                if (count >= 4 &&
                    bytes[count - 4] == '\r' &&
                    bytes[count - 3] == '\n' &&
                    bytes[count - 2] == '\r' &&
                    bytes[count - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }

                if (bytes.Count > 8192)
                {
                    throw new InvalidOperationException("Websocket handshake header is too large.");
                }
            }

            throw new OperationCanceledException();
        }

        //* این تابع هدرهای اچ تی تی پی هندشیک را به دیکشنری تبدیل می کند.
        private Dictionary<string, string> ParseHeaders(string requestText)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None);

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];

                if (string.IsNullOrWhiteSpace(line)) continue;

                int separatorIndex = line.IndexOf(':');

                if (separatorIndex <= 0) continue;

                string key = line.Substring(0, separatorIndex).Trim().ToLowerInvariant();
                string value = line.Substring(separatorIndex + 1).Trim();

                headers[key] = value;
            }

            return headers;
        }

        //* این تابع کلید پاسخ وب سوکت را طبق قرارداد استاندارد می سازد.
        private string CreateWebSocketAcceptKey(string websocketKey)
        {
            string raw = websocketKey.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(raw));
                return Convert.ToBase64String(hash);
            }
        }

        //* این تابع کانکشن را تمیز می بندد و رویداد کلوز را فقط یک بار صدا می زند.
        private async Task CloseInternalAsync(string reason)
        {
            if (isClosed) return;

            isClosed = true;
            IsOpen = false;

            try
            {
                if (stream != null)
                {
                    byte[] closeFrame = DedicatedWebSocketFrameCodec.BuildCloseFrame();
                    await stream.WriteAsync(closeFrame, 0, closeFrame.Length);
                    await stream.FlushAsync();
                }
            }
            catch
            {
            }

            try
            {
                if (stream != null) stream.Close();
            }
            catch
            {
            }

            try
            {
                tcpClient.Close();
            }
            catch
            {
            }

            Closed?.Invoke(this, string.IsNullOrWhiteSpace(reason) ? "closed" : reason);
        }

        /*
        توضیح مکتوب فایل:
        این فایل یک کانکشن وب سوکت بین کلاینت و یونیتی ددیکیتد سرور را مدیریت می کند.
        ابتدا هندشیک وب سوکت را انجام می دهد و بعد پیام های متنی را دریافت می کند.
        فعلاً در این فاز فقط اتصال، دریافت متن، پاسخ پینگ و بستن کانکشن پیاده سازی شده است.
        در فاز بعدی پیام auth_ticket از همین مسیر دریافت می شود و به وریفای تیکت وصل خواهد شد.
        */
    }
}
