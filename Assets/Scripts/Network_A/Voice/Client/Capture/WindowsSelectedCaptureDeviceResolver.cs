using System;
using System.Runtime.InteropServices;

namespace Network_A.Voice.Client.Capture
{
    internal static class WindowsSelectedCaptureDeviceResolver
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private const int STGM_READ = 0;

        private static readonly PROPERTYKEY DeviceFriendlyNamePropertyKey =
            new PROPERTYKEY
            {
                fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                pid = 14
            };

        public static bool TryGetSoundSettingsInputDevice(
            out string friendlyName,
            out string error)
        {
            friendlyName = string.Empty;
            error = string.Empty;

            IMMDeviceEnumerator enumerator = null;
            IMMDevice endpoint = null;
            IPropertyStore propertyStore = null;

            try
            {
                enumerator =
                    (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

                int hr = enumerator.GetDefaultAudioEndpoint(
                    EDataFlow.eCapture,
                    ERole.eConsole,
                    out endpoint);

                if (hr != 0 || endpoint == null)
                {
                    error =
                        "Windows GetDefaultAudioEndpoint(eCapture,eConsole) failed. HRESULT=0x" +
                        hr.ToString("X8");
                    return false;
                }

                hr = endpoint.OpenPropertyStore(
                    STGM_READ,
                    out propertyStore);

                if (hr != 0 || propertyStore == null)
                {
                    error =
                        "Windows capture endpoint property store could not be opened. HRESULT=0x" +
                        hr.ToString("X8");
                    return false;
                }

                PROPERTYKEY key = DeviceFriendlyNamePropertyKey;

                PROPVARIANT value;
                hr = propertyStore.GetValue(
                    ref key,
                    out value);

                if (hr != 0)
                {
                    error =
                        "Windows capture endpoint friendly name could not be read. HRESULT=0x" +
                        hr.ToString("X8");
                    return false;
                }

                try
                {
                    friendlyName = value.GetString();
                }
                finally
                {
                    PropVariantClear(ref value);
                }

                if (string.IsNullOrWhiteSpace(friendlyName))
                {
                    error =
                        "Windows Sound settings returned an empty input-device name.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error =
                    exception.GetType().Name +
                    ": " +
                    exception.Message;

                return false;
            }
            finally
            {
                ReleaseComObject(propertyStore);
                ReleaseComObject(endpoint);
                ReleaseComObject(enumerator);
            }
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null)
                return;

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

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(
                EDataFlow dataFlow,
                uint stateMask,
                out IntPtr devices);

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
                IntPtr client);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(
                IntPtr client);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
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
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint propertyCount);

            [PreserveSig]
            int GetAt(
                uint propertyIndex,
                out PROPERTYKEY key);

            [PreserveSig]
            int GetValue(
                ref PROPERTYKEY key,
                out PROPVARIANT value);

            [PreserveSig]
            int SetValue(
                ref PROPERTYKEY key,
                ref PROPVARIANT value);

            [PreserveSig]
            int Commit();
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
                const ushort VT_LPWSTR = 31;

                if (vt != VT_LPWSTR ||
                    pointerValue == IntPtr.Zero)
                {
                    return string.Empty;
                }

                return Marshal.PtrToStringUni(pointerValue) ??
                       string.Empty;
            }
        }
#else
        public static bool TryGetSoundSettingsInputDevice(
            out string friendlyName,
            out string error)
        {
            friendlyName = string.Empty;
            error = "Windows Sound input resolver is not active on this platform.";
            return false;
        }
#endif
    }
}
