using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Network_A.Diagnostics
{
    // Attach only to the existing scene UI button (for example, Btn_Internet_State).
    // No Update, OnGUI, automatic probing, or changes to existing game/voice services.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public sealed class NetworkStateDiagnosticButton : MonoBehaviour
    {
        [Header("Independent on-demand HTTP server health probe")]
        [SerializeField] private string healthUrl = "https://dev-world-3d.metarang.com/health";
        [SerializeField, Range(2f, 15f)] private float intervalSeconds = 3f;
        [SerializeField, Range(2, 10)] private int timeoutSeconds = 4;

        private const int HistoryLength = 20;
        private const string NotAvailable = "N/A";

        private struct ProbeSample
        {
            internal bool Healthy;
            internal float ElapsedMs;

            internal ProbeSample(bool healthy, float elapsedMs)
            {
                Healthy = healthy;
                ElapsedMs = elapsedMs;
            }
        }

        private readonly Queue<ProbeSample> history = new Queue<ProbeSample>();
        private Button targetButton;
        private GameObject popupRoot;
        private Coroutine monitorRoutine;
        private UnityWebRequest activeRequest;
        private bool running;
        private bool lastTimedOut;
        private long lastHttpCode;
        private int totalSuccess;
        private int totalFailures;
        private int totalTimeouts;
        private float lastResponseMs;
        private string lastError = "None";

        private TextMeshProUGUI statusLabel;
        private TextMeshProUGUI responseLabel;
        private TextMeshProUGUI timingLabel;
        private TextMeshProUGUI variationLabel;
        private TextMeshProUGUI failureLabel;
        private TextMeshProUGUI countsLabel;
        private TextMeshProUGUI interfaceLabel;
        private TextMeshProUGUI detailLabel;

        private void Awake()
        {
            targetButton = GetComponent<Button>();
            if (targetButton.targetGraphic == null)
                targetButton.targetGraphic = GetComponent<Graphic>();
            targetButton.onClick.AddListener(TogglePopup);
        }

        // The button component is inert until this callback runs; no timer or probe exists at rest.
        public void TogglePopup()
        {
            if (popupRoot != null)
            {
                Close();
                return;
            }

            popupRoot = new GameObject("NetworkDiagnostics_Popup_Runtime",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            Canvas canvas = popupRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 1000;

            CanvasScaler scaler = popupRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            history.Clear();
            totalSuccess = 0;
            totalFailures = 0;
            totalTimeouts = 0;
            lastHttpCode = 0;
            lastTimedOut = false;
            lastResponseMs = 0f;
            lastError = "None";

            BuildUi();
            running = true;
            Render("CHECKING SERVER...", new Color(1f, 0.80f, 0.42f));
            monitorRoutine = StartCoroutine(Monitor(healthUrl, Mathf.Max(2f, intervalSeconds),
                Mathf.Clamp(timeoutSeconds, 2, 10)));
        }

        private IEnumerator Monitor(string url, float interval, int timeout)
        {
            while (running)
            {
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) ||
                    (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    lastError = "Invalid health URL";
                    Render("INVALID PROBE URL", new Color(1f, 0.48f, 0.48f));
                    yield break;
                }

                string probeUrl = url + (url.Contains("?") ? "&" : "?") +
                                  "netdiag=" + DateTime.UtcNow.Ticks;

                UnityWebRequest request = UnityWebRequest.Get(probeUrl);
                activeRequest = request;
                request.timeout = timeout;
                float started = Time.realtimeSinceStartup;
                bool hardTimeout = false;
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                // The request yields to Unity; there is no synchronous wait or busy loop.
                while (running && !operation.isDone)
                {
                    if (Time.realtimeSinceStartup - started >= timeout)
                    {
                        hardTimeout = true;
                        request.Abort();
                        break;
                    }
                    yield return null;
                }

                if (!running)
                {
                    if (activeRequest == request)
                    {
                        activeRequest = null;
                        request.Abort();
                        request.Dispose();
                    }
                    yield break;
                }

                float elapsedMs = Mathf.Max(0f, (Time.realtimeSinceStartup - started) * 1000f);
                long code = request.responseCode;
                bool healthy = !hardTimeout && request.result == UnityWebRequest.Result.Success && code == 200;
                bool timedOut = hardTimeout || (code == 0 &&
                    !string.IsNullOrEmpty(request.error) &&
                    request.error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0);

                lastHttpCode = code;
                lastTimedOut = timedOut;
                lastResponseMs = elapsedMs;
                if (healthy) totalSuccess++;
                else
                {
                    totalFailures++;
                    if (timedOut) totalTimeouts++;
                }

                lastError = healthy ? "None" : timedOut ? "Request timeout" :
                    code > 0 ? "HTTP response " + code :
                    string.IsNullOrWhiteSpace(request.error) ? "No HTTP response" : request.error;

                history.Enqueue(new ProbeSample(healthy, elapsedMs));
                while (history.Count > HistoryLength) history.Dequeue();

                activeRequest = null;
                request.Dispose();

                if (healthy)
                    Render("SERVER HEALTH: OK", new Color(0.44f, 0.91f, 0.63f));
                else if (code > 0)
                    Render("SERVER RESPONDED: HTTP " + code, new Color(1f, 0.77f, 0.41f));
                else
                    Render(timedOut ? "SERVER CHECK: TIMEOUT" : "SERVER PATH: UNREACHABLE",
                        new Color(1f, 0.51f, 0.48f));

                if (running) yield return new WaitForSecondsRealtime(interval);
            }
        }

        private void Render(string headline, Color statusColor)
        {
            if (statusLabel == null) return;

            int unhealthy = 0;
            float sumVariation = 0f;
            int variationPairs = 0;
            float previousSuccessMs = -1f;
            foreach (ProbeSample item in history)
            {
                if (!item.Healthy)
                {
                    unhealthy++;
                    previousSuccessMs = -1f;
                    continue;
                }
                if (previousSuccessMs >= 0f)
                {
                    sumVariation += Mathf.Abs(item.ElapsedMs - previousSuccessMs);
                    variationPairs++;
                }
                previousSuccessMs = item.ElapsedMs;
            }

            statusLabel.text = headline;
            statusLabel.color = statusColor;
            responseLabel.text = "HTTP Status: " + (history.Count == 0 ? "--" :
                lastHttpCode == 0 ? "No response" : lastHttpCode.ToString());
            timingLabel.text = "HTTP response time: " +
                (history.Count > 0 && totalSuccess + totalFailures > 0 &&
                 lastHttpCode == 200 && !lastTimedOut ?
                    lastResponseMs.ToString("0") + " ms" : NotAvailable);
            variationLabel.text = "HTTP time variation: " +
                (variationPairs > 0 ? (sumVariation / variationPairs).ToString("0") + " ms" : NotAvailable);
            failureLabel.text = "HTTP check failure rate: " +
                (history.Count > 0 ? (100f * unhealthy / history.Count).ToString("0.0") + "%" : NotAvailable) +
                " (last " + history.Count + "/" + HistoryLength + ")";
            countsLabel.text = "Checks: " + totalSuccess + " OK  /  " + totalFailures +
                " failed  /  " + totalTimeouts + " timeout";
            interfaceLabel.text = "Local network hint: " + Application.internetReachability;
            detailLabel.text = "Last result: " + Truncate(lastError, 110) +
                "\nHTTP checks are not IP packet loss or game/voice RTT. " +
                "A failed check does not prove that the Internet is disconnected.";
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "None";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";
        }

        private void BuildUi()
        {
            RectTransform canvasRect = popupRoot.GetComponent<RectTransform>();
            canvasRect.anchorMin = Vector2.zero;
            canvasRect.anchorMax = Vector2.one;
            canvasRect.offsetMin = Vector2.zero;
            canvasRect.offsetMax = Vector2.zero;

            RectTransform backdrop = MakeRect("Overlay", popupRoot.transform, Vector2.zero, Vector2.one);
            Image backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.color = new Color(0.025f, 0.035f, 0.055f, 0.80f);

            Canvas.ForceUpdateCanvases();
            Canvas canvas = popupRoot.GetComponent<Canvas>();
            float canvasScale = Mathf.Max(0.1f, canvas.scaleFactor);
            float availableWidth = Screen.width / canvasScale;
            float availableHeight = Screen.height / canvasScale;

            RectTransform panel = MakeRect("DiagnosticsPanel", popupRoot.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            panel.sizeDelta = new Vector2(
                Mathf.Min(800f, Mathf.Max(280f, availableWidth - 40f)),
                Mathf.Min(680f, Mathf.Max(300f, availableHeight - 30f)));
            Image background = panel.gameObject.AddComponent<Image>();
            background.color = new Color(0.055f, 0.10f, 0.16f, 0.99f);

            AddText(panel, "Title", "NETWORK DIAGNOSTICS", 0.88f, 0.97f,
                33, FontStyles.Bold, new Color(0.91f, 0.96f, 1f));
            AddText(panel, "Subtitle", "On-demand | Direct server health HTTP GET", 0.80f, 0.88f,
                19, FontStyles.Normal, new Color(0.64f, 0.76f, 0.89f));
            statusLabel = AddText(panel, "Status", "CHECKING SERVER...", 0.69f, 0.80f,
                27, FontStyles.Bold, Color.white);
            responseLabel = AddText(panel, "HttpStatus", "HTTP Status: --", 0.61f, 0.69f,
                23, FontStyles.Normal, Color.white);
            timingLabel = AddText(panel, "HttpTiming", "HTTP response time: N/A", 0.53f, 0.61f,
                23, FontStyles.Normal, Color.white);
            variationLabel = AddText(panel, "Variation", "HTTP time variation: N/A", 0.45f, 0.53f,
                23, FontStyles.Normal, Color.white);
            failureLabel = AddText(panel, "FailureRate", "HTTP check failure rate: N/A", 0.37f, 0.45f,
                23, FontStyles.Normal, Color.white);
            countsLabel = AddText(panel, "Counts", "Checks: 0 OK / 0 failed / 0 timeout", 0.29f, 0.37f,
                20, FontStyles.Normal, Color.white);
            interfaceLabel = AddText(panel, "InterfaceHint", "Local network hint: N/A", 0.22f, 0.29f,
                19, FontStyles.Normal, new Color(0.72f, 0.83f, 0.95f));
            detailLabel = AddText(panel, "Disclaimer", "Waiting for first sample...", 0.105f, 0.22f,
                16, FontStyles.Normal, new Color(0.69f, 0.79f, 0.90f));
            detailLabel.enableWordWrapping = true;

            RectTransform closeRect = MakeRect("CloseButton", panel,
                new Vector2(0.32f, 0.015f), new Vector2(0.68f, 0.10f));
            Image closeBackground = closeRect.gameObject.AddComponent<Image>();
            closeBackground.color = new Color(0.19f, 0.36f, 0.62f, 1f);
            Button closeButton = closeRect.gameObject.AddComponent<Button>();
            closeButton.targetGraphic = closeBackground;
            closeButton.onClick.AddListener(Close);
            TextMeshProUGUI closeText = AddText(closeRect, "CloseLabel", "CLOSE", 0f, 1f,
                22, FontStyles.Bold, Color.white);
            closeText.alignment = TextAlignmentOptions.Center;
            closeText.raycastTarget = false;
        }

        private static RectTransform MakeRect(string name, Transform parent,
            Vector2 anchorsMin, Vector2 anchorsMax)
        {
            GameObject node = new GameObject(name, typeof(RectTransform));
            node.transform.SetParent(parent, false);
            RectTransform rect = node.GetComponent<RectTransform>();
            rect.anchorMin = anchorsMin;
            rect.anchorMax = anchorsMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        private static TextMeshProUGUI AddText(Transform parent, string name, string value,
            float minY, float maxY, float fontSize, FontStyles style, Color color)
        {
            RectTransform rect = MakeRect(name, parent,
                new Vector2(0.055f, minY), new Vector2(0.945f, maxY));
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.enableAutoSizing = true;
            text.fontSizeMin = Mathf.Max(11f, fontSize * 0.64f);
            text.fontSizeMax = fontSize;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        // Close cancels and disposes everything created by this diagnostics button.
        private void Close()
        {
            running = false;
            if (activeRequest != null)
            {
                activeRequest.Abort();
                activeRequest.Dispose();
                activeRequest = null;
            }
            if (monitorRoutine != null)
            {
                StopCoroutine(monitorRoutine);
                monitorRoutine = null;
            }
            if (popupRoot != null)
            {
                Destroy(popupRoot);
                popupRoot = null;
            }
            statusLabel = null;
        }

        private void OnDestroy()
        {
            if (targetButton != null)
                targetButton.onClick.RemoveListener(TogglePopup);
            Close();
        }
    }
}
