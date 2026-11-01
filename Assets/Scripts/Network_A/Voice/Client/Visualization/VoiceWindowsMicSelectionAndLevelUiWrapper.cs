#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.Participants;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Visualization
{
    // Windows-only UI adapter. Does not own audio frames, sessions or transport.
    [DefaultExecutionOrder(32000)]
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsMicSelectionAndLevelUiWrapper : MonoBehaviour
    {
        private const string VoiceScene = "Grpc_Enviroment_Voice";
        private const string SelectorButtonName = "Btn_select_mic";
        private const string SelectorScrollName = "Scroll View_Mic_Select";
        private const string MicButtonName = "Btn_Voice_Microphone";
        private const string NativeRootName = "Voice_Windows_Native_Capture_P9_2_1";
        private const string VoiceRuntimeRootName = "Voice_Client_Runtime_Root";

        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo ListPrefabField =
            typeof(VoiceParticipantListController).GetField("participantItemPrefab", PrivateInstance);
        private static readonly FieldInfo NativeFrameField =
            typeof(VoiceWindowsNativeCaptureP921Wrapper).GetField("encodeFrame", PrivateInstance);
        private static readonly FieldInfo NativeEncodedCountField =
            typeof(VoiceWindowsNativeCaptureP921Wrapper).GetField("encodedFrames", PrivateInstance);

        private Button selectorButton;
        private GameObject selectorScrollRoot;
        private ScrollRect selectorScroll;
        private VoiceParticipantListItemView itemPrefab;
        private Button microphoneButton;
        private Image microphoneImage;
        private VoiceClientRuntime runtime;
        private VoiceWindowsNativeCaptureP921Wrapper nativeCapture;
        private readonly List<GameObject> createdRows = new List<GameObject>();
        private Vector3 micBaseScale = Vector3.one;
        private Color micBaseColor = Color.white;
        private bool micBound;
        private float intensity;
        private float lastFrameAt = -100f;
        private float lastNativeRms;
        private long lastEncodedCount = -1;
        private float nextBindAt;
        private bool warnedMissingUi;

        private static readonly Color SpeakingGreen = new Color(0.16f, 1f, 0.35f, 1f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            const string name = "VME2_WIN_Mic_Selection_And_Real_Level_UI";
            GameObject root = GameObject.Find(name);
            if (root == null) root = new GameObject(name);
            DontDestroyOnLoad(root);
            if (root.GetComponent<VoiceWindowsMicSelectionAndLevelUiWrapper>() == null)
                root.AddComponent<VoiceWindowsMicSelectionAndLevelUiWrapper>();
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            nextBindAt = 0f;
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
            if (!string.Equals(SceneManager.GetActiveScene().name, VoiceScene,
                    StringComparison.Ordinal))
            {
                if (selectorButton != null || micBound) ReleaseSceneUi();
                return;
            }

            float now = Time.unscaledTime;
            if (now >= nextBindAt)
            {
                BindSceneUi();
                nextBindAt = now + 0.5f;
            }
            UpdateMicLevel(now);
        }

        private void BindSceneUi()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (selectorButton == null)
            {
                Transform t = FindTransform(scene, SelectorButtonName);
                if (t != null) selectorButton = t.GetComponent<Button>();
                if (selectorButton != null)
                    selectorButton.onClick.AddListener(ToggleMicSelector);
            }
            if (selectorScrollRoot == null)
            {
                Transform t = FindTransform(scene, SelectorScrollName);
                if (t != null)
                {
                    selectorScrollRoot = t.gameObject;
                    selectorScroll = t.GetComponent<ScrollRect>();
                    if (selectorScroll == null)
                        selectorScroll = t.GetComponentInChildren<ScrollRect>(true);
                    selectorScrollRoot.SetActive(false);
                }
            }
            if (itemPrefab == null)
            {
                VoiceParticipantListController[] controllers =
                    FindObjectsOfType<VoiceParticipantListController>(true);
                foreach (var controller in controllers)
                {
                    if (controller == null || controller.gameObject.scene != scene ||
                        ListPrefabField == null) continue;
                    itemPrefab = ListPrefabField.GetValue(controller) as VoiceParticipantListItemView;
                    if (itemPrefab != null) break;
                }
            }
            if (!micBound)
            {
                Transform t = FindTransform(scene, MicButtonName);
                if (t != null)
                {
                    microphoneButton = t.GetComponent<Button>();
                    microphoneImage = microphoneButton != null ? microphoneButton.image : null;
                    if (microphoneImage != null)
                    {
                        micBaseScale = microphoneButton.transform.localScale;
                        micBaseColor = microphoneImage.color;
                        micBound = true;
                    }
                }
            }
            if (runtime == null)
            {
                GameObject root = GameObject.Find(VoiceRuntimeRootName);
                if (root != null) runtime = root.GetComponent<VoiceClientRuntime>();
            }
            if (nativeCapture == null)
            {
                GameObject root = GameObject.Find(NativeRootName);
                if (root != null)
                    nativeCapture = root.GetComponent<VoiceWindowsNativeCaptureP921Wrapper>();
            }
            if (selectorButton != null && selectorScroll != null && itemPrefab != null)
            {
                warnedMissingUi = false;
            }
            else if (!warnedMissingUi)
            {
                warnedMissingUi = true;
                Debug.LogWarning("VME2_WIN_MIC_SELECT_UI=WAIT | button=" +
                    (selectorButton != null) + " | scroll=" + (selectorScroll != null) +
                    " | existingPrefab=" + (itemPrefab != null));
            }
        }

        private static Transform FindTransform(Scene scene, string name)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform found = FindChild(root.transform, name);
                if (found != null) return found;
            }
            return null;
        }

        private static Transform FindChild(Transform t, string name)
        {
            if (string.Equals(t.name, name, StringComparison.Ordinal)) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform found = FindChild(t.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private void ToggleMicSelector()
        {
            if (selectorScrollRoot == null || selectorScroll == null || itemPrefab == null)
            {
                Debug.LogWarning("VME2_WIN_MIC_SELECT_UI=FAIL | reason=ui_not_bound");
                return;
            }
            if (selectorScrollRoot.activeSelf)
            {
                selectorScrollRoot.SetActive(false);
                return;
            }
            PopulateMicRows();
            selectorScrollRoot.SetActive(true);
        }

        private void PopulateMicRows()
        {
            ClearRows();
            Transform content = selectorScroll.content;
            if (content == null)
            {
                Debug.LogError("VME2_WIN_MIC_SELECT_UI=FAIL | reason=scroll_content_missing");
                return;
            }

            if (!VoiceWindowsMicDeviceSelection.TryEnumerate(
                    out List<VoiceWindowsMicDeviceSelection.Device> devices,
                    out string error))
            {
                Debug.LogError("VME2_WIN_MIC_DEVICE_LIST=FAIL | reason=" + error);
                return;
            }

            // Show only physical input devices; the existing default capture remains unchanged until selection.
            foreach (VoiceWindowsMicDeviceSelection.Device device in devices)
            {
                VoiceWindowsMicDeviceSelection.Device selected = device;
                CreateRow(content, device.Name, selected);
            }
            selectorScroll.verticalNormalizedPosition = 1f;
            Debug.Log("VME2_WIN_MIC_DEVICE_LIST=PASS | count=" + devices.Count +
                " | reusedPrefab=" + itemPrefab.name);
        }

        private void CreateRow(Transform content, string caption,
            VoiceWindowsMicDeviceSelection.Device device)
        {
            VoiceParticipantListItemView row = Instantiate(itemPrefab, content, false);
            row.enabled = false; // Important: never dispatch participant mic/speaker control!
            row.SetUserName(caption);
            Button[] children = row.GetComponentsInChildren<Button>(true);
            foreach (Button child in children)
            {
                if (child != null && child.gameObject != row.gameObject)
                    child.gameObject.SetActive(false);
            }
            Image background = row.GetComponent<Image>();
            if (background == null) background = row.gameObject.AddComponent<Image>();
            if (background.sprite == null) background.color = new Color(1f, 1f, 1f, 0.001f);
            background.raycastTarget = true;
            Button button = row.GetComponent<Button>();
            if (button == null) button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.onClick.RemoveAllListeners(); // clone only, not the original prefab
            button.onClick.AddListener(() =>
            {
                VoiceWindowsMicDeviceSelection.Select(device);
                if (selectorScrollRoot != null) selectorScrollRoot.SetActive(false);
            });
            createdRows.Add(row.gameObject);
        }

        private void ClearRows()
        {
            foreach (GameObject row in createdRows)
                if (row != null) Destroy(row);
            createdRows.Clear();
        }

        private void UpdateMicLevel(float now)
        {
            if (!micBound || microphoneImage == null || microphoneButton == null)
                return;
            bool allowed = runtime != null && runtime.IsAuthenticated &&
                           runtime.ActiveSessionCount > 0 && !runtime.IsMicrophoneMuted;
            float rms = 0f;
            if (allowed && nativeCapture != null &&
                NativeEncodedCountField != null && NativeFrameField != null)
            {
                try
                {
                    long count = (long)NativeEncodedCountField.GetValue(nativeCapture);
                    if (count != lastEncodedCount)
                    {
                        lastEncodedCount = count;
                        float[] samples = NativeFrameField.GetValue(nativeCapture) as float[];
                        if (count > 0 && samples != null && samples.Length > 0)
                        {
                            double sumSq = 0d;
                            for (int i = 0; i < samples.Length; i++)
                                sumSq += samples[i] * samples[i];
                            lastNativeRms = (float)Math.Sqrt(sumSq / samples.Length);
                            lastFrameAt = now;
                        }
                    }
                }
                catch (Exception) { }
            }
            if (now - lastFrameAt <= 0.22f) rms = lastNativeRms;
            float target = allowed && rms > 0.0016f
                ? Mathf.Clamp01((rms - 0.0016f) / 0.025f)
                : 0f;
            if (!allowed) intensity = 0f;
            else
            {
                float speed = target > intensity ? 17f : 9f;
                float factor = 1f - Mathf.Exp(-speed * Mathf.Min(0.1f, Time.unscaledDeltaTime));
                intensity = Mathf.Lerp(intensity, target, factor);
                if (intensity < 0.015f) intensity = 0f;
            }
            float greenAmount = intensity > 0.025f
                ? Mathf.Lerp(0.65f, 1f, intensity) : 0f;
            Color green = SpeakingGreen;
            green.a = micBaseColor.a;
            microphoneImage.color = Color.Lerp(micBaseColor, green, greenAmount);
            microphoneButton.transform.localScale = micBaseScale * (1f + intensity * 0.25f);
        }

        private void ReleaseSceneUi()
        {
            if (selectorButton != null) selectorButton.onClick.RemoveListener(ToggleMicSelector);
            if (selectorScrollRoot != null) selectorScrollRoot.SetActive(false);
            ClearRows();
            if (microphoneImage != null) microphoneImage.color = micBaseColor;
            if (microphoneButton != null) microphoneButton.transform.localScale = micBaseScale;
            selectorButton = null;
            selectorScrollRoot = null;
            selectorScroll = null;
            itemPrefab = null;
            microphoneImage = null;
            microphoneButton = null;
            micBound = false;
            intensity = 0f;
            lastEncodedCount = -1;
            lastFrameAt = -100f;
            lastNativeRms = 0f;
            runtime = null;
            nativeCapture = null;
            warnedMissingUi = false;
        }
    }
}
#endif
