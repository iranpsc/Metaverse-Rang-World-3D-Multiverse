using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.Realtime.Stability
{
    //* صف پیام های ریل تایم را برای ارسال دوباره پیام های مهم نگه می دارد.
    public class RealtimeMessageQueue
    {
        private readonly Queue<RealtimeQueuedEnvelope> priorityQueue = new Queue<RealtimeQueuedEnvelope>();
        private readonly Queue<RealtimeQueuedEnvelope> normalQueue = new Queue<RealtimeQueuedEnvelope>();
        private readonly object lockObject = new object();

        public event Action<int> QueueCountChanged;
        public event Action<RealtimeEnvelope> MessageDropped;
        public event Action<string> QueueLogReceived;

        public int maxQueueSize = 100;
        public int maxMessageAgeMs = 60000;
        public int maxRetryCount = 3;
        public bool logQueue;

        //* اِنولوپ را در صف مناسب قرار می دهد تا بعدا از طریق کُر ارسال شود.
        public bool Enqueue(RealtimeEnvelope envelope, bool isPriority = false)
        {
            if (envelope == null || !envelope.IsValidBasic()) return false;

            lock (lockObject)
            {
                if (GetCountUnsafe() >= maxQueueSize)
                {
                    WriteLog("Realtime queue is full.");
                    return false;
                }

                envelope.EnsureDefaults();
                var queued = new RealtimeQueuedEnvelope(envelope, isPriority);
                if (isPriority) priorityQueue.Enqueue(queued);
                else normalQueue.Enqueue(queued);

                QueueCountChanged?.Invoke(GetCountUnsafe());
                return true;
            }
        }

        //* پیام بعدی صف را بدون حذف برمی گرداند تا در صورت شکست دوباره قابل تلاش باشد.
        public RealtimeEnvelope PeekNext()
        {
            lock (lockObject)
            {
                CleanupExpiredUnsafe(priorityQueue);
                CleanupExpiredUnsafe(normalQueue);

                if (priorityQueue.Count > 0) return priorityQueue.Peek().Envelope;
                if (normalQueue.Count > 0) return normalQueue.Peek().Envelope;
                return null;
            }
        }

        //* پیام موفق ارسال شده را از ابتدای صف حذف می کند.
        public void ConfirmSent(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return;

            lock (lockObject)
            {
                if (TryRemoveHeadByIdUnsafe(priorityQueue, messageId) || TryRemoveHeadByIdUnsafe(normalQueue, messageId)) QueueCountChanged?.Invoke(GetCountUnsafe());
            }
        }

        //* تلاش ناموفق پیام را ثبت می کند و اگر از حد مجاز گذشت، پیام را حذف می کند.
        public void RegisterSendFailed(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return;

            lock (lockObject)
            {
                RealtimeQueuedEnvelope queued = FindByIdUnsafe(priorityQueue, messageId) ?? FindByIdUnsafe(normalQueue, messageId);
                if (queued == null) return;

                queued.RetryCount++;
                if (queued.RetryCount <= maxRetryCount) return;

                RemoveByIdUnsafe(priorityQueue, messageId);
                RemoveByIdUnsafe(normalQueue, messageId);
                MessageDropped?.Invoke(queued.Envelope);
                QueueCountChanged?.Invoke(GetCountUnsafe());
                WriteLog("Realtime queued message dropped: " + messageId);
            }
        }

        //* همه پیام های صف شده را با کُر ریل تایم ارسال می کند تا صف خالی شود یا ارسال شکست بخورد.
        public async Task FlushAsync(RealtimeClient realtimeClient, CancellationToken cancellationToken = default)
        {
            if (realtimeClient == null) return;

            while (!cancellationToken.IsCancellationRequested)
            {
                RealtimeEnvelope envelope = PeekNext();
                if (envelope == null) return;

                bool sent = await realtimeClient.SendEnvelopeAsync(envelope, cancellationToken);
                if (!sent)
                {
                    RegisterSendFailed(envelope.id);
                    return;
                }

                ConfirmSent(envelope.id);
            }
        }


        //* همه پیام های صف شده را با کنترل اَک ارسال می کند تا پیام مهم فقط بعد از اَک از صف حذف شود.
        public async Task<bool> FlushWithAckAsync(RealtimeClient realtimeClient, RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (realtimeClient == null) return false;
            options = options ?? RealtimeReliableSendOptions.Default();
            options.Normalize();

            while (!cancellationToken.IsCancellationRequested)
            {
                RealtimeEnvelope envelope = PeekNext();
                if (envelope == null) return true;

                bool sent;
                if (envelope.requiresAck)
                {
                    RealtimeReliableSendResult result = await realtimeClient.SendEnvelopeReliableAsync(envelope, options, cancellationToken);
                    sent = result != null && result.isSuccess;
                }
                else
                {
                    sent = await realtimeClient.SendEnvelopeAsync(envelope, cancellationToken);
                }

                if (!sent)
                {
                    RegisterSendFailed(envelope.id);
                    return false;
                }

                ConfirmSent(envelope.id);
            }

            return Count() == 0;
        }

        //* همه پیام های ذخیره شده را پاک می کند.
        public void Clear()
        {
            lock (lockObject)
            {
                priorityQueue.Clear();
                normalQueue.Clear();
                QueueCountChanged?.Invoke(0);
            }
        }

        //* تعداد کل پیام های صف را برمی گرداند.
        public int Count()
        {
            lock (lockObject)
            {
                return GetCountUnsafe();
            }
        }

        //* گزارش کوتاه از وضعیت صف برای دیباگ می سازد.
        public string GetDebugInfo()
        {
            lock (lockObject)
            {
                return "Queue=" + GetCountUnsafe() + "/" + maxQueueSize + ", Priority=" + priorityQueue.Count + ", Normal=" + normalQueue.Count;
            }
        }

        //* پیام های منقضی شده ابتدای صف را حذف می کند.
        private void CleanupExpiredUnsafe(Queue<RealtimeQueuedEnvelope> queue)
        {
            while (queue.Count > 0 && queue.Peek().IsExpired(maxMessageAgeMs))
            {
                RealtimeQueuedEnvelope expired = queue.Dequeue();
                MessageDropped?.Invoke(expired.Envelope);
                WriteLog("Realtime queued message expired: " + expired.Envelope.id);
            }
        }

        //* اگر پیام ابتدای صف با آیدی داده شده برابر باشد، آن را حذف می کند.
        private bool TryRemoveHeadByIdUnsafe(Queue<RealtimeQueuedEnvelope> queue, string messageId)
        {
            if (queue.Count == 0 || queue.Peek().Envelope.id != messageId) return false;
            queue.Dequeue();
            return true;
        }

        //* پیام صف شده را با آیدی داده شده پیدا می کند.
        private RealtimeQueuedEnvelope FindByIdUnsafe(Queue<RealtimeQueuedEnvelope> queue, string messageId)
        {
            foreach (RealtimeQueuedEnvelope item in queue)
            {
                if (item.Envelope.id == messageId) return item;
            }

            return null;
        }

        //* پیام مشخص را از هر جای صف حذف می کند.
        private void RemoveByIdUnsafe(Queue<RealtimeQueuedEnvelope> queue, string messageId)
        {
            if (queue.Count == 0) return;

            int count = queue.Count;
            for (int i = 0; i < count; i++)
            {
                RealtimeQueuedEnvelope item = queue.Dequeue();
                if (item.Envelope.id != messageId) queue.Enqueue(item);
            }
        }

        //* تعداد کل پیام ها را بدون لاک جدید برمی گرداند.
        private int GetCountUnsafe()
        {
            return priorityQueue.Count + normalQueue.Count;
        }

        //* لاگ داخلی صف را در صورت فعال بودن به بیرون می فرستد.
        private void WriteLog(string message)
        {
            if (logQueue) Debug.Log("[RealtimeMessageQueue] " + message);
            QueueLogReceived?.Invoke(message);
        }

        //* مدل داخلی پیام صف شده را نگه می دارد.
        private class RealtimeQueuedEnvelope
        {
            public readonly RealtimeEnvelope Envelope;
            public readonly bool IsPriority;
            public readonly long EnqueuedUnixMs;
            public int RetryCount;

            //* پیام صف شده را با زمان ورود و اولویت ذخیره می کند.
            public RealtimeQueuedEnvelope(RealtimeEnvelope envelope, bool isPriority)
            {
                Envelope = envelope;
                IsPriority = isPriority;
                EnqueuedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            //* بررسی می کند پیام صف شده از عمر مجاز عبور کرده یا نه.
            public bool IsExpired(int maxAgeMs)
            {
                if (maxAgeMs <= 0) return false;
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - EnqueuedUnixMs > maxAgeMs;
            }
        }
    }
}

//* این فایل صف پیام های ریل تایم را مدیریت می کند.
//* این صف برای نگهداری پیام های مهم هنگام قطع یا ضعف اتصال استفاده می شود.
