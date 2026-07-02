using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.Realtime.Stability
{
    //* سلامت اتصال ریل تایم را با پینگ و پونگ کنترل می کند و به ترنسپورت وابسته نیست.
    public class RealtimeHeartbeat : IDisposable
    {
        private readonly RealtimeClient realtimeClient;
        private CancellationTokenSource heartbeatCts;
        private bool isRunning;
        private int missedPongCount;
        private long lastPongUnixMs;
        private long lastPingUnixMs;

        public event Action<int> PongMissed;
        public event Action ConnectionTimeout;
        public event Action PongReceived;
        public event Action<string> HeartbeatLogReceived;

        public int pingIntervalMs = 30000;
        public int pongTimeoutMs = 10000;
        public int maxMissedPongs = 3;
        public bool logHeartbeat;

        public bool IsRunning => isRunning;
        public int MissedPongCount => missedPongCount;
        public long LastPongUnixMs => lastPongUnixMs;
        public long LastPingUnixMs => lastPingUnixMs;

        //* هارت بیت را به کُر ریل تایم وصل می کند تا فقط اِنولوپ استاندارد ارسال و دریافت شود.
        public RealtimeHeartbeat(RealtimeClient realtimeClient)
        {
            this.realtimeClient = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
        }

        //* حلقه پینگ را شروع می کند و دریافت پونگ را از رویداد کُر گوش می دهد.
        public void Start()
        {
            if (isRunning) return;

            isRunning = true;
            missedPongCount = 0;
            lastPongUnixMs = RealtimeJsonUtil.NowUnixMs();
            realtimeClient.EnvelopeReceived += HandleEnvelopeReceived;
            heartbeatCts = new CancellationTokenSource();
            _ = RunHeartbeatLoopAsync(heartbeatCts.Token);
            WriteLog("Realtime heartbeat started.");
        }

        //* حلقه پینگ را متوقف می کند و رویدادهای کُر را آزاد می کند.
        public void Stop()
        {
            if (!isRunning) return;

            isRunning = false;
            realtimeClient.EnvelopeReceived -= HandleEnvelopeReceived;
            heartbeatCts?.Cancel();
            heartbeatCts?.Dispose();
            heartbeatCts = null;
            missedPongCount = 0;
            WriteLog("Realtime heartbeat stopped.");
        }

        //* وضعیت شمارنده پونگ ها را برای شروع دوباره یا اتصال تازه پاک می کند.
        public void Reset()
        {
            missedPongCount = 0;
            lastPongUnixMs = RealtimeJsonUtil.NowUnixMs();
            lastPingUnixMs = 0;
        }

        //* حلقه اصلی هارت بیت است و تا زمان توقف، پینگ می فرستد و تایم اوت پونگ را بررسی می کند.
        private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (realtimeClient.IsConnected) await SendPingAndWaitForPongAsync(cancellationToken);
                    await Task.Delay(Math.Max(500, pingIntervalMs), cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    WriteLog("Realtime heartbeat loop error: " + ex.Message);
                    await DelaySafeAsync(1000, cancellationToken);
                }
            }
        }

        //* یک پینگ سیستمی می فرستد و بعد از زمان مشخص، وضعیت پونگ را بررسی می کند.
        private async Task SendPingAndWaitForPongAsync(CancellationToken cancellationToken)
        {
            lastPingUnixMs = RealtimeJsonUtil.NowUnixMs();
            bool sent = await realtimeClient.SendPingAsync(cancellationToken);

            if (!sent)
            {
                RegisterMissedPong();
                return;
            }

            await Task.Delay(Math.Max(500, pongTimeoutMs), cancellationToken);
            if (lastPongUnixMs < lastPingUnixMs) RegisterMissedPong();
        }

        //* پیام های دریافتی کُر را بررسی می کند و اگر پونگ بود، سلامت اتصال را تازه می کند.
        private void HandleEnvelopeReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            if (envelope.ch != RealtimeChannels.System || envelope.t != RealtimeMessageTypes.Pong) return;

            missedPongCount = 0;
            lastPongUnixMs = RealtimeJsonUtil.NowUnixMs();
            PongReceived?.Invoke();
            WriteLog("Realtime pong received.");
        }

        //* یک پونگ از دست رفته را ثبت می کند و اگر از حد مجاز گذشت، تایم اوت اتصال را اعلام می کند.
        private void RegisterMissedPong()
        {
            missedPongCount++;
            PongMissed?.Invoke(missedPongCount);
            WriteLog("Realtime pong missed: " + missedPongCount + "/" + maxMissedPongs);

            if (missedPongCount >= maxMissedPongs) ConnectionTimeout?.Invoke();
        }

        //* تاخیر امن اجرا می کند تا لغو شدن تسک باعث خطای اضافی نشود.
        private async Task DelaySafeAsync(int delayMs, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Math.Max(1, delayMs), cancellationToken);
            }
            catch (TaskCanceledException)
            {
            }
        }

        //* لاگ داخلی هارت بیت را در صورت فعال بودن به بیرون می فرستد.
        private void WriteLog(string message)
        {
            if (logHeartbeat) Debug.Log("[RealtimeHeartbeat] " + message);
            HeartbeatLogReceived?.Invoke(message);
        }

        //* منابع هارت بیت را آزاد می کند.
        public void Dispose()
        {
            Stop();
        }
    }
}

//* این فایل هارت بیت ریل تایم را مدیریت می کند.
//* این فایل فقط پینگ و پونگ استاندارد می فرستد و به وب سوکت یا جی آر پی سی وابسته نیست.
