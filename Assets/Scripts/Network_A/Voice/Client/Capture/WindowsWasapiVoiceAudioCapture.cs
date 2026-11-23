using System;
using System.Runtime.InteropServices;
using System.Threading;
using Network_A.Voice.Client.Audio;
using UnityEngine;

namespace Network_A.Voice.Client.Capture
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal sealed class WindowsWasapiVoiceAudioCapture : IVoiceAudioCapture
    {
        private const uint ClsctxAll = 23;
        private const uint DeviceStateActive = 0x00000001;
        private const uint AudclntStreamflagsEventcallback = 0x00040000;
        private const uint AudclntStreamflagsSrcDefaultQuality = 0x08000000;
        private const uint AudclntStreamflagsAutoconvertpcm = 0x80000000;
        private const uint AudclntBufferflagsDataDiscontinuity = 0x00000001;
        private const uint AudclntBufferflagsSilent = 0x00000002;
        private const uint AudclntBufferflagsTimestampError = 0x00000004;
        private const ushort WaveFormatIeeeFloat = 0x0003;
        private const ushort WaveFormatExtensible = 0xFFFE;
        private const int CoinitApartmentThreaded = 0x2;
        private const int DispatchCapacity = 6;
        private const int StopJoinTimeoutMs = 3000;

        private static readonly Guid AudioClientGuid =
            new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

        private static readonly Guid AudioCaptureClientGuid =
            new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        private static readonly PROPERTYKEY DeviceFriendlyNamePropertyKey =
            new PROPERTYKEY
            {
                fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                pid = 14
            };

        private readonly object lifecycleSync = new object();
        private readonly object dispatchSync = new object();
        private readonly float[][] dispatchFrames = new float[DispatchCapacity][];
        private readonly float[] dispatchFrame =
            new float[VoiceAudioContract.SamplesPerFrame];
        private readonly float[] assemblyFrame =
            new float[VoiceAudioContract.SamplesPerFrame];

        private SynchronizationContext unitySynchronizationContext;
        private Thread captureThread;
        private EventWaitHandle captureEvent;
        private EventWaitHandle stopEvent;
        private IAudioClient audioClient;
        private IAudioCaptureClient captureClient;
        private IMMDeviceEnumerator deviceEnumerator;
        private IMMDevice activeEndpoint;
        private WindowsEndpointNotificationClient notificationClient;
        private float[] packetScratch = Array.Empty<float>();
        private int assemblyCount;
        private int dispatchReadIndex;
        private int dispatchWriteIndex;
        private int dispatchCount;
        private int dispatchScheduled;
        private int dispatchDropCount;
        private int packetCount;
        private int discontinuityCount;
        private int disposed;
        private volatile bool isCapturing;
        private string activeDeviceId = string.Empty;
        private string activeDeviceName = string.Empty;
        private long streamLatency100Ns;
        private uint endpointBufferFrames;
        private int nativeSampleRate;
        private int nativeChannels;
        private int nativeBitsPerSample;
        private int nativeFormatTag;
        private string nativeSubFormat = string.Empty;

        public event Action<ArraySegment<float>> FrameCaptured;
        public event Action<string> Failed;

        public bool IsCapturing
        {
            get { return isCapturing; }
        }

        public string ActiveDeviceName
        {
            get { return activeDeviceName; }
        }

        public void Start()
        {
            lock (lifecycleSync)
            {
                ThrowIfDisposed();
                if (isCapturing || captureThread != null) return;

                unitySynchronizationContext = SynchronizationContext.Current;
                if (unitySynchronizationContext == null)
                {
                    throw new InvalidOperationException(
                        "Unity SynchronizationContext is required before starting WASAPI capture.");
                }

                ResetCaptureState();
                DisposeWaitHandlesNoThrow();
                captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
                captureThread = new Thread(CaptureThreadMain)
                {
                    IsBackground = true,
                    Name = "Metarang Voice WASAPI Capture"
                };
                captureThread.SetApartmentState(ApartmentState.STA);
                captureThread.Start();
            }
        }

        public void Stop()
        {
            Thread threadToJoin;

            lock (lifecycleSync)
            {
                threadToJoin = captureThread;
                isCapturing = false;

                if (threadToJoin == null)
                {
                    DisposeWaitHandlesNoThrow();
                    ClearDispatchQueue();
                    return;
                }

                stopEvent?.Set();
            }

            if (Thread.CurrentThread != threadToJoin &&
                !threadToJoin.Join(StopJoinTimeoutMs))
            {
                PostFailure(
                    "WASAPI capture thread did not stop within " +
                    StopJoinTimeoutMs +
                    " ms.");
                return;
            }

            lock (lifecycleSync)
            {
                if (ReferenceEquals(captureThread, threadToJoin) &&
                    !threadToJoin.IsAlive)
                {
                    captureThread = null;
                }

                if (!threadToJoin.IsAlive)
                {
                    DisposeWaitHandlesNoThrow();
                }
            }

            ClearDispatchQueue();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Stop();

            lock (lifecycleSync)
            {
                if (captureThread == null)
                {
                    DisposeWaitHandlesNoThrow();
                }

                unitySynchronizationContext = null;
            }
        }

        private void CaptureThreadMain()
        {
            bool comInitialized = false;

            try
            {
                int coInitResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
                if (coInitResult < 0)
                {
                    Marshal.ThrowExceptionForHR(coInitResult);
                }

                comInitialized = true;
                InitializeWasapi();
                ThrowIfFailed(audioClient.Start(), "IAudioClient.Start");
                isCapturing = true;

                PostLog(
                    "VOICE_CLIENT_WASAPI_CAPTURE_STARTED=PASS" +
                    " | device=" + Safe(activeDeviceName) +
                    " | requestedSampleRate=" + VoiceAudioContract.SampleRate +
                    " | requestedChannels=" + VoiceAudioContract.Channels +
                    " | requestedSampleType=Float32" +
                    " | frameDurationMs=" + VoiceAudioContract.FrameDurationMs +
                    " | frameSamples=" + VoiceAudioContract.SamplesPerFrame +
                    " | streamLatencyMs=" + HundredNanosecondsToMilliseconds(streamLatency100Ns) +
                    " | endpointBufferFrames=" + endpointBufferFrames +
                    " | endpointBufferMs=" + FramesToMilliseconds(endpointBufferFrames));

                WaitHandle[] waitHandles = { stopEvent, captureEvent };

                while (true)
                {
                    int signaled = WaitHandle.WaitAny(waitHandles);
                    if (signaled == 0) break;
                    if (signaled != 1) continue;
                    DrainCapturePackets();
                }
            }
            catch (Exception exception)
            {
                PostFailure(
                    "Windows WASAPI capture failed: " +
                    exception.GetType().Name +
                    ": " +
                    exception.Message +
                    " | hresult=0x" +
                    ((uint)exception.HResult).ToString("X8") +
                    " | apartment=" +
                    Thread.CurrentThread.GetApartmentState());
            }
            finally
            {
                isCapturing = false;
                StopAudioClientNoThrow();
                ReleaseWasapiObjects();

                if (comInitialized)
                {
                    CoUninitialize();
                }

                lock (lifecycleSync)
                {
                    if (ReferenceEquals(captureThread, Thread.CurrentThread))
                    {
                        captureThread = null;
                    }
                }
            }
        }

        private void InitializeWasapi()
        {
            deviceEnumerator =
                (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            LogActiveCaptureEndpoints(deviceEnumerator);

            int hr = deviceEnumerator.GetDefaultAudioEndpoint(
                EDataFlow.eCapture,
                ERole.eConsole,
                out activeEndpoint);
            ThrowIfFailed(hr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");

            hr = activeEndpoint.GetId(out activeDeviceId);
            ThrowIfFailed(hr, "IMMDevice.GetId");
            activeDeviceName = ReadFriendlyName(activeEndpoint);

            PostLog(
                "VOICE_CLIENT_WASAPI_ENDPOINT_RESOLVED=PASS" +
                " | flow=capture" +
                " | role=console" +
                " | device=" + Safe(activeDeviceName));

            notificationClient =
                new WindowsEndpointNotificationClient(
                    activeDeviceId,
                    PostDeviceEvent);

            hr = deviceEnumerator.RegisterEndpointNotificationCallback(
                notificationClient);
            ThrowIfFailed(
                hr,
                "IMMDeviceEnumerator.RegisterEndpointNotificationCallback");

            Guid audioClientGuid = AudioClientGuid;
            hr = activeEndpoint.Activate(
                ref audioClientGuid,
                ClsctxAll,
                IntPtr.Zero,
                out object audioClientObject);
            ThrowIfFailed(hr, "IMMDevice.Activate(IAudioClient)");

            audioClient = audioClientObject as IAudioClient;
            if (audioClient == null)
            {
                throw new InvalidOperationException(
                    "Windows capture endpoint did not provide IAudioClient.");
            }

            ReadAndLogNativeMixFormat();
            InitializeCommonContractFormat();

            hr = audioClient.GetBufferSize(out endpointBufferFrames);
            ThrowIfFailed(hr, "IAudioClient.GetBufferSize");

            hr = audioClient.GetStreamLatency(out streamLatency100Ns);
            ThrowIfFailed(hr, "IAudioClient.GetStreamLatency");

            hr = audioClient.SetEventHandle(
                captureEvent.SafeWaitHandle.DangerousGetHandle());
            ThrowIfFailed(hr, "IAudioClient.SetEventHandle");

            Guid captureClientGuid = AudioCaptureClientGuid;
            hr = audioClient.GetService(
                ref captureClientGuid,
                out IntPtr captureClientPointer);
            ThrowIfFailed(hr, "IAudioClient.GetService(IAudioCaptureClient)");

            try
            {
                captureClient =
                    Marshal.GetObjectForIUnknown(captureClientPointer)
                    as IAudioCaptureClient;
            }
            finally
            {
                if (captureClientPointer != IntPtr.Zero)
                {
                    Marshal.Release(captureClientPointer);
                }
            }

            if (captureClient == null)
            {
                throw new InvalidOperationException(
                    "Windows audio client did not provide IAudioCaptureClient.");
            }
        }

        private void ReadAndLogNativeMixFormat()
        {
            int hr = audioClient.GetMixFormat(out IntPtr nativeFormatPointer);
            ThrowIfFailed(hr, "IAudioClient.GetMixFormat");

            try
            {
                WAVEFORMATEX nativeFormat =
                    Marshal.PtrToStructure<WAVEFORMATEX>(nativeFormatPointer);

                nativeSampleRate = checked((int)nativeFormat.nSamplesPerSec);
                nativeChannels = nativeFormat.nChannels;
                nativeBitsPerSample = nativeFormat.wBitsPerSample;
                nativeFormatTag = nativeFormat.wFormatTag;
                nativeSubFormat = string.Empty;

                if (nativeFormat.wFormatTag == WaveFormatExtensible &&
                    nativeFormat.cbSize >= 22)
                {
                    WAVEFORMATEXTENSIBLE extensible =
                        Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(
                            nativeFormatPointer);
                    nativeSubFormat = extensible.SubFormat.ToString("D");
                }

                PostLog(
                    "VOICE_CLIENT_WASAPI_NATIVE_FORMAT=PASS" +
                    " | device=" + Safe(activeDeviceName) +
                    " | nativeSampleRate=" + nativeSampleRate +
                    " | nativeChannels=" + nativeChannels +
                    " | nativeBitsPerSample=" + nativeBitsPerSample +
                    " | nativeFormatTag=" + nativeFormatTag +
                    " | nativeSubFormat=" + Safe(nativeSubFormat) +
                    " | requestedSampleRate=" + VoiceAudioContract.SampleRate +
                    " | requestedChannels=" + VoiceAudioContract.Channels +
                    " | requestedSampleType=Float32" +
                    " | sharedMode=True" +
                    " | systemAutoConvert=True");
            }
            finally
            {
                if (nativeFormatPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(nativeFormatPointer);
                }
            }
        }

        private void InitializeCommonContractFormat()
        {
            WAVEFORMATEX requestedFormat = new WAVEFORMATEX
            {
                wFormatTag = WaveFormatIeeeFloat,
                nChannels = VoiceAudioContract.Channels,
                nSamplesPerSec = VoiceAudioContract.SampleRate,
                nAvgBytesPerSec =
                    VoiceAudioContract.SampleRate *
                    VoiceAudioContract.Channels *
                    sizeof(float),
                nBlockAlign =
                    (ushort)(VoiceAudioContract.Channels * sizeof(float)),
                wBitsPerSample = sizeof(float) * 8,
                cbSize = 0
            };

            IntPtr requestedFormatPointer = IntPtr.Zero;
            IntPtr closestFormatPointer = IntPtr.Zero;

            try
            {
                requestedFormatPointer = Marshal.AllocCoTaskMem(
                    Marshal.SizeOf<WAVEFORMATEX>());
                Marshal.StructureToPtr(
                    requestedFormat,
                    requestedFormatPointer,
                    false);

                int supportHr = audioClient.IsFormatSupported(
                    AudioClientShareMode.Shared,
                    requestedFormatPointer,
                    out closestFormatPointer);

                string supportMessage =
                    "VOICE_CLIENT_WASAPI_CONTRACT_FORMAT_CHECK=PASS" +
                    " | exactSharedSupport=" + (supportHr == 0) +
                    " | hasClosestFormat=" + (closestFormatPointer != IntPtr.Zero) +
                    " | hresult=0x" + ((uint)supportHr).ToString("X8") +
                    " | autoConvertRequested=True" +
                    " | gating=False";

                if (supportHr < 0)
                    PostWarning(supportMessage);
                else
                    PostLog(supportMessage);

                uint streamFlags =
                    AudclntStreamflagsEventcallback |
                    AudclntStreamflagsAutoconvertpcm |
                    AudclntStreamflagsSrcDefaultQuality;

                Guid audioSessionGuid = Guid.Empty;
                int hr = audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    streamFlags,
                    0,
                    0,
                    requestedFormatPointer,
                    ref audioSessionGuid);

                ThrowIfFailed(hr, "IAudioClient.Initialize");
            }
            finally
            {
                if (closestFormatPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(closestFormatPointer);
                }

                if (requestedFormatPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(requestedFormatPointer);
                }
            }
        }

        private void DrainCapturePackets()
        {
            while (true)
            {
                int hr = captureClient.GetNextPacketSize(out uint packetFrames);
                ThrowIfFailed(hr, "IAudioCaptureClient.GetNextPacketSize");
                if (packetFrames == 0) return;

                hr = captureClient.GetBuffer(
                    out IntPtr data,
                    out uint frames,
                    out uint flags,
                    out ulong devicePosition,
                    out ulong qpcPosition100Ns);
                ThrowIfFailed(hr, "IAudioCaptureClient.GetBuffer");

                try
                {
                    int frameCount = checked((int)frames);
                    EnsurePacketScratch(frameCount);

                    if ((flags & AudclntBufferflagsSilent) != 0)
                    {
                        Array.Clear(packetScratch, 0, frameCount);
                    }
                    else
                    {
                        if (data == IntPtr.Zero)
                        {
                            throw new InvalidOperationException(
                                "WASAPI returned a null PCM pointer without the SILENT flag.");
                        }

                        Marshal.Copy(data, packetScratch, 0, frameCount);
                    }

                    if ((flags & AudclntBufferflagsDataDiscontinuity) != 0)
                    {
                        discontinuityCount += 1;
                        PostWarning(
                            "VOICE_CLIENT_WASAPI_DISCONTINUITY=PASS" +
                            " | count=" + discontinuityCount +
                            " | devicePositionFrames=" + devicePosition +
                            " | qpcPosition100ns=" + qpcPosition100Ns);
                    }

                    if ((flags & AudclntBufferflagsTimestampError) != 0)
                    {
                        PostWarning(
                            "VOICE_CLIENT_WASAPI_TIMESTAMP_ERROR=PASS" +
                            " | devicePositionFrames=" + devicePosition);
                    }

                    packetCount += 1;
                    if (packetCount == 1 || packetCount % 250 == 0)
                    {
                        PostLog(
                            "VOICE_CLIENT_WASAPI_CAPTURE_TIMESTAMP=PASS" +
                            " | packet=" + packetCount +
                            " | packetFrames=" + frames +
                            " | devicePositionFrames=" + devicePosition +
                            " | qpcPosition100ns=" + qpcPosition100Ns +
                            " | streamLatencyMs=" + HundredNanosecondsToMilliseconds(streamLatency100Ns));
                    }

                    AppendSamples(packetScratch, frameCount);
                }
                finally
                {
                    int releaseHr = captureClient.ReleaseBuffer(frames);
                    ThrowIfFailed(releaseHr, "IAudioCaptureClient.ReleaseBuffer");
                }
            }
        }

        private void AppendSamples(float[] samples, int sampleCount)
        {
            int sourceOffset = 0;

            while (sourceOffset < sampleCount)
            {
                int copyCount = Math.Min(
                    VoiceAudioContract.SamplesPerFrame - assemblyCount,
                    sampleCount - sourceOffset);

                Array.Copy(
                    samples,
                    sourceOffset,
                    assemblyFrame,
                    assemblyCount,
                    copyCount);

                assemblyCount += copyCount;
                sourceOffset += copyCount;

                if (assemblyCount != VoiceAudioContract.SamplesPerFrame)
                    continue;

                QueueFrameForUnityThread(assemblyFrame);
                assemblyCount = 0;
            }
        }

        private void QueueFrameForUnityThread(float[] source)
        {
            bool scheduleDispatch = false;

            lock (dispatchSync)
            {
                if (dispatchFrames[dispatchWriteIndex] == null)
                {
                    dispatchFrames[dispatchWriteIndex] =
                        new float[VoiceAudioContract.SamplesPerFrame];
                }

                if (dispatchCount >= DispatchCapacity)
                {
                    dispatchReadIndex =
                        (dispatchReadIndex + 1) % DispatchCapacity;
                    dispatchCount -= 1;
                    dispatchDropCount += 1;

                    if (dispatchDropCount == 1 ||
                        dispatchDropCount % 25 == 0)
                    {
                        PostWarning(
                            "VOICE_CLIENT_WASAPI_DISPATCH_DROP=PASS" +
                            " | reason=unity_main_thread_backlog" +
                            " | droppedOldest=True" +
                            " | totalDropped=" + dispatchDropCount +
                            " | capacityFrames=" + DispatchCapacity +
                            " | capacityMs=" +
                            (DispatchCapacity * VoiceAudioContract.FrameDurationMs));
                    }
                }

                Array.Copy(
                    source,
                    0,
                    dispatchFrames[dispatchWriteIndex],
                    0,
                    VoiceAudioContract.SamplesPerFrame);

                dispatchWriteIndex =
                    (dispatchWriteIndex + 1) % DispatchCapacity;
                dispatchCount += 1;

                if (dispatchScheduled == 0)
                {
                    dispatchScheduled = 1;
                    scheduleDispatch = true;
                }
            }

            if (scheduleDispatch)
            {
                unitySynchronizationContext.Post(
                    DispatchCapturedFrames,
                    null);
            }
        }

        private void DispatchCapturedFrames(object state)
        {
            while (true)
            {
                lock (dispatchSync)
                {
                    if (dispatchCount == 0)
                    {
                        dispatchScheduled = 0;
                        return;
                    }

                    Array.Copy(
                        dispatchFrames[dispatchReadIndex],
                        0,
                        dispatchFrame,
                        0,
                        VoiceAudioContract.SamplesPerFrame);

                    dispatchReadIndex =
                        (dispatchReadIndex + 1) % DispatchCapacity;
                    dispatchCount -= 1;
                }

                if (!isCapturing || Volatile.Read(ref disposed) != 0)
                    continue;

                FrameCaptured?.Invoke(
                    new ArraySegment<float>(
                        dispatchFrame,
                        0,
                        VoiceAudioContract.SamplesPerFrame));
            }
        }

        private void LogActiveCaptureEndpoints(IMMDeviceEnumerator enumerator)
        {
            int hr = enumerator.EnumAudioEndpoints(
                EDataFlow.eCapture,
                DeviceStateActive,
                out IMMDeviceCollection collection);
            ThrowIfFailed(hr, "IMMDeviceEnumerator.EnumAudioEndpoints");

            try
            {
                hr = collection.GetCount(out uint count);
                ThrowIfFailed(hr, "IMMDeviceCollection.GetCount");

                string names = string.Empty;

                for (uint index = 0; index < count; index++)
                {
                    hr = collection.Item(index, out IMMDevice endpoint);
                    ThrowIfFailed(hr, "IMMDeviceCollection.Item");

                    try
                    {
                        string name = ReadFriendlyName(endpoint);
                        if (!string.IsNullOrEmpty(names)) names += ",";
                        names += Safe(name);
                    }
                    finally
                    {
                        ReleaseComObject(endpoint);
                    }
                }

                PostLog(
                    "VOICE_CLIENT_WASAPI_DEVICE_ENUMERATION=PASS" +
                    " | activeCaptureEndpoints=" + count +
                    " | devices=" + names);
            }
            finally
            {
                ReleaseComObject(collection);
            }
        }

        private void PostDeviceEvent(string message)
        {
            PostLog(message);
        }

        private void PostLog(string message)
        {
            SynchronizationContext context = unitySynchronizationContext;
            if (context == null) return;
            context.Post(LogOnUnityThread, message);
        }

        private static void LogOnUnityThread(object state)
        {
            Debug.Log(state as string ?? string.Empty);
        }

        private void PostWarning(string message)
        {
            SynchronizationContext context = unitySynchronizationContext;
            if (context == null) return;
            context.Post(WarningOnUnityThread, message);
        }

        private static void WarningOnUnityThread(object state)
        {
            Debug.LogWarning(state as string ?? string.Empty);
        }

        private void PostFailure(string message)
        {
            SynchronizationContext context = unitySynchronizationContext;
            if (context == null)
            {
                Failed?.Invoke(message);
                return;
            }

            context.Post(FailureOnUnityThread, message);
        }

        private void FailureOnUnityThread(object state)
        {
            Failed?.Invoke(state as string ?? "Windows WASAPI capture failed.");
        }

        private void DisposeWaitHandlesNoThrow()
        {
            try
            {
                captureEvent?.Dispose();
            }
            catch
            {
            }

            captureEvent = null;

            try
            {
                stopEvent?.Dispose();
            }
            catch
            {
            }

            stopEvent = null;
        }

        private void StopAudioClientNoThrow()
        {
            try
            {
                audioClient?.Stop();
            }
            catch
            {
            }
        }

        private void ReleaseWasapiObjects()
        {
            if (deviceEnumerator != null && notificationClient != null)
            {
                try
                {
                    deviceEnumerator.UnregisterEndpointNotificationCallback(
                        notificationClient);
                }
                catch
                {
                }
            }

            notificationClient = null;
            ReleaseComObject(captureClient);
            captureClient = null;
            ReleaseComObject(audioClient);
            audioClient = null;
            ReleaseComObject(activeEndpoint);
            activeEndpoint = null;
            ReleaseComObject(deviceEnumerator);
            deviceEnumerator = null;
        }

        private void ResetCaptureState()
        {
            assemblyCount = 0;
            packetCount = 0;
            discontinuityCount = 0;
            dispatchDropCount = 0;
            streamLatency100Ns = 0;
            endpointBufferFrames = 0;
            activeDeviceId = string.Empty;
            activeDeviceName = string.Empty;
            nativeSampleRate = 0;
            nativeChannels = 0;
            nativeBitsPerSample = 0;
            nativeFormatTag = 0;
            nativeSubFormat = string.Empty;
            ClearDispatchQueue();
        }

        private void ClearDispatchQueue()
        {
            lock (dispatchSync)
            {
                dispatchReadIndex = 0;
                dispatchWriteIndex = 0;
                dispatchCount = 0;
                dispatchScheduled = 0;
            }
        }

        private void EnsurePacketScratch(int sampleCount)
        {
            if (packetScratch.Length >= sampleCount) return;
            packetScratch = new float[sampleCount];
        }

        private static string ReadFriendlyName(IMMDevice endpoint)
        {
            int hr = endpoint.OpenPropertyStore(
                0,
                out IPropertyStore propertyStore);
            ThrowIfFailed(hr, "IMMDevice.OpenPropertyStore");

            try
            {
                PROPERTYKEY key = DeviceFriendlyNamePropertyKey;
                hr = propertyStore.GetValue(ref key, out PROPVARIANT value);
                ThrowIfFailed(hr, "IPropertyStore.GetValue(PKEY_Device_FriendlyName)");

                try
                {
                    return value.GetString();
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            finally
            {
                ReleaseComObject(propertyStore);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(WindowsWasapiVoiceAudioCapture));
        }

        private static void ThrowIfFailed(int hr, string operation)
        {
            if (hr >= 0) return;
            throw new COMException(operation + " failed.", hr);
        }

        private static double HundredNanosecondsToMilliseconds(long value)
        {
            return value / 10000.0;
        }

        private static double FramesToMilliseconds(uint frames)
        {
            return frames * 1000.0 / VoiceAudioContract.SampleRate;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "<empty>"
                : value.Replace("|", "/");
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null) return;

            try
            {
                if (Marshal.IsComObject(value))
                    Marshal.FinalReleaseComObject(value);
            }
            catch
            {
            }
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(
            IntPtr reserved,
            int coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(
            ref PROPVARIANT pvar);

        private enum EDataFlow
        {
            eRender = 0,
            eCapture = 1,
            eAll = 2
        }

        private enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        private enum AudioClientShareMode
        {
            Shared = 0,
            Exclusive = 1
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(
                EDataFlow dataFlow,
                uint stateMask,
                out IMMDeviceCollection devices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(
                EDataFlow dataFlow,
                ERole role,
                out IMMDevice endpoint);

            [PreserveSig]
            int GetDevice(
                [MarshalAs(UnmanagedType.LPWStr)] string id,
                out IMMDevice device);

            [PreserveSig]
            int RegisterEndpointNotificationCallback(
                [MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(
                [MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out uint count);

            [PreserveSig]
            int Item(uint deviceIndex, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(
                ref Guid iid,
                uint clsCtx,
                IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

            [PreserveSig]
            int OpenPropertyStore(
                int accessMode,
                out IPropertyStore properties);

            [PreserveSig]
            int GetId(
                [MarshalAs(UnmanagedType.LPWStr)] out string id);

            [PreserveSig]
            int GetState(out uint state);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint propertyCount);

            [PreserveSig]
            int GetAt(uint propertyIndex, out PROPERTYKEY key);

            [PreserveSig]
            int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

            [PreserveSig]
            int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

            [PreserveSig]
            int Commit();
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(
                AudioClientShareMode shareMode,
                uint streamFlags,
                long bufferDuration,
                long periodicity,
                IntPtr format,
                ref Guid audioSessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint numBufferFrames);

            [PreserveSig]
            int GetStreamLatency(out long latency100Ns);

            [PreserveSig]
            int GetCurrentPadding(out uint numPaddingFrames);

            [PreserveSig]
            int IsFormatSupported(
                AudioClientShareMode shareMode,
                IntPtr format,
                out IntPtr closestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr deviceFormat);

            [PreserveSig]
            int GetDevicePeriod(
                out long defaultPeriod100Ns,
                out long minimumPeriod100Ns);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            int GetService(ref Guid iid, out IntPtr interfacePointer);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(
                out IntPtr data,
                out uint numFramesToRead,
                out uint flags,
                out ulong devicePosition,
                out ulong qpcPosition100Ns);

            [PreserveSig]
            int ReleaseBuffer(uint numFramesRead);

            [PreserveSig]
            int GetNextPacketSize(out uint numFramesInNextPacket);
        }

        [ComImport]
        [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMNotificationClient
        {
            void OnDeviceStateChanged(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                uint newState);

            void OnDeviceAdded(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

            void OnDeviceRemoved(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

            void OnDefaultDeviceChanged(
                EDataFlow flow,
                ERole role,
                [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

            void OnPropertyValueChanged(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                PROPERTYKEY key);
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class WindowsEndpointNotificationClient : IMMNotificationClient
        {
            private readonly string activeDeviceId;
            private readonly Action<string> eventSink;

            public WindowsEndpointNotificationClient(
                string activeDeviceId,
                Action<string> eventSink)
            {
                this.activeDeviceId = activeDeviceId ?? string.Empty;
                this.eventSink = eventSink;
            }

            public void OnDeviceStateChanged(string deviceId, uint newState)
            {
                Emit(
                    "VOICE_CLIENT_WASAPI_DEVICE_EVENT=PASS" +
                    " | event=state_changed" +
                    " | activeDevice=" + IsActive(deviceId) +
                    " | newState=" + newState);
            }

            public void OnDeviceAdded(string deviceId)
            {
                Emit(
                    "VOICE_CLIENT_WASAPI_DEVICE_EVENT=PASS" +
                    " | event=device_added" +
                    " | activeDevice=" + IsActive(deviceId));
            }

            public void OnDeviceRemoved(string deviceId)
            {
                Emit(
                    "VOICE_CLIENT_WASAPI_DEVICE_EVENT=PASS" +
                    " | event=device_removed" +
                    " | activeDevice=" + IsActive(deviceId));
            }

            public void OnDefaultDeviceChanged(
                EDataFlow flow,
                ERole role,
                string defaultDeviceId)
            {
                Emit(
                    "VOICE_CLIENT_WASAPI_DEVICE_EVENT=PASS" +
                    " | event=default_changed" +
                    " | flow=" + flow +
                    " | role=" + role +
                    " | activeDevice=" + IsActive(defaultDeviceId));
            }

            public void OnPropertyValueChanged(
                string deviceId,
                PROPERTYKEY key)
            {
                Emit(
                    "VOICE_CLIENT_WASAPI_DEVICE_EVENT=PASS" +
                    " | event=property_changed" +
                    " | activeDevice=" + IsActive(deviceId));
            }

            private void Emit(string message)
            {
                try
                {
                    eventSink?.Invoke(message);
                }
                catch
                {
                }
            }

            private bool IsActive(string deviceId)
            {
                return string.Equals(
                    activeDeviceId,
                    deviceId,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)]
            public ushort vt;

            [FieldOffset(8)]
            public IntPtr pointerValue;

            public string GetString()
            {
                const ushort VtLpwstr = 31;
                if (vt != VtLpwstr || pointerValue == IntPtr.Zero)
                    return string.Empty;

                return Marshal.PtrToStringUni(pointerValue) ?? string.Empty;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort Samples;
            public uint ChannelMask;
            public Guid SubFormat;
        }
    }
#endif
}
