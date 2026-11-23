#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Network_A.Voice.Client.WebRTC.Windows;

namespace Network_A.Voice.Client.WebRTC.Windows.Editor
{
    // A real authorized SFU signalling join from the existing in-room Unity session.
    // This check is explicitly invoked from the Editor menu. It does not start media,
    // create SFU transports, alter existing audio or own reconnect behavior.
    internal static class Vme2WindowsWebRtcSfuJoinCheck
    {
        private const int TimeoutMs = 18000;
        private const int MaxMessageBytes = 1024 * 1024;
        private static bool running;

        [MenuItem("VME2/WebRTC Windows/Check Live SFU Join")]
        private static async void CheckLiveSfuJoin()
        {
            if (running)
            {
                Debug.LogWarning("VME2_WINDOWS_SFU_JOIN=SKIPPED | reason=already_running");
                return;
            }
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("VME2_WINDOWS_SFU_JOIN=SKIPPED | reason=enter_play_mode_and_join_room_first");
                return;
            }
            running = true;
            using (var timeout = new CancellationTokenSource(TimeoutMs))
            {
                try
                {
                    string endpointString = ServerConfig.RealtimeWebSocketUrl;
                    if (!Uri.TryCreate(endpointString, UriKind.Absolute, out Uri endpoint) ||
                        !string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Realtime WebSocket endpoint must be WSS");
                    }

                    VoiceClientRuntime runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                    if (runtime == null || !runtime.IsAuthenticated ||
                        !Guid.TryParse(runtime.VoiceConnectionId, out Guid _))
                    {
                        throw new InvalidOperationException("Voice control must be authenticated in the active game room");
                    }
                    if (!MetaverseNetworkClient.isReady)
                        throw new InvalidOperationException("Dedicated game session is not ready");

                    string connectionId = runtime.VoiceConnectionId.Trim().ToLowerInvariant();
                    string roomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
                    string token = SecureTokenStorage.GetAccessToken();
                    if (roomId.Length == 0 || string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("Current room and access token are required");

                    // The W3 ticket and join contract is the same one used by the
                    // deployed WebGL mediasoup-client bundle.
                    string ticket = await RequestTicketAsync(endpoint, roomId, connectionId, token, timeout.Token);
                    if (!EditorApplication.isPlaying || !runtime.IsAuthenticated ||
                        !string.Equals(connectionId, runtime.VoiceConnectionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Voice control changed while obtaining SFU ticket");
                    }

                    var builder = new UriBuilder(endpoint);
                    var retained = new List<string>();
                    foreach (string entry in builder.Query.TrimStart('?').Split('&'))
                    {
                        if (entry.Length == 0) continue;
                        string key = entry.Split('=')[0];
                        if (!string.Equals(Uri.UnescapeDataString(key), "transport", StringComparison.OrdinalIgnoreCase))
                            retained.Add(entry);
                    }
                    retained.Add("transport=webrtc-w3");
                    builder.Query = string.Join("&", retained);

                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.SetRequestHeader("Origin", GetOrigin(endpoint));
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                        await socket.ConnectAsync(builder.Uri, timeout.Token);
                        if (socket.State != WebSocketState.Open)
                            throw new InvalidOperationException("SFU signalling WebSocket did not open");

                        var join = new JObject
                        {
                            ["id"] = 1,
                            ["action"] = "join",
                            ["data"] = new JObject
                            {
                                ["roomId"] = roomId,
                                ["controlConnectionId"] = connectionId,
                                ["ticket"] = ticket
                            }
                        };
                        byte[] bytes = Encoding.UTF8.GetBytes(join.ToString(Formatting.None));
                        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                            true, timeout.Token);

                        JObject answer = null;
                        for (int i = 0; i < 8; i++)
                        {
                            JObject message = JObject.Parse(await ReceiveJsonAsync(socket, timeout.Token));
                            if (message.Value<int?>("id") != 1) continue;
                            answer = message;
                            break;
                        }
                        if (answer == null)
                            throw new InvalidOperationException("SFU did not return join response id 1");
                        if (answer.Value<bool?>("ok") != true)
                            throw new InvalidOperationException("SFU_JOIN_DENIED: " +
                                (answer.Value<string>("error") ?? "unknown"));
                        var data = answer["data"] as JObject;
                        if (data == null || data.Value<bool?>("stageOnly") != false)
                            throw new InvalidOperationException("Production SFU stage policy mismatch");
                        var caps = data["rtpCapabilities"] as JObject;
                        if (caps == null || !(caps["codecs"] is JArray codecs) || codecs.Count == 0)
                            throw new InvalidOperationException("SFU did not provide router RTP capabilities");

                        string version = Vme2WindowsWebRtcEngine.InitializeAndGetVersion();
                        using (var device = new Vme2WindowsWebRtcDevice())
                        {
                            device.LoadRouterRtpCapabilities(caps.ToString(Formatting.None));
                            if (!device.IsLoaded || !device.CanProduceAudio)
                                throw new InvalidOperationException("Native Windows Device is not audio-capable");
                            JObject receiveCaps = JObject.Parse(device.GetReceivingRtpCapabilitiesJson());
                            if (!(receiveCaps["codecs"] is JArray receiveCodecs) || receiveCodecs.Count == 0)
                                throw new InvalidOperationException("Native Device has no receive codecs");
                            Debug.Log("VME2_WINDOWS_SFU_JOIN=PASS | nativeVersion=" + version +
                                      " | routerCodecs=" + codecs.Count +
                                      " | receiveCodecs=" + receiveCodecs.Count +
                                      " | control=authorized | transport=not_started | media=not_started");
                        }
                        try
                        {
                            using (var closing = new CancellationTokenSource(1000))
                            {
                                if (socket.State == WebSocketState.Open)
                                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                                        "check_done", closing.Token);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (WebSocketException) { }
                    }
                    // Native engine stays initialized: no other module's lifecycle is interrupted.
                }
                catch (OperationCanceledException)
                {
                    Debug.LogError("VME2_WINDOWS_SFU_JOIN=FAIL | reason=timeout_or_cancelled");
                }
                catch (Exception error)
                {
                    Debug.LogError("VME2_WINDOWS_SFU_JOIN=FAIL | reason=" +
                        error.GetType().Name + " | detail=" + error.Message);
                }
                finally
                {
                    running = false;
                }
            }
        }

        private static Uri GetTicketUri(Uri endpoint)
        {
            var api = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port,
                "/game-server-control/client/voice-webrtc-ticket");
            return api.Uri;
        }

        private static string GetOrigin(Uri endpoint)
        {
            var api = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port);
            return api.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private static async Task<string> RequestTicketAsync(
            Uri endpoint, string roomId, string connectionId, string token, CancellationToken cancellationToken)
        {
            using (var http = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, GetTicketUri(endpoint)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Origin", GetOrigin(endpoint));
                request.Content = new StringContent(new JObject
                {
                    ["roomId"] = roomId,
                    ["controlConnectionId"] = connectionId
                }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await http.SendAsync(request, cancellationToken))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new InvalidOperationException("SFU_TICKET_HTTP_" + (int)response.StatusCode);
                    string json = await response.Content.ReadAsStringAsync();
                    JObject result = JObject.Parse(json);
                    string ticket = result.Value<string>("ticket");
                    if (result.Value<bool?>("ok") != true || string.IsNullOrWhiteSpace(ticket))
                        throw new InvalidOperationException("SFU ticket was rejected");
                    return ticket;
                }
            }
        }

        private static async Task<string> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[8192];
            using (var output = new System.IO.MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult part = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken);
                    if (part.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException("SFU WebSocket closed before join response");
                    if (part.MessageType != WebSocketMessageType.Text)
                        throw new InvalidOperationException("SFU response was not text");
                    if (output.Length + part.Count > MaxMessageBytes)
                        throw new InvalidOperationException("SFU response exceeds size limit");
                    output.Write(buffer, 0, part.Count);
                    if (part.EndOfMessage)
                        return Encoding.UTF8.GetString(output.ToArray());
                }
            }
        }
    }
}
#endif
