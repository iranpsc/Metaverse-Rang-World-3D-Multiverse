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

            string contentDisposition =
                request.GetResponseHeader("Content-Disposition") ??
                "";

            string fileName = ResolveDownloadFileName(
                contentDisposition,
                sessionId,
                downloadMode,
                sha256
            );

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
            string directory = ResolveDownloadDirectory();

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                Fail(
                    $"VOICE_RECORDING_DOWNLOAD_DIRECTORY_CREATE_FAILED | path={directory} | error={exception.Message}"
                );
                yield break;
            }

            string filePath;

            try
            {
                filePath = WriteDownloadedFileWithoutOverwrite(
                    directory,
                    fileName,
                    bytes
                );
            }
            catch (Exception exception)
            {
                Fail(
                    $"VOICE_RECORDING_DOWNLOAD_FILE_WRITE_FAILED | directory={directory} | fileName={fileName} | error={exception.Message}"
                );
                yield break;
            }

            Debug.Log(
                $"VOICE_RECORDING_DOWNLOAD_SAVE=PASS | sessionId={sessionId} | path={filePath} | bytes={bytes.Length} | mode={downloadMode} | intervals={intervalCount} | sha256={sha256}"
            );

            DownloadSucceeded?.Invoke(filePath);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            RevealDownloadedFileInExplorer(filePath);
#endif
#endif
        }

        private static string ResolveDownloadFileName(
            string contentDisposition,
            string sessionId,
            string downloadMode,
            string sha256
        )
        {
            if (TryReadContentDispositionFileName(
                    contentDisposition,
                    out string responseFileName
                ))
            {
                return responseFileName;
            }

            string safeSessionId = NormalizeFileNameComponent(
                sessionId,
                "unknown-session"
            );
            string safeDownloadMode = NormalizeFileNameComponent(
                downloadMode,
                "unknown-mode"
            );
            string safeChecksum = NormalizeFileNameComponent(
                sha256,
                Guid.NewGuid().ToString("N")
            );

            if (safeChecksum.Length > 16)
            {
                safeChecksum = safeChecksum.Substring(0, 16);
            }

            return
                $"voice-session-{safeSessionId}-{safeDownloadMode}-{safeChecksum}.ogg";
        }

        private static bool TryReadContentDispositionFileName(
            string contentDisposition,
            out string fileName
        )
        {
            fileName = string.Empty;

            if (string.IsNullOrWhiteSpace(contentDisposition))
            {
                return false;
            }

            string[] parts = contentDisposition.Split(';');

            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index].Trim();

                if (!part.StartsWith(
                        "filename=",
                        StringComparison.OrdinalIgnoreCase
                    ))
                {
                    continue;
                }

                string candidate = part.Substring("filename=".Length).Trim();

                if (
                    candidate.Length >= 2 &&
                    candidate[0] == '"' &&
                    candidate[candidate.Length - 1] == '"'
                )
                {
                    candidate = candidate.Substring(1, candidate.Length - 2);
                }

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return false;
                }

                string safeName = Path.GetFileName(candidate);

                if (!string.Equals(
                        candidate,
                        safeName,
                        StringComparison.Ordinal
                    ))
                {
                    return false;
                }

                if (safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return false;
                }

                if (!safeName.EndsWith(
                        ".ogg",
                        StringComparison.OrdinalIgnoreCase
                    ))
                {
                    return false;
                }

                fileName = safeName;
                return true;
            }

            return false;
        }

        private static string NormalizeFileNameComponent(
            string value,
            string fallback
        )
        {
            string source = string.IsNullOrWhiteSpace(value)
                ? fallback
                : value.Trim();
            StringBuilder builder = new StringBuilder(source.Length);

            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];

                if (
                    char.IsLetterOrDigit(character) ||
                    character == '-' ||
                    character == '_'
                )
                {
                    builder.Append(character);
                }
                else
                {
                    builder.Append('-');
                }
            }

            string normalized = builder.ToString().Trim('-');

            return normalized.Length > 0
                ? normalized
                : fallback;
        }

        private static string WriteDownloadedFileWithoutOverwrite(
            string directory,
            string fileName,
            byte[] bytes
        )
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidDataException(
                    "Voice recording download bytes are empty."
                );
            }

            string safeFileName = Path.GetFileName(fileName);

            if (
                string.IsNullOrWhiteSpace(safeFileName) ||
                !string.Equals(
                    safeFileName,
                    fileName,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    "Voice recording download file name is invalid."
                );
            }

            string nameWithoutExtension =
                Path.GetFileNameWithoutExtension(safeFileName);
            string extension = Path.GetExtension(safeFileName);
            string temporaryPath = Path.Combine(
                directory,
                $".{nameWithoutExtension}.{Guid.NewGuid():N}.partial"
            );

            try
            {
                File.WriteAllBytes(temporaryPath, bytes);

                for (int attempt = 0; attempt < 10000; attempt++)
                {
                    string candidateFileName = attempt == 0
                        ? safeFileName
                        : $"{nameWithoutExtension} ({attempt + 1}){extension}";
                    string candidatePath = Path.Combine(
                        directory,
                        candidateFileName
                    );

                    try
                    {
                        File.Move(temporaryPath, candidatePath);
                        return candidatePath;
                    }
                    catch (IOException)
                    {
                        if (!File.Exists(candidatePath))
                        {
                            throw;
                        }
                    }
                }

                throw new IOException(
                    "Voice recording download could not reserve a unique output path."
                );
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception cleanupException)
                    {
                        Debug.LogWarning(
                            $"VOICE_RECORDING_DOWNLOAD_TEMP_CLEANUP_FAILED | path={temporaryPath} | error={cleanupException.Message}"
                        );
                    }
                }
            }
        }

        private static string ResolveDownloadDirectory()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string documentsPath =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments
                );

            if (!string.IsNullOrWhiteSpace(documentsPath))
            {
                return Path.Combine(
                    documentsPath,
                    "Metarang",
                    "VoiceRecordings"
                );
            }
#endif

            return Path.Combine(
                Application.persistentDataPath,
                "VoiceRecordings"
            );
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private static void RevealDownloadedFileInExplorer(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Debug.LogWarning(
                    "VOICE_RECORDING_DOWNLOAD_REVEAL_PATH_EMPTY"
                );
                return;
            }

            try
            {
                string fullPath = Path.GetFullPath(filePath);

                if (!File.Exists(fullPath))
                {
                    Debug.LogWarning(
                        $"VOICE_RECORDING_DOWNLOAD_REVEAL_FILE_NOT_FOUND | path={fullPath}"
                    );
                    return;
                }

                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{fullPath}\"",
                        UseShellExecute = true
                    }
                );

                Debug.Log(
                    $"VOICE_RECORDING_DOWNLOAD_REVEAL=PASS | path={fullPath}"
                );
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"VOICE_RECORDING_DOWNLOAD_REVEAL_FAILED | path={filePath} | error={exception.Message}"
                );
            }
        }
#endif

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
