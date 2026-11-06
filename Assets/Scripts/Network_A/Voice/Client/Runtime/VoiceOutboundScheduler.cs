using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Voice.Client.Protocol;
using UnityEngine;

namespace Network_A.Voice.Client.Runtime
{
    internal sealed class VoiceOutboundScheduler : IDisposable
    {
        private readonly ConcurrentQueue<VoiceOutboundMessage> controlQueue =
            new ConcurrentQueue<VoiceOutboundMessage>();
        private readonly Queue<VoiceOutboundMessage> mediaQueue =
            new Queue<VoiceOutboundMessage>();
        private readonly object mediaSync = new object();
        private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0, 1);
        private readonly CancellationTokenSource schedulerCts = new CancellationTokenSource();
        private readonly Func<VoiceOutboundMessage, CancellationToken, Task<VoiceOutboundSendResult>> sendAsync;
        private readonly int mediaCapacity;
        private readonly ulong mediaMaxAgeMs;

        private Task writerTask;
        private int wakePending;
        private int disposed;
#if UNITY_WEBGL && !UNITY_EDITOR
        private int webGlPumpRunning;
#endif
        private int mediaQueueDrops;
        private int mediaStaleDrops;
        private long mediaSentCount;
        private long controlSentCount;

        public event Action<VoiceOutboundMediaSentInfo> MediaSent;

        public VoiceOutboundScheduler(
            Func<VoiceOutboundMessage, CancellationToken, Task<VoiceOutboundSendResult>> sendAsync,
            int mediaCapacity,
            ulong mediaMaxAgeMs)
        {
            this.sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
            this.mediaCapacity = Math.Max(1, mediaCapacity);
            this.mediaMaxAgeMs = Math.Max(20UL, mediaMaxAgeMs);
#if UNITY_WEBGL && !UNITY_EDITOR
            writerTask = Task.CompletedTask;
#else
            writerTask = Task.Run(() => WriterLoopAsync(schedulerCts.Token));
#endif

            Debug.Log(
                "VOICE_CLIENT_OUTBOUND_SCHEDULER_STARTED=PASS" +
                " | mediaCapacity=" + this.mediaCapacity +
                " | mediaMaxAgeMs=" + this.mediaMaxAgeMs +
                " | controlPriority=True" +
                " | writer=persistent_single_writer" +
#if UNITY_WEBGL && !UNITY_EDITOR
                " | execution=webgl_inline_single_writer_pump");
#else
                " | execution=thread_pool");
#endif
        }

        public async Task<VoiceOutboundSendResult> EnqueueControlAsync(
            VoiceClientMessageType messageType,
            VoiceClientMessageFlags flags,
            string sessionId,
            string senderId,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested)
                return VoiceOutboundSendResult.Failed;

            VoiceOutboundMessage message = VoiceOutboundMessage.CreateControl(
                messageType,
                flags,
                sessionId,
                senderId,
                payload,
                cancellationToken);

            controlQueue.Enqueue(message);
            SignalWriter();

            try
            {
                return await message.Completion.Task;
            }
            catch (OperationCanceledException)
            {
                return VoiceOutboundSendResult.Failed;
            }
        }

        public bool TryEnqueueMedia(
            VoiceClientMessageType messageType,
            VoiceClientMessageFlags flags,
            string sessionId,
            string senderId,
            byte[] payload,
            bool dtx)
        {
            if (Volatile.Read(ref disposed) != 0 || payload == null || payload.Length == 0)
                return false;

            VoiceOutboundMessage message = VoiceOutboundMessage.CreateMedia(
                messageType,
                flags,
                sessionId,
                senderId,
                payload,
                dtx,
                UnixTimeMs());

            VoiceOutboundMessage dropped = null;
            int pending;

            lock (mediaSync)
            {
                if (mediaQueue.Count >= mediaCapacity)
                {
                    dropped = mediaQueue.Dequeue();
                }

                mediaQueue.Enqueue(message);
                pending = mediaQueue.Count;
            }

            if (dropped != null)
            {
                int totalDropped = Interlocked.Increment(ref mediaQueueDrops);
                if (totalDropped == 1 || totalDropped % 25 == 0)
                {
                    Debug.LogWarning(
                        "VOICE_CLIENT_MEDIA_QUEUE_DROP=PASS" +
                        " | reason=queue_full_oldest_dropped" +
                        " | totalDropped=" + totalDropped +
                        " | pending=" + pending +
                        " | capacity=" + mediaCapacity +
                        " | maxAgeMs=" + mediaMaxAgeMs);
                }
            }

            SignalWriter();
            return true;
        }

        public void ClearMedia(string reason)
        {
            int cleared = 0;
            lock (mediaSync)
            {
                cleared = mediaQueue.Count;
                mediaQueue.Clear();
            }

            if (cleared > 0)
            {
                Debug.Log(
                    "VOICE_CLIENT_MEDIA_QUEUE_CLEARED=PASS" +
                    " | reason=" + Safe(reason) +
                    " | cleared=" + cleared);
            }
        }

        public void Clear(string reason)
        {
            ClearMedia(reason);

            int controlsCleared = 0;
            while (controlQueue.TryDequeue(out VoiceOutboundMessage control))
            {
                controlsCleared++;
                control.Completion?.TrySetResult(VoiceOutboundSendResult.Failed);
            }

            if (controlsCleared > 0)
            {
                Debug.Log(
                    "VOICE_CLIENT_CONTROL_QUEUE_CLEARED=PASS" +
                    " | reason=" + Safe(reason) +
                    " | cleared=" + controlsCleared);
            }
        }

        private async Task WriterLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await wakeSignal.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                Interlocked.Exchange(ref wakePending, 0);

                while (!cancellationToken.IsCancellationRequested)
                {
                    VoiceOutboundMessage message = TryDequeueNext();
                    if (message == null) break;
                    await ProcessMessageAsync(message, cancellationToken);
                }

                if (HasPendingMessages()) SignalWriter();
            }

            Clear("scheduler_stopped");
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private void StartWebGlPump()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (Interlocked.CompareExchange(ref webGlPumpRunning, 1, 0) != 0)
                return;

            writerTask = DrainWebGlQueueAsync(schedulerCts.Token);
        }

        private async Task DrainWebGlQueueAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                Debug.Log(
                    "VOICE_CLIENT_OUTBOUND_WEBGL_PUMP=START" +
                    " | pendingControl=" + controlQueue.Count +
                    " | pendingMedia=" + GetMediaCount());

                while (!cancellationToken.IsCancellationRequested)
                {
                    VoiceOutboundMessage message = TryDequeueNext();
                    if (message == null) break;
                    await ProcessMessageAsync(message, cancellationToken);
                }
            }
            finally
            {
                Interlocked.Exchange(ref webGlPumpRunning, 0);

                if (!cancellationToken.IsCancellationRequested &&
                    Volatile.Read(ref disposed) == 0 &&
                    HasPendingMessages())
                {
                    StartWebGlPump();
                }
            }
        }
#endif

        private async Task ProcessMessageAsync(
            VoiceOutboundMessage message,
            CancellationToken cancellationToken)
        {
            if (message.IsMedia)
            {
                ulong nowMs = UnixTimeMs();
                ulong queueAgeMs = nowMs >= message.EnqueuedAtMs
                    ? nowMs - message.EnqueuedAtMs
                    : 0UL;

                if (queueAgeMs > mediaMaxAgeMs)
                {
                    int totalStale = Interlocked.Increment(
                        ref mediaStaleDrops);
                    if (totalStale == 1 || totalStale % 25 == 0)
                    {
                        Debug.LogWarning(
                            "VOICE_CLIENT_MEDIA_QUEUE_DROP=PASS" +
                            " | reason=stale" +
                            " | ageMs=" + queueAgeMs +
                            " | totalDropped=" + totalStale +
                            " | pending=" + GetMediaCount() +
                            " | maxAgeMs=" + mediaMaxAgeMs);
                    }

                    return;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();
                VoiceOutboundSendResult result = await SafeSendAsync(
                    message,
                    cancellationToken);
                stopwatch.Stop();
                long sendDurationMs = stopwatch.ElapsedMilliseconds;
                long sentCount = result.Success
                    ? Interlocked.Increment(ref mediaSentCount)
                    : Interlocked.Read(ref mediaSentCount);

                if (result.Success)
                {
                    MediaSent?.Invoke(new VoiceOutboundMediaSentInfo(
                        result.Sequence,
                        message.Payload == null ? 0 : message.Payload.Length,
                        message.Dtx,
                        queueAgeMs,
                        GetMediaCount(),
                        sendDurationMs));
                }

                if (sendDurationMs >= 80 ||
                    (result.Success && sentCount % 250 == 0))
                {
                    Debug.Log(
                        "VOICE_CLIENT_OUTBOUND_WRITE_METRIC=PASS" +
                        " | kind=media" +
                        " | success=" + result.Success +
                        " | sequence=" + result.Sequence +
                        " | writeMs=" + sendDurationMs +
                        " | queueAgeMs=" + queueAgeMs +
                        " | pendingMedia=" + GetMediaCount() +
                        " | pendingControl=" + controlQueue.Count);
                }

                return;
            }

            if (message.CancellationToken.IsCancellationRequested)
            {
                message.Completion?.TrySetResult(
                    VoiceOutboundSendResult.Failed);
                return;
            }

            Stopwatch controlStopwatch = Stopwatch.StartNew();
            VoiceOutboundSendResult controlResult = await SafeSendAsync(
                message,
                message.CancellationToken);
            controlStopwatch.Stop();
            long controlWriteMs = controlStopwatch.ElapsedMilliseconds;
            long controlCount = controlResult.Success
                ? Interlocked.Increment(ref controlSentCount)
                : Interlocked.Read(ref controlSentCount);

            message.Completion?.TrySetResult(controlResult);

            if (controlCount == 1 ||
                controlWriteMs >= 80 ||
                !controlResult.Success ||
                controlCount % 50 == 0)
            {
                Debug.Log(
                    "VOICE_CLIENT_OUTBOUND_WRITE_METRIC=PASS" +
                    " | kind=control" +
                    " | messageType=" + message.MessageType +
                    " | success=" + controlResult.Success +
                    " | sequence=" + controlResult.Sequence +
                    " | writeMs=" + controlWriteMs +
                    " | pendingMedia=" + GetMediaCount() +
                    " | pendingControl=" + controlQueue.Count);
            }
        }

        private async Task<VoiceOutboundSendResult> SafeSendAsync(
            VoiceOutboundMessage message,
            CancellationToken cancellationToken)
        {
            try
            {
                return await sendAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return VoiceOutboundSendResult.Failed;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "VOICE_CLIENT_OUTBOUND_WRITER_FAILED" +
                    " | messageType=" + message.MessageType +
                    " | error=" + exception.Message);
                return VoiceOutboundSendResult.Failed;
            }
        }

        private VoiceOutboundMessage TryDequeueNext()
        {
            if (controlQueue.TryDequeue(out VoiceOutboundMessage control))
                return control;

            lock (mediaSync)
            {
                if (mediaQueue.Count > 0)
                    return mediaQueue.Dequeue();
            }

            return null;
        }

        private bool HasPendingMessages()
        {
            if (!controlQueue.IsEmpty) return true;
            lock (mediaSync) return mediaQueue.Count > 0;
        }

        private int GetMediaCount()
        {
            lock (mediaSync) return mediaQueue.Count;
        }

        private void SignalWriter()
        {
            if (Volatile.Read(ref disposed) != 0) return;
#if UNITY_WEBGL && !UNITY_EDITOR
            StartWebGlPump();
#else
            if (Interlocked.Exchange(ref wakePending, 1) != 0) return;

            try { wakeSignal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
#endif
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;

            try { schedulerCts.Cancel(); } catch { }
            try { wakeSignal.Release(); } catch { }
            Clear("dispose");
            MediaSent = null;

            Task task = writerTask;
            writerTask = null;
            if (task == null || task.IsCompleted)
            {
                schedulerCts.Dispose();
                wakeSignal.Dispose();
                return;
            }

            _ = task.ContinueWith(
                _ =>
                {
                    try { schedulerCts.Dispose(); } catch { }
                    try { wakeSignal.Dispose(); } catch { }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static ulong UnixTimeMs()
        {
            return (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }

    internal sealed class VoiceOutboundMessage
    {
        public VoiceClientMessageType MessageType;
        public VoiceClientMessageFlags Flags;
        public string SessionId;
        public string SenderId;
        public byte[] Payload;
        public bool IsMedia;
        public bool Dtx;
        public ulong EnqueuedAtMs;
        public CancellationToken CancellationToken;
        public TaskCompletionSource<VoiceOutboundSendResult> Completion;

        public static VoiceOutboundMessage CreateControl(
            VoiceClientMessageType messageType,
            VoiceClientMessageFlags flags,
            string sessionId,
            string senderId,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            return new VoiceOutboundMessage
            {
                MessageType = messageType,
                Flags = flags,
                SessionId = sessionId,
                SenderId = senderId,
                Payload = payload,
                IsMedia = false,
                Dtx = false,
                EnqueuedAtMs = 0,
                CancellationToken = cancellationToken,
                Completion = new TaskCompletionSource<VoiceOutboundSendResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };
        }

        public static VoiceOutboundMessage CreateMedia(
            VoiceClientMessageType messageType,
            VoiceClientMessageFlags flags,
            string sessionId,
            string senderId,
            byte[] payload,
            bool dtx,
            ulong enqueuedAtMs)
        {
            return new VoiceOutboundMessage
            {
                MessageType = messageType,
                Flags = flags,
                SessionId = sessionId,
                SenderId = senderId,
                Payload = payload,
                IsMedia = true,
                Dtx = dtx,
                EnqueuedAtMs = enqueuedAtMs,
                CancellationToken = CancellationToken.None,
                Completion = null
            };
        }
    }

    internal readonly struct VoiceOutboundSendResult
    {
        public static readonly VoiceOutboundSendResult Failed =
            new VoiceOutboundSendResult(false, 0);

        public bool Success { get; }
        public uint Sequence { get; }

        public VoiceOutboundSendResult(bool success, uint sequence)
        {
            Success = success;
            Sequence = sequence;
        }
    }

    internal readonly struct VoiceOutboundMediaSentInfo
    {
        public uint Sequence { get; }
        public int PayloadBytes { get; }
        public bool Dtx { get; }
        public ulong QueueAgeMs { get; }
        public int PendingMedia { get; }
        public long WriteDurationMs { get; }

        public VoiceOutboundMediaSentInfo(
            uint sequence,
            int payloadBytes,
            bool dtx,
            ulong queueAgeMs,
            int pendingMedia,
            long writeDurationMs)
        {
            Sequence = sequence;
            PayloadBytes = payloadBytes;
            Dtx = dtx;
            QueueAgeMs = queueAgeMs;
            PendingMedia = pendingMedia;
            WriteDurationMs = writeDurationMs;
        }
    }
}
