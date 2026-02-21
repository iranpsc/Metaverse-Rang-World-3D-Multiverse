#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Security.Cryptography;
using UnityEngine;
using Assets.Scripts.Network.Security;

namespace Assets.Scripts.Network.Security.PlatformTokenStorage
{
    /// <summary>
    /// Quest/Android Token Storage:
    /// - PlayerPrefs + CryptoService v2 (AES-CBC + HMAC + salt per-message)
    /// - Auto-migrate v1 -> v2 via TryDecryptWithMigration
    /// - InstallSecret برای سخت‌سازی کلید (بدون نیاز به EncryptedSharedPreferences)
    /// </summary>
    public class QuestTokenStorage : ITokenStorage
    {
        private const string TOKEN_KEY = "metaverse_auth_token_quest";
        private const string REFRESH_TOKEN_KEY = "metaverse_refresh_token_quest";
        private const string EXPIRY_KEY = "metaverse_token_expiry_quest";
     //   private const string USER_ID_KEY = "metaverse_user_id_quest";

        private const string INSTALL_SECRET_KEY = "metaverse_install_secret_q";

        private readonly string encryptionKey;
        private readonly string deviceFingerprint;

        public QuestTokenStorage()
        {
            deviceFingerprint = CryptoService.GenerateDeviceFingerprint();
            encryptionKey = GenerateEncryptionKey(deviceFingerprint);
        }

        public void SaveTokens(string token, string refreshToken, int expiresIn)
        {
            try
            {
                string encryptedToken = CryptoService.Encrypt(token ?? string.Empty, encryptionKey);
                string encryptedRefreshToken = CryptoService.Encrypt(refreshToken ?? string.Empty, encryptionKey);

                long expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds();

                PlayerPrefs.SetString(TOKEN_KEY, encryptedToken);
                PlayerPrefs.SetString(REFRESH_TOKEN_KEY, encryptedRefreshToken);
                PlayerPrefs.SetString(EXPIRY_KEY, expiry.ToString());
              //  PlayerPrefs.SetString(USER_ID_KEY, userId ?? string.Empty);
                PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                Debug.LogError($"QuestTokenStorage SaveTokens error: {ex.Message}");
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
                string expiryStr = PlayerPrefs.GetString(EXPIRY_KEY, null);
                if (string.IsNullOrEmpty(expiryStr) || !long.TryParse(expiryStr, out long expiryTimestamp))
                    return false;

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return now < (expiryTimestamp - 300);
            }
            catch (Exception ex)
            {
                Debug.LogError($"QuestTokenStorage IsTokenValid error: {ex.Message}");
                return false;
            }
        }

        public void ClearTokens()
        {
            PlayerPrefs.DeleteKey(TOKEN_KEY);
            PlayerPrefs.DeleteKey(REFRESH_TOKEN_KEY);
            PlayerPrefs.DeleteKey(EXPIRY_KEY);
          //  PlayerPrefs.DeleteKey(USER_ID_KEY);
            PlayerPrefs.Save();
 
        }

        public string GetUserId()
        {
            return PlayerPrefs.GetString(USER_ID_KEY, null);
        }

        private string GetAndMaybeMigrate(string key)
        {
            try
            {
                string encrypted = PlayerPrefs.GetString(key, null);
                if (string.IsNullOrEmpty(encrypted))
                    return null;

                if (CryptoService.TryDecryptWithMigration(encrypted, encryptionKey, out string plain, out string migratedV2))
                {
                    if (!string.IsNullOrEmpty(migratedV2))
                    {
                        PlayerPrefs.SetString(key, migratedV2);
                        PlayerPrefs.Save();
                    }

                    return string.IsNullOrEmpty(plain) ? null : plain;
                }

                // decrypt fail => داده خراب/دستکاری شده
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"QuestTokenStorage GetAndMaybeMigrate error: {ex.Message}");
                return null;
            }
        }

        private string GenerateEncryptionKey(string fp)
        {
            string installSecret = GetOrCreateInstallSecret();
            return $"Metaverse_QuestKey_{fp}_{Application.identifier}_{installSecret}";
        }

        private string GetOrCreateInstallSecret()
        {
            string existing = PlayerPrefs.GetString(INSTALL_SECRET_KEY, string.Empty);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            byte[] b = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(b);

            string secret = Convert.ToBase64String(b);
            PlayerPrefs.SetString(INSTALL_SECRET_KEY, secret);
            PlayerPrefs.Save();
            return secret;
        }
    }
}
#endif
