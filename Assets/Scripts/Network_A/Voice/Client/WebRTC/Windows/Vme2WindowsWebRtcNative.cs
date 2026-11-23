#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    // Phase 1 engine, Phase 3 transport lifecycle and native signaling interop.
    // Audio tracks and consumers are not started here.
    internal static class Vme2WindowsWebRtcNative
    {
        private const string Library = "VME2WindowsWebRtc";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_abi_version();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_initialize();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_shutdown();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern IntPtr vme2_webrtc_device_create();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_destroy(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true,
            CharSet = CharSet.Ansi)]
        internal static extern int vme2_webrtc_device_load(
            IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string routerRtpCapabilitiesJson);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_is_loaded(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_can_produce_audio(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_copy_recv_rtp_capabilities_json(
            IntPtr device, IntPtr destination, int capacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int NativeSignalingCallback(IntPtr context, int operation,
            IntPtr transportId, IntPtr requestJson, IntPtr response, int responseCapacity);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_set_signaling_callback(
            IntPtr device, NativeSignalingCallback callback, IntPtr context);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_create_send_transport(
            IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string transportParametersJson);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_create_recv_transport(
            IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string transportParametersJson);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_start_audio_producer(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_send_ice_state(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_copy_audio_producer_id(
            IntPtr device, IntPtr destination, int capacity);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_set_audio_producer_muted(IntPtr device, int muted);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_push_audio_pcm(
            IntPtr device, [In] float[] pcm, int frames, int sampleRate);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_close_audio_producer(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_consume_audio(
            IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string consumerParametersJson);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_close_audio_consumer(
            IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string consumerId);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_has_audio_consumer(
            IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string consumerId);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_recv_ice_state(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_get_audio_inbound_rtp_stats(IntPtr device,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string consumerId,
            out ulong packetsReceived, out long packetsLost);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_get_audio_pcm_info(IntPtr device,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string consumerId,
            out int sampleRate, out ulong framesReceived, out int bufferedFrames);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_copy_audio_pcm(IntPtr device,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string consumerId,
            [Out] float[] destination, int capacity);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_close_send_transport(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_close_recv_transport(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_has_send_transport(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_device_has_recv_transport(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_copy_version(IntPtr destination, int capacity);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern int vme2_webrtc_copy_last_error(IntPtr destination, int capacity);

        internal delegate int NativeTextWriter(IntPtr destination, int capacity);

        internal static string CopyUtf8(NativeTextWriter writer)
        {
            var bytesRequired = writer(IntPtr.Zero, 0);
            if (bytesRequired <= 0 || bytesRequired > 1024 * 1024)
                throw new InvalidOperationException("Invalid native UTF-8 string size: " + bytesRequired);

            IntPtr bytes = Marshal.AllocHGlobal(bytesRequired);
            try
            {
                var copied = writer(bytes, bytesRequired);
                if (copied != bytesRequired)
                    throw new InvalidOperationException("Native UTF-8 string changed during copy");
                byte[] buffer = new byte[bytesRequired - 1];
                if (buffer.Length > 0) Marshal.Copy(bytes, buffer, 0, buffer.Length);
                return Encoding.UTF8.GetString(buffer);
            }
            finally { Marshal.FreeHGlobal(bytes); }
        }

        internal static Exception NativeError(string operation)
        {
            string detail;
            try { detail = CopyUtf8(vme2_webrtc_copy_last_error); }
            catch { detail = "Native error details unavailable"; }
            return new InvalidOperationException(operation + ": " + detail);
        }
    }

    public static class Vme2WindowsWebRtcEngine
    {
        private static readonly object Sync = new object();
        private static bool initialized;
        private static int deviceCount;

        public static string InitializeAndGetVersion()
        {
            lock (Sync)
            {
                if (!initialized)
                {
                    if (Vme2WindowsWebRtcNative.vme2_webrtc_abi_version() != 1)
                        throw new InvalidOperationException("Unexpected VME2 Windows WebRTC native ABI version");
                    if (Vme2WindowsWebRtcNative.vme2_webrtc_initialize() != 1)
                        throw Vme2WindowsWebRtcNative.NativeError("Initialize WebRTC engine");
                    initialized = true;
                }
                return Vme2WindowsWebRtcNative.CopyUtf8(Vme2WindowsWebRtcNative.vme2_webrtc_copy_version);
            }
        }

        internal static IntPtr CreateDevice()
        {
            lock (Sync)
            {
                InitializeAndGetVersion();
                IntPtr handle = Vme2WindowsWebRtcNative.vme2_webrtc_device_create();
                if (handle == IntPtr.Zero)
                    throw Vme2WindowsWebRtcNative.NativeError("Create WebRTC Device");
                ++deviceCount;
                return handle;
            }
        }

        internal static void DestroyDevice(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            lock (Sync)
            {
                if (Vme2WindowsWebRtcNative.vme2_webrtc_device_destroy(handle) != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Destroy WebRTC Device");
                --deviceCount;
            }
        }

        public static void Shutdown()
        {
            lock (Sync)
            {
                if (deviceCount != 0)
                    throw new InvalidOperationException("Dispose all WebRTC Devices before shutdown");
                if (!initialized) return;
                if (Vme2WindowsWebRtcNative.vme2_webrtc_shutdown() != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Shutdown WebRTC engine");
                initialized = false;
            }
        }
    }

    public sealed class Vme2WindowsWebRtcDevice : IDisposable
    {
        private IntPtr handle;
        private bool disposed;
        private readonly object audioSampleSync = new object();
        private Func<int, string, string, string> signalingHandler;
        private GCHandle signalingContext;
        private static readonly Vme2WindowsWebRtcNative.NativeSignalingCallback SignalingThunk = InvokeSignaling;

        public Vme2WindowsWebRtcDevice()
        {
            handle = Vme2WindowsWebRtcEngine.CreateDevice();
        }

        public void LoadRouterRtpCapabilities(string routerRtpCapabilitiesJson)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(routerRtpCapabilitiesJson))
                throw new ArgumentException("Router RTP capabilities JSON is required", nameof(routerRtpCapabilitiesJson));
            if (Vme2WindowsWebRtcNative.vme2_webrtc_device_load(handle, routerRtpCapabilitiesJson) != 1)
                throw Vme2WindowsWebRtcNative.NativeError("Load router RTP capabilities");
        }

        public bool IsLoaded
        {
            get
            {
                EnsureNotDisposed();
                var state = Vme2WindowsWebRtcNative.vme2_webrtc_device_is_loaded(handle);
                if (state < 0) throw Vme2WindowsWebRtcNative.NativeError("Read WebRTC Device state");
                return state == 1;
            }
        }

        public bool CanProduceAudio
        {
            get
            {
                EnsureNotDisposed();
                var result = Vme2WindowsWebRtcNative.vme2_webrtc_device_can_produce_audio(handle);
                if (result < 0) throw Vme2WindowsWebRtcNative.NativeError("Read audio production capability");
                return result == 1;
            }
        }

        public string GetReceivingRtpCapabilitiesJson()
        {
            EnsureNotDisposed();
            return Vme2WindowsWebRtcNative.CopyUtf8((destination, capacity) =>
                Vme2WindowsWebRtcNative.vme2_webrtc_device_copy_recv_rtp_capabilities_json(
                    handle, destination, capacity));
        }

        public void SetSignalingHandler(Func<int, string, string, string> handler)
        {
            EnsureNotDisposed();
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (signalingHandler != null)
                throw new InvalidOperationException("SFU signaling handler already installed");
            signalingHandler = handler;
            signalingContext = GCHandle.Alloc(this);
            try
            {
                if (Vme2WindowsWebRtcNative.vme2_webrtc_device_set_signaling_callback(
                    handle, SignalingThunk, GCHandle.ToIntPtr(signalingContext)) != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Set SFU signaling handler");
            }
            catch
            {
                signalingContext.Free();
                signalingHandler = null;
                throw;
            }
        }

        [AOT.MonoPInvokeCallback(typeof(Vme2WindowsWebRtcNative.NativeSignalingCallback))]
        private static int InvokeSignaling(IntPtr context, int operation, IntPtr transportId,
            IntPtr requestJson, IntPtr response, int responseCapacity)
        {
            try
            {
                var owner = GCHandle.FromIntPtr(context).Target as Vme2WindowsWebRtcDevice;
                if (owner == null || owner.signalingHandler == null)
                    throw new ObjectDisposedException(nameof(Vme2WindowsWebRtcDevice));
                string result = owner.signalingHandler(operation, ReadNativeUtf8(transportId),
                    ReadNativeUtf8(requestJson));
                return WriteNativeUtf8(response, responseCapacity, result ?? string.Empty) ? 1 : 0;
            }
            catch (Exception exception)
            {
                try
                {
                    WriteNativeUtf8(response, responseCapacity,
                        exception.GetType().Name + ": " + exception.Message);
                }
                catch { /* Callback exceptions must not cross the ABI. */ }
                return 0;
            }
        }

        private static string ReadNativeUtf8(IntPtr address)
        {
            if (address == IntPtr.Zero) throw new ArgumentNullException(nameof(address));
            int length = 0;
            while (length < 262144 && System.Runtime.InteropServices.Marshal.ReadByte(address, length) != 0)
                length++;
            if (length >= 262144) throw new InvalidOperationException("Native signal JSON too large");
            var bytes = new byte[length];
            if (length != 0) Marshal.Copy(address, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool WriteNativeUtf8(IntPtr destination, int capacity, string text)
        {
            if (destination == IntPtr.Zero || capacity < 1) return false;
            byte[] encoded = Encoding.UTF8.GetBytes(text ?? string.Empty);
            if (encoded.Length + 1 > capacity)
            {
                Marshal.WriteByte(destination, 0, 0);
                return false;
            }
            if (encoded.Length > 0) Marshal.Copy(encoded, 0, destination, encoded.Length);
            Marshal.WriteByte(destination, encoded.Length, 0);
            return true;
        }

        public void CreateSendTransport(string transportParametersJson)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(transportParametersJson))
                throw new ArgumentException("Transport parameters JSON is required", nameof(transportParametersJson));
            if (Vme2WindowsWebRtcNative.vme2_webrtc_device_create_send_transport(handle, transportParametersJson) != 1)
                throw Vme2WindowsWebRtcNative.NativeError("Create SendTransport");
        }

        public void CreateRecvTransport(string transportParametersJson)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(transportParametersJson))
                throw new ArgumentException("Transport parameters JSON is required", nameof(transportParametersJson));
            if (Vme2WindowsWebRtcNative.vme2_webrtc_device_create_recv_transport(handle, transportParametersJson) != 1)
                throw Vme2WindowsWebRtcNative.NativeError("Create RecvTransport");
        }

        public bool HasSendTransport
        {
            get
            {
                EnsureNotDisposed();
                int result = Vme2WindowsWebRtcNative.vme2_webrtc_device_has_send_transport(handle);
                if (result < 0) throw Vme2WindowsWebRtcNative.NativeError("Read SendTransport state");
                return result == 1;
            }
        }

        public bool HasRecvTransport
        {
            get
            {
                EnsureNotDisposed();
                int result = Vme2WindowsWebRtcNative.vme2_webrtc_device_has_recv_transport(handle);
                if (result < 0) throw Vme2WindowsWebRtcNative.NativeError("Read RecvTransport state");
                return result == 1;
            }
        }

        public int SendIceState
        {
            get
            {
                EnsureNotDisposed();
                int state = Vme2WindowsWebRtcNative.vme2_webrtc_device_send_ice_state(handle);
                if (state < 0) throw Vme2WindowsWebRtcNative.NativeError("Read send ICE state");
                return state;
            }
        }

        public string StartAudioProducer()
        {
            lock (audioSampleSync)
            {
                EnsureNotDisposed();
                if (Vme2WindowsWebRtcNative.vme2_webrtc_device_start_audio_producer(handle) != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Start WebRTC PCM Producer");
                return Vme2WindowsWebRtcNative.CopyUtf8((destination, capacity) =>
                    Vme2WindowsWebRtcNative.vme2_webrtc_device_copy_audio_producer_id(
                        handle, destination, capacity));
            }
        }

        public void SetAudioProducerMuted(bool muted)
        {
            lock (audioSampleSync)
            {
                EnsureNotDisposed();
                if (Vme2WindowsWebRtcNative.vme2_webrtc_device_set_audio_producer_muted(
                    handle, muted ? 1 : 0) != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Update WebRTC Producer mute");
            }
        }

        public void PushAudioPcm(float[] pcm, int sampleRate)
        {
            lock (audioSampleSync)
            {
                EnsureNotDisposed();
                if (pcm == null || pcm.Length != 960 || sampleRate != 48000)
                    throw new ArgumentException("Expected 960 mono PCM floats at 48kHz");
                if (Vme2WindowsWebRtcNative.vme2_webrtc_device_push_audio_pcm(
                    handle, pcm, pcm.Length, sampleRate) != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Push WebRTC PCM");
            }
        }

        public void CloseAudioProducer()
        {
            lock (audioSampleSync)
            {
                EnsureNotDisposed();
                if (Vme2WindowsWebRtcNative.vme2_webrtc_device_close_audio_producer(handle) != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Close WebRTC Producer");
            }
        }

        public void ConsumeAudio(string consumerParametersJson)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(consumerParametersJson))
                throw new ArgumentException("Audio Consumer JSON is required", nameof(consumerParametersJson));
            if (Vme2WindowsWebRtcNative.vme2_webrtc_device_consume_audio(handle, consumerParametersJson) != 1)
                throw Vme2WindowsWebRtcNative.NativeError("Create Native audio Consumer");
        }

        public bool HasAudioConsumer(string consumerId)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(consumerId)) throw new ArgumentException("Consumer ID required", nameof(consumerId));
            int result = Vme2WindowsWebRtcNative.vme2_webrtc_device_has_audio_consumer(handle, consumerId);
            if (result < 0) throw Vme2WindowsWebRtcNative.NativeError("Query Native audio Consumer");
            return result == 1;
        }

        public void CloseAudioConsumer(string consumerId)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(consumerId)) throw new ArgumentException("Consumer ID required", nameof(consumerId));
            if (Vme2WindowsWebRtcNative.vme2_webrtc_device_close_audio_consumer(handle, consumerId) != 1)
                throw Vme2WindowsWebRtcNative.NativeError("Close Native audio Consumer");
        }

        // A decoded frame count greater than zero proves the WebRTC sink received PCM.
        // Returns true only for actual WebRTC inbound-rtp packet counters.
        // Optional new native export. Older DLLs cannot silently report zero.
        public bool TryGetAudioInboundRtpStats(string consumerId,
            out ulong packetsReceived, out long packetsLost)
        {
            lock (audioSampleSync)
            {
                EnsureNotDisposed();
                if (string.IsNullOrWhiteSpace(consumerId))
                    throw new ArgumentException("Consumer ID is required", nameof(consumerId));
                int result = Vme2WindowsWebRtcNative.vme2_webrtc_device_get_audio_inbound_rtp_stats(
                    handle, consumerId, out packetsReceived, out packetsLost);
                if (result < 0)
                    throw Vme2WindowsWebRtcNative.NativeError("Read inbound audio RTP counters");
                return result == 1;
            }
        }

        public void GetAudioPcmInfo(string consumerId, out int sampleRate,
            out ulong framesReceived, out int bufferedFrames)
        {
            lock (audioSampleSync)
            {
                EnsureNotDisposed();
                if (string.IsNullOrWhiteSpace(consumerId))
                    throw new ArgumentException("Consumer ID required", nameof(consumerId));
                if (Vme2WindowsWebRtcNative.vme2_webrtc_device_get_audio_pcm_info(
                    handle, consumerId, out sampleRate, out framesReceived, out bufferedFrames) != 1)
                    throw Vme2WindowsWebRtcNative.NativeError("Read Native PCM stream information");
            }
        }

        public int ReadAudioPcm(string consumerId, float[] output)
        {
            lock (audioSampleSync)
            {
                EnsureNotDisposed();
                if (string.IsNullOrWhiteSpace(consumerId) || output == null ||
                    output.Length < 1 || output.Length > 96000)
                    throw new ArgumentException("Valid Consumer ID and mono PCM output buffer required");
                int received = Vme2WindowsWebRtcNative.vme2_webrtc_device_copy_audio_pcm(
                    handle, consumerId, output, output.Length);
                if (received < 0)
                    throw Vme2WindowsWebRtcNative.NativeError("Read Native Consumer PCM");
                return received;
            }
        }

        public int RecvIceState
        {
            get
            {
                EnsureNotDisposed();
                int state = Vme2WindowsWebRtcNative.vme2_webrtc_device_recv_ice_state(handle);
                if (state < 0) throw Vme2WindowsWebRtcNative.NativeError("Read receive ICE state");
                return state;
            }
        }

        public void CloseSendTransport()
        {
            EnsureNotDisposed();
            if (Vme2WindowsWebRtcNative.vme2_webrtc_device_close_send_transport(handle) != 1)
                throw Vme2WindowsWebRtcNative.NativeError("Close SendTransport");
        }

        public void CloseRecvTransport()
        {
            EnsureNotDisposed();
            if (Vme2WindowsWebRtcNative.vme2_webrtc_device_close_recv_transport(handle) != 1)
                throw Vme2WindowsWebRtcNative.NativeError("Close RecvTransport");
        }

        public void Dispose()
        {
            lock (audioSampleSync)
            {
                if (disposed) return;
                Vme2WindowsWebRtcEngine.DestroyDevice(handle);
                handle = IntPtr.Zero;
                disposed = true;
                if (signalingContext.IsAllocated) signalingContext.Free();
                signalingHandler = null;
            }
        }

        private void EnsureNotDisposed()
        {
            if (disposed || handle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(Vme2WindowsWebRtcDevice));
        }
    }
}
#endif
