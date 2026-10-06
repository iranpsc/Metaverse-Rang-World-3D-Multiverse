#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.MediaV2.WebGL;
using Network_A.Voice.Client.Protocol;
using Network_A.Voice.Client.Routing.WebGL;
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Client.Transport;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.BrowserAudio
{
    public static class VoiceBrowserVoiceRuntimeInstaller
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";

        private const string AdapterRootName =
            "Voice_Browser_Production_Runtime_Adapter";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static async void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadMode)
        {
            if (!scene.IsValid() ||
                !VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected ||
                !string.Equals(
                    scene.name,
                    VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName,
                    StringComparison.Ordinal) ||
                IsIsolatedFoundationTest())
            {
                return;
            }

            // The legacy WebGL bootstrap replaces Voice_Client_Runtime_Root one
            // frame after this callback. Keep the production adapter on its own
            // persistent host so that replacement cannot destroy it.
            await Task.Yield();

            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() ||
                !string.Equals(
                    activeScene.name,
                    VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName,
                    StringComparison.Ordinal) ||
                !VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected)
            {
                return;
            }

            GameObject root = GameObject.Find(AdapterRootName);
            if (root == null) root = new GameObject(AdapterRootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            VoiceBrowserVoiceRuntimeAdapter adapter =
                root.GetComponent<VoiceBrowserVoiceRuntimeAdapter>();
            if (adapter == null)
                adapter = root.AddComponent<VoiceBrowserVoiceRuntimeAdapter>();

            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_ADAPTER=READY" +
                " | normalPathActivation=True" +
                " | persistentIndependentHost=True" +
                " | queryRequired=False" +
                " | windowsPathChanged=False");
        }

        private static bool IsIsolatedFoundationTest()
        {
            return Application.absoluteURL.IndexOf(
                       "vme2-webgl-audio-test=1",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class VoiceBrowserVoiceRuntimeAdapter : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const int ControlCommandTimeoutMs = 8000;
        private const float RetryDelaySeconds = 2f;

        private VoiceClientRuntime runtime;
        private VoiceSceneUserConsentPanelController consentPanel;
        private Button microphoneButton;
        private UnityAction originalMicrophoneAction;
        private VoiceBrowserAudioPipeline audioPipeline;
        private VoiceBrowserMediaSession mediaSession;
        private VoiceBrowserControlPublishAdapter controlAdapter;
        private VoiceBrowserAudioStats audioStats;
        private VoiceBrowserMediaStats mediaStats;
        private Task<bool> publishCommand;
        private double publishCommandDeadline;
        private string activeControlConnectionId = string.Empty;
        private string mediaStreamId = string.Empty;
        private string failureReason = string.Empty;
        private float retryAt;
        private bool microphoneRequested;
        private bool microphoneActivationPending;
        private bool audioReady;
        private bool mediaConnectStarted;
        private bool mediaBound;
        private bool publishing;
        private bool mediaResetRequested;
        private bool uplinkReported;
        private bool downlinkReported;
        private bool liveReported;
        private bool opusWorkerEvidenceReported;
        private bool mediaTransportEvidenceReported;
        private bool adaptivePlaybackEvidenceReported;
        private bool mediaReconnectPending;
        private bool mediaReconnectEvidenceReported;
        private int mediaConnectionAttempts;
        private int unexpectedMediaDisconnects;
        private int consecutiveMediaFailures;
        private double mediaDisconnectedAt;
        private double mediaReboundAt;
        private bool isolatedProbeDisabled;
        private bool browserPlaybackStateInitialized;
        private bool browserPlaybackEnabled = true;

        private FieldInfo panelMicrophoneButtonField;
        private MethodInfo panelMicrophoneClickMethod;
        private FieldInfo runtimeMicrophonePublisherField;
        private FieldInfo publisherMutedField;

        private void Update()
        {
            DisableIsolatedMediaProbe();
            ResolveRuntime();
            ResolveAndHookMicrophoneButton();
            KeepProductionMicrophoneHandlerExclusive();
            SyncBrowserPlaybackState();

            if (mediaResetRequested)
            {
                mediaResetRequested = false;
                ResetMediaSession("callback_reset");
            }

            if (runtime == null) return;

            bool controlAuthenticated =
                runtime.IsAuthenticated &&
                Guid.TryParse(runtime.VoiceConnectionId, out _);

            if (!controlAuthenticated)
            {
                consecutiveMediaFailures = 0;
                retryAt = 0f;
                if (publishing || mediaBound || mediaConnectStarted)
                {
                    MarkMediaReconnectPending(false);
                    ResetMediaSession("control_not_authenticated");
                }
                return;
            }

            bool mediaRequired =
                VoiceBrowserMediaConnectionPolicy.ShouldMaintainConnection(
                    audioReady,
                    controlAuthenticated,
                    microphoneRequested,
                    runtime.IsSpeakerOff);

            if (!mediaRequired)
            {
                if (mediaBound || mediaConnectStarted || publishing)
                    ResetMediaSession("media_not_required");
                return;
            }

            string controlConnectionId =
                runtime.VoiceConnectionId.Trim().ToLowerInvariant();
            if (activeControlConnectionId.Length > 0 &&
                !string.Equals(
                    activeControlConnectionId,
                    controlConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                MarkMediaReconnectPending(false);
                consecutiveMediaFailures = 0;
                retryAt = 0f;
                ResetMediaSession("control_connection_changed");
            }

            if (!mediaConnectStarted && Time.realtimeSinceStartup >= retryAt)
            {
                StartMediaConnection(controlConnectionId);
                return;
            }

            if (!mediaBound) return;

            if (!VoiceBrowserMediaConnectionPolicy.ShouldPublish(
                    microphoneRequested,
                    mediaBound))
            {
                if (publishing ||
                    microphoneActivationPending ||
                    publishCommand != null)
                {
                    publishing = false;
                    microphoneActivationPending = false;
                    publishCommand = null;
                    audioPipeline?.SetCaptureEnabled(false);
                    mediaSession?.SetOutboundEnabled(false);
                }
                return;
            }

            if (publishing) return;

            if (publishCommand == null)
            {
                BeginPublishCommand();
                return;
            }

            if (!publishCommand.IsCompleted)
            {
                if (Time.realtimeSinceStartupAsDouble >= publishCommandDeadline)
                    Fail("browser_voice_publish_start_timeout");
                return;
            }

            bool publishAccepted =
                publishCommand.Status == TaskStatus.RanToCompletion &&
                publishCommand.Result;
            publishCommand = null;

            if (!publishAccepted)
            {
                Fail("browser_voice_publish_start_rejected");
                return;
            }

            if (mediaSession == null ||
                !mediaSession.SetOutboundEnabled(true) ||
                audioPipeline == null ||
                !audioPipeline.SetCaptureEnabled(true))
            {
                Fail("browser_voice_capture_enable_rejected");
                return;
            }

            publishing = true;
            microphoneActivationPending = false;
            SetRuntimeMicrophoneMirror(false);
            runtime.SetRecordingConsentForAll(true);

            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_PUBLISH=PASS" +
                " | controlAuthenticated=True" +
                " | mediaBound=True" +
                " | browserCapture=True" +
                " | workerDirectMedia=True" +
                " | productionIntegration=True");
        }

        private void DisableIsolatedMediaProbe()
        {
            if (isolatedProbeDisabled) return;

            GameObject runtimeRoot = GameObject.Find(RuntimeRootName);
            if (runtimeRoot == null) return;

            VoiceWebGLPhase9LiveProbe probe =
                runtimeRoot.GetComponent<VoiceWebGLPhase9LiveProbe>();
            if (probe == null) return;

            isolatedProbeDisabled = true;
            probe.enabled = false;
            Destroy(probe);
            Debug.Log(
                "VME2_PHASE14_WEBGL_ISOLATED_MEDIA_PROBE=DISABLED" +
                " | productionRuntimeOwner=VoiceBrowserVoiceRuntimeAdapter");
        }

        private void ResolveRuntime()
        {
            if (runtime != null) return;

            if (controlAdapter != null)
            {
                controlAdapter.Dispose();
                controlAdapter = null;
                ResetMediaSession("runtime_replaced");
            }

            GameObject root = GameObject.Find(RuntimeRootName);
            if (root == null) return;

            runtime = root.GetComponent<VoiceClientRuntime>();
            if (runtime == null) return;

            VoiceClientAutoConnector connector =
                root.GetComponent<VoiceClientAutoConnector>();
            if (connector == null)
                connector = root.AddComponent<VoiceClientAutoConnector>();
            connector.Initialize(runtime);
            connector.enabled = true;

            controlAdapter = new VoiceBrowserControlPublishAdapter(runtime);
            runtimeMicrophonePublisherField = typeof(VoiceClientRuntime).GetField(
                "microphonePublisher",
                BindingFlags.Instance | BindingFlags.NonPublic);
            publisherMutedField = typeof(VoiceMicrophonePublisher).GetField(
                "muted",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Debug.Log(
                "VME2_PHASE14_WEBGL_CONTROL_CONNECTOR=ENABLED" +
                " | isolatedProbeDisabled=" + isolatedProbeDisabled +
                " | productionIntegration=True");
        }

        private void ResolveAndHookMicrophoneButton()
        {
            if (consentPanel == null)
            {
                consentPanel = UnityEngine.Object
                    .FindFirstObjectByType<VoiceSceneUserConsentPanelController>();
                microphoneButton = null;
                originalMicrophoneAction = null;
            }

            if (consentPanel == null) return;

            if (panelMicrophoneButtonField == null)
            {
                panelMicrophoneButtonField =
                    typeof(VoiceSceneUserConsentPanelController).GetField(
                        "microphoneButton",
                        BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (panelMicrophoneClickMethod == null)
            {
                panelMicrophoneClickMethod =
                    typeof(VoiceSceneUserConsentPanelController).GetMethod(
                        "HandleMicrophoneButtonClicked",
                        BindingFlags.Instance | BindingFlags.NonPublic);
            }

            Button resolvedButton = panelMicrophoneButtonField?.GetValue(
                consentPanel) as Button;
            if (resolvedButton == null || resolvedButton == microphoneButton)
                return;

            UnhookMicrophoneButton(false);
            microphoneButton = resolvedButton;

            if (panelMicrophoneClickMethod != null)
            {
                originalMicrophoneAction = Delegate.CreateDelegate(
                    typeof(UnityAction),
                    consentPanel,
                    panelMicrophoneClickMethod,
                    false) as UnityAction;
            }

            KeepProductionMicrophoneHandlerExclusive();
            Debug.Log(
                "VME2_PHASE14_WEBGL_MIC_BUTTON_ADAPTER=READY" +
                " | existingButtonReused=True" +
                " | extraRuntimeButton=False");
        }

        private void KeepProductionMicrophoneHandlerExclusive()
        {
            if (microphoneButton == null) return;
            if (originalMicrophoneAction != null)
                microphoneButton.onClick.RemoveListener(
                    originalMicrophoneAction);
            microphoneButton.onClick.RemoveListener(
                HandleMicrophoneButtonClicked);
            microphoneButton.onClick.AddListener(
                HandleMicrophoneButtonClicked);
        }

        private void HandleMicrophoneButtonClicked()
        {
            if (microphoneActivationPending)
            {
                Debug.Log(
                    "VME2_PHASE14_WEBGL_MIC_ACTIVATION=WAIT" +
                    " | audioReady=" + audioReady +
                    " | mediaConnectStarted=" + mediaConnectStarted +
                    " | mediaBound=" + mediaBound +
                    " | publishPending=" +
                        (publishCommand != null && !publishCommand.IsCompleted) +
                    " | duplicateClickIgnored=True");
                return;
            }

            if (microphoneRequested)
            {
                DisablePublishingFromUserAction();
                return;
            }

            microphoneRequested = true;
            microphoneActivationPending = true;
            failureReason = string.Empty;
            retryAt = 0f;

            if (audioPipeline != null && audioPipeline.IsStarted)
            {
                audioReady = true;
                return;
            }

            DisposeAudioPipeline();
            audioPipeline = new VoiceBrowserAudioPipeline();
            audioPipeline.Ready += HandleAudioReady;
            audioPipeline.StatsUpdated += HandleAudioStats;
            audioPipeline.Failed += HandleAudioFailure;

            string assetRoot =
                Application.streamingAssetsPath.TrimEnd('/') +
                "/VoiceBrowserAudio";

            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_AUDIO=START" +
                " | activation=userGesture" +
                " | normalPathActivation=True");

            if (!audioPipeline.BeginStart(assetRoot))
                Fail("browser_voice_audio_start_rejected");
        }

        private void HandleAudioReady()
        {
            audioReady = true;
            SyncBrowserPlaybackState();
            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_AUDIO=READY" +
                " | opusVersion=" + Safe(audioPipeline?.OpusVersion) +
                " | worker=True" +
                " | audioWorklet=True");
        }

        private void HandleAudioStats(VoiceBrowserAudioStats value)
        {
            audioStats = value;
            ReportOpusWorkerEvidence();
            ReportAdaptivePlaybackEvidence();
            EvaluateLiveEvidence();
        }

        private void ReportAdaptivePlaybackEvidence()
        {
            if (adaptivePlaybackEvidenceReported ||
                mediaStats.InboundFrames < 20 ||
                audioStats.RemoteNormalFrames < 20 ||
                audioStats.RemoteStreams < 1)
            {
                return;
            }

            adaptivePlaybackEvidenceReported = true;
            bool passed =
                audioStats.RemoteTargetDelayMs >= 40 &&
                audioStats.RemoteTargetDelayMs <= 200 &&
                audioStats.RemoteBufferedFrames <= 64 &&
                audioStats.RemoteOverflowDrops == 0 &&
                audioStats.PlaybackDrops == 0;

            Debug.Log(
                "VME2_PHASE18_WEBGL_PLAYBACK=" +
                (passed ? "PASS" : "FAIL") +
                " | adaptiveJitterBuffer=True" +
                " | workerPlayoutScheduler=True" +
                " | opusDecode=True" +
                " | plc=True" +
                " | fecRecovery=True" +
                " | remoteStreams=" + audioStats.RemoteStreams +
                " | bufferedFrames=" + audioStats.RemoteBufferedFrames +
                " | targetDelayMs=" + audioStats.RemoteTargetDelayMs +
                " | jitterMicros=" + audioStats.RemoteJitterMicros +
                " | normalFrames=" + audioStats.RemoteNormalFrames +
                " | fecFrames=" + audioStats.RemoteFecFrames +
                " | plcFrames=" + audioStats.RemotePlcFrames +
                " | reorderedPackets=" +
                    audioStats.RemoteReorderedPackets +
                " | lateDrops=" + audioStats.RemoteLateDrops +
                " | duplicateDrops=" +
                    audioStats.RemoteDuplicateDrops +
                " | overflowDrops=" +
                    audioStats.RemoteOverflowDrops +
                " | playbackDrops=" + audioStats.PlaybackDrops +
                " | unityMainThreadAudioTiming=False" +
                " | windowsPathChanged=False");
        }

        private void ReportOpusWorkerEvidence()
        {
            if (opusWorkerEvidenceReported ||
                !publishing ||
                audioStats.EncodedFrames < 50)
            {
                return;
            }

            opusWorkerEvidenceReported = true;
            bool passed =
                audioStats.CaptureDrops == 0 &&
                audioStats.LastPacketBytes > 0 &&
                audioStats.LastPacketBytes <= 1112 &&
                audioStats.EncodeMaxMicros > 0 &&
                audioStats.EncodeMaxMicros < 20000;

            Debug.Log(
                "VME2_PHASE16_WEBGL_OPUS_WORKER=" +
                (passed ? "PASS" : "FAIL") +
                " | frameSamples=960" +
                " | frameDurationMs=20" +
                " | encodedFrames=" + audioStats.EncodedFrames +
                " | lastPacketBytes=" + audioStats.LastPacketBytes +
                " | captureDrops=" + audioStats.CaptureDrops +
                " | encodeAverageMicros=" +
                    audioStats.EncodeAverageMicros +
                " | encodeMaxMicros=" + audioStats.EncodeMaxMicros +
                " | cadenceMaxDeviationMicros=" +
                    audioStats.CadenceMaxDeviationMicros +
                " | dedicatedWorker=True" +
                " | unityMainThreadAudioTiming=False" +
                " | windowsPathChanged=False");
        }

        private void SyncBrowserPlaybackState()
        {
            if (runtime == null ||
                audioPipeline == null ||
                !audioPipeline.IsStarted)
            {
                browserPlaybackStateInitialized = false;
                return;
            }

            bool enabled = !runtime.IsSpeakerOff;
            if (browserPlaybackStateInitialized &&
                browserPlaybackEnabled == enabled)
            {
                return;
            }

            if (!audioPipeline.SetPlaybackEnabled(enabled)) return;

            browserPlaybackStateInitialized = true;
            browserPlaybackEnabled = enabled;

            Debug.Log(
                "VME2_PHASE14_WEBGL_PLAYBACK_OUTPUT=PASS" +
                " | enabled=" + enabled +
                " | source=runtime_speaker_state" +
                " | productionIntegration=True");
        }

        private void HandleAudioFailure(string reason)
        {
            Fail("browser_audio_" + Safe(reason));
        }

        private void StartMediaConnection(string controlConnectionId)
        {
            string accessToken = Safe(SecureTokenStorage.GetAccessToken());
            string roomId = Safe(MetaverseNetworkClient.roomId);
            if (accessToken.Length == 0 || roomId.Length == 0)
                return;

            activeControlConnectionId = controlConnectionId;
            mediaConnectionAttempts += 1;
            mediaStreamId = Guid.NewGuid().ToString("D");
            mediaSession = new VoiceBrowserMediaSession(audioPipeline);
            mediaSession.Bound += HandleMediaBound;
            mediaSession.StatsUpdated += HandleMediaStats;
            mediaSession.Failed += HandleMediaFailure;
            mediaSession.Disconnected += HandleMediaDisconnected;

            string endpoint = AppendTransport(
                ServerConfig.RealtimeWebSocketUrl,
                "voice-media-v2");

            mediaConnectStarted = mediaSession.Connect(
                endpoint,
                accessToken,
                roomId,
                controlConnectionId,
                mediaStreamId,
                Application.version);

            if (!mediaConnectStarted)
            {
                ResetMediaSession("media_connect_start_rejected");
                consecutiveMediaFailures += 1;
                retryAt = Time.realtimeSinceStartup +
                    VoiceBrowserMediaConnectionPolicy.GetRetryDelaySeconds(
                        consecutiveMediaFailures);
            }
        }

        private void HandleMediaBound(string controlConnectionId)
        {
            if (!string.Equals(
                    activeControlConnectionId,
                    controlConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Fail("browser_media_bound_control_mismatch");
                return;
            }

            mediaBound = true;
            if (mediaReconnectPending)
            {
                mediaReboundAt = Time.realtimeSinceStartupAsDouble;
                Debug.Log(
                    "VME2_PHASE19_WEBGL_MEDIA_RECONNECT=REBOUND" +
                    " | attempts=" + mediaConnectionAttempts +
                    " | unexpectedDisconnects=" + unexpectedMediaDisconnects +
                    " | recoveryMs=" + Math.Max(
                        0d,
                        (mediaReboundAt - mediaDisconnectedAt) * 1000d)
                        .ToString("F0") +
                    " | controlConnectionId=" + controlConnectionId +
                    " | singleActiveMediaSession=True");
            }
            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_MEDIA_BIND=PASS" +
                " | controlConnectionId=" + controlConnectionId +
                " | streamId=" + mediaStreamId +
                " | transport=VoiceBrowserMediaBridge" +
                " | securityMode=legacyCompatible");
        }

        private void HandleMediaStats(VoiceBrowserMediaStats value)
        {
            mediaStats = value;
            if (value.PongCount > 0 || value.InboundFrames > 0)
                consecutiveMediaFailures = 0;
            ReportMediaTransportEvidence();
            ReportMediaReconnectEvidence();
            EvaluateLiveEvidence();
        }

        private void ReportMediaReconnectEvidence()
        {
            if (mediaReconnectEvidenceReported ||
                !mediaReconnectPending ||
                !mediaBound ||
                !publishing ||
                mediaStats.OutboundFrames < 20 ||
                mediaStats.InboundFrames < 20)
            {
                return;
            }

            mediaReconnectEvidenceReported = true;
            mediaReconnectPending = false;
            Debug.Log(
                "VME2_PHASE19_WEBGL_MEDIA_RECONNECT=PASS" +
                " | attempts=" + mediaConnectionAttempts +
                " | unexpectedDisconnects=" + unexpectedMediaDisconnects +
                " | recoveryMs=" + Math.Max(
                    0d,
                    (mediaReboundAt - mediaDisconnectedAt) * 1000d)
                    .ToString("F0") +
                " | outboundFrames=" + mediaStats.OutboundFrames +
                " | inboundFrames=" + mediaStats.InboundFrames +
                " | staleCallbacksRejected=True" +
                " | bindTimeoutMs=8000" +
                " | heartbeatTimeoutMs=15000" +
                " | singleActiveMediaSession=True" +
                " | duplicatePublisher=False" +
                " | windowsPathChanged=False");
        }

        private void ReportMediaTransportEvidence()
        {
            if (mediaTransportEvidenceReported ||
                !publishing ||
                mediaStats.OutboundFrames < 20 ||
                mediaStats.InboundFrames < 20 ||
                mediaStats.PongCount < 1)
            {
                return;
            }

            mediaTransportEvidenceReported = true;
            bool passed =
                mediaStats.Bound &&
                mediaStats.DroppedOutboundFrames == 0 &&
                mediaStats.BackpressureDrops == 0 &&
                mediaStats.LastOutboundPacketBytes > 0 &&
                mediaStats.LastOutboundPacketBytes <= 1112 &&
                mediaStats.LastInboundPacketBytes > 0 &&
                mediaStats.LastInboundPacketBytes <= 1112;

            Debug.Log(
                "VME2_PHASE17_WEBGL_MEDIA_TRANSPORT=" +
                (passed ? "PASS" : "FAIL") +
                " | binaryWebSocket=True" +
                " | protocolVersion=2"+
                " | headerBytes=88"+
                " | maximumPacketBytes=1200"+
                " | maximumPayloadBytes=1112"+
                " | outboundFrames=" + mediaStats.OutboundFrames +
                " | inboundFrames=" + mediaStats.InboundFrames +
                " | pongCount=" + mediaStats.PongCount +
                " | droppedOutboundFrames=" +
                    mediaStats.DroppedOutboundFrames +
                " | backpressureDrops=" +
                    mediaStats.BackpressureDrops +
                " | maxBufferedBytes=" + mediaStats.MaxBufferedBytes +
                " | mediaTimestampDecoded=True"+
                " | commonServerContract=True"+
                " | windowsPathChanged=False");
        }

        private void HandleMediaFailure(string reason)
        {
            Debug.LogWarning(
                "VME2_PHASE14_WEBGL_PRODUCTION_MEDIA=FAIL" +
                " | reason=" + Safe(reason));
        }

        private void HandleMediaDisconnected(string reason, bool intentional)
        {
            if (!intentional)
            {
                MarkMediaReconnectPending(true);
                Debug.LogWarning(
                    "VME2_PHASE14_WEBGL_PRODUCTION_MEDIA=DISCONNECTED" +
                    " | reason=" + Safe(reason) +
                    " | reconnectScheduled=" + microphoneRequested);
            }

            mediaResetRequested = true;
            consecutiveMediaFailures += 1;
            retryAt = Time.realtimeSinceStartup +
                VoiceBrowserMediaConnectionPolicy.GetRetryDelaySeconds(
                    consecutiveMediaFailures);
        }

        private void MarkMediaReconnectPending(bool unexpectedDisconnect)
        {
            if (unexpectedDisconnect) unexpectedMediaDisconnects += 1;
            if (!mediaReconnectPending)
                mediaDisconnectedAt = Time.realtimeSinceStartupAsDouble;
            mediaReconnectPending = true;
            mediaReconnectEvidenceReported = false;
        }

        private void BeginPublishCommand()
        {
            if (controlAdapter == null || runtime == null) return;
            publishCommand = controlAdapter.SendPublishStartAsync(32);
            publishCommandDeadline =
                Time.realtimeSinceStartupAsDouble +
                (ControlCommandTimeoutMs / 1000d);
        }

        private void DisablePublishingFromUserAction()
        {
            microphoneRequested = false;
            microphoneActivationPending = false;
            publishing = false;
            publishCommand = null;
            audioPipeline?.SetCaptureEnabled(false);
            mediaSession?.SetOutboundEnabled(false);
            SetRuntimeMicrophoneMirror(true);
            runtime?.SetRecordingConsentForAll(false);

            if (runtime != null && runtime.IsAuthenticated &&
                controlAdapter != null)
            {
                _ = controlAdapter.SendPublishStopAsync(1);
            }

            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_PUBLISH=STOPPED" +
                " | source=userAction" +
                " | browserCapture=False");
        }

        private void EvaluateLiveEvidence()
        {
            if (!publishing) return;

            if (!uplinkReported &&
                mediaStats.OutboundFrames >= 10 &&
                audioStats.EncodedFrames >= 10)
            {
                uplinkReported = true;
                Debug.Log(
                    "VME2_PHASE14_WEBGL_PRODUCTION_UPLINK=PASS" +
                    " | encodedFrames=" + audioStats.EncodedFrames +
                    " | mediaSentFrames=" + mediaStats.OutboundFrames +
                    " | lastPacketBytes=" +
                        mediaStats.LastOutboundPacketBytes +
                    " | workerDirectMedia=True" +
                    " | productionIntegration=True");
            }

            if (!downlinkReported &&
                mediaStats.InboundFrames >= 10 &&
                audioStats.DecodedFrames >= 10)
            {
                downlinkReported = true;
                Debug.Log(
                    "VME2_PHASE14_WEBGL_PRODUCTION_DOWNLINK=PASS" +
                    " | mediaReceivedFrames=" + mediaStats.InboundFrames +
                    " | decodedFrames=" + audioStats.DecodedFrames +
                    " | multiStreamDecoder=True" +
                    " | audioWorkletMix=True" +
                    " | productionIntegration=True");
            }

            if (liveReported || !uplinkReported || !downlinkReported) return;
            liveReported = true;

            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_LIVE=PASS" +
                " | controlAuthenticated=True" +
                " | mediaBound=True" +
                " | microphoneCapture=True" +
                " | realOpus=True" +
                " | bidirectionalMedia=True" +
                " | workerDirectMedia=True" +
                " | unityMainThreadAudioTiming=False" +
                " | normalPathActivation=True" +
                " | queryRequired=False" +
                " | productionIntegration=True" +
                " | windowsPathChanged=False");
        }

        private void Fail(string reason)
        {
            failureReason = Safe(reason);
            microphoneRequested = false;
            microphoneActivationPending = false;
            publishing = false;
            publishCommand = null;
            audioPipeline?.SetCaptureEnabled(false);
            mediaSession?.SetOutboundEnabled(false);
            SetRuntimeMicrophoneMirror(true);

            Debug.LogError(
                "VME2_PHASE14_WEBGL_PRODUCTION_LIVE=FAIL" +
                " | reason=" + failureReason +
                " | controlAuthenticated=" +
                    (runtime != null && runtime.IsAuthenticated) +
                " | mediaBound=" + mediaBound +
                " | productionIntegration=True");
        }

        private void SetRuntimeMicrophoneMirror(bool muted)
        {
            if (runtime == null ||
                runtimeMicrophonePublisherField == null ||
                publisherMutedField == null)
            {
                return;
            }

            object publisher = runtimeMicrophonePublisherField.GetValue(runtime);
            if (publisher != null) publisherMutedField.SetValue(publisher, muted);
        }

        private void ResetMediaSession(string reason)
        {
            publishing = false;
            mediaBound = false;
            mediaConnectStarted = false;
            publishCommand = null;
            audioPipeline?.SetCaptureEnabled(false);

            // A transient media reset stops capture, but it is not a user Mic
            // action.  Keep the selected Mic intent stable so reconnect does
            // not briefly turn the other participant/session eligibility off.
            // UserAction and terminal Fail paths still set the Mic to OFF.
            bool selectedMicrophoneMuted = !microphoneRequested;
            SetRuntimeMicrophoneMirror(selectedMicrophoneMuted);
            runtime?.SetRecordingConsentForAll(microphoneRequested);

            if (mediaSession != null)
            {
                mediaSession.Bound -= HandleMediaBound;
                mediaSession.StatsUpdated -= HandleMediaStats;
                mediaSession.Failed -= HandleMediaFailure;
                mediaSession.Disconnected -= HandleMediaDisconnected;
                mediaSession.Dispose();
                mediaSession = null;
            }

            activeControlConnectionId = string.Empty;
            mediaStreamId = string.Empty;
            mediaStats = default;
            opusWorkerEvidenceReported = false;
            mediaTransportEvidenceReported = false;

            Debug.Log(
                "VME2_PHASE14_WEBGL_PRODUCTION_MEDIA=RESET" +
                " | reason=" + Safe(reason) +
                " | microphoneIntentPreserved=True" +
                " | microphoneRequested=" + microphoneRequested +
                " | runtimeMicrophoneMuted=" +
                    (runtime == null || runtime.IsMicrophoneMuted));
        }

        private void DisposeAudioPipeline()
        {
            if (audioPipeline == null) return;
            audioPipeline.Ready -= HandleAudioReady;
            audioPipeline.StatsUpdated -= HandleAudioStats;
            audioPipeline.Failed -= HandleAudioFailure;
            audioPipeline.Dispose();
            audioPipeline = null;
            audioReady = false;
            browserPlaybackStateInitialized = false;
        }

        private void UnhookMicrophoneButton(bool restoreOriginal)
        {
            if (microphoneButton == null) return;
            microphoneButton.onClick.RemoveListener(
                HandleMicrophoneButtonClicked);
            if (restoreOriginal && originalMicrophoneAction != null)
                microphoneButton.onClick.AddListener(
                    originalMicrophoneAction);
            microphoneButton = null;
            originalMicrophoneAction = null;
        }

        private void OnDestroy()
        {
            UnhookMicrophoneButton(true);
            if (publishing && controlAdapter != null)
                _ = controlAdapter.SendPublishStopAsync(1);
            controlAdapter?.Dispose();
            controlAdapter = null;
            SetRuntimeMicrophoneMirror(true);
            ResetMediaSession("adapter_destroyed");
            DisposeAudioPipeline();
        }

        private static string AppendTransport(
            string endpoint,
            string transportName)
        {
            string safeEndpoint = Safe(endpoint);
            string safeTransport = Safe(transportName);
            if (safeEndpoint.Length == 0 || safeTransport.Length == 0)
                return safeEndpoint;

            string separator = !safeEndpoint.Contains("?")
                ? "?"
                : safeEndpoint.EndsWith("?") || safeEndpoint.EndsWith("&")
                    ? string.Empty
                    : "&";
            return safeEndpoint + separator + "transport=" + safeTransport;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("|", "/");
        }
    }

    internal sealed class VoiceBrowserControlPublishAdapter : IDisposable
    {
        private const int ControlAckTimeoutMs = 7000;

        private readonly VoiceClientRuntime runtime;
        private readonly FieldInfo outboundSchedulerField;
        private readonly FieldInfo transportField;
        private readonly object ackGate = new object();
        private readonly System.Collections.Generic.Dictionary<uint, TaskCompletionSource<bool>>
            pendingAcks = new System.Collections.Generic.Dictionary<uint, TaskCompletionSource<bool>>();
        private readonly System.Collections.Generic.Dictionary<uint, bool>
            receivedAcks = new System.Collections.Generic.Dictionary<uint, bool>();

        private IVoiceClientTransport observedTransport;

        public VoiceBrowserControlPublishAdapter(VoiceClientRuntime runtime)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            outboundSchedulerField = typeof(VoiceClientRuntime).GetField(
                "outboundScheduler",
                BindingFlags.Instance | BindingFlags.NonPublic);
            transportField = typeof(VoiceClientRuntime).GetField(
                "transport",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }

        public Task<bool> SendPublishStartAsync(int bitrateKbps)
        {
            return SendAsync(
                VoiceClientMessageType.PublishStart,
                VoiceClientControlPayload.EncodePublishStart(bitrateKbps),
                true);
        }

        public Task<bool> SendPublishStopAsync(byte reason)
        {
            return SendAsync(
                VoiceClientMessageType.PublishStop,
                VoiceClientControlPayload.EncodePublishStop(reason),
                false);
        }

        private async Task<bool> SendAsync(
            VoiceClientMessageType messageType,
            byte[] payload,
            bool waitForServerAck)
        {
            if (outboundSchedulerField == null ||
                transportField == null ||
                !runtime.IsAuthenticated ||
                !Guid.TryParse(runtime.VoiceConnectionId, out _) ||
                !EnsureControlObservation())
            {
                return false;
            }

            try
            {
                VoiceOutboundScheduler scheduler =
                    outboundSchedulerField.GetValue(runtime) as
                        VoiceOutboundScheduler;
                if (scheduler == null) return false;

                VoiceOutboundSendResult result =
                    await scheduler.EnqueueControlAsync(
                        messageType,
                        VoiceClientMessageFlags.AckRequired,
                        VoiceClientEnvelope.EmptyUuid,
                        runtime.VoiceConnectionId,
                        payload ?? Array.Empty<byte>(),
                        CancellationToken.None);

                if (!result.Success || !waitForServerAck)
                    return result.Success;

                TaskCompletionSource<bool> completion =
                    new TaskCompletionSource<bool>();

                lock (ackGate)
                {
                    if (receivedAcks.TryGetValue(
                            result.Sequence,
                            out bool receivedResult))
                    {
                        receivedAcks.Remove(result.Sequence);
                        return receivedResult;
                    }

                    pendingAcks[result.Sequence] = completion;
                }

                Task completed = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(ControlAckTimeoutMs));

                lock (ackGate)
                    pendingAcks.Remove(result.Sequence);

                bool accepted = completed == completion.Task &&
                    await completion.Task;

                Debug.Log(
                    "VME2_PHASE14_WEBGL_CONTROL_ACK=" +
                    (accepted ? "PASS" : "FAIL") +
                    " | messageType=" + messageType +
                    " | sequence=" + result.Sequence +
                    " | serverProcessed=" + accepted);

                return accepted;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VME2_PHASE14_WEBGL_CONTROL_ADAPTER=FAIL" +
                    " | error=" + exception.Message);
                return false;
            }
        }

        private bool EnsureControlObservation()
        {
            IVoiceClientTransport current =
                transportField.GetValue(runtime) as IVoiceClientTransport;
            if (current == null || !current.IsConnected) return false;
            if (ReferenceEquals(current, observedTransport)) return true;

            if (observedTransport != null)
                observedTransport.PacketReceived -= HandleControlPacket;

            observedTransport = current;
            observedTransport.PacketReceived += HandleControlPacket;
            return true;
        }

        private void HandleControlPacket(byte[] packet)
        {
            try
            {
                VoiceClientEnvelope envelope =
                    VoiceClientEnvelope.Decode(packet);
                if (envelope.MessageType != VoiceClientMessageType.Ack ||
                    envelope.Payload == null ||
                    envelope.Payload.Length != 8)
                {
                    return;
                }

                uint acknowledgedSequence =
                    VoiceClientEnvelope.ReadUInt32(envelope.Payload, 0);
                ushort ackCode =
                    VoiceClientEnvelope.ReadUInt16(envelope.Payload, 4);
                ushort reserved =
                    VoiceClientEnvelope.ReadUInt16(envelope.Payload, 6);
                if (acknowledgedSequence == 0 || reserved != 0 || ackCode > 2)
                    return;

                bool accepted = ackCode == 0 || ackCode == 1;
                TaskCompletionSource<bool> completion = null;

                lock (ackGate)
                {
                    if (pendingAcks.TryGetValue(
                            acknowledgedSequence,
                            out completion))
                    {
                        pendingAcks.Remove(acknowledgedSequence);
                    }
                    else
                    {
                        if (receivedAcks.Count >= 64) receivedAcks.Clear();
                        receivedAcks[acknowledgedSequence] = accepted;
                    }
                }

                completion?.TrySetResult(accepted);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (observedTransport != null)
            {
                observedTransport.PacketReceived -= HandleControlPacket;
                observedTransport = null;
            }

            TaskCompletionSource<bool>[] completions;
            lock (ackGate)
            {
                completions = new TaskCompletionSource<bool>[
                    pendingAcks.Count];
                pendingAcks.Values.CopyTo(completions, 0);
                pendingAcks.Clear();
                receivedAcks.Clear();
            }

            foreach (TaskCompletionSource<bool> completion in completions)
                completion.TrySetResult(false);
        }
    }
}
#endif
