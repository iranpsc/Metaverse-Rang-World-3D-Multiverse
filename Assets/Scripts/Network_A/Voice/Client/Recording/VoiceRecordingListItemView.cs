using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Protocol;
using Network_A.Voice.Client.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Recording
{
    public sealed class VoiceRecordingListItemView : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";

        private static readonly float[] FinalizeRefreshRetryDelaysSeconds =
        {
            1f,
            2f,
            4f,
            8f,
            15f,
            30f,
            60f
        };

        [Serializable]
        private sealed class VoiceRecordingListResponse
        {
            public bool success;
            public int count;
            public VoiceRecordingListEntry[] recordings;
            public string reason;
        }

        [Serializable]
        private sealed class VoiceRecordingListEntry
        {
            public string sessionId;
            public long startedAtMs;
            public long endedAtMs;
            public long durationMs;
            public int authorizedIntervalCount;
            public string downloadMode;
        }

        [Header("Recording List UI")]
        [SerializeField] private Transform recordingListContent;
        [SerializeField] private GameObject recordingItemPrefab;

        [Header("Recording Download")]
        [SerializeField] private VoiceRecordingDownloadClient recordingDownloadClient;

        [Header("Server")]
        [SerializeField] private string baseHttpUrl = "https://dev-world-3d.metarang.com";

        [Header("Auth Refresh Gate")]
        [SerializeField] private int accessTokenRefreshSkewSeconds = 60;

        [Header("List")]
        [SerializeField] private bool loadOnEnable = true;

        private sealed class PendingFinalizeState
        {
            public int AttemptIndex;
            public float NextAttemptAtRealtime;
        }

        private readonly List<GameObject> recordingItems = new List<GameObject>();
        private readonly HashSet<string> loadedRecordingSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> recordingExpectedSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingFinalizeState> pendingFinalizedSessions =
            new Dictionary<string, PendingFinalizeState>(StringComparer.OrdinalIgnoreCase);

        private VoiceClientRuntime voiceRuntime;
        private int lastObservedActiveSessionCount = -1;
        private Coroutine refreshRoutine;
        private Coroutine finalizeRefreshRoutine;

        private void Awake()
        {
            ValidateReferences();
        }

        private void OnEnable()
        {
            TryResolveVoiceRuntimeSubscription();

            if (loadOnEnable) RefreshRecordingList();
        }

        private void Update()
        {
            if (voiceRuntime == null)
            {
                TryResolveVoiceRuntimeSubscription();
            }

            TrackVoiceRuntimeSessionCountChange();
        }

        private void OnDisable()
        {
            UnsubscribeVoiceRuntime();

            if (refreshRoutine != null)
            {
                StopCoroutine(refreshRoutine);
                refreshRoutine = null;
            }

            if (finalizeRefreshRoutine != null)
            {
                StopCoroutine(finalizeRefreshRoutine);
                finalizeRefreshRoutine = null;
            }

            pendingFinalizedSessions.Clear();
            recordingExpectedSessionIds.Clear();
            lastObservedActiveSessionCount = -1;
        }

        private void OnDestroy()
        {
            UnsubscribeVoiceRuntime();
            recordingItems.Clear();
            loadedRecordingSessionIds.Clear();
            pendingFinalizedSessions.Clear();
            recordingExpectedSessionIds.Clear();
            lastObservedActiveSessionCount = -1;
        }

        public void RefreshRecordingList()
        {
            if (!isActiveAndEnabled) return;

            if (refreshRoutine != null)
            {
                StopCoroutine(refreshRoutine);
                refreshRoutine = null;
            }

            refreshRoutine = StartCoroutine(RefreshRecordingListRoutine());
        }

        public void ClearRecordingItems()
        {
            for (int i = 0; i < recordingItems.Count; i++)
            {
                GameObject item = recordingItems[i];
                if (item == null) continue;

                item.SetActive(false);
                Destroy(item);
            }

            recordingItems.Clear();
        }

        public bool AddRecordingItem(string sessionId, string displayText)
        {
            if (!ValidateReferences()) return false;

            string normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
                ? string.Empty
                : sessionId.Trim();

            if (normalizedSessionId.Length == 0)
            {
                Debug.LogError("VOICE_RECORDING_LIST_SESSION_ID_EMPTY");
                return false;
            }

            GameObject item = Instantiate(recordingItemPrefab, recordingListContent);

            if (item == null)
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_ITEM_INSTANTIATE_FAILED | sessionId=" +
                    normalizedSessionId
                );
                return false;
            }

            Button button = item.GetComponentInChildren<Button>(true);
            TextMeshProUGUI text = item.GetComponentInChildren<TextMeshProUGUI>(true);

            if (button == null || text == null)
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_ITEM_UI_MISSING | sessionId=" +
                    normalizedSessionId +
                    " | button=" + (button != null) +
                    " | text=" + (text != null)
                );

                item.SetActive(false);
                Destroy(item);
                return false;
            }

            text.text = displayText ?? string.Empty;
            button.interactable = true;

            string capturedSessionId = normalizedSessionId;
            button.onClick.AddListener(
                () => HandleRecordingItemClicked(capturedSessionId)
            );

            item.SetActive(true);
            recordingItems.Add(item);

            return true;
        }

        public void SetRecordingItemsInteractable(bool value)
        {
            for (int i = 0; i < recordingItems.Count; i++)
            {
                GameObject item = recordingItems[i];
                if (item == null) continue;

                Button button = item.GetComponentInChildren<Button>(true);
                if (button != null) button.interactable = value;
            }
        }

        private IEnumerator RefreshRecordingListRoutine()
        {
            if (!ValidateReferences())
            {
                refreshRoutine = null;
                yield break;
            }

            Task<string> tokenTask = EnsureFreshAccessTokenBeforeListAsync();

            while (!tokenTask.IsCompleted)
            {
                yield return null;
            }

            if (tokenTask.IsCanceled)
            {
                Debug.LogError("VOICE_RECORDING_LIST_TOKEN_TASK_CANCELED");
                refreshRoutine = null;
                yield break;
            }

            if (tokenTask.IsFaulted)
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_TOKEN_TASK_FAILED | " +
                    tokenTask.Exception
                );
                refreshRoutine = null;
                yield break;
            }

            string accessToken = tokenTask.Result;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                Debug.LogError("VOICE_RECORDING_LIST_ACCESS_TOKEN_EMPTY");
                refreshRoutine = null;
                yield break;
            }

            string url =
                $"{baseHttpUrl.TrimEnd('/')}/voice/recordings";

            using UnityWebRequest request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            request.SetRequestHeader("Accept", "application/json");

            Debug.Log(
                $"VOICE_RECORDING_LIST_REQUEST_START | url={url}"
            );

            yield return request.SendWebRequest();

            string body =
                request.downloadHandler != null
                    ? request.downloadHandler.text
                    : string.Empty;

            if (request.responseCode == 401)
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_AUTH_FAILED_401 | body=" + body
                );
                refreshRoutine = null;
                yield break;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError(
                    $"VOICE_RECORDING_LIST_HTTP_FAILED | status={request.responseCode} | error={request.error} | body={body}"
                );
                refreshRoutine = null;
                yield break;
            }

            VoiceRecordingListResponse response;

            try
            {
                response = JsonUtility.FromJson<VoiceRecordingListResponse>(body);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_JSON_PARSE_FAILED | " +
                    exception
                );
                refreshRoutine = null;
                yield break;
            }

            if (response == null || !response.success)
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_RESPONSE_FAILED | reason=" +
                    (response != null ? response.reason : "response_null")
                );
                refreshRoutine = null;
                yield break;
            }

            RenderRecordingList(response.recordings);

            Debug.Log(
                "VOICE_RECORDING_LIST_LOAD=PASS | serverCount=" +
                response.count +
                " | renderedCount=" +
                recordingItems.Count
            );

            refreshRoutine = null;
        }

        private void RenderRecordingList(VoiceRecordingListEntry[] recordings)
        {
            ClearRecordingItems();
            loadedRecordingSessionIds.Clear();

            if (recordings == null || recordings.Length == 0)
            {
                Debug.Log(
                    "VOICE_RECORDING_LIST_EMPTY"
                );
                return;
            }

            for (int i = 0; i < recordings.Length; i++)
            {
                VoiceRecordingListEntry recording = recordings[i];

                if (
                    recording == null ||
                    string.IsNullOrWhiteSpace(recording.sessionId)
                )
                {
                    continue;
                }

                string normalizedSessionId = recording.sessionId.Trim();

                if (
                    AddRecordingItem(
                        normalizedSessionId,
                        BuildRecordingDisplayText(recording)
                    )
                )
                {
                    loadedRecordingSessionIds.Add(normalizedSessionId);
                }
            }
        }

        private void TryResolveVoiceRuntimeSubscription()
        {
            if (voiceRuntime != null) return;

            GameObject root = GameObject.Find(RuntimeRootName);
            if (root == null) return;

            VoiceClientRuntime resolvedRuntime =
                root.GetComponent<VoiceClientRuntime>();

            if (resolvedRuntime == null) return;

            voiceRuntime = resolvedRuntime;
            voiceRuntime.SessionClosed -= HandleVoiceSessionClosed;
            voiceRuntime.SessionClosed += HandleVoiceSessionClosed;
            voiceRuntime.RecordingStateChanged -= HandleVoiceRecordingStateChanged;
            voiceRuntime.RecordingStateChanged += HandleVoiceRecordingStateChanged;
            lastObservedActiveSessionCount = Math.Max(0, voiceRuntime.ActiveSessionCount);

            Debug.Log(
                "VOICE_RECORDING_LIST_RUNTIME_SUBSCRIPTION=PASS" +
                " | activeSessionCount=" + lastObservedActiveSessionCount
            );
        }

        private void UnsubscribeVoiceRuntime()
        {
            if (voiceRuntime == null) return;

            voiceRuntime.SessionClosed -= HandleVoiceSessionClosed;
            voiceRuntime.RecordingStateChanged -= HandleVoiceRecordingStateChanged;
            voiceRuntime = null;
            lastObservedActiveSessionCount = -1;
        }

        private void HandleVoiceRecordingStateChanged(
            string sessionId,
            VoiceClientRecordingState state,
            byte reason)
        {
            string normalizedSessionId =
                string.IsNullOrWhiteSpace(sessionId)
                    ? string.Empty
                    : sessionId.Trim();

            if (normalizedSessionId.Length == 0) return;

            if (
                state == VoiceClientRecordingState.Recording ||
                state == VoiceClientRecordingState.Finalizing ||
                state == VoiceClientRecordingState.Ready
            )
            {
                recordingExpectedSessionIds.Add(normalizedSessionId);

                Debug.Log(
                    "VOICE_RECORDING_LIST_RECORDING_EXPECTED=PASS" +
                    " | sessionId=" + normalizedSessionId +
                    " | state=" + state +
                    " | reason=" + reason
                );

                if (state == VoiceClientRecordingState.Ready)
                {
                    QueueFinalizeRefreshForSession(
                        normalizedSessionId,
                        "recording_state_ready"
                    );
                }

                return;
            }

            if (
                state == VoiceClientRecordingState.Declined ||
                state == VoiceClientRecordingState.Failed
            )
            {
                recordingExpectedSessionIds.Remove(normalizedSessionId);
                pendingFinalizedSessions.Remove(normalizedSessionId);

                Debug.Log(
                    "VOICE_RECORDING_LIST_RECORDING_NOT_EXPECTED=PASS" +
                    " | sessionId=" + normalizedSessionId +
                    " | state=" + state +
                    " | reason=" + reason
                );
            }
        }

        private void HandleVoiceSessionClosed(string sessionId)
        {
            string normalizedSessionId =
                string.IsNullOrWhiteSpace(sessionId)
                    ? string.Empty
                    : sessionId.Trim();

            if (normalizedSessionId.Length == 0)
            {
                Debug.LogWarning(
                    "VOICE_RECORDING_LIST_SESSION_CLOSED_ID_EMPTY"
                );
                return;
            }

            QueueFinalizeRefreshForSession(
                normalizedSessionId,
                "session_closed"
            );
        }

        private void TrackVoiceRuntimeSessionCountChange()
        {
            if (voiceRuntime == null)
            {
                lastObservedActiveSessionCount = -1;
                return;
            }

            int activeSessionCount = Math.Max(0, voiceRuntime.ActiveSessionCount);

            if (lastObservedActiveSessionCount < 0)
            {
                lastObservedActiveSessionCount = activeSessionCount;
                return;
            }

            if (activeSessionCount < lastObservedActiveSessionCount)
            {
                QueuePendingFinalizeRefreshForExpectedRecordings(
                    "active_session_count_decreased"
                );
            }

            if (activeSessionCount == 0 && pendingFinalizedSessions.Count > 0)
            {
                EnsureFinalizeRefreshRoutineStartedIfAllowed(
                    "active_session_count_zero"
                );
            }

            lastObservedActiveSessionCount = activeSessionCount;
        }

        private void QueuePendingFinalizeRefreshForExpectedRecordings(
            string reason)
        {
            if (recordingExpectedSessionIds.Count == 0) return;

            List<string> expectedSessionIds =
                new List<string>(recordingExpectedSessionIds);

            int queuedCount = 0;

            for (int i = 0; i < expectedSessionIds.Count; i++)
            {
                if (
                    QueueFinalizeRefreshForSession(
                        expectedSessionIds[i],
                        reason
                    )
                )
                {
                    queuedCount++;
                }
            }

            if (queuedCount <= 0) return;

            Debug.Log(
                "VOICE_RECORDING_LIST_LOCAL_LEAVE_FINALIZE_WAIT=PASS" +
                " | reason=" + reason +
                " | queuedCount=" + queuedCount +
                " | pendingCount=" + pendingFinalizedSessions.Count
            );
        }

        private bool QueueFinalizeRefreshForSession(
            string sessionId,
            string reason)
        {
            string normalizedSessionId =
                string.IsNullOrWhiteSpace(sessionId)
                    ? string.Empty
                    : sessionId.Trim();

            if (normalizedSessionId.Length == 0) return false;

            if (loadedRecordingSessionIds.Contains(normalizedSessionId))
            {
                recordingExpectedSessionIds.Remove(normalizedSessionId);
                pendingFinalizedSessions.Remove(normalizedSessionId);

                Debug.Log(
                    "VOICE_RECORDING_LIST_FINALIZED_ALREADY_VISIBLE" +
                    " | sessionId=" + normalizedSessionId +
                    " | reason=" + reason
                );
                return false;
            }

            if (!recordingExpectedSessionIds.Contains(normalizedSessionId))
            {
                Debug.Log(
                    "VOICE_RECORDING_LIST_FINALIZE_WAIT_SKIPPED=PASS" +
                    " | sessionId=" + normalizedSessionId +
                    " | reason=recording_never_started" +
                    " | requestedReason=" + reason
                );
                return false;
            }

            if (pendingFinalizedSessions.ContainsKey(normalizedSessionId))
            {
                EnsureFinalizeRefreshRoutineStartedIfAllowed(reason);
                return false;
            }

            pendingFinalizedSessions.Add(
                normalizedSessionId,
                new PendingFinalizeState
                {
                    AttemptIndex = 0,
                    NextAttemptAtRealtime =
                        Time.realtimeSinceStartup +
                        FinalizeRefreshRetryDelaysSeconds[0]
                }
            );

            Debug.Log(
                "VOICE_RECORDING_LIST_FINALIZE_WAIT_START" +
                " | sessionId=" + normalizedSessionId +
                " | reason=" + reason +
                " | pendingCount=" + pendingFinalizedSessions.Count
            );

            EnsureFinalizeRefreshRoutineStartedIfAllowed(reason);
            return true;
        }

        private void EnsureFinalizeRefreshRoutineStartedIfAllowed(string reason)
        {
            if (!isActiveAndEnabled) return;
            if (pendingFinalizedSessions.Count == 0) return;
            if (finalizeRefreshRoutine != null) return;

            int activeSessionCount =
                voiceRuntime != null
                    ? Math.Max(0, voiceRuntime.ActiveSessionCount)
                    : 0;

            if (activeSessionCount > 0)
            {
                Debug.Log(
                    "VOICE_RECORDING_LIST_FINALIZE_REFRESH_DEFERRED_ACTIVE_VOICE_SESSION=PASS" +
                    " | reason=" + reason +
                    " | activeSessionCount=" + activeSessionCount +
                    " | pendingCount=" + pendingFinalizedSessions.Count
                );
                return;
            }

            finalizeRefreshRoutine =
                StartCoroutine(
                    RefreshUntilFinalizedRecordingsVisibleRoutine()
                );

            Debug.Log(
                "VOICE_RECORDING_LIST_FINALIZE_REFRESH_ROUTINE_START=PASS" +
                " | reason=" + reason +
                " | pendingCount=" + pendingFinalizedSessions.Count
            );
        }

        private IEnumerator RefreshUntilFinalizedRecordingsVisibleRoutine()
        {
            while (pendingFinalizedSessions.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                float nextAttemptAt = float.MaxValue;

                foreach (PendingFinalizeState pending in pendingFinalizedSessions.Values)
                {
                    if (pending.NextAttemptAtRealtime < nextAttemptAt)
                    {
                        nextAttemptAt = pending.NextAttemptAtRealtime;
                    }
                }

                float waitSeconds = Mathf.Max(0f, nextAttemptAt - now);
                if (waitSeconds > 0f)
                {
                    yield return new WaitForSecondsRealtime(waitSeconds);
                }

                if (!isActiveAndEnabled)
                {
                    finalizeRefreshRoutine = null;
                    yield break;
                }

                now = Time.realtimeSinceStartup;
                List<string> dueSessionIds = new List<string>();

                foreach (
                    KeyValuePair<string, PendingFinalizeState> pair
                    in pendingFinalizedSessions
                )
                {
                    if (pair.Value.NextAttemptAtRealtime <= now + 0.01f)
                    {
                        dueSessionIds.Add(pair.Key);
                    }
                }

                if (dueSessionIds.Count == 0)
                {
                    yield return null;
                    continue;
                }

                RefreshRecordingList();

                while (refreshRoutine != null)
                {
                    yield return null;
                }

                RemoveVisiblePendingFinalizedSessions();

                now = Time.realtimeSinceStartup;
                for (int i = 0; i < dueSessionIds.Count; i++)
                {
                    string sessionId = dueSessionIds[i];
                    PendingFinalizeState pending;
                    if (!pendingFinalizedSessions.TryGetValue(sessionId, out pending))
                    {
                        continue;
                    }

                    int nextAttemptIndex = pending.AttemptIndex + 1;
                    if (nextAttemptIndex >= FinalizeRefreshRetryDelaysSeconds.Length)
                    {
                        pendingFinalizedSessions.Remove(sessionId);
                        recordingExpectedSessionIds.Remove(sessionId);

                        Debug.LogWarning(
                            "VOICE_RECORDING_LIST_FINALIZE_WAIT_TIMEOUT" +
                            " | sessionId=" + sessionId +
                            " | attempts=" + FinalizeRefreshRetryDelaysSeconds.Length +
                            " | pendingAfterRemove=" + pendingFinalizedSessions.Count
                        );
                        continue;
                    }

                    pending.AttemptIndex = nextAttemptIndex;
                    pending.NextAttemptAtRealtime =
                        now + FinalizeRefreshRetryDelaysSeconds[nextAttemptIndex];

                    Debug.Log(
                        "VOICE_RECORDING_LIST_FINALIZE_WAIT_RETRY" +
                        " | sessionId=" + sessionId +
                        " | attempt=" + nextAttemptIndex +
                        " | pendingCount=" + pendingFinalizedSessions.Count
                    );
                }
            }

            Debug.Log(
                "VOICE_RECORDING_LIST_FINALIZE_REFRESH=PASS" +
                " | pendingCount=0"
            );

            finalizeRefreshRoutine = null;
        }

        private void RemoveVisiblePendingFinalizedSessions()
        {
            if (pendingFinalizedSessions.Count == 0) return;

            List<string> resolvedSessionIds = null;

            foreach (string sessionId in pendingFinalizedSessions.Keys)
            {
                if (!loadedRecordingSessionIds.Contains(sessionId)) continue;

                if (resolvedSessionIds == null)
                {
                    resolvedSessionIds = new List<string>();
                }

                resolvedSessionIds.Add(sessionId);
            }

            if (resolvedSessionIds == null) return;

            for (int i = 0; i < resolvedSessionIds.Count; i++)
            {
                string sessionId = resolvedSessionIds[i];
                pendingFinalizedSessions.Remove(sessionId);
                recordingExpectedSessionIds.Remove(sessionId);

                Debug.Log(
                    "VOICE_RECORDING_LIST_FINALIZED_VISIBLE=PASS" +
                    " | sessionId=" + sessionId
                );
            }
        }

        private static string BuildRecordingDisplayText(
            VoiceRecordingListEntry recording
        )
        {
            if (recording == null) return string.Empty;

            string duration =
                FormatDuration(recording.durationMs);

            if (recording.startedAtMs <= 0)
            {
                return recording.sessionId + " | " + duration;
            }

            try
            {
                DateTimeOffset startedAt =
                    DateTimeOffset
                        .FromUnixTimeMilliseconds(recording.startedAtMs)
                        .ToLocalTime();

                return startedAt.ToString("yyyy/MM/dd HH:mm") +
                       " | " +
                       duration;
            }
            catch
            {
                return recording.sessionId + " | " + duration;
            }
        }

        private static string FormatDuration(long durationMs)
        {
            long safeDurationMs = Math.Max(0L, durationMs);
            TimeSpan duration =
                TimeSpan.FromMilliseconds(safeDurationMs);

            if (duration.TotalHours >= 1d)
            {
                return string.Format(
                    "{0:00}:{1:00}:{2:00}",
                    (int)duration.TotalHours,
                    duration.Minutes,
                    duration.Seconds
                );
            }

            return string.Format(
                "{0:00}:{1:00}",
                (int)duration.TotalMinutes,
                duration.Seconds
            );
        }

        private bool ValidateReferences()
        {
            bool valid = true;

            if (recordingListContent == null)
            {
                valid = false;
                Debug.LogError(
                    "VOICE_RECORDING_LIST_CONTENT_REFERENCE_MISSING"
                );
            }

            if (recordingItemPrefab == null)
            {
                valid = false;
                Debug.LogError(
                    "VOICE_RECORDING_LIST_PREFAB_REFERENCE_MISSING"
                );
            }

            if (recordingDownloadClient == null)
            {
                valid = false;
                Debug.LogError(
                    "VOICE_RECORDING_LIST_DOWNLOAD_CLIENT_REFERENCE_MISSING"
                );
            }

            if (string.IsNullOrWhiteSpace(baseHttpUrl))
            {
                valid = false;
                Debug.LogError(
                    "VOICE_RECORDING_LIST_BASE_HTTP_URL_EMPTY"
                );
            }

            return valid;
        }

        private void HandleRecordingItemClicked(string sessionId)
        {
            if (recordingDownloadClient == null)
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_DOWNLOAD_CLIENT_REFERENCE_MISSING"
                );
                return;
            }

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Debug.LogError(
                    "VOICE_RECORDING_LIST_CLICK_SESSION_ID_EMPTY"
                );
                return;
            }

            recordingDownloadClient.DownloadRecording(sessionId);
        }

        private async Task<string> EnsureFreshAccessTokenBeforeListAsync()
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
                    "VOICE_RECORDING_LIST_REFRESH_REQUIRED_BUT_REFRESH_TOKEN_EMPTY"
                );
                return string.Empty;
            }

            Debug.Log(
                "VOICE_RECORDING_LIST_ACCESS_TOKEN_REFRESH_START"
            );

            bool refreshed = await AuthRefreshManager.Refresh();

            if (!refreshed)
            {
                Debug.LogWarning(
                    "VOICE_RECORDING_LIST_ACCESS_TOKEN_REFRESH_FAILED"
                );
                return string.Empty;
            }

            string refreshedToken =
                SecureTokenStorage.GetAccessToken();

            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                Debug.LogWarning(
                    "VOICE_RECORDING_LIST_REFRESHED_ACCESS_TOKEN_EMPTY"
                );
                return string.Empty;
            }

            Debug.Log(
                "VOICE_RECORDING_LIST_ACCESS_TOKEN_REFRESH_PASS"
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
                json.IndexOf(
                    ':',
                    keyIndex + pattern.Length
                );

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
    }
}
