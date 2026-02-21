using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Assets.Scripts.Network.Security
{
    /// <summary>
    /// CryptoService v2:
    /// - AES-256-CBC + PKCS7
    /// - PBKDF2(SHA256) با salt تصادفی per-message
    /// - HMACSHA256 برای integrity (قبل از decrypt verify می‌شود)
    /// - Envelope نسخه‌دار: v2|iters|saltB64|ivB64|cipherB64|hmacB64
    /// - Backward compat: اگر رشته Base64 بود و v2 نبود => مسیر v1 (فرمت قدیمی: IV + cipher)
    /// </summary>
    public static class CryptoService
    {
        private const int AesKeySizeBits = 256;
        private const int AesBlockSizeBits = 128;

        // v2 defaults
        private const int DefaultPbkdf2Iterations = 100_000;
        private const int SaltSizeBytes = 16;   // 128-bit
        private const int IvSizeBytes = 16;     // 128-bit
        private const int HmacSizeBytes = 32;   // 256-bit

        private const string V2Prefix = "v2";
        private const char Sep = '|';

        // v1 legacy salt (برای decrypt قدیمی)
        private static readonly byte[] LegacySaltV1 = Encoding.UTF8.GetBytes("Metaverse_Salt_2026_Iran");

        /// <summary>
        /// Encrypt با Envelope v2
        /// </summary>
        public static string Encrypt(string plainText, string encryptionKey, int pbkdf2Iterations = DefaultPbkdf2Iterations)
        {
            if (string.IsNullOrEmpty(plainText) || string.IsNullOrEmpty(encryptionKey))
                return string.Empty;

            try
            {
                // salt per-message
                byte[] salt = RandomBytes(SaltSizeBytes);
                byte[] iv = RandomBytes(IvSizeBytes);

                // derive 64 bytes: 32 aes + 32 hmac
                byte[] keyMaterial = DeriveKeyV2(encryptionKey, salt, pbkdf2Iterations, 64);
                byte[] aesKey = Slice(keyMaterial, 0, 32);
                byte[] hmacKey = Slice(keyMaterial, 32, 32);

                byte[] cipherBytes = AesEncryptCbc(Encoding.UTF8.GetBytes(plainText), aesKey, iv);

                // header without hmac
                string itersStr = pbkdf2Iterations.ToString();
                string saltB64 = Convert.ToBase64String(salt);
                string ivB64 = Convert.ToBase64String(iv);
                string cipherB64 = Convert.ToBase64String(cipherBytes);

                // Compute HMAC on: "v2|iters|salt|iv|cipher" (as UTF8)
                string header = $"{V2Prefix}{Sep}{itersStr}{Sep}{saltB64}{Sep}{ivB64}{Sep}{cipherB64}";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                byte[] hmac = ComputeHmacSha256(hmacKey, headerBytes);
                string hmacB64 = Convert.ToBase64String(hmac);

                return $"{header}{Sep}{hmacB64}";
            }
            catch (Exception ex)
            {
                Debug.LogError($"CryptoService Encrypt error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Decrypt:
        /// - اگر v2 بود => verify HMAC سپس decrypt
        /// - اگر v2 نبود => تلاش برای v1 legacy (Base64: IV + cipher)
        /// </summary>
        public static string Decrypt(string cipherText, string encryptionKey)
        {
            if (string.IsNullOrEmpty(cipherText) || string.IsNullOrEmpty(encryptionKey))
                return string.Empty;

            try
            {
                if (IsV2Envelope(cipherText))
                {
                    return DecryptV2(cipherText, encryptionKey);
                }

                // fallback v1 legacy
                return DecryptV1(cipherText, encryptionKey);
            }
            catch (Exception ex)
            {
                Debug.LogError($"CryptoService Decrypt error: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// اگر cipherText v1 بود و با موفقیت decrypt شد، دوباره v2 ذخیره کن (برای migrate).
        /// Caller باید اگر نتیجه non-empty بود، Save مجدد انجام بده.
        /// </summary>
        public static bool TryDecryptWithMigration(string cipherText, string encryptionKey, out string plainText, out string migratedV2Cipher)
        {
            plainText = string.Empty;
            migratedV2Cipher = string.Empty;

            if (string.IsNullOrEmpty(cipherText) || string.IsNullOrEmpty(encryptionKey))
                return false;

            // v2: نیازی به migrate نیست
            if (IsV2Envelope(cipherText))
            {
                plainText = DecryptV2(cipherText, encryptionKey);
                return !string.IsNullOrEmpty(plainText);
            }

            // v1: اگر موفق شد، v2 تولید کن
            plainText = DecryptV1(cipherText, encryptionKey);
            if (string.IsNullOrEmpty(plainText))
                return false;

            migratedV2Cipher = Encrypt(plainText, encryptionKey);
            return !string.IsNullOrEmpty(migratedV2Cipher);
        }

        private static bool IsV2Envelope(string s)
        {
            // حداقل: v2|iters|salt|iv|cipher|hmac
            return s.StartsWith(V2Prefix + Sep);
        }

        private static string DecryptV2(string envelope, string encryptionKey)
        {
            // v2|iters|saltB64|ivB64|cipherB64|hmacB64
            string[] parts = envelope.Split(Sep);
            if (parts.Length != 6) return string.Empty;
            if (parts[0] != V2Prefix) return string.Empty;

            if (!int.TryParse(parts[1], out int iters)) return string.Empty;

            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] iv = Convert.FromBase64String(parts[3]);
            byte[] cipherBytes = Convert.FromBase64String(parts[4]);
            byte[] hmacGiven = Convert.FromBase64String(parts[5]);

            // derive keys
            byte[] keyMaterial = DeriveKeyV2(encryptionKey, salt, iters, 64);
            byte[] aesKey = Slice(keyMaterial, 0, 32);
            byte[] hmacKey = Slice(keyMaterial, 32, 32);

            // verify hmac on header without last part
            string header = $"{parts[0]}{Sep}{parts[1]}{Sep}{parts[2]}{Sep}{parts[3]}{Sep}{parts[4]}";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            byte[] hmacExpected = ComputeHmacSha256(hmacKey, headerBytes);

            if (!ConstantTimeEquals(hmacExpected, hmacGiven))
            {
                Debug.LogWarning("CryptoService: HMAC mismatch (data may be corrupted/tampered).");
                return string.Empty;
            }

            byte[] plainBytes = AesDecryptCbc(cipherBytes, aesKey, iv);
            return Encoding.UTF8.GetString(plainBytes);
        }

        private static string DecryptV1(string cipherText, string encryptionKey)
        {
            // legacy: Base64( IV(16) + cipher )
            byte[] fullCipher;
            try
            {
                fullCipher = Convert.FromBase64String(cipherText);
            }
            catch
            {
                return string.Empty;
            }

            if (fullCipher.Length <= IvSizeBytes)
                return string.Empty;

            byte[] iv = new byte[IvSizeBytes];
            Buffer.BlockCopy(fullCipher, 0, iv, 0, IvSizeBytes);

            byte[] cipherBytes = new byte[fullCipher.Length - IvSizeBytes];
            Buffer.BlockCopy(fullCipher, IvSizeBytes, cipherBytes, 0, cipherBytes.Length);

            byte[] aesKey = DeriveKeyV1(encryptionKey, AesKeySizeBits / 8);
            byte[] plainBytes = AesDecryptCbc(cipherBytes, aesKey, iv);

            return Encoding.UTF8.GetString(plainBytes);
        }

        private static byte[] DeriveKeyV2(string password, byte[] salt, int iterations, int bytes)
        {
            using (var deriveBytes = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            {
                return deriveBytes.GetBytes(bytes);
            }
        }

        private static byte[] DeriveKeyV1(string password, int keySizeInBytes)
        {
            using (var deriveBytes = new Rfc2898DeriveBytes(password, LegacySaltV1, 10000, HashAlgorithmName.SHA256))
            {
                return deriveBytes.GetBytes(keySizeInBytes);
            }
        }

        private static byte[] AesEncryptCbc(byte[] plainBytes, byte[] key, byte[] iv)
        {
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = AesKeySizeBits;
                aes.BlockSize = AesBlockSizeBits;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                aes.Key = key;
                aes.IV = iv;

                using (var ms = new MemoryStream())
                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(plainBytes, 0, plainBytes.Length);
                    cs.FlushFinalBlock();
                    return ms.ToArray();
                }
            }
        }

        private static byte[] AesDecryptCbc(byte[] cipherBytes, byte[] key, byte[] iv)
        {
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = AesKeySizeBits;
                aes.BlockSize = AesBlockSizeBits;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                aes.Key = key;
                aes.IV = iv;

                using (var ms = new MemoryStream(cipherBytes))
                using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                using (var outMs = new MemoryStream())
                {
                    cs.CopyTo(outMs);
                    return outMs.ToArray();
                }
            }
        }

        private static byte[] ComputeHmacSha256(byte[] key, byte[] data)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(data);
            }
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];

            return diff == 0;
        }

        private static byte[] RandomBytes(int len)
        {
            byte[] b = new byte[len];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(b);
            return b;
        }

        private static byte[] Slice(byte[] src, int offset, int len)
        {
            byte[] dst = new byte[len];
            Buffer.BlockCopy(src, offset, dst, 0, len);
            return dst;
        }

        /// <summary>
        /// همان تابع قبلی شما (Fingerprint) - فقط تمیزتر و بدون Android_id اشتباه.
        /// </summary>
        public static string GenerateDeviceFingerprint()
        {
            string deviceId;

#if UNITY_WEBGL && !UNITY_EDITOR
            deviceId = SystemInfo.deviceUniqueIdentifier + Application.platform;
#elif UNITY_ANDROID && !UNITY_EDITOR
            // android_id safe access
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var contentResolver = activity.Call<AndroidJavaObject>("getContentResolver"))
                using (var secure = new AndroidJavaClass("android.provider.Settings$Secure"))
                {
                    deviceId = secure.CallStatic<string>("getString", contentResolver, "android_id");
                }
            }
            catch
            {
                deviceId = SystemInfo.deviceUniqueIdentifier;
            }
#elif UNITY_STANDALONE_WIN
            deviceId = SystemInfo.deviceUniqueIdentifier + SystemInfo.processorType + SystemInfo.operatingSystem;
#else
            deviceId = SystemInfo.deviceUniqueIdentifier;
#endif

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(deviceId ?? "unknown"));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
