#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.DedicatedGameServer.Client;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.Codec;
using Network_A.Voice.Client.Playback;
using Network_A.Voice.Client.Participants;
using Network_A.Voice.Client.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    // Production Windows bridge. Installed by VoiceClientRuntime.Initialize(),
    // never from an Editor menu. One microphone, one native Producer, many
    // authorized per-user Consumers, lifecycle-bound cleanup and fallback.
    public sealed class WindowsWebRtcVoiceBridge : MonoBehaviour
    {
        private sealed class StreamReader
        {
            private readonly Vme2WindowsWebRtcSfuSession session;
            private readonly string id;
            private int active = 1;
            // Fail closed until playback's local Speaker policy has been applied.
            // This gate runs on Unity's audio callback thread before spatialization.
            private int audioAllowed;
            private long suppressedSamples;
            internal long SuppressedSamples => Interlocked.Read(ref suppressedSamples);
            internal bool AudioAllowed => Volatile.Read(ref audioAllowed) != 0;
            internal void SetAudioAllowed(bool allowed)
            {
                Volatile.Write(ref audioAllowed, allowed ? 1 : 0);
            }
            private long samplesDelivered;
            private long samplesMissing;
            private long squareEnergyMicroUnits;
            private long nonSilentSamples;
            internal long SamplesDelivered => Interlocked.Read(ref samplesDelivered);
            internal long SamplesMissing => Interlocked.Read(ref samplesMissing);
            internal long SquareEnergyMicroUnits => Interlocked.Read(ref squareEnergyMicroUnits);
            internal long NonSilentSamples => Interlocked.Read(ref nonSilentSamples);
            internal StreamReader(Vme2WindowsWebRtcSfuSession session, string consumerId)
            { this.session = session; id = consumerId; }
            internal void Stop() { Interlocked.Exchange(ref active, 0); }
            internal void OnPcm(float[] samples)
            {
                int count = 0;
                if (Volatile.Read(ref active) != 0)
                {
                    try { count = session.ReadAudioPcm(id, samples); }
                    catch { Stop(); }
                }
                int valid = Math.Max(0, Math.Min(count, samples.Length));
                if (valid > 0)
                {
                    // Measure decoded audio, not just samples pulled by AudioClip.
                    // Atomic integral counters avoid allocating or locking the audio thread.
                    long energy = 0;
                    long nonSilent = 0;
                    for (int i = 0; i < valid; i++)
                    {
                        float value = samples[i];
                        energy += (long)(value * value * 1000000.0);
                        if (Math.Abs(value) > 0.003f) nonSilent++;
                    }
                    Interlocked.Add(ref squareEnergyMicroUnits, energy);
                    Interlocked.Add(ref nonSilentSamples, nonSilent);
                    Interlocked.Add(ref samplesDelivered, valid);
                    // AudioSource.mute alone is not sufficient protection when
                    // native HRTF/spatializer effects are present. A blocked
                    // Speaker must never feed nonzero PCM into the audio graph.
                    if (Volatile.Read(ref audioAllowed) == 0)
                    {
                        Array.Clear(samples, 0, valid);
                        Interlocked.Add(ref suppressedSamples, valid);
                    }
                }
                if (valid < samples.Length)
                {
                    Interlocked.Add(ref samplesMissing, samples.Length - valid);
                    Array.Clear(samples, valid, samples.Length - valid);
                }
            }
        }

        private sealed class Playback
        {
            internal Vme2WindowsWebRtcSfuSession.AudioPeer Peer;
            internal StreamReader Reader;
            internal GameObject Host;
            internal AudioSource Source;
            internal string UserId;
            internal long LastReportedDelivered;
            internal long LastReportedEnergy;
            internal long LastReportedNonSilent;
            internal long LastReportedSuppressed;
            internal bool? LastAppliedMuted;
        }

        private VoiceClientRuntime runtime;
        private VoiceMicrophonePublisher microphone;
        private VoiceNativeOpusCodec decodeExistingOpus;
        private Action<byte[], bool> originalMediaHandler;
        private bool originalMediaDetached;
        private bool wrapperSupported;
        private CancellationTokenSource cancellation;
        private Vme2WindowsWebRtcSfuSession session;
        private readonly Dictionary<string, Playback> consumers =
            new Dictionary<string, Playback>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> events = new ConcurrentQueue<string>();
        private readonly HashSet<string> mutedSenders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool muteAllIncoming;
        // Read the existing per-participant SpeakerEnabled flag without touching
        // its shared UI class. Apply it locally only after that UI accepted the
        // remote mute control request (its bool flips on successful send).
        private readonly Dictionary<string, bool> speakerEnabledByUser =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        // One global SFU Producer cannot restrict itself to one recipient.
        // If the existing per-user Mic control blocks an outgoing recipient,
        // restore the old directional-control route instead of bypassing it.
        private readonly Dictionary<string, bool> outgoingEnabledByUser =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private string uiControlRoom = string.Empty;
        private string uiControlConnection = string.Empty;
        private float nextSpeakerUiPollAt;
        private float nextMediaMetricsAt;
        private long pcmFramesPushed;
        private long pcmSamplesPushed;
        private long opusFramesDecoded;
        private long lastReportedPcmFrames;
        private bool starting;
        private bool refreshing;
        private bool productionReady;
        // Per-peer outgoing restrictions must never tear down recv transport.
        // With one global SFU producer, use legacy per-peer routing while its
        // native producer is paused; native Consumers remain uninterrupted.
        private bool directionalLegacyActive;
        private bool producerCreated;
        private bool retryPending;
        private float retryAt;
        private float nextPollAt;
        // A lost newProducer event must not delay the first Consumer by a full five seconds.
        // Only the first 12 seconds of a fresh SFU session get accelerated discovery.
        private float fastConsumerDiscoveryUntil;
        // Native ICE state 5 is DISCONNECTED (transient), unlike 4 FAILED and 6 CLOSED.
        // Briefly preserve the existing Consumer/Producer before rebuilding the owner.
        private float disconnectedIceSince = -1f;
        private int consecutiveNativeFailures;
        private string activeControlConnectionId = string.Empty;
        private string activeRoomId = string.Empty;
        private int shuttingDown;

        // Snapshot on Unity thread; the caller polls the session on a background worker.
        // New session/Consumer IDs identify the stats epoch after ReloadRoom or reconnect.
        public bool TryGetNativeRtpStatTargets(out Vme2WindowsWebRtcSfuSession activeSession,
            out string[] consumerIds)
        {
            activeSession = null;
            consumerIds = Array.Empty<string>();
            if (!productionReady || session == null || Volatile.Read(ref shuttingDown) != 0 ||
                string.IsNullOrEmpty(activeRoomId) || runtime == null || !runtime.IsAuthenticated ||
                !string.Equals(activeRoomId, MetaverseNetworkClient.roomId, StringComparison.Ordinal))
                return false;
            activeSession = session;
            var ids = new List<string>(consumers.Count);
            foreach (var item in consumers.Values)
                if (item != null && item.Peer != null &&
                    !string.IsNullOrWhiteSpace(item.Peer.ConsumerId))
                    ids.Add(item.Peer.ConsumerId);
            ids.Sort(StringComparer.Ordinal);
            consumerIds = ids.ToArray();
            return consumerIds.Length > 0;
        }

        public bool NativeProducerActive => productionReady && session != null &&
            Volatile.Read(ref shuttingDown) == 0;

        // Windows-only recovery verification. These are decoded playback samples,
        // NOT RTP packet-loss statistics and NOT proof of audible loudspeaker output.
        // Read only on the Unity main thread; the audio callback updates counters atomically.
        public int NativeReadyConsumerCount
        {
            get
            {
                int count = 0;
                foreach (Playback playing in consumers.Values)
                    if (playing.Source != null && playing.Reader != null) count++;
                return count;
            }
        }

        public long NativeReceivedPcmSampleCount
        {
            get
            {
                long total = 0;
                foreach (Playback playing in consumers.Values)
                    if (playing.Reader != null) total += playing.Reader.SamplesDelivered;
                return total;
            }
        }

        public long NativeReceivedNonSilentSampleCount
        {
            get
            {
                long total = 0;
                foreach (Playback playing in consumers.Values)
                    if (playing.Reader != null) total += playing.Reader.NonSilentSamples;
                return total;
            }
        }

        public void Initialize(VoiceClientRuntime owner, VoiceMicrophonePublisher publisher)
        {
            if (runtime != null) return;
            runtime = owner ?? throw new ArgumentNullException(nameof(owner));
            microphone = publisher ?? throw new ArgumentNullException(nameof(publisher));
            // Shared Capture and Runtime files are NEVER modified. Decode the
            // EXISTING encoded Opus frames to 48 kHz mono PCM for native WebRTC.
            // This does not open a second microphone device.
            try
            {
                decodeExistingOpus = new VoiceNativeOpusCodec(40);
                FieldInfo field = typeof(VoiceMicrophonePublisher).GetField("FrameEncoded",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var handlers = field == null ? null : field.GetValue(microphone) as Delegate;
                if (handlers != null)
                    foreach (Delegate handler in handlers.GetInvocationList())
                        if (ReferenceEquals(handler.Target, runtime) &&
                            handler.Method.Name == "HandleFrameEncoded")
                            originalMediaHandler = handler as Action<byte[], bool>;
                wrapperSupported = originalMediaHandler != null;
                if (wrapperSupported)
                {
                    microphone.FrameEncoded += OnEncodedFrame;
                    microphone.MuteChanged += OnMicrophoneMuted;
                    Debug.Log("VME2_WINDOWS_VOICE_MAIN=READY | mode=wrapper_only | microphone=existing | shared_files=untouched");
                }
                else Debug.LogWarning("VME2_WINDOWS_VOICE_MAIN=DISABLED | reason=original_media_handler_not_found | legacy_unchanged=true");
            }
            catch (Exception ex)
            {
                wrapperSupported = false;
                Debug.LogWarning("VME2_WINDOWS_VOICE_MAIN=DISABLED | reason=" + ex.Message);
            }
        }

        public bool HasConsumerForSender(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return false;
            foreach (Playback value in consumers.Values)
                if (value.Source != null && string.Equals(value.Peer.SenderConnectionId,
                    connectionId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void Update()
        {
            if (runtime == null || !wrapperSupported || Volatile.Read(ref shuttingDown) != 0) return;
            bool authenticated = runtime.IsAuthenticated &&
                Guid.TryParse(runtime.VoiceConnectionId, out _) &&
                IsVoiceBoundToCurrentDedicated() &&
                MetaverseNetworkClient.isReady &&
                !string.IsNullOrWhiteSpace(MetaverseNetworkClient.roomId);
            string currentRoom = authenticated ? MetaverseNetworkClient.roomId.Trim() : string.Empty;
            string currentConnection = authenticated ? runtime.VoiceConnectionId.Trim() : string.Empty;
            if (!string.Equals(uiControlRoom, currentRoom, StringComparison.Ordinal) ||
                !string.Equals(uiControlConnection, currentConnection, StringComparison.OrdinalIgnoreCase))
            {
                outgoingEnabledByUser.Clear();
                speakerEnabledByUser.Clear();
                uiControlRoom = currentRoom;
                uiControlConnection = currentConnection;
                nextSpeakerUiPollAt = 0f;
            }
            if (!authenticated || (session != null &&
                (!string.Equals(activeRoomId, currentRoom, StringComparison.Ordinal) ||
                 !string.Equals(activeControlConnectionId, currentConnection, StringComparison.OrdinalIgnoreCase))))
            {
                StopSession();
                return;
            }
            PollNativeSpeakerStateFromExistingUi();
            bool directionalRestriction = HasNativeOutgoingRestriction();
            // Sender-only policy: do NOT StopSession here. Keeping this session
            // alive is required so microphone and per-peer UI changes cannot
            // destroy the Native receivers and spatial AudioSources.
            if (session == null)
            {
                if (!starting && Time.unscaledTime >= retryAt)
                    _ = StartSessionAsync(currentRoom, currentConnection);
                return;
            }

            // Do not suppress the working legacy route until native ICE is
            // actually connected. A created Producer ID alone is insufficient.
            if (producerCreated)
            {
                try
                {
                    int ice = session.SendIceState;
                    if (ice == 2 || ice == 3)
                        disconnectedIceSince = -1f;
                    if (ice == 5)
                    {
                        if (disconnectedIceSince < 0f)
                            disconnectedIceSince = Time.unscaledTime;
                        // Keep receive transports during a short ICE disconnection.
                        // This avoids restarting a working SFU session on a brief flap.
                        if (Time.unscaledTime - disconnectedIceSince < 1.5f)
                            return;
                    }
                    if (ice == 4 || ice == 5 || ice == 6)
                    {
                        Debug.LogWarning("VME2_WINDOWS_VOICE_SEND=FALLBACK | ice=" + ice +
                            " | retryDelaySeconds=1.0 | failedIceCannotDeclareInternetOffline=true");
                        StopSession();
                        retryAt = Time.unscaledTime + 1f;
                        return;
                    }
                    // Per-recipient Mic OFF is enforced on the listener Consumer
                    // by the existing signed SFU lease, not on this shared Producer.
                    // Preserve native media and recording for other listeners.
                    if ((ice == 2 || ice == 3) && !productionReady)
                    {
                        // Detach legacy BEFORE enabling the native producer to
                        // avoid a window in which two paths publish voice.
                        if (!DetachOriginalMediaHandler())
                            throw new InvalidOperationException("Could not safely switch legacy media to native");
                        session.SetAudioProducerMuted(microphone.IsMuted);
                        bool resumedFromDirectional = directionalLegacyActive;
                        productionReady = true;
                        directionalLegacyActive = false;
                        Debug.Log("VME2_WINDOWS_VOICE_SEND=ACTIVE | ice=" + ice +
                            " | legacyMedia=false | micMuted=" + microphone.IsMuted +
                            " | nativeReceivePreserved=true" +
                            " | serverPerReceiverDenialRequired=" + directionalRestriction);
                        if (resumedFromDirectional)
                            Debug.Log("VME2_WINDOWS_VOICE_DIRECTIONAL_SEND=NATIVE_RESUMED" +
                                " | nativeReceivePreserved=true");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("VME2_WINDOWS_VOICE_SEND=FALLBACK | reason=" + ex.Message);
                    StopSession();
                    retryAt = Time.unscaledTime + 2f;
                    return;
                }
            }
            LogNativeMediaCounters();
            bool shouldRefresh = Time.unscaledTime >= nextPollAt;
            while (events.TryDequeue(out string eventName))
                if (eventName == "newProducer" || eventName == "producerClosed")
                    shouldRefresh = true;
            if (shouldRefresh && !refreshing)
            {
                // Initial authorized-producer discovery is latency-sensitive.
                // Bound faster polling to 12s; then return to normal 5s cadence.
                nextPollAt = Time.unscaledTime +
                    (Time.unscaledTime < fastConsumerDiscoveryUntil ? 1f : 5f);
                _ = RefreshConsumersAsync();
            }
            foreach (Playback playing in consumers.Values)
            {
                if (playing.Source == null) TryAttachPlayback(playing);
                else
                {
                    ApplyNativePlaybackGate(playing);
                    // Resolve new avatar after respawn or scene loading.
                    Transform target = ResolvePeerTransform(playing.UserId);
                    if (target != null && playing.Host != null &&
                        playing.Host.transform.parent != target)
                        playing.Host.transform.SetParent(target, false);
                }
            }
        }

        private async Task StartSessionAsync(string room, string connection)
        {
            starting = true;
            var cts = new CancellationTokenSource();
            Vme2WindowsWebRtcSfuSession pending = null;
            cancellation = cts;
            try
            {
                // ServerConfig.RealtimeWebSocketUrl is derived from GrpcWebEndpoint.
                // WindowsPlayer's dedicated bootstrap selects Native gRPC and leaves
                // GrpcWebEndpoint at its *local* default (localhost:8443). That makes
                // both the voice ticket HTTP POST and native WSS connect target localhost.
                // Resolve the public signaling endpoint from the already configured
                // dedicated gRPC-Web authority instead, without changing shared config.
                string signalingUrl = ServerConfig.RealtimeWebSocketUrl;
#if !UNITY_EDITOR
                if (!ServerConfigBootstrap.HasAppliedConfiguration)
                    throw new InvalidOperationException("WINDOWS_WEBRTC_SERVER_CONFIG_NOT_APPLIED");
                if (ServerConfigBootstrap.AppliedEnvironment ==
                    ServerConfigBootstrap.ServerEnvironment.Dedicated)
                    signalingUrl = ServerConfig.BuildRealtimeWebSocketUrl(
                        ServerConfig.DedicatedGrpcWebEndpoint,
                        ServerConfig.RealtimeWebSocketPath);
#endif
                if (!Uri.TryCreate(signalingUrl, UriKind.Absolute,
                    out Uri endpoint) || endpoint.Scheme != "wss")
                    throw new InvalidOperationException("Authenticated WSS SFU endpoint required");
#if !UNITY_EDITOR
                Debug.Log("VME2_WINDOWS_VOICE_SIGNALING_ENDPOINT=SELECTED" +
                    " | authority=" + endpoint.GetLeftPart(UriPartial.Authority) +
                    " | configEnvironment=" + ServerConfigBootstrap.AppliedEnvironment +
                    " | source=" + (ServerConfigBootstrap.AppliedEnvironment ==
                        ServerConfigBootstrap.ServerEnvironment.Dedicated
                        ? "dedicated_grpc_web" : "local_grpc_web") +
                    " | ticketAndWssSameAuthority=true");
#endif
                string token = SecureTokenStorage.GetAccessToken();
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Voice access token unavailable");
                string ticket = await RequestTicketAsync(endpoint, room, connection, token, cts.Token);
                if (!IsCurrent(cts, room, connection)) return;
                pending = new Vme2WindowsWebRtcSfuSession();
                await pending.ConnectAndLoadAsync(endpoint, room, connection, ticket, cts.Token);
                await pending.CreateTransportsAsync(cts.Token);
                if (!IsCurrent(cts, room, connection)) return;
                session = pending;
                pending = null;
                activeRoomId = room;
                activeControlConnectionId = connection;
                session.EventReceived += OnSfuEvent;
                // Produce before switching off legacy media: fail-open to the
                // already working game voice path, but never dual-publish.
                string producer = await session.StartAudioProducerAsync(cts.Token);
                if (!IsCurrent(cts, room, connection)) return;
                producerCreated = true;
                retryPending = false;
                consecutiveNativeFailures = 0;
                disconnectedIceSince = -1f;
                fastConsumerDiscoveryUntil = Time.unscaledTime + 12f;
                nextPollAt = 0f;
                Debug.Log("VME2_WIN_VOICE_FAST_MEDIA=CONSUMER_DISCOVERY_ARMED | windowSeconds=12 | initialPollSeconds=1");
                Debug.Log("VME2_WINDOWS_VOICE_MAIN=CONNECTED | producer=created" +
                    " | media=waiting_for_ice | micMuted=" + microphone.IsMuted +
                    " | room=" + room);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogWarning("VME2_WINDOWS_VOICE_MAIN=RETRY | stage=connect_or_produce" +
                    " | reason=" + ex.GetType().Name + " | detail=" + ex.Message);
                retryPending = true;
                StopSession();
                consecutiveNativeFailures = Math.Min(consecutiveNativeFailures + 1, 8);
                // A timed-out HTTP ticket after a HEALTH recovery should not
                // insert another unconditional 5s idle gap. Non-network failures
                // still have bounded backoff to avoid hammering the SFU endpoint.
                bool ticketTransportFailure = ex.Message != null &&
                    ex.Message.IndexOf("VOICE_WEBRTC_TICKET_HTTP_0_", StringComparison.Ordinal) >= 0;
                float retrySeconds = ticketTransportFailure
                    ? Mathf.Min(3f, 0.75f + 0.35f * (consecutiveNativeFailures - 1))
                    : Mathf.Min(6f, 1.5f + 0.75f * (consecutiveNativeFailures - 1));
                retryAt = Time.unscaledTime + retrySeconds;
                Debug.Log("VME2_WIN_VOICE_FAST_MEDIA=RETRY_SCHEDULED | seconds=" +
                    retrySeconds.ToString("F2") + " | transportFailure=" + ticketTransportFailure +
                    " | nativeFailures=" + consecutiveNativeFailures);
            }
            finally
            {
                pending?.Dispose();
                starting = false;
            }
        }

        private bool IsVoiceBoundToCurrentDedicated()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            var ws = DedicatedGameServerWsClient.Instance;
            if (runtime == null || ws == null || !ws.IsReady ||
                !string.Equals(ws.RoomId, MetaverseNetworkClient.roomId, StringComparison.Ordinal))
                return false;
            Guid control, dedicated;
            return Guid.TryParse(runtime.VoiceConnectionId, out control) &&
                Guid.TryParse(ws.ConnectionId, out dedicated) && control == dedicated;
#else
            return true;
#endif
        }

        private bool IsCurrent(CancellationTokenSource expected, string room, string connection)
        {
            return cancellation == expected && !expected.IsCancellationRequested &&
                runtime != null &&
                runtime.IsAuthenticated && IsVoiceBoundToCurrentDedicated() &&
                MetaverseNetworkClient.isReady &&
                string.Equals(MetaverseNetworkClient.roomId, room, StringComparison.Ordinal) &&
                string.Equals(runtime.VoiceConnectionId, connection, StringComparison.OrdinalIgnoreCase);
        }

        private void OnSfuEvent(string name, JObject data)
        {
            // SFU receive loop is on a worker. Never call Unity here.
            if (name == "newProducer" || name == "producerClosed") events.Enqueue(name);
        }

        private async Task RefreshConsumersAsync()
        {
            var current = session;
            var token = cancellation;
            if (current == null || token == null || refreshing) return;
            refreshing = true;
            try
            {
                string[] listed = await current.ListAuthorizedProducerIdsAsync(token.Token);
                if (current != session || token.IsCancellationRequested) return;
                var authorized = new HashSet<string>(listed, StringComparer.Ordinal);
                foreach (string existing in new List<string>(consumers.Keys))
                    if (!authorized.Contains(existing)) RemoveConsumer(existing);
                foreach (string id in listed)
                {
                    if (consumers.ContainsKey(id) || consumers.Count >= 16) continue;
                    var peer = await current.ConsumeAuthorizedAudioAsync(id, token.Token);
                    if (current != session || token.IsCancellationRequested) return;
                    consumers.Add(id, new Playback { Peer = peer,
                        Reader = new StreamReader(current, peer.ConsumerId) });
                    Debug.Log("VME2_WINDOWS_VOICE_CONSUMER=CONNECTED | authorized=true" +
                        " | sender=" + peer.SenderConnectionId);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogWarning("VME2_WINDOWS_VOICE_CONSUMER=RETRY | reason=" +
                    ex.GetType().Name + " | detail=" + ex.Message);
            }
            finally { refreshing = false; }
        }

        private void TryAttachPlayback(Playback playing)
        {
            if (playing == null || playing.Peer == null || playing.Reader == null || session == null)
                return;
            string userId = ResolveUserId(playing.Peer.SenderConnectionId);
            if (string.IsNullOrWhiteSpace(userId)) return; // fail closed on unbound identity
            Transform target = ResolvePeerTransform(userId);
            if (target == null) return;
            // Opus decoded by WebRTC at 48k. Don't play at the wrong sample rate.
            try
            {
                session.GetAudioPcmInfo(playing.Peer.ConsumerId,
                    out int rate, out ulong decodedFrames, out int buffered);
                if (rate != 48000 || decodedFrames == 0) return;
            }
            catch { return; }
            playing.UserId = userId;
            var host = new GameObject("Voice_Playback_Native_" + playing.Peer.ConsumerId);
            host.transform.SetParent(target, false);
            host.transform.localPosition = Vector3.zero;
            var audio = host.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.loop = true;
            // Reuse the SAME Windows spatial policy as existing PlaybackManager.
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 3f;
            audio.maxDistance = 3.5f;
            audio.dopplerLevel = 0f;
            audio.priority = 0;
            audio.bypassEffects = true;
            audio.bypassListenerEffects = true;
            audio.bypassReverbZones = true;
            Network_A.Voice.Client.Spatial.VoiceWindowsSpatialPlaybackPolicy.TryApply(
                audio, target, target);
            // Apply the local receive policy BEFORE AudioClip.Play() so
            // Speaker OFF remains silent even during initial attachment.
            var clip = AudioClip.Create("Voice_Native_" + playing.Peer.ConsumerId,
                48000 * 2, 1, 48000, true, playing.Reader.OnPcm);
            audio.clip = clip;
            playing.Host = host;
            playing.Source = audio;
            ApplyNativePlaybackGate(playing);
            audio.Play();
            Debug.Log("VME2_WINDOWS_VOICE_SPATIAL=ACTIVE | user=" + userId +
                " | sampleRate=48000 | receiver=native_webrtc");
        }

        // Existing in-game Mute All / per-user controls apply instantly to
        // the native AudioSources, independently of server control delivery.
        public void SetMuteAllIncoming(bool muted)
        {
            muteAllIncoming = muted;
            RefreshMuteStates();
        }

        public void SetPeerMuted(string connectionId, bool muted)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return;
            if (muted) mutedSenders.Add(connectionId);
            else mutedSenders.Remove(connectionId);
            RefreshMuteStates();
        }

        private void RefreshMuteStates()
        {
            foreach (Playback playing in consumers.Values)
                ApplyNativePlaybackGate(playing);
        }

        private void ApplyNativePlaybackGate(Playback playing)
        {
            if (playing == null) return;
            bool blocked = ShouldMuteNativePlayback(playing);
            // First silence the actual PCM callback. Then silence Unity's
            // AudioSource as a second independent safeguard against HRTF leaks.
            playing.Reader?.SetAudioAllowed(!blocked);
            if (playing.Source != null)
            {
                playing.Source.volume = blocked ? 0f : 1f;
                playing.Source.mute = blocked;
            }
            if (!playing.LastAppliedMuted.HasValue || playing.LastAppliedMuted.Value != blocked)
            {
                playing.LastAppliedMuted = blocked;
                Debug.Log("VME2_WINDOWS_VOICE_RX_OUTPUT=" + (blocked ? "SILENCED" : "AUDIBLE") +
                    " | user=" + (playing.UserId ?? string.Empty) +
                    " | speakerOff=" + (runtime != null && runtime.IsSpeakerOff) +
                    " | muteAll=" + muteAllIncoming +
                    " | peerBlocked=" + (playing.Peer != null &&
                        mutedSenders.Contains(playing.Peer.SenderConnectionId)) +
                    " | pcmGate=" + (blocked ? "ZERO" : "OPEN") +
                    " | sourceMute=" + blocked +
                    " | sourceVolume=" + (blocked ? "0" : "1") +
                    " | micStateIndependent=true");
            }
        }

        private bool ShouldMuteNativePlayback(Playback playing)
        {
            if (runtime == null || playing == null || playing.Peer == null) return true;
            if (runtime.IsSpeakerOff || muteAllIncoming ||
                mutedSenders.Contains(playing.Peer.SenderConnectionId)) return true;
            return !string.IsNullOrWhiteSpace(playing.UserId) &&
                speakerEnabledByUser.TryGetValue(playing.UserId, out bool enabled) && !enabled;
        }

        private void PollNativeSpeakerStateFromExistingUi()
        {
            if (Time.unscaledTime < nextSpeakerUiPollAt) return;
            nextSpeakerUiPollAt = Time.unscaledTime + 0.25f;
            // The existing VoiceParticipantListItemView is read-only. Never add
            // UI handlers or modify a shared controller / scene / Inspector.
            VoiceParticipantListItemView[] items =
                FindObjectsByType<VoiceParticipantListItemView>(FindObjectsSortMode.None);
            for (int i = 0; i < items.Length; i++)
            {
                VoiceParticipantListItemView item = items[i];
                if (item == null || string.IsNullOrWhiteSpace(item.UserId)) continue;
                bool enabled = item.SpeakerEnabled;
                if (speakerEnabledByUser.TryGetValue(item.UserId, out bool previous) &&
                    previous == enabled) continue;
                speakerEnabledByUser[item.UserId] = enabled;
                RefreshMuteStates();
                Debug.Log("VME2_WINDOWS_VOICE_PEER_SPEAKER=APPLIED" +
                    " | user=" + item.UserId + " | enabled=" + enabled +
                    " | source=existing_participant_ui | sharedFilesUntouched=true");
            }
            // Capture server-accepted outgoing per-user state from existing UI.
            // The wrapper never changes the shared item or its OnClick handler.
            for (int i = 0; i < items.Length; i++)
            {
                VoiceParticipantListItemView item = items[i];
                if (item == null || string.IsNullOrWhiteSpace(item.UserId)) continue;
                bool enabled = item.MicEnabled;
                if (outgoingEnabledByUser.TryGetValue(item.UserId, out bool previous) &&
                    previous == enabled) continue;
                outgoingEnabledByUser[item.UserId] = enabled;
                Debug.Log("VME2_WINDOWS_VOICE_PEER_OUTGOING=OBSERVED" +
                    " | user=" + item.UserId + " | enabled=" + enabled +
                    " | nativePolicy=" + (enabled ? "normal" : "server_per_receiver_denial") +
                    " | nativeProducerPauseRequested=false" +
                    " | serverEnforcement=unverified");
            }
        }

        private bool HasNativeOutgoingRestriction()
        {
            foreach (bool enabled in outgoingEnabledByUser.Values)
                if (!enabled) return true;
            return false;
        }

        private void LogNativeMediaCounters()
        {
            if (session == null || Time.unscaledTime < nextMediaMetricsAt) return;
            nextMediaMetricsAt = Time.unscaledTime + 5f;
            long sentFrames = Interlocked.Read(ref pcmFramesPushed);
            long sentSamples = Interlocked.Read(ref pcmSamplesPushed);
            long decodedOpusFrames = Interlocked.Read(ref opusFramesDecoded);
            long delta = sentFrames - lastReportedPcmFrames;
            lastReportedPcmFrames = sentFrames;
            bool micOn = microphone != null && !microphone.IsMuted;
            Debug.Log("VME2_WINDOWS_VOICE_PCM_SEND=" +
                (micOn && sentFrames > 0 ? "PASS" : "OBSERVED") +
                " | pushedFrames=" + sentFrames +
                " | pushedSamples=" + sentSamples +
                " | lastIntervalFrames=" + delta +
                " | decodedOpusFrames=" + decodedOpusFrames +
                " | micMuted=" + !micOn +
                " | sendRoute=" + (productionReady ? "native" :
                    (directionalLegacyActive ? "legacy_directional" : "pending")) +
                " | mediaPath=unity_opus_decode_to_native_pcm" +
                " | remoteAudibility=unverified");
            foreach (Playback playing in consumers.Values)
            {
                if (playing.Reader == null) continue;
                long delivered = playing.Reader.SamplesDelivered;
                long energy = playing.Reader.SquareEnergyMicroUnits;
                long nonSilent = playing.Reader.NonSilentSamples;
                long deltaDelivered = Math.Max(0, delivered - playing.LastReportedDelivered);
                long deltaEnergy = Math.Max(0, energy - playing.LastReportedEnergy);
                long deltaNonSilent = Math.Max(0, nonSilent - playing.LastReportedNonSilent);
                playing.LastReportedDelivered = delivered;
                playing.LastReportedEnergy = energy;
                playing.LastReportedNonSilent = nonSilent;
                long suppressed = playing.Reader.SuppressedSamples;
                long deltaSuppressed = Math.Max(0, suppressed - playing.LastReportedSuppressed);
                playing.LastReportedSuppressed = suppressed;
                double rms = deltaDelivered == 0 ? 0.0 :
                    Math.Sqrt(deltaEnergy / (1000000.0 * deltaDelivered));
                ulong nativeDecodedFrames = 0;
                try
                {
                    session.GetAudioPcmInfo(playing.Peer.ConsumerId,
                        out int _, out nativeDecodedFrames, out int __);
                }
                catch { /* Continue reporting decoded audio already delivered. */ }
                Debug.Log("VME2_WINDOWS_VOICE_PCM_RECEIVE=" +
                    (delivered > 0 ? "PASS" : "OBSERVED") +
                    " | user=" + (playing.UserId ?? string.Empty) +
                    " | deliveredSamples=" + delivered +
                    " | intervalSamples=" + deltaDelivered +
                    " | missingSamples=" + playing.Reader.SamplesMissing +
                    " | nativeDecodedFrames=" + nativeDecodedFrames +
                    " | intervalRms=" + rms.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) +
                    " | nonSilentSamples=" + deltaNonSilent +
                    " | pcmOutputSilencedSamples=" + deltaSuppressed +
                    " | pcmGate=" + (playing.Reader.AudioAllowed ? "OPEN" : "ZERO") +
                    " | sourceVolume=" + (playing.Source == null ? "unbound" :
                        playing.Source.volume.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)) +
                    " | micMuted=" + (microphone != null && microphone.IsMuted) +
                    " | muted=" + ShouldMuteNativePlayback(playing) +
                    " | spatial=" + (playing.Source != null) +
                    " | audible=unverified");
            }
        }

        private bool DetachOriginalMediaHandler()
        {
            if (originalMediaDetached) return true;
            if (originalMediaHandler == null || microphone == null) return false;
            microphone.FrameEncoded -= originalMediaHandler;
            originalMediaDetached = true;
            Debug.Log("VME2_WINDOWS_VOICE_LEGACY_MEDIA=SUPPRESSED | original_source_file=untouched");
            return true;
        }

        private void RestoreOriginalMediaHandler()
        {
            if (!originalMediaDetached) return;
            originalMediaDetached = false;
            if (microphone != null && originalMediaHandler != null)
            {
                microphone.FrameEncoded += originalMediaHandler;
                Debug.Log("VME2_WINDOWS_VOICE_LEGACY_MEDIA=RESTORED | fallback=true");
            }
        }

        private void OnEncodedFrame(byte[] packet, bool dtx)
        {
            if (!NativeProducerActive || microphone == null || microphone.IsMuted ||
                decodeExistingOpus == null || packet == null || packet.Length == 0) return;
            try
            {
                float[] pcm = decodeExistingOpus.Decode(packet);
                if (pcm != null && pcm.Length > 0)
                {
                    Interlocked.Increment(ref opusFramesDecoded);
                    session.PushAudioPcm(pcm, VoiceNativeOpusCodec.SampleRate);
                    Interlocked.Increment(ref pcmFramesPushed);
                    Interlocked.Add(ref pcmSamplesPushed, pcm.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("VME2_WINDOWS_VOICE_SEND=FALLBACK | reason=" + ex.Message);
                StopSession();
                retryAt = Time.unscaledTime + 5f;
            }
        }

        // Adapter onto the existing runtime's private session mapping: the
        // class is untouched; unknown identities fail closed (never misroute voice).
        private string ResolveUserId(string senderConnectionId)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(senderConnectionId)) return null;
            try
            {
                FieldInfo field = typeof(VoiceClientRuntime).GetField("sessions",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var active = field?.GetValue(runtime) as IDictionary;
                if (active == null) return null;
                foreach (DictionaryEntry entry in active)
                {
                    object value = entry.Value;
                    MethodInfo resolver = value?.GetType().GetMethod("TryResolvePeerUserId",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (resolver == null) continue;
                    object[] args = { senderConnectionId, null };
                    if ((bool)resolver.Invoke(value, args) && args[1] is string id &&
                        !string.IsNullOrWhiteSpace(id)) return id;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("VME2_WINDOWS_VOICE_PEER_MAPPING=SKIPPED | " + ex.GetType().Name);
            }
            return null;
        }

        private Transform ResolvePeerTransform(string userId)
        {
            if (runtime == null || string.IsNullOrWhiteSpace(userId)) return null;
            try
            {
                var manager = runtime.GetComponent<VoiceSpatialPlaybackManager>();
                if (manager == null) return null;
                MethodInfo resolver = typeof(VoiceSpatialPlaybackManager).GetMethod("FindPeerTransform",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                return resolver?.Invoke(manager, new object[] { userId }) as Transform;
            }
            catch { return null; }
        }

        private void OnMicrophoneMuted(bool muted)
        {
            if (!NativeProducerActive) return;
            try { session.SetAudioProducerMuted(muted); }
            catch (Exception ex)
            {
                Debug.LogWarning("VME2_WINDOWS_VOICE_MUTE=FALLBACK | reason=" + ex.Message);
                productionReady = false;
                StopSession();
                retryAt = Time.unscaledTime + 5f;
            }
        }

        private void RemoveConsumer(string producerId)
        {
            if (!consumers.TryGetValue(producerId, out Playback playing)) return;
            consumers.Remove(producerId);
            playing.Reader?.Stop();
            if (playing.Source != null)
            {
                playing.Source.Stop();
                playing.Source.clip = null;
            }
            if (playing.Host != null) Destroy(playing.Host);
            try { session?.CloseAudioConsumer(playing.Peer.ConsumerId); }
            catch (Exception ex)
            { Debug.LogWarning("VME2_WINDOWS_VOICE_CONSUMER_CLOSE=CHECK | " + ex.Message); }
        }

        private void StopSession()
        {
            productionReady = false;
            RestoreOriginalMediaHandler(); // restore original route BEFORE native disposal
            producerCreated = false;
            directionalLegacyActive = false;
            disconnectedIceSince = -1f;
            fastConsumerDiscoveryUntil = 0f;
            var token = cancellation;
            cancellation = null;
            try { token?.Cancel(); } catch { }
            foreach (string id in new List<string>(consumers.Keys)) RemoveConsumer(id);
            var previous = session;
            session = null;
            if (previous != null)
            {
                previous.EventReceived -= OnSfuEvent;
                try { previous.Dispose(); }
                catch (Exception ex)
                { Debug.LogWarning("VME2_WINDOWS_VOICE_CLEANUP=CHECK | " + ex.Message); }
            }
            token?.Dispose();
            activeRoomId = string.Empty;
            activeControlConnectionId = string.Empty;
            speakerEnabledByUser.Clear();
            nextSpeakerUiPollAt = 0f;
            nextMediaMetricsAt = 0f;
            Interlocked.Exchange(ref pcmFramesPushed, 0);
            Interlocked.Exchange(ref pcmSamplesPushed, 0);
            Interlocked.Exchange(ref opusFramesDecoded, 0);
            lastReportedPcmFrames = 0;
            while (events.TryDequeue(out _)) { }
        }

        public void Shutdown()
        {
            if (Interlocked.Exchange(ref shuttingDown, 1) != 0) return;
            if (microphone != null)
            {
                microphone.FrameEncoded -= OnEncodedFrame;
                microphone.MuteChanged -= OnMicrophoneMuted;
            }
            StopSession();
            decodeExistingOpus?.Dispose();
            decodeExistingOpus = null;
        }

        private void OnDisable() { if (Application.isPlaying) StopSession(); }
        private void OnDestroy() { Shutdown(); }

        private static async Task<string> RequestTicketAsync(Uri endpoint, string room,
            string connection, string access, CancellationToken cancel)
        {
            string origin = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port).Uri
                .GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var uri = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port,
                "/game-server-control/client/voice-webrtc-ticket").Uri;
#if !UNITY_EDITOR
            // The WindowsPlayer's game-server ticket POST already uses
            // UnityWebRequest successfully. System.Net.Http.HttpClient fails
            // before receiving an HTTP status on this exact Standalone build.
            // Use the same Unity native HTTP stack for WebRTC auth tickets.
            // Do not override Origin: the SFU ticket endpoint explicitly allows
            // a missing Origin and Unity may reject/override that header.
            using (var request = new UnityWebRequest(uri.AbsoluteUri, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(new JObject
                {
                    ["roomId"] = room,
                    ["controlConnectionId"] = connection
                }.ToString(Formatting.None)));
                request.downloadHandler = new DownloadHandlerBuffer();
                // A dead ticket HTTP attempt can otherwise hold the native
                // path for 12s, followed by an additional 5s retry gap.
                // Six seconds bounds a failed attempt without changing HEALTH.
                request.timeout = 6;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + access);
                request.SetRequestHeader("X-Metaverse-Client", "unity");
                cancel.ThrowIfCancellationRequested();
                var operation = request.SendWebRequest();
                try
                {
                    while (!operation.isDone)
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            request.Abort();
                            cancel.ThrowIfCancellationRequested();
                        }
                        await Task.Yield();
                    }
                    cancel.ThrowIfCancellationRequested();
                    if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                    {
                        string cause = request.error ?? "unknown";
                        // Never log the response body or access/voice ticket.
                        string code = "none";
                        try
                        {
                            var errorText = request.downloadHandler == null
                                ? string.Empty : request.downloadHandler.text;
                            if (!string.IsNullOrEmpty(errorText))
                                code = JObject.Parse(errorText).Value<string>("error") ?? "none";
                        }
                        catch { code = "unparseable"; }
                        Debug.LogWarning("VME2_WINDOWS_VOICE_TICKET_HTTP=FAIL | transport=unitywebrequest" +
                            " | status=" + request.responseCode + " | result=" + request.result +
                            " | serverError=" + code + " | networkError=" + cause);
                        throw new InvalidOperationException("VOICE_WEBRTC_TICKET_HTTP_" +
                            request.responseCode + "_" + request.result);
                    }
                    var data = JObject.Parse(request.downloadHandler.text);
                    string ticket = data.Value<string>("ticket");
                    if (data.Value<bool?>("ok") != true || string.IsNullOrWhiteSpace(ticket))
                        throw new InvalidOperationException("SFU voice ticket rejected");
                    Debug.Log("VME2_WINDOWS_VOICE_TICKET_HTTP=PASS | transport=unitywebrequest" +
                        " | status=200 | secretLogged=false");
                    return ticket;
                }
                catch (OperationCanceledException)
                {
                    request.Abort();
                    throw;
                }
            }
#else
            // Keep the previously verified Unity Editor path unchanged.
            using (var http = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                request.Headers.TryAddWithoutValidation("Origin", origin);
                request.Content = new StringContent(new JObject
                {
                    ["roomId"] = room,
                    ["controlConnectionId"] = connection
                }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, cancel))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("VOICE_WEBRTC_TICKET_HTTP_" +
                            (int)response.StatusCode);
                    var data = JObject.Parse(await response.Content.ReadAsStringAsync());
                    string ticket = data.Value<string>("ticket");
                    if (data.Value<bool?>("ok") != true || string.IsNullOrWhiteSpace(ticket))
                        throw new InvalidOperationException("SFU voice ticket rejected");
                    return ticket;
                }
            }
#endif
        }
    }
}
#endif
