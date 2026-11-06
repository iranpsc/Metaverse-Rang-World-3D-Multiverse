#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Network_A.Voice.Client.BrowserAudio
{
    public readonly struct VoiceBrowserMediaStats
    {
        public VoiceBrowserMediaStats(
            bool bound,
            long outboundFrames,
            long inboundFrames,
            long droppedOutboundFrames,
            long pongCount,
            int lastOutboundPacketBytes,
            int lastInboundPacketBytes,
            long backpressureDrops,
            long maxBufferedBytes)
        {
            Bound = bound;
            OutboundFrames = outboundFrames;
            InboundFrames = inboundFrames;
            DroppedOutboundFrames = droppedOutboundFrames;
            PongCount = pongCount;
            LastOutboundPacketBytes = lastOutboundPacketBytes;
            LastInboundPacketBytes = lastInboundPacketBytes;
            BackpressureDrops = backpressureDrops;
            MaxBufferedBytes = maxBufferedBytes;
        }

        public bool Bound { get; }
        public long OutboundFrames { get; }
        public long InboundFrames { get; }
        public long DroppedOutboundFrames { get; }
        public long PongCount { get; }
        public int LastOutboundPacketBytes { get; }
        public int LastInboundPacketBytes { get; }
        public long BackpressureDrops { get; }
        public long MaxBufferedBytes { get; }
    }

    public sealed class VoiceBrowserMediaSession : IDisposable
    {
        private readonly VoiceBrowserAudioPipeline audioPipeline;
        private readonly int handle;
        private bool registered;
        private bool connecting;
        private bool connected;
        private bool bound;
        private bool disposed;

        public event Action Opened;
        public event Action<string> Bound;
        public event Action<VoiceBrowserMediaStats> StatsUpdated;
        public event Action<string> Failed;
        public event Action<string, bool> Disconnected;

        public bool IsBound => bound && !disposed;
        public VoiceBrowserMediaStats LatestStats { get; private set; }

        public VoiceBrowserMediaSession(VoiceBrowserAudioPipeline audioPipeline)
        {
            this.audioPipeline = audioPipeline ??
                throw new ArgumentNullException(nameof(audioPipeline));
            handle = audioPipeline.Handle;
        }

        public bool Connect(
            string endpoint,
            string accessToken,
            string roomId,
            string controlConnectionId,
            string streamId,
            string clientBuild)
        {
            ThrowIfDisposed();
            if (connecting || connected || bound) return false;
            if (!audioPipeline.IsStarted) return false;
            if (string.IsNullOrWhiteSpace(endpoint) ||
                string.IsNullOrWhiteSpace(accessToken) ||
                string.IsNullOrWhiteSpace(roomId) ||
                !Guid.TryParse(controlConnectionId, out _) ||
                !Guid.TryParse(streamId, out _))
            {
                return false;
            }

            VoiceBrowserMediaCallbackBridge.Register(handle, this);
            registered = true;
            connecting = true;

            int result = VoiceBrowserMediaNative.Connect(
                handle,
                VoiceBrowserMediaCallbackBridge.ObjectName,
                endpoint.Trim(),
                accessToken.Trim(),
                roomId.Trim(),
                controlConnectionId.Trim().ToLowerInvariant(),
                streamId.Trim().ToLowerInvariant(),
                (clientBuild ?? string.Empty).Trim());

            if (result != 0) return true;

            connecting = false;
            Unregister();
            return false;
        }

        public bool SetOutboundEnabled(bool enabled)
        {
            if (disposed || !bound) return false;
            return VoiceBrowserMediaNative.SetOutboundEnabled(
                       handle,
                       enabled ? 1 : 0) != 0;
        }

        internal void NotifyOpen()
        {
            if (disposed) return;
            connecting = false;
            connected = true;
            Opened?.Invoke();
        }

        internal void NotifyBound(string[] values)
        {
            if (disposed || values == null || values.Length != 1) return;
            string controlConnectionId = Safe(values[0]).ToLowerInvariant();
            if (!Guid.TryParse(controlConnectionId, out _))
            {
                NotifyFailure("browser_media_bound_connection_invalid");
                return;
            }

            connecting = false;
            connected = true;
            bound = true;
            Bound?.Invoke(controlConnectionId);
        }

        internal void NotifyStats(string[] values)
        {
            if (disposed || values == null) return;
            if (values.Length != 7 && values.Length != 9)
            {
                Debug.LogWarning(
                    "[VoiceBrowserMedia] stage=stats_ignored" +
                    " | reason=field_count" +
                    " | actual=" + values.Length +
                    " | expected=7_or_9");
                return;
            }

            if (!TryParseInt(values[0], out int boundValue) ||
                !TryParseLong(values[1], out long outboundFrames) ||
                !TryParseLong(values[2], out long inboundFrames) ||
                !TryParseLong(values[3], out long droppedOutboundFrames) ||
                !TryParseLong(values[4], out long pongCount) ||
                !TryParseInt(values[5], out int lastOutboundPacketBytes) ||
                !TryParseInt(values[6], out int lastInboundPacketBytes))
            {
                Debug.LogWarning(
                    "[VoiceBrowserMedia] stage=stats_ignored" +
                    " | reason=base_field_parse");
                return;
            }

            long backpressureDrops = 0;
            long maxBufferedBytes = 0;
            if (values.Length == 9 &&
                (!TryParseLong(values[7], out backpressureDrops) ||
                 !TryParseLong(values[8], out maxBufferedBytes)))
            {
                Debug.LogWarning(
                    "[VoiceBrowserMedia] stage=stats_optional_metrics_defaulted" +
                    " | reason=optional_field_parse");
                backpressureDrops = 0;
                maxBufferedBytes = 0;
            }

            LatestStats = new VoiceBrowserMediaStats(
                boundValue == 1,
                outboundFrames,
                inboundFrames,
                droppedOutboundFrames,
                pongCount,
                lastOutboundPacketBytes,
                lastInboundPacketBytes,
                backpressureDrops,
                maxBufferedBytes);
            StatsUpdated?.Invoke(LatestStats);
        }

        internal void NotifyFailure(string reason)
        {
            if (disposed) return;
            Failed?.Invoke(Safe(reason));
        }

        internal void NotifyDisconnected(string[] values)
        {
            if (disposed) return;
            string reason = values != null && values.Length > 0
                ? Safe(values[0])
                : "remote_closed";
            bool intentional = values != null &&
                values.Length > 1 &&
                string.Equals(values[1], "1", StringComparison.Ordinal);

            connecting = false;
            connected = false;
            bound = false;
            Unregister();
            Disconnected?.Invoke(reason, intentional);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (connecting || connected || bound)
            {
                VoiceBrowserMediaNative.Disconnect(
                    handle,
                    "browser_media_session_dispose");
            }

            connecting = false;
            connected = false;
            bound = false;
            Unregister();
            Opened = null;
            Bound = null;
            StatsUpdated = null;
            Failed = null;
            Disconnected = null;
        }

        private void Unregister()
        {
            if (!registered) return;
            VoiceBrowserMediaCallbackBridge.Unregister(handle);
            registered = false;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(VoiceBrowserMediaSession));
        }

        private static bool TryParseLong(string value, out long parsed)
        {
            return long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed);
        }

        private static bool TryParseInt(string value, out int parsed)
        {
            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed);
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }
    }

    internal static class VoiceBrowserMediaNative
    {
        [DllImport("__Internal")]
        private static extern int VoiceBrowserMediaConnect(
            int handle,
            string objectName,
            string endpoint,
            string accessToken,
            string roomId,
            string controlConnectionId,
            string streamId,
            string clientBuild);

        [DllImport("__Internal")]
        private static extern int VoiceBrowserMediaSetOutboundEnabled(
            int handle,
            int enabled);

        [DllImport("__Internal")]
        private static extern void VoiceBrowserMediaDisconnect(
            int handle,
            string reason);

        internal static int Connect(
            int handle,
            string objectName,
            string endpoint,
            string accessToken,
            string roomId,
            string controlConnectionId,
            string streamId,
            string clientBuild)
        {
            return VoiceBrowserMediaConnect(
                handle,
                objectName,
                endpoint,
                accessToken,
                roomId,
                controlConnectionId,
                streamId,
                clientBuild);
        }

        internal static int SetOutboundEnabled(int handle, int enabled)
        {
            return VoiceBrowserMediaSetOutboundEnabled(handle, enabled);
        }

        internal static void Disconnect(int handle, string reason)
        {
            VoiceBrowserMediaDisconnect(handle, reason);
        }
    }

    internal sealed class VoiceBrowserMediaCallbackBridge : MonoBehaviour
    {
        internal const string ObjectName = "VoiceBrowserMediaCallbackBridge";

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, VoiceBrowserMediaSession> Sessions =
            new Dictionary<int, VoiceBrowserMediaSession>();

        private static VoiceBrowserMediaCallbackBridge instance;

        internal static void Register(
            int handle,
            VoiceBrowserMediaSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            EnsureInstance();
            lock (Gate) Sessions[handle] = session;
        }

        internal static void Unregister(int handle)
        {
            lock (Gate) Sessions.Remove(handle);
        }

        public void HandleVoiceBrowserMediaEvent(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            string[] parts = payload.Split('|');
            if (parts.Length < 2 ||
                !int.TryParse(
                    parts[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int handle))
            {
                return;
            }

            VoiceBrowserMediaSession session = GetSession(handle);
            if (session == null) return;

            string[] values = new string[Math.Max(0, parts.Length - 2)];
            if (values.Length > 0)
                Array.Copy(parts, 2, values, 0, values.Length);

            switch (parts[1] ?? string.Empty)
            {
                case "open":
                    session.NotifyOpen();
                    break;
                case "bound":
                    session.NotifyBound(values);
                    break;
                case "stats":
                    session.NotifyStats(values);
                    break;
                case "failure":
                    session.NotifyFailure(
                        values.Length > 0 ? values[0] : string.Empty);
                    break;
                case "disconnected":
                    session.NotifyDisconnected(values);
                    break;
            }
        }

        private static VoiceBrowserMediaSession GetSession(int handle)
        {
            lock (Gate)
            {
                Sessions.TryGetValue(handle, out VoiceBrowserMediaSession session);
                return session;
            }
        }

        private static void EnsureInstance()
        {
            if (instance != null) return;

            GameObject target = GameObject.Find(ObjectName);
            if (target == null) target = new GameObject(ObjectName);

            instance = target.GetComponent<VoiceBrowserMediaCallbackBridge>();
            if (instance == null)
                instance = target.AddComponent<VoiceBrowserMediaCallbackBridge>();
            DontDestroyOnLoad(target);
        }
    }
}
#endif
