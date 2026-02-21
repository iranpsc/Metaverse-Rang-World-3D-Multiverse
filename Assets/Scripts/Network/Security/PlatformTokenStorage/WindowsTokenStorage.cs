#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Assets.Scripts.Network.Security;

namespace Assets.Scripts.Network.Security.PlatformTokenStorage
{
    public class WindowsTokenStorage : ITokenStorage
    {
        // legacy registry keys (همین‌هایی که الان داری)
        private const string TOKEN_KEY = "metaverse_auth_token_win";
        private const string REFRESH_TOKEN_KEY = "metaverse_refresh_token_win";
        private const string EXPIRY_KEY = "metaverse_token_expiry_win";
     //   private const string USER_ID_KEY = "metaverse_user_id_win";
        private const string LEGACY_REG_PATH = "SOFTWARE\\MetaverseIran\\Auth";

        // new dpapi file
        private readonly string _filePath;
        private readonly string _legacyEncryptionKey;

        private readonly string deviceFingerprint;

        public WindowsTokenStorage()
        {
            deviceFingerprint = CryptoService.GenerateDeviceFingerprint();
            _legacyEncryptionKey = GenerateLegacyEncryptionKey(); // برای decrypt داده‌های قدیمی registry

            _filePath = Path.Combine(Application.persistentDataPath, "mv_tokens_win_dpapi.dat");
        }

        public void SaveTokens(string token, string refreshToken, int expiresIn, string userId)
        {
            try
            {
                long expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds();
                string payload = Serialize(token, refreshToken, expiry, userId);

                byte[] plainBytes = Encoding.UTF8.GetBytes(payload);
                byte[] entropy = Encoding.UTF8.GetBytes("metaverse_dpapi_entropy_v1");

                byte[] protectedBytes = ProtectedData.Protect(plainBytes, entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_filePath, protectedBytes);
            }
            catch (Exception ex)
            {
                Debug.LogError($"WindowsTokenStorage SaveTokens(DPAPI) error: {ex.Message}");
            }
        }

        public string GetToken()
        {
            if (TryLoad(out var token, out _, out _, out _))
                return token;
            return null;
        }

        public string GetRefreshToken()
        {
            if (TryLoad(out _, out var refresh, out _, out _))
                return refresh;
            return null;
        }

        public bool IsTokenValid()
        {
            if (!TryLoad(out var token, out _, out long exp, out _))
                return false;

            if (string.IsNullOrEmpty(token))
                return false;

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return now < (exp - 300);
        }

        public void ClearTokens()
        {
            try
            {
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
            }
            catch { /* ignore */ }

            // legacy cleanup
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(LEGACY_REG_PATH, false);
            }
            catch { /* ignore */ }
        }

        public string GetUserId()
        {
            if (TryLoad(out _, out _, out _, out var userId))
                return userId;
            return null;
        }

        private bool TryLoad(out string token, out string refresh, out long expiry, out string userId)
        {
            token = null;
            refresh = null;
            expiry = 0;
            userId = null;

            // 1) مسیر جدید DPAPI
            if (TryLoadFromDpapiFile(out token, out refresh, out expiry, out userId))
                return true;

            // 2) اگر نبود، تلاش برای migrate از Registry قدیمی
            if (TryLoadFromLegacyRegistry(out token, out refresh, out expiry, out userId))
            {
                // migrate به DPAPI
                try
                {
                    string payload = Serialize(token, refresh, expiry, userId);
                    byte[] plainBytes = Encoding.UTF8.GetBytes(payload);
                    byte[] entropy = Encoding.UTF8.GetBytes("metaverse_dpapi_entropy_v1");
                    byte[] protectedBytes = ProtectedData.Protect(plainBytes, entropy, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(_filePath, protectedBytes);

                    // legacy cleanup
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(LEGACY_REG_PATH, false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"WindowsTokenStorage migrate to DPAPI failed: {ex.Message}");
                }

                return true;
            }

            return false;
        }

        private bool TryLoadFromDpapiFile(out string token, out string refresh, out long expiry, out string userId)
        {
            token = null; refresh = null; expiry = 0; userId = null;

            try
            {
                if (!File.Exists(_filePath))
                    return false;

                byte[] protectedBytes = File.ReadAllBytes(_filePath);
                byte[] entropy = Encoding.UTF8.GetBytes("metaverse_dpapi_entropy_v1");

                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
                string payload = Encoding.UTF8.GetString(plainBytes);

                return TryDeserialize(payload, out token, out refresh, out expiry, out userId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"WindowsTokenStorage DPAPI load failed (will clear): {ex.Message}");
                try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
                return false;
            }
        }

        private bool TryLoadFromLegacyRegistry(out string token, out string refresh, out long expiry, out string userId)
        {
            token = null; refresh = null; expiry = 0; userId = null;

            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LEGACY_REG_PATH);
                if (key == null) return false;

                string encToken = key.GetValue(TOKEN_KEY) as string;
                string encRefresh = key.GetValue(REFRESH_TOKEN_KEY) as string;
                string expStr = key.GetValue(EXPIRY_KEY) as string;
               // string uid = key.GetValue(USER_ID_KEY) as string;

                key.Close();

                if (string.IsNullOrEmpty(encToken) || string.IsNullOrEmpty(expStr))
                    return false;

                // decrypt legacy by CryptoService (حالا v2 هم می‌فهمه، v1 هم می‌فهمه)
                token = CryptoService.Decrypt(encToken, _legacyEncryptionKey);
                refresh = string.IsNullOrEmpty(encRefresh) ? null : CryptoService.Decrypt(encRefresh, _legacyEncryptionKey);

                if (!long.TryParse(expStr, out expiry))
                    return false;

              //  userId = uid;
                return !string.IsNullOrEmpty(token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"WindowsTokenStorage legacy registry load failed: {ex.Message}");
                return false;
            }
        }

        private string GenerateLegacyEncryptionKey()
        {
            return $"Metaverse_WinKey_{deviceFingerprint}_{Environment.UserName}";
        }

        // payload format: token|refresh|expiry|userId  (escaped)
        private static string Serialize(string token, string refresh, long expiry )
        {
            return $"{Esc(token)}|{Esc(refresh)}|{expiry}}";
        }

        private static bool TryDeserialize(string s, out string token, out string refresh, out long expiry )
        {
            token = null; refresh = null; expiry = 0;  
            if (string.IsNullOrEmpty(s)) return false;

            var parts = Split4(s);
            if (parts == null) return false;

            token = UnEsc(parts[0]);
            refresh = UnEsc(parts[1]);
            if (!long.TryParse(parts[2], out expiry)) return false;
         

            return true;
        }

        private static string Esc(string v) => (v ?? string.Empty).Replace("\\", "\\\\").Replace("|", "\\|");
        private static string UnEsc(string v)
        {
            if (v == null) return string.Empty;
            var sb = new StringBuilder();
            bool esc = false;
            foreach (char c in v)
            {
                if (esc) { sb.Append(c); esc = false; }
                else if (c == '\\') esc = true;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string[] Split4(string s)
        {
            string[] parts = new string[3];
            var sb = new StringBuilder();
            int idx = 0;
            bool esc = false;

            foreach (char c in s)
            {
                if (esc) { sb.Append(c); esc = false; continue; }
                if (c == '\\') { esc = true; continue; }

                if (c == '|' && idx < 2)
                {
                    parts[idx++] = sb.ToString();
                    sb.Clear();
                    continue;
                }
                sb.Append(c);
            }

            if (idx != 2) return null;
            parts[2] = sb.ToString();
            return parts;
        }
    }
}
#endif
