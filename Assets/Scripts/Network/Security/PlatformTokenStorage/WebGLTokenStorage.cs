#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using Assets.Scripts.Network.Security;

namespace Assets.Scripts.Network.Security.PlatformTokenStorage
{
    /// <summary>
    /// WebGL Token Storage:
    /// - Browser localStorage via jslib
    /// - CryptoService v2 (AES-CBC + HMAC + salt per-message)
    /// - Auto-migrate v1 -> v2 via TryDecryptWithMigration
    /// - InstallSecret برای سخت‌سازی کلید (بدون نیاز به پلاگین)
    /// </summary>
    public class WebGLTokenStorage : ITokenStorage
    {
        private const string TOKEN_KEY = "metaverse_auth_token";
        private const string REFRESH_TOKEN_KEY = "metaverse_refresh_token";
        private const string EXPIRY_KEY = "metaverse_token_expiry";
      //  private const string USER_ID_KEY = "metaverse_user_id";
        private const string DEVICE_FP_KEY = "metaverse_device_fp";

        // Install secret (persisted)
        private const string INSTALL_SECRET_KEY = "metaverse_install_secret";

        private readonly string encryptionKey;
        private readonly string deviceFingerprint;

        public WebGLTokenStorage()
        {
            deviceFingerprint = CryptoService.GenerateDeviceFingerprint();

            // ذخیره‌ی fingerprint برای debug/consistency (اختیاری)
            SetLocalStorage(DEVICE_FP_KEY, deviceFingerprint);

            // کلید پایدار و سخت‌تر
            encryptionKey = GenerateEncryptionKey(deviceFingerprint);
        }

        public void SaveTokens(string token, string refreshToken, int expiresIn )
        {
            try
            {
                string encryptedToken = CryptoService.Encrypt(token ?? string.Empty, encryptionKey);
                string encryptedRefreshToken = CryptoService.Encrypt(refreshToken ?? string.Empty, encryptionKey);

                long expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds();

                SetLocalStorage(TOKEN_KEY, encryptedToken);
                SetLocalStorage(REFRESH_TOKEN_KEY, encryptedRefreshToken);
                SetLocalStorage(EXPIRY_KEY, expiry.ToString());
         //       SetLocalStorage(USER_ID_KEY, userId ?? string.Empty);
            }
            catch (Exception ex)
            {
                Debug.LogError($"WebGLTokenStorage SaveTokens error: {ex.Message}");
            }
        }

        public string GetToken()
        {
            return GetAndMaybeMigrate(TOKEN_KEY);
        }

        public string GetRefreshToken()
        {
            return GetAndMaybeMigrate(REFRESH_TOKEN_KEY);
        }

        public bool IsTokenValid()
        {
            try
            {
                string expiryStr = GetLocalStorage(EXPIRY_KEY);
                if (string.IsNullOrEmpty(expiryStr))
                    return false;

                if (!long.TryParse(expiryStr, out long expiryTimestamp))
                    return false;

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return now < (expiryTimestamp - 300);
            }
            catch (Exception ex)
            {
                Debug.LogError($"WebGLTokenStorage IsTokenValid error: {ex.Message}");
                return false;
            }
        }

        public void ClearTokens()
        {
            RemoveLocalStorage(TOKEN_KEY);
            RemoveLocalStorage(REFRESH_TOKEN_KEY);
            RemoveLocalStorage(EXPIRY_KEY);
          //  RemoveLocalStorage(USER_ID_KEY);
            RemoveLocalStorage(DEVICE_FP_KEY);

            // install secret را پاک نمی‌کنیم مگر اینکه واقعاً بخواهی reset کامل شود
            // RemoveLocalStorage(INSTALL_SECRET_KEY);
        }

        public string GetUserId()
        {
            return GetLocalStorage(USER_ID_KEY);
        }

        private string GetAndMaybeMigrate(string key)
        {
            try
            {
                string encrypted = GetLocalStorage(key);
                if (string.IsNullOrEmpty(encrypted))
                    return null;

                if (CryptoService.TryDecryptWithMigration(encrypted, encryptionKey, out string plain, out string migratedV2))
                {
                    if (!string.IsNullOrEmpty(migratedV2))
                    {
                        SetLocalStorage(key, migratedV2);
                    }

                    return string.IsNullOrEmpty(plain) ? null : plain;
                }

                // decrypt fail => داده خراب/دستکاری شده
                RemoveLocalStorage(key);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"WebGLTokenStorage GetAndMaybeMigrate error: {ex.Message}");
                return null;
            }
        }

        private string GenerateEncryptionKey(string fp)
        {
            string installSecret = GetOrCreateInstallSecret();
            // پایدار، و غیرقابل پیش‌بینی‌تر از فقط fingerprint
            return $"Metaverse_WebGLKey_{fp}_{Application.identifier}_{installSecret}";
        }

        private string GetOrCreateInstallSecret()
        {
            string existing = GetLocalStorage(INSTALL_SECRET_KEY);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            byte[] b = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(b);

            string secret = Convert.ToBase64String(b);
            SetLocalStorage(INSTALL_SECRET_KEY, secret);
            return secret;
        }

        #region Interop برای localStorage مرورگر

        [DllImport("__Internal")] private static extern void SetLocalStorage(string key, string value);
        [DllImport("__Internal")] private static extern string GetLocalStorage(string key);
        [DllImport("__Internal")] private static extern void RemoveLocalStorage(string key);

        #endregion
    }
}
#endif
