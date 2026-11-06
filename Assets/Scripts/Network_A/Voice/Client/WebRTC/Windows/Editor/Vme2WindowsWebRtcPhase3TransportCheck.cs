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
    // Manual, non-media check; existing runtime and SFU join test remain untouched.
    internal static class Vme2WindowsWebRtcPhase3TransportCheck
    {
        private const int TimeoutMs = 30000;
        private static CancellationTokenSource runningCancellation;

        [MenuItem("VME2/WebRTC Windows/Check Phase 3 Native Transports")]
        private static async void CheckTransports()
        {
            if (runningCancellation != null)
            {
                Debug.LogWarning("VME2_WINDOWS_PHASE3_TRANSPORT=SKIPPED | reason=already_running");
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("VME2_WINDOWS_PHASE3_TRANSPORT=SKIPPED | reason=enter_play_mode_and_join_room_first");
                return;
            }
            using (var timeout = new CancellationTokenSource(TimeoutMs))
            {
                runningCancellation = timeout;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                try
                {
                    string address = ServerConfig.RealtimeWebSocketUrl;
                    if (!Uri.TryCreate(address, UriKind.Absolute, out Uri endpoint) ||
                        !string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Realtime endpoint must be WSS");

                    VoiceClientRuntime runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                    if (runtime == null || !runtime.IsAuthenticated ||
                        !Guid.TryParse(runtime.VoiceConnectionId, out _))
                        throw new InvalidOperationException("Authenticated in-room Voice control is required");
                    if (!MetaverseNetworkClient.isReady)
                        throw new InvalidOperationException("Dedicated game session is not ready");

                    string connectionId = runtime.VoiceConnectionId.Trim().ToLowerInvariant();
                    string roomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
                    string token = SecureTokenStorage.GetAccessToken();
                    if (roomId.Length == 0 || string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("Active room and token are required");

                    string ticket = await RequestTicketAsync(endpoint, roomId, connectionId,
                        token, timeout.Token);
                    if (!EditorApplication.isPlaying || runtime == null || !runtime.IsAuthenticated ||
                        !string.Equals(connectionId, runtime.VoiceConnectionId,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Voice control changed during ticket acquisition");

                    using (var session = new Vme2WindowsWebRtcSfuSession())
                    {
                        await session.ConnectAndLoadAsync(endpoint, roomId, connectionId,
                            ticket, timeout.Token);
                        if (!EditorApplication.isPlaying || !runtime.IsAuthenticated ||
                            !string.Equals(connectionId, runtime.VoiceConnectionId,
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Voice control changed before transport creation");
                        await session.CreateTransportsAsync(timeout.Token);
                        if (!session.HasSendTransport || !session.HasRecvTransport)
                            throw new InvalidOperationException("Native Transport retention validation failed");
                        Debug.Log("VME2_WINDOWS_PHASE3_TRANSPORT=PASS | send=created | recv=created" +
                                  " | dtls=not_connected | media=not_started");
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.LogError("VME2_WINDOWS_PHASE3_TRANSPORT=FAIL | reason=timeout_or_cancelled");
                }
                catch (EntryPointNotFoundException)
                {
                    Debug.LogError("VME2_WINDOWS_PHASE3_TRANSPORT=FAIL | reason=native_dll_not_rebuilt");
                }
                catch (Exception exception)
                {
                    Debug.LogError("VME2_WINDOWS_PHASE3_TRANSPORT=FAIL | reason=" +
                        exception.GetType().Name + " | detail=" + exception.Message);
                }
                finally
                {
                    EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                    runningCancellation = null;
                }
            }
        }

        [MenuItem("VME2/WebRTC Windows/Check Phase 3 Receive Audio")]
        private static async void CheckReceiveAudio()
        {
            if (runningCancellation != null)
            {
                Debug.LogWarning("VME2_WINDOWS_PHASE3_RECEIVE=SKIPPED | reason=already_running");
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("VME2_WINDOWS_PHASE3_RECEIVE=SKIPPED | reason=enter_play_mode_and_join_room_first");
                return;
            }
            using (var timeout = new CancellationTokenSource(45000))
            {
                runningCancellation = timeout;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                try
                {
                    string address = ServerConfig.RealtimeWebSocketUrl;
                    if (!Uri.TryCreate(address, UriKind.Absolute, out Uri endpoint) ||
                        !string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Realtime endpoint must be WSS");
                    VoiceClientRuntime runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                    if (runtime == null || !runtime.IsAuthenticated ||
                        !Guid.TryParse(runtime.VoiceConnectionId, out _) || !MetaverseNetworkClient.isReady)
                        throw new InvalidOperationException("Authenticated in-room Voice session required");
                    string connectionId = runtime.VoiceConnectionId.Trim().ToLowerInvariant();
                    string roomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
                    string token = SecureTokenStorage.GetAccessToken();
                    if (roomId.Length == 0 || string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("Room and access token are required");
                    string ticket = await RequestTicketAsync(endpoint, roomId, connectionId,
                        token, timeout.Token);
                    if (!EditorApplication.isPlaying || !runtime.IsAuthenticated ||
                        !string.Equals(connectionId, runtime.VoiceConnectionId,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Voice session changed before test");
                    using (var session = new Vme2WindowsWebRtcSfuSession())
                    {
                        await session.ConnectAndLoadAsync(endpoint, roomId, connectionId,
                            ticket, timeout.Token);
                        await session.CreateTransportsAsync(timeout.Token);
                        string consumerId = await session.ConsumeFirstAuthorizedAudioAsync(timeout.Token);
                        if (string.IsNullOrEmpty(consumerId))
                        {
                            Debug.Log("VME2_WINDOWS_PHASE3_RECEIVE=SKIPPED | reason=no_authorized_producer");
                            return;
                        }
                        await Task.Delay(3000, timeout.Token);
                        int ice = session.RecvIceState;
                        bool connected = ice == 2 || ice == 3;
                        Debug.Log("VME2_WINDOWS_PHASE3_RECEIVE=" + (connected ? "PASS" : "CHECK") +
                            " | consumer=created | server=resumed | recv_ice=" + ice +
                            " | audio_playback=unverified");
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.LogError("VME2_WINDOWS_PHASE3_RECEIVE=FAIL | reason=timeout_or_cancelled");
                }
                catch (Exception exception)
                {
                    Debug.LogError("VME2_WINDOWS_PHASE3_RECEIVE=FAIL | reason=" +
                        exception.GetType().Name + " | detail=" + exception.Message);
                }
                finally
                {
                    EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                    runningCancellation = null;
                }
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                runningCancellation?.Cancel();
        }

        private static async Task<string> RequestTicketAsync(Uri endpoint, string roomId,
            string connectionId, string token, CancellationToken cancellationToken)
        {
            var origin = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port).Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var api = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port,
                "/game-server-control/client/voice-webrtc-ticket").Uri;
            using (var http = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, api))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Origin", origin);
                request.Content = new StringContent(new JObject
                {
                    ["roomId"] = roomId,
                    ["controlConnectionId"] = connectionId
                }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await http.SendAsync(request, cancellationToken))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("SFU_TICKET_HTTP_" + (int)response.StatusCode);
                    JObject result = JObject.Parse(await response.Content.ReadAsStringAsync());
                    string ticket = result.Value<string>("ticket");
                    if (result.Value<bool?>("ok") != true || string.IsNullOrWhiteSpace(ticket))
                        throw new InvalidOperationException("SFU ticket request rejected");
                    return ticket;
                }
            }
        }
    }
}
#endif
