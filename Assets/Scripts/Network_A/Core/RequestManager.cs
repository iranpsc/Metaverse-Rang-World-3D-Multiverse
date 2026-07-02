using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Core
{
    public static class RequestManager
    {
        private static CancellationTokenSource _globalCts = new CancellationTokenSource();
        static readonly ConcurrentQueue<IRequestItem> _queue = new ConcurrentQueue<IRequestItem>();
        static int _isProcessing;

        //* Cancels all network requests when the application quits.
        public static void OnApplicationQuit()
        {
            NetworkFileLogger.Info("REQUEST_MANAGER", "OnApplicationQuit called. Global cancellation requested.");

            if (_globalCts != null)
            {
                _globalCts.Cancel();
                _globalCts.Dispose();
            }

            _globalCts = new CancellationTokenSource();
        }

        //* Main unified entry point for all network requests.
        public static Task<ApiResult<T>> Send<T>(string url, string method, object payload, bool auth, CancellationToken ct = default(CancellationToken), string logTag = "")
        {
            return Send<T>(url, method, payload, auth, null, ct, logTag);
        }

        //* Main unified entry point with custom headers.
        public static Task<ApiResult<T>> Send<T>(string url, string method, object payload, bool auth, Dictionary<string, string> headers, CancellationToken ct = default(CancellationToken), string logTag = "")
        {
            string requestId = Guid.NewGuid().ToString("N");
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_globalCts.Token, ct);
            var item = new RequestItem<T>
            {
                Action = delegate { return SendInternal<T>(url, method, payload, auth, headers, linkedCts.Token, 0, logTag, requestId); },
                Tcs = new TaskCompletionSource<ApiResult<T>>()
            };

            _queue.Enqueue(item);
            NetworkFileLogger.Request(requestId, "ENQUEUED", url, method, 0, "auth=" + auth + " logTag=" + logTag + " queueCount=" + _queue.Count);
            var ignored = Process();
            return item.Tcs.Task;
        }

        //* Processes queued requests one by one.
        static async Task Process()
        {
            if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
            {
                NetworkFileLogger.Info("REQUEST_MANAGER", "Process already running. QueueCount=" + _queue.Count);
                return;
            }

            NetworkFileLogger.Info("REQUEST_MANAGER", "Queue processor started. QueueCount=" + _queue.Count);

            try
            {
                while (true)
                {
                    IRequestItem item;
                    while (_queue.TryDequeue(out item))
                    {
                        NetworkFileLogger.Info("REQUEST_MANAGER", "Dequeued item. RemainingQueue=" + _queue.Count);
                        await item.Execute();
                    }

                    Interlocked.Exchange(ref _isProcessing, 0);
                    NetworkFileLogger.Info("REQUEST_MANAGER", "Queue processor idle. QueueEmpty=" + _queue.IsEmpty);
                    if (_queue.IsEmpty) return;
                    if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Network_A.RequestManager.Process] " + ex);
                NetworkFileLogger.Exception("REQUEST_MANAGER_PROCESS", ex);
                Interlocked.Exchange(ref _isProcessing, 0);
                if (!_queue.IsEmpty) Process();
            }
        }

        //* Builds and sends the real UnityWebRequest, then parses the response.
        static async Task<ApiResult<T>> SendInternal<T>(string url, string method, object payload, bool auth, Dictionary<string, string> headers, CancellationToken ct, int retry, string logTag, string requestId)
        {
            NetworkFileLogger.Request(requestId, "START", url, method, 0, "auth=" + auth + " retry=" + retry + " payloadType=" + ReadPayloadType(payload));

            using (UnityWebRequest req = new UnityWebRequest(url, method))
            {
                req.timeout = ServerConfig.TimeoutSeconds;
                req.downloadHandler = new DownloadHandlerBuffer();

                ApplyDefaultHeaders(req);
                ApplyCustomHeaders(req, headers);
                ApplyAuthHeaders(req, auth, requestId);
                ApplyBody(req, method, payload, requestId);

                UnityWebRequest completed;
                try
                {
                    NetworkFileLogger.Request(requestId, "SEND", url, method, 0, "timeout=" + req.timeout);
                    completed = await UnityWebRequestAsync.SendAsync(req, ct);
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("REQUEST_MANAGER_SEND", ex);
                    return ApiResult<T>.Failure(ex.Message, 0, true, string.Empty, new byte[0]);
                }

                ApiResult<T> parsed = Parse<T>(completed, logTag, requestId);

                if (ShouldRefresh(parsed, auth, retry))
                {
                    NetworkFileLogger.Info("REQUEST_MANAGER", "Starting refresh. requestId=" + requestId + " reason=" + parsed.ErrorMessage);
                    bool refreshed = await AuthRefreshManager.Refresh();
                    NetworkFileLogger.Auth("REQUEST_MANAGER_REFRESH", refreshed, refreshed ? "Refresh succeeded" : "Refresh failed", string.Empty, HasAccessToken(), HasRefreshToken());

                    if (refreshed) return await SendInternal<T>(url, method, payload, auth, headers, ct, retry + 1, logTag, requestId);
                    return ApiResult<T>.Failure("Refresh token failed", parsed.StatusCode == 0 ? 401 : parsed.StatusCode, false, parsed.RawBody, parsed.RawBytes);
                }

                return parsed;
            }
        }

        //* Applies base headers shared by all requests.
        static void ApplyDefaultHeaders(UnityWebRequest req)
        {
            req.SetRequestHeader("Accept", "application/json, application/grpc-web+proto");
            req.SetRequestHeader("X-Metaverse-Client", Application.platform.ToString());
            req.SetRequestHeader("X-Metaverse-Version", Application.version);
        }

        //* Applies caller-provided headers.
        static void ApplyCustomHeaders(UnityWebRequest req, Dictionary<string, string> headers)
        {
            if (headers == null) return;
            foreach (var pair in headers)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                req.SetRequestHeader(pair.Key, pair.Value ?? string.Empty);
            }
        }

        //* Attaches access token for authenticated requests.
        static void ApplyAuthHeaders(UnityWebRequest req, bool auth)
        {
            ApplyAuthHeaders(req, auth, string.Empty);
        }

        //* Attaches access token for authenticated requests and logs token state safely.
        static void ApplyAuthHeaders(UnityWebRequest req, bool auth, string requestId)
        {
            if (!auth)
            {
                NetworkFileLogger.Request(requestId, "AUTH_HEADER_SKIPPED", req.url, req.method, 0, "auth=false");
                return;
            }

            string token = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                NetworkFileLogger.Warning("REQUEST_MANAGER", "Auth request has no access token. url=" + req.url);
                return;
            }

            req.SetRequestHeader("Authorization", "Bearer " + token);
            //   req.SetRequestHeader("auth-token", token);
            NetworkFileLogger.TokenState("REQUEST_AUTH_HEADER_ATTACHED", token, SecureTokenStorage.GetRefreshToken());
        }

        //* Applies request body if the HTTP method supports payload.
        static void ApplyBody(UnityWebRequest req, string method, object payload)
        {
            ApplyBody(req, method, payload, string.Empty);
        }

        //* Applies request body if the HTTP method supports payload and logs payload size.
        static void ApplyBody(UnityWebRequest req, string method, object payload, string requestId)
        {
            if (payload == null)
            {
                NetworkFileLogger.Request(requestId, "BODY_SKIPPED", req.url, req.method, 0, "payload=null");
                return;
            }

            if (method != UnityWebRequest.kHttpVerbPOST && method != UnityWebRequest.kHttpVerbPUT && method != "PATCH")
            {
                NetworkFileLogger.Request(requestId, "BODY_SKIPPED", req.url, req.method, 0, "method_without_body");
                return;
            }

            byte[] bodyRaw;
            if (payload is byte[]) bodyRaw = (byte[])payload;
            else if (payload is string) bodyRaw = Encoding.UTF8.GetBytes((string)payload);
            else bodyRaw = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));

            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            NetworkFileLogger.Request(requestId, "BODY_ATTACHED", req.url, req.method, 0, "bytes=" + bodyRaw.Length + " payloadType=" + ReadPayloadType(payload));
        }

        //* Converts UnityWebRequest output to ApiResult.
        static ApiResult<T> Parse<T>(UnityWebRequest req, string logTag)
        {
            return Parse<T>(req, logTag, string.Empty);
        }

        //* Converts UnityWebRequest output to ApiResult with null-safe binary handling.
        static ApiResult<T> Parse<T>(UnityWebRequest req, string logTag, string requestId)
        {
            string text = string.Empty;
            byte[] bytes = new byte[0];
            int statusCode = 0;

            Dictionary<string, string> responseHeaders = null;

            if (req != null)
            {
                statusCode = (int)req.responseCode;
                responseHeaders = req.GetResponseHeaders();

                if (req.downloadHandler != null)
                {
                    text = req.downloadHandler.text ?? string.Empty;
                    bytes = req.downloadHandler.data ?? new byte[0];
                }
            }

            string grpcStatus = ReadResponseHeader(responseHeaders, "grpc-status");
            string grpcMessage = DecodeGrpcHeaderMessage(ReadResponseHeader(responseHeaders, "grpc-message"));
            int grpcStatusCode = ParseGrpcStatusCode(grpcStatus, statusCode);

            if (!string.IsNullOrWhiteSpace(logTag) && req != null)
            {
                Debug.Log("[" + logTag + "] URL=" + req.url);
                Debug.Log("[" + logTag + "] StatusCode=" + statusCode);
                Debug.Log("[" + logTag + "] Result=" + req.result);
                Debug.Log("[" + logTag + "] Error=" + req.error);
                Debug.Log("[" + logTag + "] Body=" + text);
                if (!string.IsNullOrEmpty(grpcStatus)) Debug.Log("[" + logTag + "] GrpcStatus=" + grpcStatus);
                if (!string.IsNullOrEmpty(grpcMessage)) Debug.Log("[" + logTag + "] GrpcMessage=" + grpcMessage);
            }

            NetworkFileLogger.Request(requestId, "RESPONSE", req != null ? req.url : "<null>", req != null ? req.method : "<null>", statusCode, "result=" + (req != null ? req.result.ToString() : "<null>") + " error=" + (req != null ? req.error : "<null>") + " textLength=" + text.Length + " bytes=" + bytes.Length + " grpcStatus=" + grpcStatus + " grpcMessage=" + grpcMessage);

            if (req == null)
            {
                return ApiResult<T>.Failure("UnityWebRequest is null", statusCode, true, text, bytes);
            }

            if (IsGrpcErrorStatus(grpcStatus))
            {
                string message = !string.IsNullOrEmpty(grpcMessage) ? grpcMessage : (!string.IsNullOrEmpty(text) ? text : "gRPC error " + grpcStatus);
                NetworkFileLogger.Request(requestId, "GRPC_WEB_ERROR", req.url, req.method, grpcStatusCode, "message=" + message);
                return ApiResult<T>.Failure(message, grpcStatusCode, false, text, bytes);
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                bool isNetworkError = req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.DataProcessingError;
                return ApiResult<T>.Failure(string.IsNullOrEmpty(text) ? req.error : text, statusCode, isNetworkError, text, bytes);
            }

            if (statusCode == 401 || statusCode == 403)
            {
                return ApiResult<T>.Failure(string.IsNullOrEmpty(text) ? "Unauthorized" : text, statusCode, false, text, bytes);
            }

            if (typeof(T) == typeof(byte[]))
            {
                if (bytes == null) bytes = new byte[0];
                if (bytes.Length == 0)
                {
                    string emptyMessage = BuildEmptyGrpcWebResponseMessage(responseHeaders);
                    NetworkFileLogger.Request(requestId, "EMPTY_BINARY_RESPONSE", req.url, req.method, statusCode, emptyMessage);
                    return ApiResult<T>.Failure(emptyMessage, statusCode, false, text, bytes);
                }

                NetworkFileLogger.Request(requestId, "SUCCESS_BYTES", req.url, req.method, statusCode, "bytes=" + bytes.Length);
                return ApiResult<T>.Success((T)(object)bytes, statusCode, text, bytes);
            }

            if (string.IsNullOrEmpty(text)) return ApiResult<T>.Success(default(T), statusCode, text, bytes);

            try
            {
                T data = JsonUtility.FromJson<T>(text);
                return ApiResult<T>.Success(data, statusCode, text, bytes);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REQUEST_MANAGER_PARSE", ex);
                return ApiResult<T>.Failure(ex.Message, statusCode, false, text, bytes);
            }
        }


        //* Reads a response header with case-insensitive fallback.
        static string ReadResponseHeader(Dictionary<string, string> headers, string key)
        {
            if (headers == null || string.IsNullOrEmpty(key)) return string.Empty;

            string value;
            if (headers.TryGetValue(key, out value)) return value ?? string.Empty;

            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) return pair.Value ?? string.Empty;
            }

            return string.Empty;
        }

        //* Decodes grpc-message header so auth errors can be mapped correctly.
        static string DecodeGrpcHeaderMessage(string value)
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

        //* Returns true when gRPC status header contains a real error.
        static bool IsGrpcErrorStatus(string grpcStatus)
        {
            if (string.IsNullOrEmpty(grpcStatus)) return false;
            return grpcStatus.Trim() != "0";
        }

        //* Parses gRPC status code and keeps HTTP status as fallback.
        static int ParseGrpcStatusCode(string grpcStatus, int fallbackStatusCode)
        {
            int parsed;
            if (int.TryParse(grpcStatus, out parsed)) return parsed;
            return fallbackStatusCode;
        }

        //* Builds a safer message for empty gRPC-Web responses and includes exposed header info when available.
        static string BuildEmptyGrpcWebResponseMessage(Dictionary<string, string> headers)
        {
            string grpcStatus = ReadResponseHeader(headers, "grpc-status");
            string grpcMessage = DecodeGrpcHeaderMessage(ReadResponseHeader(headers, "grpc-message"));

            if (IsGrpcErrorStatus(grpcStatus))
            {
                return !string.IsNullOrEmpty(grpcMessage) ? grpcMessage : "gRPC error " + grpcStatus;
            }

            return "Empty binary response";
        }

        //* Decides if RequestManager should run refresh and retry the same request.
        static bool ShouldRefresh<T>(ApiResult<T> result, bool auth, int retry)
        {
            if (!auth) return false;
            if (retry >= 1) return false;
            if (result == null) return true;
            if (result.StatusCode == 401 || result.StatusCode == 403) return true;
            if (result.IsSuccess) return false;

            string message = result.ErrorMessage ?? string.Empty;
            if (message.IndexOf("jwt", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (message.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (message.IndexOf("empty binary response", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        //* Reads payload type safely for logs.
        static string ReadPayloadType(object payload)
        {
            return payload == null ? "<null>" : payload.GetType().Name;
        }

        //* Checks if an access token exists.
        static bool HasAccessToken()
        {
            return !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken());
        }

        //* Checks if a refresh token exists.
        static bool HasRefreshToken()
        {
            return !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken());
        }
    }
}
