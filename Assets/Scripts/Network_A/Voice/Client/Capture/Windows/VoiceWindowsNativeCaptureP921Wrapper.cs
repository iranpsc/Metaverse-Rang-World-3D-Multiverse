#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Network_A.Voice.Client.Codec;
using UnityEngine;

namespace Network_A.Voice.Client.Capture
{
    public static class VoiceWindowsNativeCaptureP921Installer
    {
        private const string RootName = "Voice_Windows_Native_Capture_P9_2_1";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            if (root.GetComponent<VoiceWindowsNativeCaptureP921Wrapper>() == null)
                root.AddComponent<VoiceWindowsNativeCaptureP921Wrapper>();

            Debug.Log(
                "VOICE_WINDOWS_NATIVE_CAPTURE_P9_5=READY" +
                " | platform=Windows" +
                " | wrapperOnly=True" +
                " | nativeDll=Vme2WindowsCapture" +
                " | capturePolicy=prefer-raw" +
                " | levelMode=adaptive-noise" +
                " | autoLevelTargetDbfs=-24" +
                " | autoLevelNoiseCeilingDbfs=-46" +
                " | deviceRecovery=True" +
                " | webglPathChanged=False" +
                " | questPathChanged=False" +
                " | stablePublisherFileChanged=False");
        }
    }

    [DefaultExecutionOrder(-31996)]
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsNativeCaptureP921Wrapper : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const float DevicePollSeconds = 0.50f;
        private const float DeviceRecoveryInitialDelaySeconds = 0.10f;
        private const float DeviceRecoveryMaximumDelaySeconds = 2.00f;
        private const int LevelReportFrames = 50;
        private const int MaxDrainFramesPerUpdate = 8;
        private const int CapturePolicyPreferRaw = 1;
        private const int LevelModeAdaptiveNoise = 2;
        private const int AutoChannel = -1;

        // P9.5 automatic input-level policy.
        // The native level controller measures every 20 ms PCM frame,
        // tracks the noise floor and moves gain toward this speech target.
        private const float AutomaticLevelTargetDbfs = -24.0f;
        private const float AutomaticLevelMinGainDb = -12.0f;
        private const float AutomaticLevelMaxGainDb = 30.0f;
        private const float AutomaticLevelActivityDbfs = -72.0f;
        private const float AutomaticLevelLimiterDbfs = -3.0f;
        private const int AutomaticLevelGainRiseMs = 120;
        private const int AutomaticLevelGainFallMs = 60;
        private const int AutomaticLevelSilenceHoldMs = 1200;
        private const float AutomaticLevelNoiseMarginDb = 8.0f;
        private const int AutomaticLevelNoiseWindowMs = 2000;
        private const int AutomaticLevelNoisePercentile = 10;
        private const float AutomaticLevelMaxOutputNoiseDbfs = -46.0f;
        private const int AutomaticLevelAdjacentSpeechMs = 120;
        private const float AutomaticLevelMaxGainIncreaseDbPerSecond = 6.0f;
        private const float AutomaticLevelMaxGainDecreaseDbPerSecond = 12.0f;

        private static readonly FieldInfo CodecField =
            typeof(VoiceMicrophonePublisher).GetField(
                "codec",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo FrameEncodedField =
            typeof(VoiceMicrophonePublisher).GetField(
                "FrameEncoded",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo FailedField =
            typeof(VoiceMicrophonePublisher).GetField(
                "Failed",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo StartLegacyCaptureMethod =
            typeof(VoiceMicrophonePublisher).GetMethod(
                "StartCapture",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo StopLegacyCaptureMethod =
            typeof(VoiceMicrophonePublisher).GetMethod(
                "StopCapture",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly object startStateSync = new object();
        private readonly float[] encodeFrame =
            new float[VoiceNativeOpusCodec.FrameSamples];

        private VoiceMicrophonePublisher publisher;
        private Thread nativeStartThread;
        private string requestedDevice = string.Empty;
        private string nativeStartError = string.Empty;
        private string nativeRecoveryReason = string.Empty;
        private float nextDevicePollAt;
        private float nativeRecoveryNextAt;
        private int nativeRecoveryAttempt;
        private bool publisherWasEnabled;
        private bool publisherIntercepted;
        private bool nativeStarted;
        private bool nativeDisabledForSession;
        private bool nativeRecoveryPending;
        private volatile bool nativeStartInProgress;
        private bool nativeStartCompleted;
        private bool nativeStartSucceeded;
        private volatile bool nativeStartStopRequested;
        private bool firstFrameLogged;
        private bool applicationQuitting;
        private int levelFrameCount;
        private double levelRmsSum;
        private float levelPeak;
        private long encodedFrames;
        private long noSubscriberDrops;
        private int lastNativeDroppedFrames;

        private void Update()
        {
            ResolvePublisher();
            CompleteNativeStartIfReady();

            if (publisher == null || !publisherIntercepted) return;

            if (publisher.IsMuted)
            {
                CancelNativeRecovery();
                StopNativeCapture();
                return;
            }

            if (nativeRecoveryPending)
            {
                ContinueNativeRecovery();
                if (!nativeStarted) return;
            }
            else
            {
                EnsureNativeCapture();
                if (!nativeStarted) return;
            }

            int status = Vme2WindowsCaptureNativeP921.Status();
            if (status < 0)
            {
                string nativeError =
                    ReadNativeText(Vme2WindowsCaptureNativeP921.GetError);

                if (IsRecoverableDeviceInvalidation(nativeError))
                {
                    BeginNativeDeviceRecovery(nativeError);
                    return;
                }

                FallbackToLegacy(
                    "Native capture entered failed state. " +
                    nativeError);
                return;
            }

            DrainNativeFrames();
            PollSelectedDevice();
        }

        private void ResolvePublisher()
        {
            GameObject runtimeRoot = GameObject.Find(RuntimeRootName);
            VoiceMicrophonePublisher candidate = runtimeRoot != null
                ? runtimeRoot.GetComponent<VoiceMicrophonePublisher>()
                : null;

            if (ReferenceEquals(candidate, publisher)) return;

            UnbindPublisher();
            nativeDisabledForSession = false;
            CancelNativeRecovery();
            if (candidate == null) return;

            VoiceNativeOpusCodec codec = GetPublisherCodec(candidate);
            if (codec == null) return;

            publisher = candidate;
            publisherWasEnabled = publisher.enabled;
            publisher.enabled = false;
            publisher.MuteChanged += HandleMuteChanged;
            publisherIntercepted = true;

            Debug.Log(
                "VOICE_WINDOWS_NATIVE_PUBLISHER_INTERCEPT=PASS" +
                " | publisher=" + publisher.GetType().Name +
                " | publisherUpdateDisabled=True" +
                " | publisherMuteStatePreserved=True" +
                " | publisherCodecReused=True" +
                " | publisherFrameEncodedEventReused=True" +
                " | stablePublisherFileChanged=False");

            if (!publisher.IsMuted)
            {
                SuppressUnityMicrophoneCapture();
                EnsureNativeCapture();
            }
        }

        private void HandleMuteChanged(bool muted)
        {
            if (muted)
            {
                CancelNativeRecovery();
                StopNativeCapture();
                return;
            }

            if (nativeDisabledForSession) return;

            SuppressUnityMicrophoneCapture();
            EnsureNativeCapture();
        }

        private void SuppressUnityMicrophoneCapture()
        {
            if (publisher == null || StopLegacyCaptureMethod == null) return;

            try
            {
                StopLegacyCaptureMethod.Invoke(publisher, null);
            }
            catch (Exception exception)
            {
                FallbackToLegacy(
                    "Could not stop Unity Microphone capture before native capture. " +
                    Unwrap(exception).Message);
            }
        }

        private void EnsureNativeCapture()
        {
            if (publisher == null || publisher.IsMuted) return;
            if (nativeDisabledForSession || nativeStarted || nativeStartInProgress) return;

            if (!TryGetSelectedDevice(out requestedDevice, out string resolverError))
            {
                requestedDevice = "<windows-default>";
                Debug.LogWarning(
                    "VOICE_WINDOWS_NATIVE_DEVICE_RESOLVER_WARNING" +
                    " | reason=" + Safe(resolverError) +
                    " | nativeStartUsesDefaultEndpoint=True");
            }

            nextDevicePollAt = Time.unscaledTime + DevicePollSeconds;
            nativeStartStopRequested = false;
            nativeStartCompleted = false;
            nativeStartSucceeded = false;
            nativeStartError = string.Empty;
            nativeStartInProgress = true;

            nativeStartThread = new Thread(NativeStartThreadMain)
            {
                IsBackground = true,
                Name = "VME2 Unity Native Capture Start"
            };
            nativeStartThread.Start();
        }

        private void NativeStartThreadMain()
        {
            bool success = false;
            string error = string.Empty;

            try
            {
                int levelResult =
                    Vme2WindowsCaptureNativeP921.ConfigureLevelConditioner(
                        LevelModeAdaptiveNoise,
                        AutomaticLevelTargetDbfs,
                        AutomaticLevelMinGainDb,
                        AutomaticLevelMaxGainDb,
                        AutomaticLevelActivityDbfs,
                        AutomaticLevelLimiterDbfs,
                        AutomaticLevelGainRiseMs,
                        AutomaticLevelGainFallMs,
                        AutomaticLevelSilenceHoldMs);

                if (levelResult != 0)
                {
                    error = "ConfigureLevelConditioner failed. code=" + levelResult;
                }
                else
                {
                    int noiseResult =
                        Vme2WindowsCaptureNativeP921.ConfigureAdaptiveNoise(
                            AutomaticLevelNoiseMarginDb,
                            AutomaticLevelNoiseWindowMs,
                            AutomaticLevelNoisePercentile,
                            AutomaticLevelMaxOutputNoiseDbfs,
                            AutomaticLevelAdjacentSpeechMs,
                            AutomaticLevelMaxGainIncreaseDbPerSecond,
                            AutomaticLevelMaxGainDecreaseDbPerSecond);

                    if (noiseResult != 0)
                    {
                        error = "ConfigureAdaptiveNoise failed. code=" + noiseResult;
                    }
                    else
                    {
                        int startResult =
                            Vme2WindowsCaptureNativeP921.StartDefaultEx(
                                CapturePolicyPreferRaw,
                                AutoChannel);

                        if (startResult != 0 ||
                            Vme2WindowsCaptureNativeP921.Status() != 2)
                        {
                            error =
                                "StartDefaultEx failed. code=" + startResult +
                                " | status=" + Vme2WindowsCaptureNativeP921.Status() +
                                " | nativeError=" +
                                ReadNativeText(Vme2WindowsCaptureNativeP921.GetError);
                        }
                        else if (nativeStartStopRequested)
                        {
                            Vme2WindowsCaptureNativeP921.Stop();
                            error = "Native start was cancelled before activation.";
                        }
                        else
                        {
                            success = true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error =
                    exception.GetType().Name +
                    ": " +
                    exception.Message;
            }

            lock (startStateSync)
            {
                nativeStartSucceeded = success;
                nativeStartError = error;
                nativeStartCompleted = true;
                nativeStartInProgress = false;
            }
        }

        private void CompleteNativeStartIfReady()
        {
            bool completed;
            bool succeeded;
            string error;

            lock (startStateSync)
            {
                completed = nativeStartCompleted;
                if (!completed) return;

                succeeded = nativeStartSucceeded;
                error = nativeStartError;
                nativeStartCompleted = false;
                nativeStartSucceeded = false;
                nativeStartError = string.Empty;
            }

            Thread completedThread = nativeStartThread;
            nativeStartThread = null;
            if (completedThread != null && !completedThread.IsAlive)
                completedThread.Join(0);

            if (!succeeded)
            {
                if (!nativeStartStopRequested &&
                    publisher != null &&
                    !publisher.IsMuted)
                {
                    if (nativeRecoveryPending)
                    {
                        ScheduleNativeRecoveryRetry(error);
                    }
                    else
                    {
                        FallbackToLegacy(error);
                    }
                }
                return;
            }

            if (nativeStartStopRequested ||
                publisher == null ||
                publisher.IsMuted)
            {
                try { Vme2WindowsCaptureNativeP921.Stop(); }
                catch { }
                return;
            }

            nativeStarted = true;
            lastNativeDroppedFrames = Vme2WindowsCaptureNativeP921.DroppedFrames();

            if (nativeRecoveryPending)
            {
                Debug.Log(
                    "VOICE_WINDOWS_NATIVE_DEVICE_RECOVERY=PASS" +
                    " | attempts=" + nativeRecoveryAttempt +
                    " | reason=" + Safe(nativeRecoveryReason) +
                    " | nativeDevice=" + Safe(
                        ReadNativeText(Vme2WindowsCaptureNativeP921.GetDevice)) +
                    " | inputChannels=" +
                    Vme2WindowsCaptureNativeP921.InputChannels() +
                    " | selectedChannel=" +
                    Vme2WindowsCaptureNativeP921.SelectedChannel() +
                    " | nativeOwnerPreserved=True" +
                    " | legacyFallback=False");

                CancelNativeRecovery();
            }

            Debug.Log(
                "VOICE_WINDOWS_NATIVE_CAPTURE_STARTED=PASS" +
                " | version=" + Safe(
                    ReadNativeText(Vme2WindowsCaptureNativeP921.GetVersion)) +
                " | requestedDevice=" + Safe(requestedDevice) +
                " | nativeDevice=" + Safe(
                    ReadNativeText(Vme2WindowsCaptureNativeP921.GetDevice)) +
                " | endpointId=" + Safe(
                    ReadNativeText(Vme2WindowsCaptureNativeP921.GetEndpointId)) +
                " | raw=" + Vme2WindowsCaptureNativeP921.RawMode() +
                " | rawCapability=" + Vme2WindowsCaptureNativeP921.RawCapability() +
                " | fallbackUsed=" + Vme2WindowsCaptureNativeP921.FallbackUsed() +
                " | inputRate=" + Vme2WindowsCaptureNativeP921.InputSampleRate() +
                " | outputRate=" + Vme2WindowsCaptureNativeP921.OutputSampleRate() +
                " | inputChannels=" + Vme2WindowsCaptureNativeP921.InputChannels() +
                " | selectedChannel=" + Vme2WindowsCaptureNativeP921.SelectedChannel() +
                " | levelMode=" + Vme2WindowsCaptureNativeP921.LevelConditionerMode() +
                " | autoTargetDbfs=" + AutomaticLevelTargetDbfs.ToString("0.0") +
                " | autoNoiseCeilingDbfs=" +
                AutomaticLevelMaxOutputNoiseDbfs.ToString("0.0") +
                " | autoGainIncreaseDbps=" +
                AutomaticLevelMaxGainIncreaseDbPerSecond.ToString("0.0"));
        }

        private void DrainNativeFrames()
        {
            for (int index = 0; index < MaxDrainFramesPerUpdate; index++)
            {
                int read = Vme2WindowsCaptureNativeP921.Read(
                    encodeFrame,
                    encodeFrame.Length);

                if (read == 0) break;
                if (read < 0)
                {
                    FallbackToLegacy("Native capture read failed. code=" + read);
                    return;
                }

                PublishFrame(encodeFrame);
                if (!nativeStarted) return;
            }
        }

        private void PublishFrame(float[] samples)
        {
            VoiceMicrophonePublisher activePublisher = publisher;
            if (activePublisher == null || activePublisher.IsMuted) return;

            VoiceNativeOpusCodec codec = GetPublisherCodec(activePublisher);
            if (codec == null)
            {
                ReportPublisherFailure("Windows native capture publisher codec is unavailable.");
                activePublisher.SetMuted(true);
                return;
            }

            MeasureLevel(samples, out float rms, out float peak);

            try
            {
                byte[] packet = codec.Encode(samples);
                bool dtx = packet.Length <= 3;
                encodedFrames += 1;
                levelFrameCount += 1;
                levelRmsSum += rms;
                if (peak > levelPeak) levelPeak = peak;

                Action<byte[], bool> sink = FrameEncodedField != null
                    ? FrameEncodedField.GetValue(activePublisher) as Action<byte[], bool>
                    : null;

                if (sink == null)
                {
                    noSubscriberDrops += 1;
                    if (noSubscriberDrops == 1 || noSubscriberDrops % 50 == 0)
                    {
                        Debug.LogWarning(
                            "VOICE_WINDOWS_NATIVE_NO_FRAME_SUBSCRIBER" +
                            " | dropped=" + noSubscriberDrops +
                            " | packetBytes=" + packet.Length);
                    }
                }
                else
                {
                    sink.Invoke(packet, dtx);
                }

                if (!firstFrameLogged)
                {
                    firstFrameLogged = true;
                    Debug.Log(
                        "VOICE_WINDOWS_NATIVE_FIRST_FRAME=PASS" +
                        " | samples=" + samples.Length +
                        " | packetBytes=" + packet.Length +
                        " | dtx=" + dtx +
                        " | publisherEventPath=True");
                }

                if (levelFrameCount >= LevelReportFrames)
                {
                    int dropped = Vme2WindowsCaptureNativeP921.DroppedFrames();
                    int droppedDelta = dropped - lastNativeDroppedFrames;
                    lastNativeDroppedFrames = dropped;

                    Debug.Log(
                        "VOICE_WINDOWS_NATIVE_FRAME_LEVEL" +
                        " | avgRms=" +
                        (levelRmsSum / levelFrameCount).ToString("0.000000") +
                        " | peak=" + levelPeak.ToString("0.000000") +
                        " | gainDb=" +
                        (Vme2WindowsCaptureNativeP921.LevelCurrentGainMilliDb() / 1000.0f)
                            .ToString("0.000") +
                        " | noiseDbfs=" +
                        (Vme2WindowsCaptureNativeP921.LevelNoiseFloorMilliDbfs() / 1000.0f)
                            .ToString("0.000") +
                        " | autoTargetDbfs=" +
                        AutomaticLevelTargetDbfs.ToString("0.0") +
                        " | inputChannels=" +
                        Vme2WindowsCaptureNativeP921.InputChannels() +
                        " | selectedChannel=" +
                        Vme2WindowsCaptureNativeP921.SelectedChannel() +
                        " | activeFrames=" +
                        Vme2WindowsCaptureNativeP921.LevelActiveFrames() +
                        " | noiseRejectedActive=" +
                        Vme2WindowsCaptureNativeP921.LevelNoiseRejectedActiveFrames() +
                        " | limiterFrames=" +
                        Vme2WindowsCaptureNativeP921.LevelLimiterFrames() +
                        " | queued=" + Vme2WindowsCaptureNativeP921.QueuedFrames() +
                        " | dropped=" + dropped +
                        " | droppedDelta=" + droppedDelta +
                        " | lastPacketBytes=" + packet.Length +
                        " | dtx=" + dtx +
                        " | encodedTotal=" + encodedFrames +
                        " | noSubscriberDrops=" + noSubscriberDrops);

                    levelFrameCount = 0;
                    levelRmsSum = 0d;
                    levelPeak = 0f;
                }
            }
            catch (Exception exception)
            {
                ReportPublisherFailure(
                    "Windows native capture encode or publish failed: " +
                    Unwrap(exception).Message);
                activePublisher.SetMuted(true);
            }
        }

        private void BeginNativeDeviceRecovery(string nativeError)
        {
            if (nativeRecoveryPending) return;

            string previousDevice = requestedDevice;
            string currentDevice = previousDevice;
            if (TryGetSelectedDevice(out string selectedDevice, out _))
                currentDevice = selectedDevice;

            nativeRecoveryPending = true;
            nativeRecoveryAttempt = 0;
            nativeRecoveryReason = string.IsNullOrWhiteSpace(nativeError)
                ? "audio endpoint changed or invalidated"
                : nativeError;
            nativeRecoveryNextAt =
                Time.unscaledTime + DeviceRecoveryInitialDelaySeconds;

            Debug.LogWarning(
                "VOICE_WINDOWS_NATIVE_DEVICE_RECOVERY=START" +
                " | reason=" + Safe(nativeRecoveryReason) +
                " | previousDevice=" + Safe(previousDevice) +
                " | currentDevice=" + Safe(currentDevice) +
                " | nativeOwnerPreserved=True" +
                " | legacyFallback=False" +
                " | retryNative=True");

            StopNativeCapture();
        }

        private void ContinueNativeRecovery()
        {
            if (!nativeRecoveryPending) return;
            if (nativeStarted || nativeStartInProgress) return;
            if (publisher == null || publisher.IsMuted) return;
            if (Time.unscaledTime < nativeRecoveryNextAt) return;

            nativeRecoveryAttempt += 1;

            Debug.Log(
                "VOICE_WINDOWS_NATIVE_DEVICE_RECOVERY=RETRY" +
                " | attempt=" + nativeRecoveryAttempt +
                " | previousReason=" + Safe(nativeRecoveryReason) +
                " | legacyFallback=False");

            EnsureNativeCapture();
        }

        private void ScheduleNativeRecoveryRetry(string startError)
        {
            if (!nativeRecoveryPending) return;

            if (!string.IsNullOrWhiteSpace(startError))
                nativeRecoveryReason = startError;

            float exponent = Mathf.Min(nativeRecoveryAttempt, 5);
            float delay = Mathf.Min(
                DeviceRecoveryMaximumDelaySeconds,
                DeviceRecoveryInitialDelaySeconds * Mathf.Pow(2f, exponent));

            nativeRecoveryNextAt = Time.unscaledTime + delay;

            Debug.LogWarning(
                "VOICE_WINDOWS_NATIVE_DEVICE_RECOVERY=WAIT" +
                " | attempt=" + nativeRecoveryAttempt +
                " | delayMs=" + Mathf.RoundToInt(delay * 1000f) +
                " | reason=" + Safe(nativeRecoveryReason) +
                " | legacyFallback=False");
        }

        private void CancelNativeRecovery()
        {
            nativeRecoveryPending = false;
            nativeRecoveryAttempt = 0;
            nativeRecoveryNextAt = 0f;
            nativeRecoveryReason = string.Empty;
        }

        private static bool IsRecoverableDeviceInvalidation(string nativeError)
        {
            if (string.IsNullOrWhiteSpace(nativeError)) return false;

            return nativeError.IndexOf(
                       "0x88890004",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   nativeError.IndexOf(
                       "AUDCLNT_E_DEVICE_INVALIDATED",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void PollSelectedDevice()
        {
            if (!nativeStarted || nativeRecoveryPending ||
                Time.unscaledTime < nextDevicePollAt)
            {
                return;
            }

            nextDevicePollAt = Time.unscaledTime + DevicePollSeconds;

            if (!TryGetSelectedDevice(out string selected, out _)) return;
            if (string.Equals(
                    selected,
                    requestedDevice,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string previous = requestedDevice;

            Debug.Log(
                "VOICE_WINDOWS_NATIVE_DEVICE_SWITCH=DETECTED" +
                " | previous=" + Safe(previous) +
                " | current=" + Safe(selected) +
                " | nativeOwnerPreserved=True" +
                " | restartCapture=True");

            BeginNativeDeviceRecovery(
                "Windows default capture endpoint changed from " +
                Safe(previous) + " to " + Safe(selected));
        }

        private void StopNativeCapture()
        {
            nativeStartStopRequested = true;

            if (nativeStarted)
            {
                try { Vme2WindowsCaptureNativeP921.Stop(); }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "VOICE_WINDOWS_NATIVE_STOP_WARNING" +
                        " | reason=" + Safe(Unwrap(exception).Message));
                }
            }

            nativeStarted = false;
            requestedDevice = string.Empty;
            nextDevicePollAt = 0f;
            firstFrameLogged = false;
            levelFrameCount = 0;
            levelRmsSum = 0d;
            levelPeak = 0f;
            lastNativeDroppedFrames = 0;
        }

        private void FallbackToLegacy(string reason)
        {
            if (nativeDisabledForSession) return;

            string safeReason = string.IsNullOrWhiteSpace(reason)
                ? "Windows native capture failed."
                : reason;

            nativeDisabledForSession = true;
            CancelNativeRecovery();
            StopNativeCapture();

            VoiceMicrophonePublisher activePublisher = publisher;
            if (activePublisher != null)
            {
                activePublisher.MuteChanged -= HandleMuteChanged;
                if (publisherWasEnabled) activePublisher.enabled = true;
                publisherIntercepted = false;

                if (!activePublisher.IsMuted && StartLegacyCaptureMethod != null)
                {
                    try { StartLegacyCaptureMethod.Invoke(activePublisher, null); }
                    catch (Exception exception)
                    {
                        ReportPublisherFailure(
                            "Native fallback could not restart Unity Microphone capture: " +
                            Unwrap(exception).Message);
                    }
                }
            }

            Debug.LogError(
                "VOICE_WINDOWS_NATIVE_FALLBACK_TO_UNITY=PASS" +
                " | reason=" + Safe(safeReason) +
                " | nativeDisabledForSession=True" +
                " | publisherRestored=True");
        }

        private void UnbindPublisher()
        {
            CancelNativeRecovery();
            StopNativeCapture();

            VoiceMicrophonePublisher activePublisher = publisher;
            if (activePublisher != null)
            {
                activePublisher.MuteChanged -= HandleMuteChanged;

                if (!applicationQuitting && publisherWasEnabled)
                {
                    activePublisher.enabled = true;

                    if (!activePublisher.IsMuted && StartLegacyCaptureMethod != null)
                    {
                        try { StartLegacyCaptureMethod.Invoke(activePublisher, null); }
                        catch { }
                    }
                }
            }

            publisher = null;
            publisherWasEnabled = false;
            publisherIntercepted = false;
        }

        private void ReportPublisherFailure(string reason)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason)
                ? "Windows native capture failed."
                : reason;

            Debug.LogError(
                "VOICE_WINDOWS_NATIVE_CAPTURE_FAILED" +
                " | reason=" + Safe(safeReason));

            VoiceMicrophonePublisher activePublisher = publisher;
            if (activePublisher == null || FailedField == null) return;

            try
            {
                Action<string> failed =
                    FailedField.GetValue(activePublisher) as Action<string>;
                failed?.Invoke(safeReason);
            }
            catch
            {
            }
        }

        private static VoiceNativeOpusCodec GetPublisherCodec(
            VoiceMicrophonePublisher target)
        {
            if (target == null || CodecField == null) return null;
            return CodecField.GetValue(target) as VoiceNativeOpusCodec;
        }

        private static bool TryGetSelectedDevice(
            out string selected,
            out string error)
        {
            return WindowsSelectedCaptureDeviceResolver
                .TryGetSoundSettingsInputDevice(out selected, out error);
        }

        private static string ReadNativeText(NativeTextReader reader)
        {
            byte[] buffer = new byte[2048];
            int result = reader(buffer, buffer.Length);
            if (result < 0) return string.Empty;

            int length = Array.IndexOf(buffer, (byte)0);
            if (length < 0) length = buffer.Length;
            return Encoding.UTF8.GetString(buffer, 0, length);
        }

        private static void MeasureLevel(
            float[] samples,
            out float rms,
            out float peak)
        {
            double sum = 0d;
            float max = 0f;

            for (int index = 0; index < samples.Length; index++)
            {
                float value = samples[index];
                float absolute = Mathf.Abs(value);
                if (absolute > max) max = absolute;
                sum += value * value;
            }

            rms = samples.Length > 0
                ? (float)Math.Sqrt(sum / samples.Length)
                : 0f;
            peak = max;
        }

        private static Exception Unwrap(Exception exception)
        {
            TargetInvocationException invocation =
                exception as TargetInvocationException;
            return invocation != null && invocation.InnerException != null
                ? invocation.InnerException
                : exception;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "<empty>"
                : value.Trim().Replace("|", "/");
        }

        private void OnApplicationQuit()
        {
            applicationQuitting = true;
            nativeStartStopRequested = true;
        }

        private void OnDisable()
        {
            if (Application.isPlaying) UnbindPublisher();
        }

        private void OnDestroy()
        {
            UnbindPublisher();
        }

        private delegate int NativeTextReader(byte[] destination, int capacity);
    }

    internal static class Vme2WindowsCaptureNativeP921
    {
        private const string LibraryName = "Vme2WindowsCapture";

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_StartDefaultEx", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int StartDefaultEx(int capturePolicy, int channelIndex);

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_Stop", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Stop();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_Read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Read([In, Out] float[] destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_Status", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Status();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_RawMode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RawMode();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_RawCapability", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RawCapability();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_FallbackUsed", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FallbackUsed();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_InputChannels", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int InputChannels();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_SelectedChannel", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SelectedChannel();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_InputSampleRate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int InputSampleRate();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_OutputSampleRate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OutputSampleRate();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_QueuedFrames", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int QueuedFrames();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_DroppedFrames", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DroppedFrames();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_ConfigureLevelConditioner", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ConfigureLevelConditioner(
            int mode,
            float targetDbfs,
            float minGainDb,
            float maxGainDb,
            float activityDbfs,
            float limiterDbfs,
            int gainRiseMs,
            int gainFallMs,
            int silenceHoldMs);

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_ConfigureAdaptiveNoise", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ConfigureAdaptiveNoise(
            float noiseMarginDb,
            int noiseWindowMs,
            int noisePercentile,
            float maxOutputNoiseDbfs,
            int adjacentSpeechMs,
            float maxGainIncreaseDbPerSecond,
            float maxGainDecreaseDbPerSecond);

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_LevelConditionerMode", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LevelConditionerMode();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_LevelCurrentGainMilliDb", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LevelCurrentGainMilliDb();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_LevelLimiterFrames", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LevelLimiterFrames();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_LevelNoiseFloorMilliDbfs", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LevelNoiseFloorMilliDbfs();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_LevelActiveFrames", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LevelActiveFrames();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_LevelNoiseRejectedActiveFrames", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LevelNoiseRejectedActiveFrames();

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_GetError", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetError([Out] byte[] destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_GetDevice", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetDevice([Out] byte[] destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_GetEndpointId", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetEndpointId([Out] byte[] destination, int capacity);

        [DllImport(LibraryName, EntryPoint = "Vme2WindowsCapture_GetVersion", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetVersion([Out] byte[] destination, int capacity);
    }
}
#endif
