using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Recording
{
    public sealed class VoiceRecordingListItemView : MonoBehaviour
    {
        [Header("Recording List UI")]
        [SerializeField] private Transform recordingListContent;
        [SerializeField] private GameObject recordingItemPrefab;

        [Header("Recording Download")]
        [SerializeField] private VoiceRecordingDownloadClient recordingDownloadClient;

        private readonly List<GameObject> recordingItems = new List<GameObject>();

        private void Awake()
        {
            ValidateReferences();
        }

        private void OnDestroy()
        {
            recordingItems.Clear();
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
    }
}
