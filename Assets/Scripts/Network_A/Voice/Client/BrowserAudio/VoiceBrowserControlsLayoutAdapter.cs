#if UNITY_WEBGL && !UNITY_EDITOR
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.BrowserAudio
{
    public static class VoiceBrowserControlsLayoutInstaller
    {
        private const string LayoutRootName =
            "Voice_Browser_Controls_Layout";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadMode)
        {
            if (!scene.IsValid())
                return;

            VoiceSceneUserConsentPanelController[] panels =
                Object.FindObjectsOfType<
                    VoiceSceneUserConsentPanelController>(true);

            for (int index = 0; index < panels.Length; index++)
            {
                VoiceSceneUserConsentPanelController panel = panels[index];
                if (panel == null || panel.gameObject.scene != scene)
                    continue;

                Install(panel);
            }
        }

        private static void Install(
            VoiceSceneUserConsentPanelController panel)
        {
            Canvas canvas = panel.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning(
                    "VOICE_BROWSER_CONTROLS_LAYOUT=FAIL" +
                    " | reason=canvas_missing");
                return;
            }

            Transform existing = canvas.transform.Find(LayoutRootName);
            if (existing != null &&
                existing.GetComponent<VoiceBrowserControlsLayoutAdapter>() !=
                null)
            {
                return;
            }

            Button microphoneButton = null;
            Button speakerButton = null;
            Button[] buttons = panel.GetComponentsInChildren<Button>(true);

            for (int index = 0; index < buttons.Length; index++)
            {
                Button button = buttons[index];
                if (button == null)
                    continue;

                if (button.name == "Btn_Voice_Microphone")
                    microphoneButton = button;
                else if (button.name == "Btn_Voice_Mute_All")
                    speakerButton = button;
            }

            if (microphoneButton == null || speakerButton == null)
            {
                Debug.LogWarning(
                    "VOICE_BROWSER_CONTROLS_LAYOUT=FAIL" +
                    " | reason=voice_buttons_missing" +
                    " | microphone=" + (microphoneButton != null) +
                    " | speaker=" + (speakerButton != null));
                return;
            }

            GameObject layoutObject = new GameObject(
                LayoutRootName,
                typeof(RectTransform));

            layoutObject.layer = canvas.gameObject.layer;

            RectTransform layoutRoot =
                layoutObject.GetComponent<RectTransform>();

            layoutRoot.SetParent(canvas.transform, false);
            layoutRoot.SetAsLastSibling();

            VoiceBrowserControlsLayoutAdapter adapter =
                layoutObject.AddComponent<
                    VoiceBrowserControlsLayoutAdapter>();

            adapter.Configure(
                layoutRoot,
                microphoneButton,
                speakerButton);
        }
    }

    [DisallowMultipleComponent]
    public sealed class VoiceBrowserControlsLayoutAdapter : MonoBehaviour
    {
        private static readonly Vector2 DefaultButtonSize =
            new Vector2(100f, 100f);

        private RectTransform layoutRoot;
        private RectTransform microphoneRect;
        private RectTransform speakerRect;
        private int appliedScreenWidth = -1;
        private int appliedScreenHeight = -1;
        private Rect appliedSafeArea;
        private bool configured;

        public void Configure(
            RectTransform root,
            Button microphoneButton,
            Button speakerButton)
        {
            if (configured ||
                root == null ||
                microphoneButton == null ||
                speakerButton == null)
            {
                return;
            }

            layoutRoot = root;
            microphoneRect =
                microphoneButton.transform as RectTransform;
            speakerRect =
                speakerButton.transform as RectTransform;

            if (microphoneRect == null || speakerRect == null)
            {
                Debug.LogWarning(
                    "VOICE_BROWSER_CONTROLS_LAYOUT=FAIL" +
                    " | reason=button_rect_missing");
                return;
            }

            microphoneRect.SetParent(layoutRoot, false);
            speakerRect.SetParent(layoutRoot, false);

            microphoneRect.localScale = Vector3.one;
            speakerRect.localScale = Vector3.one;
            microphoneRect.localRotation = Quaternion.identity;
            speakerRect.localRotation = Quaternion.identity;

            EnsureButtonSize(microphoneRect);
            EnsureButtonSize(speakerRect);

            configured = true;
            ApplyLayout(true);
        }

        private void Update()
        {
            if (!configured)
                return;

            Rect safeArea = GetUsableSafeArea();
            if (appliedScreenWidth == Screen.width &&
                appliedScreenHeight == Screen.height &&
                appliedSafeArea == safeArea)
            {
                return;
            }

            ApplyLayout(false);
        }

        private void ApplyLayout(bool initialApply)
        {
            if (layoutRoot == null ||
                microphoneRect == null ||
                speakerRect == null)
            {
                return;
            }

            Rect safeArea = GetUsableSafeArea();
            float width = Mathf.Max(1f, Screen.width);
            float height = Mathf.Max(1f, Screen.height);

            layoutRoot.anchorMin = new Vector2(
                safeArea.xMin / width,
                safeArea.yMin / height);

            layoutRoot.anchorMax = new Vector2(
                safeArea.xMax / width,
                safeArea.yMax / height);

            layoutRoot.offsetMin = Vector2.zero;
            layoutRoot.offsetMax = Vector2.zero;
            layoutRoot.localScale = Vector3.one;
            layoutRoot.SetAsLastSibling();

            bool landscape = Screen.width > Screen.height;
            if (landscape)
            {
                SetButtonLayout(
                    microphoneRect,
                    new Vector2(0.75f, 0f),
                    new Vector2(-60f, 70f));

                SetButtonLayout(
                    speakerRect,
                    new Vector2(0.75f, 0f),
                    new Vector2(60f, 70f));
            }
            else
            {
                SetButtonLayout(
                    microphoneRect,
                    new Vector2(1f, 0f),
                    new Vector2(-190f, 70f));

                SetButtonLayout(
                    speakerRect,
                    new Vector2(1f, 0f),
                    new Vector2(-70f, 70f));
            }

            appliedScreenWidth = Screen.width;
            appliedScreenHeight = Screen.height;
            appliedSafeArea = safeArea;

            Canvas.ForceUpdateCanvases();

            Debug.Log(
                "VOICE_BROWSER_CONTROLS_LAYOUT=READY" +
                " | orientation=" +
                (landscape ? "landscape" : "portrait") +
                " | initialApply=" + initialApply +
                " | screen=" + Screen.width + "x" + Screen.height +
                " | safeArea=" + safeArea +
                " | microphoneVisible=" +
                microphoneRect.gameObject.activeInHierarchy +
                " | speakerVisible=" +
                speakerRect.gameObject.activeInHierarchy);
        }

        private static Rect GetUsableSafeArea()
        {
            Rect safeArea = Screen.safeArea;
            if (safeArea.width <= 0f || safeArea.height <= 0f)
            {
                return new Rect(
                    0f,
                    0f,
                    Mathf.Max(1f, Screen.width),
                    Mathf.Max(1f, Screen.height));
            }

            return safeArea;
        }

        private static void EnsureButtonSize(RectTransform buttonRect)
        {
            Vector2 size = buttonRect.sizeDelta;
            if (size.x <= 0f || size.y <= 0f)
                buttonRect.sizeDelta = DefaultButtonSize;
        }

        private static void SetButtonLayout(
            RectTransform buttonRect,
            Vector2 anchor,
            Vector2 anchoredPosition)
        {
            buttonRect.anchorMin = anchor;
            buttonRect.anchorMax = anchor;
            buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.anchoredPosition = anchoredPosition;
        }
    }
}
#endif
