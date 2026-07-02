using System;

namespace Network_A.Core
{
    [Serializable]
    public class ApiResult<T>
    {
        public bool IsSuccess;
        public T Data;
        public string ErrorMessage;
        public int StatusCode;
        public bool IsNetworkError;
        public string RawBody;
        public byte[] RawBytes;

        //* Creates a successful API result.
        public static ApiResult<T> Success(T data, int statusCode, string rawBody, byte[] rawBytes)
        {
            return new ApiResult<T>
            {
                IsSuccess = true,
                Data = data,
                StatusCode = statusCode,
                RawBody = rawBody ?? string.Empty,
                RawBytes = rawBytes ?? new byte[0]
            };
        }

        //* Creates a failed API result.
        public static ApiResult<T> Failure(string errorMessage, int statusCode = 0, bool isNetworkError = false, string rawBody = null, byte[] rawBytes = null)
        {
            return new ApiResult<T>
            {
                IsSuccess = false,
                ErrorMessage = errorMessage ?? string.Empty,
                StatusCode = statusCode,
                IsNetworkError = isNetworkError,
                RawBody = rawBody ?? string.Empty,
                RawBytes = rawBytes ?? new byte[0]
            };
        }
    }
}
