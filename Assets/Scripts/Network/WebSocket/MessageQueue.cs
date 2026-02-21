using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    /// <summary>
    /// صف پیام‌ها برای ذخیره و ارسال مجدد در حین قطعی اتصال
    /// سازگار با WebSocketClient فعلی (DequeueNext/Requeue) + Dequeue واقعی
    /// </summary>
    public class MessageQueue
    {
        private readonly Queue<QueuedMessage> priorityQueue = new();
        private readonly Queue<QueuedMessage> normalQueue = new();
        private readonly object lockObject = new();

        // تنظیمات
        public int MaxQueueSize { get; set; } = 100;
        public int MaxMessageAgeSeconds { get; set; } = 60;
        private const int MaxRetryCount = 3;

        // نگهداری retryCount بر اساس messageId (برای وقتی Requeue با WebSocketMessage صدا زده می‌شود)
        private readonly Dictionary<string, int> retryMap = new Dictionary<string, int>(256);

        public bool Enqueue(WebSocketMessage message, bool isPriority = false)
        {
            if (message == null) return false;

            lock (lockObject)
            {
                CleanupExpiredMessages(priorityQueue);
                CleanupExpiredMessages(normalQueue);

                if (GetQueueCount_NoLock() >= MaxQueueSize)
                {
                    Debug.LogWarning($"صف پیام پر شده ({GetQueueCount_NoLock()}/{MaxQueueSize})");
                    return false;
                }

                var queued = new QueuedMessage
                {
                    Message = message,
                    EnqueueTime = DateTimeOffset.UtcNow,
                    IsPriority = isPriority,
                    RetryCount = 0
                };

                if (isPriority) priorityQueue.Enqueue(queued);
                else normalQueue.Enqueue(queued);

                // اگر پیام جدید enqueue شد و قبلاً retryMap داشته، reset کن
                retryMap.Remove(message.messageId);

                return true;
            }
        }

        /// <summary>
        /// سازگار با WebSocketClient: پیام بعدی را برمی‌گرداند.
        /// نکته: این نسخه واقعاً Dequeue می‌کند (بر خلاف نسخه‌ی قبلی که Peek بود).
        /// </summary>
        public WebSocketMessage DequeueNext()
        {
            lock (lockObject)
            {
                CleanupExpiredMessages(priorityQueue);
                CleanupExpiredMessages(normalQueue);

                if (priorityQueue.Count > 0)
                    return priorityQueue.Dequeue().Message;

                if (normalQueue.Count > 0)
                    return normalQueue.Dequeue().Message;

                return null;
            }
        }

        /// <summary>
        /// سازگار با WebSocketClient: اگر ارسال شکست خورد، retryCount++ و پیام را دوباره در صف می‌گذارد
        /// یا اگر از حد گذشت، حذف می‌کند.
        /// </summary>
        public void Requeue(WebSocketMessage message)
        {
            if (message == null) return;

            lock (lockObject)
            {
                // اگر صف پر است، requeue نکن تا سیستم گیر نکند
                if (GetQueueCount_NoLock() >= MaxQueueSize)
                {
                    Debug.LogWarning($"صف پر است؛ پیام {message.messageId} برای retry حذف شد");
                    retryMap.Remove(message.messageId);
                    return;
                }

                // افزایش retryCount با map
                int current = retryMap.TryGetValue(message.messageId, out var v) ? v : 0;
                current++;
                retryMap[message.messageId] = current;

                if (current > MaxRetryCount)
                {
                    Debug.LogWarning($"پیام {message.messageId} پس از {MaxRetryCount} تلاش حذف شد");
                    retryMap.Remove(message.messageId);
                    return;
                }

                // پیام را به ته صف normal برمی‌گردانیم (در این API قدیمی اولویت را نداریم)
                var queued = new QueuedMessage
                {
                    Message = message,
                    EnqueueTime = DateTimeOffset.UtcNow,
                    IsPriority = false,
                    RetryCount = current
                };

                normalQueue.Enqueue(queued);
            }
        }

        public void Clear()
        {
            lock (lockObject)
            {
                priorityQueue.Clear();
                normalQueue.Clear();
                retryMap.Clear();
            }
        }

        public int GetQueueCount()
        {
            lock (lockObject)
            {
                return GetQueueCount_NoLock();
            }
        }

        public string GetQueueInfo()
        {
            lock (lockObject)
            {
                int expired = CountExpired(priorityQueue) + CountExpired(normalQueue);
                return $"Queue Size: {GetQueueCount_NoLock()}/{MaxQueueSize}, " +
                       $"Priority: {priorityQueue.Count}, Normal: {normalQueue.Count}, Expired: {expired}";
            }
        }

        // ---------------- helpers ----------------

        private int GetQueueCount_NoLock() => priorityQueue.Count + normalQueue.Count;

        private void CleanupExpiredMessages(Queue<QueuedMessage> queue)
        {
            while (queue.Count > 0 && queue.Peek().IsExpired(MaxMessageAgeSeconds))
            {
                var expired = queue.Dequeue();
                if (expired?.Message != null)
                    retryMap.Remove(expired.Message.messageId);

                Debug.LogWarning($"پیام منقضی حذف شد: {expired?.Message?.type} ({expired?.Message?.messageId})");
            }
        }

        private int CountExpired(Queue<QueuedMessage> queue)
        {
            int count = 0;
            foreach (var q in queue)
            {
                if (q.IsExpired(MaxMessageAgeSeconds))
                    count++;
            }
            return count;
        }

        private class QueuedMessage
        {
            public WebSocketMessage Message;
            public DateTimeOffset EnqueueTime;
            public bool IsPriority;
            public int RetryCount;

            public bool IsExpired(int maxAgeSeconds)
            {
                var age = (DateTimeOffset.UtcNow - EnqueueTime).TotalSeconds;
                return age > maxAgeSeconds;
            }
        }
    }
}
