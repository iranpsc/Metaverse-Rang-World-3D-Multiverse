using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Protocol;

namespace Network_A.Realtime.Stability
{
    //* اَک های در انتظار را نگه می دارد تا پیام های قابل اطمینان بتوانند منتظر پاسخ سرور بمانند.
    public class RealtimeAckTracker
    {
        private readonly Dictionary<string, TaskCompletionSource<RealtimeAck>> dict_PendingAckByMessageId = new Dictionary<string, TaskCompletionSource<RealtimeAck>>();
        private readonly object lockObject = new object();

        public event Action<string> AckTimeout;
        public event Action<string, RealtimeAck> AckCompleted;
        public event Action<string> AckTrackerLogReceived;

        public int PendingCount
        {
            get
            {
                lock (lockObject) return dict_PendingAckByMessageId.Count;
            }
        }

        //* انتظار اَک را برای پیام مشخص آماده می کند و اگر قبلا وجود داشته باشد، همان را جایگزین می کند.
        public Task<RealtimeAck> WatchAsync(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return Task.FromResult<RealtimeAck>(null);

            lock (lockObject)
            {
                var waiter = new TaskCompletionSource<RealtimeAck>();
                dict_PendingAckByMessageId[messageId] = waiter;
                WriteLog("Ack watch started: " + messageId);
                return waiter.Task;
            }
        }

        //* اگر اِنولوپ دریافتی اَک باشد، انتظار مربوط به پیام اصلی را کامل می کند.
        public bool TryCompleteFromEnvelope(RealtimeEnvelope envelope)
        {
            RealtimeAck ack = RealtimeAck.FromEnvelope(envelope);
            if (ack == null || string.IsNullOrWhiteSpace(ack.originalMessageId)) return false;

            TaskCompletionSource<RealtimeAck> waiter;
            lock (lockObject)
            {
                if (!dict_PendingAckByMessageId.TryGetValue(ack.originalMessageId, out waiter)) return false;
                dict_PendingAckByMessageId.Remove(ack.originalMessageId);
            }

            waiter.TrySetResult(ack);
            AckCompleted?.Invoke(ack.originalMessageId, ack);
            WriteLog("Ack completed: " + ack.originalMessageId + " | " + ack.status);
            return true;
        }

        //* تا زمان رسیدن اَک یا تمام شدن زمان مجاز منتظر می ماند.
        public async Task<RealtimeAck> WaitForAckAsync(string messageId, Task<RealtimeAck> ackTask, int timeoutMs, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(messageId) || ackTask == null) return null;

            try
            {
                Task timeoutTask = Task.Delay(Math.Max(100, timeoutMs), cancellationToken);
                Task completedTask = await Task.WhenAny(ackTask, timeoutTask);
                if (completedTask == ackTask) return await ackTask;

                Remove(messageId);
                AckTimeout?.Invoke(messageId);
                WriteLog("Ack timeout: " + messageId);
                return null;
            }
            catch (TaskCanceledException)
            {
                Remove(messageId);
                return null;
            }
        }

        //* انتظار اَک یک پیام را بدون کامل شدن حذف می کند.
        public void Remove(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return;

            lock (lockObject)
            {
                dict_PendingAckByMessageId.Remove(messageId);
            }
        }

        //* همه انتظارهای اَک را هنگام دیسکانکت یا دیسپوز لغو می کند.
        public void CancelAll(string reason)
        {
            List<TaskCompletionSource<RealtimeAck>> waiters = new List<TaskCompletionSource<RealtimeAck>>();

            lock (lockObject)
            {
                foreach (var item in dict_PendingAckByMessageId) waiters.Add(item.Value);
                dict_PendingAckByMessageId.Clear();
            }

            foreach (var waiter in waiters) waiter.TrySetResult(null);
            WriteLog("Ack tracker cleared: " + (reason ?? string.Empty));
        }

        //* لاگ داخلی اَک ترَکِر را به بیرون می فرستد.
        private void WriteLog(string message)
        {
            AckTrackerLogReceived?.Invoke(message ?? string.Empty);
        }
    }
}

//* این فایل اَک های در انتظار را مدیریت می کند.
//* هدف آن جلوگیری از گم شدن نتیجه پیام های مهم و پشتیبانی از تایم اوت و ریتِرای است.
