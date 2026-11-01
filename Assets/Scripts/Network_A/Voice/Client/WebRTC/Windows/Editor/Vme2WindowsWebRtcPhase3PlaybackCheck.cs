#if UNITY_EDITOR_WIN
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Client.WebRTC.Windows.Editor
{
    // Independent manual audio playback check; leaves the existing Voice route untouched.
    internal static class Vme2WindowsWebRtcPhase3PlaybackCheck
    {
        private static CancellationTokenSource lifetime;
        private static Vme2WindowsWebRtcSfuSession session;
        private static GameObject audioHost;
        private static AudioSource audioSource;
        private static AudioClip audioClip;
        private static PcmReader reader;
        private static string consumerId;

        private sealed class PcmReader
        {
            private readonly Vme2WindowsWebRtcSfuSession source;
            private readonly string id;
            private int active = 1;
            private long delivered;
            private long underflows;

            internal PcmReader(Vme2WindowsWebRtcSfuSession source, string id)
            {
                this.source = source;
                this.id = id;
            }

            internal long Delivered => Interlocked.Read(ref delivered);
            internal long Underflows => Interlocked.Read(ref underflows);
            internal void Stop() { Interlocked.Exchange(ref active, 0); }

            internal void Read(float[] output)
            {
                if (output == null || output.Length == 0) return;
                int count = 0;
                if (Volatile.Read(ref active) == 1)
                {
                    try { count = source.ReadAudioPcm(id, output); }
                    catch { Stop(); }
                }
                if (count < output.Length)
                {
                    Array.Clear(output, Math.Max(0, count), output.Length - Math.Max(0, count));
                    Interlocked.Increment(ref underflows);
                }
                if (count > 0) Interlocked.Add(ref delivered, count);
            }
        }

        [MenuItem("VME2/WebRTC Windows/Start Phase 3 Receive Playback")]
        private static async void Start()
        {
            if (lifetime != null)
            {
                Debug.LogWarning("VME2_WINDOWS_PHASE3_PLAYBACK=SKIPPED | reason=already_running");
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("VME2_WINDOWS_PHASE3_PLAYBACK=SKIPPED | reason=enter_play_mode_and_join_room_first");
                return;
            }

            lifetime = new CancellationTokenSource();
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            try
            {
                using (var startupTimeout = new CancellationTokenSource(45000))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    lifetime.Token, startupTimeout.Token))
                {
                    var token = linked.Token;
                    if (!Uri.TryCreate(ServerConfig.RealtimeWebSocketUrl, UriKind.Absolute, out Uri endpoint) ||
                        !string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Realtime endpoint must be WSS");

                    var runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                    if (runtime == null || !runtime.IsAuthenticated ||
                        !Guid.TryParse(runtime.VoiceConnectionId, out _) || !MetaverseNetworkClient.isReady)
                        throw new InvalidOperationException("Authenticated in-room Voice session required");
                    string connection = runtime.VoiceConnectionId.Trim().ToLowerInvariant();
                    string room = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
                    string access = SecureTokenStorage.GetAccessToken();
                    if (room.Length == 0 || string.IsNullOrWhiteSpace(access))
                        throw new InvalidOperationException("Room and access token are required");

                    string ticket = await RequestTicketAsync(endpoint, room, connection, access, token);
                    if (!EditorApplication.isPlaying || !runtime.IsAuthenticated ||
                        !string.Equals(runtime.VoiceConnectionId, connection, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Voice control session changed during ticket acquisition");

                    session = new Vme2WindowsWebRtcSfuSession();
                    await session.ConnectAndLoadAsync(endpoint, room, connection, ticket, token);
                    await session.CreateTransportsAsync(token);
                    consumerId = await session.ConsumeFirstAuthorizedAudioAsync(token);
                    if (string.IsNullOrEmpty(consumerId))
                    {
                        Debug.LogWarning("VME2_WINDOWS_PHASE3_PLAYBACK=SKIPPED | reason=no_authorized_producer");
                        return;
                    }

                    int rate = 0, buffered = 0;
                    ulong decoded = 0;
                    for (int i = 0; i < 75 && decoded == 0; ++i)
                    {
                        token.ThrowIfCancellationRequested();
                        session.GetAudioPcmInfo(consumerId, out rate, out decoded, out buffered);
                        if (decoded == 0) await Task.Delay(150, token);
                    }
                    if (decoded == 0 || rate < 8000 || rate > 96000)
                    {
                        Debug.LogWarning("VME2_WINDOWS_PHASE3_PLAYBACK=CHECK | reason=no_decoded_pcm" +
                            " | recv_ice=" + session.RecvIceState + " | decoded_frames=" + decoded);
                        return;
                    }

                    reader = new PcmReader(session, consumerId);
                    audioHost = new GameObject("VME2_Phase3_Native_Receive_Preview");
                    audioSource = audioHost.AddComponent<AudioSource>();
                    audioSource.playOnAwake = false;
                    audioSource.loop = true;
                    audioSource.spatialBlend = 0f;
                    audioSource.volume = 1f;
                    audioClip = AudioClip.Create("VME2 Phase3 Native WebRTC Mono", rate * 2,
                        1, rate, true, reader.Read);
                    audioSource.clip = audioClip;
                    audioSource.Play();
                    Debug.Log("VME2_WINDOWS_PHASE3_PLAYBACK=STARTED | consumer=created" +
                        " | recv_ice=" + session.RecvIceState + " | pcm_rate=" + rate +
                        " | decoded_frames=" + decoded + " | audible=unverified");
                }

                await Task.Delay(5000, lifetime.Token);
                if (reader != null)
                {
                    session.GetAudioPcmInfo(consumerId, out int rate,
                        out ulong decoded, out int buffered);
                    long delivered = reader.Delivered;
                    Debug.Log("VME2_WINDOWS_PHASE3_PCM_DELIVERY=" + (delivered > 0 ? "PASS" : "CHECK") +
                        " | decoded_frames=" + decoded + " | delivered_samples=" + delivered +
                        " | buffered_samples=" + buffered + " | underflows=" + reader.Underflows +
                        " | pcm_rate=" + rate + " | audible=unverified");
                }
                await Task.Delay(Timeout.Infinite, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                if (lifetime != null && !lifetime.IsCancellationRequested)
                    Debug.LogWarning("VME2_WINDOWS_PHASE3_PLAYBACK=CHECK | reason=startup_timeout");
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_WINDOWS_PHASE3_PLAYBACK=FAIL | reason=" +
                    exception.GetType().Name + " | detail=" + exception.Message);
            }
            finally
            {
                Cleanup();
            }
        }

        [MenuItem("VME2/WebRTC Windows/Stop Phase 3 Receive Playback")]
        private static void Stop()
        {
            if (lifetime == null) return;
            lifetime.Cancel();
            Debug.Log("VME2_WINDOWS_PHASE3_PLAYBACK=STOP_REQUESTED");
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode) lifetime?.Cancel();
        }

        private static void Cleanup()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            reader?.Stop();
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = null;
            }
            if (audioHost != null)
            {
                if (EditorApplication.isPlaying) UnityEngine.Object.Destroy(audioHost);
                else UnityEngine.Object.DestroyImmediate(audioHost);
            }
            audioHost = null;
            audioSource = null;
            audioClip = null;
            reader = null;
            consumerId = null;
            session?.Dispose();
            session = null;
            lifetime?.Dispose();
            lifetime = null;
            Debug.Log("VME2_WINDOWS_PHASE3_PLAYBACK=STOPPED | resources=released");
        }

        private static async Task<string> RequestTicketAsync(Uri endpoint, string room,
            string controlConnection, string token, CancellationToken cancellationToken)
        {
            var origin = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port).Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var address = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port,
                "/game-server-control/client/voice-webrtc-ticket").Uri;
            using (var http = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, address))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Origin", origin);
                request.Content = new StringContent(new JObject
                {
                    ["roomId"] = room,
                    ["controlConnectionId"] = controlConnection
                }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, cancellationToken))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("SFU_TICKET_HTTP_" + (int)response.StatusCode);
                    JObject json = JObject.Parse(await response.Content.ReadAsStringAsync());
                    string ticket = json.Value<string>("ticket");
                    if (json.Value<bool?>("ok") != true || string.IsNullOrWhiteSpace(ticket))
                        throw new InvalidOperationException("SFU ticket request rejected");
                    return ticket;
                }
            }
        }
    }
}
#endif
