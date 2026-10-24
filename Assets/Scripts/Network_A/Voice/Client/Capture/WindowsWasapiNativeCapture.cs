using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Network_A.Voice.Client.Capture.Vme2
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal readonly struct WindowsWasapiNativeFormat
    {
        // این سازنده مشخصات فرمت خامی را که ویندوز برای میکروفون گزارش می کند در یک ساختار ثابت ذخیره می کند.
        public WindowsWasapiNativeFormat(
            int sampleRate,
            int channels,
            int bitsPerSample,
            int blockAlign,
            ushort formatTag,
            Guid subFormat,
            string deviceId,
            string deviceName)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            BlockAlign = blockAlign;
            FormatTag = formatTag;
            SubFormat = subFormat;
            DeviceId = deviceId ?? string.Empty;
            DeviceName = deviceName ?? string.Empty;
        }

        public int SampleRate { get; }
        public int Channels { get; }
        public int BitsPerSample { get; }
        public int BlockAlign { get; }
        public ushort FormatTag { get; }
        public Guid SubFormat { get; }
        public string DeviceId { get; }
        public string DeviceName { get; }

        // این ویژگی مشخص می کند فرمت خام دریافت شده از نوع اعشاری ۳۲ بیتی است یا نه.
        public bool IsFloat32
        {
            get
            {
                return BitsPerSample == 32 &&
                       (FormatTag == WaveFormatIeeeFloat ||
                        SubFormat == IeeeFloatSubFormat);
            }
        }

        // این ویژگی مشخص می کند فرمت خام دریافت شده از نوع پی سی ام ۱۶ بیتی است یا نه.
        public bool IsPcm16
        {
            get
            {
                return BitsPerSample == 16 &&
                       (FormatTag == WaveFormatPcm ||
                        SubFormat == PcmSubFormat);
            }
        }

        private const ushort WaveFormatPcm = 0x0001;
        private const ushort WaveFormatIeeeFloat = 0x0003;

        private static readonly Guid PcmSubFormat =
            new Guid("00000001-0000-0010-8000-00AA00389B71");

        private static readonly Guid IeeeFloatSubFormat =
            new Guid("00000003-0000-0010-8000-00AA00389B71");
    }

    internal readonly struct WindowsWasapiNativePacket
    {
        // این سازنده یک بسته خام صوتی را همراه با تعداد فریم، پرچم ها و زمان بندی سخت افزاری آن ذخیره می کند.
        public WindowsWasapiNativePacket(
            ArraySegment<byte> data,
            uint frames,
            uint flags,
            ulong devicePositionFrames,
            ulong qpcPosition100Ns)
        {
            Data = data;
            Frames = frames;
            Flags = flags;
            DevicePositionFrames = devicePositionFrames;
            QpcPosition100Ns = qpcPosition100Ns;
        }

        public ArraySegment<byte> Data { get; }
        public uint Frames { get; }
        public uint Flags { get; }
        public ulong DevicePositionFrames { get; }
        public ulong QpcPosition100Ns { get; }
    }

    internal sealed class WindowsWasapiNativeCapture : IDisposable
    {
        private const uint ClsctxAll = 23;
        private const uint AudclntStreamflagsEventcallback = 0x00040000;
        private const uint AudclntBufferflagsSilent = 0x00000002;
        private const int CoinitApartmentThreaded = 0x2;
        private const int StopJoinTimeoutMs = 3000;
        private const int StgmRead = 0;
        private const ushort WaveFormatExtensible = 0xFFFE;

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

        private Thread captureThread;
        private EventWaitHandle captureEvent;
        private EventWaitHandle stopEvent;
        private IMMDeviceEnumerator deviceEnumerator;
        private IMMDevice endpoint;
        private IAudioClient audioClient;
        private IAudioCaptureClient captureClient;
        private IntPtr mixFormatPointer;
        private byte[] packetBuffer = Array.Empty<byte>();
        private WindowsWasapiNativeFormat nativeFormat;
        private volatile bool isCapturing;
        private int disposed;

        public event Action<WindowsWasapiNativeFormat> FormatReady;
        public event Action<WindowsWasapiNativePacket> PacketCaptured;
        public event Action<string> Failed;

        public bool IsCapturing => isCapturing;

        // این تابع نخ مستقل کپچر ویندوز را می سازد و بدون دخالت در مسیر اصلی صدا، دریافت خام میکروفون را آغاز می کند.
        public void Start()
        {
            lock (lifecycleSync)
            {
                if (disposed != 0)
                {
                    throw new ObjectDisposedException(nameof(WindowsWasapiNativeCapture));
                }

                if (captureThread != null)
                {
                    return;
                }

                captureEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

                captureThread = new Thread(CaptureThreadMain)
                {
                    IsBackground = true,
                    Name = "VME2 WASAPI Native Capture"
                };

                captureThread.SetApartmentState(ApartmentState.STA);
                captureThread.Start();
            }
        }

        // این تابع درخواست توقف کپچر مستقل را ارسال می کند و تا پایان امن نخ کپچر منتظر می ماند.
        public void Stop()
        {
            Thread threadToJoin;

            lock (lifecycleSync)
            {
                threadToJoin = captureThread;
                if (threadToJoin == null)
                {
                    DisposeWaitHandles();
                    return;
                }

                stopEvent?.Set();
            }

            if (Thread.CurrentThread != threadToJoin)
            {
                threadToJoin.Join(StopJoinTimeoutMs);
            }
        }

        // این تابع برای آزادسازی نهایی نمونه استفاده می شود و تضمین می کند عملیات توقف فقط یک بار انجام شود.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Stop();
        }

        // این تابع بدنه اصلی نخ کپچر است و مراحل راه اندازی واساپی را به ترتیب اجرا کرده و سپس وارد حلقه دریافت می شود.
        private void CaptureThreadMain()
        {
            bool comInitialized = false;

            try
            {
                if (!InitializeCom(out comInitialized)) return;
                if (!OpenDefaultCaptureEndpoint()) return;
                if (!ActivateAudioClient()) return;
                if (!ReadNativeMixFormat()) return;
                if (!InitializeSharedEventStream()) return;
                if (!CreateCaptureClient()) return;
                if (!StartAudioStream()) return;

                isCapturing = true;
                CaptureLoop();
            }
            catch (Exception exception)
            {
                Fail("CaptureThread", exception);
            }
            finally
            {
                isCapturing = false;
                StopAudioStream();
                ReleaseWasapiObjects();

                if (comInitialized)
                {
                    CoUninitialize();
                }

                lock (lifecycleSync)
                {
                    captureThread = null;
                    DisposeWaitHandles();
                }
            }
        }

        // این تابع محیط کام ویندوز را برای نخ کپچر آماده می کند تا رابط های صوتی ویندوز روی همین نخ قابل استفاده باشند.
        private bool InitializeCom(out bool comInitialized)
        {
            comInitialized = false;
            int hr = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);

            if (hr < 0)
            {
                return Fail("CoInitializeEx", hr);
            }

            comInitialized = true;
            return true;
        }

        // این تابع دستگاه پیش فرض ضبط صدا در ویندوز را پیدا می کند و هندل مربوط به همان ورودی را نگه می دارد.
        private bool OpenDefaultCaptureEndpoint()
        {
            try
            {
                deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            }
            catch (Exception exception)
            {
                return Fail("MMDeviceEnumerator", exception);
            }

            int hr = deviceEnumerator.GetDefaultAudioEndpoint(
                EDataFlow.eCapture,
                ERole.eConsole,
                out endpoint);

            if (hr < 0)
            {
                return Fail("GetDefaultAudioEndpoint", hr);
            }

            return true;
        }

        // این تابع رابط اصلی کلاینت صوتی ویندوز را از دستگاه انتخاب شده فعال می کند.
        private bool ActivateAudioClient()
        {
            Guid iid = AudioClientGuid;
            int hr = endpoint.Activate(
                ref iid,
                ClsctxAll,
                IntPtr.Zero,
                out object audioClientObject);

            if (hr < 0)
            {
                return Fail("IMMDevice.Activate(IAudioClient)", hr);
            }

            audioClient = audioClientObject as IAudioClient;
            if (audioClient == null)
            {
                return Fail(
                    "IMMDevice.Activate(IAudioClient)",
                    "IAudioClient was not returned.");
            }

            return true;
        }

        // این تابع فرمت واقعی میکس ویندوز شامل نرخ نمونه، تعداد کانال و نوع نمونه را می خواند و برای تست گزارش می کند.
        private bool ReadNativeMixFormat()
        {
            int hr = audioClient.GetMixFormat(out mixFormatPointer);
            if (hr < 0)
            {
                return Fail("IAudioClient.GetMixFormat", hr);
            }

            if (mixFormatPointer == IntPtr.Zero)
            {
                return Fail(
                    "IAudioClient.GetMixFormat",
                    "Mix format pointer was null.");
            }

            WAVEFORMATEX waveFormat = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormatPointer);

            Guid subFormat = Guid.Empty;
            if (waveFormat.wFormatTag == WaveFormatExtensible &&
                waveFormat.cbSize >= 22)
            {
                WAVEFORMATEXTENSIBLE extensible = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(mixFormatPointer);
                subFormat = extensible.SubFormat;
            }

            string deviceId = ReadDeviceId(endpoint);
            string deviceName = ReadFriendlyName(endpoint);

            nativeFormat = new WindowsWasapiNativeFormat(
                checked((int)waveFormat.nSamplesPerSec),
                waveFormat.nChannels,
                waveFormat.wBitsPerSample,
                waveFormat.nBlockAlign,
                waveFormat.wFormatTag,
                subFormat,
                deviceId,
                deviceName);

            FormatReady?.Invoke(nativeFormat);
            return true;
        }

        // این تابع جریان اشتراکی و رویدادمحور واساپی را با همان فرمت واقعی ویندوز آماده می کند و اندازه بافر را مشخص می کند.
        private bool InitializeSharedEventStream()
        {
            Guid sessionGuid = Guid.Empty;
            int hr = audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudclntStreamflagsEventcallback,
                0,
                0,
                mixFormatPointer,
                ref sessionGuid);

            if (hr < 0)
            {
                return Fail("IAudioClient.Initialize", hr);
            }

            hr = audioClient.GetBufferSize(out uint endpointBufferFrames);
            if (hr < 0)
            {
                return Fail("IAudioClient.GetBufferSize", hr);
            }

            int bufferBytes = checked((int)endpointBufferFrames * nativeFormat.BlockAlign);

            packetBuffer = new byte[Math.Max(bufferBytes, nativeFormat.BlockAlign)];

            hr = audioClient.SetEventHandle(captureEvent.SafeWaitHandle.DangerousGetHandle());

            if (hr < 0)
            {
                return Fail("IAudioClient.SetEventHandle", hr);
            }

            return true;
        }

        // این تابع رابط مخصوص دریافت بسته های خام میکروفون را از کلاینت صوتی ویندوز دریافت می کند.
        private bool CreateCaptureClient()
        {
            Guid iid = AudioCaptureClientGuid;
            int hr = audioClient.GetService(ref iid, out IntPtr servicePointer);

            if (hr < 0)
            {
                return Fail("IAudioClient.GetService(IAudioCaptureClient)", hr);
            }

            try
            {
                captureClient = Marshal.GetObjectForIUnknown(servicePointer) as IAudioCaptureClient;
            }
            finally
            {
                if (servicePointer != IntPtr.Zero)
                {
                    Marshal.Release(servicePointer);
                }
            }

            if (captureClient == null)
            {
                return Fail(
                    "IAudioClient.GetService(IAudioCaptureClient)",
                    "IAudioCaptureClient was not returned.");
            }

            return true;
        }

        // این تابع پس از کامل شدن همه مراحل آماده سازی، جریان واقعی ضبط صدا را در واساپی شروع می کند.
        private bool StartAudioStream()
        {
            int hr = audioClient.Start();
            if (hr < 0)
            {
                return Fail("IAudioClient.Start", hr);
            }

            return true;
        }

        // این تابع تا زمان توقف، منتظر رویداد صوتی می ماند و هر بار که داده آماده شد بسته های موجود را تخلیه می کند.
        private void CaptureLoop()
        {
            WaitHandle[] waitHandles = { stopEvent, captureEvent };

            while (true)
            {
                int signaled = WaitHandle.WaitAny(waitHandles);
                if (signaled == 0) return;
                if (signaled != 1) continue;

                DrainAvailablePackets();
            }
        }

        // این تابع همه بسته های صوتی آماده در بافر واساپی را یکی پس از دیگری می خواند تا داده معطل نماند.
        private void DrainAvailablePackets()
        {
            while (true)
            {
                int hr = captureClient.GetNextPacketSize(out uint packetFrames);
                if (hr < 0)
                {
                    Fail("IAudioCaptureClient.GetNextPacketSize", hr);
                    return;
                }

                if (packetFrames == 0)
                {
                    return;
                }

                if (!ReadOnePacket())
                {
                    return;
                }
            }
        }

        // این تابع یک بسته خام را از واساپی می خواند، داده را کپی می کند و زمان سخت افزاری همان بسته را برای آزمایشگر ارسال می کند.
        private bool ReadOnePacket()
        {
            int hr = captureClient.GetBuffer(
                out IntPtr dataPointer,
                out uint frames,
                out uint flags,
                out ulong devicePositionFrames,
                out ulong qpcPosition100Ns);

            if (hr < 0)
            {
                return Fail("IAudioCaptureClient.GetBuffer", hr);
            }

            bool packetOk = true;

            try
            {
                int byteCount = checked((int)frames * nativeFormat.BlockAlign);
                if (byteCount > packetBuffer.Length)
                {
                    packetOk = Fail(
                        "IAudioCaptureClient.GetBuffer",
                        "Packet exceeded endpoint buffer capacity.");
                }
                else if ((flags & AudclntBufferflagsSilent) != 0)
                {
                    Array.Clear(packetBuffer, 0, byteCount);
                }
                else if (dataPointer == IntPtr.Zero)
                {
                    packetOk = Fail(
                        "IAudioCaptureClient.GetBuffer",
                        "Data pointer was null without the SILENT flag.");
                }
                else
                {
                    Marshal.Copy(dataPointer, packetBuffer, 0, byteCount);
                }

                if (packetOk)
                {
                    PacketCaptured?.Invoke(
                        new WindowsWasapiNativePacket(
                            new ArraySegment<byte>(packetBuffer, 0, byteCount),
                            frames,
                            flags,
                            devicePositionFrames,
                            qpcPosition100Ns));
                }
            }
            finally
            {
                int releaseHr = captureClient.ReleaseBuffer(frames);
                if (releaseHr < 0)
                {
                    packetOk = Fail(
                        "IAudioCaptureClient.ReleaseBuffer",
                        releaseHr);
                }
            }

            return packetOk;
        }

        // این تابع در زمان پایان یا خطا، جریان صوتی واساپی را بدون ایجاد خطای ثانویه متوقف می کند.
        private void StopAudioStream()
        {
            try
            {
                audioClient?.Stop();
            }
            catch
            {
            }
        }

        // این تابع تمام حافظه ها و اشیای کام مربوط به واساپی را آزاد می کند تا منبعی در ویندوز باقی نماند.
        private void ReleaseWasapiObjects()
        {
            if (mixFormatPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormatPointer);
                mixFormatPointer = IntPtr.Zero;
            }

            ReleaseComObject(captureClient);
            captureClient = null;

            ReleaseComObject(audioClient);
            audioClient = null;

            ReleaseComObject(endpoint);
            endpoint = null;

            ReleaseComObject(deviceEnumerator);
            deviceEnumerator = null;

            packetBuffer = Array.Empty<byte>();
        }

        // این تابع رویدادهای انتظار مربوط به دریافت صدا و توقف نخ را آزاد می کند.
        private void DisposeWaitHandles()
        {
            captureEvent?.Dispose();
            captureEvent = null;

            stopEvent?.Dispose();
            stopEvent = null;
        }

        // این تابع خطاهای دارای کد اچ آرزالت را با نام مرحله دقیق ثبت و برای آزمایشگر ارسال می کند.
        private bool Fail(string stage, int hr)
        {
            string message =
                "VME2_PHASE2_WASAPI_FAILED" +
                " | stage=" + stage +
                " | hresult=0x" + ((uint)hr).ToString("X8") +
                " | apartment=" + Thread.CurrentThread.GetApartmentState();

            Failed?.Invoke(message);
            return false;
        }

        // این تابع استثناهای مدیریت شده را همراه با نوع خطا، کد خطا و مرحله وقوع برای آزمایشگر ارسال می کند.
        private bool Fail(string stage, Exception exception)
        {
            string message =
                "VME2_PHASE2_WASAPI_FAILED" +
                " | stage=" + stage +
                " | exception=" + exception.GetType().Name +
                " | hresult=0x" + ((uint)exception.HResult).ToString("X8") +
                " | message=" + Safe(exception.Message);

            Failed?.Invoke(message);
            return false;
        }

        // این تابع خطاهای توضیحی بدون کد سیستم را همراه با مرحله وقوع برای آزمایشگر ارسال می کند.
        private bool Fail(string stage, string details)
        {
            Failed?.Invoke(
                "VME2_PHASE2_WASAPI_FAILED" +
                " | stage=" + stage +
                " | details=" + Safe(details));
            return false;
        }

        // این تابع شناسه داخلی دستگاه ضبط انتخاب شده در ویندوز را برای گزارش تست می خواند.
        private static string ReadDeviceId(IMMDevice device)
        {
            if (device == null) return string.Empty;

            int hr = device.GetId(out string id);
            return hr >= 0 ? id ?? string.Empty : string.Empty;
        }

        // این تابع نام قابل خواندن دستگاه ضبط را از مشخصات ویندوز می گیرد تا در لاگ تست مشخص باشد کدام میکروفون باز شده است.
        private static string ReadFriendlyName(IMMDevice device)
        {
            if (device == null) return string.Empty;

            int hr = device.OpenPropertyStore(StgmRead, out IPropertyStore store);
            if (hr < 0 || store == null) return string.Empty;

            PROPVARIANT value = default;

            try
            {
                PROPERTYKEY key = DeviceFriendlyNamePropertyKey;
                hr = store.GetValue(ref key, out value);
                if (hr < 0) return string.Empty;
                return value.GetString();
            }
            finally
            {
                PropVariantClear(ref value);
                ReleaseComObject(store);
            }
        }

        // این تابع متن لاگ را ایمن می کند تا مقدار خالی یا نویسه جداکننده باعث خراب شدن ساختار گزارش نشود.
        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value)
                ? "<empty>"
                : value.Replace("|", "/");
        }

        // این تابع شیء کام دریافتی از ویندوز را به شکل کنترل شده و ایمن آزاد می کند.
        private static void ReleaseComObject(object value)
        {
            if (value == null) return;

            try
            {
                if (Marshal.IsComObject(value))
                {
                    Marshal.FinalReleaseComObject(value);
                }
            }
            catch
            {
            }
        }

        // این تابع سیستمی محیط کام ویندوز را روی نخ جاری راه اندازی می کند.
        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, int coInit);

        // این تابع سیستمی محیط کام ویندوز را روی نخ جاری می بندد.
        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        // این تابع سیستمی حافظه مقدار ویژگی خوانده شده از ویندوز را آزاد می کند.
        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

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
            // این تابع فهرست ورودی ها یا خروجی های صوتی ویندوز را بر اساس نوع درخواستی دریافت می کند.
            int EnumAudioEndpoints(
                EDataFlow dataFlow,
                uint stateMask,
                out IMMDeviceCollection devices);

            [PreserveSig]
            // این تابع دستگاه صوتی پیش فرض ویندوز را برای نقش درخواستی برمی گرداند.
            int GetDefaultAudioEndpoint(
                EDataFlow dataFlow,
                ERole role,
                out IMMDevice endpoint);

            [PreserveSig]
            // این تابع یک دستگاه صوتی را با شناسه داخلی آن از ویندوز دریافت می کند.
            int GetDevice(
                [MarshalAs(UnmanagedType.LPWStr)] string id,
                out IMMDevice device);

            [PreserveSig]
            // این تابع شنونده تغییرات دستگاه های صوتی ویندوز را ثبت می کند.
            int RegisterEndpointNotificationCallback(IntPtr client);

            [PreserveSig]
            // این تابع شنونده تغییرات دستگاه های صوتی ویندوز را حذف می کند.
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            // این تابع تعداد دستگاه های موجود در مجموعه را برمی گرداند.
            int GetCount(out uint count);

            [PreserveSig]
            // این تابع دستگاه مشخص شده با شماره فهرست را از مجموعه برمی گرداند.
            int Item(uint deviceIndex, out IMMDevice device);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            // این تابع یک رابط صوتی مشخص را روی دستگاه ویندوز فعال می کند.
            int Activate(
                ref Guid iid,
                uint clsCtx,
                IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

            [PreserveSig]
            // این تابع مخزن ویژگی های دستگاه را برای خواندن مشخصات باز می کند.
            int OpenPropertyStore(int accessMode, out IPropertyStore properties);

            [PreserveSig]
            // این تابع شناسه داخلی دستگاه صوتی را دریافت می کند.
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

            [PreserveSig]
            // این تابع وضعیت فعلی دستگاه صوتی را دریافت می کند.
            int GetState(out uint state);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            // این تابع تعداد ویژگی های موجود در مخزن ویژگی دستگاه را برمی گرداند.
            int GetCount(out uint propertyCount);

            [PreserveSig]
            // این تابع کلید ویژگی مشخص شده با شماره فهرست را دریافت می کند.
            int GetAt(uint propertyIndex, out PROPERTYKEY key);

            [PreserveSig]
            // این تابع مقدار یک ویژگی مشخص دستگاه را از ویندوز می خواند.
            int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

            [PreserveSig]
            // این تابع مقدار یک ویژگی را در مخزن دستگاه تنظیم می کند؛ در آزمایشگر فعلی استفاده نمی شود.
            int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

            [PreserveSig]
            // این تابع تغییرات مخزن ویژگی را ثبت می کند؛ در آزمایشگر فعلی استفاده نمی شود.
            int Commit();
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            // این تابع جریان صوتی واساپی را با حالت، پرچم ها و فرمت انتخاب شده آماده می کند.
            int Initialize(
                AudioClientShareMode shareMode,
                uint streamFlags,
                long bufferDuration,
                long periodicity,
                IntPtr format,
                ref Guid audioSessionGuid);

            [PreserveSig]
            // این تابع اندازه بافر انتهای صوتی را بر حسب تعداد فریم دریافت می کند.
            int GetBufferSize(out uint numBufferFrames);

            [PreserveSig]
            // این تابع تاخیر گزارش شده برای جریان صوتی را از ویندوز دریافت می کند.
            int GetStreamLatency(out long latency100Ns);

            [PreserveSig]
            // این تابع تعداد فریم های فعلی موجود در بافر را دریافت می کند.
            int GetCurrentPadding(out uint numPaddingFrames);

            [PreserveSig]
            // این تابع بررسی می کند یک فرمت مشخص توسط جریان صوتی پشتیبانی می شود یا نه.
            int IsFormatSupported(
                AudioClientShareMode shareMode,
                IntPtr format,
                out IntPtr closestMatch);

            [PreserveSig]
            // این تابع فرمت واقعی میکس دستگاه را از موتور صوتی ویندوز دریافت می کند.
            int GetMixFormat(out IntPtr deviceFormat);

            [PreserveSig]
            // این تابع دوره زمانی پیش فرض و حداقل دوره دستگاه صوتی را دریافت می کند.
            int GetDevicePeriod(
                out long defaultPeriod100Ns,
                out long minimumPeriod100Ns);

            [PreserveSig]
            // این تابع جریان آماده شده صوتی را شروع می کند.
            int Start();

            [PreserveSig]
            // این تابع جریان صوتی فعال را متوقف می کند.
            int Stop();

            [PreserveSig]
            // این تابع وضعیت بافر جریان صوتی را بازنشانی می کند.
            int Reset();

            [PreserveSig]
            // این تابع رویدادی را معرفی می کند که هنگام آماده شدن داده صوتی علامت داده می شود.
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            // این تابع سرویس داخلی مورد نیاز مانند رابط کپچر را از کلاینت صوتی دریافت می کند.
            int GetService(ref Guid iid, out IntPtr interfacePointer);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            // این تابع اشاره گر بسته صوتی آماده و اطلاعات زمان بندی آن را از واساپی دریافت می کند.
            int GetBuffer(
                out IntPtr data,
                out uint numFramesToRead,
                out uint flags,
                out ulong devicePosition,
                out ulong qpcPosition100Ns);

            [PreserveSig]
            // این تابع بسته ای را که خوانده شده به واساپی پس می دهد تا بافر آزاد شود.
            int ReleaseBuffer(uint numFramesRead);

            [PreserveSig]
            // این تابع تعداد فریم های بسته بعدی آماده برای خواندن را گزارش می کند.
            int GetNextPacketSize(out uint numFramesInNextPacket);
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

            // این تابع مقدار متنی ذخیره شده در ساختار ویژگی ویندوز را به رشته سی شارپ تبدیل می کند.
            public string GetString()
            {
                const ushort VtLpwstr = 31;
                if (vt != VtLpwstr || pointerValue == IntPtr.Zero)
                {
                    return string.Empty;
                }

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
