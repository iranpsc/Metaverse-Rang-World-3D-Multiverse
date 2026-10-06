using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Auth
{
    public static class AuthService
    {
        // این تابع Login را با Transport فعال ارسال و Tokenهای پاسخ موفق را ذخیره می‌کند.
        public static async Task<ApiResult<AuthResponseDto>> LoginAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] message = AuthProtoMapper.EncodeLoginLikeRequest(username, password);
            ApiResult<AuthResponseDto> result = await SendLoginAsync(
                ServerConfig.LoginUrl,
                message,
                cancellationToken);

            bool validResult = result != null &&
                               result.IsSuccess &&
                               result.Data != null &&
                               result.Data.success &&
                               !string.IsNullOrWhiteSpace(result.Data.accessToken);

            if (!validResult) return result;

            string currentRefreshToken = SecureTokenStorage.GetRefreshToken();
            SecureTokenStorage.SaveTokens(
                result.Data.accessToken,
                string.IsNullOrWhiteSpace(result.Data.refreshToken)
                    ? currentRefreshToken
                    : result.Data.refreshToken);

            return result;
        }

        // این تابع GetUserData را با Transport فعال ارسال و پاسخ یکپارچه کاربر را برمی‌گرداند.
        public static Task<ApiResult<GetUserDataResponseDto>> GetCurrentUserAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] message = AuthProtoMapper.EncodeEmptyRequest();
            return SendGetCurrentUserAsync(
                ServerConfig.GetUserDataUrl,
                message,
                cancellationToken);
        }

        // این تابع درخواست Login را برای Native یا gRPC-Web ارسال و پاسخ Auth را Decode می‌کند.
        private static async Task<ApiResult<AuthResponseDto>> SendLoginAsync(
            string url,
            byte[] protoMessage,
            CancellationToken cancellationToken)
        {
            if (ServerConfig.IsGrpcNative())
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                ApiResult<byte[]> raw = await GrpcNativeUnaryClient.SendAsync(
                    ServerConfig.ServiceName,
                    "Login",
                    protoMessage,
                    false,
                    null,
                    cancellationToken,
                    "AUTH_SERVICE_LOGIN_NATIVE");

                if (raw == null)
                    return ApiResult<AuthResponseDto>.Failure("Native auth result is null.", 0, true);

                if (!raw.IsSuccess)
                {
                    return ApiResult<AuthResponseDto>.Failure(
                        raw.ErrorMessage,
                        raw.StatusCode,
                        raw.IsNetworkError,
                        raw.RawBody,
                        raw.RawBytes);
                }

                AuthResponseDto dto = AuthProtoMapper.DecodeAuthResponse(ReadNativeBytes(raw));
                return ApiResult<AuthResponseDto>.Success(dto, raw.StatusCode, raw.RawBody, raw.RawBytes);
#else
                return ApiResult<AuthResponseDto>.Failure(
                    "Native auth is not enabled for this platform.",
                    0,
                    true);
#endif
            }

            byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(protoMessage);
            ApiResult<byte[]> webRaw = await RequestManager.Send<byte[]>(
                url,
                UnityWebRequest.kHttpVerbPOST,
                frame,
                false,
                BuildGrpcWebHeaders(),
                cancellationToken,
                "AUTH_SERVICE_LOGIN_WEB");

            if (webRaw == null)
                return ApiResult<AuthResponseDto>.Failure("gRPC-Web auth result is null.", 0, true);

            if (!webRaw.IsSuccess)
            {
                return ApiResult<AuthResponseDto>.Failure(
                    webRaw.ErrorMessage,
                    webRaw.StatusCode,
                    webRaw.IsNetworkError,
                    webRaw.RawBody,
                    webRaw.RawBytes);
            }

            byte[] webMessage;
            Dictionary<string, string> trailers;

            if (!AuthProtoMapper.TryDecodeGrpcWebUnaryResponse(webRaw.RawBytes, out webMessage, out trailers))
            {
                return ApiResult<AuthResponseDto>.Failure(
                    "Invalid gRPC-Web auth response.",
                    webRaw.StatusCode,
                    false,
                    webRaw.RawBody,
                    webRaw.RawBytes);
            }

            string grpcStatus = ReadTrailer(trailers, "grpc-status");

            if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0")
            {
                string grpcMessage = DecodeGrpcMessage(ReadTrailer(trailers, "grpc-message"));
                int parsedStatus;
                int status = int.TryParse(grpcStatus, out parsedStatus)
                    ? parsedStatus
                    : webRaw.StatusCode;

                return ApiResult<AuthResponseDto>.Failure(
                    grpcMessage,
                    status,
                    false,
                    webRaw.RawBody,
                    webRaw.RawBytes);
            }

            AuthResponseDto webDto = AuthProtoMapper.DecodeAuthResponse(webMessage);
            return ApiResult<AuthResponseDto>.Success(
                webDto,
                webRaw.StatusCode,
                webRaw.RawBody,
                webRaw.RawBytes);
        }

        // این تابع درخواست GetUserData را برای Native یا gRPC-Web ارسال و پاسخ کاربر را Decode می‌کند.
        private static async Task<ApiResult<GetUserDataResponseDto>> SendGetCurrentUserAsync(
            string url,
            byte[] protoMessage,
            CancellationToken cancellationToken)
        {
            if (ServerConfig.IsGrpcNative())
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                ApiResult<byte[]> raw = await GrpcNativeUnaryClient.SendAsync(
                    ServerConfig.ServiceName,
                    "GetUserData",
                    protoMessage,
                    true,
                    null,
                    cancellationToken,
                    "AUTH_SERVICE_GET_USER_NATIVE");

                if (raw == null)
                {
                    return ApiResult<GetUserDataResponseDto>.Failure(
                        "Native GetUserData result is null.",
                        0,
                        true);
                }

                if (!raw.IsSuccess)
                {
                    return ApiResult<GetUserDataResponseDto>.Failure(
                        raw.ErrorMessage,
                        raw.StatusCode,
                        raw.IsNetworkError,
                        raw.RawBody,
                        raw.RawBytes);
                }

                GetUserDataResponseDto dto = AuthProtoMapper.DecodeGetUserDataResponse(
                    ReadNativeBytes(raw));

                return ApiResult<GetUserDataResponseDto>.Success(
                    dto,
                    raw.StatusCode,
                    raw.RawBody,
                    raw.RawBytes);
#else
                return ApiResult<GetUserDataResponseDto>.Failure(
                    "Native GetUserData is not enabled for this platform.",
                    0,
                    true);
#endif
            }

            byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(protoMessage);
            ApiResult<byte[]> webRaw = await RequestManager.Send<byte[]>(
                url,
                UnityWebRequest.kHttpVerbPOST,
                frame,
                true,
                BuildGrpcWebHeaders(),
                cancellationToken,
                "AUTH_SERVICE_GET_USER_WEB");

            if (webRaw == null)
            {
                return ApiResult<GetUserDataResponseDto>.Failure(
                    "gRPC-Web GetUserData result is null.",
                    0,
                    true);
            }

            if (!webRaw.IsSuccess)
            {
                return ApiResult<GetUserDataResponseDto>.Failure(
                    webRaw.ErrorMessage,
                    webRaw.StatusCode,
                    webRaw.IsNetworkError,
                    webRaw.RawBody,
                    webRaw.RawBytes);
            }

            byte[] webMessage;
            Dictionary<string, string> trailers;

            if (!AuthProtoMapper.TryDecodeGrpcWebUnaryResponse(webRaw.RawBytes, out webMessage, out trailers))
            {
                return ApiResult<GetUserDataResponseDto>.Failure(
                    "Invalid gRPC-Web GetUserData response.",
                    webRaw.StatusCode,
                    false,
                    webRaw.RawBody,
                    webRaw.RawBytes);
            }

            string grpcStatus = ReadTrailer(trailers, "grpc-status");

            if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0")
            {
                string grpcMessage = DecodeGrpcMessage(ReadTrailer(trailers, "grpc-message"));
                int parsedStatus;
                int status = int.TryParse(grpcStatus, out parsedStatus)
                    ? parsedStatus
                    : webRaw.StatusCode;

                return ApiResult<GetUserDataResponseDto>.Failure(
                    grpcMessage,
                    status,
                    false,
                    webRaw.RawBody,
                    webRaw.RawBytes);
            }

            GetUserDataResponseDto webDto = AuthProtoMapper.DecodeGetUserDataResponse(webMessage);
            return ApiResult<GetUserDataResponseDto>.Success(
                webDto,
                webRaw.StatusCode,
                webRaw.RawBody,
                webRaw.RawBytes);
        }

        // این تابع Headerهای ثابت موردنیاز Envoy برای درخواست‌های gRPC-Web را برمی‌گرداند.
        internal static Dictionary<string, string> BuildGrpcWebHeaders()
        {
            return new Dictionary<string, string>
            {
                { "Content-Type", "application/grpc-web+proto" },
                { "Accept", "application/grpc-web+proto" },
                { "X-Grpc-Web", "1" },
                { "X-User-Agent", "grpc-web-unity" },
                { "X-Metaverse-Client", Application.platform.ToString() },
                { "X-Metaverse-Version", Application.version }
            };
        }

        // این تابع مقدار Trailer را با پشتیبانی از تفاوت حروف بزرگ و کوچک می‌خواند.
        internal static string ReadTrailer(Dictionary<string, string> trailers, string key)
        {
            if (trailers == null || string.IsNullOrWhiteSpace(key)) return string.Empty;

            string value;
            if (trailers.TryGetValue(key, out value)) return value ?? string.Empty;

            foreach (KeyValuePair<string, string> pair in trailers)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                    return pair.Value ?? string.Empty;
            }

            return string.Empty;
        }

        // این تابع بایت پاسخ Native را از Data و در صورت خالی بودن از RawBytes برمی‌گرداند.
        internal static byte[] ReadNativeBytes(ApiResult<byte[]> raw)
        {
            if (raw == null) return new byte[0];
            if (raw.Data != null && raw.Data.Length > 0) return raw.Data;
            return raw.RawBytes ?? new byte[0];
        }

        // این تابع متن grpc-message را برای تشخیص درست خطای Auth Decode می‌کند.
        internal static string DecodeGrpcMessage(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string normalized = value.Replace("+", " ");

            try
            {
                return UnityWebRequest.UnEscapeURL(normalized);
            }
            catch
            {
                return value;
            }
        }
    }
}
