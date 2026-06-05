using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Network_A.Auth
{
    public static class SecureTokenStorage
    {
        const string AccessTokenKey = "Network_A_AccessToken";
        const string RefreshTokenKey = "Network_A_RefreshToken";
        const string KeySeed = "Network_A_2026_Project_Key";

        //* Saves access and refresh tokens.
        public static void SaveTokens(string accessToken, string refreshToken)
        {
            if (!string.IsNullOrEmpty(accessToken)) PlayerPrefs.SetString(AccessTokenKey, Encrypt(accessToken));
            if (!string.IsNullOrEmpty(refreshToken)) PlayerPrefs.SetString(RefreshTokenKey, Encrypt(refreshToken));
            PlayerPrefs.Save();
        }

        //* Reads the current access token.
        public static string GetAccessToken()
        {
            if (!PlayerPrefs.HasKey(AccessTokenKey)) return null;
            return Decrypt(PlayerPrefs.GetString(AccessTokenKey));
        }

        //* Reads the current refresh token.
        public static string GetRefreshToken()
        {
            if (!PlayerPrefs.HasKey(RefreshTokenKey)) return null;
            return Decrypt(PlayerPrefs.GetString(RefreshTokenKey));
        }

        //* Clears all stored tokens.
        public static void ClearTokens()
        {
            PlayerPrefs.DeleteKey(AccessTokenKey);
            PlayerPrefs.DeleteKey(RefreshTokenKey);
            PlayerPrefs.Save();
        }

        //* Encrypts text before saving it to PlayerPrefs.
        static string Encrypt(string plainText)
        {
            byte[] key = BuildKey();
            byte[] iv = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(iv);

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var enc = aes.CreateEncryptor())
                {
                    byte[] plain = Encoding.UTF8.GetBytes(plainText);
                    byte[] cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
                    byte[] output = new byte[iv.Length + cipher.Length];
                    Buffer.BlockCopy(iv, 0, output, 0, iv.Length);
                    Buffer.BlockCopy(cipher, 0, output, iv.Length, cipher.Length);
                    return Convert.ToBase64String(output);
                }
            }
        }

        //* Decrypts text loaded from PlayerPrefs.
        static string Decrypt(string cipherText)
        {
            try
            {
                byte[] all = Convert.FromBase64String(cipherText);
                if (all.Length <= 16) return null;

                byte[] iv = new byte[16];
                byte[] cipher = new byte[all.Length - 16];
                Buffer.BlockCopy(all, 0, iv, 0, 16);
                Buffer.BlockCopy(all, 16, cipher, 0, cipher.Length);

                using (var aes = Aes.Create())
                {
                    aes.Key = BuildKey();
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var dec = aes.CreateDecryptor())
                    {
                        byte[] plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                        return Encoding.UTF8.GetString(plain);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        //* Builds a Unity-compatible SHA256 key.
        static byte[] BuildKey()
        {
            byte[] seedBytes = Encoding.UTF8.GetBytes(KeySeed);
            using (var sha = SHA256.Create()) return sha.ComputeHash(seedBytes);
        }
    }
}
