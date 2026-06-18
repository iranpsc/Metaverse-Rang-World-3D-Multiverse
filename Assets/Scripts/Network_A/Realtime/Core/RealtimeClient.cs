using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Routing;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Realtime.Core
{
    //* کلاینت اصلی کُر ریل‌تایم است و فقط با قرارداد IRealtimeTransport کار می‌کند.
    public class RealtimeClient : IDisposable
    {
        private readonly RealtimeConfig config;
        private readonly RealtimeMessageRouter router;
        private readonly RealtimeMessageQueue messageQueue = new RealtimeMessageQueue();
        private readonly RealtimeAckTracker ackTracker = new RealtimeAckTracker();
        private IRealtimeTransport transport;
        private CancellationTokenSource lifecycleCts;
        private RealtimeConnectionState state = RealtimeConnectionState.Disconnected;

        public event Action<RealtimeConnectionState> StateChanged;
        public event Action<RealtimeEnvelope> EnvelopeReceived;
        public event Action<RealtimeError> ErrorEnvelopeReceived;
        public event Action<string> TransportErrorReceived;
        public event Action<string> Disconnected;
        public event Action<int> QueueCountChanged;
        public event Action<string> QueueLogReceived;
        public event Action<RealtimeEnvelope> QueuedMessageDropped;
        public event Action<RealtimeEnvelope, RealtimeDeliveryPolicy> EnvelopeDroppedByPolicy;
        public event Action<string> ReliableAckTimeout;
        public event Action<string> ReliableLogReceived;

        public RealtimeConnectionState State => state;
        public RealtimeMessageRouter Router => router;
        public RealtimeMessageQueue MessageQueue => messageQueue;
        public bool IsConnected => transport != null && transport.IsConnected;
        public int QueuedMessageCount => messageQueue.Count();
        public int PendingAckCount => ackTracker.PendingCount;

        //* کُر ریل‌تایم را با کانفیگ داده‌شده می‌سازد.
        public RealtimeClient(RealtimeConfig config)
        {
            this.config = config ?? RealtimeConfig.CreateLocalWebSocket();
            this.config.Normalize();
            router = new RealtimeMessageRouter();
            RegisterCoreRoutes();
            BindQueueEvents();
            BindAckTrackerEvents();
        }

        //* کُر ریل‌تایم را با کانفیگ و رُتِر آماده می‌سازد.
        public RealtimeClient(RealtimeConfig config, RealtimeMessageRouter router)
        {
            this.config = config ?? RealtimeConfig.CreateLocalWebSocket();
            this.config.Normalize();
            this.router = router ?? new RealtimeMessageRouter();
            RegisterCoreRoutes();
            BindQueueEvents();
            BindAckTrackerEvents();
        }

        //* اتصال کُر را از طریق ترنسپورت انتخاب‌شده شروع می‌کند.
        public async Task<bool> ConnectAsync(Dictionary<string, string> headers = null, CancellationToken cancellationToken = default)
        {
            if (IsConnected) return true;

            SetState(RealtimeConnectionState.Connecting);
            CleanupTransportReference(transport);
            lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (config.connectTimeoutMs > 0) lifecycleCts.CancelAfter(config.connectTimeoutMs);

            transport = RealtimeTransportFactory.Create(config.transportKind);
            if (transport == null)
            {
                SetState(RealtimeConnectionState.Failed);
                TransportErrorReceived?.Invoke("Realtime transport is not registered: " + RealtimeTransportFactory.ResolveTransportKind(config.transportKind));
                return false;
            }

            BindTransportEvents(transport);
            bool connected = await transport.ConnectAsync(config.serverUrl, headers ?? new Dictionary<string, string>(), lifecycleCts.Token);

            if (!connected)
            {
                SetState(RealtimeConnectionState.Failed);
                return false;
            }

            SetState(RealtimeConnectionState.Connected);
            return true;
        }

        //* اِنولوپ آماده را از طریق ترنسپورت فعال ارسال می‌کند.
        public async Task<bool> SendEnvelopeAsync(RealtimeEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope == null || !envelope.IsValidBasic()) return false;
            if (!IsConnected || transport == null) return false;

            envelope.EnsureDefaults();
            string json = envelope.ToJson();
            if (config.logOutgoingMessages) Debug.Log("[RealtimeClient] OUT " + json);

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (config.sendTimeoutMs > 0) cts.CancelAfter(config.sendTimeoutMs);
                return await transport.SendAsync(json, cts.Token);
            }
        }

        //* اِنولوپ را بر اساس سیاست ارسال مشخص‌شده ارسال، صف، یا حذف کنترل‌شده می‌کند.
        public async Task<bool> SendEnvelopeWithPolicyAsync(RealtimeEnvelope envelope, RealtimeDeliveryPolicy deliveryPolicy, bool isPriority = false, CancellationToken cancellationToken = default)
        {
            if (envelope == null || !envelope.IsValidBasic()) return false;
            if (IsConnected && transport != null) return await SendEnvelopeAsync(envelope, cancellationToken);

            switch (deliveryPolicy)
            {
                case RealtimeDeliveryPolicy.ReliableQueued:
                    return QueueEnvelope(envelope, isPriority);

                case RealtimeDeliveryPolicy.ReliableNoQueue:
                    DropEnvelopeByPolicy(envelope, deliveryPolicy, "Reliable message dropped because queue is disabled for this message.");
                    return false;

                case RealtimeDeliveryPolicy.UnreliableLatestOnly:
                    DropEnvelopeByPolicy(envelope, deliveryPolicy, "Latest-only message dropped while disconnected.");
                    return false;

                case RealtimeDeliveryPolicy.UnreliableDropWhenDisconnected:
                    DropEnvelopeByPolicy(envelope, deliveryPolicy, "Unreliable message dropped while disconnected.");
                    return false;

                default:
                    DropEnvelopeByPolicy(envelope, deliveryPolicy, "Realtime message dropped by unknown delivery policy.");
                    return false;
            }
        }


        //* اِنولوپ قابل اطمینان را ارسال می کند و تا رسیدن اَک یا تایم اوت منتظر می ماند.
        public async Task<RealtimeReliableSendResult> SendEnvelopeReliableAsync(RealtimeEnvelope envelope, RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (envelope == null || !envelope.IsValidBasic()) return RealtimeReliableSendResult.Failed(string.Empty, 0, "Invalid realtime envelope.");
            if (!IsConnected || transport == null) return RealtimeReliableSendResult.Failed(envelope.id, 0, "Realtime client is disconnected.");

            options = options ?? RealtimeReliableSendOptions.Default();
            options.Normalize();
            envelope.EnsureDefaults();

            if (!envelope.requiresAck)
            {
                bool sentWithoutAck = await SendEnvelopeAsync(envelope, cancellationToken);
                return sentWithoutAck ? RealtimeReliableSendResult.Success(envelope.id, 1, null) : RealtimeReliableSendResult.Failed(envelope.id, 1, "Transport send failed.");
            }

            bool lastFailureWasTimeout = false;

            for (int attempt = 1; attempt <= options.maxSendAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task<RealtimeAck> ackTask = ackTracker.WatchAsync(envelope.id);
                bool sent = await SendEnvelopeAsync(envelope, cancellationToken);

                if (!sent)
                {
                    ackTracker.Remove(envelope.id);
                    ReliableLogReceived?.Invoke("Reliable send transport failed: " + envelope.id + " | attempt=" + attempt);
                    if (!options.retryOnTransportSendFailed || attempt >= options.maxSendAttempts) return RealtimeReliableSendResult.Failed(envelope.id, attempt, "Transport send failed.");
                    await DelayReliableRetryAsync(options.retryDelayMs, cancellationToken);
                    continue;
                }

                RealtimeAck ack = await ackTracker.WaitForAckAsync(envelope.id, ackTask, options.ackTimeoutMs, cancellationToken);
                if (ack != null && ack.IsProcessed()) return RealtimeReliableSendResult.Success(envelope.id, attempt, ack);

                if (ack != null)
                {
                    ReliableLogReceived?.Invoke("Reliable send ack failed: " + envelope.id + " | status=" + ack.status);
                    return RealtimeReliableSendResult.Failed(envelope.id, attempt, "Ack status is not processed: " + ack.status);
                }

                lastFailureWasTimeout = true;
                ReliableLogReceived?.Invoke("Reliable send ack timeout: " + envelope.id + " | attempt=" + attempt);
                if (!options.retryOnAckTimeout || attempt >= options.maxSendAttempts) break;
                await DelayReliableRetryAsync(options.retryDelayMs, cancellationToken);
            }

            return RealtimeReliableSendResult.Failed(envelope.id, options.maxSendAttempts, "Ack timeout.", lastFailureWasTimeout);
        }

        //* اِنولوپ قابل اطمینان را با سیاست ارسال مدیریت می کند؛ در قطعی می تواند صف شود و در اتصال فعال منتظر اَک می ماند.
        public async Task<RealtimeReliableSendResult> SendEnvelopeReliableWithPolicyAsync(RealtimeEnvelope envelope, RealtimeDeliveryPolicy deliveryPolicy, bool isPriority = false, RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (envelope == null || !envelope.IsValidBasic()) return RealtimeReliableSendResult.Failed(string.Empty, 0, "Invalid realtime envelope.");
            envelope.EnsureDefaults();

            if (IsConnected && transport != null) return await SendEnvelopeReliableAsync(envelope, options, cancellationToken);

            if (deliveryPolicy == RealtimeDeliveryPolicy.ReliableQueued)
            {
                bool queued = QueueEnvelope(envelope, isPriority);
                return queued ? RealtimeReliableSendResult.Queued(envelope.id) : RealtimeReliableSendResult.Failed(envelope.id, 0, "Queue rejected the reliable message.");
            }

            DropEnvelopeByPolicy(envelope, deliveryPolicy, "Reliable message dropped by delivery policy while disconnected.");
            return RealtimeReliableSendResult.Dropped(envelope.id, "Dropped by policy while disconnected.");
        }

        //* اِنولوپ مهم را اگر اتصال فعال باشد ارسال می‌کند و اگر قطع باشد داخل صف ریل‌تایم نگه می‌دارد.
        public async Task<bool> SendEnvelopeOrQueueAsync(RealtimeEnvelope envelope, bool isPriority = false, CancellationToken cancellationToken = default)
        {
            return await SendEnvelopeWithPolicyAsync(envelope, RealtimeDeliveryPolicy.ReliableQueued, isPriority, cancellationToken);
        }

        //* اِنولوپ را مستقیم داخل صف ذخیره می‌کند تا بعد از برگشت اتصال ارسال شود.
        public bool QueueEnvelope(RealtimeEnvelope envelope, bool isPriority = false)
        {
            if (envelope == null || !envelope.IsValidBasic()) return false;
            bool queued = messageQueue.Enqueue(envelope, isPriority);
            if (queued) QueueLogReceived?.Invoke("Realtime envelope queued: " + envelope.id);
            return queued;
        }

        //* اِنولوپی را که طبق سیاست ارسال نباید صف شود، کنترل‌شده حذف و لاگ می‌کند.
        private void DropEnvelopeByPolicy(RealtimeEnvelope envelope, RealtimeDeliveryPolicy deliveryPolicy, string reason)
        {
            if (envelope == null) return;
            QueueLogReceived?.Invoke(reason + " id=" + envelope.id + " policy=" + deliveryPolicy);
            EnvelopeDroppedByPolicy?.Invoke(envelope, deliveryPolicy);
        }

        //* پیام‌های صف‌شده را بعد از کانکت، اَث و جوین دوباره ارسال می‌کند.
        public async Task FlushQueuedMessagesAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                QueueLogReceived?.Invoke("Realtime queue flush skipped because client is disconnected.");
                return;
            }

            await messageQueue.FlushAsync(this, cancellationToken);
        }


        //* پیام‌های صف‌شده را به شکل قابل اطمینان فلش می‌کند و برای پیام‌های مهم منتظر اَک می‌ماند.
        public async Task<bool> FlushQueuedMessagesWithAckAsync(RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                QueueLogReceived?.Invoke("Realtime reliable queue flush skipped because client is disconnected.");
                return false;
            }

            return await messageQueue.FlushWithAckAsync(this, options ?? RealtimeReliableSendOptions.Default(), cancellationToken);
        }

        //* پیام خام جیسون را به اِنولوپ تبدیل می‌کند و سپس ارسال می‌کند.
        public async Task<bool> SendRawJsonAsync(string envelopeJson, CancellationToken cancellationToken = default)
        {
            RealtimeEnvelope envelope = RealtimeEnvelope.FromJson(envelopeJson);
            if (envelope == null) return false;
            return await SendEnvelopeAsync(envelope, cancellationToken);
        }

        //* یک پیام پینگ سیستمی برای تست اتصال ارسال می‌کند.
        public async Task<bool> SendPingAsync(CancellationToken cancellationToken = default)
        {
            string payloadJson = "{\"ts\":" + RealtimeJsonUtil.NowUnixMs() + "}";
            var envelope = RealtimeEnvelope.CreateWithId(RealtimeEnvelope.CreateMessageId("ping"), RealtimeChannels.System, RealtimeMessageTypes.Ping, payloadJson);
            return await SendEnvelopeAsync(envelope, cancellationToken);
        }

        //* اتصال فعال را از سمت کُر می‌بندد.
        public async Task DisconnectAsync(string reason = "Client disconnect", CancellationToken cancellationToken = default)
        {
            IRealtimeTransport activeTransport = transport;
            if (activeTransport == null)
            {
                SetState(RealtimeConnectionState.Disconnected);
                return;
            }

            SetState(RealtimeConnectionState.Disconnecting);
            await activeTransport.DisconnectAsync(reason, cancellationToken);
            CleanupTransportReference(activeTransport);
            SetState(RealtimeConnectionState.Disconnected);
        }

        //* رفرنس ترنسپورت فعلی را در زمان دیسکانکت یا ریکانکت پاک می‌کند تا اتصال بعدی روی آبجکت قدیمی ساخته نشود.
        private void CleanupTransportReference(IRealtimeTransport targetTransport)
        {
            if (targetTransport == null) return;
            if (!ReferenceEquals(transport, targetTransport)) return;

            UnbindTransportEvents(targetTransport);
            transport = null;
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        //* رویدادهای صف پیام را به بیرون کُر وصل می‌کند.
        private void BindQueueEvents()
        {
            messageQueue.QueueCountChanged += HandleQueueCountChanged;
            messageQueue.QueueLogReceived += HandleQueueLogReceived;
            messageQueue.MessageDropped += HandleQueuedMessageDropped;
        }

        //* رویدادهای صف پیام را از کُر جدا می‌کند.
        private void UnbindQueueEvents()
        {
            messageQueue.QueueCountChanged -= HandleQueueCountChanged;
            messageQueue.QueueLogReceived -= HandleQueueLogReceived;
            messageQueue.MessageDropped -= HandleQueuedMessageDropped;
        }


        //* رویدادهای اَک ترَکِر را به کُر وصل می‌کند.
        private void BindAckTrackerEvents()
        {
            ackTracker.AckTimeout += HandleAckTimeout;
            ackTracker.AckTrackerLogReceived += HandleAckTrackerLogReceived;
        }

        //* رویدادهای اَک ترَکِر را از کُر جدا می‌کند.
        private void UnbindAckTrackerEvents()
        {
            ackTracker.AckTimeout -= HandleAckTimeout;
            ackTracker.AckTrackerLogReceived -= HandleAckTrackerLogReceived;
        }

        //* تایم اوت اَک را به مصرف کننده های بیرونی اعلام می‌کند.
        private void HandleAckTimeout(string messageId)
        {
            ReliableAckTimeout?.Invoke(messageId);
        }

        //* لاگ داخلی اَک ترَکِر را به رویداد قابل مشاهده کُر تبدیل می‌کند.
        private void HandleAckTrackerLogReceived(string message)
        {
            ReliableLogReceived?.Invoke(message);
        }

        //* بین تلاش های ارسال قابل اطمینان تاخیر کوتاه و قابل لغو ایجاد می‌کند.
        private async Task DelayReliableRetryAsync(int delayMs, CancellationToken cancellationToken)
        {
            if (delayMs <= 0) return;
            await Task.Delay(delayMs, cancellationToken);
        }

        //* تغییر تعداد صف را به مصرف‌کننده‌های بیرونی اعلام می‌کند.
        private void HandleQueueCountChanged(int count)
        {
            QueueCountChanged?.Invoke(count);
        }

        //* لاگ داخلی صف را به رویداد کُر تبدیل می‌کند.
        private void HandleQueueLogReceived(string message)
        {
            QueueLogReceived?.Invoke(message);
        }

        //* حذف پیام صف‌شده را به بیرون کُر اعلام می‌کند.
        private void HandleQueuedMessageDropped(RealtimeEnvelope envelope)
        {
            QueuedMessageDropped?.Invoke(envelope);
        }

        //* رویدادهای ترنسپورت را به کُر وصل می‌کند.
        private void BindTransportEvents(IRealtimeTransport realtimeTransport)
        {
            realtimeTransport.Connected += HandleTransportConnected;
            realtimeTransport.MessageReceived += HandleTransportMessageReceived;
            realtimeTransport.ErrorReceived += HandleTransportErrorReceived;
            realtimeTransport.Disconnected += HandleTransportDisconnected;
        }

        //* رویدادهای ترنسپورت را از کُر جدا می‌کند.
        private void UnbindTransportEvents(IRealtimeTransport realtimeTransport)
        {
            realtimeTransport.Connected -= HandleTransportConnected;
            realtimeTransport.MessageReceived -= HandleTransportMessageReceived;
            realtimeTransport.ErrorReceived -= HandleTransportErrorReceived;
            realtimeTransport.Disconnected -= HandleTransportDisconnected;
        }

        //* مسیرهای داخلی کُر مثل اَک و خطا را ثبت می‌کند.
        private void RegisterCoreRoutes()
        {
            router.RegisterHandler(RealtimeChannels.System, RealtimeMessageTypes.Error, HandleErrorEnvelope);
            router.SetFallbackHandler(HandleFallbackEnvelope);
        }

        //* وصل شدن ترنسپورت را به وضعیت کُر تبدیل می‌کند.
        private void HandleTransportConnected()
        {
            SetState(RealtimeConnectionState.Connected);
        }

        //* پیام خام دریافتی از ترنسپورت را به اِنولوپ تبدیل و رُت می‌کند.
        private void HandleTransportMessageReceived(string message)
        {
            if (config.logIncomingMessages) Debug.Log("[RealtimeClient] IN " + message);

            RealtimeEnvelope envelope = RealtimeEnvelope.FromJson(message);
            if (envelope == null || !envelope.IsValidBasic())
            {
                TransportErrorReceived?.Invoke("Invalid realtime envelope received.");
                return;
            }

            if (envelope.IsAck()) ackTracker.TryCompleteFromEnvelope(envelope);
            EnvelopeReceived?.Invoke(envelope);
            router.Route(envelope);
        }

        //* خطای خام ترنسپورت را به بیرون کُر اعلام می‌کند.
        private void HandleTransportErrorReceived(string error)
        {
            TransportErrorReceived?.Invoke(error);
        }

        //* قطع اتصال ترنسپورت را به وضعیت کُر تبدیل می‌کند.
        private void HandleTransportDisconnected(string reason)
        {
            IRealtimeTransport disconnectedTransport = transport;
            ackTracker.CancelAll(reason);
            CleanupTransportReference(disconnectedTransport);
            SetState(RealtimeConnectionState.Disconnected);
            Disconnected?.Invoke(reason);
        }

        //* اِنولوپ خطای سیستمی را به مدل خطای ریل‌تایم تبدیل می‌کند.
        private void HandleErrorEnvelope(RealtimeEnvelope envelope)
        {
            RealtimeError error = RealtimeError.FromEnvelope(envelope);
            if (error != null) ErrorEnvelopeReceived?.Invoke(error);
        }

        //* پیام‌هایی که هَندلِر اختصاصی ندارند را فعلاً فقط نگه می‌دارد تا فازهای بعدی آن‌ها را مصرف کنند.
        private void HandleFallbackEnvelope(RealtimeEnvelope envelope)
        {
            // Intentionally empty. GameServerClient and future modules register their own handlers.
        }

        //* وضعیت کُر را تغییر می‌دهد و رویداد تغییر وضعیت را ارسال می‌کند.
        private void SetState(RealtimeConnectionState newState)
        {
            if (state == newState) return;
            state = newState;
            StateChanged?.Invoke(state);
        }

        //* منابع داخلی کُر و ترنسپورت را پاکسازی می‌کند.
        public void Dispose()
        {
            if (transport != null)
            {
                UnbindTransportEvents(transport);
                _ = transport.DisconnectAsync("RealtimeClient disposed");
                transport = null;
            }

            UnbindQueueEvents();
            UnbindAckTrackerEvents();
            ackTracker.CancelAll("RealtimeClient disposed");
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
            SetState(RealtimeConnectionState.Disconnected);
        }
    }
}

//* این فایل کُر اصلی ریل‌تایم کلاینت را مدیریت می‌کند.
//* این کُر فقط IRealtimeTransport را می‌شناسد و به وب‌سوکت یا جی‌آرپی‌سی وابسته نیست.
