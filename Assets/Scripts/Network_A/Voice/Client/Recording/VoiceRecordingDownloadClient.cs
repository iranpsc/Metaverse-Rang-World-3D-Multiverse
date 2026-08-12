using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Network_A.Auth;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Voice.Client.Recording
{
    public sealed class VoiceRecordingDownloadClient : MonoBehaviour
    {
        [Header("Server")]
        [SerializeField] private string baseHttpUrl = "https://dev-world-3d.metarang.com";

        [Header("Auth Refresh Gate")]
        [SerializeField] private int accessTokenRefreshSkewSeconds = 60;

        [Header("Debug Test")]
        [SerializeField] private string debugSessionId = "";

        public event Action<string> DownloadSucceeded;
        public event Action<string> DownloadFailed;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void VoiceRecordingDownloadSaveBase64(
            string fileName,
            string base64Data,
            string mimeType
        );
#endif

        public void DownloadDebugSession()
        {
            DownloadRecording(debugSessionId);
        }

        public void DownloadRecording(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Fail("VOICE_RECORDING_DOWNLOAD_SESSION_ID_EMPTY");
                return;
            }

            StartCoroutine(DownloadRecordingRoutine(sessionId.Trim()));
        }

        private IEnumerator DownloadRecordingRoutine(string sessionId)
        {
            Task<string> tokenTask = EnsureFreshAccessTokenBeforeDownloadAsync();

            while (!tokenTask.IsCompleted)
            {
                yield return null;
            }

            if (tokenTask.IsFaulted)
            {
                Fail("VOICE_RECORDING_DOWNLOAD_TOKEN_TASK_FAILED | " + tokenTask.Exception);
                yield break;
            }

            string accessToken = tokenTask.Result;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                Fail("VOICE_RECORDING_DOWNLOAD_ACCESS_TOKEN_EMPTY");
                yield break;
            }

            string url =
                $"{baseHttpUrl.TrimEnd('/')}/voice/recordings/{UnityWebRequest.EscapeURL(sessionId)}/download";

            using UnityWebRequest request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            request.SetRequestHeader("Accept", "audio/ogg");

            Debug.Log(
                $"VOICE_RECORDING_DOWNLOAD_REQUEST_START | sessionId={sessionId} | url={url}"
            );

            yield return request.SendWebRequest();

            if (request.responseCode == 401)
            {
                Debug.LogWarning(
                    $"VOICE_RECORDING_DOWNLOAD_401 | sessionId={sessionId} | auth_session_expired_or_forbidden"
                );

                Fail("VOICE_RECORDING_DOWNLOAD_AUTH_FAILED_401");
                yield break;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                string body =
                    request.downloadHandler != null
                        ? request.downloadHandler.text
                        : "";

                Fail(
                    $"VOICE_RECORDING_DOWNLOAD_HTTP_FAILED | status={request.responseCode} | error={request.error} | body={body}"
                );
                yield break;
            }

            byte[] bytes =
                request.downloadHandler != null
                    ? request.downloadHandler.data
                    : null;

            if (bytes == null || bytes.Length < 4)
            {
                Fail("VOICE_RECORDING_DOWNLOAD_EMPTY_FILE");
                yield break;
            }

            if (
                bytes[0] != (byte)'O' ||
                bytes[1] != (byte)'g' ||
                bytes[2] != (byte)'g' ||
                bytes[3] != (byte)'S'
            )
            {
                Fail("VOICE_RECORDING_DOWNLOAD_NOT_OGG");
                yield break;
            }

            string downloadMode =
                request.GetResponseHeader("X-Voice-Recording-Download-Mode") ??
                "unknown";

            string sha256 =
                request.GetResponseHeader("X-Voice-Recording-SHA256") ??
                "";

            string intervalCount =
                request.GetResponseHeader("X-Voice-Recording-Interval-Count") ??
                "";

            string fileName =
                $"voice-session-{sessionId}-{downloadMode}.ogg";

#if UNITY_WEBGL && !UNITY_EDITOR
            string base64 = Convert.ToBase64String(bytes);

            VoiceRecordingDownloadSaveBase64(
                fileName,
                base64,
                "audio/ogg"
            );

            Debug.Log(
                $"VOICE_RECORDING_DOWNLOAD_WEBGL_SAVE=PASS | sessionId={sessionId} | bytes={bytes.Length} | mode={downloadMode} | intervals={intervalCount} | sha256={sha256}"
            );

            DownloadSucceeded?.Invoke(fileName);
#else
            string directory =
                Path.Combine(
                    Application.persistentDataPath,
                    "VoiceRecordings"
                );

            Directory.CreateDirectory(directory);

            string filePath =
                Path.Combine(directory, fileName);

            File.WriteAllBytes(filePath, bytes);

            Debug.Log(
                $"VOICE_RECORDING_DOWNLOAD_SAVE=PASS | sessionId={sessionId} | path={filePath} | bytes={bytes.Length} | mode={downloadMode} | intervals={intervalCount} | sha256={sha256}"
            );

            DownloadSucceeded?.Invoke(filePath);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            Application.OpenURL(
                $"file:///{filePath.Replace("\\", "/")}"
            );
#endif
#endif
        }

        private async Task<string> EnsureFreshAccessTokenBeforeDownloadAsync()
        {
            string accessToken = SecureTokenStorage.GetAccessToken();

            if (!IsAccessTokenRefreshRequired(accessToken))
            {
                return string.IsNullOrWhiteSpace(accessToken)
                    ? string.Empty
                    : accessToken.Trim();
            }

            if (string.IsNullOrWhiteSpace(SecureTokenStorage.GetRefreshToken()))
            {
                Debug.LogWarning(
                    "VOICE_RECORDING_DOWNLOAD_REFRESH_REQUIRED_BUT_REFRESH_TOKEN_EMPTY"
                );

                return string.Empty;
            }

            Debug.Log(
                "VOICE_RECORDING_DOWNLOAD_ACCESS_TOKEN_REFRESH_START"
            );

            bool refreshed = await AuthRefreshManager.Refresh();

            if (!refreshed)
            {
                Debug.LogWarning(
                    "VOICE_RECORDING_DOWNLOAD_ACCESS_TOKEN_REFRESH_FAILED"
                );

                return string.Empty;
            }

            string refreshedToken =
                SecureTokenStorage.GetAccessToken();

            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                Debug.LogWarning(
                    "VOICE_RECORDING_DOWNLOAD_REFRESHED_ACCESS_TOKEN_EMPTY"
                );

                return string.Empty;
            }

            Debug.Log(
                "VOICE_RECORDING_DOWNLOAD_ACCESS_TOKEN_REFRESH_PASS"
            );

            return refreshedToken.Trim();
        }

        private bool IsAccessTokenRefreshRequired(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return true;
            }

            if (!TryReadJwtExpiryUnixSeconds(
                    accessToken,
                    out long expiresAtUnixSeconds
                ))
            {
                return false;
            }

            long nowUnixSeconds =
                DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            int safeSkewSeconds =
                Mathf.Clamp(
                    accessTokenRefreshSkewSeconds,
                    0,
                    3600
                );

            return expiresAtUnixSeconds <=
                   nowUnixSeconds + safeSkewSeconds;
        }

        private static bool TryReadJwtExpiryUnixSeconds(
            string token,
            out long expiresAtUnixSeconds
        )
        {
            expiresAtUnixSeconds = 0;

            string payloadJson =
                ReadJwtPayloadJson(token);

            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return false;
            }

            return TryExtractJsonLongValue(
                payloadJson,
                "exp",
                out expiresAtUnixSeconds
            );
        }

        private static string ReadJwtPayloadJson(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            string[] parts = token.Split('.');

            if (parts == null || parts.Length < 2)
            {
                return string.Empty;
            }

            return DecodeBase64UrlToString(parts[1]);
        }

        private static string DecodeBase64UrlToString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string base64 =
                value.Replace('-', '+').Replace('_', '/');

            int padding = base64.Length % 4;

            if (padding == 2)
            {
                base64 += "==";
            }
            else if (padding == 3)
            {
                base64 += "=";
            }
            else if (padding != 0)
            {
                return string.Empty;
            }

            try
            {
                byte[] bytes =
                    Convert.FromBase64String(base64);

                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryExtractJsonLongValue(
            string json,
            string key,
            out long value
        )
        {
            value = 0;

            if (
                string.IsNullOrWhiteSpace(json) ||
                string.IsNullOrWhiteSpace(key)
            )
            {
                return false;
            }

            string pattern = "\"" + key + "\"";
            int keyIndex =
                json.IndexOf(
                    pattern,
                    StringComparison.Ordinal
                );

            if (keyIndex < 0)
            {
                return false;
            }

            int colonIndex =
                json.IndexOf(':', keyIndex + pattern.Length);

            if (colonIndex < 0)
            {
                return false;
            }

            int start = colonIndex + 1;

            while (
                start < json.Length &&
                char.IsWhiteSpace(json[start])
            )
            {
                start++;
            }

            int end = start;

            while (
                end < json.Length &&
                (
                    char.IsDigit(json[end]) ||
                    json[end] == '-'
                )
            )
            {
                end++;
            }

            if (end <= start)
            {
                return false;
            }

            return long.TryParse(
                json.Substring(start, end - start),
                out value
            );
        }

        private void Fail(string reason)
        {
            Debug.LogError(reason);
            DownloadFailed?.Invoke(reason);
        }
    }
}