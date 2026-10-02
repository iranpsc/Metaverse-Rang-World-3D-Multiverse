using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Network_A.Voice.Client.Codec;
using UnityEngine;

namespace Network_A.Voice.Client.Playback
{
    public sealed class VoiceSpatialPlaybackManager : MonoBehaviour
    {
        private readonly Dictionary<string, PlaybackStream> streams =
            new Dictionary<string, PlaybackStream>(StringComparer.OrdinalIgnoreCase);

        private bool speakerOff;

        //* Ø§ÛŒÙ† ØªØ§Ø¨Ø¹ ÛŒÚ© ÙØ±ÛŒÙ… Ø§ÙˆÙ¾ÙˆØ³ Session Ø±Ø§ Ø¨Ù‡ Stream ÙØ¶Ø§ÛŒÛŒ Ù‡Ù…Ø§Ù† ÙØ±Ø³ØªÙ†Ø¯Ù‡ Ù…ÛŒâ€ŒØ¯Ù‡Ø¯.
                public void ReceiveFrame(string sessionId, string senderConnectionId, string peerUserId, byte[] opusPacket)
        {
            if (speakerOff || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(senderConnectionId)) return;

            string normalizedSessionId = sessionId.Trim();
            string normalizedSenderConnectionId = senderConnectionId.Trim();
            string streamKey = normalizedSessionId + "|" + normalizedSenderConnectionId;
            Transform peerTransform = FindPeerTransform(peerUserId);

            if (!streams.TryGetValue(streamKey, out PlaybackStream stream))
            {
                stream = new PlaybackStream(streamKey, peerTransform, transform);
                streams.Add(streamKey, stream);

                Debug.Log(
                    "VOICE_CLIENT_PLAYBACK_STREAM_CREATED" +
                    " | sessionId=" + normalizedSessionId +
                    " | senderConnectionId=" + normalizedSenderConnectionId +
                    " | streamKey=" + streamKey
                );
            }
            else
            {
                stream.UpdateSpatialTarget(peerTransform);
            }

            stream.Enqueue(opusPacket);
        }

        //* Ø§ÛŒÙ† ØªØ§Ø¨Ø¹ Speaker Off Ø±Ø§ Ù…Ø­Ù„ÛŒ Ø§Ø¹Ù…Ø§Ù„ Ùˆ ØªÙ…Ø§Ù… QueueÙ‡Ø§ÛŒ Ù‚Ø¨Ù„ÛŒ Ø±Ø§ Ù¾Ø§Ú© Ù…ÛŒâ€ŒÚ©Ù†Ø¯.
        public void SetSpeakerOff(bool value)
        {
            speakerOff = value;
            foreach (PlaybackStream stream in streams.Values) stream.SetAudible(!speakerOff);
        }

        //* Ø§ÛŒÙ† ØªØ§Ø¨Ø¹ Stream Ù¾Ø§ÛŒØ§Ù†â€ŒÛŒØ§ÙØªÙ‡ Ø±Ø§ Ú©Ø§Ù…Ù„ Ù¾Ø§Ú© Ù…ÛŒâ€ŒÚ©Ù†Ø¯.
                public void RemoveSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            string normalizedSessionId = sessionId.Trim();
            string streamPrefix = normalizedSessionId + "|";
            List<string> keysToRemove = new List<string>();

            foreach (string key in streams.Keys)
            {
                if (
                    string.Equals(key, normalizedSessionId, StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith(streamPrefix, StringComparison.OrdinalIgnoreCase)
                )
                {
                    keysToRemove.Add(key);
                }
            }

            for (int index = 0; index < keysToRemove.Count; index++)
            {
                string key = keysToRemove[index];

                if (!streams.TryGetValue(key, out PlaybackStream stream))
                {
                    continue;
                }

                streams.Remove(key);
                stream.Dispose();
            }
        }

        //* این تابع فقط Stream همان فرستنده را هنگام خروج یک عضو از Group پاک می‌کند و Session را نگه می‌دارد.
        public void RemoveSender(
            string sessionId,
            string senderConnectionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) ||
                string.IsNullOrWhiteSpace(senderConnectionId))
            {
                return;
            }

            string streamKey = sessionId.Trim() + "|" +
                               senderConnectionId.Trim();
            PlaybackStream stream;
            if (!streams.TryGetValue(streamKey, out stream)) return;

            streams.Remove(streamKey);
            stream.Dispose();
        }

        private static Transform FindPeerTransform(string peerUserId)
        {
            if (string.IsNullOrWhiteSpace(peerUserId)) return null;
            MetaverseNetworkIdentity[] identities = FindObjectsOfType<MetaverseNetworkIdentity>(true);
            for (int index = 0; index < identities.Length; index++)
            {
                MetaverseNetworkIdentity identity = identities[index];
                if (identity == null) continue;
                if (!identity.gameObject.activeInHierarchy) continue;
                if (identity.IsOwnedByUser(peerUserId)) return identity.transform;
            }
            return null;
        }

        private void OnDestroy()
        {
            foreach (PlaybackStream stream in streams.Values) stream.Dispose();
            streams.Clear();
        }

        private sealed class PlaybackStream : IDisposable
        {
            private const int MaximumQueuedFrames = 64;
            private const int MinimumBufferedFramesBeforePlayback = 10;
            private const int FadeToSilenceSamples = 480;
            private readonly ConcurrentQueue<float[]> pcmFrames = new ConcurrentQueue<float[]>();
            private readonly VoiceNativeOpusCodec codec;
            private readonly GameObject playbackObject;
            private readonly AudioSource audioSource;
            private readonly Transform fallbackParent;
            private float[] currentFrame;
            private int currentOffset;
            private bool buffering = true;

            //* Ø§ÛŒÙ† Ø³Ø§Ø²Ù†Ø¯Ù‡ AudioSource Ø³Ù‡â€ŒØ¨Ø¹Ø¯ÛŒ Stream Ø±Ø§ Ø±ÙˆÛŒ Transform ÙØ¹Ø§Ù„ Ù‡Ù…ØªØ§ Ù…ÛŒâ€ŒØ³Ø§Ø²Ø¯Ø› Ø§Ú¯Ø± Transform Ù‡Ù…ØªØ§ ØºÛŒØ±ÙØ¹Ø§Ù„ Ø¨Ø§Ø´Ø¯ Ø±ÙˆÛŒ Root ÙØ¹Ø§Ù„ Voice Ù…ÛŒâ€ŒÙ…Ø§Ù†Ø¯.
            public PlaybackStream(string sessionId, Transform peerTransform, Transform fallbackParent)
            {
                this.fallbackParent = fallbackParent;
                codec = new VoiceNativeOpusCodec(32);
                playbackObject = new GameObject("Voice_Playback_" + sessionId);
                Transform initialParent = ResolvePlaybackParent(peerTransform);
                playbackObject.transform.SetParent(initialParent, false);
                playbackObject.transform.localPosition = Vector3.zero;
                playbackObject.SetActive(true);

                audioSource = playbackObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
                audioSource.loop = true;
                audioSource.rolloffMode = AudioRolloffMode.Linear;
                audioSource.minDistance = 3f;
                audioSource.maxDistance = 3.5f;
                audioSource.dopplerLevel = 0f;
                audioSource.priority = 0;
                audioSource.bypassEffects = true;
                audioSource.bypassListenerEffects = true;
                audioSource.bypassReverbZones = true;
                ApplySpatialMode(initialParent, peerTransform);
                audioSource.clip = AudioClip.Create(
                    "Voice_PCM_" + sessionId,
                    VoiceNativeOpusCodec.SampleRate,
                    1,
                    VoiceNativeOpusCodec.SampleRate,
                    true,
                    FillPcm);

                EnsurePlayable();
            }

            public void UpdateSpatialTarget(Transform peerTransform)
            {
                Transform targetParent = ResolvePlaybackParent(peerTransform);
                if (targetParent != null && playbackObject.transform.parent != targetParent)
                {
                    playbackObject.transform.SetParent(targetParent, false);
                }

                playbackObject.transform.localPosition = Vector3.zero;
                ApplySpatialMode(targetParent, peerTransform);
                EnsurePlayable();
            }

            private Transform ResolvePlaybackParent(Transform peerTransform)
            {
                if (IsPlayableParent(peerTransform)) return peerTransform;
                if (IsPlayableParent(fallbackParent)) return fallbackParent;
                return null;
            }

            private void ApplySpatialMode(Transform resolvedParent, Transform peerTransform)
            {
                if (audioSource == null) return;

                // G.4 live validation: the server already enforces distance/session audibility.
                // Keep playback 2D and high-priority so Unity spatial distance, listener movement,
                // inactive peer transforms, or audio virtualization cannot create rhythmic volume pumping.
                audioSource.spatialBlend = 0f;
                audioSource.rolloffMode = AudioRolloffMode.Linear;
                audioSource.minDistance = 10000f;
                audioSource.maxDistance = 10000f;
                audioSource.priority = 0;
            }

            private static bool IsPlayableParent(Transform parent)
            {
                return parent != null && parent.gameObject.activeInHierarchy;
            }

            private void EnsurePlayable()
            {
                if (playbackObject == null || audioSource == null) return;
                if (!playbackObject.activeSelf) playbackObject.SetActive(true);
                if (!audioSource.enabled) audioSource.enabled = true;
                if (playbackObject.activeInHierarchy && !audioSource.isPlaying) audioSource.Play();
            }

            //* Ø§ÛŒÙ† ØªØ§Ø¨Ø¹ ÙØ±ÛŒÙ… Ø±Ø§ Decode Ùˆ Ø¨Ø§ Ø³Ù‚Ù Jitter Ù…Ø­Ø¯ÙˆØ¯ ÙˆØ§Ø±Ø¯ ØµÙ PCM Ù…ÛŒâ€ŒÚ©Ù†Ø¯.
            public void Enqueue(byte[] opusPacket)
            {
                if (opusPacket == null || opusPacket.Length == 0) return;
                EnsurePlayable();
                float[] pcm = codec.Decode(opusPacket);
                while (pcmFrames.Count >= MaximumQueuedFrames) pcmFrames.TryDequeue(out _);
                pcmFrames.Enqueue(pcm);
                if (buffering && pcmFrames.Count >= MinimumBufferedFramesBeforePlayback)
                {
                    buffering = false;
                }
            }

            public void SetAudible(bool audible)
            {
                audioSource.mute = !audible;
                if (!audible)
                {
                    while (pcmFrames.TryDequeue(out _)) { }
                    currentFrame = null;
                    currentOffset = 0;
                    buffering = true;
                }
                else
                {
                    EnsurePlayable();
                }
            }

            //* Ø§ÛŒÙ† Callback Ø¯Ø§Ø¯Ù‡ PCM Ù…ÙˆØ¬ÙˆØ¯ Ø±Ø§ Ø¨Ø¯ÙˆÙ† ØªØ®ØµÛŒØµ Unity Object Ø±ÙˆÛŒ Thread ØµÙˆØªÛŒ ØªØ­ÙˆÛŒÙ„ Ù…ÛŒâ€ŒØ¯Ù‡Ø¯.
            private void FillPcm(float[] data)
            {
                int output = 0;
                if (buffering)
                {
                    if (pcmFrames.Count < MinimumBufferedFramesBeforePlayback)
                    {
                        Array.Clear(data, 0, data.Length);
                        return;
                    }
                    buffering = false;
                }

                while (output < data.Length)
                {
                    if (currentFrame == null || currentOffset >= currentFrame.Length)
                    {
                        if (!pcmFrames.TryDequeue(out currentFrame))
                        {
                            currentFrame = null;
                            currentOffset = 0;
                            buffering = true;
                            FillFadeToSilence(data, output);
                            return;
                        }
                        currentOffset = 0;
                    }

                    int copy = Math.Min(data.Length - output, currentFrame.Length - currentOffset);
                    Array.Copy(currentFrame, currentOffset, data, output, copy);
                    currentOffset += copy;
                    output += copy;
                }
            }

            private static void FillFadeToSilence(float[] data, int start)
            {
                int remaining = data.Length - start;
                if (remaining <= 0) return;

                float seed = start > 0 ? data[start - 1] : 0f;
                int fade = Math.Min(FadeToSilenceSamples, remaining);
                for (int index = 0; index < fade; index++)
                {
                    float t = 1f - ((index + 1f) / fade);
                    data[start + index] = seed * t;
                }

                if (fade < remaining)
                {
                    Array.Clear(data, start + fade, remaining - fade);
                }
            }

            public void Dispose()
            {
                codec.Dispose();
                if (audioSource != null) audioSource.Stop();
                if (playbackObject != null) UnityEngine.Object.Destroy(playbackObject);
            }
        }
    }
}

/*
ØªÙˆØ¶ÛŒØ­ ÙØ§ÛŒÙ„:
Ø§ÛŒÙ† ÙØ§ÛŒÙ„ Ø¨Ø±Ø§ÛŒ Ù‡Ø± Session ÛŒÚ© AudioSource ÙØ¶Ø§ÛŒÛŒ Ø±ÙˆÛŒ Transform ÙØ¹Ø§Ù„ ÙØ±Ø³ØªÙ†Ø¯Ù‡ Ù…ÛŒâ€ŒØ³Ø§Ø²Ø¯ØŒ Opus Ø±Ø§ Decode Ùˆ Ø¨Ø§ Queue Ù…Ø­Ø¯ÙˆØ¯ Ù¾Ø®Ø´ Ù…ÛŒâ€ŒÚ©Ù†Ø¯. Ø§Ú¯Ø± Transform Ù‡Ù…ØªØ§ ØºÛŒØ±ÙØ¹Ø§Ù„ Ø¨Ø§Ø´Ø¯ØŒ AudioSource Ø²ÛŒØ± Root ÙØ¹Ø§Ù„ Voice Ø³Ø§Ø®ØªÙ‡ Ù…ÛŒâ€ŒØ´ÙˆØ¯ Ùˆ ØªØ§ Ø²Ù…Ø§Ù† Ù¾ÛŒØ¯Ø§ Ø´Ø¯Ù† Transform ÙØ¹Ø§Ù„ Ù‡Ù…ØªØ§ Ø¨Ù‡ Ø­Ø§Ù„Øª 2D audible fallback Ù…ÛŒâ€ŒØ±ÙˆØ¯ ØªØ§ ÙØ§ØµÙ„Ù‡â€ŒÛŒ Runtime Root Ø¨Ø§Ø¹Ø« Ø¨ÛŒâ€ŒØµØ¯Ø§ Ø´Ø¯Ù† Ø®Ø±ÙˆØ¬ÛŒ Ù†Ø´ÙˆØ¯. Ù†Ø³Ø®Ù‡ G.4 Stable2DBuffer Ø¨Ø±Ø§ÛŒ ØªØ³Øª Ø²Ù†Ø¯Ù‡ØŒ Ù¾Ø®Ø´ Voice Ø±Ø§ Ú©Ø§Ù…Ù„Ø§Ù‹ 2D Ùˆ high-priority Ù†Ú¯Ù‡ Ù…ÛŒâ€ŒØ¯Ø§Ø±Ø¯ ØªØ§ ÙØ§ØµÙ„Ù‡/Spatial/Listener Ø¨Ø§Ø¹Ø« Ø§ÙØª Ø­Ø¬Ù… Ù†Ø´ÙˆØ¯Ø› Ù‡Ù…Ú†Ù†ÛŒÙ† Prebuffer Ø±Ø§ Ø¨Ø²Ø±Ú¯â€ŒØªØ± Ù…ÛŒâ€ŒÚ©Ù†Ø¯ Ùˆ Ù‡Ù†Ú¯Ø§Ù… underflow Ø¨Ù‡ Ø¬Ø§ÛŒ Ù‚Ø·Ø¹ ØªÛŒØ²ØŒ Fade Ú©ÙˆØªØ§Ù‡ Ø¨Ù‡ Ø³Ú©ÙˆØª Ù…ÛŒâ€ŒØ¯Ù‡Ø¯.
*/


