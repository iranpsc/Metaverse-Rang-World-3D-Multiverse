#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
/*
این فایل ارسال Unary جی‌آر‌پی‌سی نیتیو را انجام می‌دهد.
در ادیتور برای جلوگیری از گیر کردن Unity هنگام Reload Domain، کانال به صورت موقت ساخته و با تایم‌اوت کوتاه بسته می‌شود.
در بیلد نیتیو، کانال قابل استفاده مجدد می‌ماند تا کارایی بهتر باشد.
WebGL این فایل را کامپایل نمی‌کند.
*/

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Network_A.Auth;

namespace Network_A.Core
{
    public static class GrpcNativeUnaryClient
    {
        private static Channel _channel;
        private static CallInvoker _callInvoker;
        private const int ShutdownTimeoutMs = 1500;

        private static readonly Marshaller<byte[]> ByteArrayMarshaller = Marshallers.Create(
            bytes => bytes ?? new byte[0],
            bytes => bytes ?? new byte[0]
        );

        //* این تابع یک درخواست Unary جی‌آر‌پی‌سی نیتیو را با نام سرویس و متد ارسال می‌کند.
        public static async Task<ApiResult<byte[]>> SendAsync(string serviceName, string methodName, byte[] protoMessage, bool auth, Dictionary<string, string> headers = null, CancellationToken ct = default(CancellationToken), string logTag = "")
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return ApiResult<byte[]>.Failure("Native gRPC service name is empty.", 0, false);
            if (string.IsNullOrWhiteSpace(methodName)) return ApiResult<byte[]>.Failure("Native gRPC method name is empty.", 0, false);

#if UNITY_EDITOR
            return await SendWithTemporaryEditorChannelAsync(serviceName, methodName, protoMessage, auth, headers, ct, logTag);
#else
            return await SendWithSharedChannelAsync(serviceName, methodName, protoMessage, auth, headers, ct, logTag);
#endif
        }

        //* این تابع یک درخواست Unary جی‌آر‌پی‌سی نیتیو را با مسیر کامل متد ارسال می‌کند.
        public static Task<ApiResult<byte[]>> SendByPathAsync(string methodPath, byte[] protoMessage, bool auth, Dictionary<string, string> headers = null, CancellationToken ct = default(CancellationToken), string logTag = "")
        {
            string serviceName;
            string methodName;

            if (!TrySplitMethodPath(methodPath, out serviceName, out methodName)) return Task.FromResult(ApiResult<byte[]>.Failure("Invalid native gRPC method path: " + methodPath, 0, false));

            return SendAsync(serviceName, methodName, protoMessage, auth, headers, ct, logTag);
        }

#if UNITY_EDITOR
        //* این تابع در ادیتور برای هر درخواست یک کانال موقت می‌سازد تا کانال باز، ریلود دامین را نگه ندارد.
        private static async Task<ApiResult<byte[]>> SendWithTemporaryEditorChannelAsync(string serviceName, string methodName, byte[] protoMessage, bool auth, Dictionary<string, string> headers, CancellationToken ct, string logTag)
        {
            Channel channel = null;

            try
            {
                CallInvoker callInvoker = CreateChannelAndInvoker(out channel);
                ApiResult<byte[]> result = await SendWithInvokerAsync(callInvoker, serviceName, methodName, protoMessage, auth, headers, ct, logTag);
                return result;
            }
            finally
            {
                await ShutdownChannelWithTimeoutAsync(channel, "NATIVE_GRPC_EDITOR_CHANNEL_SHUTDOWN");
            }
        }
#else
        //* این تابع در بیلد نیتیو از کانال مشترک استفاده می‌کند تا برای هر درخواست کانال جدید ساخته نشود.
        private static async Task<ApiResult<byte[]>> SendWithSharedChannelAsync(string serviceName, string methodName, byte[] protoMessage, bool auth, Dictionary<string, string> headers, CancellationToken ct, string logTag)
        {
            EnsureSharedChannel();
            return await SendWithInvokerAsync(_callInvoker, serviceName, methodName, protoMessage, auth, headers, ct, logTag);
        }
#endif

        //* این تابع عملیات واقعی ارسال Unary را روی کال‌این‌وُکِر داده‌شده انجام می‌دهد.
        private static async Task<ApiResult<byte[]>> SendWithInvokerAsync(CallInvoker callInvoker, string serviceName, string methodName, byte[] protoMessage, bool auth, Dictionary<string, string> headers, CancellationToken ct, string logTag)
        {
            var method = new Method<byte[], byte[]>(MethodType.Unary, serviceName, methodName, ByteArrayMarshaller, ByteArrayMarshaller);
            var metadata = GrpcMetadataAdapter.BuildMetadata(auth, headers);
            var deadline = DateTime.UtcNow.AddSeconds(ServerConfig.TimeoutSeconds);
            var options = new CallOptions(metadata, deadline, ct);

            try
            {
                NetworkFileLogger.Request(logTag, "NATIVE_GRPC_SEND", ServerConfig.BuildGrpcNativeTarget(), "RPC", 0, serviceName + "/" + methodName + " bytes=" + ReadLength(protoMessage) + " auth=" + auth);

                byte[] responseBytes = await callInvoker.AsyncUnaryCall(method, null, options, protoMessage ?? new byte[0]);

                NetworkFileLogger.Request(logTag, "NATIVE_GRPC_RESPONSE", ServerConfig.BuildGrpcNativeTarget(), "RPC", 200, serviceName + "/" + methodName + " bytes=" + ReadLength(responseBytes));

                return ApiResult<byte[]>.Success(responseBytes, 200, string.Empty, responseBytes);
            }
            catch (RpcException ex)
            {
                bool isNetworkError = IsNetworkError(ex.StatusCode);
                string message = string.IsNullOrEmpty(ex.Status.Detail) ? ex.StatusCode.ToString() : ex.Status.Detail;

                NetworkFileLogger.Warning("NATIVE_GRPC", "RpcException tag=" + logTag + " method=" + serviceName + "/" + methodName + " status=" + ex.StatusCode + " message=" + message);

                return ApiResult<byte[]>.Failure(message, (int)ex.StatusCode, isNetworkError, message, new byte[0]);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("NATIVE_GRPC_" + logTag, ex);
                return ApiResult<byte[]>.Failure(ex.Message, 0, true, ex.Message, new byte[0]);
            }
        }

#if !UNITY_EDITOR
        //* این تابع کانال مشترک جی‌آر‌پی‌سی نیتیو را در بیلد نیتیو می‌سازد و نگه می‌دارد.
        private static void EnsureSharedChannel()
        {
            if (_channel != null && _callInvoker != null) return;

            _callInvoker = CreateChannelAndInvoker(out _channel);
        }
#endif

        //* این تابع کانال جی‌آر‌پی‌سی را بر اساس تنظیمات ServerConfig می‌سازد.
        private static CallInvoker CreateChannelAndInvoker(out Channel channel)
        {
            string target = ServerConfig.BuildGrpcNativeTarget();
            ChannelCredentials credentials = ServerConfig.GrpcNativeEndpoint.UseTls ? new SslCredentials() : ChannelCredentials.Insecure;

            channel = new Channel(target, credentials);

            NetworkFileLogger.Info("NATIVE_GRPC", "Native channel created. target=" + target + " tls=" + ServerConfig.GrpcNativeEndpoint.UseTls);

            return channel.CreateCallInvoker();
        }

        //* این تابع کانال مشترک را با تایم‌اوت کوتاه خاموش می‌کند تا خروج از Play Mode گیر نکند.
        public static async Task ShutdownAsync()
        {
            Channel channel = _channel;
            _channel = null;
            _callInvoker = null;

            await ShutdownChannelWithTimeoutAsync(channel, "NATIVE_GRPC_SHUTDOWN");
        }

        //* این تابع خاموش کردن کانال را با سقف زمانی انجام می‌دهد و اجازه نمی‌دهد ادیتور معطل بماند.
        private static async Task ShutdownChannelWithTimeoutAsync(Channel channel, string logTag)
        {
            if (channel == null) return;

            try
            {
                Task shutdownTask = channel.ShutdownAsync();
                Task finishedTask = await Task.WhenAny(shutdownTask, Task.Delay(ShutdownTimeoutMs));

                if (finishedTask == shutdownTask)
                {
                    await shutdownTask;
                    NetworkFileLogger.Info("NATIVE_GRPC", logTag + " completed.");
                }
                else
                {
                    NetworkFileLogger.Warning("NATIVE_GRPC", logTag + " timed out after " + ShutdownTimeoutMs + "ms.");
                }
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception(logTag, ex);
            }
        }

        //* این تابع مسیر کامل متد جی‌آر‌پی‌سی را به نام سرویس و نام متد جدا می‌کند.
        private static bool TrySplitMethodPath(string methodPath, out string serviceName, out string methodName)
        {
            serviceName = string.Empty;
            methodName = string.Empty;

            if (string.IsNullOrWhiteSpace(methodPath)) return false;

            string path = methodPath.Trim();
            if (path.StartsWith("/")) path = path.Substring(1);

            int slashIndex = path.LastIndexOf('/');
            if (slashIndex <= 0 || slashIndex >= path.Length - 1) return false;

            serviceName = path.Substring(0, slashIndex);
            methodName = path.Substring(slashIndex + 1);

            return !string.IsNullOrWhiteSpace(serviceName) && !string.IsNullOrWhiteSpace(methodName);
        }

        //* این تابع طول آرایه بایت را به شکل امن برمی‌گرداند.
        private static int ReadLength(byte[] bytes)
        {
            return bytes == null ? 0 : bytes.Length;
        }

        //* این تابع خطاهای سطح اتصال جی‌آر‌پی‌سی نیتیو را تشخیص می‌دهد.
        private static bool IsNetworkError(StatusCode statusCode)
        {
            return statusCode == StatusCode.Unavailable ||
                   statusCode == StatusCode.DeadlineExceeded ||
                   statusCode == StatusCode.Internal ||
                   statusCode == StatusCode.Unknown;
        }
    }
}
#endif
