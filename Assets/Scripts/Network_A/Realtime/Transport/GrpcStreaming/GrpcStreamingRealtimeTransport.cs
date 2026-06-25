using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using UnityEngine;

#if !UNITY_WEBGL || UNITY_EDITOR
using Grpc.Core;
#endif

namespace Network_A.Realtime.Transport
{
    //* ترنسپورت جی‌آر‌پی‌سی استریمینگ ریل‌تایم است و فقط پیام خام اِنولوپ را داخل rawJson جابه‌جا می‌کند.
    public sealed class GrpcStreamingRealtimeTransport : IRealtimeTransport, IDisposable
    {
        private const string RealtimeServiceName = "metaverse.v1.realtime.RealtimeStreamService";
        private const string RealtimeOpenMethodName = "Open";
        private const int CleanupTimeoutMs = 2000;

#if !UNITY_WEBGL || UNITY_EDITOR
        private static readonly Marshaller<RealtimeRawJsonFrame> RawJsonFrameMarshaller = Marshallers.Create(SerializeFrameForGrpc, DeserializeFrameFromGrpc);

        private Channel channel;
        private CallInvoker callInvoker;
        private AsyncDuplexStreamingCall<RealtimeRawJsonFrame, RealtimeRawJsonFrame> streamCall;
#endif

        private CancellationTokenSource connectionCts;
        private RealtimeTransportState state = RealtimeTransportState.Disconnected;
        private bool isDisconnecting;

        public event Action Connected;
        public event Action<string> MessageReceived;
        public event Action<string> ErrorReceived;
        public event Action<string> Disconnected;

        public RealtimeTransportKind Kind { get { return RealtimeTransportKind.GrpcStreaming; } }
        public RealtimeTransportState State { get { return state; } }
        public bool IsConnected { get { return state == RealtimeTransportState.Connected && HasActiveStream(); } }

        //* ترنسپورت جی‌آر‌پی‌سی استریمینگ را برای پلتفرم‌های غیر وب‌جی‌ال داخل کارخانه ثبت می‌کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterTransportOnLoad()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return;
#else
            RealtimeTransportFactory.RegisterTransport(RealtimeTransportKind.GrpcStreaming, () => new GrpcStreamingRealtimeTransport());
#endif
        }



        //* اتصال جی‌آر‌پی‌سی استریمینگ را با آدرس و هدرهای داده‌شده شروع می‌کند.
        public async Task<bool> ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        {
            if (IsConnected) return true;

#if UNITY_WEBGL && !UNITY_EDITOR
            return FailConnect("GrpcStreamingRealtimeTransport is not supported in WebGL build. WebGL must use WebSocket.");
#else
            try
            {
                await CleanupStreamAsync("Reconnect cleanup", CancellationToken.None, false);

                SetState(RealtimeTransportState.Connecting);
                isDisconnecting = false;
                connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                string target = ResolveGrpcTarget(url);
                ChannelCredentials credentials = ResolveChannelCredentials(url);

                channel = new Channel(target, credentials);
                callInvoker = channel.CreateCallInvoker();

                Method<RealtimeRawJsonFrame, RealtimeRawJsonFrame> method = new Method<RealtimeRawJsonFrame, RealtimeRawJsonFrame>(
                    MethodType.DuplexStreaming,
                    ResolveRealtimeStreamServiceName(),
                    ResolveRealtimeStreamOpenMethodName(),
                    RawJsonFrameMarshaller,
                    RawJsonFrameMarshaller
                );

                Metadata metadata = BuildMetadata(headers);
                CallOptions options = new CallOptions(metadata, null, connectionCts.Token);

                streamCall = callInvoker.AsyncDuplexStreamingCall(method, null, options);

                SetState(RealtimeTransportState.Connected);
                Connected?.Invoke();
                _ = ReceiveLoopAsync(connectionCts.Token);

                Debug.Log("[GrpcStreamingRealtimeTransport] Connected to " + target);
                return true;
            }
            catch (OperationCanceledException)
            {
                return FailConnect("gRPC streaming connect canceled.");
            }
            catch (Exception ex)
            {
                return FailConnect("gRPC streaming connect failed: " + ex.Message);
            }
#endif
        }

        //* پیام خام آماده‌شده توسط کُر را داخل فیلد rawJson به سرور جی‌آر‌پی‌سی می‌فرستد.
        public async Task<bool> SendAsync(string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(message)) return false;

#if UNITY_WEBGL && !UNITY_EDITOR
            ErrorReceived?.Invoke("gRPC streaming send is not supported in WebGL build.");
            return false;
#else
            if (!IsConnected || streamCall == null) return false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await streamCall.RequestStream.WriteAsync(RealtimeRawJsonFrame.FromRawJson(message));
                return true;
            }
            catch (OperationCanceledException)
            {
                ErrorReceived?.Invoke("gRPC streaming send canceled.");
                return false;
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke("gRPC streaming send failed: " + ex.Message);
                await HandleRemoteCloseAsync("gRPC streaming send failed", CancellationToken.None);
                return false;
            }
#endif
        }

        //* اتصال فعال جی‌آر‌پی‌سی استریمینگ را با دلیل مشخص می‌بندد و پاکسازی را با تایم‌اوت انجام می‌دهد تا ادیتور در ریلود دامین گیر نکند.
        public async Task DisconnectAsync(string reason = "Client disconnect", CancellationToken cancellationToken = default)
        {
            if (isDisconnecting) return;

            isDisconnecting = true;
            SetState(RealtimeTransportState.Disconnecting);

            await CleanupStreamAsync(reason, cancellationToken, true);

            SetState(RealtimeTransportState.Disconnected);
            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "Client disconnect" : reason);
            isDisconnecting = false;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        //* حلقه دریافت پاسخ‌های استریم است و rawJson دریافتی را به کُر ریل‌تایم تحویل می‌دهد.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && streamCall != null)
                {
                    bool hasNext = await streamCall.ResponseStream.MoveNext(cancellationToken);
                    if (!hasNext) break;

                    RealtimeRawJsonFrame frame = streamCall.ResponseStream.Current;
                    if (frame != null && frame.HasPayload()) MessageReceived?.Invoke(frame.RawJson);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RpcException ex)
            {
                if (!isDisconnecting) ErrorReceived?.Invoke("gRPC streaming receive failed: " + ex.StatusCode + " | " + ex.Status.Detail);
            }
            catch (Exception ex)
            {
                if (!isDisconnecting) ErrorReceived?.Invoke("gRPC streaming receive failed: " + ex.Message);
            }

            if (!isDisconnecting) await HandleRemoteCloseAsync("gRPC streaming closed by remote.", CancellationToken.None);
        }

        //* فریم خام را برای مارشالر جی‌آر‌پی‌سی به بایت پروتوباف تبدیل می‌کند.
        private static byte[] SerializeFrameForGrpc(RealtimeRawJsonFrame frame)
        {
            return RealtimeRawJsonFrame.ToProtoBytes(frame);
        }

        //* بایت پروتوباف دریافتی از مارشالر جی‌آر‌پی‌سی را به فریم خام تبدیل می‌کند.
        private static RealtimeRawJsonFrame DeserializeFrameFromGrpc(byte[] bytes)
        {
            return RealtimeRawJsonFrame.FromProtoBytes(bytes);
        }

        //* هدرهای ورودی را به متادیتای جی‌آر‌پی‌سی تبدیل می‌کند.
        private Metadata BuildMetadata(Dictionary<string, string> headers)
        {
            Metadata metadata = new Metadata();

            AddMetadata(metadata, "x-metaverse-client", Application.platform.ToString());
            AddMetadata(metadata, "x-metaverse-version", Application.version);
            AddMetadata(metadata, "x-client-name", ServerConfig.ClientName);
            AddMetadata(metadata, "x-client-version", ServerConfig.ClientVersion);

            if (headers == null) return metadata;

            foreach (KeyValuePair<string, string> pair in headers)
            {
                AddMetadata(metadata, pair.Key, pair.Value);
            }

            return metadata;
        }

        //* یک مقدار متادیتا را با کلید امن و حروف کوچک اضافه می‌کند.
        private void AddMetadata(Metadata metadata, string key, string value)
        {
            if (metadata == null) return;
            if (string.IsNullOrWhiteSpace(key)) return;

            string safeKey = key.Trim().ToLowerInvariant();
            string safeValue = value ?? string.Empty;

            metadata.Add(safeKey, safeValue);
        }

        //* اتصال بسته‌شده از سمت سرور یا خطای استریم را به رویداد قطع اتصال تبدیل می‌کند.
        private async Task HandleRemoteCloseAsync(string reason, CancellationToken cancellationToken)
        {
            if (isDisconnecting) return;

            isDisconnecting = true;
            SetState(RealtimeTransportState.Disconnecting);

            await CleanupStreamAsync(reason, cancellationToken, false);

            SetState(RealtimeTransportState.Disconnected);
            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "gRPC streaming closed." : reason);
            isDisconnecting = false;
        }

        //* نام سرویس جی‌آر‌پی‌سی اِستریمینگ ریل‌تایم را از سرورکانفیگ بخش ریل‌تایم می‌خواند.
        private string ResolveRealtimeStreamServiceName()
        {
            return string.IsNullOrWhiteSpace(ServerConfig.RealtimeStreamServiceName) ? RealtimeServiceName : ServerConfig.RealtimeStreamServiceName;
        }

        //* نام متد اوپن جی‌آر‌پی‌سی اِستریمینگ ریل‌تایم را از سرورکانفیگ بخش ریل‌تایم می‌خواند.
        private string ResolveRealtimeStreamOpenMethodName()
        {
            return string.IsNullOrWhiteSpace(ServerConfig.RealtimeStreamOpenMethodName) ? RealtimeOpenMethodName : ServerConfig.RealtimeStreamOpenMethodName;
        }

        //* آدرس ورودی را به target قابل استفاده برای Channel جی‌آر‌پی‌سی تبدیل می‌کند.
        private string ResolveGrpcTarget(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return ServerConfig.BuildRealtimeGrpcStreamingTarget();

            string value = url.Trim();
            if (IsWebSocketUrl(value)) return ServerConfig.BuildRealtimeGrpcStreamingTarget();

            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                int port = uri.Port > 0 ? uri.Port : ResolveDefaultPort(uri.Scheme);
                return uri.Host + ":" + port;
            }

            int slashIndex = value.IndexOf('/');
            if (slashIndex >= 0) value = value.Substring(0, slashIndex);

            return string.IsNullOrWhiteSpace(value) ? ServerConfig.BuildRealtimeGrpcStreamingTarget() : value;
        }

        //* بر اساس آدرس یا تنظیمات مرکزی، نوع اعتبار کانال جی‌آر‌پی‌سی را مشخص می‌کند.
        private ChannelCredentials ResolveChannelCredentials(string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                string value = url.Trim().ToLowerInvariant();
                if (value.StartsWith("https://") || value.StartsWith("grpcs://")) return new SslCredentials();
                if (value.StartsWith("http://") || value.StartsWith("grpc://")) return ChannelCredentials.Insecure;
            }

            return ServerConfig.RealtimeGrpcStreamingEndpoint.UseTls ? new SslCredentials() : ChannelCredentials.Insecure;
        }

        //* پورت پیش‌فرض را بر اساس اسکیم آدرس مشخص می‌کند.
        private int ResolveDefaultPort(string scheme)
        {
            string safeScheme = string.IsNullOrWhiteSpace(scheme) ? string.Empty : scheme.ToLowerInvariant();
            if (safeScheme == "https" || safeScheme == "grpcs") return 443;
            if (safeScheme == "http" || safeScheme == "grpc") return 80;
            return ServerConfig.RealtimeGrpcStreamingEndpoint.Port;
        }

        //* بررسی می‌کند آدرس ورودی از نوع وب‌سوکت است یا نه.
        private bool IsWebSocketUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            string value = url.Trim().ToLowerInvariant();
            return value.StartsWith("ws://") || value.StartsWith("wss://");
        }
#endif

        //* منابع استریم، کال و کانال جی‌آر‌پی‌سی را پاکسازی می‌کند و برای جلوگیری از گیر کردن ادیتور، هیچ عملیات شبکه‌ای را بی‌نهایت منتظر نمی‌ماند.
        private async Task CleanupStreamAsync(string reason, CancellationToken cancellationToken, bool completeRequestStream)
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            AsyncDuplexStreamingCall<RealtimeRawJsonFrame, RealtimeRawJsonFrame> callToClose = streamCall;
            Channel channelToClose = channel;
            CancellationTokenSource ctsToDispose = connectionCts;

            streamCall = null;
            callInvoker = null;
            channel = null;
            connectionCts = null;

            try
            {
                ctsToDispose?.Cancel();
            }
            catch
            {
                // لغو توکن فقط برای آزادسازی سریع حلقه دریافت است و خطای آن مهم نیست.
            }

            if (callToClose != null)
            {
                try
                {
                    if (completeRequestStream)
                    {
                        Task completeTask = callToClose.RequestStream.CompleteAsync();
                        await WaitForTaskWithTimeoutAsync(completeTask, CleanupTimeoutMs);
                    }
                }
                catch
                {
                    // کامل کردن استریم در زمان قطع شبکه یا خروج از پلی‌مود ممکن است خطا بدهد.
                }

                try
                {
                    callToClose.Dispose();
                }
                catch
                {
                    // دیسپوز کال نباید مسیر قطع اتصال را متوقف کند.
                }
            }

            if (channelToClose != null)
            {
                try
                {
                    Task shutdownTask = channelToClose.ShutdownAsync();
                    await WaitForTaskWithTimeoutAsync(shutdownTask, CleanupTimeoutMs);
                }
                catch
                {
                    // خاموش کردن کانال ممکن است بعد از قطع شبکه خطا بدهد و اینجا فقط آزادسازی مهم است.
                }
            }

            try
            {
                ctsToDispose?.Dispose();
            }
            catch
            {
                // دیسپوز توکن نباید خطای ثانویه بسازد.
            }
#else
            await Task.CompletedTask;
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        //* یک تسک شبکه‌ای را فقط تا مدت محدود منتظر می‌ماند تا خروج از پلی‌مود یا ریلود دامین قفل نشود.
        private static async Task<bool> WaitForTaskWithTimeoutAsync(Task task, int timeoutMs)
        {
            if (task == null) return true;

            if (timeoutMs <= 0)
            {
                await task;
                return true;
            }

            Task completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (completedTask != task) return false;

            await task;
            return true;
        }

        //* خاموش کردن کانال را بدون بلاک کردن دیسپوز ادیتور انجام می‌دهد.
        private static async Task ShutdownChannelNoAwaitAsync(Channel channelToClose)
        {
            if (channelToClose == null) return;

            try
            {
                await WaitForTaskWithTimeoutAsync(channelToClose.ShutdownAsync(), CleanupTimeoutMs);
            }
            catch
            {
                // خاموش کردن کانال در مسیر دیسپوز نباید ادیتور را متوقف کند.
            }
        }
#endif

        //* خطای اتصال را ثبت می‌کند و وضعیت ترنسپورت را Failed می‌گذارد.
        private bool FailConnect(string message)
        {
            SetState(RealtimeTransportState.Failed);
            ErrorReceived?.Invoke(message);
            Debug.LogWarning("[GrpcStreamingRealtimeTransport] " + message);
            return false;
        }

        //* وضعیت داخلی ترنسپورت را تغییر می‌دهد.
        private void SetState(RealtimeTransportState nextState)
        {
            state = nextState;
        }

        //* بررسی می‌کند استریم جی‌آر‌پی‌سی فعال است یا نه.
        private bool HasActiveStream()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            return streamCall != null && !isDisconnecting;
#endif
        }

        //* پاکسازی نهایی ترنسپورت را بدون بلاک کردن ادیتور انجام می‌دهد و هیچ await همزمان داخل دیسپوز اجرا نمی‌کند.
        public void Dispose()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            isDisconnecting = true;
            SetState(RealtimeTransportState.Disconnecting);

            AsyncDuplexStreamingCall<RealtimeRawJsonFrame, RealtimeRawJsonFrame> callToClose = streamCall;
            Channel channelToClose = channel;
            CancellationTokenSource ctsToDispose = connectionCts;

            streamCall = null;
            callInvoker = null;
            channel = null;
            connectionCts = null;

            try
            {
                ctsToDispose?.Cancel();
            }
            catch
            {
                // لغو توکن در دیسپوز فقط برای آزادسازی سریع حلقه دریافت است.
            }

            try
            {
                callToClose?.Dispose();
            }
            catch
            {
                // دیسپوز کال نباید ادیتور را متوقف کند.
            }

            try
            {
                ctsToDispose?.Dispose();
            }
            catch
            {
                // دیسپوز توکن نباید خطای ثانویه بسازد.
            }

            if (channelToClose != null) _ = ShutdownChannelNoAwaitAsync(channelToClose);

            SetState(RealtimeTransportState.Disconnected);
#else
            SetState(RealtimeTransportState.Disconnected);
#endif
        }
    }
}

//* این فایل ترنسپورت جی‌آر‌پی‌سی استریمینگ را به قرارداد IRealtimeTransport وصل می‌کند.
//* مسیر ارسال نهایی این فایل streamCall.RequestStream.WriteAsync است و پیام را بدون تغییر در rawJson حمل می‌کند.
