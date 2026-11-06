using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Visualization
{
    // Visual-only component: it never changes capture, transport, playback, or user consent.
    [DisallowMultipleComponent]
    public sealed class VME2W4VoiceLiveLevelIndicator : MonoBehaviour
    {
        private const string RuntimeRoot = "Voice_Client_Runtime_Root";
        private const float SampleInterval = 0.05f;
        private const float BindingInterval = 0.75f;
        private static readonly Color SpeakingGreen = new Color(0.16f, 1f, 0.35f, 1f);
        private static readonly BindingFlags PrivateInstance =
            BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly FieldInfo MicButtonField =
            typeof(VoiceSceneUserConsentPanelController).GetField("microphoneButton", PrivateInstance);
        private static readonly FieldInfo SpeakerButtonField =
            typeof(VoiceSceneUserConsentPanelController).GetField("speakerButton", PrivateInstance);
        private static readonly FieldInfo LastPublishedSequenceField =
            typeof(VoiceClientRuntime).GetField("lastPublishedSequence", PrivateInstance);
        private static readonly FieldInfo MicClipField =
            typeof(VoiceMicrophonePublisher).GetField("microphoneClip", PrivateInstance);
        private static readonly FieldInfo MicDeviceField =
            typeof(VoiceMicrophonePublisher).GetField("microphoneDevice", PrivateInstance);

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int VME2W4VoiceMeterReadPacked();
#endif

        private VoiceSceneUserConsentPanelController panel;
        private VoiceClientRuntime runtime;
        private VoiceMicrophonePublisher publisher;
        private Button micButton;
        private Button speakerButton;
        private Image micImage;
        private Image speakerImage;
        private Vector3 micBaseScale = Vector3.one;
        private Vector3 speakerBaseScale = Vector3.one;
        private Color micBaseColor = Color.white;
        private Color speakerBaseColor = Color.white;
        private float micIntensity;
        private float speakerIntensity;
        private float nextSampleAt;
        private float nextBindAt;
        private float lastEncodedAt = -100f;
        private float lastPublishedAt = -100f;
        private uint previousPublishedSequence;
        private bool bindingsLogged;
        private bool missingLogged;
        private AudioClip lastMicClip;
        private int lastMicPosition = -1;
        private float[] microphoneSamples;
        private readonly float[] speakerSamples = new float[1024];
        private AudioSource[] voiceSources = new AudioSource[0];
        private float nextSourceScanAt;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private Component windowsMediaAdapter;
        private FieldInfo windowsMediaBoundField;
        private FieldInfo windowsOwnsOutboundField;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            const string name = "VME2_W4_Real_Audio_Level_Meters";
            var root = GameObject.Find(name);
            if (root == null) root = new GameObject(name);
            DontDestroyOnLoad(root);
            if (root.GetComponent<VME2W4VoiceLiveLevelIndicator>() == null)
                root.AddComponent<VME2W4VoiceLiveLevelIndicator>();
        }

        private void OnEnable()
        {
            nextBindAt = 0f;
            nextSampleAt = 0f;
            SceneManager.sceneLoaded += SceneLoaded;
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ReleaseButtons();
            panel = null;
            nextBindAt = 0f;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            ReleaseButtons();
            UnbindPublisher();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            ReleaseButtons();
            UnbindPublisher();
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            if (now >= nextBindAt)
            {
                ResolveBindings();
                // Avoid frequent scene-wide searches while outside the voice scene.
                nextBindAt = now + (panel == null ? 2.5f : BindingInterval);
            }
            if (now < nextSampleAt) return;
            nextSampleAt = now + SampleInterval;
            bool authenticated = runtime != null && runtime.IsAuthenticated;
            bool inSession = authenticated && runtime.ActiveSessionCount > 0;
            bool micAllowed = inSession && !runtime.IsMicrophoneMuted;
            bool speakerAllowed = inSession && !runtime.IsSpeakerOff;
            float micRms = 0f, speakerRms = 0f;
#if UNITY_WEBGL && !UNITY_EDITOR
            if (micAllowed || speakerAllowed)
            {
                int packed = VME2W4VoiceMeterReadPacked();
                micRms = ((packed >> 10) & 1023) / 1023f;
                speakerRms = (packed & 1023) / 1023f;
            }
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (micAllowed && publisher != null &&
                now - lastEncodedAt < 0.25f &&
                IsWindowsMediaPublishing() && HasRecentWindowsPublication(now))
            {
                micRms = ReadExistingWindowsMicrophone();
            }
            if (speakerAllowed)
                speakerRms = ReadWindowsVoicePlayback(now);
#endif
            SetVisual(micImage, micBaseColor, micButton, micBaseScale,
                ref micIntensity, micAllowed ? micRms : 0f, !micAllowed);
            SetVisual(speakerImage, speakerBaseColor, speakerButton, speakerBaseScale,
                ref speakerIntensity, speakerAllowed ? speakerRms : 0f, !speakerAllowed);
        }

        private void ResolveBindings()
        {
            if (panel == null || !panel.isActiveAndEnabled)
            {
                if (panel != null) ReleaseButtons();
                panel = null;
                var candidates = FindObjectsOfType<VoiceSceneUserConsentPanelController>(true);
                foreach (var candidate in candidates)
                {
                    if (candidate != null && candidate.isActiveAndEnabled)
                    {
                        panel = candidate;
                        break;
                    }
                }
            }
            if (runtime == null)
            {
                var root = GameObject.Find(RuntimeRoot);
                if (root != null) runtime = root.GetComponent<VoiceClientRuntime>();
            }
            if (runtime == null || !runtime.isActiveAndEnabled)
            {
                runtime = null;
                UnbindPublisher();
            }
            else
            {
                var activePublisher = runtime.GetComponent<VoiceMicrophonePublisher>();
                if (activePublisher != publisher)
                {
                    UnbindPublisher();
                    publisher = activePublisher;
                    if (publisher != null) publisher.FrameEncoded += FrameEncoded;
                }
            }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (windowsMediaAdapter == null)
            {
                var mediaRoot = GameObject.Find("Voice_Windows_Media_WebGL_Parity_Runtime");
                if (mediaRoot != null)
                {
                    windowsMediaAdapter = mediaRoot.GetComponent("VoiceWindowsMediaWebGLParityRuntimeAdapter");
                    if (windowsMediaAdapter != null)
                    {
                        var mediaType = windowsMediaAdapter.GetType();
                        windowsMediaBoundField = mediaType.GetField("mediaBound", PrivateInstance);
                        windowsOwnsOutboundField = mediaType.GetField("ownsOutboundMedia", PrivateInstance);
                    }
                }
            }
#endif
            if (panel == null || micButton != null && speakerButton != null)
                return;
            if (MicButtonField == null || SpeakerButtonField == null)
            {
                if (!missingLogged)
                {
                    missingLogged = true;
                    Debug.LogWarning("VME2_W4_LEVEL_METER_BIND=FAIL | reason=panel_fields_not_found");
                }
                return;
            }
            var mic = MicButtonField.GetValue(panel) as Button;
            var speaker = SpeakerButtonField.GetValue(panel) as Button;
            if (mic == null || speaker == null || mic.image == null || speaker.image == null)
                return;
            ReleaseButtons();
            micButton = mic;
            speakerButton = speaker;
            micImage = mic.image;
            speakerImage = speaker.image;
            micBaseColor = micImage.color;
            speakerBaseColor = speakerImage.color;
            micBaseScale = micButton.transform.localScale;
            speakerBaseScale = speakerButton.transform.localScale;
            if (!bindingsLogged)
            {
                bindingsLogged = true;
                Debug.Log("VME2_W4_REAL_VOICE_METER=READY | source=existing_voice_path | controls=existing_mic_speaker | rateHz=20");
            }
        }

        private void FrameEncoded(byte[] payload, bool dtx)
        {
            // An encoded frame proves that the production microphone path is alive.
            if (payload != null && payload.Length > 0)
                lastEncodedAt = Time.unscaledTime;
        }

        private void UnbindPublisher()
        {
            if (publisher != null) publisher.FrameEncoded -= FrameEncoded;
            publisher = null;
            lastEncodedAt = -100f;
            lastPublishedAt = -100f;
            previousPublishedSequence = 0;
            lastMicClip = null;
            lastMicPosition = -1;
        }

        private void ReleaseButtons()
        {
            if (micImage != null) micImage.color = micBaseColor;
            if (speakerImage != null) speakerImage.color = speakerBaseColor;
            if (micButton != null) micButton.transform.localScale = micBaseScale;
            if (speakerButton != null) speakerButton.transform.localScale = speakerBaseScale;
            micButton = null;
            speakerButton = null;
            micImage = null;
            speakerImage = null;
            micIntensity = 0f;
            speakerIntensity = 0f;
        }

        private bool HasRecentWindowsPublication(float now)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (runtime == null || LastPublishedSequenceField == null) return false;
            try
            {
                uint sequence = (uint)LastPublishedSequenceField.GetValue(runtime);
                if (sequence != 0 && sequence != previousPublishedSequence)
                    lastPublishedAt = now;
                previousPublishedSequence = sequence;
                return now - lastPublishedAt < 0.22f;
            }
            catch { return false; }
#else
            return true;
#endif
        }

        private bool IsWindowsMediaPublishing()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // The current Windows WebGL-parity media adapter owns the real TX path.
            if (windowsMediaAdapter == null || windowsMediaBoundField == null ||
                windowsOwnsOutboundField == null) return false;
            try
            {
                return windowsMediaAdapter is Behaviour active && active.isActiveAndEnabled &&
                    (bool)windowsMediaBoundField.GetValue(windowsMediaAdapter) &&
                    (bool)windowsOwnsOutboundField.GetValue(windowsMediaAdapter);
            }
            catch { return false; }
#else
            // Windows Editor uses a different voice media route.
            return true;
#endif
        }

        private float ReadExistingWindowsMicrophone()
        {
#if UNITY_WEBGL
            // WebGL microphone levels are read from the existing WebRTC producer
            // through VME2W4VoiceMeterReadPacked(), not UnityEngine.Microphone.
            return 0f;
#else
            if (publisher == null || MicClipField == null || MicDeviceField == null)
                return 0f;
            var clip = MicClipField.GetValue(publisher) as AudioClip;
            var device = MicDeviceField.GetValue(publisher) as string;
            if (clip == null || clip.samples < 1024) return 0f;
            int position;
            try { position = UnityEngine.Microphone.GetPosition(device); }
            catch { return 0f; }
            if (position < 0) return 0f;
            if (clip != lastMicClip)
            {
                lastMicClip = clip;
                lastMicPosition = -1;
            }
            if (position == lastMicPosition) return 0f;
            lastMicPosition = position;
            int frames = Mathf.Min(1024, clip.samples);
            int channels = Mathf.Max(1, clip.channels);
            int length = frames * channels;
            if (microphoneSamples == null || microphoneSamples.Length != length)
                microphoneSamples = new float[length];
            int offset = (position - frames + clip.samples) % clip.samples;
            try { if (!clip.GetData(microphoneSamples, offset)) return 0f; }
            catch { return 0f; }
            return ComputeRms(microphoneSamples, length);
#endif
        }

        private float ReadWindowsVoicePlayback(float now)
        {
            if (now >= nextSourceScanAt)
            {
                nextSourceScanAt = now + 0.5f;
                var all = FindObjectsOfType<AudioSource>(false);
                var found = new System.Collections.Generic.List<AudioSource>();
                foreach (var source in all)
                    if (source != null && source.name.StartsWith("Voice_Playback_", StringComparison.Ordinal))
                        found.Add(source);
                voiceSources = found.ToArray();
            }
            float combinedSq = 0f;
            foreach (var source in voiceSources)
            {
                if (source == null || !source.isPlaying || source.mute || source.volume <= 0f) continue;
                try
                {
                    source.GetOutputData(speakerSamples, 0);
                    float rms = ComputeRms(speakerSamples, speakerSamples.Length);
                    combinedSq += rms * rms;
                }
                catch { }
            }
            return Mathf.Clamp01(Mathf.Sqrt(combinedSq));
        }

        private static float ComputeRms(float[] values, int length)
        {
            double sum = 0d;
            for (int i = 0; i < length; i++) sum += values[i] * values[i];
            return Mathf.Clamp01((float)Math.Sqrt(sum / Math.Max(length, 1)));
        }

        private static void SetVisual(Image image, Color baseline, Button button,
            Vector3 baseScale, ref float intensity, float rms, bool immediatelyReset)
        {
            if (button == null || image == null) { intensity = 0f; return; }
            float target = rms > 0.0016f
                ? Mathf.Clamp01((rms - 0.0016f) / 0.025f)
                : 0f;
            if (immediatelyReset) intensity = 0f;
            else
            {
                float attackOrDecay = target > intensity ? 17f : 9f;
                float factor = 1f - Mathf.Exp(-attackOrDecay * SampleInterval);
                intensity = Mathf.Lerp(intensity, target, factor);
                if (intensity < 0.015f) intensity = 0f;
            }
            float greenAmount = intensity > 0.025f
                ? Mathf.Lerp(0.65f, 1f, intensity)
                : 0f;
            var green = SpeakingGreen;
            green.a = baseline.a;
            image.color = Color.Lerp(baseline, green, greenAmount);
            button.transform.localScale = baseScale * (1f + intensity * 0.25f);
        }
    }
}
