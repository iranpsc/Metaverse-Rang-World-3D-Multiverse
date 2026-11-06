#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    // Owns one authorized SFU signalling peer and its two native transports.
    // Implements authenticated transport signaling. Does not start audio media.
    public sealed class Vme2WindowsWebRtcSfuSession : IDisposable
    {
        private const int RpcTimeoutMs = 12000;
        private const int MaxMessageBytes = 1024 * 1024;
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<int, TaskCompletionSource<JObject>> pending =
            new Dictionary<int, TaskCompletionSource<JObject>>();
        private readonly object pendingLock = new object();
        private Task receiveTask;
        private Vme2WindowsWebRtcDevice device;
        private int requestId;
        private int disposed;
        private string sendTransportId;
        private string recvTransportId;

        internal event Action<string, JObject> EventReceived;

        public bool HasSendTransport => device != null && device.HasSendTransport;
        public bool HasRecvTransport => device != null && device.HasRecvTransport;

        public async Task ConnectAndLoadAsync(Uri endpoint, string roomId,
            string controlConnectionId, string ticket, CancellationToken cancellationToken)
        {
            EnsureActive();
            if (receiveTask != null) throw new InvalidOperationException("SFU session already started");
            if (endpoint == null || endpoint.Scheme != "wss" ||
                string.IsNullOrWhiteSpace(roomId) ||
                !Guid.TryParse(controlConnectionId, out _) || string.IsNullOrWhiteSpace(ticket))
            {
                throw new ArgumentException("Authorized WSS endpoint, room, control ID and ticket are required");
            }

            var origin = new UriBuilder(Uri.UriSchemeHttps, endpoint.Host,
                endpoint.IsDefaultPort ? -1 : endpoint.Port).Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            var builder = new UriBuilder(endpoint);
            var kept = new List<string>();
            foreach (string entry in builder.Query.TrimStart('?').Split('&'))
            {
                if (entry.Length == 0) continue;
                string name = entry.Split('=')[0];
                if (!string.Equals(Uri.UnescapeDataString(name), "transport", StringComparison.OrdinalIgnoreCase))
                    kept.Add(entry);
            }
            kept.Add("transport=webrtc-w3");
            builder.Query = string.Join("&", kept);

            socket.Options.SetRequestHeader("Origin", origin);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                linked.CancelAfter(RpcTimeoutMs);
                await socket.ConnectAsync(builder.Uri, linked.Token).ConfigureAwait(false);
            }
            receiveTask = ReceiveLoopAsync(lifetime.Token);

            JObject joined = await RpcAsync("join", new JObject
            {
                ["roomId"] = roomId,
                ["controlConnectionId"] = controlConnectionId,
                ["ticket"] = ticket
            }, cancellationToken);

            if (joined.Value<bool?>("stageOnly") != false ||
                !(joined["rtpCapabilities"] is JObject capabilities) ||
                !(capabilities["codecs"] is JArray codecs) || codecs.Count == 0)
            {
                throw new InvalidOperationException("SFU join returned invalid production capabilities");
            }

            Vme2WindowsWebRtcEngine.InitializeAndGetVersion();
            device = new Vme2WindowsWebRtcDevice();
            device.LoadRouterRtpCapabilities(capabilities.ToString(Formatting.None));
            if (!device.IsLoaded || !device.CanProduceAudio)
                throw new InvalidOperationException("Native Windows Device is not audio-capable");
            device.SetSignalingHandler(HandleNativeSignaling);
        }

        public async Task CreateTransportsAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            if (device == null || !device.IsLoaded)
                throw new InvalidOperationException("Join and load the Native Device before creating transports");
            if (device.HasSendTransport || device.HasRecvTransport)
                throw new InvalidOperationException("Transports have already been created");

            JObject send = await RpcAsync("createTransport",
                new JObject { ["direction"] = "send" }, cancellationToken);
            EnsureActive();
            sendTransportId = send.Value<string>("id");
            if (string.IsNullOrWhiteSpace(sendTransportId))
                throw new InvalidOperationException("Missing SFU send Transport ID");
            device.CreateSendTransport(send.ToString(Formatting.None));
            if (!device.HasSendTransport)
                throw new InvalidOperationException("Native SendTransport was not retained");

            JObject recv = await RpcAsync("createTransport",
                new JObject { ["direction"] = "recv" }, cancellationToken);
            EnsureActive();
            recvTransportId = recv.Value<string>("id");
            if (string.IsNullOrWhiteSpace(recvTransportId))
                throw new InvalidOperationException("Missing SFU receive Transport ID");
            device.CreateRecvTransport(recv.ToString(Formatting.None));
            if (!device.HasRecvTransport)
                throw new InvalidOperationException("Native RecvTransport was not retained");
        }

        // Returns null when SFU has no authorized peer Producer in this room.
        public async Task<string> ConsumeFirstAuthorizedAudioAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            if (device == null || !device.HasRecvTransport || string.IsNullOrWhiteSpace(recvTransportId))
                throw new InvalidOperationException("Receive Transport must exist before consuming audio");
            JObject listed = await RpcAsync("listProducers", new JObject(), cancellationToken)
                .ConfigureAwait(false);
            if (!(listed["producerIds"] is JArray producerIds))
                throw new InvalidOperationException("SFU returned invalid Producer list");
            if (producerIds.Count == 0) return null;
            string producerId = producerIds[0].Type == JTokenType.String
                ? producerIds[0].Value<string>() : null;
            if (string.IsNullOrWhiteSpace(producerId))
                throw new InvalidOperationException("SFU returned invalid Producer ID");

            JObject response = await RpcAsync("consume", new JObject
            {
                ["transportId"] = recvTransportId,
                ["producerId"] = producerId,
                ["rtpCapabilities"] = JObject.Parse(device.GetReceivingRtpCapabilitiesJson())
            }, cancellationToken).ConfigureAwait(false);
            string consumerId = response.Value<string>("id");
            if (string.IsNullOrWhiteSpace(consumerId) ||
                response.Value<string>("producerId") != producerId ||
                response.Value<string>("kind") != "audio" ||
                !(response["rtpParameters"] is JObject))
                throw new InvalidOperationException("SFU returned invalid audio Consumer parameters");

            // Receive starts native SDP negotiation and authenticated OnConnect.
            await Task.Run(() => device.ConsumeAudio(response.ToString(Formatting.None)),
                cancellationToken).ConfigureAwait(false);
            try
            {
                if (!device.HasAudioConsumer(consumerId))
                    throw new InvalidOperationException("Native Consumer was not retained");
                JObject resumed = await RpcAsync("resumeConsumer", new JObject
                {
                    ["consumerId"] = consumerId
                }, cancellationToken).ConfigureAwait(false);
                if (resumed.Value<bool?>("resumed") != true)
                    throw new InvalidOperationException("SFU did not resume audio Consumer");
                return consumerId;
            }
            catch
            {
                try { device.CloseAudioConsumer(consumerId); } catch { }
                throw;
            }
        }

        // Production: one native audio Producer per authenticated Windows peer.
        public Task<string> StartAudioProducerAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            if (device == null || !device.HasSendTransport)
                throw new InvalidOperationException("Native SendTransport is missing");
            // AudioTrack is signaling-thread-affine in WebRTC M140; create and
            // close it on the Unity main thread that owns this session.
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(device.StartAudioProducer());
        }

        public void SetAudioProducerMuted(bool muted)
        {
            EnsureActive();
            device.SetAudioProducerMuted(muted);
        }

        public int SendIceState => device == null ? 0 : device.SendIceState;

        public void PushAudioPcm(float[] pcm, int rate)
        {
            EnsureActive();
            device.PushAudioPcm(pcm, rate);
        }

        public void CloseAudioProducer()
        {
            EnsureActive();
            device.CloseAudioProducer();
        }

        public sealed class AudioPeer
        {
            public string ProducerId { get; internal set; }
            public string ConsumerId { get; internal set; }
            public string SenderConnectionId { get; internal set; }
        }

        public async Task<string[]> ListAuthorizedProducerIdsAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            JObject listed = await RpcAsync("listProducers", new JObject(), cancellationToken)
                .ConfigureAwait(false);
            if (!(listed["producerIds"] is JArray ids))
                throw new InvalidOperationException("SFU returned invalid Producer list");
            var result = new List<string>();
            foreach (JToken value in ids)
            {
                string id = value.Type == JTokenType.String ? value.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("SFU returned invalid Producer ID");
                if (!result.Contains(id)) result.Add(id);
            }
            return result.ToArray();
        }

        public async Task<AudioPeer> ConsumeAuthorizedAudioAsync(string producerId,
            CancellationToken cancellationToken)
        {
            EnsureActive();
            if (device == null || !device.HasRecvTransport ||
                string.IsNullOrWhiteSpace(recvTransportId) || string.IsNullOrWhiteSpace(producerId))
                throw new InvalidOperationException("Authorized Producer and RecvTransport required");
            JObject response = await RpcAsync("consume", new JObject
            {
                ["transportId"] = recvTransportId,
                ["producerId"] = producerId,
                ["rtpCapabilities"] = JObject.Parse(device.GetReceivingRtpCapabilitiesJson())
            }, cancellationToken).ConfigureAwait(false);
            string id = response.Value<string>("id");
            string sender = response.Value<string>("senderConnectionId");
            if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(sender, out _) ||
                response.Value<string>("producerId") != producerId ||
                response.Value<string>("kind") != "audio" ||
                !(response["rtpParameters"] is JObject))
                throw new InvalidOperationException("SFU returned invalid authorized Consumer");
            await Task.Run(() => device.ConsumeAudio(response.ToString(Formatting.None)),
                cancellationToken).ConfigureAwait(false);
            try
            {
                if (!device.HasAudioConsumer(id))
                    throw new InvalidOperationException("Native audio Consumer was not retained");
                JObject resumed = await RpcAsync("resumeConsumer", new JObject
                {
                    ["consumerId"] = id
                }, cancellationToken).ConfigureAwait(false);
                if (resumed.Value<bool?>("resumed") != true)
                    throw new InvalidOperationException("SFU did not resume audio Consumer");
                return new AudioPeer
                {
                    ProducerId = producerId, ConsumerId = id, SenderConnectionId = sender
                };
            }
            catch
            {
                try { device.CloseAudioConsumer(id); } catch { }
                throw;
            }
        }

        public void CloseAudioConsumer(string id)
        {
            EnsureActive();
            device.CloseAudioConsumer(id);
        }

        public bool TryGetAudioInboundRtpStats(string consumerId,
            out ulong packetsReceived, out long packetsLost)
        {
            EnsureActive();
            if (device == null) throw new InvalidOperationException("Native device is unavailable");
            return device.TryGetAudioInboundRtpStats(consumerId,
                out packetsReceived, out packetsLost);
        }

        public void GetAudioPcmInfo(string consumerId, out int sampleRate,
            out ulong framesReceived, out int bufferedFrames)
        {
            EnsureActive();
            if (device == null) throw new InvalidOperationException("Native Device not initialized");
            device.GetAudioPcmInfo(consumerId, out sampleRate, out framesReceived, out bufferedFrames);
        }

        public int ReadAudioPcm(string consumerId, float[] output)
        {
            EnsureActive();
            if (device == null) throw new InvalidOperationException("Native Device not initialized");
            return device.ReadAudioPcm(consumerId, output);
        }

        public int RecvIceState
        {
            get
            {
                EnsureActive();
                if (device == null) throw new InvalidOperationException("Native Device not initialized");
                return device.RecvIceState;
            }
        }

        private string HandleNativeSignaling(int operation, string transportId, string requestJson)
        {
            EnsureActive();
            string expectedId = operation == 2 ? recvTransportId : sendTransportId;
            if (operation < 1 || operation > 3 || string.IsNullOrWhiteSpace(expectedId) ||
                !string.Equals(expectedId, transportId, StringComparison.Ordinal))
                throw new InvalidOperationException("Unrecognized SFU Transport signaling request");

            JObject payload = JObject.Parse(requestJson);
            if (operation == 1 || operation == 2)
            {
                if (!(payload["fingerprints"] is JArray fingerprints) || fingerprints.Count == 0)
                    throw new InvalidOperationException("Native DTLS fingerprints missing");
                JObject result = RpcAsync("connectTransport", new JObject
                {
                    ["transportId"] = transportId,
                    ["dtlsParameters"] = payload
                }, lifetime.Token).GetAwaiter().GetResult();
                if (result.Value<bool?>("connected") != true)
                    throw new InvalidOperationException("SFU did not confirm Transport connection");
                return string.Empty;
            }

            if (payload.Value<string>("kind") != "audio" ||
                !(payload["rtpParameters"] is JObject parameters))
                throw new InvalidOperationException("Only valid audio RTP production is supported");
            JObject produced = RpcAsync("produce", new JObject
            {
                ["transportId"] = transportId,
                ["kind"] = "audio",
                ["rtpParameters"] = parameters
            }, lifetime.Token).GetAwaiter().GetResult();
            string producerId = produced.Value<string>("id");
            if (string.IsNullOrWhiteSpace(producerId))
                throw new InvalidOperationException("SFU did not return a Producer ID");
            return producerId;
        }

        internal async Task<JObject> RpcAsync(string action, JObject data,
            CancellationToken cancellationToken)
        {
            EnsureActive();
            if (socket.State != WebSocketState.Open)
                throw new InvalidOperationException("SFU signalling socket is not open");
            int id = Interlocked.Increment(ref requestId);
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pendingLock) pending.Add(id, completion);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                linked.CancelAfter(RpcTimeoutMs);
                using (linked.Token.Register(() => completion.TrySetCanceled()))
                {
                    try
                    {
                        var json = new JObject { ["id"] = id, ["action"] = action,
                            ["data"] = data ?? new JObject() };
                        byte[] bytes = Encoding.UTF8.GetBytes(json.ToString(Formatting.None));
                        await sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
                        try
                        {
                            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                                true, linked.Token).ConfigureAwait(false);
                        }
                        finally { sendGate.Release(); }

                        JObject response = await completion.Task.ConfigureAwait(false);
                        if (response.Value<bool?>("ok") != true)
                            throw new InvalidOperationException("SFU_" + action + "_DENIED: " +
                                (response.Value<string>("error") ?? "unknown"));
                        if (!(response["data"] is JObject result))
                            throw new InvalidOperationException("SFU_" + action + "_INVALID_RESPONSE");
                        return result;
                    }
                    finally
                    {
                        lock (pendingLock) pending.Remove(id);
                    }
                }
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            Exception error = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    JObject message = JObject.Parse(await ReceiveJsonAsync(cancellationToken).ConfigureAwait(false));
                    int id = message.Value<int?>("id") ?? 0;
                    if (id > 0)
                    {
                        TaskCompletionSource<JObject> completion;
                        lock (pendingLock) pending.TryGetValue(id, out completion);
                        completion?.TrySetResult(message);
                    }
                    else if (message.Value<string>("event") is string name &&
                             message["data"] is JObject data)
                    {
                        try { EventReceived?.Invoke(name, data); }
                        catch { /* Application subscribers must not break the receive loop. */ }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) { error = exception; }
            finally
            {
                var cause = error ?? new IOException("SFU signalling connection was closed");
                lock (pendingLock)
                {
                    foreach (var request in pending.Values) request.TrySetException(cause);
                    pending.Clear();
                }
            }
        }

        private async Task<string> ReceiveJsonAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using (var stream = new MemoryStream())
            {
                while (true)
                {
                    WebSocketReceiveResult part = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close)
                        throw new IOException("SFU closed the signalling connection");
                    if (part.MessageType != WebSocketMessageType.Text)
                        throw new InvalidDataException("SFU response was not text");
                    if (stream.Length + part.Count > MaxMessageBytes)
                        throw new InvalidDataException("SFU response exceeded size limit");
                    stream.Write(buffer, 0, part.Count);
                    if (part.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(Vme2WindowsWebRtcSfuSession));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            socket.Abort();
            socket.Dispose();
            try { device?.Dispose(); }
            finally
            {
                sendGate.Dispose();
                lifetime.Dispose();
            }
        }
    }
}
#endif
