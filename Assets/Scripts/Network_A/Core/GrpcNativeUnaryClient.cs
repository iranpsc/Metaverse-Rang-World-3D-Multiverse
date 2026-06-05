#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
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

        private static readonly Marshaller<byte[]> ByteArrayMarshaller = Marshallers.Create(
            bytes => bytes ?? new byte[0],
            bytes => bytes ?? new byte[0]
        );

        //* Sends a native gRPC unary request using a service and method name.
        public static async Task<ApiResult<byte[]>> SendAsync(string serviceName, string methodName, byte[] protoMessage, bool auth, Dictionary<string, string> headers = null, CancellationToken ct = default(CancellationToken), string logTag = "")
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return ApiResult<byte[]>.Failure("Native gRPC service name is empty.", 0, false);
            if (string.IsNullOrWhiteSpace(methodName)) return ApiResult<byte[]>.Failure("Native gRPC method name is empty.", 0, false);

            EnsureChannel();

            var method = new Method<byte[], byte[]>(MethodType.Unary, serviceName, methodName, ByteArrayMarshaller, ByteArrayMarshaller);
            var metadata = GrpcMetadataAdapter.BuildMetadata(auth, headers);
            var deadline = DateTime.UtcNow.AddSeconds(ServerConfig.TimeoutSeconds);
            var options = new CallOptions(metadata, deadline, ct);

            try
            {
                NetworkFileLogger.Request(logTag, "NATIVE_GRPC_SEND", ServerConfig.BuildGrpcNativeTarget(), "RPC", 0, serviceName + "/" + methodName + " bytes=" + ReadLength(protoMessage) + " auth=" + auth);

                byte[] responseBytes = await _callInvoker.AsyncUnaryCall(method, null, options, protoMessage ?? new byte[0]);

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

        //* Sends a native gRPC unary request using a full method path.
        public static Task<ApiResult<byte[]>> SendByPathAsync(string methodPath, byte[] protoMessage, bool auth, Dictionary<string, string> headers = null, CancellationToken ct = default(CancellationToken), string logTag = "")
        {
            string serviceName;
            string methodName;

            if (!TrySplitMethodPath(methodPath, out serviceName, out methodName)) return Task.FromResult(ApiResult<byte[]>.Failure("Invalid native gRPC method path: " + methodPath, 0, false));

            return SendAsync(serviceName, methodName, protoMessage, auth, headers, ct, logTag);
        }

        //* Creates the native gRPC channel once and reuses it.
        private static void EnsureChannel()
        {
            if (_channel != null && _callInvoker != null) return;

            string target = ServerConfig.BuildGrpcNativeTarget();
            ChannelCredentials credentials = ServerConfig.GrpcNativeEndpoint.UseTls ? new SslCredentials() : ChannelCredentials.Insecure;

            _channel = new Channel(target, credentials);
            _callInvoker = _channel.CreateCallInvoker();

            NetworkFileLogger.Info("NATIVE_GRPC", "Native channel created. target=" + target + " tls=" + ServerConfig.GrpcNativeEndpoint.UseTls);
        }

        //* Shuts down the native gRPC channel.
        public static async Task ShutdownAsync()
        {
            if (_channel == null) return;

            try
            {
                await _channel.ShutdownAsync();
                NetworkFileLogger.Info("NATIVE_GRPC", "Native channel shutdown completed.");
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("NATIVE_GRPC_SHUTDOWN", ex);
            }
            finally
            {
                _channel = null;
                _callInvoker = null;
            }
        }

        //* Splits a native gRPC method path into service and method.
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

        //* Returns byte array length safely.
        private static int ReadLength(byte[] bytes)
        {
            return bytes == null ? 0 : bytes.Length;
        }

        //* Detects connection-level native gRPC errors.
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