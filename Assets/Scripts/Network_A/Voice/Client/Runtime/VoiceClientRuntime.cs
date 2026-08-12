using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.Playback;
using Network_A.Voice.Client.Protocol;
using Network_A.Voice.Client.Transport;
using UnityEngine;

namespace Network_A.Voice.Client.Runtime
{
    public sealed class VoiceClientRuntime : MonoBehaviour
    {
        private const int MaxPendingVoiceFrames = 30;
        private const ulong MaxQueuedVoiceFrameAgeMs = 300;

        private readonly ConcurrentQueue<byte[]> receivedPackets = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<PendingVoiceFrame> pendingVoiceFrames = new ConcurrentQueue<PendingVoiceFrame>();
        private readonly Dictionary<string, ActiveVoiceSession> sessions =
            new Dictionary<string, ActiveVoiceSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> peerConnectionByUserId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> recordingConsentSentSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> recordingConsentSendInFlightSessionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly object gracefulDisconnectSync = new object();
        private readonly object voiceFramePumpSync = new object();

        private IVoiceClientTransport transport;
        private VoiceMicrophonePublisher microphonePublisher;
        private VoiceSpatialPlaybackManager playbackManager;
        private CancellationTokenSource lifetimeCts;
        private string clientInstanceId;
        private string voiceConnectionId = string.Empty;
        private string previousVoiceConnectionId = string.Empty;
        private ulong disconnectedAtMs;
        private uint outgoingSequence;
        private uint lastReceivedSequence;
        private uint lastPublishedSequence;
        private uint previousLastReceivedSequence;
        private uint previousLastPublishedSequence;
        private TaskCompletionSource<bool> authenticationCompletion;
        private TaskCompletionSource<bool> transportDisconnectCompletion;
        private Task<bool> gracefulDisconnectTask;
        private Task<bool> playerUnavailableDisconnectTask;
        private Task voiceFramePumpTask;
        private int pendingVoiceFrameDrops;
        private int staleVoiceFrameDrops;
        private bool connecting;
        private bool reconnecting;
        private bool shuttingDown;
        private bool runtimeResourcesDisposed;
        private bool applicationQuitRequested;
        private bool publishingAllowed;
        private bool firstVoiceFrameSendLogged;
        private bool recordingConsentDesired;
        private int bitrateKbps = 40;

        public event Action<string> StatusChanged;
        public event Action<string> Failed;

        public bool IsAuthenticated { get; private set; }
        public bool IsMicrophoneMuted { get { return microphonePublisher == null || microphonePublisher.IsMuted; } }
        public bool IsDisconnecting
        {
            get
            {
                return shuttingDown ||
                       runtimeResourcesDisposed ||
                       reconnecting ||
                       gracefulDisconnectTask != null ||
                       (playerUnavailableDisconnectTask != null && !playerUnavailableDisconnectTask.IsCompleted);
            }
        }
        public string VoiceConnectionId { get { return voiceConnectionId; } }
        public int ActiveSessionCount { get { return sessions.Count; } }

        //* این تابع وابستگی‌های Capture و Playback را بدون Inspector آماده می‌کند.
        public void Initialize()
        {
            if (lifetimeCts != null) return;
            lifetimeCts = new CancellationTokenSource();
            clientInstanceId = ResolveClientInstanceId();

            Debug.Log(
                "VOICE_CLIENT_INSTANCE_CREATED=PASS" +
                " | scope=runtime" +
                " | clientInstanceId=" + clientInstanceId);

            microphonePublisher = GetComponent<VoiceMicrophonePublisher>();
            if (microphonePublisher == null) microphonePublisher = gameObject.AddComponent<VoiceMicrophonePublisher>();
            microphonePublisher.Initialize(bitrateKbps);
            microphonePublisher.FrameEncoded += HandleFrameEncoded;
            microphonePublisher.MuteChanged += HandleMicrophoneMuteChanged;
            microphonePublisher.Failed += HandleFailure;

            playbackManager = GetComponent<VoiceSpatialPlaybackManager>();
            if (playbackManager == null) playbackManager = gameObject.AddComponent<VoiceSpatialPlaybackManager>();
        }

        //* این تابع پس از آماده‌شدن هویت سرور اختصاصی اتصال صوت را آغاز می‌کند و از بستن اشتباهی اتصال سالم جلوگیری می‌کند.
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
        {
            if (shuttingDown || runtimeResourcesDisposed) return false;
            if (!MetaverseNetworkClient.isReady) return false;

            if (IsAuthenticated)
            {
                return true;
            }

            if (
                transport != null &&
                transport.IsConnected &&
                !string.IsNullOrWhiteSpace(voiceConnectionId)
            )
            {
                IsAuthenticated = true;

                Debug.Log(
                    "VOICE_CLIENT_CONNECT_REUSE_EXISTING_AUTHENTICATED=PASS" +
                    " | connectionId=" + voiceConnectionId);

                return true;
            }

            if (connecting) return false;

            string userId = Safe(MetaverseNetworkClient.userId);
            string roomId = Safe(MetaverseNetworkClient.roomId);
            string accessToken = Safe(SecureTokenStorage.GetAccessToken());

            if (!Guid.TryParse(userId, out _) || roomId.Length == 0 || accessToken.Length == 0)
            {
                HandleFailure("Voice identity requires UUID userId, roomId and access token.");
                return false;
            }

            connecting = true;

            try
            {
                if (transport != null)
                {
                    await CloseExistingTransportBeforeFreshConnectAsync(
                        "replace_transport_before_connect",
                        cancellationToken);
                }

                transport = VoiceClientTransportFactory.CreateForCurrentPlatform();
                transport.PacketReceived += HandleTransportPacket;
                transport.Failed += HandleFailure;
                transport.Disconnected += HandleTransportDisconnected;

                outgoingSequence = 0;
                lastReceivedSequence = 0;
                firstVoiceFrameSendLogged = false;
                authenticationCompletion = new TaskCompletionSource<bool>();

                string endpoint = ResolveEndpoint();
                bool connected = await transport.ConnectAsync(endpoint, cancellationToken);
                if (!connected)
                {
                    await CloseTransportAfterFailedConnectAsync("transport_connect_failed");
                    return false;
                }

                if (!MetaverseNetworkClient.isReady)
                {
                    await CloseTransportAfterFailedConnectAsync("player_not_ready_after_voice_transport_connect");
                    return false;
                }

                byte[] authPayload = VoiceClientControlPayload.EncodeAuthRequest(
                    ResolvePlatform(),
                    accessToken,
                    roomId,
                    userId,
                    clientInstanceId,
                    Application.version);

                bool sent = await SendEnvelopeAsync(
                    VoiceClientMessageType.AuthRequest,
                    VoiceClientMessageFlags.AckRequired,
                    VoiceClientEnvelope.EmptyUuid,
                    VoiceClientEnvelope.EmptyUuid,
                    authPayload,
                    cancellationToken);

                if (!sent)
                {
                    await CloseTransportAfterFailedConnectAsync("auth_request_send_failed");
                    return false;
                }

                StatusChanged?.Invoke("VOICE_CLIENT_AUTH_SENT");

                Task timeout = Task.Delay(10000, cancellationToken);
                Task completed = await Task.WhenAny(authenticationCompletion.Task, timeout);
                bool authenticated = completed == authenticationCompletion.Task && await authenticationCompletion.Task;

                if (!authenticated)
                {
                    await CloseTransportAfterFailedConnectAsync("auth_failed_or_timeout");
                }

                return authenticated;
            }
            catch (OperationCanceledException)
            {
                await CloseTransportAfterFailedConnectAsync("voice_connect_cancelled");
                throw;
            }
            finally
            {
                connecting = false;
            }
        }

        //* این تابع قبل از ساخت راه انتقال تازه، اگر اتصال قبلی واقعاً شناسه معتبر دارد آن را با پیام خروج می‌بندد.
        private async Task CloseExistingTransportBeforeFreshConnectAsync(
            string reason,
            CancellationToken cancellationToken)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason)
                ? "voice_replace_existing_transport"
                : reason.Trim();

            IVoiceClientTransport existingTransport = transport;

            if (existingTransport == null) return;

            bool hadAuthenticatedConnection =
                existingTransport.IsConnected &&
                !string.IsNullOrWhiteSpace(voiceConnectionId);

            if (hadAuthenticatedConnection)
            {
                try
                {
                    await SendPublishStopForExitAsync(safeReason, cancellationToken);

                    bool disconnectSent = await SendEnvelopeAsync(
                        VoiceClientMessageType.Disconnect,
                        VoiceClientMessageFlags.None,
                        VoiceClientEnvelope.EmptyUuid,
                        voiceConnectionId,
                        Array.Empty<byte>(),
                        cancellationToken);

                    Debug.Log(
                        "VOICE_CLIENT_PRECONNECT_DISCONNECT_SENT=" +
                        (disconnectSent ? "PASS" : "FAIL") +
                        " | reason=" + safeReason +
                        " | connectionId=" + voiceConnectionId);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "[VoiceClientRuntime] Voice pre-connect disconnect failed: " +
                        exception.Message);
                }
            }

            await CloseDetachedTransportAsync(
                existingTransport,
                "preconnect_replace_" + safeReason);

            ResetVoiceStateAfterExit(true, "preconnect_replace");

            Debug.Log(
                "VOICE_CLIENT_PRECONNECT_OLD_TRANSPORT_CLOSED=PASS" +
                " | hadAuthenticatedConnection=" + hadAuthenticatedConnection +
                " | reason=" + safeReason);
        }

        //* این تابع اتصال نیمه‌کاره Voice را پس از شکست Auth فوری می‌بندد تا تلاش‌های بعدی روی سرور اتصال تکراری نسازند.
        private async Task CloseTransportAfterFailedConnectAsync(string reason)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "voice_connect_failed" : reason.Trim();
            IVoiceClientTransport failedTransport = transport;
            bool closingCurrentTransport = ReferenceEquals(transport, failedTransport);

            if (closingCurrentTransport)
            {
                IsAuthenticated = false;
                publishingAllowed = false;
                authenticationCompletion?.TrySetResult(false);
            }

            if (failedTransport == null)
            {
                Debug.Log("VOICE_CLIENT_FAILED_CONNECT_TRANSPORT_CLOSED=PASS | reason=no_transport | source=" + safeReason);
                return;
            }

            failedTransport.PacketReceived -= HandleTransportPacket;
            failedTransport.Failed -= HandleFailure;
            failedTransport.Disconnected -= HandleTransportDisconnected;

            try
            {
                if (failedTransport.IsConnected)
                {
                    await failedTransport.DisconnectAsync(safeReason, CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[VoiceClientRuntime] Failed Voice transport close failed: " + exception.Message);
            }

            failedTransport.Dispose();

            if (ReferenceEquals(transport, failedTransport))
            {
                transport = null;
                voiceConnectionId = string.Empty;
                previousVoiceConnectionId = string.Empty;
            }

            Debug.Log("VOICE_CLIENT_FAILED_CONNECT_TRANSPORT_CLOSED=PASS | source=" + safeReason);
        }

        //* این تابع خروج عمدی Voice را فقط یک‌بار اجرا می‌کند و نتیجه همان اجرای مشترک را به همه فراخوان‌ها برمی‌گرداند.
        public Task<bool> DisconnectGracefullyAsync(
            string reason,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            lock (gracefulDisconnectSync)
            {
                if (gracefulDisconnectTask != null) return gracefulDisconnectTask;
                gracefulDisconnectTask = RunGracefulDisconnectAsync(reason, timeoutMs, cancellationToken);
                return gracefulDisconnectTask;
            }
        }

        //* این تابع هنگام خارج‌شدن بازیکن از وضعیت آماده، اتصال و همه وضعیت‌های صوتی او را بدون پایان‌دادن به رانتایم پاک می‌کند.
        public Task<bool> DisconnectForPlayerUnavailableAsync(
            string reason,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            lock (gracefulDisconnectSync)
            {
                if (runtimeResourcesDisposed) return Task.FromResult(true);
                if (gracefulDisconnectTask != null) return gracefulDisconnectTask;

                if (playerUnavailableDisconnectTask != null &&
                    !playerUnavailableDisconnectTask.IsCompleted)
                {
                    return playerUnavailableDisconnectTask;
                }

                playerUnavailableDisconnectTask = RunPlayerUnavailableDisconnectAsync(
                    reason,
                    timeoutMs,
                    cancellationToken);

                return playerUnavailableDisconnectTask;
            }
        }

        //* این تابع پاک‌سازی صوت بازیکن خارج‌شده را انجام می‌دهد و پس از آن رانتایم را برای ورود تازه قابل استفاده نگه می‌دارد.
        private async Task<bool> RunPlayerUnavailableDisconnectAsync(
            string reason,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason)
                ? "player_unavailable"
                : reason.Trim();

            int safeTimeoutMs = Math.Max(250, timeoutMs);
            connecting = false;
            reconnecting = true;
            publishingAllowed = false;
            authenticationCompletion?.TrySetResult(false);

            try
            {
                microphonePublisher?.SetMuted(true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[VoiceClientRuntime] Voice player cleanup mic mute failed: " +
                    exception.Message);
            }

            bool disconnectSent = true;
            IVoiceClientTransport activeTransport = transport;

            try
            {
                if (activeTransport != null &&
                    activeTransport.IsConnected &&
                    !string.IsNullOrWhiteSpace(voiceConnectionId))
                {
                    using (CancellationTokenSource timeoutCts =
                           CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutCts.CancelAfter(safeTimeoutMs);

                        await SendPublishStopForExitAsync(safeReason, timeoutCts.Token);

                        disconnectSent = await SendEnvelopeAsync(
                            VoiceClientMessageType.Disconnect,
                            VoiceClientMessageFlags.None,
                            VoiceClientEnvelope.EmptyUuid,
                            voiceConnectionId,
                            Array.Empty<byte>(),
                            timeoutCts.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                disconnectSent = false;
            }
            catch (Exception exception)
            {
                disconnectSent = false;
                Debug.LogWarning(
                    "[VoiceClientRuntime] Voice player cleanup disconnect failed: " +
                    exception.Message);
            }

            try
            {
                if (activeTransport != null)
                {
                    await CloseDetachedTransportAsync(
                        activeTransport,
                        "player_unavailable_" + safeReason);
                }

                ResetVoiceStateAfterExit(false, "player_unavailable");

                Debug.Log(
                    "VOICE_CLIENT_PLAYER_STATE_CLEANUP=PASS" +
                    " | reason=" + safeReason +
                    " | disconnectSent=" + disconnectSent);

                return disconnectSent;
            }
            finally
            {
                reconnecting = false;

                lock (gracefulDisconnectSync)
                {
                    playerUnavailableDisconnectTask = null;
                }
            }
        }

        //* این تابع پیش از بسته‌شدن راه انتقال، پیام پروتکلی DISCONNECT را می‌فرستد و بسته‌شدن سمت سرور را تا مهلت محدود انتظار می‌کشد.
        private async Task<bool> RunGracefulDisconnectAsync(
            string reason,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "voice_client_exit" : reason.Trim();
            int safeTimeoutMs = Math.Max(250, timeoutMs);
            shuttingDown = true;
            connecting = false;
            reconnecting = false;
            publishingAllowed = false;
            authenticationCompletion?.TrySetResult(false);

            try
            {
                microphonePublisher?.SetMuted(true);
                Debug.Log("VOICE_CLIENT_EXIT_MIC_MUTED=PASS | reason=" + safeReason);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[VoiceClientRuntime] Voice exit mic mute failed: " + exception.Message);
            }

            IVoiceClientTransport activeTransport = transport;
            if (activeTransport == null || !activeTransport.IsConnected || string.IsNullOrWhiteSpace(voiceConnectionId))
            {
                if (activeTransport != null)
                {
                    await CloseDetachedTransportAsync(activeTransport, "graceful_disconnect_without_auth_" + safeReason);
                }

                ResetVoiceStateAfterExit(false, "graceful_no_auth_" + safeReason);
                Debug.Log("VOICE_CLIENT_DISCONNECT_SKIPPED=PASS | reason=no_active_authenticated_transport | source=" + safeReason);
                return true;
            }

            transportDisconnectCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            bool disconnectSent = false;
            bool serverCloseConfirmed = false;

            using (CancellationTokenSource timeoutCts =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(safeTimeoutMs);

                try
                {
                    await SendPublishStopForExitAsync(safeReason, timeoutCts.Token);

                    disconnectSent = await SendEnvelopeAsync(
                        VoiceClientMessageType.Disconnect,
                        VoiceClientMessageFlags.None,
                        VoiceClientEnvelope.EmptyUuid,
                        voiceConnectionId,
                        Array.Empty<byte>(),
                        timeoutCts.Token);

                    Debug.Log(
                        "VOICE_CLIENT_DISCONNECT_SENT=" + (disconnectSent ? "PASS" : "FAIL") +
                        " | reason=" + safeReason +
                        " | connectionId=" + voiceConnectionId);

                    if (disconnectSent)
                    {
                        Task timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                        Task completedTask = await Task.WhenAny(transportDisconnectCompletion.Task, timeoutTask);

                        if (completedTask == transportDisconnectCompletion.Task)
                        {
                            serverCloseConfirmed = await transportDisconnectCompletion.Task;
                        }
                        else if (cancellationToken.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            Debug.LogWarning(
                                "VOICE_CLIENT_DISCONNECT_TIMEOUT=FAIL | reason=" + safeReason +
                                " | timeoutMs=" + safeTimeoutMs);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Debug.LogWarning(
                        "VOICE_CLIENT_DISCONNECT_TIMEOUT=FAIL | reason=" + safeReason +
                        " | timeoutMs=" + safeTimeoutMs);
                }
                catch (Exception exception)
                {
                    HandleFailure("Voice graceful disconnect failed: " + exception.Message);
                }
            }

            Debug.Log(
                "VOICE_CLIENT_DISCONNECT_SERVER_CLOSE=" + (serverCloseConfirmed ? "PASS" : "FAIL") +
                " | reason=" + safeReason +
                " | timeoutMs=" + safeTimeoutMs);

            await CloseDetachedTransportAsync(activeTransport, "graceful_disconnect_local_close");
            ResetVoiceStateAfterExit(false, "graceful_disconnect_" + safeReason);

            return disconnectSent && serverCloseConfirmed;
        }

        //* این تابع هنگام خروج قطعی، در صورت فعال بودن انتشار صدا، پیام توقف انتشار را قبل از قطع راه انتقال می‌فرستد.
        private async Task<bool> SendPublishStopForExitAsync(string safeReason, CancellationToken cancellationToken)
        {
            if (!IsAuthenticated || string.IsNullOrWhiteSpace(voiceConnectionId)) return true;

            bool sent = await SendEnvelopeAsync(
                VoiceClientMessageType.PublishStop,
                VoiceClientMessageFlags.AckRequired | VoiceClientMessageFlags.EndOfStream,
                VoiceClientEnvelope.EmptyUuid,
                voiceConnectionId,
                VoiceClientControlPayload.EncodePublishStop(1),
                cancellationToken);

            Debug.Log(
                "VOICE_CLIENT_EXIT_PUBLISH_STOP=" + (sent ? "PASS" : "FAIL") +
                " | reason=" + safeReason);

            return sent;
        }

        //* این تابع راه انتقال انتخاب‌شده را بدون فعال‌کردن بازیابی خودکار از Runtime جدا و پاک می‌کند.
        private async Task CloseDetachedTransportAsync(IVoiceClientTransport activeTransport, string reason)
        {
            if (activeTransport == null) return;

            activeTransport.PacketReceived -= HandleTransportPacket;
            activeTransport.Failed -= HandleFailure;
            activeTransport.Disconnected -= HandleTransportDisconnected;

            try
            {
                if (activeTransport.IsConnected)
                {
                    await activeTransport.DisconnectAsync(reason, CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[VoiceClientRuntime] Voice local transport close failed: " + exception.Message);
            }

            activeTransport.Dispose();
            if (ReferenceEquals(transport, activeTransport)) transport = null;
        }

        //* این تابع وضعیت نشست‌ها و شناسه‌های اتصال صوت را پس از خروج قطعی پاک می‌کند.
        private void ResetVoiceStateAfterExit(bool preserveRecordingConsentDesired, string reason)
        {
            bool previousRecordingConsentDesired = recordingConsentDesired;

            foreach (string sessionId in new List<string>(sessions.Keys))
            {
                playbackManager?.RemoveSession(sessionId);
            }

            while (receivedPackets.TryDequeue(out _)) { }
            while (pendingVoiceFrames.TryDequeue(out _)) { }
            recordingConsentSendInFlightSessionIds.Clear();
            pendingVoiceFrameDrops = 0;
            staleVoiceFrameDrops = 0;

            IsAuthenticated = false;
            publishingAllowed = false;
            voiceConnectionId = string.Empty;
            previousVoiceConnectionId = string.Empty;
            disconnectedAtMs = 0;
            outgoingSequence = 0;
            previousLastReceivedSequence = 0;
            previousLastPublishedSequence = 0;
            lastReceivedSequence = 0;
            lastPublishedSequence = 0;
            sessions.Clear();
            peerConnectionByUserId.Clear();
            recordingConsentSentSessionIds.Clear();
            recordingConsentSendInFlightSessionIds.Clear();
            recordingConsentDesired = preserveRecordingConsentDesired && previousRecordingConsentDesired;
            transportDisconnectCompletion = null;
            authenticationCompletion?.TrySetResult(false);

            Debug.Log(
                "VOICE_CLIENT_RECORDING_CONSENT_DESIRED_RESET=PASS" +
                " | preserve=" + preserveRecordingConsentDesired +
                " | previousDesired=" + previousRecordingConsentDesired +
                " | desired=" + recordingConsentDesired +
                " | reason=" + Safe(reason));
        }

        private void Update()
        {
            while (receivedPackets.TryDequeue(out byte[] packet))
            {
                try { ProcessPacket(packet); }
                catch (Exception exception) { HandleFailure("Voice packet failed: " + exception.Message); }
            }
        }

        //* این تابع Mic Mute را به Capture واقعی و PUBLISH_START/STOP متصل می‌کند.
        public void SetMicrophoneMuted(bool muted)
        {
            microphonePublisher?.SetMuted(muted);
        }

        //* این تابع Speaker Off را هم روی سرور و هم صف Playback محلی اعمال می‌کند.
        public async void SetSpeakerOff(bool disabled)
        {
            playbackManager?.SetSpeakerOff(disabled);
            await SendMuteAsync(VoiceClientMuteKind.SpeakerOff, disabled, VoiceClientEnvelope.EmptyUuid);
        }

        //* این تابع Mute All Incoming را بدون تغییر مسیر برگشت اعمال می‌کند.
        public async void SetMuteAllIncoming(bool muted)
        {
            await SendMuteAsync(VoiceClientMuteKind.MuteAll, muted, VoiceClientEnvelope.EmptyUuid);
        }

        //* این تابع صدای یک userId را پس از دریافت connectionId همان Session یک‌طرفه قطع می‌کند.
        public async void SetUserMuted(string userId, bool muted)
        {
            if (!peerConnectionByUserId.TryGetValue(Safe(userId), out string targetConnectionId))
            {
                HandleFailure("Voice peer connectionId is not known yet for this userId.");
                return;
            }

            await SendMuteAsync(VoiceClientMuteKind.PerUser, muted, targetConnectionId);
        }

        //* این تابع رضایت ضبط همان Session را با Envelope دارای sessionId ارسال می‌کند و انتخاب کاربر را برای Sessionهای بعدی همین اتصال نگه می‌دارد.
        public void SetRecordingConsent(string sessionId, bool consented)
        {
            string safeSessionId = Safe(sessionId);
            if (string.IsNullOrWhiteSpace(safeSessionId) || !sessions.ContainsKey(safeSessionId)) return;

            recordingConsentDesired = consented;
            _ = SendRecordingConsentAsync(safeSessionId, consented, "manual_session");
        }

        //* این تابع رضایت یکسان را روی تمام Sessionهای فعال کاربر اعمال می‌کند و برای re-enter بعدی هم به‌صورت خودکار نگه می‌دارد.
        public void SetRecordingConsentForAll(bool consented)
        {
            recordingConsentDesired = consented;
            if (!consented)
            {
                recordingConsentSentSessionIds.Clear();
                recordingConsentSendInFlightSessionIds.Clear();
            }

            foreach (string sessionId in new List<string>(sessions.Keys))
                _ = SendRecordingConsentAsync(sessionId, consented, "manual_all");
        }

        private async Task SendRecordingConsentAsync(string sessionId, bool consented, string reason)
        {
            string safeSessionId = Safe(sessionId);
            if (!IsAuthenticated || shuttingDown || runtimeResourcesDisposed) return;
            if (string.IsNullOrWhiteSpace(safeSessionId) || !sessions.ContainsKey(safeSessionId)) return;

            bool markInFlight = consented;
            if (markInFlight)
            {
                if (recordingConsentSentSessionIds.Contains(safeSessionId) ||
                    recordingConsentSendInFlightSessionIds.Contains(safeSessionId))
                {
                    Debug.Log(
                        "VOICE_CLIENT_RECORDING_CONSENT_SEND_SKIPPED=PASS" +
                        " | sessionId=" + safeSessionId +
                        " | reason=already_sent_or_in_flight" +
                        " | requestedReason=" + Safe(reason) +
                        " | desired=" + recordingConsentDesired +
                        " | sentSessions=" + recordingConsentSentSessionIds.Count +
                        " | inFlight=" + recordingConsentSendInFlightSessionIds.Count);
                    return;
                }

                recordingConsentSendInFlightSessionIds.Add(safeSessionId);
            }

            bool sent = false;
            try
            {
                sent = await SendEnvelopeAsync(
                    VoiceClientMessageType.RecordingConsentChanged,
                    VoiceClientMessageFlags.AckRequired,
                    safeSessionId,
                    voiceConnectionId,
                    VoiceClientControlPayload.EncodeRecordingConsent(consented),
                    lifetimeCts.Token);

                if (sent && consented) recordingConsentSentSessionIds.Add(safeSessionId);
                if (sent && !consented)
                {
                    recordingConsentSentSessionIds.Remove(safeSessionId);
                    recordingConsentSendInFlightSessionIds.Remove(safeSessionId);
                }
            }
            finally
            {
                if (markInFlight) recordingConsentSendInFlightSessionIds.Remove(safeSessionId);
            }

            Debug.Log(
                "VOICE_CLIENT_RECORDING_CONSENT_SEND=" + (sent ? "PASS" : "FAIL") +
                " | sessionId=" + safeSessionId +
                " | consented=" + consented +
                " | reason=" + Safe(reason) +
                " | desired=" + recordingConsentDesired +
                " | sentSessions=" + recordingConsentSentSessionIds.Count +
                " | inFlight=" + recordingConsentSendInFlightSessionIds.Count);
        }

        private void TryAutoSendRecordingConsentForSession(string sessionId, string reason)
        {
            string safeSessionId = Safe(sessionId);
            if (!recordingConsentDesired) return;
            if (string.IsNullOrWhiteSpace(safeSessionId) || !sessions.ContainsKey(safeSessionId)) return;
            if (recordingConsentSentSessionIds.Contains(safeSessionId)) return;
            if (recordingConsentSendInFlightSessionIds.Contains(safeSessionId)) return;

            Debug.Log(
                "VOICE_CLIENT_RECORDING_CONSENT_AUTO_RESEND=PASS" +
                " | sessionId=" + safeSessionId +
                " | reason=" + Safe(reason));

            _ = SendRecordingConsentAsync(safeSessionId, true, "auto_" + Safe(reason));
        }

        private void TryAutoSendRecordingConsentForAll(string reason)
        {
            if (!recordingConsentDesired) return;
            recordingConsentSentSessionIds.RemoveWhere(sessionId => !sessions.ContainsKey(sessionId));

            foreach (string sessionId in new List<string>(sessions.Keys))
                TryAutoSendRecordingConsentForSession(sessionId, reason);
        }

        private async Task SendMuteAsync(VoiceClientMuteKind kind, bool muted, string targetConnectionId)
        {
            if (!IsAuthenticated) return;
            await SendEnvelopeAsync(
                VoiceClientMessageType.ListenerMuteChanged,
                VoiceClientMessageFlags.AckRequired,
                VoiceClientEnvelope.EmptyUuid,
                voiceConnectionId,
                VoiceClientControlPayload.EncodeMute(kind, muted, targetConnectionId),
                lifetimeCts.Token);
        }

        private void HandleTransportPacket(byte[] packet)
        {
            if (packet != null) receivedPackets.Enqueue(packet);
        }

        //* این تابع پیام‌های Auth، Session، Heartbeat و Frame را روی Thread اصلی پردازش می‌کند.
        private void ProcessPacket(byte[] packet)
        {
            VoiceClientEnvelope envelope = VoiceClientEnvelope.Decode(packet);
            if (envelope.Sequence == 0 || envelope.Sequence <= lastReceivedSequence)
                throw new InvalidOperationException("Voice incoming sequence is not increasing.");
            lastReceivedSequence = envelope.Sequence;

            if (envelope.MessageType == VoiceClientMessageType.AuthResult)
            {
                HandleAuthResult(envelope);
                return;
            }

            if (envelope.MessageType == VoiceClientMessageType.Heartbeat)
            {
                _ = SendEnvelopeAsync(
                    VoiceClientMessageType.HeartbeatAck,
                    VoiceClientMessageFlags.None,
                    VoiceClientEnvelope.EmptyUuid,
                    voiceConnectionId,
                    VoiceClientControlPayload.EncodeHeartbeatAck(envelope.Sequence),
                    lifetimeCts.Token);
                return;
            }

            if (envelope.MessageType == VoiceClientMessageType.SessionJoined)
            {
                VoiceClientSessionDescriptor descriptor = VoiceClientControlPayload.DecodeSessionDescriptor(envelope.Payload);
                ActiveVoiceSession activeSession;
                bool isNewSession = !sessions.TryGetValue(
                    descriptor.SessionId,
                    out activeSession);

                if (isNewSession)
                {
                    activeSession = new ActiveVoiceSession(descriptor.SessionId);
                    sessions.Add(descriptor.SessionId, activeSession);
                }

                activeSession.Merge(descriptor);
                if (isNewSession)
                {
                    recordingConsentSentSessionIds.Remove(descriptor.SessionId);
                    recordingConsentSendInFlightSessionIds.Remove(descriptor.SessionId);
                }

                Debug.Log(
                    "VOICE_CLIENT_SESSION_JOINED=PASS" +
                    " | sessionId=" + descriptor.SessionId +
                    " | activeSessionCount=" + sessions.Count +
                    " | peerCount=" + activeSession.PeerCount +
                    " | isNewSession=" + isNewSession +
                    " | recordingConsentDesired=" + recordingConsentDesired +
                    " | consentSent=" + recordingConsentSentSessionIds.Contains(descriptor.SessionId) +
                    " | consentInFlight=" + recordingConsentSendInFlightSessionIds.Contains(descriptor.SessionId));

                if (isNewSession) TryAutoSendRecordingConsentForSession(descriptor.SessionId, "session_joined");
                return;
            }

            if (envelope.MessageType == VoiceClientMessageType.ReconnectResult)
            {
                VoiceClientReconnectResult result = VoiceClientControlPayload.DecodeReconnectResult(envelope.Payload);
                if (!result.Success)
                {
                    previousVoiceConnectionId = string.Empty;
                    sessions.Clear();
                    HandleFailure("Voice reconnect was not resumed: " + result.Code + " | " + result.Message);
                    return;
                }

                previousVoiceConnectionId = string.Empty;
                previousLastReceivedSequence = 0;
                previousLastPublishedSequence = 0;
                StatusChanged?.Invoke("VOICE_CLIENT_RECONNECTED");
                return;
            }

            if (envelope.MessageType == VoiceClientMessageType.SessionSnapshot)
            {
                sessions.Clear();
                foreach (VoiceClientSessionDescriptor descriptor in
                    VoiceClientControlPayload.DecodeSessionSnapshot(envelope.Payload))
                {
                    ActiveVoiceSession activeSession;
                    if (!sessions.TryGetValue(
                            descriptor.SessionId,
                            out activeSession))
                    {
                        activeSession = new ActiveVoiceSession(descriptor.SessionId);
                        sessions.Add(descriptor.SessionId, activeSession);
                    }

                    activeSession.Merge(descriptor);
                }

                Debug.Log(
                    "VOICE_CLIENT_SESSION_SNAPSHOT=PASS" +
                    " | activeSessionCount=" + sessions.Count +
                    " | recordingConsentDesired=" + recordingConsentDesired);

                recordingConsentSendInFlightSessionIds.RemoveWhere(sessionId => !sessions.ContainsKey(sessionId));
                TryAutoSendRecordingConsentForAll("session_snapshot");
                return;
            }

            if (envelope.MessageType == VoiceClientMessageType.SessionClosed)
            {
                RemoveCompleteSession(envelope.SessionId);
                return;
            }

            if (envelope.MessageType == VoiceClientMessageType.SessionLeft)
            {
                string leavingConnectionId = Safe(envelope.SenderId);
                bool removeCompleteSession =
                    leavingConnectionId.Length == 0 ||
                    string.Equals(
                        leavingConnectionId,
                        VoiceClientEnvelope.EmptyUuid,
                        StringComparison.OrdinalIgnoreCase);

                ActiveVoiceSession activeSession;
                if (removeCompleteSession ||
                    !sessions.TryGetValue(envelope.SessionId, out activeSession))
                {
                    RemoveCompleteSession(envelope.SessionId);
                    return;
                }

                string removedPeerUserId;
                activeSession.RemovePeer(
                    leavingConnectionId,
                    out removedPeerUserId);
                playbackManager?.RemoveSender(
                    envelope.SessionId,
                    leavingConnectionId);

                Debug.Log(
                    "VOICE_CLIENT_GROUP_MEMBER_LEFT=PASS" +
                    " | sessionId=" + envelope.SessionId +
                    " | leavingConnectionId=" + leavingConnectionId +
                    " | leavingUserId=" + Safe(removedPeerUserId) +
                    " | remainingPeerCount=" + activeSession.PeerCount);
                return;
            }

            if (envelope.MessageType == VoiceClientMessageType.VoiceFrame)
            {
                if (!string.IsNullOrWhiteSpace(voiceConnectionId) &&
                    string.Equals(envelope.SenderId, voiceConnectionId, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning(
                        "VOICE_CLIENT_SELF_FRAME_DROPPED=PASS" +
                        " | sessionId=" + envelope.SessionId +
                        " | senderId=" + envelope.SenderId +
                        " | voiceConnectionId=" + voiceConnectionId);
                    return;
                }

                ActiveVoiceSession activeSession;
                string peerUserId;
                if (sessions.TryGetValue(envelope.SessionId, out activeSession) &&
                    activeSession.TryResolvePeerUserId(
                        envelope.SenderId,
                        out peerUserId))
                {
                    playbackManager?.ReceiveFrame(
                        envelope.SessionId,
                        envelope.SenderId,
                        peerUserId,
                        envelope.Payload);
                    peerConnectionByUserId[peerUserId] = envelope.SenderId;
                }
                else
                {
                    Debug.LogWarning(
                        "VOICE_CLIENT_GROUP_PEER_MAPPING_MISSING" +
                        " | sessionId=" + envelope.SessionId +
                        " | senderConnectionId=" + envelope.SenderId);
                }
            }
        }

        //* این تابع کل Session و تمام Streamهای per-sender آن را فقط برای Leave کامل یا Close پاک می‌کند.
        private void RemoveCompleteSession(string sessionId)
        {
            string safeSessionId = Safe(sessionId);
            if (safeSessionId.Length == 0) return;

            sessions.Remove(safeSessionId);
            recordingConsentSentSessionIds.Remove(safeSessionId);
            recordingConsentSendInFlightSessionIds.Remove(safeSessionId);
            playbackManager?.RemoveSession(safeSessionId);
        }

        private async void HandleAuthResult(VoiceClientEnvelope envelope)
        {
            VoiceClientAuthResult result = VoiceClientControlPayload.DecodeAuthResult(envelope.Payload);
            if (!result.Success)
            {
                authenticationCompletion?.TrySetResult(false);
                HandleFailure("Voice authentication failed: " + result.Code + " | " + result.Message);
                return;
            }

            if (!MetaverseNetworkClient.isReady)
            {
                authenticationCompletion?.TrySetResult(false);
                return;
            }

            voiceConnectionId = result.VoiceConnectionId;
            IsAuthenticated = true;
            authenticationCompletion?.TrySetResult(true);
            StatusChanged?.Invoke("VOICE_CLIENT_AUTHENTICATED");

            if (!string.IsNullOrWhiteSpace(previousVoiceConnectionId))
            {
                byte[] reconnect = VoiceClientControlPayload.EncodeReconnectRequest(
                    previousVoiceConnectionId,
                    clientInstanceId,
                    previousLastReceivedSequence,
                    previousLastPublishedSequence,
                    disconnectedAtMs,
                    SecureTokenStorage.GetAccessToken(),
                    MetaverseNetworkClient.roomId,
                    MetaverseNetworkClient.userId);

                await SendEnvelopeAsync(
                    VoiceClientMessageType.ReconnectRequest,
                    VoiceClientMessageFlags.AckRequired,
                    VoiceClientEnvelope.EmptyUuid,
                    voiceConnectionId,
                    reconnect,
                    lifetimeCts.Token);
            }

            if (microphonePublisher != null && !microphonePublisher.IsMuted)
            {
                HandleMicrophoneMuteChanged(false);
            }
        }

        private async void HandleMicrophoneMuteChanged(bool muted)
        {
            if (!IsAuthenticated || shuttingDown) return;
            publishingAllowed = false;
            firstVoiceFrameSendLogged = false;

            if (muted)
            {
                while (pendingVoiceFrames.TryDequeue(out _)) { }

                bool stopSent = await SendEnvelopeAsync(
                    VoiceClientMessageType.PublishStop,
                    VoiceClientMessageFlags.AckRequired,
                    VoiceClientEnvelope.EmptyUuid,
                    voiceConnectionId,
                    VoiceClientControlPayload.EncodePublishStop(1),
                    lifetimeCts.Token);

                Debug.Log(
                    "VOICE_CLIENT_PUBLISH_STOP_SEND=" +
                    (stopSent ? "PASS" : "FAIL") +
                    " | reason=mic_muted" +
                    " | endOfStream=False");
                return;
            }

            bool sent = await SendEnvelopeAsync(
                VoiceClientMessageType.PublishStart,
                VoiceClientMessageFlags.AckRequired,
                VoiceClientEnvelope.EmptyUuid,
                voiceConnectionId,
                VoiceClientControlPayload.EncodePublishStart(bitrateKbps),
                lifetimeCts.Token);
            publishingAllowed =
                sent &&
                IsAuthenticated &&
                !shuttingDown &&
                !runtimeResourcesDisposed &&
                microphonePublisher != null &&
                !microphonePublisher.IsMuted;

            Debug.Log(
                "VOICE_CLIENT_PUBLISH_START_SEND=" +
                (publishingAllowed ? "PASS" : "FAIL"));
        }

        private void HandleFrameEncoded(byte[] packet, bool dtx)
        {
            if (!MetaverseNetworkClient.isReady || !IsAuthenticated || !publishingAllowed) return;
            if (packet == null || packet.Length == 0) return;

            while (pendingVoiceFrames.Count >= MaxPendingVoiceFrames &&
                   pendingVoiceFrames.TryDequeue(out _))
            {
                int dropped = Interlocked.Increment(ref pendingVoiceFrameDrops);
                if (dropped == 1 || dropped % 25 == 0)
                {
                    Debug.LogWarning(
                        "VOICE_CLIENT_VOICE_FRAME_QUEUE_DROP=PASS" +
                        " | reason=queue_full" +
                        " | totalDropped=" + dropped +
                        " | pending=" + pendingVoiceFrames.Count +
                        " | maxPending=" + MaxPendingVoiceFrames);
                }
            }

            pendingVoiceFrames.Enqueue(new PendingVoiceFrame
            {
                Packet = packet,
                Dtx = dtx,
                EnqueuedAtMs = UnixTimeMs()
            });

            EnsureVoiceFramePumpRunning();
        }

        private void EnsureVoiceFramePumpRunning()
        {
            lock (voiceFramePumpSync)
            {
                if (voiceFramePumpTask != null && !voiceFramePumpTask.IsCompleted) return;

                CancellationToken cancellationToken = lifetimeCts != null
                    ? lifetimeCts.Token
                    : CancellationToken.None;

                voiceFramePumpTask = Task.Run(() => PumpVoiceFramesAsync(cancellationToken), cancellationToken);

                Debug.Log(
                    "VOICE_CLIENT_VOICE_FRAME_PUMP_STARTED=PASS" +
                    " | maxPending=" + MaxPendingVoiceFrames +
                    " | maxAgeMs=" + MaxQueuedVoiceFrameAgeMs);
            }
        }

        private async Task PumpVoiceFramesAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!MetaverseNetworkClient.isReady ||
                    !IsAuthenticated ||
                    !publishingAllowed ||
                    shuttingDown ||
                    runtimeResourcesDisposed)
                {
                    while (pendingVoiceFrames.TryDequeue(out _)) { }
                    break;
                }

                if (!pendingVoiceFrames.TryDequeue(out PendingVoiceFrame frame))
                {
                    lock (voiceFramePumpSync)
                    {
                        if (pendingVoiceFrames.IsEmpty)
                        {
                            voiceFramePumpTask = null;
                            return;
                        }
                    }

                    continue;
                }

                ulong nowMs = UnixTimeMs();
                ulong queueAgeMs = nowMs >= frame.EnqueuedAtMs ? nowMs - frame.EnqueuedAtMs : 0UL;
                if (queueAgeMs > MaxQueuedVoiceFrameAgeMs)
                {
                    int dropped = Interlocked.Increment(ref staleVoiceFrameDrops);
                    if (dropped == 1 || dropped % 25 == 0)
                    {
                        Debug.LogWarning(
                            "VOICE_CLIENT_VOICE_FRAME_QUEUE_DROP=PASS" +
                            " | reason=stale" +
                            " | ageMs=" + queueAgeMs +
                            " | totalDropped=" + dropped +
                            " | pending=" + pendingVoiceFrames.Count);
                    }

                    continue;
                }

                bool sent = await SendQueuedVoiceFrameAsync(frame, queueAgeMs, cancellationToken);
                if (!sent && (!IsAuthenticated || !publishingAllowed || shuttingDown || runtimeResourcesDisposed))
                {
                    while (pendingVoiceFrames.TryDequeue(out _)) { }
                    break;
                }
            }

            lock (voiceFramePumpSync)
            {
                if (pendingVoiceFrames.IsEmpty) voiceFramePumpTask = null;
            }
        }

        private async Task<bool> SendQueuedVoiceFrameAsync(
            PendingVoiceFrame frame,
            ulong queueAgeMs,
            CancellationToken cancellationToken)
        {
            bool sent = await SendEnvelopeAsync(
                VoiceClientMessageType.VoiceFrame,
                frame.Dtx ? VoiceClientMessageFlags.Dtx : VoiceClientMessageFlags.None,
                VoiceClientEnvelope.EmptyUuid,
                voiceConnectionId,
                frame.Packet,
                cancellationToken);
            if (!sent) return false;

            lastPublishedSequence = outgoingSequence;

            if (!firstVoiceFrameSendLogged)
            {
                firstVoiceFrameSendLogged = true;
                Debug.Log(
                    "VOICE_CLIENT_FIRST_FRAME_SENT=PASS" +
                    " | bytes=" + frame.Packet.Length +
                    " | dtx=" + frame.Dtx +
                    " | queueAgeMs=" + queueAgeMs +
                    " | pending=" + pendingVoiceFrames.Count);
            }

            return true;
        }

        //* این تابع پس از بسته‌شدن راه انتقال فقط وضعیت صوت را پاک می‌کند؛ تصمیم اتصال دوباره فقط از وضعیت آماده بازیکن گرفته می‌شود.
        private async void HandleTransportDisconnected(string reason)
        {
            transportDisconnectCompletion?.TrySetResult(true);

            string reconnectVoiceConnectionId = voiceConnectionId;
            uint reconnectLastReceivedSequence = lastReceivedSequence;
            uint reconnectLastPublishedSequence = lastPublishedSequence;
            ulong reconnectDisconnectedAtMs = UnixTimeMs();

            IsAuthenticated = false;
            publishingAllowed = false;

            if (shuttingDown)
            {
                return;
            }

            if (reconnecting) return;
            reconnecting = true;

            try
            {
                IVoiceClientTransport disconnectedTransport = transport;

                if (disconnectedTransport != null)
                {
                    await CloseDetachedTransportAsync(
                        disconnectedTransport,
                        "voice_transport_closed_" + Safe(reason));
                }

                ResetVoiceStateAfterExit(true, "transport_disconnected_" + Safe(reason));

                if (!string.IsNullOrWhiteSpace(reconnectVoiceConnectionId))
                {
                    previousVoiceConnectionId = reconnectVoiceConnectionId;
                    previousLastReceivedSequence = reconnectLastReceivedSequence;
                    previousLastPublishedSequence = reconnectLastPublishedSequence;
                    disconnectedAtMs = reconnectDisconnectedAtMs;

                    Debug.Log(
                        "VOICE_CLIENT_RECONNECT_STATE_CAPTURED=PASS" +
                        " | previousConnectionId=" + previousVoiceConnectionId +
                        " | lastReceived=" + previousLastReceivedSequence +
                        " | lastPublished=" + previousLastPublishedSequence +
                        " | disconnectedAtMs=" + disconnectedAtMs);
                }

                StatusChanged?.Invoke("VOICE_CLIENT_WAITING_FOR_PLAYER_READY");
            }
            catch (Exception exception)
            {
                HandleFailure("Voice transport cleanup failed: " + exception.Message);
            }
            finally
            {
                reconnecting = false;
            }
        }

        private async Task<bool> SendEnvelopeAsync(
            VoiceClientMessageType messageType,
            VoiceClientMessageFlags flags,
            string sessionId,
            string senderId,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            IVoiceClientTransport targetTransport = transport;
            if (targetTransport == null || !targetTransport.IsConnected) return false;

            bool lockTaken = false;

            try
            {
                await sendLock.WaitAsync(cancellationToken);
                lockTaken = true;

                if (!ReferenceEquals(transport, targetTransport) || !targetTransport.IsConnected)
                    return false;

                uint sequence = ++outgoingSequence;
                VoiceClientEnvelope envelope = new VoiceClientEnvelope
                {
                    MessageType = messageType,
                    Flags = flags,
                    Sequence = sequence,
                    TimestampMs = UnixTimeMs(),
                    SessionId = sessionId,
                    SenderId = senderId,
                    Payload = payload
                };

                return await targetTransport.SendAsync(envelope.Encode(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                HandleFailure("Voice send failed: " + exception.Message);
                return false;
            }
            finally
            {
                if (lockTaken) sendLock.Release();
            }
        }

        private sealed class ActiveVoiceSession
        {
            private readonly Dictionary<string, string> peerUserIdByConnectionId =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> legacyPeerUserIds =
                new HashSet<string>(StringComparer.Ordinal);

            public string SessionId { get; private set; }
            public int PeerCount
            {
                get
                {
                    return peerUserIdByConnectionId.Count +
                           legacyPeerUserIds.Count;
                }
            }

            public ActiveVoiceSession(string sessionId)
            {
                SessionId = Safe(sessionId);
                if (SessionId.Length == 0)
                {
                    throw new ArgumentException(
                        "Active Voice session requires sessionId.",
                        "sessionId");
                }
            }

            public void Merge(VoiceClientSessionDescriptor descriptor)
            {
                if (descriptor == null ||
                    !string.Equals(
                        SessionId,
                        Safe(descriptor.SessionId),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "Voice session descriptor does not match active session.",
                        "descriptor");
                }

                string peerUserId = Safe(descriptor.PeerUserId);
                string peerConnectionId = Safe(descriptor.PeerConnectionId);
                if (peerUserId.Length == 0)
                {
                    throw new ArgumentException(
                        "Voice session descriptor requires peerUserId.",
                        "descriptor");
                }

                if (peerConnectionId.Length == 0 ||
                    string.Equals(
                        peerConnectionId,
                        VoiceClientEnvelope.EmptyUuid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    legacyPeerUserIds.Add(peerUserId);
                    return;
                }

                peerUserIdByConnectionId[peerConnectionId] = peerUserId;
                legacyPeerUserIds.Remove(peerUserId);
            }

            public bool TryResolvePeerUserId(
                string senderConnectionId,
                out string peerUserId)
            {
                string safeConnectionId = Safe(senderConnectionId);
                if (peerUserIdByConnectionId.TryGetValue(
                        safeConnectionId,
                        out peerUserId))
                {
                    return true;
                }

                if (legacyPeerUserIds.Count == 1)
                {
                    string resolvedLegacyPeerUserId = string.Empty;
                    foreach (string candidatePeerUserId in legacyPeerUserIds)
                    {
                        resolvedLegacyPeerUserId = candidatePeerUserId;
                        break;
                    }

                    peerUserId = resolvedLegacyPeerUserId;
                    peerUserIdByConnectionId[safeConnectionId] =
                        resolvedLegacyPeerUserId;
                    legacyPeerUserIds.Clear();
                    return true;
                }

                peerUserId = string.Empty;
                return false;
            }

            public bool RemovePeer(
                string connectionId,
                out string peerUserId)
            {
                string safeConnectionId = Safe(connectionId);
                if (!peerUserIdByConnectionId.TryGetValue(
                        safeConnectionId,
                        out peerUserId))
                {
                    peerUserId = string.Empty;
                    return false;
                }

                peerUserIdByConnectionId.Remove(safeConnectionId);
                return true;
            }

            public IReadOnlyList<string> CreatePeerUserIdSnapshot()
            {
                HashSet<string> peerUserIds =
                    new HashSet<string>(legacyPeerUserIds, StringComparer.Ordinal);

                foreach (string peerUserId in peerUserIdByConnectionId.Values)
                {
                    peerUserIds.Add(peerUserId);
                }

                return new List<string>(peerUserIds);
            }
        }

        private struct PendingVoiceFrame
        {
            public byte[] Packet;
            public bool Dtx;
            public ulong EnqueuedAtMs;
        }

        private static VoiceClientPlatform ResolvePlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return VoiceClientPlatform.WebGl;
#elif UNITY_ANDROID && !UNITY_EDITOR
            return VoiceClientPlatform.Quest;
#else
            return VoiceClientPlatform.Windows;
#endif
        }

        private static string ResolveEndpoint()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string url = ServerConfig.RealtimeWebSocketUrl;
            return url + (url.Contains("?") ? "&" : "?") + "transport=voice";
#else
            return ServerConfig.BuildRealtimeGrpcStreamingTarget();
#endif
        }

        private static string ResolveClientInstanceId()
        {
            return Guid.NewGuid().ToString("D");
        }

        private static ulong UnixTimeMs()
        {
            return (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private void HandleFailure(string message)
        {
            Failed?.Invoke(message);
            Debug.LogWarning("[VoiceClientRuntime] " + message);
        }

        //* این تابع هنگام غیرفعال‌شدن Runtime، خروج صوتی را زودتر از نابودی آبجکت شروع می‌کند.
        private void OnDisable()
        {
            if (!Application.isPlaying || runtimeResourcesDisposed) return;

            Debug.Log("VOICE_CLIENT_RUNTIME_DISABLE_CLEANUP=START");
            _ = DisconnectGracefullyAsync("runtime_disabled", 1500, CancellationToken.None);
        }

        //* این تابع هنگام خروج آنی برنامه، قطع صوت را بدون انتظار به مسیر خروج می‌فرستد.
        private void OnApplicationQuit()
        {
            applicationQuitRequested = true;

            Debug.Log("VOICE_CLIENT_APPLICATION_QUIT_CLEANUP=START");
            _ = DisconnectGracefullyAsync("application_quit", 1200, CancellationToken.None);
        }

        //* این تابع هنگام نابودی Runtime ابتدا خروج پروتکلی محدود را اجرا و سپس همه اشتراک‌ها و منابع را آزاد می‌کند.
        private async void OnDestroy()
        {
            try
            {
                await DisconnectGracefullyAsync(
                    "runtime_destroyed",
                    1500,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[VoiceClientRuntime] Runtime destroy disconnect failed: " + exception.Message);
            }

            DisposeRuntimeResources();
        }

        //* این تابع منابع Runtime را فقط یک‌بار و پس از پایان مسیر خروج آزاد می‌کند.
        private void DisposeRuntimeResources()
        {
            if (runtimeResourcesDisposed) return;
            runtimeResourcesDisposed = true;
            shuttingDown = true;
            publishingAllowed = false;
            while (pendingVoiceFrames.TryDequeue(out _)) { }

            if (microphonePublisher != null)
            {
                microphonePublisher.FrameEncoded -= HandleFrameEncoded;
                microphonePublisher.MuteChanged -= HandleMicrophoneMuteChanged;
                microphonePublisher.Failed -= HandleFailure;
            }

            if (transport != null)
            {
                transport.PacketReceived -= HandleTransportPacket;
                transport.Failed -= HandleFailure;
                transport.Disconnected -= HandleTransportDisconnected;
                transport.Dispose();
                transport = null;
            }

            try { lifetimeCts?.Cancel(); } catch { }
            lifetimeCts?.Dispose();
            lifetimeCts = null;
        }
    }
}

/*
توضیح فایل:
این فایل چرخه کامل Auth، Publish، دریافت Session/Frame، Mute، Recording Consent و Reconnect کلاینت Voice را روی Transport انتخاب‌شده هر پلتفرم مدیریت می‌کند.
*/
