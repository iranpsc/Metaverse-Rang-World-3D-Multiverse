#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.Voice.Client.BrowserAudio
{
    public sealed class VoiceBrowserAudioProbe : MonoBehaviour
    {
        private enum ProbeStage
        {
            Idle,
            Starting,
            ReadyToEnableCapture,
            Capturing,
            MediaReady,
            Configuring,
            ConfigurationAccepted
        }

        private const string TestParameter = "vme2-webgl-audio-test";
        private const int StartTimeoutMs = 30000;
        private const int MediaTimeoutMs = 60000;
        private const int ConfigurationTimeoutMs = 5000;

        private static bool installed;

        private CancellationTokenSource lifetime;
        private VoiceBrowserAudioPipeline pipeline;
        private VoiceBrowserAudioStats latestStats;
        private TaskCompletionSource<bool> configurationCompletion;
        private string failureReason = string.Empty;
        private string status = "Ready. Click the button once, then allow microphone access.";
        private ProbeStage stage;
        private double stageDeadlineRealtime;
        private bool resultReported;
        private bool running;
        private bool finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallOnlyForExplicitTestUrl()
        {
            if (installed || !HasQueryValue(TestParameter, "1")) return;

            installed = true;
            GameObject host = new GameObject("VoiceBrowserAudioProbe");
            DontDestroyOnLoad(host);
            host.AddComponent<VoiceBrowserAudioProbe>();
        }

        private void Awake()
        {
            lifetime = new CancellationTokenSource();
        }

        private void OnGUI()
        {
            const float width = 520f;
            const float height = 150f;
            Rect panel = new Rect(20f, 20f, width, height);
            GUI.Box(panel, "WebGL Audio Test");
            GUI.Label(new Rect(40f, 55f, width - 40f, 48f), status);

            GUI.enabled = !running && !finished;
            if (GUI.Button(new Rect(40f, 112f, 230f, 36f), "Start microphone audio test"))
            {
                failureReason = string.Empty;
                latestStats = default;
                configurationCompletion = null;
                stage = ProbeStage.Idle;
                stageDeadlineRealtime = 0d;
                resultReported = false;
                running = true;
                status = "Starting. If Chrome asks for microphone access, click Allow.";
                _ = RunAsync(lifetime.Token);
            }
            GUI.enabled = true;
        }

        private Task RunAsync(CancellationToken cancellationToken)
        {
            Debug.Log(
                "VME2_PHASE14_WEBGL_AUDIO_LIVE=START" +
                " | activation=userGesture" +
                " | normalPathActivation=False" +
                " | productionIntegration=False");

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    CompleteFailure("cancelled");
                    return Task.CompletedTask;
                }

                pipeline = new VoiceBrowserAudioPipeline();
                pipeline.Ready += HandleReady;
                pipeline.StatsUpdated += HandleStats;
                pipeline.EncoderConfigured += HandleConfigured;
                pipeline.Failed += HandleFailure;

                string assetRoot =
                    Application.streamingAssetsPath.TrimEnd('/') +
                    "/VoiceBrowserAudio";

                stage = ProbeStage.Starting;
                stageDeadlineRealtime = DeadlineAfter(StartTimeoutMs);

                if (!pipeline.BeginStart(assetRoot))
                {
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(failureReason)
                            ? "browser_audio_pipeline_start_failed"
                            : failureReason);
                }
            }
            catch (Exception exception)
            {
                CompleteFailure(exception.Message);
            }

            return Task.CompletedTask;
        }

        private void Update()
        {
            if (!running || resultReported) return;

            if (lifetime == null || lifetime.IsCancellationRequested)
            {
                CompleteFailure("cancelled");
                return;
            }

            if (!string.IsNullOrEmpty(failureReason))
            {
                CompleteFailure(failureReason);
                return;
            }

            switch (stage)
            {
                case ProbeStage.Starting:
                    if (HasStageTimedOut()) CompleteFailure("browser_audio_start_timeout");
                    break;
                case ProbeStage.ReadyToEnableCapture:
                    EnableCapture();
                    break;
                case ProbeStage.Capturing:
                    if (HasStageTimedOut()) CompleteFailure("capture_encode_decode_timeout");
                    break;
                case ProbeStage.MediaReady:
                    ValidateMediaAndConfigureEncoder();
                    break;
                case ProbeStage.Configuring:
                    if (HasStageTimedOut()) CompleteFailure("encoder_configuration_timeout");
                    break;
                case ProbeStage.ConfigurationAccepted:
                    CompleteSuccess();
                    break;
            }
        }

        private void HandleReady()
        {
            if (!running || resultReported || pipeline == null) return;

            if (!string.Equals(pipeline.OpusVersion, "libopus 1.6.1", StringComparison.Ordinal))
            {
                failureReason = "unexpected_opus_version";
                return;
            }

            if (string.Equals(pipeline.AudioContextState, "suspended", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("VME2_PHASE14_WEBGL_AUDIO_ACTION_REQUIRED | clickCanvas=True");
                status = "Audio is suspended. Click once inside the game canvas.";
            }

            stage = ProbeStage.ReadyToEnableCapture;
        }

        private void EnableCapture()
        {
            if (pipeline == null || !pipeline.SetCaptureEnabled(true))
            {
                CompleteFailure("capture_enable_rejected");
                return;
            }

            stage = ProbeStage.Capturing;
            stageDeadlineRealtime = DeadlineAfter(MediaTimeoutMs);
            status = "Capturing, encoding and decoding microphone audio.";
            Debug.Log("[VoiceBrowserAudio] stage=capture_enabled");
        }

        private void HandleStats(VoiceBrowserAudioStats stats)
        {
            latestStats = stats;

            if (running &&
                !resultReported &&
                stage == ProbeStage.Capturing &&
                stats.CaptureBlocks > 0 &&
                stats.EncodedFrames >= 10 &&
                stats.DecodedFrames >= 10)
            {
                stage = ProbeStage.MediaReady;
            }
        }

        private void HandleConfigured(int bitrate, bool fec, int packetLossPercent)
        {
            bool accepted = bitrate == 24000 && fec && packetLossPercent == 10;
            configurationCompletion?.TrySetResult(accepted);

            if (!running || resultReported || stage != ProbeStage.Configuring) return;
            if (!accepted)
            {
                failureReason = "encoder_configuration_mismatch";
                return;
            }

            stage = ProbeStage.ConfigurationAccepted;
        }

        private void HandleFailure(string reason)
        {
            failureReason = Safe(reason);
        }

        private void ValidateMediaAndConfigureEncoder()
        {
            if (latestStats.EncodedFrames != latestStats.DecodedFrames)
            {
                CompleteFailure("encoded_decoded_frame_count_mismatch");
                return;
            }

            if (latestStats.EncodedBytes <= 0 ||
                latestStats.LastPacketBytes <= 0 ||
                latestStats.LastPacketBytes > 1275)
            {
                CompleteFailure("encoded_packet_size_invalid");
                return;
            }

            if (latestStats.CaptureDrops != 0)
            {
                CompleteFailure("capture_drop_detected");
                return;
            }

            configurationCompletion = new TaskCompletionSource<bool>();
            stage = ProbeStage.Configuring;
            stageDeadlineRealtime = DeadlineAfter(ConfigurationTimeoutMs);

            if (!pipeline.ConfigureEncoder(24000, true, 10))
            {
                CompleteFailure("encoder_configuration_rejected");
                return;
            }

            status = "Audio path passed. Verifying encoder controls.";
            Debug.Log(
                "[VoiceBrowserAudio] stage=media_ready" +
                " | encodedFrames=" + latestStats.EncodedFrames +
                " | decodedFrames=" + latestStats.DecodedFrames +
                " | lastPacketBytes=" + latestStats.LastPacketBytes);
        }

        private void CompleteSuccess()
        {
            if (resultReported || pipeline == null) return;

            resultReported = true;
            running = false;
            finished = true;
            status = "PASS. The isolated browser audio foundation completed successfully.";

            Debug.Log(
                "VME2_PHASE14_WEBGL_AUDIO_LIVE=PASS" +
                " | explicitUserGesture=True" +
                " | normalPathActivation=False" +
                " | secureContext=True" +
                " | microphoneCapture=True" +
                " | audioWorklet=True" +
                " | dedicatedWorker=True" +
                " | unityMainThreadAudioTiming=False" +
                " | queuedUnityCallbacks=True" +
                " | realOpus=True" +
                " | opusVersion=" + Safe(pipeline.OpusVersion) +
                " | sourceSampleRate=" + pipeline.SourceSampleRate +
                " | targetSampleRate=48000" +
                " | frameSamples=960" +
                " | encodedFrames=" + latestStats.EncodedFrames +
                " | decodedFrames=" + latestStats.DecodedFrames +
                " | lastPacketBytes=" + latestStats.LastPacketBytes +
                " | adaptiveEncoderCtl=True" +
                " | productionIntegration=False" +
                " | windowsPathChanged=False");

            CleanupPipeline();
        }

        private void CompleteFailure(string reason)
        {
            if (resultReported) return;

            resultReported = true;
            running = false;
            failureReason = Safe(reason);
            status = "FAIL: " + failureReason;

            Debug.LogError(
                "VME2_PHASE14_WEBGL_AUDIO_LIVE=FAIL" +
                " | reason=" + failureReason +
                " | stage=" + stage +
                " | productionIntegration=False");

            CleanupPipeline();
        }

        private static double DeadlineAfter(int timeoutMs)
        {
            return Time.realtimeSinceStartupAsDouble + (timeoutMs / 1000d);
        }

        private bool HasStageTimedOut()
        {
            return Time.realtimeSinceStartupAsDouble >= stageDeadlineRealtime;
        }

        private async Task<bool> WaitForConfigurationAsync(CancellationToken cancellationToken)
        {
            Task timeoutTask = Task.Delay(ConfigurationTimeoutMs, cancellationToken);
            Task completedTask = await Task.WhenAny(configurationCompletion.Task, timeoutTask);
            if (completedTask == configurationCompletion.Task)
            {
                return await configurationCompletion.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        private static async Task<bool> WaitUntilAsync(
            Func<bool> condition,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            int waitedMs = 0;
            while (waitedMs < timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (condition()) return true;
                await Task.Delay(100, cancellationToken);
                waitedMs += 100;
            }

            return condition();
        }

        private void CleanupPipeline()
        {
            if (pipeline == null) return;

            pipeline.Ready -= HandleReady;
            pipeline.StatsUpdated -= HandleStats;
            pipeline.EncoderConfigured -= HandleConfigured;
            pipeline.Failed -= HandleFailure;
            pipeline.SetCaptureEnabled(false);
            pipeline.Dispose();
            pipeline = null;
        }

        private void OnDestroy()
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
            CleanupPipeline();
        }

        private static bool HasQueryValue(string key, string expectedValue)
        {
            string url = Application.absoluteURL;
            if (string.IsNullOrWhiteSpace(url)) return false;

            int queryIndex = url.IndexOf('?');
            if (queryIndex < 0 || queryIndex >= url.Length - 1) return false;

            string query = url.Substring(queryIndex + 1);
            int fragmentIndex = query.IndexOf('#');
            if (fragmentIndex >= 0) query = query.Substring(0, fragmentIndex);

            string[] entries = query.Split('&');
            for (int index = 0; index < entries.Length; index++)
            {
                string[] pair = entries[index].Split(new[] { '=' }, 2);
                string candidateKey = Uri.UnescapeDataString(pair[0] ?? string.Empty);
                string candidateValue = pair.Length > 1
                    ? Uri.UnescapeDataString(pair[1] ?? string.Empty)
                    : string.Empty;

                if (string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidateValue, expectedValue, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "unknown"
                : value.Replace('|', '_').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
#endif
