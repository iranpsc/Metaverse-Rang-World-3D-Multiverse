#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Network_A.Voice.Client.Participants;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.BrowserAudio.WebGL
{
    // Only WebGL UI and microphone device selection. This does not own capture,
    // SFU Producer, voice sessions, reconnect, or participants' mute controls.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(32000)]
    public sealed class VoiceWebGLMicSelectionUiWrapper : MonoBehaviour
    {
        private const string VoiceSceneName = "WebGL_Enviroment_Voice";
        private const string SelectorButtonName = "Btn_select_mic";
        private const string SelectorScrollName = "Scroll View_Mic_Select";
        private const string HostName = "VME2_WEBGL_Mic_Selection_UI";

        private static readonly FieldInfo ParticipantPrefabField =
            typeof(VoiceParticipantListController).GetField(
                "participantItemPrefab", BindingFlags.Instance | BindingFlags.NonPublic);

        [DllImport("__Internal")]
        private static extern int VME2WebGLMicSelectorList(string receiverObject);

        [DllImport("__Internal")]
        private static extern int VME2WebGLMicSelectorSelect(string deviceId, string receiverObject);

        [DllImport("__Internal")]
        private static extern int VME2WebGLMicSelectorMaintain(string receiverObject);

        private Button selectorButton;
        private GameObject scrollRoot;
        private ScrollRect scroll;
        private VoiceParticipantListItemView existingParticipantPrefab;
        private readonly List<GameObject> createdRows = new List<GameObject>();
        private float nextBindAt;
        private float nextMaintainAt;
        private bool listRequestInFlight;
        private bool warned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            GameObject host = GameObject.Find(HostName);
            if (host == null) host = new GameObject(HostName);
            DontDestroyOnLoad(host);
            if (host.GetComponent<VoiceWebGLMicSelectionUiWrapper>() == null)
                host.AddComponent<VoiceWebGLMicSelectionUiWrapper>();
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ReleaseSceneUi();
            nextBindAt = 0f;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ReleaseSceneUi();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ReleaseSceneUi();
        }

        private void Update()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name,
                VoiceSceneName, StringComparison.Ordinal))
            {
                if (selectorButton != null || scrollRoot != null)
                    ReleaseSceneUi();
                return;
            }

            float now = Time.unscaledTime;
            if (now >= nextBindAt)
            {
                BindSceneUi();
                nextBindAt = now + 0.5f;
            }
            if (now >= nextMaintainAt)
            {
                nextMaintainAt = now + 1f;
                // Only reapply a previously user-selected microphone after the
                // existing WebRTC voice pipeline recovers or is rebuilt.
                VME2WebGLMicSelectorMaintain(gameObject.name);
            }
        }

        private void BindSceneUi()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (selectorButton == null)
            {
                Transform buttonTransform = FindInScene(scene, SelectorButtonName);
                if (buttonTransform != null)
                {
                    selectorButton = buttonTransform.GetComponent<Button>();
                    if (selectorButton != null)
                        selectorButton.onClick.AddListener(ToggleSelector);
                }
            }
            if (scrollRoot == null)
            {
                Transform scrollTransform = FindInScene(scene, SelectorScrollName);
                if (scrollTransform != null)
                {
                    scrollRoot = scrollTransform.gameObject;
                    scroll = scrollTransform.GetComponent<ScrollRect>();
                    if (scroll == null)
                        scroll = scrollTransform.GetComponentInChildren<ScrollRect>(true);
                    if (scroll != null) scrollRoot.SetActive(false);
                }
            }
            if (existingParticipantPrefab == null && ParticipantPrefabField != null)
            {
                foreach (VoiceParticipantListController controller in
                    FindObjectsOfType<VoiceParticipantListController>(true))
                {
                    if (controller == null || controller.gameObject.scene != scene)
                        continue;
                    existingParticipantPrefab = ParticipantPrefabField.GetValue(controller)
                        as VoiceParticipantListItemView;
                    if (existingParticipantPrefab != null) break;
                }
            }
            if (selectorButton != null && scroll != null && existingParticipantPrefab != null)
                warned = false;
            else if (!warned)
            {
                warned = true;
                Debug.LogWarning("VME2_WEBGL_MIC_UI=WAIT | button=" + (selectorButton != null) +
                    " | scroll=" + (scroll != null) +
                    " | reusedParticipantPrefab=" + (existingParticipantPrefab != null));
            }
        }

        private static Transform FindInScene(Scene scene, string objectName)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform found = FindChild(root.transform, objectName);
                if (found != null) return found;
            }
            return null;
        }

        private static Transform FindChild(Transform root, string objectName)
        {
            if (string.Equals(root.name, objectName, StringComparison.Ordinal)) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChild(root.GetChild(i), objectName);
                if (found != null) return found;
            }
            return null;
        }

        private void ToggleSelector()
        {
            if (scrollRoot == null || scroll == null || scroll.content == null ||
                existingParticipantPrefab == null)
            {
                Debug.LogWarning("VME2_WEBGL_MIC_UI=FAIL | reason=ui_not_bound");
                return;
            }
            if (scrollRoot.activeSelf)
            {
                scrollRoot.SetActive(false);
                return;
            }
            ClearRows();
            scrollRoot.SetActive(true);
            listRequestInFlight = true;
            if (VME2WebGLMicSelectorList(gameObject.name) == 0)
            {
                listRequestInFlight = false;
                Debug.LogError("VME2_WEBGL_MIC_LIST=FAIL | reason=browser_interface_unavailable");
            }
        }

        // Called from the WebGL .jslib on the Unity main thread.
        public void OnVME2WebGLMicDevices(string payload)
        {
            listRequestInFlight = false;
            if (scrollRoot == null || !scrollRoot.activeSelf || scroll == null ||
                !string.Equals(SceneManager.GetActiveScene().name,
                    VoiceSceneName, StringComparison.Ordinal)) return;

            ClearRows();
            if (string.IsNullOrEmpty(payload) || !payload.StartsWith("OK|", StringComparison.Ordinal))
            {
                Debug.LogWarning("VME2_WEBGL_MIC_LIST=FAIL | browser=" + payload);
                return;
            }

            string[] parts = payload.Substring(3).Split('|');
            int count = 0;
            foreach (string part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                string id = Uri.UnescapeDataString(part.Substring(0, colon));
                string label = Uri.UnescapeDataString(part.Substring(colon + 1));
                if (string.IsNullOrEmpty(id)) continue;
                CreateRow(scroll.content, label, id);
                count++;
            }
            scroll.verticalNormalizedPosition = 1f;
            Debug.Log("VME2_WEBGL_MIC_LIST=PASS | count=" + count +
                " | reusedPrefab=" + existingParticipantPrefab.name);
        }

        private void CreateRow(Transform content, string label, string deviceId)
        {
            VoiceParticipantListItemView row =
                Instantiate(existingParticipantPrefab, content, false);
            // Prefab is reused VISUALLY only: do not execute per-user
            // microphone/speaker authorization from the participant list.
            row.enabled = false;
            row.SetUserName(label);
            foreach (Button child in row.GetComponentsInChildren<Button>(true))
                if (child != null && child.gameObject != row.gameObject)
                    child.gameObject.SetActive(false);

            Image image = row.GetComponent<Image>();
            if (image == null) image = row.gameObject.AddComponent<Image>();
            if (image.sprite == null)
                image.color = new Color(1f, 1f, 1f, 0.001f);
            image.raycastTarget = true;
            Button button = row.GetComponent<Button>();
            if (button == null) button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() =>
            {
                if (VME2WebGLMicSelectorSelect(deviceId, gameObject.name) == 0)
                    Debug.LogWarning("VME2_WEBGL_MIC_SWITCH=FAIL | reason=not_started");
                if (scrollRoot != null) scrollRoot.SetActive(false);
            });
            createdRows.Add(row.gameObject);
        }

        public void OnVME2WebGLMicSelected(string payload)
        {
            if (payload != null && payload.StartsWith("PASS|", StringComparison.Ordinal))
                Debug.Log("VME2_WEBGL_MIC_SWITCH=PASS | " + payload.Substring(5));
            else
                Debug.LogWarning("VME2_WEBGL_MIC_SWITCH=FAIL | browser=" + payload);
        }

        private void ClearRows()
        {
            foreach (GameObject row in createdRows)
                if (row != null) Destroy(row);
            createdRows.Clear();
        }

        private void ReleaseSceneUi()
        {
            if (selectorButton != null)
                selectorButton.onClick.RemoveListener(ToggleSelector);
            if (scrollRoot != null) scrollRoot.SetActive(false);
            ClearRows();
            selectorButton = null;
            scrollRoot = null;
            scroll = null;
            existingParticipantPrefab = null;
            listRequestInFlight = false;
            warned = false;
        }
    }
}
#endif
