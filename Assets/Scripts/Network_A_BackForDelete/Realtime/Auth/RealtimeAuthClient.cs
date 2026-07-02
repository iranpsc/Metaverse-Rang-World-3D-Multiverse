using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.Realtime.Auth
{
    //* کلاینت احراز هویت ریل‌تایم است و فقط پیام system/auth را از طریق کُر ارسال می‌کند.
    public class RealtimeAuthClient : IDisposable
    {
        private readonly RealtimeClient realtimeClient;
        private bool isDisposed;
        private bool isAuthenticating;
        private bool isAuthenticated;
        private string connectionId = string.Empty;
        private string userId = string.Empty;
        private string lastAuthMessageId = string.Empty;

        public event Action<string, string> Authenticated;
        public event Action<RealtimeError> AuthenticationFailed;
        public event Action<string> AuthLogReceived;

        public bool IsAuthenticating => isAuthenticating;
        public bool IsAuthenticated => isAuthenticated;
        public string ConnectionId => connectionId;
        public string UserId => userId;

        #region <Constructor>

        //* کلاینت اَث ریل‌تایم را به کُر وصل می‌کند و هَندلِرهای اَث را ثبت می‌کند.
        public RealtimeAuthClient(RealtimeClient realtimeClient)
        {
            this.realtimeClient = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
            RegisterAuthHandlers();
        }

        #endregion

        #region <Auth Flow>

        //* اکسس توکن ذخیره‌شده را می‌خواند و پیام آث سیستم را به سرور می‌فرستد.
        public async Task<bool> AuthenticateWithStoredTokenAsync(CancellationToken cancellationToken = default)
        {
            string accessToken = SecureTokenStorage.GetAccessToken();
            return await AuthenticateWithAccessTokenAsync(accessToken, cancellationToken);
        }

        //* اکسس توکن داده‌شده را داخل پِیلود امن می‌گذارد و پیام اَث را از طریق کُر ارسال می‌کند.
        public async Task<bool> AuthenticateWithAccessTokenAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            if (isDisposed) return false;
            if (isAuthenticating) return false;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                FailAuthentication(RealtimeError.Create(RealtimeErrorCodes.AuthRequired, "Realtime access token is empty."));
                return false;
            }

            if (!realtimeClient.IsConnected)
            {
                FailAuthentication(RealtimeError.Create(RealtimeErrorCodes.AuthRequired, "Realtime client is not connected."));
                return false;
            }

            isAuthenticating = true;
            isAuthenticated = false;
            connectionId = string.Empty;
            userId = string.Empty;
            lastAuthMessageId = RealtimeEnvelope.CreateMessageId("auth");

            string payloadJson = BuildAuthPayloadJson(accessToken);
            RealtimeEnvelope envelope = RealtimeEnvelope.CreateWithId(lastAuthMessageId, RealtimeChannels.System, RealtimeMessageTypes.Auth, payloadJson);
            bool sent = await realtimeClient.SendEnvelopeAsync(envelope, cancellationToken);

            if (!sent)
            {
                isAuthenticating = false;
                FailAuthentication(RealtimeError.Create(RealtimeErrorCodes.InternalError, "Realtime auth message was not sent."));
                return false;
            }

            AuthLogReceived?.Invoke("Realtime auth message sent: " + lastAuthMessageId);
            return true;
        }

        //* وضعیت اَث ریل‌تایم را پاک می‌کند تا اتصال بعدی از نو اَث شود.
        public void ResetAuthState()
        {
            isAuthenticating = false;
            isAuthenticated = false;
            connectionId = string.Empty;
            userId = string.Empty;
            lastAuthMessageId = string.Empty;
        }

        #endregion

        #region <Router Binding>

        //* هَندلِرهای auth_ok و auth_failed را روی رُتِر کُر ثبت می‌کند.
        private void RegisterAuthHandlers()
        {
            realtimeClient.Router.RegisterHandler(RealtimeChannels.System, RealtimeMessageTypes.AuthOk, HandleAuthOkEnvelope);
            realtimeClient.Router.RegisterHandler(RealtimeChannels.System, RealtimeMessageTypes.AuthFailed, HandleAuthFailedEnvelope);
            realtimeClient.ErrorEnvelopeReceived += HandleSystemErrorEnvelope;
            realtimeClient.Disconnected += HandleRealtimeDisconnected;
        }

        //* هَندلِرهای اَث را از رُتِر کُر جدا می‌کند.
        private void UnregisterAuthHandlers()
        {
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.System, RealtimeMessageTypes.AuthOk);
            realtimeClient.Router.UnregisterHandler(RealtimeChannels.System, RealtimeMessageTypes.AuthFailed);
            realtimeClient.ErrorEnvelopeReceived -= HandleSystemErrorEnvelope;
            realtimeClient.Disconnected -= HandleRealtimeDisconnected;
        }

        #endregion

        #region <Envelope Handlers>

        //* پاسخ auth_ok سرور را می‌خواند و اتصال را برای ریل‌تایم معتبر می‌کند.
        private void HandleAuthOkEnvelope(RealtimeEnvelope envelope)
        {
            AuthOkPayload payload = AuthOkPayload.FromJson(envelope.payloadJson);

            if (payload == null || !payload.ok)
            {
                FailAuthentication(RealtimeError.Create(RealtimeErrorCodes.AuthFailed, "Realtime auth_ok payload is invalid."));
                return;
            }

            isAuthenticating = false;
            isAuthenticated = true;
            connectionId = payload.connectionId ?? string.Empty;
            userId = payload.userId ?? string.Empty;
            Authenticated?.Invoke(connectionId, userId);
            AuthLogReceived?.Invoke("Realtime authenticated. connectionId=" + connectionId + " userId=" + userId);
        }

        //* پاسخ auth_failed سرور را به خطای قابل استفاده در یونیتی تبدیل می‌کند.
        private void HandleAuthFailedEnvelope(RealtimeEnvelope envelope)
        {
            RealtimeError error = RealtimeError.FromPayloadJson(envelope.payloadJson) ?? RealtimeError.Create(RealtimeErrorCodes.AuthFailed, "Realtime authentication failed.");
            FailAuthentication(error);
        }

        //* خطاهای سیستمی مربوط به اَث را دریافت می‌کند و وضعیت اَث را نامعتبر می‌کند.
        private void HandleSystemErrorEnvelope(RealtimeError error)
        {
            if (error == null) return;
            if (error.code != RealtimeErrorCodes.AuthFailed && error.code != RealtimeErrorCodes.AuthRequired && error.code != RealtimeErrorCodes.TokenExpired) return;
            FailAuthentication(error);
        }

        //* بعد از قطع اتصال، وضعیت اَث محلی را پاک می‌کند.
        private void HandleRealtimeDisconnected(string reason)
        {
            ResetAuthState();
            AuthLogReceived?.Invoke("Realtime auth state reset after disconnect: " + reason);
        }

        #endregion

        #region <Payload Helpers>

        //* پِیلود پیام system/auth را با اکسس توکن می‌سازد.
        private static string BuildAuthPayloadJson(string accessToken)
        {
            return "{\"accessToken\":\"" + EscapeJsonString(accessToken) + "\"}";
        }

        //* متن را برای قرار گرفتن داخل جیسون escape می‌کند.
        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        //* خطای اَث را ثبت می‌کند و رویداد شکست را بیرون می‌دهد.
        private void FailAuthentication(RealtimeError error)
        {
            isAuthenticating = false;
            isAuthenticated = false;
            connectionId = string.Empty;
            userId = string.Empty;
            AuthenticationFailed?.Invoke(error);
            AuthLogReceived?.Invoke("Realtime auth failed: " + (error != null ? error.code + " | " + error.message : "unknown"));
        }

        #endregion

        #region <Dispose>

        //* اتصال هَندلِرهای اَث را قطع می‌کند تا نشتی رویداد ایجاد نشود.
        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;
            UnregisterAuthHandlers();
            ResetAuthState();
        }

        #endregion

        #region <Payload Models>

        [Serializable]
        private class AuthOkPayload
        {
            public bool ok;
            public string connectionId;
            public string userId;

            //* پِیلود auth_ok را از جیسون ساده سرور می‌خواند.
            public static AuthOkPayload FromJson(string json)
            {
                if (string.IsNullOrWhiteSpace(json)) return null;

                try
                {
                    return JsonUtility.FromJson<AuthOkPayload>(json);
                }
                catch (Exception ex)
                {
                    Debug.LogError("[RealtimeAuthClient] auth_ok parse failed: " + ex.Message);
                    return null;
                }
            }
        }

        #endregion
    }
}

//* این فایل احراز هویت ریل‌تایم سمت یونیتی را مدیریت می‌کند.
//* این فایل توکن آماده را از ذخیره‌ساز اَث می‌خواند و فقط پیام system/auth را از طریق کُر می‌فرستد.
