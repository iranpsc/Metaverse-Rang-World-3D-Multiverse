using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.Voice.Client.BrowserAudio
{
    public readonly struct VoiceBrowserAudioStats
    {
        public VoiceBrowserAudioStats(
            long captureBlocks,
            long encodedFrames,
            long decodedFrames,
            long encodedBytes,
            long playbackUnderflows,
            long captureDrops,
            int lastPacketBytes,
            int bitrate,
            bool fec,
            int packetLossPercent,
            int encodeAverageMicros,
            int encodeMaxMicros,
            int cadenceMaxDeviationMicros,
            long playbackDrops = 0,
            int remoteStreams = 0,
            int remoteBufferedFrames = 0,
            int remoteTargetDelayMs = 0,
            int remoteJitterMicros = 0,
            long remoteLateDrops = 0,
            long remoteDuplicateDrops = 0,
            long remoteReorderedPackets = 0,
            long remoteMissingFrames = 0,
            long remoteOverflowDrops = 0,
            long remoteNormalFrames = 0,
            long remoteFecFrames = 0,
            long remotePlcFrames = 0)
        {
            CaptureBlocks = captureBlocks;
            EncodedFrames = encodedFrames;
            DecodedFrames = decodedFrames;
            EncodedBytes = encodedBytes;
            PlaybackUnderflows = playbackUnderflows;
            CaptureDrops = captureDrops;
            LastPacketBytes = lastPacketBytes;
            Bitrate = bitrate;
            Fec = fec;
            PacketLossPercent = packetLossPercent;
            EncodeAverageMicros = encodeAverageMicros;
            EncodeMaxMicros = encodeMaxMicros;
            CadenceMaxDeviationMicros = cadenceMaxDeviationMicros;
            PlaybackDrops = playbackDrops;
            RemoteStreams = remoteStreams;
            RemoteBufferedFrames = remoteBufferedFrames;
            RemoteTargetDelayMs = remoteTargetDelayMs;
            RemoteJitterMicros = remoteJitterMicros;
            RemoteLateDrops = remoteLateDrops;
            RemoteDuplicateDrops = remoteDuplicateDrops;
            RemoteReorderedPackets = remoteReorderedPackets;
            RemoteMissingFrames = remoteMissingFrames;
            RemoteOverflowDrops = remoteOverflowDrops;
            RemoteNormalFrames = remoteNormalFrames;
            RemoteFecFrames = remoteFecFrames;
            RemotePlcFrames = remotePlcFrames;
        }

        public long CaptureBlocks { get; }
        public long EncodedFrames { get; }
        public long DecodedFrames { get; }
        public long EncodedBytes { get; }
        public long PlaybackUnderflows { get; }
        public long CaptureDrops { get; }
        public int LastPacketBytes { get; }
        public int Bitrate { get; }
        public bool Fec { get; }
        public int PacketLossPercent { get; }
        public int EncodeAverageMicros { get; }
        public int EncodeMaxMicros { get; }
        public int CadenceMaxDeviationMicros { get; }
        public long PlaybackDrops { get; }
        public int RemoteStreams { get; }
        public int RemoteBufferedFrames { get; }
        public int RemoteTargetDelayMs { get; }
        public int RemoteJitterMicros { get; }
        public long RemoteLateDrops { get; }
        public long RemoteDuplicateDrops { get; }
        public long RemoteReorderedPackets { get; }
        public long RemoteMissingFrames { get; }
        public long RemoteOverflowDrops { get; }
        public long RemoteNormalFrames { get; }
        public long RemoteFecFrames { get; }
        public long RemotePlcFrames { get; }
    }

    public sealed class VoiceBrowserAudioPipeline : IDisposable
    {
        private static int nextHandle;

        private readonly int handle;
        private readonly TaskCompletionSource<bool> startCompletion =
            new TaskCompletionSource<bool>();

        private bool registered;
        private bool starting;
        private bool started;
        private bool disposed;

        public event Action<VoiceBrowserAudioStats> StatsUpdated;
        public event Action<int, bool, int> EncoderConfigured;
        public event Action<string> Failed;
        public event Action Ready;

        public int SourceSampleRate { get; private set; }
        public string AudioContextState { get; private set; } = string.Empty;
        public string OpusVersion { get; private set; } = string.Empty;
        public VoiceBrowserAudioStats LatestStats { get; private set; }
        public bool IsStarted => started;
        internal int Handle => handle;

        public VoiceBrowserAudioPipeline()
        {
            handle = Interlocked.Increment(ref nextHandle);
        }

        public static bool IsSupported
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public async Task<bool> StartAsync(
            string assetRoot,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (!BeginStart(assetRoot)) return false;

            Task timeoutTask = Task.Delay(timeoutMs, cancellationToken);
            Task completedTask = await Task.WhenAny(startCompletion.Task, timeoutTask);

            if (completedTask == startCompletion.Task)
            {
                bool result = await startCompletion.Task;
                starting = false;
                started = result;
                if (!result) StopNative();
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            NotifyFailure("browser_audio_start_timeout");
            starting = false;
            StopNative();
            return false;
        }

        public bool BeginStart(string assetRoot)
        {
            ThrowIfDisposed();
            if (starting || started) throw new InvalidOperationException("browser_audio_pipeline_already_started");
            if (string.IsNullOrWhiteSpace(assetRoot)) throw new ArgumentException("browser_audio_asset_root_required", nameof(assetRoot));
            if (!IsSupported) return false;

            starting = true;
            VoiceBrowserAudioCallbackBridge.Register(handle, this);
            registered = true;

            int result = VoiceBrowserAudioNative.Start(
                handle,
                VoiceBrowserAudioCallbackBridge.ObjectName,
                assetRoot.TrimEnd('/'));

            if (result != 0) return true;

            NotifyFailure("browser_audio_start_rejected");
            StopNative();
            return false;
        }

        public bool SetCaptureEnabled(bool enabled)
        {
            if (disposed || !started || !IsSupported) return false;
            return VoiceBrowserAudioNative.SetCaptureEnabled(handle, enabled ? 1 : 0) != 0;
        }

        public bool SetPlaybackEnabled(bool enabled)
        {
            if (disposed || !started || !IsSupported) return false;
            return VoiceBrowserAudioNative.SetPlaybackEnabled(
                       handle,
                       enabled ? 1 : 0) != 0;
        }

        public bool ConfigureEncoder(int bitrate, bool fec, int packetLossPercent)
        {
            if (disposed || !started || !IsSupported) return false;
            if (bitrate < 6000 || bitrate > 510000) throw new ArgumentOutOfRangeException(nameof(bitrate));
            if (packetLossPercent < 0 || packetLossPercent > 100) throw new ArgumentOutOfRangeException(nameof(packetLossPercent));
            return VoiceBrowserAudioNative.ConfigureEncoder(
                       handle,
                       bitrate,
                       fec ? 1 : 0,
                       packetLossPercent) != 0;
        }

        internal void NotifyReady(string[] values)
        {
            if (disposed || values == null || values.Length != 3) return;
            if (!TryParseInt(values[0], out int sampleRate) || sampleRate < 8000)
            {
                NotifyFailure("browser_audio_sample_rate_invalid");
                return;
            }

            SourceSampleRate = sampleRate;
            AudioContextState = values[1] ?? string.Empty;
            OpusVersion = values[2] ?? string.Empty;
            Debug.Log(
                "[VoiceBrowserAudio] stage=unity_ready_received" +
                " | sampleRate=" + SourceSampleRate +
                " | contextState=" + AudioContextState +
                " | opusVersion=" + OpusVersion);

            starting = false;
            started = true;
            Ready?.Invoke();
            startCompletion.TrySetResult(true);
        }

        internal void NotifyStats(string[] values)
        {
            if (disposed || values == null) return;
            if (values.Length != 10 && values.Length != 13 &&
                values.Length != 26)
            {
                Debug.LogWarning(
                    "[VoiceBrowserAudio] stage=stats_ignored" +
                    " | reason=field_count" +
                    " | actual=" + values.Length +
                    " | expected=10_or_13_or_26");
                return;
            }

            if (!TryParseLong(values[0], out long captureBlocks) ||
                !TryParseLong(values[1], out long encodedFrames) ||
                !TryParseLong(values[2], out long decodedFrames) ||
                !TryParseLong(values[3], out long encodedBytes) ||
                !TryParseLong(values[4], out long playbackUnderflows) ||
                !TryParseLong(values[5], out long captureDrops) ||
                !TryParseInt(values[6], out int lastPacketBytes) ||
                !TryParseInt(values[7], out int bitrate) ||
                !TryParseInt(values[8], out int fecValue) ||
                !TryParseInt(values[9], out int packetLossPercent))
            {
                Debug.LogWarning(
                    "[VoiceBrowserAudio] stage=stats_ignored" +
                    " | reason=base_field_parse");
                return;
            }

            int encodeAverageMicros = 0;
            int encodeMaxMicros = 0;
            int cadenceMaxDeviationMicros = 0;
            if (values.Length >= 13 &&
                (!TryParseInt(values[10], out encodeAverageMicros) ||
                 !TryParseInt(values[11], out encodeMaxMicros) ||
                 !TryParseInt(
                     values[12],
                     out cadenceMaxDeviationMicros)))
            {
                Debug.LogWarning(
                    "[VoiceBrowserAudio] stage=stats_optional_metrics_defaulted" +
                    " | reason=optional_field_parse");
                encodeAverageMicros = 0;
                encodeMaxMicros = 0;
                cadenceMaxDeviationMicros = 0;
            }

            long playbackDrops = 0;
            int remoteStreams = 0;
            int remoteBufferedFrames = 0;
            int remoteTargetDelayMs = 0;
            int remoteJitterMicros = 0;
            long remoteLateDrops = 0;
            long remoteDuplicateDrops = 0;
            long remoteReorderedPackets = 0;
            long remoteMissingFrames = 0;
            long remoteOverflowDrops = 0;
            long remoteNormalFrames = 0;
            long remoteFecFrames = 0;
            long remotePlcFrames = 0;
            if (values.Length == 26 &&
                (!TryParseLong(values[13], out playbackDrops) ||
                 !TryParseInt(values[14], out remoteStreams) ||
                 !TryParseInt(values[15], out remoteBufferedFrames) ||
                 !TryParseInt(values[16], out remoteTargetDelayMs) ||
                 !TryParseInt(values[17], out remoteJitterMicros) ||
                 !TryParseLong(values[18], out remoteLateDrops) ||
                 !TryParseLong(values[19], out remoteDuplicateDrops) ||
                 !TryParseLong(values[20], out remoteReorderedPackets) ||
                 !TryParseLong(values[21], out remoteMissingFrames) ||
                 !TryParseLong(values[22], out remoteOverflowDrops) ||
                 !TryParseLong(values[23], out remoteNormalFrames) ||
                 !TryParseLong(values[24], out remoteFecFrames) ||
                 !TryParseLong(values[25], out remotePlcFrames)))
            {
                Debug.LogWarning(
                    "[VoiceBrowserAudio] stage=stats_ignored" +
                    " | reason=playout_field_parse");
                return;
            }

            LatestStats = new VoiceBrowserAudioStats(
                captureBlocks,
                encodedFrames,
                decodedFrames,
                encodedBytes,
                playbackUnderflows,
                captureDrops,
                lastPacketBytes,
                bitrate,
                fecValue == 1,
                packetLossPercent,
                encodeAverageMicros,
                encodeMaxMicros,
                cadenceMaxDeviationMicros,
                playbackDrops,
                remoteStreams,
                remoteBufferedFrames,
                remoteTargetDelayMs,
                remoteJitterMicros,
                remoteLateDrops,
                remoteDuplicateDrops,
                remoteReorderedPackets,
                remoteMissingFrames,
                remoteOverflowDrops,
                remoteNormalFrames,
                remoteFecFrames,
                remotePlcFrames);

            StatsUpdated?.Invoke(LatestStats);
        }

        internal void NotifyConfigured(string[] values)
        {
            if (disposed || values == null || values.Length != 3) return;
            if (!TryParseInt(values[0], out int bitrate) ||
                !TryParseInt(values[1], out int fecValue) ||
                !TryParseInt(values[2], out int packetLossPercent))
            {
                NotifyFailure("browser_audio_configuration_result_invalid");
                return;
            }

            EncoderConfigured?.Invoke(bitrate, fecValue == 1, packetLossPercent);
        }

        internal void NotifyFailure(string reason)
        {
            if (disposed) return;
            string safeReason = string.IsNullOrWhiteSpace(reason)
                ? "browser_audio_unknown_failure"
                : reason.Trim();
            startCompletion.TrySetResult(false);
            Failed?.Invoke(safeReason);
        }

        internal void NotifyStopped()
        {
            starting = false;
            started = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopNative();

            if (registered)
            {
                VoiceBrowserAudioCallbackBridge.Unregister(handle);
                registered = false;
            }

            StatsUpdated = null;
            EncoderConfigured = null;
            Failed = null;
            Ready = null;
        }

        private void StopNative()
        {
            if ((starting || started) && IsSupported)
            {
                VoiceBrowserAudioNative.Stop(handle);
            }

            starting = false;
            started = false;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceBrowserAudioPipeline));
        }

        private static bool TryParseLong(string value, out long parsed)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryParseInt(string value, out int parsed)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        }
    }

    internal static class VoiceBrowserAudioNative
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int VoiceBrowserAudioStart(int handle, string objectName, string assetRoot);

        [DllImport("__Internal")]
        private static extern int VoiceBrowserAudioSetCaptureEnabled(int handle, int enabled);

        [DllImport("__Internal")]
        private static extern int VoiceBrowserAudioSetPlaybackEnabled(int handle, int enabled);

        [DllImport("__Internal")]
        private static extern int VoiceBrowserAudioConfigureEncoder(int handle, int bitrate, int fec, int packetLossPercent);

        [DllImport("__Internal")]
        private static extern void VoiceBrowserAudioStop(int handle);
#endif

        internal static int Start(int handle, string objectName, string assetRoot)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return VoiceBrowserAudioStart(handle, objectName, assetRoot);
#else
            return 0;
#endif
        }

        internal static int SetCaptureEnabled(int handle, int enabled)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return VoiceBrowserAudioSetCaptureEnabled(handle, enabled);
#else
            return 0;
#endif
        }

        internal static int SetPlaybackEnabled(int handle, int enabled)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return VoiceBrowserAudioSetPlaybackEnabled(handle, enabled);
#else
            return 0;
#endif
        }

        internal static int ConfigureEncoder(int handle, int bitrate, int fec, int packetLossPercent)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return VoiceBrowserAudioConfigureEncoder(handle, bitrate, fec, packetLossPercent);
#else
            return 0;
#endif
        }

        internal static void Stop(int handle)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            VoiceBrowserAudioStop(handle);
#endif
        }
    }

    internal sealed class VoiceBrowserAudioCallbackBridge : MonoBehaviour
    {
        internal const string ObjectName = "VoiceBrowserAudioCallbackBridge";

        private static readonly object Gate = new object();
        private static readonly Dictionary<int, VoiceBrowserAudioPipeline> Pipelines =
            new Dictionary<int, VoiceBrowserAudioPipeline>();

        private static VoiceBrowserAudioCallbackBridge instance;

        internal static void Register(int handle, VoiceBrowserAudioPipeline pipeline)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            EnsureInstance();
            lock (Gate) Pipelines[handle] = pipeline;
        }

        internal static void Unregister(int handle)
        {
            lock (Gate) Pipelines.Remove(handle);
        }

        public void HandleVoiceBrowserAudioEvent(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            string[] parts = payload.Split('|');
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int handle))
            {
                return;
            }

            VoiceBrowserAudioPipeline pipeline = GetPipeline(handle);
            if (pipeline == null) return;

            string eventName = parts[1] ?? string.Empty;
            string[] values = new string[Math.Max(0, parts.Length - 2)];
            if (values.Length > 0) Array.Copy(parts, 2, values, 0, values.Length);

            switch (eventName)
            {
                case "ready":
                    pipeline.NotifyReady(values);
                    break;
                case "stats":
                    pipeline.NotifyStats(values);
                    break;
                case "configured":
                    pipeline.NotifyConfigured(values);
                    break;
                case "failure":
                    pipeline.NotifyFailure(values.Length > 0 ? values[0] : string.Empty);
                    break;
                case "stopped":
                    pipeline.NotifyStopped();
                    break;
            }
        }

        private static VoiceBrowserAudioPipeline GetPipeline(int handle)
        {
            lock (Gate)
            {
                Pipelines.TryGetValue(handle, out VoiceBrowserAudioPipeline pipeline);
                return pipeline;
            }
        }

        private static void EnsureInstance()
        {
            if (instance != null) return;

            GameObject target = GameObject.Find(ObjectName);
            if (target == null) target = new GameObject(ObjectName);

            instance = target.GetComponent<VoiceBrowserAudioCallbackBridge>();
            if (instance == null) instance = target.AddComponent<VoiceBrowserAudioCallbackBridge>();
            DontDestroyOnLoad(target);
        }
    }
}
