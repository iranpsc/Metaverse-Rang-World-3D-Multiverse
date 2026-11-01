#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Protocol;
using Network_A.Voice.Client.Routing.WebGL;
using Network_A.Voice.Client.Transport.WebGL;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.WebGL
{
    public sealed class VoiceWebGLPhase9LiveProbe : MonoBehaviour
    {
        private const int IdentityTimeoutMs = 15000;
        private const int AuthTimeoutMs = 10000;
        private const int BindTimeoutMs = 7000;
        private const int PongTimeoutMs = 5000;
        private const int HeartbeatTimeoutMs = 8000;

        private static bool startedOnce;

        private VoiceWebGLPhase9ControlTransport controlTransport;
        private VoiceWebGLPhase9MediaTransport mediaTransport;
        private TaskCompletionSource<VoiceClientAuthResult> authCompletion;
        private TaskCompletionSource<VoiceMediaV2BindResult> bindCompletion;
        private TaskCompletionSource<bool> pongCompletion;
        private CancellationTokenSource lifetimeCts;

        private uint nextControlSequence = 1;
        private uint lastControlSequence;
        private uint nextMediaSequence = 1;
        private uint lastMediaSequence;
        private int heartbeatAckCount;
        private string controlConnectionId = string.Empty;
        private string mediaStreamId = string.Empty;
        private bool shuttingDown;
        private bool running;

        // این تابع آزمون مستقل فاز نه را در هر بارگذاری صفحه فقط یک بار آغاز می کند.
        public void BeginIfNeeded()
        {
            if (running || startedOnce) return;
            if (!VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected) return;
            startedOnce = true;
            running = true;
            lifetimeCts = new CancellationTokenSource();
            _ = RunAsync(lifetimeCts.Token);
        }

        // این تابع مسیر کنترل و مسیر رسانه را مستقل از صدای تولیدی به صورت کامل آزمایش می کند.
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            Debug.Log("VME2_PHASE9_WEBGL_PROBE=START | feedsProductionVoice=False");

            try
            {
                IdentitySnapshot identity = await WaitForIdentityAsync(cancellationToken);
                if (!identity.Ready) throw new InvalidOperationException("phase9_identity_not_ready");

                await ConnectAndAuthenticateControlAsync(identity, cancellationToken);
                await ConnectBindAndPingMediaAsync(identity, cancellationToken);

                bool heartbeatReady = await WaitForHeartbeatAckAsync(cancellationToken);
                if (!heartbeatReady) throw new InvalidOperationException("phase9_control_heartbeat_not_observed");

                Debug.Log(
                    "VME2_PHASE9_LIVE=PASS" +
                    " | platform=WebGL" +
                    " | controlAuthenticated=True" +
                    " | heartbeatAck=True" +
                    " | mediaConnected=True" +
                    " | mediaBound=True" +
                    " | pingPong=True" +
                    " | controlConnectionId=" + controlConnectionId +
                    " | mediaTransport=VoiceWebGLPhase9MediaTransport" +
                    " | feedsProductionVoice=False");
            }
            catch (OperationCanceledException)
            {
                Debug.LogError("VME2_PHASE9_LIVE=FAIL | platform=WebGL | reason=cancelled | feedsProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE9_LIVE=FAIL | platform=WebGL | reason=" + Safe(exception.Message) + " | feedsProductionVoice=False");
            }
            finally
            {
                await CleanupAsync();
                running = false;
            }
        }

        // این تابع تا آماده شدن هویت بازیکن و توکن معتبر برای آزمون فاز نه منتظر می ماند.
        private static async Task<IdentitySnapshot> WaitForIdentityAsync(CancellationToken cancellationToken)
        {
            int waitedMs = 0;

            while (waitedMs < IdentityTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string userId = Safe(MetaverseNetworkClient.userId);
                string roomId = Safe(MetaverseNetworkClient.roomId);
                string accessToken = Safe(SecureTokenStorage.GetAccessToken());

                if (MetaverseNetworkClient.isReady && Guid.TryParse(userId, out _) && roomId.Length > 0 && accessToken.Length > 0)
                {
                    return new IdentitySnapshot(true, userId, roomId, accessToken);
                }

                await Task.Delay(100, cancellationToken);
                waitedMs += 100;
            }

            return new IdentitySnapshot(false, string.Empty, string.Empty, string.Empty);
        }

        // این تابع سوکت کنترل مستقل را باز می کند و پیام احراز را بدون صف ویندوز مستقیما می فرستد.
        private async Task ConnectAndAuthenticateControlAsync(IdentitySnapshot identity, CancellationToken cancellationToken)
        {
            controlTransport = new VoiceWebGLPhase9ControlTransport();
            controlTransport.PacketReceived += HandleControlPacket;
            controlTransport.Failed += HandleControlFailure;
            controlTransport.Disconnected += HandleControlDisconnected;

            string endpoint = AppendTransport(ServerConfig.RealtimeWebSocketUrl, "voice");
            bool connected = await controlTransport.ConnectAsync(endpoint, cancellationToken);
            if (!connected) throw new InvalidOperationException("phase9_control_socket_connect_failed");

            authCompletion = new TaskCompletionSource<VoiceClientAuthResult>();
            string clientInstanceId = Guid.NewGuid().ToString("D");
            byte[] authPayload = VoiceClientControlPayload.EncodeAuthRequest(
                VoiceClientPlatform.WebGl,
                identity.AccessToken,
                identity.RoomId,
                identity.UserId,
                clientInstanceId,
                Application.version);

            bool sent = await SendControlEnvelopeAsync(
                VoiceClientMessageType.AuthRequest,
                VoiceClientMessageFlags.AckRequired,
                VoiceClientEnvelope.EmptyUuid,
                VoiceClientEnvelope.EmptyUuid,
                authPayload,
                cancellationToken);

            if (!sent) throw new InvalidOperationException("phase9_control_auth_send_failed");
            Debug.Log("VME2_PHASE9_WEBGL_CONTROL_AUTH_SEND=PASS");

            VoiceClientAuthResult result = await WaitForTaskAsync(authCompletion.Task, AuthTimeoutMs, "phase9_control_auth_timeout", cancellationToken);
            if (result == null || !result.Success || !Guid.TryParse(result.VoiceConnectionId, out _)) throw new InvalidOperationException("phase9_control_auth_rejected:" + Safe(result?.Message));

            controlConnectionId = result.VoiceConnectionId.Trim().ToLowerInvariant();

            if (!string.Equals(Safe(result.UserId), identity.UserId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("phase9_control_user_mismatch");
            }

            Debug.Log("VME2_PHASE9_WEBGL_CONTROL_AUTH=PASS | controlConnectionId=" + controlConnectionId);
        }

        // این تابع سوکت رسانه مستقل را باز می کند و بایند و رفت و برگشت پینگ را روی همان اتصال آزمایش می کند.
        private async Task ConnectBindAndPingMediaAsync(IdentitySnapshot identity, CancellationToken cancellationToken)
        {
            mediaTransport = new VoiceWebGLPhase9MediaTransport();
            mediaTransport.PacketReceived += HandleMediaPacket;
            mediaTransport.Failed += HandleMediaFailure;
            mediaTransport.Disconnected += HandleMediaDisconnected;

            string endpoint = AppendTransport(ServerConfig.RealtimeWebSocketUrl, "voice-media-v2");
            bool connected = await mediaTransport.ConnectAsync(endpoint, cancellationToken);
            if (!connected) throw new InvalidOperationException("phase9_media_socket_connect_failed");

            mediaStreamId = Guid.NewGuid().ToString("D");
            bindCompletion = new TaskCompletionSource<VoiceMediaV2BindResult>();

            VoiceMediaV2Packet bindRequest = CreateMediaPacket(
                VoiceMediaV2PacketKind.BindRequest,
                VoiceMediaV2Codec.None,
                VoiceMediaV2BindPayload.EncodeRequest(
                    identity.AccessToken,
                    identity.RoomId,
                    controlConnectionId,
                    1,
                    Application.version));

            bool bindSent = await mediaTransport.SendAsync(bindRequest.Encode(), cancellationToken);
            if (!bindSent) throw new InvalidOperationException("phase9_media_bind_send_failed");

            VoiceMediaV2BindResult bindResult = await WaitForTaskAsync(bindCompletion.Task, BindTimeoutMs, "phase9_media_bind_timeout", cancellationToken);
            if (bindResult == null || !bindResult.Success) throw new InvalidOperationException("phase9_media_bind_rejected:" + Safe(bindResult?.Message));
            if (!string.Equals(bindResult.ControlConnectionId, controlConnectionId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("phase9_media_bind_connection_mismatch");

            Debug.Log("VME2_PHASE9_WEBGL_MEDIA_BIND=PASS | controlConnectionId=" + controlConnectionId);

            pongCompletion = new TaskCompletionSource<bool>();
            byte[] pingPayload = { 9, 2, 1 };

            VoiceMediaV2Packet ping = CreateMediaPacket(
                VoiceMediaV2PacketKind.Ping,
                VoiceMediaV2Codec.None,
                pingPayload);

            bool pingSent = await mediaTransport.SendAsync(ping.Encode(), cancellationToken);
            if (!pingSent) throw new InvalidOperationException("phase9_media_ping_send_failed");

            bool pong = await WaitForTaskAsync(pongCompletion.Task, PongTimeoutMs, "phase9_media_pong_timeout", cancellationToken);
            if (!pong) throw new InvalidOperationException("phase9_media_pong_invalid");

            Debug.Log("VME2_PHASE9_WEBGL_MEDIA_PING_PONG=PASS");
        }

        // این تابع هر بسته کنترل را با ترتیب افزایشی بررسی و نتیجه احراز یا ضربان قلب را پردازش می کند.
        private void HandleControlPacket(byte[] packet)
        {
            try
            {
                VoiceClientEnvelope envelope = VoiceClientEnvelope.Decode(packet);
                if (envelope.Sequence == 0 || envelope.Sequence <= lastControlSequence) throw new InvalidOperationException("phase9_control_sequence_invalid");
                lastControlSequence = envelope.Sequence;

                if (envelope.MessageType == VoiceClientMessageType.AuthResult)
                {
                    authCompletion?.TrySetResult(VoiceClientControlPayload.DecodeAuthResult(envelope.Payload));
                    return;
                }

                if (envelope.MessageType == VoiceClientMessageType.Heartbeat)
                {
                    _ = SendHeartbeatAckAsync(envelope.Sequence);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE9_WEBGL_CONTROL_RECEIVE=FAIL | reason=" + Safe(exception.Message));
            }
        }

        // این تابع پاسخ ضربان قلب را مستقیما روی سوکت کنترل آزمون فاز نه ارسال می کند.
        private async Task SendHeartbeatAckAsync(uint heartbeatSequence)
        {
            if (heartbeatSequence == 0 || controlTransport == null || !controlTransport.IsConnected) return;

            bool sent = await SendControlEnvelopeAsync(
                VoiceClientMessageType.HeartbeatAck,
                VoiceClientMessageFlags.None,
                VoiceClientEnvelope.EmptyUuid,
                string.IsNullOrWhiteSpace(controlConnectionId) ? VoiceClientEnvelope.EmptyUuid : controlConnectionId,
                VoiceClientControlPayload.EncodeHeartbeatAck(heartbeatSequence),
                CancellationToken.None);

            if (!sent)
            {
                Debug.LogError("VME2_PHASE9_WEBGL_CONTROL_HEARTBEAT_ACK=FAIL | heartbeatSequence=" + heartbeatSequence);
                return;
            }

            heartbeatAckCount++;
            if (heartbeatAckCount == 1)
            {
                Debug.Log("VME2_PHASE9_WEBGL_CONTROL_HEARTBEAT_ACK=PASS | heartbeatSequence=" + heartbeatSequence);
            }
        }

        // این تابع بسته کنترل را با شماره افزایشی و زمان جاری مستقیما روی سوکت آزمون فاز نه می فرستد.
        private async Task<bool> SendControlEnvelopeAsync(
            VoiceClientMessageType messageType,
            VoiceClientMessageFlags flags,
            string sessionId,
            string senderId,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            if (controlTransport == null || !controlTransport.IsConnected) return false;

            uint sequence = nextControlSequence;
            nextControlSequence = sequence == uint.MaxValue ? 1 : sequence + 1;

            VoiceClientEnvelope envelope = new VoiceClientEnvelope
            {
                MessageType = messageType,
                Flags = flags,
                Sequence = sequence,
                TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SessionId = sessionId,
                SenderId = senderId,
                Payload = payload ?? Array.Empty<byte>()
            };

            return await controlTransport.SendAsync(envelope.Encode(), cancellationToken);
        }

        // این تابع بسته های بایند و پونگ رسانه را بررسی و نتیجه انتظار مربوط را کامل می کند.
        private void HandleMediaPacket(byte[] bytes)
        {
            try
            {
                VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
                if (packet.TransportSequence == 0 || packet.TransportSequence <= lastMediaSequence) throw new InvalidOperationException("phase9_media_sequence_invalid");
                lastMediaSequence = packet.TransportSequence;

                if (packet.Kind == VoiceMediaV2PacketKind.BindResult)
                {
                    bindCompletion?.TrySetResult(VoiceMediaV2BindPayload.DecodeResult(packet.Payload));
                    return;
                }

                if (packet.Kind == VoiceMediaV2PacketKind.Pong)
                {
                    bool valid = string.Equals(packet.StreamId, mediaStreamId, StringComparison.OrdinalIgnoreCase) && packet.Payload != null && packet.Payload.Length == 3 && packet.Payload[0] == 9 && packet.Payload[1] == 2 && packet.Payload[2] == 1;
                    pongCompletion?.TrySetResult(valid);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE9_WEBGL_MEDIA_RECEIVE=FAIL | reason=" + Safe(exception.Message));
            }
        }

        // این تابع یک بسته رسانه فاز نه را با شماره انتقال افزایشی و شناسه جریان فعلی می سازد.
        private VoiceMediaV2Packet CreateMediaPacket(VoiceMediaV2PacketKind kind, VoiceMediaV2Codec codec, byte[] payload)
        {
            uint sequence = nextMediaSequence;
            nextMediaSequence = sequence == uint.MaxValue ? 1 : sequence + 1;

            return new VoiceMediaV2Packet
            {
                Kind = kind,
                Codec = codec,
                Flags = VoiceMediaV2Flags.None,
                TransportSequence = sequence,
                MediaSequence = 0,
                MediaTimestamp100Ns = 0,
                AckTransportSequence = lastMediaSequence,
                AckMask = 0,
                SessionId = VoiceMediaV2Constants.EmptyUuid,
                StreamId = mediaStreamId,
                SenderId = VoiceMediaV2Constants.EmptyUuid,
                SecurityContextId = 0,
                Payload = payload ?? Array.Empty<byte>()
            };
        }

        // این تابع تا دریافت نخستین پاسخ موفق ضربان قلب یا پایان مهلت منتظر می ماند.
        private async Task<bool> WaitForHeartbeatAckAsync(CancellationToken cancellationToken)
        {
            int waitedMs = 0;

            while (waitedMs < HeartbeatTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (heartbeatAckCount > 0) return true;
                await Task.Delay(100, cancellationToken);
                waitedMs += 100;
            }

            return false;
        }

        // این تابع نتیجه یک کار غیر همزمان را با مهلت محدود و علت مشخص دریافت می کند.
        private static async Task<T> WaitForTaskAsync<T>(Task<T> task, int timeoutMs, string timeoutReason, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task timeoutTask = Task.Delay(timeoutMs, timeoutCts.Token);
                Task completedTask = await Task.WhenAny(task, timeoutTask);

                if (completedTask == task)
                {
                    timeoutCts.Cancel();
                    return await task;
                }
            }

            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(timeoutReason);
        }

        // این تابع خطای سوکت کنترل را برای گزارش نهایی فاز نه ثبت می کند.
        private void HandleControlFailure(string message)
        {
            string reason = Safe(message);
            authCompletion?.TrySetException(new InvalidOperationException("phase9_control_socket_error:" + reason));
            Debug.LogError("VME2_PHASE9_WEBGL_CONTROL_SOCKET=FAIL | reason=" + reason);
        }

        // این تابع بسته شدن غیر منتظره سوکت کنترل را در زمان اجرای آزمون گزارش و انتظار احراز را پایان می دهد.
        private void HandleControlDisconnected(string reason)
        {
            string safeReason = Safe(reason);
            if (!shuttingDown) authCompletion?.TrySetException(new InvalidOperationException("phase9_control_socket_closed:" + safeReason));
            if (!shuttingDown) Debug.LogWarning("VME2_PHASE9_WEBGL_CONTROL_SOCKET=CLOSED | reason=" + safeReason);
        }

        // این تابع خطای سوکت رسانه را برای گزارش نهایی فاز نه ثبت و انتظارهای جاری را پایان می دهد.
        private void HandleMediaFailure(string message)
        {
            string reason = Safe(message);
            InvalidOperationException exception = new InvalidOperationException("phase9_media_socket_error:" + reason);
            bindCompletion?.TrySetException(exception);
            pongCompletion?.TrySetException(exception);
            Debug.LogError("VME2_PHASE9_WEBGL_MEDIA_SOCKET=FAIL | reason=" + reason);
        }

        // این تابع بسته شدن غیر منتظره سوکت رسانه را گزارش و انتظارهای جاری را پایان می دهد.
        private void HandleMediaDisconnected(string reason)
        {
            string safeReason = Safe(reason);
            if (!shuttingDown)
            {
                InvalidOperationException exception = new InvalidOperationException("phase9_media_socket_closed:" + safeReason);
                bindCompletion?.TrySetException(exception);
                pongCompletion?.TrySetException(exception);
                Debug.LogWarning("VME2_PHASE9_WEBGL_MEDIA_SOCKET=CLOSED | reason=" + safeReason);
            }
        }

        // این تابع هر دو سوکت آزمون را بدون اثر روی راه های سالم پروژه می بندد و منابع را آزاد می کند.
        private async Task CleanupAsync()
        {
            shuttingDown = true;

            try
            {
                if (mediaTransport != null && mediaTransport.IsConnected)
                {
                    await mediaTransport.DisconnectAsync("phase9_probe_complete", CancellationToken.None);
                }
            }
            catch { }

            try
            {
                if (controlTransport != null && controlTransport.IsConnected && Guid.TryParse(controlConnectionId, out _))
                {
                    await SendControlEnvelopeAsync(
                        VoiceClientMessageType.Disconnect,
                        VoiceClientMessageFlags.None,
                        VoiceClientEnvelope.EmptyUuid,
                        controlConnectionId,
                        Array.Empty<byte>(),
                        CancellationToken.None);

                    await Task.Delay(100);
                }
            }
            catch { }

            try
            {
                if (controlTransport != null && controlTransport.IsConnected)
                {
                    await controlTransport.DisconnectAsync("phase9_probe_complete", CancellationToken.None);
                }
            }
            catch { }

            if (controlTransport != null)
            {
                controlTransport.PacketReceived -= HandleControlPacket;
                controlTransport.Failed -= HandleControlFailure;
                controlTransport.Disconnected -= HandleControlDisconnected;
                controlTransport.Dispose();
                controlTransport = null;
            }

            if (mediaTransport != null)
            {
                mediaTransport.PacketReceived -= HandleMediaPacket;
                mediaTransport.Failed -= HandleMediaFailure;
                mediaTransport.Disconnected -= HandleMediaDisconnected;
                mediaTransport.Dispose();
                mediaTransport = null;
            }

            lifetimeCts?.Dispose();
            lifetimeCts = null;
        }

        // این تابع هنگام نابودی آبجکت تلاش جاری را لغو و منابع باقی مانده را آزاد می کند.
        private void OnDestroy()
        {
            try { lifetimeCts?.Cancel(); } catch { }
            controlTransport?.Dispose();
            mediaTransport?.Dispose();
        }

        // این تابع پارامتر نوع راه انتقال را بدون حذف پارامترهای قبلی به نشانی وب سوکت اضافه می کند.
        private static string AppendTransport(string endpoint, string transportName)
        {
            string safeEndpoint = Safe(endpoint);
            string safeTransportName = Safe(transportName);

            if (safeEndpoint.Length == 0 || safeTransportName.Length == 0)
            {
                return safeEndpoint;
            }

            string separator;

            if (!safeEndpoint.Contains("?"))
            {
                separator = "?";
            }
            else if (safeEndpoint.EndsWith("?") || safeEndpoint.EndsWith("&"))
            {
                separator = string.Empty;
            }
            else
            {
                separator = "&";
            }

            return safeEndpoint + separator + "transport=" + safeTransportName;
        }

        // این تابع متن های ورودی را برای ثبت گزارش فاز نه پاک سازی می کند.
        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private readonly struct IdentitySnapshot
        {
            public readonly bool Ready;
            public readonly string UserId;
            public readonly string RoomId;
            public readonly string AccessToken;

            // این سازنده وضعیت هویت آماده شده را بدون تغییر نگه می دارد.
            public IdentitySnapshot(bool ready, string userId, string roomId, string accessToken)
            {
                Ready = ready;
                UserId = userId;
                RoomId = roomId;
                AccessToken = accessToken;
            }
        }
    }
}
#endif
