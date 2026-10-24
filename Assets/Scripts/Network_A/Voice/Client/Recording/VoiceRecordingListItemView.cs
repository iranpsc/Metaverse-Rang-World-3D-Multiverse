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
using UnityEngine.Events;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Recording
{
    public sealed class VoiceRecordingListItemView : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";

        // Poll a single authorized session's inexpensive status endpoint.
        // Never scan every historical recording on each retry.
        private static readonly float[] FinalizeRefreshRetryDelaysSeconds =
        {
            1f,
            2f,
            3f,
            5f
        };

        private const float PassiveListRefreshIntervalSeconds = 60f;

        // Only a small initial window is rendered. Older entries are appended
        // when the user scrolls to the bottom, instead of instantiating hundreds.
        private const int InitialRenderedRowCount = 24;
        private const int AdditionalRenderedRowCount = 24;
        private const int MaxNewRowsPerFrame = 4;

        [Serializable]
        private sealed class VoiceRecordingListResponse
        {
            public bool success;
            public int count;
            public VoiceRecordingListEntry[] recordings;
            public string reason;
        }

        [Serializable]
        private sealed class VoiceRecordingStatusResponse
        {
            public bool success;
            public bool ready;
            public VoiceRecordingListEntry recording;
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

        private sealed class RecordingUiItem
        {
            public GameObject Root;
            public Button Button;
            public TextMeshProUGUI Text;
            public UnityAction ClickHandler;
            public string DisplayText;
        }

        private readonly List<GameObject> recordingItems = new List<GameObject>();
        private readonly Dictionary<string, RecordingUiItem> uiItemsBySessionId =
            new Dictionary<string, RecordingUiItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<RecordingUiItem> pooledUiItems =
            new Stack<RecordingUiItem>();
        private readonly List<string> visibleSessionOrder = new List<string>();
        private readonly List<VoiceRecordingListEntry> serverRecordingRows =
            new List<VoiceRecordingListEntry>();
        private readonly List<string> renderingSessionIds = new List<string>();
        private readonly HashSet<string> renderingSessionSet =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> sessionsToRecycle = new List<string>();
        private ScrollRect recordingScrollRect;
        private bool scrollListenerRegistered;
        private bool bottomLoadArmed = true;
        private int renderedRowLimit = InitialRenderedRowCount;
        private bool uiSyncRunning;
        private Coroutine appendRowsRoutine;
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
        private Coroutine passiveRefreshRoutine;

        private void Awake()
        {
            ValidateReferences();
        }

        private void OnEnable()
        {
            TryResolveVoiceRuntimeSubscription();
            TryRegisterScrollListener();

            if (loadOnEnable) RefreshRecordingList();

            if (loadOnEnable && passiveRefreshRoutine == null)
            {
                passiveRefreshRoutine = StartCoroutine(PassiveRefreshRecordingListRoutine());
            }

            if (recordingExpectedSessionIds.Count > 0)
            {
                QueuePendingFinalizeRefreshForExpectedRecordings("recording_list_reenabled");
            }
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
            UnregisterScrollListener();

            if (appendRowsRoutine != null)
            {
                StopCoroutine(appendRowsRoutine);
                appendRowsRoutine = null;
            }

            uiSyncRunning = false;
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

            if (passiveRefreshRoutine != null)
            {
                StopCoroutine(passiveRefreshRoutine);
                passiveRefreshRoutine = null;
            }

            pendingFinalizedSessions.Clear();
            lastObservedActiveSessionCount = -1;
        }

        private void OnDestroy()
        {
            UnsubscribeVoiceRuntime();
            UnregisterScrollListener();
            ClearRecordingItems();
            loadedRecordingSessionIds.Clear();
            serverRecordingRows.Clear();
            pendingFinalizedSessions.Clear();
            recordingExpectedSessionIds.Clear();
            lastObservedActiveSessionCount = -1;
        }

        public void RefreshRecordingList()
        {
            if (!isActiveAndEnabled) return;

            if (appendRowsRoutine != null)
            {
                StopCoroutine(appendRowsRoutine);
                appendRowsRoutine = null;
            }

            uiSyncRunning = false;
            if (refreshRoutine != null)
            {
                StopCoroutine(refreshRoutine);
                refreshRoutine = null;
            }

            refreshRoutine = StartCoroutine(RefreshRecordingListRoutine());
        }

        public void ClearRecordingItems()
        {
            // Explicit clear still destroys all UI, including inactive pooled rows.
            foreach (RecordingUiItem ui in uiItemsBySessionId.Values)
            {
                ReleaseRecordingUiItem(ui, true);
            }

            while (pooledUiItems.Count > 0)
            {
                ReleaseRecordingUiItem(pooledUiItems.Pop(), true);
            }

            uiItemsBySessionId.Clear();
            recordingItems.Clear();
            visibleSessionOrder.Clear();
            renderingSessionIds.Clear();
            renderingSessionSet.Clear();
            sessionsToRecycle.Clear();
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

            if (uiItemsBySessionId.TryGetValue(normalizedSessionId, out RecordingUiItem existing))
            {
                UpdateRecordingUiText(existing, displayText);
                return true;
            }

            RecordingUiItem ui;
            if (pooledUiItems.Count > 0)
            {
                ui = pooledUiItems.Pop();
            }
            else
            {
                GameObject item = Instantiate(recordingItemPrefab, recordingListContent);
                if (item == null)
                {
                    Debug.LogError("VOICE_RECORDING_LIST_ITEM_INSTANTIATE_FAILED | sessionId=" + normalizedSessionId);
                    return false;
                }

                Button button = item.GetComponentInChildren<Button>(true);
                TextMeshProUGUI text = item.GetComponentInChildren<TextMeshProUGUI>(true);
                if (button == null || text == null)
                {
                    Debug.LogError("VOICE_RECORDING_LIST_ITEM_UI_MISSING | sessionId=" + normalizedSessionId +
                                   " | button=" + (button != null) + " | text=" + (text != null));
                    item.SetActive(false);
                    Destroy(item);
                    return false;
                }

                ui = new RecordingUiItem { Root = item, Button = button, Text = text };
            }

            // Rebind the recycled row without touching persistent prefab listeners.
            string capturedSessionId = normalizedSessionId;
            ui.ClickHandler = () => HandleRecordingItemClicked(capturedSessionId);
            ui.Button.onClick.AddListener(ui.ClickHandler);
            ui.Button.interactable = true;
            UpdateRecordingUiText(ui, displayText);
            ui.Root.SetActive(true);

            uiItemsBySessionId.Add(normalizedSessionId, ui);
            recordingItems.Add(ui.Root);
            return true;
        }

        private static void UpdateRecordingUiText(RecordingUiItem ui, string displayText)
        {
            string value = displayText ?? string.Empty;
            if (ui.DisplayText == value) return;
            ui.DisplayText = value;
            ui.Text.text = value;
        }

        private static void ReleaseRecordingUiItem(RecordingUiItem ui, bool destroy)
        {
            if (ui == null || ui.Root == null) return;
            if (ui.ClickHandler != null)
            {
                ui.Button.onClick.RemoveListener(ui.ClickHandler);
                ui.ClickHandler = null;
            }
            ui.Root.SetActive(false);
            if (destroy) Destroy(ui.Root);
        }

        private void RecycleRecordingUiItem(string sessionId)
        {
            if (!uiItemsBySessionId.TryGetValue(sessionId, out RecordingUiItem ui)) return;
            uiItemsBySessionId.Remove(sessionId);
            recordingItems.Remove(ui.Root);
            ReleaseRecordingUiItem(ui, false);
            pooledUiItems.Push(ui);
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

        private IEnumerator PassiveRefreshRecordingListRoutine()
        {
            while (isActiveAndEnabled)
            {
                yield return new WaitForSecondsRealtime(PassiveListRefreshIntervalSeconds);

                if (!isActiveAndEnabled)
                {
                    break;
                }

                if (refreshRoutine == null && finalizeRefreshRoutine == null)
                {
                    RefreshRecordingList();
                }
            }

            passiveRefreshRoutine = null;
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
            yield return SynchronizeVisibleRecordingRowsRoutine();
            RemoveVisiblePendingFinalizedSessions();

            Debug.Log(
                "VOICE_RECORDING_LIST_LOAD=PASS | serverCount=" +
                response.count +
                " | availableCount=" + serverRecordingRows.Count +
                " | renderedCount=" + recordingItems.Count
            );

            refreshRoutine = null;
        }

        private void RenderRecordingList(VoiceRecordingListEntry[] recordings)
        {
            // Keep every authorized session ID for finalize detection, but avoid
            // allocating Unity objects for all historical recordings.
            loadedRecordingSessionIds.Clear();
            serverRecordingRows.Clear();

            if (recordings == null || recordings.Length == 0)
            {
                Debug.Log("VOICE_RECORDING_LIST_EMPTY");
                return;
            }

            for (int i = 0; i < recordings.Length; i++)
            {
                VoiceRecordingListEntry recording = recordings[i];
                if (recording == null || string.IsNullOrWhiteSpace(recording.sessionId)) continue;

                string normalizedSessionId = recording.sessionId.Trim();
                if (!loadedRecordingSessionIds.Add(normalizedSessionId)) continue;
                serverRecordingRows.Add(recording);
            }
        }

        private IEnumerator SynchronizeVisibleRecordingRowsRoutine()
        {
            uiSyncRunning = true;
            renderingSessionIds.Clear();
            renderingSessionSet.Clear();
            sessionsToRecycle.Clear();

            int targetCount = Math.Min(serverRecordingRows.Count, renderedRowLimit);
            for (int i = 0; i < targetCount; i++)
            {
                string sessionId = serverRecordingRows[i].sessionId.Trim();
                renderingSessionIds.Add(sessionId);
                renderingSessionSet.Add(sessionId);
            }

            bool orderChanged = visibleSessionOrder.Count != renderingSessionIds.Count;
            if (!orderChanged)
            {
                for (int i = 0; i < renderingSessionIds.Count; i++)
                {
                    if (!string.Equals(visibleSessionOrder[i], renderingSessionIds[i],
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        orderChanged = true;
                        break;
                    }
                }
            }

            foreach (string sessionId in uiItemsBySessionId.Keys)
            {
                if (!renderingSessionSet.Contains(sessionId)) sessionsToRecycle.Add(sessionId);
            }

            for (int i = 0; i < sessionsToRecycle.Count; i++)
            {
                RecycleRecordingUiItem(sessionsToRecycle[i]);
            }

            int newRowsThisFrame = 0;
            for (int i = 0; i < targetCount; i++)
            {
                VoiceRecordingListEntry recording = serverRecordingRows[i];
                string sessionId = renderingSessionIds[i];
                string label = BuildRecordingDisplayText(recording);

                if (uiItemsBySessionId.TryGetValue(sessionId, out RecordingUiItem existing))
                {
                    UpdateRecordingUiText(existing, label);
                    continue;
                }

                if (!AddRecordingItem(sessionId, label)) continue;
                newRowsThisFrame++;
                if (newRowsThisFrame >= MaxNewRowsPerFrame)
                {
                    newRowsThisFrame = 0;
                    yield return null;
                }
            }

            if (orderChanged)
            {
                // Only reorder when the server order actually changed.
                // This preserves the existing prefab, Canvas layout and click actions.
                for (int i = 0; i < renderingSessionIds.Count; i++)
                {
                    if (uiItemsBySessionId.TryGetValue(renderingSessionIds[i], out RecordingUiItem ui))
                    {
                        ui.Root.transform.SetAsLastSibling();
                    }
                }
            }

            visibleSessionOrder.Clear();
            visibleSessionOrder.AddRange(renderingSessionIds);
            uiSyncRunning = false;
        }

        private void TryRegisterScrollListener()
        {
            if (scrollListenerRegistered) return;
            if (recordingListContent == null) return;

            recordingScrollRect = recordingListContent.GetComponentInParent<ScrollRect>(true);
            if (recordingScrollRect == null)
            {
                // Preserve the full legacy list when there is no ScrollRect.
                renderedRowLimit = int.MaxValue;
                return;
            }

            recordingScrollRect.onValueChanged.AddListener(HandleRecordingScrollChanged);
            scrollListenerRegistered = true;
        }

        private void UnregisterScrollListener()
        {
            if (!scrollListenerRegistered || recordingScrollRect == null) return;
            recordingScrollRect.onValueChanged.RemoveListener(HandleRecordingScrollChanged);
            scrollListenerRegistered = false;
        }

        private void HandleRecordingScrollChanged(Vector2 normalizedPosition)
        {
            if (!isActiveAndEnabled || recordingScrollRect == null) return;
            if (normalizedPosition.y > 0.16f) bottomLoadArmed = true;
            if (normalizedPosition.y > 0.04f || !bottomLoadArmed) return;
            if (refreshRoutine != null || appendRowsRoutine != null || uiSyncRunning) return;
            if (serverRecordingRows.Count <= renderedRowLimit) return;

            // The scroll must really be scrollable, to avoid fetching all rows
            // automatically when the panel/layout first initializes.
            if (recordingScrollRect.viewport != null && recordingScrollRect.content != null &&
                recordingScrollRect.content.rect.height <= recordingScrollRect.viewport.rect.height + 1f)
                return;

            bottomLoadArmed = false;
            renderedRowLimit = Math.Min(serverRecordingRows.Count,
                                        renderedRowLimit + AdditionalRenderedRowCount);
            appendRowsRoutine = StartCoroutine(AppendVisibleRecordingRowsRoutine());
        }

        private IEnumerator AppendVisibleRecordingRowsRoutine()
        {
            yield return SynchronizeVisibleRecordingRowsRoutine();
            appendRowsRoutine = null;
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

                // Only check the session(s) actually awaiting finalization.
                // The server verifies userId and session metadata, so an
                // unauthorized or incomplete file is never shown as ready.
                bool anyReady = false;
                Task<string> tokenTask = EnsureFreshAccessTokenBeforeListAsync();
                while (!tokenTask.IsCompleted)
                {
                    yield return null;
                }

                string accessToken = string.Empty;
                if (!tokenTask.IsFaulted && !tokenTask.IsCanceled)
                {
                    accessToken = tokenTask.Result;
                }

                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    for (int i = 0; i < dueSessionIds.Count; i++)
                    {
                        string sessionId = dueSessionIds[i];
                        if (!pendingFinalizedSessions.ContainsKey(sessionId)) continue;

                        string url = baseHttpUrl.TrimEnd('/') +
                            "/voice/recordings/" +
                            UnityWebRequest.EscapeURL(sessionId) + "/status";

                        using (UnityWebRequest request = UnityWebRequest.Get(url))
                        {
                            request.SetRequestHeader("Authorization", "Bearer " + accessToken);
                            request.SetRequestHeader("Accept", "application/json");
                            request.timeout = 10;
                            yield return request.SendWebRequest();

                            if (request.result == UnityWebRequest.Result.Success &&
                                request.responseCode == 200)
                            {
                                try
                                {
                                    VoiceRecordingStatusResponse status =
                                        JsonUtility.FromJson<VoiceRecordingStatusResponse>(
                                            request.downloadHandler.text);
                                    if (status != null && status.success && status.ready &&
                                        status.recording != null &&
                                        string.Equals(status.recording.sessionId, sessionId,
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        anyReady = true;
                                        Debug.Log("VOICE_RECORDING_STATUS_READY=PASS" +
                                            " | sessionId=" + sessionId);
                                    }
                                }
                                catch (Exception exception)
                                {
                                    Debug.LogWarning("VOICE_RECORDING_STATUS_PARSE_FAILED | " +
                                        exception.Message);
                                }
                            }
                            else
                            {
                                Debug.LogWarning("VOICE_RECORDING_STATUS_HTTP_FAILED" +
                                    " | sessionId=" + sessionId +
                                    " | code=" + request.responseCode +
                                    " | error=" + request.error);
                            }
                        }
                    }
                }

                if (anyReady)
                {
                    RefreshRecordingList();
                    while (refreshRoutine != null)
                    {
                        yield return null;
                    }
                    RemoveVisiblePendingFinalizedSessions();
                }

                now = Time.realtimeSinceStartup;
                for (int i = 0; i < dueSessionIds.Count; i++)
                {
                    string sessionId = dueSessionIds[i];
                    PendingFinalizeState pending;
                    if (!pendingFinalizedSessions.TryGetValue(sessionId, out pending))
                    {
                        continue;
                    }

                    // Never abandon an early leaver: continue low-cost single-session
                    // status checks at a capped five-second interval.
                    int nextAttemptIndex = pending.AttemptIndex + 1;
                    int retryDelayIndex = Math.Min(
                        nextAttemptIndex,
                        FinalizeRefreshRetryDelaysSeconds.Length - 1
                    );

                    pending.AttemptIndex = nextAttemptIndex;
                    pending.NextAttemptAtRealtime =
                        now + FinalizeRefreshRetryDelaysSeconds[retryDelayIndex];

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
