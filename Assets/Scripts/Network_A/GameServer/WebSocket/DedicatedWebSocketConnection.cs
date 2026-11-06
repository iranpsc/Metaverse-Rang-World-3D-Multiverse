using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer.Protocol;
using Network_A.Realtime.Protocol;

namespace Network_A.GameServer.WebSocket
{
    public class DedicatedWebSocketConnection
    {
        private readonly TcpClient tcpClient;
        private readonly CancellationToken serverCancellationToken;
        private readonly bool enableLivenessCheck;
        private readonly int pingIntervalMilliseconds;
        private readonly int livenessTimeoutMilliseconds;
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);

        private NetworkStream stream;
        private CancellationTokenSource connectionCts;
        private bool isClosed;
        private long lastInboundUnixMilliseconds;

        public string ConnectionId { get; private set; }
        public string RemoteEndPoint { get; private set; }
        public bool IsOpen { get; private set; }
        public long LastInboundUnixMilliseconds => Interlocked.Read(ref lastInboundUnixMilliseconds);
        public float InactiveSeconds => MathfSafeSecondsSince(LastInboundUnixMilliseconds);

        public event Action<DedicatedWebSocketConnection> Opened;
        public event Action<DedicatedWebSocketConnection, string> TextReceived;
        public event Action<DedicatedWebSocketConnection, string> Closed;

        //* این تابع یک کانکشن جدید را با تنظیمات پیش فرض کنترل زنده بودن می سازد.
        public DedicatedWebSocketConnection(TcpClient tcpClient, CancellationToken serverCancellationToken)
            : this(tcpClient, serverCancellationToken, true, 5f, 15f)
        {
        }

        //* این تابع یک کانکشن جدید وب سوکت را همراه فاصله پینگ و مهلت تشخیص اتصال مرده می سازد.
        public DedicatedWebSocketConnection(
            TcpClient tcpClient,
            CancellationToken serverCancellationToken,
            bool enableLivenessCheck,
            float pingIntervalSeconds,
            float livenessTimeoutSeconds)
        {
            this.tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
            this.serverCancellationToken = serverCancellationToken;
            this.enableLivenessCheck = enableLivenessCheck;

            float safePingIntervalSeconds = Math.Max(1f, pingIntervalSeconds);
            float safeLivenessTimeoutSeconds = Math.Max(safePingIntervalSeconds + 1f, livenessTimeoutSeconds);
            pingIntervalMilliseconds = Math.Max(1000, (int)Math.Round(safePingIntervalSeconds * 1000f));
            livenessTimeoutMilliseconds = Math.Max(pingIntervalMilliseconds + 1000, (int)Math.Round(safeLivenessTimeoutSeconds * 1000f));

            ConnectionId = Guid.NewGuid().ToString("N");
            RemoteEndPoint = tcpClient.Client.RemoteEndPoint != null ? tcpClient.Client.RemoteEndPoint.ToString() : "unknown";
            MarkInboundActivity();
        }

        //* این تابع هندشیک وب سوکت را انجام می دهد و سپس حلقه دریافت و کنترل زنده بودن را شروع می کند.
        public async Task StartAsync()
        {
            Task livenessTask = null;

            try
            {
                connectionCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
                CancellationToken connectionToken = connectionCts.Token;
                stream = tcpClient.GetStream();

                await PerformHandshakeAsync(connectionToken).ConfigureAwait(false);

                IsOpen = true;
                MarkInboundActivity();
                Opened?.Invoke(this);

                await SendServerHelloAsync(connectionToken).ConfigureAwait(false);

                if (enableLivenessCheck) livenessTask = RunLivenessLoopAsync(connectionToken);
                await ReceiveLoopAsync(connectionToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await CloseInternalAsync("cancelled").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await CloseInternalAsync(ex.Message).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (connectionCts != null && !connectionCts.IsCancellationRequested) connectionCts.Cancel();
                }
                catch
                {
                }

                if (livenessTask != null)
                {
                    try
                    {
                        await livenessTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        //* این تابع پیام آماده بودن سرور را با اِنولوپ استاندارد برای کلاینت می فرستد.
        private async Task SendServerHelloAsync(CancellationToken cancellationToken)
        {
            string payloadJson = "{\"message\":\"unity_dedicated_websocket_ready\"}";
            string envelopeJson = DedicatedRealtimeEnvelopeCodec.WrapSystemPayload(RealtimeMessageTypes.ServerHello, payloadJson);
            await SendTextAsync(envelopeJson, cancellationToken).ConfigureAwait(false);
        }

        //* این تابع یک پیام متنی را با قفل ارسال برای جلوگیری از تداخل فریم ها می فرستد.
        public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (!IsOpen || stream == null) return;
            await SendFrameAsync(DedicatedWebSocketFrameCodec.BuildTextFrame(text), cancellationToken).ConfigureAwait(false);
        }

        //* این تابع کانکشن را با ارسال فریم کلوز می بندد.
        public async Task CloseAsync(string reason)
        {
            await CloseInternalAsync(reason).ConfigureAwait(false);
        }

        //* این تابع هندشیک اچ تی تی پی آپگرید وب سوکت را انجام می دهد.
        private async Task PerformHandshakeAsync(CancellationToken cancellationToken)
        {
            string requestText = await ReadHttpHeaderAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> headers = ParseHeaders(requestText);

            if (!headers.TryGetValue("sec-websocket-key", out string websocketKey)) throw new InvalidOperationException("Missing Sec-WebSocket-Key.");

            string acceptKey = CreateWebSocketAcceptKey(websocketKey);
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                "\r\n";

            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        //* این تابع حلقه دریافت پیام های وب سوکت را اجرا و زمان آخرین فعالیت ورودی را تازه می کند.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsOpen)
            {
                DedicatedWebSocketFrame frame = await DedicatedWebSocketFrameCodec.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                MarkInboundActivity();

                if (frame.IsClose())
                {
                    await CloseInternalAsync("client_closed").ConfigureAwait(false);
                    return;
                }

                if (frame.IsPing())
                {
                    await SendFrameAsync(DedicatedWebSocketFrameCodec.BuildPongFrame(frame.payload), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (frame.IsPong()) continue;

                if (frame.IsText())
                {
                    TextReceived?.Invoke(this, frame.ReadText());
                    continue;
                }
            }
        }

        //* این تابع به شکل فعال پینگ می فرستد و اتصال بدون هیچ پاسخ یا پیام را در مهلت مشخص می بندد.
        private async Task RunLivenessLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsOpen)
            {
                await Task.Delay(pingIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested || !IsOpen) return;

                long inactiveMilliseconds = GetInactiveMilliseconds();

                if (inactiveMilliseconds >= livenessTimeoutMilliseconds)
                {
                    await CloseInternalAsync("websocket_liveness_timeout:inactive_ms=" + inactiveMilliseconds).ConfigureAwait(false);
                    return;
                }

                byte[] pingPayload = Encoding.ASCII.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
                await SendFrameAsync(DedicatedWebSocketFrameCodec.BuildPingFrame(pingPayload), cancellationToken).ConfigureAwait(false);
            }
        }

        //* این تابع همه فریم های خروجی را با یک قفل مشترک می فرستد تا پینگ، پانگ و متن با هم ترکیب نشوند.
        private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            if (frame == null || frame.Length == 0 || !IsOpen || stream == null) return;

            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!IsOpen || stream == null) return;
                await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        //* این تابع زمان آخرین فریم معتبر ورودی را ثبت می کند.
        private void MarkInboundActivity()
        {
            Interlocked.Exchange(ref lastInboundUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        //* این تابع مدت بی فعالیت بودن اتصال را به میلی ثانیه برمی گرداند.
        private long GetInactiveMilliseconds()
        {
            long lastInbound = LastInboundUnixMilliseconds;
            if (lastInbound <= 0) return 0;
            return Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastInbound);
        }

        //* این تابع مدت بی فعالیت بودن اتصال را برای محاسبه باقیمانده گریس به ثانیه تبدیل می کند.
        private static float MathfSafeSecondsSince(long lastInboundUnixMs)
        {
            if (lastInboundUnixMs <= 0) return 0f;
            long elapsedMilliseconds = Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastInboundUnixMs);
            return (float)(elapsedMilliseconds / 1000.0);
        }

        //* این تابع هدر اچ تی تی پی اولیه کلاینت را تا پایان هدر به صورت بافری می خواند تا هندشیک به فریم های Unity وابسته نشود.
        private async Task<string> ReadHttpHeaderAsync(CancellationToken cancellationToken)
        {
            const int maxHeaderBytes = 8192;
            const int readBufferBytes = 2048;

            List<byte> bytes = new List<byte>(1024);
            byte[] buffer = new byte[readBufferBytes];

            while (!cancellationToken.IsCancellationRequested)
            {
                int remainingBytes = maxHeaderBytes - bytes.Count;
                if (remainingBytes <= 0) throw new InvalidOperationException("Websocket handshake header is too large.");

                int requestedBytes = Math.Min(buffer.Length, remainingBytes);
                int read = await stream.ReadAsync(buffer, 0, requestedBytes, cancellationToken).ConfigureAwait(false);
                if (read <= 0) throw new InvalidOperationException("Client disconnected during websocket handshake.");

                for (int i = 0; i < read; i++)
                {
                    bytes.Add(buffer[i]);
                    int count = bytes.Count;

                    if (count >= 4 &&
                        bytes[count - 4] == '\r' &&
                        bytes[count - 3] == '\n' &&
                        bytes[count - 2] == '\r' &&
                        bytes[count - 1] == '\n')
                    {
                        return Encoding.ASCII.GetString(bytes.ToArray());
                    }
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
                if (connectionCts != null && !connectionCts.IsCancellationRequested) connectionCts.Cancel();
            }
            catch
            {
            }

            try
            {
                if (stream != null)
                {
                    using (CancellationTokenSource closeCts = new CancellationTokenSource(1000))
                    {
                        await sendGate.WaitAsync(closeCts.Token).ConfigureAwait(false);

                        try
                        {
                            byte[] closeFrame = DedicatedWebSocketFrameCodec.BuildCloseFrame();
                            await stream.WriteAsync(closeFrame, 0, closeFrame.Length, closeCts.Token).ConfigureAwait(false);
                            await stream.FlushAsync(closeCts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            sendGate.Release();
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                stream?.Close();
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
        این فایل اتصال وب سوکت کلاینت به یونیتی ددیکیتد سرور را مدیریت می کند.
        سرور هر چند ثانیه یک پینگ واقعی وب سوکت می فرستد و هر فریم ورودی را نشانه زنده بودن اتصال می داند.
        اگر در مهلت تعیین شده هیچ متن، پینگ یا پانگی نرسد، اتصال مرده بسته می شود تا گریس ریکانکت فوراً آغاز شود.
        */
    }
}
